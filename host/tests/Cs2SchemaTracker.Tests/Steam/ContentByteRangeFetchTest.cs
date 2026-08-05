// byte-range-selective content fetch.
//
// Covers the pure pieces of the byte-range-selective content acquire (no Steam, no
// network):
//   * VpkByteRange.Overlaps + ContentPakSelector.MergeRanges (range algebra).
//   * ChunkRangeMath.SelectOverlapping + IsFullyCovered (byte-range -> covering
// depot-chunk mapping + the gap-coverage gate).
//   * ContentPakSelector.SelectContentByteRanges over a synthetic pak01_dir.vpk
//     (exact resource body ranges; embedded resources excluded; no-`.gameevents`
//     -> empty plan = fail-loud).
//   * ContentFetchPlan.TryGetRanges / AllFiles / SelectedPredicate.
//   * END-TO-END EQUIVALENCE: emit gameevents.json + item_definitions.json from a
//     FULL external-chunk VPK, then from a SPARSE one (only the depot-chunks the
//     plan selects are populated, the rest zeroed), and assert the artifacts are
//     BYTE-IDENTICAL — and that the sparse fetch transferred a small fraction of
//     the bytes. Reading a resource we did NOT fetch fails loud.

using System.Buffers.Binary;
using System.Text;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.GameEvents;
using Cs2SchemaTracker.Host.Items;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

// CA1861: expected-value arrays inline in Assert.Equal are clearer here than hoisted
// static fields and are not a hot path (one assertion per test).
#pragma warning disable CA1861

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentByteRangeFetchTest
{
    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;
    private const string Build = "13385739";
    private const string Platform = "windows-x86_64";

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

    // One resource placed in an EXTERNAL pak01_<ArchiveIndex>.vpk at a given offset.
    private sealed record ExtFile(string Path, string Ext, string Name, ushort ArchiveIndex, uint Offset, byte[] Body);

    private static void WriteCString(Stream s, string v) { s.Write(Encoding.UTF8.GetBytes(v)); s.WriteByte(0); }
    private static void WriteU32(Stream s, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); s.Write(b); }
    private static void WriteU16(Stream s, ushort v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); s.Write(b); }

    // Build a v2 pak01_dir.vpk that references each file as an EXTERNAL-archive body
    // (no embedded data section). Records CRC32 of the body + its (archiveIndex, offset, length).
    private static byte[] BuildDirVpk(IReadOnlyList<ExtFile> files)
    {
        var tree = new MemoryStream();
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
                    WriteU16(tree, 0);                 // preload bytes
                    WriteU16(tree, f.ArchiveIndex);
                    WriteU32(tree, f.Offset);
                    WriteU32(tree, (uint)f.Body.Length);
                    WriteU16(tree, Terminator);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        byte[] treeBytes = tree.ToArray();
        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, 2);
        WriteU32(ms, (uint)treeBytes.Length);
        WriteU32(ms, 0); // FileDataSectionSize (no embedded data)
        WriteU32(ms, 0);
        WriteU32(ms, 0);
        WriteU32(ms, 0);
        ms.Write(treeBytes);
        return ms.ToArray();
    }

    // Build the FULL bytes of one external pak01_<idx>.vpk: total size `size`, each
    // file's body placed at its offset, all other bytes a non-zero filler so a sparse
    // copy that zeros the filler is observably different on disk yet reads identically.
    private static byte[] BuildExternalFull(int size, IEnumerable<ExtFile> filesInThisArchive)
    {
        var buf = new byte[size];
        for (int i = 0; i < size; i++)
            buf[i] = 0xCC; // filler (NOT zero, NOT a body)
        foreach (var f in filesInThisArchive)
        {
            Array.Copy(f.Body, 0, buf, (int)f.Offset, f.Body.Length);
        }
        return buf;
    }

    // Fixed chunk tiling for an external file (mirrors a depot manifest's gap-free tiling).
    private static List<(long Offset, long Length)> Tile(int size, int chunk)
    {
        var list = new List<(long, long)>();
        for (long o = 0; o < size; o += chunk)
        {
            list.Add((o, Math.Min(chunk, size - o)));
        }
        return list;
    }

    // ---- Range algebra -------------------------------------------------------

    [Theory]
    [InlineData(0, 10, 5, 5, true)]    // [0,10) vs [5,10) overlap
    [InlineData(0, 10, 10, 5, false)]  // [0,10) vs [10,15) adjacent, NOT overlapping (half-open)
    [InlineData(0, 10, 9, 1, true)]    // last byte
    [InlineData(20, 5, 0, 20, false)]  // [20,25) vs [0,20) adjacent below
    [InlineData(20, 5, 19, 2, true)]   // straddles the boundary
    public void VpkByteRange_Overlaps_is_half_open(long ro, long rl, long co, long cl, bool expected)
        => Assert.Equal(expected, new VpkByteRange(ro, rl).Overlaps(co, cl));

    [Fact]
    public void MergeRanges_sorts_and_coalesces_overlapping_and_adjacent()
    {
        var merged = ContentPakSelector.MergeRanges(new[]
        {
            new VpkByteRange(100, 10),  // [100,110)
            new VpkByteRange(0, 5),     // [0,5)
            new VpkByteRange(105, 20),  // [105,125) overlaps prev -> [100,125)
            new VpkByteRange(5, 5),     // [5,10) adjacent to [0,5) -> [0,10)
            new VpkByteRange(200, 1),   // [200,201) standalone
        });
        Assert.Equal(new[]
        {
            new VpkByteRange(0, 10),
            new VpkByteRange(100, 25),
            new VpkByteRange(200, 1),
        }, merged);
    }

    // ---- byte-range -> covering chunk mapping --------------------------------

    [Fact]
    public void SelectOverlapping_picks_only_chunks_touching_a_required_range()
    {
        var chunks = Tile(size: 100, chunk: 10); // 10 chunks [0,10),[10,20),...
        var ranges = new[] { new VpkByteRange(15, 12) }; // [15,27) -> chunks [10,20) and [20,30)
        var sel = ChunkRangeMath.SelectOverlapping(ranges, chunks, c => c.Offset, c => c.Length);
        Assert.Equal(new (long, long)[] { (10, 10), (20, 10) }, sel);
    }

    [Fact]
    public void IsFullyCovered_true_when_chunks_tile_the_range_and_false_on_a_gap()
    {
        var ranges = new[] { new VpkByteRange(15, 12) };
        var covering = ChunkRangeMath.SelectOverlapping(ranges, Tile(100, 10), c => c.Offset, c => c.Length);
        Assert.True(ChunkRangeMath.IsFullyCovered(ranges, covering, c => c.Offset, c => c.Length, out _, out _));

        // Drop the chunk holding [20,30): a gap at offset 20 remains.
        var holed = covering.Where(c => c.Offset != 20).ToList();
        Assert.False(ChunkRangeMath.IsFullyCovered(ranges, holed, c => c.Offset, c => c.Length,
            out var uncovered, out var gap));
        Assert.Equal(new VpkByteRange(15, 12), uncovered);
        Assert.Equal(20, gap);
    }

    // ---- SelectContentByteRanges ---------------------------------------------

    [Fact]
    public void SelectContentByteRanges_yields_exact_resource_ranges_excluding_embedded()
    {
        var files = new List<ExtFile>
        {
            new("resource", "gameevents", "core", 7, 64, Encoding.ASCII.GetBytes("EVENTS-CORE")),
            new("resource", "gameevents", "game", 7, 256, Encoding.ASCII.GetBytes("EVENTS-GAME-BODY")),
            new("scripts/items", "txt", "items_game", 12, 512, Encoding.ASCII.GetBytes("ITEMS")),
            new("resource", "gameevents", "embed", Embedded, 0, Encoding.ASCII.GetBytes("RIDES-IN-DIR")),
            new("maps", "vpk", "de_dust", 3, 0, Encoding.ASCII.GetBytes("MAP")), // unrelated -> excluded
        };
        var archive = VpkArchive.Parse("pak01_dir.vpk", BuildDirVpk(files));

        var plan = ContentPakSelector.SelectContentByteRanges(archive);

        Assert.False(plan.IsEmpty);
        Assert.Equal(new[] { "game/csgo/pak01_dir.vpk" }, plan.WholeFiles);
        // chunk 7 carries two gameevents bodies; chunk 12 carries items_game; chunk 3 (map) excluded.
        Assert.Equal(new[] { "game/csgo/pak01_007.vpk", "game/csgo/pak01_012.vpk" },
            plan.ChunkRanges.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        Assert.True(plan.TryGetRanges("game/csgo/pak01_007.vpk", out var r7));
        Assert.Equal(new[]
        {
            new VpkByteRange(64, 11),   // "EVENTS-CORE"
            new VpkByteRange(256, 16),  // "EVENTS-GAME-BODY"
        }, r7);

        Assert.True(plan.TryGetRanges("game\\csgo\\pak01_012.vpk", out var r12)); // backslash form
        Assert.Equal(new[] { new VpkByteRange(512, 5) }, r12);

        Assert.Equal(new[] { "game/csgo/pak01_007.vpk", "game/csgo/pak01_012.vpk", "game/csgo/pak01_dir.vpk" },
            plan.AllFiles);
        var pred = plan.SelectedPredicate();
        Assert.True(pred("game/csgo/pak01_dir.vpk"));
        Assert.True(pred("game/csgo/pak01_007.vpk"));
        Assert.False(pred("game/csgo/pak01_003.vpk"));
    }

    [Fact]
    public void SelectContentByteRanges_no_gameevents_is_empty_plan_for_failloud()
    {
        var files = new List<ExtFile>
        {
            new("scripts/items", "txt", "items_game", 12, 0, Encoding.ASCII.GetBytes("ITEMS")),
        };
        var archive = VpkArchive.Parse("pak01_dir.vpk", BuildDirVpk(files));
        var plan = ContentPakSelector.SelectContentByteRanges(archive);
        Assert.True(plan.IsEmpty);
    }

    // ---- END-TO-END equivalence: sparse fetch == whole-file fetch ------------

    private const string CoreGameEvents =
        """
        "GameEvents"
        {
            "player_death"
            {
                "local"    "0"
                "reliable" "1"
                "userid"   "short"
                "weapon"   "string"
            }
        }
        """;

    private const string ItemsGame =
        """
        "items_game"
        {
            "items"
            {
                "1"
                {
                    "name" "weapon_knife"
                }
            }
        }
        """;

    [Fact]
    public void Sparse_fetch_yields_byte_identical_gameevents_and_items_artifacts()
    {
        // Two resources placed at non-trivial offsets inside two external chunk files,
        // each padded out to a large total size dominated by filler we must NOT need.
        const int Pak7Size = 4096;
        const int Pak12Size = 4096;
        const int ChunkSize = 256;

        byte[] eventsBody = Encoding.UTF8.GetBytes(CoreGameEvents);
        byte[] itemsBody = Encoding.UTF8.GetBytes(ItemsGame);

        var files = new List<ExtFile>
        {
            new("resource", "gameevents", "core", 7, 1024, eventsBody),
            new("scripts/items", "txt", "items_game", 12, 2000, itemsBody),
        };
        byte[] dirVpk = BuildDirVpk(files);

        var dirArchive = VpkArchive.Parse("pak01_dir.vpk", dirVpk);
        var plan = ContentPakSelector.SelectContentByteRanges(dirArchive);

        // Build the FULL external files.
        var full = new Dictionary<string, byte[]>
        {
            ["game/csgo/pak01_007.vpk"] = BuildExternalFull(Pak7Size, files.Where(f => f.ArchiveIndex == 7)),
            ["game/csgo/pak01_012.vpk"] = BuildExternalFull(Pak12Size, files.Where(f => f.ArchiveIndex == 12)),
        };
        var sizes = new Dictionary<string, int>
        {
            ["game/csgo/pak01_007.vpk"] = Pak7Size,
            ["game/csgo/pak01_012.vpk"] = Pak12Size,
        };

        // Emit from the FULL tree.
        string fullDir = NewWorkDir();
        WriteTree(fullDir, dirVpk, full);
        var (fullEvents, fullItems) = Emit(fullDir);

        // Build SPARSE external files: allocate full size, populate ONLY the depot-chunks
        // the plan selects (the production path) — everything else stays zero.
        long fetchedBytes = 0;
        var sparse = new Dictionary<string, byte[]>();
        foreach (var (relName, ranges) in plan.ChunkRanges)
        {
            int size = sizes[relName];
            var tiling = Tile(size, ChunkSize);
            var selected = ChunkRangeMath.SelectOverlapping(ranges, tiling, c => c.Offset, c => c.Length);
            Assert.True(ChunkRangeMath.IsFullyCovered(ranges, selected, c => c.Offset, c => c.Length, out _, out _));

            var sparseBuf = new byte[size]; // zero-filled (sparse)
            foreach (var (off, len) in selected)
            {
                Array.Copy(full[relName], off, sparseBuf, off, len);
                fetchedBytes += len;
            }
            sparse[relName] = sparseBuf;
            // The sparse file MUST differ from the full file on disk (filler dropped) ...
            Assert.NotEqual(full[relName], sparseBuf);
        }

        // Emit from the SPARSE tree.
        string sparseDir = NewWorkDir();
        WriteTree(sparseDir, dirVpk, sparse);
        var (sparseEvents, sparseItems) = Emit(sparseDir);

        //... yet the extracted artifacts are BYTE-IDENTICAL.
        Assert.Equal(fullEvents, sparseEvents);
        Assert.Equal(fullItems, sparseItems);

        // And the selective footprint is a small fraction of the whole-file footprint.
        long wholeBytes = Pak7Size + Pak12Size;
        Assert.True(fetchedBytes < wholeBytes / 4,
            $"expected selective fetch ({fetchedBytes} B) to be << whole-file ({wholeBytes} B)");
    }

    [Fact]
    public void Reading_an_unfetched_resource_from_a_sparse_file_fails_loud()
    {
        // A sparse external file that fetched ONLY the gameevents chunk; an unrelated resource
        // sharing the file but in an un-fetched region must CRC-fail (we never read it in
        // practice, but if we did the VPK layer refuses corrupt bytes —).
        const int Size = 1024;
        const int ChunkSize = 128;
        byte[] eventsBody = Encoding.UTF8.GetBytes(CoreGameEvents);
        byte[] otherBody = Encoding.ASCII.GetBytes("UNFETCHED-RESOURCE-BODY");

        var files = new List<ExtFile>
        {
            new("resource", "gameevents", "core", 7, 64, eventsBody),
            new("scripts", "cfg", "other", 7, 768, otherBody), // same chunk file, different (un-fetched) region
        };
        byte[] dirVpk = BuildDirVpk(files);
        var dirArchive = VpkArchive.Parse("pak01_dir.vpk", dirVpk);
        var plan = ContentPakSelector.SelectContentByteRanges(dirArchive);

        byte[] fullExt = BuildExternalFull(Size, files);
        var tiling = Tile(Size, ChunkSize);
        Assert.True(plan.TryGetRanges("game/csgo/pak01_007.vpk", out var ranges));
        var selected = ChunkRangeMath.SelectOverlapping(ranges, tiling, c => c.Offset, c => c.Length);
        var sparseBuf = new byte[Size];
        foreach (var (off, len) in selected)
        {
            Array.Copy(fullExt, off, sparseBuf, off, len);
        }

        string dir = NewWorkDir();
        WriteTree(dir, dirVpk, new Dictionary<string, byte[]> { ["game/csgo/pak01_007.vpk"] = sparseBuf });

        var archive = VpkArchive.Open(Path.Combine(dir, "game", "csgo", "pak01_dir.vpk"));
        // The gameevents resource (fetched) reads fine.
        var ev = archive.Find("resource/core.gameevents");
        Assert.NotNull(ev);
        Assert.Equal(eventsBody, archive.ReadEntryBytes(ev!));
        // The un-fetched resource is a zeroed region -> CRC mismatch -> fail loud.
        var other = archive.Find("scripts/other.cfg");
        Assert.NotNull(other);
        Assert.Throws<InvalidDataException>(() => archive.ReadEntryBytes(other!));
    }

    // ---- helpers -------------------------------------------------------------

    private static (byte[] Events, byte[] Items) Emit(string treeDir)
    {
        var archive = VpkArchive.Open(Path.Combine(treeDir, "game", "csgo", "pak01_dir.vpk"));
        string evPath = Path.Combine(treeDir, "gameevents.json");
        string itPath = Path.Combine(treeDir, "item_definitions.json");
        new GameEventsEmitter(SchemaFamily.Version, Build, Platform).Emit(archive, evPath);
        new ItemDefinitionsEmitter(SchemaFamily.Version, Build, Platform).Emit(archive, itPath);
        return (File.ReadAllBytes(evPath), File.ReadAllBytes(itPath));
    }

    private static void WriteTree(string root, byte[] dirVpk, IReadOnlyDictionary<string, byte[]> externals)
    {
        var csgo = Path.Combine(root, "game", "csgo");
        Directory.CreateDirectory(csgo);
        File.WriteAllBytes(Path.Combine(csgo, "pak01_dir.vpk"), dirVpk);
        foreach (var (rel, bytes) in externals)
        {
            var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
    }

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "byterange-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
