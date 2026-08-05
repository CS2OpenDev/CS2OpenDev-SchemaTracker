// shared canonical-proto3-JSON serialize + atomic write for the host's
// proto3-message artifact emitters.
//
// Every per-platform artifact (entity_schema, convars, commands, network_messages, modules,
// provenance) is a proto3 message serialized through Google.Protobuf.JsonFormatter (canonical
// proto3 JSON mapping: uint64 -> string, lowerCamelCase field names, field-number order) then
// post-sorted through CanonicalJson (sorted keys, LF, UTF-8 no BOM) so the output is
// byte-identical across runs and round-trips byte-identically through its .proto.
//
// This collapses the formatter settings + sibling-.tmp-then-rename boilerplate that each
// emitter previously duplicated into one chokepoint. EntitySchemaEmitter / GameEventsEmitter
// keep their own inlined copies (they predate this and are intentionally left untouched in
// this change); the emitters route through here.
//
// All-or-nothing: write to a sibling .tmp then atomically rename; on a mid-write
//          throw the temp file is deleted and any pre-existing target is left untouched.

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Serialization;

internal static class AtomicWrite
{
    // FormatDefaultValues: emit zero-valued scalars so the record is complete and stable per
    // run. Two-space indent; CanonicalJson then sorts keys + forces LF + strips BOM.
    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true)
            .WithIndentation("  "));

    /// <summary>Canonical proto3 JSON for <paramref name="message"/> (sorted keys, LF, no BOM).</summary>
    public static string SerializeCanonical(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return CanonicalJson.SerializeRawJson(Formatter.Format(message));
    }

    /// <summary>
    /// Serialize <paramref name="message"/> to canonical proto3 JSON and write it atomically to
    /// <paramref name="outputPath"/> (sibling .tmp then rename). Throws (and leaves no partial
    /// output) on any failure.
    /// </summary>
    public static void WriteCanonical(IMessage message, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        string json = SerializeCanonical(message);

        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var tmpPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, System.Text.Encoding.UTF8.GetBytes(json));
            File.Move(tmpPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try
                { File.Delete(tmpPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }
}
