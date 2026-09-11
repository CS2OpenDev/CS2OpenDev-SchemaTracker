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
// so the saving is a number in the run log rather than an assumption.
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
        // Optional pause between GIDs. The fetch re-authenticates per GID, so spacing them out reduces
        // the chance Steam throttles the account (which halts the run). Default 0 (fastest).
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
        bool throttled = false;
        bool first = true;
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
                transferredBytes += result.DownloadedBytes;
                Console.Error.WriteLine(
                    $"content-backfill: GID {t.ContentGid} done — {result.DownloadedBytes:N0} byte(s) "
                    + $"transferred from Steam ({transferredBytes:N0} across {fetched} GID(s) so far). The "
                    + "per-entry re-used-vs-fetched split for this GID is on the `content refresh plan` "
                    + "and `content-store repack` lines above.");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine(
                    $"content-backfill: FAILED GID {t.ContentGid}: {ex.GetType().Name}: {ex.Message}");

                // Steam login THROTTLE is not a per-GID fault — it means "too many logons, back off".
                // Every subsequent attempt makes it worse (and burns the run), so STOP HERE. The store
                // is content-addressed + idempotent: re-running after the throttle clears skips every
                // GID already fetched and resumes with the rest.
                if (IsSteamThrottle(ex))
                {
                    throttled = true;
                    Console.Error.WriteLine(
                        "content-backfill: STOPPING — Steam is rate-limiting authenticated logons "
                        + "(the fetch re-auths per GID). Wait for the throttle to clear (minutes to a "
                        + "few hours), then re-run the SAME command — completed GIDs are skipped and it "
                        + "resumes with the remainder.");
                    break;
                }
            }
        }

        int remaining = targets.Count - fetched - skipped;
        Console.Error.WriteLine(
            $"content-backfill: {(throttled ? "STOPPED (Steam throttle)" : "done")} — fetched={fetched} "
            + $"failed={failed} skipped={skipped} this run, {transferredBytes:N0} byte(s) transferred "
            + $"in total; {remaining} GID(s) still missing the '{pak.BaseRelDir}' pak (of "
            + $"{targets.Count}). Re-run to resume; then `extract` over the affected builds to re-emit "
            + $"the '{pak.BaseRelDir}' content artifacts.");
        return (failed > 0 || throttled) ? 1 : 0;
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

    /// <summary>
    /// True when <paramref name="ex"/> indicates Steam is throttling authenticated logons — the
    /// signal to stop and resume later rather than keep hammering (which prolongs the throttle).
    /// </summary>
    private static bool IsSteamThrottle(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("RateLimitExceeded", StringComparison.OrdinalIgnoreCase)
            || m.Contains("AccountLoginDeniedThrottle", StringComparison.OrdinalIgnoreCase)
            || m.Contains("must be connected", StringComparison.OrdinalIgnoreCase);
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
  --delay-seconds N      Pause N seconds between GIDs to avoid Steam logon throttling (default 0).
  --steam-guard <code>   Steam Guard code, if credentialed auth is required for historical manifests.

The store is content-addressed + idempotent: the fetch is deduped per content GID, completed GIDs are
skipped, and the run STOPS cleanly if Steam throttles logons — just re-run to resume. After the fetch,
re-run `extract` over the affected builds so their content artifacts are re-emitted.");
    }
}
