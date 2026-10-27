// Tests for entity_io.json: the Hammer FGDs parsed, include-expanded, exclusion/override-applied
// and inheritance-resolved into the EntityIo message.
//
// The happy path runs over the committed fixture tree under ToolsContent/fixtures/ (trimmed real
// csgo.fgd, base.fgd and markup_volumes.fgd from the 2026-10-22 Workshop Tools depot). Edge and
// fail-loud cases use small inline trees.
//
// We assert:
//   * point_script carries cs_script (resource:vjs) and input RunScriptInput(string);
//   * func_button inherits OnPressed from Button through BaseFuncButton, with declared_in naming
//     the base, and csgo.fgd's @OverrideClass displaytext replaces Button's in place;
//   * spawnflags flags (bit, label, default), tag_list entries with four fields;
//   * @exclude removes classes (listed in `excluded`), exclude-then-redefine replaces them;
//   * every file the include graph reached is a source with its sha256, sorted;
//   * include resolution prefers game/csgo/ over game/core/;
//   * determinism and fail-loud (unknown base, cycle, duplicate definition, override of nothing,
//     missing include, unterminated string, too many keyvalue fields, non-integer flag).

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.EntityIo;
using Cs2SchemaTracker.Host.ToolsContent;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.EntityIo;

public sealed class EntityIoEmitterTest
{
    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "ToolsContent", "fixtures");

    private static Schemas.EntityIo BuildFixture()
        => new EntityIoEmitter(SchemaFamily.Version, "25966348", "windows-x86_64").Build(new ToolsContentTree(FixtureRoot));

    private static Schemas.EntityIo BuildInline(params (string Path, string Text)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2-fgd-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (rel, text) in files)
            {
                var full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            return new EntityIoEmitter(SchemaFamily.Version, "1", "windows-x86_64").Build(new ToolsContentTree(root));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static EntityClass Class(Schemas.EntityIo io, string name) => Assert.Single(io.Classes, c => c.Name == name);

    [Fact]
    public void Fixture_point_script_keyvalues_and_input()
    {
        var io = BuildFixture();
        Assert.Equal(EntityIoEmitter.RootPath, io.Root);
        var ps = Class(io, "point_script");
        Assert.Equal("PointClass", ps.ClassType);
        Assert.False(ps.IsBase);
        Assert.Equal("game/csgo/csgo.fgd", ps.SourceFile);
        Assert.Equal("iconsprite(\"materials/editor/point_script.vmat\")", string.Join(",", ps.Helpers));
        var cs = Assert.Single(ps.Keyvalues, k => k.Name == "cs_script");
        Assert.Equal("resource:vjs", cs.Type);
        Assert.Equal("cs_script", cs.DisplayName);
        Assert.False(cs.HasDefaultValue);
        var target = Assert.Single(ps.Keyvalues, k => k.Name == "targetname");
        Assert.Equal("{ sort_priority = 90 }", target.Metadata);
        var input = Assert.Single(ps.Inputs);
        Assert.Equal("RunScriptInput", input.Name);
        Assert.Equal("string", input.ParamType);
        Assert.Equal("Run callback registered using OnScriptInput.", input.Description);
        Assert.Equal("point_script", input.DeclaredIn);
    }

    [Fact]
    public void Fixture_func_button_resolves_inheritance_and_override()
    {
        var io = BuildFixture();
        var fb = Class(io, "func_button");
        Assert.Equal("BaseFuncButton", string.Join(",", fb.Bases));
        Assert.Equal("game/csgo/csgo.fgd", string.Join(",", fb.OverrideSources));
        var pressed = Assert.Single(fb.Outputs, o => o.Name == "OnPressed");
        Assert.Equal("Button", pressed.DeclaredIn);
        Assert.Contains(fb.Outputs, o => o.Name == "OnUser1" && o.DeclaredIn == "GameEntity");

        // The override's displaytext replaced the inherited one IN PLACE (same index as in Button).
        var names = fb.Keyvalues.Select(k => k.Name).ToList();
        Assert.Single(names, n => n == "displaytext");
        var display = Assert.Single(fb.Keyvalues, k => k.Name == "displaytext");
        Assert.Equal("func_button", display.DeclaredIn);
        Assert.Equal("Use Prompt Token", display.DisplayName);
        Assert.True(names.IndexOf("displaytext") < names.IndexOf("use_sound"));

        var flags = Assert.Single(fb.Keyvalues, k => k.Name == "spawnflags");
        Assert.Equal("flags", flags.Type, ignoreCase: true);
        var use = Assert.Single(flags.Flags, f => f.Value == 1024);
        Assert.Equal("Use Activates", use.Label);
        Assert.True(use.Default);
    }

    [Fact]
    public void Fixture_exclusions_redefinitions_and_tag_lists()
    {
        var io = BuildFixture();
        Assert.Equal("env_sky,func_wall", string.Join(",", io.Excluded.Select(e => e.Name)));
        Assert.All(io.Excluded, e => Assert.Equal("game/csgo/csgo.fgd", e.SourceFile));
        Assert.DoesNotContain(io.Classes, c => c.Name is "env_sky" or "func_wall");

        var fog = Class(io, "fog_volume");
        Assert.Equal("game/csgo/csgo.fgd", fog.SourceFile);
        Assert.Contains(fog.Keyvalues, k => k.Name == "FogName");
        Assert.DoesNotContain(fog.Keyvalues, k => k.Name == "PostProcessName");

        var nav = Class(io, "func_nav_markup");
        var gen = Assert.Single(nav.Keyvalues, k => k.Name == "navProperty_NavGen");
        var nonav = Assert.Single(gen.Choices, c => c.Value == "NONAV");
        Assert.Equal("No Nav", nonav.Label);
        Assert.Equal("0", nonav.DefaultValue);
        Assert.Equal("test detailed description", nonav.Description);

        Assert.True(Class(io, "Targetname").IsBase);
    }

    [Fact]
    public void Fixture_sources_are_every_reached_file_sorted_with_hashes()
    {
        var io = BuildFixture();
        Assert.Equal("game/core/base.fgd,game/core/markup_volumes.fgd,game/csgo/csgo.fgd", string.Join(",", io.Sources.Select(s => s.Path)));
        Assert.All(io.Sources, s => Assert.Equal(64, s.Sha256.Length));
        var names = io.Classes.Select(c => c.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }

    [Fact]
    public void Emit_is_deterministic_and_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-fgd-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var emitter = new EntityIoEmitter(SchemaFamily.Version, "25966348", "windows-x86_64");
            var tree = new ToolsContentTree(FixtureRoot);
            var read = emitter.Emit(tree, Path.Combine(dir, "a.json"));
            emitter.Emit(tree, Path.Combine(dir, "b.json"));
            Assert.Equal(File.ReadAllBytes(Path.Combine(dir, "a.json")), File.ReadAllBytes(Path.Combine(dir, "b.json")));
            Assert.Equal(3, read.Count);
            var parsed = new JsonParser(JsonParser.Settings.Default).Parse<Schemas.EntityIo>(File.ReadAllText(Path.Combine(dir, "a.json")));
            Assert.Equal(BuildFixture(), parsed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Include_prefers_mod_dir_and_handles_crlf_concat_and_escapes()
    {
        var io = BuildInline(
            ("game/csgo/csgo.fgd", "@include \"shared.fgd\"\r\n@PointClass base(B) = p : \"one \" +\r\n \"two\" [ k(string) : \"K\" : \"say \\\"hi\\\"\" : \"d\" ]\r\n"),
            ("game/csgo/shared.fgd", "@BaseClass = B [ input In(void) ]"),
            ("game/core/shared.fgd", "@BaseClass = B [ input Wrong(void) ]"));
        var p = Class(io, "p");
        Assert.Equal("one two", p.Description);
        Assert.Equal("say \"hi\"", p.Keyvalues[0].DefaultValue);
        Assert.Equal("In", Assert.Single(p.Inputs).Name);
        Assert.Equal("game/csgo/shared.fgd", Assert.Single(io.Sources, s => s.Path.EndsWith("shared.fgd", StringComparison.Ordinal)).Path);
    }

    [Fact]
    public void Flags_merge_per_bit_down_the_chain()
    {
        var io = BuildInline(("game/csgo/csgo.fgd", """
            @BaseClass = A [ spawnflags(flags) = [ 1 : "One" : 0  2 : "Two" : 1 ] ]
            @PointClass base(A) = b [ spawnflags(flags) = [ 2 : "Two!" : 0  4 : "Four" : 0 ] ]
            """));
        var flags = Assert.Single(Class(io, "b").Keyvalues).Flags;
        Assert.Equal("1,2,4", string.Join(",", flags.Select(f => f.Value)));
        Assert.Equal("A", flags[0].DeclaredIn);
        Assert.Equal("Two!", flags[1].Label);
        Assert.Equal("b", flags[1].DeclaredIn);
    }

    [Theory]
    [InlineData("@PointClass base(Nope) = a []", "unknown base class 'Nope'")]
    [InlineData("@BaseClass base(B) = A []\n@BaseClass base(A) = B []\n@PointClass base(A) = c []", "inheritance cycle")]
    [InlineData("@PointClass = a []\n@PointClass = a []", "already defined")]
    [InlineData("@OverrideClass = ghost []", "names no class defined before it")]
    [InlineData("@include \"missing.fgd\"", "not found")]
    [InlineData("@PointClass = a : \"unterminated [", "unterminated string")]
    [InlineData("@PointClass = a [ k(string) : \"a\" : \"b\" : \"c\" : \"d\" ]", "more than three")]
    [InlineData("@PointClass = a [ spawnflags(flags) = [ x : \"bad\" : 0 ] ]", "not a 32-bit unsigned integer")]
    [InlineData("@PointClass = a [ 42 ]", "unexpected")]
    [InlineData("@PointClass = a [ k(string ]", "mismatched")]
    public void Malformed_fgd_fails_loud(string csgo, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => BuildInline(("game/csgo/csgo.fgd", csgo)));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_root_fails_loud_and_writes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2-fgd-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tree = new ToolsContentTree(root);
            Assert.False(EntityIoEmitter.HasSource(tree));
            var outPath = Path.Combine(root, "entity_io.json");
            Assert.Throws<InvalidDataException>(() =>
                new EntityIoEmitter(SchemaFamily.Version, "1", "windows-x86_64").Emit(tree, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Directives_without_a_class_are_tolerated()
    {
        var io = BuildInline(("game/csgo/csgo.fgd", """
            @mapsize(-16384, 16384)
            @EntityGroup "Player" { start_expanded = true }
            @VisGroupFilter { filter_type = "toolsMaterial" material = "x.vmat" group = "G" }
            @PointClass = a []
            """));
        Assert.Equal("a", Assert.Single(io.Classes).Name);
    }
}
