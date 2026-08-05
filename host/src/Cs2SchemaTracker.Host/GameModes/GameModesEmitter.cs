// Game-mode / game-type extraction (game_modes.json).
//
// Pipeline (clones ItemDefinitionsEmitter): open a content-depot pak01_dir.vpk (VpkArchive) -> find
// the loose "gamemodes.txt" entry (a top-level KV1 file inside pak01, NOT packed under a sub-path)
// -> extract its bytes (CRC-verified by the VPK layer) -> parse the KV1 text (Kv1) -> map the
// gameTypes / mapgroups sub-sections into the public GameModes message (schemas/game_modes.proto)
// -> serialize the canonical proto3 JSON -> atomic .tmp+rename.
//
// === gamemodes.txt KV1 shape (the subset we mirror) ===
// A single top-level wrapper block (conventionally "GameModes++" / "GameModes_Server.txt";
// located STRUCTURALLY — the first/only top-level block — matching the
// ItemDefinitionsEmitter precedent). Its children include:
//
//   "GameModes++"
//   {
//     "gameTypes"
//     {
//       "classic" { "index" "0" "gameModes" { "casual" { ... } "competitive" { ... } } }
//       ...
//     }
//     "mapgroups"
//     {
//       "mg_active" { "displayname" "Active" "maps" { "de_ancient" "" ... } }
//       ...
//     }
//   }
//
// === v1 mapping decisions (mirror item_definitions) ===
//   - Structured messages, NOT a generic KV1 mirror. Only the join tables are surfaced.
//   - RAW values: convar overrides + mapgroup references VERBATIM; no cfg exec resolution.
//   - Absent scalar keys ⇒ proto3-default "" / 0 (no sentinel strings).
//   - Only `gameTypes` is REQUIRED; `mapgroups` is optional (absent ⇒ empty repeated).
//
// Invariants:
//   Determinism: game_types / map_groups by id Ordinal; game_modes within a type by id Ordinal;
//     convars by name Ordinal; mapgroupsMP + maps de-duped then Ordinal. Canonical JSON, LF, UTF-8
//     no BOM.
//   Fail-loud: missing vpk / missing "gamemodes.txt" entry / CRC mismatch / malformed KV1 /
//     top-level not a block / `gameTypes` missing or scalar / a gametype/gamemode entry that is a
//     scalar / a duplicate id / a non-integer maxplayers / zero game types — all throw BEFORE any
//     output bytes. No catch-and-continue.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and atomically
//     rename.

using System.Globalization;

using Cs2SchemaTracker.Host.GameEvents;   // Kv1 / Kv1Node (shared minimal KV1 parser)
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.GameModes;

/// <summary>
/// Extracts <c>gamemodes.txt</c> KV1 content from a content-depot VPK and writes the
/// canonical game_modes.json. Host-only identity fields (schema_version, build_id, platform) are
/// stamped by the constructor; the definitions come verbatim from the parsed KV1.
/// </summary>
internal sealed class GameModesEmitter
{
    /// <summary>The depot-relative path of the game-mode KV1 inside pak01 (a loose top-level file).</summary>
    public const string GameModesPath = "gamemodes.txt";

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public GameModesEmitter(string schemaVersion, string buildId, string platform)
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
    /// <c>gamemodes.txt</c>, and write game_modes.json to <paramref name="outputPath"/>.
    /// Fail-loud: throws before any output bytes if the VPK is missing, the entry is absent, or the
    /// KV1 is malformed.
    /// </summary>
    public void EmitFromVpk(string vpkDirPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkDirPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var archive = VpkArchive.Open(vpkDirPath);
        Emit(archive, outputPath);
    }

    /// <summary>
    /// True iff <paramref name="archive"/> ships the loose <c>gamemodes.txt</c> entry in its
    /// directory tree. Distinguishes a GENUINE absence (not shipped this era ⇒ graceful omission)
    /// from a present-but-unreadable source (a missing backing chunk, which <see cref="Emit"/> still
    /// fails loud on). Directory-tree check only.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        return archive.Find(GameModesPath) is not null;
    }

    /// <summary>
    /// Map <c>gamemodes.txt</c> in <paramref name="archive"/> into the public
    /// <see cref="Schemas.GameModes"/> message and write the canonical game_modes.json.
    /// All validation + the full document build happen before any disk write.
    /// </summary>
    public void Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var entry = archive.Find(GameModesPath)
            ?? throw new InvalidDataException(
                $"GameModesEmitter: '{GameModesPath}' not found in the VPK — refusing to write "
                + "game_modes.json. Was the correct content pak01_dir.vpk supplied?");

        byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified by the VPK layer; throws on mismatch.
        string text = System.Text.Encoding.UTF8.GetString(bytes);

        IReadOnlyList<Kv1Node> roots = Kv1.Parse(text, GameModesPath);
        Kv1Node wrapper = LocateWrapperBlock(roots);

        var document = new Schemas.GameModes
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // `gameTypes` is REQUIRED. `mapgroups` is optional ⇒ empty repeated.
        var gameTypesBlock = RequireBlockSection(wrapper, "gameTypes");
        foreach (var gt in MapGameTypes(gameTypesBlock))
        {
            document.GameTypes.Add(gt);
        }
        if (document.GameTypes.Count == 0)
        {
            throw new InvalidDataException(
                "GameModesEmitter: parsed zero game types from the 'gameTypes' table — "
                + "refusing to write an empty game_modes.json.");
        }

        foreach (var mg in MapMapGroups(OptionalBlockSection(wrapper, "mapgroups")))
        {
            document.MapGroups.Add(mg);
        }

        string json = SerializeCanonical(document);
        AtomicWrite(outputPath, json);
    }

    // ---- Wrapper + section location (clones ItemDefinitionsEmitter) -------------------

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
            $"GameModesEmitter: '{GameModesPath}' has no top-level block — expected the "
            + "GameModes wrapper.");
    }

    private static Kv1Node RequireBlockSection(Kv1Node wrapper, string name)
    {
        var section = MergeSiblingBlocks(wrapper, name);
        if (section is null)
        {
            throw new InvalidDataException(
                $"GameModesEmitter: required section '{name}' is missing from '{GameModesPath}' "
                + ".");
        }
        return section;
    }

    private static Kv1Node? OptionalBlockSection(Kv1Node wrapper, string name)
        => MergeSiblingBlocks(wrapper, name);   // null when absent ⇒ empty repeated field.

    // gamemodes.txt may repeat a section name at the top level; mirror the items_game.txt
    // union semantics (concatenate the children of every sibling block keyed `name`, in source
    // order) rather than last-occurrence-wins. Fail-loud if any sibling keyed `name` is a scalar
    // rather than a block.
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
                    $"GameModesEmitter: section '{name}' in '{GameModesPath}' is a scalar, "
                    + "expected a block.");
            }
            merged ??= new List<Kv1Node>();
            merged.AddRange(child.Children!);
        }

        return merged is null ? null : new Kv1Node { Key = name, Children = merged };
    }

    // ---- Scalar lookup (last-occurrence-wins) ----------------------------------------

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

    // Last block child keyed `key`, or null when absent / only-scalar.
    private static Kv1Node? ChildBlock(Kv1Node block, string key)
    {
        Kv1Node? found = null;
        foreach (var child in block.Children!)
        {
            if (string.Equals(child.Key, key, StringComparison.Ordinal) && child.IsBlock)
            {
                found = child;
            }
        }
        return found;
    }

    // ---- id-keyed table iteration (gameTypes / gameModes / mapgroups) ----------------

    // Each child must be a block whose KEY is the verbatim id. A scalar where a block is required is
    // a structural surprise ⇒ fail-loud. A DUPLICATE id is NOT corruption — Valve ships gamemodes.txt
    // with genuinely-repeated mapgroup ids (e.g. two "mg_de_basalt" blocks differing only in
    // authorID, build 20503857). The conventional KV1 semantics is LAST-OCCURRENCE-WINS (the engine
    // keeps the final definition); we mirror that (faithful to the game, and deterministic) rather
    // than killing the whole extract. Sorted by id Ordinal.
    private static List<Kv1Node> ReadIdBlocks(Kv1Node table, string tableName)
    {
        var byId = new Dictionary<string, Kv1Node>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var child in table.Children!)
        {
            if (!child.IsBlock)
            {
                throw new InvalidDataException(
                    $"GameModesEmitter: '{tableName}' entry '{child.Key}' is a scalar, expected "
                    + "a block.");
            }
            if (!byId.ContainsKey(child.Key))
            {
                order.Add(child.Key);
            }
            byId[child.Key] = child;   // last-occurrence-wins (KV1 override).
        }

        return order
            .Select(id => byId[id])
            .OrderBy(n => n.Key, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<GameType> MapGameTypes(Kv1Node table)
    {
        foreach (var node in ReadIdBlocks(table, "gameTypes"))
        {
            var gt = new GameType
            {
                Id = node.Key,
                Index = ParseOptionalInt32(node, "index", "gameTypes"),
            };
            var modes = ChildBlock(node, "gameModes");
            if (modes is not null)
            {
                foreach (var gm in MapGameModes(modes))
                {
                    gt.GameModes.Add(gm);
                }
            }
            yield return gt;
        }
    }

    private static IEnumerable<GameMode> MapGameModes(Kv1Node table)
    {
        foreach (var node in ReadIdBlocks(table, "gameModes"))
        {
            var gm = new GameMode
            {
                Id = node.Key,
                NameId = Scalar(node, "nameID"),
                DisplayName = Scalar(node, "displayName"),
                DescriptionId = Scalar(node, "descID"),
                MaxPlayers = ParseOptionalUint32(node, "maxplayers", "gameModes"),
                ExhibitGameType = Scalar(node, "exhibitGameType"),
                GameType = ParseOptionalInt32(node, "game_type", "gameModes"),
                GameMode_ = ParseOptionalInt32(node, "game_mode", "gameModes"),
                TypeFlags = ParseOptionalInt32(node, "typeflags", "gameModes"),
            };

            // mapgroupsMP: the referenced map-group names are the KEYS of the child block.
            var mgmp = ChildBlock(node, "mapgroupsMP");
            if (mgmp is not null)
            {
                foreach (var name in SortedDistinctKeys(mgmp))
                {
                    gm.MapGroupsMp.Add(name);
                }
            }

            // convars: name→value scalar overrides, sorted by name Ordinal.
            var convars = ChildBlock(node, "convars");
            if (convars is not null)
            {
                foreach (var ov in ReadConVarOverrides(convars))
                {
                    gm.Convars.Add(ov);
                }
            }

            yield return gm;
        }
    }

    private static IEnumerable<MapGroup> MapMapGroups(Kv1Node? table)
    {
        if (table is null)
        {
            yield break;
        }
        foreach (var node in ReadIdBlocks(table, "mapgroups"))
        {
            var mg = new MapGroup
            {
                Id = node.Key,
                DisplayName = Scalar(node, "displayname"),
            };
            var maps = ChildBlock(node, "maps");
            if (maps is not null)
            {
                foreach (var name in SortedDistinctKeys(maps))
                {
                    mg.Maps.Add(name);
                }
            }
            yield return mg;
        }
    }

    // The KEYS of a block, de-duplicated and Ordinal-sorted. Used for the mapgroupsMP-reference and
    // maps lists (the values are conventionally empty).
    private static SortedSet<string> SortedDistinctKeys(Kv1Node block)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var child in block.Children!)
        {
            set.Add(child.Key);
        }
        return set;
    }

    // The convars block, mapped to sorted-by-name overrides. A nested block under a convar
    // key is treated as having an empty value (we never coerce a block to a string); a
    // duplicate convar name is last-occurrence-wins (KV1 override).
    private static IEnumerable<ConVarOverride> ReadConVarOverrides(Kv1Node convars)
    {
        var byName = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in convars.Children!)
        {
            byName[child.Key] = child.IsBlock ? "" : (child.Value ?? "");
        }
        foreach (var kv in byName)
        {
            yield return new ConVarOverride { Name = kv.Key, Value = kv.Value };
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
            $"GameModesEmitter: '{tableName}' entry '{node.Key}' has non-integer '{key}' value "
            + $"'{raw}'.");
    }

    // An int32-valued scalar: "" / absent ⇒ proto3-default 0. Accepts an optional leading sign. A
    // present-but-non-integer value is fail-loud.
    private static int ParseOptionalInt32(Kv1Node node, string key, string tableName)
    {
        string raw = Scalar(node, key);
        if (raw.Length == 0)
        {
            return 0;
        }
        if (int.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        throw new InvalidDataException(
            $"GameModesEmitter: '{tableName}' entry '{node.Key}' has non-integer '{key}' value "
            + $"'{raw}'.");
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
