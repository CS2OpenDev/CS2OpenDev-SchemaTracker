// tests — breakable-prop + collision-group extraction from scripts/propdata.txt (KV1) +
// scripts/collision_properties.txt (KV3 text) inside a content-depot VPK.
//
// We assert:
//   * prop classes mapped from every top-level propdata block EXCEPT BreakableModels, each with a
//     sorted name->value bag; the BreakableModels block maps gib-group -> sorted model paths;
//   * collision groups mapped from the KV3 collision_properties array (name/description/
//     collision_group + interact_* lists in source order);
//   * determinism (byte-identical across two runs);
// * fail-loud on both sources absent, malformed KV1, malformed KV3, a duplicate prop
//     class id, and zero rows — with NO output bytes;
//   * an artifact with ONLY propdata (no collision file) or ONLY collision still emits.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.PropData;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.PropData;

public class PropDataEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    private static readonly string[] ExpectedWoodModels =
        { "models/Gibs/wood_gib01a.vmdl", "models/Gibs/wood_gib01b.vmdl" };

    private const string Kv3Header =
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

    private static VpkArchive ArchiveWith(string? propData, string? collision)
    {
        var files = new List<FileSpec>();
        if (propData is not null)
            files.Add(new("scripts", "txt", "propdata", Encoding.UTF8.GetBytes(propData)));
        if (collision is not null)
            files.Add(new("scripts", "txt", "collision_properties", Encoding.UTF8.GetBytes(collision)));
        if (files.Count == 0)
            files.Add(new("scripts", "txt", "unrelated", Encoding.UTF8.GetBytes("{}")));
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(files));
    }

    private static PropDataEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "propdata-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Out-of-order to exercise sorting; BreakableModels mixed among prop classes.
    private const string PropDataTxt =
        """
        "PropData.txt"
        {
            "Door.Standard" { "dmg.bullets" "1.0" "health" "1000" }
            "BreakableModels"
            {
                "WoodChunks" { "models/Gibs/wood_gib01b.vmdl" "1" "models/Gibs/wood_gib01a.vmdl" "1" }
            }
            "Cloth.Small" { "base" "Cloth.Base" "health" "30" }
        }
        """;

    private static readonly string CollisionKv3 = Kv3Header +
        """
        {
            collision_properties =
            [
                { name = "window" description = "win" collision_group = "ConditionallySolid" interact_as = [ "window" ] interact_with = [] interact_exclude = [] },
                { name = "default" description = "def" collision_group = "default" interact_as = [] interact_with = [] interact_exclude = [] },
            ]
        }
        """;

    [Fact]
    public void Maps_PropClasses_BreakableModels_And_CollisionGroups()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "prop_data.json");
            NewEmitter().Emit(ArchiveWith(PropDataTxt, CollisionKv3), outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());

            // prop_classes sorted by id Ordinal: "Cloth.Small" < "Door.Standard".
            var classes = root.GetProperty("propClasses");
            Assert.Equal(2, classes.GetArrayLength());
            Assert.Equal("Cloth.Small", classes[0].GetProperty("id").GetString());
            Assert.Equal("Door.Standard", classes[1].GetProperty("id").GetString());
            // Cloth.Small properties sorted by name: base < health.
            var clothProps = classes[0].GetProperty("properties");
            Assert.Equal("base", clothProps[0].GetProperty("name").GetString());
            Assert.Equal("Cloth.Base", clothProps[0].GetProperty("value").GetString());
            Assert.Equal("health", clothProps[1].GetProperty("name").GetString());
            Assert.Equal("30", clothProps[1].GetProperty("value").GetString());

            // breakable_models: WoodChunks, models sorted+deduped Ordinal.
            var bm = root.GetProperty("breakableModels");
            Assert.Equal(1, bm.GetArrayLength());
            Assert.Equal("WoodChunks", bm[0].GetProperty("id").GetString());
            var models = bm[0].GetProperty("models").EnumerateArray().Select(m => m.GetString()).ToList();
            Assert.Equal(ExpectedWoodModels, models);

            // collision_groups sorted by name Ordinal: default < window.
            var cg = root.GetProperty("collisionGroups");
            Assert.Equal(2, cg.GetArrayLength());
            Assert.Equal("default", cg[0].GetProperty("name").GetString());
            Assert.Equal("window", cg[1].GetProperty("name").GetString());
            Assert.Equal("ConditionallySolid", cg[1].GetProperty("collisionGroup").GetString());
            Assert.Equal("window", cg[1].GetProperty("interactAs")[0].GetString());
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
            NewEmitter().Emit(ArchiveWith(PropDataTxt, CollisionKv3), a);
            NewEmitter().Emit(ArchiveWith(PropDataTxt, CollisionKv3), b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OnlyPropData_Emits()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "prop_data.json");
            NewEmitter().Emit(ArchiveWith(PropDataTxt, null), outPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.True(doc.RootElement.GetProperty("propClasses").GetArrayLength() > 0);
            Assert.Equal(0, doc.RootElement.GetProperty("collisionGroups").GetArrayLength());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OnlyCollision_Emits()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "prop_data.json");
            NewEmitter().Emit(ArchiveWith(null, CollisionKv3), outPath);
            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.Equal(0, doc.RootElement.GetProperty("propClasses").GetArrayLength());
            Assert.True(doc.RootElement.GetProperty("collisionGroups").GetArrayLength() > 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_BothSourcesAbsent()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "prop_data.json");
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(ArchiveWith(null, null), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_DuplicatePropClass()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "prop_data.json");
            string dup = """
                "PropData.txt" { "Cloth.Small" { "health" "30" } "Cloth.Small" { "health" "40" } }
                """;
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(ArchiveWith(dup, null), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_MalformedKv1()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "prop_data.json");
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(ArchiveWith("\"PropData.txt\" { \"x\" {", null), outPath));
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
            var outPath = Path.Combine(dir, "prop_data.json");
            Assert.Throws<InvalidDataException>(() =>
                NewEmitter().Emit(ArchiveWith(null, Kv3Header + "{ collision_properties = [ {"), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
