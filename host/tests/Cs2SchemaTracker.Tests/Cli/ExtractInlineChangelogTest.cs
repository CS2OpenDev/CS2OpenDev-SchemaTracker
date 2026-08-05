// inline changelog wiring: `extract` auto-produces changelog.json against the immediate
// committed predecessor, resolved by the SHARED ChangelogPredecessor rule (the same rule the
// verify-artifacts gate uses). Synthetic: fake walker, no Steam, no real binaries.
//
// Coverage:
//   1. A build WITH a committed predecessor -> the promoted set carries changelog.json with the
//      correct from_build/to_build and the expected added/changed deltas.
// 2. The EARLIEST build (no predecessor) -> NO changelog.json (floor invariant).
//   3. --no-changelog with a predecessor present -> NO changelog.json (opt-out).
//   4. The changelog lands atomically (present in outDir after a successful promote).
// 5. Determinism: two extracts of the newer build produce a byte-identical changelog.json.
// 6. A set produced by extract PASSES the ArtifactSetValidator predecessor gate (proves the
//      shared-helper agreement — extract never produces output its own verify gate rejects).

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public class ExtractInlineChangelogTest
{
    // Predecessor < newer, both numeric so ChangelogPredecessor's numeric ordering applies.
    private const string PredBuild = "13370000";
    private const string NewBuild = "13370007";

    private static string? MatchingPlatform()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-x86_64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows-x86_64";
        return null;
    }

    // The baseline (predecessor) walk = the shared canned output verbatim.
    private static WalkerOutput BaselineWalk(string platform)
        => ExtractCommandTestShared.CannedWalkerOutput(platform);

    // The newer walk: change sv_cheats' default (0 -> 1) and ADD one convar (sv_new). The registry
    // universe must mirror every produced symbol (PATH A cross-check), so add sv_new there.
    private static WalkerOutput NewWalk(string platform)
    {
        var w = ExtractCommandTestShared.CannedWalkerOutput(platform);
        w.Convars.Convars[0].Default = "1";                    // sv_cheats default change
        w.Convars.Convars.Add(new ConVar { Name = "sv_new", Default = "5", Description = "new convar" });
        w.RegistryUniverse.Symbols.Add(
            new ObservedRegistrySymbol { Symbol = "sv_new", Module = "", Category = "convar" });
        return w;
    }

    // Create cache/binaries/<build>/<platform> under workDir with the shared fixture binaries (a
    // well-formed ELF + PE, each embedding an FDP + the RTTI descriptors the scanners need).
    private static void SetupBinaries(string workDir, string build, string platform)
    {
        var binariesDir = Path.Combine(workDir, "cache", "binaries", build, platform);
        Directory.CreateDirectory(binariesDir);
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"),
            ExtractCommandTestShared.WithEmbeddedFdp(
                ExtractCommandTestShared.BuildElf(),
                ExtractCommandTestShared.BuildFdp("netmessages.proto")));
        File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"),
            ExtractCommandTestShared.WithEmbeddedFdp(
                ExtractCommandTestShared.BuildPe(),
                ExtractCommandTestShared.BuildFdp("networkbasetypes.proto")));
    }

    // Pin cwd to a fresh workDir, set up binaries for both builds, run body, restore + clean up.
    private static void InTwoBuildWorkDir(string platform, Action<string> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "inline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        SetupBinaries(workDir, PredBuild, platform);
        SetupBinaries(workDir, NewBuild, platform);

        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        { body(workDir); }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // Extract one build into the shared off-repo extract-out root (both builds share it, so the
    // newer build sees the predecessor's committed set on disk — the forward-capture ordering).
    private static int ExtractInto(string build, string platform, WalkerOutput walk, params string[] extra)
    {
        var fake = new FakeWalkerRunner(0, "", walk);
        var args = new List<string> { "--build", build, "--platform", platform };
        args.AddRange(extra);
        return ExtractCommand.Run(args.ToArray(), () => fake);
    }

    private static string SetDir(string workDir, string build, string platform)
        => Path.Combine(workDir, "extract-out", build, platform);

    private static BuildChangelog ParseChangelog(string path)
        => new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true))
            .Parse<BuildChangelog>(File.ReadAllText(path));

    [WindowsOnlyFact]
    public void Extract_With_Committed_Predecessor_Emits_Changelog_With_Correct_Ends_And_Deltas()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InTwoBuildWorkDir(platform, workDir =>
        {
            // Predecessor first (forward capture: oldest -> newest).
            Assert.Equal(0, ExtractInto(PredBuild, platform, BaselineWalk(platform)));
            Assert.False(File.Exists(Path.Combine(SetDir(workDir, PredBuild, platform), "changelog.json")),
                "the FLOOR build (no predecessor) must have NO changelog.json");

            // Newer build sees the committed predecessor on disk -> inline changelog.
            Assert.Equal(0, ExtractInto(NewBuild, platform, NewWalk(platform)));
            var changelogPath = Path.Combine(SetDir(workDir, NewBuild, platform), "changelog.json");
            Assert.True(File.Exists(changelogPath), "extract must auto-produce changelog.json");

            var cl = ParseChangelog(changelogPath);
            Assert.Equal(PredBuild, cl.FromBuild);
            Assert.Equal(NewBuild, cl.ToBuild);
            Assert.Equal(platform, cl.Platform);

            // Convars family: sv_new ADDED, sv_cheats default CHANGED.
            var convars = cl.Families.Single(f => f.Family == "convars");
            Assert.Contains("sv_new", convars.Added);
            Assert.DoesNotContain("sv_new", convars.Removed);
            var cheats = convars.Changed.Single(c => c.Name == "sv_cheats");
            var def = cheats.Fields.Single(f => f.Field == "default");
            Assert.Equal("0", def.OldValue);
            Assert.Equal("1", def.NewValue);
        });
    }

    [WindowsOnlyFact]
    public void Floor_Build_Emits_No_Changelog()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InTwoBuildWorkDir(platform, workDir =>
        {
            Assert.Equal(0, ExtractInto(PredBuild, platform, BaselineWalk(platform)));
            Assert.False(File.Exists(Path.Combine(SetDir(workDir, PredBuild, platform), "changelog.json")),
                "the earliest committed build has no predecessor -> no changelog.json");
        });
    }

    [WindowsOnlyFact]
    public void NoChangelog_Flag_Suppresses_Inline_Changelog_Even_With_Predecessor()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InTwoBuildWorkDir(platform, workDir =>
        {
            Assert.Equal(0, ExtractInto(PredBuild, platform, BaselineWalk(platform)));
            Assert.Equal(0, ExtractInto(NewBuild, platform, NewWalk(platform), "--no-changelog"));

            Assert.False(File.Exists(Path.Combine(SetDir(workDir, NewBuild, platform), "changelog.json")),
                "--no-changelog must skip the inline changelog even when a predecessor exists");
        });
    }

    [WindowsOnlyFact]
    public void Changelog_Lands_Atomically_In_Promoted_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InTwoBuildWorkDir(platform, workDir =>
        {
            Assert.Equal(0, ExtractInto(PredBuild, platform, BaselineWalk(platform)));
            Assert.Equal(0, ExtractInto(NewBuild, platform, NewWalk(platform)));

            var setDir = SetDir(workDir, NewBuild, platform);
            // changelog.json is in the PROMOTED set dir (not a leftover staging dir).
            Assert.True(File.Exists(Path.Combine(setDir, "changelog.json")));
            var stagingSurvivors = Directory.GetDirectories(Path.Combine(workDir, "extract-out", NewBuild))
                .Select(Path.GetFileName)
                .Where(d => d!.StartsWith(platform + ".staging-", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(stagingSurvivors);
        });
    }

    [WindowsOnlyFact]
    public void Changelog_Is_Deterministic_Byte_Identical_Across_Two_Extracts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InTwoBuildWorkDir(platform, workDir =>
        {
            Assert.Equal(0, ExtractInto(PredBuild, platform, BaselineWalk(platform)));

            Assert.Equal(0, ExtractInto(NewBuild, platform, NewWalk(platform)));
            var first = File.ReadAllBytes(Path.Combine(SetDir(workDir, NewBuild, platform), "changelog.json"));

            // Re-extract the newer build against the SAME committed predecessor -> byte-identical.
            Assert.Equal(0, ExtractInto(NewBuild, platform, NewWalk(platform)));
            var second = File.ReadAllBytes(Path.Combine(SetDir(workDir, NewBuild, platform), "changelog.json"));

            Assert.Equal(first, second);
        });
    }

    [WindowsOnlyFact]
    public void Extract_Output_Passes_ArtifactSetValidator_Predecessor_Gate()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InTwoBuildWorkDir(platform, workDir =>
        {
            Assert.Equal(0, ExtractInto(PredBuild, platform, BaselineWalk(platform)));
            Assert.Equal(0, ExtractInto(NewBuild, platform, NewWalk(platform)));

            // Off-repo extract does not write a build-level omissions.json (only the content-omission
            // path does); requires the mandatory empty-list file. Seed it so ValidateAll
            // reaches the changelog gate — the behaviour under test here.
            foreach (var b in new[] { PredBuild, NewBuild })
            {
                File.WriteAllText(
                    Path.Combine(workDir, "extract-out", b, ArtifactSet.OmissionsFileName), "{}");
            }

            // The extract-out root IS the artifacts root the shared predecessor rule resolved
            // against. The validator (delegating to the SAME rule) must accept BOTH the floor
            // build (changelog forbidden + absent) and the newer build (changelog required +
            // present with the right ends) — proving extract never emits output its own gate rejects.
            var report = new ArtifactSetValidator(Path.Combine(workDir, "extract-out")).ValidateAll();
            Assert.True(report.Passed,
                "extract's own output must pass + the gate: " +
                string.Join("; ", report.AllViolations.Select(x => x.Message)));
        });
    }
}
