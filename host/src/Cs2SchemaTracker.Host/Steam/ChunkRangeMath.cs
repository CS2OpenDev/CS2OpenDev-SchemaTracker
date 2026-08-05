// byte-range → depot-chunk mapping (pure, chunk-type-agnostic).
//
// The byte-range-selective content acquire needs to answer two questions about a
// file's depot-manifest chunk list, given the resource byte ranges we must cover:
//   1. WHICH chunks overlap a required range (the ones to download)?
// 2. Do those chunks FULLY cover every required range (else fail loud)?
//
// Both are pure functions of (ranges, chunk offsets/lengths). They are factored
// here, generic over the chunk type, so the SteamKit2 download path and the unit
// tests share ONE implementation — the tests drive it with plain tuples instead of
// constructing SteamKit2 DepotManifest.ChunkData.

namespace Cs2SchemaTracker.Host.Steam;

internal static class ChunkRangeMath
{
    /// <summary>
    /// Select the chunks whose <c>[offset, offset+length)</c> overlaps ANY required range,
    /// returned in ascending-offset order (deterministic). Chunks backing only
    /// un-needed regions are dropped — those bytes are never downloaded (the sparse fetch).
    /// </summary>
    public static List<T> SelectOverlapping<T>(
        IReadOnlyList<VpkByteRange> ranges,
        IEnumerable<T> chunks,
        Func<T, long> offsetOf,
        Func<T, long> lengthOf)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(offsetOf);
        ArgumentNullException.ThrowIfNull(lengthOf);

        return chunks
            .Where(c => ranges.Any(r => r.Overlaps(offsetOf(c), lengthOf(c))))
            .OrderBy(offsetOf)
            .ToList();
    }

    /// <summary>
    /// Verify the <paramref name="selectedChunks"/> FULLY cover every required range. Depot
    /// manifests tile a file with contiguous, gap-free chunks, so the union of all chunks
    /// overlapping a range covers it; a gap means a malformed/truncated manifest. Returns true
    /// when fully covered; otherwise false with the first uncovered range and the gap offset.
    /// Pure over ascending-offset-sorted chunks.
    /// </summary>
    public static bool IsFullyCovered<T>(
        IReadOnlyList<VpkByteRange> ranges,
        IReadOnlyList<T> selectedChunks,
        Func<T, long> offsetOf,
        Func<T, long> lengthOf,
        out VpkByteRange uncoveredRange,
        out long gapOffset)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(selectedChunks);

        var ordered = selectedChunks.OrderBy(offsetOf).ToList();
        foreach (var range in ranges)
        {
            long cursor = range.Offset;
            foreach (var chunk in ordered)
            {
                long start = offsetOf(chunk);
                long end = start + lengthOf(chunk);
                if (start <= cursor && cursor < end)
                {
                    cursor = end; // advance across this contiguous chunk
                }
            }
            if (cursor < range.End)
            {
                uncoveredRange = range;
                gapOffset = cursor;
                return false;
            }
        }
        uncoveredRange = default;
        gapOffset = 0;
        return true;
    }
}
