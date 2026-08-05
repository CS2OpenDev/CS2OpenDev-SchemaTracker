// regression — items_game.txt SPLITS each economy table across many sibling top-level
// blocks that all share one key (the base "items" block, then one fragment per
// operation/tournament; likewise "prefabs"/"paint_kits"/"sticker_kits"/"music_definitions").
//
// The original emitter applied last-occurrence-wins when locating a section, so on the
// REAL items_game.txt (133 top-level "items" blocks) it kept ONLY the final fragment — 14 of
// 2003 items, with ALL weapons missing. This fixture reproduces that exact shape in miniature:
// three separate "items" blocks (and split prefabs/paint_kits/sticker_kits) whose union must be
// captured. If the emitter regresses to last-wins, the counts and weapon_ak47/weapon_knife
// assertions below fail.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Items;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.Items;

public class ItemDefinitionsRepeatedSectionTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    // items_game.txt with THREE separate top-level "items" blocks (base weapons + two
    // "operation" fragments), split prefabs/paint_kits/sticker_kits, and a trailing 14-entry
    // fragment that mimics the real cologne2026 tail that used to be the ONLY surviving block.
    private const string Split =
        """
        "items_game"
        {
            "items"
            {
                "default" { "name" "default" "item_class" "weapon_knife" }
                "1"       { "name" "weapon_deagle" "prefab" "weapon_deagle_prefab" }
                "7"       { "name" "weapon_ak47" "prefab" "weapon_ak47_prefab" }
                "42"      { "name" "weapon_knife" "item_class" "weapon_knife" }
                "500"     { "name" "weapon_knife_cord" "prefab" "melee_unusual" }
            }
            "prefabs"
            {
                "weapon_ak47_prefab" { "item_class" "weapon_ak47" "item_slot" "rifle" }
            }
            "paint_kits"
            {
                "0" { "name" "default" }
            }
            "sticker_kits"
            {
                "0" { "name" "default" }
            }
            "items"
            {
                "1000" { "name" "item_kevlar" "item_class" "item_kevlar" }
                "1001" { "name" "item_assaultsuit" "item_class" "item_assaultsuit" }
            }
            "prefabs"
            {
                "weapon_deagle_prefab" { "item_class" "weapon_deagle" "item_slot" "secondary" }
            }
            "paint_kits"
            {
                "9" { "name" "aa_fade" }
            }
            "sticker_kits"
            {
                "3" { "name" "sticker_a" }
            }
            "items"
            {
                "5309" { "name" "tournament_pass_credits" "prefab" "credits_prefab" }
                "5310" { "name" "tournament_pass_credits2" "prefab" "credits_prefab" }
            }
        }
        """;

    [Fact]
    public void Merges_All_Sibling_Section_Blocks_Not_Last_Wins()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "item_definitions.json");
            NewEmitter().Emit(ArchiveWith(Split), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = doc.RootElement;

            // Union of all THREE "items" blocks: 5 + 2 + 2 = 9 (NOT the 2-entry last fragment).
            var items = root.GetProperty("items");
            Assert.Equal(9, items.GetArrayLength());

            // Union of both "prefabs" / "paint_kits" / "sticker_kits" blocks.
            Assert.Equal(2, root.GetProperty("prefabs").GetArrayLength());
            Assert.Equal(2, root.GetProperty("paintKits").GetArrayLength());
            Assert.Equal(2, root.GetProperty("stickerKits").GetArrayLength());

            // The base weapons + the equipment fragment must survive (they did NOT, pre-fix).
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.GetArrayLength(); i++)
            {
                names.Add(items[i].GetProperty("name").GetString()!);
            }
            Assert.Contains("weapon_ak47", names);
            Assert.Contains("weapon_knife", names);
            Assert.Contains("weapon_knife_cord", names);
            Assert.Contains("item_kevlar", names);
            Assert.Contains("item_assaultsuit", names);
            // and the trailing fragment that used to be the only survivor is STILL present.
            Assert.Contains("tournament_pass_credits", names);

            // Numeric sort across the merged set: default(0)... 5310.
            Assert.Equal(0u, items[0].GetProperty("defIndex").GetUInt32());
            Assert.Equal(5310u, items[items.GetArrayLength() - 1].GetProperty("defIndex").GetUInt32());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // A genuine duplicate def_index ACROSS sibling blocks must still fail loud: the
    // merge unions children, then the existing dedup runs over the union.
    [Fact]
    public void FailLoud_Duplicate_DefIndex_Across_Sibling_Blocks()
    {
        var dir = NewWorkDir();
        try
        {
            const string collide =
                """
                "items_game"
                {
                    "items" { "7" { "name" "a" } }
                    "items" { "7" { "name" "b" } }
                }
                """;
            var outPath = Path.Combine(dir, "item_definitions.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(ArchiveWith(collide), outPath));
            Assert.Contains("duplicate", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ---- fixture plumbing (mirrors ItemDefinitionsEmitterTest) ----

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

    private static VpkArchive ArchiveWith(string itemsGameTxt) =>
        VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
        {
            new("scripts/items", "txt", "items_game", Encoding.UTF8.GetBytes(itemsGameTxt)),
        }));

    private static ItemDefinitionsEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "repeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }
}
