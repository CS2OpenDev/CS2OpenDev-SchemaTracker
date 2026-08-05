// AcquireCommand argument-parsing tests.
//
// These exercise the CLI argument path without touching Steam by injecting
// a fake ISteamAcquirer through the internal AcquireCommand.RunAsync(args, factory)
// seam.
//
// Covers (fail-loud surface):
//   - --help exits 0 without invoking the acquirer
//   - missing --build / --platform exits 64
//   - unknown --platform exits 64 (including the retired *.client / *.server names)
//   - non-integer --build exits 64
//   - --build latest keys the default out-dir off the RESOLVED build_id (via PICS)
//   - hash-mismatch (InvalidDataException) → exit 65
//   - PICS-error (InvalidOperationException) → exit 65
//   - unknown failure → exit 1
//
// AcquireCommand is internal — the host project's InternalsVisibleTo lets
// the test project reach it.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class AcquireCommandArgsTest
{
    // Static arg arrays — CA1861-safe (avoid per-call allocation in tests).
    private static readonly string[] ArgsHelp = { "--help" };
    private static readonly string[] ArgsMissingBuild = { "--platform", "linux-x86_64" };
    private static readonly string[] ArgsMissingPlatform = { "--build", "12345" };
    private static readonly string[] ArgsUnknownPlatform = { "--build", "12345", "--platform", "mac-arm64" };
    private static readonly string[] ArgsRetiredTupleName = { "--build", "12345", "--platform", "linux-x86_64.server" };
    private static readonly string[] ArgsNonIntegerBuild = { "--build", "not-a-number", "--platform", "linux-x86_64" };
    private static readonly string[] ArgsLatestLinux = { "--build", "latest", "--platform", "linux-x86_64" };
    private static readonly string[] ArgsIntegerLinux = { "--build", "1555", "--platform", "linux-x86_64" };
    private static readonly string[] ArgsHashMismatch = { "--build", "1555", "--platform", "linux-x86_64" };
    private static readonly uint[] ExpectedLinuxBinDepots = { SteamAppIdMap.Cs2LinuxBinariesDepotId };

    private sealed class FakeAcquirer : ISteamAcquirer
    {
        public Func<uint, IReadOnlyList<uint>, uint, string, CancellationToken, Task<AcquireResult>>? OnAcquire { get; init; }
        public Func<ManifestSpec, string, CancellationToken, Task<AcquireResult>>? OnAcquireExplicit { get; init; }
        public int InvocationCount;
        public int ExplicitInvocationCount;
        public int CurrentPicsCount;
        public uint LastAppId;
        public IReadOnlyList<uint> LastDepots = Array.Empty<uint>();
        public uint LastBuildId;
        /// <summary>
        /// Build id captured by the FULL-binary AcquireAsync leg ONLY. Unlike the shared
        /// <see cref="LastBuildId"/>, this is NOT clobbered by the co-located content leg
        /// (which calls AcquireContentPakAsync with buildId 0), so a test can assert the
        /// build id the binary acquire actually received on the unified forward path.
        /// </summary>
        public uint LastFullAcquireBuildId;
        public string LastOutDir = string.Empty;
        public ManifestSpec? LastExplicitSpec;

        /// <summary>Build id ProbeCurrentPicsAsync resolves 'latest' to.</summary>
        public uint CurrentPicsBuildId { get; init; } = 12345u;

        public Task<AcquireResult> AcquireAsync(
            uint appId, IReadOnlyList<uint> depotIds, uint buildId, string outDir, CancellationToken ct)
        {
            InvocationCount++;
            LastAppId = appId;
            LastDepots = depotIds;
            LastBuildId = buildId;
            LastFullAcquireBuildId = buildId;
            LastOutDir = outDir;
            if (OnAcquire is not null)
                return OnAcquire(appId, depotIds, buildId, outDir, ct);
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: buildId == 0 ? 12345u : buildId,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: Array.Empty<AcquiredFileInfo>(),
                TotalBytes: 0));
        }

        public Task<AcquireResult> AcquireExplicitAsync(
            ManifestSpec spec, string outDir, CancellationToken ct)
        {
            ExplicitInvocationCount++;
            LastExplicitSpec = spec;
            LastAppId = spec.AppId;
            LastDepots = spec.OrderedDepotIds;
            LastBuildId = spec.BuildId;
            LastOutDir = outDir;
            if (OnAcquireExplicit is not null)
                return OnAcquireExplicit(spec, outDir, ct);
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: spec.BuildId,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: Array.Empty<AcquiredFileInfo>(),
                TotalBytes: 0));
        }

        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(
            uint appId, IReadOnlyList<uint> depotIds, CancellationToken ct)
        {
            CurrentPicsCount++;
            var depots = depotIds.OrderBy(x => x)
                .Select(id => new CurrentDepotManifest(id, 1UL)).ToList();
            return Task.FromResult(new CurrentPicsResult(appId, CurrentPicsBuildId, depots));
        }

        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(
            ManifestSpec spec, bool probeOneChunk, CancellationToken ct) =>
            throw new NotSupportedException("probe not exercised by this fake");

        // --binaries-only path capture.
        public int BinariesOnlyInvocationCount;
        public string LastBinariesOnlyPlatform = string.Empty;
        public ManifestSpec? LastBinariesOnlySpec;
        public Func<uint, IReadOnlyList<uint>, uint, string, string, ManifestSpec?, CancellationToken, Task<AcquireResult>>? OnBinariesOnly { get; init; }

        /// <summary>
        /// Build id captured by the binary-depot AcquireBinariesOnlyAsync leg ONLY. Unlike the shared
        /// <see cref="LastBuildId"/>, this is NOT clobbered by the co-located content leg (which calls
        /// AcquireContentPakAsync with buildId 0), so a test can assert the build id the binary
        /// acquire actually received on the unified forward path.
        /// </summary>
        public uint LastBinariesOnlyBuildId;

        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint appId, IReadOnlyList<uint> depotIds, uint buildId, string outDir, string platform,
            ManifestSpec? explicitSpec, CancellationToken ct)
        {
            BinariesOnlyInvocationCount++;
            LastAppId = appId;
            LastDepots = depotIds;
            LastBuildId = explicitSpec?.BuildId ?? buildId;
            LastBinariesOnlyBuildId = explicitSpec?.BuildId ?? buildId;
            LastOutDir = outDir;
            LastBinariesOnlyPlatform = platform;
            LastBinariesOnlySpec = explicitSpec;
            if (OnBinariesOnly is not null)
                return OnBinariesOnly(appId, depotIds, buildId, outDir, platform, explicitSpec, ct);
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: explicitSpec?.BuildId ?? (buildId == 0 ? 12345u : buildId),
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: Array.Empty<AcquiredFileInfo>(),
                TotalBytes: 0));
        }

        // --content path capture.
        public int ContentInvocationCount;
        public uint ContentDepotId;
        public bool ContentMinimal;
        public bool ContentDirOnly;
        public ManifestSpec? ContentExplicitSpec;
        public Func<uint, uint, uint, string, bool, CancellationToken, Task<AcquireResult>>? OnContent { get; init; }

        public Task<AcquireResult> AcquireContentPakAsync(
            uint appId, uint contentDepotId, uint buildId, string outDir, bool minimalGameEvents,
            ManifestSpec? explicitSpec, bool dirOnly, CancellationToken ct)
        {
            ContentInvocationCount++;
            LastAppId = appId;
            ContentDepotId = contentDepotId;
            LastBuildId = buildId;
            LastOutDir = outDir;
            ContentMinimal = minimalGameEvents;
            ContentDirOnly = dirOnly;
            ContentExplicitSpec = explicitSpec;
            if (OnContent is not null)
                return OnContent(appId, contentDepotId, buildId, outDir, minimalGameEvents, ct);
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: buildId == 0 ? 12345u : buildId,
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: Array.Empty<AcquiredFileInfo>(),
                TotalBytes: 0));
        }

        // --tools path capture.
        public int ToolsInvocationCount;
        public uint ToolsDepotId;
        public ManifestSpec? ToolsExplicitSpec;
        public Func<uint, uint, uint, string, ManifestSpec?, CancellationToken, Task<AcquireResult>>? OnTools { get; init; }

        public Task<AcquireResult> AcquireToolsAsync(
            uint appId, uint toolsDepotId, uint buildId, string outDir,
            ManifestSpec? explicitSpec, CancellationToken ct)
        {
            ToolsInvocationCount++;
            LastAppId = appId;
            ToolsDepotId = toolsDepotId;
            LastBuildId = buildId;
            LastOutDir = outDir;
            ToolsExplicitSpec = explicitSpec;
            if (OnTools is not null)
                return OnTools(appId, toolsDepotId, buildId, outDir, explicitSpec, ct);
            return Task.FromResult(new AcquireResult(
                OutDir: outDir,
                ResolvedBuildId: explicitSpec?.BuildId ?? (buildId == 0 ? 12345u : buildId),
                Depots: Array.Empty<AcquiredDepotInfo>(),
                Files: Array.Empty<AcquiredFileInfo>(),
                TotalBytes: 0));
        }
    }

    [Fact]
    public async Task Help_flag_exits_zero_and_does_not_invoke_acquirer()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsHelp, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(0, fake.InvocationCount);
    }

    [Fact]
    public async Task Missing_build_exits_64()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsMissingBuild, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.InvocationCount);
    }

    [Fact]
    public async Task Missing_platform_exits_64()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsMissingPlatform, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.InvocationCount);
    }

    [Fact]
    public async Task Unknown_platform_exits_64_without_steam_contact()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsUnknownPlatform, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.InvocationCount);
    }

    [Fact]
    public async Task Retired_tuple_name_exits_64_without_steam_contact()
    {
        // The old 4-tuple names (e.g. linux-x86_64.server) are no longer valid
        // platforms and must be rejected before any Steam contact.
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsRetiredTupleName, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.InvocationCount);
    }

    [Fact]
    public async Task Non_integer_build_exits_64()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsNonIntegerBuild, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.InvocationCount);
    }

    [Fact]
    public async Task Latest_passes_buildid_zero_to_acquirer()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsLatestLinux, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);
        // 'latest' is still passed to AcquireBinariesOnlyAsync as buildId 0 (it resolves the
        // manifest server-side); the PICS probe only resolves the PATH component.
        Assert.Equal(0u, fake.LastBuildId);
    }

    [Fact]
    public async Task Latest_keys_default_outdir_off_resolved_build_id()
    {
        // --build latest with no --out: the command resolves the real build_id via
        // a PICS probe and uses THAT (not the literal 'latest') in the path.
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var code = await AcquireCommand.RunAsync(ArgsLatestLinux, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.CurrentPicsCount);
        var expectedSuffix = Path.Combine("cache", "binaries", "23669931", "linux-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);
        Assert.DoesNotContain("latest", fake.LastOutDir);
    }

    [Fact]
    public async Task Latest_with_explicit_out_skips_pics_probe()
    {
        // An explicit --out wins verbatim — no need to resolve the build_id for
        // the path, so no PICS probe is issued.
        var fake = new FakeAcquirer();
        var customOut = Path.Combine(Path.GetTempPath(), "cs2-latest-out-" + Guid.NewGuid().ToString("N"));
        var args = new[] { "--build", "latest", "--platform", "linux-x86_64", "--out", customOut };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(0, fake.CurrentPicsCount);
        Assert.Equal(Path.GetFullPath(customOut), fake.LastOutDir);
    }

    // ----- UNIFIED ACQUIRE (Gap A): default 'latest' fetches binaries + co-located content -----

    [Fact]
    public async Task Latest_unified_also_fetches_colocated_content()
    {
        // Gap A: the default 'latest' forward path now fetches the selective content pak into the
        // SAME outDir as the binaries, so a single extract emits every content artifact. The binary
        // leg and the content leg both run, against the same resolved dir, with a SINGLE PICS probe
        // (the content leg reuses the already-resolved outDir and must NOT re-probe).
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var code = await AcquireCommand.RunAsync(ArgsLatestLinux, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binary leg (minimal-footprint filter)
        Assert.Equal(1, fake.ContentInvocationCount);                // content leg (NEW)
        Assert.Equal(1, fake.CurrentPicsCount);                      // single probe — content did not re-probe
        Assert.Equal(SteamAppIdMap.Cs2SharedContentDepotId, fake.ContentDepotId);
        Assert.True(fake.ContentMinimal);                            // selective, not the ~59 GB depot
        Assert.False(fake.ContentDirOnly);
        var expectedSuffix = Path.Combine("cache", "binaries", "23669931", "linux-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);            // content co-located with binaries
    }

    [Fact]
    public async Task Integer_build_unified_also_fetches_colocated_content()
    {
        // Co-location consistency: the forward PICS-current path now co-locates content for the
        // CURRENT build whether requested as 'latest' OR as the concrete current build_id by number.
        // `extract` auto-acquires with the concrete build id it was given, so a content-LESS dir here
        // would silently drop all 7 content artifacts. Anonymous Steam only resolves the CURRENT
        // manifest, so this content leg only ever runs when the requested build IS current (a
        // non-current --build <number> fails at binary acquisition before reaching here).
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsIntegerLinux, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binary leg (minimal-footprint filter)
        Assert.Equal(1, fake.ContentInvocationCount);                // content leg (co-located)
        Assert.Equal(SteamAppIdMap.Cs2SharedContentDepotId, fake.ContentDepotId);
        Assert.True(fake.ContentMinimal);                            // selective, not the ~59 GB depot
        Assert.False(fake.ContentDirOnly);
        // A concrete integer build needs NO PICS probe — neither the binary leg nor the content leg
        // re-probes (single-probe contract: content reuses the already-resolved outDir).
        Assert.Equal(0, fake.CurrentPicsCount);
        var expectedSuffix = Path.Combine("cache", "binaries", "1555", "linux-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);            // content co-located with binaries
    }

    [Fact]
    public async Task Integer_build_unified_content_failure_aborts_acquire_65()
    {
        // Fail-loud holds for the concrete-current-build form too: a content-leg
        // failure makes the unified set incomplete, so the whole acquire aborts non-zero.
        var fake = new FakeAcquirer
        {
            OnContent = (_, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidDataException("synthetic content failure")),
        };
        var code = await AcquireCommand.RunAsync(ArgsIntegerLinux, () => fake);
        Assert.Equal(65, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binaries succeeded first
        Assert.Equal(1, fake.ContentInvocationCount);                // content attempted, then threw
    }

    [Fact]
    public async Task BinariesOnly_integer_build_skips_content_leg()
    {
        // --binaries-only is the explicit opt-out of the content leg — it must NOT co-locate content
        // even for the concrete current build (it routes to the binaries-only acquire path).
        var fake = new FakeAcquirer();
        var args = new[] { "--binaries-only", "--build", "1555", "--platform", "linux-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binaries-only path
        Assert.Equal(0, fake.InvocationCount);                       // NOT the full binary path
        Assert.Equal(0, fake.ContentInvocationCount);                // NO content leg
    }

    [Fact]
    public async Task Latest_unified_content_failure_aborts_acquire_65()
    {
        // Fail-loud: a content-leg failure makes the unified set incomplete, so the
        // whole acquire aborts non-zero (the binaries already on disk are left for a re-run).
        var fake = new FakeAcquirer
        {
            OnContent = (_, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidDataException("synthetic content failure")),
        };
        var code = await AcquireCommand.RunAsync(ArgsLatestLinux, () => fake);
        Assert.Equal(65, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binaries succeeded first
        Assert.Equal(1, fake.ContentInvocationCount);                // content attempted, then threw
    }

    [Fact]
    public async Task Integer_build_passes_through_to_acquirer()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsIntegerLinux, () => fake);
        Assert.Equal(0, code);
        // Assert the build id the BINARY leg received (LastBuildId is now clobbered by the
        // co-located content leg's buildId-0 call on the unified forward path).
        Assert.Equal(1555u, fake.LastBinariesOnlyBuildId);
        // App ID 730 — CS2 is one app for every platform.
        Assert.Equal(SteamAppIdMap.Cs2AppId, fake.LastAppId);
        // An explicit integer build needs no PICS probe for the path (neither leg re-probes).
        Assert.Equal(0, fake.CurrentPicsCount);
    }

    [Fact]
    public async Task Hash_mismatch_exception_exits_65()
    {
        var fake = new FakeAcquirer
        {
            OnBinariesOnly = (_, _, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidDataException("synthetic hash mismatch")),
        };
        var code = await AcquireCommand.RunAsync(ArgsHashMismatch, () => fake);
        Assert.Equal(65, code);
    }

    [Fact]
    public async Task Pics_failure_exits_65()
    {
        var fake = new FakeAcquirer
        {
            OnBinariesOnly = (_, _, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidOperationException("synthetic PICS failure")),
        };
        var code = await AcquireCommand.RunAsync(ArgsHashMismatch, () => fake);
        Assert.Equal(65, code);
    }

    [Fact]
    public async Task Unknown_failure_exits_1()
    {
        var fake = new FakeAcquirer
        {
            OnBinariesOnly = (_, _, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new System.Net.Sockets.SocketException()),
        };
        var code = await AcquireCommand.RunAsync(ArgsHashMismatch, () => fake);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Default_outdir_is_cache_binaries_build_platform()
    {
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsHashMismatch, () => fake);
        Assert.Equal(0, code);
        // Default goes through Path.GetFullPath, so we check the suffix.
        var expectedSuffix = Path.Combine("cache", "binaries", "1555", "linux-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);
    }

    // ----- --content -----

    [Fact]
    public async Task Content_routes_to_content_acquire_minimal_by_default()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--content", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.ContentInvocationCount);
        Assert.Equal(0, fake.InvocationCount);                       // not the binary path
        Assert.Equal(SteamAppIdMap.Cs2SharedContentDepotId, fake.ContentDepotId);
        Assert.Equal(23669931u, fake.LastBuildId);
        Assert.True(fake.ContentMinimal);                           // minimal unless --full-pak
        var expectedSuffix = Path.Combine("cache", "binaries", "23669931", "windows-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);
    }

    [Fact]
    public async Task Content_dir_only_threads_dironly_true_to_acquirer()
    {
        // gameevents-dedup: --dir-only must reach the acquirer as dirOnly=true.
        var fake = new FakeAcquirer();
        var args = new[] { "--content", "--dir-only", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.ContentInvocationCount);
        Assert.True(fake.ContentDirOnly);
        Assert.Equal(23669931u, fake.LastBuildId);
    }

    [Fact]
    public async Task Content_without_dir_only_threads_dironly_false()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--content", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.False(fake.ContentDirOnly);
    }

    [Fact]
    public async Task Content_full_pak_disables_minimal()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--content", "--full-pak", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.ContentInvocationCount);
        Assert.False(fake.ContentMinimal);
    }

    [Fact]
    public async Task Content_missing_build_exits_64()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--content", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.ContentInvocationCount);
    }

    [Fact]
    public async Task Content_explicit_out_overrides_default()
    {
        var fake = new FakeAcquirer();
        var customOut = Path.Combine(Path.GetTempPath(), "cs2-content-" + Guid.NewGuid().ToString("N"));
        var args = new[] { "--content", "--build", "23669931", "--platform", "windows-x86_64", "--out", customOut };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(Path.GetFullPath(customOut), fake.LastOutDir);
    }

    [Fact]
    public async Task Content_no_gameevents_dataerror_exits_65()
    {
        // an empty gameevents selection surfaces as InvalidDataException.
        var fake = new FakeAcquirer
        {
            OnContent = (_, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidDataException("no .gameevents entries")),
        };
        var args = new[] { "--content", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(65, code);
    }

    [Fact]
    public async Task Explicit_outdir_overrides_default()
    {
        var fake = new FakeAcquirer();
        var customOut = Path.Combine(Path.GetTempPath(), "cs2-acquire-test-" + Guid.NewGuid().ToString("N"));
        var args = new[]
        {
            "--build", "1555",
            "--platform", "linux-x86_64",
            "--out", customOut,
        };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(Path.GetFullPath(customOut), fake.LastOutDir);
    }

    // ----- --binaries-only (backfill) -----

    [Fact]
    public async Task BinariesOnly_routes_to_binaries_acquire_with_platform()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--binaries-only", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);
        Assert.Equal(0, fake.InvocationCount);                       // NOT the full binary path
        Assert.Equal(0, fake.ContentInvocationCount);                // NOT the content path
        Assert.Equal("windows-x86_64", fake.LastBinariesOnlyPlatform);
        Assert.Equal(23669931u, fake.LastBuildId);
        Assert.Equal(SteamAppIdMap.Cs2AppId, fake.LastAppId);
        Assert.Null(fake.LastBinariesOnlySpec);                      // PICS-current sourcing
        var expectedSuffix = Path.Combine("cache", "binaries", "23669931", "windows-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);
    }

    [Fact]
    public async Task BinariesOnly_passes_correct_depot_for_linux()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--binaries-only", "--build", "1555", "--platform", "linux-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal("linux-x86_64", fake.LastBinariesOnlyPlatform);
        Assert.Equal(ExpectedLinuxBinDepots, fake.LastDepots);
    }

    [Fact]
    public async Task BinariesOnly_missing_build_and_manifest_exits_64()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--binaries-only", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.BinariesOnlyInvocationCount);
    }

    [Fact]
    public async Task BinariesOnly_non_integer_build_exits_64()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--binaries-only", "--build", "not-a-number", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.BinariesOnlyInvocationCount);
    }

    [Fact]
    public async Task BinariesOnly_explicit_out_overrides_default()
    {
        var fake = new FakeAcquirer();
        var customOut = Path.Combine(Path.GetTempPath(), "cs2-binonly-" + Guid.NewGuid().ToString("N"));
        var args = new[] { "--binaries-only", "--build", "23669931", "--platform", "windows-x86_64", "--out", customOut };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(Path.GetFullPath(customOut), fake.LastOutDir);
    }

    [Fact]
    public async Task BinariesOnly_latest_resolves_path_buildid_via_pics()
    {
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var args = new[] { "--binaries-only", "--build", "latest", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.CurrentPicsCount);
        Assert.DoesNotContain("latest", fake.LastOutDir);
        var expectedSuffix = Path.Combine("cache", "binaries", "23669931", "windows-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);
    }

    // ----- --tools (Workshop Tools editor-DLL co-location) -----

    private static readonly string[] ArgsToolsLatestWindows =
        { "--tools", "--build", "latest", "--platform", "windows-x86_64" };
    private static readonly string[] ArgsToolsIntegerWindows =
        { "--tools", "--build", "1555", "--platform", "windows-x86_64" };
    private static readonly string[] ArgsToolsLinux =
        { "--tools", "--build", "latest", "--platform", "linux-x86_64" };

    [Fact]
    public async Task Tools_latest_windows_runs_unified_tools_leg_colocated()
    {
        // --tools adds a UNIFIED tools leg after binaries + content: PICS-current (buildId 0),
        // depot 2347779, into the SAME resolved outDir, with a SINGLE PICS probe (no re-probe).
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var code = await AcquireCommand.RunAsync(ArgsToolsLatestWindows, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binary leg (minimal-footprint filter)
        Assert.Equal(1, fake.ContentInvocationCount);                // content leg
        Assert.Equal(1, fake.ToolsInvocationCount);                  // tools leg (NEW)
        Assert.Equal(1, fake.CurrentPicsCount);                      // single probe
        Assert.Equal(SteamAppIdMap.Cs2WorkshopToolsDepotId, fake.ToolsDepotId);
        Assert.Null(fake.ToolsExplicitSpec);                         // PICS-current sourcing
        var expectedSuffix = Path.Combine("cache", "binaries", "23669931", "windows-x86_64");
        Assert.EndsWith(expectedSuffix, fake.LastOutDir);            // tools co-located with binaries
    }

    [Fact]
    public async Task Tools_now_default_on_for_windows_without_the_flag()
    {
        // Schema-coverage default: the tools leg now rides the unified windows acquire
        // AUTOMATICALLY — no --tools flag needed.
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var args = new[] { "--build", "latest", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.ToolsInvocationCount);
    }

    [Fact]
    public async Task Tools_default_on_linux_never_attempts_tools_leg()
    {
        // The tools depot is windows-only; the linux platform never gets a default tools leg
        // (silently, like every other platform-scoped leg — not a usage error).
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var args = new[] { "--build", "latest", "--platform", "linux-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(0, fake.ToolsInvocationCount);
    }

    [Fact]
    public async Task No_tools_flag_opts_out_of_the_default_tools_leg()
    {
        var fake = new FakeAcquirer { CurrentPicsBuildId = 23669931u };
        var args = new[] { "--build", "latest", "--platform", "windows-x86_64", "--no-tools" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(0, fake.ToolsInvocationCount);
    }

    [Fact]
    public async Task Tools_and_no_tools_are_mutually_exclusive()
    {
        var fake = new FakeAcquirer();
        var args = new[] { "--tools", "--no-tools", "--build", "latest", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.ToolsInvocationCount);
    }

    [Fact]
    public async Task Default_implied_tools_failure_is_best_effort_and_does_not_abort()
    {
        // Schema-coverage default: unlike an EXPLICIT --tools request, a DEFAULT-implied tools leg
        // (no --tools flag) is best-effort — the DLC-gated depot needs an authenticated Steam logon,
        // so a failure here (e.g. anonymous access denied) must not abort an otherwise-clean
        // binaries+content acquire.
        var fake = new FakeAcquirer
        {
            CurrentPicsBuildId = 23669931u,
            OnBinariesOnly = null,
            OnTools = (_, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidOperationException("synthetic: anonymous access denied")),
        };
        var args = new[] { "--build", "latest", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);                                       // best-effort: still succeeds
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);
        Assert.Equal(1, fake.ContentInvocationCount);
    }

    [Fact]
    public async Task Tools_non_windows_platform_exits_2_without_steam_contact()
    {
        // The Workshop Tools depot ships windows editor DLLs only — any other platform is a hard
        // error (exit 2), surfaced BEFORE any acquire leg runs.
        var fake = new FakeAcquirer();
        var code = await AcquireCommand.RunAsync(ArgsToolsLinux, () => fake);
        Assert.Equal(2, code);
        Assert.Equal(0, fake.InvocationCount);
        Assert.Equal(0, fake.ContentInvocationCount);
        Assert.Equal(0, fake.ToolsInvocationCount);
    }

    [Fact]
    public async Task Tools_with_binaries_only_exits_64()
    {
        // --binaries-only opts out of every co-located leg; --tools requests one. Contradiction is
        // a usage error, never a silently-ignored flag.
        var fake = new FakeAcquirer();
        var args = new[] { "--tools", "--binaries-only", "--build", "1555", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.BinariesOnlyInvocationCount);
        Assert.Equal(0, fake.ToolsInvocationCount);
    }

    // ----- --tools cache-first RETROFIT (populated cache dir, record-authoritative) -----

    /// <summary>
    /// A throwaway populated cache dir: a dummy binary + a manifest-record.json listing the windows
    /// binary depot and (optionally) the Workshop Tools depot 2347779. The record — not file
    /// presence — is what the tools-aware cache-first HIT consults.
    /// </summary>
    private static string SeedPopulatedCacheDir(bool recordListsTools)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-tools-retrofit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "client.dll"), "fake-binary");
        var depots = recordListsTools
            ? """[{"depotId":2347771,"manifestId":"111","manifestCreatedUtc":"2026-01-01T00:00:00Z"},{"depotId":2347779,"manifestId":"777","manifestCreatedUtc":"2026-01-01T00:00:00Z"}]"""
            : """[{"depotId":2347771,"manifestId":"111","manifestCreatedUtc":"2026-01-01T00:00:00Z"}]""";
        File.WriteAllText(
            Path.Combine(dir, ManifestRecord.FileName),
            $$"""{"appId":730,"buildId":24134959,"depots":{{depots}}}""");
        return dir;
    }

    /// <summary>A throwaway inventory whose build 24134959 records a tools GID (or not).</summary>
    private static string SeedInventory(bool withToolsGid)
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2-tools-inv-" + Guid.NewGuid().ToString("N") + ".json");
        var tools = withToolsGid ? ", \"tools\": \"7895084913465193678\"" : "";
        File.WriteAllText(path, $$"""
            {
              "app": { "app_id": 730 },
              "depots": [
                { "depot_id": 2347771, "role": "binary", "platforms": ["windows-x86_64"] },
                { "depot_id": 2347779, "role": "tools",  "platforms": ["windows-x86_64"] }
              ],
              "builds": [
                { "build_id": 24134959, "binaries": { "windows-x86_64": "111" }{{tools}} }
              ]
            }
            """);
        return path;
    }

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task Tools_cache_hit_without_tools_record_retrofits_tools_only()
    {
        // THE retrofit case: the (build, platform) dir is already cached from a prior acquire and
        // its manifest-record.json lacks depot 2347779. The cache-first HIT must NOT short-circuit;
        // it acquires ONLY the missing tools leg (explicit spec from the inventory's builds[].tools
        // GID), leaving binaries/content untouched — --no-cache (a full re-download) is never needed.
        var outDir = SeedPopulatedCacheDir(recordListsTools: false);
        var inv = SeedInventory(withToolsGid: true);
        var fake = new FakeAcquirer();
        try
        {
            var args = new[]
            {
                "--tools", "--build", "24134959", "--platform", "windows-x86_64",
                "--out", outDir, "--inventory", inv,
            };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(1, fake.ToolsInvocationCount);              // ONLY the tools leg ran
            Assert.Equal(0, fake.InvocationCount);                   // binaries untouched
            Assert.Equal(0, fake.ContentInvocationCount);            // content untouched
            Assert.Equal(0, fake.CurrentPicsCount);                  // no PICS probe
            Assert.Equal(SteamAppIdMap.Cs2WorkshopToolsDepotId, fake.ToolsDepotId);
            Assert.NotNull(fake.ToolsExplicitSpec);                  // historical GID from the inventory
            Assert.Equal(24134959u, fake.ToolsExplicitSpec!.BuildId);
            Assert.Contains(fake.ToolsExplicitSpec.Depots,
                d => d.DepotId == SteamAppIdMap.Cs2WorkshopToolsDepotId && d.ManifestId == 7895084913465193678UL);
            Assert.Equal(Path.GetFullPath(outDir), fake.LastOutDir); // merged into the SAME cached dir
        }
        finally
        {
            BestEffortDelete(outDir);
            BestEffortDelete(inv);
        }
    }

    [Fact]
    public async Task Tools_cache_hit_with_tools_record_is_full_hit_no_acquirer_contact()
    {
        // Idempotent re-run: the record already lists 2347779 -> keep the cache-first HIT; the
        // acquirer is never contacted for any leg.
        var outDir = SeedPopulatedCacheDir(recordListsTools: true);
        var inv = SeedInventory(withToolsGid: true);
        var fake = new FakeAcquirer();
        try
        {
            var args = new[]
            {
                "--tools", "--build", "24134959", "--platform", "windows-x86_64",
                "--out", outDir, "--inventory", inv,
            };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(0, fake.ToolsInvocationCount);
            Assert.Equal(0, fake.InvocationCount);
            Assert.Equal(0, fake.ContentInvocationCount);
            Assert.Equal(0, fake.CurrentPicsCount);
        }
        finally
        {
            BestEffortDelete(outDir);
            BestEffortDelete(inv);
        }
    }

    [Fact]
    public async Task Tools_cache_hit_without_inventory_tools_gid_is_loud_skip()
    {
        // The cached dir lacks the tools record AND the inventory records no tools GID for the
        // build: the existing loud skip-of-record holds (exit 0, tools omitted, no fetch).
        var outDir = SeedPopulatedCacheDir(recordListsTools: false);
        var inv = SeedInventory(withToolsGid: false);
        var fake = new FakeAcquirer();
        try
        {
            var args = new[]
            {
                "--tools", "--build", "24134959", "--platform", "windows-x86_64",
                "--out", outDir, "--inventory", inv,
            };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(0, fake.ToolsInvocationCount);              // nothing to fetch, nothing fetched
            Assert.Equal(0, fake.InvocationCount);
        }
        finally
        {
            BestEffortDelete(outDir);
            BestEffortDelete(inv);
        }
    }

    [Fact]
    public async Task Tools_leg_failure_aborts_acquire_65()
    {
        // Fail-loud: a tools-leg failure makes the unified set incomplete, so the whole acquire
        // aborts non-zero (binaries + content already on disk are left for a re-run).
        var fake = new FakeAcquirer
        {
            OnTools = (_, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidDataException("synthetic tools failure")),
        };
        var code = await AcquireCommand.RunAsync(ArgsToolsIntegerWindows, () => fake);
        Assert.Equal(65, code);
        Assert.Equal(1, fake.BinariesOnlyInvocationCount);           // binaries succeeded first
        Assert.Equal(1, fake.ContentInvocationCount);                // content succeeded
        Assert.Equal(1, fake.ToolsInvocationCount);                  // tools attempted, then threw
    }

    [Fact]
    public async Task BinariesOnly_hash_mismatch_exits_65()
    {
        var fake = new FakeAcquirer
        {
            OnBinariesOnly = (_, _, _, _, _, _, _) =>
                Task.FromException<AcquireResult>(new InvalidDataException("synthetic hash mismatch")),
        };
        var args = new[] { "--binaries-only", "--build", "1555", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(65, code);
    }
}
