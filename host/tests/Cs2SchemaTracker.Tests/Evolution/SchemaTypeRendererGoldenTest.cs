// Golden fixtures that FREEZE the <renderedType> grammar (host SchemaTypeRenderer).
//
// SchemaTypeRenderer is the type-equality oracle for the whole schema-evolution artifact: a change
// in its output silently re-classifies fields as type-changed/unchanged across every transition. So
// every category and nesting combination is asserted here against an EXACT expected string literal.
// Any edit that moves the rendering of any shape fails loudly here instead of rewriting published
// history.
//
// The fixtures use REAL Valve shapes observed in committed entity_schema.json (see the renderer file
// header) so the grammar is pinned against reality, not an invented dialect — including the two real
// motivating specimens (CUtlVector -> CUtlLeanVector and CUtlString -> CGlobalSymbol) whose only
// distinguishing signal is the ATOMIC `name`.

using System.Globalization;

using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class SchemaTypeRendererGoldenTest
{
    private static SchemaType Builtin(string name) =>
        new() { Category = SchemaType.Types.Category.Builtin, Name = name };

    private static SchemaType Class(string module, string name) =>
        new() { Category = SchemaType.Types.Category.DeclaredClass, Module = module, Name = name };

    private static SchemaType Enum(string module, string name) =>
        new() { Category = SchemaType.Types.Category.DeclaredEnum, Module = module, Name = name };

    // ---- the pinned golden table -----------------------------------------------------------

    public static TheoryData<SchemaType, string> Golden() => new()
    {
        // BUILTIN — module never emitted.
        { Builtin("int32"), "B:int32" },
        { Builtin("bool"),  "B:bool" },

        // DECLARED_CLASS / DECLARED_ENUM — module prefix when present; none when empty.
        { Class("server.dll", "CBaseEntity"),          "C:server.dll/CBaseEntity" },
        { Class("", "CFoo"),                            "C:CFoo" },
        { Enum("!GlobalTypes", "ObjectTypeFlags_t"),    "E:!GlobalTypes/ObjectTypeFlags_t" },

        // ATOMIC — no inner (name is the whole signal).
        { new SchemaType { Category = SchemaType.Types.Category.Atomic, Name = "VectorWS" }, "A:VectorWS" },
        { new SchemaType { Category = SchemaType.Types.Category.Atomic, Name = "CUtlString" }, "A:CUtlString" },
        { new SchemaType { Category = SchemaType.Types.Category.Atomic, Name = "CGlobalSymbol" }, "A:CGlobalSymbol" },

        // PTR — real shape: name carries the '*', inner carries the (moduled) pointee.
        {
            new SchemaType
            {
                Category = SchemaType.Types.Category.Ptr,
                Name = "CBaseModelEntity*",
                Inner = Class("server.dll", "CBaseModelEntity"),
            },
            "P:CBaseModelEntity*<C:server.dll/CBaseModelEntity>"
        },

        // FIXED_ARRAY — real shape: name carries "[10]", inner carries the element type. '[' ']' are
        // NOT grammar delimiters, so they stay verbatim.
        {
            new SchemaType
            {
                Category = SchemaType.Types.Category.FixedArray,
                Name = "CPoseHandle[10]",
                Count = 10,
                Inner = Class("!GlobalTypes", "CPoseHandle"),
            },
            "FA:CPoseHandle[10]<C:!GlobalTypes/CPoseHandle>"
        },

        // BITFIELD — name "bitfield:1"; the ':' inside the name is a reserved delimiter -> escaped.
        { new SchemaType { Category = SchemaType.Types.Category.Bitfield, Name = "bitfield:1", Count = 1 }, "BF:bitfield\\:1" },

        // ATOMIC container change — the real CBaseConstraint::m_slaves specimen. Both are ATOMIC with
        // an IDENTICAL structured inner; only `name` differs. The name-centric grammar catches it;
        // a structure-only grammar would MISS it. Reserved '<' '>' inside the name are escaped, while
        // the structural inner group uses UNescaped '<' '>'.
        {
            new SchemaType
            {
                Category = SchemaType.Types.Category.Atomic,
                Name = "CUtlVector< CConstraintSlave >",
                Inner = Class("!GlobalTypes", "CConstraintSlave"),
            },
            "A:CUtlVector\\< CConstraintSlave \\><C:!GlobalTypes/CConstraintSlave>"
        },
        {
            new SchemaType
            {
                Category = SchemaType.Types.Category.Atomic,
                Name = "CUtlLeanVector< CConstraintSlave >",
                Inner = Class("!GlobalTypes", "CConstraintSlave"),
            },
            "A:CUtlLeanVector\\< CConstraintSlave \\><C:!GlobalTypes/CConstraintSlave>"
        },

        // Nested template (synthetic, spelling simplified) — proves inner recursion.
        {
            new SchemaType
            {
                Category = SchemaType.Types.Category.Atomic,
                Name = "CUtlVector",
                Inner = new SchemaType
                {
                    Category = SchemaType.Types.Category.Atomic,
                    Name = "CHandle",
                    Inner = Class("client", "C_BaseEntity"),
                },
            },
            "A:CUtlVector<A:CHandle<C:client/C_BaseEntity>>"
        },

        // Two inner slots -> comma-separated inside the structural group.
        {
            new SchemaType
            {
                Category = SchemaType.Types.Category.Atomic,
                Name = "CUtlPair",
                Inner = Builtin("int32"),
                Inner2 = Builtin("float32"),
            },
            "A:CUtlPair<B:int32,B:float32>"
        },

        // Escaping proofs.
        { Class("client", "CFoo::CBar"), "C:client/CFoo\\:\\:CBar" },       // nested-name '::'
        { Builtin("a/b,c"),              "B:a\\/b\\,c" },                    // '/' and ',' in a name
        { Builtin("a\\b"),               "B:a\\\\b" },                       // backslash escaped first
    };

    [Theory]
    [MemberData(nameof(Golden))]
    public void Renders_to_exact_pinned_string(SchemaType type, string expected)
        => Assert.Equal(expected, SchemaTypeRenderer.Render(type));

    // ---- fail-loud corners -----------------------------------------------------------------

    [Fact]
    public void Null_type_throws()
        => Assert.Throws<ArgumentNullException>(() => SchemaTypeRenderer.Render(null!));

    [Fact]
    public void Unspecified_category_throws()
        => Assert.Throws<InvalidDataException>(() =>
            SchemaTypeRenderer.Render(new SchemaType { Name = "x" })); // Category defaults to UNSPECIFIED

    [Fact]
    public void Inner_gap_throws()
    {
        // inner2 set while inner is unset — a walker-corruption signal, not a silently-droppable slot.
        var t = new SchemaType
        {
            Category = SchemaType.Types.Category.Atomic,
            Name = "Bad",
            Inner2 = Builtin("int32"),
        };
        Assert.Throws<InvalidDataException>(() => SchemaTypeRenderer.Render(t));
    }

    // ---- determinism: no culture-sensitive formatting --------------------------------------

    [Fact]
    public void Is_culture_invariant()
    {
        // The renderer emits only fixed literals + verbatim/escaped identifier text (count is never
        // numerically formatted), so a non-invariant thread culture must not change the output.
        var type = new SchemaType
        {
            Category = SchemaType.Types.Category.FixedArray,
            Name = "CPoseHandle[10]",
            Count = 10,
            Inner = Class("!GlobalTypes", "CPoseHandle"),
        };
        const string expected = "FA:CPoseHandle[10]<C:!GlobalTypes/CPoseHandle>";

        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, SchemaTypeRenderer.Render(type));
        }
        finally { CultureInfo.CurrentCulture = prior; }
    }
}
