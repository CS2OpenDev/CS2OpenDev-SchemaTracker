// `acquire` BATCH selection mode tests (--all / repeatable --build).
//
//
// These exercise the inventory-driven historical-binary backfill that replaced
// scripts/backfill-acquire.ps1, mirroring ExtractBatchTest's batch / fake-acquirer
// pattern: a fake ISteamAcquirer records which (build, platform) it was asked to
// acquire (and can be configured to FAIL specific builds), and a throwaway temp
// inventory + cwd-relative output root let the resume marker / continue-on-failure /
// summary / exit semantics be asserted with NO real Steam.
//
// Covered:
//   - --all selects exactly the inventory builds with a binary manifest for the platform
//     (the fake acquirer is invoked per (build, platform)); --platform-less --all spans all
//     platforms a build lists; repeated --build selects only the named builds;
//   - continue-on-failure: one build fails -> the others still proceed, the summary reports
//     the failed id, and the run exits non-zero (the hard-failure exit);
//   - resumable skip: an already-.acq-done (build, platform) is skipped (acquirer NOT invoked),
//     and --force overrides;
//   - mutual-exclusion / fail-loud usage errors (--all + --from-manifest / --from-provenance;
//     --all + --build; an off-inventory --build; an unknown platform) exit 64 before any acquire.
//
// CA1861: every constant string[] passed to RunAsync / Assert is hoisted to a static readonly
// field (the project treats CA1861 as an error — same convention as AcquireCommandArgsTest).

using System.Globalization;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

[Collection("cwd-mutating")]
public sealed class AcquireBatchTest
{
    private const string Win = "windows-x86_64";
    private const string Lin = "linux-x86_64";
    private const string AcqDone = ".acq-done";   // mirror of AcquireCommand.AcqDoneMarker (internal const).

    // A fake acquirer that records every AcquireBinariesOnlyAsync call as "build/platform" and can be
    // told to FAIL (throw) for a configured set of build ids. Only the binaries-only entry point is
    // exercised by the batch path; the rest are stubs.
    private sealed class BatchFakeAcquirer : ISteamAcquirer
    {
        private readonly HashSet<uint> _failBuilds;
        public List<string> Calls { get; } = new();

        // --- Steam session-lifecycle modelling (the single-logon fix) -------------------------
        // Faithfully mirrors SteamAnonymousAcquirer's lease contract so the batch's BeginSharedSession
        // wiring is observable: outside a shared scope every acquire "logs on" (LogonCount++); inside a
        // scope the FIRST acquire logs on once and every later acquire reuses it. A 244-build batch that
        // opens one scope therefore records LogonCount == 1; the pre-fix per-build logon would record one
        // per acquire leg. Connect/logon are the throttled operation, so this is the load-bearing count.
        public int LogonCount { get; private set; }
        private bool _scopeActive;
        private bool _scopeLoggedOn;

        public IDisposable BeginSharedSession()
        {
            _scopeActive = true;
            _scopeLoggedOn = false;
            return new Scope(this);
        }

        private void EnsureLogon()
        {
            if (_scopeActive)
            {
                if (!_scopeLoggedOn)
                { LogonCount++; _scopeLoggedOn = true; }
            }
            else
            {
                LogonCount++;   // per-call lifecycle: each acquire connects+logons on its own session.
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly BatchFakeAcquirer _owner;
            public Scope(BatchFakeAcquirer owner) => _owner = owner;
            public void Dispose() { _owner._scopeActive = false; _owner._scopeLoggedOn = false; }
        }

        // Bytes the simulated BINARY leg reports as transferred-from-CDN. 0 (the default) models a
        // full cache-hit over already-acquired binaries; >0 models a real (re-)download. This is the
        // signal the batch summary surfaces so a content backfill can be SEEN to be binaries-cache-hit.
        public long BinDownloadedBytes { get; set; }
        // Bytes the simulated CONTENT leg reports as transferred-from-CDN.
        public long ContentDownloadedBytes { get; set; }

        public BatchFakeAcquirer(params uint[] failBuilds) => _failBuilds = new HashSet<uint>(failBuilds);

        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint appId, IReadOnlyList<uint> depotIds, uint buildId, string outDir, string platform,
            ManifestSpec? explicitSpec, CancellationToken ct)
        {
            EnsureLogon();
            Calls.Add($"{buildId}/{platform}");
            if (_failBuilds.Contains(buildId))
            {
                return Task.FromException<AcquireResult>(
                    new InvalidOperationException($"synthetic Steam failure for build {buildId}"));
            }
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "client.dll"), "fake-binary");
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: explicitSpec?.BuildId ?? buildId,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: OneFile,
                TotalBytes: 11,
                DownloadedBytes: BinDownloadedBytes));
        }

        public Task<AcquireResult> AcquireAsync(uint a, IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec s, string o, CancellationToken c)
            => throw new NotSupportedException();
        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(uint a, IReadOnlyList<uint> d, CancellationToken c)
            => throw new NotSupportedException();
        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(ManifestSpec s, bool p, CancellationToken c)
            => throw new NotSupportedException();
        // UNIFIED batch tools leg (--tools): records "<build>/<toolsDepot>" per call and drops a fake
        // editor DLL so the co-located tools presence is observable.
        public List<string> ToolsCalls { get; } = new();
        // Builds whose tools call should throw (simulates the DLC-gated depot denying an anonymous/
        // no-credentials session) — used to assert the DEFAULT-implied tools leg is best-effort.
        public HashSet<uint> FailToolsBuilds { get; } = new();
        public Task<AcquireResult> AcquireToolsAsync(
            uint a, uint td, uint b, string o, ManifestSpec? s, CancellationToken c)
        {
            EnsureLogon();
            var effectiveBuildId = s?.BuildId ?? b;
            ToolsCalls.Add($"{effectiveBuildId}/{td}");
            if (FailToolsBuilds.Contains(effectiveBuildId))
            {
                return Task.FromException<AcquireResult>(
                    new InvalidOperationException($"synthetic: anonymous access denied for build {effectiveBuildId}"));
            }
            Directory.CreateDirectory(o);
            File.WriteAllText(Path.Combine(o, "hammer.dll"), "fake-tools-dll");
            return Task.FromResult(new AcquireResult(
                OutDir: o,
                ResolvedBuildId: effectiveBuildId,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: OneFile,
                TotalBytes: 14));
        }

        // UNIFIED batch content leg (Gap A): records "<build>/<contentDepot>" per call and drops a fake
        // pak01_dir.vpk so the co-located content presence is observable.
        public List<string> ContentCalls { get; } = new();
        public Task<AcquireResult> AcquireContentPakAsync(
            uint a, uint cd, uint b, string o, bool m, ManifestSpec? s, bool dir, CancellationToken c)
        {
            EnsureLogon();
            ContentCalls.Add($"{s?.BuildId ?? b}/{cd}");
            Directory.CreateDirectory(o);
            File.WriteAllText(Path.Combine(o, "pak01_dir.vpk"), "fake-vpk");
            return Task.FromResult(new AcquireResult(
                OutDir: o,
                ResolvedBuildId: s?.BuildId ?? b,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: OneFile,
                TotalBytes: 8,
                DownloadedBytes: ContentDownloadedBytes));
        }
    }

    private static readonly AcquiredFileInfo[] OneFile =
        { new("client.dll", "deadbeef", 11, "2026-01-01T00:00:00Z") };

    // A throwaway inventory: app 730, both binary depots, and the given builds. Each build lists a
    // binary GID for the platforms passed in (so we can make some builds single-platform).
    private static string WriteInventory(string dir, params (uint Build, string[] Platforms)[] builds)
    {
        string Depot(uint id, string plat) => $$"""
            { "depot_id": {{id}}, "role": "binary", "platforms": ["{{plat}}"], "history": [] }
            """;
        string Build((uint Build, string[] Platforms) b)
        {
            var bins = string.Join(",", b.Platforms.Select((p, i) =>
                $"\"{p}\": \"{1000UL + b.Build + (ulong)i}\""));
            return $$"""
                { "build_id": {{b.Build}}, "binaries": { {{bins}} } }
                """;
        }
        var json = $$"""
        {
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "depots": [
            {{Depot(SteamAppIdMap.Cs2WindowsBinariesDepotId, Win)}},
            {{Depot(SteamAppIdMap.Cs2LinuxBinariesDepotId, Lin)}}
          ],
          "builds": [
            {{string.Join(",\n            ", builds.Select(Build))}}
          ]
        }
        """;
        var path = Path.Combine(dir, "inv.json");
        File.WriteAllText(path, json);
        return path;
    }

    // Run `body` with cwd pinned to a fresh temp dir; restores cwd in a finally and deletes the tree.
    // (cwd is process-global; the [Collection("cwd-mutating")] attribute serializes these.)
    private static async Task InTempCwd(Func<string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "acq-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(root);
        try
        { await body(root); }
        finally
        {
            Directory.SetCurrentDirectory(prev);
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static string OutRoot(string root) => Path.Combine(root, "out");

    private static string MarkerPath(string root, uint build, string platform) =>
        Path.Combine(OutRoot(root), build.ToString(CultureInfo.InvariantCulture), platform, AcqDone);

    // Build the argv for a batch run. inv/out come from the temp fixture so they cannot be hoisted;
    // the flag prefix IS hoisted per-test where it is a pure constant array.
    private static string[] AllArgs(string platform, string inv, string outRoot, bool force = false) =>
        force
            ? new[] { "--all", "--platform", platform, "--force", "--inventory", inv, "--out", outRoot }
            : new[] { "--all", "--platform", platform, "--inventory", inv, "--out", outRoot };

    // ---- --all selection (acquirer invoked per inventory build) ------------------------------

    private static readonly string[] ExpectedWinTwo = { "30000001/windows-x86_64", "30000002/windows-x86_64" };

    [Fact]
    public async Task All_SinglePlatform_Acquires_Every_Inventory_Build_For_That_Platform()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root,
                (30000001u, new[] { Win }),
                (30000002u, new[] { Win, Lin }),
                (30000003u, new[] { Lin }));   // linux-only: NOT selected for windows.

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            Assert.Equal(ExpectedWinTwo, fake.Calls.OrderBy(c => c, StringComparer.Ordinal).ToArray());
            Assert.True(File.Exists(MarkerPath(root, 30000001u, Win)));
            Assert.True(File.Exists(MarkerPath(root, 30000002u, Win)));
        });
    }

    private static readonly string[] ExpectedAllPlatforms =
        { "40000001/linux-x86_64", "40000001/windows-x86_64", "40000002/linux-x86_64" };
    private static string[] AllNoPlatformArgs(string inv, string outRoot) =>
        new[] { "--all", "--inventory", inv, "--out", outRoot };

    [Fact]
    public async Task All_NoPlatform_Acquires_Every_Platform_Each_Build_Lists()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root,
                (40000001u, new[] { Win, Lin }),
                (40000002u, new[] { Lin }));

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllNoPlatformArgs(inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            // build 1: both platforms; build 2: linux only -> three (build, platform) acquisitions.
            Assert.Equal(ExpectedAllPlatforms, fake.Calls.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        });
    }

    private static readonly string[] ExpectedNamedTwo = { "50000001/windows-x86_64", "50000003/windows-x86_64" };
    private static string[] TwoBuildArgs(string inv, string outRoot) =>
        new[] { "--build", "50000001", "--build", "50000003", "--platform", Win, "--inventory", inv, "--out", outRoot };

    [Fact]
    public async Task Build_Repeatable_Acquires_Exactly_The_Named_Inventory_Builds()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root,
                (50000001u, new[] { Win }),
                (50000002u, new[] { Win }),
                (50000003u, new[] { Win }));

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(TwoBuildArgs(inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            // Only the two named builds (not 50000002) were acquired.
            Assert.Equal(ExpectedNamedTwo, fake.Calls.OrderBy(c => c, StringComparer.Ordinal).ToArray());
        });
    }

    // ---- continue-on-failure -----------------------------------------------------------------

    private static readonly string[] ExpectedThreeAttempts =
        { "60000001/windows-x86_64", "60000002/windows-x86_64", "60000003/windows-x86_64" };

    [Fact]
    public async Task One_Build_Fails_Others_Proceed_Summary_Reports_It_Exit_NonZero()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root,
                (60000001u, new[] { Win }),
                (60000002u, new[] { Win }),   // <- this one fails
                (60000003u, new[] { Win }));

            var fake = new BatchFakeAcquirer(failBuilds: 60000002u);
            var stderr = await CaptureStderrAsync(async () =>
            {
                var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);
                // A hard failure makes the whole run exit non-zero (mirrors extract's batch EX_SOFTWARE).
                Assert.Equal(70, code);
            });

            // The failure did NOT abort the batch: all three were attempted.
            Assert.Equal(ExpectedThreeAttempts, fake.Calls.OrderBy(c => c, StringComparer.Ordinal).ToArray());
            // The two good builds got their resume marker; the failed one did NOT.
            Assert.True(File.Exists(MarkerPath(root, 60000001u, Win)));
            Assert.True(File.Exists(MarkerPath(root, 60000003u, Win)));
            Assert.False(File.Exists(MarkerPath(root, 60000002u, Win)));
            // The summary names the failed id and reports the counts.
            Assert.Contains("60000002/windows-x86_64", stderr);
            Assert.Contains("acquired=2", stderr);
            Assert.Contains("failed=1", stderr);
        });
    }

    // ---- resumable skip ----------------------------------------------------------------------

    private static readonly string[] ExpectedSecondOnly = { "70000002/windows-x86_64" };
    private static readonly string[] ExpectedForcedOne = { "80000001/windows-x86_64" };

    [Fact]
    public async Task Already_Done_Build_Is_Skipped_Acquirer_Not_Invoked()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root,
                (70000001u, new[] { Win }),
                (70000002u, new[] { Win }));

            // Pre-seed the .acq-done marker for build 1 (it must be skipped).
            var marker = MarkerPath(root, 70000001u, Win);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "acquired\n");

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            // Only build 2 was acquired; build 1 was skipped (acquirer never called for it).
            Assert.Equal(ExpectedSecondOnly, fake.Calls.ToArray());
        });
    }

    [Fact]
    public async Task Force_ReAcquires_An_Already_Done_Build()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root, (80000001u, new[] { Win }));

            var marker = MarkerPath(root, 80000001u, Win);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "acquired\n");

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root), force: true), () => fake);

            Assert.Equal(0, code);
            // --force ignores the marker and re-acquires.
            Assert.Equal(ExpectedForcedOne, fake.Calls.ToArray());
        });
    }

    // ---- --tools (Workshop Tools co-acquire, windows items only) -----------------------------

    // A throwaway inventory with the windows binary depot + the Workshop Tools depot (2347779).
    // Each build lists a windows binary GID; builds in `toolsBuilds` also list a tools GID.
    private static string WriteInventoryWithTools(string dir, uint[] builds, uint[] toolsBuilds)
    {
        string Build(uint b)
        {
            var tools = toolsBuilds.Contains(b) ? $", \"tools\": \"{7000UL + b}\"" : "";
            return $$"""
                { "build_id": {{b}}, "binaries": { "{{Win}}": "{{1000UL + b}}" }{{tools}} }
                """;
        }
        var json = $$"""
        {
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "depots": [
            { "depot_id": {{SteamAppIdMap.Cs2WindowsBinariesDepotId}}, "role": "binary", "platforms": ["{{Win}}"], "history": [] },
            { "depot_id": {{SteamAppIdMap.Cs2WorkshopToolsDepotId}}, "role": "tools", "platforms": ["{{Win}}"], "history": [] }
          ],
          "builds": [
            {{string.Join(",\n            ", builds.Select(Build))}}
          ]
        }
        """;
        var path = Path.Combine(dir, "inv-tools.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string[] AllToolsArgs(string inv, string outRoot) =>
        new[] { "--all", "--tools", "--platform", Win, "--inventory", inv, "--out", outRoot };

    private static readonly uint[] ToolsBothBuilds = { 91000001u, 91000002u };
    private static readonly uint[] ToolsOnlyFirst = { 91000001u };
    private static readonly string[] ExpectedOneToolsCall = { "91000001/2347779" };

    [Fact]
    public async Task All_Tools_CoAcquires_Tools_For_Builds_With_Gid_And_Notes_Omission()
    {
        await InTempCwd(async root =>
        {
            // Build 1 has a recorded tools GID; build 2 does not (tools omitted, a loud note).
            var inv = WriteInventoryWithTools(root, ToolsBothBuilds, ToolsOnlyFirst);

            var fake = new BatchFakeAcquirer();
            var stderr = await CaptureStderrAsync(async () =>
            {
                var code = await AcquireCommand.RunAsync(AllToolsArgs(inv, OutRoot(root)), () => fake);
                Assert.Equal(0, code);
            });

            // The tools leg ran ONLY for the build with a recorded tools GID, via its explicit spec.
            Assert.Equal(ExpectedOneToolsCall, fake.ToolsCalls.ToArray());
            // The omission for build 2 was surfaced loudly on stderr (a skip-of-record, not an error).
            Assert.Contains("91000002", stderr);
            Assert.Contains("NO tools GID", stderr);
            // Marker tokens are leg-aware: build 1 carries "+tools", build 2 does not.
            Assert.Contains("tools", File.ReadAllText(MarkerPath(root, 91000001u, Win)));
            Assert.DoesNotContain("tools", File.ReadAllText(MarkerPath(root, 91000002u, Win)));
        });
    }

    /// <summary>
    /// Seed a (build, platform) cache dir with a manifest-record.json listing the binary depot and
    /// (optionally) the Workshop Tools depot 2347779 — the AUTHORITATIVE tools-presence signal the
    /// retrofit decision reads (file presence is not consulted).
    /// </summary>
    private static void SeedManifestRecord(string root, uint build, string platform, bool withTools)
    {
        var dir = Path.Combine(OutRoot(root), build.ToString(CultureInfo.InvariantCulture), platform);
        Directory.CreateDirectory(dir);
        var depots = withTools
            ? """[{"depotId":2347771,"manifestId":"111","manifestCreatedUtc":"2026-01-01T00:00:00Z"},{"depotId":2347779,"manifestId":"777","manifestCreatedUtc":"2026-01-01T00:00:00Z"}]"""
            : """[{"depotId":2347771,"manifestId":"111","manifestCreatedUtc":"2026-01-01T00:00:00Z"}]""";
        File.WriteAllText(
            Path.Combine(dir, ManifestRecord.FileName),
            $$"""{"appId":730,"buildId":{{build}},"depots":{{depots}}}""");
    }

    [Fact]
    public async Task All_Tools_Retrofits_ToolsOnly_Over_A_Completed_Base_Acquire()
    {
        // THE 372-build backfill case: the (build, platform) dir was fully acquired by a prior batch
        // (marker present) and its manifest-record.json lacks depot 2347779. --tools must NOT
        // short-circuit on the marker AND must NOT re-acquire binaries/content — it acquires ONLY
        // the missing tools leg into the same dir (record-authoritative retrofit).
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithTools(root, ToolsOnlyFirst, ToolsOnlyFirst);

            var marker = MarkerPath(root, 91000001u, Win);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "acquired\n");
            SeedManifestRecord(root, 91000001u, Win, withTools: false);

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllToolsArgs(inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            Assert.Equal(ExpectedOneToolsCall, fake.ToolsCalls.ToArray());   // ONLY the tools leg
            Assert.Empty(fake.Calls);                                        // binaries untouched
            Assert.Empty(fake.ContentCalls);                                 // content untouched
            Assert.Contains("tools", File.ReadAllText(marker));              // marker token appended
        });
    }

    [Fact]
    public async Task All_Tools_Record_Already_Lists_Tools_Is_Full_Hit_No_Acquirer_Contact()
    {
        // Idempotent re-run: the record already lists 2347779 -> full cache HIT; the acquirer is
        // never contacted for any leg.
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithTools(root, ToolsOnlyFirst, ToolsOnlyFirst);

            var marker = MarkerPath(root, 91000001u, Win);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "acquired\n");
            SeedManifestRecord(root, 91000001u, Win, withTools: true);

            var fake = new BatchFakeAcquirer();
            var stderr = await CaptureStderrAsync(async () =>
            {
                var code = await AcquireCommand.RunAsync(AllToolsArgs(inv, OutRoot(root)), () => fake);
                Assert.Equal(0, code);
            });

            Assert.Empty(fake.ToolsCalls);
            Assert.Empty(fake.Calls);
            Assert.Empty(fake.ContentCalls);
            Assert.Contains("incl. tools", stderr);                          // loud HIT note
        });
    }

    private static readonly string[] AllToolsLinux = { "--all", "--tools", "--platform", Lin };

    [Fact]
    public async Task All_Tools_With_NonWindows_Platform_Exits_2_Before_Steam()
    {
        var fake = new BatchFakeAcquirer();
        var code = await AcquireCommand.RunAsync(AllToolsLinux, () => fake);
        Assert.Equal(2, code);
        Assert.Empty(fake.Calls);
        Assert.Empty(fake.ToolsCalls);
    }

    private static readonly string[] AllToolsBinariesOnly = { "--all", "--tools", "--binaries-only", "--platform", Win };

    [Fact]
    public async Task All_Tools_With_BinariesOnly_Is_Usage_Error_Exit_64()
    {
        var fake = new BatchFakeAcquirer();
        var code = await AcquireCommand.RunAsync(AllToolsBinariesOnly, () => fake);
        Assert.Equal(64, code);
        Assert.Empty(fake.Calls);
        Assert.Empty(fake.ToolsCalls);
    }

    // ---- tools now DEFAULT ON for windows batch items (schema-coverage; --no-tools opts out) ----

    private static string[] AllArgsNoToolsFlag(string inv, string outRoot) =>
        new[] { "--all", "--platform", Win, "--inventory", inv, "--out", outRoot };
    private static string[] AllArgsExplicitNoTools(string inv, string outRoot) =>
        new[] { "--all", "--no-tools", "--platform", Win, "--inventory", inv, "--out", outRoot };

    [Fact]
    public async Task All_Default_NoToolsFlag_Still_CoAcquires_Tools_For_Recorded_Builds()
    {
        // Schema-coverage default: the batch now co-acquires tools for windows items automatically,
        // no --tools flag needed.
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithTools(root, ToolsOnlyFirst, ToolsOnlyFirst);
            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgsNoToolsFlag(inv, OutRoot(root)), () => fake);
            Assert.Equal(0, code);
            Assert.Equal(ExpectedOneToolsCall, fake.ToolsCalls.ToArray());
        });
    }

    [Fact]
    public async Task All_NoTools_Opts_Out_Of_The_Default_Tools_Leg()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithTools(root, ToolsOnlyFirst, ToolsOnlyFirst);
            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgsExplicitNoTools(inv, OutRoot(root)), () => fake);
            Assert.Equal(0, code);
            Assert.Empty(fake.ToolsCalls);
        });
    }

    [Fact]
    public async Task All_Default_Implied_Tools_Failure_Is_Best_Effort_Not_Failed()
    {
        // Unlike an EXPLICIT --tools request, a DEFAULT-implied tools leg (no --tools flag) must not
        // mark an otherwise-clean build Failed when the DLC-gated depot denies an anonymous/
        // no-credentials session — it is a per-build best-effort note, and the run still exits 0.
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithTools(root, ToolsOnlyFirst, ToolsOnlyFirst);
            var fake = new BatchFakeAcquirer();
            fake.FailToolsBuilds.Add(91000001u);
            var stderr = await CaptureStderrAsync(async () =>
            {
                var code = await AcquireCommand.RunAsync(AllArgsNoToolsFlag(inv, OutRoot(root)), () => fake);
                Assert.Equal(0, code);
            });
            Assert.Single(fake.Calls);   // binaries still acquired
            Assert.Contains("tools leg skipped (non-fatal", stderr);
        });
    }

    // ---- mutual-exclusion / fail-loud usage errors (exit 64, before any acquirer contact) ----

    private static readonly string[] AllPlusFromManifest =
        { "--all", "--from-manifest", "spec.json", "--platform", Win };
    private static readonly string[] AllPlusFromProvenance =
        { "--all", "--from-provenance", "prov.json", "--platform", Win };
    private static readonly string[] AllPlusExplicitBuilds =
        { "--all", "--build", "12345", "--build", "67890", "--platform", Win };
    private static readonly string[] AllUnknownPlatform = { "--all", "--platform", "mac-arm64" };

    [Fact]
    public async Task All_With_FromManifest_Is_Usage_Error_Exit_64()
    {
        var fake = new BatchFakeAcquirer();
        var code = await AcquireCommand.RunAsync(AllPlusFromManifest, () => fake);
        Assert.Equal(64, code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task All_With_FromProvenance_Is_Usage_Error_Exit_64()
    {
        var fake = new BatchFakeAcquirer();
        var code = await AcquireCommand.RunAsync(AllPlusFromProvenance, () => fake);
        Assert.Equal(64, code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task All_With_Explicit_Build_List_Is_Usage_Error_Exit_64()
    {
        var fake = new BatchFakeAcquirer();
        var code = await AcquireCommand.RunAsync(AllPlusExplicitBuilds, () => fake);
        Assert.Equal(64, code);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Batch_Unknown_Platform_Is_Usage_Error_Exit_64()
    {
        var fake = new BatchFakeAcquirer();
        var code = await AcquireCommand.RunAsync(AllUnknownPlatform, () => fake);
        Assert.Equal(64, code);
        Assert.Empty(fake.Calls);
    }

    private static string[] OffInventoryBuildArgs(string inv, string outRoot) =>
        new[] { "--build", "90000001", "--build", "99999999", "--platform", Win, "--inventory", inv, "--out", outRoot };

    [Fact]
    public async Task Batch_Build_Not_In_Inventory_Is_Usage_Error_Exit_64()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventory(root, (90000001u, new[] { Win }));
            var fake = new BatchFakeAcquirer();
            // 90000001 is in the inventory; 99999999 is not -> fail-loud usage error.
            var code = await AcquireCommand.RunAsync(OffInventoryBuildArgs(inv, OutRoot(root)), () => fake);
            Assert.Equal(64, code);
            Assert.Empty(fake.Calls);
        });
    }

    // ---- UNIFIED batch (Gap A): binaries + co-located content per build -----------------------

    // Inventory WITH a shared content depot (2347770) + a per-build builds[].content GID, so the
    // unified batch's content leg (ContentTargetFor) resolves a target per build.
    private static string WriteInventoryWithContent(string dir, params (uint Build, string[] Platforms)[] builds)
    {
        string BinDepot(uint id, string plat) => $$"""
            { "depot_id": {{id}}, "role": "binary", "platforms": ["{{plat}}"], "history": [] }
            """;
        string Build((uint Build, string[] Platforms) b)
        {
            var bins = string.Join(",", b.Platforms.Select((p, i) =>
                $"\"{p}\": \"{1000UL + b.Build + (ulong)i}\""));
            return $$"""
                { "build_id": {{b.Build}}, "content": "{{9000UL + b.Build}}", "binaries": { {{bins}} } }
                """;
        }
        var json = $$"""
        {
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "depots": [
            {{BinDepot(SteamAppIdMap.Cs2WindowsBinariesDepotId, Win)}},
            {{BinDepot(SteamAppIdMap.Cs2LinuxBinariesDepotId, Lin)}},
            { "depot_id": {{SteamAppIdMap.Cs2SharedContentDepotId}}, "role": "content", "platforms": ["windows-x86_64","linux-x86_64"], "history": [] }
          ],
          "builds": [
            {{string.Join(",\n            ", builds.Select(Build))}}
          ]
        }
        """;
        var path = Path.Combine(dir, "inv-content.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task Unified_Batch_Also_Fetches_Colocated_Content_Per_Build()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithContent(root, (31000001u, new[] { Win }), (31000002u, new[] { Win }));
            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            Assert.Equal(2, fake.Calls.Count);                       // binaries per build
            Assert.Equal(2, fake.ContentCalls.Count);                // content per build (NEW)
            Assert.Contains($"31000001/{SteamAppIdMap.Cs2SharedContentDepotId}", fake.ContentCalls);
            // The resume marker is content-aware ("acquired+content").
            Assert.Contains("content", File.ReadAllText(MarkerPath(root, 31000001u, Win)));
        });
    }

    [Fact]
    public async Task BinariesOnly_Batch_Skips_The_Content_Leg()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithContent(root, (32000001u, new[] { Win }));
            var fake = new BatchFakeAcquirer();
            var args = new[] { "--all", "--binaries-only", "--platform", Win, "--inventory", inv, "--out", OutRoot(root) };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.Single(fake.Calls);
            Assert.Empty(fake.ContentCalls);                         // opted out of content
            Assert.Equal("acquired\n", File.ReadAllText(MarkerPath(root, 32000001u, Win)));
        });
    }

    [Fact]
    public async Task Unified_Batch_ReAcquires_A_BinariesOnly_Marker_To_Add_Content()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithContent(root, (33000001u, new[] { Win }));
            // Pre-seed a BINARIES-ONLY marker (from a prior --binaries-only batch). The unified default
            // must NOT treat it as done — it re-acquires to ADD the content, with no --force.
            var marker = MarkerPath(root, 33000001u, Win);
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, "acquired\n");

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            Assert.Single(fake.Calls);                               // re-acquired (not skipped)
            Assert.Single(fake.ContentCalls);                        // content added
            Assert.Contains("content", File.ReadAllText(marker));    // marker upgraded
        });
    }

    // ---- single shared Steam session across the whole batch (the login-throttle fix) ----------
    // The batch must LOG ON ONCE and reuse that one session for every build's binary + content
    // leg — not connect+logon per build (which tripped Steam's AccountLoginDeniedThrottle after
    // ~58 builds). The fake mirrors SteamAnonymousAcquirer's lease contract (LogonCount), so this
    // asserts the command opens exactly one BeginSharedSession scope around the per-build loop.

    [Fact]
    public async Task Batch_Logs_On_Once_Across_Many_Builds_Binaries_And_Content()
    {
        await InTempCwd(async root =>
        {
            // Three builds, each with binaries + co-located content => six acquire legs total.
            var inv = WriteInventoryWithContent(root,
                (36000001u, new[] { Win }),
                (36000002u, new[] { Win }),
                (36000003u, new[] { Win }));

            var fake = new BatchFakeAcquirer();
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);

            Assert.Equal(0, code);
            Assert.Equal(3, fake.Calls.Count);          // three binary legs
            Assert.Equal(3, fake.ContentCalls.Count);   // three content legs
            // ...all six acquire legs reused ONE shared session: exactly one logon for the batch.
            Assert.Equal(1, fake.LogonCount);
        });
    }

    [Fact]
    public async Task Batch_With_One_Failure_Still_Logs_On_Once()
    {
        await InTempCwd(async root =>
        {
            // A per-build DATA failure fail-isolates but must NOT cause extra logons —
            // the shared session is unaffected by a build's data error.
            var inv = WriteInventory(root,
                (37000001u, new[] { Win }),
                (37000002u, new[] { Win }),   // <- fails
                (37000003u, new[] { Win }));

            var fake = new BatchFakeAcquirer(failBuilds: 37000002u);
            var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);

            Assert.Equal(70, code);            // a hard failure => non-zero run exit.
            Assert.Equal(3, fake.Calls.Count); // all three attempted (continue-on-failure)...
            Assert.Equal(1, fake.LogonCount);  // ...on a single shared logon.
        });
    }

    // ---- binary-cache reuse reporting (the fix's observable signal) ---------------------------
    // The batch summary must distinguish a binary CACHE-HIT (DownloadedBytes==0 from the acquirer:
    // already-cached binaries were verified in place, not re-fetched) from a real binary download,
    // and report the content leg's fetched bytes separately — so a content backfill over the
    // already-cached builds is visibly content-only (no ~378 MB/build binary re-transfer).

    [Fact]
    public async Task Binary_CacheHit_Is_Reported_And_Content_Bytes_Shown_Separately()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithContent(root, (34000001u, new[] { Win }));
            // BinDownloadedBytes==0 => binary cache-hit; content actually fetched 4096 bytes.
            var fake = new BatchFakeAcquirer { BinDownloadedBytes = 0, ContentDownloadedBytes = 4096 };
            var stderr = await CaptureStderrAsync(async () =>
            {
                var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);
                Assert.Equal(0, code);
            });

            Assert.Single(fake.Calls);                 // binary leg still ran (to verify in place)...
            Assert.Single(fake.ContentCalls);          // ...and the content leg co-located the pak.
            // Binary leg reported as a cache-hit with zero CDN transfer.
            Assert.Contains("binaries=cache-hit", stderr);
            Assert.Contains("bin-fetched=0", stderr);
            // Content leg's transferred bytes are shown (content-only transfer).
            Assert.Contains("fetched=4,096B", stderr);
        });
    }

    [Fact]
    public async Task Binary_Download_Reports_Fetched_Bytes_Not_CacheHit()
    {
        await InTempCwd(async root =>
        {
            var inv = WriteInventoryWithContent(root, (35000001u, new[] { Win }));
            // BinDownloadedBytes>0 => a real binary (re-)download (missing/corrupt cache, or first acquire).
            var fake = new BatchFakeAcquirer { BinDownloadedBytes = 123456, ContentDownloadedBytes = 0 };
            var stderr = await CaptureStderrAsync(async () =>
            {
                var code = await AcquireCommand.RunAsync(AllArgs(Win, inv, OutRoot(root)), () => fake);
                Assert.Equal(0, code);
            });

            Assert.DoesNotContain("binaries=cache-hit", stderr);
            Assert.Contains("bin-fetched=123,456B", stderr);
        });
    }

    // Run an async action with Console.Error redirected to an in-memory writer; returns the text.
    private static async Task<string> CaptureStderrAsync(Func<Task> body)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        { await body(); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }
}
