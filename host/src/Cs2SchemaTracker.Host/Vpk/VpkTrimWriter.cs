// in-house VPK1 writer (the inverse of VpkArchive).
//
// === Why this exists ===
// The CS2 content depot (2347770) ships a ~1.7 GB pak01 archive set per platform,
// but the 7 content emitters read only ~50 MB of it (the .gameevents entries plus a
// handful of KV text files — see ContentPakSelector.EnumerateRequiredEntries). This
// writer TRIMS a source pak01 down to exactly those required entries, emitting a
// genuine, valid VPK1 that VpkArchive.Open reads with ZERO reader change:
//
//   pak01_dir.vpk   — a rebuilt directory tree carrying ONLY the required entries,
//                     each remapped to external archive index 0 with a recomputed
//                     cumulative offset into pak01_000.vpk. Crc32 / PreloadBytes /
//                     EntryLength are preserved VERBATIM from the source entry.
//   pak01_000.vpk   — the concatenation of the required entries' bodies, in Entries
//                     Ordinal (FullPath) order.
//
// Because each entry keeps its stored CRC32 and the bytes it resolves to are
// identical (same preload prefix + same body bytes), ReadEntryBytes returns
// byte-identical bytes and the CRC still verifies — so byte-identical validation
// reduces to "same entry bytes in → same JSON out".
//
// === Determinism ===
// The byte layout is a pure function of the source entries:
//   * bodies are laid out in FullPath Ordinal order (matching VpkArchive.Entries),
//   * the tree groups extensions / paths / filenames each sorted Ordinal,
//   * no timestamps or environment state ever enter the output.
// So repacking the same inputs yields byte-identical pak01_dir.vpk + pak01_000.vpk.
//
// === Fail-loud ===
// Each required entry is read via VpkArchive.ReadEntryBytes (CRC-verified); a
// missing/short/corrupt region throws BEFORE any output file is written. The two
// output files are written to a sibling .partial dir and atomically moved into
// place only on full success, so an interrupted repack never leaves a half-written
// _content/<gid> a later run would treat as authoritative.
//
// === independence ===
// Hand-rolled against the documented VPK1 on-disk format (see VpkArchive.cs header);
// no ValveResourceFormat / ValveKeyValue / VPK-tool dependency.

using System.Buffers.Binary;
using System.Text;

namespace Cs2SchemaTracker.Host.Vpk;

internal static class VpkTrimWriter
{
    private const uint Signature = 0x55AA1234u;
    private const uint Version1 = 1u;
    private const ushort EntryTerminator = 0xFFFF;

    /// <summary>The single external chunk index every trimmed body is remapped into.</summary>
    private const ushort TrimmedArchiveIndex = 0;

    /// <summary>
    /// Repack <paramref name="required"/> (entries belonging to <paramref name="source"/>) into a
    /// trimmed VPK1 pair written at <paramref name="dirVpkPath"/> (the <c>pak01_dir.vpk</c>) and its
    /// sibling <c>pak01_000.vpk</c> (derived from the dir path's <c>_dir</c> base name). The two files
    /// are staged in a sibling <c>.vpktrim</c> dir and moved into place atomically on success.
    /// </summary>
    public static void Write(VpkArchive source, IReadOnlyList<VpkDirectoryEntry> required, string dirVpkPath)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentException.ThrowIfNullOrEmpty(dirVpkPath);
        if (required.Count == 0)
        {
            // A trimmed VPK with no entries is never intended — the caller's gate (empty plan /
            // no .gameevents) is supposed to fire first. Refuse rather than emit a useless pak.
            throw new ArgumentException(
                "refusing to write a trimmed VPK with zero entries.", nameof(required));
        }

        var (dirBytes, chunkBytes) = Build(source, required);

        string dirFull = Path.GetFullPath(dirVpkPath);
        string targetDir = Path.GetDirectoryName(dirFull)
            ?? throw new ArgumentException($"VpkTrimWriter: cannot resolve parent of '{dirVpkPath}'.", nameof(dirVpkPath));
        string chunkFull = Path.Combine(targetDir, ChunkFileName(dirFull));

        // Stage into a sibling .vpktrim dir, then atomically move both files into place. Writing the
        // final files directly would risk a half-written pair if the process died between the two.
        string stageDir = targetDir + ".vpktrim";
        if (Directory.Exists(stageDir))
        {
            Directory.Delete(stageDir, recursive: true);
        }
        Directory.CreateDirectory(stageDir);
        try
        {
            string stagedDir = Path.Combine(stageDir, Path.GetFileName(dirFull));
            string stagedChunk = Path.Combine(stageDir, Path.GetFileName(chunkFull));
            File.WriteAllBytes(stagedDir, dirBytes);
            File.WriteAllBytes(stagedChunk, chunkBytes);

            Directory.CreateDirectory(targetDir);
            MoveOverwrite(stagedDir, dirFull);
            MoveOverwrite(stagedChunk, chunkFull);
        }
        finally
        {
            try
            { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Pure builder: produce the (dir.vpk bytes, chunk_000.vpk bytes) pair for <paramref name="required"/>.
    /// Exposed for tests that assert the byte layout without touching the filesystem.
    /// </summary>
    internal static (byte[] DirBytes, byte[] ChunkBytes) Build(
        VpkArchive source, IReadOnlyList<VpkDirectoryEntry> required)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(required);

        // Bodies laid out in FullPath Ordinal order (== VpkArchive.Entries order).
        var ordered = required
            .OrderBy(e => e.FullPath, StringComparer.Ordinal)
            .ToList();

        var chunk = new MemoryStream();
        var newOffset = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var entry in ordered)
        {
            // CRC-verified full read (preload + body). Fail-loud on any short/corrupt region.
            byte[] full = source.ReadEntryBytes(entry);
            int preloadLen = entry.PreloadBytes.Length;
            long bodyLen = entry.EntryLength;
            if (full.Length != preloadLen + bodyLen)
            {
                throw new InvalidDataException(
                    $"VpkTrimWriter: entry '{entry.FullPath}' decoded to {full.Length} bytes but the tree " +
                    $"records preload {preloadLen} + body {bodyLen}. Refusing to repack inconsistent bytes.");
            }

            newOffset[entry.FullPath] = checked((uint)chunk.Length);
            if (bodyLen > 0)
            {
                // Append ONLY the body (the preload stays inline in the rebuilt tree).
                chunk.Write(full, preloadLen, checked((int)bodyLen));
            }
        }

        byte[] tree = BuildTree(ordered, newOffset);

        var dir = new MemoryStream();
        WriteU32(dir, Signature);
        WriteU32(dir, Version1);
        WriteU32(dir, checked((uint)tree.Length));
        dir.Write(tree, 0, tree.Length);
        // v1: no embedded data section (every body is external in chunk 000).

        return (dir.ToArray(), chunk.ToArray());
    }

    /// <summary>
    /// Serialize the directory tree: extensions → paths → filenames, EACH sorted Ordinal.
    /// Every entry is remapped to external archive index 0 with its recomputed cumulative offset;
    /// Crc32 / preload / EntryLength are copied verbatim from the source entry.
    /// </summary>
    private static byte[] BuildTree(IReadOnlyList<VpkDirectoryEntry> entries, Dictionary<string, uint> newOffset)
    {
        var tree = new MemoryStream();

        // Group deterministically: Ordinal on the RAW triple tokens (the space-sentinel forms).
        var byExtension = entries
            .GroupBy(e => e.Extension, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var extGroup in byExtension)
        {
            WriteCString(tree, extGroup.Key);

            var byPath = extGroup
                .GroupBy(e => e.DirectoryPath, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);

            foreach (var pathGroup in byPath)
            {
                WriteCString(tree, pathGroup.Key);

                foreach (var entry in pathGroup.OrderBy(e => e.FileName, StringComparer.Ordinal))
                {
                    WriteCString(tree, entry.FileName);
                    WriteU32(tree, entry.Crc32);
                    WriteU16(tree, checked((ushort)entry.PreloadBytes.Length));
                    WriteU16(tree, TrimmedArchiveIndex);
                    WriteU32(tree, newOffset[entry.FullPath]);
                    WriteU32(tree, entry.EntryLength);
                    WriteU16(tree, EntryTerminator);
                    if (entry.PreloadBytes.Length > 0)
                    {
                        tree.Write(entry.PreloadBytes.Span);
                    }
                }
                tree.WriteByte(0); // end of files for this path
            }
            tree.WriteByte(0); // end of paths for this extension
        }
        tree.WriteByte(0); // end of extension list

        return tree.ToArray();
    }

    /// <summary>Derive the <c>pak01_000.vpk</c> name from a <c>pak01_dir.vpk</c> path.</summary>
    private static string ChunkFileName(string dirVpkPath)
    {
        string name = Path.GetFileName(dirVpkPath);
        const string suffix = "_dir.vpk";
        string baseName = name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(name);
        return $"{baseName}_000.vpk";
    }

    private static void MoveOverwrite(string src, string dst)
    {
        if (File.Exists(dst))
        {
            File.Delete(dst);
        }
        File.Move(src, dst);
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
}
