// Type-expression nodes for the point_script.d.ts parser, and their canonical rendering.
//
// The artifact carries type expressions as text, but as text RE-RENDERED from a parse rather than
// sliced from the source, so spacing and separator choices in Valve's file (`{a:T,b:U}` against
// `{ a: T; b: U }`) never leak into the output or show up as spurious changes between builds.
// One rendering rule per construct:
//
//   union          A | B            intersection   A & B
//   array          T[]              parenthesised  (T)
//   generic        Name<A, B>       tuple          [A, B]
//   function       (a: T, b?: U) => R     with type params: <T>(x: T) => T
//   object         { a: T, b?: U, m(x: T): R }      empty: {}
//   operators      keyof T / typeof x / readonly T[] / unique symbol
//   indexed        T["k"]
//   literals       "text" (double-quoted), numbers as written, keywords as written

using System.Text;

namespace Cs2SchemaTracker.Host.CsScript;

internal abstract record TsTypeNode
{
    public abstract void Render(StringBuilder sb);

    public string Render()
    {
        var sb = new StringBuilder();
        Render(sb);
        return sb.ToString();
    }
}

internal sealed record TsRefType(string Name, IReadOnlyList<TsTypeNode> Args) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        sb.Append(Name);
        if (Args.Count > 0)
        {
            sb.Append('<');
            Join(sb, Args, ", ");
            sb.Append('>');
        }
    }

    internal static void Join(StringBuilder sb, IReadOnlyList<TsTypeNode> nodes, string sep)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
                sb.Append(sep);
            nodes[i].Render(sb);
        }
    }
}

internal sealed record TsLiteralType(string Text) : TsTypeNode
{
    public override void Render(StringBuilder sb) => sb.Append(Text);

    public static TsLiteralType FromString(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return new TsLiteralType(sb.ToString());
    }
}

internal sealed record TsUnionType(IReadOnlyList<TsTypeNode> Members) : TsTypeNode
{
    public override void Render(StringBuilder sb) => TsRefType.Join(sb, Members, " | ");
}

internal sealed record TsIntersectionType(IReadOnlyList<TsTypeNode> Members) : TsTypeNode
{
    public override void Render(StringBuilder sb) => TsRefType.Join(sb, Members, " & ");
}

internal sealed record TsArrayType(TsTypeNode Element) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        Element.Render(sb);
        sb.Append("[]");
    }
}

internal sealed record TsIndexedType(TsTypeNode Object, TsTypeNode Index) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        Object.Render(sb);
        sb.Append('[');
        Index.Render(sb);
        sb.Append(']');
    }
}

internal sealed record TsParenType(TsTypeNode Inner) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        sb.Append('(');
        Inner.Render(sb);
        sb.Append(')');
    }
}

internal sealed record TsTupleType(IReadOnlyList<TsTypeNode> Elements) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        sb.Append('[');
        TsRefType.Join(sb, Elements, ", ");
        sb.Append(']');
    }
}

internal sealed record TsOperatorType(string Operator, TsTypeNode Operand) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        sb.Append(Operator).Append(' ');
        Operand.Render(sb);
    }
}

internal sealed record TsParam(string Name, TsTypeNode? Type, bool Optional, bool Rest)
{
    public void Render(StringBuilder sb)
    {
        if (Rest)
            sb.Append("...");
        sb.Append(Name);
        if (Optional)
            sb.Append('?');
        if (Type is not null)
        {
            sb.Append(": ");
            Type.Render(sb);
        }
    }

    public static void RenderList(StringBuilder sb, IReadOnlyList<TsParam> ps)
    {
        sb.Append('(');
        for (int i = 0; i < ps.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            ps[i].Render(sb);
        }
        sb.Append(')');
    }
}

internal sealed record TsFunctionType(
    IReadOnlyList<string> TypeParams, IReadOnlyList<TsParam> Params, TsTypeNode Return, bool IsConstructor)
    : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        if (IsConstructor)
            sb.Append("new ");
        TsMemberSig.RenderTypeParams(sb, TypeParams);
        TsParam.RenderList(sb, Params);
        sb.Append(" => ");
        Return.Render(sb);
    }
}

internal sealed record TsObjectType(IReadOnlyList<TsMemberSig> Members) : TsTypeNode
{
    public override void Render(StringBuilder sb)
    {
        if (Members.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        sb.Append("{ ");
        for (int i = 0; i < Members.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            Members[i].Render(sb);
        }
        sb.Append(" }");
    }
}

/// <summary>
/// One member signature — of an interface, a class, or an inline object type. A method has
/// <see cref="Params"/> (possibly empty) and an optional <see cref="ReturnType"/>; a property has
/// <see cref="PropertyType"/> and null <see cref="Params"/>.
/// </summary>
internal sealed record TsMemberSig(
    string Name,
    bool Optional,
    IReadOnlyList<string> Modifiers,
    IReadOnlyList<string> TypeParams,
    IReadOnlyList<TsParam>? Params,
    TsTypeNode? ReturnType,
    TsTypeNode? PropertyType)
{
    public bool IsMethod => Params is not null;

    public void Render(StringBuilder sb)
    {
        foreach (var m in Modifiers)
            sb.Append(m).Append(' ');
        sb.Append(Name);
        if (Optional)
            sb.Append('?');
        if (IsMethod)
        {
            RenderTypeParams(sb, TypeParams);
            TsParam.RenderList(sb, Params!);
            if (ReturnType is not null)
            {
                sb.Append(": ");
                ReturnType.Render(sb);
            }
        }
        else if (PropertyType is not null)
        {
            sb.Append(": ");
            PropertyType.Render(sb);
        }
    }

    public string Render()
    {
        var sb = new StringBuilder();
        Render(sb);
        return sb.ToString();
    }

    public static void RenderTypeParams(StringBuilder sb, IReadOnlyList<string> typeParams)
    {
        if (typeParams.Count == 0)
            return;
        sb.Append('<').Append(string.Join(", ", typeParams)).Append('>');
    }
}
