// Recursive-descent parser for the TypeScript declaration subset point_script.d.ts uses.
//
// Supported, because Valve's file uses it:
//   - one `declare module "name" { ... }` block per file (anything else at top level fails);
//   - inside it: `export const A: T;` (comma-separated declarators allowed), `type A<T> = T;`,
//     `interface A<T> extends B, C { ... }`, `[abstract] class A<T> extends B implements C { ... }`,
//     `[const] enum A { ... }`, each optionally `export` / `declare`;
//   - members: methods (generics, optional `?`, rest and optional params, overloads by repetition)
//     and properties, with readonly/static/public/... modifiers;
//   - types: references with generic arguments, unions and intersections (leading `|` / `&`
//     allowed), arrays, indexed access, parentheses, function and constructor types, inline object
//     types, tuples, string/number/keyword literals, keyof / typeof / readonly / unique.
//
// Everything else — index signatures, call signatures, accessors, template literal types,
// conditional and mapped types, function / namespace declarations, string enums — is rejected with
// file:line, because silently skipping an API declaration is the one outcome this artifact must not
// have. When Valve's file outgrows the subset the emit stops and the parser gets extended.
//
// JSDoc: a block attaches to the declaration or member that directly follows it (all blocks between
// the previous token and that declaration, merged in order). A block that lands anywhere else —
// mid-expression — is not attached to anything.

using System.Globalization;
using System.Text;

namespace Cs2SchemaTracker.Host.CsScript;

internal sealed record ParsedExport(string Name, TsTypeNode Type, JsDoc Doc, IReadOnlyCollection<string> Refs);

internal sealed record ParsedMember(TsMemberSig Sig, JsDoc Doc, IReadOnlyCollection<string> Refs);

internal sealed record ParsedType(
    string Name,
    string Kind,
    bool Exported,
    IReadOnlyList<string> Extends,
    IReadOnlyList<string> Implements,
    IReadOnlyList<string> TypeParams,
    JsDoc Doc,
    TsTypeNode? AliasType,
    IReadOnlyCollection<string> AliasRefs,
    IReadOnlyList<ParsedMember> Members,
    int Line);

internal sealed record ParsedEnumMember(string Name, int Value, string InitializerText, JsDoc Doc);

internal sealed record ParsedEnum(string Name, bool Exported, JsDoc Doc, IReadOnlyList<ParsedEnumMember> Members, int Line);

internal sealed record ParsedModule(
    string Name,
    JsDoc ModuleDoc,
    JsDoc FileDoc,
    IReadOnlyList<ParsedExport> Exports,
    IReadOnlyList<ParsedType> Types,
    IReadOnlyList<ParsedEnum> Enums);

internal sealed class TsDeclarationParser
{
    private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
    {
        "readonly", "static", "public", "private", "protected", "abstract", "declare", "override",
    };

    private readonly List<TsToken> _tokens;
    private readonly string _sourceName;
    private readonly List<JsDoc> _pendingDocs = new();
    private int _pos;
    private HashSet<string>? _refs;

    private TsDeclarationParser(List<TsToken> tokens, string sourceName)
    {
        _tokens = tokens;
        _sourceName = sourceName;
    }

    public static ParsedModule Parse(string source, string sourceName)
    {
        var tokens = TsLexer.Tokenize(source, sourceName);
        return new TsDeclarationParser(tokens, sourceName).ParseFile();
    }

    // ---- token plumbing -------------------------------------------------------------------

    private void SkipTrivia()
    {
        while (_tokens[_pos].Kind == TsTokenKind.JsDoc)
        {
            _pendingDocs.Add(JsDoc.Parse(_tokens[_pos].Text));
            _pos++;
        }
    }

    private TsToken Peek()
    {
        SkipTrivia();
        return _tokens[_pos];
    }

    /// <summary>The significant token <paramref name="ahead"/> positions past the current one.</summary>
    private TsToken PeekAt(int ahead)
    {
        SkipTrivia();
        int i = _pos;
        int seen = 0;
        while (true)
        {
            if (_tokens[i].Kind == TsTokenKind.End)
                return _tokens[i];
            if (_tokens[i].Kind != TsTokenKind.JsDoc)
            {
                if (seen == ahead)
                    return _tokens[i];
                seen++;
            }
            i++;
        }
    }

    private TsToken Next()
    {
        SkipTrivia();
        _pendingDocs.Clear();   // a block not claimed by TakeDocs before this token is orphaned.
        var t = _tokens[_pos];
        if (t.Kind != TsTokenKind.End)
            _pos++;
        return t;
    }

    private JsDoc TakeDocs()
    {
        SkipTrivia();
        var doc = JsDoc.Merge(_pendingDocs.ToList());
        _pendingDocs.Clear();
        return doc;
    }

    private List<JsDoc> TakeDocList()
    {
        SkipTrivia();
        var docs = _pendingDocs.ToList();
        _pendingDocs.Clear();
        return docs;
    }

    private bool TryPunct(string p)
    {
        if (Peek().IsPunct(p))
        {
            Next();
            return true;
        }
        return false;
    }

    private bool TryIdent(string id)
    {
        if (Peek().IsIdent(id))
        {
            Next();
            return true;
        }
        return false;
    }

    private void ExpectPunct(string p)
    {
        var t = Next();
        if (!t.IsPunct(p))
            throw Fail(t, $"expected '{p}' but found {Describe(t)}");
    }

    private string ExpectIdent(string what)
    {
        var t = Next();
        if (t.Kind != TsTokenKind.Identifier)
            throw Fail(t, $"expected {what} but found {Describe(t)}");
        return t.Text;
    }

    private InvalidDataException Fail(TsToken at, string message)
        => TsLexer.Error(_sourceName, at.Line, message);

    private static string Describe(TsToken t) => t.Kind switch
    {
        TsTokenKind.End => "end of file",
        TsTokenKind.String => $"string \"{t.Text}\"",
        _ => $"'{t.Text}'",
    };

    private void AddRef(string name) => _refs?.Add(name);

    private (T Value, HashSet<string> Refs) WithRefs<T>(Func<T> parse)
    {
        var saved = _refs;
        var refs = new HashSet<string>(StringComparer.Ordinal);
        _refs = refs;
        try
        {
            var v = parse();
            return (v, refs);
        }
        finally
        {
            _refs = saved;
            saved?.UnionWith(refs);
        }
    }

    // ---- file / module --------------------------------------------------------------------

    private ParsedModule ParseFile()
    {
        var docs = TakeDocList();
        var start = Next();
        if (!start.IsIdent("declare"))
            throw Fail(start, $"expected 'declare module \"...\"' at top level but found {Describe(start)}");
        var moduleKw = Next();
        if (!moduleKw.IsIdent("module"))
            throw Fail(moduleKw, $"expected 'module' after 'declare' but found {Describe(moduleKw)}");
        var nameTok = Next();
        if (nameTok.Kind != TsTokenKind.String)
            throw Fail(nameTok, $"expected the module name string but found {Describe(nameTok)}");
        ExpectPunct("{");

        var exports = new List<ParsedExport>();
        var types = new List<ParsedType>();
        var enums = new List<ParsedEnum>();
        while (true)
        {
            var doc = TakeDocs();
            var t = Peek();
            if (t.IsPunct("}"))
            {
                Next();
                break;
            }
            if (t.Kind == TsTokenKind.End)
                throw Fail(t, $"unterminated module block '{nameTok.Text}'");
            if (t.IsPunct(";"))
            {
                Next();
                continue;
            }
            ParseStatement(doc, exports, types, enums);
        }

        var trailing = Peek();
        if (trailing.Kind != TsTokenKind.End)
            throw Fail(trailing, $"unexpected {Describe(trailing)} after the module block (one module block per file is supported)");

        var moduleDoc = docs.Count > 0 ? docs[^1] : JsDoc.Empty;
        var fileDoc = docs.Count > 1 ? JsDoc.Merge(docs.Take(docs.Count - 1).ToList()) : JsDoc.Empty;
        return new ParsedModule(nameTok.Text, moduleDoc, fileDoc, exports, types, enums);
    }

    private void ParseStatement(JsDoc doc, List<ParsedExport> exports, List<ParsedType> types, List<ParsedEnum> enums)
    {
        bool exported = TryIdent("export");
        if (Peek().IsIdent("default") || Peek().IsPunct("{") || Peek().IsPunct("*") || Peek().IsPunct("="))
            throw Fail(Peek(), "export forms other than 'export <declaration>' are outside the supported subset");
        TryIdent("declare");

        var kw = Peek();
        int line = kw.Line;
        if (kw.IsIdent("const") && PeekAt(1).IsIdent("enum"))
        {
            Next();
            Next();
            enums.Add(ParseEnum(doc, exported, line));
            return;
        }
        if (kw.IsIdent("const") || kw.IsIdent("let") || kw.IsIdent("var"))
        {
            Next();
            do
            {
                string name = ExpectIdent("a constant name");
                ExpectPunct(":");
                var (type, refs) = WithRefs(ParseType);
                exports.Add(new ParsedExport(name, type, doc, refs));
            }
            while (TryPunct(","));
            TryPunct(";");
            return;
        }
        if (kw.IsIdent("type"))
        {
            Next();
            string name = ExpectIdent("a type alias name");
            var tps = ParseTypeParamsOpt();
            ExpectPunct("=");
            var (type, refs) = WithRefs(ParseType);
            TryPunct(";");
            types.Add(new ParsedType(name, "type_alias", exported, Array.Empty<string>(), Array.Empty<string>(),
                tps, doc, type, refs, Array.Empty<ParsedMember>(), line));
            return;
        }
        if (kw.IsIdent("interface"))
        {
            Next();
            string name = ExpectIdent("an interface name");
            var tps = ParseTypeParamsOpt();
            var ext = new List<string>();
            if (TryIdent("extends"))
            {
                do
                    ext.Add(ParseHeritage());
                while (TryPunct(","));
            }
            var members = ParseMemberBlock();
            types.Add(new ParsedType(name, "interface", exported, ext, Array.Empty<string>(), tps, doc, null,
                Array.Empty<string>(), members, line));
            return;
        }
        if (kw.IsIdent("abstract") && PeekAt(1).IsIdent("class"))
        {
            Next();
            kw = Peek();
        }
        if (kw.IsIdent("class"))
        {
            Next();
            string name = ExpectIdent("a class name");
            var tps = ParseTypeParamsOpt();
            var ext = new List<string>();
            var impl = new List<string>();
            if (TryIdent("extends"))
                ext.Add(ParseHeritage());
            if (TryIdent("implements"))
            {
                do
                    impl.Add(ParseHeritage());
                while (TryPunct(","));
            }
            var members = ParseMemberBlock();
            types.Add(new ParsedType(name, "class", exported, ext, impl, tps, doc, null, Array.Empty<string>(),
                members, line));
            return;
        }
        if (kw.IsIdent("enum"))
        {
            Next();
            enums.Add(ParseEnum(doc, exported, line));
            return;
        }
        throw Fail(kw, $"unsupported declaration starting with {Describe(kw)} (supported: const, type, interface, class, enum)");
    }

    /// <summary>An extends/implements target: a (possibly qualified, possibly generic) type reference.</summary>
    private string ParseHeritage()
    {
        var t = Peek();
        var node = ParsePrimary();
        if (node is not TsRefType)
            throw Fail(t, "extends/implements targets must be type references");
        return node.Render();
    }

    private List<string> ParseTypeParamsOpt()
    {
        var result = new List<string>();
        if (!Peek().IsPunct("<"))
            return result;
        Next();
        do
        {
            string name = ExpectIdent("a type parameter name");
            var sb = new StringBuilder(name);
            if (TryIdent("extends"))
            {
                sb.Append(" extends ");
                ParseType().Render(sb);
            }
            if (TryPunct("="))
            {
                sb.Append(" = ");
                ParseType().Render(sb);
            }
            result.Add(sb.ToString());
        }
        while (TryPunct(","));
        ExpectPunct(">");
        return result;
    }

    // ---- members --------------------------------------------------------------------------

    private List<ParsedMember> ParseMemberBlock()
    {
        ExpectPunct("{");
        var members = new List<ParsedMember>();
        while (true)
        {
            var doc = TakeDocs();
            var t = Peek();
            if (t.IsPunct("}"))
            {
                Next();
                return members;
            }
            if (t.Kind == TsTokenKind.End)
                throw Fail(t, "unterminated member block");
            if (t.IsPunct(";") || t.IsPunct(","))
            {
                Next();
                continue;
            }
            var (sig, refs) = WithRefs(ParseMemberSig);
            members.Add(new ParsedMember(sig, doc, refs));
        }
    }

    private TsMemberSig ParseMemberSig()
    {
        var modifiers = new List<string>();
        while (Peek().Kind == TsTokenKind.Identifier && MemberModifiers.Contains(Peek().Text) && IsMemberNameStart(PeekAt(1)))
        {
            modifiers.Add(Next().Text);
        }

        var t = Peek();
        if (t.IsPunct("["))
            throw Fail(t, "index signatures are outside the supported subset");
        if (t.IsPunct("(") || t.IsPunct("<"))
            throw Fail(t, "call signatures are outside the supported subset");
        if (t.IsIdent("new") && PeekAt(1).IsPunct("("))
            throw Fail(t, "construct signatures are outside the supported subset");
        if ((t.IsIdent("get") || t.IsIdent("set")) && IsMemberNameStart(PeekAt(1)))
            throw Fail(t, "accessors are outside the supported subset");

        var nameTok = Next();
        string name = nameTok.Kind switch
        {
            TsTokenKind.Identifier or TsTokenKind.Number => nameTok.Text,
            TsTokenKind.String => TsLiteralType.FromString(nameTok.Text).Text,
            _ => throw Fail(nameTok, $"expected a member name but found {Describe(nameTok)}"),
        };
        bool optional = TryPunct("?");

        if (Peek().IsPunct("(") || Peek().IsPunct("<"))
        {
            var tps = ParseTypeParamsOpt();
            var ps = ParseParams();
            TsTypeNode? ret = null;
            if (TryPunct(":"))
                ret = ParseType();
            TryMemberSeparator();
            return new TsMemberSig(name, optional, modifiers, tps, ps, ret, null);
        }

        TsTypeNode? propType = null;
        if (TryPunct(":"))
            propType = ParseType();
        if (Peek().IsPunct("="))
            throw Fail(Peek(), "property initializers are outside the supported subset");
        TryMemberSeparator();
        return new TsMemberSig(name, optional, modifiers, Array.Empty<string>(), null, null, propType);
    }

    private static bool IsMemberNameStart(TsToken t)
        => t.Kind is TsTokenKind.Identifier or TsTokenKind.String or TsTokenKind.Number || t.IsPunct("[");

    private void TryMemberSeparator()
    {
        if (!TryPunct(";"))
            TryPunct(",");
    }

    private List<TsParam> ParseParams()
    {
        ExpectPunct("(");
        var ps = new List<TsParam>();
        if (TryPunct(")"))
            return ps;
        while (true)
        {
            bool rest = TryPunct("...");
            var nameTok = Next();
            if (nameTok.Kind != TsTokenKind.Identifier)
                throw Fail(nameTok, $"expected a parameter name but found {Describe(nameTok)} (destructuring patterns are outside the supported subset)");
            bool optional = TryPunct("?");
            TsTypeNode? type = null;
            if (TryPunct(":"))
                type = ParseType();
            if (Peek().IsPunct("="))
                throw Fail(Peek(), "parameter initializers are outside the supported subset");
            ps.Add(new TsParam(nameTok.Text, type, optional, rest));
            if (TryPunct(","))
            {
                if (TryPunct(")"))
                    return ps;
                continue;
            }
            ExpectPunct(")");
            return ps;
        }
    }

    // ---- types ----------------------------------------------------------------------------

    private TsTypeNode ParseType()
    {
        var first = Peek();
        if (first.IsIdent("infer") || first.IsIdent("asserts") || first.IsIdent("is"))
            throw Fail(first, $"'{first.Text}' types are outside the supported subset");

        TryPunct("|");   // leading separator
        var parts = new List<TsTypeNode> { ParseIntersection() };
        while (TryPunct("|"))
            parts.Add(ParseIntersection());
        var node = parts.Count == 1 ? parts[0] : new TsUnionType(parts);

        if (Peek().IsIdent("extends"))
            throw Fail(Peek(), "conditional types are outside the supported subset");
        return node;
    }

    private TsTypeNode ParseIntersection()
    {
        TryPunct("&");
        var parts = new List<TsTypeNode> { ParsePostfix() };
        while (TryPunct("&"))
            parts.Add(ParsePostfix());
        return parts.Count == 1 ? parts[0] : new TsIntersectionType(parts);
    }

    private TsTypeNode ParsePostfix()
    {
        var node = ParsePrimary();
        while (Peek().IsPunct("["))
        {
            Next();
            if (TryPunct("]"))
            {
                node = new TsArrayType(node);
                continue;
            }
            var index = ParseType();
            ExpectPunct("]");
            node = new TsIndexedType(node, index);
        }
        return node;
    }

    private TsTypeNode ParsePrimary()
    {
        var t = Peek();

        if (t.IsPunct("("))
        {
            if (IsFunctionTypeAhead())
                return ParseFunctionType(isConstructor: false);
            Next();
            var inner = ParseType();
            ExpectPunct(")");
            return new TsParenType(inner);
        }
        if (t.IsPunct("<"))
            return ParseFunctionType(isConstructor: false);
        if (t.IsIdent("new") && (PeekAt(1).IsPunct("(") || PeekAt(1).IsPunct("<")))
        {
            Next();
            return ParseFunctionType(isConstructor: true);
        }
        if (t.IsPunct("{"))
        {
            Next();
            var members = new List<TsMemberSig>();
            while (true)
            {
                var c = Peek();
                if (c.IsPunct("}"))
                {
                    Next();
                    break;
                }
                if (c.Kind == TsTokenKind.End)
                    throw Fail(c, "unterminated object type");
                if (c.IsPunct(";") || c.IsPunct(","))
                {
                    Next();
                    continue;
                }
                members.Add(ParseMemberSig());
            }
            return new TsObjectType(members);
        }
        if (t.IsPunct("["))
        {
            Next();
            var elems = new List<TsTypeNode>();
            if (!Peek().IsPunct("]"))
            {
                do
                {
                    if (Peek().IsPunct("]"))
                        break;
                    elems.Add(ParseType());
                }
                while (TryPunct(","));
            }
            ExpectPunct("]");
            return new TsTupleType(elems);
        }
        if (t.Kind == TsTokenKind.String)
        {
            Next();
            return TsLiteralType.FromString(t.Text);
        }
        if (t.Kind == TsTokenKind.Number)
        {
            Next();
            return new TsLiteralType(t.Text);
        }
        if (t.IsPunct("-") && PeekAt(1).Kind == TsTokenKind.Number)
        {
            Next();
            return new TsLiteralType("-" + Next().Text);
        }
        if (t.Kind == TsTokenKind.Identifier)
        {
            if (t.Text is "keyof" or "typeof" or "readonly" or "unique")
            {
                Next();
                if (t.Text == "typeof")
                {
                    // typeof takes a value path, not a type: render it verbatim.
                    var path = new StringBuilder(ExpectIdent("a value name after typeof"));
                    while (TryPunct("."))
                        path.Append('.').Append(ExpectIdent("a qualified name segment"));
                    return new TsOperatorType("typeof", new TsLiteralType(path.ToString()));
                }
                return new TsOperatorType(t.Text, ParsePostfix());
            }
            Next();
            var name = new StringBuilder(t.Text);
            while (Peek().IsPunct(".") && PeekAt(1).Kind == TsTokenKind.Identifier)
            {
                Next();
                name.Append('.').Append(Next().Text);
            }
            AddRef(t.Text);
            var args = new List<TsTypeNode>();
            if (Peek().IsPunct("<"))
            {
                Next();
                do
                    args.Add(ParseType());
                while (TryPunct(","));
                ExpectPunct(">");
            }
            return new TsRefType(name.ToString(), args);
        }
        throw Fail(t, $"expected a type but found {Describe(t)}");
    }

    /// <summary>At '(' — is this a function type's parameter list (the matching ')' is followed by '=>')?</summary>
    private bool IsFunctionTypeAhead()
    {
        SkipTrivia();
        int depth = 0;
        for (int i = _pos; i < _tokens.Count; i++)
        {
            var t = _tokens[i];
            if (t.Kind == TsTokenKind.End)
                return false;
            if (t.Kind != TsTokenKind.Punct)
                continue;
            if (t.Text is "(" or "[" or "{")
                depth++;
            else if (t.Text is ")" or "]" or "}")
            {
                depth--;
                if (depth == 0)
                {
                    for (int j = i + 1; j < _tokens.Count; j++)
                    {
                        if (_tokens[j].Kind == TsTokenKind.JsDoc)
                            continue;
                        return _tokens[j].IsPunct("=>");
                    }
                    return false;
                }
            }
        }
        return false;
    }

    private TsFunctionType ParseFunctionType(bool isConstructor)
    {
        var tps = ParseTypeParamsOpt();
        var ps = ParseParams();
        ExpectPunct("=>");
        var ret = ParseType();
        return new TsFunctionType(tps, ps, ret, isConstructor);
    }

    // ---- enums ----------------------------------------------------------------------------

    private ParsedEnum ParseEnum(JsDoc doc, bool exported, int line)
    {
        string name = ExpectIdent("an enum name");
        ExpectPunct("{");
        var members = new List<ParsedEnumMember>();
        var known = new Dictionary<string, long>(StringComparer.Ordinal);
        long next = 0;
        while (true)
        {
            var mdoc = TakeDocs();
            var t = Peek();
            if (t.IsPunct("}"))
            {
                Next();
                break;
            }
            if (t.Kind == TsTokenKind.End)
                throw Fail(t, $"unterminated enum '{name}'");
            if (t.IsPunct(","))
            {
                Next();
                continue;
            }
            var nameTok = Next();
            if (nameTok.Kind is not (TsTokenKind.Identifier or TsTokenKind.String))
                throw Fail(nameTok, $"expected an enum member name in '{name}' but found {Describe(nameTok)}");
            string memberName = nameTok.Text;
            if (known.ContainsKey(memberName))
                throw Fail(nameTok, $"duplicate enum member '{name}.{memberName}'");

            long value;
            string initText = "";
            if (TryPunct("="))
            {
                var expr = new EnumExpr(this, name, known);
                (value, initText) = expr.ParseOr();
            }
            else
            {
                value = next;
            }
            if (value < int.MinValue || value > int.MaxValue)
                throw Fail(nameTok, $"enum member '{name}.{memberName}' value {value} does not fit a 32-bit integer");
            known[memberName] = value;
            next = value + 1;
            members.Add(new ParsedEnumMember(memberName, (int)value, initText, mdoc));

            var sep = Peek();
            if (sep.IsPunct(","))
            {
                Next();
                continue;
            }
            if (!sep.IsPunct("}"))
                throw Fail(sep, $"expected ',' or '}}' after enum member '{name}.{memberName}' but found {Describe(sep)}");
        }
        return new ParsedEnum(name, exported, doc, members, line);
    }

    /// <summary>
    /// Integer constant-expression evaluator for enum initializers. TypeScript precedence:
    /// | &lt; ^ &lt; &amp; &lt; shifts &lt; + - &lt; * / % &lt; unary (- + ~) &lt; primary. Bitwise and shift
    /// operators follow JavaScript's 32-bit semantics. Returns the value and the expression re-rendered
    /// with canonical spacing ("1 &lt;&lt; 6").
    /// </summary>
    private sealed class EnumExpr
    {
        private readonly TsDeclarationParser _p;
        private readonly string _enumName;
        private readonly Dictionary<string, long> _known;

        public EnumExpr(TsDeclarationParser p, string enumName, Dictionary<string, long> known)
        {
            _p = p;
            _enumName = enumName;
            _known = known;
        }

        public (long, string) ParseOr() => Binary(0);

        private static readonly string[][] Levels =
        {
            new[] { "|" },
            new[] { "^" },
            new[] { "&" },
            new[] { "<<", ">>", ">>>" },
            new[] { "+", "-" },
            new[] { "*", "/", "%" },
        };

        private (long, string) Binary(int level)
        {
            if (level == Levels.Length)
                return Unary();
            var (lv, lt) = Binary(level + 1);
            while (true)
            {
                string? op = MatchOperator(Levels[level]);
                if (op is null)
                    return (lv, lt);
                var at = _p.Peek();
                var (rv, rt) = Binary(level + 1);
                lv = Apply(op, lv, rv, at);
                lt = $"{lt} {op} {rt}";
            }
        }

        /// <summary>Match one of <paramref name="ops"/>; shifts are adjacent '&lt;' / '&gt;' tokens.</summary>
        private string? MatchOperator(string[] ops)
        {
            var t = _p.Peek();
            if (t.Kind != TsTokenKind.Punct)
                return null;
            foreach (var op in ops.OrderByDescending(o => o.Length))
            {
                if (op.Length == 1)
                {
                    if (t.Text == op)
                    {
                        _p.Next();
                        return op;
                    }
                    continue;
                }
                // Multi-char shift: the lexer emits '<' / '>' singly; require them adjacent.
                bool all = true;
                int prevEnd = -1;
                for (int k = 0; k < op.Length; k++)
                {
                    var tk = _p.PeekAt(k);
                    if (!tk.IsPunct(op[k].ToString()) || (prevEnd >= 0 && tk.Start != prevEnd))
                    {
                        all = false;
                        break;
                    }
                    prevEnd = tk.End;
                }
                if (all)
                {
                    for (int k = 0; k < op.Length; k++)
                        _p.Next();
                    return op;
                }
            }
            return null;
        }

        private (long, string) Unary()
        {
            var t = _p.Peek();
            if (t.IsPunct("-") || t.IsPunct("+") || t.IsPunct("~"))
            {
                _p.Next();
                var (v, s) = Unary();
                long r = t.Text switch
                {
                    "-" => -v,
                    "+" => v,
                    _ => ~ToInt32(v),
                };
                return (r, t.Text + s);
            }
            return Primary();
        }

        private (long, string) Primary()
        {
            var t = _p.Next();
            if (t.IsPunct("("))
            {
                var (v, s) = Binary(0);
                _p.ExpectPunct(")");
                return (v, "(" + s + ")");
            }
            if (t.Kind == TsTokenKind.Number)
                return (ParseIntegerLiteral(t), t.Text);
            if (t.Kind == TsTokenKind.Identifier)
            {
                string member = t.Text;
                string text = t.Text;
                if (member == _enumName && _p.Peek().IsPunct("."))
                {
                    _p.Next();
                    member = _p.ExpectIdent("an enum member name");
                    text = $"{_enumName}.{member}";
                }
                if (_known.TryGetValue(member, out var v))
                    return (v, text);
                throw _p.Fail(t, $"enum '{_enumName}' initializer references '{text}', which is not an earlier member of the enum");
            }
            if (t.Kind == TsTokenKind.String)
                throw _p.Fail(t, $"enum '{_enumName}' has a string initializer; only integer enums are supported");
            throw _p.Fail(t, $"enum '{_enumName}' initializer: expected an integer constant expression but found {Describe(t)}");
        }

        private long ParseIntegerLiteral(TsToken t)
        {
            string s = t.Text.Replace("_", "", StringComparison.Ordinal);
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt64(s[2..], 16);
                if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt64(s[2..], 2);
                if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt64(s[2..], 8);
                if (s.Contains('.', StringComparison.Ordinal) || s.Contains('e', StringComparison.OrdinalIgnoreCase))
                    throw _p.Fail(t, $"enum '{_enumName}' initializer '{t.Text}' is not an integer");
                return long.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw _p.Fail(t, $"enum '{_enumName}' initializer '{t.Text}' is not a representable integer");
            }
        }

        private long Apply(string op, long l, long r, TsToken at)
        {
            switch (op)
            {
                case "|":
                    return ToInt32(l) | ToInt32(r);
                case "^":
                    return ToInt32(l) ^ ToInt32(r);
                case "&":
                    return ToInt32(l) & ToInt32(r);
                case "<<":
                    return ToInt32(l) << (int)(r & 31);
                case ">>":
                    return ToInt32(l) >> (int)(r & 31);
                case ">>>":
                    return (long)((uint)ToInt32(l) >> (int)(r & 31));
                case "+":
                    return checked(l + r);
                case "-":
                    return checked(l - r);
                case "*":
                    return checked(l * r);
                case "/":
                case "%":
                    if (r == 0 || (op == "/" && l % r != 0))
                        throw _p.Fail(at, $"enum '{_enumName}' initializer '{op}' does not produce an integer");
                    return op == "/" ? l / r : l % r;
                default:
                    throw _p.Fail(at, $"enum '{_enumName}' initializer uses unsupported operator '{op}'");
            }
        }

        private static int ToInt32(long v) => unchecked((int)v);
    }
}
