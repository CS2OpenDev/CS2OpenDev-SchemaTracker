// Contract coverage for the cumulative schema-evolution artifact (schema_evolution.json).
//
// Structured like ChangelogContractTest: hand-built proto3 entity_schema.json snapshots written to a
// throwaway temp artifacts/ tree, diffed through the REAL SchemaEvolutionEmitter / EvolutionCommand.
// No walker, no Steam, no network. Covers:
//   - golden delta: a crafted 3-build chain exercising every op family, asserted structurally;
//   - determinism: two full builds are byte-identical;
//   - incremental == full: appending the newest transition to the prior artifact matches a full walk
//     byte-for-byte (the load-bearing new contract);
//   - round-trip: the written file strict-parses (schema-valid) and re-serializes canonical identically;
//   - fail-loud: a missing snapshot in the walk throws before any bytes are written.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class SchemaEvolutionContractTest
{
    private const string Platform = "linux-x86_64";
    private const string SchemaVersion = "0.5.0";
    private const string B1 = "1000";
    private const string B2 = "1001";
    private const string B3 = "1002";

    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(false));

    // CA1861: expected arrays / repeatedly-passed chains hoisted to static readonly fields.
    private static readonly string[] CBaseParent = { "CBase" };
    private static readonly string[] ChainAll = { B1, B2, B3 };
    private static readonly string[] ChainB1B2 = { B1, B2 };
    private static readonly string[] ExpectedAddedB1B2 = { "client/CBar", "client/CBase" };
    private static readonly string[] ExpectedReparentTo = { "client/CBase" };
    private static readonly string[] ExpectedRemovedB2B3 = { "client/CBar" };
    private static readonly string[] ExpectedPairSignals = { "offsetExact", "typeMatch" };

    // ---- fixture builders ------------------------------------------------------------------

    private static SchemaType Builtin(string name) =>
        new() { Category = SchemaType.Types.Category.Builtin, Name = name };

    private static SchemaField Field(string name, long offset, SchemaType type) =>
        new() { Name = name, Offset = offset, Type = type };

    private static SchemaClass Class(string module, string name, ulong size, string[] parents, params SchemaField[] fields)
    {
        var c = new SchemaClass { Module = module, Name = name, Size = size };
        foreach (var p in parents)
            c.Parents.Add(new SchemaClassParent { Module = "client", Name = p });
        c.Fields.AddRange(fields);
        return c;
    }

    private static SchemaEnum Enum(string module, string name, params (string Name, long Value)[] members)
    {
        var e = new SchemaEnum { Module = module, Name = name, Alignment = "uint32_t", Size = 4 };
        foreach (var m in members)
            e.Members.Add(new SchemaEnumMember { Name = m.Name, Value = m.Value });
        return e;
    }

    private static Schemas.EntitySchema Snapshot(string build, SchemaClass[] classes, SchemaEnum[] enums)
    {
        var s = new Schemas.EntitySchema { SchemaVersion = SchemaVersion, BuildId = build, Platform = Platform };
        s.Classes.AddRange(classes);
        s.Enums.AddRange(enums);
        return s;
    }

    // The crafted 3-build chain (see the file header for the op coverage it drives).
    private static Schemas.EntitySchema SnapB1() => Snapshot(B1,
        new[]
        {
            Class("client", "CFoo", 24, Array.Empty<string>(),
                Field("m_a", 0, Builtin("int32")),
                Field("m_b", 8, Builtin("float32")),
                Field("m_x", 16, Builtin("bool"))),
        },
        new[] { Enum("client", "EFoo", ("A", 0), ("B", 1)) });

    private static Schemas.EntitySchema SnapB2() => Snapshot(B2,
        new[]
        {
            Class("client", "CBase", 8, Array.Empty<string>()),
            Class("client", "CBar", 8, Array.Empty<string>()),
            Class("client", "CFoo", 32, CBaseParent,   // reparent [] -> [CBase] + resize 24 -> 32
                Field("m_a", 4, Builtin("int32")),           // offset 0 -> 4
                Field("m_b", 8, Builtin("int64")),           // type float32 -> int64
                Field("m_x", 16, Builtin("bool")),
                Field("m_c", 24, Builtin("bool"))),          // add field
        },
        new[] { Enum("client", "EFoo", ("A", 0), ("B", 1), ("C", 2)) });   // add member C

    private static Schemas.EntitySchema SnapB3() => Snapshot(B3,
        new[]
        {
            Class("client", "CBase", 8, Array.Empty<string>()),          // CBar removed
            Class("client", "CFoo", 32, CBaseParent,
                Field("m_a", 4, Builtin("int32")),
                Field("m_b", 8, Builtin("int64")),
                Field("m_c", 24, Builtin("bool")),
                Field("m_y", 16, Builtin("bool"))),                       // m_x removed, m_y added @16 bool
        },
        new[] { Enum("client", "EFoo", ("A", 0), ("B", 1), ("C", 2)) });

    // ---- temp-tree scaffolding -------------------------------------------------------------

    private static void InRoot(Action<string> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "evo-tr-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        { body(root); }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    private static void WriteSnapshot(string root, Schemas.EntitySchema snapshot)
    {
        var dir = Path.Combine(root, snapshot.BuildId, Platform);
        Directory.CreateDirectory(dir);
        AtomicWrite.WriteCanonical(snapshot, Path.Combine(dir, "entity_schema.json"));
        // Every committed set carries provenance; the emitter joins steam.manifest_created_utc
        // from it for the transition calendar axis. Deterministic per-build fixture time.
        var provenance = new Schemas.Provenance
        {
            Steam = new Schemas.SteamIdentity
            { ManifestCreatedUtc = $"2026-01-01T00:00:{snapshot.BuildId[^1]}0Z" },
        };
        AtomicWrite.WriteCanonical(provenance, Path.Combine(dir, "provenance.json"));
    }

    private static SchemaEvolutionEmitter Emitter() => new(SchemaVersion, Platform);

    // ---- golden delta ----------------------------------------------------------------------

    [Fact]
    public void Golden_delta_over_the_crafted_chain()
    {
        InRoot(root =>
        {
            WriteSnapshot(root, SnapB1());
            WriteSnapshot(root, SnapB2());
            WriteSnapshot(root, SnapB3());

            var evo = Emitter().BuildFull(root, ChainAll);

            Assert.Equal(Platform, evo.Platform);
            Assert.Equal(B1, evo.BaselineBuild);
            Assert.Equal(B3, evo.LatestBuild);
            Assert.Equal(2, evo.Transitions.Count);

            // Transition B1 -> B2.
            var t0 = evo.Transitions[0];
            Assert.Equal(ExpectedAddedB1B2, t0.ClassAdded);
            Assert.Empty(t0.ClassRemoved);
            var foo0 = Assert.Single(t0.ClassChanged, c => c.Name == "client/CFoo");

            // field_ops Ordinal by (field, kind): m_a OFFSET, m_b TYPE, m_c ADD.
            Assert.Collection(foo0.FieldOps,
                op => { Assert.Equal(FieldOp.Types.Kind.OffsetChange, op.Kind); Assert.Equal("m_a", op.Field); Assert.Equal("0", op.FromOffset); Assert.Equal("4", op.ToOffset); },
                op =>
                {
                    Assert.Equal(FieldOp.Types.Kind.TypeChange, op.Kind);
                    Assert.Equal("m_b", op.Field);
                    Assert.Equal("B:float32", SchemaTypeRenderer.Render(op.FromType));
                    Assert.Equal("B:int64", SchemaTypeRenderer.Render(op.ToType));
                    Assert.Equal(4ul, op.FromWidth.Bytes);   // float32 = 4 bytes (provable)
                    Assert.Equal(8ul, op.ToWidth.Bytes);     // int64   = 8 bytes
                },
                op => { Assert.Equal(FieldOp.Types.Kind.Add, op.Kind); Assert.Equal("m_c", op.Field); });

            Assert.NotNull(foo0.Reparent);
            Assert.Empty(foo0.Reparent.From);
            Assert.Equal(ExpectedReparentTo, foo0.Reparent.To);
            Assert.NotNull(foo0.Resize);
            Assert.Equal("24", foo0.Resize.From);
            Assert.Equal("32", foo0.Resize.To);

            var efoo0 = Assert.Single(t0.EnumChanged, e => e.Name == "client/EFoo");
            var addC = Assert.Single(efoo0.MemberOps);
            Assert.Equal(EnumMemberOp.Types.Kind.AddMember, addC.Kind);
            Assert.Equal("C", addC.Member);
            Assert.Equal("2", addC.ToValue);

            // Transition B2 -> B3: CBar removed; m_x/m_y remove+add produce paired evidence.
            var t1 = evo.Transitions[1];
            Assert.Equal(ExpectedRemovedB2B3, t1.ClassRemoved);
            var foo1 = Assert.Single(t1.ClassChanged, c => c.Name == "client/CFoo");
            Assert.Contains(foo1.FieldOps, op => op.Kind == FieldOp.Types.Kind.Remove && op.Field == "m_x");
            Assert.Contains(foo1.FieldOps, op => op.Kind == FieldOp.Types.Kind.Add && op.Field == "m_y");
            var pair = Assert.Single(foo1.PairedEvidence);
            Assert.Equal("m_x", pair.From);
            Assert.Equal("m_y", pair.To);
            Assert.Equal(ExpectedPairSignals, pair.Signals);

            // field_history facts.
            var mb = Single(evo.FieldHistory, "client/CFoo", "m_b");
            Assert.Equal(B1, mb.FirstSeenBuild);
            Assert.Equal(B3, mb.LastSeenBuild);
            Assert.Collection(mb.TypeHistory,
                ta => { Assert.Equal(B1, ta.Build); Assert.Equal("B:float32", SchemaTypeRenderer.Render(ta.Type)); },
                ta => { Assert.Equal(B2, ta.Build); Assert.Equal("B:int64", SchemaTypeRenderer.Render(ta.Type)); });

            var mx = Single(evo.FieldHistory, "client/CFoo", "m_x");
            Assert.Equal(B1, mx.FirstSeenBuild);
            Assert.Equal(B2, mx.LastSeenBuild);   // removed at B3 -> last present is B2

            var my = Single(evo.FieldHistory, "client/CFoo", "m_y");
            Assert.Equal(B3, my.FirstSeenBuild);
            Assert.Equal(B3, my.LastSeenBuild);
        });
    }

    private static FieldHistory Single(IEnumerable<FieldHistory> all, string className, string field)
        => Assert.Single(all, f => f.ClassName == className && f.Field == field);

    // ---- determinism + incremental == full -------------------------------------------------

    [Fact]
    public void Two_full_builds_are_byte_identical()
    {
        InRoot(root =>
        {
            WriteSnapshot(root, SnapB1());
            WriteSnapshot(root, SnapB2());
            WriteSnapshot(root, SnapB3());

            var a = AtomicWrite.SerializeCanonical(Emitter().BuildFull(root, ChainAll));
            var b = AtomicWrite.SerializeCanonical(Emitter().BuildFull(root, ChainAll));
            Assert.Equal(a, b);
        });
    }

    [Fact]
    public void Incremental_refresh_equals_full_backfill()
    {
        InRoot(root =>
        {
            WriteSnapshot(root, SnapB1());
            WriteSnapshot(root, SnapB2());
            WriteSnapshot(root, SnapB3());

            var full = AtomicWrite.SerializeCanonical(Emitter().BuildFull(root, ChainAll));

            // Prior cumulative as of B2, then append B2 -> B3.
            var prior = Emitter().BuildFull(root, ChainB1B2);
            var incremental = AtomicWrite.SerializeCanonical(
                Emitter().BuildIncremental(root, prior, SnapB2(), B2, SnapB3(), B3));

            Assert.Equal(full, incremental);
        });
    }

    // ---- round-trip + fail-loud ------------------------------------------------------------

    [Fact]
    public void Command_writes_a_strict_parseable_canonical_artifact()
    {
        InRoot(root =>
        {
            WriteSnapshot(root, SnapB1());
            WriteSnapshot(root, SnapB2());
            WriteSnapshot(root, SnapB3());

            var exit = EvolutionCommand.Run(
                new[] { "--platform", Platform, "--artifacts", root, "--full" }, artifactsRootOverride: root);
            Assert.Equal(0, exit);

            var path = Path.Combine(root, "schema_evolution", Platform + ".json");   // fixed per-platform path
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            var bytes = File.ReadAllText(path);
            var parsed = StrictParser.Parse<SchemaEvolution>(bytes);          // strict => schema-valid
            Assert.Equal(AtomicWrite.SerializeCanonical(parsed), bytes);       // canonical fixpoint
            Assert.Equal(B3, parsed.LatestBuild);
        });
    }

    [Fact]
    public void Fail_loud_on_missing_snapshot_writes_nothing()
    {
        InRoot(root =>
        {
            WriteSnapshot(root, SnapB1());
            WriteSnapshot(root, SnapB3());   // B2 dir exists via nothing — create an empty B2 platform dir
            Directory.CreateDirectory(Path.Combine(root, B2, Platform));   // present but no entity_schema.json

            // BuildFull must throw on the missing B2 snapshot, before any output.
            Assert.ThrowsAny<Exception>(() => Emitter().BuildFull(root, ChainAll));

            // And the command surfaces it as a non-zero exit with no artifact written.
            var exit = EvolutionCommand.Run(
                new[] { "--platform", Platform, "--artifacts", root, "--full" }, artifactsRootOverride: root);
            Assert.NotEqual(0, exit);
            Assert.False(File.Exists(Path.Combine(root, "schema_evolution", Platform + ".json")));
        });
    }
}
