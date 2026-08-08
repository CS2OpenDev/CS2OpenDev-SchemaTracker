// Entity schema emitter tests (host-mapping leg).
//
// Hand-constructs synthetic WalkerOutput fixtures (no real binaries / no walker run /
// no remote) and asserts the host mapping:
//   * faithful class/enum/field mapping, incl. recursive SchemaType (template + pointer +
//     fixed-array + bitfield), parents, and an enum with members;
//   * host-stamped identity fields (schema_version, build_id, platform, source_revision);
//   * the union model: one platform set carries module="client" AND module="server" classes;
//   * SchemaMetadata raw `value` carried through verbatim for every entry (including
// MGetKV3ClassDefaults, whose value the walker now emits pre-serialized + determinism-filtered);
//     `value_parsed` is always unset (the host no longer re-parses the filtered blob);
// * deterministic byte-identical output across two runs;
// * fail-loud on (a) a missing WalkerOutput file and
//     (b) a field missing name / type / module, with NO output bytes written.
//
// Mirrors the ModuleManifestEmitter test style.

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.EntitySchema;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.EntitySchema;

public class EntitySchemaEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "linux-x86_64";
    private const string SourceRevision = "987654";

    // ---- Fixture builders ------------------------------------------------------------

    // A class with a parent and four fields exercising every recursive SchemaType shape:
    //   - builtin (int32)
    //   - template / atomic (CUtlVector<CHandle<C_BaseEntity>>) — two inner levels, the
    //     innermost being a declared-class ref (so type_module is required and present)
    //   - pointer (PTR -> declared class)
    //   - fixed array (FIXED_ARRAY of float32, count 3)
    //   - bitfield (BITFIELD, width 4)
    // Plus a raw MGetKV3ClassDefaults metadata string (carries it through).
    private static WalkerOutput BuildRichFixture()
    {
        var entity = new SchemaEnum
        {
            Name = "MoveType_t",
            Module = "server",
            Alignment = "uint8_t",
            // Batch-1 additive enum-info enrichments: opaque flags bitmask + underlying-type
            // byte width. Distinct from `alignment` (the derived type-name string).
            Flags = 0x0002,
            Size = 1,
            // Owning project — the enum-side counterpart of SchemaClass.project_name.
            ProjectName = "server",
        };
        entity.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        entity.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_WALK", Value = 2 });
        // Negative member value — Valve uses these (proto int64 is signed for this reason).
        var invalid = new SchemaEnumMember { Name = "MOVETYPE_INVALID", Value = -1 };
        invalid.Metadata.Add(new SchemaMetadata { Name = "MPropertyDescription", Value = "sentinel" });
        entity.Members.Add(invalid);

        var cls = new SchemaClass
        {
            Name = "C_BaseEntity",
            Module = "client",
            Size = 1416,
            // Additive class-info enrichments: numeric alignment boundary + opaque flags bitmask.
            Alignment = 8,
            Flags = 0x0004,
            // Batch-1 additive class-info enrichments: second flags word, inheritance depths,
            // and the project / C++ name strings. All opaque verbatim copy-through.
            Flags2 = 0x0010,
            SingleInheritanceDepth = 2,
            MultipleInheritanceDepth = 0,
            ProjectName = "client.dll",
            CppName = "C_BaseEntity",
        };
        // Parent carries a base-class subobject offset (multiple-inheritance layout).
        cls.Parents.Add(new SchemaClassParent { Name = "CEntityInstance", Module = "client", Offset = 16 });

        // builtin int32, carrying per-field reflection annotations. All values are carried verbatim;
        // value_parsed is never set. (A field-level MGetKV3ClassDefaults is synthetic here — in real
        // walks the accessor is class-level only — but proves the value is carried on fields too.)
        var healthField = new SchemaField
        {
            Name = "m_iHealth",
            Offset = 80,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        };
        healthField.Metadata.Add(new SchemaMetadata { Name = "MNetworkEnable", Value = "" });
        healthField.Metadata.Add(new SchemaMetadata { Name = "MNetworkVar", Value = "" });
        healthField.Metadata.Add(new SchemaMetadata
        {
            Name = "MGetKV3ClassDefaults",
            Value = "{ min = 0 max = 100 }",
        });
        cls.Fields.Add(healthField);

        // CUtlVector<CHandle<C_BaseEntity>> — atomic template, nested template, declared ref.
        cls.Fields.Add(new SchemaField
        {
            Name = "m_hChildren",
            Offset = 88,
            TypeModule = "client",   // required: nested type references a declared class
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.Atomic,
                Name = "CUtlVector",
                Inner = new SchemaType
                {
                    Category = SchemaType.Types.Category.Atomic,
                    Name = "CHandle",
                    Inner = new SchemaType
                    {
                        Category = SchemaType.Types.Category.DeclaredClass,
                        Name = "C_BaseEntity",
                        Module = "client",
                    },
                },
            },
        });

        // pointer to declared class
        cls.Fields.Add(new SchemaField
        {
            Name = "m_pOwner",
            Offset = 112,
            TypeModule = "server",   // required: PTR -> declared class
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.Ptr,
                Inner = new SchemaType
                {
                    Category = SchemaType.Types.Category.DeclaredClass,
                    Name = "C_BaseCombatCharacter",
                    Module = "server",
                },
            },
        });

        // fixed array of float32, count 3
        cls.Fields.Add(new SchemaField
        {
            Name = "m_vecOrigin",
            Offset = 120,
            TypeModule = "",
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Count = 3,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "float32" },
            },
        });

        // bitfield width 4 — offset 0 is legitimate here (proves offset-0 is not rejected).
        cls.Fields.Add(new SchemaField
        {
            Name = "m_nFlags",
            Offset = 0,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Bitfield, Count = 4 },
        });

        // Raw KV3 class-defaults metadata (carries value through; value_parsed unset).
        cls.Metadata.Add(new SchemaMetadata
        {
            Name = "MGetKV3ClassDefaults",
            Value = "{ m_iHealth = 100 m_flScale = 1.0 }",
        });
        cls.Metadata.Add(new SchemaMetadata { Name = "MPropertyFriendlyName", Value = "Base Entity" });

        // One static field (m_pStaticFields), itself carrying a per-field annotation — proves
        // static fields map exactly like instance fields incl. their metadata.
        var staticField = new SchemaField
        {
            Name = "s_nInstanceCount",
            Offset = 0,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        };
        staticField.Metadata.Add(new SchemaMetadata { Name = "MNotSaved", Value = "" });
        cls.StaticFields.Add(staticField);

        var walk = new EntitySchemaWalk();
        walk.Classes.Add(cls);
        walk.Enums.Add(entity);

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",  // host re-stamps with the family version
            WalkerVersion = "0.0.0-test",
            Platform = Platform,
            EntitySchema = walk,
            SchemaSystemLayoutSignature = "sig-test",
        };
    }

    private static EntitySchemaEmitter NewEmitter() =>
        new(SchemaFamily.Version, BuildId, Platform, SourceRevision);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Tests -----------------------------------------------------------------------

    [Xunit.Fact]
    public void Maps_Classes_Enums_Fields_Faithfully_And_Stamps_Host_Fields()
    {
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(BuildRichFixture(), outPath);

            Xunit.Assert.True(File.Exists(outPath));
            var bytes = File.ReadAllBytes(outPath);
            Xunit.Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "entity_schema.json must not have a UTF-8 BOM");
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Xunit.Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // Host-stamped identity fields (canonical proto3 JSON: lowerCamelCase).
            Xunit.Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Xunit.Assert.Equal(BuildId, root.GetProperty("buildId").GetString());
            Xunit.Assert.Equal(Platform, root.GetProperty("platform").GetString());
            Xunit.Assert.Equal(SourceRevision, root.GetProperty("sourceRevision").GetString());

            // One class, one enum.
            var classes = root.GetProperty("classes");
            Xunit.Assert.Equal(1, classes.GetArrayLength());
            var cls = classes[0];
            Xunit.Assert.Equal("C_BaseEntity", cls.GetProperty("name").GetString());
            Xunit.Assert.Equal("client", cls.GetProperty("module").GetString());
            // uint64 size is a JSON string in canonical proto3 JSON.
            Xunit.Assert.Equal("1416", cls.GetProperty("size").GetString());

            // Parent chain preserved, incl. the base-class subobject offset (uint32 -> number).
            var parents = cls.GetProperty("parents");
            Xunit.Assert.Equal(1, parents.GetArrayLength());
            Xunit.Assert.Equal("CEntityInstance", parents[0].GetProperty("name").GetString());
            Xunit.Assert.Equal(16u, parents[0].GetProperty("offset").GetUInt32());

            // Fields preserve declared (offset) order.
            var fields = cls.GetProperty("fields");
            Xunit.Assert.Equal(5, fields.GetArrayLength());
            Xunit.Assert.Equal("m_iHealth", fields[0].GetProperty("name").GetString());
            Xunit.Assert.Equal("m_hChildren", fields[1].GetProperty("name").GetString());
            Xunit.Assert.Equal("m_pOwner", fields[2].GetProperty("name").GetString());
            Xunit.Assert.Equal("m_vecOrigin", fields[3].GetProperty("name").GetString());
            Xunit.Assert.Equal("m_nFlags", fields[4].GetProperty("name").GetString());

            // Recursive template: CUtlVector<CHandle<C_BaseEntity(declared)>>.
            var vecType = fields[1].GetProperty("type");
            Xunit.Assert.Equal("CUtlVector", vecType.GetProperty("name").GetString());
            var handleType = vecType.GetProperty("inner");
            Xunit.Assert.Equal("CHandle", handleType.GetProperty("name").GetString());
            var declared = handleType.GetProperty("inner");
            // proto3 JSON renders enum values by name: DECLARED_CLASS.
            Xunit.Assert.Equal("DECLARED_CLASS", declared.GetProperty("category").GetString());
            Xunit.Assert.Equal("C_BaseEntity", declared.GetProperty("name").GetString());
            Xunit.Assert.Equal("client", fields[1].GetProperty("typeModule").GetString());

            // Pointer.
            Xunit.Assert.Equal("PTR", fields[2].GetProperty("type").GetProperty("category").GetString());
            Xunit.Assert.Equal("C_BaseCombatCharacter",
                fields[2].GetProperty("type").GetProperty("inner").GetProperty("name").GetString());

            // Fixed array, count 3 (uint64 -> JSON string).
            var arr = fields[3].GetProperty("type");
            Xunit.Assert.Equal("FIXED_ARRAY", arr.GetProperty("category").GetString());
            Xunit.Assert.Equal("3", arr.GetProperty("count").GetString());
            Xunit.Assert.Equal("float32", arr.GetProperty("inner").GetProperty("name").GetString());

            // Bitfield width 4 at offset 0 (offset-0 accepted).
            var bf = fields[4].GetProperty("type");
            Xunit.Assert.Equal("BITFIELD", bf.GetProperty("category").GetString());
            Xunit.Assert.Equal("4", bf.GetProperty("count").GetString());

            // Enum with members, incl. a negative value and member metadata.
            var enums = root.GetProperty("enums");
            Xunit.Assert.Equal(1, enums.GetArrayLength());
            var en = enums[0];
            Xunit.Assert.Equal("MoveType_t", en.GetProperty("name").GetString());
            Xunit.Assert.Equal("uint8_t", en.GetProperty("alignment").GetString());
            // Batch-1 additive enum-info enrichments copy through (uint32 -> JSON number).
            Xunit.Assert.Equal(0x0002u, en.GetProperty("flags").GetUInt32());
            Xunit.Assert.Equal(1u, en.GetProperty("size").GetUInt32());
            Xunit.Assert.Equal("server", en.GetProperty("projectName").GetString());
            var members = en.GetProperty("members");
            Xunit.Assert.Equal(3, members.GetArrayLength());
            // int64 value -> JSON string.
            Xunit.Assert.Equal("-1", members[2].GetProperty("value").GetString());
            Xunit.Assert.Equal("MOVETYPE_INVALID", members[2].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Carries_Raw_Metadata_Verbatim_Including_Kv3_ClassDefaults()
    {
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(BuildRichFixture(), outPath);

            var text = File.ReadAllText(outPath);
            using var doc = JsonDocument.Parse(text);
            var meta = doc.RootElement.GetProperty("classes")[0].GetProperty("metadata");

            // Metadata sorted by (name, value) Ordinal: MGetKV3ClassDefaults < MPropertyFriendlyName.
            Xunit.Assert.Equal(2, meta.GetArrayLength());

            // MGetKV3ClassDefaults carries the raw value verbatim. The walker now emits the class
            // defaults already serialized + determinism-filtered (a diff-stable blob that is
            // intentionally not strictly parseable JSON/KV3), so value_parsed is UNSET — the host no
            // longer re-parses it.
            var kv3 = meta[0];
            Xunit.Assert.Equal("MGetKV3ClassDefaults", kv3.GetProperty("name").GetString());
            Xunit.Assert.Equal("{ m_iHealth = 100 m_flScale = 1.0 }", kv3.GetProperty("value").GetString());
            Xunit.Assert.False(kv3.TryGetProperty("valueParsed", out _),
                "value_parsed is no longer populated (the walker emits a filtered blob, host carries it verbatim)");

            // A non-KV3 annotation likewise carries its value verbatim, no value_parsed.
            var friendly = meta[1];
            Xunit.Assert.Equal("MPropertyFriendlyName", friendly.GetProperty("name").GetString());
            Xunit.Assert.Equal("Base Entity", friendly.GetProperty("value").GetString());
            Xunit.Assert.False(friendly.TryGetProperty("valueParsed", out _),
                "non-KV3 annotations must not carry value_parsed");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Maps_Field_Metadata_Class_Alignment_Flags_And_StaticFields()
    {
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(BuildRichFixture(), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var cls = doc.RootElement.GetProperty("classes")[0];

            // Class-level numeric enrichments copy through verbatim (uint32 -> JSON number).
            Xunit.Assert.Equal(8u, cls.GetProperty("alignment").GetUInt32());
            Xunit.Assert.Equal(0x0004u, cls.GetProperty("flags").GetUInt32());
            // Batch-1 additive class-info enrichments copy through verbatim.
            Xunit.Assert.Equal(0x0010u, cls.GetProperty("flags2").GetUInt32());
            Xunit.Assert.Equal(2u, cls.GetProperty("singleInheritanceDepth").GetUInt32());
            Xunit.Assert.Equal(0u, cls.GetProperty("multipleInheritanceDepth").GetUInt32());
            Xunit.Assert.Equal("client.dll", cls.GetProperty("projectName").GetString());
            Xunit.Assert.Equal("C_BaseEntity", cls.GetProperty("cppName").GetString());

            // ---- Per-field metadata on the first instance field (m_iHealth). ----
            var healthMeta = cls.GetProperty("fields")[0].GetProperty("metadata");
            // Sorted by (name, value) Ordinal: MGetKV3ClassDefaults < MNetworkEnable < MNetworkVar.
            Xunit.Assert.Equal(3, healthMeta.GetArrayLength());

            // The field annotation carries its value verbatim; value_parsed is unset (host no longer
            // parses MGetKV3ClassDefaults — the walker emits a determinism-filtered blob).
            var fieldKv3 = healthMeta[0];
            Xunit.Assert.Equal("MGetKV3ClassDefaults", fieldKv3.GetProperty("name").GetString());
            Xunit.Assert.Equal("{ min = 0 max = 100 }", fieldKv3.GetProperty("value").GetString());
            Xunit.Assert.False(fieldKv3.TryGetProperty("valueParsed", out _),
                "field-level MGetKV3ClassDefaults carries value verbatim, no value_parsed");

            // Plain/empty-value annotations carry the raw value and leave value_parsed unset.
            var networkEnable = healthMeta[1];
            Xunit.Assert.Equal("MNetworkEnable", networkEnable.GetProperty("name").GetString());
            Xunit.Assert.False(networkEnable.TryGetProperty("valueParsed", out _),
                "empty-value field annotations must not carry value_parsed");
            Xunit.Assert.Equal("MNetworkVar", healthMeta[2].GetProperty("name").GetString());

            // Fields without annotations carry no metadata array element (default-empty repeated).
            var vecField = cls.GetProperty("fields")[3];
            Xunit.Assert.Equal("m_vecOrigin", vecField.GetProperty("name").GetString());
            Xunit.Assert.False(vecField.TryGetProperty("metadata", out var emptyMeta)
                && emptyMeta.GetArrayLength() > 0,
                "a field with no annotations must not emit metadata entries");

            // ---- Static fields map like instance fields, incl. their metadata. ----
            var staticFields = cls.GetProperty("staticFields");
            Xunit.Assert.Equal(1, staticFields.GetArrayLength());
            var sf = staticFields[0];
            Xunit.Assert.Equal("s_nInstanceCount", sf.GetProperty("name").GetString());
            Xunit.Assert.Equal("int32", sf.GetProperty("type").GetProperty("name").GetString());
            var sfMeta = sf.GetProperty("metadata");
            Xunit.Assert.Equal(1, sfMeta.GetArrayLength());
            Xunit.Assert.Equal("MNotSaved", sfMeta[0].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Enum_ProjectName_Distinguishes_Globally_Registered_Enums()
    {
        // The case the field exists for: `module` is the BINARY a scope belongs to, so every
        // globally-registered enum reports "!GlobalTypes" there and the whole set collapses into
        // one bucket. project_name keeps the per-project attribution, exactly as it already does
        // for classes. An enum whose record carries no project string emits "" rather than
        // failing the artifact (FormatDefaultValues keeps the key present either way).
        var workDir = NewWorkDir();
        try
        {
            var walk = new EntitySchemaWalk();
            walk.Enums.Add(new SchemaEnum
            {
                Name = "EParticleFalloffFunction_t",
                Module = "!GlobalTypes",
                ProjectName = "particles",
            });
            walk.Enums.Add(new SchemaEnum
            {
                Name = "FieldNetworkOption_t",
                Module = "!GlobalTypes",
                ProjectName = "animgraphlib",
            });
            walk.Enums.Add(new SchemaEnum { Name = "Untagged_t", Module = "!GlobalTypes" });

            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(new WalkerOutput { Platform = Platform, EntitySchema = walk }, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var enums = doc.RootElement.GetProperty("enums");
            Xunit.Assert.Equal(3, enums.GetArrayLength());

            // Ordered by (module, name) Ordinal — module is identical, so name decides.
            Xunit.Assert.Equal("EParticleFalloffFunction_t", enums[0].GetProperty("name").GetString());
            Xunit.Assert.Equal("particles", enums[0].GetProperty("projectName").GetString());
            Xunit.Assert.Equal("FieldNetworkOption_t", enums[1].GetProperty("name").GetString());
            Xunit.Assert.Equal("animgraphlib", enums[1].GetProperty("projectName").GetString());
            // Same module for all three: without project_name they are indistinguishable.
            Xunit.Assert.Equal("!GlobalTypes", enums[0].GetProperty("module").GetString());
            Xunit.Assert.Equal("!GlobalTypes", enums[1].GetProperty("module").GetString());

            // Untagged record: key present, value empty — not an emit failure.
            Xunit.Assert.Equal("Untagged_t", enums[2].GetProperty("name").GetString());
            Xunit.Assert.Equal("", enums[2].GetProperty("projectName").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void StaticFields_Empty_Is_Handled_Gracefully()
    {
        // The walker emits static_fields empty when reachability fails; the emitter must not
        // choke and must not emit a populated staticFields array.
        var workDir = NewWorkDir();
        try
        {
            var cls = new SchemaClass { Name = "C_NoStatics", Module = "client", Size = 8 };
            cls.Fields.Add(new SchemaField
            {
                Name = "m_x",
                Offset = 0,
                Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
            });
            var walk = new EntitySchemaWalk();
            walk.Classes.Add(cls);

            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(new WalkerOutput { Platform = Platform, EntitySchema = walk }, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var cls0 = doc.RootElement.GetProperty("classes")[0];
            Xunit.Assert.False(cls0.TryGetProperty("staticFields", out var sf) && sf.GetArrayLength() > 0,
                "empty static_fields must not emit populated staticFields");
            // alignment / flags default to 0 and are emitted (FormatDefaultValues) as numbers.
            Xunit.Assert.Equal(0u, cls0.GetProperty("alignment").GetUInt32());
            Xunit.Assert.Equal(0u, cls0.GetProperty("flags").GetUInt32());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Kv3_ClassDefaults_Carried_Verbatim_Never_Parsed()
    {
        var workDir = NewWorkDir();
        try
        {
            var cls = new SchemaClass { Name = "C_Bad", Module = "client", Size = 8 };
            cls.Fields.Add(new SchemaField
            {
                Name = "m_x",
                Offset = 0,
                Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
            });
            cls.Metadata.Add(new SchemaMetadata
            {
                Name = "MGetKV3ClassDefaults",
                Value = "{ m_iHealth = 100 ",   // unterminated map -> KV3 parse failure
            });
            var walk = new EntitySchemaWalk();
            walk.Classes.Add(cls);

            var outPath = Path.Combine(workDir, "entity_schema.json");
            // degrade: a single unparseable KV3 must NOT fail the extract.
            NewEmitter().Emit(new WalkerOutput { Platform = Platform, EntitySchema = walk }, outPath);
            Xunit.Assert.True(File.Exists(outPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var kv3 = doc.RootElement.GetProperty("classes")[0].GetProperty("metadata")[0];
            // Raw value preserved, value_parsed unset.
            Xunit.Assert.Equal("{ m_iHealth = 100 ", kv3.GetProperty("value").GetString());
            Xunit.Assert.False(kv3.TryGetProperty("valueParsed", out _),
                "malformed KV3 leaves value_parsed unset");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void One_Platform_Set_Carries_Both_Client_And_Server_Module_Classes()
    {
        // The union model (v0.2): one walk per platform loads ALL modules, so a single
        // emitted entity_schema.json must carry classes tagged module="client" AND
        // module="server". Platform itself never encodes client/server.
        var workDir = NewWorkDir();
        try
        {
            SchemaField IntField(string name) => new()
            {
                Name = name,
                Offset = 0,
                TypeModule = "",
                Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
            };

            var clientCls = new SchemaClass { Name = "C_BaseEntity", Module = "client", Size = 8 };
            clientCls.Fields.Add(IntField("m_iHealth"));
            var serverCls = new SchemaClass { Name = "CBaseEntity", Module = "server", Size = 8 };
            serverCls.Fields.Add(IntField("m_iMaxHealth"));

            var walk = new EntitySchemaWalk();
            walk.Classes.Add(clientCls);
            walk.Classes.Add(serverCls);

            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(new WalkerOutput { Platform = Platform, EntitySchema = walk }, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var modules = doc.RootElement.GetProperty("classes")
                .EnumerateArray()
                .Select(c => c.GetProperty("module").GetString())
                .ToHashSet();

            Xunit.Assert.Contains("client", modules);
            Xunit.Assert.Contains("server", modules);
            // platform stays a single OS+arch token; no client/server suffix.
            Xunit.Assert.Equal(Platform, doc.RootElement.GetProperty("platform").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Produces_Byte_Identical_Output_Across_Two_Runs()
    {
        var workDir = NewWorkDir();
        try
        {
            var outA = Path.Combine(workDir, "a.json");
            var outB = Path.Combine(workDir, "b.json");
            // Fresh fixtures each time; same logical content.
            NewEmitter().Emit(BuildRichFixture(), outA);
            NewEmitter().Emit(BuildRichFixture(), outB);

            var bytesA = File.ReadAllBytes(outA);
            var bytesB = File.ReadAllBytes(outB);
            Xunit.Assert.Equal(bytesA, bytesB);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Reorders_Input_To_Stable_Output_Regardless_Of_Walker_Order()
    {
        var workDir = NewWorkDir();
        try
        {
            // Two classes in two different input orders must produce identical output.
            static WalkerOutput TwoClasses(bool reversed)
            {
                SchemaField F() => new()
                {
                    Name = "m_x",
                    Offset = 0,
                    Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
                };
                var a = new SchemaClass { Name = "C_Alpha", Module = "client", Size = 8 };
                a.Fields.Add(F());
                var b = new SchemaClass { Name = "C_Beta", Module = "client", Size = 8 };
                b.Fields.Add(F());
                var walk = new EntitySchemaWalk();
                if (reversed)
                { walk.Classes.Add(b); walk.Classes.Add(a); }
                else
                { walk.Classes.Add(a); walk.Classes.Add(b); }
                return new WalkerOutput { Platform = Platform, EntitySchema = walk };
            }

            var outA = Path.Combine(workDir, "a.json");
            var outB = Path.Combine(workDir, "b.json");
            NewEmitter().Emit(TwoClasses(reversed: false), outA);
            NewEmitter().Emit(TwoClasses(reversed: true), outB);

            Xunit.Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_Missing_WalkerOutput_File_Throws_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            var missing = Path.Combine(workDir, "does-not-exist.pb");
            var outPath = Path.Combine(workDir, "entity_schema.json");

            Xunit.Assert.Throws<FileNotFoundException>(
                () => NewEmitter().EmitFromFile(missing, outPath));

            Xunit.Assert.False(File.Exists(outPath), "no output bytes on failure");
            Xunit.Assert.False(File.Exists(outPath + ".tmp"), "no leftover temp file");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_Corrupt_WalkerOutput_Throws_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            var corrupt = Path.Combine(workDir, "corrupt.pb");
            // Bytes that are not a valid protobuf message.
            File.WriteAllBytes(corrupt, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            var outPath = Path.Combine(workDir, "entity_schema.json");

            Xunit.Assert.Throws<InvalidDataException>(
                () => NewEmitter().EmitFromFile(corrupt, outPath));

            Xunit.Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_Field_Missing_Name_Throws_And_Writes_Nothing()
    {
        AssertFieldFailLoud(f => f.Name = "");
    }

    [Xunit.Fact]
    public void FailLoud_Field_Missing_Type_Throws_And_Writes_Nothing()
    {
        // Clear the type message entirely (proto3 default: unset).
        AssertFieldFailLoud(f => f.Type = null);
    }

    [Xunit.Fact]
    public void FailLoud_Field_Type_Unspecified_Category_Throws()
    {
        AssertFieldFailLoud(f => f.Type = new SchemaType
        {
            Category = SchemaType.Types.Category.Unspecified,
            Name = "garbage",
        });
    }

    [Xunit.Fact]
    public void FailLoud_DeclaredRef_Field_Missing_Module_Throws()
    {
        // A field whose type references a declared class but carries no type_module.
        AssertFieldFailLoud(f =>
        {
            f.TypeModule = "";
            f.Type = new SchemaType
            {
                Category = SchemaType.Types.Category.DeclaredClass,
                Name = "C_SomethingDeclared",
                Module = "server",
            };
        });
    }

    private static void AssertFieldFailLoud(Action<SchemaField> mutate)
    {
        var workDir = NewWorkDir();
        try
        {
            var field = new SchemaField
            {
                Name = "m_ok",
                Offset = 0,
                TypeModule = "",
                Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
            };
            mutate(field);

            var cls = new SchemaClass { Name = "C_Test", Module = "client", Size = 8 };
            cls.Fields.Add(field);
            var walk = new EntitySchemaWalk();
            walk.Classes.Add(cls);
            var wo = new WalkerOutput { Platform = Platform, EntitySchema = walk };

            var outPath = Path.Combine(workDir, "entity_schema.json");
            Xunit.Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(wo, outPath));

            Xunit.Assert.False(File.Exists(outPath), "no output bytes on failure");
            Xunit.Assert.False(File.Exists(outPath + ".tmp"), "no leftover temp file");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_Missing_EntitySchema_Throws()
    {
        var workDir = NewWorkDir();
        try
        {
            var wo = new WalkerOutput { Platform = Platform };  // EntitySchema unset.
            var outPath = Path.Combine(workDir, "entity_schema.json");
            Xunit.Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(wo, outPath));
            Xunit.Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
