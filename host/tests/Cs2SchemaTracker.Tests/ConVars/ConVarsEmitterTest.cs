// ConVarsEmitter unit tests (synthetic WalkerOutput; no walker, no Steam).
//
// Asserts: faithful lift of the ConVar walk, deterministic ordering (sort by name) +
// byte-identical re-run, and fail-loud on a missing walk / empty-named convar.

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.ConVars;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.ConVars;

public class ConVarsEmitterTest
{
    private const string Platform = "linux-x86_64";

    private static WalkerOutput WalkWith(params ConVar[] convars)
    {
        var walk = new ConVarsWalk();
        walk.Convars.AddRange(convars);
        return new WalkerOutput { Platform = Platform, Convars = walk };
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "convars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Lifts_Faithfully_And_Sorts_By_Name()
    {
        var dir = NewDir();
        try
        {
            var b = new ConVar { Name = "b_two", Default = "1", Description = "second" };
            b.Flags.Add("gamedll");
            var a = new ConVar { Name = "a_one", Default = "0", Description = "first" };
            a.Flags.Add("release");
            a.Flags.Add("cheat");

            var outPath = Path.Combine(dir, "convars.json");
            new ConVarsEmitter(SchemaFamily.Version, "build1", Platform).Emit(WalkWith(b, a), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = doc.RootElement;
            Assert.Equal("build1", root.GetProperty("buildId").GetString());
            Assert.Equal(Platform, root.GetProperty("platform").GetString());

            var convars = root.GetProperty("convars");
            Assert.Equal(2, convars.GetArrayLength());
            // Sorted: a_one before b_two.
            Assert.Equal("a_one", convars[0].GetProperty("name").GetString());
            Assert.Equal("b_two", convars[1].GetProperty("name").GetString());
            Assert.Equal("0", convars[0].GetProperty("default").GetString());
            // Flags preserved in declared order.
            var flags = convars[0].GetProperty("flags").EnumerateArray().Select(e => e.GetString()).ToArray();
            string[] expectedFlags = { "release", "cheat" };
            Assert.Equal(expectedFlags, flags);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Carries_Through_Typing_And_Bounds_Fields()
    {
        var dir = NewDir();
        try
        {
            // A typed convar WITH both bounds, and a typed convar WITHOUT bounds.
            var bounded = new ConVar
            {
                Name = "mp_roundtime",
                Default = "1.92",
                Description = "round time",
                ValueType = "Float32",
                HasMin = true,
                MinValue = "0",
                HasMax = true,
                MaxValue = "60",
            };
            var unbounded = new ConVar
            {
                Name = "sv_cheats",
                Default = "0",
                ValueType = "Bool",
                // has_min/has_max default false; min/max stay "".
            };

            var outPath = Path.Combine(dir, "convars.json");
            new ConVarsEmitter(SchemaFamily.Version, "build1", Platform)
                .Emit(WalkWith(bounded, unbounded), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var convars = doc.RootElement.GetProperty("convars");

            // Sorted by name: mp_roundtime before sv_cheats.
            var round = convars[0];
            Assert.Equal("mp_roundtime", round.GetProperty("name").GetString());
            Assert.Equal("Float32", round.GetProperty("valueType").GetString());
            Assert.True(round.GetProperty("hasMin").GetBoolean());
            Assert.Equal("0", round.GetProperty("minValue").GetString());
            Assert.True(round.GetProperty("hasMax").GetBoolean());
            Assert.Equal("60", round.GetProperty("maxValue").GetString());

            var cheats = convars[1];
            Assert.Equal("sv_cheats", cheats.GetProperty("name").GetString());
            Assert.Equal("Bool", cheats.GetProperty("valueType").GetString());
            Assert.False(cheats.GetProperty("hasMin").GetBoolean());
            Assert.Equal("", cheats.GetProperty("minValue").GetString());
            Assert.False(cheats.GetProperty("hasMax").GetBoolean());
            Assert.Equal("", cheats.GetProperty("maxValue").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewDir();
        try
        {
            var walk = WalkWith(
                new ConVar { Name = "z", Default = "1" },
                new ConVar { Name = "a", Default = "0" });
            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            new ConVarsEmitter(SchemaFamily.Version, "build", Platform).Emit(walk, pa);
            new ConVarsEmitter(SchemaFamily.Version, "build", Platform).Emit(walk, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Convars_Walk()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "convars.json");
            var wo = new WalkerOutput { Platform = Platform }; // no convars walk
            Assert.Throws<InvalidDataException>(() =>
                new ConVarsEmitter(SchemaFamily.Version, "b", Platform).Emit(wo, outPath));
            Assert.False(File.Exists(outPath), "no bytes on fail-loud");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Name_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "convars.json");
            Assert.Throws<InvalidDataException>(() =>
                new ConVarsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(new ConVar { Name = "", Default = "0" }), outPath));
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
