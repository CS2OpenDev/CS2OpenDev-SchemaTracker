// regression coverage for the KV3-text typed-value enhancement.
//
// The shared KV3-text parser (host EntitySchema/Kv3.cs) gained additive support for KV3 TYPED
// values of the form `type:value` (e.g. `resource:"…"`, `subclass:"…"`), needed by the
// surfaceproperties_impact_effects.txt source. These tests pin:
//   * a typed value UNWRAPS to its inner value (the type tag is dropped from the structural payload);
//   * plain bare scalars (numbers / bools / null / unquoted tokens with NO ':<value>' suffix) parse
// EXACTLY as before — the enhancement is backward-compatible for all prior inputs;
//   * a bare token that merely CONTAINS a colon but is NOT a typed value (inner not "/[/{ — e.g.
//     `12:30`, `foo:bar`) is kept WHOLE, never narrowed to its suffix (no silent data loss).

using Cs2SchemaTracker.Host.EntitySchema;

using Google.Protobuf.WellKnownTypes;

using Xunit;

namespace Cs2SchemaTracker.Tests.EntitySchema;

public class Kv3TypedValueTest
{
    [Fact]
    public void TypedResource_Value_Unwraps_To_Inner_String()
    {
        var v = Kv3.Parse("""{ effect = resource:"particles/x.vpcf" }""");
        var effect = v.StructValue.Fields["effect"];
        Assert.Equal(Value.KindOneofCase.StringValue, effect.KindCase);
        Assert.Equal("particles/x.vpcf", effect.StringValue);
    }

    [Fact]
    public void TypedArray_Value_Unwraps_To_Inner_List()
    {
        var v = Kv3.Parse("""{ items = sometype:[ "a", "b" ] }""");
        var items = v.StructValue.Fields["items"];
        Assert.Equal(Value.KindOneofCase.ListValue, items.KindCase);
        Assert.Equal(2, items.ListValue.Values.Count);
        Assert.Equal("a", items.ListValue.Values[0].StringValue);
    }

    [Fact]
    public void PlainBareScalars_Parse_Unchanged()
    {
        // Numbers, bools, null, and an unquoted token with NO ':<value>' suffix must be unaffected.
        var v = Kv3.Parse("{ n = 12  f = 1.5  b = true  z = null  tok = some_token }");
        var f = v.StructValue.Fields;
        Assert.Equal(12d, f["n"].NumberValue);
        Assert.Equal(1.5d, f["f"].NumberValue);
        Assert.True(f["b"].BoolValue);
        Assert.Equal(Value.KindOneofCase.NullValue, f["z"].KindCase);
        Assert.Equal("some_token", f["tok"].StringValue);
    }

    [Fact]
    public void TypedMap_Value_Unwraps_To_Inner_Map()
    {
        var v = Kv3.Parse("{ sub = subclass:{ a = 1 } }");
        var sub = v.StructValue.Fields["sub"];
        Assert.Equal(Value.KindOneofCase.StructValue, sub.KindCase);
        Assert.Equal(1d, sub.StructValue.Fields["a"].NumberValue);
    }

    [Fact]
    public void BareToken_With_Colon_Is_Not_Typed_And_Kept_Whole()
    {
        // Regression: a colon inside a bare token whose inner value is NOT a KV3 typed-value
        // form ("/[/{) must be preserved whole — the prefix is never dropped (12:30, foo:bar).
        var v = Kv3.Parse("{ ratio = 12:30  pair = foo:bar }");
        var f = v.StructValue.Fields;
        Assert.Equal("12:30", f["ratio"].StringValue);
        Assert.Equal("foo:bar", f["pair"].StringValue);
    }

    [Fact]
    public void BareToken_With_Colon_Then_Backslash_Path_Is_Kept_Whole()
    {
        // Regression (edge case): a Windows-style path `c:\path` has a colon
        // whose following char is a backslash, NOT a KV3 typed-value form — keep it whole.
        var v = Kv3.Parse(@"{ path = c:\dir\file }");
        Assert.Equal(@"c:\dir\file", v.StructValue.Fields["path"].StringValue);
    }

    [Fact]
    public void BareToken_Ending_In_Colon_With_No_Inner_Value_Is_Kept_Whole_No_Throw()
    {
        // Regression (edge case): a bare token ending in ':' with no following
        // value (here ':' is the last char before the closing brace) must NOT be treated as a
        // typed prefix — that would consume ':' and then throw on the missing inner value. It is
        // kept whole as a bare scalar. Covers the trailing-colon-before-separator case.
        var v = Kv3.Parse("{ tag = resource: }");
        Assert.Equal("resource:", v.StructValue.Fields["tag"].StringValue);
    }

    [Fact]
    public void TopLevel_BareToken_Ending_In_Colon_At_EndOfInput_Is_Kept_Whole_No_Throw()
    {
        // Regression (edge case): the trailing-colon case at end-of-input
        // (':' is the final character) must parse whole, not throw on a missing inner value.
        var top = Kv3.Parse("plain:");
        Assert.Equal(Value.KindOneofCase.StringValue, top.KindCase);
        Assert.Equal("plain:", top.StringValue);
    }
}
