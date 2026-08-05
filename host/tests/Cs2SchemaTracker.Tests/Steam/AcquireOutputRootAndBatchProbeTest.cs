// `acquire` output-root resolution (CS2_BINARIES_ROOT) + batch --probe safety.
//
// Two confirmed AcquireCommand bugs, regression-locked here:
//
//   Bug 1 — the DEFAULT acquire output root must honor the binaries-store root
//   (env CS2_BINARIES_ROOT, else appsettings BinariesRoot; env wins), the SAME
//   location `extract` reads. Precedence: --out (explicit) > CS2_BINARIES_ROOT >
//   cache/binaries. Asserted for BOTH the single-build path (via the fake acquirer's
//   captured outDir) and the batch path (via the .acq-done marker location).
//
//   Bug 2 — `--probe` in batch mode (--all / repeated --build) must NEVER trigger a
//   bulk download. The fake records every bulk-fetch call; a batch probe run must
//   leave that count at zero and instead drive the manifest-level probe seam
//   (ProbeCurrentPicsAsync + ProbeExplicitManifestAsync), exiting non-zero when any
//   selected build's historical manifest is unreachable.
//
// The class mutates the process-global CS2_BINARIES_ROOT env var, so it joins the
// serialized "env-mutating" collection (DisableParallelization) and snapshots +
// restores the var in a finally. Deterministic: throwaway temp dirs, no wall-clock,
// no real Steam. CA1861: constant string[] argv are hoisted to static readonly fields.

using System.Globalization;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

[Collection("env-mutating")]
public sealed class AcquireOutputRootAndBatchProbeTest
{
    private const string Lin = "linux-x86_64";
    private const string Win = "windows-x86_64";
    private const string AcqDone = ".acq-done";
    private static string BinariesEnv => ExtractCommand.BinariesRootEnvVar;   // "CS2_BINARIES_ROOT"

    // A fake acquirer that (a) captures the outDir every acquire leg received and (b) counts
    // whether ANY bulk-download entry point was invoked (the load-bearing bit for the probe test),
    // while faithfully serving the manifest-level probe seam ManifestProbeRunner drives.
    private sealed class RecordingAcquirer : ISteamAcquirer
    {
        public string? LastOutDir;
        public readonly List<string> AcquiredOutDirs = new();
        public int BulkDownloadCount;      // Acquire / AcquireExplicit / AcquireContentPak / AcquireBinariesOnly
        public int ProbeCurrentCount;
        public int ProbeExplicitCount;

        public uint CurrentPicsBuildId { get; init; } = 1555u;
        /// <summary>Whether the explicit-manifest probe reports every depot fetchable.</summary>
        public bool ExplicitFetchable { get; init; } = true;

        private static AcquireResult Ok(string outDir, uint buildId) => new(
            OutDir: outDir, ResolvedBuildId: buildId, Depots: Array.Empty<AcquiredDepotInfo>(),
            Files: Array.Empty<AcquiredFileInfo>(), TotalBytes: 0);

        public Task<AcquireResult> AcquireAsync(
            uint appId, IReadOnlyList<uint> depotIds, uint buildId, string outDir, CancellationToken ct)
        {
            BulkDownloadCount++;
            LastOutDir = outDir;
            AcquiredOutDirs.Add(outDir);
            return Task.FromResult(Ok(outDir, buildId == 0 ? CurrentPicsBuildId : buildId));
        }

        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec spec, string outDir, CancellationToken ct)
        {
            BulkDownloadCount++;
            LastOutDir = outDir;
            AcquiredOutDirs.Add(outDir);
            return Task.FromResult(Ok(outDir, spec.BuildId));
        }

        public Task<AcquireResult> AcquireContentPakAsync(
            uint appId, uint contentDepotId, uint buildId, string outDir, bool minimalGameEvents,
            ManifestSpec? explicitSpec, bool dirOnly, CancellationToken ct)
        {
            BulkDownloadCount++;
            LastOutDir = outDir;
            AcquiredOutDirs.Add(outDir);
            return Task.FromResult(Ok(outDir, explicitSpec?.BuildId ?? (buildId == 0 ? CurrentPicsBuildId : buildId)));
        }

        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint appId, IReadOnlyList<uint> depotIds, uint buildId, string outDir, string platform,
            ManifestSpec? explicitSpec, CancellationToken ct)
        {
            BulkDownloadCount++;
            LastOutDir = outDir;
            AcquiredOutDirs.Add(outDir);
            return Task.FromResult(Ok(outDir, explicitSpec?.BuildId ?? (buildId == 0 ? CurrentPicsBuildId : buildId)));
        }

        public Task<AcquireResult> AcquireToolsAsync(
            uint appId, uint toolsDepotId, uint buildId, string outDir,
            ManifestSpec? explicitSpec, CancellationToken ct)
        {
            BulkDownloadCount++;
            LastOutDir = outDir;
            AcquiredOutDirs.Add(outDir);
            return Task.FromResult(Ok(outDir, explicitSpec?.BuildId ?? (buildId == 0 ? CurrentPicsBuildId : buildId)));
        }

        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(
            uint appId, IReadOnlyList<uint> depotIds, CancellationToken ct)
        {
            ProbeCurrentCount++;
            var depots = depotIds.OrderBy(x => x).Select(id => new CurrentDepotManifest(id, 1UL)).ToList();
            return Task.FromResult(new CurrentPicsResult(appId, CurrentPicsBuildId, depots));
        }

        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(
            ManifestSpec spec, bool probeOneChunk, CancellationToken ct)
        {
            ProbeExplicitCount++;
            var depots = spec.OrderedDepots.Select(d => new ExplicitDepotManifestProbe(
                DepotId: d.DepotId,
                ManifestId: d.ManifestId,
                ManifestFetched: ExplicitFetchable,
                ManifestCreatedUtc: "2026-01-01T00:00:00Z",
                FileCount: 1,
                TotalUncompressedBytes: 10,
                ChunkProbeAttempted: probeOneChunk,
                SampleChunkFetched: probeOneChunk && ExplicitFetchable,
                SampleChunkSha1: probeOneChunk ? "0000000000000000000000000000000000000000" : null,
                Error: ExplicitFetchable ? null : "synthetic unreachable")).ToList();
            return Task.FromResult(new ExplicitManifestProbe(spec.AppId, spec.BuildId, depots));
        }
    }

    private static string NewTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Run body with CS2_BINARIES_ROOT set to `value` (null clears it); snapshot + restore in finally.
    private static async Task WithBinariesRoot(string? value, Func<Task> body)
    {
        var prev = Environment.GetEnvironmentVariable(BinariesEnv);
        Environment.SetEnvironmentVariable(BinariesEnv, value);
        try
        { await body(); }
        finally { Environment.SetEnvironmentVariable(BinariesEnv, prev); }
    }

    // A throwaway inventory: app 730, the windows binary depot, and the given builds (each lists a
    // windows binary GID). No content depot => the unified batch's content leg is a no-op (omitted).
    private static string WriteWinInventory(string dir, params uint[] builds)
    {
        string Build(uint b) => $$"""
            { "build_id": {{b}}, "binaries": { "{{Win}}": "{{1000UL + b}}" } }
            """;
        var json = $$"""
        {
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "depots": [
            { "depot_id": {{SteamAppIdMap.Cs2WindowsBinariesDepotId}}, "role": "binary", "platforms": ["{{Win}}"], "history": [] }
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

    // ---- Bug 1: single-build default out honors CS2_BINARIES_ROOT ----------------------------

    private static readonly string[] SingleBuildArgs = { "--build", "1555", "--platform", Lin };

    [Fact]
    public async Task Single_Build_Default_Out_Honors_BinariesRoot()
    {
        var root = NewTempDir("acq-root-");
        await WithBinariesRoot(root, async () =>
        {
            var fake = new RecordingAcquirer();
            var code = await AcquireCommand.RunAsync(SingleBuildArgs, () => fake);
            Assert.Equal(0, code);
            // The DEFAULT out (no --out) is now <CS2_BINARIES_ROOT>/<build>/<platform>, not cache/binaries.
            Assert.Equal(Path.Combine(root, "1555", Lin), fake.LastOutDir);
        });
    }

    [Fact]
    public async Task Single_Build_Explicit_Out_Wins_Over_BinariesRoot()
    {
        var root = NewTempDir("acq-root-");
        var customOut = Path.Combine(Path.GetTempPath(), "acq-out-" + Guid.NewGuid().ToString("N"));
        await WithBinariesRoot(root, async () =>
        {
            var fake = new RecordingAcquirer();
            var args = new[] { "--build", "1555", "--platform", Lin, "--out", customOut };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            // Explicit --out wins verbatim even with the store root set (operator override).
            Assert.Equal(Path.GetFullPath(customOut), fake.LastOutDir);
        });
    }

    [Fact]
    public async Task Single_Build_No_BinariesRoot_Falls_Back_To_Cache_Binaries()
    {
        await WithBinariesRoot(null, async () =>
        {
            var fake = new RecordingAcquirer();
            var code = await AcquireCommand.RunAsync(SingleBuildArgs, () => fake);
            Assert.Equal(0, code);
            var expectedSuffix = Path.Combine("cache", "binaries", "1555", Lin);
            Assert.EndsWith(expectedSuffix, fake.LastOutDir);
        });
    }

    // ---- Bug 1: batch default out honors CS2_BINARIES_ROOT (marker location) ------------------

    [Fact]
    public async Task Batch_Default_Out_Honors_BinariesRoot()
    {
        var root = NewTempDir("acq-root-");
        var invDir = NewTempDir("acq-inv-");
        var inv = WriteWinInventory(invDir, 20000001u, 20000002u);
        await WithBinariesRoot(root, async () =>
        {
            var fake = new RecordingAcquirer();
            // Repeated --build => batch selection; NO --out, so each (build, platform) defaults under
            // the store root. The .acq-done marker lands in <root>/<build>/<platform>/.
            var args = new[] { "--build", "20000001", "--build", "20000002", "--platform", Win, "--inventory", inv };
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(root, "20000001", Win, AcqDone)));
            Assert.True(File.Exists(Path.Combine(root, "20000002", Win, AcqDone)));
            // Every binary leg wrote into the store-root-derived dir (never cache/binaries).
            Assert.All(fake.AcquiredOutDirs, d => Assert.StartsWith(root, d));
        });
    }

    [Fact]
    public async Task Batch_Explicit_Out_Wins_Over_BinariesRoot()
    {
        var root = NewTempDir("acq-root-");
        var outRoot = NewTempDir("acq-outroot-");
        var invDir = NewTempDir("acq-inv-");
        var inv = WriteWinInventory(invDir, 21000001u);
        await WithBinariesRoot(root, async () =>
        {
            var fake = new RecordingAcquirer();
            var args = new[] { "--build", "21000001", "--build", "21000001", "--platform", Win, "--inventory", inv, "--out", outRoot };
            // (repeated same id de-dups to one work item; --out is the batch ROOT that gets /<build>/<platform>.)
            var code = await AcquireCommand.RunAsync(args, () => fake);
            Assert.Equal(0, code);
            // --out root wins over the store root: marker under <outRoot>, not <root>.
            Assert.True(File.Exists(Path.Combine(outRoot, "21000001", Win, AcqDone)));
            Assert.False(File.Exists(Path.Combine(root, "21000001", Win, AcqDone)));
        });
    }

    // ---- Bug 2: batch --probe never downloads --------------------------------------------------

    [Fact]
    public async Task Batch_Probe_Does_Not_Download_And_Exits_Zero_When_Fetchable()
    {
        var invDir = NewTempDir("acq-inv-");
        var inv = WriteWinInventory(invDir, 22000001u, 22000002u);
        await WithBinariesRoot(null, async () =>
        {
            var fake = new RecordingAcquirer { ExplicitFetchable = true };
            var args = new[] { "--build", "22000001", "--build", "22000002", "--platform", Win, "--probe", "--inventory", inv };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);                       // every selected build's manifest is fetchable
            Assert.Equal(0, fake.BulkDownloadCount);     // THE hard requirement: no bulk download in probe mode
            Assert.True(fake.ProbeExplicitCount >= 2);   // manifest-level probe ran per selected build
        });
    }

    [Fact]
    public async Task Batch_Probe_Unreachable_Exits_NonZero_Still_No_Download()
    {
        var invDir = NewTempDir("acq-inv-");
        var inv = WriteWinInventory(invDir, 23000001u, 23000002u);
        await WithBinariesRoot(null, async () =>
        {
            var fake = new RecordingAcquirer { ExplicitFetchable = false };
            var args = new[] { "--all", "--platform", Win, "--probe", "--inventory", inv };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(65, code); // a NOT-fetchable manifest => non-zero
            Assert.Equal(0, fake.BulkDownloadCount);     // ...and STILL no bulk download
        });
    }
}
