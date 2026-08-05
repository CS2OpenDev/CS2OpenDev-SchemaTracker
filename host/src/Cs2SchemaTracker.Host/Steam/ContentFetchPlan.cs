// 22 — byte-range-selective content-depot fetch plan.
//
// A whole-FILE content acquire would fetch every pak01_<NNN>.vpk that BACKS a
// resource our 7 content emitters read. But each resource is a tiny byte range
// inside one of those multi-hundred-MB chunk files, so a whole-file fetch pulls
// ~1.3 GB/build of mostly-unneeded bytes (and hammers the Steam CDN into 503s).
// This plan (built by ContentPakSelector.SelectContentByteRanges) narrows the
// fetch to the exact resource byte ranges: for each pak01_<NNN>.vpk we record the
// union of [EntryOffset, EntryOffset+EntryLength) body ranges of the resources we
// actually read. The acquirer then downloads ONLY the depot-chunks overlapping
// those ranges, writing a SPARSE pak01_<NNN>.vpk (just the needed regions populated).
//
// Determinism: both the whole-file set and the per-file range lists are
// Ordinal-sorted and de-duplicated, so the fetch plan + on-disk write order are
// identical for the same pak01_dir.vpk input.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// A half-open byte range <c>[Offset, Offset+Length)</c> inside a backing
/// <c>pak01_&lt;NNN&gt;.vpk</c> chunk file (the body location of one VPK resource).
/// </summary>
internal readonly record struct VpkByteRange(long Offset, long Length)
{
    /// <summary>Exclusive end offset.</summary>
    public long End => Offset + Length;

    /// <summary>True when this range overlaps the half-open range <c>[offset, offset+length)</c>.</summary>
    public bool Overlaps(long offset, long length)
        => Offset < offset + length && offset < End;
}

/// <summary>
/// The byte-range-selective content fetch plan: which whole files to fetch (the
/// pak01_dir.vpk index — needed in full), and which exact body byte ranges back
/// the resources our content emitters read inside each external pak01_&lt;NNN&gt;.vpk.
///
/// Keys are depot-relative, forward-slash manifest names (e.g.
/// <c>game/csgo/pak01_462.vpk</c>); <see cref="TryGetRanges"/> normalizes its
/// argument so a raw manifest name in either slash style / any case still matches.
/// </summary>
internal sealed record ContentFetchPlan(
    /// <summary>Files fetched in FULL (the directory index). Ordinal-sorted, de-duplicated.</summary>
    IReadOnlyList<string> WholeFiles,
    /// <summary>
    /// Per external chunk file: the union of required body byte ranges. Each list is
    /// Ordinal-by-offset sorted and de-duplicated/merged of identical ranges.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyList<VpkByteRange>> ChunkRanges)
{
    /// <summary>
    /// True iff nothing is selected. The two-phase caller treats this as the
    /// fail-loud "wrong VPK — no `.gameevents`" condition, preserving the
    /// whole-file selector's contract.
    /// </summary>
    public bool IsEmpty => WholeFiles.Count == 0 && ChunkRanges.Count == 0;

    /// <summary>
    /// Every manifest file the plan touches (whole files ∪ chunk-range keys),
    /// Ordinal-sorted + de-duplicated. This is the file-filter set for the acquire.
    /// </summary>
    public IReadOnlyList<string> AllFiles
    {
        get
        {
            var set = new SortedSet<string>(WholeFiles, StringComparer.Ordinal);
            foreach (var k in ChunkRanges.Keys)
            {
                set.Add(k);
            }
            return set.ToList();
        }
    }

    /// <summary>
    /// The acquire file-filter predicate: a manifest file is fetched iff its
    /// normalized name is one of <see cref="AllFiles"/>.
    /// </summary>
    public Func<string, bool> SelectedPredicate() => ContentPakSelector.SelectedPredicate(AllFiles);

    /// <summary>
    /// Merge several per-pak plans into one. Chunk-range keys are DISJOINT across paks (each pak's
    /// chunks live under its own base dir, e.g. <c>game/csgo/*</c> vs <c>game/core/*</c>), so the
    /// union is unambiguous; whole-file lists union + Ordinal-sort + de-dup. Lets a single acquire
    /// fetch every content pak's minimal byte ranges in one pass.
    /// </summary>
    public static ContentFetchPlan Merge(IEnumerable<ContentFetchPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        var whole = new SortedSet<string>(StringComparer.Ordinal);
        var ranges = new Dictionary<string, IReadOnlyList<VpkByteRange>>(StringComparer.Ordinal);
        foreach (var p in plans)
        {
            foreach (var w in p.WholeFiles)
            {
                whole.Add(w);
            }
            foreach (var kv in p.ChunkRanges)
            {
                // Disjoint by construction (per-pak base dirs). A duplicate key would be a caller bug
                // (same pak merged twice); last-writer-wins is harmless since the ranges are identical.
                ranges[kv.Key] = kv.Value;
            }
        }
        return new ContentFetchPlan(whole.ToList(), ranges);
    }

    /// <summary>
    /// Get the required body ranges for a manifest file name (any slash style / case).
    /// Returns false (and an empty list) when the file is to be fetched in FULL
    /// (a whole-file entry, e.g. the directory index) or is not in the plan at all —
    /// the caller fetches every chunk in that case.
    /// </summary>
    public bool TryGetRanges(string manifestFileName, out IReadOnlyList<VpkByteRange> ranges)
    {
        ranges = Array.Empty<VpkByteRange>();
        if (string.IsNullOrEmpty(manifestFileName))
        {
            return false;
        }
        var normalized = manifestFileName.Replace('\\', '/').TrimStart('/');
        foreach (var kvp in ChunkRanges)
        {
            if (string.Equals(kvp.Key, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ranges = kvp.Value;
                return true;
            }
        }
        return false;
    }
}
