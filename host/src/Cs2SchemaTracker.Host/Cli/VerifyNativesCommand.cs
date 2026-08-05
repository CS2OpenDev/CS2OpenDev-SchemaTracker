// Host-owned native-bundle completeness validator (`verify-natives`).
//
// The release bundle ships one walker binary per compile-pin era, per platform, under
// natives/<platform>/<era>[.exe]. assemble-bundle used to re-derive that expected set by hand-parsing
// the inventory in PowerShell; this command owns the check so the MSBuild bundle target (and any
// script) gates on the host's own InventoryCatalog — the compile-pin era list can never drift.
//
// A gap (a missing era binary, or an absent platform dir) is fail-loud: exit 65 with each missing
// path on stderr. runtime-variant eras produce no binary (they ride a compile pin) and are excluded.
//
// --platform <p> narrows the check to ONE canonical platform (used by the per-RID self-contained
// bundle target, which stages only its own platform's natives); omitted, BOTH platforms are required.
//
// Exit codes: 0 complete · 64 usage error · 65 incomplete (missing binaries).

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Inventory;

namespace Cs2SchemaTracker.Host.Cli;

internal static class VerifyNativesCommand
{
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker verify-natives — assert the native bundle carries every compile-pin
era's walker binary for both platforms (natives/<platform>/<era>[.exe]).

Usage:
  cs2-schema-tracker verify-natives --natives <root> [--platform <p>] [--inventory <path>]

Arguments:
  --natives <root>   The natives/ root produced by the build-era-walkers scripts (required).
  --platform <p>     Narrow the check to one platform (linux-x86_64|windows-x86_64); default: both.
  --inventory <path> Inventory path (default: data/cs2-assets-inventory.json).

Exit codes: 0 complete · 64 usage error · 65 incomplete (missing binaries).");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("natives", out var nativesRoot) || string.IsNullOrEmpty(nativesRoot))
        {
            Console.Error.WriteLine("verify-natives: --natives <root> is required.");
            return 64;
        }
        var inventoryPath = Path.GetFullPath(
            parsed.TryGetValue("inventory", out var inv) && !string.IsNullOrEmpty(inv)
                ? inv
                : InventoryCatalog.DefaultRelativePath);

        // --platform narrows the check to one canonical platform (the per-RID bundle case); default both.
        IReadOnlyList<string> platforms = ArtifactSet.CanonicalPlatforms;
        if (parsed.TryGetValue("platform", out var plat) && !string.IsNullOrEmpty(plat))
        {
            if (!ArtifactSet.CanonicalPlatforms.Contains(plat))
            {
                Console.Error.WriteLine(
                    $"verify-natives: unknown --platform '{plat}'. Valid: {string.Join(", ", ArtifactSet.CanonicalPlatforms)}.");
                return 64;
            }
            platforms = new[] { plat };
        }

        var catalog = InventoryCatalog.LoadFromFile(inventoryPath);
        var eras = catalog.Eras.Where(e => e.IsCompilePin).Select(e => e.Era).ToList();
        if (eras.Count == 0)
        {
            Console.Error.WriteLine("verify-natives: inventory has no compile-pin eras — nothing to bundle.");
            return 65;
        }

        var missing = new List<string>();
        foreach (var platform in platforms)
        {
            var dir = Path.Combine(nativesRoot, platform);
            if (!Directory.Exists(dir))
            {
                missing.Add($"{platform}/ (directory absent — run the {platform} walker build first)");
                continue;
            }
            // windows binaries carry the .exe suffix; linux binaries are bare-named.
            var suffix = platform == "windows-x86_64" ? ".exe" : "";
            foreach (var era in eras)
            {
                if (!File.Exists(Path.Combine(dir, era + suffix)))
                {
                    missing.Add($"{platform}/{era}{suffix}");
                }
            }
        }

        if (missing.Count > 0)
        {
            Console.Error.WriteLine("verify-natives: natives/ is INCOMPLETE — build the walkers first. Missing:");
            foreach (var m in missing)
                Console.Error.WriteLine($"  {m}");
            return 65;
        }

        Console.WriteLine(
            $"verify-natives: OK — {eras.Count} compile-pin era binaries present for each of: " +
            string.Join(", ", platforms));
        return 0;
    }
}
