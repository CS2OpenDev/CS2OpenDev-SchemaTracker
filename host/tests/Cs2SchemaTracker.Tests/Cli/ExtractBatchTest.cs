// Extract — multi-build / batch / --commit orchestration tests (formerly the `rewalk` subcommand,
// folded INTO `extract`).
//
// ExtractCommand.Run(args, runnerFactory, resolver) derives the repo root for SELECTION and
// --verify from the injected EraWalkerResolver's RepoRoot getter, so a fixture-rooted resolver
// isolates the WHOLE command — selection, the per-build extract, the era-aware class gate, AND
// --verify — from the real checked-out repo.
//
// This suite drives the merged batch behaviors END-TO-END through ExtractCommand.Run over a
// fixture repo:
//
//   - usage errors (no selection / two modes / unknown platform -> exit 64) through Run directly
//     (these return before any repo access);
//   - selection (--all / --era / --pin) END-TO-END through Run: assert exactly the fixture's
//     committed builds (NOT the real repo's 244) are re-walked, proving the injected resolver's
//     RepoRoot governs selection — observed via the per-build output dirs Run produces and the
//     era-aware fake runner's per-build calls;
//   - --verify END-TO-END through Run: a build whose re-walk CORE matches the committed set ->
//     CORE-CLEAN (exit 0); a mutated committed CORE -> REGRESSION (exit 70);
//   - --commit END-TO-END: promote (clobber) into the FIXTURE artifacts/, the non-blocking gate +
// verify review signals, the hard-gate-no-write, and the build-level promote hook
//     (pics-appinfo.json emit + inventory upsert);
//   - the lower-layer primitive tests (CommittedBuilds.Discover, the per-build class gate,
//     fail-isolation, the normalized verify compare) are KEPT: they pin the individual seams at a
//     finer grain than the end-to-end Run assertions can.
//
// The content-artifact emit block is gated on a content VPK the fixture never carries, so a full
// extract here produces the CORE set only (the dropped --core-only flag is therefore a no-op for
// this fixture — same on-disk output).
//
// Deterministic: throwaway temp dir, cwd pinned (shared "cwd-mutating" collection), era env vars
// cleared/restored in a finally. No real walker, no real CS2 binaries, no Steam.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class ExtractBatchTest
{
    private const string CurrentSha = "b8dcaf14c603076300cab3861c99b44878d65db4";
    private const string CurrentSig = "hl2sdk-cs2/b8dcaf14c603076300cab3861c99b44878d65db4/v1/3d1200e346019c59";
    private const string Q1Sha = "0da05cff57162fe8f950192cf73d89e77ab9ee00";
    private const string Q1Sig = "hl2sdk-cs2/0da05cff57162fe8f950192cf73d89e77ab9ee00/v1/3e396404979881c9";

    // The CORE files ExtractCommand's gate/verify operate on (the subset present from the canned
    // walk; the full CoreJson list also includes registry_audit.json, present below).
    private static readonly string[] CoreFileNames =
    {
        "entity_schema.json", "convars.json", "commands.json",
        "network_messages.json", "engine_constants.json", "string_pools.json",
        "modules.json", "registry_audit.json",
    };

    // Content artifacts a full extract adds when a content VPK is present (never in the fixture).
    private static readonly string[] ContentFileNames =
    {
        "gameevents.json", "item_definitions.json", "game_modes.json",
        "localization.json", "surface_properties.json", "prop_data.json", "map_overviews.json",
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

    // A fake walker that writes a canned WalkerOutput (settable signature + class count); a
    // configured non-zero exit writes nothing (the walker-crash / fail-isolation path).
    private sealed class CountingFakeRunner : IWalkerRunner
    {
        private readonly string _signature;
        private readonly int _classCount;
        private readonly int _exitCode;
        public int Calls { get; private set; }

        public CountingFakeRunner(string signature, int classCount, int exitCode = 0)
        {
            _signature = signature;
            _classCount = classCount;
            _exitCode = exitCode;
        }

        public int Run(string binariesDir, string platform, string outPath, out string stderr)
        {
            Calls++;
            stderr = "";
            if (_exitCode != 0)
                return _exitCode;
            var payload = CannedWalkerOutput(_signature, _classCount);
            payload.Platform = platform;
            File.WriteAllBytes(outPath, payload.ToByteArray());
            return 0;
        }
    }

    // An ERA-AWARE fake walker for END-TO-END Run tests: it parses the build id out of the
    // binaries-dir path (cache/binaries/<build>/<platform>) and emits the layout signature mapped
    // for that build, so the host's second gate passes for BOTH eras in a single --all run.
    // It records, per build, how many times it was invoked (proves Run re-walked exactly the
    // selected builds). A configurable class count drives the era band gate.
    private sealed class EraAwareFakeRunner : IWalkerRunner
    {
        private readonly IReadOnlyDictionary<string, string> _buildToSignature;
        private readonly int _classCount;
        public Dictionary<string, int> CallsByBuild { get; } = new(StringComparer.Ordinal);

        public EraAwareFakeRunner(IReadOnlyDictionary<string, string> buildToSignature, int classCount)
        {
            _buildToSignature = buildToSignature;
            _classCount = classCount;
        }

        public int Run(string binariesDir, string platform, string outPath, out string stderr)
        {
            stderr = "";
            // .../cache/binaries/<build>/<platform> -> the build id is the parent dir's name.
            var build = Path.GetFileName(Path.GetDirectoryName(binariesDir.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!)!;
            CallsByBuild[build] = CallsByBuild.GetValueOrDefault(build) + 1;

            if (!_buildToSignature.TryGetValue(build, out var signature))
            {
                // A build the test did not register -> emit a deliberately-wrong signature so the
                // gate would fail loudly (this never happens for a correctly-set-up fixture).
                signature = "hl2sdk-cs2/unregistered/v1/wrongsig";
            }
            var payload = CannedWalkerOutput(signature, _classCount);
            payload.Platform = platform;
            File.WriteAllBytes(outPath, payload.ToByteArray());
            return 0;
        }
    }

    // A complete-enough WalkerOutput for EmitFullSet. classCount controls the schema-class count
    // so the era-aware gate can be driven in/out of band. Every produced symbol is mirrored into
    // the registry universe so PATH A's cross-check passes.
    private static WalkerOutput CannedWalkerOutput(string signature, int classCount)
    {
        var walk = new EntitySchemaWalk();
        var universe = new RegistryUniverse();
        void Obs(string symbol, string module, string category) =>
            universe.Symbols.Add(new ObservedRegistrySymbol { Symbol = symbol, Module = module, Category = category });

        for (int i = 0; i < classCount; i++)
        {
            var module = (i % 2 == 0) ? "client" : "server";
            var name = (i % 2 == 0) ? $"C_Klass{i}" : $"CKlass{i}";
            var cls = new SchemaClass { Name = name, Module = module, Size = 8 };
            cls.Fields.Add(new SchemaField
            {
                Name = "m_x",
                Offset = 0,
                Type = new SchemaType { Category = SchemaType.Types.Category.Builtin, Name = "int32" },
            });
            walk.Classes.Add(cls);
            Obs(name, module, "schema_class");
        }

        var en = new SchemaEnum { Name = "MoveType_t", Module = "server", Alignment = "uint8_t" };
        en.Members.Add(new SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        walk.Enums.Add(en);
        Obs("MoveType_t", "server", "schema_enum");

        var convars = new ConVarsWalk();
        convars.Convars.Add(new ConVar { Name = "sv_cheats", Default = "0", Description = "Allow cheats" });
        Obs("sv_cheats", "", "convar");
        var commands = new CommandsWalk();
        commands.Commands.Add(new Command { Name = "kill", Description = "Commit suicide" });
        Obs("kill", "", "command");
        var netmsg = new NetworkMessagesWalk();
        var channel = new NetworkChannel { Name = "NetMessages" };
        channel.Messages.Add(new NetworkMessageEntry { Id = 7, ProtoMessageType = "CNETMsg_Tick" });
        netmsg.Channels.Add(channel);
        Obs("CNETMsg_Tick", "NetMessages", "network_message");
        var engineConstants = new EngineConstantsWalk();
        engineConstants.Constants.Add(new EngineConstant
        {
            Name = "MAX_PLAYERS",
            Source = "schema_enum:server.dll/CGameRules",
            IntValue = 64,
        });
        Obs("MAX_PLAYERS", "server.dll", "engine_constant");
        var stringPools = new StringPoolsWalk();
        var symPool = new StringPool { Name = "CUtlSymbolLarge" };
        symPool.Entries.Add("m_x");
        stringPools.Pools.Add(symPool);
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

    private sealed record BuildSpec(string Build, string PinSig);

    /// <summary>The era id a BuildSpec's layout signature belongs to (fixture eras).</summary>
    private static string EraForSig(string sig) => sig == Q1Sig ? "cs2-2026-01-22" : "cs2-2026-04-21";

    // Build a fixture repo with data/cs2-assets-inventory.json (eras[] cs2-2026-04-21 + cs2-2026-01-22,
    // each with a class-count band + per-platform signature; depots[] content+binary; builds[] one
    // entry per NUMERIC committed build mapping build_id -> era). Per committed build: cache/binaries
    // + a committed artifacts/<build>/<platform>/ set (entity_schema.json marks it discoverable).
    // Tests use FAKE runners, so no natives walker binary is written (never launched). Pins cwd +
    // clears env vars for the body; restores all. Returns the fixture-rooted resolver to the body.
    private static void InExtractFixture(
        string platform, (int Min, int Max) currentBand, (int Min, int Max) q1Band,
        IEnumerable<BuildSpec> committed, Action<string, EraWalkerResolver> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "extract-" + Guid.NewGuid().ToString("N"));
        // walker/CMakeLists.txt is the repo-root sentinel (used by non-explicit-root callers); the
        // resolver here is given an explicit root, but keep it for realism.
        Directory.CreateDirectory(Path.Combine(root, "walker"));
        File.WriteAllText(Path.Combine(root, "walker", "CMakeLists.txt"), "# fixture");
        Directory.CreateDirectory(Path.Combine(root, "data"));

        var committedList = committed.ToList();

        // builds[] rows for the NUMERIC committed builds (hex-ish single-build test ids resolve via
        // the fresh-build fallback to the newest compile-pin era instead).
        var buildRows = new List<string>();
        foreach (var spec in committedList)
        {
            if (long.TryParse(spec.Build, out _))
            {
                buildRows.Add(
                    $$"""{ "build_id": {{spec.Build}}, "era": "{{EraForSig(spec.PinSig)}}", "content": "0", "binaries": {} }""");
            }
        }

        // Register each era's signature under the RUNNING platform so the per-platform second gate
        // passes on either a windows-x86_64 or linux-x86_64 host (the fake emits the era sig).
        File.WriteAllText(Path.Combine(root, "data", "cs2-assets-inventory.json"), $$"""
        {
          "_meta": { "counts": {} },
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "eras": [
            { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
              "layoutSignatures": { "{{platform}}": "{{CurrentSig}}" },
              "minClasses": {{currentBand.Min}}, "maxClasses": {{currentBand.Max}} },
            { "era": "cs2-2026-01-22", "kind": "compile-pin", "hl2sdkSha": "{{Q1Sha}}",
              "layoutSignatures": { "{{platform}}": "{{Q1Sig}}" },
              "minClasses": {{q1Band.Min}}, "maxClasses": {{q1Band.Max}} }
          ],
          "depots": [
            { "depot_id": 2347770, "role": "content", "platforms": ["windows-x86_64","linux-x86_64"], "history": [] },
            { "depot_id": 2347771, "role": "binary",  "platforms": ["windows-x86_64","linux-x86_64"], "history": [] }
          ],
          "builds": [{{string.Join(",", buildRows)}}]
        }
        """);

        foreach (var spec in committedList)
        {
            var binariesDir = Path.Combine(root, "cache", "binaries", spec.Build, platform);
            Directory.CreateDirectory(binariesDir);
            File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"),
                WithEmbeddedFdp(BuildElf(), BuildFdp("netmessages.proto")));
            File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"),
                WithEmbeddedFdp(BuildPe(), BuildFdp("networkbasetypes.proto")));

            var setDir = Path.Combine(root, "artifacts", spec.Build, platform);
            Directory.CreateDirectory(setDir);
            var prov = new Cs2SchemaTracker.Schemas.Provenance
            {
                SchemaVersion = "test",
                BuildId = spec.Build,
                Platform = platform,
                Cs2Build = new CS2BuildIdentity { SchemaRevision = spec.PinSig, SteamBuildId = spec.Build },
            };
            File.WriteAllText(Path.Combine(setDir, "provenance.json"),
                new JsonFormatter(JsonFormatter.Settings.Default).Format(prov));
            File.WriteAllText(Path.Combine(setDir, "entity_schema.json"), "{}");
        }

        var oldBin = Environment.GetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar);
        var oldNatives = Environment.GetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar);
        var oldBins = Environment.GetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar);
        Environment.SetEnvironmentVariable(WalkerProcessRunner.BinaryPathEnvVar, null);
        Environment.SetEnvironmentVariable(EraWalkerResolver.NativesRootEnvVar, null);
        Environment.SetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar, null);
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
            Environment.SetEnvironmentVariable(ExtractCommand.BinariesRootEnvVar, oldBins);
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static readonly (int, int) WideBand = (1, 1000);

    // Expected selection results (hoisted to satisfy CA1861 — constant arrays as Assert args).
    private static readonly string[] ExpectedAllBuilds = { "20000001", "20000002", "20000003" };
    // --only-existing-builds after uncommitting 20000002 (its artifacts/ set removed, inventory row kept).
    private static readonly string[] ExpectedExistingOnlyBuilds = { "20000001", "20000003" };
    private static readonly string[] ExpectedQ1Builds = { "30000002", "30000003" };
    private static readonly string[] ExpectedBackfillBuilds = { "20000002" };
    private static readonly string[] ExpectedPinBuilds = { "40000001" };
    private static readonly string[] UnknownPlatformArgs = { "--all", "--platform", "macos-arm64" };

    // ---- USAGE ERRORS (through ExtractCommand.Run; return before any repo access) -------------

    [Fact]
    public void Selection_None_Is_Usage_Error_Exit_64()
    {
        var platform = MatchingPlatform() ?? "windows-x86_64";
        int runs = 0;
        var code = ExtractCommand.Run(
            new[] { "--platform", platform },
            () => { runs++; return new CountingFakeRunner(CurrentSig, 5); },
            new EraWalkerResolver(Path.GetTempPath()));

        Assert.Equal(64, code);
        Assert.Equal(0, runs);   // never reached the per-build loop.
    }

    [Fact]
    public void Selection_TwoModes_Is_Usage_Error_Exit_64()
    {
        var platform = MatchingPlatform() ?? "windows-x86_64";
        int runs = 0;
        var code = ExtractCommand.Run(
            new[] { "--all", "--era", "cs2-2026-01-22", "--platform", platform },
            () => { runs++; return new CountingFakeRunner(CurrentSig, 5); },
            new EraWalkerResolver(Path.GetTempPath()));

        Assert.Equal(64, code);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void Selection_UnknownPlatform_Is_Usage_Error_Exit_64()
    {
        int runs = 0;
        var code = ExtractCommand.Run(
            UnknownPlatformArgs,
            () => { runs++; return new CountingFakeRunner(CurrentSig, 5); },
            new EraWalkerResolver(Path.GetTempPath()));

        Assert.Equal(64, code);
        Assert.Equal(0, runs);
    }

    // ---- END-TO-END through ExtractCommand.Run (the injected resolver's RepoRoot governs all) --

    // Map a committed BuildSpec to the layout signature its era expects, so the era-aware fake
    // runner emits the right signature per build and the host's second gate passes.
    private static Dictionary<string, string> SignatureMap(IEnumerable<BuildSpec> committed)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in committed)
            map[s.Build] = s.PinSig;
        return map;
    }

    // The output dirs Run produced for a platform under <root>/extract-out/, in Ordinal order.
    private static string[] ProducedOutputBuilds(string root, string platform)
    {
        var outRoot = Path.Combine(root, "extract-out");
        if (!Directory.Exists(outRoot))
            return Array.Empty<string>();
        return Directory.EnumerateDirectories(outRoot)
            .Where(d => File.Exists(Path.Combine(d, platform, "entity_schema.json")))
            .Select(Path.GetFileName)
            .OrderBy(b => b, StringComparer.Ordinal)
            .ToArray()!;
    }

    [WindowsOnlyFact]
    public void Run_All_EndToEnd_ReWalks_Exactly_The_Fixture_Committed_Builds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Mixed-era fixture: two cs2-2026-04-21, one cs2-2026-01-22. --all must select exactly these three —
        // proving the injected resolver's RepoRoot (the fixture, NOT the real 244-build repo)
        // governs selection end-to-end through Run.
        var committed = new[]
        {
            new BuildSpec("20000003", CurrentSig),
            new BuildSpec("20000001", CurrentSig),
            new BuildSpec("20000002", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--all", "--platform", platform, "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            Assert.Equal(0, code);
            // Exactly the fixture's three committed builds were re-walked (and only those) — each
            // exactly once. If selection had hit the real repo, this would be 244 / mismatched.
            Assert.Equal(ExpectedAllBuilds, runner.CallsByBuild.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.All(runner.CallsByBuild.Values, c => Assert.Equal(1, c));
            // And exactly those three produced a CORE set under the out root.
            Assert.Equal(ExpectedAllBuilds, ProducedOutputBuilds(root, platform));
        });
    }

    [WindowsOnlyFact]
    public void Run_All_Selects_Inventory_Builds_Even_Without_A_Committed_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Same three-build fixture, but the body UNCOMMITS 20000002 (removes its artifacts/ set) while
        // leaving its inventory builds[] row and input binaries intact. --all is INVENTORY-driven, so
        // it must STILL select all three — including the build with no committed set. Under the old
        // committed-artifacts selection this would have been two.
        var committed = new[]
        {
            new BuildSpec("20000003", CurrentSig),
            new BuildSpec("20000001", CurrentSig),
            new BuildSpec("20000002", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            Directory.Delete(Path.Combine(root, "artifacts", "20000002", platform), recursive: true);
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--all", "--platform", platform, "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            Assert.Equal(0, code);
            Assert.Equal(ExpectedAllBuilds, runner.CallsByBuild.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        });
    }

    [WindowsOnlyFact]
    public void Run_All_OnlyExistingBuilds_Restricts_To_Committed_Builds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Uncommit 20000002 (remove artifacts/, keep inventory row + binaries). --all alone would pick
        // all three (inventory); --only-existing-builds must trim it back to the two that still have a
        // committed set — the legacy re-walk-only behavior, now an explicit modifier.
        var committed = new[]
        {
            new BuildSpec("20000003", CurrentSig),
            new BuildSpec("20000001", CurrentSig),
            new BuildSpec("20000002", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            Directory.Delete(Path.Combine(root, "artifacts", "20000002", platform), recursive: true);
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--all", "--only-existing-builds", "--platform", platform, "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            Assert.Equal(0, code);
            Assert.Equal(ExpectedExistingOnlyBuilds, runner.CallsByBuild.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        });
    }

    [WindowsOnlyFact]
    public void Run_Backfill_Selects_Only_Builds_Missing_The_Platform_With_Binaries()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Three committed builds. In the body we simulate the backfill scenario:
        //   20000001 — keeps its committed set          -> already done, must be SKIPPED.
        //   20000002 — set removed, binaries kept       -> needs backfill, must be SELECTED.
        //   20000003 — set removed AND binaries removed -> no inputs, must be SKIPPED (not failed).
        var committed = new[]
        {
            new BuildSpec("20000001", CurrentSig),
            new BuildSpec("20000002", Q1Sig),
            new BuildSpec("20000003", CurrentSig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            Directory.Delete(Path.Combine(root, "artifacts", "20000002", platform), recursive: true);
            Directory.Delete(Path.Combine(root, "artifacts", "20000003", platform), recursive: true);
            Directory.Delete(Path.Combine(root, "cache", "binaries", "20000003", platform), recursive: true);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--backfill", "--platform", platform, "--out", Path.Combine(root, "extract-out"), "--no-acquire" },
                () => runner, resolver);

            Assert.Equal(0, code);
            // ONLY 20000002 was walked: 20000001 already has the set, 20000003 has no binaries.
            Assert.Equal(ExpectedBackfillBuilds, runner.CallsByBuild.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(ExpectedBackfillBuilds, ProducedOutputBuilds(root, platform));
        });
    }

    [WindowsOnlyFact]
    public void Run_Era_EndToEnd_Filters_To_That_Eras_Builds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[]
        {
            new BuildSpec("30000001", CurrentSig),
            new BuildSpec("30000002", Q1Sig),
            new BuildSpec("30000003", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--era", "cs2-2026-01-22", "--platform", platform, "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            Assert.Equal(0, code);
            // Only the two cs2-2026-01-22 builds were re-walked (cs2-2026-04-21 build untouched).
            Assert.Equal(ExpectedQ1Builds, runner.CallsByBuild.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(ExpectedQ1Builds, ProducedOutputBuilds(root, platform));
        });
    }

    [WindowsOnlyFact]
    public void Run_Pin_EndToEnd_Filters_To_That_Pins_Builds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[]
        {
            new BuildSpec("40000001", CurrentSig),
            new BuildSpec("40000002", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            // --pin is a prefix of the cs2-2026-04-21 era's hl2sdk SHA.
            var code = ExtractCommand.Run(
                new[] { "--pin", "b8dcaf14", "--platform", platform, "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            Assert.Equal(0, code);
            Assert.Equal(ExpectedPinBuilds, runner.CallsByBuild.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(ExpectedPinBuilds, ProducedOutputBuilds(root, platform));
        });
    }

    [WindowsOnlyFact]
    public void Run_Verify_EndToEnd_Matching_Core_Is_CoreClean_Exit_0()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("c0000001", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // Seed a REAL committed CORE set (replacing the fixture's "{}" stub) by extracting with
            // the SAME canned walk a re-walk will reproduce. RunExtract writes to the committed
            // artifacts dir; --verify then byte-compares the re-walk CORE to it -> CORE-CLEAN.
            SeedCommittedCoreSet(root, "c0000001", platform, resolver, CurrentSig);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "c0000001", "--platform", platform, "--verify",
                        "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            // CORE-CLEAN across the board -> no hard failure -> exit 0.
            Assert.Equal(0, code);
        });
    }

    [WindowsOnlyFact]
    public void Run_Verify_EndToEnd_Mutated_Committed_Core_Is_Regression_Exit_1()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("c0000002", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedCommittedCoreSet(root, "c0000002", platform, resolver, CurrentSig);

            // Mutate ONE committed CORE artifact so the re-walk no longer reproduces it. The
            // normalized compare (schemaVersion/toolGitSha excepted) must report a diff -> REGRESSION.
            var committedConvars = Path.Combine(root, "artifacts", "c0000002", platform, "convars.json");
            File.WriteAllText(committedConvars, "{ \"convars\": [ { \"name\": \"tampered\" } ] }");

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "c0000002", "--platform", platform, "--verify",
                        "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            // A CORE regression is a HARD failure -> Summarize's batch exit truth (flat 1; the
            // Regression classification is derived AFTER a successful RunExtract, so the raw-code
            // passthrough never engages here — see RunSelection's comment).
            Assert.Equal(1, code);
        });
    }

    // ---- --commit (promote re-walked sets into the FIXTURE artifacts/, NO git) ----------------
    //
    // CRITICAL ISOLATION: every test below drives --commit ONLY through the fixture-rooted
    // resolver seam, so ExtractCommand derives its artifacts/ target from resolver.RepoRoot (the
    // throwaway temp fixture), NEVER the real checked-out artifacts/. All assertions are against
    // the FIXTURE's artifacts/<build>/<platform>/.

    // The full committed CORE set --commit must land (mirrors EmitFullSet's CORE outputs).
    private static void AssertFullCoreSetPresent(string setDir)
    {
        foreach (var name in CoreFileNames)
        {
            Assert.True(File.Exists(Path.Combine(setDir, name)), $"--commit must promote {name}");
        }
        Assert.True(File.Exists(Path.Combine(setDir, "provenance.json")), "--commit must promote provenance.json");
        Assert.True(File.Exists(Path.Combine(setDir, "protos.descriptorset")),
            "--commit must promote protos.descriptorset");
    }

    [WindowsOnlyFact]
    public void Commit_Promotes_ReWalked_Set_Into_Fixture_Artifacts_Status_Committed_Exit_0()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("e0000001", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "e0000001", platform);
            // Precondition: the fixture seeded only an "{}" entity_schema stub — no full CORE set.
            Assert.Equal("{}", File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000001", "--platform", platform, "--commit" },
                    () => runner, resolver);
                Assert.Equal(0, code);   // promote succeeded; nothing hard.
            });

            // The FIXTURE artifacts/<build>/<platform>/ now carries the full re-walked CORE set.
            AssertFullCoreSetPresent(setDir);
            // The "{}" stub was replaced by a real entity_schema with the canned 5 classes.
            Assert.Equal(5, CountClasses(Path.Combine(setDir, "entity_schema.json")));

            // Summary: status Committed, committed= count == 1.
            Assert.Contains("Committed", stderr);
            Assert.Contains("committed=1", stderr);
        });
    }

    [WindowsOnlyFact]
    public void Commit_Clobbers_PreExisting_Different_Set_With_ReWalked_Content()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("e0000002", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "e0000002", platform);

            // Seed a DIFFERENT pre-existing set: a sentinel CORE file + a stray file that must NOT
            // survive the clobber (the promote replaces the whole directory).
            File.WriteAllText(Path.Combine(setDir, "entity_schema.json"),
                "{ \"classes\": [ { \"name\": \"COldStaleClass\" } ] }");
            File.WriteAllText(Path.Combine(setDir, "convars.json"),
                "{ \"convars\": [ { \"name\": \"old_stale_cvar\" } ] }");
            File.WriteAllText(Path.Combine(setDir, "STRAY_OLD_FILE.txt"), "should not survive clobber");

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "e0000002", "--platform", platform, "--commit" },
                () => runner, resolver);
            Assert.Equal(0, code);

            // The OLD content is gone; the NEW re-walked content is present.
            AssertFullCoreSetPresent(setDir);
            Assert.Equal(5, CountClasses(Path.Combine(setDir, "entity_schema.json")));
            var entity = File.ReadAllText(Path.Combine(setDir, "entity_schema.json"));
            Assert.DoesNotContain("COldStaleClass", entity);
            var convars = File.ReadAllText(Path.Combine(setDir, "convars.json"));
            Assert.Contains("sv_cheats", convars);                 // canned re-walk convar.
            Assert.DoesNotContain("old_stale_cvar", convars);      // old set replaced.
            // The atomic dir replace (rename-aside + Move — the two-step promote) drops files not
            // in the re-walked set, same as the prior delete-then-move.
            Assert.False(File.Exists(Path.Combine(setDir, "STRAY_OLD_FILE.txt")),
                "the clobber replaces the whole set dir; stray prior files must not survive");
            // No ".old-*" sibling survives a clean --commit promote either.
            var oldSurvivors = Directory.GetDirectories(Path.Combine(root, "artifacts", "e0000002"))
                .Select(Path.GetFileName)
                .Where(n => n!.Contains(".old-", StringComparison.Ordinal))
                .ToList();
            Assert.Empty(oldSurvivors);
        });
    }

    // ---- orphan staging-dir sweep --------------------------------------------------------------

    [Fact]
    public void SweepOrphanedStagingDirs_RemovesStagingAndOldSiblings_LeavesRealPlatformDirsAlone()
    {
        var root = Path.Combine(Path.GetTempPath(), "sweep-" + Guid.NewGuid().ToString("N"));
        var artifactsRoot = Path.Combine(root, "artifacts");
        Directory.CreateDirectory(Path.Combine(artifactsRoot, "20000001", "windows-x86_64.staging-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        Directory.CreateDirectory(Path.Combine(artifactsRoot, "20000001", "windows-x86_64.old-bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        var realWin = Path.Combine(artifactsRoot, "20000001", "windows-x86_64");
        Directory.CreateDirectory(realWin);
        File.WriteAllText(Path.Combine(realWin, "entity_schema.json"), "{}");
        var realLinux = Path.Combine(artifactsRoot, "20000002", "linux-x86_64");
        Directory.CreateDirectory(realLinux);
        File.WriteAllText(Path.Combine(realLinux, "entity_schema.json"), "{}");
        try
        {
            ExtractCommand.SweepOrphanedStagingDirs(artifactsRoot);

            var remaining = Directory.EnumerateDirectories(artifactsRoot, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .ToList();
            Assert.DoesNotContain(remaining, n => n!.Contains(".staging-", StringComparison.Ordinal));
            Assert.DoesNotContain(remaining, n => n!.Contains(".old-", StringComparison.Ordinal));
            // Real platform dirs (and their content) are untouched.
            Assert.True(Directory.Exists(realWin));
            Assert.True(File.Exists(Path.Combine(realWin, "entity_schema.json")));
            Assert.True(Directory.Exists(realLinux));
        }
        finally
        {
            try
            { Directory.Delete(root, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void SweepOrphanedStagingDirs_NoArtifactsRoot_IsANoOp()
    {
        var artifactsRoot = Path.Combine(Path.GetTempPath(), "sweep-absent-" + Guid.NewGuid().ToString("N"));
        // Must not throw when artifacts/ does not exist (e.g. a brand-new off-repo checkout).
        ExtractCommand.SweepOrphanedStagingDirs(artifactsRoot);
    }

    [WindowsOnlyFact]
    public void Commit_SweepsPreExistingOrphanedStagingDirs_BeforeSelection()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("e0000004", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // Simulate leftovers from a PRIOR killed extract: an orphaned staging dir under a
            // DIFFERENT build than the one this run selects, plus an orphaned ".old-" dir under the
            // SAME build. Neither should survive the commit run's startup sweep.
            var orphanedStaging = Path.Combine(
                root, "artifacts", "e0000002", "windows-x86_64.staging-cccccccccccccccccccccccccccccccc");
            Directory.CreateDirectory(orphanedStaging);
            File.WriteAllText(Path.Combine(orphanedStaging, "partial.txt"), "leftover from a killed run");

            var setDir = Path.Combine(root, "artifacts", "e0000004", platform);
            var orphanedOld = setDir + ".old-dddddddddddddddddddddddddddddddd";
            Directory.CreateDirectory(orphanedOld);
            File.WriteAllText(Path.Combine(orphanedOld, "stale.txt"), "leftover superseded set");

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000004", "--platform", platform, "--commit" },
                    () => runner, resolver);
                Assert.Equal(0, code);
            });

            Assert.Contains($"extract: removed orphaned staging dir {orphanedStaging}", stderr);
            Assert.Contains($"extract: removed orphaned staging dir {orphanedOld}", stderr);
            Assert.False(Directory.Exists(orphanedStaging), "the pre-existing orphan must be swept");
            Assert.False(Directory.Exists(orphanedOld), "the pre-existing .old- orphan must be swept");
            // The run's own promote still succeeded normally.
            AssertFullCoreSetPresent(setDir);
        });
    }

    [WindowsOnlyFact]
    public void Commit_OutOfBand_ClassCount_Is_Blocked_NoWrite_Committed_Set_Untouched()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Narrow band [100,200] so the canned 5-class walk is OUTSIDE the era band.
        var committed = new[] { new BuildSpec("e0000003", CurrentSig) };
        InExtractFixture(platform, (100, 200), WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "e0000003", platform);
            var before = File.ReadAllText(Path.Combine(setDir, "entity_schema.json"));
            Assert.Equal("{}", before);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000003", "--platform", platform, "--commit" },
                    () => runner, resolver);
                // BLOCKING: the class-band gate is HARD under --commit too — RunExtract
                // itself refuses to write ANYTHING (exit 77), so the batch's own exit is non-zero (the
                // flat 1 Summarize returns whenever any build ended Gated).
                Assert.Equal(1, code);
            });

            // Nothing was promoted: the committed fixture set is UNTOUCHED (still the "{}" stub, no
            // partial/garbage CORE set clobbered in) — this is exactly the "Gated but promoted" shape
            // the blocking class-band gate makes structurally impossible.
            Assert.Equal("{}", File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));
            Assert.False(File.Exists(Path.Combine(setDir, "protos.descriptorset")),
                "an out-of-band --commit must not clobber the committed set with a partial/garbage set");
            Assert.Contains("CLASS-BAND GATE FAILED", stderr);
            Assert.Contains("Gated", stderr);
            Assert.Contains("committed=0", stderr);
            Assert.Contains("gated=1", stderr);
        });
    }

    [WindowsOnlyFact]
    public void OffRepo_OutOfBand_ClassCount_Is_Gated_And_NOT_Promoted()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Same out-of-band build, but OFF-REPO (no --commit). Now that the class-band gate blocks
        // under --commit too, this is no longer a contrast to the --commit case above — both refuse
        // to write, symmetrically. The single, non-commit build surfaces RunExtract's own raw exit
        // code (77) verbatim instead of going through the batch summary's flat 1.
        var committed = new[] { new BuildSpec("e0000004", CurrentSig) };
        InExtractFixture(platform, (100, 200), WideBand, committed, (root, resolver) =>
        {
            var outRoot = Path.Combine(root, "extract-out");
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000004", "--platform", platform, "--out", outRoot },
                    () => runner, resolver);
                Assert.Equal(77, code);
            });

            // Off-repo: the gated set is DISCARDED — no output dir survived (RunExtract never even
            // wrote it; there was never anything to discard).
            var outDir = Path.Combine(outRoot, "e0000004", platform);
            Assert.False(Directory.Exists(outDir),
                "off-repo gate must discard an out-of-band set (not promote it)");
            Assert.Contains("Gated", stderr);
            Assert.Contains("gated=1", stderr);
            // And the committed fixture set is untouched (still the "{}" stub) — off-repo never writes it.
            Assert.Equal("{}",
                File.ReadAllText(Path.Combine(root, "artifacts", "e0000004", platform, "entity_schema.json")));
        });
    }

    // A genuine multi-build BATCH (two --build ids, not one) where ONE build is
    // class-band-gated must still make the WHOLE batch's exit non-zero (the flat 1 from Summarize's
    // batch exit truth), while fail-isolation keeps processing the OTHER, healthy build to completion.
    // The two builds are pinned to DIFFERENT eras (different signatures) so each gets its own band:
    // the CurrentSig era stays WideBand (in-band -> Ok), the Q1Sig era is narrowed to [100,200]
    // (out-of-band at the canned classCount:5 -> Gated).
    [WindowsOnlyFact]
    public void Batch_TwoBuilds_OneGated_Makes_Whole_Batch_Exit_1_Other_Build_Still_Processed()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[]
        {
            new BuildSpec("f0000001", CurrentSig),   // WideBand era -> in-band -> Ok.
            new BuildSpec("f0000002", Q1Sig),        // narrowed era -> out-of-band -> Gated.
        };
        InExtractFixture(platform, WideBand, (100, 200), committed, (root, resolver) =>
        {
            var outRoot = Path.Combine(root, "extract-out");
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "f0000001", "--build", "f0000002", "--platform", platform,
                            "--out", outRoot },
                    () => runner, resolver);
                // The batch's OWN exit is the flat 1 (Summarize's batch exit truth), NOT the raw
                // per-build 77 — a real batch (2+ builds) always goes through Summarize, never the
                // single-forward-build raw-code passthrough.
                Assert.Equal(1, code);
            });

            // Fail-isolation: BOTH builds were visited (the fake runner was invoked for both), the
            // healthy one produced a full set, the gated one produced nothing.
            Assert.Equal(2, runner.CallsByBuild.Values.Sum());
            Assert.True(File.Exists(Path.Combine(outRoot, "f0000001", platform, "entity_schema.json")),
                "the in-band build must still be fully processed despite its sibling being gated");
            Assert.False(Directory.Exists(Path.Combine(outRoot, "f0000002", platform)),
                "the out-of-band build must be gated (nothing written), not promoted");
            Assert.Contains("ok=1", stderr);
            Assert.Contains("gated=1", stderr);
        });
    }

    // --single-walk must be ACCEPTED (not rejected as an unknown argument) and the
    // fake runner must be invoked exactly ONCE per build regardless — the fake-runner test seam never
    // double-walks in the first place (RunExtract's doWalkTwice is forced off whenever runnerFactory
    // is non-null), so --single-walk is a true no-op here; real double-walk byte-compare coverage
    // lives on the production/real-runner path (the byte-compare itself is deliberately not covered
    // by a fake-runner test).
    [WindowsOnlyFact]
    public void SingleWalk_Flag_Is_Accepted_And_Commit_Still_Promotes()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("f0000003", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "f0000003", platform);
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "f0000003", "--platform", platform, "--commit", "--single-walk" },
                () => runner, resolver);

            Assert.Equal(0, code);
            Assert.Equal(1, runner.CallsByBuild.GetValueOrDefault("f0000003"));   // one walk, not two.
            AssertFullCoreSetPresent(setDir);
        });
    }

    // --allow-mixed-walkers must be ACCEPTED (not rejected as an unknown argument) and the
    // commit must still promote. The WALKER IDENTITY GATE itself (PreflightWalkerIdentity) never
    // fires on the fake-runner test seam (runnerFactory is non-null -- no real binary to identify),
    // so this pins ONLY the flag's plumbing through TryParseSelection into Options.AllowMixedWalkers;
    // the gate's fail/warn/proceed decision matrix is covered directly (no process launch needed) by
    // ExtractWalkerIdentityGateTest against EvaluateWalkerIdentityGate.
    [WindowsOnlyFact]
    public void AllowMixedWalkers_Flag_Is_Accepted_And_Commit_Still_Promotes()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("f0000004", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "f0000004", platform);
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "f0000004", "--platform", platform, "--commit", "--allow-mixed-walkers" },
                () => runner, resolver);

            Assert.Equal(0, code);
            AssertFullCoreSetPresent(setDir);
        });
    }

    // --no-gate must bypass the (now-blocking) class-band gate under --commit too: opts.Gate threads
    // into RunExtract's classCountGate parameter, so an out-of-band count is promoted anyway when the
    // operator explicitly opted out.
    [WindowsOnlyFact]
    public void Commit_NoGate_Promotes_Even_When_OutOfBand()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("f0000004", CurrentSig) };
        InExtractFixture(platform, (100, 200), WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "f0000004", platform);
            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "f0000004", "--platform", platform, "--commit", "--no-gate" },
                () => runner, resolver);

            Assert.Equal(0, code);
            AssertFullCoreSetPresent(setDir);
            Assert.Equal(5, CountClasses(Path.Combine(setDir, "entity_schema.json")));
        });
    }

    [WindowsOnlyFact]
    public void Commit_Verify_Is_NonBlocking_Promoted_Status_Committed_Exit_0()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Seed a committed CORE set that DIFFERS from what the re-walk will produce (a tampered
        // convars). Under --commit, --verify SNAPSHOTS the prior committed CORE's normalized SHAs
        // BEFORE the promote clobbers the set, then compares the freshly-promoted set against that
        // in-memory snapshot. Because the re-walk reproduces the SeedCommittedCoreSet bytes for every
        // CORE file EXCEPT the tampered convars.json, the snapshot compare reports a real diff:
        // "CHANGED vs prior: convars.json" (the changed CORE file is named) — but the promote still
        // proceeds (NON-BLOCKING), status Committed, exit 0. The re-walk content fully replaces the
        // differing prior set.
        var committed = new[] { new BuildSpec("e0000005", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedCommittedCoreSet(root, "e0000005", platform, resolver, CurrentSig);
            var setDir = Path.Combine(root, "artifacts", "e0000005", platform);
            // Make the committed CORE differ from what the re-walk reproduces (ONLY convars.json).
            File.WriteAllText(Path.Combine(setDir, "convars.json"),
                "{ \"convars\": [ { \"name\": \"tampered_prior\" } ] }");

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000005", "--platform", platform, "--commit", "--verify",
                            },
                    () => runner, resolver);
                // NON-BLOCKING --verify in --commit mode: exit 0 (review catches drift, not the gate).
                Assert.Equal(0, code);
            });

            // The differing prior CORE was clobbered by the re-walk (tamper gone, canned convar back).
            var convars = File.ReadAllText(Path.Combine(setDir, "convars.json"));
            Assert.Contains("sv_cheats", convars);
            Assert.DoesNotContain("tampered_prior", convars);
            // The snapshot-before-clobber compare reports the prior-vs-promoted CORE drift, naming the
            // changed CORE file (convars.json) — surfaced LOUDLY in the per-build detail + summary.
            Assert.Contains("CHANGED vs prior: convars.json", stderr);
            // Status Committed in the summary; promoted (clobbered); no hard regression/failed.
            Assert.Contains("Committed", stderr);
            Assert.Contains("committed=1", stderr);
            Assert.Contains("regression=0", stderr);
            Assert.Contains("failed=0", stderr);
        });
    }

    [WindowsOnlyFact]
    public void Commit_Verify_Identical_Prior_Reports_Unchanged_Promoted_Exit_0()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Seed the prior committed CORE with the EXACT bytes the fake-runner-driven extract will
        // reproduce (SeedCommittedCoreSet uses the same gate-armed extract path + same canned walk).
        // Under --commit + --verify the snapshot-before-clobber compare therefore finds no CORE drift
        // -> "unchanged vs prior" in the detail, still promoted, exit 0.
        var committed = new[] { new BuildSpec("e0000009", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedCommittedCoreSet(root, "e0000009", platform, resolver, CurrentSig);
            var setDir = Path.Combine(root, "artifacts", "e0000009", platform);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000009", "--platform", platform, "--commit", "--verify",
                            },
                    () => runner, resolver);
                Assert.Equal(0, code);
            });

            // No CORE drift between the prior committed bytes and the freshly-promoted set.
            Assert.Contains("unchanged vs prior", stderr);
            Assert.DoesNotContain("CHANGED vs prior", stderr);
            // Still promoted (clobbered) into the fixture artifacts/, status Committed, exit 0.
            AssertFullCoreSetPresent(setDir);
            Assert.Contains("Committed", stderr);
            Assert.Contains("committed=1", stderr);
            Assert.Contains("regression=0", stderr);
            Assert.Contains("failed=0", stderr);
        });
    }

    [WindowsOnlyFact]
    public void Commit_Verify_No_Prior_Committed_CoreFiles_Reports_New_Promoted_Exit_0()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // "No prior committed set" through the public Run seam: a build is only SELECTABLE when its
        // artifacts/<build>/<platform>/ carries an entity_schema.json (CommittedBuilds.Discover /
        // --build both require it), so a literally-absent dir can never be selected. The reachable
        // "no prior CORE to compare against" shape is the fixture's default seed: the dir exists with
        // ONLY the entity_schema "{}" stub + provenance.json and NONE of the other CORE artifacts.
        //
        // SnapshotCore therefore captures Existed=true but with a null SHA for every CORE file except
        // entity_schema.json. After the promote lands the full re-walked CORE, the compare sees those
        // previously-absent files appear (null -> non-null) AND the stub entity_schema "{}" replaced by
        // the real 5-class schema, so it reports CHANGED naming those CORE files. (A literal Existed=
        // false "new" verdict requires an absent dir, which is unreachable via Run — see note above.)
        var committed = new[] { new BuildSpec("e0000010", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "e0000010", platform);
            // Precondition: the fixture seeded ONLY the entity_schema "{}" stub + provenance — no
            // other CORE artifacts (so every other CORE file's prior snapshot SHA is null).
            Assert.Equal("{}", File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));
            Assert.False(File.Exists(Path.Combine(setDir, "convars.json")));

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000010", "--platform", platform, "--commit", "--verify",
                            },
                    () => runner, resolver);
                Assert.Equal(0, code);
            });

            // The previously-absent CORE files now exist after the promote -> CHANGED vs prior,
            // naming (among others) the entity_schema + the now-present convars CORE file.
            Assert.Contains("CHANGED vs prior:", stderr);
            Assert.Contains("convars.json", stderr);
            // The re-walked set was still promoted into the artifacts dir, status Committed, exit 0.
            AssertFullCoreSetPresent(setDir);
            Assert.Equal(5, CountClasses(Path.Combine(setDir, "entity_schema.json")));
            Assert.Contains("Committed", stderr);
            Assert.Contains("committed=1", stderr);
            Assert.Contains("regression=0", stderr);
            Assert.Contains("failed=0", stderr);
        });
    }

    [WindowsOnlyFact]
    public void Commit_Verify_Prior_Differs_Only_In_SchemaVersion_Reports_Unchanged()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // CHANGED is computed on NORMALIZED content (the snapshot uses NormalizedJsonSha, which masks
        // schemaVersion + toolGitSha). Seed the prior committed CORE with the exact re-walk bytes, then
        // perturb ONLY the schemaVersion field of a CORE file. The normalized snapshot compare must NOT
        // count that as a diff -> "unchanged vs prior" (version-stamp noise is not CORE drift).
        var committed = new[] { new BuildSpec("e0000011", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedCommittedCoreSet(root, "e0000011", platform, resolver, CurrentSig);
            var setDir = Path.Combine(root, "artifacts", "e0000011", platform);

            // Rewrite the committed convars.json so it differs from the re-walk ONLY in schemaVersion.
            var convarsPath = Path.Combine(setDir, "convars.json");
            var original = File.ReadAllText(convarsPath);
            var perturbed = SchemaVersionRegex.Replace(original,
                "\"schemaVersion\": \"PERTURBED-ONLY-VERSION\"");
            // Guard: the seed actually carried a schemaVersion to perturb (else this test is vacuous).
            Assert.NotEqual(original, perturbed);
            File.WriteAllText(convarsPath, perturbed);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000011", "--platform", platform, "--commit", "--verify",
                            },
                    () => runner, resolver);
                Assert.Equal(0, code);
            });

            // schemaVersion-only drift is normalized away: the build reports unchanged, not CHANGED.
            Assert.Contains("unchanged vs prior", stderr);
            Assert.DoesNotContain("CHANGED vs prior", stderr);
            Assert.Contains("Committed", stderr);
            Assert.Contains("committed=1", stderr);
        });
    }

    [WindowsOnlyFact]
    public void OffRepo_Verify_Regression_Stays_Hard_Failure_Exit_1_Contrast()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Contrast to the non-blocking --commit verify: off-repo, a CORE regression is HARD (the
        // batch summary's flat exit 1 — see Summarize's batch exit truth).
        var committed = new[] { new BuildSpec("e0000006", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedCommittedCoreSet(root, "e0000006", platform, resolver, CurrentSig);
            var committedConvars = Path.Combine(root, "artifacts", "e0000006", platform, "convars.json");
            File.WriteAllText(committedConvars, "{ \"convars\": [ { \"name\": \"tampered\" } ] }");

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "e0000006", "--platform", platform, "--verify",
                        "--out", Path.Combine(root, "extract-out") },
                () => runner, resolver);

            // Off-repo --verify regression remains a hard failure.
            Assert.Equal(1, code);
        });
    }

    [WindowsOnlyFact]
    public void Commit_SignatureMismatch_Is_Gated_NoWrite_Exit_1_FixtureSet_Untouched()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("e0000007", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var setDir = Path.Combine(root, "artifacts", "e0000007", platform);
            // Capture the committed set's pre-run state (the fixture "{}" stub) to prove no clobber.
            var before = File.ReadAllText(Path.Combine(setDir, "entity_schema.json"));
            Assert.Equal("{}", before);

            // The fake runner emits a signature that does NOT match the era's expected signature
            // (the gate is armed from the fixture resolver). RunExtract's SECOND GATE rejects
            // it at exit 75 -> NOTHING is staged or promoted.
            var runner = new CountingFakeRunner("hl2sdk-cs2/WRONG/v1/deadbeefdeadbeef", classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000007", "--platform", platform, "--commit" },
                    () => runner, resolver);
                // ExtractCommand classifies exit-75 as Gated (not a walker crash) — but per
                // Summarize's batch exit truth, a Gated build still makes the batch's own exit
                // non-zero (flat 1).
                Assert.Equal(1, code);
            });

            // The gate is HARD: no partial/garbage set was written; the FIXTURE committed
            // set is UNTOUCHED (still the "{}" stub, no full CORE set clobbered in).
            Assert.Equal("{}", File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));
            Assert.False(File.Exists(Path.Combine(setDir, "protos.descriptorset")),
                "an -gated --commit must not clobber the committed set with a partial/garbage set");
            // Surfaced as Gated / NOT promoted in the summary; nothing committed.
            Assert.Contains("Gated", stderr);
            Assert.Contains("NOT promoted", stderr);
            Assert.Contains("committed=0", stderr);
        });
    }

    [WindowsOnlyFact]
    public void Commit_Ignores_Out_And_Writes_To_Artifacts_With_Warning()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("e0000008", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var bogusOut = Path.Combine(root, "should-not-be-used");
            var setDir = Path.Combine(root, "artifacts", "e0000008", platform);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "e0000008", "--platform", platform, "--commit",
                            "--out", bogusOut },
                    () => runner, resolver);
                Assert.Equal(0, code);
            });

            // The documented warning fired and --out was ignored: nothing under the --out path...
            Assert.Contains("--out is ignored with --commit", stderr);
            Assert.False(Directory.Exists(bogusOut), "--commit must ignore --out entirely");
            // ...the set landed in the FIXTURE artifacts/<build>/<platform>/ instead.
            AssertFullCoreSetPresent(setDir);
            Assert.Equal(5, CountClasses(Path.Combine(setDir, "entity_schema.json")));
        });
    }

    // ---- NO-CLOBBER content protection ----------------------------------------
    //
    // A content-less acquire (no pak01_dir.vpk co-located with the binaries) must NEVER clobber a
    // committed set's backfilled content artifacts. RunExtract refuses to promote when the freshly
    // walked set OMITS a content artifact the committed set already carries — fail loud, NO promote,
    // committed set intact. (Neither-side-has-it is fine — a documented omission.)

    [WindowsOnlyFact]
    public void Commit_Refuses_To_Clobber_Committed_Content_Artifact_When_Walk_Omits_It()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("d0000001", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // Seed a REAL committed CORE set, then drop a backfilled content artifact into it (the
            // fixture cache has NO content VPK, so a re-walk produces a CORE-only set that OMITS it).
            SeedCommittedCoreSet(root, "d0000001", platform, resolver, CurrentSig);
            var setDir = Path.Combine(root, "artifacts", "d0000001", platform);
            var committedGameEvents = Path.Combine(setDir, "gameevents.json");
            File.WriteAllText(committedGameEvents,
                "{ \"buildId\": \"d0000001\", \"events\": [ { \"name\": \"player_death\" } ] }");
            var seededEntity = File.ReadAllText(Path.Combine(setDir, "entity_schema.json"));

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", "d0000001", "--platform", platform, "--commit" },
                    () => runner, resolver);
                // RunExtract refuses the content-less clobber (exit 65) -> classified Failed (a HARD
                // failure) -> the single-build --commit run exits non-zero via the summary.
                Assert.NotEqual(0, code);
            });

            // Fail-loud message named the destroyed content artifact.
            Assert.Contains("REFUSING to promote", stderr);
            Assert.Contains("gameevents.json", stderr);
            // the committed set is INTACT — the content artifact AND the prior CORE survive
            // (no delete+move promote ran).
            Assert.True(File.Exists(committedGameEvents),
                "the committed content artifact must NOT be clobbered by a content-less walk");
            Assert.Equal(seededEntity, File.ReadAllText(Path.Combine(setDir, "entity_schema.json")));
        });
    }

    [WindowsOnlyFact]
    public void Commit_Promotes_When_Neither_Side_Has_The_Content_Artifact()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Contrast: when the committed set has NO content artifact either, a content-less re-walk is
        // a clean documented omission — the promote proceeds (the guard only fires on actual loss).
        var committed = new[] { new BuildSpec("d0000002", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedCommittedCoreSet(root, "d0000002", platform, resolver, CurrentSig);
            var setDir = Path.Combine(root, "artifacts", "d0000002", platform);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", "d0000002", "--platform", platform, "--commit" },
                () => runner, resolver);

            Assert.Equal(0, code);
            AssertFullCoreSetPresent(setDir);
        });
    }

    // ---- BUILD-LEVEL PROMOTE HOOK (pics-appinfo.json emit + inventory upsert) -----------
    //
    // The integration coverage for ExtractCommand.PromoteBuildLevel, driven END-TO-END through the
    // public Run(--commit) seam over the fixture-rooted resolver. These exercise the two build-level
    // side effects that ride a successful per-platform promote:
    //   (1) emit artifacts/<build>/pics-appinfo.json IFF a forward-acquisition PICS capture sidecar
    //       (pics-appinfo-capture.json) exists in the build's cache/binaries dir — at BUILD level
    //       (NOT under <platform>/), captured_utc sourced from the PROMOTED provenance's
    // steam.manifest_created_utc (never DateTime.Now), body == the captured verbatim
    //       canonical JSON;
    //   (2) auto-upsert the build into the (fixture) data/cs2-assets-inventory.json.
    //
    // Isolation: FindPicsCapture probes HostConfig.BinariesRoot/<build>/<platform> first, then the
    // cwd-relative cache/binaries/<build>/<platform>; the fixture pins cwd to <root>, so the
    // cwd-relative probe resolves INTO the fixture. The inventory path is repoRoot-derived from the
    // resolver, so it too lands in the fixture. No real Steam, no real artifacts/.

    private const string PromoteManifestUtc = "2026-06-10T22:07:09Z";
    private const string PromoteContentGid = "5146470907583764090";
    private const string PromoteBinaryGid = "8287382081622299196";
    private const string PromoteChangeNumber = "36481865";
    // A uint64-looking manifest GID carried as a STRING inside the opaque canonical body — the whole
    // reason the appinfo body is a string (no float coercion). Asserted to survive verbatim (D).
    private const string PromoteManifestGidInBody = "8287382081622299196";

    private static string PicsCaptureSidecarJson(string appinfoBody) => $$"""
    {
      "appId": 730,
      "changeNumber": "{{PromoteChangeNumber}}",
      "appInfoSha1": "abcd1234",
      "appInfoJson": {{System.Text.Json.JsonSerializer.Serialize(appinfoBody)}}
    }
    """;

    // The verbatim canonical appinfo body the emitter must carry into pics-appinfo.json unchanged,
    // INCLUDING a uint64-looking manifest GID kept as a string (D: no float coercion).
    private static readonly string PromoteAppinfoBody =
        "{\n  \"appinfo\": {\n    \"appid\": \"730\",\n    \"depots\": {\n      \"2347771\": {\n" +
        "        \"manifests\": {\n          \"public\": \"" + PromoteManifestGidInBody + "\"\n" +
        "        }\n      }\n    }\n  }\n}";

    // Seed a manifest-record.json next to the build's binaries (the acquire sidecar ExtractCommand
    // reads to populate the REGENERATED provenance's steam block). This is the realistic seam: the
    // --commit extract OVERWRITES artifacts/<build>/<platform>/provenance.json from the walk + this
    // record, so steam.manifest_created_utc (-> pics-appinfo captured_utc) and the content/
    // binary depot GIDs (-> inventory upsert) must originate here, not in a hand-seeded provenance.
    private static void SeedManifestRecord(string root, string build, string platform)
    {
        var binDir = Path.Combine(root, "cache", "binaries", build, platform);
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "manifest-record.json"), $$"""
        {
          "appId": 730,
          "buildId": {{build}},
          "depots": [
            { "depotId": 2347770, "manifestId": "{{PromoteContentGid}}", "manifestCreatedUtc": "{{PromoteManifestUtc}}" },
            { "depotId": 2347771, "manifestId": "{{PromoteBinaryGid}}", "manifestCreatedUtc": "{{PromoteManifestUtc}}" }
          ]
        }
        """);
    }

    // Write the forward-acquisition PICS capture sidecar into the cwd-relative cache/binaries dir
    // (the SECOND FindPicsCapture candidate; cwd is pinned to <root>).
    private static void SeedPicsCapture(string root, string build, string platform, string appinfoBody)
    {
        var binDir = Path.Combine(root, "cache", "binaries", build, platform);
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "pics-appinfo-capture.json"),
            PicsCaptureSidecarJson(appinfoBody));
    }

    // A fixture inventory with eras[] modeled + depots modeled but NO builds (so a promoted build is
    // forward-captured as new) at <root>/data/cs2-assets-inventory.json.
    private static void SeedFixtureInventory(string root, string platform)
    {
        var dataDir = Path.Combine(root, "data");
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "cs2-assets-inventory.json"), $$"""
        {
          "_meta": { "counts": {} },
          "app": { "app_id": 730, "name": "Counter-Strike 2" },
          "eras": [
            { "era": "cs2-2026-04-21", "kind": "compile-pin", "hl2sdkSha": "{{CurrentSha}}",
              "layoutSignatures": { "{{platform}}": "{{CurrentSig}}" }, "minClasses": 1, "maxClasses": 100000 }
          ],
          "depots": [
            { "depot_id": 2347770, "role": "content", "platforms": ["windows-x86_64","linux-x86_64"], "history": [] },
            { "depot_id": 2347771, "role": "binary",  "platforms": ["windows-x86_64","linux-x86_64"], "history": [] }
          ],
          "builds": []
        }
        """);
    }

    [WindowsOnlyFact]
    public void Commit_WithPicsCapture_Emits_BuildLevel_PicsAppInfo_From_Provenance_Time()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var build = "60000001";
        var committed = new[] { new BuildSpec(build, CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // A capture sidecar present in the build cache + a manifest-record carrying a manifest
            // time (the regenerated provenance's steam.manifest_created_utc -> captured_utc).
            SeedManifestRecord(root, build, platform);
            SeedPicsCapture(root, build, platform, PromoteAppinfoBody);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", build, "--platform", platform, "--commit" },
                () => runner, resolver);
            Assert.Equal(0, code);

            // pics-appinfo.json landed at BUILD level (next to omissions.json), NOT under <platform>/.
            var buildLevel = Path.Combine(root, "artifacts", build, "pics-appinfo.json");
            Assert.True(File.Exists(buildLevel), "promote hook must emit build-level pics-appinfo.json");
            Assert.False(File.Exists(Path.Combine(root, "artifacts", build, platform, "pics-appinfo.json")),
                "pics-appinfo.json is build-level, never under <platform>/");

            // Parses as the public PicsAppInfo proto with the right framing.
            var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
            var pics = parser.Parse<Cs2SchemaTracker.Schemas.PicsAppInfo>(File.ReadAllText(buildLevel));
            Assert.Equal(build, pics.BuildId);
            Assert.Equal(730u, pics.AppId);
            Assert.Equal(PromoteChangeNumber, pics.ChangeNumber);
            // captured_utc is the PROMOTED provenance's manifest time — NOT DateTime.Now.
            Assert.Equal(PromoteManifestUtc, pics.CapturedUtc);
            // body == the captured verbatim canonical JSON (opaque, end-to-end).
            Assert.Equal(PromoteAppinfoBody, pics.AppinfoJson);
            // The uint64-looking GID survives as a string inside the opaque body (no float coercion).
            Assert.Contains($"\"public\": \"{PromoteManifestGidInBody}\"", pics.AppinfoJson);
        });
    }

    [WindowsOnlyFact]
    public void Commit_NoPicsCapture_Skips_PicsAppInfo_NoFail_PromoteSucceeds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var build = "60000002";
        var committed = new[] { new BuildSpec(build, CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // Manifest-record present, but NO capture sidecar (the historical re-walk case).
            SeedManifestRecord(root, build, platform);

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var stderr = CaptureStderr(() =>
            {
                var code = ExtractCommand.Run(
                    new[] { "--build", build, "--platform", platform, "--commit" },
                    () => runner, resolver);
                Assert.Equal(0, code);   // promote still succeeds.
            });

            // No pics-appinfo.json written — its absence is benign (OPTIONAL), no fail-loud.
            Assert.False(File.Exists(Path.Combine(root, "artifacts", build, "pics-appinfo.json")),
                "absent capture => SKIP pics-appinfo.json (never fail-loud)");
            // The core set was still promoted (the skip is silent / non-fatal).
            AssertFullCoreSetPresent(Path.Combine(root, "artifacts", build, platform));
            Assert.DoesNotContain("pics-appinfo emit failed", stderr);
        });
    }

    [WindowsOnlyFact]
    public void Commit_PromoteHook_ForwardCaptures_New_Build_Into_Fixture_Inventory()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var build = "60000003";
        var committed = new[] { new BuildSpec(build, CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            SeedManifestRecord(root, build, platform);
            // Overwrite the fixture inventory with an eras[]-only catalog (NO builds), so the promoted
            // build is genuinely NEW and gets forward-captured (appended) rather than found present.
            SeedFixtureInventory(root, platform);

            var inventoryPath = Path.Combine(root, "data", "cs2-assets-inventory.json");
            var before = File.ReadAllText(inventoryPath);
            Assert.DoesNotContain(build, before);   // build absent pre-promote.

            var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
            var code = ExtractCommand.Run(
                new[] { "--build", build, "--platform", platform, "--commit" },
                () => runner, resolver);
            Assert.Equal(0, code);

            // Forward-capture: the promoted build is APPENDED to the inventory's builds[] with its
            // era + content/binaries (derived from the promoted provenance). change_number/title are
            // not in provenance, so a best-effort forward-capture row carries era + content/binaries.
            var after = File.ReadAllText(inventoryPath);
            Assert.Contains($"\"build_id\": {build}", after);      // builds[] row appended.
            Assert.Contains("\"era\": \"cs2-2026-04-21\"", after); // exact resolved era id.
            Assert.Contains(PromoteContentGid, after);            // content GID.
            Assert.Contains(PromoteBinaryGid, after);             // binaries[platform] GID.
        });
    }

    [WindowsOnlyFact]
    public void Commit_PicsAppInfo_Emit_Is_Deterministic_ByteIdentical()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // emitting pics-appinfo.json twice from the SAME capture + framing is byte-identical.
        // Run the whole --commit promote twice over two independent fixtures and compare the emitted
        // build-level files byte-for-byte (the captured_utc is provenance-sourced, not DateTime.Now).
        string EmitOnce(string build)
        {
            string captured = "";
            var committed = new[] { new BuildSpec(build, CurrentSig) };
            InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
            {
                SeedManifestRecord(root, build, platform);
                SeedPicsCapture(root, build, platform, PromoteAppinfoBody);
                var runner = new EraAwareFakeRunner(SignatureMap(committed), classCount: 5);
                var code = ExtractCommand.Run(
                    new[] { "--build", build, "--platform", platform, "--commit" },
                    () => runner, resolver);
                Assert.Equal(0, code);
                captured = File.ReadAllText(Path.Combine(root, "artifacts", build, "pics-appinfo.json"));
            });
            return captured;
        }

        var a = EmitOnce("60000004");
        var b = EmitOnce("60000005");
        // Differ only in build_id; normalize that away and the rest (incl. the string-bodied GID) is identical.
        Assert.Equal(a.Replace("60000004", "<B>"), b.Replace("60000005", "<B>"));
        // And the uint64-looking GID is carried as a quoted string in BOTH (no float coercion).
        Assert.Contains($"\\\"public\\\": \\\"{PromoteManifestGidInBody}\\\"", a);
    }

    // Run an action with Console.Error redirected to an in-memory writer; returns the captured
    // text. Restores the original Console.Error in a finally (snapshot-restore convention).
    private static string CaptureStderr(Action body)
    {
        var prev = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try
        { body(); }
        finally { Console.SetError(prev); }
        return sw.ToString();
    }

    // Replace a fixture build's "{}" committed stub with a REAL CORE set produced by the same
    // gate-armed extract path a re-walk uses, so a re-walk reproduces it byte-for-byte (CORE-CLEAN
    // baseline). The fixture carries no content VPK, so the emitted set is CORE-only by nature.
    private static void SeedCommittedCoreSet(
        string root, string build, string platform, EraWalkerResolver resolver, string signature)
    {
        var setDir = Path.Combine(root, "artifacts", build, platform);
        try
        { if (Directory.Exists(setDir)) Directory.Delete(setDir, recursive: true); }
        catch { /* best effort */ }
        int code = ExtractCommand.RunExtract(
            build, platform, setDir,
            () => new CountingFakeRunner(signature, classCount: 5),
            resolver, gateFromResolver: true);
        Assert.Equal(0, code);
        Assert.True(File.Exists(Path.Combine(setDir, "entity_schema.json")));
        Assert.True(File.Exists(Path.Combine(setDir, "provenance.json")));
    }

    // ---- SELECTION (via the CommittedBuilds.Discover primitive SelectBuilds delegates to) ----

    [Fact]
    public void Discover_All_Enumerates_Every_Committed_Build_Ordinal_Sorted()
    {
        var platform = MatchingPlatform() ?? "windows-x86_64";
        var committed = new[]
        {
            new BuildSpec("20000003", CurrentSig),
            new BuildSpec("20000001", CurrentSig),
            new BuildSpec("20000002", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var all = CommittedBuilds.Discover(root, platform, resolver);

            Assert.Equal(3, all.Count);
            // Sorted by build id (Ordinal) for a stable run.
            Assert.Equal(ExpectedAllBuilds, all.Select(c => c.Build).ToArray());
            // Each build's era is resolved from its committed provenance pin.
            Assert.Equal("cs2-2026-04-21", all.Single(c => c.Build == "20000001").Era);
            Assert.Equal("cs2-2026-01-22", all.Single(c => c.Build == "20000002").Era);
        });
    }

    [Fact]
    public void Discover_Era_Filter_Selects_Only_That_Eras_Builds()
    {
        var platform = MatchingPlatform() ?? "windows-x86_64";
        var committed = new[]
        {
            new BuildSpec("30000001", CurrentSig),
            new BuildSpec("30000002", Q1Sig),
            new BuildSpec("30000003", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var all = CommittedBuilds.Discover(root, platform, resolver);
            // The --era filter SelectBuilds applies: Era == "cs2-2026-01-22" (formerly q1-2026).
            var q1 = all.Where(c => c.Era == "cs2-2026-01-22").Select(c => c.Build).ToArray();
            Assert.Equal(ExpectedQ1Builds, q1);
        });
    }

    [Fact]
    public void Discover_Pin_Filter_Selects_Only_That_Pins_Builds()
    {
        var platform = MatchingPlatform() ?? "windows-x86_64";
        var committed = new[]
        {
            new BuildSpec("40000001", CurrentSig),
            new BuildSpec("40000002", Q1Sig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var all = CommittedBuilds.Discover(root, platform, resolver);
            // The --pin filter SelectBuilds applies: Pin.StartsWith("b8dcaf14").
            var matched = all.Where(c => c.Pin.StartsWith("b8dcaf14", StringComparison.Ordinal))
                             .Select(c => c.Build).ToArray();
            Assert.Equal(ExpectedPinBuilds, matched);
        });
    }

    // ---- PER-BUILD EXTRACT + ERA-AWARE CLASS GATE (the exact path RunOneBuild delegates to) ---

    // Drive ONE build through the same per-build sequence ExtractCommand.RunOneBuild uses. The
    // class-count gate is now evaluated INSIDE RunExtract itself (exit 77, before any promote — see
    // its "BLOCKING CLASS-BAND GATE" comment), so this helper just maps RunExtract's own exit code:
    // 75/77 -> Gated (a gate rejected the walk; nothing was written), any other non-zero -> Failed,
    // 0 -> Ok. Returns the ExtractCommand.Status the build would be classified.
    private static ExtractCommand.Status RunOneLikeBatch(
        string build, string platform, string outDir, EraWalkerResolver resolver,
        Func<IWalkerRunner> runnerFactory, bool gate)
    {
        int code = ExtractCommand.RunExtract(
            build, platform, outDir, runnerFactory, resolver, gateFromResolver: true,
            classCountGate: gate);
        if (code == 75 || code == 77)
            return ExtractCommand.Status.Gated;
        if (code != 0)
            return ExtractCommand.Status.Failed;
        return ExtractCommand.Status.Ok;
    }

    private static int CountClasses(string entitySchemaPath)
    {
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        var doc = parser.Parse<Cs2SchemaTracker.Schemas.EntitySchema>(File.ReadAllText(entitySchemaPath));
        return doc.Classes.Count;
    }

    [WindowsOnlyFact]
    public void Gate_ClassCount_InsideBand_Is_Ok_And_Promotes_The_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("90000001", CurrentSig) };
        InExtractFixture(platform, (3, 9), WideBand, committed, (root, resolver) =>
        {
            var outDir = Path.Combine(root, "out", "90000001", platform);
            var status = RunOneLikeBatch(
                "90000001", platform, outDir, resolver,
                () => new CountingFakeRunner(CurrentSig, classCount: 5), gate: true);

            Assert.Equal(ExtractCommand.Status.Ok, status);
            Assert.True(File.Exists(Path.Combine(outDir, "entity_schema.json")),
                "an in-band set must be promoted");
        });
    }

    [WindowsOnlyFact]
    public void Gate_ClassCount_OutsideBand_Is_Gated_And_Discards_The_Set()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("90000002", CurrentSig) };
        InExtractFixture(platform, (100, 200), WideBand, committed, (root, resolver) =>
        {
            var outDir = Path.Combine(root, "out", "90000002", platform);
            var status = RunOneLikeBatch(
                "90000002", platform, outDir, resolver,
                () => new CountingFakeRunner(CurrentSig, classCount: 5), gate: true);

            Assert.Equal(ExtractCommand.Status.Gated, status);
            Assert.False(Directory.Exists(outDir), "a gated set must be discarded, not promoted");
        });
    }

    [WindowsOnlyFact]
    public void Gate_NoGate_Promotes_Even_When_OutsideBand()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("90000003", CurrentSig) };
        InExtractFixture(platform, (100, 200), WideBand, committed, (root, resolver) =>
        {
            var outDir = Path.Combine(root, "out", "90000003", platform);
            // gate:false == the --no-gate flag: the out-of-band count is not gated.
            var status = RunOneLikeBatch(
                "90000003", platform, outDir, resolver,
                () => new CountingFakeRunner(CurrentSig, classCount: 5), gate: false);

            Assert.Equal(ExtractCommand.Status.Ok, status);
            Assert.True(File.Exists(Path.Combine(outDir, "entity_schema.json")),
                "--no-gate must promote the set even if the class count is out of band");
        });
    }

    [Fact]
    public void PerBuild_WalkerCrash_Is_Classified_Failed()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("80000002", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var outDir = Path.Combine(root, "out", "80000002", platform);
            var status = RunOneLikeBatch(
                "80000002", platform, outDir, resolver,
                () => new CountingFakeRunner(CurrentSig, 5, exitCode: 70), gate: true);

            Assert.Equal(ExtractCommand.Status.Failed, status);
            Assert.False(File.Exists(Path.Combine(outDir, "entity_schema.json")),
                "a crashed walker produces no set");
        });
    }

    [WindowsOnlyFact]
    public void FailIsolation_OneCrash_Does_Not_Stop_Other_Builds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[]
        {
            new BuildSpec("80000001", CurrentSig),
            new BuildSpec("80000002", CurrentSig),
            new BuildSpec("80000003", CurrentSig),
        };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // Mirror RunOne's per-build loop with isolation: the middle build crashes; the loop must
            // keep going and STILL process the other two (the Summarize exit is non-zero overall).
            var results = new List<ExtractCommand.Status>();
            foreach (var spec in committed.OrderBy(s => s.Build, StringComparer.Ordinal))
            {
                var outDir = Path.Combine(root, "out", spec.Build, platform);
                int exit = spec.Build == "80000002" ? 70 : 0;
                results.Add(RunOneLikeBatch(
                    spec.Build, platform, outDir, resolver,
                    () => new CountingFakeRunner(CurrentSig, 5, exitCode: exit), gate: true));
            }

            // One Failed, two Ok — and the loop visited all three (fail-isolation).
            Assert.Equal(3, results.Count);
            Assert.Equal(1, results.Count(s => s == ExtractCommand.Status.Failed));
            Assert.Equal(2, results.Count(s => s == ExtractCommand.Status.Ok));
            // The two healthy builds produced their sets despite the middle crash.
            Assert.True(File.Exists(Path.Combine(root, "out", "80000001", platform, "entity_schema.json")));
            Assert.True(File.Exists(Path.Combine(root, "out", "80000003", platform, "entity_schema.json")));
            Assert.False(File.Exists(Path.Combine(root, "out", "80000002", platform, "entity_schema.json")));

            // The summary exit (Summarize semantics): any hard failure -> non-zero.
            int hard = results.Count(s => s is ExtractCommand.Status.Failed or ExtractCommand.Status.Regression);
            Assert.True(hard > 0);
        });
    }

    // ---- full extract without a content VPK emits the CORE set + skips content ---------------

    [WindowsOnlyFact]
    public void FullExtract_Without_ContentVpk_Emits_Core_And_Skips_Content()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("a0000001", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var outDir = Path.Combine(root, "out", "a0000001", platform);
            var status = RunOneLikeBatch(
                "a0000001", platform, outDir, resolver,
                () => new CountingFakeRunner(CurrentSig, 5), gate: true);

            Assert.Equal(ExtractCommand.Status.Ok, status);

            foreach (var name in CoreFileNames)
            {
                Assert.True(File.Exists(Path.Combine(outDir, name)), $"extract must emit {name}");
            }
            Assert.True(File.Exists(Path.Combine(outDir, "provenance.json")));
            Assert.True(File.Exists(Path.Combine(outDir, "protos.descriptorset")));

            // No content VPK in the fixture -> the content artifacts are a documented omission.
            foreach (var name in ContentFileNames)
            {
                Assert.False(File.Exists(Path.Combine(outDir, name)),
                    $"a content-less extract must skip the content artifact {name}");
            }
        });
    }

    // ---- --verify classification (the normalized CORE compare ExtractCommand.Verify performs) -

    [WindowsOnlyFact]
    public void Verify_Matching_Core_Is_CoreClean()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("b0000001", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            // Produce a CORE set twice from the identical canned walk; "committed" = run A,
            // "re-extract" = run B. Verify (CORE-CLEAN) compares the normalized CORE SHAs.
            var dirA = Path.Combine(root, "out-a", "b0000001", platform);
            var dirB = Path.Combine(root, "out-b", "b0000001", platform);
            Func<IWalkerRunner> mk = () => new CountingFakeRunner(CurrentSig, 5);
            Assert.Equal(ExtractCommand.Status.Ok,
                RunOneLikeBatch("b0000001", platform, dirA, resolver, mk, gate: true));
            Assert.Equal(ExtractCommand.Status.Ok,
                RunOneLikeBatch("b0000001", platform, dirB, resolver, mk, gate: true));

            Assert.True(CoreClean(dirA, dirB), "identical CORE re-extract must verify CORE-CLEAN");
        });
    }

    [WindowsOnlyFact]
    public void Verify_Mutated_Committed_Core_Is_Regression()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        var committed = new[] { new BuildSpec("b0000002", CurrentSig) };
        InExtractFixture(platform, WideBand, WideBand, committed, (root, resolver) =>
        {
            var dirCommitted = Path.Combine(root, "out-committed", "b0000002", platform);
            var dirReextract = Path.Combine(root, "out-reextract", "b0000002", platform);
            Func<IWalkerRunner> mk = () => new CountingFakeRunner(CurrentSig, 5);
            Assert.Equal(ExtractCommand.Status.Ok,
                RunOneLikeBatch("b0000002", platform, dirCommitted, resolver, mk, gate: true));
            Assert.Equal(ExtractCommand.Status.Ok,
                RunOneLikeBatch("b0000002", platform, dirReextract, resolver, mk, gate: true));

            // Corrupt a committed CORE artifact -> the normalized compare must report a diff.
            File.WriteAllText(Path.Combine(dirCommitted, "convars.json"), "{ \"corrupted\": true }");

            Assert.False(CoreClean(dirCommitted, dirReextract),
                "a mutated committed CORE must verify as REGRESSION");
        });
    }

    // Mirror ExtractCommand.Verify's CORE compare: each CORE JSON via schemaVersion/toolGitSha-
    // normalized SHA, protos.descriptorset raw. True == CORE-CLEAN (no diffs).
    private static readonly string[] VerifyCoreJson =
    {
        "entity_schema.json", "convars.json", "commands.json", "engine_constants.json",
        "modules.json", "string_pools.json", "network_messages.json", "registry_audit.json",
    };

    private static bool CoreClean(string committedDir, string producedDir)
    {
        foreach (var f in VerifyCoreJson)
        {
            var c = Path.Combine(committedDir, f);
            var p = Path.Combine(producedDir, f);
            if (!File.Exists(c) && !File.Exists(p))
                continue;
            if (NormalizedJsonSha(c) != NormalizedJsonSha(p))
                return false;
        }
        return FileSha(Path.Combine(committedDir, "protos.descriptorset"))
            == FileSha(Path.Combine(producedDir, "protos.descriptorset"));
    }

    private static readonly Regex SchemaVersionRegex = new("\"schemaVersion\":\\s*\"[^\"]*\"");
    private static readonly Regex ToolGitShaRegex = new("\"toolGitSha\":\\s*\"[^\"]*\"");

    private static string? NormalizedJsonSha(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        text = SchemaVersionRegex.Replace(text, "\"schemaVersion\": \"<NORM>\"");
        text = ToolGitShaRegex.Replace(text, "\"toolGitSha\": \"<NORM>\"");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string? FileSha(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    // ---- input-binary fixture helpers (mirror ExtractCommandTest) ---------------------------

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
