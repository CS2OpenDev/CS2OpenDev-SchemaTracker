// Build-to-build changelog diff engine + emitter (changelog.json).
//
// Diffs two committed (build, platform) artifact sets — a predecessor (--from) and the newer build
// this file is committed under (--to) — into the public BuildChangelog message
// (schemas/build_changelog.proto), then writes the canonical proto3-JSON changelog.json under the
// NEWER build's per-platform dir.
//
// It diffs FIVE binary-derived families, each read from the build's committed JSON via the
// generated proto3 message's JsonParser (the same pattern ExtractCommand.CountClasses uses — NOT
// ad-hoc System.Text.Json):
//   classes            entity_schema.json  classes[]   — field_count / parent
//   enums              entity_schema.json  enums[]     — member added/removed (member:<name> rows)
//   convars            convars.json        convars[]   — default / flags
//   commands           commands.json       commands[]  — flags
//   engine_constants   engine_constants.json constants[] — value
//
// PLUS an optional SIXTH content-derived family:
//   localization       localization.json   tokens[]    — englishValue / valuesHash (a hash of the
//                                                         per-language values map)
// localization.json is BUILD-ON-DEMAND (produced every dump but NOT committed — ~199 MB/set), so it
// is NOT read from the committed set dir like the five binary families. Instead the caller supplies
// two localization.json source PATHS explicitly (fromLocalizationPath / toLocalizationPath): the `to`
// side is the fresh staged localization.json, the `from` side is the predecessor's localization
// REGENERATED on demand from its content (both discarded/absent after the diff). The localization
// family is emitted as families[5] ONLY when BOTH paths are supplied; otherwise the changelog stays
// the five binary families. Because localization is content-derived, a changelog that carries it is
// NOT content-depot-exempt — but the five binary families always are, so a content-less build still
// gets its 5-family changelog.
//
// CHANGELOG ENTITY KEY (the "name" of every added[]/removed[]/changed[].name row)
// --------------------------------------------------------------------------------
// A bare `name` is NOT unique within several families: the same class/enum name legitimately
// appears once per module (e.g. CEntityIdentity in client.dll, engine2.dll, server.dll), and the
// same engine-constant name appears once per source pool. Keying on `name` alone fail-louded on
// real data ("duplicate record name 'CEntityIdentity'"). So each family is matched across the two
// builds by a COMPOSITE, family-specific QUALIFIED KEY, emitted as a single qualified STRING in
// the existing proto fields (added/removed are repeated string; EntryChange.name is string — the
// proto is UNCHANGED). A consumer parses these qualified strings per family as follows:
//   classes           "<module>/<name>"   e.g. "client.dll/CEntityIdentity"
//   enums             "<module>/<name>"   e.g. "animationsystem.dll/AnimPoseControl_t"
//   engine_constants  "<source>/<name>"   e.g. "schema_enum:animationsystem.dll/PulseBestOutflowRules_t/PULSE_REL_..."
//   convars           "<name>"            (name is already unique)
//   commands          "<name>"            (name is already unique)
// The per-field "changed" detection is unchanged (module no longer appears as a class FieldChange
// because it is now part of the matching key, so it cannot vary within a matched pair).
//
// EMPTY-FAMILY DECISION: ALWAYS emit all five binary FamilyDelta entries, in the fixed declared
// order classes, enums, convars, commands, engine_constants — even when a family has no add/remove/
// change. The shape is then stable across every build (a consumer can index families[i] by the
// declared order), which matters more than terseness for a committed artifact. The optional
// localization family, when present, is ALWAYS appended as families[5] (the 6th slot) so the index
// of the five binary families never shifts.
//
// Invariants:
//   Determinism: added/removed qualified-key lists Ordinal-sorted; EntryChange.name Ordinal-sorted;
//     FieldChange rows Ordinal-sorted by `field`; families in the fixed declared order; all scalar
//     rendering is culture-invariant. Re-running diff over the same two sets is byte-identical.
//   Fail-loud: a missing (build,platform) set dir, or a required family source file that is absent
//     and NOT accounted-for in that build's omissions.json, throws BEFORE any bytes are written. A
//     malformed source JSON throws (strict parser). No catch-and-continue.
//   All-or-nothing: the full BuildChangelog is built in memory, then atomically written (sibling
//     .tmp -> rename) via AtomicWrite.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Changelog;

/// <summary>
/// Diffs two committed (build, platform) artifact sets into a <see cref="BuildChangelog"/> and
/// writes the canonical changelog.json. See file header. Pure of git; reads only the two committed
/// sets and writes one file.
/// </summary>
public sealed class BuildChangelogEmitter
{
    // Strict: a foreign / malformed source artifact must fail loud, not be silently treated as empty
    // (the defect of the old python script this replaces).
    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// The five ALWAYS-EMITTED binary-derived family names, in the FIXED declared emit order. The
    /// optional content-derived <see cref="LocalizationFamily"/> is appended after these as
    /// families[5] when both localization source paths are supplied.
    /// </summary>
    public static readonly IReadOnlyList<string> FamilyOrder = new[]
    {
        "classes", "enums", "convars", "commands", "engine_constants",
    };

    /// <summary>The optional 6th (content-derived) family name — appended as families[5] when emitted.</summary>
    public const string LocalizationFamily = "localization";

    private readonly string _schemaVersion;
    private readonly string _platform;
    private readonly string _fromBuild;
    private readonly string _toBuild;

    /// <param name="schemaVersion">schemas/ family version (SchemaFamily.Version).</param>
    /// <param name="platform">Canonical platform name.</param>
    /// <param name="fromBuild">Predecessor (baseline) build id.</param>
    /// <param name="toBuild">Newer build id — the build this changelog is committed under.</param>
    public BuildChangelogEmitter(string schemaVersion, string platform, string fromBuild, string toBuild)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        ArgumentException.ThrowIfNullOrEmpty(fromBuild);
        ArgumentException.ThrowIfNullOrEmpty(toBuild);
        _schemaVersion = schemaVersion;
        _platform = platform;
        _fromBuild = fromBuild;
        _toBuild = toBuild;
    }

    /// <summary>
    /// Build the <see cref="BuildChangelog"/> from the two committed set dirs and write changelog.json
    /// to <paramref name="outputPath"/>. Fail-loud BEFORE any byte hits disk.
    /// </summary>
    /// <param name="fromSetDir">The predecessor (build,platform) dir (must exist + be complete).</param>
    /// <param name="toSetDir">The newer (build,platform) dir (must exist + be complete).</param>
    /// <param name="outputPath">Where changelog.json is written (typically inside <paramref name="toSetDir"/>).</param>
    /// <param name="fromLocalizationPath">
    /// Predecessor localization.json path (regenerated on demand). When BOTH this and
    /// <paramref name="toLocalizationPath"/> are supplied, the 6th localization family is appended.
    /// </param>
    /// <param name="toLocalizationPath">Newer-build localization.json path (the fresh staged file).</param>
    public void Emit(
        string fromSetDir, string toSetDir, string outputPath,
        string? fromLocalizationPath = null, string? toLocalizationPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromSetDir);
        ArgumentException.ThrowIfNullOrEmpty(toSetDir);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = Build(fromSetDir, toSetDir, fromLocalizationPath, toLocalizationPath);
        AtomicWrite.WriteCanonical(document, outputPath);
    }

    /// <summary>
    /// Build the full <see cref="BuildChangelog"/> in memory (no I/O to the output). Exposed for
    /// tests + callers that want the message without writing it. When both localization paths are
    /// supplied, the 6th <c>localization</c> family is appended (see <see cref="LocalizationFamily"/>).
    /// </summary>
    public BuildChangelog Build(
        string fromSetDir, string toSetDir,
        string? fromLocalizationPath = null, string? toLocalizationPath = null)
    {
        IReadOnlyDictionary<string, LocRow>? fromRows = null;
        IReadOnlyDictionary<string, LocRow>? toRows = null;
        if (!string.IsNullOrEmpty(fromLocalizationPath) && !string.IsNullOrEmpty(toLocalizationPath))
        {
            // Loaded one side at a time: LoadLocalizationRows releases each side's full parsed graph
            // before it returns the compact map, so the peak footprint is one parsed side, not two.
            fromRows = LoadLocalizationRows(fromLocalizationPath);
            toRows = LoadLocalizationRows(toLocalizationPath);
        }
        return BuildFromRows(fromSetDir, toSetDir, fromRows, toRows);
    }

    /// <summary>
    /// Build the full <see cref="BuildChangelog"/> from the two committed set dirs (five binary
    /// families) plus PRE-LOADED localization row maps for the optional 6th family. Lets a caller
    /// that already holds the compact token→row maps (e.g. a corpus backfill reusing each build's map
    /// as its successor's `from` side) avoid re-parsing the ~150 MB localization.json. When either
    /// map is null the changelog stays the five binary families. Same output as
    /// <see cref="Build(string,string,string,string)"/>.
    /// </summary>
    internal BuildChangelog BuildFromRows(
        string fromSetDir, string toSetDir,
        IReadOnlyDictionary<string, LocRow>? fromRows, IReadOnlyDictionary<string, LocRow>? toRows)
    {
        ArgumentException.ThrowIfNullOrEmpty(fromSetDir);
        ArgumentException.ThrowIfNullOrEmpty(toSetDir);

        if (!Directory.Exists(fromSetDir))
        {
            throw new DirectoryNotFoundException(
                $"BuildChangelogEmitter: predecessor set dir not found: '{fromSetDir}'.");
        }
        if (!Directory.Exists(toSetDir))
        {
            throw new DirectoryNotFoundException(
                $"BuildChangelogEmitter: target set dir not found: '{toSetDir}'.");
        }

        var doc = new BuildChangelog
        {
            SchemaVersion = _schemaVersion,
            Platform = _platform,
            FromBuild = _fromBuild,
            ToBuild = _toBuild,
        };

        // entity_schema.json carries BOTH the classes and enums families; load each side once.
        var fromSchema = LoadRequired<Schemas.EntitySchema>(fromSetDir, "entity_schema.json");
        var toSchema = LoadRequired<Schemas.EntitySchema>(toSetDir, "entity_schema.json");
        var fromConvars = LoadRequired<Schemas.ConVars>(fromSetDir, "convars.json");
        var toConvars = LoadRequired<Schemas.ConVars>(toSetDir, "convars.json");
        var fromCommands = LoadRequired<Schemas.Commands>(fromSetDir, "commands.json");
        var toCommands = LoadRequired<Schemas.Commands>(toSetDir, "commands.json");
        var fromConsts = LoadRequired<Schemas.EngineConstants>(fromSetDir, "engine_constants.json");
        var toConsts = LoadRequired<Schemas.EngineConstants>(toSetDir, "engine_constants.json");

        // FIXED declared order — see FamilyOrder / the empty-family decision in the header.
        doc.Families.Add(DiffClasses(fromSchema, toSchema));
        doc.Families.Add(DiffEnums(fromSchema, toSchema));
        doc.Families.Add(DiffConvars(fromConvars, toConvars));
        doc.Families.Add(DiffCommands(fromCommands, toCommands));
        doc.Families.Add(DiffEngineConstants(fromConsts, toConsts));

        // Optional 6th content-derived family — appended AFTER the five binary families so their
        // index never shifts. Emitted only when BOTH localization row maps are supplied (both builds
        // produced the build-on-demand localization.json); otherwise the changelog stays the five
        // binary families.
        if (fromRows is not null && toRows is not null)
        {
            doc.Families.Add(DiffLocalizationRows(fromRows, toRows));
        }
        return doc;
    }

    // --- per-family diffs ------------------------------------------------------------------

    private static FamilyDelta DiffClasses(Schemas.EntitySchema from, Schemas.EntitySchema to)
    {
        // Qualified key "<module>/<name>": a class name is reused per module (CEntityIdentity in
        // client.dll/engine2.dll/server.dll), so (module,name) is the unique identity.
        var fromByName = ByName(from.Classes, c => Qualify(c.Module, c.Name));
        var toByName = ByName(to.Classes, c => Qualify(c.Module, c.Name));
        return BuildDelta("classes", fromByName, toByName, (oldC, newC) =>
        {
            var fields = new List<FieldChange>();
            // field_count: the SchemaClass carries fields[] (no scalar count) — the tracked
            // change is the number of fields. parent: the FIRST parent's name (Valve classes are
            // single-inheritance in practice; render the joined parent name list to be robust).
            // module is NOT a FieldChange here — it is part of the matching key, so it cannot
            // differ within a matched pair.
            AddIfChanged(fields, "field_count",
                oldC.Fields.Count.ToString(CultureInfo.InvariantCulture),
                newC.Fields.Count.ToString(CultureInfo.InvariantCulture));
            AddIfChanged(fields, "parent", JoinParents(oldC), JoinParents(newC));
            return fields;
        });
    }

    private static FamilyDelta DiffEnums(Schemas.EntitySchema from, Schemas.EntitySchema to)
    {
        // Qualified key "<module>/<name>": an enum name is reused per module, so (module,name) is
        // the unique identity (matches the convention engine_constants `source` already uses).
        var fromByName = ByName(from.Enums, e => Qualify(e.Module, e.Name));
        var toByName = ByName(to.Enums, e => Qualify(e.Module, e.Name));
        return BuildDelta("enums", fromByName, toByName, (oldE, newE) =>
        {
            // Member-presence diff: a row per member added/removed. Encoded as a FieldChange whose
            // `field` is "member:<memberName>"; old_value/new_value carry the member's int value
            // (or "" when the member is absent on that side). A row therefore reads:
            //   added member   -> {field:"member:FOO", old_value:"",  new_value:"3"}
            //   removed member -> {field:"member:FOO", old_value:"3", new_value:""}
            var fields = new List<FieldChange>();
            var oldMembers = MembersByName(oldE);
            var newMembers = MembersByName(newE);
            foreach (var name in oldMembers.Keys.Union(newMembers.Keys, StringComparer.Ordinal))
            {
                var oldVal = oldMembers.TryGetValue(name, out var ov)
                    ? ov.ToString(CultureInfo.InvariantCulture) : "";
                var newVal = newMembers.TryGetValue(name, out var nv)
                    ? nv.ToString(CultureInfo.InvariantCulture) : "";
                // A member present on both sides with the SAME value is not a change. A value
                // change of an existing member is also surfaced (old/new both non-empty, differ).
                AddIfChanged(fields, "member:" + name, oldVal, newVal);
            }
            return fields;
        });
    }

    private static FamilyDelta DiffConvars(Schemas.ConVars from, Schemas.ConVars to)
    {
        // Qualified key "<name>": convar names are already globally unique (no module/source axis).
        var fromByName = ByName(from.Convars, c => c.Name);
        var toByName = ByName(to.Convars, c => c.Name);
        return BuildDelta("convars", fromByName, toByName, (oldC, newC) =>
        {
            var fields = new List<FieldChange>();
            AddIfChanged(fields, "default", oldC.Default ?? "", newC.Default ?? "");
            AddIfChanged(fields, "flags", JoinFlags(oldC.Flags), JoinFlags(newC.Flags));
            return fields;
        });
    }

    private static FamilyDelta DiffCommands(Schemas.Commands from, Schemas.Commands to)
    {
        // Qualified key "<name>": command names are already globally unique (no module/source axis).
        // Note: the repeated field on the Commands message is generated as `Commands_` (trailing
        // underscore) because it collides with the containing type name (same as Omissions_).
        var fromByName = ByName(from.Commands_, c => c.Name);
        var toByName = ByName(to.Commands_, c => c.Name);
        return BuildDelta("commands", fromByName, toByName, (oldC, newC) =>
        {
            var fields = new List<FieldChange>();
            AddIfChanged(fields, "flags", JoinFlags(oldC.Flags), JoinFlags(newC.Flags));
            return fields;
        });
    }

    private static FamilyDelta DiffEngineConstants(Schemas.EngineConstants from, Schemas.EngineConstants to)
    {
        // Qualified key "<source>/<name>": the same constant name appears once per source pool
        // (e.g. source "schema_enum:animationsystem.dll/PulseBestOutflowRules_t"), so (source,name)
        // is the unique identity.
        var fromByName = ByName(from.Constants, c => Qualify(c.Source, c.Name));
        var toByName = ByName(to.Constants, c => Qualify(c.Source, c.Name));
        return BuildDelta("engine_constants", fromByName, toByName, (oldC, newC) =>
        {
            var fields = new List<FieldChange>();
            // EngineConstant is a oneof int_value | string_value — render whichever case is set
            // (empty string when unset). The closest real tracked scalar to "value".
            AddIfChanged(fields, "value", RenderConstantValue(oldC), RenderConstantValue(newC));
            return fields;
        });
    }

    // --- localization family (content-derived, build-on-demand source) ---------------------

    /// <summary>
    /// Diff the two build-on-demand localization.json files at <paramref name="fromPath"/> /
    /// <paramref name="toPath"/> into the <c>localization</c> FamilyDelta. Keyed by TOKEN:
    /// <list type="bullet">
    ///   <item><c>added</c>   — tokens in `to` not `from` (Ordinal-sorted);</item>
    ///   <item><c>removed</c> — tokens in `from` not `to` (Ordinal-sorted);</item>
    ///   <item><c>changed</c> — tokens in BOTH whose <c>englishValue</c> differs and/or whose
    ///         per-language values map differs, surfaced as up to two FieldChange rows: an
    ///         <c>englishValue</c> row (the meaningful human scalar) and a <c>valuesHash</c> row (a
    ///         SHA-256 over the token's Ordinal-sorted (language,value) pairs — captures ANY
    ///         per-language change without dumping every language for every changed token).</item>
    /// </list>
    /// Memory: each side is parsed to a compact token→(englishValue, valuesHash) map ONE AT A TIME
    /// (the full ~199 MB proto graph for a side is released before the other is parsed), so the peak
    /// footprint is one parsed side, not two. Determinism: Ordinal-sorted throughout, culture-invariant.
    /// </summary>
    private static FamilyDelta DiffLocalizationRows(
        IReadOnlyDictionary<string, LocRow> from, IReadOnlyDictionary<string, LocRow> to)
        => BuildDelta(LocalizationFamily, from, to, (oldRow, newRow) =>
        {
            var fields = new List<FieldChange>();
            AddIfChanged(fields, "englishValue", oldRow.EnglishValue, newRow.EnglishValue);
            AddIfChanged(fields, "valuesHash", oldRow.ValuesHash, newRow.ValuesHash);
            return fields;
        });

    /// <summary>One token's changelog-relevant content: its english string + a hash of all values.</summary>
    internal readonly record struct LocRow(string EnglishValue, string ValuesHash);

    /// <summary>
    /// Parse one localization.json into a compact token→<see cref="LocRow"/> map. The full parsed
    /// <see cref="Schemas.Localization"/> graph is local to this method so it is GC-eligible once the
    /// compact map is built (only the compact map crosses the return). A duplicate token is a
    /// fail-loud data error (the emitter writes Ordinal-unique tokens). Exposed internally so a
    /// corpus backfill can load each build's rows once and reuse them as the successor's `from` side.
    /// </summary>
    internal static Dictionary<string, LocRow> LoadLocalizationRows(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"BuildChangelogEmitter: localization source not found: '{path}'.", path);
        }

        Schemas.Localization msg;
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);
            msg = StrictParser.Parse<Schemas.Localization>(reader);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new InvalidDataException(
                $"BuildChangelogEmitter: localization source '{path}' does not parse as Localization: {ex.Message}",
                ex);
        }

        var rows = new Dictionary<string, LocRow>(StringComparer.Ordinal);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var t in msg.Tokens)
        {
            if (string.IsNullOrEmpty(t.Token))
            {
                throw new InvalidDataException(
                    $"BuildChangelogEmitter: localization source '{path}' has a token with an empty key.");
            }
            if (!rows.TryAdd(t.Token, new LocRow(t.EnglishValue ?? "", HashValues(hasher, t.Values))))
            {
                throw new InvalidDataException(
                    $"BuildChangelogEmitter: duplicate token '{t.Token}' in localization source '{path}'.");
            }
        }
        return rows;
    }

    /// <summary>
    /// Deterministic hex SHA-256 over a token's <paramref name="values"/> (already Ordinal-sorted by
    /// language in the emitter). Each pair is fed as <c>language \0 value \0</c> so distinct pairings
    /// cannot collide. Reuses one <see cref="IncrementalHash"/> via GetHashAndReset to avoid a
    /// per-token allocation across a ~large token set.
    /// </summary>
    private static string HashValues(IncrementalHash hasher, IEnumerable<Schemas.LanguageValue> values)
    {
        Span<byte> sep = stackalloc byte[] { 0 };
        foreach (var v in values)
        {
            hasher.AppendData(Encoding.UTF8.GetBytes(v.Language ?? ""));
            hasher.AppendData(sep);
            hasher.AppendData(Encoding.UTF8.GetBytes(v.Value ?? ""));
            hasher.AppendData(sep);
        }
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }

    // --- generic delta assembly ------------------------------------------------------------

    /// <summary>
    /// Assemble a <see cref="FamilyDelta"/> keyed by the family's qualified entity key (see file
    /// header): added = keys in to_ not from_ (Ordinal-sorted); removed = keys in from_ not to_
    /// (Ordinal-sorted); changed = keys in both whose <paramref name="changedFields"/> yields a
    /// non-empty FieldChange list, with EntryChange.name carrying the qualified key (Ordinal-sorted)
    /// and the FieldChange rows Ordinal-sorted by `field`.
    /// </summary>
    private static FamilyDelta BuildDelta<T>(
        string family,
        IReadOnlyDictionary<string, T> from,
        IReadOnlyDictionary<string, T> to,
        Func<T, T, List<FieldChange>> changedFields)
    {
        var delta = new FamilyDelta { Family = family };

        foreach (var name in to.Keys.Where(n => !from.ContainsKey(n))
                                     .OrderBy(n => n, StringComparer.Ordinal))
        {
            delta.Added.Add(name);
        }
        foreach (var name in from.Keys.Where(n => !to.ContainsKey(n))
                                       .OrderBy(n => n, StringComparer.Ordinal))
        {
            delta.Removed.Add(name);
        }

        foreach (var name in from.Keys.Where(to.ContainsKey)
                                       .OrderBy(n => n, StringComparer.Ordinal))
        {
            var fields = changedFields(from[name], to[name]);
            if (fields.Count == 0)
                continue;
            var entry = new EntryChange { Name = name };
            entry.Fields.AddRange(fields.OrderBy(f => f.Field, StringComparer.Ordinal));
            delta.Changed.Add(entry);
        }

        return delta;
    }

    /// <summary>Append a <see cref="FieldChange"/> iff the rendered scalars differ (Ordinal).</summary>
    private static void AddIfChanged(List<FieldChange> fields, string field, string oldValue, string newValue)
    {
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
            return;
        fields.Add(new FieldChange { Field = field, OldValue = oldValue, NewValue = newValue });
    }

    // --- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// Build a family's qualified entity key "&lt;qualifier&gt;/&lt;name&gt;" (module for classes/enums,
    /// source for engine_constants). Deterministic, single-pass; the '/' join matches the format a
    /// consumer parses out of added[]/removed[]/changed[].name (see file header).
    /// </summary>
    private static string Qualify(string? qualifier, string name) => (qualifier ?? "") + "/" + name;

    /// <summary>
    /// Index a repeated message by its COMPOSITE qualified key (see <see cref="Qualify"/>). A
    /// duplicate composite key is a fail-loud data error: the qualified key is the unique per-family
    /// identity, so two records sharing it is genuine corruption — NOT the normal per-module name
    /// reuse (that produces distinct composite keys and so distinct entries).
    /// </summary>
    private static Dictionary<string, T> ByName<T>(IEnumerable<T> items, Func<T, string> key)
    {
        var map = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var k = key(item);
            if (string.IsNullOrEmpty(k))
            {
                throw new InvalidDataException(
                    "BuildChangelogEmitter: a record has an empty key (corrupt source set).");
            }
            if (!map.TryAdd(k, item))
            {
                throw new InvalidDataException(
                    $"BuildChangelogEmitter: duplicate composite key '{k}' in a source family " +
                    "(the qualified key must be unique to diff).");
            }
        }
        return map;
    }

    private static Dictionary<string, long> MembersByName(Schemas.SchemaEnum e)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in e.Members)
        {
            if (string.IsNullOrEmpty(m.Name))
                continue;
            map[m.Name] = m.Value;   // last-wins on a dup member; enums are small + well-formed.
        }
        return map;
    }

    private static string JoinParents(Schemas.SchemaClass c)
        => string.Join(",", c.Parents.Select(p => p.Name));

    private static string JoinFlags(IEnumerable<string> flags)
        => string.Join(",", flags);

    private static string RenderConstantValue(Schemas.EngineConstant c)
        => c.ValueCase switch
        {
            Schemas.EngineConstant.ValueOneofCase.IntValue => c.IntValue.ToString(CultureInfo.InvariantCulture),
            Schemas.EngineConstant.ValueOneofCase.StringValue => c.StringValue ?? "",
            _ => "",
        };

    /// <summary>
    /// Read + parse a required source artifact through its generated proto3 message. A missing file
    /// throws — the caller (DiffCommand) checks omissions.json accounting BEFORE invoking the
    /// emitter, so by this point a missing source file is a genuine corrupt set.
    /// </summary>
    private static T LoadRequired<T>(string setDir, string fileName)
        where T : IMessage<T>, new()
    {
        var path = Path.Combine(setDir, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"BuildChangelogEmitter: required source artifact '{fileName}' not found in '{setDir}'.",
                path);
        }
        try
        {
            return StrictParser.Parse<T>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            throw new InvalidDataException(
                $"BuildChangelogEmitter: source artifact '{path}' does not parse as {typeof(T).Name}: {ex.Message}",
                ex);
        }
    }
}
