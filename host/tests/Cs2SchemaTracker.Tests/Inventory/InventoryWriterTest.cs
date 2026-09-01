// InventoryWriter — lossless read-modify-write append tests.
//
// Locks the forward-capture writer contract: appending a new builds[] row preserves _meta / app /
// eras / depots and every existing build verbatim; emits canonical Python-ingest form (2-space
// indent, LF, UTF-8 no BOM, trailing newline, no HTML escaping); writes the build-key order
// build_id,date_utc,change_number,title,era,content,binaries; inserts newest-build_id-first; and is
// new-only (a duplicate build_id fails loud).
//
// Deterministic: throwaway temp files, no wall-clock, no Steam.

using System.Text.Json.Nodes;

using Cs2SchemaTracker.Host.Inventory;

using Xunit;

namespace Cs2SchemaTracker.Tests.Inventory;

public sealed class InventoryWriterTest
{
    private const string Seed = """
        {
          "_meta": {
            "note": "keep me"
          },
          "app": {
            "app_id": 730
          },
          "eras": [
            {
              "era": "cs2-2026-04-21"
            }
          ],
          "depots": [
            {
              "depot_id": 2347771
            }
          ],
          "builds": [
            {
              "build_id": 20000000,
              "date_utc": "2026-01-01T00:00:00Z",
              "era": "cs2-2026-04-21"
            }
          ]
        }

        """;

    private static string NewInventory(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "inv-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cs2-assets-inventory.json");
        File.WriteAllText(path, content.Replace("\r\n", "\n"));
        return path;
    }

    [Fact]
    public void AppendBuild_Full_Record_Preserves_Everything_And_Writes_Canonical()
    {
        var path = NewInventory(Seed);

        InventoryWriter.AppendBuild(path, new InventoryBuildRecord(
            BuildId: 24000000,
            DateUtc: "2026-07-01T00:00:00Z",
            Era: "cs2-2026-04-21",
            ChangeNumber: 37000000,
            Title: "Counter-Strike 2 Update",
            Content: "1111111111111111111",
            Binaries: new Dictionary<string, string>
            {
                ["windows-x86_64"] = "2222222222222222222",
                ["linux-x86_64"] = "3333333333333333333",
            }));

        var after = File.ReadAllText(path);

        // Everything preserved.
        Assert.Contains("\"note\": \"keep me\"", after);
        Assert.Contains("\"build_id\": 20000000", after);   // the pre-existing build survives.
        Assert.Contains("\"depot_id\": 2347771", after);

        // The new row landed with the exact fields.
        Assert.Contains("\"build_id\": 24000000", after);
        Assert.Contains("\"title\": \"Counter-Strike 2 Update\"", after);
        Assert.Contains("\"content\": \"1111111111111111111\"", after);

        // Newest-build_id-first: the appended (higher) build precedes the pre-existing one.
        Assert.True(after.IndexOf("24000000", StringComparison.Ordinal)
                    < after.IndexOf("20000000", StringComparison.Ordinal),
            "builds[] must stay newest-build_id-first");

        // Canonical key order in the new row: build_id < date_utc < change_number < title < era < content < binaries.
        int i0 = after.IndexOf("\"build_id\": 24000000", StringComparison.Ordinal);
        int iDate = after.IndexOf("\"date_utc\"", i0, StringComparison.Ordinal);
        int iChange = after.IndexOf("\"change_number\"", i0, StringComparison.Ordinal);
        int iTitle = after.IndexOf("\"title\"", i0, StringComparison.Ordinal);
        int iEra = after.IndexOf("\"era\"", i0, StringComparison.Ordinal);
        int iContent = after.IndexOf("\"content\"", i0, StringComparison.Ordinal);
        int iBin = after.IndexOf("\"binaries\"", i0, StringComparison.Ordinal);
        Assert.True(i0 < iDate && iDate < iChange && iChange < iTitle && iTitle < iEra
                    && iEra < iContent && iContent < iBin, "new-row key order is wrong");

        // binaries sub-keys are Ordinal-sorted (linux-x86_64 before windows-x86_64).
        Assert.True(after.IndexOf("linux-x86_64", iBin, StringComparison.Ordinal)
                    < after.IndexOf("windows-x86_64", iBin, StringComparison.Ordinal));

        // Canonical form: 2-space indent, LF, single trailing newline.
        Assert.EndsWith("}\n", after, StringComparison.Ordinal);
        Assert.DoesNotContain("\r\n", after);
    }

    [Fact]
    public void AppendBuild_Omits_Absent_Optional_Fields()
    {
        var path = NewInventory(Seed);

        // A best-effort forward-capture row: no change_number, no title.
        InventoryWriter.AppendBuild(path, new InventoryBuildRecord(
            BuildId: 24000001,
            DateUtc: "2026-07-02T00:00:00Z",
            Era: "cs2-2026-04-21",
            Content: "9999999999999999999"));

        var after = File.ReadAllText(path);
        int i0 = after.IndexOf("\"build_id\": 24000001", StringComparison.Ordinal);
        int next = after.IndexOf("\"build_id\": 20000000", StringComparison.Ordinal);
        var row = after[i0..next];
        Assert.DoesNotContain("change_number", row);
        Assert.DoesNotContain("title", row);
        Assert.Contains("\"era\": \"cs2-2026-04-21\"", row);
        Assert.Contains("\"content\": \"9999999999999999999\"", row);
        Assert.DoesNotContain("binaries", row);   // no binaries supplied -> omitted.
    }

    [Fact]
    public void AppendBuild_Tools_Gid_Is_Emitted_Last_And_Omitted_When_Absent()
    {
        var path = NewInventory(Seed);

        // A forward-capture row with a Workshop Tools (2347779) GID: emitted as a single string
        // (like content), LAST in the row's key order (after binaries).
        InventoryWriter.AppendBuild(path, new InventoryBuildRecord(
            BuildId: 24000002,
            DateUtc: "2026-07-03T00:00:00Z",
            Era: "cs2-2026-04-21",
            Content: "1111111111111111111",
            Binaries: new Dictionary<string, string> { ["windows-x86_64"] = "2222222222222222222" },
            Tools: "7895084913465193678"));

        var after = File.ReadAllText(path);
        int i0 = after.IndexOf("\"build_id\": 24000002", StringComparison.Ordinal);
        int next = after.IndexOf("\"build_id\": 20000000", StringComparison.Ordinal);
        var row = after[i0..next];
        Assert.Contains("\"tools\": \"7895084913465193678\"", row);
        Assert.True(row.IndexOf("\"binaries\"", StringComparison.Ordinal)
                    < row.IndexOf("\"tools\"", StringComparison.Ordinal),
            "tools must follow binaries in the row's key order");

        // A row WITHOUT a tools GID omits the key (honest best-effort capture, no placeholder).
        InventoryWriter.AppendBuild(path, new InventoryBuildRecord(
            BuildId: 24000003, DateUtc: "2026-07-04T00:00:00Z", Era: "cs2-2026-04-21"));
        var after2 = File.ReadAllText(path);
        int j0 = after2.IndexOf("\"build_id\": 24000003", StringComparison.Ordinal);
        int j1 = after2.IndexOf("\"build_id\": 24000002", StringComparison.Ordinal);
        Assert.DoesNotContain("tools", after2[j0..j1]);
    }

    [Fact]
    public void AppendBuild_Duplicate_BuildId_Fails_Loud()
    {
        var path = NewInventory(Seed);
        var ex = Assert.Throws<InvalidDataException>(() => InventoryWriter.AppendBuild(path,
            new InventoryBuildRecord(BuildId: 20000000, DateUtc: "x", Era: "cs2-2026-04-21")));
        Assert.Contains("already carries build_id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBuild_Missing_File_Fails_Loud()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N"), "inv.json");
        Assert.Throws<InvalidDataException>(() => InventoryWriter.AppendBuild(missing,
            new InventoryBuildRecord(BuildId: 1, DateUtc: "x", Era: "e")));
    }

    /// <summary>
    /// A seed whose _meta carries a STALE derived counts block plus a key the writer does not
    /// derive — the shape data/cs2-assets-inventory.json actually has.
    /// </summary>
    private const string SeedWithCounts = """
        {
          "_meta": {
            "note": "keep me",
            "counts": {
              "builds": 99,
              "builds_with_content": 99,
              "builds_with_change_number": 99,
              "depots": 42,
              "hand_maintained_key": 7
            }
          },
          "app": {
            "app_id": 730
          },
          "eras": [
            {
              "era": "cs2-2026-04-21"
            }
          ],
          "depots": [
            {
              "depot_id": 2347771
            }
          ],
          "builds": [
            {
              "build_id": 20000000,
              "date_utc": "2026-01-01T00:00:00Z",
              "era": "cs2-2026-04-21",
              "content": "111"
            }
          ]
        }

        """;

    [Fact]
    public void AppendBuild_Refreshes_Stale_Meta_Counts()
    {
        var path = NewInventory(SeedWithCounts);
        InventoryWriter.AppendBuild(path, new InventoryBuildRecord(
            BuildId: 20000001,
            DateUtc: "2026-01-02T00:00:00Z",
            Era: "cs2-2026-04-21",
            ChangeNumber: 12345,
            Content: "222"));

        var counts = JsonNode.Parse(File.ReadAllText(path))!["_meta"]!["counts"]!;
        Assert.Equal(2, (int)counts["builds"]!);              // was 99
        Assert.Equal(2, (int)counts["builds_with_content"]!); // was 99
        Assert.Equal(1, (int)counts["builds_with_change_number"]!);
        Assert.Equal(1, (int)counts["depots"]!);              // was 42
        // A key the writer has no derivation for is left exactly as found.
        Assert.Equal(7, (int)counts["hand_maintained_key"]!);
        // Non-counts _meta content is still preserved verbatim.
        Assert.Contains("\"note\": \"keep me\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void AppendBuild_Never_Invents_A_Counts_Block()
    {
        // The base Seed's _meta has no counts; the writer must not add one.
        var path = NewInventory(Seed);
        InventoryWriter.AppendBuild(path, new InventoryBuildRecord(
            BuildId: 20000001, DateUtc: "2026-01-02T00:00:00Z", Era: "cs2-2026-04-21"));

        var meta = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!["_meta"]!;
        Assert.False(meta.ContainsKey("counts"));
        Assert.Equal("keep me", (string)meta["note"]!);
    }

    [Fact]
    public void MergeBuildFacts_Adds_Absent_ChangeNumber_And_Title_Only()
    {
        var path = NewInventory(SeedWithCounts);

        Assert.True(InventoryWriter.MergeBuildFacts(
            path, 20000000, content: null, binaries: null, tools: null,
            changeNumber: 555, title: "Build 20000000 on 1 January 2026"));

        var row = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!["builds"]![0]!;
        Assert.Equal(555, (int)row["change_number"]!);
        Assert.Equal("Build 20000000 on 1 January 2026", (string)row["title"]!);
        Assert.Equal("111", (string)row["content"]!);   // present value untouched

        // Second merge with DIFFERENT values changes nothing — never rewrite what is there.
        Assert.False(InventoryWriter.MergeBuildFacts(
            path, 20000000, content: null, binaries: null, tools: null,
            changeNumber: 999, title: "Counter-Strike 2 Update"));
        var again = (JsonObject)JsonNode.Parse(File.ReadAllText(path))!["builds"]![0]!;
        Assert.Equal(555, (int)again["change_number"]!);
        Assert.Equal("Build 20000000 on 1 January 2026", (string)again["title"]!);
    }
}
