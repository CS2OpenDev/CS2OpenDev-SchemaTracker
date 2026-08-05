// End-to-end Steam acquisition integration test.
//
// SKIPPED BY DEFAULT. Run with:
//   dotnet test --filter Category=Integration
//
// Even when Category=Integration is matched, the test only runs when the
// CS2_ACQUIRE_INTEGRATION environment variable is set to "1". This keeps the
// test inert on developer machines that filter purely by the trait and on
// CI runs that don't allocate enough disk/bandwidth for a multi-GB download.
//
// What this test exercises:
//   - real anonymous Steam logon
//   - PICS lookup for app 730 (open to anonymous)
//   - depot-key fetch
//   - manifest download from the CDN
//   - chunk download + SHA-1 verification for every file in the manifest
// - whole-file SHA-256 computation for provenance
//   - final-rename of the .partial directory to the output directory
//
// Platform selection:
//   We use `linux-x86_64` (CS2 app 730). CS2 is ONE app — there is no separate
//   dedicated-server app (the previously-assumed 2347780 was fabricated). The
//   Linux binary depot (2347773) ships BOTH client.so AND server.so, so a single
//   platform download covers everything the walker needs; there is no separate
//   "server tuple" acquisition to exercise.

using Cs2SchemaTracker.Host.Steam;

using Xunit;
using Xunit.Abstractions;

namespace Cs2SchemaTracker.Tests.Steam;

[Trait("Category", "Integration")]
public class SteamAcquireIntegrationTest
{
    // The linux-x86_64 binary depot ships BOTH client and server modules — we
    // look for any well-known CS2 native binary (client.so OR server.so etc.).
    private static readonly string[] BinaryHints =
    {
        "client.so",
        "server.so",
        "engine2.so",
        "tier0.so",
        "schemasystem.so",
        "libclient.so",
        "bin/",
        "game/csgo/",
    };

    private readonly ITestOutputHelper output;

    public SteamAcquireIntegrationTest(ITestOutputHelper output)
    {
        this.output = output;
    }

    [Fact]
    public async Task Acquire_latest_linux_platform_downloads_expected_binaries()
    {
        if (Environment.GetEnvironmentVariable("CS2_ACQUIRE_INTEGRATION") != "1")
        {
            output.WriteLine(
                "Skipping integration test (set CS2_ACQUIRE_INTEGRATION=1 to enable). " +
                "Note: this performs a real anonymous Steam connection and downloads ~hundreds of MB.");
            return;
        }

        var outDir = Path.Combine(Path.GetTempPath(),
            "cs2-acquire-itest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var acquirer = new SteamAnonymousAcquirer(new TestOutputWriter(output));
            var plan = PlatformToDepots.Resolve("linux-x86_64");

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var result = await acquirer.AcquireAsync(
                plan.AppId, plan.DepotIds, buildId: 0, outDir, cts.Token);

            output.WriteLine(
                $"acquire result: build={result.ResolvedBuildId} files={result.Files.Count} bytes={result.TotalBytes:N0}");
            foreach (var depot in result.Depots)
            {
                output.WriteLine($"  depot {depot.DepotId} manifest {depot.ManifestId} created {depot.ManifestCreatedUtc}");
            }

            Assert.True(Directory.Exists(outDir), $"output dir should exist: {outDir}");
            Assert.NotEmpty(result.Files);
            Assert.True(result.TotalBytes > 0, "expected non-zero total bytes acquired");

            // Every file's SHA-256 should be a 64-char hex string.
            foreach (var f in result.Files)
            {
                Assert.Equal(64, f.Sha256Hex.Length);
                Assert.True(f.Sha256Hex.All(c => "0123456789abcdef".Contains(c)),
                    $"Sha256 not lowercase hex: {f.Sha256Hex} for {f.RelativePath}");
            }

            // Look for at least one well-known CS2 native binary path in the result.
            Assert.Contains(result.Files,
                f => BinaryHints.Any(h => f.RelativePath.Contains(h, StringComparison.Ordinal)));
        }
        finally
        {
            try
            { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); }
            catch { }
            try
            {
                var partial = outDir + ".partial";
                if (Directory.Exists(partial))
                    Directory.Delete(partial, recursive: true);
            }
            catch { }
        }
    }

    private sealed class TestOutputWriter : TextWriter
    {
        private readonly ITestOutputHelper inner;
        private readonly System.Text.StringBuilder lineBuf = new();
        public TestOutputWriter(ITestOutputHelper inner) { this.inner = inner; }
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value)
        {
            if (value == '\n')
            {
                inner.WriteLine(lineBuf.ToString().TrimEnd('\r'));
                lineBuf.Clear();
            }
            else
            {
                lineBuf.Append(value);
            }
        }
        public override void WriteLine(string? value)
        {
            inner.WriteLine(value ?? string.Empty);
        }
    }
}
