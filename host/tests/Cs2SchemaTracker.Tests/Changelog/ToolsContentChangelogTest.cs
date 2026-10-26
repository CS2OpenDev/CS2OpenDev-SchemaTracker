// The optional tools-content changelog families: cs_script_api and entity_io.
//
// We assert, through the real DiffCommand:
//   * both families are appended after the five binary families (in that order) when BOTH sets
//     carry the artifact, and are absent when only one side does — the first set to carry an
//     artifact must not list its whole content as "added";
//   * cs_script_api keys: "export:<Name>", "<Type>", "<Type>.<member>#<overload>", "<Enum>",
//     "<Enum>.<MEMBER>"; a signature change, a deprecation and an enum value change surface as
//     FieldChange rows named signature / deprecated / value;
//   * entity_io keys by classname with keyvalue:/input:/output: fields, "" marking absence;
//   * determinism: two diffs are byte-identical.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Cli;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Changelog;

public sealed class ToolsContentChangelogTest
{
    private const string Platform = "windows-x86_64";

    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static void InRoot(Action<string> body)
    {
        var work = Path.Combine(Path.GetTempPath(), "tools-changelog-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        try
        { body(root); }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    private static Schemas.CsScriptApi Api(string build, bool newer)
    {
        var api = new Schemas.CsScriptApi { SchemaVersion = "0.13.0", BuildId = build, Platform = Platform, Module = "cs_script/point_script" };
        api.Exports.Add(new Schemas.CsScriptExport { Name = "Instance", Type = "IPointScript" });
        var ips = new Schemas.CsScriptType { Name = "IPointScript", Kind = "interface" };
        ips.Members.Add(new Schemas.CsScriptMember
        {
            Name = "SetNextThink",
            Kind = "method",
            SignatureText = newer ? "SetNextThink(time: number, x?: number): void" : "SetNextThink(time: number): void",
        });
        ips.Members.Add(new Schemas.CsScriptMember { Name = "Msg", Kind = "method", SignatureText = "Msg(text: any): void", Deprecated = newer });
        if (newer)
            ips.Members.Add(new Schemas.CsScriptMember { Name = "OnNewHook", Kind = "method", SignatureText = "OnNewHook(callback: () => void): void" });
        api.Types_.Add(ips);
        var flags = new Schemas.CsScriptEnum { Name = "CSDamageFlags", Exported = true };
        flags.Members.Add(new Schemas.CsScriptEnumMember { Name = "IGNORE_ARMOR", Value = newer ? 128 : 64 });
        api.Enums.Add(flags);
        return api;
    }

    private static Schemas.EntityIo Io(string build, bool newer)
    {
        var io = new Schemas.EntityIo { SchemaVersion = "0.13.0", BuildId = build, Platform = Platform, Root = "game/csgo/csgo.fgd" };
        var ps = new Schemas.EntityClass { Name = "point_script", ClassType = "PointClass" };
        ps.Keyvalues.Add(new Schemas.EntityKeyValue { Name = "cs_script", Type = "resource:vjs" });
        ps.Inputs.Add(new Schemas.EntityIoPort { Name = "RunScriptInput", ParamType = "string" });
        if (newer)
            ps.Outputs.Add(new Schemas.EntityIoPort { Name = "OnScriptError", ParamType = "string" });
        io.Classes.Add(ps);
        if (!newer)
            io.Classes.Add(new Schemas.EntityClass { Name = "func_gone", ClassType = "SolidClass" });
        return io;
    }

    private static void MakeSet(string root, string build, bool newer, bool withTools)
    {
        ChangelogTestSets.MakeSet(root, build, Platform, ("server.dll", "CBaseEntity"));
        if (!withTools)
            return;
        var dir = Path.Combine(root, build, Platform);
        ChangelogTestSets.Write(Api(build, newer), Path.Combine(dir, "cs_script_api.json"));
        ChangelogTestSets.Write(Io(build, newer), Path.Combine(dir, "entity_io.json"));
    }

    private static Schemas.BuildChangelog Diff(string root)
    {
        var code = DiffCommand.Run(
            new[] { "--from", "1000", "--to", "1001", "--platform", Platform, "--artifacts", root },
            artifactsRootOverride: root);
        Assert.Equal(0, code);
        return Parser.Parse<Schemas.BuildChangelog>(
            File.ReadAllText(Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName)));
    }

    [Fact]
    public void Both_families_follow_the_binary_families_when_both_sides_carry_them()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", newer: false, withTools: true);
            MakeSet(root, "1001", newer: true, withTools: true);
            var cl = Diff(root);
            Assert.Equal(
                string.Join(",", BuildChangelogEmitter.FamilyOrder) + ",cs_script_api,entity_io",
                string.Join(",", cl.Families.Select(f => f.Family)));
        });
    }

    [Fact]
    public void Families_are_absent_when_only_the_newer_side_carries_the_artifacts()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", newer: false, withTools: false);
            MakeSet(root, "1001", newer: true, withTools: true);
            var cl = Diff(root);
            Assert.Equal(string.Join(",", BuildChangelogEmitter.FamilyOrder), string.Join(",", cl.Families.Select(f => f.Family)));
        });
    }

    [Fact]
    public void Cs_script_api_rows_are_keyed_per_element()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", newer: false, withTools: true);
            MakeSet(root, "1001", newer: true, withTools: true);
            var api = Diff(root).Families.Single(f => f.Family == BuildChangelogEmitter.CsScriptApiFamily);

            Assert.Equal("IPointScript.OnNewHook#0", Assert.Single(api.Added));
            Assert.Empty(api.Removed);

            var think = Assert.Single(api.Changed, c => c.Name == "IPointScript.SetNextThink#0");
            var sig = Assert.Single(think.Fields);
            Assert.Equal("signature", sig.Field);
            Assert.Equal("SetNextThink(time: number): void", sig.OldValue);

            var msg = Assert.Single(api.Changed, c => c.Name == "IPointScript.Msg#0");
            Assert.Equal("deprecated", Assert.Single(msg.Fields).Field);

            var armor = Assert.Single(api.Changed, c => c.Name == "CSDamageFlags.IGNORE_ARMOR");
            var v = Assert.Single(armor.Fields);
            Assert.Equal("value", v.Field);
            Assert.Equal("64", v.OldValue);
            Assert.Equal("128", v.NewValue);
        });
    }

    [Fact]
    public void Entity_io_rows_are_keyed_by_classname()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", newer: false, withTools: true);
            MakeSet(root, "1001", newer: true, withTools: true);
            var io = Diff(root).Families.Single(f => f.Family == BuildChangelogEmitter.EntityIoFamily);
            Assert.Empty(io.Added);
            Assert.Equal("func_gone", Assert.Single(io.Removed));
            var ps = Assert.Single(io.Changed);
            Assert.Equal("point_script", ps.Name);
            var output = Assert.Single(ps.Fields);
            Assert.Equal("output:OnScriptError", output.Field);
            Assert.Equal("", output.OldValue);
            Assert.Equal("string", output.NewValue);
        });
    }

    [Fact]
    public void Diff_is_byte_identical_across_runs()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", newer: false, withTools: true);
            MakeSet(root, "1001", newer: true, withTools: true);
            Diff(root);
            var path = Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName);
            var first = File.ReadAllBytes(path);
            Diff(root);
            Assert.Equal(first, File.ReadAllBytes(path));
        });
    }
}
