// 0.7.0 coverage (issue #7 items 3+6): the class/enum attribute deltas the diff previously
// skipped (static_field_ops, flags2, class metadata, cpp_name, project_name, inheritance depths,
// enum project_name) and the provenance-joined transition calendar axis.

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class AttributeCoverageTest
{
    private const string Platform = "linux-x86_64";
    private const string B1 = "1000";
    private const string B2 = "1001";
    private const string B3 = "1002";

    private static SchemaType Builtin(string name) =>
        new() { Category = SchemaType.Types.Category.Builtin, Name = name };

    private static SchemaField Field(string name, long offset, SchemaType type) =>
        new() { Name = name, Offset = offset, Type = type };

    private static Schemas.EntitySchema Snapshot(string build, params SchemaClass[] classes)
    {
        var s = new Schemas.EntitySchema
        { SchemaVersion = SchemaFamily.Version, BuildId = build, Platform = Platform };
        s.Classes.AddRange(classes);
        return s;
    }

    private static Transition Diff(Schemas.EntitySchema from, Schemas.EntitySchema to) =>
        SchemaSnapshotDiff.Diff(from, to, B1, B2);

    // ---- item 3: the previously-silent class attributes --------------------------------------

    [Fact]
    public void Static_field_changes_are_diffed_with_the_full_op_vocabulary()
    {
        var oldC = new SchemaClass { Module = "client", Name = "CFoo", Size = 8 };
        oldC.StaticFields.Add(Field("s_kept", 0, Builtin("int32")));
        oldC.StaticFields.Add(Field("s_gone", 8, Builtin("bool")));
        var newC = new SchemaClass { Module = "client", Name = "CFoo", Size = 8 };
        newC.StaticFields.Add(Field("s_kept", 16, Builtin("int32")));   // offset moved
        newC.StaticFields.Add(Field("s_new", 8, Builtin("bool")));      // s_gone -> s_new shape

        var delta = Assert.Single(Diff(Snapshot(B1, oldC), Snapshot(B2, newC)).ClassChanged);

        Assert.Empty(delta.FieldOps); // instance fields untouched
        Assert.Collection(delta.StaticFieldOps,
            op => { Assert.Equal(FieldOp.Types.Kind.Remove, op.Kind); Assert.Equal("s_gone", op.Field); },
            op => { Assert.Equal(FieldOp.Types.Kind.OffsetChange, op.Kind); Assert.Equal("s_kept", op.Field); },
            op => { Assert.Equal(FieldOp.Types.Kind.Add, op.Kind); Assert.Equal("s_new", op.Field); });
        // Statics deliberately join no pairing surface, even when a remove/add pair would qualify.
        Assert.Empty(delta.PairCandidates);
        Assert.Empty(delta.PairedEvidence);
    }

    [Fact]
    public void Flags2_metadata_names_and_depths_all_surface_as_scalar_changes()
    {
        var oldC = new SchemaClass
        {
            Module = "client",
            Name = "CFoo",
            Size = 8,
            Flags2 = 0,
            CppName = "CFoo",
            ProjectName = "client",
            SingleInheritanceDepth = 1,
            MultipleInheritanceDepth = 0,
        };
        oldC.Metadata.Add(new SchemaMetadata { Name = "MTag", Value = "old" });
        var newC = new SchemaClass
        {
            Module = "client",
            Name = "CFoo",
            Size = 8,
            Flags2 = 4,
            CppName = "CFoo2",
            ProjectName = "particles",
            SingleInheritanceDepth = 2,
            MultipleInheritanceDepth = 1,
        };
        newC.Metadata.Add(new SchemaMetadata { Name = "MTag", Value = "new" });

        var delta = Assert.Single(Diff(Snapshot(B1, oldC), Snapshot(B2, newC)).ClassChanged);

        Assert.Equal(("0", "4"), (delta.Flags2.From, delta.Flags2.To));
        var metaOp = Assert.Single(delta.MetaOps);
        Assert.Equal(MetaEntryOp.Types.Kind.ChangeValue, metaOp.Kind);
        Assert.Equal("MTag", metaOp.Name);
        Assert.Equal(("old", "new"), (metaOp.From.Value, metaOp.To.Value));
        Assert.Equal(("CFoo", "CFoo2"), (delta.CppName.From, delta.CppName.To));
        Assert.Equal(("client", "particles"), (delta.ProjectName.From, delta.ProjectName.To));
        Assert.Equal(("1", "2"), (delta.SingleInheritanceDepth.From, delta.SingleInheritanceDepth.To));
        Assert.Equal(("0", "1"), (delta.MultipleInheritanceDepth.From, delta.MultipleInheritanceDepth.To));
    }

    [Fact]
    public void An_unchanged_class_still_produces_no_delta()
    {
        var make = () =>
        {
            var c = new SchemaClass
            {
                Module = "client",
                Name = "CFoo",
                Size = 8,
                Flags2 = 4,
                CppName = "CFoo",
                ProjectName = "client",
                SingleInheritanceDepth = 1,
                MultipleInheritanceDepth = 0,
            };
            c.Metadata.Add(new SchemaMetadata { Name = "MTag", Value = "v" });
            c.StaticFields.Add(Field("s_a", 0, Builtin("int32")));
            return c;
        };

        Assert.Empty(Diff(Snapshot(B1, make()), Snapshot(B2, make())).ClassChanged);
    }

    [Fact]
    public void Enum_project_name_changes_surface()
    {
        var oldE = new SchemaEnum
        { Module = "client", Name = "EFoo", Alignment = "uint32_t", Size = 4, ProjectName = "client" };
        var newE = new SchemaEnum
        { Module = "client", Name = "EFoo", Alignment = "uint32_t", Size = 4, ProjectName = "particles" };
        var from = Snapshot(B1);
        from.Enums.Add(oldE);
        var to = Snapshot(B2);
        to.Enums.Add(newE);

        var delta = Assert.Single(Diff(from, to).EnumChanged);
        Assert.Equal(("client", "particles"), (delta.ProjectName.From, delta.ProjectName.To));
    }

    // ---- item 6: the calendar axis -----------------------------------------------------------

    [Fact]
    public void Transitions_carry_the_two_builds_manifest_times_from_provenance()
    {
        var work = Path.Combine(Path.GetTempPath(), "evo-cal-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        {
            WriteSnapshot(root, Snapshot(B1, new SchemaClass { Module = "client", Name = "CFoo", Size = 8 }));
            WriteSnapshot(root, Snapshot(B2, new SchemaClass { Module = "client", Name = "CFoo", Size = 16 }));
            WriteSnapshot(root, Snapshot(B3, new SchemaClass { Module = "client", Name = "CFoo", Size = 24 }));

            var evo = new SchemaEvolutionEmitter(SchemaFamily.Version, Platform)
                .BuildFull(root, [B1, B2, B3]);

            Assert.Collection(evo.Transitions,
                t =>
                {
                    Assert.Equal("2026-01-01T00:00:00Z", t.FromManifestCreatedUtc);
                    Assert.Equal("2026-01-01T00:00:10Z", t.ToManifestCreatedUtc);
                },
                t =>
                {
                    // Consecutive transitions overlap by design: to of N == from of N+1.
                    Assert.Equal("2026-01-01T00:00:10Z", t.FromManifestCreatedUtc);
                    Assert.Equal("2026-01-01T00:00:20Z", t.ToManifestCreatedUtc);
                });
        }
        finally
        {
            try
            { Directory.Delete(work, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void A_build_without_provenance_fails_loud()
    {
        var work = Path.Combine(Path.GetTempPath(), "evo-noprov-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        {
            WriteSnapshot(root, Snapshot(B1, new SchemaClass { Module = "client", Name = "CFoo", Size = 8 }));
            WriteSnapshot(root, Snapshot(B2, new SchemaClass { Module = "client", Name = "CFoo", Size = 16 }));
            File.Delete(Path.Combine(root, B2, Platform, "provenance.json"));

            Assert.Throws<FileNotFoundException>(() =>
                new SchemaEvolutionEmitter(SchemaFamily.Version, Platform).BuildFull(root, [B1, B2]));
        }
        finally
        {
            try
            { Directory.Delete(work, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static void WriteSnapshot(string root, Schemas.EntitySchema snapshot)
    {
        var dir = Path.Combine(root, snapshot.BuildId, Platform);
        Directory.CreateDirectory(dir);
        AtomicWrite.WriteCanonical(snapshot, Path.Combine(dir, "entity_schema.json"));
        var provenance = new Schemas.Provenance
        {
            Steam = new Schemas.SteamIdentity
            { ManifestCreatedUtc = $"2026-01-01T00:00:{snapshot.BuildId[^1]}0Z" },
        };
        AtomicWrite.WriteCanonical(provenance, Path.Combine(dir, "provenance.json"));
    }
}
