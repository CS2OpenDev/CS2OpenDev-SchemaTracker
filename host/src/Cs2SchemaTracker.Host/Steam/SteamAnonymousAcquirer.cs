// Anonymous SteamKit2-backed depot downloader.
//
// SteamKit2 (library) chosen over DepotDownloader (subprocess): keeps all error/retry/
// resume logic in our process so fail-loud is enforced by code we own, and avoids fragile
// subprocess-string-parsing of chunk failures. SteamKit2 is an MIT Steam-protocol library,
// not a CS2-data project, so it stays within the independence rule.
//
// Flow (anonymous, since CS2 is free-to-play): Connect → LogOnAnonymous → PICS resolve of
// each depot's manifestid at the requested buildid → per-depot decryption key → pick a CDN
// server → download manifest → per file, allocate + download each chunk (SteamKit2 verifies
// each chunk's SHA-1 against the manifest) → verify the assembled file's SHA-1 against the
// manifest's.
//
// Resume is per-chunk: read the existing bytes at chunk.Offset, hash with SHA-1, and skip the
// network fetch when it matches chunk.ChunkID. Depot-key rotation: on AccessDenied/Expired with
// a cached key, discard and re-fetch once, then throw.
//
// Fail-loud: any chunk or final-file SHA-1 mismatch, depot-key fetch failure (after one retry),
// or non-OK logon throws and leaves the .partial dir in place.
//
// Determinism: files are enumerated in sorted FileName order, so the write sequence and the
// returned AcquireResult are stable per (build, platform).
//
// Per-chunk retry uses exponential backoff up to MaxChunkAttempts. Hash mismatches do NOT retry:
// they are corruption or a stale depot key, and retrying would only mask the failure.
//
// CDN failover: GetServersForSteamPipe returns a directory that includes Valve-internal hosts
// (e.g. cache1-blv2.valve.org) that don't resolve publicly, so picking one as the sole server
// kills the download on DNS failure. We rank all servers deterministically (public
// *.steamcontent.com first, ordinal tie-break) and fail over on transport/DNS errors only.
// Which mirror serves a chunk doesn't affect output — every chunk's SHA-1 is verified — so
// failover is determinism-safe. A hash mismatch is corruption and never triggers failover.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;

using Cs2SchemaTracker.Host.Vpk;

using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;

namespace Cs2SchemaTracker.Host.Steam;

internal sealed class SteamAnonymousAcquirer : ISteamAcquirer
{
    // --- Tunables ------------------------------------------------------------

    /// <summary>Number of network attempts per chunk before failing loud. Hash mismatches do NOT retry.</summary>
    private const int MaxChunkAttempts = 5;

    /// <summary>Initial backoff between chunk retries (doubled per attempt).</summary>
    private static readonly TimeSpan InitialChunkBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>Connect + login overall timeout.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    private readonly TextWriter log;

    // Anonymous is the default; authenticated mode engages only when AuthMode ==
    // Authenticated (explicit --auth, or auto-selected for the historical/explicit-manifest
    // path when creds exist). When authenticated, credentials carries the username/password
    // (from the gitignored .env) and sessionStore caches the refresh token for non-interactive
    // reuse. All three are null in the default anonymous path.
    private readonly SteamAuthMode authMode;
    private readonly SteamCredentials? credentials;
    private readonly SteamSessionStore? sessionStore;

    public SteamAnonymousAcquirer(TextWriter? logSink = null)
        : this(SteamAuthMode.Anonymous, credentials: null, sessionStore: null, logSink)
    {
    }

    /// <summary>
    /// Construct an acquirer with an explicit auth mode. In
    /// <see cref="SteamAuthMode.Authenticated"/> mode <paramref name="credentials"/>
    /// is required and <paramref name="sessionStore"/> (when supplied) caches the
    /// refresh token for non-interactive reuse.
    /// </summary>
    public SteamAnonymousAcquirer(
        SteamAuthMode authMode,
        SteamCredentials? credentials,
        SteamSessionStore? sessionStore,
        TextWriter? logSink = null)
    {
        log = logSink ?? Console.Error;
        this.authMode = authMode;
        this.credentials = credentials;
        this.sessionStore = sessionStore;
        if (authMode == SteamAuthMode.Authenticated && credentials is null)
        {
            throw new ArgumentNullException(nameof(credentials),
                "Authenticated mode requires Steam credentials.");
        }
    }

    private Session NewSession()
        => new Session(log, authMode, credentials, sessionStore);

    // --- Shared-session lifecycle (batch single-logon) -----------------------
    //
    // Default (sharedScopeActive == false): every acquire/probe owns its session —
    // LeaseSessionAsync connects a fresh Session and the lease disconnects it when the
    // acquire completes (the single-build / --from-provenance / --content / probe lifecycle).
    //
    // Inside a BeginSharedSession scope (the batch path): ONE Session connects lazily on the
    // first acquire and is reused for every subsequent one (its callback pump keeps the
    // connection alive between builds via Steam heartbeats). The lease does NOT disconnect it;
    // the scope owns teardown. So a 244-build batch performs ONE logon, not 244 — defeating
    // Steam's AccountLoginDeniedThrottle.
    //
    // If the shared session drops between builds (IsHealthy == false), LeaseSessionAsync
    // reconnects it once and continues. A drop DURING a build's work fail-isolates that build
    // (the batch continues) and the next build's lease reconnects. Per-build data failures (bad
    // manifest, missing chunk, hash mismatch) are unaffected and still fail-isolate.
    private bool sharedScopeActive;
    private Session? sharedSession;

    /// <summary>
    /// Acquire a connected+logged-on session for one acquire/probe call. When a shared
    /// scope is active the returned lease wraps the ONE shared session (not disconnected
    /// on dispose); otherwise it wraps a freshly-connected session the lease disconnects.
    /// </summary>
    private async Task<SessionLease> LeaseSessionAsync(CancellationToken ct)
    {
        if (!sharedScopeActive)
        {
            var owned = NewSession();
            await owned.ConnectAndLogonAsync(ConnectTimeout, ct).ConfigureAwait(false);
            return new SessionLease(owned, owns: true);
        }

        if (sharedSession is null || !sharedSession.IsHealthy)
        {
            if (sharedSession is not null)
            {
                log.WriteLine(
                    "steam-acquire: shared Steam session dropped; reconnecting ONCE and continuing the batch.");
                try
                { sharedSession.Disconnect(); }
                catch { /* best effort */ }
                sharedSession = null;
            }
            var fresh = NewSession();
            await fresh.ConnectAndLogonAsync(ConnectTimeout, ct).ConfigureAwait(false);
            sharedSession = fresh;
        }
        return new SessionLease(sharedSession, owns: false);
    }

    /// <summary>
    /// Batch single-logon: open ONE shared Steam session reused by every acquire until the
    /// returned scope is disposed (see <see cref="ISteamAcquirer.BeginSharedSession"/>). The
    /// session connects lazily on the first acquire inside the scope.
    /// </summary>
    public IDisposable BeginSharedSession()
    {
        if (sharedScopeActive)
        {
            throw new InvalidOperationException(
                "A shared Steam session scope is already open on this acquirer (nested scopes are not supported).");
        }
        sharedScopeActive = true;
        log.WriteLine(
            "steam-acquire: opened ONE shared Steam session for the batch — a single logon is reused across every build.");
        return new SharedSessionScope(this);
    }

    /// <summary>Disposes the shared session and clears the scope flag (idempotent).</summary>
    private sealed class SharedSessionScope : IDisposable
    {
        private readonly SteamAnonymousAcquirer owner;
        private bool disposed;
        public SharedSessionScope(SteamAnonymousAcquirer owner) => this.owner = owner;
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            { owner.sharedSession?.Disconnect(); }
            catch { /* best effort */ }
            owner.sharedSession = null;
            owner.sharedScopeActive = false;
            owner.log.WriteLine("steam-acquire: closed the shared Steam session (batch complete).");
        }
    }

    /// <summary>
    /// A leased session for one acquire/probe. Disconnects the underlying session on
    /// dispose ONLY when this lease owns it (the non-shared per-call lifecycle); for a
    /// shared-scope lease, dispose is a no-op (the scope owns teardown).
    /// </summary>
    private readonly struct SessionLease : IDisposable
    {
        private readonly bool owns;
        public Session Session { get; }
        public SessionLease(Session session, bool owns)
        {
            Session = session;
            this.owns = owns;
        }
        public void Dispose()
        {
            if (owns)
            {
                Session.Disconnect();
            }
        }
    }

    /// <summary>
    /// Diagnostic: fetch an app's CURRENT PICS product-info (appinfo) and return the raw
    /// appinfo <see cref="KeyValue"/> tree plus the PICS change-number and the appinfo SHA-1
    /// (hex). Anonymous suffices for the current public build of app 730 (PICS is current-only).
    /// The caller chooses the rendering (JSON or VDF); the returned KeyValue is detached data,
    /// safe to use after the session disposes.
    /// </summary>
    public async Task<(uint ChangeNumber, string ShaHex, KeyValue KeyValues)> DumpAppInfoAsync(
        uint appId, CancellationToken ct)
    {
        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;
        var product = await session.DumpAppInfoAsync(appId, ct).ConfigureAwait(false);
        var sha = product.SHAHash is { Length: > 0 } h ? System.Convert.ToHexString(h) : "";
        return (product.ChangeNumber, sha, product.KeyValues);
    }

    public async Task<AcquireResult> AcquireAsync(
        uint appId,
        IReadOnlyList<uint> depotIds,
        uint buildId,
        string outDir,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depotIds);
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        if (depotIds.Count == 0)
        {
            throw new ArgumentException("At least one depot ID is required.", nameof(depotIds));
        }
        if (!Path.IsPathFullyQualified(outDir))
        {
            outDir = Path.GetFullPath(outDir);
        }

        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;

        // PICS lookup → resolve manifestid per depot for the requested buildId.
        var resolved = await session.ResolveManifestIdsAsync(appId, depotIds, buildId, ct).ConfigureAwait(false);
        var resolvedBuild = await session.ResolveBuildIdAsync(appId, buildId, ct).ConfigureAwait(false);

        return await AcquireResolvedAsync(
            session, appId, resolvedBuild, resolved, outDir, fileFilter: null, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Content-depot minimal-footprint acquire (two-phase).
    //
    // The CS2 content depot (2347770) is ~59 GB, but we only need the pak01 VPK files backing
    // the `.gameevents` resources. This fetches a tiny slice in two phases:
    //   Phase A: fetch ONLY game/csgo/pak01_dir.vpk (the directory file — a few MB) via a
    //            manifest file filter, into a STAGING dir.
    //   Phase B: parse it with VpkArchive, compute which external pak01_<NNN>.vpk chunk(s) back
    //            the `.gameevents` entries, then re-acquire the content depot with a filter that
    //            admits the directory file + exactly those chunks into the staging dir.
    //
    // Both phases acquire into a dedicated staging dir (NOT outDir) via the same atomic
    // .partial-then-move + per-file SHA verify path as every other acquire. On full success the
    // selected pak01 files are MERGED into outDir non-destructively: pre-existing files (e.g. the
    // per-platform binaries in the same tree) are preserved; only the pak01 files are
    // added/overwritten. That is why --out may safely point at the binaries dir — the
    // standalone-acquire delete-then-move (which wipes outDir) is applied only to the staging dir.
    //
    // Fallback (minimalGameEvents=false): admit the full pak01_*.vpk set (dir + all chunks),
    // still far smaller than the whole depot.
    // ---------------------------------------------------------------------

    public async Task<AcquireResult> AcquireContentPakAsync(
        uint appId,
        uint contentDepotId,
        uint buildId,
        string outDir,
        bool minimalGameEvents,
        ManifestSpec? explicitSpec,
        bool dirOnly,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        if (!Path.IsPathFullyQualified(outDir))
        {
            outDir = Path.GetFullPath(outDir);
        }

        // EXPLICIT (historical) path: the content depot's manifest GID must come from the
        // supplied spec. Fail loud BEFORE any Steam contact if the spec doesn't carry the
        // content depot we were asked to fetch — re-fetching a prior build's pak01 VPK is
        // meaningless without its 2347770 GID, and falling back to PICS-current would fetch
        // the WRONG build.
        if (explicitSpec is not null &&
            !explicitSpec.Depots.Any(d => d.DepotId == contentDepotId))
        {
            throw new InvalidDataException(
                $"--content historical acquire of build {explicitSpec.BuildId} requires the " +
                $"content depot {contentDepotId} GID in the manifest spec, but the spec lists only " +
                $"depots [{string.Join(",", explicitSpec.OrderedDepotIds)}].");
        }

        var depotIds = new[] { contentDepotId };
        var stagingDir = outDir + ".contentstage";

        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;
        {
            IReadOnlyList<Session.ResolvedDepot> resolved;
            uint resolvedBuild;
            if (explicitSpec is not null)
            {
                // Historical: take the content depot's GID verbatim from the spec (no PICS
                // round-trip — anonymous PICS exposes only the CURRENT 2347770 manifest).
                // Acquire ONLY the content depot even if the spec also carries the binary depot
                // (that is a separate --binaries-only acquire). Build identity is the spec's.
                var contentEntry = explicitSpec.OrderedDepots.First(d => d.DepotId == contentDepotId);
                resolved = new[] { new Session.ResolvedDepot(contentEntry.DepotId, contentEntry.ManifestId) };
                resolvedBuild = explicitSpec.BuildId;
            }
            else
            {
                resolved = await session.ResolveManifestIdsAsync(appId, depotIds, buildId, ct).ConfigureAwait(false);
                resolvedBuild = await session.ResolveBuildIdAsync(appId, buildId, ct).ConfigureAwait(false);
            }

            AcquireResult staged;
            IReadOnlyList<string> selected;

            if (dirOnly)
            {
                // ---- DIR-ONLY (gameevents-dedup): Phase A only ----
                // Fetch the directory file, verify it landed (fail-loud), and STOP — no Phase B
                // archive fetch. The merged record still carries the content depot identity so a
                // later full --content acquire / extract accumulates re-fetchable history as usual.
                log.WriteLine(
                    $"steam-acquire: content DIR-ONLY — fetching content pak directory file(s) into staging '{stagingDir}' (Phase A; no archive chunks).");
                staged = await AcquireResolvedAsync(
                    session, appId, resolvedBuild, resolved, stagingDir,
                    fileFilter: n => ContentPak.All.Any(p => p.IsDirectoryFile(n)), ct).ConfigureAwait(false);

                var dirOnlyVpkPath = Path.Combine(stagingDir, ContentPakSelector.DirectoryFileRelPath
                    .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dirOnlyVpkPath))
                {
                    throw new InvalidOperationException(
                        $"content depot {contentDepotId} did not yield '{ContentPakSelector.DirectoryFileRelPath}' " +
                        $"in DIR-ONLY Phase A (expected at '{dirOnlyVpkPath}'). The depot layout may have changed.");
                }

                var dirOnlyMerged = MergeStagedFiles(staged, outDir, log);
                ManifestRecord.FromAcquireResult(dirOnlyMerged.ResolvedBuildId, dirOnlyMerged.Depots)
                    .MergeIntoTupleDir(outDir);

                try
                {
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, recursive: true);
                }
                catch { /* best effort */ }

                return dirOnlyMerged;
            }

            if (!minimalGameEvents)
            {
                // Fallback: admit the whole pak01 archive set of EVERY content pak (csgo + engine core)
                // in a single filtered acquire. The core pak's files are admitted too (disjoint tree);
                // when the manifest doesn't ship game/core, nothing extra is fetched.
                log.WriteLine("steam-acquire: content acquire (FALLBACK) — fetching the full pak01_*.vpk set for game/csgo + game/core.");
                staged = await AcquireResolvedAsync(
                    session, appId, resolvedBuild, resolved, stagingDir,
                    fileFilter: n => ContentPak.All.Any(p => p.IsAnyPakFile(n)), ct).ConfigureAwait(false);
                selected = staged.Files.Select(f => f.RelativePath).ToList();
            }
            else
            {
                // ---- Phase A: directory file(s) only ----
                // One combined predicate admits EVERY content pak's dir file (csgo + engine core).
                // csgo always matches (so the fail-loud below is unchanged); a missing core dir file
                // is simply not fetched — no second manifest round-trip, no failure.
                log.WriteLine($"steam-acquire: content Phase A — fetching content pak directory file(s) {string.Join(", ", ContentPak.All.Select(p => "'" + p.DirectoryFileRelPath + "'"))} into staging '{stagingDir}'.");
                await AcquireResolvedAsync(
                    session, appId, resolvedBuild, resolved, stagingDir,
                    fileFilter: n => ContentPak.All.Any(p => p.IsDirectoryFile(n)), ct).ConfigureAwait(false);

                var dirVpkPath = Path.Combine(stagingDir, ContentPakSelector.DirectoryFileRelPath
                    .Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dirVpkPath))
                {
                    throw new InvalidOperationException(
                        $"content depot {contentDepotId} did not yield '{ContentPakSelector.DirectoryFileRelPath}' " +
                        $"in Phase A (expected at '{dirVpkPath}'). The depot layout may have changed.");
                }

                // The engine core pak is OPTIONAL — its absence from the manifest is normal (not every
                // build/era ships it, and the depot path is unverified). Presence gates the Phase B
                // core plan + the core repack below.
                var coreDirVpkPath = Path.Combine(stagingDir, ContentPak.Core.DirectoryFileRelPath
                    .Replace('/', Path.DirectorySeparatorChar));
                bool coreStaged = File.Exists(coreDirVpkPath);
                log.WriteLine(coreStaged
                    ? $"steam-acquire: content Phase A — engine core pak '{ContentPak.Core.DirectoryFileRelPath}' present."
                    : $"steam-acquire: content Phase A — engine core pak '{ContentPak.Core.DirectoryFileRelPath}' NOT in this manifest (core.gameevents not tracked this build; graceful).");

                // ---- Phase B: parse + select the minimal BYTE-RANGE set ----
                var archive = VpkArchive.Open(dirVpkPath);
                // Build the byte-range-selective fetch plan: the exact body byte ranges of the
                // resources our 7 content emitters read, grouped by backing external
                // pak01_<NNN>.vpk. The acquirer then fetches ONLY the depot-chunks overlapping
                // those ranges (a sparse pak01 file), shrinking the per-build content fetch from
                // ~1.3 GB to tens of MB and avoiding the CDN 503 storm.
                var csgoPlan = ContentPakSelector.SelectContentByteRanges(archive, ContentPak.Csgo);
                if (csgoPlan.IsEmpty)
                {
                    throw new InvalidDataException(
                        $"pak01_dir.vpk from content depot {contentDepotId} contains no '.gameevents' entries — " +
                        "refusing to lay down a content tree that cannot satisfy. " +
                        "Verify the content depot / build.");
                }
                // Additively fold the engine core pak's minimal ranges into the SAME fetch (disjoint
                // chunk keys under game/core/*), when its dir file staged. Absent ⇒ csgo-only plan.
                var plan = csgoPlan;
                if (coreStaged)
                {
                    var coreArchive = VpkArchive.Open(coreDirVpkPath);
                    var corePlan = ContentPakSelector.SelectContentByteRanges(coreArchive, ContentPak.Core);
                    plan = ContentFetchPlan.Merge(new[] { csgoPlan, corePlan });
                }
                selected = plan.AllFiles;
                long plannedRangeBytes = plan.ChunkRanges.Values.Sum(rs => rs.Sum(r => r.Length));
                log.WriteLine(
                    $"steam-acquire: content Phase B (byte-range-selective) — pak01_dir.vpk parsed; " +
                    $"{selected.Count} file(s), {plan.ChunkRanges.Count} sparse chunk file(s), " +
                    $"{plannedRangeBytes:N0} resource bytes across required ranges: " +
                    string.Join(", ", selected));

                // Re-acquire the directory file (whole) + the selected chunk(s) — but for the
                // chunk files, ONLY the depot-chunks overlapping the required ranges (sparse).
                // The directory file is refetched (whole) — cheap — so staging is complete.
                staged = await AcquireResolvedAsync(
                    session, appId, resolvedBuild, resolved, stagingDir,
                    fileFilter: plan.SelectedPredicate(), ct, rangePlan: plan).ConfigureAwait(false);
            }

            // ---- REPACK into the content-addressed trimmed store ----
            // Instead of co-locating the (full-size, mostly-zero) pak01 chunks under
            // <build>/<platform>/game/csgo/, trim the staged pak down to exactly the entries the 7
            // content emitters read and store it ONCE, keyed by the 2347770 content-depot manifest
            // GID, at <storeRoot>/_content/<gid>/game/csgo/{pak01_dir.vpk,pak01_000.vpk}. That copy
            // is identical for win + lin of a build and shared across builds whose content depot
            // didn't change, so it dedups by construction. The staged full-size chunks are transient
            // (staging is deleted below).
            var stagedDirVpk = Path.Combine(stagingDir, ContentPakSelector.DirectoryFileRelPath
                .Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(stagedDirVpk))
            {
                throw new InvalidOperationException(
                    $"content depot {contentDepotId} staging is missing " +
                    $"'{ContentPakSelector.DirectoryFileRelPath}' before repack (expected at '{stagedDirVpk}').");
            }

            ulong contentGid = resolved.First(d => d.DepotId == contentDepotId).ManifestId;
            var contentStoreRoot = ContentStore.RootForTupleDir(outDir)
                ?? throw new InvalidOperationException(
                    $"cannot derive the _content store root from outDir '{outDir}' " +
                    "(expected a <storeRoot>/<build>/<platform> tuple dir).");

            // Open the freshly-staged pak + compute the required-entry set. Fail-loud gate covering
            // both the minimal Phase-B and the fallback (full-pak) paths: no `.gameevents` means the
            // wrong VPK / depot.
            var stagedArchive = VpkArchive.Open(stagedDirVpk);
            var required = ContentPakSelector.EnumerateRequiredEntries(stagedArchive);
            if (required.Count == 0)
            {
                throw new InvalidDataException(
                    $"pak01_dir.vpk from content depot {contentDepotId} contains no '.gameevents' " +
                    "entries — refusing to write a content-store copy that cannot satisfy.");
            }

            // Ensure a COMPLETE trimmed store copy for this GID, SELF-HEALING an incomplete/legacy one
            // (e.g. the old python gameevents-only backfill whose dir tree still references the ORIGINAL
            // external chunks that were never fetched). A genuinely-complete trim is a content-addressed
            // no-op (a second build/platform with the same GID skips fast); an incomplete one is
            // re-trimmed from THIS fresh staging without needing --force.
            var action = ContentStore.EnsureTrimmedStore(
                stagedArchive, required, contentStoreRoot, contentGid, force: false, out var storeDetail);
            log.WriteLine(action == ContentStore.StoreEnsureAction.SkippedComplete
                ? $"steam-acquire: content-store HIT — _content/{contentGid} already a complete trim; skipping repack ({storeDetail})."
                : $"steam-acquire: content-store repack — _content/{contentGid} {storeDetail} ({required.Count} required entrie(s), CRC-verified reads).");

            // ---- ENGINE CORE pak (resource/core.gameevents) — additive, optional ----
            // Trim + store the engine core pak into _content/<gid>/game/core alongside the csgo copy,
            // keyed by the SAME content GID. Present only when Phase A staged its dir file (or the
            // fallback fetched it); its absence is the normal path (unverified depot layout / era that
            // doesn't ship it) and is logged, never fatal.
            var stagedCoreDirVpk = Path.Combine(
                stagingDir, ContentPak.Core.DirectoryFileRelPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(stagedCoreDirVpk))
            {
                var coreArch = VpkArchive.Open(stagedCoreDirVpk);
                var coreRequired = ContentPakSelector.EnumerateRequiredEntries(coreArch); // core.gameevents only
                if (coreRequired.Count == 0)
                {
                    throw new InvalidDataException(
                        $"engine core pak '{ContentPak.Core.DirectoryFileRelPath}' from content depot " +
                        $"{contentDepotId} contains no '.gameevents' — refusing to store an empty core copy.");
                }
                var coreAction = ContentStore.EnsureTrimmedStore(
                    coreArch, coreRequired, contentStoreRoot, contentGid, force: false, out var coreDetail,
                    ContentPak.Core);
                log.WriteLine(coreAction == ContentStore.StoreEnsureAction.SkippedComplete
                    ? $"steam-acquire: content-store HIT (core) — _content/{contentGid}/game/core already a complete trim ({coreDetail})."
                    : $"steam-acquire: content-store repack (core) — _content/{contentGid}/game/core {coreDetail} ({coreRequired.Count} required entrie(s)).");
            }
            else
            {
                log.WriteLine(
                    $"steam-acquire: NOTE engine core pak '{ContentPak.Core.DirectoryFileRelPath}' not present in " +
                    $"content depot {contentDepotId} for this build — core.gameevents (engine registry) not stored this era.");
            }

            // Persist the content depot's resolved manifest identity into outDir's
            // manifest-record.json, merging with any existing record (e.g. the binary depot entry
            // from a prior `acquire`). extract resolves the store copy from this 2347770 GID, so it
            // must land even though we no longer copy any pak file into outDir. MergeIntoTupleDir
            // reads any present record (fail-loud on corrupt), unions by depotId (content entry
            // wins), sorts, and writes canonical JSON.
            Directory.CreateDirectory(outDir);
            ManifestRecord.FromAcquireResult(staged.ResolvedBuildId, staged.Depots)
                .MergeIntoTupleDir(outDir);

            // Staging is consumed; best-effort cleanup.
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch { /* best effort */ }

            return staged with { OutDir = outDir };
        }
    }

    /// <summary>
    /// Copy every file a staged acquire produced (already SHA-verified) into
    /// <paramref name="outDir"/>, PRESERVING any pre-existing files there (e.g. the
    /// per-platform binaries). Only the staged file paths are added/overwritten.
    /// Files are copied in sorted relative-path order (deterministic). Returns an
    /// AcquireResult whose OutDir is the merge target. Shared by the content-pak
    /// and the Workshop-Tools co-location legs.
    /// </summary>
    internal static AcquireResult MergeStagedFiles(AcquireResult staged, string outDir, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(staged);
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        Directory.CreateDirectory(outDir);
        foreach (var file in staged.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var rel = file.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var src = Path.Combine(staged.OutDir, rel);
            var dst = Path.Combine(outDir, rel);
            var parent = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.Copy(src, dst, overwrite: true);
            log.WriteLine($"steam-acquire: staged merge — {file.RelativePath} -> '{dst}'.");
        }
        return staged with { OutDir = outDir };
    }

    // ---------------------------------------------------------------------
    // Workshop-Tools-depot minimal-footprint acquire (editor DLLs only).
    //
    // The Workshop Tools depot (2347779 — windows-only) is ~2.09 GB/build, but the walker only
    // loads its editor tool DLLs (~200 MB: game/bin/win64/*.dll incl. tools/, plus
    // game/csgo/bin/win64/modtools.dll — see CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md).
    // This fetches ONLY the ToolsBinSelector slice (".dll" under "game/") via the same manifest
    // file-filter path as the content/binaries-only acquires, so per-chunk SHA verify, chunk
    // resume, depot-key rotation, CDN failover, and atomic .partial-then-move are all identical.
    //
    // The acquire lands in a dedicated STAGING dir (outDir + ".toolsstage"), NOT outDir: the
    // standalone-acquire delete-then-move (which wipes outDir) is applied only to staging. On full
    // success the staged DLLs are MERGED into outDir non-destructively (MergeStagedFiles preserves
    // pre-existing files) — outDir is the per-build windows BINARIES dir, and the tools DLLs land
    // in the SAME game/bin/win64 + game/csgo/bin/win64 subtrees the base binaries occupy, so the
    // wipe-and-replace binary path would destroy the co-located base binaries. This mirrors the
    // content-pak co-location exactly. manifest-record.json accumulates the 2347779 depot entry
    // via MergeIntoTupleDir (union by depotId), keeping the slice re-fetchable.
    // ---------------------------------------------------------------------

    public async Task<AcquireResult> AcquireToolsAsync(
        uint appId,
        uint toolsDepotId,
        uint buildId,
        string outDir,
        ManifestSpec? explicitSpec,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        if (!Path.IsPathFullyQualified(outDir))
        {
            outDir = Path.GetFullPath(outDir);
        }

        // EXPLICIT (historical) path: the tools depot's manifest GID must come from the supplied
        // spec. Fail loud BEFORE any Steam contact if the spec doesn't carry the tools depot we
        // were asked to fetch — re-fetching a prior build's tools slice is meaningless without its
        // 2347779 GID, and falling back to PICS-current would fetch the WRONG build.
        if (explicitSpec is not null &&
            !explicitSpec.Depots.Any(d => d.DepotId == toolsDepotId))
        {
            throw new InvalidDataException(
                $"--tools historical acquire of build {explicitSpec.BuildId} requires the " +
                $"tools depot {toolsDepotId} GID in the manifest spec, but the spec lists only " +
                $"depots [{string.Join(",", explicitSpec.OrderedDepotIds)}].");
        }

        var depotIds = new[] { toolsDepotId };
        var stagingDir = outDir + ".toolsstage";

        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;
        {
            IReadOnlyList<Session.ResolvedDepot> resolved;
            uint resolvedBuild;
            if (explicitSpec is not null)
            {
                // Historical: take the tools depot's GID verbatim from the spec (no PICS
                // round-trip — anonymous PICS exposes only the CURRENT 2347779 manifest).
                // Acquire ONLY the tools depot even if the spec also carries the binary/content
                // depots (those are separate legs). Build identity is the spec's.
                var toolsEntry = explicitSpec.OrderedDepots.First(d => d.DepotId == toolsDepotId);
                resolved = new[] { new Session.ResolvedDepot(toolsEntry.DepotId, toolsEntry.ManifestId) };
                resolvedBuild = explicitSpec.BuildId;
            }
            else
            {
                resolved = await session.ResolveManifestIdsAsync(appId, depotIds, buildId, ct).ConfigureAwait(false);
                resolvedBuild = await session.ResolveBuildIdAsync(appId, buildId, ct).ConfigureAwait(false);
            }

            log.WriteLine(
                $"steam-acquire: tools acquire — fetching the editor-DLL slice (\".dll\" under \"game/\") " +
                $"of depot {toolsDepotId} into staging '{stagingDir}'.");
            var staged = await AcquireResolvedAsync(
                session, appId, resolvedBuild, resolved, stagingDir,
                fileFilter: ToolsBinSelector.Predicate, ct).ConfigureAwait(false);

            // Fail-loud gate: a tools acquire that matched ZERO .dll files means the depot layout
            // changed (or the wrong depot was named) — never lay down an empty tools slice. The
            // shared download core already throws on a zero-match filter; this re-assertion keeps
            // the invariant local and explicit.
            if (staged.Files.Count == 0)
            {
                throw new InvalidOperationException(
                    $"tools depot {toolsDepotId} yielded ZERO '.dll' files under 'game/' — the " +
                    "depot layout may have changed; refusing to record an empty tools slice.");
            }

            // Non-destructive merge into the binaries dir (see the block comment above: the
            // wipe-and-replace path would destroy the co-located base binaries), then accumulate
            // the 2347779 identity into manifest-record.json (union by depotId).
            var merged = MergeStagedFiles(staged, outDir, log);
            ManifestRecord.FromAcquireResult(merged.ResolvedBuildId, merged.Depots)
                .MergeIntoTupleDir(outDir);

            // Staging is consumed; best-effort cleanup.
            try
            {
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch { /* best effort */ }

            return merged;
        }
    }

    public async Task<AcquireResult> AcquireExplicitAsync(
        ManifestSpec spec,
        string outDir,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        if (!Path.IsPathFullyQualified(outDir))
        {
            outDir = Path.GetFullPath(outDir);
        }

        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;

        // EXPLICIT path: skip PICS-current resolution entirely. The caller supplied the exact
        // per-depot manifest GIDs (from our own recorded history). Everything downstream (depot
        // key, CDN rotation, manifest download, chunk verify, resume) is identical to the PICS
        // path. Content must still be CDN-resident or we fail loud.
        var resolved = spec.OrderedDepots
            .Select(d => new Session.ResolvedDepot(d.DepotId, d.ManifestId))
            .ToList();

        // The build ID is taken verbatim from the spec — there is no PICS
        // round-trip to "resolve" it; the explicit GIDs ARE the build identity.
        return await AcquireResolvedAsync(
            session, spec.AppId, spec.BuildId, resolved, outDir, fileFilter: null, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // Loadable-BINARIES-ONLY binary-depot acquire (historical backfill).
    //
    // The per-OS binary depot (2347771 win / 2347773 linux) is ~7.9 GB/build, but only ~0.46 GB
    // is the native binaries the walker loads (the DLLs/.so under the per-OS bin dirs). Across
    // the 337-win + 330-linux backfill the full-depot pull is ~5.3 TB; the loadable-binaries-only
    // slice is ~307 GB. This fetches ONLY the bin-directory subtrees (BinaryBinSelector) via the
    // same manifest file-filter path as the content-depot acquire, so per-chunk SHA verify, chunk
    // resume, depot-key rotation, CDN failover, atomic .partial-then-move, and manifest-record
    // merge are all identical to a full binary acquire.
    //
    // Both PICS-current and EXPLICIT (--from-manifest) GID sourcing are supported:
    // <paramref name="explicitSpec"/>, when non-null, supplies the GIDs verbatim (the historical
    // path — anonymous PICS exposes only the current manifest, so historical builds require
    // authenticated --auth + explicit GIDs); when null, PICS resolves the current build.
    //
    // The output dir is wiped-and-replaced like a standalone binary acquire (it is THIS depot's
    // tree), and manifest-record.json carries the binary depot entry (merged with any prior
    // record in the dir) so the filtered backfill still accumulates re-fetchable history.
    // ---------------------------------------------------------------------

    public async Task<AcquireResult> AcquireBinariesOnlyAsync(
        uint appId,
        IReadOnlyList<uint> depotIds,
        uint buildId,
        string outDir,
        string platform,
        ManifestSpec? explicitSpec,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(outDir);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        if (!Path.IsPathFullyQualified(outDir))
        {
            outDir = Path.GetFullPath(outDir);
        }

        // Fail loud on an unknown platform BEFORE any Steam contact: the prefix set is what
        // makes this a binaries-only acquire, and there is no safe default.
        var filter = BinaryBinSelector.PredicateFor(platform);

        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;
        {
            uint resolvedBuild;
            IReadOnlyList<Session.ResolvedDepot> resolved;
            if (explicitSpec is not null)
            {
                // EXPLICIT (historical) path: GIDs verbatim, no PICS round-trip.
                resolved = explicitSpec.OrderedDepots
                    .Select(d => new Session.ResolvedDepot(d.DepotId, d.ManifestId))
                    .ToList();
                resolvedBuild = explicitSpec.BuildId;
            }
            else
            {
                ArgumentNullException.ThrowIfNull(depotIds);
                if (depotIds.Count == 0)
                {
                    throw new ArgumentException("At least one depot ID is required.", nameof(depotIds));
                }
                resolved = await session.ResolveManifestIdsAsync(appId, depotIds, buildId, ct).ConfigureAwait(false);
                resolvedBuild = await session.ResolveBuildIdAsync(appId, buildId, ct).ConfigureAwait(false);
            }

            // reuseExisting: true — a binaries-only acquire into an ALREADY-cached
            // build dir cache-hits the on-disk binaries (SHA-1-verified in place, no
            // re-download, no rewrite) and re-fetches only missing/corrupt files. This
            // is what makes a content backfill over the committed builds transfer
            // content-only instead of re-pulling ~378 MB of binaries per build.
            return await AcquireResolvedAsync(
                session,
                explicitSpec?.AppId ?? appId,
                resolvedBuild,
                resolved,
                outDir,
                fileFilter: filter,
                ct,
                reuseExisting: true).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------
    // Cheap manifest-level probes — NO bulk download.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolve the CURRENT public-branch build id + per-depot current manifest
    /// GIDs for <paramref name="appId"/> via PICS. Manifest-level only — fetches
    /// no depot content. Answers "what is the public branch on right now?".
    /// </summary>
    public async Task<CurrentPicsResult> ProbeCurrentPicsAsync(
        uint appId, IReadOnlyList<uint> depotIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(depotIds);
        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;
        var resolved = await session.ResolveManifestIdsAsync(appId, depotIds, 0, ct).ConfigureAwait(false);
        var buildId = await session.ResolveBuildIdAsync(appId, 0, ct).ConfigureAwait(false);
        var depots = resolved
            .OrderBy(d => d.DepotId)
            .Select(d => new CurrentDepotManifest(d.DepotId, d.ManifestId))
            .ToList();
        return new CurrentPicsResult(appId, buildId, depots);
    }

    /// <summary>
    /// Probe whether a SPECIFIC PRIOR (depot, manifestId) set is still
    /// anonymously fetchable AT MANIFEST LEVEL, and optionally whether one sample
    /// chunk per depot is still CDN-resident. Downloads NO bulk content — at most
    /// one chunk per depot when <paramref name="probeOneChunk"/> is true.
    ///
    /// Per-depot failures are CAPTURED into the result (not thrown) so the probe
    /// can report a partial verdict ("depot A reachable, depot B purged"); this is
    /// a diagnostic, not an acquire. The caller (validate-manifest) decides the
    /// process exit code from the aggregate verdict.
    /// </summary>
    public async Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(
        ManifestSpec spec, bool probeOneChunk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        using var lease = await LeaseSessionAsync(ct).ConfigureAwait(false);
        var session = lease.Session;
        var rotation = await session.PickCdnRotationAsync(ct).ConfigureAwait(false);

        var results = new List<ExplicitDepotManifestProbe>(spec.Depots.Count);
        foreach (var depot in spec.OrderedDepots)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await ProbeOneDepotManifestAsync(
                session, spec.AppId, depot, rotation, probeOneChunk, ct).ConfigureAwait(false));
        }
        return new ExplicitManifestProbe(spec.AppId, spec.BuildId, results);
    }

    private static async Task<ExplicitDepotManifestProbe> ProbeOneDepotManifestAsync(
        Session session,
        uint appId,
        ManifestSpecDepot depot,
        CdnServerRotation rotation,
        bool probeOneChunk,
        CancellationToken ct)
    {
        try
        {
            var depotKey = await session.GetDepotKeyAsync(depot.DepotId, appId, ct).ConfigureAwait(false);
            var manifest = await session.DownloadManifestAsync(
                depot.DepotId, depot.ManifestId, appId, depotKey, rotation, ct).ConfigureAwait(false);

            var createdUtc = manifest.CreationTime == default
                ? "1970-01-01T00:00:00Z"
                : DateTime.SpecifyKind(manifest.CreationTime, DateTimeKind.Utc)
                      .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            var files = (manifest.Files ?? new List<DepotManifest.FileData>())
                .Where(f => (f.Flags & EDepotFileFlag.Directory) == 0)
                .ToList();
            int fileCount = files.Count;
            long totalBytes = files.Sum(f => checked((long)f.TotalSize));

            bool chunkProbed = false;
            bool chunkOk = false;
            string? chunkSha1 = null;
            if (probeOneChunk)
            {
                chunkProbed = true;
                // Pick the first chunk of the first file (by ordinal name, then offset) so the
                // choice is deterministic across runs.
                var firstChunk = files
                    .OrderBy(f => f.FileName, StringComparer.Ordinal)
                    .SelectMany(f => f.Chunks ?? new List<DepotManifest.ChunkData>())
                    .Where(c => c.ChunkID is { Length: > 0 })
                    .OrderBy(c => ToLowerHex(c.ChunkID!), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (firstChunk is not null)
                {
                    // SteamKit2 verifies the chunk SHA-1 internally; a successful
                    // return means the chunk was CDN-resident AND intact.
                    var dest = new byte[firstChunk.UncompressedLength];
                    await session.DownloadChunkAsync(
                        depot.DepotId, depotKey, rotation.Current, firstChunk, dest, ct).ConfigureAwait(false);
                    chunkOk = true;
                    chunkSha1 = ToLowerHex(firstChunk.ChunkID!);
                }
            }

            return new ExplicitDepotManifestProbe(
                DepotId: depot.DepotId,
                ManifestId: depot.ManifestId,
                ManifestFetched: true,
                ManifestCreatedUtc: createdUtc,
                FileCount: fileCount,
                TotalUncompressedBytes: totalBytes,
                ChunkProbeAttempted: chunkProbed,
                SampleChunkFetched: chunkOk,
                SampleChunkSha1: chunkSha1,
                Error: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diagnostic capture: a purged manifest / denied depot is exactly the
            // verdict this probe exists to report. We record it rather than throw.
            return new ExplicitDepotManifestProbe(
                DepotId: depot.DepotId,
                ManifestId: depot.ManifestId,
                ManifestFetched: false,
                ManifestCreatedUtc: null,
                FileCount: 0,
                TotalUncompressedBytes: 0,
                ChunkProbeAttempted: false,
                SampleChunkFetched: false,
                SampleChunkSha1: null,
                Error: $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Shared download core: given a session and an already-resolved list of
    /// (depotId, manifestId), fetch every depot's manifest + files into
    /// <paramref name="outDir"/>.partial and atomically move into place. This is
    /// the single chokepoint used by BOTH the PICS-current path
    /// (<see cref="AcquireAsync"/>) and the explicit-manifest path
    /// (<see cref="AcquireExplicitAsync"/>), so the CDN rotation + per-chunk SHA
    /// verify + resume behavior is identical regardless of how the manifest GIDs
    /// were sourced (same input → byte-identical output).
    /// </summary>
    private async Task<AcquireResult> AcquireResolvedAsync(
        Session session,
        uint appId,
        uint resolvedBuildId,
        IReadOnlyList<Session.ResolvedDepot> resolved,
        string outDir,
        Func<string, bool>? fileFilter,
        CancellationToken ct,
        bool reuseExisting = false,
        ContentFetchPlan? rangePlan = null)
    {
        // BINARY CACHE REUSE.
        //
        // Default (reuseExisting=false — content staging, explicit/PICS full acquire): stage into
        // outDir.partial and atomically rename over outDir, so an interrupted fresh acquire never
        // leaves a half-written outDir. With a fresh .partial the per-chunk resume probe sees an
        // empty dir, so every chunk is fetched + every file written — correct for a first acquire,
        // but it re-fetches an already-cached binary tree (the bug this fixes: a content backfill
        // over already-cached builds re-downloaded ~378 MB of binaries/build).
        //
        // reuseExisting=true (the binaries-only backfill path): when outDir is already populated
        // by a prior successful acquire of THIS pinned manifest, download in place against outDir.
        // The resume probe (ChunkAlreadyValidAsync) SHA-1-verifies each on-disk chunk and skips
        // both the CDN fetch and the write for every already-valid chunk — a true cache-hit: zero
        // transfer, zero rewrite (mtime preserved, since a fully-valid file is only read). Missing
        // / wrong-size / hash-mismatched files are re-fetched in place and fail loud on real
        // corruption via the whole-file SHA-1 check. The pinned manifest is identical across runs,
        // so the file set matches and no stale binary is left behind.
        bool inPlace = reuseExisting &&
                       Directory.Exists(outDir) &&
                       Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Any();

        var workDir = inPlace ? outDir : outDir + ".partial";
        if (!inPlace)
        {
            Directory.CreateDirectory(workDir);
        }

        // Get the ranked CDN candidate list (with failover). See CdnServerRotation.
        var rotation = await session.PickCdnRotationAsync(ct).ConfigureAwait(false);

        var depotInfos = new List<AcquiredDepotInfo>(resolved.Count);
        var allFiles = new List<AcquiredFileInfo>();
        long totalBytes = 0;
        long downloadedBytes = 0;

        // Stable depot iteration order: sort by depot ID.
        foreach (var entry in resolved.OrderBy(e => e.DepotId))
        {
            ct.ThrowIfCancellationRequested();

            var depotKey = await session.GetDepotKeyAsync(entry.DepotId, appId, ct).ConfigureAwait(false);
            var manifest = await session.DownloadManifestAsync(
                entry.DepotId, entry.ManifestId, appId, depotKey, rotation, ct).ConfigureAwait(false);

            // Manifest creation time, UTC ISO 8601.
            var createdUtc = manifest.CreationTime == default
                ? "1970-01-01T00:00:00Z"
                : DateTime.SpecifyKind(manifest.CreationTime, DateTimeKind.Utc)
                      .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            depotInfos.Add(new AcquiredDepotInfo(
                AppId: appId,
                DepotId: entry.DepotId,
                ManifestId: entry.ManifestId,
                ManifestCreatedUtc: createdUtc));

            var depotFiles = await DownloadDepotFilesAsync(
                session, entry.DepotId, depotKey, rotation, manifest, workDir, fileFilter, ct, rangePlan).ConfigureAwait(false);
            allFiles.AddRange(depotFiles.Files);
            totalBytes += depotFiles.Files.Sum(f => f.SizeBytes);
            downloadedBytes += depotFiles.DownloadedBytes;
        }

        // Sort outputs for determinism.
        var orderedFiles = allFiles
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal)
            .ToList();

        var orderedDepots = depotInfos.OrderBy(d => d.DepotId).ToList();

        // The standalone-acquire delete-then-move below wipes outDir (this depot's binaries are
        // fully replaced). But the existing manifest-record.json there may carry OTHER depots
        // (e.g. the content depot 2347770 from a prior `--content` acquire into the same dir).
        // Capture it BEFORE the delete so we merge rather than clobber — order-of-acquire must not
        // lose a depot entry. A present-but-corrupt record fails loud here (a real input problem,
        // not something to silently drop). In-place reuse reads the same record from outDir
        // (== workDir) and merges idempotently, so a prior content depot entry survives untouched.
        var existingRecordPath = Path.Combine(outDir, ManifestRecord.FileName);
        ManifestRecord? priorRecord =
            File.Exists(existingRecordPath) ? ManifestRecord.ReadFromFile(existingRecordPath) : null;

        if (!inPlace)
        {
            // Atomic-ish rename: drop any pre-existing outDir, then move.
            if (Directory.Exists(outDir))
            {
                Directory.Delete(outDir, recursive: true);
            }
            Directory.Move(workDir, outDir);
        }
        // inPlace: outDir already holds the (cache-hit / repaired) files; no move.

        // Persist a deterministic record of exactly what we resolved + fetched. Written AFTER the
        // move so it lands in the final out dir and only on full success (never partial). Merge
        // any pre-captured prior record (e.g. the content depot from an earlier acquire) so both
        // depots survive regardless of acquire order.
        var thisRecord = ManifestRecord.FromAcquireResult(resolvedBuildId, orderedDepots);
        if (priorRecord is not null)
        {
            thisRecord = priorRecord.MergeWith(thisRecord);
        }
        thisRecord.WriteToTupleDir(outDir);

        return new AcquireResult(
            OutDir: outDir,
            ResolvedBuildId: resolvedBuildId,
            Depots: orderedDepots,
            Files: orderedFiles,
            TotalBytes: totalBytes,
            DownloadedBytes: downloadedBytes);
    }

    // ---------------------------------------------------------------------
    // Depot download (per-file, per-chunk)
    // ---------------------------------------------------------------------

    private async Task<(List<AcquiredFileInfo> Files, long DownloadedBytes)> DownloadDepotFilesAsync(
        Session session,
        uint depotId,
        byte[] depotKey,
        CdnServerRotation rotation,
        DepotManifest manifest,
        string outDir,
        Func<string, bool>? fileFilter,
        CancellationToken ct,
        ContentFetchPlan? rangePlan = null)
    {
        if (manifest.Files is null)
        {
            throw new InvalidDataException(
                $"Depot {depotId} manifest has no Files collection — refusing to acquire.");
        }

        // Stable file order: sort by manifest FileName ordinal. A non-null fileFilter restricts
        // acquisition to a subset of the manifest (the content-depot minimal-footprint path):
        // only files whose manifest name satisfies the predicate are fetched. The filter is
        // applied to the raw manifest FileName so the caller's predicate can match either slash
        // style. Directories are still pre-created below regardless of the filter.
        var orderedFiles = manifest.Files
            .Where(f => (f.Flags & EDepotFileFlag.Directory) == 0)
            .Where(f => fileFilter is null || fileFilter(f.FileName))
            .OrderBy(f => f.FileName, StringComparer.Ordinal)
            .ToList();

        if (fileFilter is not null && orderedFiles.Count == 0)
        {
            // A filtered acquire that matches NOTHING is fail-loud: the depot layout changed or
            // the wrong depot was named — never silently produce an empty acquire.
            throw new InvalidDataException(
                $"filtered acquire of depot {depotId} matched zero manifest files — " +
                "the requested files are not present in this depot's manifest.");
        }

        // Directories: pre-create every directory mentioned by the manifest.
        foreach (var dir in manifest.Files
            .Where(f => (f.Flags & EDepotFileFlag.Directory) != 0)
            .OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            var dirPath = SafeJoin(outDir, dir.FileName);
            Directory.CreateDirectory(dirPath);
        }

        var acquired = new List<AcquiredFileInfo>(orderedFiles.Count);
        long downloadedBytes = 0;
        foreach (var file in orderedFiles)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<VpkByteRange>? selectiveRanges = null;
            if (rangePlan is not null && rangePlan.TryGetRanges(file.FileName, out var rngs))
            {
                selectiveRanges = rngs;
            }
            var (info, fetched) = await DownloadSingleFileAsync(
                session, depotId, depotKey, rotation, file, outDir, ct, selectiveRanges).ConfigureAwait(false);
            acquired.Add(info);
            downloadedBytes += fetched;
        }
        return (acquired, downloadedBytes);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "FileStream is disposed via the using-await block at end of method.")]
    private async Task<(AcquiredFileInfo Info, long DownloadedBytes)> DownloadSingleFileAsync(
        Session session,
        uint depotId,
        byte[] depotKey,
        CdnServerRotation rotation,
        DepotManifest.FileData file,
        string outDir,
        CancellationToken ct,
        IReadOnlyList<VpkByteRange>? selectiveRanges = null)
    {
        var relPath = NormalizeRelativePath(file.FileName);
        var localPath = SafeJoin(outDir, file.FileName);
        var parent = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // Allocate / size the target file. If it exists at the right size we
        // re-open in read/write so we can probe individual chunks for resume.
        // If size is wrong, truncate to TotalSize and let every chunk be
        // re-downloaded.
        long expectedSize = checked((long)file.TotalSize);

        // FileShare.Read so AV (Windows Defender) can scan the file while we
        // write to it without erroring out our handle. Multiple-process concurrent
        // acquire to the same outDir is not supported and will fail loud.
        FileStream fs;
        if (File.Exists(localPath) && new FileInfo(localPath).Length == expectedSize)
        {
            fs = new FileStream(localPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        }
        else
        {
            fs = new FileStream(localPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            fs.SetLength(expectedSize);
        }

        await using (fs.ConfigureAwait(false))
        {
            // Stable chunk order: by Offset. In byte-range-selective mode (selectiveRanges !=
            // null) only the depot-chunks overlapping a required resource byte range are fetched;
            // the rest of the file stays zero (a sparse pak01_<NNN>.vpk). The file is still
            // allocated at full TotalSize (SetLength above) so the VPK reader's [offset,length)
            // seeks stay in-bounds; only the fetched regions are populated, and our emitters only
            // ever read those (CRC-verified) regions.
            var allChunks = (file.Chunks ?? new List<DepotManifest.ChunkData>())
                .OrderBy(c => c.Offset)
                .ToList();

            List<DepotManifest.ChunkData> chunks;
            if (selectiveRanges is null)
            {
                chunks = allChunks;
            }
            else
            {
                chunks = ChunkRangeMath.SelectOverlapping(
                    selectiveRanges, allChunks,
                    c => checked((long)c.Offset), c => c.UncompressedLength);
                // Fail loud if any required range is NOT fully covered by the selected chunks — a
                // manifest whose chunk list doesn't tile the file would otherwise yield a
                // silently-incomplete resource the emitter would mis-read or CRC-fail on.
                if (!ChunkRangeMath.IsFullyCovered(
                        selectiveRanges, chunks,
                        c => checked((long)c.Offset), c => c.UncompressedLength,
                        out var uncovered, out var gapOffset))
                {
                    throw new InvalidDataException(
                        $"byte-range-selective acquire of '{relPath}' (depot {depotId}) cannot cover " +
                        $"required range [{uncovered.Offset}, {uncovered.End}) — the manifest chunk list leaves a " +
                        $"gap at offset {gapOffset}. Refusing to write a silently-incomplete resource.");
                }
            }

            long fileDownloadedBytes = 0;
            foreach (var chunk in chunks)
            {
                ct.ThrowIfCancellationRequested();
                fileDownloadedBytes += await EnsureChunkAsync(
                    session, depotId, depotKey, rotation, fs, chunk, ct).ConfigureAwait(false);
            }

            if (selectiveRanges is not null)
            {
                // SPARSE file: a whole-file SHA over a mostly-zero 1.3 GB file is both meaningless
                // (manifest SHA-1 cannot match a partial file) and wasteful (re-reads the full
                // allocation). Instead hash ONLY the fetched chunk regions, in Offset order —
                // deterministic, tens of MB. Per-chunk SHA-1 (verified by SteamKit2 on download AND
                // by the resume probe) is the integrity guarantee here; the VPK layer re-verifies
                // each resource's CRC32 at extract time. fileDownloadedBytes (CDN-fetched bytes, 0
                // per resume-hit chunk) is the unified transfer counter — distinct from SizeBytes
                // below, the on-disk populated footprint.
#pragma warning disable CA5350
                using var sparseSha = SHA256.Create();
#pragma warning restore CA5350
                long footprint = 0;
                foreach (var chunk in chunks)
                {
                    long off = checked((long)chunk.Offset);
                    int len = checked((int)chunk.UncompressedLength);
                    var sbuf = new byte[len];
                    fs.Position = off;
                    await fs.ReadExactlyAsync(sbuf.AsMemory(0, len), ct).ConfigureAwait(false);
                    sparseSha.TransformBlock(sbuf, 0, len, null, 0);
                    footprint += len;
                }
                sparseSha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return (new AcquiredFileInfo(
                    RelativePath: relPath,
                    // SHA-256 of the fetched chunk bytes (NOT the whole sparse file).
                    Sha256Hex: ToLowerHex(sparseSha.Hash!),
                    // The on-disk populated footprint (sum of selected chunk lengths) —
                    // the sparse file's meaningful size, not the full TotalSize.
                    SizeBytes: footprint,
                    MtimeUtc: null), fileDownloadedBytes);
            }

            // Compute the whole-file SHA-256 (for provenance) and verify the manifest's whole-file
            // SHA-1 if it carries one.
            fs.Position = 0;
            string sha256;
            byte[] computedSha1;
            // SHA-1 here is NOT a security primitive — it's how Steam content-addresses chunks +
            // files in its depot manifests. Matching the server-recorded hash is the acceptance
            // criterion.
#pragma warning disable CA5350
            using (var sha256Algo = SHA256.Create())
            using (var sha1Algo = SHA1.Create())
#pragma warning restore CA5350
            {
                // Stream both hashes in one pass. CryptoStream chaining works but
                // is awkward; a manual buffered read is simpler.
                var buf = new byte[1 << 16];
                int read;
                while ((read = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                {
                    sha256Algo.TransformBlock(buf, 0, read, null, 0);
                    sha1Algo.TransformBlock(buf, 0, read, null, 0);
                }
                sha256Algo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha1Algo.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                sha256 = ToLowerHex(sha256Algo.Hash!);
                computedSha1 = sha1Algo.Hash!;
            }

            // Manifest-recorded SHA-1 may be all-zero for empty/sparse files;
            // only enforce when non-zero.
            if (file.FileHash is { Length: > 0 } && !AllZero(file.FileHash))
            {
                if (!file.FileHash.AsSpan().SequenceEqual(computedSha1))
                {
                    throw new InvalidDataException(
                        $"assembled file SHA-1 does not match manifest for '{relPath}' " +
                        $"(depot {depotId}). Expected {ToLowerHex(file.FileHash)}, got {ToLowerHex(computedSha1)}.");
                }
            }

            return (new AcquiredFileInfo(
                RelativePath: relPath,
                Sha256Hex: sha256,
                SizeBytes: expectedSize,
                // The manifest's per-file mtime isn't exposed by DepotManifest.FileData in all
                // SteamKit2 versions; leave null and let the host derive mtime from the manifest
                // creation time at provenance-emit time.
                MtimeUtc: null), fileDownloadedBytes);
        }
    }

    /// <summary>
    /// Ensure one chunk is present + valid at its file offset. Returns the number of
    /// bytes transferred from the CDN: 0 when the on-disk chunk resume-probed valid
    /// (cache-hit — no fetch, no write), else the chunk's uncompressed length.
    /// </summary>
    private async Task<long> EnsureChunkAsync(
        Session session,
        uint depotId,
        byte[] depotKey,
        CdnServerRotation rotation,
        FileStream fs,
        DepotManifest.ChunkData chunk,
        CancellationToken ct)
    {
        // 1. Resume probe: read the bytes at chunk.Offset, hash with SHA-1,
        //    compare to chunk.ChunkID (manifest's chunk hash).
        if (chunk.ChunkID is { Length: > 0 } && await ChunkAlreadyValidAsync(fs, chunk, ct).ConfigureAwait(false))
        {
            return 0;
        }

        // 2. Download with retry + CDN failover. Two distinct failure classes:
        //      - TRANSPORT error (DNS, connection refused/reset, timeout, short read): retry on the
        //        same server with backoff, then advance to the next ranked CDN candidate. A single
        //        unreachable host (e.g. cache1-blv2.valve.org) must NOT kill the download.
        //      - HASH mismatch (chunk SHA-1 != manifest): corruption, thrown by SteamKit2. NOT
        //        treated as failover/transport — re-fetching from another mirror would mask it. It
        //        propagates and fails loud. See IsTransportFailure for the exact classification.
        Exception? lastTransportError = null;

        while (true)
        {
            var server = rotation.Current;
            for (int attempt = 1; attempt <= MaxChunkAttempts; attempt++)
            {
                try
                {
                    var dest = new byte[chunk.UncompressedLength];
                    int written = await session.DownloadChunkAsync(
                        depotId, depotKey, server, chunk, dest, ct).ConfigureAwait(false);
                    if (written != dest.Length)
                    {
                        // DownloadDepotChunkAsync validates the chunk SHA-1 internally, so a short
                        // read here is a SteamKit2-side anomaly. Treat as transport, retry/failover.
                        throw new IOException(
                            $"CDN chunk download returned {written} bytes, expected {dest.Length}.");
                    }

                    fs.Position = checked((long)chunk.Offset);
                    await fs.WriteAsync(dest.AsMemory(0, written), ct).ConfigureAwait(false);
                    await fs.FlushAsync(ct).ConfigureAwait(false);
                    return written;
                }
                catch (Exception ex) when (IsTransportFailure(ex, ct))
                {
                    lastTransportError = ex;
                    if (attempt == MaxChunkAttempts)
                        break;
                    var backoff = InitialChunkBackoff * Math.Pow(2, attempt - 1);
                    log.WriteLine(
                        $"steam-acquire: chunk download attempt {attempt}/{MaxChunkAttempts} on CDN '{server.Host}' for depot {depotId} " +
                        $"chunk {ToLowerHex(chunk.ChunkID ?? Array.Empty<byte>())} failed ({ex.GetType().Name}: {ex.Message}); retrying in {backoff.TotalMilliseconds:0}ms.");
                    try
                    {
                        await Task.Delay(backoff, ct).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        throw;
                    }
                }
                // Any exception NOT matching IsTransportFailure (e.g. a SteamKit2 chunk SHA-1
                // mismatch) is intentionally not caught here — it propagates and fails loud.
            }

            // Per-server attempts exhausted for a transport reason: fail over to
            // the next ranked CDN candidate. When candidates are exhausted, throw.
            if (!rotation.AdvanceOnTransportFailure(out var nextServer))
            {
                throw new IOException(
                    $"chunk download for depot {depotId} chunk " +
                    $"{ToLowerHex(chunk.ChunkID ?? Array.Empty<byte>())} failed on all {rotation.CandidateCount} ranked CDN candidate(s).",
                    lastTransportError);
            }
            log.WriteLine(
                $"steam-acquire: CDN '{server.Host}' exhausted for depot {depotId}; failing over to '{nextServer.Host}' " +
                $"(candidate {rotation.CurrentIndex + 1}/{rotation.CandidateCount}).");
        }
    }

    /// <summary>
    /// Classifies an exception as a transport/connection failure that warrants retry + CDN
    /// failover, vs. a content failure (hash mismatch / corruption) that must fail loud. Only
    /// transport failures return true. A SHA-1 mismatch surfaced by SteamKit2 is excluded so it
    /// propagates uncaught — never mask corruption by switching mirrors.
    /// </summary>
    internal static bool IsTransportFailure(Exception ex, CancellationToken ct)
    {
        // A cancellation we asked for is not a transport failure.
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        return ex switch
        {
            HttpRequestException => true,      // includes "No such host is known." (DNS / NXDOMAIN)
            SocketException => true,           // connection refused / reset / unreachable
            TimeoutException => true,
            // TaskCanceledException with no caller-requested cancellation is an
            // HttpClient timeout, not a real cancel.
            TaskCanceledException => true,
            IOException => true,               // short read, transport-level stream error
            _ => HasTransportInner(ex.InnerException),
        };
    }

    private static bool HasTransportInner(Exception? inner)
        => inner is not null &&
           (inner is HttpRequestException || inner is SocketException ||
            inner is TimeoutException || inner is IOException ||
            HasTransportInner(inner.InnerException));

    private static async Task<bool> ChunkAlreadyValidAsync(
        FileStream fs, DepotManifest.ChunkData chunk, CancellationToken ct)
    {
        long offset = checked((long)chunk.Offset);
        long length = chunk.UncompressedLength;
        if (offset + length > fs.Length)
        {
            return false;
        }
        fs.Position = offset;
        var buf = new byte[length];
        int total = 0;
        while (total < length)
        {
            int n = await fs.ReadAsync(buf.AsMemory(total, (int)(length - total)), ct).ConfigureAwait(false);
            if (n == 0)
                return false;
            total += n;
        }
        // SHA-1 here matches Steam's content addressing scheme; not a security primitive.
#pragma warning disable CA5350
        var computed = SHA1.HashData(buf);
#pragma warning restore CA5350
        return chunk.ChunkID!.AsSpan().SequenceEqual(computed);
    }

    // ---------------------------------------------------------------------
    // Utilities
    // ---------------------------------------------------------------------

    private static bool AllZero(byte[] bytes)
    {
        foreach (var b in bytes)
            if (b != 0)
                return false;
        return true;
    }

    /// <summary>
    /// Manifest filenames use Windows-style backslashes for depots packed on
    /// Windows tooling. Normalize to forward slashes for the public-facing
    /// AcquiredFileInfo.RelativePath, but preserve the original characters
    /// when joining for on-disk writes.
    /// </summary>
    private static string NormalizeRelativePath(string manifestName)
        => manifestName.Replace('\\', '/');

    /// <summary>
    /// Join the output root with a manifest-supplied relative path, rejecting
    /// any path that tries to escape the output directory (security: a
    /// hostile manifest must not write outside our acquire scope).
    /// </summary>
    private static string SafeJoin(string outDir, string relPath)
    {
        var normalized = relPath.Replace('\\', Path.DirectorySeparatorChar)
                                .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            throw new InvalidDataException(
                $"Manifest path '{relPath}' is absolute; refusing to write.");
        }
        var combined = Path.GetFullPath(Path.Combine(outDir, normalized));
        var rootFull = Path.GetFullPath(outDir);
        if (!combined.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !string.Equals(combined, rootFull, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Manifest path '{relPath}' escapes acquire output directory; refusing to write.");
        }
        return combined;
    }

    internal static string ToLowerHex(byte[] bytes)
    {
        // Convert.ToHexStringLower is .NET 9+; emit our own .NET 8-safe version.
        var chars = new char[bytes.Length * 2];
        const string hex = "0123456789abcdef";
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[2 * i] = hex[bytes[i] >> 4];
            chars[2 * i + 1] = hex[bytes[i] & 0xF];
        }
        return new string(chars);
    }

    // ---------------------------------------------------------------------
    // CdnServerRotation: an ordered, ranked list of CDN candidates with a cursor that advances
    // ONLY on transport failure. The manifest/chunk download paths consult Current and call
    // AdvanceOnTransportFailure when a host is unreachable — turning a single unresolvable internal
    // host (e.g. cache1-blv2.valve.org) from a fatal error into a failover. Ranking is
    // deterministic (see RankServers); which candidate serves a chunk doesn't affect output bytes,
    // since every chunk is SHA-1-verified against the manifest.
    // ---------------------------------------------------------------------

    internal sealed class CdnServerRotation
    {
        private readonly IReadOnlyList<Server> candidates;
        private int index;

        public CdnServerRotation(IReadOnlyList<Server> rankedCandidates)
        {
            ArgumentNullException.ThrowIfNull(rankedCandidates);
            if (rankedCandidates.Count == 0)
            {
                // Defense in depth: callers already guard the empty-directory case, but an empty
                // rotation has no valid Current and must not be silently tolerated.
                throw new InvalidOperationException(
                    "cannot build a CDN rotation from an empty candidate list.");
            }
            candidates = rankedCandidates;
            index = 0;
        }

        /// <summary>The candidate currently selected for downloads.</summary>
        public Server Current => candidates[index];

        /// <summary>Zero-based index of the current candidate.</summary>
        public int CurrentIndex => index;

        /// <summary>Total number of ranked candidates.</summary>
        public int CandidateCount => candidates.Count;

        /// <summary>
        /// Advance to the next ranked candidate after a TRANSPORT failure on the current one.
        /// Returns false (leaving the cursor at the last candidate) when every candidate is
        /// exhausted, at which point the caller must fail loud. Never called for hash/corruption
        /// failures — those bypass the rotation entirely and fail loud.
        /// </summary>
        public bool AdvanceOnTransportFailure(out Server next)
        {
            if (index + 1 >= candidates.Count)
            {
                next = Current;
                return false;
            }
            index++;
            next = candidates[index];
            return true;
        }
    }

    // ---------------------------------------------------------------------
    // Session: encapsulates SteamClient + CallbackManager + handlers + the
    // PICS / depot-key / CDN-server / manifest fetch flow. Keeping it nested
    // makes the public surface of SteamAnonymousAcquirer small.
    // ---------------------------------------------------------------------

    internal sealed class Session : IDisposable
    {
        private readonly SteamClient steamClient;
        private readonly CallbackManager callbacks;
        private readonly SteamUser steamUser;
        private readonly SteamApps steamApps;
        private readonly SteamContent steamContent;
        private readonly TextWriter log;
        private readonly Client cdnClient;

        // Auth context. In Anonymous mode these are null/Anonymous and ConnectAndLogonAsync
        // calls LogOnAnonymous().
        private readonly SteamAuthMode authMode;
        private readonly SteamCredentials? credentials;
        private readonly SteamSessionStore? sessionStore;

        private TaskCompletionSource<SteamClient.ConnectedCallback>? connectedTcs;
        private TaskCompletionSource<SteamClient.DisconnectedCallback>? disconnectedTcs;
        private TaskCompletionSource<SteamUser.LoggedOnCallback>? loggedOnTcs;
        private TaskCompletionSource<SteamApps.LicenseListCallback>? licenseListTcs;
        private CancellationTokenSource? callbackPumpCts;
        private Task? callbackPumpTask;

        // Cached depot keys; cleared per depot on rotation retry.
        private readonly Dictionary<uint, byte[]> depotKeyCache = new();

        // Liveness for the shared-session (batch) reuse path: set true once a logon
        // completes OK, cleared if the connection drops (OnDisconnected). The batch's
        // LeaseSessionAsync consults IsHealthy to decide whether to reconnect-once.
        private volatile bool sessionLoggedOn;

        /// <summary>True while this session is connected and logged on (no drop seen).</summary>
        public bool IsHealthy => sessionLoggedOn;

        public Session(TextWriter log)
            : this(log, SteamAuthMode.Anonymous, credentials: null, sessionStore: null)
        {
        }

        public Session(
            TextWriter log,
            SteamAuthMode authMode,
            SteamCredentials? credentials,
            SteamSessionStore? sessionStore)
        {
            this.log = log;
            this.authMode = authMode;
            this.credentials = credentials;
            this.sessionStore = sessionStore;
            steamClient = new SteamClient();
            callbacks = new CallbackManager(steamClient);
            steamUser = steamClient.GetHandler<SteamUser>()
                ?? throw new InvalidOperationException("SteamUser handler unavailable.");
            steamApps = steamClient.GetHandler<SteamApps>()
                ?? throw new InvalidOperationException("SteamApps handler unavailable.");
            steamContent = steamClient.GetHandler<SteamContent>()
                ?? throw new InvalidOperationException("SteamContent handler unavailable.");
            cdnClient = new Client(steamClient);

            callbacks.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            callbacks.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            callbacks.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            callbacks.Subscribe<SteamApps.LicenseListCallback>(OnLicenseList);
        }

        public async Task ConnectAndLogonAsync(TimeSpan overallTimeout, CancellationToken ct)
        {
            connectedTcs = new TaskCompletionSource<SteamClient.ConnectedCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
            loggedOnTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
            licenseListTcs = new TaskCompletionSource<SteamApps.LicenseListCallback>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Run the callback pump on a background task. SteamKit2's
            // CallbackManager is single-threaded by design — only one thread
            // calls RunWaitCallbacks at a time.
            callbackPumpCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            callbackPumpTask = Task.Run(() =>
            {
                while (!callbackPumpCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        callbacks.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                    }
                    catch (OperationCanceledException) { break; }
                }
            }, CancellationToken.None);

            steamClient.Connect();

            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            overallCts.CancelAfter(overallTimeout);

            _ = await connectedTcs.Task.WaitAsync(overallCts.Token).ConfigureAwait(false);
            log.WriteLine("steam-acquire: connected to Steam network.");

            SteamUser.LoggedOnCallback loggedOn = authMode == SteamAuthMode.Authenticated
                ? await LogOnAuthenticatedAsync(overallCts.Token).ConfigureAwait(false)
                : await LogOnAnonymousAsync(overallCts.Token).ConfigureAwait(false);

            if (loggedOn.Result != EResult.OK)
            {
                throw new InvalidOperationException(
                    $"{(authMode == SteamAuthMode.Authenticated ? "authenticated" : "anonymous")} logon " +
                    $"failed with EResult={loggedOn.Result} ExtendedResult={loggedOn.ExtendedResult}.");
            }
            log.WriteLine($"steam-acquire: {(authMode == SteamAuthMode.Authenticated ? "authenticated" : "anonymous")} logon OK (CellID={loggedOn.CellID}).");
            sessionLoggedOn = true;

            // Wait briefly for the license list to arrive before issuing any
            // PICS calls. Anonymous users sometimes don't receive a license
            // list at all (Steam server behavior), so we don't fail-loud here —
            // PICS calls will surface the gate via their own response.
            using var licenseListCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            licenseListCts.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var licenses = await licenseListTcs.Task.WaitAsync(licenseListCts.Token).ConfigureAwait(false);
                log.WriteLine($"steam-acquire: license list received ({licenses.LicenseList.Count} licenses).");
            }
            catch (OperationCanceledException)
            {
                log.WriteLine("steam-acquire: no license list received within 5s; proceeding to PICS.");
            }
        }

        private async Task<SteamUser.LoggedOnCallback> LogOnAnonymousAsync(CancellationToken ct)
        {
            steamUser.LogOnAnonymous();
            return await loggedOnTcs!.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Authenticated logon. Reuses a cached refresh token when present (non-interactive);
        /// otherwise runs the SteamKit2 credentials auth flow once (optionally consuming an
        /// operator-seeded Steam Guard code) and caches the refresh token + guard data for
        /// subsequent runs.
        ///
        /// Steam Guard: if a code is required and none is seeded, the
        /// NonInteractiveAuthenticator fails loud with a SteamGuardRequiredException
        /// (propagated to the CLI for a clear operator message). We never block on a
        /// console prompt (no TTY in CI).
        /// </summary>
        private async Task<SteamUser.LoggedOnCallback> LogOnAuthenticatedAsync(CancellationToken ct)
        {
            var creds = credentials
                ?? throw new InvalidOperationException("Authenticated mode requires credentials.");

            // 1. Try a cached refresh token first (no password / Guard prompt).
            var cached = sessionStore?.TryLoad(creds.Username);
            if (cached is not null)
            {
                var byTokenResult = await TryLogOnWithRefreshTokenAsync(
                    cached.AccountName, cached.RefreshToken, ct).ConfigureAwait(false);
                if (byTokenResult is { Result: EResult.OK })
                {
                    return byTokenResult;
                }
                // The cached token was rejected (expired / revoked). Drop it and
                // fall through to a fresh credentials logon.
                log.WriteLine(
                    $"steam-acquire: cached session rejected (EResult={byTokenResult?.Result.ToString() ?? "disconnect"}); re-authenticating with credentials.");
                sessionStore?.Clear(creds.Username);
                ResetLoggedOnTcs();
            }

            // 2. Fresh credentials auth flow → refresh token.
            log.WriteLine("steam-acquire: starting authenticated credentials logon (refresh-token mint).");
            var authenticator = new NonInteractiveAuthenticator(creds.GuardCode);
            CredentialsAuthSession authSession;
            try
            {
                authSession = await steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                    new AuthSessionDetails
                    {
                        Username = creds.Username,
                        Password = creds.Password,
                        IsPersistentSession = true,
                        GuardData = cached?.GuardData,
                        Authenticator = authenticator,
                        DeviceFriendlyName = "cs2-schema-tracker",
                    }).ConfigureAwait(false);
            }
            catch (AuthenticationException ex)
            {
                // Wrong password / disabled account / rate-limited. Fail loud — but
                // NEVER echo the password. SteamKit2's message names the EResult only.
                throw new InvalidOperationException(
                    $"authenticated logon rejected at credentials stage (EResult={ex.Result}). " +
                    "Check STEAM_USERNAME / STEAM_PASSWORD (values are never logged).", ex);
            }

            AuthPollResult poll = await authSession.PollingWaitForResultAsync(ct).ConfigureAwait(false);

            // 3. Cache the durable session BEFORE the logon round-trip so a later
            //    run can reuse it even if this process dies mid-logon.
            sessionStore?.Save(new SteamSessionData(
                AccountName: poll.AccountName,
                RefreshToken: poll.RefreshToken,
                GuardData: poll.NewGuardData));

            // 4. Exchange the refresh token for a logged-on session.
            return await TryLogOnWithRefreshTokenAsync(poll.AccountName, poll.RefreshToken, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "authenticated logon produced no LoggedOnCallback after token exchange.");
        }

        private async Task<SteamUser.LoggedOnCallback?> TryLogOnWithRefreshTokenAsync(
            string accountName, string refreshToken, CancellationToken ct)
        {
            ResetLoggedOnTcs();
            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = accountName,
                AccessToken = refreshToken,
                ShouldRememberPassword = true,
            });
            try
            {
                return await loggedOnTcs!.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // OnDisconnected faulted the TCS (token rejected → server dropped us).
                return null;
            }
        }

        private void ResetLoggedOnTcs()
            => loggedOnTcs = new TaskCompletionSource<SteamUser.LoggedOnCallback>(
                TaskCreationOptions.RunContinuationsAsynchronously);

        public void Disconnect()
        {
            sessionLoggedOn = false;
            try
            {
                disconnectedTcs = new TaskCompletionSource<SteamClient.DisconnectedCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
                steamClient.Disconnect();
                // Best-effort wait, no hard timeout.
                if (disconnectedTcs is not null)
                {
                    disconnectedTcs.Task.Wait(TimeSpan.FromSeconds(5));
                }
            }
            catch { /* best effort */ }
            finally
            {
                try
                {
                    callbackPumpCts?.Cancel();
                    callbackPumpTask?.Wait(TimeSpan.FromSeconds(2));
                }
                catch { /* best effort */ }
                callbackPumpCts?.Dispose();
                callbackPumpCts = null;
            }
        }

        public void Dispose() => Disconnect();

        // --- Callbacks ----------------------------------------------------

        private void OnConnected(SteamClient.ConnectedCallback cb) => connectedTcs?.TrySetResult(cb);

        private void OnDisconnected(SteamClient.DisconnectedCallback cb)
        {
            sessionLoggedOn = false;
            disconnectedTcs?.TrySetResult(cb);
            // If connection drops before logon, surface as a logon failure.
            loggedOnTcs?.TrySetException(new InvalidOperationException(
                $"disconnected before logon completed (UserInitiated={cb.UserInitiated})."));
            connectedTcs?.TrySetException(new InvalidOperationException(
                $"disconnected before connect completed (UserInitiated={cb.UserInitiated})."));
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback cb) => loggedOnTcs?.TrySetResult(cb);

        private void OnLicenseList(SteamApps.LicenseListCallback cb) => licenseListTcs?.TrySetResult(cb);

        // --- PICS / depot key / CDN / manifest ----------------------------

        public sealed record ResolvedDepot(uint DepotId, ulong ManifestId);

        public async Task<IReadOnlyList<ResolvedDepot>> ResolveManifestIdsAsync(
            uint appId, IReadOnlyList<uint> depotIds, uint buildId, CancellationToken ct)
        {
            var product = await FetchPicsProductInfoAsync(appId, ct).ConfigureAwait(false);
            var depots = product.KeyValues["depots"];
            if (depots == KeyValue.Invalid)
            {
                throw new InvalidOperationException(
                    $"PICS product info for app {appId} contains no 'depots' section.");
            }

            // Per-build resolution: if buildId == 0 ("latest"), read the public branch's current
            // buildid. Otherwise the contract is "fetch the public branch's current manifest" —
            // Steam doesn't expose per-historical-buildid manifest IDs to anonymous users for free
            // games. A specific buildId is recorded but manifests still resolve from the public
            // branch's current state. Historical-build acquisition goes through the explicit-GID
            // path; here we hand back whatever the public branch is on right now.

            var result = new List<ResolvedDepot>(depotIds.Count);
            foreach (var depotId in depotIds.OrderBy(d => d))
            {
                ct.ThrowIfCancellationRequested();
                var depotNode = depots[depotId.ToString(CultureInfo.InvariantCulture)];
                if (depotNode == KeyValue.Invalid)
                {
                    throw new InvalidOperationException(
                        $"depot {depotId} not present in app {appId}'s PICS depot section.");
                }
                var manifests = depotNode["manifests"];
                if (manifests == KeyValue.Invalid)
                {
                    throw new InvalidOperationException(
                        $"depot {depotId} has no 'manifests' subsection in PICS (depot may be encrypted or DLC-gated).");
                }
                var publicBranch = manifests["public"];
                if (publicBranch == KeyValue.Invalid)
                {
                    throw new InvalidOperationException(
                        $"depot {depotId} has no public branch manifest in PICS.");
                }
                // SteamKit2 represents this as either a leaf with the gid or a
                // record with a "gid" child (newer PICS schema).
                var manifestStr = publicBranch.Value ?? publicBranch["gid"].Value;
                if (string.IsNullOrEmpty(manifestStr))
                {
                    throw new InvalidOperationException(
                        $"depot {depotId} public-branch manifest ID is empty in PICS.");
                }
                if (!ulong.TryParse(manifestStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var manifestId))
                {
                    throw new InvalidOperationException(
                        $"depot {depotId} public-branch manifest ID '{manifestStr}' is not a uint64.");
                }
                result.Add(new ResolvedDepot(depotId, manifestId));
            }
            return result;
        }

        public async Task<uint> ResolveBuildIdAsync(uint appId, uint requested, CancellationToken ct)
        {
            if (requested != 0)
                return requested;
            var product = await FetchPicsProductInfoAsync(appId, ct).ConfigureAwait(false);
            var publicBranch = product.KeyValues["depots"]["branches"]["public"];
            if (publicBranch == KeyValue.Invalid)
                return 0;
            var buildStr = publicBranch["buildid"].Value;
            if (string.IsNullOrEmpty(buildStr))
                return 0;
            return uint.TryParse(buildStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b)
                ? b : 0u;
        }

        /// <summary>Diagnostic: the app's current PICS product info (appinfo) verbatim.</summary>
        public Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> DumpAppInfoAsync(
            uint appId, CancellationToken ct)
            => FetchPicsProductInfoAsync(appId, ct);

        private async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo> FetchPicsProductInfoAsync(
            uint appId, CancellationToken ct)
        {
            // First attempt: bare PICS lookup. Apps fully open to anonymous (e.g. 730) resolve
            // immediately; apps gated behind a free license come back with the app in UnknownApps.
            var found = await TryFetchPicsProductInfoAsync(appId, ct).ConfigureAwait(false);
            if (found is not null)
                return found;

            // Second attempt: request a free license, then retry PICS. This mirrors how
            // DepotDownloader / SteamCMD handle free-to-play apps gated behind a free license —
            // anonymous must opt in before PICS returns product info. (CS2 / app 730 is typically
            // open outright, so this path is a fallback.)
            log.WriteLine($"steam-acquire: PICS for app {appId} returned no info; requesting free license and retrying.");
            try
            {
                var freeLicense = await steamApps.RequestFreeLicense(appId).ToTask().WaitAsync(ct).ConfigureAwait(false);
                if (freeLicense.GrantedApps.Contains(appId) || freeLicense.GrantedPackages.Count > 0)
                {
                    log.WriteLine(
                        $"steam-acquire: free license granted (apps=[{string.Join(",", freeLicense.GrantedApps)}], " +
                        $"packages=[{string.Join(",", freeLicense.GrantedPackages)}]).");
                }
            }
            catch (Exception ex)
            {
                log.WriteLine($"steam-acquire: RequestFreeLicense({appId}) threw {ex.GetType().Name}: {ex.Message}");
            }

            found = await TryFetchPicsProductInfoAsync(appId, ct).ConfigureAwait(false);
            if (found is not null)
                return found;

            throw new InvalidOperationException(
                $"PICS product info for app {appId} was not returned even after RequestFreeLicense. " +
                $"Anonymous access to this app may be denied; verify the app/platform, or run with an authenticated SteamKit2 user (out of v1 scope).");
        }

        private async Task<SteamApps.PICSProductInfoCallback.PICSProductInfo?> TryFetchPicsProductInfoAsync(
            uint appId, CancellationToken ct)
        {
            var appList = new List<uint> { appId };
            var emptyPackages = new List<uint>();
            var tokensResp = await steamApps.PICSGetAccessTokens(appList, emptyPackages)
                .ToTask().WaitAsync(ct).ConfigureAwait(false);

            var request = new SteamApps.PICSRequest(appId);
            if (tokensResp.AppTokens.TryGetValue(appId, out var token))
            {
                request.AccessToken = token;
            }

            var resultSet = await steamApps.PICSGetProductInfo(
                    new List<SteamApps.PICSRequest> { request },
                    new List<SteamApps.PICSRequest>())
                .ToTask().WaitAsync(ct).ConfigureAwait(false);
            var resultsList = resultSet.Results ?? Enumerable.Empty<SteamApps.PICSProductInfoCallback>();
            foreach (var resultBatch in resultsList)
            {
                if (resultBatch.Apps.TryGetValue(appId, out var info))
                {
                    return info;
                }
            }
            return null;
        }

        public async Task<byte[]> GetDepotKeyAsync(uint depotId, uint appId, CancellationToken ct)
        {
            // Depot-key rotation: a stale cached key makes the depot-key call return AccessDenied;
            // drop the cache and retry once.
            if (depotKeyCache.TryGetValue(depotId, out var cached))
            {
                return cached;
            }

            for (int attempt = 1; attempt <= 2; attempt++)
            {
                var job = steamApps.GetDepotDecryptionKey(depotId, appId);
                var resp = await job.ToTask().WaitAsync(ct).ConfigureAwait(false);
                if (resp.Result == EResult.OK)
                {
                    depotKeyCache[depotId] = resp.DepotKey;
                    return resp.DepotKey;
                }
                if (resp.Result == EResult.AccessDenied || resp.Result == EResult.Expired)
                {
                    log.WriteLine($"steam-acquire: depot {depotId} key fetch returned {resp.Result} on attempt {attempt}; retrying once.");
                    depotKeyCache.Remove(depotId);
                    continue;
                }
                throw new InvalidOperationException(
                    $"depot {depotId} key fetch failed with EResult={resp.Result}.");
            }
            throw new InvalidOperationException(
                $"depot {depotId} key fetch failed after rotation retry (EResult.AccessDenied or .Expired persisted).");
        }

        public async Task<CdnServerRotation> PickCdnRotationAsync(CancellationToken ct)
        {
            var servers = await steamContent.GetServersForSteamPipe().WaitAsync(ct).ConfigureAwait(false);
            if (servers is null || servers.Count == 0)
            {
                throw new InvalidOperationException("no Steam CDN servers returned by SteamPipe.");
            }

            // Build a RANKED candidate list rather than picking one host. The SteamPipe directory
            // for a CellID can include Valve-internal hosts that don't resolve publicly (e.g.
            // cache1-blv2.valve.org); picking one kills the download with a DNS error. Rank public
            // CDN hosts first and fail over on transport errors. Every chunk is SHA-1-verified, so
            // server choice doesn't affect the artifact bytes.
            var ranked = RankServers(servers);
            var rotation = new CdnServerRotation(ranked);
            log.WriteLine(
                $"steam-acquire: ranked {rotation.CandidateCount} CDN candidate(s); " +
                $"primary='{rotation.Current.Host}'.");
            return rotation;
        }

        /// <summary>
        /// Deterministically rank CDN servers so that publicly-resolvable hosts
        /// are preferred and tried first, with internal/other hosts kept as
        /// later failover candidates. Ordering:
        ///   1. Hosts ending in ".steamcontent.com" (the public Steam CDN) first.
        ///   2. Everything else after (NOT excluded — list contents vary; we only
        ///      deprioritize so failover can still reach them last).
        ///   3. Tie-break by Host ordinal for a stable, reproducible order.
        /// Null/empty hosts sort last within their group. Pure + deterministic.
        /// </summary>
        internal static IReadOnlyList<Server> RankServers(IEnumerable<Server> servers)
        {
            return servers
                .OrderBy(s => HostRankKey(s.Host))
                .ThenBy(s => s.Host ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>
        /// Primary rank key for a CDN host string: 0 = public *.steamcontent.com
        /// (preferred), 1 = any other resolvable-looking host, 2 = null/empty.
        /// Exposed for unit testing the ranking in isolation (no live CDN).
        /// </summary>
        internal static int HostRankKey(string? host)
        {
            if (string.IsNullOrEmpty(host))
            {
                return 2;
            }
            return host.EndsWith(".steamcontent.com", StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1;
        }

        /// <summary>
        /// Rank a list of host strings deterministically (used by unit tests and
        /// as the basis for <see cref="RankServers"/>). Returns the hosts in the
        /// order they would be tried.
        /// </summary>
        internal static IReadOnlyList<string?> RankHosts(IEnumerable<string?> hosts)
        {
            return hosts
                .OrderBy(HostRankKey)
                .ThenBy(h => h ?? string.Empty, StringComparer.Ordinal)
                .ToList();
        }

        public async Task<DepotManifest> DownloadManifestAsync(
            uint depotId, ulong manifestId, uint appId, byte[] depotKey, CdnServerRotation rotation, CancellationToken ct)
        {
            // Manifest request code: required for anonymous downloads from the
            // public branch since ~2022. Pass "public" branch unconditionally.
            var requestCode = await steamContent
                .GetManifestRequestCode(depotId, appId, manifestId, "public")
                .WaitAsync(ct).ConfigureAwait(false);
            if (requestCode == 0)
            {
                throw new InvalidOperationException(
                    $"manifest request code for depot {depotId} manifest {manifestId} came back zero. " +
                    "Anonymous access to this depot may be denied (paid DLC, region-gated, etc).");
            }

            // Try each ranked CDN candidate in turn. Transport/DNS failures fail over to the next
            // host (a single unreachable internal host must not kill the manifest fetch). Any
            // non-transport failure (e.g. a manifest integrity/decrypt error) fails loud.
            Exception? lastTransportError = null;
            while (true)
            {
                var server = rotation.Current;
                try
                {
                    var manifest = await cdnClient
                        .DownloadManifestAsync(depotId, manifestId, requestCode, server, depotKey)
                        .WaitAsync(ct).ConfigureAwait(false);
                    if (manifest is null)
                    {
                        throw new InvalidOperationException(
                            $"manifest download for depot {depotId} returned null.");
                    }
                    return manifest;
                }
                catch (Exception ex) when (IsTransportFailure(ex, ct))
                {
                    lastTransportError = ex;
                    if (!rotation.AdvanceOnTransportFailure(out var nextServer))
                    {
                        throw new InvalidOperationException(
                            $"manifest download for depot {depotId} failed on all " +
                            $"{rotation.CandidateCount} ranked CDN candidate(s).",
                            lastTransportError);
                    }
                    log.WriteLine(
                        $"steam-acquire: manifest fetch on CDN '{server.Host}' for depot {depotId} failed " +
                        $"({ex.GetType().Name}: {ex.Message}); failing over to '{nextServer.Host}'.");
                }
            }
        }

        public async Task<int> DownloadChunkAsync(
            uint depotId, byte[] depotKey, Server server,
            DepotManifest.ChunkData chunk, byte[] dest, CancellationToken ct)
        {
            // SteamKit2's CDN.Client.DownloadDepotChunkAsync verifies the chunk
            // SHA-1 against chunk.ChunkID internally. A mismatch surfaces as an
            // exception thrown by SteamKit2 — we let it propagate.
            return await cdnClient
                .DownloadDepotChunkAsync(depotId, chunk, server, dest, depotKey)
                .WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
