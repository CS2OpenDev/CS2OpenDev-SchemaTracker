// config locates inputs but never LEAKS into artifact bytes.
//
// Extends the determinism contract to the config layer: the configured
// binaries-store location (CS2_BINARIES_ROOT) only LOCATES the input binaries; it must never
// reach an emitted artifact byte. Two full extracts of the SAME fixture binaries, run with the
// store rooted at two DIFFERENT absolute directories (each holding byte-identical inputs), must
// produce byte-identical artifact sets — and no configured value / absolute path may appear in
// provenance.json (which relativizes its input paths independently of this layer).
//
// Driven through the ExtractCommand fake-runner seam: TryResolveBinariesDir reads
// HostConfig.BinariesRoot (CS2_BINARIES_ROOT, live) first, so pointing it at store-a vs store-b
// exercises exactly the config-location path. Deterministic: throwaway temp dirs, cwd pinned to a
// neutral empty dir (so the default cache/ path can't shadow the override), env snapshot+restore.

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Tests.Cli;   // FakeWalkerRunner

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Config;

[Collection("config-no-leak")]
public sealed class ConfigNoLeakTest
{
    private const string BuildId = "c0ffee01";

    private static string? MatchingPlatform()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-x86_64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows-x86_64";
        return null;
    }

    // Lay down a store root holding cache-layout binaries at <root>/<build>/<platform>/ (the
    // acquirer's --out convention, which CS2_BINARIES_ROOT resolution mirrors). The two stores
    // are byte-identical inputs at DIFFERENT absolute paths.
    private static void WriteStore(string storeRoot, string platform)
    {
        var dir = Path.Combine(storeRoot, BuildId, platform);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "libserver.so"),
            WithEmbeddedFdp(BuildElf(), BuildFdp("netmessages.proto")));
        File.WriteAllBytes(Path.Combine(dir, "client.dll"),
            WithEmbeddedFdp(BuildPe(), BuildFdp("networkbasetypes.proto")));
    }

    // Run one full extract with CS2_BINARIES_ROOT pointed at storeRoot, cwd pinned to a neutral
    // empty dir, output into a fresh dir; return every produced file keyed by set-relative path.
    private static Dictionary<string, byte[]> ExtractWithStore(string storeRoot, string platform)
    {
        var captured = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        var neutralCwd = Path.Combine(Path.GetTempPath(), "cfgleak-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(neutralCwd);
        var outDir = Path.Combine(Path.GetTempPath(), "cfgleak-out-" + Guid.NewGuid().ToString("N"));

        var prevCwd = Directory.GetCurrentDirectory();
        var prevBins = Environment.GetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar);
        Environment.SetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar, storeRoot);
        Directory.SetCurrentDirectory(neutralCwd);
        try
        {
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform, "--out", outDir }, () => fake);
            Assert.Equal(0, code);

            foreach (var f in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(outDir, f).Replace('\\', '/');
                captured[rel] = File.ReadAllBytes(f);
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            Environment.SetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar, prevBins);
            try
            { Directory.Delete(neutralCwd, recursive: true); }
            catch { /* best effort */ }
            try
            { Directory.Delete(outDir, recursive: true); }
            catch { /* best effort */ }
        }
        return captured;
    }

    // Drives a full `extract` (RTTI network-message stage): windows-x86_64 fixtures only.
    [Cs2SchemaTracker.Tests.WindowsOnlyFact]
    public void Two_Configured_Store_Locations_Produce_Byte_Identical_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var storeA = Path.Combine(Path.GetTempPath(), "cfgleak-storeA-" + Guid.NewGuid().ToString("N"));
        var storeB = Path.Combine(Path.GetTempPath(), "cfgleak-storeB-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteStore(storeA, platform);
            WriteStore(storeB, platform);

            var a = ExtractWithStore(storeA, platform);
            var b = ExtractWithStore(storeB, platform);

            // Same set of files, byte-identical contents — the configured location LOCATED the
            // inputs but did not LEAK into any artifact byte.
            Assert.Equal(
                a.Keys.OrderBy(k => k, StringComparer.Ordinal),
                b.Keys.OrderBy(k => k, StringComparer.Ordinal));
            foreach (var key in a.Keys)
            {
                Assert.Equal(a[key], b[key]);
            }
        }
        finally
        {
            try
            { Directory.Delete(storeA, recursive: true); }
            catch { /* best effort */ }
            try
            { Directory.Delete(storeB, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // Drives a full `extract` (RTTI network-message stage): windows-x86_64 fixtures only.
    [Cs2SchemaTracker.Tests.WindowsOnlyFact]
    public void Configured_Store_Path_Does_Not_Appear_In_Provenance()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // A store root with a DISTINCTIVE absolute path; assert that path never shows up in
        // provenance.json (inputs are recorded depot-relative, not as the configured location).
        var marker = "cfgleak-MARKER-" + Guid.NewGuid().ToString("N");
        var storeRoot = Path.Combine(Path.GetTempPath(), marker);
        try
        {
            WriteStore(storeRoot, platform);
            var produced = ExtractWithStore(storeRoot, platform);

            // --out is an output ROOT now: the set nests under <out>/<build>/<platform>/.
            var provText = System.Text.Encoding.UTF8.GetString(
                produced[$"{BuildId}/{platform}/provenance.json"]);
            Assert.DoesNotContain(marker, provText, StringComparison.Ordinal);
            Assert.DoesNotContain(storeRoot, provText, StringComparison.Ordinal);
            // Inputs are recorded by bare depot-relative name, never an absolute path.
            Assert.Contains("libserver.so", provText, StringComparison.Ordinal);
            Assert.DoesNotContain(
                Path.Combine(storeRoot, BuildId, platform), provText, StringComparison.Ordinal);
        }
        finally
        {
            try
            { Directory.Delete(storeRoot, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ---- canned walk + input-binary helpers (mirror ExtractCommandTest) ---------------------

    private static Cs2SchemaTracker.Schemas.WalkerOutput CannedWalkerOutput(string platform)
    {
        var walk = new Cs2SchemaTracker.Schemas.EntitySchemaWalk();
        var universe = new Cs2SchemaTracker.Schemas.RegistryUniverse();
        void Obs(string symbol, string module, string category) =>
            universe.Symbols.Add(new Cs2SchemaTracker.Schemas.ObservedRegistrySymbol
            { Symbol = symbol, Module = module, Category = category });

        var clientCls = new Cs2SchemaTracker.Schemas.SchemaClass { Name = "C_BaseEntity", Module = "client", Size = 8 };
        clientCls.Fields.Add(new Cs2SchemaTracker.Schemas.SchemaField
        {
            Name = "m_iHealth",
            Offset = 0,
            Type = new Cs2SchemaTracker.Schemas.SchemaType
            { Category = Cs2SchemaTracker.Schemas.SchemaType.Types.Category.Builtin, Name = "int32" },
        });
        var serverCls = new Cs2SchemaTracker.Schemas.SchemaClass { Name = "CBaseEntity", Module = "server", Size = 8 };
        serverCls.Fields.Add(new Cs2SchemaTracker.Schemas.SchemaField
        {
            Name = "m_iMaxHealth",
            Offset = 4,
            Type = new Cs2SchemaTracker.Schemas.SchemaType
            { Category = Cs2SchemaTracker.Schemas.SchemaType.Types.Category.Builtin, Name = "int32" },
        });
        walk.Classes.Add(clientCls);
        walk.Classes.Add(serverCls);
        Obs("C_BaseEntity", "client", "schema_class");
        Obs("CBaseEntity", "server", "schema_class");

        var en = new Cs2SchemaTracker.Schemas.SchemaEnum { Name = "MoveType_t", Module = "server", Alignment = "uint8_t" };
        en.Members.Add(new Cs2SchemaTracker.Schemas.SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        walk.Enums.Add(en);
        Obs("MoveType_t", "server", "schema_enum");

        var convars = new Cs2SchemaTracker.Schemas.ConVarsWalk();
        convars.Convars.Add(new Cs2SchemaTracker.Schemas.ConVar { Name = "sv_cheats", Default = "0", Description = "Allow cheats" });
        Obs("sv_cheats", "", "convar");
        var commands = new Cs2SchemaTracker.Schemas.CommandsWalk();
        commands.Commands.Add(new Cs2SchemaTracker.Schemas.Command { Name = "kill", Description = "Commit suicide" });
        Obs("kill", "", "command");
        var netmsg = new Cs2SchemaTracker.Schemas.NetworkMessagesWalk();
        var channel = new Cs2SchemaTracker.Schemas.NetworkChannel { Name = "NetMessages" };
        channel.Messages.Add(new Cs2SchemaTracker.Schemas.NetworkMessageEntry { Id = 7, ProtoMessageType = "CNETMsg_Tick" });
        netmsg.Channels.Add(channel);
        Obs("CNETMsg_Tick", "NetMessages", "network_message");
        var engineConstants = new Cs2SchemaTracker.Schemas.EngineConstantsWalk();
        engineConstants.Constants.Add(new Cs2SchemaTracker.Schemas.EngineConstant
        { Name = "MAX_PLAYERS", Source = "schema_enum:server.dll/CGameRules", IntValue = 64 });
        Obs("MAX_PLAYERS", "server.dll", "engine_constant");
        var stringPools = new Cs2SchemaTracker.Schemas.StringPoolsWalk();
        var symPool = new Cs2SchemaTracker.Schemas.StringPool { Name = "CUtlSymbolLarge" };
        symPool.Entries.Add("m_iHealth");
        stringPools.Pools.Add(symPool);
        Obs("CUtlSymbolLarge", "", "string_pool");

        return new Cs2SchemaTracker.Schemas.WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-fake",
            Platform = platform,
            EntitySchema = walk,
            Convars = convars,
            Commands = commands,
            NetworkMessages = netmsg,
            EngineConstants = engineConstants,
            StringPools = stringPools,
            RegistryUniverse = universe,
            SchemaSystemLayoutSignature = "sig-fake",
        };
    }

    private static Google.Protobuf.Reflection.FileDescriptorProto BuildFdp(string name)
        => new()
        {
            Name = name,
            Package = "test",
            Syntax = "proto3",
            MessageType =
            {
                new Google.Protobuf.Reflection.DescriptorProto
                {
                    Name = "Msg",
                    Field =
                    {
                        new Google.Protobuf.Reflection.FieldDescriptorProto
                        {
                            Name = "x",
                            Number = 1,
                            Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type.Int32,
                            Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label.Optional,
                            JsonName = "x",
                        },
                    },
                },
            },
        };

    private static byte[] WithEmbeddedFdp(byte[] baseBinary, Google.Protobuf.Reflection.FileDescriptorProto fdp)
    {
        var noise = new byte[256];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = 0xff;
        using var ms = new MemoryStream();
        ms.Write(baseBinary);
        ms.Write(noise);
        ms.Write(fdp.ToByteArray());
        ms.Write(noise);
        // also embed one CNetMessagePB RTTI type descriptor so the now-wired offline RTTI
        // scanner (NetworkMessageRttiScanner, which fail-louds on ZERO decoded messages) decodes
        // the canned universe's network_message symbol (CNETMsg_Tick, channel NetMessages). ASCII,
        // NUL-terminated so the scanner's printable-run stops.
        ms.Write(System.Text.Encoding.ASCII.GetBytes("?$CNetMessagePB@$06VCNETMsg_Tick@@\0"));
        // demo_messages: embed one CDemoMessagePB descriptor (CDemoPacket=7) so the demo RTTI
        // scanner (DemoMessageRttiScanner, fail-loud on ZERO decoded) emits demo_messages.json.
        ms.Write(System.Text.Encoding.ASCII.GetBytes("?$CDemoMessagePB@$06VCDemoPacket@@\0"));
        return ms.ToArray();
    }

    private static byte[] BuildElf()
    {
        var build = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildElf64", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var symB = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildSym", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sym = (byte[])symB.Invoke(null, new object[] { (byte)1, (byte)2, (ushort)1 })!;
        return (byte[])build.Invoke(null, new object[] { new[] { sym }, true })!;
    }

    private static byte[] BuildPe()
    {
        var mi = typeof(Cs2SchemaTracker.Tests.Modules.PortableExecutableInspectorTest).GetMethod(
            "BuildMinimalPe", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (byte[])mi.Invoke(null, new object[] { 1, false })!;
    }
}
