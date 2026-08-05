// Game-event extraction (gameevents.json).
//
// Pipeline: open a pak01_dir.vpk (VpkArchive) -> find every `.gameevents` entry -> extract its
// bytes (CRC-verified by the VPK layer) -> parse the KV1 text (Kv1) -> map each event block into a
// GameEvent -> serialize the canonical proto3 JSON gameevents.json (schemas/gameevents.proto).
//
// === `.gameevents` KV1 shape ===
// A `.gameevents` file is a single top-level block (conventionally keyed "GameEvents",
// though we do not require that name) whose children are event blocks:
//
//   "GameEvents"
//   {
//     "player_death"            // a comment about the event
//     {
//       "local"    "1"          // event-level property
//       "reliable" "1"
//       "userid"   "short"      // a field: name -> KV1 type label
//       "attacker" "short"      // a comment about the field
//     }
//   }
//
// Property vs field discrimination: the event-level KV1 keys that are NOT fields are a small fixed
// set Valve uses for transport metadata. We treat `local` and `reliable` as properties and
// EVERYTHING ELSE as a field. The properties map in gameevents.proto is string->string precisely so
// an unforeseen transport key Valve adds round-trips losslessly as a property rather than being
// misclassified — but to keep "properties (local, reliable)" precise and stable we only route the
// known transport keys into `properties`; any other scalar is a field (value = the KV1 type label).
//
// Invariants:
//   Determinism: events sorted by (source, name) Ordinal; fields and properties keep a deterministic
//     order (fields: source order — meaningful; properties: map, sorted by the proto3 JSON layer +
//     CanonicalJson). Canonical JSON, LF, UTF-8 no BOM.
//   Fail-loud: missing VPK, missing/empty `.gameevents` set, malformed KV1, or a structurally
//     invalid event (e.g. a scalar where an event block was required) throws BEFORE any output bytes
//     are written. No catch-and-continue.
//   All-or-nothing: write to a sibling .tmp then atomically rename.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.GameEvents;

/// <summary>
/// Extracts `.gameevents` KV1 content from a VPK and writes the canonical gameevents.json. Host-only
/// identity fields (schema_version, build_id, platform) are stamped by the constructor; the events
/// themselves come verbatim from the parsed KV1.
/// </summary>
internal sealed class GameEventsEmitter
{
    // The two event "properties (local, reliable)". Every other scalar key inside an event block is
    // a field whose value is the KV1 type label.
    private static readonly HashSet<string> PropertyKeys = new(StringComparer.Ordinal)
    {
        "local",
        "reliable",
    };

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public GameEventsEmitter(string schemaVersion, string buildId, string platform)
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
    /// <c>.gameevents</c> entry, and write gameevents.json to <paramref name="outputPath"/>.
    /// Fail-loud: throws before any output bytes if the VPK is missing, no <c>.gameevents</c>
    /// entries exist, or any KV1 is malformed.
    /// </summary>
    public void EmitFromVpk(string vpkDirPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkDirPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var archive = VpkArchive.Open(vpkDirPath);
        Emit(archive, outputPath);
    }

    /// <summary>
    /// True iff <paramref name="archive"/> ships at least one <c>.gameevents</c> entry in its
    /// directory tree. Distinguishes a GENUINE absence (none shipped this era ⇒ graceful omission)
    /// from a present-but-unreadable source (a missing backing chunk, which <see cref="Emit"/> still
    /// fails loud on). Directory-tree check only.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        foreach (var e in archive.Entries)
        {
            if (e.FullPath.EndsWith(".gameevents", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Map every <c>.gameevents</c> entry in <paramref name="archive"/> into the public
    /// <see cref="Schemas.GameEvents"/> message and write the canonical gameevents.json.
    /// All validation + the full document build happen before any disk write.
    /// </summary>
    public void Emit(VpkArchive archive, string outputPath)
        => Emit(new[] { archive ?? throw new ArgumentNullException(nameof(archive)) }, outputPath);

    /// <summary>
    /// Map every <c>.gameevents</c> entry across ALL <paramref name="archives"/> into the public
    /// <see cref="Schemas.GameEvents"/> message and write the canonical gameevents.json. Multiple
    /// archives let the engine core pak (<c>resource/core.gameevents</c>) be merged with the csgo pak
    /// (<c>game.gameevents</c> / <c>mod.gameevents</c>); the events are collected across archives and
    /// sorted by (source, name), so the output is deterministic regardless of archive order. Zero
    /// <c>.gameevents</c> across the WHOLE set fails loud (the wrong VPK was supplied).
    /// </summary>
    public void Emit(IReadOnlyList<VpkArchive> archives, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        // Find every `.gameevents` entry across all archives. Entries within an archive are already
        // Ordinal-sorted by FullPath (VpkArchive guarantees that); the final document is sorted by
        // (source, name) below, so cross-archive order does not affect output.
        var entries = new List<(VpkArchive Archive, VpkDirectoryEntry Entry)>();
        foreach (var archive in archives)
        {
            ArgumentNullException.ThrowIfNull(archive);
            foreach (var e in archive.Entries.Where(e => e.FullPath.EndsWith(".gameevents", StringComparison.Ordinal)))
            {
                entries.Add((archive, e));
            }
        }

        if (entries.Count == 0)
        {
            // A gameevents extract with zero `.gameevents` files is not a valid artifact (the wrong
            // VPK was supplied). Fail loud rather than emit an empty document that drops every event.
            throw new InvalidDataException(
                "GameEventsEmitter: no '.gameevents' entries found in the VPK — refusing to write an "
                + "empty gameevents.json. Was the correct pak01_dir.vpk supplied?");
        }

        var document = new Schemas.GameEvents
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // Collect every event across all source files, then sort by (source, name) Ordinal. We sort
        // the materialized list rather than relying on file/KV1 order.
        var events = new List<GameEvent>();
        foreach (var (archive, entry) in entries)
        {
            // basename, e.g. "core.gameevents" (source filename preserved verbatim).
            string source = entry.FullPath.Contains('/', StringComparison.Ordinal)
                ? entry.FullPath[(entry.FullPath.LastIndexOf('/') + 1)..]
                : entry.FullPath;

            byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified; throws on mismatch.
            string text = System.Text.Encoding.UTF8.GetString(bytes);

            IReadOnlyList<Kv1Node> roots = Kv1.Parse(text, source);
            foreach (var ev in ExtractEvents(roots, source))
            {
                events.Add(ev);
            }
        }

        foreach (var ev in events
                     .OrderBy(e => e.Source, StringComparer.Ordinal)
                     .ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            document.Events.Add(ev);
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

    // ---- KV1 tree -> GameEvent[] ------------------------------------------------------

    // The top-level node(s) are the wrapper block(s) (conventionally "GameEvents"). Each
    // CHILD of a wrapper that is itself a block is one event; the event's children are
    // properties (known transport keys) and fields (everything else).
    private static IEnumerable<GameEvent> ExtractEvents(IReadOnlyList<Kv1Node> roots, string source)
    {
        foreach (Kv1Node root in roots)
        {
            if (!root.IsBlock)
            {
                throw new InvalidDataException(
                    $"GameEventsEmitter: '{source}' top-level key '{root.Key}' is a scalar, expected a "
                    + "block of events.");
            }

            foreach (Kv1Node eventNode in root.Children!)
            {
                if (!eventNode.IsBlock)
                {
                    throw new InvalidDataException(
                        $"GameEventsEmitter: '{source}' event '{eventNode.Key}' is a scalar, expected an "
                        + "event block.");
                }

                yield return MapEvent(eventNode, source);
            }
        }
    }

    private static GameEvent MapEvent(Kv1Node eventNode, string source)
    {
        if (string.IsNullOrEmpty(eventNode.Key))
        {
            throw new InvalidDataException(
                $"GameEventsEmitter: '{source}' carries an event with an empty name.");
        }

        var ev = new GameEvent
        {
            Name = eventNode.Key,
            Source = source,
            Comment = eventNode.Comment,
        };

        foreach (Kv1Node child in eventNode.Children!)
        {
            if (child.IsBlock)
            {
                // CS2 `.gameevents` events are flat (scalar properties + scalar fields). A nested
                // block inside an event is unexpected structure — fail loud rather than drop it.
                throw new InvalidDataException(
                    $"GameEventsEmitter: '{source}' event '{eventNode.Key}' has a nested block under "
                    + $"'{child.Key}'; only scalar properties/fields are expected.");
            }

            string value = child.Value ?? "";
            if (PropertyKeys.Contains(child.Key))
            {
                // Map preserves the value verbatim. Duplicate property keys are a malformed
                // input (the map can't carry both) — fail loud.
                if (ev.Properties.ContainsKey(child.Key))
                {
                    throw new InvalidDataException(
                        $"GameEventsEmitter: '{source}' event '{eventNode.Key}' repeats property "
                        + $"'{child.Key}'.");
                }
                ev.Properties[child.Key] = value;
            }
            else
            {
                // A field: key=name, value=KV1 type label, plus any trailing comment.
                ev.Fields.Add(new GameEventField
                {
                    Name = child.Key,
                    Type = value,
                    Comment = child.Comment,
                });
            }
        }

        return ev;
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
