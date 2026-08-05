// NetworkMessageRttiScanner tests.
//
// Two layers:
//   1. Unit tests over the MSVC template-constant decoder against KNOWN mangled tails
//      (the $0BCN@ = 301 CCSUsrMsg_VGUIMenu case, the Wrapper-suffix normalisation, the
//      nested CBaseCmdKeyValues<Inner> form, negative group, connectionless negative id),
//      plus the channel-assignment map.
//   2. An integration test that runs the scanner over the REAL installed build-23517234
//      binaries (engine2 + networksystem + server + client) and asserts it reproduces the
//      live listen-capture membership + ids (live-only = 0, ids byte-identical) and the
//      194-message / 12-channel shape. The live-capture oracle is a COMMITTED fixture
//      (NetworkMessages/fixtures/captured-23517234-listen.txt). The 194/12 shape and the
//      byte-identical ids are only valid for that exact Steam build, so the test is
//      env-gated on BOTH the SteamGameDir binaries AND the installed Steam build id: it
//      returns (skips) when CS2 is not installed OR when Steam has auto-updated the local
//      install to a different build (whose network-message set legitimately differs).

using Cs2SchemaTracker.Host.NetworkMessages;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.NetworkMessages;

public class NetworkMessageRttiScannerTest
{
    // ---- unit: the MSVC template-constant decoder ----------------------------------------

    [Fact]
    public void Decodes_Long_Base16_Id_And_Simple_Type()
    {
        // $0BCN@  : B=1,C=2,N=13 over base-16 -> 1*16*16 + 2*16 + 13 = 301.
        var d = NetworkMessageRttiScanner.Decode("$0BCN@VCCSUsrMsg_VGUIMenu@@");
        Assert.NotNull(d);
        Assert.Equal(301, d!.Value.Id);
        Assert.Equal("CCSUsrMsg_VGUIMenu", d.Value.ProtoMessageType);
        Assert.Equal("UserMessages", NetworkMessageRttiScanner.ChannelOf(d.Value.ProtoMessageType));
    }

    [Fact]
    public void Decodes_Short_Id_Digit_Plus_One()
    {
        // $06 : short form digit 6 -> 6 + 1 = 7.
        var d = NetworkMessageRttiScanner.Decode("$06VCNETMsg_Tick@@");
        Assert.NotNull(d);
        Assert.Equal(7, d!.Value.Id);
        Assert.Equal("CNETMsg_Tick", d.Value.ProtoMessageType);
        Assert.Equal("NetMessages", NetworkMessageRttiScanner.ChannelOf(d.Value.ProtoMessageType));
    }

    [Fact]
    public void Normalises_The_Wrapper_Suffix()
    {
        // $0CJ@ : C=2,J=9 -> 2*16 + 9 = 41. RAW binding class keeps 'Wrapper'.
        var d = NetworkMessageRttiScanner.Decode("$0CJ@VCSVCMsg_FlattenedSerializerWrapper@@");
        Assert.NotNull(d);
        Assert.Equal(41, d!.Value.Id);
        Assert.Equal("CSVCMsg_FlattenedSerializerWrapper", d.Value.ProtoMessageType);
        // The wire/proto name drops 'Wrapper'.
        Assert.Equal("CSVCMsg_FlattenedSerializer",
            NetworkMessageRttiScanner.Normalize(d.Value.ProtoMessageType));
        // Only CSVCMsg_*Wrapper is normalised; an unrelated *Wrapper stays put.
        Assert.Equal("CFooWrapper", NetworkMessageRttiScanner.Normalize("CFooWrapper"));
    }

    [Fact]
    public void Unwraps_Nested_Templated_Type_To_Inner_Class()
    {
        // $0CC@ : C=2,C=2 -> 2*16 + 2 = 34. Templated wrapper -> proto name == innermost class.
        var d = NetworkMessageRttiScanner.Decode("$0CC@V?$CBaseCmdKeyValues@VCCLCMsg_CmdKeyValues@@@@");
        Assert.NotNull(d);
        Assert.Equal(34, d!.Value.Id);
        Assert.Equal("CCLCMsg_CmdKeyValues", d.Value.ProtoMessageType);
        Assert.Equal("ClcMessages", NetworkMessageRttiScanner.ChannelOf(d.Value.ProtoMessageType));
    }

    [Fact]
    public void Decodes_Negative_Group_Sentinel()
    {
        // After the type: $0?0 = -(0+1) = -1 group; $00 = 1 reliable.
        var d = NetworkMessageRttiScanner.Decode("$00VCSVCMsg_ServerInfo@@$0?0$00");
        Assert.NotNull(d);
        Assert.Equal(1, d!.Value.Id);              // $00 short form -> 0 + 1 = 1.
        Assert.Equal("CSVCMsg_ServerInfo", d.Value.ProtoMessageType);
        Assert.Equal(-1, d.Value.Group);
        Assert.Equal(1, d.Value.Reliable);
    }

    [Fact]
    public void Decodes_Connectionless_Negative_Id()
    {
        // $0?9 : leading '?' -> negative; short 9 -> 9 + 1 = 10 -> -10. Filtered (id < 0) by Scan.
        var d = NetworkMessageRttiScanner.Decode("$0?9VC2S_ConnectRequest@@");
        Assert.NotNull(d);
        Assert.True(d!.Value.Id < 0);
    }

    [Theory]
    [InlineData("CNETMsg_NOP", "NetMessages")]
    [InlineData("CSVCMsg_ServerInfo", "SvcMessages")]
    [InlineData("CCLCMsg_Move", "ClcMessages")]
    [InlineData("CBidirMsg_PredictionEvent", "Bidirectional")]
    [InlineData("CClientMsg_CustomGameEvent", "ClientMessages")]
    [InlineData("CUserMessageSayText2", "UserMessages")]
    [InlineData("CCSUsrMsg_VGUIMenu", "UserMessages")]
    [InlineData("CUserMsg_ParticleManager", "UserMessages")]
    [InlineData("CEntityMessageDoSpark", "UserMessages")]
    [InlineData("CMsgTEFireBullets", "TempEntities")]
    [InlineData("CMsgSosStartSoundEvent", "Sounds")]
    [InlineData("CMsgSource1LegacyGameEvent", "Source1Legacy")]
    [InlineData("CMsgPlaceDecalEvent", "Decals")]
    [InlineData("CMsgClearWorldDecalsEvent", "Decals")]
    [InlineData("CP2P_Voice", "PeerToPeer")]
    [InlineData("CMsgPlayerBulletHit", "GameEvents")]   // CMsg* catch-all
    [InlineData("CWhatever", "Other")]
    public void Assigns_Channel_By_Type_Prefix(string type, string expectedChannel)
        => Assert.Equal(expectedChannel, NetworkMessageRttiScanner.ChannelOf(type));

    [Fact]
    public void Returns_Null_On_NonCNetMessage_Tail()
        => Assert.Null(NetworkMessageRttiScanner.Decode("garbage-not-a-template"));

    [Fact]
    public void Unknown_Platform_Is_FailLoud_Unsupported()
        => Assert.Throws<NotSupportedException>(
            () => NetworkMessageRttiScanner.Scan(Array.Empty<string>(), "macos-arm64"));

    // ---- unit: the Itanium (linux-x86_64) template-constant decoder ------------------------
    // Same messages, same ids — only the demangle differs. The tails below are the exact
    // Itanium type_info-name arg lists present in the CS2 Linux `.so` binaries (the substring right
    // after the `13CNetMessagePBI` marker).

    [Fact]
    public void Itanium_Decodes_Int_Id_And_Simple_Type()
    {
        // Li101E = int 101; then the message type; then the group/reliable/flag literals.
        var d = NetworkMessageRttiScanner.DecodeItanium(
            "Li101E28CUserMessageAchievementEventL13SignonGroup_t13EL19NetChannelBufType_t1ELb1EE");
        Assert.NotNull(d);
        Assert.Equal(101, d!.Value.Id);
        Assert.Equal("CUserMessageAchievementEvent", d.Value.ProtoMessageType);
        Assert.Equal(13, d.Value.Group);
        Assert.Equal(1, d.Value.Reliable);
        Assert.Equal(1, d.Value.Flag);
        Assert.Equal("UserMessages", NetworkMessageRttiScanner.ChannelOf(d.Value.ProtoMessageType));
    }

    [Fact]
    public void Itanium_Decodes_The_Documented_CmdKeyValues_Example()
    {
        // The exact example from the Itanium task: id 52 (Li52E) wrapped
        // CBaseCmdKeyValues<CSVCMsg_CmdKeyValues> -> the innermost class is the proto type.
        var d = NetworkMessageRttiScanner.DecodeItanium(
            "Li52E17CBaseCmdKeyValuesI20CSVCMsg_CmdKeyValuesEL13SignonGroup_t9EL19NetChannelBufType_t1ELb0EE");
        Assert.NotNull(d);
        Assert.Equal(52, d!.Value.Id);
        Assert.Equal("CSVCMsg_CmdKeyValues", d.Value.ProtoMessageType);
        Assert.Equal(9, d.Value.Group);
        Assert.Equal(1, d.Value.Reliable);
        Assert.Equal(0, d.Value.Flag);
        Assert.Equal("SvcMessages", NetworkMessageRttiScanner.ChannelOf(d.Value.ProtoMessageType));
    }

    [Fact]
    public void Itanium_Normalises_The_FlattenedSerializer_Wrapper_Suffix()
    {
        // id 41 ships on Linux as the plain (non-template) CSVCMsg_FlattenedSerializerWrapper; the
        // shared Normalize drops 'Wrapper' just like the MSVC path (both platforms land on the same
        // proto spelling —).
        var d = NetworkMessageRttiScanner.DecodeItanium(
            "Li41E34CSVCMsg_FlattenedSerializerWrapperL13SignonGroup_t10EL19NetChannelBufType_t1ELb0EE");
        Assert.NotNull(d);
        Assert.Equal(41, d!.Value.Id);
        Assert.Equal("CSVCMsg_FlattenedSerializerWrapper", d.Value.ProtoMessageType);
        Assert.Equal("CSVCMsg_FlattenedSerializer",
            NetworkMessageRttiScanner.Normalize(d.Value.ProtoMessageType));
    }

    [Fact]
    public void Itanium_Returns_Null_On_Non_Literal_Tail()
        => Assert.Null(NetworkMessageRttiScanner.DecodeItanium("28CUserMessageAchievementEvent"));

    // ---- unit: the CUserMessagePB cross-validator (hardening) ----------------------

    [Fact]
    public void CrossValidate_Agrees_When_User_Ids_Match_Net_Ids()
    {
        // CUserMessagePB carries the SAME (id, type) the CNetMessagePB scan derived for the
        // user-message family — agreement, no throw. A type CUserMessagePB carries but
        // CNetMessagePB does not (CUserMsg_Orphan) is NOT a disagreement (no corresponding entry).
        var net = new[] { (301, "CCSUsrMsg_VGUIMenu"), (118, "CUserMessageSayText2") };
        var user = new[] { (301, "CCSUsrMsg_VGUIMenu"), (118, "CUserMessageSayText2"), (999, "CUserMsg_Orphan") };
        NetworkMessageRttiScanner.CrossValidateUserMessages(net, user);   // does not throw.
    }

    [Fact]
    public void CrossValidate_Tolerates_Per_Type_Id_Duals()
    {
        // A type registered under two ids by CNetMessagePB (the DisconnectToLobby 335/374 dual):
        // a CUserMessagePB id matching EITHER of the set is agreement.
        var net = new[] { (335, "CSVCMsg_DisconnectToLobby"), (374, "CSVCMsg_DisconnectToLobby") };
        var user = new[] { (374, "CSVCMsg_DisconnectToLobby") };
        NetworkMessageRttiScanner.CrossValidateUserMessages(net, user);   // does not throw.
    }

    [Fact]
    public void CrossValidate_FailLoud_On_Id_Disagreement()
    {
        // CUserMessagePB says id 999 for a type the CNetMessagePB scan registered at 301 -> a real
        // divergence (decode bug / unexpected build) -> fail loud.
        var net = new[] { (301, "CCSUsrMsg_VGUIMenu") };
        var user = new[] { (999, "CCSUsrMsg_VGUIMenu") };
        var ex = Assert.Throws<InvalidDataException>(
            () => NetworkMessageRttiScanner.CrossValidateUserMessages(net, user));
        Assert.Contains("CCSUsrMsg_VGUIMenu", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cross-validation FAILED", ex.Message, StringComparison.Ordinal);
    }

    // ---- integration: the real build-23517234 binaries vs the live oracle ----------------

    // Standard default Steam install location on Windows (no per-user path); the test is
    // environment-gated and no-ops when CS2 is not installed here.
    private const string SteamGameDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game";

    // The exact Steam depot build the committed live-capture oracle was recorded against.
    // The integration test scans the LOCALLY INSTALLED binaries, which Steam auto-updates in
    // place; the 194/12 shape and the byte-identical ids only hold for this build. On a newer
    // build the message set legitimately differs (e.g. build 24304127 dropped
    // CUserMessageCloseCaption[102]/CUserMessageCloseCaptionDirect[103]/
    // CEntityMessageRemoveAllDecals[138]/CClientMsg_ListenForResponseFound[286]), so the test
    // skips a mismatched install rather than failing on a build it has no oracle for.
    private const string OracleBuildId = "23517234";

    // Committed live-capture oracle (host/tests/.../NetworkMessages/fixtures/), copied to
    // the test output dir by the csproj <Content> include and resolved here via the assembly
    // base dir — a STABLE repo-relative path. (It was previously hardcoded to an absolute path
    // on one developer's machine, outside the repo, so the 194/12 cross-validation silently
    // no-op'd on every machine, including CI.)
    private static readonly string ListenOracle =
        Path.Combine(AppContext.BaseDirectory, "NetworkMessages", "fixtures", "captured-23517234-listen.txt");

    private static string[]? ResolveRealBinaries()
    {
        var paths = new[]
        {
            Path.Combine(SteamGameDir, "bin", "win64", "engine2.dll"),
            Path.Combine(SteamGameDir, "bin", "win64", "networksystem.dll"),
            Path.Combine(SteamGameDir, "csgo", "bin", "win64", "server.dll"),
            Path.Combine(SteamGameDir, "csgo", "bin", "win64", "client.dll"),
        };
        return paths.All(File.Exists) ? paths : null;
    }

    // The build id Steam recorded for the local install, read from the app manifest that sits
    // three levels above SteamGameDir (steamapps/appmanifest_730.acf). Returns null when the
    // manifest is absent or carries no "buildid" line.
    private static string? InstalledBuildId()
    {
        string manifest = Path.GetFullPath(
            Path.Combine(SteamGameDir, "..", "..", "..", "appmanifest_730.acf"));
        if (!File.Exists(manifest))
        {
            return null;
        }

        foreach (var raw in File.ReadAllLines(manifest))
        {
            // A Valve VDF line: "buildid"\t\t"24304127" — the last quoted token is the value.
            string line = raw.Trim();
            if (!line.StartsWith("\"buildid\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] tokens = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length >= 2 ? tokens[^1].Trim() : null;
        }

        return null;
    }

    [Fact]
    public void Real_Binaries_Reproduce_Live_Oracle_And_194_12_Shape()
    {
        var binaries = ResolveRealBinaries();
        if (binaries is null)
        {
            return;   // CS2 not installed — environment-gated integration test (SteamGameDir).
        }

        if (InstalledBuildId() != OracleBuildId)
        {
            return;   // Steam auto-updated the local install off the oracle build — skip cleanly.
        }

        // The oracle is a committed fixture: if it's missing, that's a real defect (a broken
        // <Content> copy), NOT a reason to silently skip the cross-validation.
        Assert.True(File.Exists(ListenOracle),
            $"committed RTTI live-capture oracle fixture is missing: {ListenOracle}");

        IReadOnlyList<NetworkChannel> channels =
            NetworkMessageRttiScanner.Scan(binaries, "windows-x86_64");

        // The validated shape: 194 messages across 12 channels.
        Assert.Equal(12, channels.Count);
        Assert.Equal(194, channels.Sum(c => c.Messages.Count));

        // Scan (id, type) pairs.
        var scanPairs = new HashSet<(int Id, string Type)>();
        var scanNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ch in channels)
        {
            foreach (var m in ch.Messages)
            {
                scanPairs.Add((m.Id, m.ProtoMessageType));
                scanNames.Add(m.ProtoMessageType);
            }
        }

        // Parse the live capture: "id<TAB>Name [id]<TAB>group" -> (id, name) pairs.
        var livePairs = new HashSet<(int Id, string Name)>();
        var liveNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(ListenOracle))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split('\t');
            if (parts.Length < 2)
                continue;
            int id = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            string name = parts[1].Split(" [")[0].Trim();
            livePairs.Add((id, name));
            liveNames.Add(name);
        }

        // live-only = 0: every live message name is also in the offline scan.
        var liveOnly = liveNames.Where(n => !scanNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.True(liveOnly.Count == 0, "live-only names (in live, absent from scan): " + string.Join(", ", liveOnly));

        // Every live (id, name) is present in the scan with a byte-identical id.
        var idMismatch = livePairs.Where(p => !scanPairs.Contains(p))
            .OrderBy(p => p.Id).Select(p => $"{p.Id} {p.Name}").ToList();
        Assert.True(idMismatch.Count == 0, "live (id,name) not matched in scan: " + string.Join(", ", idMismatch));
    }

    // ---- integration: the real build-23773332 LINUX .so set vs the committed WINDOWS artifact ----
    // the ids are identical across platforms. The Itanium scan of the Linux binaries must
    // reproduce the committed windows-x86_64 network_messages.json's (id -> proto type) set exactly.
    // Env-gated on the cached Linux binary tree (root from $CS2_BINARIES_ROOT); returns
    // (skips) when the env var is unset or the tree is absent.

    private static readonly string? LinuxBinRoot =
        Environment.GetEnvironmentVariable("CS2_BINARIES_ROOT") is { Length: > 0 } root
            ? Path.Combine(root, "23773332", "linux-x86_64", "game")
            : null;

    private static string[]? ResolveLinuxBinaries()
    {
        if (LinuxBinRoot is null)
            return null;
        var dirs = new[]
        {
            Path.Combine(LinuxBinRoot, "bin", "linuxsteamrt64"),
            Path.Combine(LinuxBinRoot, "csgo", "bin", "linuxsteamrt64"),
        };
        if (!dirs.All(Directory.Exists))
            return null;
        return dirs.SelectMany(d => Directory.EnumerateFiles(d, "*.so"))
                   .OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public void Linux_Itanium_Scan_Reproduces_The_Committed_Windows_Id_Type_Set()
    {
        var binaries = ResolveLinuxBinaries();
        if (binaries is null)
        {
            return;   // cached Linux binaries not present — environment-gated integration test.
        }

        IReadOnlyList<NetworkChannel> channels =
            NetworkMessageRttiScanner.Scan(binaries, "linux-x86_64");

        Assert.Equal(12, channels.Count);
        Assert.Equal(194, channels.Sum(c => c.Messages.Count));

        var linuxPairs = new HashSet<(int Id, string Type)>();
        foreach (var ch in channels)
        {
            foreach (var m in ch.Messages)
                linuxPairs.Add((m.Id, m.ProtoMessageType));
        }

        var windowsPairs = LoadCommittedWindowsNetworkMessagePairs();

        var linuxOnly = linuxPairs.Except(windowsPairs).OrderBy(p => p.Id).Select(p => $"{p.Id} {p.Type}").ToList();
        var windowsOnly = windowsPairs.Except(linuxPairs).OrderBy(p => p.Id).Select(p => $"{p.Id} {p.Type}").ToList();
        Assert.True(linuxOnly.Count == 0, "linux-only (id,type): " + string.Join(", ", linuxOnly));
        Assert.True(windowsOnly.Count == 0, "windows-only (id,type): " + string.Join(", ", windowsOnly));
    }

    private static HashSet<(int Id, string Type)> LoadCommittedWindowsNetworkMessagePairs()
    {
        string path = Path.Combine(
            FindRepoRoot(), "artifacts", "23773332", "windows-x86_64", "network_messages.json");
        Assert.True(File.Exists(path), $"committed windows network_messages.json missing: {path}");

        var pairs = new HashSet<(int Id, string Type)>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var channel in doc.RootElement.GetProperty("channels").EnumerateArray())
        {
            if (!channel.TryGetProperty("messages", out var messages))
                continue;
            foreach (var m in messages.EnumerateArray())
            {
                int id = m.GetProperty("id").GetInt32();
                string type = m.GetProperty("protoMessageType").GetString()!;
                pairs.Add((id, type));
            }
        }
        return pairs;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(
                    dir.FullName, "artifacts", "23773332", "windows-x86_64", "network_messages.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "could not locate repo root (artifacts/23773332/windows-x86_64/network_messages.json).");
    }
}
