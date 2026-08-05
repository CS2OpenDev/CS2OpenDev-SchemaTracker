// Economy item-definition extraction (item_definitions.json).
//
// Pipeline (clones GameEventsEmitter): open a content-depot pak01_dir.vpk (VpkArchive) -> find the
// "scripts/items/items_game.txt" entry -> extract its bytes (CRC-verified by the VPK layer) ->
// parse the KV1 text (Kv1) -> map the named sub-sections into the public ItemDefinitions message
// (schemas/item_definitions.proto) -> serialize the canonical proto3 JSON -> atomic .tmp+rename.
//
// === items_game.txt KV1 shape ===
// A single top-level wrapper block (conventionally keyed "items_game", but we locate it
// STRUCTURALLY — the first/only top-level block — rather than hard-requiring that name,
// matching the GameEventsEmitter precedent). Its children are named tables:
//
//   "items_game"
//   {
//     "items"             { "0" { "name" "default" ... }  "1" { ... } ... }
//     "prefabs"           { "weapon_base" { ... } ... }
//     "paint_kits"        { "0" { ... } ... }
//     "sticker_kits"      { "0" { ... } ... }
//     "music_definitions" { "1" { ... } ... }
//     "rarities"          { "common" { ... } ... }
//     "qualities"         { "normal" { ... } ... }
//     ...                 // other tables intentionally out of scope for v1
//   }
//
// === v1 mapping decisions (lead-fixed; do not re-litigate) ===
//   - Structured messages, NOT a generic KV1 mirror. Only the join tables are surfaced.
//   - RAW item + prefab tables: `prefab` references are carried VERBATIM (space-separated,
//     NOT tokenized, NOT resolved). Consumers join items↔prefabs themselves.
//   - The literal "default" item is kept as a real row with is_default=true / def_index=0.
//   - No generic extra_attributes map.
//   - Only `items` is REQUIRED; every other table is optional (absent ⇒ empty repeated).
//   - Absent scalar keys ⇒ proto3-default "" (no sentinel strings).
//
// === Repeated section + duplicate-key KV semantics ===
// Kv1.Parse does NOT de-duplicate — it appends children in source order. items_game.txt
// SPLITS each economy table across many top-level blocks that all share one key (dozens of
// "items" blocks, dozens of "sticker_kits" blocks, etc. — the base weapons block, then one
// fragment per operation/tournament). For these repeated CONTAINER sections the emitter
// MERGES (unions) the children of every sibling block keyed `name` in source order — NOT
// last-occurrence-wins, which would silently keep only the final fragment. Within a single
// entry block this emitter applies LAST-OCCURRENCE-WINS for scalar keys (the conventional
// KV1 override semantics: a later "name" overrides an earlier one). A duplicate def_index
// across the MERGED set (two children resolving to the same numeric index) is a fail-loud condition
// — it would silently collide two distinct definitions.
//
// Invariants:
//   Determinism: items/paint_kits/sticker_kits/music_definitions sorted by def_index NUMERIC;
//     prefabs/rarities/qualities by id Ordinal. Canonical JSON, LF, UTF-8 no BOM.
//   Fail-loud: missing vpk / missing "scripts/items/items_game.txt" entry / CRC mismatch (raised by
//     the VPK layer) / malformed KV1 / top-level not a block / `items` missing or scalar / a
//     def-index-keyed child whose key is neither a non-negative integer nor the "default" sentinel /
//     a duplicate def_index / zero item definitions — all throw BEFORE any output bytes are written.
//     No catch-and-continue.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and atomically
//     rename.

using System.Globalization;

using Cs2SchemaTracker.Host.GameEvents;   // Kv1 / Kv1Node (shared minimal KV1 parser)
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Items;

/// <summary>
/// Extracts <c>scripts/items/items_game.txt</c> KV1 content from a content-depot VPK and writes
/// the canonical item_definitions.json. Host-only identity fields (schema_version, build_id,
/// platform) are stamped by the constructor; the definitions come verbatim from the
/// parsed KV1 (no inheritance resolution in v1).
/// </summary>
internal sealed class ItemDefinitionsEmitter
{
    /// <summary>The depot-relative path of the economy item-definition KV1 inside pak01.</summary>
    public const string ItemsGamePath = "scripts/items/items_game.txt";

    /// <summary>The literal item key that denotes the default/fallback item (def_index 0).</summary>
    private const string DefaultItemKey = "default";

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public ItemDefinitionsEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Open the <paramref name="vpkDirPath"/> (a <c>*_dir.vpk</c>), extract and parse
    /// <c>scripts/items/items_game.txt</c>, and write item_definitions.json to
    /// <paramref name="outputPath"/>. Fail-loud: throws before any output bytes if the VPK is
    /// missing, the entry is absent, or the KV1 is malformed.
    /// </summary>
    public void EmitFromVpk(string vpkDirPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkDirPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var archive = VpkArchive.Open(vpkDirPath);
        Emit(archive, outputPath);
    }

    /// <summary>
    /// True iff <paramref name="archive"/> ships the <c>scripts/items/items_game.txt</c> entry in
    /// its directory tree. Distinguishes a GENUINE absence (not shipped this era ⇒ graceful
    /// omission) from a present-but-unreadable source (a missing backing chunk, which
    /// <see cref="Emit"/> still fails loud on). Directory-tree check only.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.Find(ItemsGamePath) is not null;
    }

    /// <summary>
    /// Map <c>scripts/items/items_game.txt</c> in <paramref name="archive"/> into the public
    /// <see cref="Schemas.ItemDefinitions"/> message and write the canonical item_definitions.json.
    /// All validation + the full document build happen before any disk write.
    /// </summary>
    public void Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var entry = archive.Find(ItemsGamePath)
            ?? throw new InvalidDataException(
                $"ItemDefinitionsEmitter: '{ItemsGamePath}' not found in the VPK — refusing to write "
                + "item_definitions.json. Was the correct content pak01_dir.vpk supplied?");

        byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified by the VPK layer; throws on mismatch.
        string text = System.Text.Encoding.UTF8.GetString(bytes);

        IReadOnlyList<Kv1Node> roots = Kv1.Parse(text, ItemsGamePath);

        // Locate the top-level wrapper STRUCTURALLY: the (single) top-level block. We do not
        // hard-require the name "items_game" (mirrors GameEventsEmitter's structural locate).
        Kv1Node wrapper = LocateWrapperBlock(roots);

        var document = new Schemas.ItemDefinitions
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // `items` is REQUIRED. Every other table is optional ⇒ empty repeated.
        var itemsBlock = RequireBlockSection(wrapper, "items");
        foreach (var item in MapItems(itemsBlock))
        {
            document.Items.Add(item);
        }
        if (document.Items.Count == 0)
        {
            throw new InvalidDataException(
                "ItemDefinitionsEmitter: parsed zero item definitions from the 'items' table — "
                + "refusing to write an empty item_definitions.json.");
        }

        foreach (var prefab in MapPrefabs(OptionalBlockSection(wrapper, "prefabs")))
        {
            document.Prefabs.Add(prefab);
        }
        foreach (var pk in MapPaintKits(OptionalBlockSection(wrapper, "paint_kits")))
        {
            document.PaintKits.Add(pk);
        }
        foreach (var sk in MapStickerKits(OptionalBlockSection(wrapper, "sticker_kits")))
        {
            document.StickerKits.Add(sk);
        }
        foreach (var md in MapMusicDefinitions(OptionalBlockSection(wrapper, "music_definitions")))
        {
            document.MusicDefinitions.Add(md);
        }
        foreach (var r in MapRarities(OptionalBlockSection(wrapper, "rarities")))
        {
            document.Rarities.Add(r);
        }
        foreach (var q in MapQualities(OptionalBlockSection(wrapper, "qualities")))
        {
            document.Qualities.Add(q);
        }

        string json = SerializeCanonical(document);

        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var tmpPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, System.Text.Encoding.UTF8.GetBytes(json));
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

    // ---- Wrapper + section location ---------------------------------------------------

    private static Kv1Node LocateWrapperBlock(IReadOnlyList<Kv1Node> roots)
    {
        // Exactly one top-level block is expected (the items_game wrapper). The KV1 file may
        // carry only that one node; if it carries multiple top-level nodes we take the first
        // block and ignore non-block siblings (none are expected in items_game.txt).
        foreach (var root in roots)
        {
            if (root.IsBlock)
            {
                return root;
            }
        }

        throw new InvalidDataException(
            $"ItemDefinitionsEmitter: '{ItemsGamePath}' has no top-level block — expected the "
            + "items_game wrapper.");
    }

    private static Kv1Node RequireBlockSection(Kv1Node wrapper, string name)
    {
        var section = MergeSiblingBlocks(wrapper, name);
        if (section is null)
        {
            throw new InvalidDataException(
                $"ItemDefinitionsEmitter: required section '{name}' is missing from '{ItemsGamePath}' "
                + ".");
        }
        return section;
    }

    private static Kv1Node? OptionalBlockSection(Kv1Node wrapper, string name)
    {
        return MergeSiblingBlocks(wrapper, name);   // null when absent ⇒ empty repeated field.
    }

    // items_game.txt repeats each section name many times at the top level — the economy
    // tables are SPLIT across dozens of sibling blocks all keyed e.g. "items" (the base
    // weapons block, then one block per operation/tournament, etc.). A last-occurrence-wins pick
    // would silently keep only the FINAL fragment (the original bug: 14 of 2003 items). The correct
    // KV1 semantics for repeated CONTAINER keys is union/merge: concatenate the children of every
    // sibling block keyed `name`, in source order. Scalar last-occurrence-wins (the override
    // semantics) still applies WITHIN an entry via Scalar().
    //
    // Returns null when no sibling is named `name`. Fail-loud if any sibling keyed `name` is a scalar
    // rather than a block — that would be a structural surprise we refuse to paper over. The merged
    // node is synthetic; its trailing comment is irrelevant here.
    private static Kv1Node? MergeSiblingBlocks(Kv1Node wrapper, string name)
    {
        List<Kv1Node>? merged = null;
        foreach (var child in wrapper.Children!)
        {
            if (!string.Equals(child.Key, name, StringComparison.Ordinal))
            {
                continue;
            }
            if (!child.IsBlock)
            {
                throw new InvalidDataException(
                    $"ItemDefinitionsEmitter: section '{name}' in '{ItemsGamePath}' is a scalar, "
                    + "expected a block.");
            }
            merged ??= new List<Kv1Node>();
            merged.AddRange(child.Children!);
        }

        if (merged is null)
        {
            return null;
        }
        return new Kv1Node { Key = name, Children = merged };
    }

    // ---- Scalar lookup (last-occurrence-wins) ----------------------------------------

    // Return the scalar value for `key` inside `block`, or "" when absent / non-scalar.
    // Last occurrence wins (conventional KV1 override). A nested block under the key is
    // treated as absent for scalar purposes (we never coerce a block to a string).
    private static string Scalar(Kv1Node block, string key)
    {
        string value = "";
        foreach (var child in block.Children!)
        {
            if (string.Equals(child.Key, key, StringComparison.Ordinal) && !child.IsBlock)
            {
                value = child.Value ?? "";
            }
        }
        return value;
    }

    private static bool HasDirectScalar(Kv1Node block, string key)
    {
        foreach (var child in block.Children!)
        {
            if (string.Equals(child.Key, key, StringComparison.Ordinal) && !child.IsBlock)
            {
                return true;
            }
        }
        return false;
    }

    // ---- def_index-keyed tables (items / paint_kits / sticker_kits / music_definitions) -

    private readonly record struct DefIndexEntry(uint DefIndex, bool IsDefault, Kv1Node Node);

    // Iterate a def-index-keyed table: each child block's KEY is a non-negative integer def_index,
    // or the literal "default" sentinel (def_index 0, is_default=true). Scalars and other keys are
    // a fail-loud structural error. Duplicate def_index across siblings is fail-loud.
    private static List<DefIndexEntry> ReadDefIndexEntries(Kv1Node table, string tableName)
    {
        var seen = new HashSet<uint>();
        var entries = new List<DefIndexEntry>();
        foreach (var child in table.Children!)
        {
            if (!child.IsBlock)
            {
                throw new InvalidDataException(
                    $"ItemDefinitionsEmitter: '{tableName}' entry '{child.Key}' is a scalar, expected "
                    + "a block.");
            }

            uint defIndex;
            bool isDefault = false;
            if (string.Equals(child.Key, DefaultItemKey, StringComparison.Ordinal))
            {
                defIndex = 0;
                isDefault = true;
            }
            else if (uint.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out defIndex))
            {
                // non-negative integer def_index (NumberStyles.None rejects sign/whitespace).
            }
            else
            {
                throw new InvalidDataException(
                    $"ItemDefinitionsEmitter: '{tableName}' key '{child.Key}' is neither a non-negative "
                    + $"integer nor the '{DefaultItemKey}' sentinel.");
            }

            if (!seen.Add(defIndex))
            {
                throw new InvalidDataException(
                    $"ItemDefinitionsEmitter: '{tableName}' has a duplicate def_index {defIndex} "
                    + $"(key '{child.Key}').");
            }

            entries.Add(new DefIndexEntry(defIndex, isDefault, child));
        }

        entries.Sort(static (a, b) => a.DefIndex.CompareTo(b.DefIndex)); // numeric sort.
        return entries;
    }

    private static IEnumerable<ItemDefinition> MapItems(Kv1Node table)
    {
        foreach (var e in ReadDefIndexEntries(table, "items"))
        {
            yield return new ItemDefinition
            {
                DefIndex = e.DefIndex,
                Name = Scalar(e.Node, "name"),
                // classname only when present DIRECTLY on the item block.
                Classname = HasDirectScalar(e.Node, "item_class") ? Scalar(e.Node, "item_class") : "",
                NameToken = Scalar(e.Node, "item_name"),
                DescriptionToken = Scalar(e.Node, "item_description"),
                Prefab = Scalar(e.Node, "prefab"),            // VERBATIM, not tokenized/resolved.
                ItemTypeName = Scalar(e.Node, "item_type_name"),
                ItemSlot = Scalar(e.Node, "item_slot"),
                IsDefault = e.IsDefault,
            };
        }
    }

    private static IEnumerable<PaintKit> MapPaintKits(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var e in ReadDefIndexEntries(table, "paint_kits"))
        {
            yield return new PaintKit
            {
                DefIndex = e.DefIndex,
                Name = Scalar(e.Node, "name"),
                DescriptionTag = Scalar(e.Node, "description_tag"),
            };
        }
    }

    private static IEnumerable<StickerKit> MapStickerKits(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var e in ReadDefIndexEntries(table, "sticker_kits"))
        {
            yield return new StickerKit
            {
                DefIndex = e.DefIndex,
                Name = Scalar(e.Node, "name"),
                ItemName = Scalar(e.Node, "item_name"),
                DescriptionString = Scalar(e.Node, "description_string"),
            };
        }
    }

    private static IEnumerable<MusicDefinition> MapMusicDefinitions(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var e in ReadDefIndexEntries(table, "music_definitions"))
        {
            yield return new MusicDefinition
            {
                DefIndex = e.DefIndex,
                Name = Scalar(e.Node, "name"),
                LocName = Scalar(e.Node, "loc_name"),
            };
        }
    }

    // ---- id-keyed tables (prefabs / rarities / qualities) ----------------------------

    // Iterate an id-keyed table: each child block's KEY is the verbatim string id. Scalars are a
    // fail-loud structural error. Duplicate ids are fail-loud. Sorted by id Ordinal.
    private static List<Kv1Node> ReadIdEntries(Kv1Node table, string tableName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<Kv1Node>();
        foreach (var child in table.Children!)
        {
            if (!child.IsBlock)
            {
                throw new InvalidDataException(
                    $"ItemDefinitionsEmitter: '{tableName}' entry '{child.Key}' is a scalar, expected "
                    + "a block.");
            }
            if (!seen.Add(child.Key))
            {
                throw new InvalidDataException(
                    $"ItemDefinitionsEmitter: '{tableName}' has a duplicate id '{child.Key}'.");
            }
            entries.Add(child);
        }

        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key)); // Ordinal sort.
        return entries;
    }

    private static IEnumerable<Prefab> MapPrefabs(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var node in ReadIdEntries(table, "prefabs"))
        {
            yield return new Prefab
            {
                Id = node.Key,
                Prefab_ = Scalar(node, "prefab"),            // verbatim parent chain.
                Classname = Scalar(node, "item_class"),
                ItemSlot = Scalar(node, "item_slot"),
                NameToken = Scalar(node, "item_name"),
                ItemTypeName = Scalar(node, "item_type_name"),
            };
        }
    }

    private static IEnumerable<Rarity> MapRarities(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var node in ReadIdEntries(table, "rarities"))
        {
            yield return new Rarity
            {
                Id = node.Key,
                Value = ParseOptionalUint32(node, "value", "rarities"),
                LocKey = Scalar(node, "loc_key"),
                LocKeyWeapon = Scalar(node, "loc_key_weapon"),
            };
        }
    }

    private static IEnumerable<Quality> MapQualities(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var node in ReadIdEntries(table, "qualities"))
        {
            yield return new Quality
            {
                Id = node.Key,
                Value = ParseOptionalUint32(node, "value", "qualities"),
            };
        }
    }

    // A uint32-valued scalar: "" / absent ⇒ proto3-default 0. A present-but-non-integer value is a
    // fail-loud structural error — never a silently coerced 0.
    private static uint ParseOptionalUint32(Kv1Node node, string key, string tableName)
    {
        string raw = Scalar(node, key);
        if (raw.Length == 0)
        {
            return 0;
        }
        if (uint.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        throw new InvalidDataException(
            $"ItemDefinitionsEmitter: '{tableName}' entry '{node.Key}' has non-integer '{key}' value "
            + $"'{raw}'.");
    }

    // ---- Canonical proto3 JSON --------------------------------------------------------

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
