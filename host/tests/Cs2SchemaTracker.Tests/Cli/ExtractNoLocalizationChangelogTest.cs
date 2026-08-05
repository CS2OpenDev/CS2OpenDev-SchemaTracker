// `--no-localization-changelog` opt-out: extract still emits the five binary changelog families
// (and still produces + fingerprints THIS build's own localization.json into
// provenance.localization), but skips ONLY the content-derived localization changelog family — the
// one whose predecessor side must be regenerated from the PREDECESSOR build's content. That
// regeneration is impossible on the forward-capture path (anonymous / ephemeral runners: anonymous
// Steam serves only the current build), which is what the scheduled-extract CI job hit.
//
// Both builds here carry a resolvable content depot (via the content-addressed store), so both
// produce localization. The predecessor's content is then made UNresolvable (its _content store
// copy is removed, leaving its manifest-record.json GID pointing at nothing) before the newer build
// is extracted — reproducing the CI environment.
//
// Coverage:
//   1. WITHOUT the flag (default): extracting the newer build FAILS loud (exit 65) naming the
//      localization changelog stage; nothing is promoted (reproduces the CI failure).
//   2. WITH --no-localization-changelog: the newer build extracts (exit 0); the promoted
//      changelog.json carries EXACTLY the five binary families and NO localization family; and
//      provenance.localization is still present (localization was produced + fingerprinted, only the
//      predecessor DIFF was skipped).
//   3. WITH the flag but NO predecessor (floor build): still no changelog (floor invariant holds).

using System.Globalization;
using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Schemas;
using Cs2SchemaTracker.Tests.Content;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class ExtractNoLocalizationChangelogTest
{
    // Predecessor < newer, both numeric so ChangelogPredecessor's numeric ordering applies.
    private const string PredBuild = "13380000";
    private const string NewBuild = "13380007";

    // Distinct content-depot GIDs so removing the predecessor's store copy leaves the newer build's
    // own content resolvable.
    private const ulong PredGid = 555000001UL;
    private const ulong NewGid = 555000007UL;

    private const string LocalizationChangelogStageError =
        "cannot regenerate predecessor '13380000' localization for the changelog localization family";

    private static readonly string[] FiveBinaryFamilies =
        { "classes", "enums", "convars", "commands", "engine_constants" };

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

    private static WalkerOutput Walk(string platform)
        => ExtractCommandTestShared.CannedWalkerOutput(platform);

    // Well-formed ELF + PE fixture binaries (each embeds an FDP + the RTTI descriptors the scanners
    // need), same as the inline-changelog harness.
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

    private static string TupleDir(string workDir, string build, string platform)
        => Path.Combine(workDir, "cache", "binaries", build, platform);

    // manifest-record.json carrying the content depot (2347770) -> this build's content GID, so
    // ContentStore resolves the trimmed store copy.
    private static void WriteManifestRecord(string workDir, string build, string platform, ulong gid)
    {
        new ManifestRecord(730, uint.Parse(build, CultureInfo.InvariantCulture), new[]
        {
            new ManifestRecordDepot(ContentStore.ContentDepotId, gid, "2026-06-10T00:00:00Z"),
        }).WriteToTupleDir(TupleDir(workDir, build, platform));
    }

    // Write the trimmed content pak into the content-addressed store at _content/<gid>/game/csgo so
    // TryResolveContentVpk finds it. StandardEntries includes the csgo_<lang>.txt localization tokens
    // that make LocalizationEmitter.HasSource true (so the build produces localization.json).
    private static void WriteContentStore(string workDir, string build, string platform, ulong gid)
    {
        var contentRoot = ContentStore.RootForTupleDir(TupleDir(workDir, build, platform))!;
        ContentVpkFixture.Write(ContentStore.StoreDirForGid(contentRoot, gid), ContentSamples.StandardEntries());
    }

    // Make a build's content UNresolvable: remove its _content/<gid> store copy while LEAVING its
    // manifest-record.json GID in place — exactly the anonymous-runner state where the predecessor's
    // content cannot be re-acquired.
    private static void RemoveContentStore(string workDir, string platform, ulong gid)
    {
        var contentRoot = ContentStore.RootForTupleDir(TupleDir(workDir, PredBuild, platform))!;
        var gidDir = Path.Combine(contentRoot, gid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(gidDir))
            Directory.Delete(gidDir, recursive: true);
    }

    private static void SetupBuild(string workDir, string build, string platform, ulong gid)
    {
        SetupBinaries(workDir, build, platform);
        WriteManifestRecord(workDir, build, platform, gid);
        WriteContentStore(workDir, build, platform, gid);
    }

    // Pin cwd to a fresh workDir, run body, restore + clean up.
    private static void InWorkDir(Action<string> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "no-loc-cl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
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

    // Extract one build into the shared off-repo extract-out root (both builds share it, so the newer
    // build sees the predecessor's committed set on disk — the forward-capture ordering).
    private static int ExtractInto(string build, string platform, params string[] extra)
    {
        var fake = new FakeWalkerRunner(0, "", Walk(platform));
        var args = new List<string> { "--build", build, "--platform", platform };
        args.AddRange(extra);
        return ExtractCommand.Run(args.ToArray(), () => fake);
    }

    private static string SetDir(string workDir, string build, string platform)
        => Path.Combine(workDir, "extract-out", build, platform);

    private static BuildChangelog ParseChangelog(string path)
        => new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true))
            .Parse<BuildChangelog>(File.ReadAllText(path));

    private static Schemas.Provenance ParseProvenance(string path)
        => new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true))
            .Parse<Schemas.Provenance>(File.ReadAllText(path));

    private static string CaptureStderr(Action body)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        { body(); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }

    [WindowsOnlyFact]
    public void Without_Flag_Newer_Build_Fails_Loud_When_Predecessor_Content_Is_Unresolvable()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBuild(workDir, PredBuild, platform, PredGid);
            SetupBuild(workDir, NewBuild, platform, NewGid);

            // Predecessor first (forward capture): it produces localization + fingerprints it.
            Assert.Equal(0, ExtractInto(PredBuild, platform));
            var predProv = ParseProvenance(Path.Combine(SetDir(workDir, PredBuild, platform), "provenance.json"));
            Assert.NotNull(predProv.Localization);
            Assert.False(string.IsNullOrEmpty(predProv.Localization.Sha256));

            // Remove ONLY the predecessor's content store copy: its GID now points at nothing, so the
            // localization changelog family cannot regenerate the predecessor side.
            RemoveContentStore(workDir, platform, PredGid);

            int exit = -1;
            var stderr = CaptureStderr(() => exit = ExtractInto(NewBuild, platform));

            Assert.Equal(65, exit);
            Assert.Contains(LocalizationChangelogStageError, stderr);

            // Fail-loud: nothing promoted for the newer build (it had no prior committed set).
            Assert.False(Directory.Exists(SetDir(workDir, NewBuild, platform)),
                "a failed localization-changelog regeneration must promote NO artifacts");
        });
    }

    [WindowsOnlyFact]
    public void With_Flag_Newer_Build_Extracts_With_Five_Family_Changelog_And_Keeps_Provenance_Localization()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBuild(workDir, PredBuild, platform, PredGid);
            SetupBuild(workDir, NewBuild, platform, NewGid);

            Assert.Equal(0, ExtractInto(PredBuild, platform));
            RemoveContentStore(workDir, platform, PredGid);

            // Same unresolvable-predecessor state as above, but the opt-out makes it succeed.
            Assert.Equal(0, ExtractInto(NewBuild, platform, "--no-localization-changelog"));

            var setDir = SetDir(workDir, NewBuild, platform);

            // The changelog is present and carries EXACTLY the five binary families — NO localization.
            var changelogPath = Path.Combine(setDir, "changelog.json");
            Assert.True(File.Exists(changelogPath), "the five-family changelog must still be produced");
            var cl = ParseChangelog(changelogPath);
            Assert.Equal(PredBuild, cl.FromBuild);
            Assert.Equal(NewBuild, cl.ToBuild);
            Assert.Equal(FiveBinaryFamilies, cl.Families.Select(f => f.Family).ToArray());
            Assert.DoesNotContain("localization", cl.Families.Select(f => f.Family));

            // This build's OWN localization was still produced + fingerprinted into provenance.
            var prov = ParseProvenance(Path.Combine(setDir, "provenance.json"));
            Assert.NotNull(prov.Localization);
            Assert.False(string.IsNullOrEmpty(prov.Localization.Sha256),
                "the build's own localization.json must still be fingerprinted into provenance.localization");

            // localization.json itself is build-on-demand and never committed.
            Assert.False(File.Exists(Path.Combine(setDir, "localization.json")),
                "localization.json is build-on-demand and must not be promoted");
        });
    }

    [WindowsOnlyFact]
    public void With_Flag_Floor_Build_Still_Emits_No_Changelog()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBuild(workDir, PredBuild, platform, PredGid);

            // The floor build has no predecessor, so the flag changes nothing: no changelog at all.
            Assert.Equal(0, ExtractInto(PredBuild, platform, "--no-localization-changelog"));
            Assert.False(File.Exists(Path.Combine(SetDir(workDir, PredBuild, platform), "changelog.json")),
                "the floor build (no predecessor) never emits a changelog");
        });
    }
}
