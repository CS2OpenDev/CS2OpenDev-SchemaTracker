// Steam acquisition test seam.
//
// AcquireCommand depends on this interface, not on the concrete SteamAnonymousAcquirer, so the
// command can be unit-tested without standing up a Steam connection.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// A no-op <see cref="IDisposable"/> used as the default <see cref="ISteamAcquirer.BeginSharedSession"/>
/// result for acquirers that do not model a shared Steam session (the unit-test fakes that connect to
/// nothing). Disposing it does nothing; the per-call connect-then-done lifecycle is unchanged.
/// </summary>
internal sealed class NoOpDisposable : IDisposable
{
    public static readonly NoOpDisposable Instance = new();
    private NoOpDisposable() { }
    public void Dispose() { }
}

internal interface ISteamAcquirer
{
    /// <summary>
    /// Batch session lifecycle. Open ONE shared Steam connection+logon for the lifetime of the
    /// returned scope; while open, EVERY acquire call on this acquirer reuses that single session
    /// instead of connecting+logging-on per call. Disposing the scope tears the session down. The BATCH
    /// path (<c>acquire --all</c> / repeated <c>--build</c>) opens exactly one scope around its whole
    /// per-build loop so a 244-build run does ONE logon, not 244 — Steam rate-limits LOGONS
    /// (<c>AccountLoginDeniedThrottle</c> after ~58 logons/window), not data transfer.
    ///
    /// Outside a scope (the default), every acquire owns its own connect+logon+disconnect — so the
    /// single-build / <c>--from-provenance</c> / <c>--content</c> / probe paths keep their
    /// connect-once-then-done lifecycle. The default implementation is a no-op (acquirers that connect
    /// to nothing, e.g. test fakes, need do nothing).
    /// </summary>
    IDisposable BeginSharedSession() => NoOpDisposable.Instance;

    /// <summary>
    /// Fetch every binary required for the given (appId, depotIds) at the
    /// given <paramref name="buildId"/>, write them under <paramref name="outDir"/>,
    /// and return a result describing what was acquired.
    /// </summary>
    /// <param name="appId">Steam app ID — always 730 (CS2 is one app; there is no separate server app).</param>
    /// <param name="depotIds">Depot IDs to acquire (every binary in every depot is fetched).</param>
    /// <param name="buildId">Steam build ID, or 0 to mean "latest public-branch build".</param>
    /// <param name="outDir">
    /// Absolute path to the output directory. Acquisition writes to
    /// "<paramref name="outDir"/>.partial" first; on full success the partial
    /// directory is moved to <paramref name="outDir"/>.
    /// </param>
    /// <param name="ct">Cancellation token; cancellation discards the partial output.</param>
    /// <exception cref="System.IO.InvalidDataException">
    /// On any manifest / chunk / file hash mismatch. The partial output is left
    /// in place for forensic inspection — but the final <paramref name="outDir"/>
    /// is NOT created. Caller surfaces this as a non-zero exit code.
    /// </exception>
    Task<AcquireResult> AcquireAsync(
        uint appId,
        IReadOnlyList<uint> depotIds,
        uint buildId,
        string outDir,
        CancellationToken ct);

    /// <summary>
    /// Fetch a SPECIFIC, EXPLICIT set of (depotId -> manifestId) — bypassing PICS "current public
    /// branch" resolution. This is the historical-build path: anonymous PICS only ever exposes the
    /// current manifest per depot, so re-fetching a PRIOR build requires the per-depot manifest GIDs
    /// supplied out-of-band (from our own recorded history — see <see cref="ManifestRecord"/>).
    ///
    /// Everything downstream of manifest resolution is shared with
    /// <see cref="AcquireAsync(uint, IReadOnlyList{uint}, uint, string, CancellationToken)"/>:
    /// the same CDN rotation + per-chunk SHA verify + resume path. The content
    /// must still be CDN-resident; Valve purges old chunks over time, so this
    /// can fail-loud at manifest download or chunk fetch even with a valid GID.
    /// </summary>
    /// <param name="spec">The explicit app/build/depot-manifest set to acquire.</param>
    /// <param name="outDir">Absolute output directory (same .partial-then-move semantics).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AcquireResult> AcquireExplicitAsync(
        ManifestSpec spec,
        string outDir,
        CancellationToken ct);

    /// <summary>
    /// Minimal-footprint CONTENT-depot acquire for gameevents.
    /// The CS2 content depot (2347770) is ~59 GB; this fetches only the pak01 VPK
    /// files needed for the `.gameevents` resources, in two phases:
    ///   Phase A fetches game/csgo/pak01_dir.vpk only; Phase B parses it to find the external
    ///   `_NNN.vpk` chunk(s) backing the `.gameevents` entries and fetches just those (plus the
    ///   directory file) into <paramref name="outDir"/>.
    /// When <paramref name="minimalGameEvents"/> is false, the FALLBACK admits the
    /// entire game/csgo/pak01_*.vpk set (dir + all chunks) — still far less than
    /// the full depot. Same atomic .partial-then-move + per-file SHA verify path as
    /// every other acquire.
    ///
    /// <paramref name="explicitSpec"/> supplies the content depot's manifest GID
    /// verbatim for the HISTORICAL path: anonymous PICS exposes only the CURRENT
    /// 2347770 manifest, so re-fetching a PRIOR build's pak01 VPK requires that
    /// build's recorded 2347770 GID + authenticated logon. When the spec carries the
    /// content depot, Phase A/B fetch THAT manifest's pak01_dir.vpk + chunks instead
    /// of PICS-current. When null, PICS resolves the current public-branch content
    /// manifest (the forward-capture path). The resolved 2347770 identity is merged
    /// into manifest-record.json either way (load-bearing: gameevents.json ⟺ 2347770
    /// ∈ provenance).
    /// </summary>
    /// <param name="appId">Steam app ID — 730.</param>
    /// <param name="contentDepotId">The CS2 shared content depot ID (2347770).</param>
    /// <param name="buildId">Steam build ID, or 0 for the current public-branch build.</param>
    /// <param name="outDir">Absolute output directory (content lands under game/csgo/).</param>
    /// <param name="minimalGameEvents">True = two-phase minimal; false = full pak01 set.</param>
    /// <param name="explicitSpec">
    /// Non-null = historical path: the per-depot manifest GIDs (must include the
    /// content depot <paramref name="contentDepotId"/>) are taken verbatim, bypassing
    /// PICS. Null = PICS-current resolution (forward capture).
    /// </param>
    /// <param name="dirOnly">
    /// gameevents-dedup groundwork. When true, run PHASE A ONLY: fetch the
    /// directory file <c>game/csgo/pak01_dir.vpk</c> into <paramref name="outDir"/>,
    /// merge the content depot identity into manifest-record.json as usual, and STOP
    /// (skip Phase B archive fetch). This is the cheap (~7 MB) per-content-manifest
    /// index pull used to read the <c>.gameevents</c> CRC32s for fileset dedup across
    /// the ~199 distinct content manifests. <paramref name="minimalGameEvents"/> is
    /// ignored when this is true. Fail-loud if pak01_dir.vpk is absent from the manifest.
    /// </param>
    Task<AcquireResult> AcquireContentPakAsync(
        uint appId,
        uint contentDepotId,
        uint buildId,
        string outDir,
        bool minimalGameEvents,
        ManifestSpec? explicitSpec,
        bool dirOnly,
        CancellationToken ct);

    /// <summary>
    /// Minimal-footprint WORKSHOP-TOOLS-depot acquire (windows-only editor DLLs).
    /// The CS2 Workshop Tools depot (2347779) is ~2.09 GB/build, but the walker only loads the
    /// editor tool DLLs (~200 MB — game/bin/win64/*.dll incl. the tools/ subdir, and
    /// game/csgo/bin/win64/modtools.dll). This fetches ONLY the manifest files matching
    /// <see cref="ToolsBinSelector"/> (".dll" under "game/") into a staging dir, then MERGES them
    /// non-destructively into <paramref name="outDir"/> — the per-build windows binaries dir — the
    /// same stage-then-merge co-location the content pak uses (a wipe-and-replace would destroy the
    /// co-located base binaries). The resolved 2347779 identity is merged into the dir's
    /// manifest-record.json so the tools slice accumulates re-fetchable history.
    ///
    /// <paramref name="explicitSpec"/> supplies the tools depot's manifest GID verbatim for the
    /// HISTORICAL path (anonymous PICS exposes only the CURRENT 2347779 manifest, so a prior
    /// build's tools slice requires that build's recorded GID + authenticated logon; the spec MUST
    /// list the tools depot — fail-loud before any Steam contact otherwise). When null, PICS
    /// resolves the current public-branch tools manifest (the forward-capture path).
    /// </summary>
    /// <param name="appId">Steam app ID — 730 (the tools depot lives under the CS2 app).</param>
    /// <param name="toolsDepotId">The CS2 Workshop Tools depot ID (2347779).</param>
    /// <param name="buildId">Steam build ID, or 0 for the current public-branch build.</param>
    /// <param name="outDir">Absolute output directory — the per-build windows binaries dir the DLLs merge into.</param>
    /// <param name="explicitSpec">
    /// Non-null = historical path: the per-depot manifest GIDs (must include the tools depot
    /// <paramref name="toolsDepotId"/>) are taken verbatim, bypassing PICS. Null = PICS-current
    /// resolution (forward capture).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<AcquireResult> AcquireToolsAsync(
        uint appId,
        uint toolsDepotId,
        uint buildId,
        string outDir,
        ManifestSpec? explicitSpec,
        CancellationToken ct);

    /// <summary>
    /// Loadable-BINARIES-ONLY binary-depot acquire (historical backfill). The per-OS binary depot
    /// (2347771 win / 2347773 linux) is ~7.9 GB/build but only ~0.46 GB is the native binaries the
    /// walker loads (the DLLs/.so under the per-OS bin dirs). This fetches ONLY those bin-directory
    /// subtrees (<see cref="BinaryBinSelector"/>) via a manifest file filter — the same per-chunk SHA
    /// verify / chunk resume / depot-key rotation / atomic .partial-then-move / manifest-record path
    /// as a full acquire — turning the ~5.3 TB full-history pull into ~307 GB.
    ///
    /// <paramref name="explicitSpec"/> supplies the per-depot manifest GIDs verbatim for the
    /// HISTORICAL path (anonymous PICS exposes only the current manifest, so historical builds
    /// require authenticated logon + explicit GIDs); when null, PICS resolves the current
    /// public-branch build for (appId, depotIds, buildId). <paramref name="platform"/> selects the
    /// per-OS bin-directory prefix set and MUST be a known platform (fail-loud, before any Steam
    /// contact).
    /// </summary>
    Task<AcquireResult> AcquireBinariesOnlyAsync(
        uint appId,
        IReadOnlyList<uint> depotIds,
        uint buildId,
        string outDir,
        string platform,
        ManifestSpec? explicitSpec,
        CancellationToken ct);

    /// <summary>
    /// CHEAP probe: resolve the CURRENT public-branch build id + per-depot current manifest GIDs via
    /// PICS. Fetches NO depot content.
    /// </summary>
    Task<CurrentPicsResult> ProbeCurrentPicsAsync(
        uint appId, IReadOnlyList<uint> depotIds, CancellationToken ct);

    /// <summary>
    /// CHEAP probe: test whether a SPECIFIC PRIOR manifest set is still anonymously fetchable at
    /// manifest level, optionally pulling one
    /// sample chunk per depot to confirm CDN residency. Captures per-depot
    /// failures into the result (diagnostic, not an acquire).
    /// </summary>
    Task<ExplicitManifestProbe> ProbeExplicitManifestAsync(
        ManifestSpec spec, bool probeOneChunk, CancellationToken ct);
}
