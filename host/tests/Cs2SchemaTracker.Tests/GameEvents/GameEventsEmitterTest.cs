// tests — game-event extraction from `.gameevents` KV1 inside a VPK.
//
// Every fixture is a hand-constructed in-memory VPK (the VpkArchive's own
// synthetic-fixture style) carrying a couple of fake `.gameevents` KV1 files. We assert:
//   * every event is extracted with name, source filename, properties (local, reliable),
// and field list (name, type, comment) preserved verbatim;
//   * deterministic byte-identical output across two runs, with events ordered by
// (source, name) regardless of source-file iteration order;
// * fail-loud on malformed KV1 and on a missing VPK, with NO output bytes.
//
// REAL-CORPUS FOLLOW-UP: real-corpus validation needs the genuine pak01_dir.vpk from the
// CONTENT depot (app 730 / depot 2347770), which the binaries-only `acquire` deliberately
// SKIPS (it fetches the per-OS BINARY depot 2347771/2347773 only). So a real `.gameevents`
// run against Valve's shipped bytes is a separate follow-up gated on a content-VPK fetch;
// the synthetic coverage here proves the KV1 parser + property/field mapping + fail-loud.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.GameEvents;
using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Tests.GameEvents;

public class GameEventsEmitterTest
{
    private const string BuildId = "13371337";
    private const string Platform = "windows-x86_64";

    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    // -- IEEE CRC32 (test-side re-implementation, matches VpkArchiveTest). --
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

    // Build a single-archive VPK (all bodies embedded in _dir.vpk). Same layout the
    // test uses, reproduced here so this suite is self-contained.
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

    private static void WriteCString(Stream s, string value)
    {
        s.Write(Encoding.UTF8.GetBytes(value));
        s.WriteByte(0);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }

    // -- Sample `.gameevents` KV1 payloads --

    // core.gameevents: two events, one with both transport properties + a commented field.
    private const string CoreGameEvents =
        """
        "GameEvents"
        {
            "player_death"           // a player died
            {
                "local"    "0"
                "reliable" "1"
                "userid"   "short"   // user ID who died
                "attacker" "short"
                "weapon"   "string"
            }
            "round_start"
            {
                "reliable" "1"
                "timelimit" "long"
                "fraglimit" "long"
            }
        }
        """;

    // game.gameevents: one event with no transport properties (fields only).
    private const string GameGameEvents =
        """
        "GameEvents"
        {
            "bomb_planted"
            {
                "userid" "short"
                "site"   "short"
            }
        }
        """;

    private static IReadOnlyList<FileSpec> SampleFiles() =>
    [
        // Reverse-of-sorted insertion order to prove (source,name) sorting is robust.
        new("resource", "gameevents", "game", Encoding.UTF8.GetBytes(GameGameEvents)),
        new("resource", "gameevents", "core", Encoding.UTF8.GetBytes(CoreGameEvents)),
        new("scripts", "txt", "items", Encoding.ASCII.GetBytes("not an event file")),
    ];

    private static VpkArchive SampleArchive(int version = 2) =>
        VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(version, SampleFiles()));

    private static GameEventsEmitter NewEmitter() =>
        new(SchemaFamily.Version, BuildId, Platform);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gameevents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ---- Tests -----------------------------------------------------------------------

    [Xunit.Fact]
    public void Extracts_Every_Event_With_Fields_Properties_And_Comments_Verbatim()
    {
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "gameevents.json");
            NewEmitter().Emit(SampleArchive(), outPath);

            var bytes = File.ReadAllBytes(outPath);
            Xunit.Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "gameevents.json must not have a UTF-8 BOM");
            var text = Encoding.UTF8.GetString(bytes);
            Xunit.Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            Xunit.Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Xunit.Assert.Equal(BuildId, root.GetProperty("buildId").GetString());
            Xunit.Assert.Equal(Platform, root.GetProperty("platform").GetString());

            var events = root.GetProperty("events");
            // 3 events total: core has player_death + round_start, game has bomb_planted.
            Xunit.Assert.Equal(3, events.GetArrayLength());

            // Sorted by (source, name) Ordinal:
            //   core.gameevents/player_death, core.gameevents/round_start, game.gameevents/bomb_planted
            Xunit.Assert.Equal("player_death", events[0].GetProperty("name").GetString());
            Xunit.Assert.Equal("core.gameevents", events[0].GetProperty("source").GetString());
            Xunit.Assert.Equal("round_start", events[1].GetProperty("name").GetString());
            Xunit.Assert.Equal("core.gameevents", events[1].GetProperty("source").GetString());
            Xunit.Assert.Equal("bomb_planted", events[2].GetProperty("name").GetString());
            Xunit.Assert.Equal("game.gameevents", events[2].GetProperty("source").GetString());

            // player_death: event comment, transport properties, and three fields (one commented).
            var death = events[0];
            Xunit.Assert.Equal("a player died", death.GetProperty("comment").GetString());
            var props = death.GetProperty("properties");
            Xunit.Assert.Equal("0", props.GetProperty("local").GetString());
            Xunit.Assert.Equal("1", props.GetProperty("reliable").GetString());

            var fields = death.GetProperty("fields");
            Xunit.Assert.Equal(3, fields.GetArrayLength());
            Xunit.Assert.Equal("userid", fields[0].GetProperty("name").GetString());
            Xunit.Assert.Equal("short", fields[0].GetProperty("type").GetString());
            Xunit.Assert.Equal("user ID who died", fields[0].GetProperty("comment").GetString());
            Xunit.Assert.Equal("attacker", fields[1].GetProperty("name").GetString());
            Xunit.Assert.Equal("short", fields[1].GetProperty("type").GetString());
            // No trailing comment on attacker.
            Xunit.Assert.Equal("", fields[1].GetProperty("comment").GetString());
            Xunit.Assert.Equal("weapon", fields[2].GetProperty("name").GetString());
            Xunit.Assert.Equal("string", fields[2].GetProperty("type").GetString());

            // bomb_planted: no transport properties -> empty properties map, fields only.
            var bomb = events[2];
            Xunit.Assert.False(bomb.GetProperty("properties").EnumerateObject().Any(),
                "bomb_planted has no local/reliable -> empty properties map");
            Xunit.Assert.Equal(2, bomb.GetProperty("fields").GetArrayLength());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void Merges_GameEvents_Across_Multiple_Archives_Sorted_By_Source_Then_Name()
    {
        // The engine core pak lives in a SEPARATE archive from the csgo pak. Emitting from BOTH
        // must merge their events into one document, sorted by (source, name), independent of the
        // archive order passed in.
        var csgo = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2,
        [
            new("resource", "gameevents", "game", Encoding.UTF8.GetBytes(GameGameEvents)),
            new("scripts", "txt", "items", Encoding.ASCII.GetBytes("not an event file")),
        ]));
        var core = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2,
        [
            new("resource", "gameevents", "core", Encoding.UTF8.GetBytes(CoreGameEvents)),
        ]));

        var workDir = NewWorkDir();
        try
        {
            var outCoreFirst = Path.Combine(workDir, "core-first.json");
            var outCsgoFirst = Path.Combine(workDir, "csgo-first.json");
            NewEmitter().Emit(new[] { core, csgo }, outCoreFirst);
            NewEmitter().Emit(new[] { csgo, core }, outCsgoFirst);

            // Archive order must NOT affect the output (events are sorted by (source, name)).
            Xunit.Assert.Equal(File.ReadAllBytes(outCoreFirst), File.ReadAllBytes(outCsgoFirst));

            using var doc = JsonDocument.Parse(File.ReadAllText(outCoreFirst));
            var events = doc.RootElement.GetProperty("events");
            // core.gameevents: player_death, round_start ; game.gameevents: bomb_planted.
            Xunit.Assert.Equal(3, events.GetArrayLength());
            Xunit.Assert.Equal("core.gameevents", events[0].GetProperty("source").GetString());
            Xunit.Assert.Equal("player_death", events[0].GetProperty("name").GetString());
            Xunit.Assert.Equal("core.gameevents", events[1].GetProperty("source").GetString());
            Xunit.Assert.Equal("round_start", events[1].GetProperty("name").GetString());
            Xunit.Assert.Equal("game.gameevents", events[2].GetProperty("source").GetString());
            Xunit.Assert.Equal("bomb_planted", events[2].GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_No_GameEvents_In_Any_Archive_Throws()
    {
        // Zero `.gameevents` across the WHOLE archive set (not just one) fails loud.
        var noEvents = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2,
        [
            new("scripts", "txt", "items", Encoding.ASCII.GetBytes("not an event file")),
        ]));
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, "gameevents.json");
            Xunit.Assert.Throws<InvalidDataException>(
                () => NewEmitter().Emit(new[] { noEvents, noEvents }, outPath));
            Xunit.Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Produces_Byte_Identical_Output_Across_Two_Runs(int version)
    {
        var workDir = NewWorkDir();
        try
        {
            var outA = Path.Combine(workDir, "a.json");
            var outB = Path.Combine(workDir, "b.json");
            NewEmitter().Emit(SampleArchive(version), outA);
            NewEmitter().Emit(SampleArchive(version), outB);

            Xunit.Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void EmitFromVpk_Reads_Dir_Vpk_From_Disk()
    {
        var workDir = NewWorkDir();
        try
        {
            var dirPath = Path.Combine(workDir, "pak01_dir.vpk");
            File.WriteAllBytes(dirPath, BuildEmbeddedVpk(2, SampleFiles()));

            var outPath = Path.Combine(workDir, "gameevents.json");
            NewEmitter().EmitFromVpk(dirPath, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Xunit.Assert.Equal(3, doc.RootElement.GetProperty("events").GetArrayLength());
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- fail-loud paths ----

    [Xunit.Fact]
    public void FailLoud_Malformed_Kv1_Throws_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            // Unbalanced braces -> KV1 parse failure.
            var bad = "\"GameEvents\"\n{\n  \"evt\"\n  {\n    \"userid\" \"short\"\n";
            var files = new List<FileSpec>
            {
                new("resource", "gameevents", "core", Encoding.UTF8.GetBytes(bad)),
            };
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));

            var outPath = Path.Combine(workDir, "gameevents.json");
            var ex = Xunit.Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Xunit.Assert.Contains("KV1", ex.Message, StringComparison.OrdinalIgnoreCase);

            Xunit.Assert.False(File.Exists(outPath), "no output bytes on failure");
            Xunit.Assert.False(File.Exists(outPath + ".tmp"), "no leftover temp file");
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_Missing_Vpk_Throws()
    {
        var workDir = NewWorkDir();
        try
        {
            var missing = Path.Combine(workDir, "does-not-exist_dir.vpk");
            var outPath = Path.Combine(workDir, "gameevents.json");

            Xunit.Assert.Throws<FileNotFoundException>(() => NewEmitter().EmitFromVpk(missing, outPath));
            Xunit.Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Xunit.Fact]
    public void FailLoud_No_GameEvents_Entries_Throws()
    {
        var workDir = NewWorkDir();
        try
        {
            // A VPK with only an unrelated .txt file -> zero .gameevents entries.
            var files = new List<FileSpec>
            {
                new("scripts", "txt", "items", Encoding.ASCII.GetBytes("not an event file")),
            };
            var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(2, files));

            var outPath = Path.Combine(workDir, "gameevents.json");
            var ex = Xunit.Assert.Throws<InvalidDataException>(() => NewEmitter().Emit(archive, outPath));
            Xunit.Assert.Contains("gameevents", ex.Message, StringComparison.OrdinalIgnoreCase);
            Xunit.Assert.False(File.Exists(outPath));
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
