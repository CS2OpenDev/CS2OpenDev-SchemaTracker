// ExtractCommand end-to-end orchestration tests (synthetic; no real walker,
// no real binaries, no Steam).
//
// Exercises the pipeline by injecting a FAKE IWalkerRunner (the single walker
// seam) so the orchestration runs without a built walker binary:
//   resolve binaries dir -> run walker (fake) -> read binary WalkerOutput ->
//   EntitySchemaEmitter -> entity_schema.json under the out dir.
//
// PLATFORM MODEL (v0.2): two platforms only — "windows-x86_64" / "linux-x86_64".
// One walk per platform loads ALL modules, so a single emitted entity_schema.json
// carries classes tagged module="client" AND module="server" (the union model).
//
// Coverage:
//   1. Happy path: a fake walker that emits a canned binary WalkerOutput produces a
//      deterministic, schema-correct entity_schema.json at the expected path, and the
//      emitted set carries BOTH a client-module class and a server-module class.
// 2. fail-loud: a fake walker exiting non-zero (simulating the
//      unknown-layout rejection, exit 75) -> extract exits with that code, writes NO
//      artifacts, leaves no leftover temp file.
// 3. cross-OS guard still fires (exit 70) before any walker work — and the
//      fake runner is never even constructed.
//
// ExtractCommand / IWalkerRunner are internal — reached via the host project's
// InternalsVisibleTo.
//
// The orchestration's binary-resolution step reads cache/binaries/<build>/<platform>/
// RELATIVE to the process working directory, so the happy/fail-loud tests pin the
// working directory to a throwaway temp dir for the duration of the test (serialized
// via a collection so the process-global cwd is not raced).

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

/// <summary>
/// A fake <see cref="IWalkerRunner"/>: returns a configured exit code, writes a
/// configured stderr, and (when <paramref name="ExitCode"/> is 0 and a payload is
/// supplied) writes that canned binary <see cref="WalkerOutput"/> to the out path —
/// exactly what the real walker contract promises on success.
/// </summary>
internal sealed class FakeWalkerRunner : IWalkerRunner
{
    private readonly int _exitCode;
    private readonly string _stderr;
    private readonly WalkerOutput? _payload;

    public int Calls { get; private set; }
    public string? LastBinariesDir { get; private set; }
    public string? LastPlatform { get; private set; }
    public string? LastOutPath { get; private set; }

    public FakeWalkerRunner(int exitCode, string stderr, WalkerOutput? payload)
    {
        _exitCode = exitCode;
        _stderr = stderr;
        _payload = payload;
    }

    public int Run(string binariesDir, string platform, string outPath, out string stderr)
    {
        Calls++;
        LastBinariesDir = binariesDir;
        LastPlatform = platform;
        LastOutPath = outPath;

        // On success the real walker writes a binary WalkerOutput to outPath. On a
        // non-zero exit it writes nothing trustworthy — mirror both behaviours.
        if (_exitCode == 0 && _payload is not null)
        {
            File.WriteAllBytes(outPath, _payload.ToByteArray());
        }

        stderr = _stderr;
        return _exitCode;
    }
}

[Collection("cwd-mutating")]
public class ExtractCommandTest
{
    private const string BuildId = "13371337";

    // The full producible artifact set for one (build, platform), per the orchestration.
    private static readonly string[] FullSetFileNames =
    {
        "entity_schema.json", "convars.json", "commands.json",
        "network_messages.json", "engine_constants.json", "string_pools.json",
        "modules.json", "provenance.json", "registry_audit.json",
    };

    // The descriptorset a full extract emits, Name-sorted. The two fixture binaries embed
    // netmessages.proto + networkbasetypes.proto (binary-derived, canonical); the always-on wire
    // merge (data/wire_descriptors.pb) adds the other six engine wire families the binaries don't
    // carry. netmessages/networkbasetypes are NOT duplicated — the binary copy wins over the
    // same-named supplemental — so the union is exactly the eight wire names.
    private static readonly string[] ExpectedDescriptorNames =
    {
        "clientmessages.proto", "cs_gameevents.proto", "cstrike15_usermessages.proto",
        "gameevents.proto", "netmessages.proto", "networkbasetypes.proto",
        "te.proto", "usermessages.proto",
    };

    // A platform this host can natively extract (so the guard passes and the
    // orchestration proceeds). null on a host that matches neither platform (e.g. macOS):
    // the happy/fail-loud cases self-skip there, the cross-OS case still runs.
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

    // A platform this host CANNOT extract (opposite OS family). Always non-null because the
    // two platforms partition the hosts we support; on a macOS host either is cross-OS.
    private static string CrossOsPlatform() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "linux-x86_64"
            : "windows-x86_64";

    // A single walk loads ALL modules: this canned output deliberately carries a
    // client-module class AND a server-module enum (and a server-module class) so the
    // union model is exercised end-to-end.
    private static WalkerOutput CannedWalkerOutput(string platform)
    {
        var clientCls = new SchemaClass { Name = "C_BaseEntity", Module = "client", Size = 1416 };
        clientCls.Parents.Add(new SchemaClassParent { Name = "CEntityInstance", Module = "client" });
        clientCls.Fields.Add(new SchemaField
        {
            Name = "m_iHealth",
            Offset = 80,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        });

        var serverCls = new SchemaClass { Name = "CBaseEntity", Module = "server", Size = 1416 };
        serverCls.Fields.Add(new SchemaField
        {
            Name = "m_iMaxHealth",
            Offset = 84,
            TypeModule = "",
            Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
        });

        var en = new SchemaEnum { Name = "MoveType_t", Module = "server", Alignment = "uint8_t" };
        en.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        en.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_WALK", Value = 2 });

        var walk = new EntitySchemaWalk();
        walk.Classes.Add(clientCls);
        walk.Classes.Add(serverCls);
        walk.Enums.Add(en);

        // walks: a real walk always carries these, so the full-set orchestration
        // test exercises them. Each carries one record so the artifact is non-empty.
        var convars = new ConVarsWalk();
        convars.Convars.Add(new ConVar { Name = "sv_cheats", Default = "0", Description = "Allow cheats" });
        var commands = new CommandsWalk();
        commands.Commands.Add(new Command { Name = "kill", Description = "Commit suicide" });
        var netmsg = new NetworkMessagesWalk();
        var channel = new NetworkChannel { Name = "NetMessages" };
        channel.Messages.Add(new NetworkMessageEntry { Id = 7, ProtoMessageType = "CNETMsg_Tick" });
        netmsg.Channels.Add(channel);

        // walks: a real walk always carries these. One record each so the artifact
        // is non-empty and the full-set orchestration test exercises both emitters.
        var engineConstants = new EngineConstantsWalk();
        engineConstants.Constants.Add(new EngineConstant
        {
            // The walker emits engine-constant `source` ONLY as "schema_enum:<module>/<EnumName>"
            // (engine_constants_walk.cpp). The host (and registry_universe_walk) parse the
            // originating module out of it -> "server.dll". The universe entry below must use that
            // same PARSED module so PATH A's (symbol, module) cross-check agrees.
            Name = "MAX_PLAYERS",
            Source = "schema_enum:server.dll/CGameRules",
            IntValue = 64,
        });
        var stringPools = new StringPoolsWalk();
        var symPool = new StringPool { Name = "CUtlSymbolLarge" };
        symPool.Entries.Add("m_iHealth");
        stringPools.Pools.Add(symPool);

        // registry universe: the FULL observed-symbol set. PATH A's cross-check
        // requires every PRODUCED symbol to appear here, so list every symbol the artifacts above
        // emit, PLUS two observed-but-not-extracted symbols (two deferred string_pools) so the
        // extract-time audit mints Omitted rows too.
        //
        // the host now sources network_messages.json from its offline RTTI scan, and the
        // extract reconciles the universe's network_message rows to that scan (universe ==
        // extraction). The fixture binaries embed a CNetMessagePB descriptor for CNETMsg_Tick
        // (channel NetMessages), so the reconciled universe re-mints exactly that one row. There
        // are NO omitted network messages by construction (the scan IS the registered membership),
        // so the two Omitted rows below are deferred STRING POOLS, not a deferred network_message.
        var universe = new RegistryUniverse();
        void Obs(string symbol, string module, string category) =>
            universe.Symbols.Add(new ObservedRegistrySymbol { Symbol = symbol, Module = module, Category = category });
        Obs("C_BaseEntity", "client", "schema_class");
        Obs("CBaseEntity", "server", "schema_class");
        Obs("MoveType_t", "server", "schema_enum");
        Obs("sv_cheats", "", "convar");
        Obs("kill", "", "command");
        Obs("CNETMsg_Tick", "NetMessages", "network_message");
        // Module is the PARSED form from the constant's source ("schema_enum:server.dll/..."
        // -> "server.dll"), mirroring registry_universe_walk.ModuleFromConstantSource.
        Obs("MAX_PLAYERS", "server.dll", "engine_constant");
        Obs("CUtlSymbolLarge", "", "string_pool");
        // Observed but NOT extracted -> Omitted rows at extract time. Two deferred string_pools
        // (see the note above: a deferred network_message no longer exists under the RTTI scan).
        Obs("CUtlSymbolTable", "engine2", "string_pool");
        Obs("CUtlSymbolTableMt", "engine2", "string_pool");

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

    // Run a body with the process working directory pinned to a fresh temp dir that
    // already contains an (empty-but-present) cache/binaries/<build>/<platform>/ so binary
    // resolution succeeds. Restores the cwd and deletes the temp dir afterwards.
    private static void InPinnedWorkDir(string build, string platform, Action<string> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var binariesDir = Path.Combine(workDir, "cache", "binaries", build, platform);
        Directory.CreateDirectory(binariesDir);
        // A real, inspectable input binary so the modules.json + provenance.json
        // emitters (which hash + parse every *.so/*.dll in this dir) succeed. The fake walker
        // does not read it, but the host-side emitters do. Write BOTH a .so and a .dll so the
        // set is well-formed regardless of host platform (ModuleInspector sniffs by magic).
        //
        // each fixture binary also EMBEDS a serialized FileDescriptorProto (sandwiched in
        // 0xFF noise) so the now-wired ProtoDescriptorExtractor recovers at least one descriptor —
        // mirroring a real CS2 binary set, which always carries FDPs. Without this the
        // requireNonEmpty:true zero-descriptors guard would (correctly) abort the extract.
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"),
            WithEmbeddedFdp(BuildElf(), BuildFdp("netmessages.proto")));
        File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"),
            WithEmbeddedFdp(BuildPe(), BuildFdp("networkbasetypes.proto")));

        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        {
            body(workDir);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // A minimal, valid FileDescriptorProto whose embedded serialized bytes the
    // DescriptorScanner will recover (name ends in .proto, fields all in the FDP set).
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

    // Sandwich a serialized FDP between 0xFF noise inside a base binary so the scanner's anchor
    // heuristic finds it (matches ProtoDescriptorExtractorTest.CreateSyntheticBinary's layout).
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

    [WindowsOnlyFact]
    public void HappyPath_FakeWalker_Produces_Full_Artifact_Set_Atomically()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            var code = ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake);
            Assert.Equal(0, code);

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            // The full producible set lands together: entity_schema + convars +
            // commands + network_messages + engine_constants + string_pools + modules +
            // provenance. gameevents is SKIPPED (no content-depot VPK) — a documented
            // omission, not a file.
            foreach (var name in FullSetFileNames)
            {
                Assert.True(File.Exists(Path.Combine(setDir, name)), $"expected {name}");
            }
            Assert.False(File.Exists(Path.Combine(setDir, "gameevents.json")),
                "gameevents.json must be skipped without a content-depot VPK");

            // the protos/ directory + protos.descriptorset land in the same set. The two
            // fixture binaries each embed one FDP (netmessages.proto, networkbasetypes.proto), so
            // protos/ is NON-EMPTY and the descriptorset parses to those two, Name-sorted.
            var protosDir = Path.Combine(setDir, "protos");
            Assert.True(Directory.Exists(protosDir), "expected protos/ directory");
            Assert.NotEmpty(Directory.GetFiles(protosDir, "*", SearchOption.AllDirectories));
            Assert.True(File.Exists(Path.Combine(setDir, "protos.descriptorset")),
                "expected protos.descriptorset");
            Assert.True(File.Exists(Path.Combine(protosDir, "netmessages.proto")));
            Assert.True(File.Exists(Path.Combine(protosDir, "networkbasetypes.proto")));
            var dset = Google.Protobuf.Reflection.FileDescriptorSet.Parser.ParseFrom(
                File.ReadAllBytes(Path.Combine(setDir, "protos.descriptorset")));
            Assert.Equal(ExpectedDescriptorNames, dset.File.Select(f => f.Name).ToArray());

            // No staging dir survives a successful promote.
            var stagingSurvivors = Directory.GetDirectories(Path.Combine(workDir, "extract-out", BuildId))
                .Select(Path.GetFileName)
                .Where(d => d!.StartsWith(platform + ".staging-", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(stagingSurvivors);

            // provenance.json records BOTH input binaries (sorted by path).
            using var prov = JsonDocument.Parse(File.ReadAllText(Path.Combine(setDir, "provenance.json")));
            Assert.Equal(2, prov.RootElement.GetProperty("inputs").GetArrayLength());

            // schema_registration_count is attributed host-side from the walk's
            // entity_schema, NOT a 0 placeholder. The canned walk has one module="client"
            // class and one module="server" class + one module="server" enum. The fixture
            // input binaries are client.dll (PE) and libserver.so (ELF):
            //   client.dll    -> module key "client" -> 1 registration
            //   libserver.so  -> module key "server" (lib + .so stripped) -> 2 registrations
            using var mods = JsonDocument.Parse(File.ReadAllText(Path.Combine(setDir, "modules.json")));
            var byPath = mods.RootElement.GetProperty("modules").EnumerateArray()
                .ToDictionary(
                    m => m.GetProperty("path").GetString()!,
                    m => m.GetProperty("schemaRegistrationCount").GetUInt32());
            Assert.Equal(1u, byPath["client.dll"]);
            Assert.Equal(2u, byPath["libserver.so"]);
        });
    }

    [WindowsOnlyFact]
    public void Extract_Synthesizes_RegistryAudit_From_Walker_Universe_With_Omitted_Rows()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            Assert.Equal(0, ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake));

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            var auditJson = File.ReadAllText(Path.Combine(setDir, "registry_audit.json"));
            var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(false));
            var audit = parser.Parse<Cs2SchemaTracker.Schemas.RegistryAudit>(auditJson);

            // One entry per observed universe symbol (10 in CannedWalkerOutput).
            Assert.Equal(10, audit.Entries.Count);
            // Every entry has a disposition.
            Assert.All(audit.Entries, e => Assert.NotEqual(RegistryEntry.DispositionOneofCase.None, e.DispositionCase));

            // The two observed-but-not-extracted symbols are Omitted with non-empty rationales.
            var omitted = audit.Entries.Where(e => e.DispositionCase == RegistryEntry.DispositionOneofCase.Omitted).ToList();
            Assert.Equal(2, omitted.Count);
            Assert.All(omitted, e => Assert.False(string.IsNullOrEmpty(e.Omitted.Rationale)));

            // A produced symbol resolves to its artifact filename.
            var cheats = audit.Entries.Single(e => e.Symbol == "sv_cheats");
            Assert.Equal("convars.json", cheats.Extracted.ArtifactFilename);
        });
    }

    [WindowsOnlyFact]
    public void FullSet_Is_Deterministic_Byte_Identical_Across_Two_Runs()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        Dictionary<string, byte[]> RunOnce()
        {
            var captured = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            InPinnedWorkDir(BuildId, platform, workDir =>
            {
                var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
                Assert.Equal(0, ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake));
                var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
                // Recurse so the protos/<descriptor>.proto files are part of the
                // byte-identical determinism assertion alongside the top-level JSON +
                // protos.descriptorset. Key by set-relative path (forward-slashed) so the two runs
                // compare like-for-like across the per-descriptor subtree.
                foreach (var f in Directory.GetFiles(setDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(setDir, f).Replace('\\', '/');
                    captured[rel] = File.ReadAllBytes(f);
                }
            });
            return captured;
        }

        var a = RunOnce();
        var b = RunOnce();
        Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.Ordinal), b.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in a.Keys)
        {
            Assert.Equal(a[key], b[key]);
        }
    }

    [Fact]
    public void FailLoud_Emitter_Throw_Leaves_No_Artifacts_And_No_Staging()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            // A walk that yields a structurally invalid entity schema (a field with no resolved
            // type) makes EntitySchemaEmitter throw mid-set.: nothing must be promoted.
            var bad = CannedWalkerOutput(platform);
            bad.EntitySchema.Classes[0].Fields[0].Type = null;   // CATEGORY_UNSPECIFIED via missing type

            var fake = new FakeWalkerRunner(0, "", bad);
            var code = ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake);

            Assert.NotEqual(0, code);
            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Assert.False(Directory.Exists(setDir), "no artifact set on emitter failure");
            // No leftover staging dirs either.
            var buildDir = Path.Combine(workDir, "extract-out", BuildId);
            if (Directory.Exists(buildDir))
            {
                Assert.Empty(Directory.GetDirectories(buildDir));
            }
        });
    }

    [Fact]
    public void FailLoud_ZeroDescriptors_From_Binaries_Aborts_Whole_Set()
    {
        // a CS2 binary set always embeds FileDescriptorProtos. Binaries that yield
        // ZERO descriptors are a structural failure — the extract must abort BEFORE promotion and
        // leave NO artifact set, not silently emit an empty protos/ directory.
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var workDir = Path.Combine(Path.GetTempPath(), "zero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var binariesDir = Path.Combine(workDir, "cache", "binaries", BuildId, platform);
        Directory.CreateDirectory(binariesDir);
        // Plain ELF/PE with NO embedded FDP — module inspection still succeeds, but the scan
        // recovers nothing.
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"), BuildElf());
        File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"), BuildPe());

        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        {
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            var code = ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake);

            Assert.NotEqual(0, code);
            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Assert.False(Directory.Exists(setDir), "no artifact set on zero-descriptors failure");
            var buildDir = Path.Combine(workDir, "extract-out", BuildId);
            if (Directory.Exists(buildDir))
            {
                Assert.Empty(Directory.GetDirectories(buildDir));   // no leftover staging dir
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void FailLoud_Does_Not_Clobber_PreExisting_Artifact_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Directory.CreateDirectory(setDir);
            var sentinel = Path.Combine(setDir, "entity_schema.json");
            File.WriteAllText(sentinel, "PREEXISTING");

            // An emitter-throwing walk must NOT delete/replace the pre-existing set (:
            // the delete-then-move promote only runs AFTER a clean full stage).
            var bad = CannedWalkerOutput(platform);
            bad.EntitySchema.Classes[0].Fields[0].Type = null;
            var fake = new FakeWalkerRunner(0, "", bad);

            var code = ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake);
            Assert.NotEqual(0, code);
            Assert.Equal("PREEXISTING", File.ReadAllText(sentinel));
        });
    }

    [WindowsOnlyFact]
    public void HappyPath_FakeWalker_Produces_SchemaCorrect_EntitySchema_At_Default_Path()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;   // host matches neither platform; covered by cross-OS test.

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var fake = new FakeWalkerRunner(exitCode: 0, stderr: "", payload: CannedWalkerOutput(platform));

            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform },
                runnerFactory: () => fake);

            Assert.Equal(0, code);
            Assert.Equal(1, fake.Calls);
            Assert.Equal(platform, fake.LastPlatform);

            // Default OFF-REPO output path (extract/rewalk merge): extract-out/<build>/<platform>/.
            // (artifacts/<build>/<platform>/ now requires --commit; covered by ExtractBatchTest.)
            var expected = Path.Combine(workDir, "extract-out", BuildId, platform, "entity_schema.json");
            Assert.True(File.Exists(expected), $"expected artifact at {expected}");

            // No leftover walker intermediate temp.
            Assert.False(File.Exists(fake.LastOutPath!), "walker intermediate must be cleaned up");

            // Schema-correct + host-stamped identity fields (canonical proto3 JSON).
            var bytes = File.ReadAllBytes(expected);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "entity_schema.json must not have a UTF-8 BOM");
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(BuildId, root.GetProperty("buildId").GetString());
            Assert.Equal(platform, root.GetProperty("platform").GetString());
            Assert.Equal(2, root.GetProperty("classes").GetArrayLength());
            Assert.Equal(1, root.GetProperty("enums").GetArrayLength());
            Assert.Equal("MoveType_t", root.GetProperty("enums")[0].GetProperty("name").GetString());
        });
    }

    [WindowsOnlyFact]
    public void HappyPath_One_Platform_Set_Carries_Both_Client_And_Server_Modules()
    {
        // The union model: a single walk loads ALL modules, so one entity_schema.json
        // for one platform must contain classes tagged module="client" AND module="server".
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake);
            Assert.Equal(0, code);

            var text = File.ReadAllText(
                Path.Combine(workDir, "extract-out", BuildId, platform, "entity_schema.json"));
            using var doc = JsonDocument.Parse(text);
            var modules = doc.RootElement.GetProperty("classes")
                .EnumerateArray()
                .Select(c => c.GetProperty("module").GetString())
                .ToHashSet();

            Assert.Contains("client", modules);
            Assert.Contains("server", modules);
        });
    }

    [WindowsOnlyFact]
    public void HappyPath_Is_Deterministic_Byte_Identical_Across_Two_Runs()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        byte[] RunOnce()
        {
            byte[] captured = Array.Empty<byte>();
            InPinnedWorkDir(BuildId, platform, workDir =>
            {
                var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
                var code = ExtractCommand.Run(
                    new[] { "--build", BuildId, "--platform", platform }, () => fake);
                Assert.Equal(0, code);
                captured = File.ReadAllBytes(
                    Path.Combine(workDir, "extract-out", BuildId, platform, "entity_schema.json"));
            });
            return captured;
        }

        Assert.Equal(RunOnce(), RunOnce());
    }

    [WindowsOnlyFact]
    public void Honors_Explicit_Out_Dir()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            // --out is now an output ROOT: per-build sets nest under <out>/<build>/<platform>/.
            var outRoot = Path.Combine(workDir, "custom-out");
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));

            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform, "--out", outRoot }, () => fake);

            Assert.Equal(0, code);
            Assert.True(File.Exists(Path.Combine(outRoot, BuildId, platform, "entity_schema.json")));
        });
    }

    [Fact]
    public void FailLoud_Walker_NonZero_Exit_Aborts_With_That_Code_And_No_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            // Exit 75 = the unknown-schema-system-layout rejection; stderr carries
            // the layout signature. The orchestration must surface the code verbatim.
            const int UnknownLayoutExit = 75;
            const string LayoutSig = "schema-system-layout signature: 0xDEADBEEF (unknown) — refusing to guess";
            var fake = new FakeWalkerRunner(UnknownLayoutExit, LayoutSig, payload: null);

            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake);

            // Walker's exit code is propagated verbatim.
            Assert.Equal(UnknownLayoutExit, code);
            Assert.Equal(1, fake.Calls);

            // No artifact bytes anywhere under the default artifact path.
            var artifact = Path.Combine(workDir, "extract-out", BuildId, platform, "entity_schema.json");
            Assert.False(File.Exists(artifact), "no artifacts on walker failure");
            Assert.False(File.Exists(artifact + ".tmp"), "no leftover artifact temp");
            // The walker intermediate temp must also be cleaned up.
            Assert.False(File.Exists(fake.LastOutPath!), "walker intermediate must be cleaned up");
        });
    }

    [Fact]
    public void FailLoud_Walker_Reports_Success_But_Writes_No_Output_Aborts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            // Exit 0 but payload null => the fake writes nothing: a contract violation the
            // orchestration must catch (EX_SOFTWARE = 70), not paper over.
            var fake = new FakeWalkerRunner(0, "", payload: null);

            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake);

            Assert.Equal(70, code);
            var artifact = Path.Combine(workDir, "extract-out", BuildId, platform, "entity_schema.json");
            Assert.False(File.Exists(artifact));
        });
    }

    // ---- provenance.steam population from manifest-record.json -----------------------

    private static void WriteManifestRecord(string binariesDir, string contents) =>
        File.WriteAllText(
            Path.Combine(binariesDir, Cs2SchemaTracker.Host.Steam.ManifestRecord.FileName), contents);

    [WindowsOnlyFact]
    public void Provenance_Steam_Block_Populated_From_TwoDepot_Manifest_Record()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var binariesDir = Path.Combine(workDir, "cache", "binaries", BuildId, platform);
            // Two depots, on-disk in REVERSE depotId order, with DIFFERENT manifestCreatedUtc
            // so the depot-sort + latest-time selection are both observable.
            WriteManifestRecord(binariesDir,
                "{\"appId\":730,\"buildId\":23669931,\"depots\":[" +
                "{\"depotId\":2347771,\"manifestCreatedUtc\":\"2026-06-10T22:05:05Z\",\"manifestId\":\"8287382081622299196\"}," +
                "{\"depotId\":2347770,\"manifestCreatedUtc\":\"2026-06-09T01:00:00Z\",\"manifestId\":\"5146470907583764090\"}" +
                "]}");

            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            Assert.Equal(0, ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake));

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            using var prov = JsonDocument.Parse(File.ReadAllText(Path.Combine(setDir, "provenance.json")));
            var steam = prov.RootElement.GetProperty("steam");

            Assert.Equal(730u, steam.GetProperty("appId").GetUInt32());
            Assert.Equal(BuildId, steam.GetProperty("steamBuildId").GetString());
            // Top-level manifestCreatedUtc = the LATEST of the per-depot times.
            Assert.Equal("2026-06-10T22:05:05Z", steam.GetProperty("manifestCreatedUtc").GetString());

            // Both depots present, SORTED by depotId regardless of on-disk order.
            var depots = steam.GetProperty("depots");
            Assert.Equal(2, depots.GetArrayLength());
            Assert.Equal(2347770u, depots[0].GetProperty("depotId").GetUInt32());
            Assert.Equal("5146470907583764090", depots[0].GetProperty("manifestId").GetString());
            Assert.Equal(2347771u, depots[1].GetProperty("depotId").GetUInt32());
            Assert.Equal("8287382081622299196", depots[1].GetProperty("manifestId").GetString());
        });
    }

    [WindowsOnlyFact]
    public void Provenance_Steam_Block_Empty_But_Valid_Without_Manifest_Record()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            // InPinnedWorkDir does NOT write a manifest-record.json (mirrors a --binaries dev run).
            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            Assert.Equal(0, ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake));

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            using var prov = JsonDocument.Parse(File.ReadAllText(Path.Combine(setDir, "provenance.json")));
            var steam = prov.RootElement.GetProperty("steam");

            // Absent record => empty-but-valid steam block (NOT a failure). appId 0, no depots,
            // empty manifestCreatedUtc; steamBuildId is still echoed from the build argument.
            Assert.Equal(0u, steam.GetProperty("appId").GetUInt32());
            Assert.Equal(BuildId, steam.GetProperty("steamBuildId").GetString());
            Assert.Equal("", steam.GetProperty("manifestCreatedUtc").GetString());
            Assert.Equal(0, steam.GetProperty("depots").GetArrayLength());
        });
    }

    [Fact]
    public void Malformed_Manifest_Record_Fails_Loud_With_No_Artifact_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var binariesDir = Path.Combine(workDir, "cache", "binaries", BuildId, platform);
            // Present but unparseable JSON: a real input problem -> fail loud.
            WriteManifestRecord(binariesDir, "{ this is not valid json ]");

            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            var code = ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake);

            Assert.NotEqual(0, code);
            // nothing promoted, no staging survivor.
            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Assert.False(Directory.Exists(setDir), "no artifact set on malformed manifest-record");
            var buildDir = Path.Combine(workDir, "extract-out", BuildId);
            if (Directory.Exists(buildDir))
            {
                Assert.Empty(Directory.GetDirectories(buildDir));
            }
        });
    }

    [Fact]
    public void CrossOs_Guard_Fires_Before_Any_Walker_Work()
    {
        var platform = CrossOsPlatform();
        if (HostPlatform.CanExtractPlatform(platform))
        {
            // Defensive: CrossOsPlatform() should never return an extractable platform.
            return;
        }

        var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
        var factoryInvoked = false;

        var code = ExtractCommand.Run(
            new[] { "--build", BuildId, "--platform", platform },
            runnerFactory: () => { factoryInvoked = true; return fake; });

        // Guard fires (EX_SOFTWARE) and the runner factory is never even reached.
        Assert.Equal(70, code);
        Assert.False(factoryInvoked, "runner must not be constructed when the cross-OS guard fires");
        Assert.Equal(0, fake.Calls);
    }

    // end-to-end: a co-located content VPK that ships ONLY one content source (a single
    // .gameevents) — the other six content sources genuinely absent — produces gameevents.json,
    // OMITS the six others (no fail-loud, the whole extract still succeeds), and RECORDS those six
    // as per-artifact content omissions in the build-level omissions.json.
    [WindowsOnlyFact]
    public void FullExtract_PartialContent_OmitsAbsentArtifacts_AndRecordsThem()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, workDir =>
        {
            var binariesDir = Path.Combine(workDir, "cache", "binaries", BuildId, platform);
            // A content VPK whose ONLY content source is one valid .gameevents file.
            File.WriteAllBytes(Path.Combine(binariesDir, "pak01_dir.vpk"), BuildGameEventsOnlyVpk());

            var fake = new FakeWalkerRunner(0, "", CannedWalkerOutput(platform));
            var code = ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake);
            Assert.Equal(0, code);

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Assert.True(File.Exists(Path.Combine(setDir, "gameevents.json")), "gameevents.json must be emitted");
            foreach (var absent in AbsentContentArtifacts)
            {
                Assert.False(File.Exists(Path.Combine(setDir, absent)), $"{absent} must be omitted (absent source)");
            }

            // The build-level omissions.json records exactly the six genuinely-absent artifacts.
            var omissionsPath = Path.Combine(workDir, "extract-out", BuildId, "omissions.json");
            Assert.True(File.Exists(omissionsPath), "omissions.json must be written");
            var omissions = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true))
                .Parse<Omissions>(File.ReadAllText(omissionsPath));
            var carrier = Assert.Single(omissions.Omissions_, o => o.Platform == platform);
            Assert.Equal(PlatformOmission.Types.Reason.Unspecified, carrier.Reason);
            Assert.Equal(
                "game_modes.json,item_definitions.json,localization.json,map_overviews.json,prop_data.json,surface_properties.json",
                string.Join(",", carrier.ContentOmissions.Select(c => c.Artifact)));
            Assert.All(carrier.ContentOmissions,
                c => Assert.Equal(PlatformOmission.Types.Reason.ContentNotShippedThisEra, c.Reason));
        });
    }

    private static readonly string[] AbsentContentArtifacts =
    {
        "item_definitions.json", "game_modes.json", "localization.json",
        "surface_properties.json", "prop_data.json", "map_overviews.json",
    };

    // ---- PromoteStagingDir (two-step promote) -----------------------------------------------
    //
    // These exercise ExtractCommand.PromoteStagingDir directly (real filesystem, no fake walker
    // needed — the promote is a pure directory-rename operation over whatever staging/out dirs
    // already exist), proving both promote shapes: fresh outDir (no ".old-" step needed) and a
    // pre-existing outDir (the ".old-" rename-aside path, with no residue left after success).

    [Fact]
    public void PromoteStagingDir_NoExistingOutDir_MovesStagingDirectlyIntoPlace()
    {
        var root = Path.Combine(Path.GetTempPath(), "promote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var outDir = Path.Combine(root, "e0000001", "windows-x86_64");
            var stagingDir = outDir + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDir);
            File.WriteAllText(Path.Combine(stagingDir, "entity_schema.json"), "NEW");

            ExtractCommand.PromoteStagingDir(stagingDir, outDir);

            Assert.True(Directory.Exists(outDir));
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(outDir, "entity_schema.json")));
            Assert.False(Directory.Exists(stagingDir), "staging dir must be consumed by the move");
        }
        finally
        {
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void PromoteStagingDir_ExistingOutDir_TakesOldRenamePath_NoResidueAfterSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "promote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var buildDir = Path.Combine(root, "e0000002");
            var outDir = Path.Combine(buildDir, "windows-x86_64");
            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "entity_schema.json"), "OLD");
            File.WriteAllText(Path.Combine(outDir, "STRAY_OLD_FILE.txt"), "must not survive");

            var stagingDir = outDir + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stagingDir);
            File.WriteAllText(Path.Combine(stagingDir, "entity_schema.json"), "NEW");

            ExtractCommand.PromoteStagingDir(stagingDir, outDir);

            // The .old- path was taken (a pre-existing outDir): the NEW content fully replaced OLD,
            // including dropping a stray file the delete-then-move promote would ALSO have dropped —
            // but via rename-aside + move instead of delete-then-move, so the "nothing on disk"
            // crash window no longer exists.
            Assert.Equal("NEW", File.ReadAllText(Path.Combine(outDir, "entity_schema.json")));
            Assert.False(File.Exists(Path.Combine(outDir, "STRAY_OLD_FILE.txt")));
            Assert.False(Directory.Exists(stagingDir), "staging dir must be consumed by the move");

            // No ".old-*" sibling survives a CLEAN promote (best-effort delete succeeded).
            var oldSurvivors = Directory.GetDirectories(buildDir)
                .Select(Path.GetFileName)
                .Where(n => n!.Contains(".old-", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(oldSurvivors);
        }
        finally
        {
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // A minimal embedded VPK (v2) carrying ONE root-level "core.gameevents" KV1 entry and nothing
    // else — so only the content source is present.
    private static byte[] BuildGameEventsOnlyVpk()
    {
        byte[] body = Encoding.UTF8.GetBytes("\"GameEvents\" { \"player_death\" { \"userid\" \"short\" } }");

        var tree = new MemoryStream();
        void CStr(string s)
        { tree.Write(Encoding.UTF8.GetBytes(s)); tree.WriteByte(0); }
        void U32(uint v)
        { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); tree.Write(b); }
        void U16(ushort v)
        { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); tree.Write(b); }

        // extension "gameevents" -> path " " (root) -> file "core".
        CStr("gameevents");
        CStr(" ");
        CStr("core");
        U32(VpkCrc32(body));
        U16(0);            // preload bytes
        U16(0x7FFF);       // archive index = embedded
        U32(0);            // entry offset (into embedded data section)
        U32((uint)body.Length);
        U16(0xFFFF);       // terminator
        tree.WriteByte(0); // end files
        tree.WriteByte(0); // end paths
        tree.WriteByte(0); // end extensions

        byte[] treeBytes = tree.ToArray();
        var ms = new MemoryStream();
        void MU32(uint v)
        { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b); }
        MU32(0x55AA1234u);          // signature
        MU32(2u);                   // version
        MU32((uint)treeBytes.Length);
        MU32((uint)body.Length);    // FileDataSectionSize
        MU32(0);
        MU32(0);
        MU32(0);  // md5 + signature section sizes
        ms.Write(treeBytes);
        ms.Write(body);
        return ms.ToArray();
    }

    private static uint VpkCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
