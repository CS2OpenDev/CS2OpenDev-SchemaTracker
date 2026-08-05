// Determinism. Canonical-form JSON serializer used by every artifact emitter.
//
// Why this exists at scaffolding stage: is load-bearing. The first time
// anyone serializes an artifact with default System.Text.Json options, output is
// already non-deterministic (property order matches reflection order, which depends
// on inheritance + ordering quirks that change across runtimes). Establishing the
// helper now means there's a single chokepoint to enforce sorted keys + invariant
// culture + UTF-8 without BOM, and every artifact emitter MUST go through it.
//
// For proto3-message artifacts, the host should prefer Google.Protobuf.JsonFormatter
// (which has its own canonical-form mode). This helper is for the small number of
// auxiliary JSONs that aren't proto3 (e.g. ad-hoc tool output, debugging dumps).

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cs2SchemaTracker.Host.Serialization;

public static class CanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Serialize <paramref name="value"/> to canonical-form JSON: keys sorted
    /// recursively, two-space indent, LF line endings, UTF-8 without BOM. Output
    /// is byte-identical across runs and across .NET runtime versions given the
    /// same input.
    /// </summary>
    public static string Serialize(object? value)
    {
        if (value is null)
            return "null";

        // System.Text.Json doesn't sort keys by default; round-trip through
        // JsonDocument so we can write properties in a stable (alphabetical)
        // order regardless of source.
        using var sourceDoc = JsonDocument.Parse(JsonSerializer.Serialize(value, SerializerOptions));
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteSorted(writer, sourceDoc.RootElement);
        }
        // Force LF line endings — Utf8JsonWriter uses Environment.NewLine, which is
        // CRLF on Windows. wants byte-identical across OSes.
        var s = Encoding.UTF8.GetString(stream.ToArray());
        return s.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Sort the keys of an already-serialized JSON document into canonical form
    /// (keys sorted recursively, two-space indent, LF line endings, UTF-8 without
    /// BOM). Use this for output produced by a serializer that does NOT sort keys —
    /// notably <c>Google.Protobuf.JsonFormatter</c>, which emits in proto field-number
    /// order. Array element order is preserved (the caller is responsible for ordering
    /// repeated fields deterministically before serialization).
    /// </summary>
    public static string SerializeRawJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var sourceDoc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteSorted(writer, sourceDoc.RootElement);
        }
        var s = Encoding.UTF8.GetString(stream.ToArray());
        return s.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Write the same canonical-form JSON to a file (UTF-8, no BOM, LF endings).
    /// </summary>
    public static void WriteFile(string path, object? value)
    {
        var bytes = Encoding.UTF8.GetBytes(Serialize(value));
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject()
                                            .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(prop.Name);
                    WriteSorted(writer, prop.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteSorted(writer, item);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
