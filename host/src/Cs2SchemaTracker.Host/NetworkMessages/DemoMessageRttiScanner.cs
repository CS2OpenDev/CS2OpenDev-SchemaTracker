// demo_messages — Offline RTTI demo-command scanner (demo_messages.json source of record).
//
// Decodes the per-build `.dem` demo-command id<->type table directly from the shipped binaries by
// parsing every `CDemoMessagePB<id, MessageType>` MSVC RTTI type descriptor (`?$CDemoMessagePB@`
// in .rdata). No DLL load, no engine launch — a pure File.ReadAllBytes + RTTI parse over the SAME
// input binaries the rest of the set is built from, reusing the SAME validated MSVC mangle decoder
// (MsvcRttiTemplateDecoder) as the CNetMessagePB scan. A demo command appears IFF the build
// instantiated CDemoMessagePB for it (a static binding => linked); the 19 instantiations live in
// engine2.dll. CDemoMessagePB has only (id, type) template args — the decoder's surplus
// group/reliable/flag slots are simply ignored here.
//
// DUAL-ID (id 15): two messages bind id 15 (the spawn-groups command + an HLTV-broadcast variant).
// Both are kept — union by (id, type) preserves them as two distinct rows, exactly as the
// scan keeps the DisconnectToLobby 335/374 dual.
//
// PLATFORM SCOPE: BOTH committed platforms (same as the network scanner). windows-x86_64 uses
// the MSVC `?$CDemoMessagePB@` mangling; linux-x86_64 uses the Itanium `14CDemoMessagePBI...`
// type_info-name mangling. NOTE the Itanium demo id is carried as an EDemoCommands ENUM literal
// (`L13EDemoCommands13E`), not an int constant like CNetMessagePB — the shared Itanium decoder reads
// the enum ordinal as the id. the ids are identical across platforms; validated 19/19 vs
// the windows set on build 23773332. FAIL LOUD on any other platform.
//
// Invariants:
// Determinism: output is a pure function of the input bytes; entries are unioned by
//          (id, type) across the binary set. The emitter applies the final canonical sort.
// Fail-loud: an unknown platform, or zero decoded entries across the whole set (engine2
//          always carries CDemoMessagePB RTTI), throws BEFORE any artifact byte.

namespace Cs2SchemaTracker.Host.NetworkMessages;

/// <summary>
/// Scans a build's input binaries for `CDemoMessagePB&lt;id, type&gt;` RTTI type descriptors (MSVC on
/// windows-x86_64, Itanium on linux-x86_64) and produces the per-build `.dem` demo-command
/// id->type table.
/// </summary>
internal sealed class DemoMessageRttiScanner
{
    /// <summary>The two platforms whose RTTI mangling this scanner decodes.</summary>
    public const string WindowsPlatform = "windows-x86_64";
    public const string LinuxPlatform = "linux-x86_64";

    // The RTTI type-descriptor markers for the CDemoMessagePB template, per ABI.
    private const string MsvcMarker = "?$CDemoMessagePB@";
    private const string ItaniumMarker = "14CDemoMessagePBI";

    /// <summary>One decoded demo-command binding: numeric id -> proto message type name.</summary>
    internal readonly record struct DemoEntry(int Id, string ProtoMessageType);

    /// <summary>
    /// Scan <paramref name="binaryPaths"/> and return the registered demo-command id->type set.
    /// Fail-loud: non-MSVC platform, or zero decoded entries.
    /// </summary>
    public static IReadOnlyList<DemoEntry> Scan(IReadOnlyList<string> binaryPaths, string platform)
    {
        ArgumentNullException.ThrowIfNull(binaryPaths);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        bool itanium = platform switch
        {
            WindowsPlatform => false,   // MSVC mangling.
            LinuxPlatform => true,      // Itanium mangling.
            // never guess a mangling we have not implemented + validated.
            _ => throw new NotSupportedException(
                $"DemoMessageRttiScanner: platform '{platform}' is not supported. Only " +
                $"'{WindowsPlatform}' (MSVC mangling) and '{LinuxPlatform}' (Itanium mangling) are " +
                "implemented; refusing to scan an unknown platform rather than mis-decode."),
        };
        string marker = itanium ? ItaniumMarker : MsvcMarker;

        // Union by (id, type) across the whole binary set (the instantiations live in engine2;
        // unioning the full set is robust to which binary carries them and dedupes any repeat).
        var union = new HashSet<(int Id, string Type)>();

        foreach (var path in binaryPaths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"DemoMessageRttiScanner: input binary not found: '{path}'.", path);
            }

            byte[] data = File.ReadAllBytes(path);
            IEnumerable<(int Id, string Type)> decoded = itanium
                ? ItaniumRttiTemplateDecoder.ScanMarker(data, marker).Select(d => (d.Id, d.ProtoMessageType))
                : MsvcRttiTemplateDecoder.ScanMarker(data, marker).Select(d => (d.Id, d.ProtoMessageType));

            foreach (var (id, type) in decoded)
            {
                if (id < 0)
                    continue; // no negative demo ids; mirror the sanity filter.
                if (!MsvcRttiTemplateDecoder.IsProtoClassName(type))
                    continue;
                union.Add((id, type));
            }
        }

        if (union.Count == 0)
        {
            // engine2 ALWAYS instantiates CDemoMessagePB. Zero is a structural failure (wrong
            // binaries, stripped RTTI, or a decoder regression) — abort the set.
            throw new InvalidDataException(
                "DemoMessageRttiScanner: decoded ZERO demo messages from the input binary set. " +
                "A real CS2 binary set always carries CDemoMessagePB RTTI descriptors (engine2); " +
                "refusing to emit an empty demo_messages.json.");
        }

        return union.Select(e => new DemoEntry(e.Id, e.Type)).ToList();
    }
}
