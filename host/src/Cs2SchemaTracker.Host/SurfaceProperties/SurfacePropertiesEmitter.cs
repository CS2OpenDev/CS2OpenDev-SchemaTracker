// Surface-property extraction (surface_properties.json).
//
// Pipeline (clones ItemDefinitionsEmitter, but over a FAMILY of KV3-TEXT files): open a
// content-depot pak01_dir.vpk (VpkArchive) -> for each scripts/surfaceproperties_*.txt
// entry, extract bytes (CRC-verified by the VPK layer) -> parse the KV3 text (Kv3) -> harvest
// the SurfacePropertiesList array -> flatten each material to a (name, source_file) Surface row
// carrying a sorted name->value property bag -> COMBINE all four files -> serialize the canonical
// proto3 JSON -> atomic .tmp+rename.
//
// === The source family (CONFIRMED against cached content VPKs, 2026-06-19) ===
// FOUR sibling KV3-text files, all packed in pak01, all shaped
//   `{ SurfacePropertiesList = [ { surfacePropertyName = "..." ... } ] }`
// with DISJOINT field sets per material (game=physics, footsteps=sounds,
// impact_effects=particles/decals, steamaudio=acoustics).
//
// === SHAPE DECISION (generic property bag; see surface_properties.proto header) ===
// One repeated `surfaces`, each `Surface` keyed by (name, source_file). The four files
// produce four rows per material, distinguished by source_file. A material's scalars are a
// sorted name->value bag; a nested array/map property carries its KV3-text rendering verbatim
// (no source data dropped). This is FAITHFUL + structured (flattened material->property pairs,
// NOT a raw KV tree).
//
// Invariants:
//   Determinism: surfaces by (name, source_file) Ordinal; each Surface's properties by name Ordinal.
//     Canonical JSON, LF, UTF-8 no BOM.
//   Fail-loud: missing vpk / zero surfaceproperties_*.txt entries / malformed KV3 / a
//     SurfacePropertiesList that is not an array / an entry missing surfacePropertyName / zero
//     surfaces across all files — all throw BEFORE any output bytes. No catch-and-continue.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and atomically
//     rename.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Cs2SchemaTracker.Host.EntitySchema;     // Kv3 / Kv3ParseException (shared minimal KV3-text parser)
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Cs2SchemaTracker.Host.SurfaceProperties;

/// <summary>
/// Extracts the <c>scripts/surfaceproperties_*.txt</c> KV3-text family from a content-depot VPK
/// and writes the canonical combined surface_properties.json. Host-only identity fields are stamped
/// by the constructor; properties come verbatim from the parsed KV3.
/// </summary>
internal sealed class SurfacePropertiesEmitter
{
    /// <summary>The depot-relative path prefix of the surfaceproperties family inside pak01.</summary>
    private static readonly Regex SurfaceFileRegex =
        new("^scripts/surfaceproperties_[a-z_]+\\.txt$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The KV3 array key holding the per-material entries.</summary>
    private const string ListKey = "SurfacePropertiesList";

    /// <summary>The KV3 scalar key naming the material.</summary>
    private const string NameKey = "surfacePropertyName";

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public SurfacePropertiesEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Open the <paramref name="vpkDirPath"/> (a <c>*_dir.vpk</c>), extract and parse every
    /// <c>scripts/surfaceproperties_*.txt</c>, and write surface_properties.json to
    /// <paramref name="outputPath"/>. Fail-loud: throws before any output bytes.
    /// </summary>
    public void EmitFromVpk(string vpkDirPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkDirPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var archive = VpkArchive.Open(vpkDirPath);
        Emit(archive, outputPath);
    }

    /// <summary>
    /// True iff <paramref name="archive"/> ships at least one <c>scripts/surfaceproperties_*.txt</c>
    /// entry in its directory tree. Distinguishes a GENUINE absence (the file family was never
    /// shipped this era ⇒ graceful omission) from a present-but-unreadable source (a missing backing
    /// chunk / CRC failure, which <see cref="Emit"/> still fails loud on). A directory-tree check
    /// only — never touches an archive chunk.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        foreach (var entry in archive.Entries)
        {
            if (SurfaceFileRegex.IsMatch(entry.FullPath))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Map every <c>scripts/surfaceproperties_*.txt</c> in <paramref name="archive"/> into the
    /// public combined <see cref="Schemas.SurfaceProperties"/> message and write the canonical
    /// surface_properties.json. All validation + the full document build happen before any write.
    /// </summary>
    public void Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        // Discover the source files; iterate deterministically (Ordinal by full path).
        var sourceFiles = new SortedDictionary<string, VpkDirectoryEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (SurfaceFileRegex.IsMatch(entry.FullPath))
            {
                sourceFiles[entry.FullPath] = entry;
            }
        }

        if (sourceFiles.Count == 0)
        {
            throw new InvalidDataException(
                "SurfacePropertiesEmitter: no 'scripts/surfaceproperties_*.txt' entries in the VPK — "
                + "refusing to write surface_properties.json. Was the correct content "
                + "pak01_dir.vpk supplied?");
        }

        var surfaces = new List<Surface>();
        foreach (var (fullPath, entry) in sourceFiles)
        {
            // source_file tag is the base file name (e.g. "surfaceproperties_game.txt").
            string sourceFile = fullPath[(fullPath.LastIndexOf('/') + 1)..];

            byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified; throws on mismatch.
            string text = Encoding.UTF8.GetString(bytes);

            Value root = ParseKv3(text, fullPath);
            foreach (var surface in MapFile(root, sourceFile, fullPath))
            {
                surfaces.Add(surface);
            }
        }

        if (surfaces.Count == 0)
        {
            throw new InvalidDataException(
                "SurfacePropertiesEmitter: parsed zero surfaces across all surfaceproperties_*.txt "
                + "files — refusing to write an empty surface_properties.json.");
        }

        // Sort by (name, source_file, scope) Ordinal, with a property-bag tiebreak so the order is a
        // TOTAL order even when two rows share (name, source_file, scope) — possible if one file
        // lists a surfacePropertyName twice. List.Sort is unstable, so without a tiebreak such rows
        // would reorder run-to-run.
        surfaces.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0)
                return c;
            c = string.CompareOrdinal(a.SourceFile, b.SourceFile);
            if (c != 0)
                return c;
            c = string.CompareOrdinal(a.Scope, b.Scope);
            if (c != 0)
                return c;
            return string.CompareOrdinal(PropertyBagKey(a), PropertyBagKey(b));
        });

        var document = new Schemas.SurfaceProperties
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };
        document.Surfaces.AddRange(surfaces);

        string json = SerializeCanonical(document);
        AtomicWrite(outputPath, json);
    }

    // Parse KV3 text fail-loud: unlike the KV3-class-defaults caller (which degrades gracefully), a
    // surfaceproperties file that does not parse is a hard input failure here.
    private static Value ParseKv3(string text, string sourceName)
    {
        try
        {
            return Kv3.Parse(text);
        }
        catch (Kv3ParseException ex)
        {
            throw new InvalidDataException(
                $"SurfacePropertiesEmitter: '{sourceName}' is not valid KV3 text: {ex.Message}.");
        }
    }

    // The surfaceproperties family ships TWO confirmed shapes (both eras, 2023-2026):
    //   (A) TOP-LEVEL:   { SurfacePropertiesList = [ … ] }
    //                    — surfaceproperties_game / impact_effects / steamaudio.
    //   (B) PER-ACTOR:   { ct_player = { SurfacePropertiesList = [ … ] }, t_player = { … } }
    //                    — surfaceproperties_footsteps.txt (one list per player faction).
    // Shape (B) is NOT era drift — footsteps has ALWAYS nested under an actor key — the original
    // emitter simply never handled it. We parse both faithfully: shape (A) yields scope=""; each
    // shape-(B) actor key yields scope=<actorKey>, carried in Surface.scope so ct_player's and
    // t_player's same-named materials stay DISTINCT rows (and the sort stays a total order).
    private static IEnumerable<Surface> MapFile(Value root, string sourceFile, string sourceName)
    {
        if (root.KindCase != Value.KindOneofCase.StructValue)
        {
            throw new InvalidDataException(
                $"SurfacePropertiesEmitter: '{sourceName}' top-level value is not a KV3 map.");
        }

        // Locate every (scope, SurfacePropertiesList) the file carries. Scopes iterate Ordinal so
        // the row order is deterministic regardless of KV3 field order.
        var scoped = new SortedDictionary<string, Value>(StringComparer.Ordinal);
        if (root.StructValue.Fields.TryGetValue(ListKey, out var topList))
        {
            scoped[""] = topList;   // shape (A): top-level list, no scope.
        }
        else
        {
            // shape (B): each top-level field that is a map containing a SurfacePropertiesList is an
            // actor scope. A file with neither a top-level list NOR any nested list is a structural
            // surprise (NOT a genuine-absence — the file IS present) ⇒ fail loud.
            foreach (var (key, value) in root.StructValue.Fields)
            {
                if (value.KindCase == Value.KindOneofCase.StructValue
                    && value.StructValue.Fields.TryGetValue(ListKey, out var nestedList))
                {
                    scoped[key] = nestedList;
                }
            }
            if (scoped.Count == 0)
            {
                throw new InvalidDataException(
                    $"SurfacePropertiesEmitter: '{sourceName}' has no '{ListKey}' array at the top level "
                    + $"nor nested one level under an actor key.");
            }
        }

        foreach (var (scope, listVal) in scoped)
        {
            foreach (var surface in MapList(listVal, sourceFile, scope, sourceName))
            {
                yield return surface;
            }
        }
    }

    private static IEnumerable<Surface> MapList(Value listVal, string sourceFile, string scope, string sourceName)
    {
        if (listVal.KindCase != Value.KindOneofCase.ListValue)
        {
            throw new InvalidDataException(
                $"SurfacePropertiesEmitter: '{sourceName}' '{ListKey}' is not an array.");
        }

        foreach (var element in listVal.ListValue.Values)
        {
            if (element.KindCase != Value.KindOneofCase.StructValue)
            {
                throw new InvalidDataException(
                    $"SurfacePropertiesEmitter: '{sourceName}' '{ListKey}' entry is not a map.");
            }
            var fields = element.StructValue.Fields;
            if (!fields.TryGetValue(NameKey, out var nameVal)
                || nameVal.KindCase != Value.KindOneofCase.StringValue
                || nameVal.StringValue.Length == 0)
            {
                throw new InvalidDataException(
                    $"SurfacePropertiesEmitter: '{sourceName}' '{ListKey}' entry is missing a non-empty "
                    + $"'{NameKey}'.");
            }

            var surface = new Surface { Name = nameVal.StringValue, SourceFile = sourceFile, Scope = scope };

            // Every other field becomes a sorted name->value scalar property. The name key itself is
            // excluded (it is the row key, not a property).
            var props = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in fields)
            {
                if (string.Equals(key, NameKey, StringComparison.Ordinal))
                {
                    continue;
                }
                props[key] = RenderScalar(value);
            }
            foreach (var (name, val) in props)
            {
                surface.Properties.Add(new SurfaceProperty { Name = name, Value = val });
            }

            yield return surface;
        }
    }

    /// <summary>
    /// A deterministic total-order tiebreak key for a <see cref="Surface"/>: its already-sorted
    /// (name Ordinal) properties flattened to "name=value\n…". Used only to break a (name,
    /// source_file) tie so the surface sort is a total order.
    /// </summary>
    private static string PropertyBagKey(Surface s)
    {
        var sb = new StringBuilder();
        foreach (var p in s.Properties)
        {
            sb.Append(p.Name).Append('=').Append(p.Value).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render a parsed KV3 <see cref="Value"/> as a verbatim string. Strings/numbers/bools render
    /// to their lexical form; a nested array/map renders to a stable KV3-text-ish rendering so no
    /// source data is dropped. Deterministic: map keys Ordinal-sorted, array order preserved.
    /// </summary>
    internal static string RenderScalar(Value value)
    {
        switch (value.KindCase)
        {
            case Value.KindOneofCase.StringValue:
                return value.StringValue;
            case Value.KindOneofCase.BoolValue:
                return value.BoolValue ? "true" : "false";
            case Value.KindOneofCase.NullValue:
                return "null";
            case Value.KindOneofCase.NumberValue:
                return RenderNumber(value.NumberValue);
            case Value.KindOneofCase.ListValue:
                {
                    var sb = new StringBuilder("[");
                    bool first = true;
                    foreach (var v in value.ListValue.Values)
                    {
                        if (!first)
                            sb.Append(", ");
                        sb.Append(RenderScalar(v));
                        first = false;
                    }
                    sb.Append(']');
                    return sb.ToString();
                }
            case Value.KindOneofCase.StructValue:
                {
                    var sb = new StringBuilder("{");
                    bool first = true;
                    foreach (var kv in new SortedDictionary<string, Value>(
                                 value.StructValue.Fields, StringComparer.Ordinal))
                    {
                        if (!first)
                            sb.Append(", ");
                        sb.Append(kv.Key).Append(" = ").Append(RenderScalar(kv.Value));
                        first = false;
                    }
                    sb.Append('}');
                    return sb.ToString();
                }
            default:
                return "";
        }
    }

    // Render a KV3 number without lossy scientific notation, integers without a trailing ".0".
    // (KV3 ints + floats both arrive as proto double via Kv3; "12" must not become "12.0".)
    private static string RenderNumber(double d)
    {
        if (d == Math.Floor(d) && !double.IsInfinity(d)
            && d >= long.MinValue && d <= long.MaxValue)
        {
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        }
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    // ---- Atomic write + canonical proto3 JSON ----------------------------------------

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
