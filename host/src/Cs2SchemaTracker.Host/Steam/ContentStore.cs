// content-addressed trimmed-VPK store path resolver.
//
// The trimmed content pak is stored ONCE per content-depot manifest GID (depot
// 2347770), shared across BOTH platforms AND every build whose content depot did
// not change:
//
//   <contentStoreRoot>/<gid>/game/csgo/pak01_dir.vpk
//   <contentStoreRoot>/<gid>/game/csgo/pak01_000.vpk
//
// where contentStoreRoot is the `_content` directory that lives directly under the
// binaries STORE ROOT (the dir that holds every <build>/<platform> tuple dir), i.e.
// `<binaries-root>/_content` for a store rooted at `<binaries-root>`. This matches
// the location the earlier python gameevents backfill already used.
//
// HostConfig.BinariesRoot IS the store root (`<binaries-root>`) and the per-build
// tuple dir is `<BinariesRoot>/<build>/<platform>`, so the content store is
// `<BinariesRoot>/_content` — a CHILD of BinariesRoot, not its parent. The store
// root reached by walking two levels up from a tuple dir is BinariesRoot itself; we
// derive contentStoreRoot from there.

using System.Globalization;

using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Host.Steam;

internal static class ContentStore
{
    /// <summary>The two files a PROPER trimmed store owns (everything else in the GID dir is legacy stray).</summary>
    private const string TrimDirVpkName = "pak01_dir.vpk";
    private const string TrimChunkVpkName = "pak01_000.vpk";

    /// <summary>The disposition of an <see cref="EnsureTrimmedStore"/> call (for caller logging).</summary>
    public enum StoreEnsureAction
    {
        /// <summary>A complete self-contained trim already existed and was left untouched (fast path).</summary>
        SkippedComplete,

        /// <summary>No store copy existed for this GID; a fresh trim was written.</summary>
        Built,

        /// <summary>An INCOMPLETE / legacy store existed (missing a required entry / stray external
        /// chunks) and was re-trimmed in place to a complete self-contained pair (auto self-heal).</summary>
        ReTrimmedIncomplete,

        /// <summary>A complete store existed but <c>--force</c> re-trimmed it anyway.</summary>
        ReTrimmedForced,
    }
    /// <summary>The CS2 cross-platform shared-content depot whose manifest GID keys the store.</summary>
    public const uint ContentDepotId = 2347770;

    /// <summary>The `_content` directory name under the binaries store root.</summary>
    public const string ContentDirName = "_content";

    /// <summary>
    /// The per-GID store dir for a pak's trimmed pair (<c>&lt;root&gt;/&lt;gid&gt;/&lt;pak base dir&gt;</c>,
    /// e.g. <c>.../game/csgo</c> or <c>.../game/core</c>). Both paks of a build share the ONE content
    /// GID, so they sit side-by-side under it. Defaults to the csgo pak so every existing caller is
    /// unchanged.
    /// </summary>
    public static string StoreDirForGid(string contentStoreRoot, ulong gid, ContentPak? pak = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentStoreRoot);
        return Path.Combine(
            contentStoreRoot,
            gid.ToString(CultureInfo.InvariantCulture),
            (pak ?? ContentPak.Csgo).BaseRelDir.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>The trimmed <c>pak01_dir.vpk</c> path for a GID inside the store (csgo pak by default).</summary>
    public static string ResolveDirVpk(string contentStoreRoot, ulong gid, ContentPak? pak = null)
        => Path.Combine(StoreDirForGid(contentStoreRoot, gid, pak), "pak01_dir.vpk");

    /// <summary>True iff a trimmed pak already exists for this GID (content-addressed idempotency).</summary>
    public static bool GidExists(string contentStoreRoot, ulong gid, ContentPak? pak = null)
        => File.Exists(ResolveDirVpk(contentStoreRoot, gid, pak));

    /// <summary>
    /// True iff a store copy exists for <paramref name="gid"/> AND it is a COMPLETE, self-contained
    /// proper trim: its <c>pak01_dir.vpk</c> parses, carries at least one <c>.gameevents</c> entry, and
    /// EVERY <see cref="ContentPakSelector.EnumerateRequiredEntries"/> entry resolves + reads +
    /// CRC-verifies (i.e. all bodies live in the local <c>pak01_000.vpk</c> / preload, none in an absent
    /// original external chunk). A legacy/partial <c>_content/&lt;gid&gt;</c> — the old python
    /// gameevents-only backfill whose dir tree still references the ORIGINAL external chunk indices
    /// (<c>pak01_154.vpk</c> …) that were never fetched, or one that predates the newer non-gameevents
    /// families — fails this check and MUST be re-trimmed (see <see cref="EnsureTrimmedStore"/>).
    ///
    /// This is a completeness PROBE, not an artifact-emitting read: a parse/read/CRC failure is the
    /// SIGNAL to re-trim (fault-safe → returns false with a reason), NOT an abort. The re-trim
    /// itself reads a KNOWN-GOOD source pak and fail-louds if THAT source is bad.
    /// </summary>
    public static bool IsCompleteTrimmedStore(string contentStoreRoot, ulong gid, out string reason,
        ContentPak? pak = null)
    {
        reason = "";
        var dirVpk = ResolveDirVpk(contentStoreRoot, gid, pak);
        if (!File.Exists(dirVpk))
        {
            reason = "no pak01_dir.vpk present in the store";
            return false;
        }
        try
        {
            var archive = VpkArchive.Open(dirVpk);
            var required = ContentPakSelector.EnumerateRequiredEntries(archive);
            if (required.Count == 0)
            {
                reason = "stored pak has no '.gameevents' entries (legacy/wrong content)";
                return false;
            }
            foreach (var entry in required)
            {
                // CRC-verified full read; throws on a missing external chunk / short region / CRC mismatch.
                _ = archive.ReadEntryBytes(entry);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                      or FileNotFoundException or UnauthorizedAccessException)
        {
            reason = $"stored pak is not a complete self-contained trim: {ex.Message}";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Delete any stray <c>pak01_*.vpk</c> in the GID store dir that a proper trim does NOT own
    /// (keeps only <c>pak01_dir.vpk</c> + <c>pak01_000.vpk</c>). Used after a (re-)trim so a legacy
    /// full-size <c>_content/&lt;gid&gt;</c> (which held the ORIGINAL external chunks, e.g.
    /// <c>pak01_154.vpk</c>) is reduced to the two-file trimmed pair, reclaiming the legacy chunk bytes.
    /// Deterministic (Ordinal order) + idempotent. Returns the count removed.
    /// </summary>
    public static int PruneStrayStoreChunks(string contentStoreRoot, ulong gid, ContentPak? pak = null)
    {
        var storeDir = StoreDirForGid(contentStoreRoot, gid, pak);
        if (!Directory.Exists(storeDir))
        {
            return 0;
        }
        int removed = 0;
        foreach (var f in Directory.EnumerateFiles(storeDir, "pak01_*.vpk")
                     .OrderBy(x => x, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(f);
            if (string.Equals(name, TrimDirVpkName, StringComparison.Ordinal) ||
                string.Equals(name, TrimChunkVpkName, StringComparison.Ordinal))
            {
                continue;
            }
            File.Delete(f);
            removed++;
        }
        return removed;
    }

    /// <summary>
    /// Ensure a COMPLETE trimmed store copy exists for <paramref name="gid"/>, self-healing an
    /// incomplete/legacy one WITHOUT needing <c>--force</c>. Decision:
    /// <list type="bullet">
    /// <item>a complete self-contained trim already present + !force ⇒ skip (fast, no I/O beyond the probe);</item>
    /// <item>otherwise trim <paramref name="required"/> from <paramref name="source"/> into the store
    /// (<see cref="VpkTrimWriter.Write"/>, CRC-verified reads —) and prune any stray legacy chunks.</item>
    /// </list>
    /// Idempotent and deterministic: the trimmed bytes are a pure function of the source
    /// entries. Both the acquire repack and <c>content-store migrate</c> route through here so a fresh
    /// acquire OR a migrate over a legacy <c>_content/&lt;gid&gt;</c> self-heals identically.
    /// </summary>
    public static StoreEnsureAction EnsureTrimmedStore(
        VpkArchive source,
        IReadOnlyList<VpkDirectoryEntry> required,
        string contentStoreRoot,
        ulong gid,
        bool force,
        out string detail,
        ContentPak? pak = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentException.ThrowIfNullOrEmpty(contentStoreRoot);

        bool complete = IsCompleteTrimmedStore(contentStoreRoot, gid, out var incompleteReason, pak);
        if (complete && !force)
        {
            detail = "already a complete self-contained trim";
            return StoreEnsureAction.SkippedComplete;
        }

        bool existed = GidExists(contentStoreRoot, gid, pak);
        var storeDirVpk = ResolveDirVpk(contentStoreRoot, gid, pak);
        // Fail-loud: a required entry that can't be read from the source throws here.
        VpkTrimWriter.Write(source, required, storeDirVpk);
        int pruned = PruneStrayStoreChunks(contentStoreRoot, gid, pak);
        string prunedNote = pruned > 0 ? $"; pruned {pruned} stray legacy chunk(s)" : "";

        if (complete)   // implies force (the !force complete case returned above).
        {
            detail = $"re-trimmed (--force){prunedNote}";
            return StoreEnsureAction.ReTrimmedForced;
        }
        if (existed)
        {
            detail = $"self-healed incomplete/legacy store ({incompleteReason}){prunedNote}";
            return StoreEnsureAction.ReTrimmedIncomplete;
        }
        detail = $"built{prunedNote}";
        return StoreEnsureAction.Built;
    }

    /// <summary>
    /// Read the content depot (2347770) manifest GID from a tuple dir's <c>manifest-record.json</c>.
    /// Returns false when there is no record or no content depot entry. Fail-loud on a
    /// PRESENT-but-corrupt record (via <see cref="ManifestRecord.ReadFromFile"/>).
    /// </summary>
    public static bool TryReadContentGid(string binariesTupleDir, out ulong gid)
    {
        ArgumentException.ThrowIfNullOrEmpty(binariesTupleDir);
        gid = 0;
        var recordPath = Path.Combine(binariesTupleDir, ManifestRecord.FileName);
        if (!File.Exists(recordPath))
        {
            return false;
        }
        var record = ManifestRecord.ReadFromFile(recordPath);
        var content = record.Depots.FirstOrDefault(d => d.DepotId == ContentDepotId);
        if (content is null)
        {
            return false;
        }
        gid = content.ManifestId;
        return true;
    }

    /// <summary>
    /// Resolve the trimmed <c>pak01_dir.vpk</c> for a binaries TUPLE dir via its
    /// <c>manifest-record.json</c>: read the content depot (2347770) GID and resolve
    /// <c>&lt;storeRoot&gt;/_content/&lt;gid&gt;/game/csgo/pak01_dir.vpk</c>. Returns false when there is no
    /// record, no content depot entry, no derivable store root, or the store copy is absent — the
    /// caller then falls back to a co-located pak (migration / dev trees). Fail-loud: a
    /// PRESENT-but-corrupt manifest-record.json throws via <see cref="ManifestRecord.ReadFromFile"/>.
    /// </summary>
    public static bool TryResolveStoreDirVpk(string binariesTupleDir, out string dirVpkPath)
        => TryResolveStorePak(binariesTupleDir, ContentPak.Csgo, out dirVpkPath);

    /// <summary>
    /// <see cref="TryResolveStoreDirVpk"/> for an explicit <paramref name="pak"/>: resolve
    /// <c>&lt;storeRoot&gt;/_content/&lt;gid&gt;/&lt;pak base dir&gt;/pak01_dir.vpk</c>. Returns false when
    /// there is no record, no content depot entry, no derivable store root, or that pak's copy is
    /// absent — for the (non-required) core pak, absence is the normal back-compat path (an existing
    /// store built before the core pak was tracked simply has no <c>game/core</c> subtree), and the
    /// caller emits csgo-only events with an explicit note.
    /// </summary>
    public static bool TryResolveStorePak(string binariesTupleDir, ContentPak pak, out string dirVpkPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(binariesTupleDir);
        ArgumentNullException.ThrowIfNull(pak);
        dirVpkPath = "";

        var recordPath = Path.Combine(binariesTupleDir, ManifestRecord.FileName);
        if (!File.Exists(recordPath))
        {
            return false;
        }
        var record = ManifestRecord.ReadFromFile(recordPath); // throws on a corrupt record.
        var content = record.Depots.FirstOrDefault(d => d.DepotId == ContentDepotId);
        if (content is null)
        {
            return false;
        }
        var root = RootForTupleDir(binariesTupleDir);
        if (root is null)
        {
            return false;
        }
        var candidate = ResolveDirVpk(root, content.ManifestId, pak);
        if (!File.Exists(candidate))
        {
            return false;
        }
        dirVpkPath = candidate;
        return true;
    }

    /// <summary>
    /// Derive the <c>_content</c> store root from a binaries TUPLE dir
    /// (<c>&lt;storeRoot&gt;/&lt;build&gt;/&lt;platform&gt;</c>): walk two levels up to the store
    /// root, then append <c>_content</c>. Returns null when the tuple dir is too shallow (a dev
    /// tree with no store root above it), which lets the extract path fall back to the co-located pak.
    /// </summary>
    public static string? RootForTupleDir(string tupleDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(tupleDir);
        var full = Path.GetFullPath(tupleDir);
        var buildDir = Path.GetDirectoryName(full);          // <storeRoot>/<build>
        var storeRoot = buildDir is null ? null : Path.GetDirectoryName(buildDir); // <storeRoot>
        if (string.IsNullOrEmpty(storeRoot))
        {
            return null;
        }
        return Path.Combine(storeRoot, ContentDirName);
    }
}
