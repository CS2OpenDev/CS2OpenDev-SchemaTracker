// Walker multi-era build — ExtractCommand post-load SECOND-GATE tests.
//
// Exercises the runtime half of the two-gate design: after the selected per-era
// walker runs, the host asserts the EMITTED schema-system layout signature equals the resolved
// era's EXPECTED signature (from the inventory eras[]) BEFORE staging any artifact byte. A mismatch
// aborts non-zero (exit 75 = EX_PROTOCOL) with zero artifacts; a match proceeds.
//
// Driven via the gate-enabled test seam ExtractCommand.Run(args, fakeRunnerFactory, eraResolver):
//   - the FAKE IWalkerRunner produces the WalkerOutput (no real exe), letting us feed a
//     matching / mismatching SchemaSystemLayoutSignature;
//   - the fixture-rooted EraWalkerResolver supplies the era's EXPECTED signature that arms
//     the gate (a FRESH build with no committed provenance -> the `cs2-2026-04-21` era).
//
// Deterministic: a throwaway repo-root temp dir holds data/cs2-assets-inventory.json +
// cache/binaries/<build>/<platform>/ fixture binaries; the process cwd is pinned there (shared
// "cwd-mutating" collection serializes that) and env vars are cleared/restored. No wall-clock, no
// real walker, no real CS2 binaries, no Steam.

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Walker;

// Joins BOTH the cwd-mutating collection (pins the process working directory, like
// ExtractCommandTest) — that is the binding serialization for the cwd; this class also
// clears/restores the era env vars inside each test.
[Collection("cwd-mutating")]
public sealed class ExtractCommandSecondGateTest
{
    private const string BuildId = "44445555";   // a FRESH build (no committed provenance) -> cs2-2026-04-21 era

    private const string CurrentSha = "b8dcaf14c603076300cab3861c99b44878d65db4";
    private const string CurrentSig = "hl2sdk-cs2/b8dcaf14c603076300cab3861c99b44878d65db4/v1/3d1200e346019c59";

    // Same artifact-set names ExtractCommandTest asserts on (full set).
    private static readonly string[] FullSetFileNames =
    {
        "entity_schema.json", "convars.json", "commands.json",
        "network_messages.json", "engine_constants.json", "string_pools.json",
        "modules.json", "provenance.json", "registry_audit.json",
    };

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

    // A fake walker whose emitted WalkerOutput carries a configurable layout signature so the
    // second gate can be driven to match / mismatch. On success it writes the canned output to
    // outPath, exactly as the real walker contract promises.
    private sealed class SignatureFakeRunner : IWalkerRunner
    {
        private readonly WalkerOutput _payload;
        public int Calls { get; private set; }

        public SignatureFakeRunner(string emittedSignature)
            => _payload = CannedWalkerOutput(emittedSignature);

        public int Run(string binariesDir, string platform, string outPath, out string stderr)
        {
            Calls++;
            _payload.Platform = platform;
            File.WriteAllBytes(outPath, _payload.ToByteArray());
            stderr = "";
            return 0;
        }
    }

    // A complete-enough WalkerOutput for EmitFullSet to succeed, with a settable signature.
    // (Mirrors ExtractCommandTest.CannedWalkerOutput; one record per walk so each artifact is
    // non-empty and the registry-universe cross-check passes.)
    private static WalkerOutput CannedWalkerOutput(string signature)
    {
        var clientCls = new SchemaClass { Name = "C_BaseEntity", Module = "client", Size = 1416 };
        clientCls.Fields.Add(new SchemaField
        {
            Name = "m_iHealth",
            Offset = 80,
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        });
        var serverCls = new SchemaClass { Name = "CBaseEntity", Module = "server", Size = 1416 };
        serverCls.Fields.Add(new SchemaField
        {
            Name = "m_iMaxHealth",
            Offset = 84,
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        });
        var en = new SchemaEnum { Name = "MoveType_t", Module = "server", Alignment = "uint8_t" };
        en.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });

        var walk = new EntitySchemaWalk();
        walk.Classes.Add(clientCls);
        walk.Classes.Add(serverCls);
        walk.Enums.Add(en);

        var convars = new ConVarsWalk();
        convars.Convars.Add(new ConVar { Name = "sv_cheats", Default = "0", Description = "Allow cheats" });
        var commands = new CommandsWalk();
        commands.Commands.Add(new Command { Name = "kill", Description = "Commit suicide" });
        var netmsg = new NetworkMessagesWalk();
        var channel = new NetworkChannel { Name = "NetMessages" };
        channel.Messages.Add(new NetworkMessageEntry { Id = 7, ProtoMessageType = "CNETMsg_Tick" });
        netmsg.Channels.Add(channel);
        var engineConstants = new EngineConstantsWalk();
        engineConstants.Constants.Add(new EngineConstant
        {
            Name = "MAX_PLAYERS",
            Source = "schema_enum:server.dll/CGameRules",
            IntValue = 64,
        });
        var stringPools = new StringPoolsWalk();
        var symPool = new StringPool { Name = "CUtlSymbolLarge" };
        symPool.Entries.Add("m_iHealth");
        stringPools.Pools.Add(symPool);

        var universe = new RegistryUniverse();
        void Obs(string symbol, string module, string category) =>
            universe.Symbols.Add(new ObservedRegistrySymbol { Symbol = symbol, Module = module, Category = category });
        Obs("C_BaseEntity", "client", "schema_class");
        Obs("CBaseEntity", "server", "schema_class");
        Obs("MoveType_t", "server", "schema_enum");
        Obs("sv_cheats", "", "convar");
        Obs("kill", "", "command");
        Obs("CNETMsg_Tick", "NetMessages", "network_message");
        Obs("MAX_PLAYERS", "server.dll", "engine_constant");
        Obs("CUtlSymbolLarge", "", "string_pool");

        return new WalkerOutput
        {
            SchemaVersion = "ignored-by-host",
            WalkerVersion = "0.0.0-fake",
            EntitySchema = walk,
            Convars = convars,
            Commands = commands,
            NetworkMessages = netmsg,
            EngineConstants = engineConstants,
            StringPools = stringPools,
            RegistryUniverse = universe,
            SchemaSystemLayoutSignature = signature,
        };
    }

    // Build a fixture repo root (= the pinned cwd) carrying:
    //   data/cs2-assets-inventory.json (cs2-2026-04-21 compile-pin era, signature registered for the
    //     running platform so the fresh build defaults to it and the per-platform gate is armed),
    //   cache/binaries/<build>/<platform>/{libserver.so, client.dll} (with embedded FDPs).
    // The gate test uses a FAKE runner, so no natives walker binary is needed (it is never launched).
    // Pins cwd to it and clears CS2_WALKER_BIN / CS2_WALKER_ERAS_ROOT for the body; restores all.
    private static void InGateFixture(string platform, Action<string, EraWalkerResolver> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "gate-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));

        // Inventory with the cs2-2026-04-21 compile-pin era only — enough for the fresh build to
        // default to it. The signature is registered under the RUNNING platform so the per-platform
        // second gate passes on either a windows-x86_64 or linux-x86_64 host (the fake emits CurrentSig).
        File.WriteAllText(Path.Combine(root, "data", "cs2-assets-inventory.json"), $$"""
        {
          "app": { "app_id": 730 },
          "eras": [
            { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
              "layoutSignatures": { "{{platform}}": "{{CurrentSig}}" }, "minClasses": 1, "maxClasses": 10000 }
          ],
          "depots": [],
          "builds": []
        }
        """);

        // cache/binaries/<build>/<platform>/ with two real-enough input binaries (FDP-embedded)
        // so the host-side modules/provenance/proto-descriptor emitters succeed on the match path.
        var binariesDir = Path.Combine(root, "cache", "binaries", BuildId, platform);
        Directory.CreateDirectory(binariesDir);
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"),
            WithEmbeddedFdp(BuildElf(), BuildFdp("netmessages.proto")));
        File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"),
            WithEmbeddedFdp(BuildPe(), BuildFdp("networkbasetypes.proto")));

        var oldBin = Environment.GetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar);
        var oldNatives = Environment.GetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar);
        Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, null);
        Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, null);
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(root);
        try
        {
            body(root, new EraWalkerResolver(root));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, oldBin);
            Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, oldNatives);
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void SecondGate_Signature_Mismatch_Fails_Loud_With_No_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InGateFixture(platform, (root, resolver) =>
        {
            // The fake walker emits a signature that does NOT equal the era's expected one.
            var fake = new SignatureFakeRunner("hl2sdk-cs2/deadbeef/v1/wrongsig");
            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake, resolver);

            // Exit 75 (EX_PROTOCOL) — the second gate.
            Assert.Equal(75, code);

            // zero artifact bytes on disk. The artifacts dir must not exist (the
            // gate fires before the staging dir is created / promoted), and no staging survives.
            var setDir = Path.Combine(root, "extract-out", BuildId, platform);
            Assert.False(Directory.Exists(setDir), "no artifact set may be written on a gate failure");
            var buildDir = Path.Combine(root, "extract-out", BuildId);
            if (Directory.Exists(buildDir))
            {
                Assert.Empty(Directory.GetFileSystemEntries(buildDir));
            }
        });
    }

    [WindowsOnlyFact]
    public void SecondGate_Signature_Match_Proceeds_To_Full_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InGateFixture(platform, (root, resolver) =>
        {
            // The fake walker emits EXACTLY the era's expected signature -> the gate passes.
            var fake = new SignatureFakeRunner(CurrentSig);
            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake, resolver);

            Assert.Equal(0, code);
            Assert.Equal(1, fake.Calls);

            var setDir = Path.Combine(root, "extract-out", BuildId, platform);
            foreach (var name in FullSetFileNames)
            {
                Assert.True(File.Exists(Path.Combine(setDir, name)), $"expected {name}");
            }
        });
    }

    // ---- input-binary fixture helpers (mirror ExtractCommandTest) ----

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
