// Determinism check for the canonical-form JSON helper (sentinel).
//
// Doesn't yet exercise round-trip (that needs the schemas/*.proto compiled
// into C# types). This test just guards the load-bearing invariants of
// CanonicalJson itself: keys sorted, LF endings, byte-identical across runs.

using Cs2SchemaTracker.Host.Serialization;

namespace Cs2SchemaTracker.Tests;

public class CanonicalJsonTest
{
    [Xunit.Fact]
    public void Keys_Are_Sorted_Recursively()
    {
        var input = new
        {
            zeta = 1,
            alpha = new { z = "last", a = "first", m = "middle" },
            beta = new object[] { new { c = 3, a = 1, b = 2 }, "literal" },
        };

        var output = CanonicalJson.Serialize(input);

        var expected =
            "{\n  \"alpha\": {\n    \"a\": \"first\",\n    \"m\": \"middle\",\n    \"z\": \"last\"\n  },\n  " +
            "\"beta\": [\n    {\n      \"a\": 1,\n      \"b\": 2,\n      \"c\": 3\n    },\n    \"literal\"\n  ],\n  " +
            "\"zeta\": 1\n}";

        Xunit.Assert.Equal(expected, output);
    }

    [Xunit.Fact]
    public void Output_Uses_Lf_Endings_Not_Crlf()
    {
        var output = CanonicalJson.Serialize(new { a = 1, b = 2 });
        Xunit.Assert.DoesNotContain("\r", output);
        Xunit.Assert.Contains("\n", output);
    }

    [Xunit.Fact]
    public void Same_Input_Twice_Is_Byte_Identical()
    {
        var input = new { x = 1, y = new[] { 1, 2, 3 }, nested = new { deep = new { value = "test" } } };
        var first = CanonicalJson.Serialize(input);
        var second = CanonicalJson.Serialize(input);
        Xunit.Assert.Equal(first, second);
    }

    [Xunit.Fact]
    public void Insertion_Order_Does_Not_Affect_Output()
    {
        // Two equivalent inputs with different property declaration order must
        // serialize identically — this is the property guards against.
        var dictAtoZ = new System.Collections.Generic.Dictionary<string, int>
        {
            ["a"] = 1,
            ["b"] = 2,
            ["c"] = 3,
        };
        var dictZtoA = new System.Collections.Generic.Dictionary<string, int>
        {
            ["c"] = 3,
            ["b"] = 2,
            ["a"] = 1,
        };

        Xunit.Assert.Equal(CanonicalJson.Serialize(dictAtoZ), CanonicalJson.Serialize(dictZtoA));
    }

    [Xunit.Fact]
    public void Null_Roundtrips_As_Null_Literal()
    {
        Xunit.Assert.Equal("null", CanonicalJson.Serialize(null));
    }
}
