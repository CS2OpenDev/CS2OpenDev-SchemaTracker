// JSDoc block parsing for point_script.d.ts.
//
// A block's text is de-starred line by line (the leading "*" and one following space go), blank
// lines at either end are dropped, and trailing whitespace is trimmed. Line breaks inside the text
// are kept: Valve's comments use them for lists and paragraphs.
//
// Tags: "@name" starts a tag when it is at the start of the text or follows whitespace, and the
// tag runs to the next tag. That is how TypeScript reads them too, and it matters here because
// Valve writes "@default" mid-sentence ("... penetrates solids. @default 100") while an address
// like "CSGOTeamFeedback@valvesoftware.com" is not a tag (the "@" follows a letter).
//
// Several blocks can precede one declaration; TypeScript attaches all of them, so Merge combines
// them in source order.

using System.Text;

namespace Cs2SchemaTracker.Host.CsScript;

internal sealed record JsDocTag(string Name, string Text);

internal sealed class JsDoc
{
    public static readonly JsDoc Empty = new("", Array.Empty<JsDocTag>());

    public JsDoc(string description, IReadOnlyList<JsDocTag> tags)
    {
        Description = description;
        Tags = tags;
    }

    public string Description { get; }

    public IReadOnlyList<JsDocTag> Tags { get; }

    public bool IsEmpty => Description.Length == 0 && Tags.Count == 0;

    public static JsDoc Parse(string rawBody)
    {
        var lines = rawBody.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var cleaned = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            var l = raw.TrimStart(' ', '\t');
            if (l.StartsWith('*'))
            {
                l = l[1..];
                if (l.StartsWith(' '))
                    l = l[1..];
            }
            cleaned.Add(l.TrimEnd());
        }
        string text = string.Join("\n", cleaned).Trim('\n', ' ', '\t');

        // Split into description + tags.
        var tagStarts = new List<int>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '@')
                continue;
            bool boundary = i == 0 || char.IsWhiteSpace(text[i - 1]);
            bool named = i + 1 < text.Length && char.IsAsciiLetter(text[i + 1]);
            if (boundary && named)
                tagStarts.Add(i);
        }

        string description = (tagStarts.Count == 0 ? text : text[..tagStarts[0]]).Trim();
        var tags = new List<JsDocTag>();
        for (int t = 0; t < tagStarts.Count; t++)
        {
            int s = tagStarts[t] + 1;
            int e = t + 1 < tagStarts.Count ? tagStarts[t + 1] : text.Length;
            int nameEnd = s;
            while (nameEnd < e && (char.IsAsciiLetterOrDigit(text[nameEnd]) || text[nameEnd] == '_'))
                nameEnd++;
            string name = text[s..nameEnd];
            string body = NormalizeBlock(text[nameEnd..e]);
            tags.Add(new JsDocTag(name, body));
        }
        return new JsDoc(NormalizeBlock(description), tags);
    }

    public static JsDoc Merge(IReadOnlyList<JsDoc> docs)
    {
        if (docs.Count == 0)
            return Empty;
        if (docs.Count == 1)
            return docs[0];
        var desc = string.Join("\n\n", docs.Select(d => d.Description).Where(d => d.Length > 0));
        var tags = docs.SelectMany(d => d.Tags).ToList();
        return new JsDoc(desc, tags);
    }

    /// <summary>
    /// Trim trailing whitespace per line, drop leading/trailing blank lines, and trim the start of
    /// the first line only. Later lines keep their indentation: Valve nests list items with it.
    /// </summary>
    private static string NormalizeBlock(string s)
    {
        var lines = s.Split('\n').Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count > 0)
            lines[0] = lines[0].TrimStart();
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                sb.Append('\n');
            sb.Append(lines[i]);
        }
        return sb.ToString();
    }
}
