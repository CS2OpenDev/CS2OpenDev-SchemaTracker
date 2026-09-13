// `content-backfill` — fetch a NEWLY-TRACKED content pak (today: the engine core pak,
// resource/core.gameevents) for committed builds whose content store predates it.
//
// The content store is keyed by the 2347770 manifest GID and shared across every build/platform whose
// content depot did not change, so the backfill is per-GID: fetch the missing pak ONCE per unique GID
// and every build sharing it is covered. ContentBackfillPlanner does the pure enumeration (which GIDs
// still lack the pak). For each target this command runs the SAME historical content acquire the
// `acquire --from-manifest` unified content leg uses — AcquireContentPakAsync with the representative
// build's manifest spec — which (post core-pak support) fetches + trims the core pak into
// _content/<gid>/game/core while skipping the already-complete csgo copy.
//
// `--pak` selects WHICH pak to plan for (default `core`, the historical behaviour). `--pak csgo`
// targets the primary content pak, whose store copies also go stale when
// ContentPakSelector.EnumerateRequiredEntries gains a resource: the stored trim predates the new
// path, so it is a proper-but-OLD trim. ContentStore's required-set generation marker makes those
// visible to the planner, and this is the command that re-fetches them — the remedy
// ExtractCommand's stale-store guard names.
//
// INCREMENTAL by construction. `--pak csgo` targets GIDs that already HAVE a store copy — only a
// stale one — so re-fetching the whole required set would re-download bytes we already hold,
// byte-identical, in the very trim about to be overwritten. Measured across 13 builds spanning every
// era, 93.9% of those bytes are the resource/csgo_<lang>.txt localization tables and 5.8% is
// items_game.txt, against 0.02% for the resource that made them stale. The acquire path therefore
// partitions the fresh required set against the store copy (ContentPatchPlan) and fetches only what
// is genuinely missing, which turns a ~50 GB corpus-wide campaign into a few GB. The per-GID
// re-used-vs-fetched split and the transferred byte count are LOGGED per GID and totalled at the end,
// so the saving is a number in the run log rather than an assumption. That total ADDS the Phase-A
// directory-index fetch the acquire's own AcquireResult.DownloadedBytes omits on the Phase-B branch —
// the index is downloaded twice (once to parse the required set, once as the range plan's whole-file
// entry) and only one of those is reported. The run log therefore says what Steam actually sent rather
// than a figure that flatters the saving.
//
// ONE Steam logon per RUN, not per GID. Steam rate-limits authenticated LOGONS, not bytes: a campaign
// that stood up its own SteamClient per GID was refused with AccountLoginDeniedThrottle after 118 of
// 381 GIDs, and a re-run ten hours later was refused before it transferred a byte. `--execute` opens
// ONE ISteamAcquirer.BeginSharedSession scope around the whole per-GID loop — the same single-logon
// wiring the `acquire` batch uses — so a 381-GID run connects and logs on once. Spacing the GIDs out
// does not help and never did; the limit counts logons, not their rate.
//
// The failure taxonomy is what keeps a shared session from turning one fault into 381. A per-GID DATA
// fault (bad manifest, purged chunk, hash mismatch) fail-isolates and the run continues. A session DROP
// fails only the GID it hit — the next GID's lease reconnects once and the run carries on. A logon
// THROTTLE, or a connect/logon that cannot be re-established, stops the run: every remaining GID would
// fail identically, and the store is content-addressed + idempotent, so re-running skips the completed
// GIDs and resumes with the rest. Any other cascade is caught by the consecutive-failure stop, so a
// drop can never quietly mark the whole remainder failed.
//
// DRY-RUN by default (prints the plan, contacts no Steam). `--execute` performs the fetch. It does NOT
// re-extract the content artifacts — that is a subsequent `extract` pass over the affected builds
// (the core.gameevents events, or the newly-required csgo resources, flow in once the store carries
// them).

using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host.Cli;

internal static class ContentBackfillCommand
{
    public static int Run(string[] args)
        => RunAsync(args, acquirerFactory: null).GetAwaiter().GetResult();

    /// <summary>
    /// Test seam: inject a fake <see cref="ISteamAcquirer"/> so the fetch loop can be exercised without
    /// Steam. Production passes null and builds the real anonymous/credentialed acquirer.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, Func<ISteamAcquirer>? acquirerFactory)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        bool execute = parsed.ContainsKey("execute");
        int limit = parsed.TryGetValue("limit", out var lim) && int.TryParse(lim, out var l) && l > 0
            ? l
            : int.MaxValue;
        // Optional pause between GIDs — CDN pacing only. It does NOT mitigate logon throttling (the run
        // logs on once, and the limit counts logons, not their rate). Default 0 (fastest).
        int delaySeconds = parsed.TryGetValue("delay-seconds", out var ds) && int.TryParse(ds, out var d) && d > 0
            ? d
            : 0;

        var root = parsed.TryGetValue("binaries-root", out var r) && !string.IsNullOrEmpty(r)
            ? r
            : Config.HostConfig.BinariesRoot;
        if (string.IsNullOrEmpty(root))
        {
            Console.Error.WriteLine(
                "content-backfill: no store root — pass --binaries-root <dir> or set CS2_BINARIES_ROOT.");
            return 64;
        }
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"content-backfill: store root not found: '{root}'.");
            return 66;
        }

        // Which pak to plan for. Default `core` preserves the original behaviour verbatim; `csgo`
        // targets the primary pak (the required-set-generation backfill).
        if (!TryResolvePak(parsed, out var pak))
        {
            Console.Error.WriteLine(
                $"content-backfill: unknown --pak '{(parsed.TryGetValue("pak", out var bad) ? bad : "")}' "
                + $"(expected one of: {ContentPak.NamesForHelp}).");
            return 64;
        }

        IReadOnlyList<ContentBackfillPlanner.BackfillTarget> targets;
        try
        {
            targets = ContentBackfillPlanner.Plan(root, pak);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            Console.Error.WriteLine($"content-backfill: planning failed: {ex.Message}");
            return 65;
        }

        if (targets.Count == 0)
        {
            Console.Error.WriteLine(
                $"content-backfill: nothing to do — every committed content GID already carries the "
                + $"'{pak.BaseRelDir}' pak in the store.");
            return 0;
        }

        long totalBuilds = targets.Sum(t => t.BuildCount);
        Console.Error.WriteLine(
            $"content-backfill: {targets.Count} content GID(s) missing the '{pak.BaseRelDir}' pak, "
            + $"covering {totalBuilds} committed (build, platform) set(s):");
        foreach (var t in targets)
        {
            Console.Error.WriteLine(
                $"  GID {t.ContentGid} — {t.BuildCount} set(s), representative '{t.RepresentativeTupleDir}'.");
        }

        if (!execute)
        {
            Console.Error.WriteLine(
                "content-backfill: DRY-RUN (no Steam contact). Re-run with --execute to fetch. After a "
                + $"successful fetch, re-run `extract` over the affected builds so the '{pak.BaseRelDir}' "
                + "content artifacts are re-emitted from the refreshed store.");
            return 0;
        }

        // ---- EXECUTE: fetch the missing pak per GID via a historical content acquire ----
        ISteamAcquirer acquirer = acquirerFactory?.Invoke()
            ?? AcquireCommand.BuildRealAcquirer(
                explicitAuth: false, historicalPath: true,
                guardCode: parsed.TryGetValue("steam-guard", out var g) ? g : null);

        var toFetch = limit == int.MaxValue ? targets : targets.Take(limit).ToList();
        if (toFetch.Count < targets.Count)
        {
            Console.Error.WriteLine(
                $"content-backfill: --limit {limit} — fetching the first {toFetch.Count} of {targets.Count} GID(s) this run.");
        }

        int fetched = 0, failed = 0, skipped = 0;
        // Campaign accounting. The saving the incremental refresh buys is only believable if it is
        // MEASURED, so each GID reports what Steam actually transferred and the run reports the total —
        // a number to compare against the full-fetch cost rather than an estimate to trust.
        long transferredBytes = 0;
        // Non-null once something RUN-level (not GID-level) ended the loop early: it names the stop in
        // the summary and makes the run exit non-zero.
        string? stopReason = null;
        // Failures since the last successful fetch — the cascade guard described on ConsecutiveStop.
        int consecutiveFailures = 0;
        bool first = true;

        // ONE shared Steam session for the whole run. Every AcquireContentPakAsync below leases that
        // single connection+logon instead of standing up its own SteamClient, so a 381-GID campaign
        // performs ONE logon rather than 381 — and logons are what Steam rate-limits. The scope tears
        // the session down when the loop ends, whether it ran out of targets or broke out early.
        using (acquirer.BeginSharedSession())
        {
            foreach (var t in toFetch)
            {
                if (!first && delaySeconds > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds)).ConfigureAwait(false);
                }
                first = false;

                var spec = new ManifestSpec(
                    t.Record.AppId, t.Record.BuildId,
                    t.Record.Depots.Select(d => new ManifestSpecDepot(d.DepotId, d.ManifestId)).ToList());
                if (!spec.Depots.Any(d => d.DepotId == ContentStore.ContentDepotId))
                {
                    Console.Error.WriteLine(
                        $"content-backfill: SKIP GID {t.ContentGid} — representative record carries no "
                        + $"{ContentStore.ContentDepotId} content depot GID (cannot spec the fetch).");
                    skipped++;
                    continue;
                }
                try
                {
                    Console.Error.WriteLine(
                        $"content-backfill: fetching '{pak.BaseRelDir}' for GID {t.ContentGid} via build "
                        + $"{spec.BuildId} into '{t.RepresentativeTupleDir}' ...");
                    var result = await acquirer.AcquireContentPakAsync(
                        spec.AppId, ContentStore.ContentDepotId, buildId: 0, t.RepresentativeTupleDir,
                        minimalGameEvents: true, explicitSpec: spec, dirOnly: false,
                        CancellationToken.None).ConfigureAwait(false);
                    fetched++;
                    consecutiveFailures = 0;
                    long phaseAIndexBytes = PhaseAIndexBytes(result);
                    long gidBytes = result.DownloadedBytes + phaseAIndexBytes;
                    transferredBytes += gidBytes;
                    string indexNote = phaseAIndexBytes > 0
                        ? $" ({result.DownloadedBytes:N0} counted by the acquire + {phaseAIndexBytes:N0} for the "
                            + "Phase-A directory index it does not count)"
                        : "";
                    Console.Error.WriteLine(
                        $"content-backfill: GID {t.ContentGid} done — {gidBytes:N0} byte(s) transferred from Steam"
                        + $"{indexNote} ({transferredBytes:N0} across {fetched} GID(s) so far). The "
                        + "per-entry re-used-vs-fetched split for this GID is on the `content refresh plan` "
                        + "and `content-store repack` lines above.");
                }
                catch (Exception ex)
                {
                    failed++;
                    consecutiveFailures++;
                    Console.Error.WriteLine(
                        $"content-backfill: FAILED GID {t.ContentGid}: {ex.GetType().Name}: {ex.Message}");

                    stopReason = ClassifyRunStop(ex, consecutiveFailures);
                    if (stopReason is not null)
                    {
                        Console.Error.WriteLine(StopAdvice(stopReason));
                        break;
                    }

                    // A DROPPED session is survivable: the next fetch's lease reconnects it ONCE and
                    // the run carries on, so only this GID is lost. Say so in the log rather than
                    // leaving the reader to infer it from the acquirer's reconnect line.
                    if (IsSessionDrop(ex))
                    {
                        Console.Error.WriteLine(
                            "content-backfill: the shared Steam session dropped on this GID; the next "
                            + "fetch reconnects it ONCE and the run continues.");
                    }
                }
            }
        }

        int remaining = targets.Count - fetched - skipped;
        Console.Error.WriteLine(
            $"content-backfill: {(stopReason is null ? "done" : $"STOPPED ({stopReason})")} — "
            + $"fetched={fetched} failed={failed} skipped={skipped} this run, "
            + $"{transferredBytes:N0} byte(s) transferred in total; {remaining} GID(s) still missing "
            + $"the '{pak.BaseRelDir}' pak (of {targets.Count}). Re-run to resume; then `extract` over "
            + $"the affected builds to re-emit the '{pak.BaseRelDir}' content artifacts.");
        return (failed > 0 || stopReason is not null) ? 1 : 0;
    }

    /// <summary>The <c>--pak</c> default: the engine core pak, preserving the original behaviour.</summary>
    internal const string DefaultPakName = "core";

    /// <summary>
    /// Resolve the <c>--pak</c> value the way <see cref="RunAsync"/> does: absent/empty ⇒
    /// <see cref="DefaultPakName"/>, otherwise a case-insensitive short pak name. False on an
    /// unrecognized value (the command then exits 64 naming the accepted set).
    /// </summary>
    internal static bool TryResolvePak(IReadOnlyDictionary<string, string> parsed, out ContentPak pak)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var name = parsed.TryGetValue("pak", out var value) && !string.IsNullOrEmpty(value)
            ? value
            : DefaultPakName;
        return ContentPak.TryParse(name, out pak);
    }

    /// <summary>Summary label for a run stopped by Steam refusing further logons.</summary>
    internal const string StopThrottle = "Steam throttle";

    /// <summary>Summary label for a run stopped because the shared session could not be re-established.</summary>
    internal const string StopNoSession = "Steam session unavailable";

    /// <summary>
    /// Failures since the last success that end the run. A per-GID fault is isolated and the loop
    /// continues, but a fault that repeats this many times running is not per-GID — it is the shared
    /// session, Steam, or the CDN being unavailable in a way the message did not name. Stopping there
    /// costs at most two wasted attempts and keeps a bad session from marking every remaining GID
    /// failed; the store is idempotent, so the re-run picks up exactly where this one left off.
    /// </summary>
    internal const int ConsecutiveStop = 3;

    /// <summary>
    /// Classify a per-GID failure as RUN-level (returns the summary label, and the caller stops) or
    /// GID-level (returns null, and the caller carries on). Order matters: a throttle arrives as a
    /// credentials-stage logon rejection, so it is recognized before the general logon failure.
    /// </summary>
    private static string? ClassifyRunStop(Exception ex, int consecutiveFailures)
    {
        if (IsSteamThrottle(ex))
        {
            return StopThrottle;
        }
        if (IsSessionUnavailable(ex))
        {
            return StopNoSession;
        }
        return consecutiveFailures >= ConsecutiveStop
            ? $"{consecutiveFailures} consecutive failures"
            : null;
    }

    /// <summary>What to tell the operator about a stop named by <see cref="ClassifyRunStop"/>.</summary>
    private static string StopAdvice(string stopReason) => stopReason switch
    {
        StopThrottle =>
            "content-backfill: STOPPING — Steam is rate-limiting authenticated logons. This run logs on "
            + "ONCE, so the throttle is carried over from earlier logons rather than earned by this run; "
            + "wait for it to clear (minutes to a few hours), then re-run the SAME command — completed "
            + "GIDs are skipped and it resumes with the remainder.",
        StopNoSession =>
            "content-backfill: STOPPING — the shared Steam session could not be established or "
            + "re-established, so every remaining GID would fail identically. Re-run the SAME command "
            + "once Steam is reachable — completed GIDs are skipped and it resumes with the remainder.",
        _ =>
            $"content-backfill: STOPPING — {ConsecutiveStop} GIDs failed in a row, which is a run-level "
            + "fault (an unusable session, Steam, or the CDN) rather than that many bad GIDs. Fix what "
            + "the failures above name, then re-run the SAME command — completed GIDs are skipped and "
            + "it resumes with the remainder.",
    };

    /// <summary>
    /// True when <paramref name="ex"/> indicates Steam is throttling authenticated logons — the
    /// signal to stop and resume later rather than keep hammering (which prolongs the throttle).
    /// </summary>
    private static bool IsSteamThrottle(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || m.Contains("AccountLoginDeniedThrottle", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the shared session DROPPED mid-fetch (the connection went away; SteamKit reports the
    /// handler calls as needing a connection). Survivable: the acquirer's next lease reconnects once,
    /// so only the GID that hit the drop is lost. This is NOT a stop — before the run shared one
    /// session, a drop had nothing left to reconnect and was treated as terminal.
    /// </summary>
    private static bool IsSessionDrop(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("must be connected", StringComparison.OrdinalIgnoreCase)
            || m.Contains("not connected", StringComparison.OrdinalIgnoreCase)
            || m.Contains("session dropped", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when connect-or-logon ITSELF failed, so the shared session does not exist and the acquirer's
    /// reconnect-once already had its turn. Every remaining GID would fail the same way, so the run
    /// stops rather than converting the whole remainder into failures.
    /// <para>
    /// The probes are the messages <c>SteamAnonymousAcquirer.Session.ConnectAndLogonAsync</c> actually
    /// produces, because that is the method the reconnect goes through: the <c>OnDisconnected</c> faults
    /// <c>disconnected before connect completed (UserInitiated=...)</c> and <c>disconnected before logon
    /// completed (UserInitiated=...)</c>, the <c>&lt;anonymous|authenticated&gt; logon failed with
    /// EResult=... ExtendedResult=...</c> refusal, the <c>authenticated logon rejected at credentials
    /// stage (EResult=...)</c> refusal, and <c>authenticated logon produced no LoggedOnCallback after
    /// token exchange.</c> An earlier probe here matched "failed to connect to steam", which the acquirer
    /// never emits — so the one-session-per-run change made the refused-reconnect path reachable and this
    /// classifier could not recognize a single message it produces.
    /// </para>
    /// <para>
    /// These are substrings only because there is nothing stabler to match: the acquirer exposes no typed
    /// session-failure exception and no session-status enum. <see cref="SteamGuardRequiredException"/> is
    /// the one typed case and is matched as a type above. The two "disconnected before ..." faults cannot
    /// steal a mid-fetch drop from <see cref="IsSessionDrop"/>: they complete TCSs that are awaited only
    /// inside ConnectAndLogonAsync, so a drop during a fetch finds them already completed.
    /// </para>
    /// </summary>
    private static bool IsSessionUnavailable(Exception ex)
    {
        if (ex is SteamGuardRequiredException)
        {
            return true;
        }
        var m = ex.Message;
        return m.Contains("logon failed with EResult", StringComparison.OrdinalIgnoreCase)
            || m.Contains("logon rejected at credentials stage", StringComparison.OrdinalIgnoreCase)
            || m.Contains("produced no LoggedOnCallback", StringComparison.OrdinalIgnoreCase)
            || m.Contains("disconnected before connect completed", StringComparison.OrdinalIgnoreCase)
            || m.Contains("disconnected before logon completed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Phase-A directory-index transfer that <paramref name="result"/> does NOT count, so the run can
    /// report what Steam actually sent.
    /// <para>
    /// AcquireContentPakAsync fetches every content pak's <c>pak01_dir.vpk</c> WHOLE in Phase A to parse
    /// the required set, then — when the patch partition leaves body ranges to fetch — returns ONLY Phase
    /// B's result, dropping Phase A's transfer from <see cref="AcquireResult.DownloadedBytes"/>. Phase B
    /// genuinely re-downloads that index (BuildByteRangePlan always lists the directory file as a whole
    /// file, and Phase B stages into a fresh <c>.partial</c> where the chunk-resume probe hits nothing),
    /// so the index is paid for TWICE and the acquire reports one of them. A whole-file fetch's
    /// <see cref="AcquiredFileInfo.SizeBytes"/> IS the number of bytes it transferred, which is why
    /// summing the result's directory files restores the unreported half.
    /// </para>
    /// <para>
    /// When Phase B was SKIPPED the acquirer hands back Phase A's own result, whose DownloadedBytes
    /// already counts the index and whose Files are directory files ONLY — the "carries a non-directory
    /// file" gate returns 0 there, so that shape is never double-counted. The addend is exact only while
    /// both of those hold: Phase B re-fetching the directory file whole, and Phase A's staging being
    /// fresh. If a later acquirer change lets Phase B reuse Phase A's staging, this over-reports.
    /// </para>
    /// <para>
    /// This derivation exists ONLY because AcquireResult carries no Phase-A field. If the acquirer ever
    /// folds Phase A into DownloadedBytes, DELETE this helper and its call site in the SAME change or the
    /// index is counted twice.
    /// </para>
    /// </summary>
    private static long PhaseAIndexBytes(AcquireResult result)
    {
        static bool IsIndex(AcquiredFileInfo f)
            => ContentPak.All.Any(p => p.IsDirectoryFile(f.RelativePath));
        return result.Files.Any(f => !IsIndex(f))
            ? result.Files.Where(IsIndex).Sum(f => f.SizeBytes)
            : 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(
@"cs2-schema-tracker content-backfill — fetch missing/stale content paks for committed builds.

Fetches one content pak for every committed content GID whose store copy is missing or was trimmed
under an older required-set generation, keyed + deduped by the 2347770 manifest GID (ONCE per GID).

Usage: cs2-schema-tracker content-backfill [--binaries-root <dir>] [--pak <csgo|core>] [--execute]
                                           [--limit N] [--delay-seconds N] [--steam-guard <code>]

  --binaries-root <dir>  Store root (default: CS2_BINARIES_ROOT).
  --pak <csgo|core>      Which content pak to back-fill (default: core, the engine
                         resource/core.gameevents pak). Use `csgo` to refresh primary-pak store
                         copies that predate a newly-required content resource — the remedy
                         `extract` names when it refuses a stale store.
  --execute              Perform the Steam fetch. Omit for a DRY-RUN plan (no Steam contact).
  --limit N              Fetch at most N content GIDs this run (controlled rollout).
  --delay-seconds N      Pause N seconds between GIDs to pace CDN work (default 0). It does NOT affect
                         logon throttling: the run logs on ONCE, and Steam limits the NUMBER of
                         authenticated logons rather than their rate.
  --steam-guard <code>   Steam Guard code, if credentialed auth is required for historical manifests.

One authenticated Steam logon serves the WHOLE run: the session is opened once and every GID's fetch
reuses it, so a 381-GID campaign costs one logon rather than 381 (Steam limits logons, not bytes).

The store is content-addressed + idempotent: the fetch is deduped per content GID, completed GIDs are
skipped, and the run STOPS cleanly if Steam throttles logons or the shared session cannot be
re-established — just re-run to resume. A session that merely drops mid-run costs the GID it hit and
then reconnects. After the fetch, re-run `extract` over the affected builds so their content artifacts
are re-emitted.");
    }
}
