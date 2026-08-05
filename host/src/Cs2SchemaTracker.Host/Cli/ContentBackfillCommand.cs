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
// DRY-RUN by default (prints the plan, contacts no Steam). `--execute` performs the fetch. It does NOT
// re-extract gameevents.json — that is a subsequent `extract` pass over the affected builds (the
// core.gameevents events flow in once the store carries the core pak).

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

        // Today the only newly-tracked pak is the engine core pak; --pak is reserved for future paks.
        var pak = ContentPak.Core;

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
                + "successful fetch, re-run `extract` over the affected builds so gameevents.json picks "
                + "up the core.gameevents events.");
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
                await acquirer.AcquireContentPakAsync(
                    spec.AppId, ContentStore.ContentDepotId, buildId: 0, t.RepresentativeTupleDir,
                    minimalGameEvents: true, explicitSpec: spec, dirOnly: false,
                    CancellationToken.None).ConfigureAwait(false);
                fetched++;
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
            + $"failed={failed} skipped={skipped} this run; {remaining} GID(s) still missing the "
            + $"'{pak.BaseRelDir}' pak (of {targets.Count}). Re-run to resume; then `extract` over the "
            + "affected builds to fold core.gameevents into gameevents.json.");
        return (failed > 0 || throttled) ? 1 : 0;
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
@"cs2-schema-tracker content-backfill — fetch newly-tracked content paks for committed builds.

Fetches the engine core pak (resource/core.gameevents) for every committed content GID whose store
copy predates core-pak tracking, keyed + deduped by the 2347770 manifest GID (fetched ONCE per GID).

Usage: cs2-schema-tracker content-backfill [--binaries-root <dir>] [--execute] [--limit N]
                                           [--delay-seconds N] [--steam-guard <code>]

  --binaries-root <dir>  Store root (default: CS2_BINARIES_ROOT).
  --execute              Perform the Steam fetch. Omit for a DRY-RUN plan (no Steam contact).
  --limit N              Fetch at most N content GIDs this run (controlled rollout).
  --delay-seconds N      Pause N seconds between GIDs to avoid Steam logon throttling (default 0).
  --steam-guard <code>   Steam Guard code, if credentialed auth is required for historical manifests.

The store is content-addressed + idempotent: the fetch is deduped per content GID, completed GIDs are
skipped, and the run STOPS cleanly if Steam throttles logons — just re-run to resume. After the fetch,
re-run `extract` over the affected builds so gameevents.json folds in the core.gameevents events.");
    }
}
