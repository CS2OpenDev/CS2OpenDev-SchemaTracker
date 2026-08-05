// Demo-command id table serializer (demo_messages.json).
//
// Consumes the host's offline RTTI scan (DemoMessageRttiScanner) of the build's input binaries and
// lifts its DemoEntry[] into the public DemoMessages message (schemas/demo_messages.proto), then
// stamps host-only identity (schema_version, build_id, platform) and writes canonical proto3-JSON
// demo_messages.json. The id-space is flat (no channels) — a single repeated DemoMessageEntry list.
//
// Invariants:
//   Determinism: entries sorted by (id, proto_message_type) so the table is stable regardless of
//     scan order; canonical JSON (sorted keys); LF; UTF-8 no BOM.
//   Fail-loud: a decoded entry with an empty proto_message_type is malformed structure and fails
//     loud (the scanner only emits accepted C-class names, so this is a guard). (Zero decoded
//     entries is caught upstream in DemoMessageRttiScanner.)
//   All-or-nothing: sibling .tmp then atomic rename (via AtomicWrite).

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.NetworkMessages;

/// <summary>
/// Maps the demo RTTI scanner's <see cref="DemoMessageRttiScanner.DemoEntry"/>s into the public
/// <see cref="Schemas.DemoMessages"/> and writes the canonical demo_messages.json.
/// </summary>
public sealed class DemoMessagesEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public DemoMessagesEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Map the scanner's <paramref name="entries"/> and write demo_messages.json. Validation +
    /// document build happen before any disk write. Entries may arrive in any order — the canonical
    /// sort below makes the output stable.
    /// </summary>
    internal void Emit(IReadOnlyList<DemoMessageRttiScanner.DemoEntry> entries, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = new Schemas.DemoMessages
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        foreach (var entry in entries
                     .OrderBy(e => e.Id)
                     .ThenBy(e => e.ProtoMessageType, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(entry.ProtoMessageType))
            {
                throw new InvalidDataException(
                    "DemoMessagesEmitter: a DemoMessageEntry has an empty proto_message_type.");
            }

            document.Messages.Add(new DemoMessageEntry
            {
                Id = entry.Id,
                ProtoMessageType = entry.ProtoMessageType,
            });
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
