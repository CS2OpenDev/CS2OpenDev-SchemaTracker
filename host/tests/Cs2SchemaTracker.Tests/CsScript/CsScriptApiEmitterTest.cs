// Tests for cs_script_api.json: Valve's point_script.d.ts parsed into the CsScriptApi message.
//
// The happy-path assertions run over the committed fixture under ToolsContent/fixtures/ — a subset
// of the real 2026-10-22 declaration file whose blocks are verbatim (IPointScript in full, the
// console and game-rules interfaces, several enums, the trace and damage interfaces, the Entity
// classes). The syntax-edge and fail-loud cases use small inline sources, because the point of each
// is one construct.
//
// We assert:
//   * the module, its three exports, es_target from tsconfig.json, both module-level JSDoc blocks;
//   * IPointScript's On* hooks, overload numbering with @deprecated on the legacy overloads,
//     generics, @param / @experimental / @example / @default lifting, intersections and unions;
//   * enum resolution: implicit increments after a negative literal, `1 << n` (IGNORE_ARMOR == 64),
//     trailing line comments, an empty deprecated enum;
//   * referenced_types holds declared names only, sorted;
//   * types and enums are Ordinal-sorted; members keep declaration order;
//   * determinism (two emits byte-identical, LF, no BOM) and fail-loud with no bytes written.

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.CsScript;
using Cs2SchemaTracker.Host.ToolsContent;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.CsScript;

public sealed class CsScriptApiEmitterTest
{
    internal static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "ToolsContent", "fixtures");

    private static CsScriptApi BuildFixture()
        => new CsScriptApiEmitter(SchemaFamily.Version, "25966348", "windows-x86_64")
            .Build(new ToolsContentTree(FixtureRoot), out _);

    private static CsScriptApi BuildInline(string dts, string? tsconfig = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2-csscript-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "content", "csgo_addons", "cs_script_demo", "maps", "scripts");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "point_script.d.ts"), dts);
            if (tsconfig is not null)
                File.WriteAllText(Path.Combine(dir, "tsconfig.json"), tsconfig);
            return new CsScriptApiEmitter(SchemaFamily.Version, "1", "windows-x86_64")
                .Build(new ToolsContentTree(root), out _);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static CsScriptType Type(CsScriptApi api, string name) => Assert.Single(api.Types_, t => t.Name == name);

    private static CsScriptEnum Enum(CsScriptApi api, string name) => Assert.Single(api.Enums, e => e.Name == name);

    [Fact]
    public void Fixture_module_exports_and_envelope()
    {
        var api = BuildFixture();
        Assert.Equal(SchemaFamily.Version, api.SchemaVersion);
        Assert.Equal("25966348", api.BuildId);
        Assert.Equal("windows-x86_64", api.Platform);
        Assert.Equal("cs_script/point_script", api.Module);
        Assert.Equal(CsScriptApiEmitter.DeclarationPath, api.SourcePath);
        Assert.Equal(64, api.SourceSha256.Length);
        Assert.Equal("es2022", api.EsTarget);
        Assert.Equal(CsScriptApiEmitter.TsconfigPath, api.TsconfigPath);
        Assert.Equal("Instance:IPointScript,Console:IConsole,GameRules:IGameRules", string.Join(",", api.Exports.Select(e => e.Name + ":" + e.Type)));
        Assert.Equal("IPointScript", string.Join(",", api.Exports[0].ReferencedTypes));
        Assert.StartsWith("`\"cs_script/point_script\"` is the module provided", api.ModuleDoc, StringComparison.Ordinal);
        Assert.StartsWith("This file, `point_script.d.ts`, documents", api.FileDoc, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_IPointScript_hooks_overloads_and_tags()
    {
        var api = BuildFixture();
        var ips = Type(api, "IPointScript");
        Assert.Equal("interface", ips.Kind);
        Assert.False(ips.Exported);

        // Every On* registration in the shipped file (including the three deprecated ones).
        var hooks = ips.Members.Where(m => m.Name.StartsWith("On", StringComparison.Ordinal))
            .Select(m => m.Name).Distinct().ToList();
        Assert.Equal(43, hooks.Count);
        Assert.Contains("OnRoundEnd", hooks);
        Assert.Contains("OnModifyPlayerDamage", hooks);

        var debugLine = ips.Members.Where(m => m.Name == "DebugLine").ToList();
        Assert.Equal(2, debugLine.Count);
        Assert.Equal(0u, debugLine[0].OverloadIndex);
        Assert.False(debugLine[0].Deprecated);
        Assert.Equal(1u, debugLine[1].OverloadIndex);
        Assert.True(debugLine[1].Deprecated);
        Assert.Equal("This overload will be removed in a future update", debugLine[1].DeprecationNote);
        Assert.Equal(
            "DebugLine(config: { start: Vector, end: Vector, duration?: number, color?: ColorArg }): void",
            debugLine[0].SignatureText);

        var reload = Assert.Single(ips.Members, m => m.Name == "OnScriptReload");
        Assert.Equal("T", string.Join(",", reload.TypeParams));
        Assert.Equal("{ before?: () => T, after?: (memory: T) => void }", Assert.Single(reload.Params).TypeText);

        var modify = Assert.Single(ips.Members, m => m.Name == "OnModifyPlayerDamage");
        Assert.Equal(
            "(event: ModifyPlayerDamageEvent) => ModfiyPlayerDamageResult | void",
            Assert.Single(modify.Params).TypeText);
        Assert.StartsWith("Return `{ damage: N }` to modify", modify.ParamDocs["callback"], StringComparison.Ordinal);
        Assert.Equal("ModfiyPlayerDamageResult,ModifyPlayerDamageEvent", string.Join(",", modify.ReferencedTypes));

        var precache = Assert.Single(ips.Members, m => m.Name == "Precache");
        Assert.True(precache.Experimental);
        Assert.StartsWith("This feature is experimental", precache.ExperimentalNote, StringComparison.Ordinal);
        Assert.Equal("CSPrecache | undefined", precache.ReturnTypeText);

        var sphere = Assert.Single(ips.Members, m => m.Name == "TraceSphere");
        Assert.Equal("{ radius: number } & BaseTraceConfig", Assert.Single(sphere.Params).TypeText);

        // A member whose JSDoc is separated from it by a blank line still gets it.
        var rounds = Assert.Single(Type(api, "IGameRules").Members, m => m.Name == "GetRoundsPlayed");
        Assert.Equal("Get the number of rounds played in the current game.", rounds.Doc);

        // The legacy no-semicolon members parse like the rest.
        Assert.Equal("void", Assert.Single(ips.Members, m => m.Name == "OnPlayerReset").ReturnTypeText);
    }

    [Fact]
    public void Fixture_properties_examples_defaults_and_aliases()
    {
        var api = BuildFixture();
        var bullet = Type(api, "BulletTrace");
        Assert.Equal(4, bullet.Examples.Count);
        Assert.Equal("{damage:36, rangeModifier:.98, penetration:2} // AK47", bullet.Examples[2]);
        var damage = Assert.Single(bullet.Members, m => m.Name == "damage");
        Assert.Equal("property", damage.Kind);
        Assert.True(damage.Optional);
        Assert.Equal("number", damage.TypeText);
        Assert.Equal("damage?: number", damage.SignatureText);
        Assert.Equal("100", damage.DefaultValue);
        Assert.Equal("The starting damage value of the bullet. This will reduce as it travels through the air and penetrates solids.", damage.Doc);

        var input = Type(api, "InputValue");
        Assert.Equal("type_alias", input.Kind);
        Assert.Equal("boolean | number | string | Vector | Color | undefined", input.TypeText);
        Assert.Equal("Color,Vector", string.Join(",", input.ReferencedTypes));
        Assert.Equal("{ x: number, y: number, z: number }", Type(api, "Vector").TypeText);

        var entity = Type(api, "Entity");
        Assert.Equal("class", entity.Kind);
        Assert.True(entity.Exported);
        Assert.Equal("Entity", string.Join(",", Type(api, "BaseModelEntity").Extends));
        var teleport = entity.Members.Where(m => m.Name == "Teleport").ToList();
        Assert.Equal(2, teleport.Count);
        Assert.Equal(
            "Teleport(newPosition: Vector | null, newAngles: QAngle | null, newVelocity: Vector | null): void",
            teleport[1].SignatureText);
    }

    [Fact]
    public void Fixture_enums_resolve_to_integers()
    {
        var api = BuildFixture();
        var flags = Enum(api, "CSDamageFlags");
        var armor = Assert.Single(flags.Members, m => m.Name == "IGNORE_ARMOR");
        Assert.Equal(64, armor.Value);
        Assert.Equal("1 << 6", armor.InitializerText);

        var reason = Enum(api, "CSRoundEndReason");
        Assert.Equal(-1, reason.Members[0].Value);
        Assert.Equal("-1", reason.Members[0].InitializerText);
        Assert.Equal("IN_PROGRESS", reason.Members[1].Name);
        Assert.Equal(0, reason.Members[1].Value);
        Assert.Equal("", reason.Members[1].InitializerText);

        var weapon = Enum(api, "CSWeaponType");
        Assert.Equal(11, Assert.Single(weapon.Members, m => m.Name == "STACKABLEITEM").Value);   // "// Healthshot" ignored
        Assert.Equal(12, weapon.Members[^1].Value);

        var money = Enum(api, "CSTeamMoneyReason");
        Assert.Equal("cash_team_terrorist_win_bomb", Assert.Single(money.Members, m => m.Name == "TERRORIST_WIN_BOMB").Doc);

        var legacy = Enum(api, "CSDamageType");
        Assert.Empty(legacy.Members);
        Assert.True(legacy.Deprecated);
    }

    [Fact]
    public void Types_and_enums_are_ordinal_sorted()
    {
        var api = BuildFixture();
        var names = api.Types_.Select(t => t.Name).ToList();
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
        var enums = api.Enums.Select(e => e.Name).ToList();
        Assert.Equal(enums.OrderBy(n => n, StringComparer.Ordinal), enums);
    }

    [Fact]
    public void Emit_is_deterministic_canonical_and_reports_sources()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cs2-csscript-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var emitter = new CsScriptApiEmitter(SchemaFamily.Version, "25966348", "windows-x86_64");
            var tree = new ToolsContentTree(FixtureRoot);
            var read = emitter.Emit(tree, Path.Combine(dir, "a.json"));
            emitter.Emit(tree, Path.Combine(dir, "b.json"));
            var a = File.ReadAllBytes(Path.Combine(dir, "a.json"));
            Assert.Equal(a, File.ReadAllBytes(Path.Combine(dir, "b.json")));
            Assert.False(a.Length >= 3 && a[0] == 0xEF && a[1] == 0xBB && a[2] == 0xBF);
            Assert.DoesNotContain((byte)'\r', a);
            Assert.Equal(CsScriptApiEmitter.DeclarationPath + "," + CsScriptApiEmitter.TsconfigPath, string.Join(",", read));

            // Strict round-trip through the contract.
            var parsed = new JsonParser(JsonParser.Settings.Default).Parse<CsScriptApi>(File.ReadAllText(Path.Combine(dir, "a.json")));
            Assert.Equal(BuildFixture(), parsed);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Inline_syntax_is_normalised()
    {
        var api = BuildInline("""
            declare module "m" {
                export interface A<T extends B = B> extends B, C { m?<U>(...rest: U[]): Array<T>; readonly p: "x" | 'y' | -1; q: [B, C][]; r: B["k"]; }
                interface B {}
                interface C {}
                export const enum E { X = 0x10, Y = X | 1, Z = E.Y << 1, W }
            }
            """);
        var a = Type(api, "A");
        Assert.Equal("T extends B = B", string.Join(",", a.TypeParams));
        Assert.Equal("B,C", string.Join(",", a.Extends));
        Assert.Equal("m?<U>(...rest: U[]): Array<T>", a.Members[0].SignatureText);
        Assert.True(a.Members[0].Optional);
        Assert.True(a.Members[0].Params[0].Rest);
        Assert.Equal("readonly p: \"x\" | \"y\" | -1", a.Members[1].SignatureText);
        Assert.Equal("readonly", string.Join(",", a.Members[1].Modifiers));
        Assert.Equal("r: B[\"k\"]", a.Members[3].SignatureText);
        var e = Enum(api, "E");
        Assert.Equal("16,17,34,35", string.Join(",", e.Members.Select(m => m.Value)));
        Assert.Equal("E.Y << 1", e.Members[2].InitializerText);
        Assert.Equal("", api.EsTarget);
        Assert.Equal("", api.TsconfigPath);
    }

    [Theory]
    [InlineData("declare module \"m\" { interface A { [k: string]: number } }", "index signatures")]
    [InlineData("declare module \"m\" { enum E { A = \"a\" } }", "string initializer")]
    [InlineData("declare module \"m\" { enum E { A = B } }", "not an earlier member")]
    [InlineData("declare module \"m\" { function f(): void; }", "unsupported declaration")]
    [InlineData("declare module \"m\" { interface A { x: `t` } }", "template literal")]
    [InlineData("declare module \"m\" { interface A { x: number }", "unterminated")]
    [InlineData("export const x: number;", "declare module")]
    [InlineData("declare module \"m\" { interface A {} class A {} }", "more than once")]
    [InlineData("declare module \"m\" { }", "no types or enums")]
    public void Unsupported_or_malformed_declarations_fail_loud(string dts, string expected)
    {
        var ex = Assert.Throws<InvalidDataException>(() => BuildInline(dts));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_failure_names_file_and_line()
    {
        var ex = Assert.Throws<InvalidDataException>(() => BuildInline("declare module \"m\" {\n  interface A {\n    [k: string]: number\n  }\n}"));
        Assert.Contains(CsScriptApiEmitter.DeclarationPath + ":3:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_tsconfig_fails_loud()
    {
        var ex = Assert.Throws<InvalidDataException>(() => BuildInline("declare module \"m\" { interface A {} }", "{ not json"));
        Assert.Contains("tsconfig.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_declaration_file_fails_loud_and_writes_nothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2-csscript-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var tree = new ToolsContentTree(root);
            Assert.False(CsScriptApiEmitter.HasSource(tree));
            var outPath = Path.Combine(root, "cs_script_api.json");
            Assert.Throws<InvalidDataException>(() =>
                new CsScriptApiEmitter(SchemaFamily.Version, "1", "windows-x86_64").Emit(tree, outPath));
            Assert.False(File.Exists(outPath));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void JsDoc_tag_boundaries_follow_whitespace_rule()
    {
        var doc = JsDoc.Parse("*\n * Mail CSGOTeamFeedback@valvesoftware.com please. @default 5\n * @param x the x\n * @custom keep me\n ");
        Assert.Equal("Mail CSGOTeamFeedback@valvesoftware.com please.", doc.Description);
        Assert.Equal("default,param,custom", string.Join(",", doc.Tags.Select(t => t.Name)));
        Assert.Equal("5", doc.Tags[0].Text);
        Assert.Equal("x the x", doc.Tags[1].Text);
    }
}
