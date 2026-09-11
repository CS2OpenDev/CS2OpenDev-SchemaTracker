// End-to-end + unit coverage for extract's commit-path WALKER FINGERPRINT DRIFT GUARD.
//
// The corpus records, per committed set, the walker that produced it
// (provenance.tool.walkerSrcFingerprint). Re-emitting that set with a DIFFERENT walker rewrites the
// artifacts themselves rather than just the tool stamp, and the resulting diff reads exactly like a
// real engine change. So under --commit the guard compares, BEFORE any era is
// resolved or anything is walked, what the committed set records against what the resolved walker
// reports, and REFUSES the whole run (exit 78) on a known mismatch unless --allow-walker-change.
//
// The other half of the contract, exercised just as hard: it must be invisible in every other
// situation. A brand-new build, a matching walker, an off-repo run, a set recording no fingerprint,
// and a walker whose identity will not resolve must all behave exactly as they did before the guard
// existed — the last two warning, none of them blocking. Blocking on "unknown" would make the tool
// unusable against legitimate corpora; blocking on a KNOWN mismatch is the entire point.
//
// Driven through the drift test seam ExtractCommand.Run(args, fakeRunnerFactory, eraResolver,
// walkerIdentitySource): the FAKE runner produces the WalkerOutput (no real exe), the fixture-rooted
// EraWalkerResolver supplies the era + the expected layout signature, and the injected identity
// source states what the resolved walker reports — production resolves that by launching the binary,
// which a fixture path cannot satisfy.
//
// Deterministic: a throwaway repo-root temp dir (inventory + cache/binaries + a committed
// artifacts/ set), the process cwd pinned there (the shared "cwd-mutating" collection serializes
// that), era env vars cleared/restored. No wall-clock, no real walker, no real CS2 binaries, no Steam.

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class ExtractWalkerDriftGuardTest
{
    private const string Era = "cs2-2026-04-21";
    private const string EraSha = "b8dcaf14c603076300cab3861c99b44878d65db4";

    // ExtractCommandTestShared.CannedWalkerOutput emits this layout signature, so registering it as
    // the era's expected one keeps the post-load second gate satisfied on the resolver seam.
    private const string EraSig = "sig-fake";

    // The fingerprint the committed corpus carries (the real windows majority value) and the one the
    // leftover, unversioned natives/ walker reported.
    private const string CorpusFingerprint =
        "e20615c988e979ec05020dccd52e59fc61ed10e6a586646b8acde17b096578ee";
    private const string RunFingerprint =
        "f06f88f82f20b6d0a3f4c0a0e9dfd2c63a6c9d0b4d5ee5a0c58d5f1b0a2e3c44";
    private const string RunGitSha = "b53b4844e6f0a1c2d3e4f50617283940a1b2c3d4";

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

    /// <summary>A walker whose <c>--version</c> would report this identity, for any binary path.</summary>
    private static Func<string, WalkerIdentity> Reporting(string fingerprint, string gitSha = RunGitSha)
        => _ => new WalkerIdentity("0.0.0-fake", gitSha, fingerprint);

    /// <summary>A walker whose identity will not resolve at all (the missing-binary production case).</summary>
    private static Func<string, WalkerIdentity> Unresolvable()
        => path => throw new FileNotFoundException($"walker binary not found at '{path}'.", path);

    /// <summary>
    /// One fixture build: its committed set's recorded walkerSrcFingerprint, or null for a build with
    /// NO committed set at all (the brand-new-build case the guard must never touch).
    /// </summary>
    private sealed record Seed(string Build, string? RecordedFingerprint);

    // Fixture repo root (= the pinned cwd): data/cs2-assets-inventory.json (one compile-pin era whose
    // signature is registered for the running platform, plus a builds[] row per seeded build),
    // cache/binaries/<build>/<platform>/{libserver.so, client.dll}, and — for every seed carrying a
    // fingerprint — a committed artifacts/<build>/<platform>/ set (entity_schema.json "{}" stub +
    // provenance.json). Tests use a FAKE runner, so no natives walker binary is written.
    private static void InDriftFixture(string platform, IReadOnlyList<Seed> seeds, Action<string, EraWalkerResolver> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "walker-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "walker"));
        File.WriteAllText(Path.Combine(root, "walker", "CMakeLists.txt"), "# fixture");
        Directory.CreateDirectory(Path.Combine(root, "data"));

        var buildRows = seeds.Select(s =>
            $$"""{ "build_id": {{s.Build}}, "era": "{{Era}}", "content": "0", "binaries": {} }""");
        File.WriteAllText(Path.Combine(root, "data", "cs2-assets-inventory.json"), $$"""
        {
          "_meta": { "counts": {} },
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "eras": [
            { "era": "{{Era}}", "kind": "compile-pin", "hl2sdkSha": "{{EraSha}}",
              "layoutSignatures": { "{{platform}}": "{{EraSig}}" },
              "minClasses": 1, "maxClasses": 1000 }
          ],
          "depots": [
            { "depot_id": 2347770, "role": "content", "platforms": ["windows-x86_64","linux-x86_64"], "history": [] },
            { "depot_id": 2347771, "role": "binary",  "platforms": ["windows-x86_64","linux-x86_64"], "history": [] }
          ],
          "builds": [{{string.Join(",", buildRows)}}]
        }
        """);

        foreach (var seed in seeds)
        {
            var binariesDir = Path.Combine(root, "cache", "binaries", seed.Build, platform);
            Directory.CreateDirectory(binariesDir);
            File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"),
                ExtractCommandTestShared.WithEmbeddedFdp(
                    ExtractCommandTestShared.BuildElf(),
                    ExtractCommandTestShared.BuildFdp("netmessages.proto")));
            File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"),
                ExtractCommandTestShared.WithEmbeddedFdp(
                    ExtractCommandTestShared.BuildPe(),
                    ExtractCommandTestShared.BuildFdp("networkbasetypes.proto")));

            if (seed.RecordedFingerprint is null)
            {
                continue;   // brand-new build: no committed set to rewrite.
            }

            var setDir = Path.Combine(root, "artifacts", seed.Build, platform);
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "entity_schema.json"), "{}");
            var provenance = new Cs2SchemaTracker.Schemas.Provenance
            {
                SchemaVersion = "test",
                BuildId = seed.Build,
                Platform = platform,
                Tool = new ToolVersion
                {
                    Semver = "test",
                    GitCommit = "fixture",
                    WalkerGitSha = seed.RecordedFingerprint.Length > 0 ? "fixture-walker-sha" : "",
                    WalkerSrcFingerprint = seed.RecordedFingerprint,
                },
                Cs2Build = new CS2BuildIdentity { SchemaRevision = EraSig, SteamBuildId = seed.Build },
            };
            File.WriteAllText(Path.Combine(setDir, "provenance.json"),
                new JsonFormatter(JsonFormatter.Settings.Default).Format(provenance));
        }

        var oldBin = Environment.GetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar);
        var oldNatives = Environment.GetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar);
        var oldBinaries = Environment.GetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar);
        var oldExpect = Environment.GetEnvironmentVariable(ExtractCommand.ExpectFingerprintEnvVar);
        Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, null);
        Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, null);
        Environment.SetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar, null);
        Environment.SetEnvironmentVariable(ExtractCommand.ExpectFingerprintEnvVar, null);
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(root);
        try
        {
            body(root, new EraWalkerResolver(root));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, oldBin);
            Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, oldNatives);
            Environment.SetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar, oldBinaries);
            Environment.SetEnvironmentVariable(ExtractCommand.ExpectFingerprintEnvVar, oldExpect);
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static FakeWalkerRunner NewRunner(string platform)
        => new(0, "", ExtractCommandTestShared.CannedWalkerOutput(platform));

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

    /// <summary>The committed set is exactly as seeded and no staging/old sibling survives — the
    /// "refused before any side effect" half of the contract.</summary>
    private static void AssertNothingWritten(string root, string build, string platform)
    {
        var setDir = Path.Combine(root, "artifacts", build, platform);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));
        Assert.False(File.Exists(Path.Combine(setDir, "convars.json")),
            "a refused run must not emit any artifact into the committed set");
        var siblings = Directory.GetDirectories(Path.Combine(root, "artifacts", build))
            .Select(Path.GetFileName)
            .Where(n => n!.Contains(".staging-", StringComparison.Ordinal)
                     || n.Contains(".old-", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(siblings);
    }

    // ---- the refusal -------------------------------------------------------------------------

    [WindowsOnlyFact]
    public void Drift_Under_Commit_Refuses_Naming_Both_Fingerprints_And_Writes_Nothing()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000001", CorpusFingerprint) }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[] { "--build", "50000001", "--platform", platform, "--commit" },
                () => runner, resolver, Reporting(RunFingerprint)));

            Assert.Equal(78, code);

            // Names the build, the platform, BOTH fingerprints, the walker's gitSha and the opt-in.
            Assert.Contains("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);
            Assert.Contains("build 50000001", stderr, StringComparison.Ordinal);
            Assert.Contains(platform, stderr, StringComparison.Ordinal);
            Assert.Contains(CorpusFingerprint, stderr, StringComparison.Ordinal);
            Assert.Contains(RunFingerprint, stderr, StringComparison.Ordinal);
            Assert.Contains(RunGitSha, stderr, StringComparison.Ordinal);
            Assert.Contains("--allow-walker-change", stderr, StringComparison.Ordinal);
            Assert.Contains("No artifacts written.", stderr, StringComparison.Ordinal);

            // Refused BEFORE the walk: the fake runner was never invoked, and nothing landed.
            Assert.Equal(0, runner.Calls);
            AssertNothingWritten(root, "50000001", platform);
        });
    }

    [WindowsOnlyFact]
    public void Drift_Across_Several_Builds_Refuses_Once_For_The_Whole_Run_Naming_Each_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var seeds = new[] { new Seed("50000001", CorpusFingerprint), new Seed("50000002", CorpusFingerprint) };
        InDriftFixture(platform, seeds, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[]
                {
                    "--build", "50000001", "--build", "50000002", "--platform", platform, "--commit",
                },
                () => runner, resolver, Reporting(RunFingerprint)));

            Assert.Equal(78, code);
            Assert.Contains("2 committed sets", stderr, StringComparison.Ordinal);
            Assert.Contains("build 50000001", stderr, StringComparison.Ordinal);
            Assert.Contains("build 50000002", stderr, StringComparison.Ordinal);

            // The WHOLE run aborted before build 1 — no partial batch, nothing walked, nothing written.
            Assert.Equal(0, runner.Calls);
            AssertNothingWritten(root, "50000001", platform);
            AssertNothingWritten(root, "50000002", platform);
        });
    }

    // ---- the opt-in --------------------------------------------------------------------------

    [WindowsOnlyFact]
    public void Drift_With_AllowWalkerChange_Proceeds_And_Logs_The_Transition()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000001", CorpusFingerprint) }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[]
                {
                    "--build", "50000001", "--platform", platform, "--commit", "--allow-walker-change",
                },
                () => runner, resolver, Reporting(RunFingerprint)));

            Assert.Equal(0, code);

            // The audit trail: one line per affected set, old -> new, naming the flag that allowed it.
            Assert.Contains(
                $"WALKER CHANGE AUTHORISED (build 50000001, {platform}): {CorpusFingerprint} -> {RunFingerprint}",
                stderr, StringComparison.Ordinal);
            Assert.Contains("--allow-walker-change", stderr, StringComparison.Ordinal);
            // ... and it is a NOTICE, not the refusal.
            Assert.DoesNotContain("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);

            // The rewalk really happened: the "{}" stub was promoted over.
            var setDir = Path.Combine(root, "artifacts", "50000001", platform);
            Assert.NotEqual("{}", File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));
            Assert.True(File.Exists(Path.Combine(setDir, "convars.json")));
        });
    }

    // ---- everything the guard must leave alone -----------------------------------------------

    [WindowsOnlyFact]
    public void Matching_Fingerprint_Proceeds_Silently()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000001", CorpusFingerprint) }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[] { "--build", "50000001", "--platform", platform, "--commit" },
                () => runner, resolver, Reporting(CorpusFingerprint)));

            Assert.Equal(0, code);
            Assert.DoesNotContain("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("WALKER CHANGE AUTHORISED", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("drift guard", stderr, StringComparison.Ordinal);

            var setDir = Path.Combine(root, "artifacts", "50000001", platform);
            Assert.True(File.Exists(Path.Combine(setDir, "convars.json")));
        });
    }

    [WindowsOnlyFact]
    public void Committed_Set_Recording_No_Fingerprint_Warns_But_Never_Blocks()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000001", "") }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[] { "--build", "50000001", "--platform", platform, "--commit" },
                () => runner, resolver, Reporting(RunFingerprint)));

            Assert.Equal(0, code);
            Assert.Contains("WARNING walker drift guard", stderr, StringComparison.Ordinal);
            Assert.Contains("record no tool.walkerSrcFingerprint", stderr, StringComparison.Ordinal);
            Assert.Contains("50000001", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);

            var setDir = Path.Combine(root, "artifacts", "50000001", platform);
            Assert.True(File.Exists(Path.Combine(setDir, "convars.json")));
        });
    }

    [WindowsOnlyFact]
    public void Unresolvable_Walker_Identity_Warns_That_The_Guard_Could_Not_Run_And_Proceeds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000001", CorpusFingerprint) }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[] { "--build", "50000001", "--platform", platform, "--commit" },
                () => runner, resolver, Unresolvable()));

            Assert.Equal(0, code);
            Assert.Contains("walker drift guard could NOT run", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);

            var setDir = Path.Combine(root, "artifacts", "50000001", platform);
            Assert.True(File.Exists(Path.Combine(setDir, "convars.json")));
        });
    }

    [WindowsOnlyFact]
    public void Brand_New_Build_With_No_Committed_Set_Is_Never_Guarded()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000009", null) }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[] { "--build", "50000009", "--platform", platform, "--commit" },
                () => runner, resolver, Reporting(RunFingerprint)));

            Assert.Equal(0, code);
            // Not blocked, and not even warned about: there is no prior record to contradict.
            Assert.DoesNotContain("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("drift guard", stderr, StringComparison.Ordinal);

            var setDir = Path.Combine(root, "artifacts", "50000009", platform);
            Assert.True(File.Exists(Path.Combine(setDir, "entity_schema.json")));
            Assert.True(File.Exists(Path.Combine(setDir, "provenance.json")));
        });
    }

    [WindowsOnlyFact]
    public void NonCommit_Run_With_Drift_Proceeds_Off_Repo_And_Leaves_The_Committed_Set_Alone()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InDriftFixture(platform, new[] { new Seed("50000001", CorpusFingerprint) }, (root, resolver) =>
        {
            var runner = NewRunner(platform);
            int code = 0;
            var stderr = CaptureStderr(() => code = ExtractCommand.Run(
                new[] { "--build", "50000001", "--platform", platform },
                () => runner, resolver, Reporting(RunFingerprint)));

            Assert.Equal(0, code);
            Assert.DoesNotContain("WALKER FINGERPRINT DRIFT", stderr, StringComparison.Ordinal);

            // Off-repo output produced; the committed set is untouched (it was never the target).
            Assert.True(File.Exists(Path.Combine(root, "extract-out", "50000001", platform, "entity_schema.json")));
            AssertNothingWritten(root, "50000001", platform);
        });
    }

    // ---- flag parsing ------------------------------------------------------------------------

    [Theory]
    // The flag parses alongside every selection family (the guard is batch-level: one flag
    // authorises the whole run). Each case fails on the SELECTION, never on the flag — and each
    // returns before any repo access, so no fixture is needed.
    [InlineData("--allow-walker-change")]
    [InlineData("--build 1 --all --allow-walker-change")]
    [InlineData("--era cs2-2026-04-21 --pin b8dcaf14 --allow-walker-change")]
    public void AllowWalkerChange_Is_Accepted_By_The_Extract_Parser(string commandLine)
    {
        var args = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (code, _, err) = ConsoleCapture.Run(
            () => ExtractCommand.Run(args, () => new FakeWalkerRunner(0, "", null)));

        Assert.Equal(64, code);   // a SELECTION error ...
        Assert.DoesNotContain("unknown argument", err, StringComparison.Ordinal);   // ... not the flag.
    }

    // ---- the pure decision core --------------------------------------------------------------

    [Theory]
    // Both sides known: the only pair that can block is a genuine mismatch. (The verdict is compared
    // by NAME — the enum is internal, and an xUnit theory parameter must be at least as accessible
    // as the public test method.)
    [InlineData("aaaa", "aaaa", "Match")]
    [InlineData("aaaa", "bbbb", "Drift")]
    // Nothing recorded in the corpus — including the documented "unknown"/"<error>" sentinels, which
    // are the ABSENCE of a fingerprint, never a value to compare.
    [InlineData("", "bbbb", "NoRecordedFingerprint")]
    [InlineData(null, "bbbb", "NoRecordedFingerprint")]
    [InlineData("unknown", "bbbb", "NoRecordedFingerprint")]
    [InlineData("<error>", "bbbb", "NoRecordedFingerprint")]
    // The walker will not say what it is: the guard cannot run, and must not block on unknown.
    [InlineData("aaaa", "", "WalkerUnidentified")]
    [InlineData("aaaa", null, "WalkerUnidentified")]
    [InlineData("aaaa", "unknown", "WalkerUnidentified")]
    [InlineData("aaaa", "<error>", "WalkerUnidentified")]
    public void EvaluateWalkerDrift_Blocks_Only_On_A_Known_Mismatch(
        string? recorded, string? resolved, string expected)
    {
        Assert.Equal(expected, ExtractCommand.EvaluateWalkerDrift(recorded, resolved).ToString());
    }

    [Fact]
    public void Committed_Fingerprint_Reader_Distinguishes_No_Set_From_No_Record()
    {
        var root = Path.Combine(Path.GetTempPath(), "drift-read-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string Platform = "windows-x86_64";

            // No set at all -> false (a brand-new build, never guarded).
            Assert.False(ExtractCommand.TryReadCommittedWalkerFingerprint(root, "1", Platform, out var none));
            Assert.Equal("", none);

            // A set whose provenance records the fingerprint -> that value.
            var setDir = Path.Combine(root, "2", Platform);
            Directory.CreateDirectory(setDir);
            File.WriteAllText(Path.Combine(setDir, "entity_schema.json"), "{}");
            File.WriteAllText(Path.Combine(setDir, "provenance.json"),
                $$"""{ "tool": { "walkerSrcFingerprint": "{{CorpusFingerprint}}" } }""");
            Assert.True(ExtractCommand.TryReadCommittedWalkerFingerprint(root, "2", Platform, out var recorded));
            Assert.Equal(CorpusFingerprint, recorded);

            // A set with NO provenance.json -> exists, records nothing (warn, never block).
            var bare = Path.Combine(root, "3", Platform);
            Directory.CreateDirectory(bare);
            File.WriteAllText(Path.Combine(bare, "entity_schema.json"), "{}");
            Assert.True(ExtractCommand.TryReadCommittedWalkerFingerprint(root, "3", Platform, out var bareFprint));
            Assert.Equal("", bareFprint);

            // A set with an UNREADABLE provenance.json -> same: an unreadable record is not evidence
            // that the walker changed.
            var corrupt = Path.Combine(root, "4", Platform);
            Directory.CreateDirectory(corrupt);
            File.WriteAllText(Path.Combine(corrupt, "entity_schema.json"), "{}");
            File.WriteAllText(Path.Combine(corrupt, "provenance.json"), "{ this is not json");
            Assert.True(ExtractCommand.TryReadCommittedWalkerFingerprint(root, "4", Platform, out var corruptFprint));
            Assert.Equal("", corruptFprint);
        }
        finally
        {
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
