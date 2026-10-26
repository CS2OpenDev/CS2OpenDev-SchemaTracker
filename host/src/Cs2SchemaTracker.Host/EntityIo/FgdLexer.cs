// Tokenizer for Source 2 FGD files.
//
// Tokens: quoted strings (only \" is an escape; every other backslash is literal text, which is how
// Hammer reads them), barewords (names, numbers, unquoted values: letters, digits and _ . - / \ $ *
// # % ! ? '), and the punctuation @ = : [ ] ( ) { } , +. "//" starts a comment to end of line.
//
// Each token records whether whitespace or a comment separated it from the previous token. Raw
// captures (helper arguments, keyvalue types, metadata blocks) are re-joined from tokens with a
// single space wherever the source had a gap, so `iconsprite("x.vmat")` survives verbatim and a
// metadata block that spans several commented lines collapses to one line.

using System.Globalization;
using System.Text;

namespace Cs2SchemaTracker.Host.EntityIo;

internal enum FgdTokenKind
{
    Word,
    String,
    Punct,
    End,
}

internal readonly record struct FgdToken(FgdTokenKind Kind, string Text, int Line, bool SpaceBefore)
{
    public bool IsPunct(char c) => Kind == FgdTokenKind.Punct && Text.Length == 1 && Text[0] == c;

    public bool IsWord(string w) => Kind == FgdTokenKind.Word && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);

    /// <summary>The token as it appears in source (strings re-quoted, \" re-escaped).</summary>
    public string SourceForm => Kind == FgdTokenKind.String
        ? "\"" + Text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
        : Text;
}

internal static class FgdLexer
{
    private const string PunctChars = "@=:[](){},+";

    public static List<FgdToken> Tokenize(string source, string sourceName)
    {
        var tokens = new List<FgdToken>();
        int i = 0;
        int line = 1;
        int n = source.Length;
        bool gap = true;

        while (i < n)
        {
            char c = source[i];
            if (c == '\n')
            {
                line++;
                i++;
                gap = true;
                continue;
            }
            if (char.IsWhiteSpace(c) || c == '﻿')
            {
                i++;
                gap = true;
                continue;
            }
            if (c == '/' && i + 1 < n && source[i + 1] == '/')
            {
                while (i < n && source[i] != '\n')
                    i++;
                gap = true;
                continue;
            }
            if (c == '"')
            {
                int startLine = line;
                var sb = new StringBuilder();
                i++;
                while (true)
                {
                    if (i >= n || source[i] == '\n')
                        throw Error(sourceName, startLine, "unterminated string");
                    char s = source[i];
                    if (s == '\\' && i + 1 < n && source[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    if (s == '"')
                    {
                        i++;
                        break;
                    }
                    if (s != '\r')
                        sb.Append(s);
                    i++;
                }
                tokens.Add(new FgdToken(FgdTokenKind.String, sb.ToString(), startLine, gap));
                gap = false;
                continue;
            }
            if (PunctChars.Contains(c, StringComparison.Ordinal))
            {
                tokens.Add(new FgdToken(FgdTokenKind.Punct, c.ToString(), line, gap));
                i++;
                gap = false;
                continue;
            }
            if (IsWordChar(c))
            {
                int start = i;
                while (i < n && IsWordChar(source[i]) && !(source[i] == '/' && i + 1 < n && source[i + 1] == '/'))
                    i++;
                tokens.Add(new FgdToken(FgdTokenKind.Word, source[start..i], line, gap));
                gap = false;
                continue;
            }
            throw Error(sourceName, line,
                $"unexpected character '{c}' (U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)})");
        }
        tokens.Add(new FgdToken(FgdTokenKind.End, "", line, true));
        return tokens;
    }

    private static bool IsWordChar(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or '/' or '\\' or '$' or '*' or '#' or '%' or '!' or '?' or '\'' or '<' or '>' or '|' or '&' or ';';

    internal static InvalidDataException Error(string sourceName, int line, string message)
        => new($"{sourceName}:{line}: {message}");
}
