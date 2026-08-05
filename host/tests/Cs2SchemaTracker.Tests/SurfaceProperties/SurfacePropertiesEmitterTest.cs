// tests — surface-property extraction from the KV3-TEXT scripts/surfaceproperties_*.txt
// family inside a content-depot VPK.
//
// Every fixture is a hand-constructed in-memory VPK carrying fake surfaceproperties_*.txt files.
// We assert:
//   * the documented (name, source_file) row model — the same material in two files yields two
//     rows distinguished by source_file; each row's properties are the file's per-material scalars
// minus surfacePropertyName, sorted by name Ordinal;
//   * the typed-resource KV3 value form (resource:"…") carries the inner string verbatim;
//   * deterministic byte-identical output across two runs;
// * fail-loud on malformed KV3, no surfaceproperties file, a non-array
//     SurfacePropertiesList, a missing surfacePropertyName, and zero surfaces — with NO output.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.SurfaceProperties;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.SurfaceProperties;

public class SurfacePropertiesEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    private static readonly string[] ExpectedMetalPropNames = { "climbable", "gamematerial", "jumpfactor" };

    private const string Header =
        "<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->\n";

    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private sealed record FileSpec(string Path, string Ext, string Name, byte[] Body);

    private static byte[] BuildEmbeddedVpk(IReadOnlyList<FileSpec> files)
    {
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();
        var offsets = new Dictionary<FileSpec, uint>();
        foreach (var f in files)
        { offsets[f] = (uint)dataSection.Length; dataSection.Write(f.Body); }

        foreach (var byExt in files.GroupBy(f => f.Ext))
        {
            WriteCString(tree, byExt.Key);
            foreach (var byPath in byExt.GroupBy(f => f.Path))
            {
                WriteCString(tree, byPath.Key);
                foreach (var f in byPath)
                {
                    WriteCString(tree, f.Name);
                    WriteU32(tree, Crc32(f.Body));
                    WriteU16(tree, 0);
                    WriteU16(tree, Embedded);
                    WriteU32(tree, offsets[f]);
                    WriteU32(tree, (uint)f.Body.Length);
                    WriteU16(tree, Terminator);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        byte[] treeBytes = tree.ToArray();
        byte[] dataBytes = dataSection.ToArray();
        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, 2u);
        WriteU32(ms, (uint)treeBytes.Length);
        WriteU32(ms, (uint)dataBytes.Length);
        WriteU32(ms, 0);
        WriteU32(ms, 0);
        WriteU32(ms, 0);
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }

    private static void WriteCString(Stream s, string v) { s.Write(Encoding.UTF8.GetBytes(v)); s.WriteByte(0); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }

    private static VpkArchive ArchiveWith(params (string name, string body)[] files) =>
        VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(
            files.Select(f => new FileSpec("scripts", "txt", f.name, Encoding.UTF8.GetBytes(f.body))).ToList()));

    private static SurfacePropertiesEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "surfaces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string GameTxt = Header +
        """
        {
            SurfacePropertiesList =
            [
                { surfacePropertyName = "metal"  gamematerial = "M" jumpfactor = 1.0 climbable = false },
                { surfacePropertyName = "default" gamematerial = "C" bulletPenetrationDamageModifier = 0.5 },
            ]
        }
        """;

    private const string ImpactTxt = Header +
        """
        {
            SurfacePropertiesList =
            [
                { surfacePropertyName = "default" effect = resource:"particles/impact_concrete.vpcf" impactDecalName = "Impact.Concrete" },
            ]
        }
        """;

    [Fact]
    public void Maps_PerFile_Rows_And_TypedResource()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            NewEmitter().Emit(
                ArchiveWith(("surfaceproperties_game", GameTxt), ("surfaceproperties_impact_effects", ImpactTxt)),
                outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "surface_properties.json must not have a UTF-8 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Assert.Equal(BuildId, root.GetProperty("buildId").GetString());

            var surfaces = root.GetProperty("surfaces");
            // 2 materials in game + 1 in impact = 3 rows, sorted by (name, source_file) Ordinal:
            //   ("default","..._game.txt"), ("default","..._impact_effects.txt"), ("metal","..._game.txt")
            Assert.Equal(3, surfaces.GetArrayLength());

            Assert.Equal("default", surfaces[0].GetProperty("name").GetString());
            Assert.Equal("surfaceproperties_game.txt", surfaces[0].GetProperty("sourceFile").GetString());

            Assert.Equal("default", surfaces[1].GetProperty("name").GetString());
            Assert.Equal("surfaceproperties_impact_effects.txt", surfaces[1].GetProperty("sourceFile").GetString());

            Assert.Equal("metal", surfaces[2].GetProperty("name").GetString());
            Assert.Equal("surfaceproperties_game.txt", surfaces[2].GetProperty("sourceFile").GetString());

            // metal's properties sorted by name Ordinal; surfacePropertyName is NOT a property.
            var metalProps = surfaces[2].GetProperty("properties");
            var names = metalProps.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
            Assert.Equal(ExpectedMetalPropNames, names);
            Assert.DoesNotContain("surfacePropertyName", names);
            // verbatim scalar renderings (int-ish vs bool vs string).
            var metalMap = metalProps.EnumerateArray()
                .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("value").GetString());
            Assert.Equal("M", metalMap["gamematerial"]);
            Assert.Equal("1", metalMap["jumpfactor"]);   // 1.0 renders as integer "1"
            Assert.Equal("false", metalMap["climbable"]);

            // typed-resource value carries the inner string verbatim.
            var impactProps = surfaces[1].GetProperty("properties").EnumerateArray()
                .ToDictionary(p => p.GetProperty("name").GetString()!, p => p.GetProperty("value").GetString());
            Assert.Equal("particles/impact_concrete.vpcf", impactProps["effect"]);
            Assert.Equal("Impact.Concrete", impactProps["impactDecalName"]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Output_Is_Deterministic_Byte_Identical()
    {
        var dir = NewWorkDir();
        try
        {
            var a = Path.Combine(dir, "a.json");
            var b = Path.Combine(dir, "b.json");
            NewEmitter().Emit(ArchiveWith(("surfaceproperties_game", GameTxt), ("surfaceproperties_impact_effects", ImpactTxt)), a);
            NewEmitter().Emit(ArchiveWith(("surfaceproperties_game", GameTxt), ("surfaceproperties_impact_effects", ImpactTxt)), b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_NoSurfaceFile()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(new List<FileSpec>
            {
                new("scripts", "txt", "unrelated", Encoding.UTF8.GetBytes("{}")),
            }));
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_MalformedKv3()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            Assert.Throws<InvalidDataException>(() =>
                NewEmitter().Emit(ArchiveWith(("surfaceproperties_game", Header + "{ SurfacePropertiesList = [ {")), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_MissingSurfacePropertyName()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            string bad = Header + """{ SurfacePropertiesList = [ { gamematerial = "M" } ] }""";
            Assert.Throws<InvalidDataException>(() =>
                NewEmitter().Emit(ArchiveWith(("surfaceproperties_game", bad)), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_ListNotArray()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            string bad = Header + """{ SurfacePropertiesList = "oops" }""";
            Assert.Throws<InvalidDataException>(() =>
                NewEmitter().Emit(ArchiveWith(("surfaceproperties_game", bad)), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ERA-FAITHFUL: surfaceproperties_footsteps.txt nests SurfacePropertiesList one level under a
    // per-actor key (ct_player / t_player) in ALL eras — the original emitter only handled a
    // top-level list and fail-louded ("has no 'SurfacePropertiesList' array") on EVERY build with
    // footsteps (the confirmed build-13387786 failure). The emitter now parses the nested shape and
    // records the actor scope in Surface.scope so the same-named materials stay distinct rows.
    private const string FootstepsTxt = Header +
        """
        {
            ct_player =
            {
                SurfacePropertiesList =
                [
                    { surfacePropertyName = "default" walkleft = "CT_Default.StepLeft" },
                ]
            }
            t_player =
            {
                SurfacePropertiesList =
                [
                    { surfacePropertyName = "default" walkleft = "T_Default.StepLeft" },
                ]
            }
        }
        """;

    [Fact]
    public void Footsteps_NestedPerActor_RecordsScope()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            NewEmitter().Emit(ArchiveWith(("surfaceproperties_footsteps", FootstepsTxt)), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var surfaces = doc.RootElement.GetProperty("surfaces");
            Assert.Equal(2, surfaces.GetArrayLength());

            // Both rows are "default" from the same source file, distinguished by Surface.scope
            // (the per-actor key; Ordinal-sorted: 'ct_player' < 't_player'). source_file is the
            // plain base filename — the scope is NOT folded into it.
            Assert.Equal("default", surfaces[0].GetProperty("name").GetString());
            Assert.Equal("surfaceproperties_footsteps.txt", surfaces[0].GetProperty("sourceFile").GetString());
            Assert.Equal("ct_player", surfaces[0].GetProperty("scope").GetString());
            Assert.Equal("default", surfaces[1].GetProperty("name").GetString());
            Assert.Equal("surfaceproperties_footsteps.txt", surfaces[1].GetProperty("sourceFile").GetString());
            Assert.Equal("t_player", surfaces[1].GetProperty("scope").GetString());

            // The faction-specific footstep value is preserved per scope.
            Assert.Equal("CT_Default.StepLeft",
                surfaces[0].GetProperty("properties")[0].GetProperty("value").GetString());
            Assert.Equal("T_Default.StepLeft",
                surfaces[1].GetProperty("properties")[0].GetProperty("value").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A top-level (unscoped) SurfacePropertiesList yields scope = "" (proto3 default).
    [Fact]
    public void TopLevel_List_HasEmptyScope()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            NewEmitter().Emit(ArchiveWith(("surfaceproperties_game", GameTxt)), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            foreach (var s in doc.RootElement.GetProperty("surfaces").EnumerateArray())
            {
                Assert.Equal("", s.GetProperty("scope").GetString());
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A present file with NEITHER a top-level NOR a nested SurfacePropertiesList is a structural
    // surprise (the file IS shipped) ⇒ still fail-loud, not a graceful omission.
    [Fact]
    public void FailLoud_NoListAnywhere()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "surface_properties.json");
            string bad = Header + """{ ct_player = { something = "x" } }""";
            Assert.Throws<InvalidDataException>(() =>
                NewEmitter().Emit(ArchiveWith(("surfaceproperties_footsteps", bad)), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // HasSource: a VPK with a surfaceproperties_*.txt has a source; one without genuinely lacks it.
    [Fact]
    public void HasSource_TrueWhenPresent_FalseWhenGenuinelyAbsent()
    {
        Assert.True(SurfacePropertiesEmitter.HasSource(ArchiveWith(("surfaceproperties_game", GameTxt))));

        var none = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(new List<FileSpec>
        {
            new("scripts", "txt", "propdata", Encoding.UTF8.GetBytes("\"x\" {}")),
        }));
        Assert.False(SurfacePropertiesEmitter.HasSource(none));
    }
}
