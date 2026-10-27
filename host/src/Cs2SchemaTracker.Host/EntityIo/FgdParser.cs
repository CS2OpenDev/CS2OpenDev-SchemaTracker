// Parser for one Source 2 FGD file.
//
// Produces the file's top-level items in source order: class declarations, @include and @exclude.
// Include expansion, @exclude / @OverrideClass application and inheritance live in FgdResolver;
// this type only reads syntax.
//
// Grammar (what CS2's FGDs use):
//
//   item        := '@' Word header* ( '=' Name [':' [desc]] [ '[' body ']' ] )?
//   header      := Word '(' raw ')' | Word '{' raw '}' | Word | String | '{' raw '}'
//   desc        := String ('+' String)*
//   body        := ( io | keyvalue )*
//   io          := ('input' | 'output') Name '(' raw ')' meta* [':' desc]
//   keyvalue    := Name '(' raw ')' meta* (':' [value])* ['=' '[' entry* ']']
//   meta        := '{' raw '}' | '[' raw ']' | 'readonly' | 'report'
//   value       := desc | Word
//   entry       := (String | Word) ':' desc [':' (String | Word)]
//
// An '@' item with no '=' is a directive (@mapsize, @EntityGroup, @VisGroupFilter, ...): tolerated
// and skipped, it configures the editor rather than declaring a class. A keyvalue takes at most three
// ':' fields (display name, default, description); a fourth, an unterminated bracket or string, or a
// token that cannot start a body item is a hard failure naming file:line.

namespace Cs2SchemaTracker.Host.EntityIo;

internal sealed record FgdChoiceDecl(string Value, string Label, string? Third, string Description);

internal sealed record FgdKeyValueDecl(
    string Name, string Type, string DisplayName, string? DefaultValue, string Description, string Metadata,
    IReadOnlyList<FgdChoiceDecl> Entries, int Line);

internal sealed record FgdIoDecl(string Name, string ParamType, string Description, string Metadata, bool IsInput);

internal sealed record FgdClassDecl(
    string ClassType,
    string Name,
    string Description,
    IReadOnlyList<string> Bases,
    IReadOnlyList<string> Helpers,
    IReadOnlyList<FgdKeyValueDecl> KeyValues,
    IReadOnlyList<FgdIoDecl> Ports,
    string SourceFile,
    int Line);

internal abstract record FgdItem(string SourceFile, int Line);

internal sealed record FgdIncludeItem(string Target, string SourceFile, int Line) : FgdItem(SourceFile, Line);

internal sealed record FgdExcludeItem(string ClassName, string SourceFile, int Line) : FgdItem(SourceFile, Line);

internal sealed record FgdClassItem(FgdClassDecl Decl) : FgdItem(Decl.SourceFile, Decl.Line);

internal sealed class FgdParser
{
    private readonly List<FgdToken> _t;
    private readonly string _source;
    private int _pos;

    private FgdParser(List<FgdToken> tokens, string sourceName)
    {
        _t = tokens;
        _source = sourceName;
    }

    public static List<FgdItem> Parse(string text, string sourceName)
        => new FgdParser(FgdLexer.Tokenize(text, sourceName), sourceName).ParseFile();

    private FgdToken Peek(int ahead = 0) => _t[Math.Min(_pos + ahead, _t.Count - 1)];

    private FgdToken Next()
    {
        var t = _t[_pos];
        if (t.Kind != FgdTokenKind.End)
            _pos++;
        return t;
    }

    private InvalidDataException Fail(FgdToken at, string message) => FgdLexer.Error(_source, at.Line, message);

    private static string Describe(FgdToken t) => t.Kind switch
    {
        FgdTokenKind.End => "end of file",
        FgdTokenKind.String => $"string \"{t.Text}\"",
        _ => $"'{t.Text}'",
    };

    private void Expect(char punct)
    {
        var t = Next();
        if (!t.IsPunct(punct))
            throw Fail(t, $"expected '{punct}' but found {Describe(t)}");
    }

    private string ExpectWord(string what)
    {
        var t = Next();
        if (t.Kind != FgdTokenKind.Word)
            throw Fail(t, $"expected {what} but found {Describe(t)}");
        return t.Text;
    }

    private List<FgdItem> ParseFile()
    {
        var items = new List<FgdItem>();
        while (true)
        {
            var t = Peek();
            if (t.Kind == FgdTokenKind.End)
                return items;
            if (!t.IsPunct('@'))
                throw Fail(t, $"expected an '@' declaration but found {Describe(t)}");
            var item = ParseAt();
            if (item is not null)
                items.Add(item);
        }
    }

    private FgdItem? ParseAt()
    {
        var at = Next();   // '@'
        var kwTok = Next();
        if (kwTok.Kind != FgdTokenKind.Word)
            throw Fail(kwTok, $"expected a declaration keyword after '@' but found {Describe(kwTok)}");
        string kw = kwTok.Text;

        if (kwTok.IsWord("include"))
        {
            var target = Next();
            if (target.Kind != FgdTokenKind.String)
                throw Fail(target, $"@include expects a quoted file name but found {Describe(target)}");
            return new FgdIncludeItem(target.Text, _source, at.Line);
        }
        if (kwTok.IsWord("exclude"))
        {
            var name = Next();
            if (name.Kind is not (FgdTokenKind.Word or FgdTokenKind.String))
                throw Fail(name, $"@exclude expects a class name but found {Describe(name)}");
            return new FgdExcludeItem(name.Text, _source, at.Line);
        }

        var bases = new List<string>();
        var helpers = new List<string>();
        while (true)
        {
            var t = Peek();
            if (t.IsPunct('='))
                break;
            if (t.IsPunct('@') || t.Kind == FgdTokenKind.End)
                return null;   // a directive with no '=': editor configuration, tolerated and skipped.
            if (t.Kind == FgdTokenKind.Word)
            {
                Next();
                if (Peek().IsPunct('('))
                {
                    var (inner, innerTokens) = CaptureRaw();
                    if (t.IsWord("base"))
                    {
                        foreach (var b in innerTokens.Where(x => x.Kind != FgdTokenKind.Punct))
                            bases.Add(b.Text);
                    }
                    else
                    {
                        helpers.Add($"{t.Text}({inner})");
                    }
                }
                else if (Peek().IsPunct('{'))
                {
                    var (inner, _) = CaptureRaw();
                    helpers.Add($"{t.Text} {{ {inner} }}");
                }
                else
                {
                    helpers.Add(t.Text);
                }
                continue;
            }
            if (t.IsPunct('{'))
            {
                var (inner, _) = CaptureRaw();
                helpers.Add($"{{ {inner} }}");
                continue;
            }
            if (t.IsPunct('('))
            {
                // A directive taking arguments directly: @mapsize(-16384, 16384).
                var (inner, _) = CaptureRaw();
                helpers.Add($"{kw}({inner})");
                continue;
            }
            if (t.Kind == FgdTokenKind.String)
            {
                Next();
                helpers.Add(t.SourceForm);
                continue;
            }
            throw Fail(t, $"unexpected {Describe(t)} in the header of @{kw}");
        }

        Next();   // '='
        var nameTok = Next();
        if (nameTok.Kind != FgdTokenKind.Word)
            throw Fail(nameTok, $"expected a class name after '=' in @{kw} but found {Describe(nameTok)}");

        string description = "";
        if (Peek().IsPunct(':'))
        {
            Next();
            if (Peek().Kind == FgdTokenKind.String)
                description = ParseConcat();
        }

        var kvs = new List<FgdKeyValueDecl>();
        var ports = new List<FgdIoDecl>();
        if (Peek().IsPunct('['))
        {
            Next();
            ParseBody(kvs, ports, nameTok.Text);
        }
        else if (!(Peek().IsPunct('@') || Peek().Kind == FgdTokenKind.End))
        {
            throw Fail(Peek(), $"expected '[' to open the body of '{nameTok.Text}' but found {Describe(Peek())}");
        }

        return new FgdClassItem(new FgdClassDecl(
            kw, nameTok.Text, description, bases, helpers, kvs, ports, _source, at.Line));
    }

    private void ParseBody(List<FgdKeyValueDecl> kvs, List<FgdIoDecl> ports, string className)
    {
        while (true)
        {
            var t = Peek();
            if (t.IsPunct(']'))
            {
                Next();
                return;
            }
            if (t.Kind == FgdTokenKind.End)
                throw Fail(t, $"unterminated body of '{className}'");
            if (StartsIo(0))
            {
                ports.Add(ParseIo());
                continue;
            }
            if (t.Kind == FgdTokenKind.Word && Peek(1).IsPunct('('))
            {
                kvs.Add(ParseKeyValue(className));
                continue;
            }
            throw Fail(t, $"unexpected {Describe(t)} in the body of '{className}'");
        }
    }

    /// <summary>Does the token at <paramref name="ahead"/> start an `input Name(` / `output Name(` line?</summary>
    private bool StartsIo(int ahead)
        => (Peek(ahead).IsWord("input") || Peek(ahead).IsWord("output"))
           && Peek(ahead + 1).Kind == FgdTokenKind.Word && Peek(ahead + 2).IsPunct('(');

    private FgdIoDecl ParseIo()
    {
        bool isInput = Next().IsWord("input");
        string name = ExpectWord("an input/output name");
        var (paramType, _) = CaptureRaw();
        string meta = ParseMetadata();
        string desc = "";
        if (Peek().IsPunct(':'))
        {
            Next();
            if (Peek().Kind == FgdTokenKind.String)
                desc = ParseConcat();
            if (Peek().IsPunct(':'))
                throw Fail(Peek(), $"{(isInput ? "input" : "output")} '{name}' has more fields than a description");
        }
        return new FgdIoDecl(name, paramType, desc, meta, isInput);
    }

    private FgdKeyValueDecl ParseKeyValue(string className)
    {
        var nameTok = Next();
        var (type, _) = CaptureRaw();
        string meta = ParseMetadata();

        var fields = new List<string?>();
        while (Peek().IsPunct(':'))
        {
            var colon = Next();
            if (fields.Count == 3)
                throw Fail(colon, $"keyvalue '{className}.{nameTok.Text}' has more than three ':' fields");
            var v = Peek();
            if (v.Kind == FgdTokenKind.String)
                fields.Add(ParseConcat());
            else if (v.Kind == FgdTokenKind.Word && !Peek(1).IsPunct('(') && !StartsIo(0))
                fields.Add(Next().Text);
            else
                fields.Add(null);   // empty slot: `: :`
        }

        var entries = new List<FgdChoiceDecl>();
        if (Peek().IsPunct('='))
        {
            Next();
            Expect('[');
            while (true)
            {
                var e = Peek();
                if (e.IsPunct(']'))
                {
                    Next();
                    break;
                }
                if (e.Kind is not (FgdTokenKind.String or FgdTokenKind.Word))
                    throw Fail(e, $"expected a choice value in '{className}.{nameTok.Text}' but found {Describe(e)}");
                string value = Next().Text;
                Expect(':');
                if (Peek().Kind != FgdTokenKind.String)
                    throw Fail(Peek(), $"expected a quoted label for choice '{value}' in '{className}.{nameTok.Text}'");
                string label = ParseConcat();
                string? third = null;
                string fourth = "";
                if (Peek().IsPunct(':'))
                {
                    Next();
                    var d = Next();
                    if (d.Kind is not (FgdTokenKind.String or FgdTokenKind.Word))
                        throw Fail(d, $"expected a default after choice '{value}' in '{className}.{nameTok.Text}'");
                    third = d.Text;
                    if (Peek().IsPunct(':'))
                    {
                        Next();
                        if (Peek().Kind != FgdTokenKind.String)
                            throw Fail(Peek(), $"expected a quoted description after choice '{value}' in '{className}.{nameTok.Text}'");
                        fourth = ParseConcat();
                    }
                    if (Peek().IsPunct(':'))
                        throw Fail(Peek(), $"choice '{value}' in '{className}.{nameTok.Text}' has more than four fields");
                }
                entries.Add(new FgdChoiceDecl(value, label, third, fourth));
            }
        }

        string display = fields.Count > 0 ? fields[0] ?? "" : "";
        string? defaultValue = fields.Count > 1 ? fields[1] : null;
        string description = fields.Count > 2 ? fields[2] ?? "" : "";
        return new FgdKeyValueDecl(nameTok.Text, type, display, defaultValue, description, meta, entries, nameTok.Line);
    }

    private string ParseMetadata()
    {
        var parts = new List<string>();
        while (true)
        {
            var t = Peek();
            if (t.IsPunct('{'))
            {
                var (inner, _) = CaptureRaw();
                parts.Add($"{{ {inner} }}");
            }
            else if (t.IsPunct('['))
            {
                var (inner, _) = CaptureRaw();
                parts.Add($"[ {inner} ]");
            }
            else if (t.IsWord("readonly") || t.IsWord("report"))
            {
                Next();
                parts.Add(t.Text);
            }
            else
            {
                return string.Join(" ", parts);
            }
        }
    }

    private string ParseConcat()
    {
        var t = Next();
        if (t.Kind != FgdTokenKind.String)
            throw Fail(t, $"expected a quoted string but found {Describe(t)}");
        var s = t.Text;
        while (Peek().IsPunct('+'))
        {
            Next();
            var more = Next();
            if (more.Kind != FgdTokenKind.String)
                throw Fail(more, $"expected a quoted string after '+' but found {Describe(more)}");
            s += more.Text;
        }
        return s;
    }

    /// <summary>
    /// At an opening '(' / '{' / '[': consume through the matching close (nesting checked across all
    /// three kinds) and return the inner tokens re-joined with single spaces where the source had a
    /// gap, plus the inner tokens themselves.
    /// </summary>
    private (string Inner, List<FgdToken> Tokens) CaptureRaw()
    {
        var open = Next();
        var stack = new Stack<char>();
        stack.Push(Close(open.Text[0]));
        var inner = new List<FgdToken>();
        while (true)
        {
            var t = Next();
            if (t.Kind == FgdTokenKind.End)
                throw Fail(open, $"unterminated '{open.Text}'");
            if (t.Kind == FgdTokenKind.Punct && t.Text is "(" or "{" or "[")
            {
                stack.Push(Close(t.Text[0]));
            }
            else if (t.Kind == FgdTokenKind.Punct && t.Text is ")" or "}" or "]")
            {
                if (stack.Peek() != t.Text[0])
                    throw Fail(t, $"mismatched '{t.Text}' (expected '{stack.Peek()}')");
                stack.Pop();
                if (stack.Count == 0)
                    break;
            }
            inner.Add(t);
        }
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < inner.Count; i++)
        {
            if (i > 0 && inner[i].SpaceBefore)
                sb.Append(' ');
            sb.Append(inner[i].SourceForm);
        }
        return (sb.ToString(), inner);
    }

    private static char Close(char open) => open switch
    {
        '(' => ')',
        '{' => '}',
        _ => ']',
    };
}
