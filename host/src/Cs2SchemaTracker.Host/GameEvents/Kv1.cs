// minimal KV1 (Valve KeyValues v1, text form) parser for the `.gameevents`
// file family.
//
// === Why hand-rolled (spirit) ===
// The `.gameevents` KV1 grammar is tiny and stable: nested brace blocks of
// quoted (or bare) tokens, with `//` line comments. A focused ~150-line parser
// keeps the dependency surface clean (no ValveKeyValue / ValveResourceFormat
// runtime dep) and keeps every fail-loud decision in our own code, unit
// testable against synthetic fixtures.
//
// === Grammar (the subset CS2 `.gameevents` files use) ===
//   document   := value*                       (one top-level "GameEvents" block in practice)
//   keyvalue   := token (block | token)        key followed by a block OR a scalar value
//   block      := '{' keyvalue* '}'
//   token      := '"' ...escaped... '"' | bare-run-until-whitespace-or-brace
//   comment    := '//' ... end-of-line         (anywhere whitespace is allowed)
//
// We model the result as a tree of nodes. A node is EITHER a scalar (Value set,
// Children null) OR a block (Children set, Value null). The trailing `//` comment
// on the line that introduces a key is captured on that key's node (requires
// per-event and per-field comments preserved verbatim).
//
// Determinism: the parser preserves source order in Children — the caller
// is responsible for any sorting it needs. No timestamps, no globals.

using System.Text;

namespace Cs2SchemaTracker.Host.GameEvents;

/// <summary>
/// One node in a parsed KV1 tree. A node is either a scalar (<see cref="Value"/> set,
/// <see cref="Children"/> null) or a block (<see cref="Children"/> set, <see cref="Value"/>
/// null). <see cref="Comment"/> is the verbatim trailing <c>//</c> comment on the line that
/// introduced the key (without the leading <c>//</c> and trimmed), or "" if absent.
/// </summary>
internal sealed class Kv1Node
{
    public required string Key { get; init; }

    /// <summary>Scalar value, or null when this node is a block.</summary>
    public string? Value { get; init; }

    /// <summary>Child key/value pairs in source order, or null when this node is a scalar.</summary>
    public List<Kv1Node>? Children { get; init; }

    /// <summary>Trailing <c>//</c> comment captured for this key; "" when absent.</summary>
    public string Comment { get; init; } = "";

    public bool IsBlock => Children is not null;
}

/// <summary>
/// Minimal recursive-descent KV1 text parser. Fail-loud: any malformed input
/// (unterminated string, unbalanced braces, a value where a block was required, trailing
/// junk) throws <see cref="InvalidDataException"/> — never a partial / best-effort tree.
/// </summary>
internal static class Kv1
{
    /// <summary>
    /// Parse a KV1 document into its top-level nodes (source order). The CS2 `.gameevents`
    /// files carry a single top-level <c>"GameEvents"</c> block, but the parser does not
    /// assume that — it returns every top-level key/value pair.
    /// </summary>
    public static IReadOnlyList<Kv1Node> Parse(string text, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(sourceName);

        var lexer = new Lexer(text, sourceName);
        var nodes = new List<Kv1Node>();

        while (true)
        {
            Token key = lexer.Next();
            if (key.Kind == TokenKind.End)
            {
                break;
            }
            if (key.Kind != TokenKind.String)
            {
                throw Malformed(sourceName, key,
                    $"expected a key string at top level, got {Describe(key)}");
            }
            nodes.Add(ParseValueFor(lexer, key, sourceName));
        }

        return nodes;
    }

    // Parse the value that follows an already-consumed key token: either a block
    // ('{' ... '}') or a scalar string. The key's trailing comment rides along.
    private static Kv1Node ParseValueFor(Lexer lexer, Token key, string sourceName)
    {
        Token next = lexer.Next();
        switch (next.Kind)
        {
            case TokenKind.OpenBrace:
                var children = ParseBlockBody(lexer, sourceName);
                return new Kv1Node { Key = key.Text, Children = children, Comment = key.Comment };

            case TokenKind.String:
                // For a scalar `"key" "value" // comment`, the trailing comment follows the
                // VALUE token, not the key — prefer the value's comment, falling back to any
                // comment that sat on the key line itself.
                string comment = !string.IsNullOrEmpty(next.Comment) ? next.Comment : key.Comment;
                return new Kv1Node { Key = key.Text, Value = next.Text, Comment = comment };

            case TokenKind.CloseBrace:
                throw Malformed(sourceName, next,
                    $"key '{key.Text}' has no value (unexpected '}}')");

            case TokenKind.End:
                throw Malformed(sourceName, next,
                    $"key '{key.Text}' has no value (end of input)");

            default:
                throw Malformed(sourceName, next,
                    $"key '{key.Text}' has no value, got {Describe(next)}");
        }
    }

    // Parse the body of a block up to and including its closing '}'.
    private static List<Kv1Node> ParseBlockBody(Lexer lexer, string sourceName)
    {
        var children = new List<Kv1Node>();
        while (true)
        {
            Token key = lexer.Next();
            if (key.Kind == TokenKind.CloseBrace)
            {
                return children;
            }
            if (key.Kind == TokenKind.End)
            {
                throw Malformed(sourceName, key, "unbalanced braces: end of input inside a block");
            }
            if (key.Kind != TokenKind.String)
            {
                throw Malformed(sourceName, key, $"expected a key string inside a block, got {Describe(key)}");
            }
            children.Add(ParseValueFor(lexer, key, sourceName));
        }
    }

    private static string Describe(Token t) => t.Kind switch
    {
        TokenKind.OpenBrace => "'{'",
        TokenKind.CloseBrace => "'}'",
        TokenKind.End => "end of input",
        TokenKind.String => $"value '{t.Text}'",
        _ => t.Kind.ToString(),
    };

    private static InvalidDataException Malformed(string sourceName, Token at, string detail) =>
        new($"KV1 parse error in '{sourceName}' at line {at.Line}: {detail} (fail-loud).");

    // ---- Lexer ------------------------------------------------------------------------

    private enum TokenKind { String, OpenBrace, CloseBrace, End }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, string Comment);

    private sealed class Lexer
    {
        private readonly string _s;
        private readonly string _source;
        private int _pos;
        private int _line = 1;

        public Lexer(string s, string source)
        {
            _s = s;
            _source = source;
        }

        public Token Next()
        {
            // The trailing comment on the line that produced this token is captured as
            // the token is read; brace tokens carry no comment.
            SkipTrivia();
            if (_pos >= _s.Length)
            {
                return new Token(TokenKind.End, "", _line, "");
            }

            char c = _s[_pos];
            switch (c)
            {
                case '{':
                    _pos++;
                    return new Token(TokenKind.OpenBrace, "{", _line, "");
                case '}':
                    _pos++;
                    return new Token(TokenKind.CloseBrace, "}", _line, "");
                case '"':
                    return ReadQuoted();
                default:
                    return ReadBare();
            }
        }

        // Skip whitespace and `//` line comments that PRECEDE a token. Trailing comments
        // (after a token, same line) are captured by the token readers below.
        private void SkipTrivia()
        {
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c == '\n')
                {
                    _line++;
                    _pos++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    _pos++;
                }
                else if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
                {
                    SkipToEndOfLine();
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipToEndOfLine()
        {
            while (_pos < _s.Length && _s[_pos] != '\n')
            {
                _pos++;
            }
        }

        private Token ReadQuoted()
        {
            int startLine = _line;
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _s.Length)
                {
                    throw Malformed(_source, new Token(TokenKind.End, "", _line, ""),
                        "unterminated quoted string");
                }
                char c = _s[_pos];
                if (c == '"')
                {
                    _pos++;
                    string comment = CaptureTrailingComment();
                    return new Token(TokenKind.String, sb.ToString(), startLine, comment);
                }
                if (c == '\\' && _pos + 1 < _s.Length)
                {
                    // KV1 honors a small set of escapes inside quotes.
                    char n = _s[_pos + 1];
                    sb.Append(n switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        '\\' => '\\',
                        '"' => '"',
                        _ => n,
                    });
                    _pos += 2;
                    continue;
                }
                if (c == '\n')
                {
                    _line++;
                }
                sb.Append(c);
                _pos++;
            }
        }

        private Token ReadBare()
        {
            int startLine = _line;
            int start = _pos;
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"')
                {
                    break;
                }
                if (c == '/' && _pos + 1 < _s.Length && _s[_pos + 1] == '/')
                {
                    break;
                }
                _pos++;
            }
            string text = _s.Substring(start, _pos - start);
            string comment = CaptureTrailingComment();
            return new Token(TokenKind.String, text, startLine, comment);
        }

        // After a token ends, look ahead on the SAME line: if the only thing before the
        // newline is whitespace then a `//` comment, capture it verbatim (trimmed) and
        // attach it to the token. Anything else (another token) leaves the comment "".
        private string CaptureTrailingComment()
        {
            int p = _pos;
            while (p < _s.Length && _s[p] != '\n')
            {
                char c = _s[p];
                if (c == '/' && p + 1 < _s.Length && _s[p + 1] == '/')
                {
                    int commentStart = p + 2;
                    int end = commentStart;
                    while (end < _s.Length && _s[end] != '\n')
                    {
                        end++;
                    }
                    return _s.Substring(commentStart, end - commentStart).Trim();
                }
                if (!char.IsWhiteSpace(c))
                {
                    return ""; // another token on this line; no trailing comment for THIS token
                }
                p++;
            }
            return "";
        }
    }
}
