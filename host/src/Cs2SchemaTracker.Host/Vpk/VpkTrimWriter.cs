// in-house VPK1 writer (the inverse of VpkArchive).
//
// === Why this exists ===
// The CS2 content depot (2347770) ships a ~1.7 GB pak01 archive set per platform,
// but the 8 content emitters read only ~50 MB of it (the .gameevents entries plus a
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
// === Multi-source repack (the patch path) ===
// The unit of work is a VpkTrimSource — ONE entry paired with the archive that entry
// belongs to — so a single trim may draw its entries from SEVERAL archives. That is
// what makes an incremental content refresh possible: when a stored trim already holds
// most of the required entries (byte-identical, because the store is keyed by the same
// content-depot manifest GID), those entries are read back OUT of the store and only
// the genuinely-new ones are fetched. See ContentPatchPlan, which does the partition.
//
// The single-source form — Build/Write(VpkArchive, IReadOnlyList<VpkDirectoryEntry>) —
// is a thin wrapper that pairs every entry with the one archive, so a full re-fetch
// and a patched refresh run through the SAME builder. Each source's Entry supplies
// BOTH the bytes (read from that source) and the tree fields (Crc32 / PreloadBytes /
// EntryLength, copied verbatim), so the caller is responsible for only ever pairing an
// entry with the archive it was parsed from.
//
// === Determinism ===
// The byte layout is a pure function of the source entries, NOT of which archive each
// one came from:
//   * bodies are laid out in FullPath Ordinal order (matching VpkArchive.Entries),
//   * the tree groups extensions / paths / filenames each sorted Ordinal,
//   * no timestamps or environment state ever enter the output.
// So repacking the same entries yields byte-identical pak01_dir.vpk + pak01_000.vpk
// whether they arrived from one archive or ten — which is the whole safety argument
// for the patch path: a patched trim is byte-identical to the full re-fetch it
// replaces. VpkTrimWriterTest pins that equivalence directly.
//
// === Fail-loud ===
// Each required entry is read via VpkArchive.ReadEntryBytes (CRC-verified); a
// missing/short/corrupt region throws BEFORE any output file is written. Two sources
// naming the SAME FullPath is a caller bug (the body layout would keep one offset and
// the tree would emit two entries) and throws for the same reason. The two output
// files are written to a sibling .partial dir and atomically moved into place only on
// full success, so an interrupted repack never leaves a half-written _content/<gid> a
// later run would treat as authoritative.
//
// Every read happens inside Build, which returns finished in-memory buffers, so a
// source archive may safely BE the file the caller is about to overwrite — the patch
// path reads the stored trim and then writes over it in the same call.
//
// === Sidecar ===
// A caller may hand Write an optional VpkTrimSidecar: one tiny text file staged and
// moved into place TOGETHER with the pair, so the trimmed bytes and the fact that
// describes them can never be out of step. The content store uses it to stamp the
// ContentPakSelector.RequiredSetGeneration the trim was built under — the trimmed
// tree itself cannot record which selection rules produced it, so that fact has to
// ride alongside. The sidecar content is supplied by the caller and must be a pure
// function of that fact (no timestamps, paths, or environment state) or the store
// stops being byte-reproducible.
//
// === independence ===
// Hand-rolled against the documented VPK1 on-disk format (see VpkArchive.cs header);
// no ValveResourceFormat / ValveKeyValue / VPK-tool dependency.

using System.Buffers.Binary;
using System.Text;

namespace Cs2SchemaTracker.Host.Vpk;

/// <summary>
/// A tiny text file written into the trimmed pak's directory as part of the SAME atomic move that
/// puts <c>pak01_dir.vpk</c> + <c>pak01_000.vpk</c> in place — so a trimmed store never carries
/// bytes without its sidecar, nor a sidecar without its bytes. <paramref name="FileName"/> is a
/// bare file name (no directory separators); <paramref name="Content"/> is written UTF-8, no BOM,
/// verbatim, and MUST be a pure function of what it records (no timestamps, paths, or environment
/// state) so repacking the same inputs stays byte-identical.
/// </summary>
internal readonly record struct VpkTrimSidecar(string FileName, string Content);

/// <summary>
/// One entry to repack, paired with the archive its bytes come from. <paramref name="Entry"/> MUST
/// be an entry of <paramref name="Source"/>: it supplies both the bytes (via
/// <see cref="VpkArchive.ReadEntryBytes"/>, CRC-verified) and the tree fields copied verbatim into
/// the rebuilt directory (Crc32 / PreloadBytes / EntryLength). Pairing an entry with a DIFFERENT
/// archive would repack whatever bytes live at that offset under the right name; the CRC is
/// recomputed from the bytes actually read, so such a mix-up fails loud at read time rather than
/// producing a plausible-looking wrong trim.
/// </summary>
internal readonly record struct VpkTrimSource(VpkArchive Source, VpkDirectoryEntry Entry);

internal static class VpkTrimWriter
{
    private const uint Signature = 0x55AA1234u;
    private const uint Version1 = 1u;
    private const ushort EntryTerminator = 0xFFFF;

    /// <summary>The single external chunk index every trimmed body is remapped into.</summary>
    private const ushort TrimmedArchiveIndex = 0;

    /// <summary>
    /// Pair every entry of <paramref name="required"/> with the ONE archive it was parsed from —
    /// the single-source shape every full (non-patched) repack uses.
    /// </summary>
    public static IReadOnlyList<VpkTrimSource> FromSingleSource(
        VpkArchive source, IReadOnlyList<VpkDirectoryEntry> required)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(required);
        var pairs = new List<VpkTrimSource>(required.Count);
        foreach (var entry in required)
        {
            pairs.Add(new VpkTrimSource(source, entry));
        }
        return pairs;
    }

    /// <summary>
    /// Single-source <see cref="Write(IReadOnlyList{VpkTrimSource}, string, VpkTrimSidecar?)"/>:
    /// every entry of <paramref name="required"/> is read from <paramref name="source"/>.
    /// </summary>
    public static void Write(VpkArchive source, IReadOnlyList<VpkDirectoryEntry> required, string dirVpkPath,
        VpkTrimSidecar? sidecar = null)
        => Write(FromSingleSource(source, required), dirVpkPath, sidecar);

    /// <summary>
    /// Repack <paramref name="sources"/> (each an entry plus the archive it belongs to) into a
    /// trimmed VPK1 pair written at <paramref name="dirVpkPath"/> (the <c>pak01_dir.vpk</c>) and its
    /// sibling <c>pak01_000.vpk</c> (derived from the dir path's <c>_dir</c> base name). The two files
    /// — plus <paramref name="sidecar"/> when supplied — are staged in a sibling <c>.vpktrim</c> dir
    /// and moved into place atomically on success, so the pair and its sidecar land together or not
    /// at all.
    ///
    /// Every source is read BEFORE anything is written, so one of the sources may be the very pair
    /// this call overwrites (the patch path re-uses the stored trim's own bytes).
    /// </summary>
    public static void Write(IReadOnlyList<VpkTrimSource> sources, string dirVpkPath,
        VpkTrimSidecar? sidecar = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(dirVpkPath);
        if (sources.Count == 0)
        {
            // A trimmed VPK with no entries is never intended — the caller's gate (empty plan /
            // no .gameevents) is supposed to fire first. Refuse rather than emit a useless pak.
            throw new ArgumentException(
                "refusing to write a trimmed VPK with zero entries.", nameof(sources));
        }

        if (sidecar is { } s
            && (string.IsNullOrEmpty(s.FileName)
                || s.FileName.Contains('/', StringComparison.Ordinal)
                || s.FileName.Contains('\\', StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"VpkTrimWriter: sidecar file name '{s.FileName}' must be a bare file name.", nameof(sidecar));
        }

        var (dirBytes, chunkBytes) = Build(sources);

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
            // UTF-8 without a BOM and with the caller's bytes verbatim: the sidecar is compared and
            // reproduced byte-for-byte, so no encoding preamble and no re-formatting.
            string? stagedSidecar = null;
            if (sidecar is { } side)
            {
                stagedSidecar = Path.Combine(stageDir, side.FileName);
                File.WriteAllText(stagedSidecar, side.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            Directory.CreateDirectory(targetDir);
            MoveOverwrite(stagedDir, dirFull);
            MoveOverwrite(stagedChunk, chunkFull);
            if (stagedSidecar is not null && sidecar is { } moved)
            {
                MoveOverwrite(stagedSidecar, Path.Combine(targetDir, moved.FileName));
            }
        }
        finally
        {
            try
            { if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Single-source <see cref="Build(IReadOnlyList{VpkTrimSource})"/>: every entry of
    /// <paramref name="required"/> is read from <paramref name="source"/>.
    /// </summary>
    internal static (byte[] DirBytes, byte[] ChunkBytes) Build(
        VpkArchive source, IReadOnlyList<VpkDirectoryEntry> required)
        => Build(FromSingleSource(source, required));

    /// <summary>
    /// Pure builder: produce the (dir.vpk bytes, chunk_000.vpk bytes) pair for
    /// <paramref name="sources"/>. Exposed for tests that assert the byte layout without touching
    /// the filesystem — including the one that proves splitting the same entries across two
    /// archives produces the same bytes as taking them all from one.
    /// </summary>
    internal static (byte[] DirBytes, byte[] ChunkBytes) Build(IReadOnlyList<VpkTrimSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // Bodies laid out in FullPath Ordinal order (== VpkArchive.Entries order), INDEPENDENT of
        // which archive each entry came from — that is what makes a patched trim byte-identical to
        // the full re-fetch it replaces.
        var ordered = sources
            .OrderBy(p => p.Entry.FullPath, StringComparer.Ordinal)
            .ToList();

        var chunk = new MemoryStream();
        var newOffset = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (source, entry) in ordered)
        {
            if (newOffset.ContainsKey(entry.FullPath))
            {
                // Two sources claiming the same logical path: the tree would emit both entries but
                // only one body offset survives. A partition bug, not recoverable data.
                throw new InvalidDataException(
                    $"VpkTrimWriter: entry '{entry.FullPath}' was supplied by more than one source. " +
                    "Refusing to repack an ambiguous entry set.");
            }

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

        byte[] tree = BuildTree(ordered.Select(p => p.Entry).ToList(), newOffset);

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
    /// Crc32 / preload / EntryLength are copied verbatim from the source entry. This works purely
    /// off those entry fields and never consults the backing archive, so an entry read out of a
    /// stored trim and the same entry read out of a fresh directory index serialize identically.
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
