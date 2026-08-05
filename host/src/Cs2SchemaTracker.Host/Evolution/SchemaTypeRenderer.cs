// The pinned <renderedType> grammar — the type-equality ORACLE for schema evolution.
//
// A single deterministic function that flattens a recursive SchemaType graph (entity_schema.proto)
// to one canonical string. It is used ONLY internally (never published — the artifact carries
// structured SchemaType) as the equality test behind:
//   - TYPE_CHANGE detection  (render(oldType) != render(newType) <=> the field's type changed)
//   - a field's type_history (a new entry is recorded only when the rendered type differs)
//   - the `typeMatch` paired-evidence signal
//
// WHY THIS IS PINNED + GOLDEN-TESTED (SchemaTypeRendererGoldenTest)
// ----------------------------------------------------------------
// This function is the equality oracle for the ENTIRE evolution artifact. A one-character shift in
// its output silently re-classifies every field of the affected shape across all transitions —
// fabricating or dropping thousands of TYPE_CHANGE ops, with no exception thrown. So the grammar is
// frozen here and asserted by exact-string golden fixtures; any edit that moves the output fails a
// test loudly instead of rewriting published history.
//
// GROUNDED IN REAL DATA (not an invented grammar). Inspecting committed entity_schema.json shows the
// SchemaType.name field already carries the FULL type spelling:
//   BUILTIN        name "bool"
//   ATOMIC         name "VectorWS"  |  "CUtlVector< CConstraintSlave >" (+ structured inner)
//   DECLARED_CLASS name "CBaseEntity"          module "server.dll"
//   DECLARED_ENUM  name "ObjectTypeFlags_t"    module "!GlobalTypes"
//   FIXED_ARRAY    name "CPoseHandle[10]"      count 10  inner = element type
//   PTR            name "CBaseModelEntity*"    inner = pointee type
//   BITFIELD       name "bitfield:1"           count = bit width
// The name is therefore load-bearing: CUtlVector<T> and CUtlLeanVector<T> are BOTH ATOMIC with an
// identical structured inner and differ ONLY in `name`. A real, observed change
// (CBaseConstraint::m_slaves: CUtlVector -> CUtlLeanVector) would be MISSED by a structure-only
// render that dropped the name. Hence this grammar is NAME-CENTRIC: category tag + module + escaped
// name + recursed inners. The inners are recursed additionally so a module change on an inner
// declared type (which `name` does not spell out) is still caught.
//
// GRAMMAR (pinned)
//   render(t) = <tag> ":" [ esc(module) "/" ] esc(name) [ "<" render(inner){","render(innerN)} ">" ]
//   tag:  BUILTIN=B  ATOMIC=A  DECLARED_CLASS=C  DECLARED_ENUM=E  PTR=P  FIXED_ARRAY=FA  BITFIELD=BF
//   - module prefix emitted only when module != "" (declared types); "<...>" only when >=1 inner.
//   - inners rendered in order inner, inner2, inner3, skipping only a contiguous unset tail; a set
//     slot after an unset earlier slot is a walker-corruption fail-loud.
//   - count is NOT rendered separately: `name` already encodes it ("[10]", "bitfield:1"), so adding
//     it would be redundant; the structured inner supplies the module `name` omits.
//   - CATEGORY_UNSPECIFIED / a null type is a data defect -> fail loud (never occurs in committed
//     data: the only categories present are the seven tagged above).
//
// ESCAPING. Only the delimiters this grammar uses are escaped, so a name that legitimately contains
// one cannot be confused with structure. Reserved = { '\\', ':', '/', '<', '>', ',' } (backslash
// first). Chars that appear in real names but are NOT delimiters here ('[', ']', '#', '*', ' ') are
// left verbatim. Example: a nested name "CFoo::CBar" -> "CFoo\:\:CBar".
//
// Deterministic: pure function, no culture-sensitive formatting (only fixed literals + verbatim/
// escaped identifier text). Re-rendering the same SchemaType is byte-identical.

using System.Text;

using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Evolution;

/// <summary>
/// Renders a <see cref="SchemaType"/> to its pinned canonical string — the type-equality oracle for
/// schema evolution. See file header for the grammar and why it is frozen + golden-tested. Pure,
/// deterministic, culture-independent.
/// </summary>
public static class SchemaTypeRenderer
{
    // The delimiters the grammar uses as structure. A name/module containing any of these is
    // backslash-escaped so it can never be mistaken for a delimiter. Backslash itself is reserved
    // (it is the escape character) and MUST be handled first in Escape.
    private static readonly char[] Reserved = { '\\', ':', '/', '<', '>', ',' };

    /// <summary>
    /// Render <paramref name="type"/> to its canonical string. Throws <see cref="InvalidDataException"/>
    /// on a null type, an unspecified category, or a walker-corrupt inner gap (a set inner slot after
    /// an unset earlier one) — all data defects, never silently rendered.
    /// </summary>
    public static string Render(SchemaType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var sb = new StringBuilder();
        Append(sb, type);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, SchemaType type)
    {
        if (type is null)
        {
            throw new InvalidDataException(
                "SchemaTypeRenderer: a SchemaType node is null (corrupt schema graph).");
        }

        sb.Append(Tag(type.Category));
        sb.Append(':');
        if (!string.IsNullOrEmpty(type.Module))
        {
            Escape(sb, type.Module);
            sb.Append('/');
        }
        Escape(sb, type.Name ?? "");

        AppendInners(sb, type);
    }

    private static void AppendInners(StringBuilder sb, SchemaType type)
    {
        // Contiguous-from-the-front presence only. A gap (e.g. inner2 set while inner is unset) is a
        // walker defect: fail loud rather than silently drop a slot and mis-render the type.
        var i1 = type.Inner;
        var i2 = type.Inner2;
        var i3 = type.Inner3;

        if (i1 is null && (i2 is not null || i3 is not null))
        {
            throw new InvalidDataException(
                $"SchemaTypeRenderer: SchemaType '{type.Name}' has an inner gap (inner unset but a " +
                "later inner slot set) — corrupt schema graph.");
        }
        if (i2 is null && i3 is not null)
        {
            throw new InvalidDataException(
                $"SchemaTypeRenderer: SchemaType '{type.Name}' has an inner gap (inner2 unset but " +
                "inner3 set) — corrupt schema graph.");
        }

        if (i1 is null)
            return;

        sb.Append('<');
        Append(sb, i1);
        if (i2 is not null)
        {
            sb.Append(',');
            Append(sb, i2);
        }
        if (i3 is not null)
        {
            sb.Append(',');
            Append(sb, i3);
        }
        sb.Append('>');
    }

    /// <summary>The fixed one/two-char category tag. Fail-loud on an unspecified/unknown category.</summary>
    private static string Tag(SchemaType.Types.Category category) => category switch
    {
        SchemaType.Types.Category.Builtin => "B",
        SchemaType.Types.Category.Atomic => "A",
        SchemaType.Types.Category.DeclaredClass => "C",
        SchemaType.Types.Category.DeclaredEnum => "E",
        SchemaType.Types.Category.Ptr => "P",
        SchemaType.Types.Category.FixedArray => "FA",
        SchemaType.Types.Category.Bitfield => "BF",
        _ => throw new InvalidDataException(
            $"SchemaTypeRenderer: unrenderable SchemaType category '{category}' (data defect — the " +
            "only categories present in committed schemas are BUILTIN/ATOMIC/DECLARED_CLASS/" +
            "DECLARED_ENUM/PTR/FIXED_ARRAY/BITFIELD)."),
    };

    /// <summary>Append <paramref name="text"/>, backslash-escaping each reserved delimiter char.</summary>
    private static void Escape(StringBuilder sb, string text)
    {
        foreach (var ch in text)
        {
            if (Array.IndexOf(Reserved, ch) >= 0)
                sb.Append('\\');
            sb.Append(ch);
        }
    }
}
