// content-addressed trimmed-VPK store path resolver.
//
// The trimmed content pak is stored ONCE per content-depot manifest GID (depot
// 2347770), shared across BOTH platforms AND every build whose content depot did
// not change:
//
//   <contentStoreRoot>/<gid>/game/csgo/pak01_dir.vpk
//   <contentStoreRoot>/<gid>/game/csgo/pak01_000.vpk
//   <contentStoreRoot>/<gid>/game/csgo/trim-generation.json
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
//
// === The trim-generation marker ===
// A stored trim holds ONLY the entries ContentPakSelector.EnumerateRequiredEntries selected when it
// was written, and the VPK tree records nothing about which rule set that was. So asking the stored
// pak "are all your required entries present?" is circular — the required set derived FROM a stale
// trim is exactly what the stale trim contains, every entry reads and CRC-verifies, and the answer
// is always yes. That is how a store trimmed before a resource was added stays "complete" forever
// while extract records a FALSE CONTENT_NOT_SHIPPED_THIS_ERA omission for it.
//
// trim-generation.json breaks the circle: one deterministic line,
// {"required_set_generation":<n>}, written into the GID/pak dir by the same atomic move that lands
// the trimmed pair (never separately), holding ContentPakSelector.RequiredSetGeneration as of the
// trim. IsCompleteTrimmedStore treats absent / unparseable / below-current as INCOMPLETE, which is
// all EnsureTrimmedStore and ContentBackfillPlanner need to start self-healing and planning.
//
// The gate applies to REQUIRED paks only (ContentPak.Csgo). See IsCompleteTrimmedStore for why the
// non-required core pak is stamped but not enforced.
//
// === Refreshing a stale trim without re-downloading it ===
// A generation bump marks every existing store copy stale, but the bytes in those copies are still
// exactly right for their GID — the GID IS the content identity. So EnsureTrimmedStore also takes a
// MULTI-SOURCE repack (a VpkTrimSource list): ContentPatchPlan partitions the fresh required set into
// entries the store copy already holds byte-for-byte and entries that genuinely have to be fetched,
// and the refresh re-reads the former straight out of the store instead of off the CDN. Measured on
// the generation-2 (weapons.vdata_c) campaign that is ~5-9 MB per GID instead of 20-199 MB, because
// 93.9% of the required bytes are localization tables that did not change.
//
// Determinism is what makes that safe: the trimmed bytes are a pure function of the entries, not of
// which archive supplied each one, so a patched trim is byte-identical to the full re-fetch it
// replaces (VpkTrimWriterTest pins the equivalence). Fail-loud is what keeps it honest: an entry that
// disagrees between the store copy and the fresh index under ONE GID means the store is corrupt or
// mis-keyed, and ContentPatchPlan throws rather than papering over it.
//
// Re-use is additionally gated on the store copy having the LAYOUT of a self-contained trimmed pair
// (IsSelfContainedTrimLayout): tree fields alone do not prove a body is readable, and a legacy /
// partial _content/<gid> whose tree still points at ORIGINAL external chunks matches them all. Such a
// copy is re-trimmed from fresh staging exactly as it was before the patch path existed, rather than
// being repacked out of bodies it does not hold.

using System.Globalization;
using System.Text;
using System.Text.Json;

using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Host.Steam;

internal static class ContentStore
{
    /// <summary>The two pak files a PROPER trimmed store owns (every OTHER pak01_*.vpk in the GID
    /// dir is legacy stray). The third file a proper store owns is
    /// <see cref="TrimMarkerFileName"/>.</summary>
    private const string TrimDirVpkName = "pak01_dir.vpk";
    private const string TrimChunkVpkName = "pak01_000.vpk";

    /// <summary>
    /// The single external archive index <see cref="VpkTrimWriter"/> remaps EVERY trimmed body into.
    /// Duplicated here because the writer's own constant is private, and because the value is part of
    /// the ON-DISK shape of a trimmed pair rather than an implementation detail of the writer: a
    /// stored entry naming any other index — an original chunk like 154, or the 0x7FFF embedded
    /// sentinel — keeps its body somewhere this store copy does not own.
    /// </summary>
    private const ushort TrimmedArchiveIndex = 0;

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
    /// The trim-generation marker file, written into the GID/pak dir beside <c>pak01_dir.vpk</c>.
    /// Deliberately OUTSIDE the <c>pak01_*.vpk</c> shape <see cref="PruneStrayStoreChunks"/>
    /// sweeps, and explicitly kept there as well.
    /// </summary>
    public const string TrimMarkerFileName = "trim-generation.json";

    /// <summary>The single property the marker carries.</summary>
    private const string TrimMarkerProperty = "required_set_generation";

    /// <summary>
    /// The EXACT marker file content for <paramref name="generation"/>: one JSON object and a
    /// trailing newline, invariant-formatted. A pure function of the generation — no timestamps, no
    /// paths, no environment state — so two stores at the same generation are byte-identical here.
    /// </summary>
    internal static string TrimMarkerContent(int generation)
        => "{\"" + TrimMarkerProperty + "\":"
            + generation.ToString(CultureInfo.InvariantCulture) + "}\n";

    /// <summary>The marker path for a GID's pak dir (csgo pak by default).</summary>
    public static string TrimMarkerPath(string contentStoreRoot, ulong gid, ContentPak? pak = null)
        => Path.Combine(StoreDirForGid(contentStoreRoot, gid, pak), TrimMarkerFileName);

    /// <summary>
    /// Stamp the marker for <paramref name="generation"/> into an EXISTING store dir. Production
    /// NEVER calls this: <see cref="EnsureTrimmedStore"/> hands the marker to
    /// <see cref="VpkTrimWriter.Write"/> so it moves into place with the trimmed pair and the two
    /// can never disagree. This is the seam for tests that pre-place a store by hand (and for a
    /// future one-shot re-stamp migration).
    /// </summary>
    internal static void WriteTrimGenerationMarker(
        string contentStoreRoot, ulong gid, int generation, ContentPak? pak = null)
    {
        var path = TrimMarkerPath(contentStoreRoot, gid, pak);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, TrimMarkerContent(generation),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Read the required-set generation a stored trim was written under. False when the marker file
    /// is absent, is not a JSON object, lacks the property, or the property is not an integer — the
    /// caller distinguishes absent from unparseable by testing <see cref="File.Exists"/> first.
    /// </summary>
    internal static bool TryReadTrimGeneration(
        string contentStoreRoot, ulong gid, ContentPak? pak, out int generation)
    {
        generation = 0;
        var path = TrimMarkerPath(contentStoreRoot, gid, pak);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(TrimMarkerProperty, out var value)
                || value.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            return value.TryGetInt32(out generation);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            generation = 0;
            return false;
        }
    }

    /// <summary>
    /// True iff the store copy for <paramref name="gid"/> was trimmed under the CURRENT
    /// <see cref="ContentPakSelector.RequiredSetGeneration"/> (or a newer one — a store written by a
    /// later build of the tool is a superset, never something to downgrade). Absent, unparseable, or
    /// below-current ⇒ false with a <paramref name="reason"/> naming BOTH generations. This is the
    /// single question the extract-time guard and the store-completeness probe both ask.
    /// </summary>
    public static bool IsTrimGenerationCurrent(
        string contentStoreRoot, ulong gid, ContentPak? pak, out string reason)
    {
        int current = ContentPakSelector.RequiredSetGeneration;
        if (!File.Exists(TrimMarkerPath(contentStoreRoot, gid, pak)))
        {
            reason =
                $"stored trim carries no '{TrimMarkerFileName}', so it was written for required-set "
                + $"generation {ContentPakSelector.FirstMarkedGeneration - 1} or older (markers begin at "
                + $"generation {ContentPakSelector.FirstMarkedGeneration}); current is {current}";
            return false;
        }
        if (!TryReadTrimGeneration(contentStoreRoot, gid, pak, out int stored))
        {
            reason =
                $"stored trim's '{TrimMarkerFileName}' is unreadable, so its required-set generation "
                + $"is treated as {ContentPakSelector.FirstMarkedGeneration - 1} or older; current is {current}";
            return false;
        }
        if (stored < current)
        {
            reason = $"stored trim was written for required-set generation {stored}; current is {current}";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// Whether the trim-generation gate is ENFORCED for <paramref name="pak"/>.
    ///
    /// Only REQUIRED paks (<see cref="ContentPak.Csgo"/>) are gated. Every resource
    /// <see cref="ContentPakSelector.EnumerateRequiredEntries"/> has ever gained — items_game,
    /// gamemodes, the localization tables, surfaceproperties, propdata/collision, the map overviews,
    /// weapons.vdata_c — lives in the csgo pak, so a generation bump never changes what the
    /// gameevents-only core pak would select: its selection is the `.gameevents` rule, unchanged
    /// since generation 1. Enforcing the gate there would mark every core store stale on a
    /// csgo-only rule change and force ~387 authenticated Steam re-fetches that cannot alter one
    /// emitted byte, while breaking the graceful absence semantics
    /// <see cref="SteamAnonymousAcquirer"/>'s core leg and <see cref="ContentBackfillPlanner"/>
    /// depend on. The marker is still WRITTEN for the core pak (so the fact is on disk if the core
    /// selection ever does change and this gate has to widen), just not enforced.
    /// </summary>
    private static bool GenerationGateApplies(ContentPak pak) => pak.Required;

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

    /// <summary>The trimmed <c>pak01_000.vpk</c> (body file) path for a GID inside the store (csgo pak by default).</summary>
    public static string ResolveChunkVpk(string contentStoreRoot, ulong gid, ContentPak? pak = null)
        => Path.Combine(StoreDirForGid(contentStoreRoot, gid, pak), TrimChunkVpkName);

    /// <summary>
    /// True iff <paramref name="store"/> has the LAYOUT <see cref="VpkTrimWriter"/> writes — every
    /// body at trimmed archive index 0, in bounds, inside a <c>pak01_000.vpk</c> whose length is
    /// EXACTLY what the tree accounts for. This is the CHEAP structural sibling of
    /// <see cref="IsCompleteTrimmedStore"/>: it walks the already-parsed entry tree and reads one
    /// file length, never a body, so it costs nothing beside that probe's ~50 MB CRC-verified sweep.
    ///
    /// It answers the one question <see cref="ContentPatchPlan.Create"/> must ask before re-using
    /// anything: can this copy actually PRODUCE the bytes it appears to hold? A legacy
    /// <c>_content/&lt;gid&gt;</c> — the old python gameevents-only backfill, a verbatim copy of the
    /// ORIGINAL index still pointing at <c>pak01_154.vpk</c> and friends that were never fetched —
    /// answers no on its first entry even though its tree fields match the fresh index on every
    /// required entry. The final exact-length equality is what additionally catches a full-size but
    /// partially-fetched body file whose per-entry bounds all pass.
    /// </summary>
    public static bool IsSelfContainedTrimLayout(VpkArchive store, string chunkVpkPath, out string reason)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(chunkVpkPath);

        if (!File.Exists(chunkVpkPath))
        {
            reason = $"the store copy has no '{TrimChunkVpkName}' beside its {TrimDirVpkName}";
            return false;
        }

        long chunkLength = new FileInfo(chunkVpkPath).Length;
        long bodyBytes = 0;
        foreach (var entry in store.Entries)
        {
            if (entry.EntryLength == 0)
            {
                // Wholly preloaded (or empty): its bytes ride in the tree, so it needs no body file
                // and says nothing about whether this copy owns its bodies.
                continue;
            }
            if (entry.ArchiveIndex != TrimmedArchiveIndex)
            {
                reason =
                    $"stored entry '{entry.FullPath}' keeps its body in archive index "
                    + $"{entry.ArchiveIndex}, not the trimmed pair's '{TrimChunkVpkName}'";
                return false;
            }
            long end = (long)entry.EntryOffset + entry.EntryLength;
            if (end > chunkLength)
            {
                reason =
                    $"stored entry '{entry.FullPath}' needs bytes [{entry.EntryOffset}, {end}) of "
                    + $"'{TrimChunkVpkName}', which is {chunkLength} bytes";
                return false;
            }
            bodyBytes += entry.EntryLength;
        }

        if (bodyBytes != chunkLength)
        {
            reason =
                $"'{TrimChunkVpkName}' is {chunkLength} bytes but the stored tree accounts for "
                + $"{bodyBytes} — it is not the exact concatenation a trimmed pair is";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// True iff a store copy exists for <paramref name="gid"/> AND it is a COMPLETE, self-contained
    /// proper trim of the CURRENT required set:
    /// <list type="number">
    /// <item>its <c>trim-generation.json</c> is present, parseable, and at or above
    /// <see cref="ContentPakSelector.RequiredSetGeneration"/> (REQUIRED paks only — see
    /// <see cref="GenerationGateApplies"/>);</item>
    /// <item>its <c>pak01_dir.vpk</c> parses and carries at least one <c>.gameevents</c> entry;</item>
    /// <item>EVERY <see cref="ContentPakSelector.EnumerateRequiredEntries"/> entry resolves + reads +
    /// CRC-verifies (i.e. all bodies live in the local <c>pak01_000.vpk</c> / preload, none in an
    /// absent original external chunk).</item>
    /// </list>
    /// Checks 2 and 3 catch CORRUPTION and the legacy/partial <c>_content/&lt;gid&gt;</c> shape (the
    /// old python gameevents-only backfill whose dir tree still references the ORIGINAL external
    /// chunk indices — <c>pak01_154.vpk</c> … — that were never fetched). They CANNOT catch a proper
    /// trim of an OLDER required set: the required set they derive comes from the stored pak itself,
    /// so a path the trim never kept is a path they never look for. Check 1 is the out-of-band fact
    /// that closes that hole, and it runs FIRST because it is one small file read rather than a
    /// CRC-verified read of every entry — the difference matters across a ~387-GID store sweep.
    ///
    /// This is a completeness PROBE, not an artifact-emitting read: a stale marker or a
    /// parse/read/CRC failure is the SIGNAL to re-trim (fault-safe → returns false with a reason),
    /// NOT an abort. The re-trim itself reads a KNOWN-GOOD source pak and fail-louds if THAT source
    /// is bad. Callers that have NO source to re-trim from (extract) must fail loud instead — see
    /// <see cref="IsTrimGenerationCurrent"/>.
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
        if (GenerationGateApplies(pak ?? ContentPak.Csgo)
            && !IsTrimGenerationCurrent(contentStoreRoot, gid, pak, out var generationReason))
        {
            reason = generationReason;
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
    ///
    /// The sweep is scoped to the <c>pak01_*.vpk</c> shape and the KEEP set below is the contract:
    /// <see cref="TrimMarkerFileName"/> is listed there even though the glob could not match it, so
    /// that widening the glob later cannot silently delete the marker and re-open the stale-store
    /// hole this store's completeness check depends on.
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
                string.Equals(name, TrimChunkVpkName, StringComparison.Ordinal) ||
                string.Equals(name, TrimMarkerFileName, StringComparison.Ordinal))
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
    /// "Complete" now includes "trimmed under the current
    /// <see cref="ContentPakSelector.RequiredSetGeneration"/>", so a store built before a resource
    /// was added to the required set takes the SECOND branch and self-heals as
    /// <see cref="StoreEnsureAction.ReTrimmedIncomplete"/> — this method needed no other change.
    ///
    /// Idempotent and deterministic: the trimmed bytes are a pure function of the source
    /// entries. Both the acquire repack and <c>content-store migrate</c> route through here so a fresh
    /// acquire OR a migrate over a legacy <c>_content/&lt;gid&gt;</c> self-heals identically. The
    /// generation marker is handed to <see cref="VpkTrimWriter.Write"/> rather than written after
    /// it, so it rides the SAME atomic move as the trimmed pair — no window in which a store has
    /// bytes without a marker, or a marker without bytes. It is written for EVERY pak; only
    /// required paks enforce it (<see cref="GenerationGateApplies"/>).
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
        return EnsureTrimmedStore(
            VpkTrimWriter.FromSingleSource(source, required),
            contentStoreRoot, gid, force, out detail, pak);
    }

    /// <summary>
    /// <see cref="EnsureTrimmedStore(VpkArchive, IReadOnlyList{VpkDirectoryEntry}, string, ulong, bool, out string, ContentPak?)"/>
    /// for a MULTI-SOURCE repack: each entry arrives paired with the archive its bytes come from, so
    /// one trim can be assembled from several archives at once.
    ///
    /// This is what the incremental ("patch") refresh writes. <see cref="ContentPatchPlan"/> pairs the
    /// entries the store copy already holds with THAT store copy and the rest with the fresh staging
    /// archive, so refreshing a stale trim re-downloads only what genuinely changed instead of the
    /// whole required set. The store copy being one of the sources is safe on purpose:
    /// <see cref="VpkTrimWriter.Write(IReadOnlyList{VpkTrimSource}, string, VpkTrimSidecar?)"/>
    /// finishes every CRC-verified read into memory before it stages or moves a single byte, so the
    /// pair it overwrites may be the pair it read from.
    ///
    /// Determinism is unchanged: the trimmed bytes are a pure function of the entries, not of which
    /// archive each one came from, so a patched trim is byte-identical to the full re-fetch it
    /// replaces.
    /// </summary>
    public static StoreEnsureAction EnsureTrimmedStore(
        IReadOnlyList<VpkTrimSource> sources,
        string contentStoreRoot,
        ulong gid,
        bool force,
        out string detail,
        ContentPak? pak = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrEmpty(contentStoreRoot);

        bool complete = IsCompleteTrimmedStore(contentStoreRoot, gid, out var incompleteReason, pak);
        if (complete && !force)
        {
            detail = "already a complete self-contained trim";
            return StoreEnsureAction.SkippedComplete;
        }

        bool existed = GidExists(contentStoreRoot, gid, pak);
        var storeDirVpk = ResolveDirVpk(contentStoreRoot, gid, pak);
        // Fail-loud: a required entry that can't be read from the source throws here. The marker
        // moves into place WITH the pair (single atomic success path), never as a follow-up write.
        VpkTrimWriter.Write(sources, storeDirVpk,
            new VpkTrimSidecar(
                TrimMarkerFileName, TrimMarkerContent(ContentPakSelector.RequiredSetGeneration)));
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
