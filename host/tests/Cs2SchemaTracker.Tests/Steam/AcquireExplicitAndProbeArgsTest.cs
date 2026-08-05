// AcquireCommand explicit-manifest + probe argument-path tests.
//
// Exercises the new CLI surface without Steam:
//   --from-manifest <file> routes to AcquireExplicitAsync with the parsed spec
// a malformed/missing spec exits 64 BEFORE any acquirer call
//   --probe drives the probe runner and maps the verdict to an exit code
//
// A separate probe-capable fake is used for --probe so we can return canned
// CurrentPicsResult / ExplicitManifestProbe values and assert the exit mapping.

using System.IO;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class AcquireExplicitAndProbeArgsTest
{
    // ----- explicit-manifest mode ------------------------------------------

    private sealed class ExplicitFake : ISteamAcquirer
    {
        public int ExplicitCount;
        public ManifestSpec? LastSpec;
        public string LastOutDir = "";

        public Task<AcquireResult> AcquireAsync(uint a, System.Collections.Generic.IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new System.NotSupportedException("PICS path not expected in explicit test");

        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec spec, string outDir, CancellationToken ct)
        {
            ExplicitCount++;
            LastSpec = spec;
            LastOutDir = outDir;
            return Task.FromResult(new AcquireResult(
                outDir, spec.BuildId,
                System.Array.Empty<AcquiredDepotInfo>(),
                System.Array.Empty<AcquiredFileInfo>(), 0));
        }

        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(uint a, System.Collections.Generic.IReadOnlyList<uint> d, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(ManifestSpec s, bool ch, CancellationToken c)
            => throw new System.NotSupportedException();
        public int ContentCount;
        public ManifestSpec? LastContentSpec;
        public uint LastContentBuildId;
        public bool LastContentDirOnly;
        public Task<AcquireResult> AcquireContentPakAsync(
            uint a, uint cd, uint b, string o, bool m, ManifestSpec? es, bool dirOnly, CancellationToken c)
        {
            ContentCount++;
            LastContentSpec = es;
            LastContentBuildId = b;
            LastContentDirOnly = dirOnly;
            LastOutDir = o;
            return Task.FromResult(new AcquireResult(
                o, es?.BuildId ?? b,
                System.Array.Empty<AcquiredDepotInfo>(),
                System.Array.Empty<AcquiredFileInfo>(), 0));
        }

        public int BinariesOnlyCount;
        public ManifestSpec? LastBinariesOnlySpec;
        public string LastBinariesOnlyPlatform = "";
        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint a, System.Collections.Generic.IReadOnlyList<uint> d, uint b, string o, string platform,
            ManifestSpec? explicitSpec, CancellationToken c)
        {
            BinariesOnlyCount++;
            LastBinariesOnlySpec = explicitSpec;
            LastBinariesOnlyPlatform = platform;
            LastOutDir = o;
            return Task.FromResult(new AcquireResult(
                o, explicitSpec?.BuildId ?? b,
                System.Array.Empty<AcquiredDepotInfo>(),
                System.Array.Empty<AcquiredFileInfo>(), 0));
        }

        public int ToolsCount;
        public ManifestSpec? LastToolsSpec;
        public uint LastToolsDepotId;
        public Task<AcquireResult> AcquireToolsAsync(
            uint a, uint td, uint b, string o, ManifestSpec? explicitSpec, CancellationToken c)
        {
            ToolsCount++;
            LastToolsSpec = explicitSpec;
            LastToolsDepotId = td;
            LastOutDir = o;
            return Task.FromResult(new AcquireResult(
                o, explicitSpec?.BuildId ?? b,
                System.Array.Empty<AcquiredDepotInfo>(),
                System.Array.Empty<AcquiredFileInfo>(), 0));
        }
    }

    private static string WriteSpec(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "cs2-spec-" + System.Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public async Task From_manifest_routes_to_explicit_acquire()
    {
        // The DEFAULT --from-manifest binary leg routes through the minimal-footprint
        // AcquireBinariesOnlyAsync (BinaryBinSelector filter) rather than the unfiltered
        // AcquireExplicitAsync, so a several-GB depot (shader VPKs / community-addon maps the walker
        // never touches) isn't fetched in full by default.
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 23669931, "depots": [
              { "depotId": 2347770, "manifestId": "5146470907583764090" },
              { "depotId": 2347771, "manifestId": "8287382081622299196" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(0, fake.ExplicitCount);                  // NOT the unfiltered full-depot path
            Assert.Equal(1, fake.BinariesOnlyCount);
            Assert.Equal("windows-x86_64", fake.LastBinariesOnlyPlatform);
            Assert.NotNull(fake.LastBinariesOnlySpec);
            Assert.Equal(23669931u, fake.LastBinariesOnlySpec!.BuildId);
            Assert.Equal(2, fake.LastBinariesOnlySpec.Depots.Count);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task From_manifest_with_content_depot_also_fetches_colocated_content()
    {
        // UNIFIED ACQUIRE (Gap A): an explicit historical spec that lists the 2347770 content depot
        // now ALSO fetches the selective content pak into the SAME outDir (binaries + content from one
        // invocation). The spec is threaded into the content leg so the PRIOR build's pak01 is fetched.
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 23669931, "depots": [
              { "depotId": 2347770, "manifestId": "5146470907583764090" },
              { "depotId": 2347771, "manifestId": "8287382081622299196" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(1, fake.BinariesOnlyCount);              // binary leg (minimal-footprint filter)
            Assert.Equal(1, fake.ContentCount);                   // content leg (NEW)
            Assert.NotNull(fake.LastContentSpec);
            Assert.Equal(23669931u, fake.LastContentSpec!.BuildId);
            Assert.Contains(fake.LastContentSpec.Depots, d => d.DepotId == 2347770);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task From_manifest_binaries_only_spec_skips_content()
    {
        // A spec WITHOUT the 2347770 content depot is binaries-only by construction — the content leg
        // has nothing to fetch and is skipped (no change for such specs).
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 23669931, "depots": [
              { "depotId": 2347771, "manifestId": "8287382081622299196" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(1, fake.BinariesOnlyCount);
            Assert.Equal(0, fake.ContentCount);                   // no content depot in the spec
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task From_manifest_with_tools_depot_also_fetches_colocated_tools()
    {
        // An explicit historical spec that lists the 2347779 Workshop Tools depot ALSO fetches the
        // editor-DLL slice into the SAME outDir. The spec is threaded into the tools leg so the
        // PRIOR build's tools manifest is fetched (not PICS-current).
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 24134959, "depots": [
              { "depotId": 2347771, "manifestId": "7679405674131902105" },
              { "depotId": 2347779, "manifestId": "7895084913465193678" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(1, fake.BinariesOnlyCount);              // binary leg (minimal-footprint filter)
            Assert.Equal(1, fake.ToolsCount);                     // tools leg (NEW)
            Assert.Equal(SteamAppIdMap.Cs2WorkshopToolsDepotId, fake.LastToolsDepotId);
            Assert.NotNull(fake.LastToolsSpec);
            Assert.Equal(24134959u, fake.LastToolsSpec!.BuildId);
            Assert.Contains(fake.LastToolsSpec.Depots, d => d.DepotId == 2347779);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task From_manifest_tools_depot_on_linux_platform_exits_2()
    {
        // The Workshop Tools depot is windows-only: a spec carrying 2347779 under --platform
        // linux-x86_64 is a hard error (exit 2) — the DLLs must never merge into a linux dir.
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 24134959, "depots": [
              { "depotId": 2347773, "manifestId": "5502194087696430282" },
              { "depotId": 2347779, "manifestId": "7895084913465193678" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "linux-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(2, code);
            Assert.Equal(0, fake.BinariesOnlyCount);              // validated BEFORE any acquire
            Assert.Equal(0, fake.ToolsCount);                     // tools leg never ran
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task From_manifest_tools_flag_without_tools_depot_exits_64()
    {
        // --tools with a spec that lacks 2347779 is fail-loud: the operator asked for a leg the
        // spec cannot pin (falling back to PICS-current would fetch the WRONG build).
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 23669931, "depots": [
              { "depotId": 2347771, "manifestId": "8287382081622299196" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--tools", "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(64, code);
            Assert.Equal(0, fake.BinariesOnlyCount);              // validated BEFORE any acquire
            Assert.Equal(0, fake.ToolsCount);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task From_manifest_missing_file_exits_64_without_acquire()
    {
        var fake = new ExplicitFake();
        var missing = Path.Combine(Path.GetTempPath(), "nope-" + System.Guid.NewGuid().ToString("N") + ".json");
        var args = new[] { "--from-manifest", missing, "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.BinariesOnlyCount);
    }

    [Fact]
    public async Task From_manifest_malformed_exits_64_without_acquire()
    {
        var fake = new ExplicitFake();
        var specPath = WriteSpec("""{ "appId": 730 }""");  // no depots
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(64, code);
            Assert.Equal(0, fake.BinariesOnlyCount);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task BinariesOnly_from_manifest_routes_to_binaries_acquire_with_spec()
    {
        // backfill: --binaries-only + --from-manifest is the HISTORICAL
        // path — the per-depot GIDs come from the spec and route to the filtered
        // binary acquire (NOT the full-depot AcquireExplicitAsync).
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 23669931, "depots": [
              { "depotId": 2347771, "manifestId": "8287382081622299196" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--binaries-only", "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(1, fake.BinariesOnlyCount);
            Assert.Equal(0, fake.ExplicitCount);                 // NOT the full-depot explicit path
            Assert.NotNull(fake.LastBinariesOnlySpec);
            Assert.Equal(23669931u, fake.LastBinariesOnlySpec!.BuildId);
            Assert.Equal("windows-x86_64", fake.LastBinariesOnlyPlatform);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task BinariesOnly_from_manifest_data_failure_exits_65()
    {
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 1, "depots": [ { "depotId": 2347771, "manifestId": "9" } ] }
            """);
        var fake = new ThrowingExplicitFake(new InvalidDataException("synthetic purge / hash"));
        try
        {
            var args = new[] { "--binaries-only", "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(65, code);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    // ----- --content HISTORICAL (gameevents backfill) -----------------

    [Fact]
    public async Task Content_from_manifest_routes_to_content_with_spec()
    {
        // --content + --from-manifest carrying the 2347770 content depot GID
        // routes to the historical content acquire (NOT PICS-current, NOT the
        // full-depot explicit path). The spec is threaded through verbatim.
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 10832117, "depots": [
              { "depotId": 2347770, "manifestId": "1111111111111111111" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--content", "--from-manifest", specPath, "--build", "10832117", "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.Equal(1, fake.ContentCount);
            Assert.Equal(0, fake.ExplicitCount);                  // NOT the full-depot explicit path
            Assert.NotNull(fake.LastContentSpec);
            Assert.Equal(10832117u, fake.LastContentSpec!.BuildId);
            Assert.Contains(fake.LastContentSpec.Depots, d => d.DepotId == 2347770);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task Content_from_manifest_without_content_depot_exits_64()
    {
        // Fail loud: a spec that lacks the 2347770 content depot cannot
        // back a gameevents acquire — reject before any acquirer call.
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 10832117, "depots": [
              { "depotId": 2347771, "manifestId": "8287382081622299196" }
            ] }
            """);
        var fake = new ExplicitFake();
        try
        {
            var args = new[] { "--content", "--from-manifest", specPath, "--build", "10832117", "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(64, code);
            Assert.Equal(0, fake.ContentCount);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    [Fact]
    public async Task Content_specific_build_resolves_historical_gid_from_history()
    {
        // a SPECIFIC --build with no --from-manifest resolves the historical
        // 2347770 GID from KnownManifestHistory. Build 23669931 is seeded with a
        // 2347770 entry, so the content acquire gets a spec carrying that depot.
        var fake = new ExplicitFake();
        var args = new[] { "--content", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.ContentCount);
        Assert.NotNull(fake.LastContentSpec);
        Assert.Equal(23669931u, fake.LastContentSpec!.BuildId);
        Assert.Contains(fake.LastContentSpec.Depots, d => d.DepotId == 2347770);
    }

    [Fact]
    public async Task Content_specific_unknown_build_exits_64()
    {
        // A specific build NOT in recorded history (and no --from-manifest) has an
        // unknown historical 2347770 GID — fail loud before any acquire.
        var fake = new ExplicitFake();
        var args = new[] { "--content", "--build", "999999", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.ContentCount);
    }

    [Fact]
    public async Task Explicit_data_failure_exits_65()
    {
        var specPath = WriteSpec("""
            { "appId": 730, "buildId": 1, "depots": [ { "depotId": 5, "manifestId": "9" } ] }
            """);
        var fake = new ThrowingExplicitFake(new InvalidDataException("synthetic purge / hash"));
        try
        {
            var args = new[] { "--from-manifest", specPath, "--platform", "windows-x86_64" };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(65, code);
        }
        finally
        {
            try
            { File.Delete(specPath); }
            catch { }
        }
    }

    private sealed class ThrowingExplicitFake : ISteamAcquirer
    {
        private readonly System.Exception _ex;
        public ThrowingExplicitFake(System.Exception ex) => _ex = ex;
        public Task<AcquireResult> AcquireAsync(uint a, System.Collections.Generic.IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec s, string o, CancellationToken c)
            => Task.FromException<AcquireResult>(_ex);
        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(uint a, System.Collections.Generic.IReadOnlyList<uint> d, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(ManifestSpec s, bool ch, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<AcquireResult> AcquireContentPakAsync(uint a, uint cd, uint b, string o, bool m, ManifestSpec? es, bool dirOnly, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint a, System.Collections.Generic.IReadOnlyList<uint> d, uint b, string o, string platform,
            ManifestSpec? explicitSpec, CancellationToken c)
            => Task.FromException<AcquireResult>(_ex);
        public Task<AcquireResult> AcquireToolsAsync(
            uint a, uint td, uint b, string o, ManifestSpec? explicitSpec, CancellationToken c)
            => throw new System.NotSupportedException();
    }

    // ----- probe mode ------------------------------------------------------

    private sealed class ProbeFake : ISteamAcquirer
    {
        private readonly uint _currentBuild;
        private readonly bool _historicalFetchable;
        public int CurrentProbeCount;
        public int ExplicitProbeCount;

        public ProbeFake(uint currentBuild, bool historicalFetchable)
        {
            _currentBuild = currentBuild;
            _historicalFetchable = historicalFetchable;
        }

        public Task<AcquireResult> AcquireAsync(uint a, System.Collections.Generic.IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec s, string o, CancellationToken c)
            => throw new System.NotSupportedException();

        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(
            uint appId, System.Collections.Generic.IReadOnlyList<uint> depotIds, CancellationToken ct)
        {
            CurrentProbeCount++;
            var depots = depotIds.OrderBy(x => x)
                .Select(id => new CurrentDepotManifest(id, 99999UL)).ToList();
            return Task.FromResult(new CurrentPicsResult(appId, _currentBuild, depots));
        }

        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(
            ManifestSpec spec, bool probeOneChunk, CancellationToken ct)
        {
            ExplicitProbeCount++;
            var depots = spec.OrderedDepots.Select(d => new ExplicitDepotManifestProbe(
                d.DepotId, d.ManifestId,
                ManifestFetched: _historicalFetchable,
                ManifestCreatedUtc: _historicalFetchable ? "2026-06-10T00:00:00Z" : null,
                FileCount: _historicalFetchable ? 10 : 0,
                TotalUncompressedBytes: _historicalFetchable ? 1234 : 0,
                ChunkProbeAttempted: false,
                SampleChunkFetched: false,
                SampleChunkSha1: null,
                Error: _historicalFetchable ? null : "synthetic: manifest purged")).ToList();
            return Task.FromResult(new ExplicitManifestProbe(spec.AppId, spec.BuildId, depots));
        }

        public Task<AcquireResult> AcquireContentPakAsync(uint a, uint cd, uint b, string o, bool m, ManifestSpec? es, bool dirOnly, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint a, System.Collections.Generic.IReadOnlyList<uint> d, uint b, string o, string platform,
            ManifestSpec? explicitSpec, CancellationToken c)
            => throw new System.NotSupportedException();
        public Task<AcquireResult> AcquireToolsAsync(
            uint a, uint td, uint b, string o, ManifestSpec? explicitSpec, CancellationToken c)
            => throw new System.NotSupportedException();
    }

    [Fact]
    public async Task Probe_recorded_build_fetchable_exits_0()
    {
        var fake = new ProbeFake(currentBuild: 23999999, historicalFetchable: true);
        var args = new[] { "--probe", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(0, code);
        Assert.Equal(1, fake.CurrentProbeCount);
        Assert.Equal(1, fake.ExplicitProbeCount);
    }

    [Fact]
    public async Task Probe_recorded_build_purged_exits_65()
    {
        var fake = new ProbeFake(currentBuild: 23999999, historicalFetchable: false);
        var args = new[] { "--probe", "--build", "23669931", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(65, code);
    }

    [Fact]
    public async Task Probe_unknown_build_exits_64()
    {
        var fake = new ProbeFake(currentBuild: 1, historicalFetchable: true);
        var args = new[] { "--probe", "--build", "999", "--platform", "windows-x86_64" };
        var code = await AcquireCommand.RunAsync(args, () => fake);
        Assert.Equal(64, code);
    }
}
