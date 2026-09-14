// Weapon VData extraction (weapon_vdata.json).
//
// Pipeline: open a content-depot pak01_dir.vpk (VpkArchive) -> read scripts/weapons.vdata_c
// (CRC-verified by the VPK layer) -> decode the COMPILED Source 2 resource with
// Kv3BinaryReader (binary KV3, not the KV1/KV3-text every other content emitter reads) ->
// mirror each top-level KV3 entry VERBATIM into the public WeaponVData message -> serialize
// canonical proto3 JSON -> atomic .tmp+rename.
//
// === source ===
// scripts/weapons.vdata_c, CONFIRMED against the committed build-25175329 fixture,
// 2026-09-09. The decoded DATA block is one KV3 map with 176 top-level entries: 88 `weapon_*`
// keys (45 plain + 43 `_prefab` variants), 72 numeric item-definition-index aliases that are
// near-duplicates of the named entries, 15 category/prefab objects, and one BARE STRING.
//
//   weapon_ak47 = {
//       _class = "weapon_ak47"  _base = "weapon_ak47_prefab"   // genuine KV3 keys
//       m_nDamage = 36  m_nRecoilSeed = 223  m_flArmorRatio = 1.55
//       m_flSpread = [ 0.0006, 0.0006 ]   // a 2-element array
//       m_flCycleTime = 0.1               // schema type is a 2-array; the source's shorthand
//       ... ~87 fields in total
//   }
//   "7" = { _class = "weapon_ak47" ... }              // numeric alias
//   rifle = { ... }                                   // category object
//   generic_data_type = "CBasePlayerWeaponVData"      // NOT a map
//
// The bare string is the shape trap here: code that assumes every top-level entry is a Struct
// crashes on it. The payload field is google.protobuf.Value precisely so it does not have to.
//
// === v1 mapping decisions ===
//   - A VERBATIM KV3 mirror, NOT the structured-message treatment item_definitions /
//     prop_data / surface_properties get. ~87 heterogeneous per-entry fields that move
//     between builds make a typed proto brittle and lossy; a structural mirror drops nothing.
//   - NO flattening. Valve's compiler already resolved the prefab chain at build time, so the
//     compiled entries are flat and complete. `_class` / `_base` stay ordinary payload keys —
//     they are retained as metadata, never promoted to typed fields.
//   - NO normalisation of mixed arity: m_flSpread stays a 2-element array and m_flCycleTime
//     stays a bare scalar, exactly as the resource has them. Widening the shorthand into a
//     2-array would invent data this build does not contain.
//   - NO field filtering. Every key the decode produces reaches the artifact.
//   - Kv3BinaryReader is the single decode path and is NOT re-implemented here; a
//     Kv3BinaryException is wrapped in an InvalidDataException naming the source, per the
//     repo's wrap-the-parser convention (see PropDataEmitter.ParseKv3).
//   - NUMBERS ARE DOUBLES, and that is lossy past 2^53. Kv3BinaryReader narrows KV3 int and
//     real alike to Value.NumberValue and google.protobuf.Value has no integer case, so an
//     INT64 / UINT64 larger than 2^53 has ALREADY been rounded by the time it reaches this
//     file — ulong.MaxValue arrives as 1.8446744073709552e+19 and 9007199254740993 as
//     9007199254740992, indistinguishable from a genuine DOUBLE. Nothing here can tell those
//     apart, so the check lives where it can: the narrowing site RECORDS every 64-bit integer
//     that is not the same number as the double it became, DecodeWithLoss hands that record
//     over, and RequireLossless below refuses the whole emit rather than publish a number the
//     depot did not contain. The reader still narrows — a shipped .vmdl_c carries ulong.MaxValue
//     and has to stay decodable — so the fail-loud is this caller's choice, not the reader's.
//     No weapon field in any measured build comes close to that bound, so this refusal is a
//     drift alarm, not a condition today's data meets.
//
// Invariants:
//   Determinism: entries sorted by key Ordinal (which puts the numeric aliases first and
//     LEXICOGRAPHICALLY — "1" < "10" < "13" — not in numeric order). Keys are unique by
//     CONSTRUCTION, not by assertion: the decoded top level is a protobuf map, and
//     Kv3BinaryReader.ReadObject collapses a repeated KV3 key LAST-WINS before the tree ever
//     reaches here, so a duplicate is still unobservable IN THE TREE and an emitter-side check
//     on the tree would be dead code. It is observable in the decode's LOSS RECORD, which is
//     the reader change it took to surface one, and RequireLossless refuses on that record
//     rather than sort a top level that quietly lost an entry. CanonicalJson sorts object
//     keys only, so array order
//     inside a Value is this emitter's responsibility and it is SOURCE ORDER: nothing re-orders
//     a ListValue. Canonical JSON, LF, UTF-8 no BOM.
//   Fail-loud: missing vpk / the source absent from the archive / a decode failure / a decode
//     that LOST data (a repeated KV3 key, a 64-bit integer past 2^53) / a top-level value that
//     is not a KV3 map / zero entries / a tree that nests deeper than canonical JSON will read
//     — all throw BEFORE any output bytes. No catch-and-continue.
//   All-or-nothing: build the full message in memory, then write to a sibling .tmp and
//     atomically rename.

using System.Text.Json;

using Cs2SchemaTracker.Host.Kv3Binary;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf.WellKnownTypes;

namespace Cs2SchemaTracker.Host.WeaponVData;

/// <summary>
/// Extracts <c>scripts/weapons.vdata_c</c> (compiled binary KV3) from a content-depot VPK and
/// writes the canonical weapon_vdata.json. Host-only identity fields are stamped by the
/// constructor; the payload comes verbatim from the decoded KV3 tree.
/// </summary>
/// <remarks>
/// The message type is qualified <c>Schemas.WeaponVData</c> throughout: this namespace is also
/// named <c>WeaponVData</c>, so the bare name would bind to the namespace. Same workaround
/// PropDataEmitter uses for <c>Schemas.PropData</c>.
/// </remarks>
internal sealed class WeaponVDataEmitter
{
    public const string WeaponVDataPath = "scripts/weapons.vdata_c";

    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public WeaponVDataEmitter(string schemaVersion, string buildId, string platform)
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
    /// True iff <paramref name="archive"/> ships <c>scripts/weapons.vdata_c</c> in its directory
    /// tree. Distinguishes a GENUINE absence (an era that never shipped the compiled vdata ⇒
    /// graceful omission) from a present-but-unreadable source (a missing backing chunk, which
    /// <see cref="Emit"/> still fails loud on). Directory-tree check only — no chunk is read.
    /// </summary>
    public static bool HasSource(VpkArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        // VpkArchive.Find is StringComparison.Ordinal, so the literal must be exactly lower-case.
        return archive.Find(WeaponVDataPath) is not null;
    }

    public void Emit(VpkArchive archive, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var entry = archive.Find(WeaponVDataPath);
        if (entry is null)
        {
            throw new InvalidDataException(
                $"WeaponVDataEmitter: '{WeaponVDataPath}' is not present in the VPK — refusing to "
                + "write weapon_vdata.json. Was the correct content pak01_dir.vpk supplied?");
        }

        byte[] bytes = archive.ReadEntryBytes(entry); // CRC-verified.
        (Value root, Kv3DecodeLoss loss) = Decode(bytes);
        RequireLossless(loss);

        if (root.KindCase != Value.KindOneofCase.StructValue)
        {
            throw new InvalidDataException(
                $"WeaponVDataEmitter: '{WeaponVDataPath}' top-level value is not a KV3 map "
                + $"(found {root.KindCase}).");
        }

        var document = new Schemas.WeaponVData
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
            SourceFile = WeaponVDataPath,
        };

        // Mirror the top level VERBATIM. The decoded tree's Value nodes are handed straight to
        // the message — no flattening, no normalisation, no filtering.
        var entries = new List<WeaponVDataEntry>();
        foreach (var (key, value) in root.StructValue.Fields)
        {
            entries.Add(new WeaponVDataEntry { Key = key, Value = value });
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException(
                $"WeaponVDataEmitter: parsed zero top-level entries from '{WeaponVDataPath}' — "
                + "refusing to write an empty weapon_vdata.json.");
        }

        // Ordinal by key. Keys are unique by CONSTRUCTION — the decoded top level is a protobuf
        // map and the reader already collapsed any repeated KV3 key, which RequireLossless has
        // already refused above — so no tiebreak is needed. Array order WITHIN a Value is source
        // order and is never touched.
        entries.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        document.Entries.AddRange(entries);

        try
        {
            AtomicWrite.WriteCanonical(document, outputPath);
        }
        catch (JsonException ex)
        {
            // Wrapping the whole call is safe for the all-or-nothing invariant: AtomicWrite
            // serializes fully before it creates the sibling .tmp, so a serialization failure
            // leaves no bytes behind. Deliberately NOT widened past JsonException — an IOException
            // from the write itself must keep propagating as-is.
            //
            // The headline does NOT claim depth. Depth is the only cause we have actually seen and
            // by far the likeliest, but this catch cannot distinguish it from any other JsonException
            // out of the formatter, and a message that asserts "nests too deeply" at an operator
            // staring at some other serializer failure sends them somewhere the bug is not. Name the
            // likely cause, hand over the inner message, and let it be read.
            throw new InvalidDataException(
                $"WeaponVDataEmitter: '{WeaponVDataPath}' decoded to a tree that canonical JSON "
                + $"REFUSED TO SERIALIZE: {ex.Message} The usual cause is DEPTH: Kv3BinaryReader "
                + "accepts 512 levels but CanonicalJson re-reads the formatter's output through "
                + "JsonDocument, whose depth cap is 64, so an over-deep tree fails only at "
                + "serialization — long after the decode and every check above have passed. Read the "
                + $"inner message above before assuming that is what happened. Refusing to write '{outputPath}'.", ex);
        }
    }

    /// <summary>
    /// Refuse a decode that could not carry the block in full. Kv3BinaryReader records these
    /// two losses instead of throwing on them, because a shipped .vmdl_c really does contain a
    /// UINT64 past 2^53 and a reader that refused at the narrowing site could not read it at
    /// all; opting into the refusal is per-caller, and this emitter opts in. weapon_vdata.json
    /// is a build-over-build diff source, so a value that silently disagrees with the depot is
    /// worse here than no artifact: it would read as a real change in whichever build the
    /// offending field first appeared in.
    /// </summary>
    private static void RequireLossless(Kv3DecodeLoss loss)
    {
        if (loss.DuplicateKeys.Count > 0)
        {
            throw new InvalidDataException(
                $"WeaponVDataEmitter: '{WeaponVDataPath}' repeats {loss.DuplicateKeys.Count} KV3 "
                + "key(s), which the decode collapsed LAST-WINS — the earlier value of each is "
                + $"gone and the artifact could not say so: {Sample(loss.DuplicateKeys, quoted: true)}. "
                + "Refusing to write weapon_vdata.json.");
        }

        if (loss.NarrowedIntegers.Count > 0)
        {
            throw new InvalidDataException(
                $"WeaponVDataEmitter: '{WeaponVDataPath}' carries {loss.NarrowedIntegers.Count} "
                + "64-bit integer(s) that did not survive the narrowing to the double "
                + $"google.protobuf.Value holds: {Sample(loss.NarrowedIntegers, quoted: false)}. "
                + "Each would be published as the nearest double instead, which is a DIFFERENT "
                + "number from the one the depot shipped. Refusing to write weapon_vdata.json.");
        }
    }

    /// <summary>
    /// Render the head of a loss list. One repeated key is reported once, but a block that
    /// desynchronised could name thousands, and a message that long is not one an operator
    /// reads — the count above it already carries the scale.
    /// </summary>
    private static string Sample(IReadOnlyList<string> values, bool quoted)
    {
        const int Limit = 8;
        var head = values.Take(Limit).Select(v => quoted ? $"'{v}'" : v);
        string rendered = string.Join(", ", head);
        return values.Count <= Limit
            ? rendered
            : $"{rendered}, ... (+{values.Count - Limit} more)";
    }

    private static (Value Root, Kv3DecodeLoss Loss) Decode(byte[] resourceBytes)
    {
        try
        {
            return Kv3BinaryReader.DecodeWithLoss(resourceBytes);
        }
        catch (Kv3BinaryException ex)
        {
            throw new InvalidDataException(
                $"WeaponVDataEmitter: '{WeaponVDataPath}' is not a decodable compiled KV3 "
                + $"resource: {ex.Message}.");
        }
    }
}
