// (redesign) — `acquire --from-provenance` + `--cache-only` / `--no-cache` tests.
//
// The CAS + `fetch-cached-binaries` command were removed. The surviving
// acquire surface is:
//   - `--from-provenance <p>`  : re-acquire the EXACT inputs a committed provenance.json pins
//                                (steam.depots[].manifest_id), then SHA-256-VERIFY every acquired
//                                file against inputs[].sha256. The ONLY hash-verifying acquire mode.
//   - `--cache-only`           : resolve ONLY from the local cache dir; never contact Steam.
//   - `--no-cache`             : bypass a populated cache; force a fresh Steam acquire.
//
// These exercise the CLI argument + verification path WITHOUT touching Steam by injecting a fake
// ISteamAcquirer through the internal AcquireCommand.RunAsync(args, factory) seam (the same seam
// AcquireCommandArgsTest uses). The fake's AcquireExplicitAsync WRITES configurable bytes for each
// pinned input into the out dir, so the real InputBinaryVerifier + ProvenanceReader + Sha256Hex
// chokepoint runs over real on-disk bytes (no mocks for binary I/O).
//
// Deterministic: throwaway absolute temp dirs, cleaned up in finally; no network.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public sealed class AcquireFromProvenanceTest
{
    private const string Platform = "windows-x86_64";
    private const string BuildId = "23669931";

    // A fake acquirer that, on AcquireExplicitAsync, WRITES a configurable set of (relative-path ->
    // bytes) files into the out dir — simulating what a real Steam re-acquire would leave on disk so
    // the genuine SHA-256 verification chokepoint runs over real bytes. Captures the spec it was
    // invoked with (for the manifest_id-passthrough assertion).
    private sealed class WritingFake : ISteamAcquirer
    {
        private readonly IReadOnlyDictionary<string, byte[]> _filesToWrite;

        public int ExplicitCount;
        public ManifestSpec? LastSpec;
        public string LastOutDir = "";

        public WritingFake(IReadOnlyDictionary<string, byte[]> filesToWrite)
            => _filesToWrite = filesToWrite;

        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec spec, string outDir, CancellationToken ct)
        {
            ExplicitCount++;
            LastSpec = spec;
            LastOutDir = outDir;
            Directory.CreateDirectory(outDir);
            foreach (var (rel, bytes) in _filesToWrite)
            {
                var dst = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, bytes);
            }
            return Task.FromResult(new AcquireResult(
                outDir, spec.BuildId,
                Array.Empty<AcquiredDepotInfo>(), Array.Empty<AcquiredFileInfo>(), 0));
        }

        // No other acquire path is exercised by --from-provenance.
        public Task<AcquireResult> AcquireAsync(uint a, IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new NotSupportedException("PICS path not used by --from-provenance");
        public Task<CurrentPicsResult> ProbeCurrentPicsAsync(uint a, IReadOnlyList<uint> d, CancellationToken c)
            => throw new NotSupportedException();
        public Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(ManifestSpec s, bool ch, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireContentPakAsync(
            uint a, uint cd, uint b, string o, bool m, ManifestSpec? es, bool dirOnly, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireBinariesOnlyAsync(
            uint a, IReadOnlyList<uint> d, uint b, string o, string platform, ManifestSpec? es, CancellationToken c)
            => throw new NotSupportedException();
        public Task<AcquireResult> AcquireToolsAsync(
            uint a, uint td, uint b, string o, ManifestSpec? es, CancellationToken c)
            => throw new NotSupportedException();
    }

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // Write a committed provenance.json that pins one Steam depot (so ReadSteamSpec yields a
    // ManifestSpec) and lists each (path, sha256, file_size) input row. canonical proto3 JSON keys
    // (camelCase) — the JsonParser ProvenanceReader uses ignores unknown fields and accepts these.
    private static string WriteProvenance(
        string dir, IReadOnlyList<(string Path, string Sha256, int Size)> inputs,
        uint appId = 730, string manifestId = "5146470907583764090", uint depotId = 2347771)
    {
        var inputsJson = string.Join(",", inputs.Select(i =>
            $$"""{ "path": "{{i.Path}}", "sha256": "{{i.Sha256}}", "fileSize": "{{i.Size}}", "mtimeUtc": "2026-06-10T12:00:00Z" }"""));
        var json = $$"""
        {
          "schemaVersion": "0.4.0",
          "buildId": "{{BuildId}}",
          "platform": "{{Platform}}",
          "steam": {
            "appId": {{appId}},
            "steamBuildId": "{{BuildId}}",
            "manifestCreatedUtc": "2026-06-10T12:00:00Z",
            "depots": [ { "depotId": {{depotId}}, "manifestId": "{{manifestId}}" } ]
          },
          "inputs": [ {{inputsJson}} ]
        }
        """;
        var path = Path.Combine(dir, "provenance.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "fromprov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    // ===================================================================================
    // A. acquire --from-provenance (the only hash-verifying mode)
    // ===================================================================================

    [Fact]
    public async Task A1_HappyPath_Provenance_Matches_Acquired_Bytes_Exits_0_Files_Written()
    {
        var dir = NewTempDir();
        try
        {
            var binA = Encoding.ASCII.GetBytes("the server binary bytes");
            var binB = Encoding.ASCII.GetBytes("the client binary bytes");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[]
            {
                ("bin/libserver.so", Sha256(binA), binA.Length),
                ("bin/client.dll", Sha256(binB), binB.Length),
            });

            var outDir = Path.Combine(dir, "out");
            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["bin/libserver.so"] = binA,
                ["bin/client.dll"] = binB,
            });

            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.Equal(1, fake.ExplicitCount);               // re-acquired (cache empty -> Steam)
            Assert.True(File.Exists(Path.Combine(outDir, "bin", "libserver.so")));
            Assert.True(File.Exists(Path.Combine(outDir, "bin", "client.dll")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task A2_Sha_Mismatch_Fails_Loud_Exit_65_Reports_MISMATCH()
    {
        var dir = NewTempDir();
        try
        {
            var expected = Encoding.ASCII.GetBytes("EXPECTED bytes the provenance pins");
            var actual = Encoding.ASCII.GetBytes("DIFFERENT bytes that hash differently");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            // provenance records the EXPECTED hash, but the fake writes the (differing) ACTUAL bytes.
            var prov = WriteProvenance(provDir, new[]
            {
                ("bin/libserver.so", Sha256(expected), expected.Length),
            });

            var outDir = Path.Combine(dir, "out");
            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["bin/libserver.so"] = actual,
            });

            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(65, code);

            // Re-run the SAME shared verification chokepoint the command used, over the bytes it left
            // on disk, into a LOCAL writer (no process-global Console redirect that would race other
            // tests): the per-file report names the failed input as MISMATCH with expected/actual.
            var report = FormatFailureReport(prov, outDir);
            Assert.Contains("MISMATCH", report);
            Assert.Contains("bin/libserver.so", report);
            Assert.Contains("expected=" + Sha256(expected), report);
            Assert.Contains("actual=" + Sha256(actual), report);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task A3_Missing_Provenance_Input_Fails_Loud_Exit_65_Reports_MISSING()
    {
        var dir = NewTempDir();
        try
        {
            var present = Encoding.ASCII.GetBytes("present binary bytes");
            var absent = Encoding.ASCII.GetBytes("a binary the acquire never produced");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[]
            {
                ("bin/libserver.so", Sha256(present), present.Length),
                ("bin/missing.dll", Sha256(absent), absent.Length),
            });

            var outDir = Path.Combine(dir, "out");
            // The fake writes ONLY libserver.so; bin/missing.dll is never produced.
            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["bin/libserver.so"] = present,
            });

            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(65, code);

            // The per-file report (via the same chokepoint, into a LOCAL writer) names the absent
            // input as MISSING with its expected hash.
            var report = FormatFailureReport(prov, outDir);
            Assert.Contains("MISSING", report);
            Assert.Contains("bin/missing.dll", report);
            Assert.Contains("expected=" + Sha256(absent), report);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // Render the InputBinaryVerifier per-file failure report for (provenance, binariesDir) into a
    // local string — exactly the format the command writes to stderr (MISMATCH/MISSING + expected/
    // actual), but without redirecting process-global Console (which would race parallel tests).
    private static string FormatFailureReport(string provenancePath, string binariesDir)
    {
        var result = Cs2SchemaTracker.Host.Cache.InputBinaryVerifier.Verify(provenancePath, binariesDir);
        var sw = new StringWriter();
        Cs2SchemaTracker.Host.Cache.InputBinaryVerifier.WriteFailureReport(sw, "from-provenance verify", result);
        return sw.ToString();
    }

    [Fact]
    public async Task A4_From_Provenance_With_From_Manifest_Is_Mutually_Exclusive_Exit_64()
    {
        var dir = NewTempDir();
        try
        {
            var bin = Encoding.ASCII.GetBytes("bytes");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[] { ("bin/x.dll", Sha256(bin), bin.Length) });
            // --from-manifest spec content is irrelevant: the mutual-exclusion check fires first.
            var specPath = Path.Combine(dir, "spec.json");
            File.WriteAllText(specPath,
                """{ "appId": 730, "buildId": 23669931, "depots": [ { "depotId": 2347771, "manifestId": "1" } ] }""");

            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal));
            var args = new[]
            {
                "--from-provenance", prov, "--from-manifest", specPath, "--platform", Platform,
            };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(64, code);
            Assert.Equal(0, fake.ExplicitCount);   // rejected before any acquire.
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task A5_ReAcquire_Uses_Provenance_Pinned_Manifest_Ids()
    {
        // The re-acquire must drive AcquireExplicitAsync with a ManifestSpec built from the
        // provenance's pinned steam.{app_id, build_id, depots[].manifest_id} — NOT PICS-current.
        var dir = NewTempDir();
        try
        {
            var bin = Encoding.ASCII.GetBytes("server bytes");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            const string PinnedManifest = "8287382081622299196";
            const uint PinnedDepot = 2347771;
            var prov = WriteProvenance(
                provDir,
                new[] { ("bin/libserver.so", Sha256(bin), bin.Length) },
                appId: 730, manifestId: PinnedManifest, depotId: PinnedDepot);

            var outDir = Path.Combine(dir, "out");
            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["bin/libserver.so"] = bin,
            });

            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.NotNull(fake.LastSpec);
            Assert.Equal(730u, fake.LastSpec!.AppId);
            Assert.Equal(uint.Parse(BuildId, CultureInfo.InvariantCulture), fake.LastSpec.BuildId);
            var depot = Assert.Single(fake.LastSpec.OrderedDepots);
            Assert.Equal(PinnedDepot, depot.DepotId);
            Assert.Equal(ulong.Parse(PinnedManifest, CultureInfo.InvariantCulture), depot.ManifestId);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    // ===================================================================================
    // B. --from-provenance --cache-only / --no-cache
    //
    // The from-provenance path resolves the cache by PRESENCE of every pinned input under the out
    // dir, so it does not need a Steam probe for the build_id (the provenance carries it). This is
    // the cleanest seam to exercise the cache-resolution flags without standing up the PICS path.
    // ===================================================================================

    [Fact]
    public async Task B1_CacheOnly_With_Inputs_Present_Exits_0_Without_Steam()
    {
        var dir = NewTempDir();
        try
        {
            var bin = Encoding.ASCII.GetBytes("already-cached binary bytes");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[] { ("bin/libserver.so", Sha256(bin), bin.Length) });

            // Pre-populate the cache (out dir) so --cache-only resolves in place.
            var outDir = Path.Combine(dir, "out");
            var cached = Path.Combine(outDir, "bin", "libserver.so");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllBytes(cached, bin);

            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal));
            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir, "--cache-only" };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.Equal(0, fake.ExplicitCount);   // NO Steam call — resolved from the cache.
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task B2_CacheOnly_With_Input_Absent_Exits_65_No_Steam_Fallback()
    {
        var dir = NewTempDir();
        try
        {
            var bin = Encoding.ASCII.GetBytes("a binary that is NOT in the cache");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[] { ("bin/libserver.so", Sha256(bin), bin.Length) });

            // Out dir exists but is EMPTY — the pinned input is absent.
            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);

            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["bin/libserver.so"] = bin,
            });
            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir, "--cache-only" };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(65, code);
            Assert.Equal(0, fake.ExplicitCount);   // --cache-only forbids the Steam fallback.
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task B3_CacheOnly_Build_Latest_Is_Rejected_Cache_Free()
    {
        // PICS-current path: --cache-only --build latest cannot name the cache dir without a Steam
        // probe (the build_id is unknowable cache-free) -> rejected with the documented usage exit.
        var dir = NewTempDir();
        try
        {
            // A plain ISteamAcquirer fake: --cache-only latest is rejected BEFORE any acquire/probe.
            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal));
            var args = new[] { "--build", "latest", "--platform", Platform, "--cache-only" };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(64, code);
            Assert.Equal(0, fake.ExplicitCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task B4_NoCache_Bypasses_Populated_Cache_And_Invokes_Steam()
    {
        var dir = NewTempDir();
        try
        {
            var bin = Encoding.ASCII.GetBytes("cached AND re-acquired binary bytes");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[] { ("bin/libserver.so", Sha256(bin), bin.Length) });

            // Cache is already populated — default cache-first would skip Steam, but --no-cache forces it.
            var outDir = Path.Combine(dir, "out");
            var cached = Path.Combine(outDir, "bin", "libserver.so");
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            File.WriteAllBytes(cached, bin);

            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["bin/libserver.so"] = bin,   // the fresh acquire re-writes the same (matching) bytes.
            });
            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir, "--no-cache" };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(0, code);
            Assert.Equal(1, fake.ExplicitCount);   // --no-cache forced a fresh Steam acquire.
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task B5_CacheOnly_And_NoCache_Are_Mutually_Exclusive_Exit_64()
    {
        var dir = NewTempDir();
        try
        {
            var bin = Encoding.ASCII.GetBytes("bytes");
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteProvenance(provDir, new[] { ("bin/x.dll", Sha256(bin), bin.Length) });

            var fake = new WritingFake(new Dictionary<string, byte[]>(StringComparer.Ordinal));
            var args = new[]
            {
                "--from-provenance", prov, "--platform", Platform, "--cache-only", "--no-cache",
            };
            var code = await AcquireCommand.RunAsync(args, () => fake);

            Assert.Equal(64, code);
            Assert.Equal(0, fake.ExplicitCount);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
