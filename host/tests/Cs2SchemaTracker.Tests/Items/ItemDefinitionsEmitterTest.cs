// tests — economy item-definition extraction from scripts/items/items_game.txt KV1
// inside a content-depot VPK.
//
// Every fixture is a hand-constructed in-memory VPK (the VpkArchive's synthetic-fixture
// style, mirroring GameEventsEmitterTest) carrying a fake items_game.txt. We assert:
//   * each named table maps with the documented field mapping, RAW (prefab refs verbatim,
//     no inheritance resolution), "default" kept as is_default=true/def_index=0;
//   * deterministic byte-identical output across two runs, with def-index tables sorted
// NUMERIC and id tables sorted Ordinal regardless of source order;
// * fail-loud on malformed KV1, missing entry, missing/scalar `items`, a bad
//     def-index key, a duplicate def_index, and zero item definitions — with NO output bytes.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Items;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.Items;

public class ItemDefinitionsEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

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
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private sealed record FileSpec(string Path, string Ext, string Name, byte[] Body);

    private static byte[] BuildEmbeddedVpk(int version, IReadOnlyList<FileSpec> files)
    {
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();

        var offsets = new Dictionary<FileSpec, uint>();
        foreach (var f in files)
        {
            offsets[f] = (uint)dataSection.Length;
            dataSection.Write(f.Body);
        }

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
        WriteU32(ms, (uint)version);
        WriteU32(ms, (uint)treeBytes.Length);
        if (version == 2)
        {
            WriteU32(ms, (uint)dataBytes.Length);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
        }
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }

    private static void WriteCString(Stream s, string value) { s.Write(Encoding.UTF8.GetBytes(value)); s.WriteByte(0); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }

    // Build a VPK carrying a single items_game.txt with the given KV1 body, embedded in _dir.vpk.
    private static VpkArchive ArchiveWith(string itemsGameTxt) =>
        VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
        {
            new("scripts/items", "txt", "items_game", Encoding.UTF8.GetBytes(itemsGameTxt)),
        }));

    private static ItemDefinitionsEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "items-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A rich, deliberately out-of-order sample exercising every table + the field mapping.
    private const string Sample =
        """
        "items_game"
        {
            "items"
            {
                "7"       { "name" "ak47" "item_name" "#weapon_ak47" "item_description" "#desc_ak47" "prefab" "primary weapon" "item_type_name" "#type_rifle" "item_slot" "rifle" }
                "default" { "name" "default" "item_class" "default_class" }
                "1"       { "name" "deagle" "item_name" "#weapon_deagle" }
            }
            "prefabs"
            {
                "weapon_base"  { "item_class" "weapon" "item_slot" "rifle" "item_name" "#base" "item_type_name" "#t" }
                "ak_prefab"    { "prefab" "weapon_base valve" }
            }
            "paint_kits"
            {
                "5" { "name" "aa_fade" "description_tag" "#PaintKit_aa_fade_Tag" }
                "0" { "name" "default" "description_tag" "#none" }
            }
            "sticker_kits"
            {
                "2" { "name" "sticker_a" "item_name" "#sticker_a" "description_string" "#desc_a" }
            }
            "music_definitions"
            {
                "3" { "name" "music_x" "loc_name" "#music_x" }
            }
            "rarities"
            {
                "rare"   { "value" "3" "loc_key" "#rare" "loc_key_weapon" "#rare_w" }
                "common" { "value" "1" "loc_key" "#common" }
            }
            "qualities"
            {
                "unusual" { "value" "8" }
                "normal"  { "value" "0" }
            }
        }
        """;

    [Fact]
    public void Maps_Every_Table_With_Documented_Field_Mapping()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "item_definitions.json");
            NewEmitter().Emit(ArchiveWith(Sample), outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "item_definitions.json must not have a UTF-8 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Assert.Equal(BuildId, root.GetProperty("buildId").GetString());
            Assert.Equal(Platform, root.GetProperty("platform").GetString());

            // items sorted by def_index NUMERIC: default(0), deagle(1), ak47(7).
            var items = root.GetProperty("items");
            Assert.Equal(3, items.GetArrayLength());

            var def = items[0];
            // proto3 uint32 def_index: 0 is the default value; FormatDefaultValues emits it.
            Assert.Equal(0u, def.GetProperty("defIndex").GetUInt32());
            Assert.Equal("default", def.GetProperty("name").GetString());
            Assert.True(def.GetProperty("isDefault").GetBoolean());
            // classname present DIRECTLY on the item block (item_class).
            Assert.Equal("default_class", def.GetProperty("classname").GetString());

            var deagle = items[1];
            Assert.Equal(1u, deagle.GetProperty("defIndex").GetUInt32());
            Assert.Equal("deagle", deagle.GetProperty("name").GetString());
            Assert.Equal("#weapon_deagle", deagle.GetProperty("nameToken").GetString());
            // absent keys -> proto3-default "".
            Assert.Equal("", deagle.GetProperty("prefab").GetString());
            Assert.False(deagle.GetProperty("isDefault").GetBoolean());

            var ak = items[2];
            Assert.Equal(7u, ak.GetProperty("defIndex").GetUInt32());
            Assert.Equal("ak47", ak.GetProperty("name").GetString());
            Assert.Equal("#weapon_ak47", ak.GetProperty("nameToken").GetString());
            Assert.Equal("#desc_ak47", ak.GetProperty("descriptionToken").GetString());
            // prefab VERBATIM, space-separated, NOT tokenized/resolved.
            Assert.Equal("primary weapon", ak.GetProperty("prefab").GetString());
            Assert.Equal("#type_rifle", ak.GetProperty("itemTypeName").GetString());
            Assert.Equal("rifle", ak.GetProperty("itemSlot").GetString());
            // ak47 has no item_class on its own block -> classname "".
            Assert.Equal("", ak.GetProperty("classname").GetString());

            // prefabs sorted by id Ordinal: ak_prefab < weapon_base.
            var prefabs = root.GetProperty("prefabs");
            Assert.Equal(2, prefabs.GetArrayLength());
            Assert.Equal("ak_prefab", prefabs[0].GetProperty("id").GetString());
            // verbatim parent chain.
            Assert.Equal("weapon_base valve", prefabs[0].GetProperty("prefab").GetString());
            Assert.Equal("weapon_base", prefabs[1].GetProperty("id").GetString());
            Assert.Equal("weapon", prefabs[1].GetProperty("classname").GetString());
            Assert.Equal("rifle", prefabs[1].GetProperty("itemSlot").GetString());

            // paint_kits sorted by def_index NUMERIC: 0 then 5.
            var paint = root.GetProperty("paintKits");
            Assert.Equal(2, paint.GetArrayLength());
            Assert.Equal(0u, paint[0].GetProperty("defIndex").GetUInt32());
            Assert.Equal("default", paint[0].GetProperty("name").GetString());
            Assert.Equal(5u, paint[1].GetProperty("defIndex").GetUInt32());
            Assert.Equal("#PaintKit_aa_fade_Tag", paint[1].GetProperty("descriptionTag").GetString());

            var stickers = root.GetProperty("stickerKits");
            Assert.Equal(1, stickers.GetArrayLength());
            Assert.Equal(2u, stickers[0].GetProperty("defIndex").GetUInt32());
            Assert.Equal("#sticker_a", stickers[0].GetProperty("itemName").GetString());
            Assert.Equal("#desc_a", stickers[0].GetProperty("descriptionString").GetString());

            var music = root.GetProperty("musicDefinitions");
            Assert.Equal(1, music.GetArrayLength());
            Assert.Equal(3u, music[0].GetProperty("defIndex").GetUInt32());
            Assert.Equal("#music_x", music[0].GetProperty("locName").GetString());

            // rarities sorted by id Ordinal: common < rare.
            var rarities = root.GetProperty("rarities");
            Assert.Equal(2, rarities.GetArrayLength());
            Assert.Equal("common", rarities[0].GetProperty("id").GetString());
            Assert.Equal(1u, rarities[0].GetProperty("value").GetUInt32());
            Assert.Equal("rare", rarities[1].GetProperty("id").GetString());
            Assert.Equal("#rare_w", rarities[1].GetProperty("locKeyWeapon").GetString());

            // qualities sorted by id Ordinal: normal < unusual.
            var qualities = root.GetProperty("qualities");
            Assert.Equal(2, qualities.GetArrayLength());
            Assert.Equal("normal", qualities[0].GetProperty("id").GetString());
            Assert.Equal(0u, qualities[0].GetProperty("value").GetUInt32());
            Assert.Equal("unusual", qualities[1].GetProperty("id").GetString());
            Assert.Equal(8u, qualities[1].GetProperty("value").GetUInt32());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Optional_Tables_Absent_Yields_Empty_Repeated_Fields()
    {
        var dir = NewWorkDir();
        try
        {
            // Only the required `items` table present.
            const string minimal =
                """
                "items_game"
                {
                    "items" { "0" { "name" "default" } }
                }
                """;
            var outPath = Path.Combine(dir, "item_definitions.json");
            NewEmitter().Emit(ArchiveWith(minimal), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("items").GetArrayLength());
            Assert.Equal(0, root.GetProperty("prefabs").GetArrayLength());
            Assert.Equal(0, root.GetProperty("paintKits").GetArrayLength());
            Assert.Equal(0, root.GetProperty("stickerKits").GetArrayLength());
            Assert.Equal(0, root.GetProperty("musicDefinitions").GetArrayLength());
            Assert.Equal(0, root.GetProperty("rarities").GetArrayLength());
            Assert.Equal(0, root.GetProperty("qualities").GetArrayLength());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Duplicate_Scalar_Key_Last_Occurrence_Wins()
    {
        var dir = NewWorkDir();
        try
        {
            const string dupScalar =
                """
                "items_game"
                {
                    "items" { "0" { "name" "first" "name" "second" } }
                }
                """;
            var outPath = Path.Combine(dir, "item_definitions.json");
            NewEmitter().Emit(ArchiveWith(dupScalar), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.Equal("second",
                doc.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Two_Runs_Byte_Identical(int version)
    {
        var dir = NewWorkDir();
        try
        {
            var archiveBytes = BuildEmbeddedVpk(version, new List<FileSpec>
            {
                new("scripts/items", "txt", "items_game", Encoding.UTF8.GetBytes(Sample)),
            });
            var a = Path.Combine(dir, "a.json");
            var b = Path.Combine(dir, "b.json");
            NewEmitter().Emit(VpkArchive.Parse("pak01_dir.vpk", archiveBytes), a);
            NewEmitter().Emit(VpkArchive.Parse("pak01_dir.vpk", archiveBytes), b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- fail-loud paths ----

    [Fact]
    public void FailLoud_Missing_ItemsGame_Entry()
    {
        var dir = NewWorkDir();
        try
        {
            // A VPK with only an unrelated file -> no scripts/items/items_game.txt.
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
            {
                new("resource", "txt", "other", Encoding.ASCII.GetBytes("nope")),
            }));
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("items_game.txt", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Malformed_Kv1()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith("\"items_game\"\n{\n  \"items\"\n  {\n"); // unbalanced braces
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("KV1", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Items_Section()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith("\"items_game\" { \"prefabs\" { } }");
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("items", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Items_Scalar_Not_Block()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith("\"items_game\" { \"items\" \"oops\" }");
            var outPath = Path.Combine(dir, "item_definitions.json");
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Bad_DefIndex_Key()
    {
        var dir = NewWorkDir();
        try
        {
            // key "abc" is neither a non-negative integer nor "default".
            var archive = ArchiveWith("\"items_game\" { \"items\" { \"abc\" { \"name\" \"x\" } } }");
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("integer", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Negative_DefIndex_Key()
    {
        var dir = NewWorkDir();
        try
        {
            // "-1" must be rejected (NumberStyles.None forbids the sign).
            var archive = ArchiveWith("\"items_game\" { \"items\" { \"-1\" { \"name\" \"x\" } } }");
            var outPath = Path.Combine(dir, "item_definitions.json");
            Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Duplicate_DefIndex()
    {
        var dir = NewWorkDir();
        try
        {
            // "default" => def_index 0 collides with explicit "0".
            var archive = ArchiveWith(
                "\"items_game\" { \"items\" { \"default\" { \"name\" \"d\" } \"0\" { \"name\" \"z\" } } }");
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Zero_Items()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith("\"items_game\" { \"items\" { } }");
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("zero item definitions", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Vpk()
    {
        var dir = NewWorkDir();
        try
        {
            var missing = Path.Combine(dir, "does-not-exist_dir.vpk");
            var outPath = Path.Combine(dir, "item_definitions.json");
            Assert.Throws<FileNotFoundException>(() => NewEmitter().EmitFromVpk(missing, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
