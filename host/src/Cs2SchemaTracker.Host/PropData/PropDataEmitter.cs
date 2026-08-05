// Breakable-prop + collision-group extraction (prop_data.json).
//
// Pipeline: open a content-depot pak01_dir.vpk (VpkArchive) -> read TWO source files
// (scripts/propdata.txt KV1 + scripts/collision_properties.txt KV3 text), each CRC-verified by
// the VPK layer -> map propdata.txt's prop classes + the BreakableModels block + collision
// groups into the public PropData message -> serialize canonical proto3 JSON -> atomic .tmp+rename.
//
// === propdata.txt (KV1; CONFIRMED 2026-06-19) ===
//   "PropData.txt" { "<Class>" { "base" "..." "health" "..." ... }  ...
//                    "BreakableModels" { "<Group>" { "<model.vmdl>" "1" ... } ... } }
// Every top-level child EXCEPT "BreakableModels" is a prop class (flat name->value scalars).
//
// === collision_properties.txt (KV3 text; CONFIRMED 2026-06-19) ===
//   { collision_properties = [ { name = "..." description = "..." collision_group = "..."
//                                interact_as = [...] interact_with = [...] interact_exclude = [...] } ] }
//
// === v1 mapping decisions (mirror item_definitions) ===
//   - Structured messages, NOT a raw KV tree. Prop classes carry a sorted name->value bag.
//   - `base` references are VERBATIM, not resolved (consumers walk the chain themselves).
//   - Both sources are OPTIONAL individually; the artifact requires at least one of
//     {prop_classes, collision_groups} non-empty.
//
// Invariants:
//   Determinism: prop_classes by id Ordinal; each class's properties by name Ordinal;
//     breakable_models by id Ordinal, models de-duped then Ordinal; collision_groups by name
//     Ordinal, interact lists in source order. Canonical JSON, LF, UTF-8.
//   Fail-loud: missing vpk / both source files absent / malformed KV1 or KV3 / a top-level that is
//     not a block / a collision_properties that is not an array / zero rows across both sources —
//     all throw BEFORE any output bytes. No catch-and-continue.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and atomically
//     rename.

using System.Text;

using Cs2SchemaTracker.Host.EntitySchema;   // Kv3 / Kv3ParseException
using Cs2SchemaTracker.Host.GameEvents;     // Kv1 / Kv1Node
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Cs2SchemaTracker.Host.PropData;

/// <summary>
/// Extracts <c>scripts/propdata.txt</c> (KV1) + <c>scripts/collision_properties.txt</c> (KV3
/// text) from a content-depot VPK and writes the canonical prop_data.json. Host-only identity fields
/// are stamped by the constructor; payload comes verbatim from the parsed KV.
/// </summary>
internal sealed class PropDataEmitter
{
    public const string PropDataPath = "scripts/propdata.txt";
    public const string CollisionPath = "scripts/collision_properties.txt";

    private const string BreakableModelsKey = "BreakableModels";
    private const string CollisionListKey = "collision_properties";

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public PropDataEmitter(string schemaVersion, string buildId, string platform)
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
    /// True iff <paramref name="archive"/> ships at least one of the two prop-data sources
    /// (<c>scripts/propdata.txt</c> or <c>scripts/collision_properties.txt</c>) in its directory
    /// tree. Distinguishes a GENUINE absence (neither shipped this era ⇒ graceful omission) from a
    /// present-but-unreadable source (a missing backing chunk, which <see cref="Emit"/> still fails
    /// loud on). Directory-tree check only.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.Find(PropDataPath) is not null || archive.Find(CollisionPath) is not null;
    }

    public void Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = new Schemas.PropData
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        var propEntry = archive.Find(PropDataPath);
        var collisionEntry = archive.Find(CollisionPath);

        if (propEntry is null && collisionEntry is null)
        {
            throw new InvalidDataException(
                $"PropDataEmitter: neither '{PropDataPath}' nor '{CollisionPath}' is present in the "
                + "VPK — refusing to write prop_data.json. Was the correct content "
                + "pak01_dir.vpk supplied?");
        }

        if (propEntry is not null)
        {
            byte[] bytes = archive.ReadEntryBytes(propEntry); // CRC-verified.
            string text = Encoding.UTF8.GetString(bytes);
            MapPropData(text, document);
        }

        if (collisionEntry is not null)
        {
            byte[] bytes = archive.ReadEntryBytes(collisionEntry); // CRC-verified.
            string text = Encoding.UTF8.GetString(bytes);
            MapCollisionGroups(text, document);
        }

        if (document.PropClasses.Count == 0 && document.CollisionGroups.Count == 0)
        {
            throw new InvalidDataException(
                "PropDataEmitter: parsed zero prop classes AND zero collision groups — refusing to "
                + "write an empty prop_data.json.");
        }

        string json = SerializeCanonical(document);
        AtomicWrite(outputPath, json);
    }

    // ---- propdata.txt (KV1) ----------------------------------------------------------

    private static void MapPropData(string text, Schemas.PropData document)
    {
        IReadOnlyList<Kv1Node> roots = Kv1.Parse(text, PropDataPath);
        Kv1Node wrapper = LocateWrapperBlock(roots);

        var classes = new List<PropClass>();
        var groups = new List<BreakableModelGroup>();

        foreach (var child in wrapper.Children!)
        {
            if (!child.IsBlock)
            {
                throw new InvalidDataException(
                    $"PropDataEmitter: '{PropDataPath}' top-level entry '{child.Key}' is a scalar, "
                    + "expected a block.");
            }

            if (string.Equals(child.Key, BreakableModelsKey, StringComparison.Ordinal))
            {
                foreach (var g in MapBreakableModels(child))
                {
                    groups.Add(g);
                }
                continue;
            }

            // A prop class: a flat name->value block. Last-occurrence-wins per scalar key.
            var byName = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in child.Children!)
            {
                byName[prop.Key] = prop.IsBlock ? "" : (prop.Value ?? "");
            }
            var cls = new PropClass { Id = child.Key };
            foreach (var (name, value) in byName)
            {
                cls.Properties.Add(new PropProperty { Name = name, Value = value });
            }
            classes.Add(cls);
        }

        classes.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        groups.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        // Fail-loud on duplicate class ids — would silently collide two definitions.
        DetectDuplicateIds(classes.Select(c => c.Id), "prop class");
        DetectDuplicateIds(groups.Select(g => g.Id), "breakable-model group");
        document.PropClasses.AddRange(classes);
        document.BreakableModels.AddRange(groups);
    }

    private static IEnumerable<BreakableModelGroup> MapBreakableModels(Kv1Node breakableModels)
    {
        foreach (var group in breakableModels.Children!)
        {
            if (!group.IsBlock)
            {
                throw new InvalidDataException(
                    $"PropDataEmitter: '{BreakableModelsKey}' entry '{group.Key}' is a scalar, "
                    + "expected a block.");
            }
            // The model paths are the KEYS of the group block (the value is a count). De-dup + sort.
            var models = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var model in group.Children!)
            {
                models.Add(model.Key);
            }
            var g = new BreakableModelGroup { Id = group.Key };
            foreach (var m in models)
            {
                g.Models.Add(m);
            }
            yield return g;
        }
    }

    private static Kv1Node LocateWrapperBlock(IReadOnlyList<Kv1Node> roots)
    {
        foreach (var root in roots)
        {
            if (root.IsBlock)
            {
                return root;
            }
        }
        throw new InvalidDataException(
            $"PropDataEmitter: '{PropDataPath}' has no top-level block — expected the PropData "
            + "wrapper.");
    }

    // ---- collision_properties.txt (KV3 text) -----------------------------------------

    private static void MapCollisionGroups(string text, Schemas.PropData document)
    {
        Value root = ParseKv3(text, CollisionPath);
        if (root.KindCase != Value.KindOneofCase.StructValue)
        {
            throw new InvalidDataException(
                $"PropDataEmitter: '{CollisionPath}' top-level value is not a KV3 map.");
        }
        if (!root.StructValue.Fields.TryGetValue(CollisionListKey, out var listVal))
        {
            throw new InvalidDataException(
                $"PropDataEmitter: '{CollisionPath}' has no '{CollisionListKey}' array.");
        }
        if (listVal.KindCase != Value.KindOneofCase.ListValue)
        {
            throw new InvalidDataException(
                $"PropDataEmitter: '{CollisionPath}' '{CollisionListKey}' is not an array.");
        }

        var groups = new List<CollisionGroup>();
        foreach (var element in listVal.ListValue.Values)
        {
            if (element.KindCase != Value.KindOneofCase.StructValue)
            {
                throw new InvalidDataException(
                    $"PropDataEmitter: '{CollisionPath}' '{CollisionListKey}' entry is not a map.");
            }
            var fields = element.StructValue.Fields;
            var g = new CollisionGroup
            {
                Name = StringField(fields, "name"),
                Description = StringField(fields, "description"),
                CollisionGroup_ = StringField(fields, "collision_group"),
            };
            g.InteractAs.AddRange(StringList(fields, "interact_as"));
            g.InteractWith.AddRange(StringList(fields, "interact_with"));
            g.InteractExclude.AddRange(StringList(fields, "interact_exclude"));
            groups.Add(g);
        }

        groups.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        document.CollisionGroups.AddRange(groups);
    }

    private static string StringField(Google.Protobuf.Collections.MapField<string, Value> fields, string key)
        => fields.TryGetValue(key, out var v) && v.KindCase == Value.KindOneofCase.StringValue
            ? v.StringValue
            : "";

    private static IEnumerable<string> StringList(Google.Protobuf.Collections.MapField<string, Value> fields, string key)
    {
        if (!fields.TryGetValue(key, out var v) || v.KindCase != Value.KindOneofCase.ListValue)
        {
            yield break;
        }
        foreach (var item in v.ListValue.Values)
        {
            // Each interact entry is a string; render verbatim (source order preserved).
            yield return SurfaceProperties.SurfacePropertiesEmitter.RenderScalar(item);
        }
    }

    private static Value ParseKv3(string text, string sourceName)
    {
        try
        {
            return Kv3.Parse(text);
        }
        catch (Kv3ParseException ex)
        {
            throw new InvalidDataException(
                $"PropDataEmitter: '{sourceName}' is not valid KV3 text: {ex.Message}.");
        }
    }

    // ---- shared helpers --------------------------------------------------------------

    private static void DetectDuplicateIds(IEnumerable<string> ids, string what)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!seen.Add(id))
            {
                throw new InvalidDataException(
                    $"PropDataEmitter: duplicate {what} id '{id}' in '{PropDataPath}'.");
            }
        }
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
