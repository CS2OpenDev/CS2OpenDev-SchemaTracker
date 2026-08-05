// Shared synthetic-content-VPK builder for the content-store tests.
//
//
// Builds a valid VPK1 pak01_dir.vpk (+ optional external pak01_<NNN>.vpk chunks) carrying an
// arbitrary set of content entries, some EMBEDDED in _dir.vpk and some EXTERNAL — so the trim
// writer's "remap every entry to external chunk 0" is exercised from BOTH source layouts. The
// tree/CRC layout mirrors VpkArchive's on-disk format (see VpkArchive.cs).

using System.Buffers.Binary;
using System.Text;

namespace Cs2SchemaTracker.Tests.Content;

internal static class ContentVpkFixture
{
    public const uint Signature = 0x55AA1234u;
    public const ushort Embedded = 0x7FFF;
    public const ushort Terminator = 0xFFFF;

    /// <summary>One source entry: the raw (dir, ext, name) triple, its body, and where the body lives
    /// (<see cref="Embedded"/> = in _dir.vpk, else external chunk index N). Dir " " = root.</summary>
    internal sealed record Entry(string Dir, string Ext, string Name, byte[] Body, ushort ArchiveIndex);

    /// <summary>IEEE CRC32, independent of the production Crc32 (test-side check).</summary>
    public static uint Crc32(byte[] data)
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

    /// <summary>Build the (pak01_dir.vpk bytes, external chunk index → bytes) pair.</summary>
    public static (byte[] Dir, IReadOnlyDictionary<ushort, byte[]> Chunks) Build(IReadOnlyList<Entry> entries)
    {
        var dataSection = new MemoryStream();
        var external = new Dictionary<ushort, MemoryStream>();
        var offsets = new Dictionary<Entry, uint>();

        foreach (var e in entries)
        {
            if (e.ArchiveIndex == Embedded)
            {
                offsets[e] = (uint)dataSection.Length;
                dataSection.Write(e.Body);
            }
            else
            {
                if (!external.TryGetValue(e.ArchiveIndex, out var ms))
                {
                    ms = new MemoryStream();
                    external[e.ArchiveIndex] = ms;
                }
                offsets[e] = (uint)ms.Length;
                ms.Write(e.Body);
            }
        }

        var tree = new MemoryStream();
        foreach (var byExt in entries.GroupBy(x => x.Ext))
        {
            WriteCString(tree, byExt.Key);
            foreach (var byDir in byExt.GroupBy(x => x.Dir))
            {
                WriteCString(tree, byDir.Key);
                foreach (var e in byDir)
                {
                    WriteCString(tree, e.Name);
                    WriteU32(tree, Crc32(e.Body));
                    WriteU16(tree, 0);                     // preload bytes
                    WriteU16(tree, e.ArchiveIndex);
                    WriteU32(tree, offsets[e]);
                    WriteU32(tree, (uint)e.Body.Length);
                    WriteU16(tree, Terminator);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        byte[] treeBytes = tree.ToArray();
        var dir = new MemoryStream();
        WriteU32(dir, Signature);
        WriteU32(dir, 1u);                                 // VPK v1
        WriteU32(dir, (uint)treeBytes.Length);
        dir.Write(treeBytes);
        dir.Write(dataSection.ToArray());                  // embedded data section (v1: after tree)

        var chunks = external.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
        return (dir.ToArray(), chunks);
    }

    /// <summary>Write the pak01 set into <paramref name="csgoDir"/> and return the pak01_dir.vpk path.</summary>
    public static string Write(string csgoDir, IReadOnlyList<Entry> entries)
    {
        Directory.CreateDirectory(csgoDir);
        var (dir, chunks) = Build(entries);
        var dirPath = Path.Combine(csgoDir, "pak01_dir.vpk");
        File.WriteAllBytes(dirPath, dir);
        foreach (var (index, bytes) in chunks)
        {
            File.WriteAllBytes(Path.Combine(csgoDir, $"pak01_{index:D3}.vpk"), bytes);
        }
        return dirPath;
    }

    private static void WriteCString(Stream s, string v)
    {
        s.Write(Encoding.UTF8.GetBytes(v));
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
}
