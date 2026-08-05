// tests — VPK1/VPK2 container parsing, list + extract + CRC round-trip,
// and the fail-loud failure modes.
//
// No real pak01_dir.vpk is committed to the repo, so every fixture here is
// hand-constructed in memory: tiny valid VPK1 / VPK2 blobs with a couple of fake
// `.gameevents` entries plus an unrelated entry, embedded data and (for the
// external-archive test) a body referenced in a sibling _NNN.vpk.
//
// REMAINING HALF OF COVERAGE: real-corpus verification against an actual
// shipped pak01_dir.vpk (list + extract the genuine resource/*.gameevents files)
// is gated on Steam acquisition landing a real VPK to test against. Synthetic
// coverage below proves the parser + CRC + fail-loud paths; corpus coverage proves
// it against Valve's real bytes.

using System.Buffers.Binary;
using System.Text;

using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Tests.Vpk;

public partial class VpkArchiveTest
{
    private const uint Signature = 0x55AA1234u;
    private const ushort Embedded = 0x7FFF;
    private const ushort Terminator = 0xFFFF;

    // -- IEEE CRC32, independent re-implementation for the test side. --
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

    // Build a single-archive VPK with all bodies embedded in _dir.vpk.
    // version: 1 or 2. Returns the full _dir.vpk bytes.
    private static byte[] BuildEmbeddedVpk(int version, IReadOnlyList<FileSpec> files)
    {
        // 1. Group by extension then path, matching the tree structure.
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();

        // Lay out bodies first so we know each entry's offset within the data section.
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
                    WriteU16(tree, 0);            // preload bytes
                    WriteU16(tree, Embedded);     // archive index 0x7FFF => in _dir.vpk
                    WriteU32(tree, offsets[f]);   // entry offset
                    WriteU32(tree, (uint)f.Body.Length);
                    WriteU16(tree, Terminator);
                }
                tree.WriteByte(0); // end of files for this path
            }
            tree.WriteByte(0); // end of paths for this extension
        }
        tree.WriteByte(0); // end of extension list

        byte[] treeBytes = tree.ToArray();
        byte[] dataBytes = dataSection.ToArray();

        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, (uint)version);
        WriteU32(ms, (uint)treeBytes.Length);
        if (version == 2)
        {
            WriteU32(ms, (uint)dataBytes.Length); // FileDataSectionSize
            WriteU32(ms, 0);                       // ArchiveMd5SectionSize
            WriteU32(ms, 0);                       // OtherMd5SectionSize
            WriteU32(ms, 0);                       // SignatureSectionSize
        }
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }

    // Build a VPK whose single entry's body lives in an external _000.vpk.
    private static (byte[] Dir, byte[] External) BuildExternalVpk(int version, FileSpec file, ushort archiveIndex)
    {
        byte[] external = file.Body;

        var tree = new MemoryStream();
        WriteCString(tree, file.Ext);
        WriteCString(tree, file.Path);
        WriteCString(tree, file.Name);
        WriteU32(tree, Crc32(file.Body));
        WriteU16(tree, 0);                 // preload
        WriteU16(tree, archiveIndex);      // external archive
        WriteU32(tree, 0);                 // offset in external file
        WriteU32(tree, (uint)file.Body.Length);
        WriteU16(tree, Terminator);
        tree.WriteByte(0); // end files
        tree.WriteByte(0); // end paths
        tree.WriteByte(0); // end extensions

        byte[] treeBytes = tree.ToArray();

        var ms = new MemoryStream();
        WriteU32(ms, Signature);
        WriteU32(ms, (uint)version);
        WriteU32(ms, (uint)treeBytes.Length);
        if (version == 2)
        {
            WriteU32(ms, 0);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
            WriteU32(ms, 0);
        }
        ms.Write(treeBytes);
        return (ms.ToArray(), external);
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

    private static IReadOnlyList<FileSpec> SampleFiles() =>
    [
        new("resource", "gameevents", "game", Encoding.ASCII.GetBytes("EVENTS-GAME-PAYLOAD")),
        new("resource", "gameevents", "core", Encoding.ASCII.GetBytes("EVENTS-CORE")),
        new("scripts", "txt", "items", Encoding.ASCII.GetBytes("not an event file")),
    ];

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Lists_All_Entries_With_Logical_Paths(int version)
    {
        var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(version, SampleFiles()));

        Xunit.Assert.Equal((uint)version, archive.Version);

        var paths = archive.Entries.Select(e => e.FullPath).ToArray();
        Xunit.Assert.Equal(
            ["resource/core.gameevents", "resource/game.gameevents", "scripts/items.txt"],
            paths); // also asserts Ordinal sort order
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Extracts_Embedded_Body_And_Verifies_Crc(int version)
    {
        var files = SampleFiles();
        var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(version, files));

        var entry = archive.Find("resource/game.gameevents");
        Xunit.Assert.NotNull(entry);

        byte[] bytes = archive.ReadEntryBytes(entry!);
        Xunit.Assert.Equal("EVENTS-GAME-PAYLOAD", Encoding.ASCII.GetString(bytes));
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Extracts_Every_Entry_RoundTrip(int version)
    {
        var files = SampleFiles();
        var archive = VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(version, files));

        foreach (var f in files)
        {
            string logical = $"{f.Path}/{f.Name}.{f.Ext}";
            var entry = archive.Find(logical);
            Xunit.Assert.NotNull(entry);
            Xunit.Assert.Equal(f.Body, archive.ReadEntryBytes(entry!));
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(1)]
    [Xunit.InlineData(2)]
    public void Resolves_External_Archive(int version)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "vpk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var file = new FileSpec("resource", "gameevents", "game", Encoding.ASCII.GetBytes("EXTERNAL-EVENTS-BODY"));
            var (dir, external) = BuildExternalVpk(version, file, archiveIndex: 0);

            string dirPath = Path.Combine(workDir, "pak01_dir.vpk");
            File.WriteAllBytes(dirPath, dir);
            File.WriteAllBytes(Path.Combine(workDir, "pak01_000.vpk"), external);

            var archive = VpkArchive.Open(dirPath);
            var entry = archive.Find("resource/game.gameevents");
            Xunit.Assert.NotNull(entry);
            Xunit.Assert.Equal(external, archive.ReadEntryBytes(entry!));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    // ----- fail-loud paths -----

    [Xunit.Fact]
    public void Bad_Signature_Throws()
    {
        byte[] bytes = BuildEmbeddedVpk(2, SampleFiles());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0xDEADBEEFu);

        var ex = Xunit.Assert.Throws<InvalidDataException>(() => VpkArchive.Parse("bad.vpk", bytes));
        Xunit.Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void Unknown_Version_Throws()
    {
        byte[] bytes = BuildEmbeddedVpk(1, SampleFiles());
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 99u);

        var ex = Xunit.Assert.Throws<InvalidDataException>(() => VpkArchive.Parse("v99.vpk", bytes));
        Xunit.Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void Truncated_Header_Throws()
    {
        byte[] bytes = BuildEmbeddedVpk(2, SampleFiles())[..8]; // cut mid-header
        Xunit.Assert.Throws<InvalidDataException>(() => VpkArchive.Parse("short.vpk", bytes));
    }

    [Xunit.Fact]
    public void Truncated_Tree_Throws()
    {
        byte[] full = BuildEmbeddedVpk(2, SampleFiles());
        // Inflate the declared TreeSize so it overruns the actual file -> truncation.
        BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(8, 4), (uint)full.Length + 64);
        var ex = Xunit.Assert.Throws<InvalidDataException>(() => VpkArchive.Parse("cut.vpk", full));
        Xunit.Assert.Contains("truncat", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void Crc_Mismatch_On_Extract_Throws()
    {
        var files = SampleFiles();
        byte[] bytes = BuildEmbeddedVpk(2, files);
        var archive = VpkArchive.Parse("pak01_dir.vpk", bytes);

        // Corrupt one byte of the embedded body for game.gameevents.
        var entry = archive.Find("resource/game.gameevents")!;
        long bodyAbs = 28 /* v2 header */ + GetTreeSize(bytes) + entry.EntryOffset;
        bytes[bodyAbs] ^= 0xFF;

        var corrupted = VpkArchive.Parse("pak01_dir.vpk", bytes);
        var e2 = corrupted.Find("resource/game.gameevents")!;
        var ex = Xunit.Assert.Throws<InvalidDataException>(() => corrupted.ReadEntryBytes(e2));
        Xunit.Assert.Contains("CRC32", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void Missing_External_Archive_Throws()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "vpk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var file = new FileSpec("resource", "gameevents", "game", Encoding.ASCII.GetBytes("BODY"));
            var (dir, _) = BuildExternalVpk(2, file, archiveIndex: 0);

            string dirPath = Path.Combine(workDir, "pak01_dir.vpk");
            File.WriteAllBytes(dirPath, dir);
            // Deliberately do NOT write pak01_000.vpk.

            var archive = VpkArchive.Open(dirPath);
            var entry = archive.Find("resource/game.gameevents")!;
            Xunit.Assert.Throws<FileNotFoundException>(() => archive.ReadEntryBytes(entry));
        }
        finally
        {
            TryDelete(workDir);
        }
    }

    private static uint GetTreeSize(byte[] bytes) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));

    private static void TryDelete(string dir)
    {
        if (Directory.Exists(dir))
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
