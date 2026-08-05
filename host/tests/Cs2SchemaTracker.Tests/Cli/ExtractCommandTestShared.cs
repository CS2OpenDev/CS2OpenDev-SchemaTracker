// Shared synthetic-input helpers for the ExtractCommand test families (orchestration +
// the at-use verification). Mirrors the private builders in ExtractCommandTest so the
// at-use suite can construct the SAME well-formed ELF/PE + embedded-FDP fixture binaries and a
// full-enough canned WalkerOutput WITHOUT reflecting into a sibling test class's privates.
//

using System.Security.Cryptography;

using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Tests.Cli;

internal static class ExtractCommandTestShared
{
    public static string Sha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    // A complete-enough WalkerOutput for EmitFullSet: a client + server class, an enum, the
    // walks each with one record, and a registry universe mirroring every produced symbol so PATH A's
    // cross-check passes.
    public static WalkerOutput CannedWalkerOutput(string platform)
    {
        var clientCls = new SchemaClass { Name = "C_BaseEntity", Module = "client", Size = 1416 };
        clientCls.Parents.Add(new SchemaClassParent { Name = "CEntityInstance", Module = "client" });
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

    public static Google.Protobuf.Reflection.FileDescriptorProto BuildFdp(string name)
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

    public static byte[] WithEmbeddedFdp(byte[] baseBinary, Google.Protobuf.Reflection.FileDescriptorProto fdp)
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
        // demo_messages: likewise embed one CDemoMessagePB RTTI type descriptor so the demo RTTI
        // scanner (DemoMessageRttiScanner, also fail-loud on ZERO decoded) decodes a demo command
        // (CDemoPacket=7) and emits a non-empty demo_messages.json for the canned fixture set.
        ms.Write(System.Text.Encoding.ASCII.GetBytes("?$CDemoMessagePB@$06VCDemoPacket@@\0"));
        return ms.ToArray();
    }

    public static byte[] BuildElf()
    {
        var build = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildElf64", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var symB = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildSym", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sym = (byte[])symB.Invoke(null, new object[] { (byte)1, (byte)2, (ushort)1 })!;
        return (byte[])build.Invoke(null, new object[] { new[] { sym }, true })!;
    }

    public static byte[] BuildPe()
    {
        var mi = typeof(Cs2SchemaTracker.Tests.Modules.PortableExecutableInspectorTest).GetMethod(
            "BuildMinimalPe", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (byte[])mi.Invoke(null, new object[] { 1, false })!;
    }
}
