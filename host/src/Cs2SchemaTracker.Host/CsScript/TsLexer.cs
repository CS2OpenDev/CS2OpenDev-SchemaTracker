// Tokenizer for the TypeScript declaration subset point_script.d.ts uses.
//
// Not a TypeScript lexer. It knows identifiers, string and numeric literals, the punctuation a
// declaration file can contain, and comments. JSDoc blocks (/** ... */) are kept as tokens so the
// parser can attach them to the declaration that follows; plain /* */ and // comments are dropped,
// which is also what TypeScript does with them.
//
// Every token records its 1-based line so a parse failure can say where it is. Anything the lexer
// does not recognise is a hard failure: a declaration file that has grown syntax this subset does
// not cover must stop the emit, not be skimmed.

using System.Globalization;
using System.Text;

namespace Cs2SchemaTracker.Host.CsScript;

internal enum TsTokenKind
{
    Identifier,
    String,
    Number,
    Punct,
    JsDoc,
    End,
}

/// <param name="Text">
/// Identifier / punctuation text verbatim; a string literal's DECODED value; a number's source
/// spelling; a JSDoc block's raw text between the delimiters.
/// </param>
/// <param name="Start">Offset of the token's first character.</param>
/// <param name="End">Offset one past the token's last character.</param>
internal readonly record struct TsToken(TsTokenKind Kind, string Text, int Line, int Start, int End)
{
    public bool IsPunct(string p) => Kind == TsTokenKind.Punct && Text == p;

    public bool IsIdent(string id) => Kind == TsTokenKind.Identifier && Text == id;
}

internal static class TsLexer
{
    // Longest first, so "=>" and "..." win over "=" and ".".
    private static readonly string[] Puncts =
    {
        "...", "=>", "{", "}", "(", ")", "[", "]", "<", ">", ",", ";", ":", "?", ".", "|", "&", "=",
        "-", "+", "~", "*", "^", "!", "/", "%",
    };

    public static List<TsToken> Tokenize(string source, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(source);
        var tokens = new List<TsToken>();
        int i = 0;
        int line = 1;
        int n = source.Length;

        while (i < n)
        {
            char c = source[i];

            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }
            if (c == '\r' || c == ' ' || c == '\t' || c == '\f' || c == '\v' || c == '﻿')
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < n && source[i + 1] == '*')
            {
                int startLine = line;
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw Error(sourceName, startLine, "unterminated block comment");
                }
                // "/**/" is an empty plain comment, not JSDoc; "/**" followed by anything else is.
                bool isJsDoc = i + 2 < n && source[i + 2] == '*' && close != i + 2;
                string body = source.Substring(i + (isJsDoc ? 3 : 2), close - i - (isJsDoc ? 3 : 2));
                for (int k = i; k < close; k++)
                {
                    if (source[k] == '\n')
                        line++;
                }
                if (isJsDoc)
                {
                    tokens.Add(new TsToken(TsTokenKind.JsDoc, body, startLine, i, close + 2));
                }
                i = close + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                int start = i;
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= n || source[i] == '\n')
                    {
                        throw Error(sourceName, line, "unterminated string literal");
                    }
                    char s = source[i];
                    if (s == c)
                    {
                        i++;
                        break;
                    }
                    if (s == '\\')
                    {
                        if (i + 1 >= n)
                        {
                            throw Error(sourceName, line, "unterminated string literal");
                        }
                        char e = source[i + 1];
                        sb.Append(e switch
                        {
                            'n' => '\n',
                            't' => '\t',
                            'r' => '\r',
                            '0' => '\0',
                            _ => e,
                        });
                        i += 2;
                        continue;
                    }
                    sb.Append(s);
                    i++;
                }
                tokens.Add(new TsToken(TsTokenKind.String, sb.ToString(), line, start, i));
                continue;
            }

            if (c == '`')
            {
                throw Error(sourceName, line, "template literal types are outside the supported declaration subset");
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(source[i + 1])))
            {
                int start = i;
                if (c == '0' && i + 1 < n && (source[i + 1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O'))
                {
                    i += 2;
                    while (i < n && (char.IsAsciiHexDigit(source[i]) || source[i] == '_'))
                        i++;
                }
                else
                {
                    while (i < n && (char.IsAsciiDigit(source[i]) || source[i] == '_' || source[i] == '.'))
                        i++;
                    if (i < n && (source[i] is 'e' or 'E'))
                    {
                        i++;
                        if (i < n && (source[i] is '+' or '-'))
                            i++;
                        while (i < n && char.IsAsciiDigit(source[i]))
                            i++;
                    }
                }
                if (i < n && (char.IsAsciiLetter(source[i]) || source[i] == '_'))
                {
                    throw Error(sourceName, line, $"malformed numeric literal '{source[start..(i + 1)]}'");
                }
                tokens.Add(new TsToken(TsTokenKind.Number, source[start..i], line, start, i));
                continue;
            }

            if (IsIdentStart(c))
            {
                int start = i;
                i++;
                while (i < n && IsIdentPart(source[i]))
                    i++;
                tokens.Add(new TsToken(TsTokenKind.Identifier, source[start..i], line, start, i));
                continue;
            }

            string? punct = null;
            foreach (var p in Puncts)
            {
                if (string.CompareOrdinal(source, i, p, 0, p.Length) == 0)
                {
                    punct = p;
                    break;
                }
            }
            if (punct is null)
            {
                throw Error(sourceName, line,
                    $"unexpected character '{c}' (U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)})");
            }
            tokens.Add(new TsToken(TsTokenKind.Punct, punct, line, i, i + punct.Length));
            i += punct.Length;
        }

        tokens.Add(new TsToken(TsTokenKind.End, "", line, n, n));
        return tokens;
    }

    private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_' || c == '$';

    private static bool IsIdentPart(char c) => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '$';

    internal static InvalidDataException Error(string sourceName, int line, string message)
        => new($"{sourceName}:{line}: {message}");
}
