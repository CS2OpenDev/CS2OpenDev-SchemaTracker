// tests — game-mode/game-type extraction from the loose gamemodes.txt KV1 inside a
// content-depot VPK.
//
// Every fixture is a hand-constructed in-memory VPK (mirroring ItemDefinitionsEmitterTest)
// carrying a fake gamemodes.txt at the VPK root. We assert:
//   * gameTypes -> gameModes nesting + the documented field mapping (nameID/displayName/
//     maxplayers/game_type/game_mode/typeflags, mapgroupsMP keys, convars name->value);
//   * mapgroups maps the displayname + the maps keys;
//   * deterministic byte-identical output across two runs, with ids sorted Ordinal and
// convars/maps sorted+deduped regardless of source order;
// * fail-loud on malformed KV1, missing entry, missing/scalar gameTypes, a
//     duplicate id, a non-integer maxplayers, and zero game types — with NO output bytes.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.GameModes;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.GameModes;

public class GameModesEmitterTest
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

    // Build a VPK carrying a single gamemodes.txt at the VPK root (path " "), embedded in _dir.vpk.
    private static VpkArchive ArchiveWith(string gameModesTxt) =>
        VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
        {
            new(" ", "txt", "gamemodes", Encoding.UTF8.GetBytes(gameModesTxt)),
        }));

    private static GameModesEmitter NewEmitter() => new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gamemodes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Deliberately out-of-order to exercise the sorting.
    private const string Sample =
        """
        "GameModes++"
        {
            "gameTypes"
            {
                "gungame"
                {
                    "index" "1"
                    "gameModes"
                    {
                        "deathmatch" { "maxplayers" "16" "game_mode" "2" }
                    }
                }
                "classic"
                {
                    "index" "0"
                    "gameModes"
                    {
                        "competitive"
                        {
                            "nameID" "#name_comp"
                            "displayName" "#disp_comp"
                            "descID" "#desc_comp"
                            "maxplayers" "10"
                            "exhibitGameType" "classic"
                            "game_type" "0"
                            "game_mode" "1"
                            "typeflags" "4"
                            "mapgroupsMP" { "mg_active" "" "mg_active2" "" }
                            "convars" { "mp_roundtime" "1.92" "bot_quota" "0" }
                        }
                        "casual" { "maxplayers" "20" }
                    }
                }
            }
            "mapgroups"
            {
                "mg_active" { "displayname" "Active" "maps" { "de_anubis" "" "de_ancient" "" } }
            }
        }
        """;

    [Fact]
    public void Maps_GameTypes_GameModes_And_MapGroups()
    {
        var dir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(dir, "game_modes.json");
            NewEmitter().Emit(ArchiveWith(Sample), outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "game_modes.json must not have a UTF-8 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Assert.Equal(BuildId, root.GetProperty("buildId").GetString());
            Assert.Equal(Platform, root.GetProperty("platform").GetString());

            // gameTypes sorted by id Ordinal: classic < gungame.
            var types = root.GetProperty("gameTypes");
            Assert.Equal(2, types.GetArrayLength());
            Assert.Equal("classic", types[0].GetProperty("id").GetString());
            Assert.Equal(0, types[0].GetProperty("index").GetInt32());
            Assert.Equal("gungame", types[1].GetProperty("id").GetString());
            Assert.Equal(1, types[1].GetProperty("index").GetInt32());

            // classic gameModes sorted by id Ordinal: casual < competitive.
            var modes = types[0].GetProperty("gameModes");
            Assert.Equal(2, modes.GetArrayLength());
            Assert.Equal("casual", modes[0].GetProperty("id").GetString());
            Assert.Equal(20u, modes[0].GetProperty("maxPlayers").GetUInt32());

            var comp = modes[1];
            Assert.Equal("competitive", comp.GetProperty("id").GetString());
            Assert.Equal("#name_comp", comp.GetProperty("nameId").GetString());
            Assert.Equal("#disp_comp", comp.GetProperty("displayName").GetString());
            Assert.Equal("#desc_comp", comp.GetProperty("descriptionId").GetString());
            Assert.Equal(10u, comp.GetProperty("maxPlayers").GetUInt32());
            Assert.Equal("classic", comp.GetProperty("exhibitGameType").GetString());
            Assert.Equal(0, comp.GetProperty("gameType").GetInt32());
            Assert.Equal(1, comp.GetProperty("gameMode").GetInt32());
            Assert.Equal(4, comp.GetProperty("typeFlags").GetInt32());

            // mapgroupsMP keys, sorted+deduped.
            var mgmp = comp.GetProperty("mapGroupsMp");
            Assert.Equal(2, mgmp.GetArrayLength());
            Assert.Equal("mg_active", mgmp[0].GetString());
            Assert.Equal("mg_active2", mgmp[1].GetString());

            // convars sorted by name Ordinal: bot_quota < mp_roundtime.
            var convars = comp.GetProperty("convars");
            Assert.Equal(2, convars.GetArrayLength());
            Assert.Equal("bot_quota", convars[0].GetProperty("name").GetString());
            Assert.Equal("0", convars[0].GetProperty("value").GetString());
            Assert.Equal("mp_roundtime", convars[1].GetProperty("name").GetString());
            Assert.Equal("1.92", convars[1].GetProperty("value").GetString());

            // mapgroups: maps sorted Ordinal (de_ancient < de_anubis).
            var groups = root.GetProperty("mapGroups");
            Assert.Equal(1, groups.GetArrayLength());
            Assert.Equal("mg_active", groups[0].GetProperty("id").GetString());
            Assert.Equal("Active", groups[0].GetProperty("displayName").GetString());
            var maps = groups[0].GetProperty("maps");
            Assert.Equal("de_ancient", maps[0].GetString());
            Assert.Equal("de_anubis", maps[1].GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Optional_MapGroups_Absent_Yields_Empty_Repeated()
    {
        var dir = NewWorkDir();
        try
        {
            const string minimal =
                """
                "GameModes++"
                {
                    "gameTypes" { "classic" { "gameModes" { "casual" { "maxplayers" "20" } } } }
                }
                """;
            var outPath = Path.Combine(dir, "game_modes.json");
            NewEmitter().Emit(ArchiveWith(minimal), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("gameTypes").GetArrayLength());
            Assert.Equal(0, root.GetProperty("mapGroups").GetArrayLength());
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
                new(" ", "txt", "gamemodes", Encoding.UTF8.GetBytes(Sample)),
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
    public void FailLoud_Missing_GameModes_Entry()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
            {
                new("resource", "txt", "other", Encoding.ASCII.GetBytes("nope")),
            }));
            var outPath = Path.Combine(dir, "game_modes.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("gamemodes.txt", ex.Message, StringComparison.OrdinalIgnoreCase);
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
            var archive = ArchiveWith("\"GameModes++\"\n{\n  \"gameTypes\"\n  {\n"); // unbalanced braces
            var outPath = Path.Combine(dir, "game_modes.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("KV1", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_GameTypes_Section()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith("\"GameModes++\" { \"mapgroups\" { } }");
            var outPath = Path.Combine(dir, "game_modes.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("gameTypes", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ERA-FAITHFUL: a duplicate id is NOT corruption — Valve ships gamemodes.txt with genuinely
    // repeated ids (build 20503857 has TWO "mg_de_basalt" mapgroups differing only in authorID).
    // The emitter now applies the conventional KV1 LAST-OCCURRENCE-WINS (the engine keeps the final
    // definition) instead of fail-louding and killing the whole extract.
    [Fact]
    public void Duplicate_Id_Is_LastOccurrenceWins()
    {
        var dir = NewWorkDir();
        try
        {
            // Two "classic" gameTypes: the later index (1) must win.
            var archive = ArchiveWith(
                "\"GameModes++\" { \"gameTypes\" { \"classic\" { \"index\" \"0\" } \"classic\" { \"index\" \"1\" } } }");
            var outPath = Path.Combine(dir, "game_modes.json");
            NewEmitter().Emit(archive, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var types = doc.RootElement.GetProperty("gameTypes");
            Assert.Equal(1, types.GetArrayLength());
            Assert.Equal("classic", types[0].GetProperty("id").GetString());
            Assert.Equal(1, types[0].GetProperty("index").GetInt32());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // HasSource: a VPK with the loose gamemodes.txt has a source; one without genuinely lacks it.
    [Fact]
    public void HasSource_TrueWhenGameModesPresent_FalseWhenAbsent()
    {
        Assert.True(GameModesEmitter.HasSource(ArchiveWith("\"GameModes++\" { \"gameTypes\" { } }")));

        var noGameModes = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, new List<FileSpec>
        {
            new("scripts", "txt", "propdata", Encoding.UTF8.GetBytes("\"x\" {}")),
        }));
        Assert.False(GameModesEmitter.HasSource(noGameModes));
    }

    [Fact]
    public void FailLoud_NonInteger_MaxPlayers()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith(
                "\"GameModes++\" { \"gameTypes\" { \"classic\" { \"gameModes\" { \"casual\" { \"maxplayers\" \"lots\" } } } } }");
            var outPath = Path.Combine(dir, "game_modes.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("non-integer", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Zero_GameTypes()
    {
        var dir = NewWorkDir();
        try
        {
            var archive = ArchiveWith("\"GameModes++\" { \"gameTypes\" { } }");
            var outPath = Path.Combine(dir, "game_modes.json");
            var ex = Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Assert.Contains("zero game types", ex.Message, StringComparison.OrdinalIgnoreCase);
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
            var outPath = Path.Combine(dir, "game_modes.json");
            Assert.Throws<FileNotFoundException>(() => NewEmitter().EmitFromVpk(missing, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
