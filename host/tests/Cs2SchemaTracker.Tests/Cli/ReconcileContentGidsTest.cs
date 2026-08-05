// `reconcile-content-gids` dev command tests.
//
// Covers: --check drift + cross-platform disagreement reporting (exit 1); --apply rewriting the
// stale 2347770 GID to the authoritative inventory value BYTE-FOR-BYTE preserving the rest of the
// record + the loud re-acquire warning (exit 0); and fail-loud (exit 65) when a store build
// is absent from the inventory.

using System.Globalization;
using System.Text;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class ReconcileContentGidsTest
{
    private const uint ContentDepot = 2347770;
    private const uint BinaryDepot = 2347771;

    private const ulong Auth111 = 999999999999999999UL;   // authoritative content GID for build 111
    private const ulong Auth222 = 888888888888888888UL;   // authoritative content GID for build 222

    private static readonly string InventoryJson =
        """
        {
          "app": { "app_id": 730 },
          "depots": [
            { "depot_id": 2347770, "role": "content", "history": [] },
            { "depot_id": 2347771, "role": "binary", "platforms": ["windows-x86_64", "linux-x86_64"], "history": [] }
          ],
          "builds": [
            { "build_id": 111, "content": "999999999999999999" },
            { "build_id": 222, "content": "888888888888888888" }
          ]
        }
        """;

    private static string NewScratch(out string inventoryPath)
    {
        var scratch = Path.Combine(Path.GetTempPath(), "reconcile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        inventoryPath = Path.Combine(scratch, "inventory.json");
        File.WriteAllText(inventoryPath, InventoryJson, new UTF8Encoding(false));
        return scratch;
    }

    private static void WriteRecord(string storeRoot, uint build, string platform, ulong contentGid, ulong binaryGid)
    {
        var tupleDir = Path.Combine(storeRoot, build.ToString(CultureInfo.InvariantCulture), platform);
        Directory.CreateDirectory(tupleDir);
        new ManifestRecord(730, build, new[]
        {
            new ManifestRecordDepot(BinaryDepot, binaryGid, "2026-06-10T00:00:00Z"),
            new ManifestRecordDepot(ContentDepot, contentGid, "2026-06-10T00:00:00Z"),
        }).WriteToTupleDir(tupleDir);
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Check_Reports_Drift_And_CrossPlatform_Disagreement_Exit1()
    {
        var scratch = NewScratch(out var inventoryPath);
        var storeRoot = Path.Combine(scratch, "store");
        try
        {
            // build 111: win=123 lin=456 -> both stale AND disagree with each other. auth=999...
            WriteRecord(storeRoot, 111, "windows-x86_64", contentGid: 123UL, binaryGid: 700UL);
            WriteRecord(storeRoot, 111, "linux-x86_64", contentGid: 456UL, binaryGid: 701UL);
            // build 222: win already authoritative -> ok.
            WriteRecord(storeRoot, 222, "windows-x86_64", contentGid: Auth222, binaryGid: 702UL);

            var sw = new StringWriter();
            int rc = ReconcileContentGidsCommand.Run(
                new[] { "--check", "--binaries-root", storeRoot, "--inventory", inventoryPath }, sw);

            Assert.Equal(1, rc);   // drift signal
            var log = sw.ToString();
            Assert.Contains("DRIFT build 111", log);
            Assert.Contains("DISAGREE build 111", log);
            Assert.DoesNotContain("build 222", log);   // 222 matches -> not reported
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void Apply_Rewrites_Stale_Gid_Preserving_Rest_ByteForByte()
    {
        var scratch = NewScratch(out var inventoryPath);
        var storeRoot = Path.Combine(scratch, "store");
        try
        {
            WriteRecord(storeRoot, 111, "windows-x86_64", contentGid: 123UL, binaryGid: 700UL);
            var recordPath = Path.Combine(storeRoot, "111", "windows-x86_64", ManifestRecord.FileName);
            var before = File.ReadAllText(recordPath, Encoding.UTF8);

            var sw = new StringWriter();
            int rc = ReconcileContentGidsCommand.Run(
                new[] { "--apply", "--binaries-root", storeRoot, "--inventory", inventoryPath }, sw);

            Assert.Equal(0, rc);   // applied cleanly, no errors

            var after = File.ReadAllText(recordPath, Encoding.UTF8);
            // Byte-for-byte preserved except the single content GID token.
            var expected = before.Replace("\"123\"", "\"" + Auth111 + "\"", StringComparison.Ordinal);
            Assert.Equal(expected, after);

            // Re-parse: the content depot now carries the authoritative GID; the binary depot is untouched.
            var reparsed = ManifestRecord.ReadFromFile(recordPath);
            Assert.Equal(Auth111, reparsed.Depots.First(d => d.DepotId == ContentDepot).ManifestId);
            Assert.Equal(700UL, reparsed.Depots.First(d => d.DepotId == BinaryDepot).ManifestId);

            // Loud re-acquire warning names the reconciled build + authoritative GID.
            var log = sw.ToString();
            Assert.Contains("WARNING", log);
            Assert.Contains("build 111", log);
            Assert.Contains(Auth111.ToString(CultureInfo.InvariantCulture), log);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void Apply_Is_Idempotent_Second_Run_Is_NoOp()
    {
        var scratch = NewScratch(out var inventoryPath);
        var storeRoot = Path.Combine(scratch, "store");
        try
        {
            WriteRecord(storeRoot, 111, "windows-x86_64", contentGid: 123UL, binaryGid: 700UL);
            var args = new[] { "--apply", "--binaries-root", storeRoot, "--inventory", inventoryPath };

            Assert.Equal(0, ReconcileContentGidsCommand.Run(args, new StringWriter()));
            var afterFirst = File.ReadAllBytes(Path.Combine(storeRoot, "111", "windows-x86_64", ManifestRecord.FileName));

            // Second run: already reconciled -> nothing to fix, byte-identical file.
            Assert.Equal(0, ReconcileContentGidsCommand.Run(args, new StringWriter()));
            var afterSecond = File.ReadAllBytes(Path.Combine(storeRoot, "111", "windows-x86_64", ManifestRecord.FileName));
            Assert.Equal(afterFirst, afterSecond);
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    [Fact]
    public void FailLoud_When_Store_Build_Absent_From_Inventory_Exit65()
    {
        var scratch = NewScratch(out var inventoryPath);
        var storeRoot = Path.Combine(scratch, "store");
        try
        {
            // build 333 is NOT in the inventory -> fail-loud.
            WriteRecord(storeRoot, 333, "windows-x86_64", contentGid: 123UL, binaryGid: 700UL);

            var sw = new StringWriter();
            int rc = ReconcileContentGidsCommand.Run(
                new[] { "--check", "--binaries-root", storeRoot, "--inventory", inventoryPath }, sw);

            Assert.Equal(65, rc);
            Assert.Contains("absent from the inventory", sw.ToString());
        }
        finally
        {
            TryDelete(scratch);
        }
    }
}
