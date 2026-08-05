// Per-map radar/overview metadata extraction (map_overviews.json).
//
// Pipeline (clones LocalizationEmitter's per-file-family shape, but KV1): open a content-depot
// pak01_dir.vpk (VpkArchive) -> enumerate every resource/overviews/<map>.txt entry ->
// for each, extract bytes (CRC-verified) -> parse the KV1 text (Kv1) -> map the single top-level
// block (keyed by map name) into a MapOverview row -> aggregate all maps -> serialize canonical
// proto3 JSON -> atomic .tmp+rename.
//
// === resource/overviews/<map>.txt KV1 shape (CONFIRMED 2026-06-19) ===
//   "de_dust2" { "material" "overviews/de_dust2_v2" "pos_x" "-2476" "pos_y" "3239"
//                "scale" "4.4" "rotate" "1" "zoom" "1.1"
//                "CTSpawn_x" "0.62" ... "bombA_x" "0.80" ... }
//
// === SHAPE DECISION (see map_overviews.proto header) ===
// Well-known radar fields (material, pos_x/y, scale, rotate, zoom, the bombsite + spawn positions)
// are surfaced as named string fields (verbatim — floats kept as strings for byte-stability).
// Everything else is preserved in a sorted `properties` bag (faithful long-tail capture). A flat
// `map_names` inventory lists every shipped overview.
//
// Invariants:
//   Determinism: maps by name Ordinal; each map's properties by name Ordinal; map_names Ordinal +
//     de-duped. Canonical JSON, LF, UTF-8 no BOM.
//   Fail-loud: missing vpk / zero overview entries / malformed KV1 / a file with no top-level block
//     / a duplicate map name / zero maps — all throw BEFORE any output bytes.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and atomically
//     rename.

using System.Text;
using System.Text.RegularExpressions;

using Cs2SchemaTracker.Host.GameEvents;   // Kv1 / Kv1Node
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.MapOverviews;

/// <summary>
/// Extracts the <c>resource/overviews/*.txt</c> KV1 family from a content-depot VPK and writes
/// the canonical aggregated map_overviews.json. Host-only identity fields are stamped by the
/// constructor; radar metadata comes verbatim from the parsed KV1.
/// </summary>
internal sealed class MapOverviewsEmitter
{
    private static readonly Regex OverviewFileRegex =
        new("^resource/overviews/[^/]+\\.txt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The well-known radar keys, mapped onto the typed proto fields. Everything NOT here lands in
    // the per-map `properties` bag (faithful long-tail capture).
    private static readonly HashSet<string> WellKnownKeys = new(StringComparer.Ordinal)
    {
        "material", "pos_x", "pos_y", "scale", "rotate", "zoom",
        "bombA_x", "bombA_y", "bombB_x", "bombB_y",
        "CTSpawn_x", "CTSpawn_y", "TSpawn_x", "TSpawn_y",
    };

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public MapOverviewsEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    public void EmitFromVpk(string vpkDirPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkDirPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var archive = VpkArchive.Open(vpkDirPath);
        Emit(archive, outputPath);
    }

    /// <summary>
    /// True iff <paramref name="archive"/> ships at least one <c>resource/overviews/*.txt</c> entry
    /// in its directory tree. Distinguishes a GENUINE absence (no overviews shipped this era ⇒
    /// graceful omission) from a present-but-unreadable source (a missing backing chunk, which
    /// <see cref="Emit"/> still fails loud on). Directory-tree check only.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        foreach (var entry in archive.Entries)
        {
            if (OverviewFileRegex.IsMatch(entry.FullPath))
            {
                return true;
            }
        }
        return false;
    }

    public void Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        // Discover overview files; iterate deterministically (Ordinal by full path).
        var files = new SortedDictionary<string, VpkDirectoryEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (OverviewFileRegex.IsMatch(entry.FullPath))
            {
                files[entry.FullPath] = entry;
            }
        }

        if (files.Count == 0)
        {
            throw new InvalidDataException(
                "MapOverviewsEmitter: no 'resource/overviews/*.txt' entries in the VPK — refusing "
                + "to write map_overviews.json. Was the correct content pak01_dir.vpk supplied?");
        }

        var maps = new List<MapOverview>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (fullPath, entry) in files)
        {
            byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified.
            string text = Encoding.UTF8.GetString(bytes);
            IReadOnlyList<Kv1Node> roots = Kv1.Parse(text, fullPath);
            Kv1Node block = LocateWrapperBlock(roots, fullPath);

            // Identity = the FILE STEM (resource/overviews/<stem>.txt), not the inner block key.
            // The two are usually equal, but Valve ships variant overview files whose INNER block
            // key collides while the stem stays unique: e.g. de_inferno.txt AND de_inferno_s2.txt
            // both inner-key "de_inferno"; de_overpass.txt AND de_overpass_2v2.txt both "de_overpass".
            // Keying on the inner block key made those a fail-loud duplicate (killing the whole
            // extract) and would have dropped one radar definition. The stem is unique per dir, so
            // every shipped overview is preserved and the row order stays a total order.
            // SCHEMA-SHAPE FLAG: a `block_name` field on MapOverview would also carry the inner
            // key when it differs from the stem; see report.
            string stem = fullPath[(fullPath.LastIndexOf('/') + 1)..];
            if (stem.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^4];
            }

            var map = MapOne(block, stem);
            if (!seenNames.Add(map.Name))
            {
                // File stems are unique within one directory tree, so this is unreachable in
                // practice; kept as a fail-loud guard against a malformed tree.
                throw new InvalidDataException(
                    $"MapOverviewsEmitter: duplicate overview file stem '{map.Name}' "
                    + $"(latest from '{fullPath}').");
            }
            maps.Add(map);
        }

        if (maps.Count == 0)
        {
            throw new InvalidDataException(
                "MapOverviewsEmitter: parsed zero map overviews — refusing to write an empty "
                + "map_overviews.json.");
        }

        maps.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

        var document = new Schemas.MapOverviews
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };
        document.Maps.AddRange(maps);
        foreach (var name in maps.Select(m => m.Name).Distinct(StringComparer.Ordinal)
                                 .OrderBy(n => n, StringComparer.Ordinal))
        {
            document.MapNames.Add(name);
        }

        string json = SerializeCanonical(document);
        AtomicWrite(outputPath, json);
    }

    private static MapOverview MapOne(Kv1Node block, string mapName)
    {
        // block_name carries the inner KV1 block key WHEN it differs from the file stem (the variant
        // case: de_inferno_s2.txt inner-keys "de_inferno"). "" when they are equal (the common case),
        // so the faithful logical-map link is preserved without redundancy.
        var map = new MapOverview
        {
            Name = mapName,
            BlockName = string.Equals(block.Key, mapName, StringComparison.Ordinal) ? "" : block.Key,
        };

        // Collect scalars (last-occurrence-wins per key). Well-known keys route to typed fields;
        // everything else to the sorted properties bag.
        var scalars = new Dictionary<string, string>(StringComparer.Ordinal);
        var other = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in block.Children!)
        {
            if (child.IsBlock)
            {
                continue;   // overview files are flat scalar tables; ignore any nested block.
            }
            string value = child.Value ?? "";
            scalars[child.Key] = value;
            if (!WellKnownKeys.Contains(child.Key))
            {
                other[child.Key] = value;
            }
        }

        map.Material = Get(scalars, "material");
        map.PosX = Get(scalars, "pos_x");
        map.PosY = Get(scalars, "pos_y");
        map.Scale = Get(scalars, "scale");
        map.Rotate = Get(scalars, "rotate");
        map.Zoom = Get(scalars, "zoom");
        map.BombAX = Get(scalars, "bombA_x");
        map.BombAY = Get(scalars, "bombA_y");
        map.BombBX = Get(scalars, "bombB_x");
        map.BombBY = Get(scalars, "bombB_y");
        map.CtSpawnX = Get(scalars, "CTSpawn_x");
        map.CtSpawnY = Get(scalars, "CTSpawn_y");
        map.TSpawnX = Get(scalars, "TSpawn_x");
        map.TSpawnY = Get(scalars, "TSpawn_y");

        foreach (var (name, value) in other)
        {
            map.Properties.Add(new MapOverviewProperty { Name = name, Value = value });
        }
        return map;
    }

    private static string Get(Dictionary<string, string> scalars, string key)
        => scalars.TryGetValue(key, out var v) ? v : "";

    private static Kv1Node LocateWrapperBlock(IReadOnlyList<Kv1Node> roots, string sourceName)
    {
        foreach (var root in roots)
        {
            if (root.IsBlock)
            {
                return root;
            }
        }
        throw new InvalidDataException(
            $"MapOverviewsEmitter: '{sourceName}' has no top-level block — expected the per-map "
            + "overview wrapper.");
    }

    private static void AtomicWrite(string outputPath, string json)
    {
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var tmpPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, Encoding.UTF8.GetBytes(json));
            File.Move(tmpPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try
                { File.Delete(tmpPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true)
            .WithIndentation("  "));

    internal static string SerializeCanonical(IMessage message)
    {
        string formatted = Formatter.Format(message);
        return CanonicalJson.SerializeRawJson(formatted);
    }
}
