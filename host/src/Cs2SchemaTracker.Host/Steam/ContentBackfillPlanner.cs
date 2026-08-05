// Planner for the content backfill: which content-depot manifest GIDs still need a NEWLY-TRACKED
// content pak fetched (today: the engine core pak, resource/core.gameevents).
//
// The content store is keyed by the 2347770 manifest GID and shared across every (build, platform)
// whose content depot did not change, so a backfill is per-GID, NOT per-build: fetch the missing pak
// ONCE per unique GID and every build sharing it is covered. This planner is PURE (no Steam, no
// mutation): it walks the committed tuple dirs, reads each one's content GID from its
// manifest-record.json, drops the GIDs whose core pak is already in the store, and returns one target
// per still-missing GID with a representative tuple dir the caller can build a historical acquire spec
// from. The actual fetch (SteamAnonymousAcquirer) and the gameevents.json re-extract are the caller's.

namespace Cs2SchemaTracker.Host.Steam;

internal static class ContentBackfillPlanner
{
    /// <summary>
    /// One unit of backfill work: a content GID whose <paramref name="Pak"/> copy is absent from the
    /// store, plus a representative committed tuple dir (Ordinal-first) whose manifest-record.json
    /// carries that GID — the caller uses its <see cref="Record"/> to build the historical acquire spec.
    /// </summary>
    internal sealed record BackfillTarget(ulong ContentGid, ContentPak Pak, string RepresentativeTupleDir,
        ManifestRecord Record, int BuildCount);

    /// <summary>
    /// Plan the backfill of <paramref name="pak"/> (default: the engine core pak) across the store
    /// rooted at <paramref name="binariesRoot"/>. Enumerates every <c>&lt;root&gt;/&lt;build&gt;/&lt;platform&gt;</c>
    /// tuple dir that carries a manifest-record.json with a 2347770 content depot entry, groups them by
    /// content GID, and returns one <see cref="BackfillTarget"/> per GID whose <paramref name="pak"/>
    /// is NOT already a complete trim in the store. Deterministic: targets are Ordinal by GID, and each
    /// target's representative is the Ordinal-first tuple dir for that GID. Fail-loud on a
    /// present-but-corrupt manifest-record.json (via <see cref="ManifestRecord.ReadFromFile"/>).
    /// </summary>
    public static IReadOnlyList<BackfillTarget> Plan(string binariesRoot, ContentPak? pak = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(binariesRoot);
        pak ??= ContentPak.Core;

        var contentRoot = Path.Combine(binariesRoot, ContentStore.ContentDirName);

        // Group committed tuple dirs by their content GID. Representative = Ordinal-first tuple dir.
        var byGid = new SortedDictionary<ulong, (string Repr, ManifestRecord Record, int Count)>();
        foreach (var tupleDir in EnumerateCommittedTupleDirs(binariesRoot))
        {
            if (!ContentStore.TryReadContentGid(tupleDir, out var gid))
            {
                continue; // no content depot for this build — nothing to back-fill.
            }
            if (byGid.TryGetValue(gid, out var existing))
            {
                // Keep the Ordinal-first representative; just bump the build count.
                var repr = string.CompareOrdinal(tupleDir, existing.Repr) < 0 ? tupleDir : existing.Repr;
                byGid[gid] = (repr, existing.Record, existing.Count + 1);
            }
            else
            {
                var record = ManifestRecord.ReadFromFile(Path.Combine(tupleDir, ManifestRecord.FileName));
                byGid[gid] = (tupleDir, record, 1);
            }
        }

        var targets = new List<BackfillTarget>();
        foreach (var (gid, info) in byGid)
        {
            // Already have this pak (a complete trim) for this GID ⇒ nothing to fetch.
            if (ContentStore.IsCompleteTrimmedStore(contentRoot, gid, out _, pak))
            {
                continue;
            }
            targets.Add(new BackfillTarget(gid, pak, info.Repr, info.Record, info.Count));
        }
        return targets;
    }

    /// <summary>
    /// Enumerate every committed <c>&lt;root&gt;/&lt;build&gt;/&lt;platform&gt;</c> tuple dir that carries a
    /// manifest-record.json, in Ordinal path order. A build dir is any immediate child of
    /// <paramref name="binariesRoot"/> other than the <c>_content</c> / <c>_pics</c> sidecar dirs; a
    /// platform dir is any immediate child of a build dir holding a manifest-record.json.
    /// </summary>
    internal static IEnumerable<string> EnumerateCommittedTupleDirs(string binariesRoot)
    {
        if (!Directory.Exists(binariesRoot))
        {
            yield break;
        }
        foreach (var buildDir in Directory.EnumerateDirectories(binariesRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(buildDir);
            if (name.StartsWith('_')) // _content, _pics, and any other sidecar
            {
                continue;
            }
            foreach (var platformDir in Directory.EnumerateDirectories(buildDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(platformDir, ManifestRecord.FileName)))
                {
                    yield return platformDir;
                }
            }
        }
    }
}
