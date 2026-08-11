// 0.8.0 coverage (issue #7 item 4): structured per-key metadata ops — kinds, the size-threshold
// value representation, the multiset join rule, marker keys, and the three attachment points
// (class, field META_CHANGE, enum member).

using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Schemas;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Evolution;

public sealed class MetadataOpsTest
{
    private const string Platform = "linux-x86_64";

    private static SchemaType Builtin(string name) =>
        new() { Category = SchemaType.Types.Category.Builtin, Name = name };

    private static SchemaMetadata Meta(string name, string value) =>
        new() { Name = name, Value = value };

    private static SchemaClass Class(string name, params SchemaMetadata[] metadata)
    {
        var c = new SchemaClass { Module = "client", Name = name, Size = 8 };
        c.Metadata.AddRange(metadata);
        return c;
    }

    private static Schemas.EntitySchema Snapshot(string build, params SchemaClass[] classes)
    {
        var s = new Schemas.EntitySchema
        { SchemaVersion = "0.8.0", BuildId = build, Platform = Platform };
        s.Classes.AddRange(classes);
        return s;
    }

    private static Transition Diff(Schemas.EntitySchema from, Schemas.EntitySchema to) =>
        SchemaSnapshotDiff.Diff(from, to, "1000", "1001");

    private static ClassDelta SingleDelta(SchemaClass oldC, SchemaClass newC) =>
        Assert.Single(Diff(Snapshot("1000", oldC), Snapshot("1001", newC)).ClassChanged);

    // ---- kinds and ordering ------------------------------------------------------------------

    [Fact]
    public void Added_removed_and_changed_keys_each_get_their_own_op()
    {
        var delta = SingleDelta(
            Class("CFoo", Meta("MGone", "x"), Meta("MKept", "old")),
            Class("CFoo", Meta("MKept", "new"), Meta("MNew", "y")));

        Assert.Collection(delta.MetaOps,
            op =>
            {
                Assert.Equal(MetaEntryOp.Types.Kind.RemoveKey, op.Kind);
                Assert.Equal("MGone", op.Name);
                Assert.Equal("x", op.From.Value);
                Assert.Null(op.To);
            },
            op =>
            {
                Assert.Equal(MetaEntryOp.Types.Kind.ChangeValue, op.Kind);
                Assert.Equal("MKept", op.Name);
                Assert.Equal(("old", "new"), (op.From.Value, op.To.Value));
            },
            op =>
            {
                Assert.Equal(MetaEntryOp.Types.Kind.AddKey, op.Kind);
                Assert.Equal("MNew", op.Name);
                Assert.Null(op.From);
                Assert.Equal("y", op.To.Value);
            });
    }

    [Fact]
    public void An_unchanged_key_emits_no_op()
    {
        var oldC = Class("CFoo", Meta("MSame", "v"), Meta("MChanged", "a"));
        var newC = Class("CFoo", Meta("MSame", "v"), Meta("MChanged", "b"));

        var op = Assert.Single(SingleDelta(oldC, newC).MetaOps);
        Assert.Equal("MChanged", op.Name);
    }

    // ---- the size-threshold representation ---------------------------------------------------

    [Fact]
    public void A_bulky_value_is_carried_as_sha256_and_byte_length_not_verbatim()
    {
        var bulky = new string('k', 300); // > 256 UTF-8 bytes
        var delta = SingleDelta(
            Class("CFoo", Meta("MGetKV3ClassDefaults", bulky)),
            Class("CFoo", Meta("MGetKV3ClassDefaults", bulky + "x")));

        var op = Assert.Single(delta.MetaOps);
        Assert.Equal("", op.From.Value);
        Assert.Equal(64, op.From.ValueSha256.Length);
        Assert.Equal(op.From.ValueSha256, op.From.ValueSha256.ToLowerInvariant());
        Assert.Equal(300ul, op.From.ValueBytes);
        Assert.Equal(301ul, op.To.ValueBytes);
        Assert.NotEqual(op.From.ValueSha256, op.To.ValueSha256); // the change stays visible
    }

    [Fact]
    public void The_threshold_is_decided_per_side()
    {
        // Old side small (verbatim), new side bulky (hashed) — each side independently encoded.
        var delta = SingleDelta(
            Class("CFoo", Meta("MKey", "small")),
            Class("CFoo", Meta("MKey", new string('v', 400))));

        var op = Assert.Single(delta.MetaOps);
        Assert.Equal("small", op.From.Value);
        Assert.Equal(0ul, op.From.ValueBytes);
        Assert.Equal("", op.To.Value);
        Assert.Equal(400ul, op.To.ValueBytes);
    }

    [Fact]
    public void A_256_byte_value_is_still_verbatim()
    {
        var atLimit = new string('a', 256);
        var delta = SingleDelta(
            Class("CFoo", Meta("MKey", "x")),
            Class("CFoo", Meta("MKey", atLimit)));

        var op = Assert.Single(delta.MetaOps);
        Assert.Equal(atLimit, op.To.Value);
        Assert.Equal("", op.To.ValueSha256);
    }

    // ---- multiset join + marker keys ---------------------------------------------------------

    [Fact]
    public void Duplicate_keys_join_into_one_logical_value_and_one_op()
    {
        // "MMulti" appears twice; only the second copy's value changes. One op, joined values.
        var delta = SingleDelta(
            Class("CFoo", Meta("MMulti", "b"), Meta("MMulti", "a")),
            Class("CFoo", Meta("MMulti", "c"), Meta("MMulti", "a")));

        var op = Assert.Single(delta.MetaOps);
        Assert.Equal(MetaEntryOp.Types.Kind.ChangeValue, op.Kind);
        Assert.Equal(("a;b", "a;c"), (op.From.Value, op.To.Value)); // Ordinal-sorted before join
    }

    [Fact]
    public void A_marker_key_with_an_empty_value_is_a_value_not_an_absence()
    {
        // MNetworkEnable-style marker: added with value "". The op is ADD_KEY, and the empty
        // value is legal — presence rides the kind.
        var delta = SingleDelta(
            Class("CFoo"),
            Class("CFoo", Meta("MNetworkEnable", "")));

        var op = Assert.Single(delta.MetaOps);
        Assert.Equal(MetaEntryOp.Types.Kind.AddKey, op.Kind);
        Assert.Equal("MNetworkEnable", op.Name);
        Assert.NotNull(op.To);
        Assert.Equal("", op.To.Value);
    }

    // ---- the other two attachment points -----------------------------------------------------

    [Fact]
    public void A_field_meta_change_carries_the_structured_ops_alongside_the_frozen_dumps()
    {
        var oldC = new SchemaClass { Module = "client", Name = "CFoo", Size = 8 };
        var oldF = new SchemaField { Name = "m_v", Offset = 0, Type = Builtin("int32") };
        oldF.Metadata.Add(Meta("MNetworkAlias", "m_old"));
        oldC.Fields.Add(oldF);
        var newC = new SchemaClass { Module = "client", Name = "CFoo", Size = 8 };
        var newF = new SchemaField { Name = "m_v", Offset = 0, Type = Builtin("int32") };
        newF.Metadata.Add(Meta("MNetworkAlias", "m_new"));
        newC.Fields.Add(newF);

        var delta = SingleDelta(oldC, newC);

        var fieldOp = Assert.Single(delta.FieldOps);
        Assert.Equal(FieldOp.Types.Kind.MetaChange, fieldOp.Kind);
        Assert.Equal("MNetworkAlias=m_old", fieldOp.FromMeta);   // frozen dump still emitted
        var metaOp = Assert.Single(fieldOp.MetaOps);
        Assert.Equal("MNetworkAlias", metaOp.Name);
        Assert.Equal(("m_old", "m_new"), (metaOp.From.Value, metaOp.To.Value));
    }

    [Fact]
    public void An_enum_member_metadata_change_surfaces_as_change_member_meta()
    {
        // Previously invisible: the member value is unchanged, only its metadata moved.
        SchemaEnum MakeEnum(string metaValue)
        {
            var e = new SchemaEnum { Module = "client", Name = "EFoo", Alignment = "uint32_t", Size = 4 };
            var m = new SchemaEnumMember { Name = "A", Value = 1 };
            m.Metadata.Add(Meta("MPropertySuppressEnumerator", metaValue));
            e.Members.Add(m);
            return e;
        }
        var from = Snapshot("1000");
        from.Enums.Add(MakeEnum("old"));
        var to = Snapshot("1001");
        to.Enums.Add(MakeEnum("new"));

        var delta = Assert.Single(Diff(from, to).EnumChanged);
        var op = Assert.Single(delta.MemberOps);
        Assert.Equal(EnumMemberOp.Types.Kind.ChangeMemberMeta, op.Kind);
        Assert.Equal("A", op.Member);
        var metaOp = Assert.Single(op.MetaOps);
        Assert.Equal(("old", "new"), (metaOp.From.Value, metaOp.To.Value));
    }
}
