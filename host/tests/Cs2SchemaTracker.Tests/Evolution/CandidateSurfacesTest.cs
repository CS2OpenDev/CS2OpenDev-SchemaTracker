// Candidate-surface coverage (issue #7 items 1+2): the unselected N:M pair/class/field-move
// candidate lists — floors, signals, ordering, the frozen-vs-wider split against paired_evidence,
// and the schema-version gate that keeps a pre-candidates artifact off the incremental path.

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class CandidateSurfacesTest
{
    private const string Platform = "linux-x86_64";
    private const string B1 = "1000";
    private const string B2 = "1001";
    private const string B3 = "1002";


    // CA1861: expected signal arrays hoisted to static readonly fields.
    private static readonly string[] SigTypeSize = { "sizeMatch", "typeMatch" };
    private static readonly string[] SigOffsetOnly = { "offsetExact" };
    private static readonly string[] SigOffsetSize = { "offsetExact", "sizeMatch" };
    private static readonly string[] SigFrozenPair = { "offsetExact", "typeMatch" };
    private static readonly string[] SigFullMatch = { "offsetExact", "sizeMatch", "typeMatch" };
    private static readonly string[] SigClassFull = { "bareNameMatch", "fieldSetMatch", "sizeMatch" };
    private static readonly string[] SigClassBare = { "bareNameMatch" };
    private static readonly string[] SigHoist = { "fieldNameMatch", "parentChainUp", "typeMatch" };
    private static readonly string[] SigPushDown = { "fieldNameMatch", "parentChainDown", "typeMatch" };
    private static readonly string[] SigSideways = { "fieldNameMatch", "typeMatch" };

    private static readonly JsonParser StrictParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(false));

    // ---- fixture builders ------------------------------------------------------------------

    private static SchemaType Builtin(string name) =>
        new() { Category = SchemaType.Types.Category.Builtin, Name = name };

    private static SchemaField Field(string name, long offset, SchemaType type) =>
        new() { Name = name, Offset = offset, Type = type };

    private static SchemaClass Class(
        string module, string name, ulong size,
        (string Module, string Name)[]? parents = null, params SchemaField[] fields)
    {
        var c = new SchemaClass { Module = module, Name = name, Size = size };
        foreach (var p in parents ?? [])
            c.Parents.Add(new SchemaClassParent { Module = p.Module, Name = p.Name });
        c.Fields.AddRange(fields);
        return c;
    }

    private static Schemas.EntitySchema Snapshot(string build, params SchemaClass[] classes)
    {
        var s = new Schemas.EntitySchema
        { SchemaVersion = SchemaFamily.Version, BuildId = build, Platform = Platform };
        s.Classes.AddRange(classes);
        return s;
    }

    private static Transition Diff(Schemas.EntitySchema from, Schemas.EntitySchema to) =>
        SchemaSnapshotDiff.Diff(from, to, B1, B2);

    // ---- within-class pair candidates ------------------------------------------------------

    [Fact]
    public void Type_match_at_a_different_offset_yields_a_candidate_but_no_paired_evidence()
    {
        // The slot-shift case the frozen paired_evidence bar cannot see.
        var from = Snapshot(B1, Class("client", "CFoo", 16, null, Field("m_old", 0, Builtin("int32"))));
        var to = Snapshot(B2, Class("client", "CFoo", 16, null, Field("m_new", 8, Builtin("int32"))));

        var delta = Assert.Single(Diff(from, to).ClassChanged);

        Assert.Empty(delta.PairedEvidence);
        var candidate = Assert.Single(delta.PairCandidates);
        Assert.Equal("m_old", candidate.From);
        Assert.Equal("m_new", candidate.To);
        Assert.Equal(SigTypeSize, candidate.Signals);
    }

    [Fact]
    public void Offset_match_with_a_type_change_yields_a_candidate()
    {
        // Same slot, new type: signals carry offsetExact only (int32 -> int64 widths differ).
        var from = Snapshot(B1, Class("client", "CFoo", 16, null, Field("m_a", 0, Builtin("int32"))));
        var to = Snapshot(B2, Class("client", "CFoo", 16, null, Field("m_b", 0, Builtin("int64"))));

        var candidate = Assert.Single(Assert.Single(Diff(from, to).ClassChanged).PairCandidates);
        Assert.Equal(SigOffsetOnly, candidate.Signals);
    }

    [Fact]
    public void Size_match_rides_an_offset_match_but_never_stands_alone()
    {
        // int32 -> uint32 at the same offset: offsetExact + sizeMatch (types render differently).
        var from = Snapshot(B1, Class("client", "CFoo", 16, null, Field("m_a", 0, Builtin("int32"))));
        var to = Snapshot(B2, Class("client", "CFoo", 16, null, Field("m_b", 0, Builtin("uint32"))));
        var candidate = Assert.Single(Assert.Single(Diff(from, to).ClassChanged).PairCandidates);
        Assert.Equal(SigOffsetSize, candidate.Signals);

        // Equal widths at DIFFERENT offsets with DIFFERENT types (int32 vs float32): floor not met,
        // no candidate — sizeMatch alone would pair every same-width field in the class.
        var from2 = Snapshot(B1, Class("client", "CFoo", 16, null, Field("m_a", 0, Builtin("int32"))));
        var to2 = Snapshot(B2, Class("client", "CFoo", 16, null, Field("m_b", 8, Builtin("float32"))));
        Assert.Empty(Assert.Single(Diff(from2, to2).ClassChanged).PairCandidates);
    }

    [Fact]
    public void A_paired_evidence_pair_reappears_in_candidates_with_the_wider_signals()
    {
        // The candidates list is complete on its own — consumers never union the two surfaces.
        var from = Snapshot(B1, Class("client", "CFoo", 16, null, Field("m_x", 4, Builtin("bool"))));
        var to = Snapshot(B2, Class("client", "CFoo", 16, null, Field("m_y", 4, Builtin("bool"))));

        var delta = Assert.Single(Diff(from, to).ClassChanged);

        var evidence = Assert.Single(delta.PairedEvidence);
        Assert.Equal(SigFrozenPair, evidence.Signals); // frozen shape
        var candidate = Assert.Single(delta.PairCandidates);
        Assert.Equal(SigFullMatch, candidate.Signals);
    }

    [Fact]
    public void All_qualifying_pairs_are_emitted_without_selection()
    {
        // One removed int32, two added int32s: BOTH pairs emitted, (from, to) Ordinal order.
        // A 1:1 pick between them would be an inference.
        var from = Snapshot(B1, Class("client", "CFoo", 32, null, Field("m_r", 0, Builtin("int32"))));
        var to = Snapshot(B2, Class("client", "CFoo", 32, null,
            Field("m_a2", 16, Builtin("int32")),
            Field("m_a1", 8, Builtin("int32"))));

        var delta = Assert.Single(Diff(from, to).ClassChanged);
        Assert.Collection(delta.PairCandidates,
            c => { Assert.Equal("m_r", c.From); Assert.Equal("m_a1", c.To); },
            c => { Assert.Equal("m_r", c.From); Assert.Equal("m_a2", c.To); });
        Assert.Empty(delta.PairedEvidence);
    }

    // ---- cross-module class pair candidates ------------------------------------------------

    [Fact]
    public void A_cross_module_class_move_yields_a_class_pair_candidate()
    {
        var fields = new[] { Field("m_a", 0, Builtin("int32")) };
        var from = Snapshot(B1, Class("!GlobalTypes", "CThing", 8, null, fields));
        var to = Snapshot(B2, Class("libserver.so", "CThing", 8, null, Field("m_a", 0, Builtin("int32"))));

        var transition = Diff(from, to);

        Assert.Equal(["libserver.so/CThing"], transition.ClassAdded);
        Assert.Equal(["!GlobalTypes/CThing"], transition.ClassRemoved);
        var pair = Assert.Single(transition.ClassPairCandidates);
        Assert.Equal("!GlobalTypes/CThing", pair.From);
        Assert.Equal("libserver.so/CThing", pair.To);
        Assert.Equal(SigClassFull, pair.Signals);
    }

    [Fact]
    public void A_reshaped_cross_module_move_carries_only_the_signals_that_hold()
    {
        var from = Snapshot(B1, Class("!GlobalTypes", "CThing", 8, null, Field("m_a", 0, Builtin("int32"))));
        var to = Snapshot(B2, Class("libserver.so", "CThing", 24, null, Field("m_b", 0, Builtin("int64"))));

        var pair = Assert.Single(Diff(from, to).ClassPairCandidates);
        Assert.Equal(SigClassBare, pair.Signals);
    }

    [Fact]
    public void Unrelated_births_and_deaths_yield_no_class_pair()
    {
        var from = Snapshot(B1, Class("client", "COld", 8, null));
        var to = Snapshot(B2, Class("client", "CNew", 8, null));

        Assert.Empty(Diff(from, to).ClassPairCandidates);
    }

    // ---- cross-class field-move candidates -------------------------------------------------

    [Fact]
    public void A_hoist_to_a_parent_yields_a_field_move_candidate_with_parentChainUp()
    {
        (string, string)[] baseParent = [("client", "CBase")];
        var from = Snapshot(B1,
            Class("client", "CBase", 8, null),
            Class("client", "CChild", 16, baseParent, Field("m_v", 0, Builtin("int32"))));
        var to = Snapshot(B2,
            Class("client", "CBase", 8, null, Field("m_v", 0, Builtin("int32"))),
            Class("client", "CChild", 16, baseParent));

        var transition = Diff(from, to);

        var move = Assert.Single(transition.FieldMoveCandidates);
        Assert.Equal("client/CChild", move.FromClass);
        Assert.Equal("client/CBase", move.ToClass);
        Assert.Equal("m_v", move.Field);
        Assert.Equal(SigHoist, move.Signals);
    }

    [Fact]
    public void A_push_down_to_a_descendant_yields_parentChainDown()
    {
        (string, string)[] baseParent = [("client", "CBase")];
        var from = Snapshot(B1,
            Class("client", "CBase", 8, null, Field("m_v", 0, Builtin("int32"))),
            Class("client", "CChild", 16, baseParent));
        var to = Snapshot(B2,
            Class("client", "CBase", 8, null),
            Class("client", "CChild", 16, baseParent, Field("m_v", 0, Builtin("int32"))));

        var move = Assert.Single(Diff(from, to).FieldMoveCandidates);
        Assert.Equal(SigPushDown, move.Signals);
    }

    [Fact]
    public void A_sideways_move_between_unrelated_classes_carries_only_the_floor_signals()
    {
        var from = Snapshot(B1,
            Class("client", "CA", 8, null, Field("m_v", 0, Builtin("int32"))),
            Class("client", "CB", 8, null));
        var to = Snapshot(B2,
            Class("client", "CA", 8, null),
            Class("client", "CB", 8, null, Field("m_v", 4, Builtin("int32"))));

        var move = Assert.Single(Diff(from, to).FieldMoveCandidates);
        Assert.Equal(SigSideways, move.Signals);
    }

    [Fact]
    public void A_field_move_requires_type_equality_not_just_the_name()
    {
        // Same field name, different type: name alone across classes is noise, not evidence.
        var from = Snapshot(B1,
            Class("client", "CA", 8, null, Field("m_v", 0, Builtin("int32"))),
            Class("client", "CB", 8, null));
        var to = Snapshot(B2,
            Class("client", "CA", 8, null),
            Class("client", "CB", 8, null, Field("m_v", 0, Builtin("float32"))));

        Assert.Empty(Diff(from, to).FieldMoveCandidates);
    }

    // ---- the schema-version gate on the incremental path -------------------------------------

    [Fact]
    public void A_prior_artifact_from_an_older_schema_version_forces_a_full_backfill()
    {
        var work = Path.Combine(Path.GetTempPath(), "evo-ver-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        {
            // B1 -> B2 contains a wide-floor-only candidate (type match, shifted offset) that a
            // pre-0.6.0 artifact could not carry.
            WriteSnapshot(root, Snapshot(B1,
                Class("client", "CFoo", 16, null, Field("m_old", 0, Builtin("int32")))));
            WriteSnapshot(root, Snapshot(B2,
                Class("client", "CFoo", 16, null, Field("m_new", 8, Builtin("int32")))));
            Assert.Equal(0, EvolutionCommand.Run(["--platform", Platform], root));

            // Simulate the file having been written by pre-candidates code: strip every candidate
            // surface and stamp the previous family version.
            var outputPath = Path.Combine(root, "schema_evolution", Platform + ".json");
            var aged = StrictParser.Parse<SchemaEvolution>(File.ReadAllText(outputPath));
            aged.SchemaVersion = "0.5.1";
            foreach (var t in aged.Transitions)
            {
                t.ClassPairCandidates.Clear();
                t.FieldMoveCandidates.Clear();
                foreach (var cd in t.ClassChanged)
                    cd.PairCandidates.Clear();
            }
            AtomicWrite.WriteCanonical(aged, outputPath);

            // A new build lands; the file is CONTIGUOUS (latest_build == B2) but version-stale.
            // Without the version gate the incremental path would extend it into a mixed shape;
            // with it, the full backfill restores the candidate on the B1 -> B2 transition.
            WriteSnapshot(root, Snapshot(B3,
                Class("client", "CFoo", 16, null, Field("m_new", 8, Builtin("int32")))));
            Assert.Equal(0, EvolutionCommand.Run(["--platform", Platform], root));

            var refreshed = StrictParser.Parse<SchemaEvolution>(File.ReadAllText(outputPath));
            Assert.Equal(SchemaFamily.Version, refreshed.SchemaVersion);
            Assert.Equal(2, refreshed.Transitions.Count);
            var candidate = Assert.Single(Assert.Single(refreshed.Transitions[0].ClassChanged).PairCandidates);
            Assert.Equal("m_old", candidate.From);
            Assert.Equal("m_new", candidate.To);
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
