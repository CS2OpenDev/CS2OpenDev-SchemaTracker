// Maintenance command: (re)derive builds[].predecessor in data/cs2-assets-inventory.json.
//
// Idempotent. Sets every build's `predecessor` from its neighbours (the next-lower committed
// build_id; null for the in-scope floor) and rewrites the inventory in canonical form. Seeds the
// field for the schema-evolution feature and repairs it if it ever drifts; forward-capture keeps it
// current thereafter (InventoryWriter.AppendBuild self-heals on every append).
//
// Surface: backfill-predecessors --inventory <path>
//   --inventory defaults to CS2_INVENTORY_PATH / appsettings, else the repo-root data/cs2-assets-inventory.json.

using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Inventory;
using Cs2SchemaTracker.Host.Walker;

namespace Cs2SchemaTracker.Host.Cli;

internal static class BackfillPredecessorsCommand
{
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker backfill-predecessors — (re)derive builds[].predecessor in the inventory.

Usage:
  cs2-schema-tracker backfill-predecessors [--inventory <path>]

Idempotent: sets each build's predecessor from its neighbours (next-lower build_id; null at the
in-scope floor) and rewrites the inventory canonically. Exit codes: 0 ok · 65 missing/invalid inventory.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        parsed.TryGetValue("inventory", out var inventoryArg);

        var inventoryPath = !string.IsNullOrEmpty(inventoryArg)
            ? inventoryArg
            : HostConfig.InventoryPath
              ?? Path.Combine(EraWalkerResolver.DiscoverRepoRoot(), "data", "cs2-assets-inventory.json");

        try
        {
            InventoryWriter.BackfillPredecessors(inventoryPath);
            Console.Error.WriteLine($"backfill-predecessors: rewrote predecessors in {inventoryPath}.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"backfill-predecessors: {ex.Message}");
            return 65;
        }
    }
}
