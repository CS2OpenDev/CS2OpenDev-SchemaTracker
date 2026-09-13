// INCREMENTAL ("patch") content refresh: decide which required entries can be re-used out of the
// store copy we already hold, and which genuinely have to come off the CDN.
//
// === The problem this solves ===
// The content store is keyed by the content depot (2347770) manifest GID, so a store copy for a GID
// holds, BY DEFINITION, the exact bytes that GID ships. When
// ContentPakSelector.EnumerateRequiredEntries grows a resource (generation 2 added
// scripts/weapons.vdata_c), every existing store copy becomes a proper-but-OLD trim and has to be
// refreshed. Refreshing it by re-running the full two-phase acquire re-fetches the ENTIRE required
// set, because VpkTrimWriter used to read every entry from ONE source archive. Measured across 13
// builds spanning every era, that entire set is 93.9% localization tables (resource/csgo_<lang>.txt,
// 29 files, ~155 MB on build 24116939) and 5.8% items_game.txt — bytes that are already on disk,
// byte-identical, in the very trim about to be overwritten. weapons.vdata_c itself is 0.02%.
//
// === What this does ===
// Partition the FRESH required set (derived from the freshly-fetched pak01_dir.vpk, which is
// authoritative — it is the only place a new entry's offset/length/CRC exists) into:
//   * REUSE  — the stored trim already holds this exact entry; take its bytes from the store.
//   * FETCH  — the stored trim does not hold it; it must come off the CDN.
// The result is handed to VpkTrimWriter as a multi-source repack, so the new trim is assembled from
// both archives at once. ContentPakSelector.BuildByteRangePlan then builds the Phase-B byte-range
// fetch plan over the FETCH half only.
//
// Irreducible cost per GID (measured): the directory index is always fetched whole (3.97 MB in 2023,
// 7.47 MB in 2026 — it is the index, there is no sparse form of it) plus 0.8-1.8 MB of depot-chunk
// rounding per chunk file touched. So a patched refresh costs ~5-9 MB where a full one costs
// 20-199 MB.
//
// === Strict matching, and why a mismatch is fatal ===
// An entry is re-usable ONLY when its FullPath, EntryLength, Crc32 AND preload bytes are identical
// between the stored trim and the fresh index. Those four are exactly the fields VpkTrimWriter
// copies verbatim into the rebuilt tree, so equality on them is what makes the patched output
// byte-identical to a full re-fetch; nothing weaker would.
//
// Two entries under the same GID that disagree on any of them is NOT a cache-miss to paper over. The
// GID *is* the content identity: same GID means same bytes. A disagreement therefore means the store
// copy is corrupt, or it was written under the wrong GID — a corpus-integrity fault that would go on
// producing wrong artifacts for every build sharing that GID. So it THROWS, naming the entry and both
// CRCs, rather than silently re-fetching (which would hide the corruption) or silently trusting the
// store (which would bake it in). The reused bodies are CRC-verified again on the way out, because
// VpkArchive.ReadEntryBytes verifies every read.
//
// An ABSENT or UNPARSEABLE store copy is a different thing entirely and is not fatal: there is
// simply nothing to re-use, so the plan degrades to a full fetch with the reason recorded for the
// log. That matches ContentStore's existing fault-safe probe semantics — an unreadable store is the
// signal to rebuild it from a known-good source, and the full fetch IS that rebuild.
//
// === Re-use is gated on a store copy we can actually read from ===
// Those four matched fields are directory-TREE fields, and a tree can describe bytes the store does
// not hold. The legacy _content/<gid> is exactly that shape — a verbatim copy of the ORIGINAL index,
// so it matches every required entry on FullPath / EntryLength / Crc32 / preload while its bodies
// still point at original chunks (pak01_154.vpk and friends) that were never fetched. Marking those
// REUSE emptied the fetch half, which left the byte-range plan with no chunk ranges, which skipped
// Phase B, which left the repack reading bodies nobody had fetched: FileNotFoundException out of
// VpkArchive.ReadBody, identically on every re-run — --force included, because the sources were the
// broken store either way — and three such GIDs in a row stop a backfill campaign.
//
// So before ANY entry is re-used the copy must pass ContentStore.IsSelfContainedTrimLayout: every
// body at trimmed archive index 0, in bounds, in a pak01_000.vpk exactly as long as the tree accounts
// for. That is a walk of the already-parsed tree plus one file length — no body read. A copy that
// fails it is not a trim at all, so the plan degrades to the full fetch and the repack rewrites the
// store from fresh staging: the self-heal EnsureTrimmedStore has always promised for legacy/partial
// stores, and precisely the behaviour this path had before the patch partition existed.
//
// === Determinism ===
// The partition is a pure function of (fresh required set, stored tree): no timestamps, no
// environment, no ordering dependence. Both halves preserve the required set's Ordinal-by-FullPath
// order, and VpkTrimWriter re-sorts by FullPath anyway, so the trim bytes do not depend on how the
// partition split.

using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// The reuse-vs-fetch partition of one pak's required entry set for one content GID, plus the
/// multi-source repack input that realizes it. Build it with <see cref="Create"/>.
/// </summary>
internal sealed class ContentPatchPlan
{
    private ContentPatchPlan(
        IReadOnlyList<VpkTrimSource> trimSources,
        IReadOnlyList<VpkDirectoryEntry> fetch,
        int reusedCount,
        long reusedBytes,
        long fetchBytes,
        bool isPatch,
        string reason)
    {
        TrimSources = trimSources;
        Fetch = fetch;
        ReusedCount = reusedCount;
        ReusedBytes = reusedBytes;
        FetchBytes = fetchBytes;
        IsPatch = isPatch;
        Reason = reason;
    }

    /// <summary>
    /// EVERY required entry paired with the archive its bytes come from — re-used entries with the
    /// stored trim, must-fetch entries with the fresh staging archive. This is the exact input
    /// <see cref="VpkTrimWriter.Write(IReadOnlyList{VpkTrimSource}, string, VpkTrimSidecar?)"/> takes.
    /// </summary>
    public IReadOnlyList<VpkTrimSource> TrimSources { get; }

    /// <summary>
    /// The FRESH entries that must be fetched, in required-set order. Their ArchiveIndex /
    /// EntryOffset are the fresh index's, so they are what the Phase-B byte-range plan is built from.
    /// </summary>
    public IReadOnlyList<VpkDirectoryEntry> Fetch { get; }

    /// <summary>How many required entries came out of the store.</summary>
    public int ReusedCount { get; }

    /// <summary>Total decoded bytes (preload + body) re-used from the store, i.e. NOT re-downloaded.</summary>
    public long ReusedBytes { get; }

    /// <summary>
    /// Total body bytes of the must-fetch entries. The ACTUAL transfer is larger: the directory
    /// index is fetched whole and Steam's granularity is the depot chunk, so each touched chunk file
    /// rounds up. This is the resource-bytes figure, not the wire figure.
    /// </summary>
    public long FetchBytes { get; }

    /// <summary>True when a store copy was usable and at least one entry was re-used.</summary>
    public bool IsPatch { get; }

    /// <summary>Why this is (or is not) a patch — carried into the acquire log.</summary>
    public string Reason { get; }

    /// <summary>Required entries in total (re-used + must-fetch).</summary>
    public int RequiredCount => TrimSources.Count;

    /// <summary>One log line describing the partition, for the per-GID campaign record.</summary>
    public string Describe()
        => IsPatch
            ? $"PATCH — reusing {ReusedCount}/{RequiredCount} required entrie(s) ({ReusedBytes:N0} B) "
                + $"from the stored trim; fetching {Fetch.Count} ({FetchBytes:N0} resource B)"
            : $"FULL — {Reason}; fetching all {Fetch.Count} required entrie(s) ({FetchBytes:N0} resource B)";

    /// <summary>
    /// Partition <paramref name="required"/> (the FRESH required set, read from
    /// <paramref name="fresh"/>) against the store copy for <paramref name="gid"/> /
    /// <paramref name="pak"/>.
    ///
    /// No store copy, one whose <c>pak01_dir.vpk</c> will not parse, or one that is not a
    /// self-contained trimmed pair (its bodies are not all in its own <c>pak01_000.vpk</c>) yields a
    /// FULL plan (every entry fetched from <paramref name="fresh"/>) with the reason recorded. That
    /// third check runs BEFORE the per-entry comparison below, so the throw is reserved for a genuine
    /// trim that disagrees with the fresh index. Otherwise every required entry the store also holds
    /// — same FullPath, EntryLength, Crc32 and preload — is re-used from the store, and the rest are
    /// fetched.
    ///
    /// THROWS <see cref="InvalidDataException"/> when the store holds an entry of the same path that
    /// disagrees on length / CRC / preload: under one content GID that cannot happen unless the store
    /// is corrupt or mis-keyed, and either way re-fetching it silently would hide a corpus fault.
    /// </summary>
    public static ContentPatchPlan Create(
        VpkArchive fresh,
        IReadOnlyList<VpkDirectoryEntry> required,
        string contentStoreRoot,
        ulong gid,
        ContentPak pak)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentException.ThrowIfNullOrEmpty(contentStoreRoot);
        ArgumentNullException.ThrowIfNull(pak);

        var storeDirVpk = ContentStore.ResolveDirVpk(contentStoreRoot, gid, pak);
        if (!File.Exists(storeDirVpk))
        {
            return Full(fresh, required, "no store copy for this GID");
        }

        VpkArchive store;
        try
        {
            store = VpkArchive.Open(storeDirVpk);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                      or FileNotFoundException or UnauthorizedAccessException)
        {
            // Fault-safe, exactly like ContentStore's completeness probe: an unreadable store copy is
            // the signal to rebuild it from a known-good source, and the full fetch IS that rebuild.
            return Full(fresh, required, $"stored trim is unreadable ({ex.Message})");
        }

        // The four fields compared below all live in the TREE, and a tree can describe bytes the
        // store does not hold — which is exactly the legacy _content/<gid> shape, a verbatim copy of
        // the ORIGINAL index whose bodies still point at chunks that were never fetched. It matched
        // every required entry, so everything was marked REUSE, the fetch half emptied, Phase B was
        // skipped, and the repack threw FileNotFoundException for pak01_154.vpk — on every re-run,
        // --force included, because the sources were the broken store either way. So re-use first
        // has to prove the copy is a trim THIS store owns. Ahead of the loop on purpose: it keeps a
        // legacy copy from ever tripping the corruption throw below, and it is what keeps the
        // byte-range plan (built from plan.Fetch) non-empty so Phase B still runs.
        if (!ContentStore.IsSelfContainedTrimLayout(
                store, ContentStore.ResolveChunkVpk(contentStoreRoot, gid, pak), out var layoutReason))
        {
            return Full(fresh, required, $"stored trim is not a self-contained trimmed pair ({layoutReason})");
        }

        var sources = new List<VpkTrimSource>(required.Count);
        var fetch = new List<VpkDirectoryEntry>();
        int reusedCount = 0;
        long reusedBytes = 0;
        long fetchBytes = 0;

        foreach (var entry in required)
        {
            var stored = store.Find(entry.FullPath);
            if (stored is null)
            {
                // The newly-required resource (the whole point of the refresh), or a resource this
                // era gained. Must be fetched.
                sources.Add(new VpkTrimSource(fresh, entry));
                fetch.Add(entry);
                fetchBytes += entry.EntryLength;
                continue;
            }

            if (stored.EntryLength != entry.EntryLength
                || stored.Crc32 != entry.Crc32
                || !stored.PreloadBytes.Span.SequenceEqual(entry.PreloadBytes.Span))
            {
                throw new InvalidDataException(
                    $"content store copy for GID {gid} ({pak.BaseRelDir}) disagrees with the fresh "
                    + $"directory index on '{entry.FullPath}': stored CRC 0x{stored.Crc32:X8} / length "
                    + $"{stored.EntryLength} / preload {stored.PreloadBytes.Length}, fresh CRC "
                    + $"0x{entry.Crc32:X8} / length {entry.EntryLength} / preload "
                    + $"{entry.PreloadBytes.Length}. One content GID is one set of bytes, so this store "
                    + $"copy is corrupt or was written under the wrong GID. Refusing to patch it: "
                    + $"delete '{storeDirVpk}' and its sibling pak01_000.vpk to force a full re-fetch, "
                    + $"and check whether other GIDs share the fault.");
            }

            // Re-use: the STORED entry supplies both the bytes and the (identical) tree fields.
            sources.Add(new VpkTrimSource(store, stored));
            reusedCount++;
            reusedBytes += stored.TotalLength;
        }

        return new ContentPatchPlan(
            sources, fetch, reusedCount, reusedBytes, fetchBytes,
            isPatch: reusedCount > 0,
            reason: reusedCount > 0 ? "stored trim re-used" : "store copy holds none of the required entries");
    }

    /// <summary>The no-store fallback: every required entry read from the fresh staging archive.</summary>
    private static ContentPatchPlan Full(
        VpkArchive fresh, IReadOnlyList<VpkDirectoryEntry> required, string reason)
        => new(
            VpkTrimWriter.FromSingleSource(fresh, required),
            required,
            reusedCount: 0,
            reusedBytes: 0,
            fetchBytes: required.Sum(e => (long)e.EntryLength),
            isPatch: false,
            reason: reason);
}
