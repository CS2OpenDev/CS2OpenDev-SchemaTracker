// Entity schema serializer (entity_schema.json).
//
// Consumes the walker's per-(binary-dir, platform) intermediate (WalkerOutput, a binary protobuf;
// schemas/walker_output.proto) and lifts WalkerOutput.entity_schema's classes[]/enums[] straight
// into the public EntitySchema message (schemas/entity_schema.proto), then stamps the host-only
// identity fields the walker cannot know (schema_version, build_id, platform, source_revision) and
// writes the canonical proto3-JSON entity_schema.json. One walk per platform loads ALL modules, so
// a single emitted set carries classes tagged module="client" AND module="server" (the union
// model); client/server is the per-class SchemaClass.module tag, NOT a platform dimension.
//
// Deserialization uses Google.Protobuf generated message classes (Grpc.Tools codegen for
// entity_schema.proto + walker_output.proto):
//   - The walker emits a BINARY protobuf WalkerOutput; the host must read exactly that wire format.
//     Generated message classes parse it natively; a POCO mirror would re-implement protobuf wire
//     decoding by hand (hand-maintained twins drift).
//   - walker_output.proto reuses entity_schema.proto's messages by import, so the lift from
//     WalkerOutput.EntitySchema -> EntitySchema is a mechanical field copy of the SAME generated
//     types. No translation layer.
//   - The public artifact is serialized via Google.Protobuf.JsonFormatter (canonical proto3 JSON
//     mapping), the path the round-trip test verifies. JsonFormatter emits in field-number order
//     (not sorted), so we post-process through the CanonicalJson sorter (sorted keys, LF, UTF-8 no
//     BOM).
//
// Scope: this emitter performs the structural mapping AND the KV3 default-value parse. It carries
// SchemaMetadata.value (the raw KV3 / annotation string) through verbatim for every metadata entry,
// and for MGetKV3ClassDefaults entries also parses the raw KV3 into the structural
// google.protobuf.Value SchemaMetadata.value_parsed. A class whose KV3 string fails to parse keeps
// the raw value only, with value_parsed UNSET and a parse-failure note to stderr. A single
// unparseable KV3 does NOT fail the extract — the parity behavior degrades gracefully, and is not a
// fail-loud input-binary failure.
//
// Invariants:
//   Determinism: classes/enums/fields/members/metadata/parents emitted in a deterministic order;
//     canonical JSON (sorted keys); LF; UTF-8 no BOM.
//   Fail-loud: missing/corrupt WalkerOutput, missing entity_schema, or any field record lacking
//     non-default name/type/module throws BEFORE any output bytes are written. No catch-and-continue.
//   All-or-nothing: write to a sibling .tmp then atomically rename; on mid-write throw the temp file
//     is deleted and any pre-existing target is left untouched.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.EntitySchema;

/// <summary>
/// Maps a walker <see cref="WalkerOutput"/> into the public <see cref="Schemas.EntitySchema"/>
/// and writes the canonical entity_schema.json.
/// </summary>
public sealed class EntitySchemaEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;
    private readonly string _sourceRevision;

    /// <summary>
    /// Construct an emitter parameterised by the host-only identity fields the walker cannot know.
    /// <paramref name="schemaVersion"/> is the schemas/*.proto FAMILY version — pass
    /// <see cref="SchemaFamily.Version"/>; do not hardcode a literal.
    /// <paramref name="platform"/> is one of "windows-x86_64" | "linux-x86_64".
    /// <paramref name="sourceRevision"/> is the Steam changelist as a string, fed by the
    /// extract orchestrator / provenance assembly.
    /// </summary>
    public EntitySchemaEmitter(string schemaVersion, string buildId, string platform, string sourceRevision)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        ArgumentNullException.ThrowIfNull(sourceRevision); // changelist may be "" if genuinely unknown upstream
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
        _sourceRevision = sourceRevision;
    }

    /// <summary>
    /// Read the walker output file (binary protobuf <see cref="WalkerOutput"/>), map it,
    /// and write entity_schema.json to <paramref name="outputPath"/>. Throws on the first
    /// validation failure without writing any output bytes.
    /// </summary>
    public void EmitFromFile(string walkerOutputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(walkerOutputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!File.Exists(walkerOutputPath))
        {
            throw new FileNotFoundException(
                $"EntitySchemaEmitter: walker output file not found: '{walkerOutputPath}'.",
                walkerOutputPath);
        }

        byte[] bytes = File.ReadAllBytes(walkerOutputPath);
        WalkerOutput walkerOutput;
        try
        {
            walkerOutput = WalkerOutput.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            // Corrupt / truncated intermediate. Fail loud — re-wrap so the failed stage is named,
            // but do NOT swallow-and-continue.
            throw new InvalidDataException(
                $"EntitySchemaEmitter: failed to parse walker output '{walkerOutputPath}' as WalkerOutput.", ex);
        }

        Emit(walkerOutput, outputPath);
    }

    /// <summary>
    /// Map an in-memory <see cref="WalkerOutput"/> and write entity_schema.json. Validates,
    /// builds the full document, then atomically writes. No bytes hit disk before validation passes.
    /// </summary>
    public void Emit(WalkerOutput walkerOutput, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(walkerOutput);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (walkerOutput.EntitySchema is null)
        {
            throw new InvalidDataException(
                "EntitySchemaEmitter: WalkerOutput.entity_schema is unset — nothing to map.");
        }

        // 1. Build the public EntitySchema. Lift classes/enums from the walk, deep-copying so we own
        //    the instances, validating every field record, and ordering every collection
        //    deterministically. All BEFORE any disk write.
        var document = new Schemas.EntitySchema
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
            SourceRevision = _sourceRevision,
        };

        EntitySchemaWalk walk = walkerOutput.EntitySchema;

        foreach (SchemaClass cls in OrderClasses(walk.Classes))
        {
            document.Classes.Add(MapClass(cls));
        }
        foreach (SchemaEnum en in OrderEnums(walk.Enums))
        {
            document.Enums.Add(MapEnum(en));
        }

        // 2. Serialize via canonical proto3 JSON, then sort keys.
        string json = SerializeCanonical(document);

        // 3. Atomic write: sibling .tmp then File.Move overwrite.
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

    // ---- Canonical proto3 JSON --------------------------------------------------------

    // FormatDefaultValues: emit zero-valued scalars (e.g. offset 0, size 0) so the output is a
    // complete, stable record per run regardless of which fields happen to be default for a given
    // build. PreserveProtoFieldNames is intentionally NOT set: canonical proto3 JSON uses
    // lowerCamelCase, matching the other host emitters (cf. ModuleManifestEmitter).
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true)
            .WithIndentation("  "));

    internal static string SerializeCanonical(IMessage message)
    {
        // JsonFormatter emits fields in field-number order, not sorted. Round-trip through the
        // shared CanonicalJson sorter to get sorted keys + LF + UTF-8 no BOM.
        string formatted = Formatter.Format(message);
        return CanonicalJson.SerializeRawJson(formatted);
    }

    // ---- Deterministic ordering ------------------------------------------------------
    //
    // The walker SHOULD already emit in a stable order, but the host must not rely on that for
    // determinism. We sort by stable keys here. Class/enum identity is (module, name);
    // fields and members keep their declared order (offset / value), which is meaningful
    // and already stable; metadata is sorted by (name, value) since metadata order is not
    // semantically meaningful and the walker's traversal order is not guaranteed stable.

    private static IEnumerable<SchemaClass> OrderClasses(IEnumerable<SchemaClass> classes) =>
        classes.OrderBy(c => c.Module, StringComparer.Ordinal)
               .ThenBy(c => c.Name, StringComparer.Ordinal);

    private static IEnumerable<SchemaEnum> OrderEnums(IEnumerable<SchemaEnum> enums) =>
        enums.OrderBy(e => e.Module, StringComparer.Ordinal)
             .ThenBy(e => e.Name, StringComparer.Ordinal);

    // ---- Mapping (deep copy + validate) ----------------------------------------------

    private static SchemaClass MapClass(SchemaClass src)
    {
        if (string.IsNullOrEmpty(src.Name))
        {
            throw new InvalidDataException(
                "EntitySchemaEmitter: SchemaClass with empty name (requires a named class).");
        }
        if (string.IsNullOrEmpty(src.Module))
        {
            throw new InvalidDataException(
                $"EntitySchemaEmitter: SchemaClass '{src.Name}' has empty module.");
        }

        var dst = new SchemaClass
        {
            Name = src.Name,
            Module = src.Module,
            Size = src.Size,
            // Additive numeric class-info scalars — opaque/verbatim copy-through (proto3 default 0
            // emitted by FormatDefaultValues). alignment is a byte boundary, flags an opaque Source2
            // bitmask; the host does not interpret either.
            Alignment = src.Alignment,
            Flags = src.Flags,
            // More additive class-info scalars/strings — opaque/verbatim copy-through. flags2 is a
            // second Source2 bitmask word (often 0); the inheritance depths are raw
            // m_nSingleInheritanceDepth / m_nMultipleInheritanceDepth; project_name / cpp_name are
            // the Source2 module-project + C++ type-name strings. The host interprets none of them.
            Flags2 = src.Flags2,
            SingleInheritanceDepth = src.SingleInheritanceDepth,
            MultipleInheritanceDepth = src.MultipleInheritanceDepth,
            ProjectName = src.ProjectName,
            CppName = src.CppName,
        };

        // Parents keep declared order (the C++ parent chain order is meaningful).
        foreach (SchemaClassParent p in src.Parents)
        {
            if (string.IsNullOrEmpty(p.Name))
            {
                throw new InvalidDataException(
                    $"EntitySchemaEmitter: class '{src.Name}' has a parent with empty name.");
            }
            // offset is the base-class subobject byte offset (SchemaBaseClassInfoData_t.m_nOffset),
            // verbatim copy-through for multiple-inheritance layout reconstruction.
            dst.Parents.Add(new SchemaClassParent { Name = p.Name, Module = p.Module, Offset = p.Offset });
        }

        // Fields keep declared order (offset order is meaningful and stable).
        foreach (SchemaField f in src.Fields)
        {
            dst.Fields.Add(MapField(f, src.Name));
        }

        // Static fields (m_pStaticFields) map exactly like instance fields, preserving the
        // walker's declared order. May be empty (walker emits empty if unreachable) — the
        // loop simply does nothing then.
        foreach (SchemaField f in src.StaticFields)
        {
            dst.StaticFields.Add(MapField(f, src.Name));
        }

        foreach (SchemaMetadata m in OrderMetadata(src.Metadata))
        {
            dst.Metadata.Add(MapMetadata(m));
        }

        return dst;
    }

    private static SchemaField MapField(SchemaField src, string ownerClassName)
    {
        // Every emitted field record must carry non-default values for name, offset, type, and
        // module. Enforced as a fail-loud gate:
        //   - name : must be non-empty.
        //   - type : the SchemaType message must be present with a resolved Category
        //            (CATEGORY_UNSPECIFIED == the proto3 default == not a real type).
        //   - module: a field's module attribution is type_module, REQUIRED for declared_class /
        //            declared_enum references (the proto documents it as "" otherwise). So require
        //            type_module non-empty exactly when the field's type (or a nested template arg)
        //            is a DECLARED_CLASS / DECLARED_ENUM. Builtins/atomics legitimately carry "".
        //   - offset: offset 0 is LEGITIMATE (the first field of any struct), so we do not reject it;
        //            the proto always carries the field, default 0 included.
        if (string.IsNullOrEmpty(src.Name))
        {
            throw new InvalidDataException(
                $"EntitySchemaEmitter: class '{ownerClassName}' has a field with empty name.");
        }
        if (src.Type is null || src.Type.Category == SchemaType.Types.Category.Unspecified)
        {
            throw new InvalidDataException(
                $"EntitySchemaEmitter: class '{ownerClassName}' field '{src.Name}' has no resolved type "
                + "(missing SchemaType / CATEGORY_UNSPECIFIED).");
        }
        if (ReferencesDeclaredType(src.Type) && string.IsNullOrEmpty(src.TypeModule))
        {
            throw new InvalidDataException(
                $"EntitySchemaEmitter: class '{ownerClassName}' field '{src.Name}' references a "
                + "declared class/enum but carries no type_module (requires module attribution).");
        }

        var dst = new SchemaField
        {
            Name = src.Name,
            Offset = src.Offset,
            Type = MapType(src.Type, ownerClassName, src.Name),
            TypeModule = src.TypeModule,
        };

        // Per-field reflection annotations (m_pMetadata) route through the SAME MapMetadata path as
        // class / enum-member metadata: raw `value` carried verbatim, and the MGetKV3ClassDefaults
        // structural parse applied uniformly. Most field annotations (MNetworkVar, MNotSaved, ...)
        // have a plain/empty value and leave value_parsed unset; routing them through the same parser
        // keeps behavior consistent so any structured ones get value_parsed for free. Sorted by
        // (name, value) like all metadata.
        foreach (SchemaMetadata m in OrderMetadata(src.Metadata))
        {
            dst.Metadata.Add(MapMetadata(m));
        }

        return dst;
    }

    private static bool ReferencesDeclaredType(SchemaType? t)
    {
        if (t is null)
        {
            return false;
        }
        if (t.Category is SchemaType.Types.Category.DeclaredClass or SchemaType.Types.Category.DeclaredEnum)
        {
            return true;
        }
        // A POINTER is a REFERENCE, not an embedding. A `Foo*` field does not embed Foo's layout —
        // it points to a separate object whose module is attributed where Foo is itself emitted (and
        // pointer targets are often non-schema interfaces, e.g. IPhysicsRagdollControl, whose
        // type-scope is null so the module is genuinely unresolvable). So module attribution is
        // required only for declared types reached BY VALUE — embedded fields and ATOMIC/FIXED_ARRAY
        // container elements — never through pointer indirection. Stop the recursion at PTR boundaries.
        if (t.Category is SchemaType.Types.Category.Ptr)
        {
            return false;
        }
        return ReferencesDeclaredType(t.Inner)
            || ReferencesDeclaredType(t.Inner2)
            || ReferencesDeclaredType(t.Inner3);
    }

    private static SchemaType MapType(SchemaType src, string ownerClassName, string fieldName)
    {
        // Recursive deep copy. A declared class/enum reference must name a target.
        if (src.Category is SchemaType.Types.Category.DeclaredClass or SchemaType.Types.Category.DeclaredEnum
            && string.IsNullOrEmpty(src.Name))
        {
            throw new InvalidDataException(
                $"EntitySchemaEmitter: class '{ownerClassName}' field '{fieldName}' has a declared "
                + "class/enum type with no name.");
        }

        var dst = new SchemaType
        {
            Category = src.Category,
            Name = src.Name,
            Module = src.Module,
            Count = src.Count,
        };
        if (src.Inner is not null)
        {
            dst.Inner = MapType(src.Inner, ownerClassName, fieldName);
        }
        if (src.Inner2 is not null)
        {
            dst.Inner2 = MapType(src.Inner2, ownerClassName, fieldName);
        }
        if (src.Inner3 is not null)
        {
            dst.Inner3 = MapType(src.Inner3, ownerClassName, fieldName);
        }
        return dst;
    }

    private static SchemaEnum MapEnum(SchemaEnum src)
    {
        if (string.IsNullOrEmpty(src.Name))
        {
            throw new InvalidDataException(
                "EntitySchemaEmitter: SchemaEnum with empty name.");
        }
        if (string.IsNullOrEmpty(src.Module))
        {
            throw new InvalidDataException(
                $"EntitySchemaEmitter: SchemaEnum '{src.Name}' has empty module.");
        }

        var dst = new SchemaEnum
        {
            Name = src.Name,
            Module = src.Module,
            Alignment = src.Alignment,
            // Batch-1 additive enum-info scalars — opaque/verbatim copy-through. flags is the
            // raw SchemaEnumInfoData_t.m_nFlags bitmask; size is the underlying-type byte width
            // (m_nSize), distinct from `alignment` which is the derived type-name string.
            Flags = src.Flags,
            Size = src.Size,
        };
        // Members keep declared order (value order is meaningful and stable).
        foreach (SchemaEnumMember m in src.Members)
        {
            if (string.IsNullOrEmpty(m.Name))
            {
                throw new InvalidDataException(
                    $"EntitySchemaEmitter: enum '{src.Name}' has a member with empty name.");
            }
            var dstMember = new SchemaEnumMember { Name = m.Name, Value = m.Value };
            foreach (SchemaMetadata meta in OrderMetadata(m.Metadata))
            {
                dstMember.Metadata.Add(MapMetadata(meta));
            }
            dst.Members.Add(dstMember);
        }
        return dst;
    }

    private static IEnumerable<SchemaMetadata> OrderMetadata(IEnumerable<SchemaMetadata> metadata) =>
        metadata.OrderBy(m => m.Name, StringComparer.Ordinal)
                .ThenBy(m => m.Value, StringComparer.Ordinal);

    // The one metadata key whose `value` is a KV3 default-value payload we parse into the structural
    // value_parsed. Other annotation values (MPropertyFriendlyName, ...) are plain strings and carry
    // value_parsed unset.
    private static SchemaMetadata MapMetadata(SchemaMetadata src)
    {
        if (string.IsNullOrEmpty(src.Name))
        {
            throw new InvalidDataException(
                "EntitySchemaEmitter: SchemaMetadata with empty name.");
        }

        // Carry the RAW value through verbatim for every entry. The walker owns the value: for
        // MGetKV3ClassDefaults it now emits the class-defaults serialized by tier0's SaveKV3AsJSON,
        // already run through the walker's determinism filter (auto-id fields blanked so the artifact
        // is byte-identical across runs). That filter makes the text intentionally NOT strictly valid
        // JSON/KV3, so it is carried verbatim in `value` and value_parsed is left UNSET — we do NOT
        // attempt a structural parse of the filtered blob (doing so would fail on every class and spam
        // the log). If a future walker emits UNfiltered structured defaults, re-introduce a parse here.
        var dst = new SchemaMetadata
        {
            Name = src.Name,
            Value = src.Value,
        };

        return dst;
    }
}
