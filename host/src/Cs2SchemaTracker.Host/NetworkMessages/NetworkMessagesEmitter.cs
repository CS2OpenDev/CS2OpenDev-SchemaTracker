// Network message ID table serializer (network_messages.json).
//
// Consumes the host's offline RTTI scan (NetworkMessageRttiScanner) of the build's input binaries —
// the source of record — and lifts its NetworkChannel[] straight into the public NetworkMessages
// message (schemas/network_messages.proto), then stamps host-only identity (schema_version,
// build_id, platform) and writes canonical proto3-JSON network_messages.json.
//
// (Was: lifted WalkerOutput.network_messages, the walker's pin-static generated table. That
// table is byte-identical across every build walked with one hl2sdk pin — NOT a per-build
// observation. The RTTI scanner replaces it with the per-build REGISTERED membership, decoded
// from each build's own CNetMessagePB instantiations. The serialization / sort / canonical-JSON
// / atomic-write path below is UNCHANGED — only the source changed.)
//
// Each channel carries integer message-ID -> protobuf message-type-name bindings. An ID with
// no resolvable type name (proto_message_type == "") is KEPT (the proto documents this so the
// registry_audit can later account for it) — an empty type name is NOT a fail-loud condition.
// An empty CHANNEL name, however, is malformed structure and fails loud.
//
// Invariants:
//   Determinism: channels sorted by name (Ordinal); entries within a channel sorted by
//     (id, proto_message_type) so the table is stable regardless of scan order; canonical JSON
//     (sorted keys); LF; UTF-8 no BOM.
//   Fail-loud: a channel with an empty name throws BEFORE any output bytes are written. (Zero
//     decoded messages is caught upstream in NetworkMessageRttiScanner.)
//   All-or-nothing: sibling .tmp then atomic rename.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.NetworkMessages;

/// <summary>
/// Maps the RTTI scanner's <see cref="NetworkChannel"/>s into the public
/// <see cref="Schemas.NetworkMessages"/> and writes the canonical network_messages.json.
/// </summary>
public sealed class NetworkMessagesEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public NetworkMessagesEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Map the RTTI scanner's <paramref name="channels"/> and write network_messages.json.
    /// Validation + document build happen before any disk write. Channels may arrive in any order
    /// with entries in any order — the canonical sort below makes the output stable.
    /// </summary>
    public void Emit(IReadOnlyList<NetworkChannel> channels, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = new Schemas.NetworkMessages
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        foreach (NetworkChannel src in channels
                     .OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(src.Name))
            {
                throw new InvalidDataException(
                    "NetworkMessagesEmitter: a NetworkChannel has an empty name.");
            }

            var dstChannel = new NetworkChannel { Name = src.Name };

            // Entries sorted by (id, type) for a stable table. An empty proto_message_type is
            // legitimate (unresolved binding) and is preserved, not dropped.
            foreach (NetworkMessageEntry entry in src.Messages
                         .OrderBy(m => m.Id)
                         .ThenBy(m => m.ProtoMessageType, StringComparer.Ordinal))
            {
                dstChannel.Messages.Add(new NetworkMessageEntry
                {
                    Id = entry.Id,
                    ProtoMessageType = entry.ProtoMessageType,
                });
            }

            document.Channels.Add(dstChannel);
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
