// demo_messages — DemoMessageRttiScanner tests.
//
// Two layers:
//   1. Unit tests over the shared MSVC template-constant decoder against KNOWN CDemoMessagePB
//      mangled tails (CDemoPacket=7 short form, CDemoFullPacket=13 long form), plus the id-15 dual
//      registration (CDemoSpawnGroups + an HLTV-broadcast variant) decoded end-to-end from a
//      synthetic binary, and the fail-loud guards (non-MSVC platform, zero decoded).
//   2. An integration test that runs the scanner over the REAL installed build-23517234 binaries
//      (engine2 + server + client) and asserts the 19 demo messages, the known ids, and the id-15
//      dual. Skips (returns) when the binaries are not present (CI / a machine without CS2).

using System.Text;

using Cs2SchemaTracker.Host.NetworkMessages;

using Xunit;

namespace Cs2SchemaTracker.Tests.NetworkMessages;

public class DemoMessageRttiScannerTest
{
    private const string Platform = "windows-x86_64";

    // Build a synthetic .rdata-shaped blob: each descriptor is `?$CDemoMessagePB@<tail>` separated
    // by a NUL so each printable run is isolated (matching how real descriptors are laid out).
    private static string WriteSyntheticBinary(params string[] tails)
    {
        var sb = new StringBuilder();
        foreach (var t in tails)
        {
            sb.Append("?$CDemoMessagePB@").Append(t).Append('\0');
        }
        var path = Path.Combine(Path.GetTempPath(), "demo-rtti-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    // ---- unit: the shared MSVC template-constant decoder over CDemoMessagePB tails -------------

    [Fact]
    public void Decodes_Short_Id_CDemoPacket_7()
    {
        // $06 : short form digit 6 -> 6 + 1 = 7.
        var d = MsvcRttiTemplateDecoder.Decode("$06VCDemoPacket@@");
        Assert.NotNull(d);
        Assert.Equal(7, d!.Value.Id);
        Assert.Equal("CDemoPacket", d.Value.ProtoMessageType);
    }

    [Fact]
    public void Decodes_Long_Base16_Id_CDemoFullPacket_13()
    {
        // $0N@ : N=13 over base-16 (single hex digit) -> 13.
        var d = MsvcRttiTemplateDecoder.Decode("$0N@VCDemoFullPacket@@");
        Assert.NotNull(d);
        Assert.Equal(13, d!.Value.Id);
        Assert.Equal("CDemoFullPacket", d.Value.ProtoMessageType);
    }

    [Fact]
    public void Decodes_Long_Base16_Zero_CDemoStop_0()
    {
        // $0A@ : A=0 -> 0 (the long form is required for 0 and 11+).
        var d = MsvcRttiTemplateDecoder.Decode("$0A@VCDemoStop@@");
        Assert.NotNull(d);
        Assert.Equal(0, d!.Value.Id);
        Assert.Equal("CDemoStop", d.Value.ProtoMessageType);
    }

    [Fact]
    public void Scan_Keeps_The_Id15_Dual_As_Two_Rows()
    {
        // Two CDemoMessagePB instantiations both at id 15 ($0P@): the spawn-groups command and
        // an HLTV-broadcast variant. Both must survive (union by (id,type)), exactly as the
        // scan keeps the DisconnectToLobby 335/374 dual.
        var path = WriteSyntheticBinary(
            "$0P@VCDemoSpawnGroups@@",
            "$0P@VCDemoSpawnGroupsHLTVBroadcast@@");
        try
        {
            var rows = DemoMessageRttiScanner.Scan(new[] { path }, Platform);
            var id15 = rows.Where(r => r.Id == 15).Select(r => r.ProtoMessageType)
                           .OrderBy(t => t, StringComparer.Ordinal).ToList();
            string[] expected = { "CDemoSpawnGroups", "CDemoSpawnGroupsHLTVBroadcast" };
            Assert.Equal(expected, id15);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Scan_Unions_And_Filters_NonProto_Tails()
    {
        var path = WriteSyntheticBinary(
            "$06VCDemoPacket@@",
            "$06VCDemoPacket@@",      // duplicate -> unioned away.
            "$0N@VCDemoFullPacket@@",
            "garbage-not-a-template", // un-decodable -> dropped.
            "$0A@Vlowercasebad@@");   // not a C-class name -> dropped.
        try
        {
            var rows = DemoMessageRttiScanner.Scan(new[] { path }, Platform);
            var set = rows.Select(r => (r.Id, r.ProtoMessageType)).OrderBy(x => x.Id).ToList();
            Assert.Equal(new[] { (7, "CDemoPacket"), (13, "CDemoFullPacket") }, set);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Unknown_Platform_Is_FailLoud_Unsupported()
        => Assert.Throws<NotSupportedException>(
            () => DemoMessageRttiScanner.Scan(Array.Empty<string>(), "macos-arm64"));

    // ---- unit: the Itanium (linux-x86_64) CDemoMessagePB decoder ------------------------------
    // NOTE the Linux demo id is an EDemoCommands ENUM literal (L13EDemoCommands<n>E), not an int
    // constant like CNetMessagePB — the shared Itanium decoder reads the enum ordinal as the id.
    // The tails below are the exact arg lists after the `14CDemoMessagePBI` marker in libengine2.so.

    [Fact]
    public void Itanium_Decodes_Enum_Id_CDemoStop_0()
    {
        var d = ItaniumRttiTemplateDecoder.Decode("L13EDemoCommands0E9CDemoStopE");
        Assert.NotNull(d);
        Assert.Equal(0, d!.Value.Id);
        Assert.Equal("CDemoStop", d.Value.ProtoMessageType);
    }

    [Fact]
    public void Itanium_Decodes_Enum_Id_CDemoFullPacket_13()
    {
        var d = ItaniumRttiTemplateDecoder.Decode("L13EDemoCommands13E15CDemoFullPacketE");
        Assert.NotNull(d);
        Assert.Equal(13, d!.Value.Id);
        Assert.Equal("CDemoFullPacket", d.Value.ProtoMessageType);
    }

    // Build a synthetic Itanium blob: each descriptor is `14CDemoMessagePBI<tail>` separated by NUL.
    private static string WriteSyntheticItaniumBinary(params string[] tails)
    {
        var sb = new StringBuilder();
        foreach (var t in tails)
        {
            sb.Append("14CDemoMessagePBI").Append(t).Append('\0');
        }
        var path = Path.Combine(Path.GetTempPath(), "demo-itanium-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
        return path;
    }

    [Fact]
    public void Itanium_Scan_Keeps_The_Id15_Dual_As_Two_Rows()
    {
        var path = WriteSyntheticItaniumBinary(
            "L13EDemoCommands15E16CDemoSpawnGroupsE",
            "L13EDemoCommands15E29CDemoSpawnGroupsHLTVBroadcastE");
        try
        {
            var rows = DemoMessageRttiScanner.Scan(new[] { path }, "linux-x86_64");
            var id15 = rows.Where(r => r.Id == 15).Select(r => r.ProtoMessageType)
                           .OrderBy(t => t, StringComparer.Ordinal).ToList();
            string[] expected = { "CDemoSpawnGroups", "CDemoSpawnGroupsHLTVBroadcast" };
            Assert.Equal(expected, id15);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Zero_Decoded_Is_FailLoud()
    {
        var path = WriteSyntheticBinary("not a descriptor at all");
        try
        {
            Assert.Throws<InvalidDataException>(
                () => DemoMessageRttiScanner.Scan(new[] { path }, Platform));
        }
        finally { File.Delete(path); }
    }

    // ---- integration: the real build-23517234 binaries ----------------------------------------

    // Standard default Steam install location on Windows (no per-user path); the test is
    // environment-gated and no-ops when CS2 is not installed here.
    private const string SteamGameDir =
        @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game";

    private static string[]? ResolveRealBinaries()
    {
        var paths = new[]
        {
            Path.Combine(SteamGameDir, "bin", "win64", "engine2.dll"),
            Path.Combine(SteamGameDir, "csgo", "bin", "win64", "server.dll"),
            Path.Combine(SteamGameDir, "csgo", "bin", "win64", "client.dll"),
        };
        return paths.All(File.Exists) ? paths : null;
    }

    [Fact]
    public void Real_Binaries_Produce_19_Demo_Messages_With_Known_Ids_And_The_Id15_Dual()
    {
        var binaries = ResolveRealBinaries();
        if (binaries is null)
        {
            return;   // CS2 not installed — environment-gated integration test.
        }

        var rows = DemoMessageRttiScanner.Scan(binaries, "windows-x86_64");

        // The validated shape: 19 distinct (id, type) demo-command bindings.
        Assert.Equal(19, rows.Count);

        var byId = rows.ToLookup(r => r.Id, r => r.ProtoMessageType);

        // Known anchor ids.
        string[] expectId7 = { "CDemoPacket" };
        string[] expectId13 = { "CDemoFullPacket" };
        Assert.Equal(expectId7, byId[7].ToArray());
        Assert.Equal(expectId13, byId[13].ToArray());

        // The id-15 dual: both messages present.
        var id15 = byId[15].OrderBy(t => t, StringComparer.Ordinal).ToArray();
        string[] expectId15 = { "CDemoSpawnGroups", "CDemoSpawnGroupsHLTVBroadcast" };
        Assert.Equal(expectId15, id15);

        // Every id is in the expected small flat id-space and every type is a C-class proto name.
        Assert.All(rows, r => Assert.InRange(r.Id, 0, 18));
        Assert.All(rows, r => Assert.StartsWith("CDemo", r.ProtoMessageType, StringComparison.Ordinal));
    }

    // ---- integration: the real build-23773332 LINUX .so set vs the committed WINDOWS artifact ----
    // the demo ids are identical across platforms. The Itanium scan of the Linux binaries must
    // reproduce the committed windows-x86_64 demo_messages.json's (id -> proto type) set exactly.
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
    public void Linux_Itanium_Scan_Reproduces_The_Committed_Windows_Demo_Id_Type_Set()
    {
        var binaries = ResolveLinuxBinaries();
        if (binaries is null)
        {
            return;   // cached Linux binaries not present — environment-gated integration test.
        }

        var rows = DemoMessageRttiScanner.Scan(binaries, "linux-x86_64");
        var linuxPairs = rows.Select(r => (r.Id, r.ProtoMessageType)).ToHashSet();

        var windowsPairs = LoadCommittedWindowsDemoMessagePairs();

        var linuxOnly = linuxPairs.Except(windowsPairs).OrderBy(p => p.Item1).Select(p => $"{p.Item1} {p.Item2}").ToList();
        var windowsOnly = windowsPairs.Except(linuxPairs).OrderBy(p => p.Item1).Select(p => $"{p.Item1} {p.Item2}").ToList();
        Assert.True(linuxOnly.Count == 0, "linux-only (id,type): " + string.Join(", ", linuxOnly));
        Assert.True(windowsOnly.Count == 0, "windows-only (id,type): " + string.Join(", ", windowsOnly));
        Assert.Equal(windowsPairs.Count, linuxPairs.Count);
    }

    private static HashSet<(int Id, string Type)> LoadCommittedWindowsDemoMessagePairs()
    {
        string path = Path.Combine(
            FindRepoRoot(), "artifacts", "23773332", "windows-x86_64", "demo_messages.json");
        Assert.True(File.Exists(path), $"committed windows demo_messages.json missing: {path}");

        var pairs = new HashSet<(int Id, string Type)>();
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        foreach (var m in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            int id = m.GetProperty("id").GetInt32();
            string type = m.GetProperty("protoMessageType").GetString()!;
            pairs.Add((id, type));
        }
        return pairs;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(
                    dir.FullName, "artifacts", "23773332", "windows-x86_64", "demo_messages.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "could not locate repo root (artifacts/23773332/windows-x86_64/demo_messages.json).");
    }
}
