// formal coverage for the build-to-build changelog artifact.
//
// WHY THIS IS A STANDALONE FIXTURE (and NOT a row in ArtifactCases.All)
// --------------------------------------------------------------------
// Every entry in Invariants/ArtifactFixtures.cs (ArtifactCases.All) emits ONE artifact from
// ONE in-memory walk fixture via a single Action<outputPath> (one walk -> one emit). The
// changelog is structurally different: its input is TWO already-committed (build, platform)
// artifact SETS (a predecessor --from and a newer --to), each a directory of OTHER emitted
// artifacts (entity_schema.json, convars.json, commands.json, engine_constants.json). That does
// not fit the single-emit-fn shape without smuggling two whole produced directories through
// `outputPath` — exactly the reason registry_audit.json is also excluded from that table (see the
// closing comment in ArtifactFixtures.cs). So the changelog's (round-trip),
// (determinism), (fail-loud), and golden-delta content coverage live here, structured the
// same way the cross-artifact invariants suites are (emit a REAL file, parse it back strict,
// re-serialize canonical, assert byte-identical; two emissions diffed byte-for-byte; failure
// modes assert non-zero exit + zero bytes on disk).
//
// No network, no walker, no Steam: the two source sets are hand-built proto3 artifacts written to
// a throwaway temp tree, and the diff runs through the real BuildChangelogEmitter / DiffCommand.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Serialization;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Changelog;

public sealed class ChangelogContractTest
{
    private const string Platform = "linux-x86_64";
    private const string SchemaVersion = "0.4.0";
    private const string FromBuild = "1000";
    private const string ToBuild = "1001";

    // Strict parse (schema-validation primitive): unknown fields are rejected, so a clean
    // parse proves the emitted changelog.json validates against schemas/build_changelog.proto.
    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(false));

    // The canonical proto3 JSON form — identical to what AtomicWrite.WriteCanonical produces:
    // format-default-values on, two-space indent, then CanonicalJson (sorted keys, LF, no BOM).
    private static readonly JsonFormatter CanonicalFormatter = new(
        JsonFormatter.Settings.Default.WithFormatDefaultValues(true).WithIndentation("  "));

    private static readonly string[] ExpectedFamilyOrder =
        { "classes", "enums", "convars", "commands", "engine_constants" };

    // Expected sorted qualified-key lists (CA1861: static readonly, not inline constant array args).
    // Classes carry Module="client", so the qualified key is "client/<name>" (changelog key).
    private static readonly string[] SortedAdded = { "client/C_Bee", "client/C_Newt", "client/C_Yak" };
    private static readonly string[] SortedRemoved = { "client/C_Apple", "client/C_Mango", "client/C_Zebra" };
    private static readonly string[] GoldenClassesAdded = { "client/C_NewThing" };
    private static readonly string[] GoldenClassesRemoved = { "client/C_Removed" };
    private static readonly string[] GoldenEnumMemberFields =
        { "member:MOVETYPE_OLD", "member:MOVETYPE_WALK" };
    private static readonly string[] GoldenCommandsAdded = { "kill" };
    private static readonly string[] FromRemovedClasses = { "C_Zebra", "C_Apple", "C_Mango" };
    private static readonly string[] ToAddedClasses = { "C_Yak", "C_Bee", "C_Newt" };

    // ---- temp-tree scaffolding -------------------------------------------------------------

    /// <summary>A fresh throwaway artifacts root (final segment "artifacts"); cleaned in finally.</summary>
    private static void InRoot(Action<string> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "changelog-tr-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        { body(root); }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    private static string SetDir(string root, string buildId)
    {
        var dir = Path.Combine(root, buildId, Platform);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ChangelogPath(string root, string toBuild)
        => Path.Combine(root, toBuild, Platform, ArtifactSet.ChangelogFileName);

    private static void WriteCanonical(IMessage msg, string path)
        => AtomicWrite.WriteCanonical(msg, path);

    /// <summary>
    /// Write a complete (build,platform) source set: entity_schema.json (classes + enums),
    /// convars.json, commands.json, engine_constants.json — the four files DiffCommand requires.
    /// The caller supplies the message contents per family.
    /// </summary>
    private static void WriteSet(
        string root,
        string buildId,
        Schemas.EntitySchema schema,
        Schemas.ConVars convars,
        Schemas.Commands commands,
        Schemas.EngineConstants constants)
    {
        var dir = SetDir(root, buildId);
        WriteCanonical(schema, Path.Combine(dir, "entity_schema.json"));
        WriteCanonical(convars, Path.Combine(dir, "convars.json"));
        WriteCanonical(commands, Path.Combine(dir, "commands.json"));
        WriteCanonical(constants, Path.Combine(dir, "engine_constants.json"));
    }

    private static Schemas.EntitySchema Schema(string buildId) =>
        new() { SchemaVersion = SchemaVersion, BuildId = buildId, Platform = Platform };

    private static Schemas.ConVars Convars(string buildId) =>
        new() { SchemaVersion = SchemaVersion, BuildId = buildId, Platform = Platform };

    private static Schemas.Commands Commands(string buildId) =>
        new() { SchemaVersion = SchemaVersion, BuildId = buildId, Platform = Platform };

    private static Schemas.EngineConstants Constants(string buildId) =>
        new() { SchemaVersion = SchemaVersion, BuildId = buildId, Platform = Platform };

    /// <summary>
    /// Emit a changelog.json for the (FromBuild -> ToBuild) pair under <paramref name="root"/> and
    /// return its path. Uses the real BuildChangelogEmitter (the production diff engine).
    /// </summary>
    private static string EmitChangelog(string root)
    {
        var outPath = ChangelogPath(root, ToBuild);
        new BuildChangelogEmitter(SchemaVersion, Platform, FromBuild, ToBuild)
            .Emit(Path.Combine(root, FromBuild, Platform), Path.Combine(root, ToBuild, Platform), outPath);
        return outPath;
    }

    /// <summary>
    /// Build a representative two-build pair into <paramref name="root"/>: a class field_count
    /// change, an added class, a removed class, an added/removed enum member, a convar default
    /// change, and a command added — so every leg of every suite has real deltas to chew on.
    /// </summary>
    private static void WriteRepresentativePair(string root)
    {
        // --- FROM side ---
        var fromSchema = Schema(FromBuild);
        var baseEntity = new Schemas.SchemaClass { Name = "C_BaseEntity", Module = "client" };
        baseEntity.Fields.Add(new Schemas.SchemaField { Name = "m_iHealth" });
        fromSchema.Classes.Add(baseEntity);
        fromSchema.Classes.Add(new Schemas.SchemaClass { Name = "C_Removed", Module = "client" });
        var moveType = new Schemas.SchemaEnum { Name = "MoveType_t", Module = "server" };
        moveType.Members.Add(new Schemas.SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        moveType.Members.Add(new Schemas.SchemaEnumMember { Name = "MOVETYPE_OLD", Value = 9 });
        fromSchema.Enums.Add(moveType);

        var fromConvars = Convars(FromBuild);
        fromConvars.Convars.Add(new Schemas.ConVar { Name = "sv_cheats", Default = "0" });

        var fromCommands = Commands(FromBuild);

        // --- TO side ---
        var toSchema = Schema(ToBuild);
        var baseEntity2 = new Schemas.SchemaClass { Name = "C_BaseEntity", Module = "client" };
        baseEntity2.Fields.Add(new Schemas.SchemaField { Name = "m_iHealth" });
        baseEntity2.Fields.Add(new Schemas.SchemaField { Name = "m_iMaxHealth" });   // +1 field
        toSchema.Classes.Add(baseEntity2);
        toSchema.Classes.Add(new Schemas.SchemaClass { Name = "C_NewThing", Module = "client" }); // added
        var moveType2 = new Schemas.SchemaEnum { Name = "MoveType_t", Module = "server" };
        moveType2.Members.Add(new Schemas.SchemaEnumMember { Name = "MOVETYPE_NONE", Value = 0 });
        moveType2.Members.Add(new Schemas.SchemaEnumMember { Name = "MOVETYPE_WALK", Value = 2 }); // member added
        toSchema.Enums.Add(moveType2);

        var toConvars = Convars(ToBuild);
        toConvars.Convars.Add(new Schemas.ConVar { Name = "sv_cheats", Default = "1" });           // default change

        var toCommands = Commands(ToBuild);
        toCommands.Commands_.Add(new Schemas.Command { Name = "kill" });                            // command added

        WriteSet(root, FromBuild, fromSchema, fromConvars, fromCommands, Constants(FromBuild));
        WriteSet(root, ToBuild, toSchema, toConvars, toCommands, Constants(ToBuild));
    }

    private static Schemas.FamilyDelta Family(Schemas.BuildChangelog cl, string name)
        => cl.Families.Single(f => f.Family == name);

    // ----: schema validation + byte-identical round-trip -------------------------------

    [Fact]
    public void Changelog_Validates_And_RoundTrips_Byte_Identical()
    {
        InRoot(root =>
        {
            WriteRepresentativePair(root);
            var path = EmitChangelog(root);
            Assert.True(File.Exists(path), "diff engine produced no changelog.json");

            var emittedBytes = File.ReadAllBytes(path);
            var emittedJson = System.Text.Encoding.UTF8.GetString(emittedBytes);

            // (1) strict parse == schema-valid against build_changelog.proto.
            var message = StrictParser.Parse<Schemas.BuildChangelog>(emittedJson);
            Assert.NotNull(message);

            // (2) re-serialize canonical and assert byte-identical to the emitted file.
            var reserialized = CanonicalJson.SerializeRawJson(CanonicalFormatter.Format(message));
            var reserializedBytes = System.Text.Encoding.UTF8.GetBytes(reserialized);
            Assert.Equal(emittedBytes, reserializedBytes);
        });
    }

    // ----: determinism (byte-identical across two runs) + stable ordering ---------------

    [Fact]
    public void Changelog_Is_Byte_Identical_Across_Two_Runs()
    {
        InRoot(root =>
        {
            WriteRepresentativePair(root);

            // Two independent emissions of the same two committed sets into separate output files —
            // the in-process equivalent of `diff -r` over two extraction runs.
            var emitter = new BuildChangelogEmitter(SchemaVersion, Platform, FromBuild, ToBuild);
            var fromDir = Path.Combine(root, FromBuild, Platform);
            var toDir = Path.Combine(root, ToBuild, Platform);

            var outA = Path.Combine(root, "run-a.json");
            var outB = Path.Combine(root, "run-b.json");
            emitter.Emit(fromDir, toDir, outA);
            emitter.Emit(fromDir, toDir, outB);

            Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
        });
    }

    [Fact]
    public void Changelog_Has_No_Bom_And_No_Cr()
    {
        InRoot(root =>
        {
            WriteRepresentativePair(root);
            var bytes = File.ReadAllBytes(EmitChangelog(root));

            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "changelog.json must not carry a UTF-8 BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
        });
    }

    [Fact]
    public void Changelog_Names_Are_Ordinal_Sorted_Regardless_Of_Input_Order()
    {
        // Construct inputs whose added/removed names are inserted in NON-sorted order (and whose
        // hash/insertion order would otherwise leak through) and verify the emitted lists are
        // strictly Ordinal-sorted — the determinism leg the locked design pins.
        InRoot(root =>
        {
            var fromSchema = Schema(FromBuild);
            // Removed classes inserted in reverse-sorted order.
            foreach (var n in FromRemovedClasses)
            {
                fromSchema.Classes.Add(new Schemas.SchemaClass { Name = n, Module = "client" });
            }

            var toSchema = Schema(ToBuild);
            // Added classes inserted in reverse-sorted order.
            foreach (var n in ToAddedClasses)
            {
                toSchema.Classes.Add(new Schemas.SchemaClass { Name = n, Module = "client" });
            }

            WriteSet(root, FromBuild, fromSchema, Convars(FromBuild), Commands(FromBuild), Constants(FromBuild));
            WriteSet(root, ToBuild, toSchema, Convars(ToBuild), Commands(ToBuild), Constants(ToBuild));

            var cl = StrictParser.Parse<Schemas.BuildChangelog>(File.ReadAllText(EmitChangelog(root)));
            var classes = Family(cl, "classes");

            Assert.Equal(SortedAdded, classes.Added.ToArray());
            Assert.Equal(SortedRemoved, classes.Removed.ToArray());
        });
    }

    // ----: fail-loud — non-zero exit + ZERO bytes on disk ------------------------------

    [Fact]
    public void Missing_From_Set_Dir_FailsLoud_NoChangelog()
    {
        InRoot(root =>
        {
            // Only the --to side exists; --from is absent entirely.
            WriteSet(root, ToBuild, Schema(ToBuild), Convars(ToBuild), Commands(ToBuild), Constants(ToBuild));

            var code = DiffCommand.Run(
                new[] { "--from", FromBuild, "--to", ToBuild, "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);

            Assert.NotEqual(0, code);
            AssertNoChangelog(root);
        });
    }

    [Fact]
    public void Missing_To_Set_Dir_FailsLoud_NoChangelog()
    {
        InRoot(root =>
        {
            // Only the --from side exists; --to is absent entirely.
            WriteSet(root, FromBuild, Schema(FromBuild), Convars(FromBuild), Commands(FromBuild), Constants(FromBuild));

            var code = DiffCommand.Run(
                new[] { "--from", FromBuild, "--to", ToBuild, "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);

            Assert.NotEqual(0, code);
            AssertNoChangelog(root);
        });
    }

    [Fact]
    public void Missing_Required_Family_Source_Unaccounted_FailsLoud_NoChangelog()
    {
        InRoot(root =>
        {
            WriteSet(root, FromBuild, Schema(FromBuild), Convars(FromBuild), Commands(FromBuild), Constants(FromBuild));
            WriteSet(root, ToBuild, Schema(ToBuild), Convars(ToBuild), Commands(ToBuild), Constants(ToBuild));

            // Delete a required family source file from the --to side with NO omissions accounting.
            File.Delete(Path.Combine(root, ToBuild, Platform, "convars.json"));

            var code = DiffCommand.Run(
                new[] { "--from", FromBuild, "--to", ToBuild, "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);

            Assert.Equal(65, code);   // EX_DATAERR — unaccounted-for missing input.
            AssertNoChangelog(root);
        });
    }

    private static void AssertNoChangelog(string root)
    {
        var path = ChangelogPath(root, ToBuild);
        Assert.False(File.Exists(path), $"no changelog bytes on a fail-loud path: {path}");
        Assert.False(File.Exists(path + ".tmp"), $"no leftover temp file: {path}.tmp");
    }

    // ---- Golden-output content: exact emitted delta across several families ------------------

    [Fact]
    public void Golden_Delta_Has_Exact_Added_Removed_Changed_Across_Families()
    {
        InRoot(root =>
        {
            WriteRepresentativePair(root);
            var cl = StrictParser.Parse<Schemas.BuildChangelog>(File.ReadAllText(EmitChangelog(root)));

            // Envelope.
            Assert.Equal(FromBuild, cl.FromBuild);
            Assert.Equal(ToBuild, cl.ToBuild);
            Assert.Equal(Platform, cl.Platform);
            Assert.Equal(SchemaVersion, cl.SchemaVersion);

            // All five families, fixed declared order — even the empty engine_constants.
            Assert.Equal(ExpectedFamilyOrder, cl.Families.Select(f => f.Family).ToArray());

            // classes: one added, one removed, one changed (field_count 1 -> 2).
            var classes = Family(cl, "classes");
            Assert.Equal(GoldenClassesAdded, classes.Added.ToArray());
            Assert.Equal(GoldenClassesRemoved, classes.Removed.ToArray());
            var changedClass = Assert.Single(classes.Changed);
            Assert.Equal("client/C_BaseEntity", changedClass.Name);   // qualified "<module>/<name>" key.
            var fieldCount = Assert.Single(changedClass.Fields);
            Assert.Equal("field_count", fieldCount.Field);
            Assert.Equal("1", fieldCount.OldValue);
            Assert.Equal("2", fieldCount.NewValue);

            // enums: MOVETYPE_OLD removed (member:... old=9 new=""), MOVETYPE_WALK added
            // (member:... old="" new=2). MOVETYPE_NONE unchanged on both sides -> no row. The
            // two member rows are Ordinal-sorted by `field` ("member:MOVETYPE_OLD" < "member:MOVETYPE_WALK").
            var enums = Family(cl, "enums");
            Assert.Empty(enums.Added);
            Assert.Empty(enums.Removed);
            var changedEnum = Assert.Single(enums.Changed);
            Assert.Equal("server/MoveType_t", changedEnum.Name);   // qualified "<module>/<name>" key.
            Assert.Equal(
                GoldenEnumMemberFields,
                changedEnum.Fields.Select(f => f.Field).ToArray());
            var oldMember = changedEnum.Fields.Single(f => f.Field == "member:MOVETYPE_OLD");
            Assert.Equal("9", oldMember.OldValue);
            Assert.Equal("", oldMember.NewValue);
            var newMember = changedEnum.Fields.Single(f => f.Field == "member:MOVETYPE_WALK");
            Assert.Equal("", newMember.OldValue);
            Assert.Equal("2", newMember.NewValue);

            // convars: sv_cheats default 0 -> 1 rendered as FieldChange{field:"default"}.
            var convars = Family(cl, "convars");
            Assert.Empty(convars.Added);
            Assert.Empty(convars.Removed);
            var changedConvar = Assert.Single(convars.Changed);
            Assert.Equal("sv_cheats", changedConvar.Name);
            var defaultChange = Assert.Single(changedConvar.Fields);
            Assert.Equal("default", defaultChange.Field);
            Assert.Equal("0", defaultChange.OldValue);
            Assert.Equal("1", defaultChange.NewValue);

            // commands: kill added; nothing removed/changed.
            var commands = Family(cl, "commands");
            Assert.Equal(GoldenCommandsAdded, commands.Added.ToArray());
            Assert.Empty(commands.Removed);
            Assert.Empty(commands.Changed);

            // engine_constants: empty family still emitted, fully empty.
            var consts = Family(cl, "engine_constants");
            Assert.Empty(consts.Added);
            Assert.Empty(consts.Removed);
            Assert.Empty(consts.Changed);
        });
    }
}
