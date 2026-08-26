// record-build: append or fact-merge one build's row in the assets inventory, reading the
// promoted artifacts/<build>/<platform>/provenance.json in the CURRENT checkout.
//
// The scheduled pipeline's commit job runs this once per (build, platform) it lands, against a
// checkout of main's true tip. The extraction legs' own inventory edits never leave their runners:
// recording against the tip is what lets concurrent inventory writers (an operator backfill push,
// a second build in the same run, a re-run leg) compose instead of clobbering whole files.
// ForwardCaptureRecorder holds the judgement: a new build appends a row; an existing row gains only
// its MISSING facts (the other platform's binaries GID, absent tools/content), and a present value
// is never rewritten.
//
// Exit codes: 0 appended/merged/no-op · 64 usage error · 65 no provenance to record from /
// unreadable inventory or catalog.

using Cs2SchemaTracker.Host.Inventory;
using Cs2SchemaTracker.Host.Walker;

namespace Cs2SchemaTracker.Host.Cli;

internal static class RecordBuildCommand
{
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker record-build: append or fact-merge one build's row in
data/cs2-assets-inventory.json from the promoted provenance.json in the current checkout.

Usage:
  cs2-schema-tracker record-build --build <id> --platform <p> [--inventory <path>]

Arguments:
  --build <id>       Build id whose promoted set is on disk (required).
  --platform <p>     linux-x86_64 or windows-x86_64 (required).
  --inventory <path> Inventory path (default: the repo's data/cs2-assets-inventory.json).

Behavior:
  A build absent from builds[] is appended (era resolved from the catalog, date/GIDs from
  artifacts/<build>/<platform>/provenance.json). A build already present gains only its MISSING
  facts (this platform's binaries GID, absent tools/content); present values are never rewritten.

Exit codes: 0 appended/merged/no-op · 64 usage error · 65 no provenance / unreadable inventory.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("build", out var build) || string.IsNullOrEmpty(build))
        {
            Console.Error.WriteLine("record-build: --build <id> is required.");
            return 64;
        }
        if (!parsed.TryGetValue("platform", out var platform) || string.IsNullOrEmpty(platform))
        {
            Console.Error.WriteLine("record-build: --platform <linux-x86_64|windows-x86_64> is required.");
            return 64;
        }

        try
        {
            var resolver = new EraWalkerResolver();
            var inventoryPath = parsed.TryGetValue("inventory", out var inv) && !string.IsNullOrEmpty(inv)
                ? Path.GetFullPath(inv)
                : InventoryCatalogProvider.ResolveInventoryPath(resolver.RepoRoot);

            var outcome = ForwardCaptureRecorder.RecordIfNew(
                inventoryPath, resolver.RepoRoot, build, platform, resolver);
            if (outcome == ForwardCaptureRecorder.Outcome.Skipped)
            {
                Console.Error.WriteLine(
                    $"record-build: nothing to record for build '{build}' ({platform}): no promoted " +
                    $"provenance.json under artifacts/{build}/{platform}/ (or a non-numeric build id).");
                return 65;
            }

            Console.WriteLine($"record-build: {outcome}: build {build} ({platform}) in '{inventoryPath}'.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"record-build: FAILED: {ex.GetType().Name}: {ex.Message}");
            return 65;
        }
    }
}
