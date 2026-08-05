// Coverage for the derived builds[].predecessor field (InventoryWriter).
//
// predecessor is a self-healing derived field: for the newest-first builds[] array, a row's
// predecessor is the NEXT (lower) build_id, and the floor row gets null. BackfillPredecessors seeds
// it idempotently; AppendBuild keeps it current on every forward-capture append.

using System.Text.Json.Nodes;

using Cs2SchemaTracker.Host.Inventory;

using Xunit;

namespace Cs2SchemaTracker.Tests.Inventory;

public sealed class InventoryPredecessorTest
{
    // A minimal inventory (newest-first builds[], no predecessor yet), in the canonical on-disk form.
    private const string BareInventory = """
        {
          "_meta": {
            "purpose": "test"
          },
          "builds": [
            {
              "build_id": 3000,
              "date_utc": "2026-03-01T00:00:00Z",
              "era": "e3"
            },
            {
              "build_id": 2000,
              "date_utc": "2026-02-01T00:00:00Z",
              "era": "e2"
            },
            {
              "build_id": 1000,
              "date_utc": "2026-01-01T00:00:00Z",
              "era": "e1"
            }
          ]
        }
        """;

    private static string InTempInventory(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "inv-pred-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    private static JsonArray Builds(string path)
        => (JsonNode.Parse(File.ReadAllText(path))!["builds"] as JsonArray)!;

    private static long? PredecessorOf(JsonObject build)
        => build["predecessor"] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    [Fact]
    public void Backfill_derives_predecessor_from_neighbours_floor_null()
    {
        var path = InTempInventory(BareInventory);
        try
        {
            InventoryWriter.BackfillPredecessors(path);
            var builds = Builds(path);

            Assert.Equal(2000, PredecessorOf((JsonObject)builds[0]!));   // 3000 -> 2000
            Assert.Equal(1000, PredecessorOf((JsonObject)builds[1]!));   // 2000 -> 1000
            Assert.Null(PredecessorOf((JsonObject)builds[2]!));          // 1000 (floor) -> null

            // predecessor sits right after build_id.
            var keys = ((JsonObject)builds[0]!).Select(kv => kv.Key).ToList();
            Assert.Equal("build_id", keys[0]);
            Assert.Equal("predecessor", keys[1]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Backfill_is_idempotent()
    {
        var path = InTempInventory(BareInventory);
        try
        {
            InventoryWriter.BackfillPredecessors(path);
            var once = File.ReadAllText(path);
            InventoryWriter.BackfillPredecessors(path);
            Assert.Equal(once, File.ReadAllText(path));   // byte-identical re-run
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Append_newest_sets_its_predecessor_to_the_prior_newest()
    {
        var path = InTempInventory(BareInventory);
        try
        {
            InventoryWriter.BackfillPredecessors(path);
            InventoryWriter.AppendBuild(path, new InventoryBuildRecord(BuildId: 4000, DateUtc: "2026-04-01T00:00:00Z", Era: "e4"));

            var builds = Builds(path);
            Assert.Equal(4000, ((JsonObject)builds[0]!)["build_id"]!.GetValue<long>());
            Assert.Equal(3000, PredecessorOf((JsonObject)builds[0]!));   // new newest -> prior newest
            Assert.Equal(2000, PredecessorOf((JsonObject)builds[1]!));   // unchanged
            Assert.Null(PredecessorOf((JsonObject)builds[3]!));          // floor still null
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Backfill_of_a_midchain_build_relinks_both_neighbours()
    {
        var path = InTempInventory(BareInventory);
        try
        {
            InventoryWriter.BackfillPredecessors(path);
            // Insert a historical build BETWEEN 2000 and 3000.
            InventoryWriter.AppendBuild(path, new InventoryBuildRecord(BuildId: 2500, DateUtc: "2026-02-15T00:00:00Z", Era: "e2"));

            var builds = Builds(path);
            // Order is newest-first: 3000, 2500, 2000, 1000.
            Assert.Equal(2500, PredecessorOf((JsonObject)builds[0]!));   // 3000 now points at the inserted 2500
            Assert.Equal(2000, PredecessorOf((JsonObject)builds[1]!));   // 2500 -> 2000
            Assert.Equal(1000, PredecessorOf((JsonObject)builds[2]!));   // 2000 -> 1000
            Assert.Null(PredecessorOf((JsonObject)builds[3]!));          // 1000 floor
        }
        finally { File.Delete(path); }
    }
}
