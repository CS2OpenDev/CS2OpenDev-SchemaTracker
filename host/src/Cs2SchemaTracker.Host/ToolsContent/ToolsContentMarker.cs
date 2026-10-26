// The tools-content generation marker: <tuple>/tools-content.json = {"generation":N}.
//
// A tools acquire writes it after merging the slice into the tuple dir. It answers one question a
// file listing cannot: was this tuple's tools slice selected by a selector that ALREADY KNEW about
// the tools-content files? A cache written before ToolsBinSelector kept the FGDs carries the tools
// DLLs and a manifest-record.json listing depot 2347779, but no FGD — and without the marker that
// is indistinguishable from "this build never shipped an FGD", which extract would otherwise write
// into the corpus as a false CONTENT_NOT_SHIPPED_THIS_ERA omission. Same reasoning as the content
// store's trim-generation.json; the response differs because the tools artifacts are optional:
// extract SKIPS them (no file, no omission) and the cache-first acquire re-fetches the slice.

using System.Globalization;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host.ToolsContent;

internal static class ToolsContentMarker
{
    private const string Property = "generation";

    public static string PathFor(string tupleDir)
        => Path.Combine(tupleDir, ToolsBinSelector.ToolsContentMarkerFileName);

    public static void Write(string tupleDir, int generation = ToolsBinSelector.ToolsContentGeneration)
    {
        ArgumentException.ThrowIfNullOrEmpty(tupleDir);
        Directory.CreateDirectory(tupleDir);
        File.WriteAllText(
            PathFor(tupleDir),
            "{\"" + Property + "\":" + generation.ToString(CultureInfo.InvariantCulture) + "}\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>The stored generation, or false when the marker is absent or unreadable.</summary>
    public static bool TryRead(string tupleDir, out int generation)
    {
        generation = 0;
        var path = PathFor(tupleDir);
        if (!File.Exists(path))
            return false;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(Property, out var v)
                && v.ValueKind == JsonValueKind.Number
                && v.TryGetInt32(out generation);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            generation = 0;
            return false;
        }
    }

    /// <summary>True iff the tuple's marker exists and is at least the current generation.</summary>
    public static bool IsCurrent(string tupleDir)
        => TryRead(tupleDir, out var g) && g >= ToolsBinSelector.ToolsContentGeneration;
}
