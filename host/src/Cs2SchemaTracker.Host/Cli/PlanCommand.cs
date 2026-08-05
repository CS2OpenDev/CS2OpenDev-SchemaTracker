// Host-owned build-target selector CLI (`plan`).
//
// Single source of truth for the target-selection every build/validate/bundle script used to
// re-derive by hand-parsing data/cs2-assets-inventory.json (PowerShell ConvertFrom-Json in some,
// Python heredocs in others). Those independent reimplementations were a drift hazard; this command
// projects the host's own InventoryCatalog model into a stable, machine-readable list so each script
// consumes ONE authoritative selection.
//
// Targets (--targets):
//   compile-pins  The compile-pin eras (the only eras that produce a walker binary). Each row
//                 carries era, hl2sdkSha, and the per-platform layoutSignature. build-era-walkers.*
//                 iterate these (and skip a row whose --platform signature is empty — not yet
//                 validated on that platform); assemble-bundle just needs the era ids.
//   validation    The bundle-validation build set: every era's OLDEST and NEWEST build_id (by
//                 build_id, deduped when an era has a single build), era-sorted. validate-bundle.*
//                 extract each with the shipped bundle and byte-verify against artifacts/.
//
// Output (--format): json (default) or tsv. tsv is the low-dependency shape the bash consumers read
// with `mapfile`/`while read` — no jq/python needed. Deterministic: stable ordering, no timestamps.
//
// Exit codes: 0 ok · 64 usage error. Fail-loud: a missing/malformed inventory throws (never a
// partially-valid selection).

using System.Globalization;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host.Inventory;

namespace Cs2SchemaTracker.Host.Cli;

internal static class PlanCommand
{
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker plan — project the assets inventory into the authoritative
build-target selection scripts consume (target-selection lives here, not in each script).

Usage:
  cs2-schema-tracker plan --targets compile-pins [--platform <p>] [--format json|tsv] [--inventory <path>]
  cs2-schema-tracker plan --targets validation [--format json|tsv] [--inventory <path>]

Arguments:
  --targets <kind>   compile-pins | validation (required).
  --platform <p>     linux-x86_64 or windows-x86_64. Scopes compile-pins' layoutSignature to that
                     platform (required for --format tsv with compile-pins).
  --format <fmt>     json (default) or tsv.
  --inventory <path> Inventory path (default: data/cs2-assets-inventory.json).

targets:
  compile-pins  One row per compile-pin era (the eras that produce a walker binary): era, hl2sdkSha,
                and per-platform layoutSignature. runtime-variant eras are excluded (they ride a
                compile pin). tsv columns: era<TAB>hl2sdkSha<TAB>layoutSignature (needs --platform).
  validation    Every era's oldest+newest build_id (deduped for single-build eras), era-sorted.
                tsv columns: build_id<TAB>era.

Exit codes: 0 ok · 64 usage error.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        if (!parsed.TryGetValue("targets", out var targets) || string.IsNullOrEmpty(targets))
        {
            Console.Error.WriteLine("plan: --targets is required (compile-pins | validation). Run 'plan --help'.");
            return 64;
        }

        var format = parsed.TryGetValue("format", out var f) && !string.IsNullOrEmpty(f) ? f : "json";
        if (format is not ("json" or "tsv"))
        {
            Console.Error.WriteLine($"plan: --format '{format}' is not valid (json | tsv).");
            return 64;
        }

        parsed.TryGetValue("platform", out var platform);
        var inventoryPath = Path.GetFullPath(
            parsed.TryGetValue("inventory", out var inv) && !string.IsNullOrEmpty(inv)
                ? inv
                : InventoryCatalog.DefaultRelativePath);

        // Fail-loud: LoadFromFile throws InvalidDataException on a missing/malformed/empty inventory.
        var catalog = InventoryCatalog.LoadFromFile(inventoryPath);

        switch (targets)
        {
            case "compile-pins":
                return EmitCompilePins(catalog, platform, format);
            case "validation":
                return EmitValidation(catalog, format);
            default:
                Console.Error.WriteLine($"plan: unknown --targets '{targets}' (compile-pins | validation).");
                return 64;
        }
    }

    private static int EmitCompilePins(InventoryCatalog catalog, string? platform, string format)
    {
        // Inventory order preserved (each era is built independently; order is not significant).
        var eras = catalog.Eras.Where(e => e.IsCompilePin).ToList();

        if (format == "tsv")
        {
            if (string.IsNullOrEmpty(platform))
            {
                Console.Error.WriteLine("plan: --targets compile-pins --format tsv requires --platform (the layoutSignature column is per-platform).");
                return 64;
            }
            var sb = new StringBuilder();
            foreach (var e in eras)
            {
                var sig = e.LayoutSignatures?.GetValueOrDefault(platform) ?? "";
                sb.Append(e.Era).Append('\t').Append(e.Hl2SdkSha ?? "").Append('\t').Append(sig).Append('\n');
            }
            Console.Out.Write(sb.ToString());
            return 0;
        }

        WriteJson(writer =>
        {
            writer.WriteStartArray();
            foreach (var e in eras)
            {
                writer.WriteStartObject();
                writer.WriteString("era", e.Era);
                writer.WriteString("hl2sdkSha", e.Hl2SdkSha);
                if (string.IsNullOrEmpty(platform))
                {
                    writer.WriteStartObject("layoutSignatures");
                    foreach (var kv in e.LayoutSignatures ?? [])
                    {
                        writer.WriteString(kv.Key, kv.Value);
                    }
                    writer.WriteEndObject();
                }
                else
                {
                    writer.WriteString("layoutSignature", e.LayoutSignatures?.GetValueOrDefault(platform) ?? "");
                }
                if (e.MinClasses is int min)
                    writer.WriteNumber("minClasses", min);
                if (e.MaxClasses is int max)
                    writer.WriteNumber("maxClasses", max);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        return 0;
    }

    private static int EmitValidation(InventoryCatalog catalog, string format)
    {
        // Every era's oldest + newest build_id (deduped for single-build eras), era-sorted ascending;
        // oldest before newest. This is the exact selection validate-bundle.ps1 / -linux.sh derived.
        var byEra = new SortedDictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var b in catalog.Builds)
        {
            if (!byEra.TryGetValue(b.Era, out var list))
            {
                list = new List<long>();
                byEra[b.Era] = list;
            }
            list.Add(b.BuildId);
        }

        var rows = new List<(long BuildId, string Era, string Position)>();
        foreach (var (era, ids) in byEra)
        {
            ids.Sort();
            var oldest = ids[0];
            var newest = ids[^1];
            rows.Add((oldest, era, "oldest"));
            if (newest != oldest)
                rows.Add((newest, era, "newest"));
        }

        if (format == "tsv")
        {
            var sb = new StringBuilder();
            foreach (var r in rows)
            {
                sb.Append(r.BuildId.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(r.Era).Append('\n');
            }
            Console.Out.Write(sb.ToString());
            return 0;
        }

        WriteJson(writer =>
        {
            writer.WriteStartArray();
            foreach (var r in rows)
            {
                writer.WriteStartObject();
                writer.WriteNumber("build_id", r.BuildId);
                writer.WriteString("era", r.Era);
                writer.WriteString("position", r.Position);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        return 0;
    }

    /// <summary>
    /// Serialize an array to indented JSON and write it through <see cref="Console.Out"/> (so it
    /// respects a redirected writer, unlike writing to the raw stdout stream). Trailing newline.
    /// </summary>
    private static void WriteJson(Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            body(writer);
        }
        Console.Out.Write(Encoding.UTF8.GetString(buffer.ToArray()));
        Console.Out.Write('\n');
    }
}
