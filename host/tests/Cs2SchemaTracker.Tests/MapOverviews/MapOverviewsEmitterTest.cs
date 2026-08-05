// tests — per-map radar/overview metadata extraction from resource/overviews/*.txt (KV1)
// inside a content-depot VPK.
//
// We assert:
//   * each overview file's top-level block maps to one MapOverview (name = block key); the
//     well-known radar fields populate the typed fields; the long tail lands in the sorted
//     properties bag; map_names is the Ordinal-sorted inventory;
// * maps sorted by name Ordinal;
//   * determinism (byte-identical across two runs);
// * fail-loud on no overview file, malformed KV1, a duplicate map name, and a file
//     with no top-level block — with NO output bytes.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.MapOverviews;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.MapOverviews;

public class MapOverviewsEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    private static readonly string[] ExpectedMapNames = { "de_dust2", "de_mirage" };

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
            files.Select(f => new FileSpec("resource/overviews", "txt", f.name, Encoding.UTF8.GetBytes(f.body))).ToList()));

    private static MapOverviewsEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "overviews-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private const string Dust2 =
        """
        "de_dust2"
        {
            "material" "overviews/de_dust2_v2"
            "pos_x" "-2476"
            "pos_y" "3239"
            "scale" "4.4"
            "rotate" "1"
            "zoom" "1.1"
            "inset_left" "0.0"
            "bombA_x" "0.80"
            "CTSpawn_x" "0.62"
        }
        """;

    private const string Mirage =
        """
        "de_mirage" { "material" "overviews/de_mirage" "pos_x" "-3230" "scale" "5.0" }
        """;

    [Fact]
    public void Maps_WellKnownFields_And_LongTail()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "map_overviews.json");
            // de_mirage file given first to prove sort by name.
            NewEmitter().Emit(ArchiveWith(("de_mirage", Mirage), ("de_dust2", Dust2)), outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());

            var maps = root.GetProperty("maps");
            Assert.Equal(2, maps.GetArrayLength());
            // sorted by name Ordinal: de_dust2 < de_mirage.
            Assert.Equal("de_dust2", maps[0].GetProperty("name").GetString());
            Assert.Equal("de_mirage", maps[1].GetProperty("name").GetString());

            var d2 = maps[0];
            Assert.Equal("overviews/de_dust2_v2", d2.GetProperty("material").GetString());
            Assert.Equal("-2476", d2.GetProperty("posX").GetString());
            Assert.Equal("3239", d2.GetProperty("posY").GetString());
            Assert.Equal("4.4", d2.GetProperty("scale").GetString());
            Assert.Equal("1", d2.GetProperty("rotate").GetString());
            Assert.Equal("1.1", d2.GetProperty("zoom").GetString());
            Assert.Equal("0.80", d2.GetProperty("bombAX").GetString());
            Assert.Equal("0.62", d2.GetProperty("ctSpawnX").GetString());

            // long-tail property bag carries inset_left (not a well-known field).
            var props = d2.GetProperty("properties");
            Assert.Equal(1, props.GetArrayLength());
            Assert.Equal("inset_left", props[0].GetProperty("name").GetString());
            Assert.Equal("0.0", props[0].GetProperty("value").GetString());

            // map_names inventory, Ordinal-sorted.
            var names = root.GetProperty("mapNames").EnumerateArray().Select(n => n.GetString()).ToList();
            Assert.Equal(ExpectedMapNames, names);
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
            NewEmitter().Emit(ArchiveWith(("de_mirage", Mirage), ("de_dust2", Dust2)), a);
            NewEmitter().Emit(ArchiveWith(("de_mirage", Mirage), ("de_dust2", Dust2)), b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_NoOverviewFile()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "map_overviews.json");
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(new List<FileSpec>
            {
                new("resource", "txt", "csgo_english", Encoding.UTF8.GetBytes("\"lang\" {}")),
            }));
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
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
            var outPath = Path.Combine(dir, "map_overviews.json");
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(ArchiveWith(("de_x", "\"de_x\" {")), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ERA-FAITHFUL: Valve ships variant overview files whose INNER block key collides while the
    // file STEM stays unique (de_inferno.txt + de_inferno_s2.txt BOTH inner-key "de_inferno";
    // de_overpass.txt + de_overpass_2v2.txt BOTH "de_overpass" — confirmed in build 13387786). The
    // emitter now keys the row on the unique file stem, so BOTH radar definitions are preserved
    // (the old fail-loud-on-duplicate killed the whole extract and would have dropped one).
    [Fact]
    public void VariantFiles_SameInnerKey_AreDistinctByFileStem()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "map_overviews.json");
            NewEmitter().Emit(ArchiveWith(("de_inferno", "\"de_inferno\" { \"scale\" \"1\" }"),
                                          ("de_inferno_s2", "\"de_inferno\" { \"scale\" \"2\" }")), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var maps = doc.RootElement.GetProperty("maps");
            Assert.Equal(2, maps.GetArrayLength());
            // Row identity = the file stem, so both variants survive and are distinct.
            Assert.Equal("de_inferno", maps[0].GetProperty("name").GetString());
            Assert.Equal("de_inferno_s2", maps[1].GetProperty("name").GetString());
            Assert.Equal("1", maps[0].GetProperty("scale").GetString());
            Assert.Equal("2", maps[1].GetProperty("scale").GetString());
            // block_name carries the inner KV1 key only when it differs from the stem: "" for the
            // matching de_inferno.txt, "de_inferno" for the de_inferno_s2.txt variant.
            Assert.Equal("", maps[0].GetProperty("blockName").GetString());
            Assert.Equal("de_inferno", maps[1].GetProperty("blockName").GetString());

            var names = doc.RootElement.GetProperty("mapNames");
            Assert.Equal("de_inferno", names[0].GetString());
            Assert.Equal("de_inferno_s2", names[1].GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // HasSource: a VPK that ships at least one resource/overviews/*.txt has a source; one with none
    // genuinely lacks it (graceful-omission signal — the full extract omits rather than fail-louds).
    [Fact]
    public void HasSource_TrueWhenOverviewPresent_FalseWhenGenuinelyAbsent()
    {
        Assert.True(MapOverviewsEmitter.HasSource(ArchiveWith(("de_dust2", Dust2))));

        var noOverviews = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(new List<FileSpec>
        {
            new("scripts", "txt", "surfaceproperties_game", Encoding.UTF8.GetBytes("{}")),
        }));
        Assert.False(MapOverviewsEmitter.HasSource(noOverviews));
    }
}
