// Effective-builtin derivation tests (SchemaClass.effective_builtin; issue #10).
//
// Hand-constructs synthetic WalkerOutput fixtures (no real binaries / no walker run) and
// asserts the host-side derivation over the emitted artifact:
//   * the motivating case — a single-member struct wrapping a fixed builtin array
//     (CInButtonState / uint64[3]) — resolves through the struct hop and the array wrapper;
//   * bare strong-typedef wrappers (GameTime_t / float32) resolve;
//   * wrapper-of-wrapper chains resolve through multiple by-value hops;
//   * a class whose only fields live on its parent chain resolves;
//   * everything the rule excludes stays UNSET: multi-member decompositions, ATOMIC/PTR/
//     DECLARED_ENUM leaves, by-value targets absent from the walk, zero-count arrays,
//     unknown builtin names, by-value reference cycles, and zero-field classes;
//   * the emitted JSON shape consumers read (effectiveBuiltin object, camelCase keys,
//     elementCount as a proto3 uint64 string).
//
// Mirrors the EntitySchemaEmitterTest style: synthetic fixtures, temp dirs, emitted-JSON
// assertions through the real Emit path.

using System.Text.Json;

using Cs2SchemaTracker.Host.EntitySchema;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.EntitySchema;

public class EffectiveBuiltinDerivationTest
{
    private const string BuildId = "13371337";
    private const string Platform = "linux-x86_64";
    private const string SourceRevision = "987654";

    private static EntitySchemaEmitter NewEmitter() =>
        new(Cs2SchemaTracker.Host.SchemaFamily.Version, BuildId, Platform, SourceRevision);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ese-ebw-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static SchemaClass NewClass(string name, string module = "!GlobalTypes", ulong size = 8) =>
        new() { Name = name, Module = module, Size = size };

    private static SchemaField BuiltinField(string name, string builtin, long offset = 0) =>
        new()
        {
            Name = name,
            Offset = offset,
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = builtin },
        };

    private static SchemaField DeclaredClassField(string name, string className, string module = "!GlobalTypes") =>
        new()
        {
            Name = name,
            Offset = 0,
            TypeModule = module,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.DeclaredClass,
                Name = className,
                Module = module,
            },
        };

    /// <summary>Emit the fixture through the real pipeline and parse the artifact back.</summary>
    private static Cs2SchemaTracker.Schemas.EntitySchema EmitAndParse(EntitySchemaWalk walk)
    {
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(new WalkerOutput { Platform = Platform, EntitySchema = walk }, outPath);
            return Google.Protobuf.JsonParser.Default
                .Parse<Cs2SchemaTracker.Schemas.EntitySchema>(File.ReadAllText(outPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    private static SchemaClass Emitted(Cs2SchemaTracker.Schemas.EntitySchema doc, string name) =>
        doc.Classes.Single(c => c.Name == name);

    // ---- Resolving cases ---------------------------------------------------------------

    [Xunit.Fact]
    public void FixedArray_Wrapper_Resolves_Through_Struct_Hop()
    {
        // The issue #10 motivating case: CInButtonState { uint64 m_pButtonStates[3] }.
        var walk = new EntitySchemaWalk();
        var cls = NewClass("CInButtonState", size: 32);
        cls.Fields.Add(new SchemaField
        {
            Name = "m_pButtonStates",
            Offset = 8,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "uint64[3]",
                Count = 3,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "uint64" },
            },
        });
        walk.Classes.Add(cls);

        var fact = Emitted(EmitAndParse(walk), "CInButtonState").EffectiveBuiltin;
        Xunit.Assert.NotNull(fact);
        Xunit.Assert.Equal("uint64", fact!.Builtin);
        Xunit.Assert.Equal(8u, fact.ElementWidth);
        Xunit.Assert.Equal(3ul, fact.ElementCount);
    }

    [Xunit.Fact]
    public void Bare_Scalar_Wrapper_Resolves()
    {
        // GameTime_t { float32 m_Value } — the strong-typedef shape.
        var walk = new EntitySchemaWalk();
        var cls = NewClass("GameTime_t", size: 4);
        cls.Fields.Add(BuiltinField("m_Value", "float32"));
        walk.Classes.Add(cls);

        var fact = Emitted(EmitAndParse(walk), "GameTime_t").EffectiveBuiltin;
        Xunit.Assert.NotNull(fact);
        Xunit.Assert.Equal("float32", fact!.Builtin);
        Xunit.Assert.Equal(4u, fact.ElementWidth);
        Xunit.Assert.Equal(1ul, fact.ElementCount);
    }

    [Xunit.Fact]
    public void Nested_Wrappers_Resolve_Through_Multiple_Hops()
    {
        // CPlayerControllerComponent -> CNetworkVarChainer -> ChangeAccessorFieldPathIndex_t -> int32,
        // the deepest by-value chain observed in the committed corpus.
        var walk = new EntitySchemaWalk();
        var leaf = NewClass("ChangeAccessorFieldPathIndex_t", size: 4);
        leaf.Fields.Add(BuiltinField("m_Value", "int32"));
        var mid = NewClass("CNetworkVarChainer", size: 40);
        mid.Fields.Add(DeclaredClassField("m_PathIndex", "ChangeAccessorFieldPathIndex_t"));
        var outer = NewClass("CPlayerControllerComponent", size: 40);
        outer.Fields.Add(DeclaredClassField("__m_pChainEntity", "CNetworkVarChainer"));
        walk.Classes.Add(leaf);
        walk.Classes.Add(mid);
        walk.Classes.Add(outer);

        var doc = EmitAndParse(walk);
        foreach (var name in new[]
                 { "ChangeAccessorFieldPathIndex_t", "CNetworkVarChainer", "CPlayerControllerComponent" })
        {
            var fact = Emitted(doc, name).EffectiveBuiltin;
            Xunit.Assert.NotNull(fact);
            Xunit.Assert.Equal("int32", fact!.Builtin);
            Xunit.Assert.Equal(4u, fact.ElementWidth);
            Xunit.Assert.Equal(1ul, fact.ElementCount);
        }
    }

    [Xunit.Fact]
    public void Parent_Chain_Field_Resolves()
    {
        // CAnimCycle : CCycleBase { float32 m_flCycle } — no own fields, one inherited leaf.
        var walk = new EntitySchemaWalk();
        var baseCls = NewClass("CCycleBase", module: "animlib", size: 4);
        baseCls.Fields.Add(BuiltinField("m_flCycle", "float32"));
        var derived = NewClass("CAnimCycle", module: "animlib", size: 4);
        derived.Parents.Add(new SchemaClassParent { Name = "CCycleBase", Module = "animlib", Offset = 0 });
        walk.Classes.Add(baseCls);
        walk.Classes.Add(derived);

        var fact = Emitted(EmitAndParse(walk), "CAnimCycle").EffectiveBuiltin;
        Xunit.Assert.NotNull(fact);
        Xunit.Assert.Equal("float32", fact!.Builtin);
    }

    [Xunit.Fact]
    public void FixedArray_Counts_Multiply_Across_Hops()
    {
        // Outer { Inner m_inner[2] }, Inner { uint16 m_v[4] } => uint16 x 8.
        var walk = new EntitySchemaWalk();
        var inner = NewClass("CInner", size: 8);
        inner.Fields.Add(new SchemaField
        {
            Name = "m_v",
            Offset = 0,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "uint16[4]",
                Count = 4,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "uint16" },
            },
        });
        var outer = NewClass("COuter", size: 16);
        outer.Fields.Add(new SchemaField
        {
            Name = "m_inner",
            Offset = 0,
            TypeModule = "!GlobalTypes",
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "CInner[2]",
                Count = 2,
                Inner = new SchemaType
                {
                    Category = SchemaType.Types.Category.DeclaredClass,
                    Name = "CInner",
                    Module = "!GlobalTypes",
                },
            },
        });
        walk.Classes.Add(inner);
        walk.Classes.Add(outer);

        var fact = Emitted(EmitAndParse(walk), "COuter").EffectiveBuiltin;
        Xunit.Assert.NotNull(fact);
        Xunit.Assert.Equal("uint16", fact!.Builtin);
        Xunit.Assert.Equal(2u, fact.ElementWidth);
        Xunit.Assert.Equal(8ul, fact.ElementCount);
    }

    // ---- Deliberately unresolved cases ---------------------------------------------------

    [Xunit.Fact]
    public void Excluded_Shapes_Stay_Unset()
    {
        var walk = new EntitySchemaWalk();

        // Multi-member: two builtin leaves.
        var multi = NewClass("CMulti");
        multi.Fields.Add(BuiltinField("m_a", "int32"));
        multi.Fields.Add(BuiltinField("m_b", "int32", offset: 4));
        walk.Classes.Add(multi);

        // ATOMIC leaf.
        var atomic = NewClass("CAtomicLeaf");
        atomic.Fields.Add(new SchemaField
        {
            Name = "m_str",
            Offset = 0,
            Type = new SchemaType { Category = SchemaType.Types.Category.Atomic, Name = "CUtlString" },
        });
        walk.Classes.Add(atomic);

        // PTR leaf — a pointer is a reference, not an embedding.
        var ptr = NewClass("CPtrLeaf");
        ptr.Fields.Add(new SchemaField
        {
            Name = "m_p",
            Offset = 0,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.Ptr,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "uint64" },
            },
        });
        walk.Classes.Add(ptr);

        // DECLARED_ENUM leaf — enum widths are SchemaEnum.size, a separate fact.
        var enumWrap = NewClass("CEnumLeaf");
        enumWrap.Fields.Add(new SchemaField
        {
            Name = "m_e",
            Offset = 0,
            TypeModule = "server",
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.DeclaredEnum,
                Name = "MoveType_t",
                Module = "server",
            },
        });
        walk.Classes.Add(enumWrap);

        // By-value target absent from the walk.
        var missing = NewClass("CMissingTarget");
        missing.Fields.Add(DeclaredClassField("m_gone", "CNotWalked"));
        walk.Classes.Add(missing);

        // Zero-count fixed array.
        var zeroArr = NewClass("CZeroArray");
        zeroArr.Fields.Add(new SchemaField
        {
            Name = "m_z",
            Offset = 0,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "uint8[0]",
                Count = 0,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "uint8" },
            },
        });
        walk.Classes.Add(zeroArr);

        // Unknown builtin name (closed-universe drift) — degrades to unset, never a guess.
        var unknown = NewClass("CUnknownBuiltin");
        unknown.Fields.Add(BuiltinField("m_wide", "int128"));
        walk.Classes.Add(unknown);

        // Zero fields anywhere: no leaf to resolve.
        walk.Classes.Add(NewClass("CEmpty"));

        // By-value cycle (cannot occur in real C++ layout; must not hang or resolve).
        var cycleA = NewClass("CCycleA");
        cycleA.Fields.Add(DeclaredClassField("m_b", "CCycleB"));
        var cycleB = NewClass("CCycleB");
        cycleB.Fields.Add(DeclaredClassField("m_a", "CCycleA"));
        walk.Classes.Add(cycleA);
        walk.Classes.Add(cycleB);

        var doc = EmitAndParse(walk);
        foreach (var name in new[]
                 {
                     "CMulti", "CAtomicLeaf", "CPtrLeaf", "CEnumLeaf", "CMissingTarget",
                     "CZeroArray", "CUnknownBuiltin", "CEmpty", "CCycleA", "CCycleB",
                 })
        {
            Xunit.Assert.Null(Emitted(doc, name).EffectiveBuiltin);
        }
    }

    [Xunit.Fact]
    public void Consumer_Class_With_Other_Fields_Stays_Unset_While_Wrapper_Resolves()
    {
        // The consumer side of the motivating case: CPlayer_MovementServices embeds
        // CInButtonState by value but has other members, so IT stays unset — the fact lives
        // on the wrapper class the field's DECLARED_CLASS ref points at.
        var walk = new EntitySchemaWalk();
        var wrapper = NewClass("CInButtonState", size: 32);
        wrapper.Fields.Add(new SchemaField
        {
            Name = "m_pButtonStates",
            Offset = 8,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "uint64[3]",
                Count = 3,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "uint64" },
            },
        });
        var services = NewClass("CPlayer_MovementServices", module: "server", size: 256);
        services.Fields.Add(DeclaredClassField("m_nButtons", "CInButtonState"));
        services.Fields.Add(BuiltinField("m_nQueuedButtonDownMask", "uint64", offset: 88));
        walk.Classes.Add(wrapper);
        walk.Classes.Add(services);

        var doc = EmitAndParse(walk);
        Xunit.Assert.NotNull(Emitted(doc, "CInButtonState").EffectiveBuiltin);
        Xunit.Assert.Null(Emitted(doc, "CPlayer_MovementServices").EffectiveBuiltin);
    }

    // ---- Emitted JSON shape --------------------------------------------------------------

    [Xunit.Fact]
    public void Emitted_Json_Carries_The_Fact_In_Proto3_Shape()
    {
        // The concrete shape consumers read: camelCase keys, uint32 width as a JSON number,
        // uint64 count as a proto3 JSON string; absent (not null) on unresolved classes.
        var walk = new EntitySchemaWalk();
        var cls = NewClass("CInButtonState", size: 32);
        cls.Fields.Add(new SchemaField
        {
            Name = "m_pButtonStates",
            Offset = 8,
            Type = new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "uint64[3]",
                Count = 3,
                Inner = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "uint64" },
            },
        });
        var plain = NewClass("CPlain");
        plain.Fields.Add(BuiltinField("m_a", "int32"));
        plain.Fields.Add(BuiltinField("m_b", "bool", offset: 4));
        walk.Classes.Add(cls);
        walk.Classes.Add(plain);

        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "entity_schema.json");
            NewEmitter().Emit(new WalkerOutput { Platform = Platform, EntitySchema = walk }, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var classes = doc.RootElement.GetProperty("classes");

            var wrapper = classes.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "CInButtonState");
            var fact = wrapper.GetProperty("effectiveBuiltin");
            Xunit.Assert.Equal("uint64", fact.GetProperty("builtin").GetString());
            Xunit.Assert.Equal(8, fact.GetProperty("elementWidth").GetInt32());
            Xunit.Assert.Equal("3", fact.GetProperty("elementCount").GetString());

            var unresolved = classes.EnumerateArray().Single(c => c.GetProperty("name").GetString() == "CPlain");
            Xunit.Assert.False(
                unresolved.TryGetProperty("effectiveBuiltin", out _),
                "unresolved classes must omit effectiveBuiltin entirely (absent, not null/empty)");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
