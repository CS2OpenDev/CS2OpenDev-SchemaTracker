// Offline RTTI network-message scanner (network_messages.json source of record).
//
// Decodes the per-build REGISTERED network-message set directly from a build's shipped binaries
// by parsing every `CNetMessagePB<id, MessageType, group, reliable, flag>` MSVC RTTI type
// descriptor (`.?AV?$CNetMessagePB@...` in .rdata). No DLL load, no engine launch — a pure
// File.ReadAllBytes + .rdata RTTI parse, mirroring DescriptorScanner's infra. A message appears
// IFF the build instantiated CNetMessagePB for it (a static binding => registered); dead /
// unregistered proto-enum entries have no instantiation and are excluded by construction.
//
// The MSVC mangle decode lives in the SHARED MsvcRttiTemplateDecoder so this scan, the
// demo_messages CDemoMessagePB scan, and the CUserMessagePB cross-validator below cannot drift.
//
// This is a faithful C# port of the validated Python prototype: vs a live listen capture it
// reproduced live-only=0 and 192/192 ids byte-identical, producing 194 msgs / 12 channels for build
// 23517234. It REPLACES the walker's pin-static generated table (walker_output.network_messages) as
// the source of record.
//
// CROSS-VALIDATION (hardening): CS2 also instantiates `CUserMessagePB<id, type, bool>`
// for the user-message family — a SECOND, independent id<->type source for the same messages the
// CNetMessagePB scan already covers under the UserMessages channel. We decode those too and
// ASSERT every CUserMessagePB (id->type) agrees with the CNetMessagePB-derived table. Any
// disagreement (a type registered under different ids by the two templates) means a decode bug
// or an unexpected build, and fails loud. It is NOT emitted — it only strengthens the table.
//
// PLATFORM SCOPE: BOTH committed platforms. windows-x86_64 uses the MSVC `?$CNetMessagePB@...`
// mangling via MsvcRttiTemplateDecoder; linux-x86_64 uses the Itanium `13CNetMessagePBI...`
// type_info-name mangling via ItaniumRttiTemplateDecoder. The registered ids are IDENTICAL across
// platforms — only the demangle differs — and this is validated: over build 23773332's Linux `.so`
// set the Itanium path reproduces the committed windows-x86_64 network_messages.json exactly (194
// msgs / 12 channels, zero linux-only / windows-only). Any OTHER platform still fails loud (never
// silently mis-scan a mangling we have not implemented + validated).
//
// Invariants:
//   Determinism: output is a pure function of the input bytes; messages are unioned by (id, type)
//     across the binary set and grouped into channels by a fixed type-prefix map. The emitter
//     applies the final canonical sort. The two ABI decoders yield the identical (id, type) set, so
//     the platform axis does not change the message membership.
//   Fail-loud: an unknown platform, zero decoded messages across the whole set (a real CS2 binary
//     set always carries CNetMessagePB RTTI), or a CUserMessagePB cross-validation disagreement,
//     throws BEFORE any artifact byte.

using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.NetworkMessages;

/// <summary>
/// Scans a build's input binaries for `CNetMessagePB&lt;...&gt;` RTTI type descriptors (MSVC on
/// windows-x86_64, Itanium on linux-x86_64) and produces the per-build registered network-message
/// set as <see cref="NetworkChannel"/>s. Cross-validates against the `CUserMessagePB`
/// instantiations.
/// </summary>
internal sealed class NetworkMessageRttiScanner
{
    /// <summary>The two platforms whose RTTI mangling this scanner decodes.</summary>
    public const string WindowsPlatform = "windows-x86_64";
    public const string LinuxPlatform = "linux-x86_64";

    // MSVC (windows-x86_64) RTTI type-descriptor markers. Everything after the trailing '@' is the
    // mangled template argument list. ASCII by construction.
    private const string MsvcNetMarker = "?$CNetMessagePB@";
    private const string MsvcUserMarker = "?$CUserMessagePB@";

    // Itanium (linux-x86_64) type_info-name markers: the length-prefixed class name + the 'I' that
    // opens the template arg list. Everything after the 'I' is the mangled arg list.
    private const string ItaniumNetMarker = "13CNetMessagePBI";
    private const string ItaniumUserMarker = "14CUserMessagePBI";

    /// <summary>
    /// Decoded constants of one CNetMessagePB template instantiation (delegates to the shared
    /// <see cref="MsvcRttiTemplateDecoder"/>). Re-exported so existing decode tests bind to the same
    /// record shape.
    /// </summary>
    internal readonly record struct Decoded(int Id, string ProtoMessageType, int? Group, int? Reliable, int? Flag);

    /// <summary>
    /// Decode one CNetMessagePB MSVC mangled tail (the substring right after
    /// <c>?$CNetMessagePB@</c>) into its template constants, or null if it does not parse. Thin
    /// wrapper over the shared MSVC decoder so unit tests keep their entry point.
    /// </summary>
    internal static Decoded? Decode(string tail)
    {
        if (MsvcRttiTemplateDecoder.Decode(tail) is not { } d)
            return null;
        return new Decoded(d.Id, d.ProtoMessageType, d.Group, d.Reliable, d.Flag);
    }

    /// <summary>
    /// Decode one CNetMessagePB Itanium template-arg-list tail (the substring right after the
    /// <c>13CNetMessagePBI</c> marker) into its template constants, or null if it does not parse.
    /// Thin wrapper over the shared Itanium decoder so the linux-x86_64 tests have an entry point
    /// symmetric with <see cref="Decode"/>.
    /// </summary>
    internal static Decoded? DecodeItanium(string tail)
    {
        if (ItaniumRttiTemplateDecoder.Decode(tail) is not { } d)
            return null;
        return new Decoded(d.Id, d.ProtoMessageType, d.Group, d.Reliable, d.Flag);
    }

    /// <summary>
    /// Scan <paramref name="binaryPaths"/> and return the registered network-message set grouped
    /// into channels. Fail-loud: non-MSVC platform, zero decoded messages, or a CUserMessagePB
    /// cross-validation disagreement.
    /// </summary>
    public static IReadOnlyList<NetworkChannel> Scan(IReadOnlyList<string> binaryPaths, string platform)
    {
        ArgumentNullException.ThrowIfNull(binaryPaths);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        bool itanium = platform switch
        {
            WindowsPlatform => false,   // MSVC mangling.
            LinuxPlatform => true,      // Itanium mangling.
            // Never guess a mangling we have not implemented + validated.
            _ => throw new NotSupportedException(
                $"NetworkMessageRttiScanner: platform '{platform}' is not supported. Only " +
                $"'{WindowsPlatform}' (MSVC mangling) and '{LinuxPlatform}' (Itanium mangling) are " +
                "implemented; refusing to scan an unknown platform rather than mis-decode."),
        };
        string netMarker = itanium ? ItaniumNetMarker : MsvcNetMarker;
        string userMarker = itanium ? ItaniumUserMarker : MsvcUserMarker;

        // Union by (id, type) across the whole binary set, exactly like the Python prototype.
        var union = new Dictionary<(int Id, string Type), byte>();
        // Independent CUserMessagePB observations (raw accepted (id,type)) for cross-validation.
        var userMessages = new HashSet<(int Id, string Type)>();

        foreach (var path in binaryPaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"NetworkMessageRttiScanner: input binary not found: '{path}'.", path);
            }

            byte[] data = File.ReadAllBytes(path);
            foreach (var (id, type) in ScanAccepted(data, netMarker, itanium))
            {
                union.TryAdd((id, type), 0);
            }
            foreach (var (id, type) in ScanAccepted(data, userMarker, itanium))
            {
                userMessages.Add((id, type));
            }
        }

        if (union.Count == 0)
        {
            // A real CS2 binary set ALWAYS instantiates CNetMessagePB. Zero is a structural
            // failure (wrong binaries, stripped RTTI, or a decoder regression) — abort the set.
            throw new InvalidDataException(
                "NetworkMessageRttiScanner: decoded ZERO network messages from the input binary set. " +
                "A real CS2 binary set always carries CNetMessagePB RTTI descriptors; refusing to emit " +
                "an empty network_messages.json.");
        }

        // Hardening: every CUserMessagePB (id->type) must agree with the CNetMessagePB table. A
        // divergence is a decode bug / unexpected build — fail loud BEFORE any output.
        CrossValidateUserMessages(union.Keys, userMessages);

        // Group into channels by the fixed type-prefix map. The emitter applies the canonical sort.
        var byChannel = new Dictionary<string, NetworkChannel>(StringComparer.Ordinal);
        foreach (var (id, type) in union.Keys)
        {
            string channelName = ChannelOf(type);
            if (!byChannel.TryGetValue(channelName, out var channel))
            {
                channel = new NetworkChannel { Name = channelName };
                byChannel[channelName] = channel;
            }
            channel.Messages.Add(new NetworkMessageEntry { Id = id, ProtoMessageType = type });
        }

        return byChannel.Values.ToList();
    }

    /// <summary>
    /// Yield every accepted (id, normalised-type) pair for <paramref name="marker"/> from one
    /// binary's bytes: decode via the ABI decoder selected by <paramref name="itanium"/>, then apply
    /// the connectionless (id &lt; 0) filter, the Wrapper normalisation, and the C-name sanity gate.
    /// Both ABIs feed the same filter chain so the linux/windows scans cannot diverge.
    /// </summary>
    private static IEnumerable<(int Id, string Type)> ScanAccepted(byte[] data, string marker, bool itanium)
    {
        IEnumerable<(int Id, string RawType)> raw = itanium
            ? ItaniumRttiTemplateDecoder.ScanMarker(data, marker).Select(d => (d.Id, d.ProtoMessageType))
            : MsvcRttiTemplateDecoder.ScanMarker(data, marker).Select(d => (d.Id, d.ProtoMessageType));

        foreach (var (id, rawType) in raw)
        {
            if (id < 0)
                continue;   // connectionless (C2S_*) — not in the numbered registry.
            string type = Normalize(rawType);
            if (!IsAcceptedTypeName(type))
                continue;
            yield return (id, type);
        }
    }

    /// <summary>
    /// Cross-check: assert every CUserMessagePB (id, type) agrees with the CNetMessagePB table. For
    /// a type registered by both templates, the CUserMessagePB id MUST be one of the
    /// CNetMessagePB ids for that type (the (id,type) duals — e.g. DisconnectToLobby 335/374 — are
    /// preserved as a SET of ids per type). A type CUserMessagePB carries but CNetMessagePB does
    /// not is NOT a failure (no "corresponding" entry to disagree with). Any genuine id mismatch
    /// throws. <paramref name="netMessages"/> is the accepted CNetMessagePB (id,type) set.
    /// </summary>
    internal static void CrossValidateUserMessages(
        IReadOnlyCollection<(int Id, string Type)> netMessages,
        IReadOnlyCollection<(int Id, string Type)> userMessages)
    {
        ArgumentNullException.ThrowIfNull(netMessages);
        ArgumentNullException.ThrowIfNull(userMessages);

        // type -> the set of ids the CNetMessagePB scan registered for it.
        var netTypeIds = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var (id, type) in netMessages)
        {
            if (!netTypeIds.TryGetValue(type, out var ids))
            {
                ids = new HashSet<int>();
                netTypeIds[type] = ids;
            }
            ids.Add(id);
        }

        var divergences = new List<string>();
        foreach (var (id, type) in userMessages)
        {
            if (netTypeIds.TryGetValue(type, out var ids) && !ids.Contains(id))
            {
                divergences.Add(
                    $"{type}: CUserMessagePB id {id} vs CNetMessagePB id(s) " +
                    string.Join("/", ids.OrderBy(x => x)));
            }
        }

        if (divergences.Count > 0)
        {
            divergences.Sort(StringComparer.Ordinal);
            throw new InvalidDataException(
                "NetworkMessageRttiScanner: CUserMessagePB cross-validation FAILED — the user-message " +
                "id<->type table decoded from CUserMessagePB disagrees with the CNetMessagePB table " +
                "(a decode bug or an unexpected build): " + string.Join("; ", divergences));
        }
    }

    // ------------------------------------------------------------------------------------------
    // Type-name normalisation, acceptance, and channel assignment.
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Normalise a RTTI binding class to its wire/proto name. For a tiny set Valve suffixes the
    /// impl class 'Wrapper' while the wire/proto + live GetName() drop it
    /// (CSVCMsg_FlattenedSerializerWrapper -> CSVCMsg_FlattenedSerializer).
    /// </summary>
    internal static string Normalize(string type)
        => type.EndsWith("Wrapper", StringComparison.Ordinal)
           && type.StartsWith("CSVCMsg_", StringComparison.Ordinal)
            ? type[..^"Wrapper".Length]
            : type;

    /// <summary>A real proto message class: starts with 'C', then word chars only (^C[A-Za-z0-9_]+$).</summary>
    internal static bool IsAcceptedTypeName(string type) => MsvcRttiTemplateDecoder.IsProtoClassName(type);

    // Type-prefix -> channel name, in match order (first prefix wins). Mirrors the Python CHAN
    // table exactly. Order matters: the broad 'CUserMessage' must precede nothing that would
    // shadow it, and the CMsg* specialisations precede the 'CMsg' GameEvents catch-all below.
    private static readonly (string Prefix, string Channel)[] ChannelTable =
    {
        ("CNETMsg_", "NetMessages"),
        ("CSVCMsg_", "SvcMessages"),
        ("CCLCMsg_", "ClcMessages"),
        ("CBidirMsg_", "Bidirectional"),
        ("CClientMsg_", "ClientMessages"),
        ("CUserMessage", "UserMessages"),
        ("CCSUsrMsg", "UserMessages"),
        ("CUserMsg_", "UserMessages"),
        ("CEntityMessage", "UserMessages"),
        ("CMsgTE", "TempEntities"),
        ("CMsgSos", "Sounds"),
        ("CMsgSource1Legacy", "Source1Legacy"),
        ("CMsgPlaceDecal", "Decals"),
        ("CMsgClear", "Decals"),
        ("CP2P", "PeerToPeer"),
    };

    /// <summary>
    /// Assign a proto message type to its registered channel by fixed type-prefix. A CMsg* type
    /// that matches no specialisation falls into "GameEvents"; anything else "Other".
    /// </summary>
    internal static string ChannelOf(string type)
    {
        foreach (var (prefix, channel) in ChannelTable)
        {
            if (type.StartsWith(prefix, StringComparison.Ordinal))
                return channel;
        }
        if (type.StartsWith("CMsg", StringComparison.Ordinal))
            return "GameEvents";
        return "Other";
    }
}
