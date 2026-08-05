// Reproducibility smoke (redesign re-expression).
//
// HISTORY: originally fetched input binaries from a content-addressed (CAS) binary cache by
// SHA-256, re-ran the tool, and asserted byte-identical output. The CAS + `fetch-cached-binaries`
// command were REMOVED: Steam is now the durable store, a committed
// provenance.json pins everything to re-acquire (steam.depots[].manifest_id) AND to verify
// (inputs[].{path,sha256}). This suite re-expresses the intent on the surviving surface:
//
//   - A fixture provenance.json + matching input bytes round-trips through `acquire
//     --from-provenance` (a fake acquirer returns the fixture bytes) and VERIFIES byte-identical
//     (exit 0) — the reproducibility "re-run reproduces the pinned set" guarantee.
// - A ONE-BYTE mutation of any acquired input -> SHA-256 verification FAILURE (exit 65) —
// "missing/corrupt input is a violation, not a silent pass."
//
// Built on the real ProvenanceReader + Sha256Hex + InputBinaryVerifier chokepoint over real
// on-disk bytes (no mocks for binary I/O). Deterministic: absolute temp dirs, cleaned up
// in finally; no real Steam.

using System.Security.Cryptography;
using System.Text;

using Cs2SchemaTracker.Host.Cache;
using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Invariants;

public sealed class ReproducibilityTest
{
    private const string Platform = "windows-x86_64";
    private const string BuildId = "23669931";

    private static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // A fake acquirer whose AcquireExplicitAsync re-materializes a fixed (relativePath -> bytes) set
    // into the out dir — the deterministic stand-in for "re-run the tool at the recorded commit and
    // re-fetch its pinned inputs". No real Steam.
    private sealed class FixtureBytesAcquirer : ISteamAcquirer
    {
        private readonly IReadOnlyDictionary<string, byte[]> _bytes;
        public int ExplicitCount;

        public FixtureBytesAcquirer(IReadOnlyDictionary<string, byte[]> bytes) => _bytes = bytes;

        public Task<AcquireResult> AcquireExplicitAsync(ManifestSpec spec, string outDir, CancellationToken ct)
        {
            ExplicitCount++;
            Directory.CreateDirectory(outDir);
            foreach (var (rel, b) in _bytes)
            {
                var dst = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, b);
            }
            return Task.FromResult(new AcquireResult(
                outDir, spec.BuildId,
                Array.Empty<AcquiredDepotInfo>(), Array.Empty<AcquiredFileInfo>(), 0));
        }

        public Task<AcquireResult> AcquireAsync(uint a, IReadOnlyList<uint> d, uint b, string o, CancellationToken c)
            => throw new NotSupportedException();
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

    // A committed fixture provenance.json pinning one depot (so ReadSteamSpec resolves) + the input
    // rows (path, sha256, size). canonical proto3 camelCase JSON.
    private static string WriteFixtureProvenance(
        string dir, IReadOnlyList<(string Path, byte[] Bytes)> inputs)
    {
        var rows = string.Join(",", inputs.Select(i =>
            $$"""{ "path": "{{i.Path}}", "sha256": "{{Sha256(i.Bytes)}}", "fileSize": "{{i.Bytes.Length}}", "mtimeUtc": "2026-06-10T12:00:00Z" }"""));
        var json = $$"""
        {
          "schemaVersion": "0.4.0",
          "buildId": "{{BuildId}}",
          "platform": "{{Platform}}",
          "steam": {
            "appId": 730, "steamBuildId": "{{BuildId}}", "manifestCreatedUtc": "2026-06-10T12:00:00Z",
            "depots": [ { "depotId": 2347771, "manifestId": "5146470907583764090" } ]
          },
          "inputs": [ {{rows}} ]
        }
        """;
        var path = Path.Combine(dir, "provenance.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static readonly (string Path, byte[] Bytes)[] FixtureInputs =
    {
        ("bin/win64/server.dll", Encoding.ASCII.GetBytes("deterministic server payload v1")),
        ("bin/win64/client.dll", Encoding.ASCII.GetBytes("deterministic client payload v1")),
    };

    [Fact]
    public async Task FixtureProvenance_RoundTrips_Through_FromProvenance_ByteIdentical_Exit0()
    {
        var dir = NewTempDir();
        try
        {
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteFixtureProvenance(provDir, FixtureInputs);

            var outDir = Path.Combine(dir, "out");
            var acquirer = new FixtureBytesAcquirer(
                FixtureInputs.ToDictionary(i => i.Path, i => i.Bytes, StringComparer.Ordinal));

            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir };
            var code = await AcquireCommand.RunAsync(args, () => acquirer);

            Assert.Equal(0, code);
            Assert.Equal(1, acquirer.ExplicitCount);

            // The re-acquired bytes are byte-identical to the fixture the provenance pins.
            foreach (var (rel, bytes) in FixtureInputs)
            {
                var landed = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(landed));
                Assert.Equal(bytes, File.ReadAllBytes(landed));
            }
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task OneByte_Mutation_Of_Acquired_Input_Is_Verification_Failure_Exit65()
    {
        var dir = NewTempDir();
        try
        {
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteFixtureProvenance(provDir, FixtureInputs);

            // Re-acquire returns a ONE-BYTE-mutated copy of the first input (last byte flipped).
            var mutated = FixtureInputs.ToDictionary(i => i.Path, i => (byte[])i.Bytes.Clone(), StringComparer.Ordinal);
            var first = FixtureInputs[0].Path;
            mutated[first][^1] ^= 0x01;

            var outDir = Path.Combine(dir, "out");
            var acquirer = new FixtureBytesAcquirer(mutated);

            var args = new[] { "--from-provenance", prov, "--platform", Platform, "--out", outDir };
            var code = await AcquireCommand.RunAsync(args, () => acquirer);

            // the mutated byte hashes differently from the pinned sha256 -> fail-loud.
            Assert.Equal(65, code);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void InputBinaryVerifier_Chokepoint_OneByte_Mutation_Reports_Mismatch()
    {
        // The lower-layer assertion the round-trip relies on: the shared verification chokepoint
        // (ProvenanceReader + Sha256Hex + InputBinaryVerifier) catches a one-byte mutation of an
        // on-disk input against its committed provenance hash, in deterministic path-ordinal order.
        var dir = NewTempDir();
        try
        {
            var provDir = Path.Combine(dir, "prov");
            Directory.CreateDirectory(provDir);
            var prov = WriteFixtureProvenance(provDir, FixtureInputs);

            var binDir = Path.Combine(dir, "bin");
            foreach (var (rel, bytes) in FixtureInputs)
            {
                var dst = Path.Combine(binDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.WriteAllBytes(dst, bytes);
            }

            // Clean state: every input verifies.
            var ok = InputBinaryVerifier.Verify(prov, binDir);
            Assert.True(ok.Ok);
            Assert.Equal(FixtureInputs.Length, ok.Verified);

            // Mutate one byte of one input on disk.
            var target = Path.Combine(binDir, FixtureInputs[1].Path.Replace('/', Path.DirectorySeparatorChar));
            var b = File.ReadAllBytes(target);
            b[0] ^= 0xFF;
            File.WriteAllBytes(target, b);

            var bad = InputBinaryVerifier.Verify(prov, binDir);
            Assert.False(bad.Ok);
            var failure = Assert.Single(bad.Failures);
            Assert.Equal(FixtureInputs[1].Path, failure.RelativePath);
            Assert.Equal(Sha256(FixtureInputs[1].Bytes), failure.Expected);
            Assert.NotEqual(failure.Expected, failure.Actual);
            Assert.False(failure.IsMissing);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
