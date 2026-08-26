// InventoryWriter.MergeBuildFacts: the one sanctioned edit of an existing builds[] row.
//
// Locks the merge contract: only ABSENT keys are added (content / tools / binaries[<platform>]),
// a present value is never rewritten, the row keeps the canonical key order, an untouched file is
// not rewritten, and a missing build_id fails loud (merge never appends).

using Cs2SchemaTracker.Host.Inventory;

using Xunit;

namespace Cs2SchemaTracker.Tests.Inventory;

public sealed class InventoryWriterMergeTest
{
    private const string Seed = """
        {
          "_meta": {
            "note": "keep me"
          },
          "eras": [
            {
              "era": "cs2-2026-04-21"
            }
          ],
          "builds": [
            {
              "build_id": 24000000,
              "predecessor": 20000000,
              "date_utc": "2026-07-01T00:00:00Z",
              "era": "cs2-2026-04-21",
              "binaries": {
                "windows-x86_64": "222"
              },
              "tools": "777"
            },
            {
              "build_id": 20000000,
              "predecessor": null,
              "date_utc": "2026-01-01T00:00:00Z",
              "era": "cs2-2026-04-21"
            }
          ]
        }

        """;

    private static string NewInventory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "inv-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cs2-assets-inventory.json");
        File.WriteAllText(path, Seed.Replace("\r\n", "\n"));
        return path;
    }

    [Fact]
    public void Adds_Missing_Binaries_Platform_And_Content_Preserving_Present_Values()
    {
        var path = NewInventory();

        var changed = InventoryWriter.MergeBuildFacts(
            path, 24000000,
            content: "999",
            binaries: new Dictionary<string, string>
            {
                ["linux-x86_64"] = "444",
                ["windows-x86_64"] = "SHOULD-NOT-WIN",
            },
            tools: "SHOULD-NOT-WIN");

        Assert.True(changed);
        var after = File.ReadAllText(path);

        // Present values survive; only the absent keys were added.
        Assert.Contains("\"windows-x86_64\": \"222\"", after);
        Assert.Contains("\"linux-x86_64\": \"444\"", after);
        Assert.Contains("\"tools\": \"777\"", after);
        Assert.Contains("\"content\": \"999\"", after);
        Assert.DoesNotContain("SHOULD-NOT-WIN", after);
        Assert.Contains("\"note\": \"keep me\"", after);

        // Canonical row key order: era < content < binaries < tools.
        int i0 = after.IndexOf("\"build_id\": 24000000", StringComparison.Ordinal);
        int iEra = after.IndexOf("\"era\"", i0, StringComparison.Ordinal);
        int iContent = after.IndexOf("\"content\"", i0, StringComparison.Ordinal);
        int iBin = after.IndexOf("\"binaries\"", i0, StringComparison.Ordinal);
        int iTools = after.IndexOf("\"tools\"", i0, StringComparison.Ordinal);
        Assert.True(iEra < iContent && iContent < iBin && iBin < iTools,
            "merged row must keep the canonical key order");
    }

    [Fact]
    public void NoOp_When_Every_Fact_Is_Already_Present()
    {
        var path = NewInventory();
        var before = File.ReadAllText(path);

        var changed = InventoryWriter.MergeBuildFacts(
            path, 24000000,
            content: null,
            binaries: new Dictionary<string, string> { ["windows-x86_64"] = "SHOULD-NOT-WIN" },
            tools: "SHOULD-NOT-WIN");

        Assert.False(changed);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void Missing_Build_Fails_Loud()
    {
        var path = NewInventory();
        Assert.Throws<InvalidDataException>(() =>
            InventoryWriter.MergeBuildFacts(path, 999, "x", null, null));
    }

    [Fact]
    public void NonString_Binaries_Entry_Survives_The_Merge_Verbatim()
    {
        // A hand-curated row may carry a non-string node; the merge must never drop or rewrite
        // a present value, whatever its shape.
        var path = NewInventory();
        var seeded = File.ReadAllText(path).Replace(
            "\"windows-x86_64\": \"222\"", "\"windows-x86_64\": 222");
        File.WriteAllText(path, seeded);

        var changed = InventoryWriter.MergeBuildFacts(
            path, 24000000,
            content: null,
            binaries: new Dictionary<string, string> { ["linux-x86_64"] = "444" },
            tools: null);

        Assert.True(changed);
        var after = File.ReadAllText(path);
        Assert.Contains("\"windows-x86_64\": 222", after);
        Assert.Contains("\"linux-x86_64\": \"444\"", after);
    }
}
