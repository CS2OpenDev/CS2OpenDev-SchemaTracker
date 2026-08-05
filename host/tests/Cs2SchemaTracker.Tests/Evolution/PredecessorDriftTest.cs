// Coverage for PredecessorDriftCheck — the inventory<->on-disk predecessor reconciliation.
//
// Builds a synthetic artifacts tree (empty platform dirs are enough for the numeric rule) and a
// minimal inventory, with a deliberate platform GAP (a build the inventory chains through but that is
// not committed for the platform). Asserts: (a) a correct inventory agrees with the on-disk rule
// across the gap; (b) a tampered inventory predecessor is caught.

using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class PredecessorDriftTest
{
    private const string Platform = "linux-x86_64";

    // Inventory (newest-first) 4000->3000->2000->1000->floor. The on-disk tree omits build 3000 for
    // the platform, so the numeric rule's predecessor of 4000 is 2000 (skipping 3000). A correct
    // inventory chain, walked skipping the uncommitted 3000, must also land on 2000.
    private static string Inventory(uint predOf4000) => $$"""
        {
          "app": { "app_id": 730 },
          "depots": [],
          "builds": [
            { "build_id": 4000, "predecessor": {{predOf4000}} },
            { "build_id": 3000, "predecessor": 2000 },
            { "build_id": 2000, "predecessor": 1000 },
            { "build_id": 1000, "predecessor": null }
          ]
        }
        """;

    private static void InTree(string inventoryJson, Action<string, AssetsInventory> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "pred-drift-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        // Commit builds 1000, 2000, 4000 for the platform (3000 is the gap).
        foreach (var b in new[] { "1000", "2000", "4000" })
            Directory.CreateDirectory(Path.Combine(root, b, Platform));
        var invPath = Path.Combine(work, "inv.json");
        File.WriteAllText(invPath, inventoryJson.ReplaceLineEndings("\n"));
        try
        { body(root, AssetsInventory.Load(invPath)); }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void Correct_inventory_agrees_across_a_platform_gap()
    {
        InTree(Inventory(predOf4000: 3000), (root, inv) =>
        {
            // 4000 -> (3000 uncommitted, skip) -> 2000 == on-disk Resolve(4000) = 2000. No drift.
            Assert.Empty(PredecessorDriftCheck.FindDisagreements(root, inv, Platform));
        });
    }

    [Fact]
    public void Tampered_inventory_predecessor_is_caught()
    {
        // 4000's inventory predecessor wrongly points at 1000 (committed), but the on-disk rule says
        // 2000 — a genuine disagreement the gate must surface.
        InTree(Inventory(predOf4000: 1000), (root, inv) =>
        {
            var issues = PredecessorDriftCheck.FindDisagreements(root, inv, Platform);
            Assert.Contains(issues, m => m.Contains("build 4000") && m.Contains("2000") && m.Contains("1000"));
        });
    }
}
