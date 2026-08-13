// Steam acquisition.
//
// CLI surface (README.md — `acquire --build <N> --platform <P>`):
//   - --build <id|latest>   (required UNLESS --from-manifest supplies it)
//   - --platform <name>     (required: windows-x86_64 / linux-x86_64. CS2 is one app 730; the
//                           per-OS binary depot ships both client+server.)
//   - --out   <dir>         (optional; default <CS2_BINARIES_ROOT or appsettings BinariesRoot>/
//                           <build_id>/<platform>/ — the same store `extract` reads — else
//                           cache/binaries/<build_id>/<platform>/. Explicit --out wins.)
//   - --from-manifest <f>   (optional) explicit-manifest acquisition: reads a small JSON spec
//                           ({build_id, app_id, depots:[{depot_id, manifest_id}]}) and fetches
//                           THOSE exact per-depot manifest GIDs, bypassing PICS-current. This is
//                           how a specific prior build is re-fetched (anonymous PICS only exposes
//                           the current manifest).
//
// Two modes, mutually exclusive:
//   PICS-current:   acquire --build <id|latest> --platform <P> [--out <dir>]
//   explicit:       acquire --from-manifest <spec.json> --platform <P> [--out <dir>]
//                   (--build is ignored if also present; the spec carries it.)
//
// MINIMAL-FOOTPRINT BY DEFAULT: the binary-depot leg (both modes above) fetches ONLY the
// loadable native binaries the walker loads (BinaryBinSelector — the per-OS bin-directory
// subtrees), not the full per-OS binary depot. Valve ships several extra GB in that depot the
// walker never touches (shader VPKs, and — as of build ~24442510 — default-installed
// community/workshop map addons under game/csgo_community_addons/ + a duplicate
// game/csgo_core/ shader tree). The co-located content leg is independently minimal already
// (ContentPakSelector byte-range selection into the shared _content store). See
// --binaries-only in PrintHelp for the flag that additionally skips the content(+tools) leg.
//
// Exit codes:
//   0   success
//   64  EX_USAGE — argument parse / platform validation failed (no Steam contact)
//   65  EX_DATAERR — Steam reported the build/depot was unreachable / corrupt
//   1   any other acquisition failure (fail-loud)
//
// OS-agnostic: a Linux developer may acquire Windows binaries (they are content-only data here;
// OS-matching is only required for the walker subprocess later).

using System.Globalization;

using Cs2SchemaTracker.Host.Cache;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;

namespace Cs2SchemaTracker.Host.Cli;

internal static class AcquireCommand
{
    public static int Run(string[] args)
        => RunAsync(args, acquirerFactory: null).GetAwaiter().GetResult();

    /// <summary>
    /// Test seam: allows the test suite to inject a fake ISteamAcquirer so
    /// argument-handling can be exercised without standing up a Steam connection.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, Func<ISteamAcquirer>? acquirerFactory)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        // BATCH SELECTION (historical binary backfill): engaged by --all, or by two or more --build
        // values. A single --build with no --all stays on the single-(build, platform) path below.
        // Detected from the raw args — the CliArgs dictionary collapses repeated --build into one
        // key, so it cannot see a batch.
        int buildCount = args.Count(a => a is "--build");
        bool batchAll = args.Any(a => a is "--all");
        if (batchAll || buildCount > 1)
        {
            return await RunBatchAsync(args, acquirerFactory).ConfigureAwait(false);
        }

        var parsed = CliArgs.Parse(args);

        // --platform is required in BOTH modes (it names the output location /
        // depot even when manifest GIDs come from an explicit spec).
        if (!parsed.TryGetValue("platform", out var platform))
        {
            Console.Error.WriteLine("acquire: --platform <platform> is required. Run 'acquire --help'.");
            return 64;
        }
        if (!PlatformToDepots.IsKnown(platform))
        {
            Console.Error.WriteLine(
                $"acquire: unknown platform '{platform}'. Known: {string.Join(", ", PlatformToDepots.KnownPlatforms)}.");
            return 64;
        }

        var fromManifest = parsed.TryGetValue("from-manifest", out var fmArg) && !string.IsNullOrEmpty(fmArg)
            ? fmArg
            : null;
        var fromProvenance = parsed.TryGetValue("from-provenance", out var fpArg) && !string.IsNullOrEmpty(fpArg)
            ? fpArg
            : null;

        // --from-provenance and --from-manifest both "fetch a specific pinned set" and are mutually
        // exclusive (a request can't be pinned to two sources).
        if (fromProvenance is not null && fromManifest is not null)
        {
            Console.Error.WriteLine(
                "acquire: --from-provenance and --from-manifest are mutually exclusive " +
                "(both pin a specific set; choose one).");
            return 64;
        }

        // Cache-resolution overrides (default = cache-first -> Steam-fallback).
        bool cacheOnly = parsed.ContainsKey("cache-only");
        bool noCache = parsed.ContainsKey("no-cache");
        if (cacheOnly && noCache)
        {
            Console.Error.WriteLine(
                "acquire: --cache-only and --no-cache are mutually exclusive " +
                "(cache-only forbids Steam; no-cache forces Steam).");
            return 64;
        }

        // --tools: co-locate the Workshop Tools editor-DLL slice (depot 2347779) with the windows
        // binaries so the walker can register the editor modules' schema projects (see
        // CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md). The tools depot is WINDOWS-ONLY
        // (Valve publishes no Linux/mac Workshop Tools) and DLC-GATED (invisible to anonymous Steam
        // logons — GetDepotDecryptionKey has no free-license fallback for it, unlike PICS product
        // info). --no-tools opts out entirely; --tools is still accepted as an EXPLICIT, fail-loud
        // request (unchanged validation below). The narrowing/diagnostic modes have no tools leg, so
        // an explicit --tools combining with them is a usage error (exit 64), never a silently-
        // ignored flag.
        bool noTools = parsed.ContainsKey("no-tools");
        bool explicitTools = parsed.ContainsKey("tools");
        if (noTools && explicitTools)
        {
            Console.Error.WriteLine(
                "acquire: --tools and --no-tools are mutually exclusive.");
            return 64;
        }
        bool isWindowsPlatform = string.Equals(platform, "windows-x86_64", StringComparison.Ordinal);
        if (explicitTools)
        {
            if (!isWindowsPlatform)
            {
                Console.Error.WriteLine(
                    $"acquire: --tools is windows-x86_64 only (the Workshop Tools depot " +
                    $"{SteamAppIdMap.Cs2WorkshopToolsDepotId} ships windows editor DLLs; Valve publishes " +
                    $"no Linux/mac Workshop Tools). Got --platform {platform}.");
                return 2;
            }
            if (parsed.ContainsKey("binaries-only") || parsed.ContainsKey("content") ||
                parsed.ContainsKey("probe") || fromProvenance is not null)
            {
                Console.Error.WriteLine(
                    "acquire: --tools rides the default unified acquire (or a --from-manifest spec " +
                    "listing depot 2347779); it cannot combine with --binaries-only / --content / " +
                    "--probe / --from-provenance (those modes have no tools leg).");
                return 64;
            }
        }

        // DEFAULT (schema-coverage): the unified acquire (and the explicit --from-manifest path, when
        // its spec lists the tools depot) now attempts the tools leg on windows AUTOMATICALLY — no
        // --tools flag needed. It is BEST-EFFORT when default-implied (not explicitly requested): the
        // tools depot needs an authenticated Steam logon (DLC-gated), so an anonymous/no-credentials
        // session simply logs a note and continues rather than aborting an otherwise-clean acquire.
        // An EXPLICIT --tools request keeps today's fail-loud contract (the operator asked for it by
        // name; a failure must be surfaced). Scoped to the modes that ever had a tools leg (the
        // default unified acquire + --from-manifest) — --content/--binaries-only/--probe/
        // --from-provenance never acquire tools regardless of this default.
        bool modeHasToolsLeg = fromProvenance is null && !parsed.ContainsKey("content") &&
            !parsed.ContainsKey("binaries-only") && !parsed.ContainsKey("probe");
        bool toolsWanted = modeHasToolsLeg && isWindowsPlatform && !noTools;

        // Auth-mode selection. Anonymous is the DEFAULT (current-build / forward-capture works
        // fully without an account). Authenticated mode engages when:
        //   - --auth is passed explicitly (fail loud if creds are missing), OR
        //   - the request is the EXPLICIT / HISTORICAL path (--from-manifest, or a specific prior
        //     build) AND credentials are present in the env (auto-select), because anonymous Steam
        //     only issues a manifest request code for the CURRENT manifest, OR
        //   - the tools leg WILL be attempted (toolsWanted, above) AND credentials are present —
        //     the DLC-gated tools depot needs auth regardless of whether the requested build is
        //     current or historical. This only ever ENGAGES auth when creds already exist in the
        //     environment; it never requires an operator to set them up (anonymous-only machines are
        //     unaffected — toolsWanted's best-effort leg below simply logs a skip for them).
        // The test seam (acquirerFactory) bypasses all of this.
        ISteamAcquirer acquirer;
        if (acquirerFactory is not null)
        {
            acquirer = acquirerFactory();
        }
        else
        {
            bool explicitAuth = parsed.ContainsKey("auth");
            // The historical/explicit path needs authentication: anonymous Steam only issues a
            // manifest request code for the CURRENT manifest. True for --from-manifest and for any
            // --content / PICS acquire naming a SPECIFIC prior build (not 'latest').
            bool buildIsLatest = parsed.TryGetValue("build", out var authBuildArg)
                && string.Equals(authBuildArg, "latest", StringComparison.OrdinalIgnoreCase);
            bool specificBuildRequested = parsed.ContainsKey("build") && !buildIsLatest;
            bool historicalPath = fromManifest is not null || specificBuildRequested;
            var guardCode = parsed.TryGetValue("guard-code", out var gc) && !string.IsNullOrEmpty(gc) ? gc : null;
            try
            {
                acquirer = BuildRealAcquirer(explicitAuth, historicalPath || toolsWanted, guardCode);
            }
            catch (CredentialsMissingException ex)
            {
                Console.Error.WriteLine($"acquire: {ex.Message}");
                return 64;
            }
        }

        // --------------------------------------------------------------------
        // FROM-PROVENANCE MODE (--from-provenance): re-acquire the exact inputs a committed
        // provenance.json pins (steam.depots[].manifest_id), then SHA-256-verify every acquired
        // file against inputs[].sha256. This is the ONLY acquire mode that hash-verifies (a bare
        // --build/--platform acquire has no expected hashes). Cache-first by default; --cache-only
        // / --no-cache override.
        // --------------------------------------------------------------------
        if (fromProvenance is not null)
        {
            return await RunFromProvenanceAsync(
                acquirer, parsed, platform, fromProvenance, cacheOnly, noCache).ConfigureAwait(false);
        }

        // --------------------------------------------------------------------
        // CONTENT MODE (--content): minimal-footprint CONTENT-depot acquire for gameevents. The
        // CS2 content depot (2347770) is ~59 GB; by default we fetch only the pak01 VPK files
        // backing the `.gameevents` resources (two-phase). --full-pak admits the whole pak01_*.vpk.
        // --------------------------------------------------------------------
        if (parsed.ContainsKey("content"))
        {
            return await RunContentAsync(acquirer, parsed, platform, fromManifest).ConfigureAwait(false);
        }

        // --------------------------------------------------------------------
        // BINARIES-ONLY MODE (--binaries-only): loadable-binaries-only binary-depot acquire for
        // the historical backfill. The per-OS binary depot is ~7.9 GB/build but only ~0.46 GB is
        // the native binaries the walker loads; this fetches ONLY the per-OS bin-directory
        // subtrees. Works in both sourcing modes (PICS-current --build, or explicit
        // --from-manifest GIDs for a historical build). Historical builds need --auth.
        // --------------------------------------------------------------------
        if (parsed.ContainsKey("binaries-only"))
        {
            return await RunBinariesOnlyAsync(acquirer, parsed, platform, fromManifest).ConfigureAwait(false);
        }

        // --------------------------------------------------------------------
        // PROBE MODE (--probe): cheap manifest-level feasibility check. Resolves current PICS and
        // tries the recorded build's explicit historical manifest. NO bulk download. With
        // --probe-chunk it also pulls ONE sample chunk per depot to confirm CDN residency. Exit 0
        // iff the historical manifest is fetchable for every depot.
        // --------------------------------------------------------------------
        if (parsed.ContainsKey("probe"))
        {
            return await RunProbeAsync(acquirer, parsed, platform).ConfigureAwait(false);
        }

        // --------------------------------------------------------------------
        // EXPLICIT-MANIFEST MODE (--from-manifest): fetch specific per-depot GIDs.
        // --------------------------------------------------------------------
        if (fromManifest is not null)
        {
            ManifestSpec spec;
            try
            {
                spec = ManifestSpec.ParseFile(fromManifest);
            }
            catch (InvalidDataException ex)
            {
                // Spec malformed / missing / unreadable — a usage error surfaced before any Steam
                // contact.
                Console.Error.WriteLine($"acquire: --from-manifest spec invalid: {ex.Message}");
                return 64;
            }

            // EXPLICIT path keys the output dir off the spec's build_id (which must be the real
            // app-730 build_id — a prior build is re-fetched by its GIDs here).
            var explicitOut = parsed.TryGetValue("out", out var explicitModeOutArg) && !string.IsNullOrEmpty(explicitModeOutArg)
                ? explicitModeOutArg
                : DefaultOutDir(spec.BuildId.ToString(CultureInfo.InvariantCulture), platform);
            explicitOut = Path.GetFullPath(explicitOut);

            // Tools-leg validation BEFORE any Steam contact. The Workshop Tools depot (2347779) is
            // windows-only, so a spec carrying it under a non-windows --platform is a hard error
            // (exit 2) — the editor DLLs must never merge into a non-windows dir. And --tools with
            // a spec that LACKS the depot is fail-loud too — the operator asked for a leg the spec
            // cannot pin (falling back to PICS-current would fetch the WRONG build).
            uint explicitToolsDepot = SteamAppIdMap.Cs2WorkshopToolsDepotId;
            bool specHasTools = spec.Depots.Any(d => d.DepotId == explicitToolsDepot);
            if (explicitTools && !specHasTools)
            {
                Console.Error.WriteLine(
                    $"acquire: --tools was requested but the --from-manifest spec for build {spec.BuildId} " +
                    $"does not list the tools depot {explicitToolsDepot}; refusing to fetch. Spec depots: " +
                    $"[{string.Join(",", spec.OrderedDepotIds)}].");
                return 64;
            }
            if (specHasTools && !string.Equals(platform, "windows-x86_64", StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"acquire: the --from-manifest spec lists the Workshop Tools depot {explicitToolsDepot}, " +
                    $"which is windows-x86_64 only; got --platform {platform}.");
                return 2;
            }

            try
            {
                Console.Error.WriteLine(
                    $"acquire: starting EXPLICIT build={spec.BuildId} platform={platform} appId={spec.AppId} " +
                    $"depots=[{string.Join(",", spec.OrderedDepots.Select(d => $"{d.DepotId}:{d.ManifestId}"))}] " +
                    $"outDir='{explicitOut}'");
                // MINIMAL-FOOTPRINT BINARY LEG: route through the same loadable-binaries-only filter
                // (BinaryBinSelector) as --binaries-only, instead of the unfiltered full-depot
                // AcquireExplicitAsync. Valve's per-OS binary depot ships several GB of shader VPKs /
                // community-addon maps the walker never touches (game/bin/<os>/ + game/csgo/bin/<os>/
                // are the only subtrees it loads) — the unfiltered leg is still available on the
                // acquirer for diagnostic/integration use, just no longer the CLI default.
                //
                // The binary leg gets a BINARY-ONLY sub-spec: the content/tools depots have their own
                // legs below, and the binaries filter matches zero files in them — passing the full
                // spec made the acquirer's zero-match guard hard-fail every content-bearing spec
                // before the content leg could run (observed on the PR #9 re-dumps).
                uint explicitContentDepot = SteamAppIdMap.Cs2SharedContentDepotId;
                var explicitBinaryDepots = spec.Depots
                    .Where(d => d.DepotId != explicitContentDepot && d.DepotId != explicitToolsDepot)
                    .ToList();
                if (explicitBinaryDepots.Count > 0)
                {
                    var binarySpec = new ManifestSpec(spec.AppId, spec.BuildId, explicitBinaryDepots);
                    var explicitResult = await acquirer.AcquireBinariesOnlyAsync(
                        binarySpec.AppId, binarySpec.OrderedDepotIds, binarySpec.BuildId, explicitOut, platform,
                        explicitSpec: binarySpec, CancellationToken.None).ConfigureAwait(false);
                    PrintSummary(explicitResult);
                }
                else
                {
                    // Content/tools-only spec: nothing for the binary leg. NOTE the binary leg is
                    // also what wipes-and-replaces outDir, so a skipped leg merges the later legs
                    // into whatever the dir already holds — same contract as `--content` standalone.
                    Console.Error.WriteLine(
                        $"acquire: --from-manifest spec for build {spec.BuildId} lists no binary depot — " +
                        "skipping the binaries leg (content/tools-only spec).");
                }

                // UNIFIED ACQUIRE: if the explicit spec lists the content depot, co-locate the
                // selective content pak in the SAME outDir so one extract emits every content
                // artifact. The spec's content GID drives the historical content fetch (explicitSpec
                // threaded through, so the prior build's pak01 is fetched, not PICS-current). A spec
                // without the content depot is binaries-only by construction — nothing to fetch. A
                // content failure aborts via the outer catch, never a silent partial set.
                if (spec.Depots.Any(d => d.DepotId == explicitContentDepot))
                {
                    Console.Error.WriteLine(
                        $"acquire: UNIFIED content leg — explicit spec lists content depot {explicitContentDepot}; " +
                        $"fetching selective content pak into '{explicitOut}'.");
                    var explicitContent = await acquirer.AcquireContentPakAsync(
                        spec.AppId, explicitContentDepot, buildId: 0, explicitOut, minimalGameEvents: true,
                        explicitSpec: spec, dirOnly: false, CancellationToken.None).ConfigureAwait(false);
                    PrintSummary(explicitContent);
                }

                // UNIFIED tools leg (explicit): if the spec lists the Workshop Tools depot,
                // co-locate its editor-DLL slice in the SAME outDir — the spec's 2347779 GID drives
                // the historical fetch (explicitSpec threaded through, so the prior build's tools
                // manifest is fetched, not PICS-current). Validated windows-only above, before any
                // Steam contact. A tools failure aborts via the outer catch, never a silent
                // partial set.
                if (specHasTools && !noTools)
                {
                    Console.Error.WriteLine(
                        $"acquire: UNIFIED tools leg — explicit spec lists tools depot {explicitToolsDepot}; " +
                        $"fetching the editor-DLL slice into '{explicitOut}'.");
                    var explicitToolsResult = await acquirer.AcquireToolsAsync(
                        spec.AppId, explicitToolsDepot, buildId: 0, explicitOut,
                        explicitSpec: spec, CancellationToken.None).ConfigureAwait(false);
                    PrintSummary(explicitToolsResult);
                }
                return 0;
            }
            catch (SteamGuardRequiredException ex)
            {
                PrintGuardSeedInstructions(ex);
                return 77;
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine($"acquire: cancelled before completion: {ex.Message}");
                return 1;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"acquire: data verification failure: {ex.Message}");
                return 65;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"acquire: Steam acquisition failed: {ex.Message}");
                return 65;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"acquire: unexpected failure: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // --------------------------------------------------------------------
        // PICS-CURRENT MODE (default, unchanged): --build resolves the current
        // public-branch manifest.
        // --------------------------------------------------------------------
        if (!parsed.TryGetValue("build", out var buildArg))
        {
            Console.Error.WriteLine(
                "acquire: --build <id> is required (or use --from-manifest <file> for explicit GIDs). Run 'acquire --help'.");
            return 64;
        }

        uint buildId;
        if (string.Equals(buildArg, "latest", StringComparison.OrdinalIgnoreCase))
        {
            buildId = 0;
        }
        else if (!uint.TryParse(buildArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out buildId))
        {
            Console.Error.WriteLine($"acquire: --build must be 'latest' or a non-negative integer (got '{buildArg}').");
            return 64;
        }

        var plan = PlatformToDepots.Resolve(platform);

        var explicitOutArg = parsed.TryGetValue("out", out var outArg) && !string.IsNullOrEmpty(outArg)
            ? outArg
            : null;

        try
        {
            // Cache-resolution. The local binary cache IS the build/platform-keyed output dir
            // (CS2_BINARIES_ROOT-rooted or the cache/binaries/<build>/<platform> convention). When
            // the cache dir can be named WITHOUT a Steam probe (an explicit --out or a concrete
            // --build), apply cache-first / --cache-only first. 'latest' with a default --out needs
            // a PICS probe to learn the build_id, so its cache dir isn't knowable Steam-free;
            // --cache-only therefore rejects it.
            string? steamFreeOutDir = explicitOutArg is not null
                ? Path.GetFullPath(explicitOutArg)
                : (buildId != 0 ? Path.GetFullPath(DefaultOutDir(buildArg, platform)) : null);

            if (cacheOnly)
            {
                if (steamFreeOutDir is null)
                {
                    Console.Error.WriteLine(
                        "acquire --cache-only: cannot resolve 'latest' from the cache without a Steam probe. " +
                        "Pass a concrete --build <id> or an explicit --out <dir>.");
                    return 64;
                }
                if (CacheDirPopulated(steamFreeOutDir))
                {
                    // --tools under --cache-only: the tools slice must ALREADY be in the cache
                    // (record-authoritative — depot 2347779 in manifest-record.json); acquiring the
                    // missing leg would need Steam, which --cache-only forbids. An EXPLICIT --tools
                    // request fails loud (never a silent tools-less "success"); the DEFAULT-implied
                    // want (toolsWanted, no explicit flag) is best-effort — --cache-only cannot fetch
                    // anything regardless, so a cache predating tools tracking is still a clean hit.
                    bool cachedHasTools = RecordListsToolsDepot(steamFreeOutDir);
                    if (toolsWanted && !cachedHasTools && explicitTools)
                    {
                        Console.Error.WriteLine(
                            $"acquire --cache-only: cache '{steamFreeOutDir}' holds the binaries but its " +
                            $"manifest-record.json lists NO tools depot {SteamAppIdMap.Cs2WorkshopToolsDepotId}; " +
                            "fetching the missing tools slice needs Steam. Drop --cache-only.");
                        return 65;
                    }
                    Console.Error.WriteLine(
                        $"acquire --cache-only: resolved build={buildArg} platform={platform} from local cache '{steamFreeOutDir}'" +
                        (toolsWanted && cachedHasTools ? " (incl. tools — manifest-record lists depot 2347779)."
                            : toolsWanted ? " (tools not yet in cache; --cache-only cannot fetch it — non-fatal)."
                            : "."));
                    return 0;
                }
                Console.Error.WriteLine(
                    $"acquire --cache-only: required binaries are NOT in the local cache '{steamFreeOutDir}'. " +
                    "No Steam access permitted. Drop --cache-only to download.");
                return 65;
            }

            // Cache-first (default): use the local cache when present (unless --no-cache forces
            // a fresh Steam download + cache refresh).
            //
            // TOOLS RETROFIT: the HIT is tools-aware. With --tools, a populated cache dir whose
            // manifest-record.json lacks the tools depot (2347779) must NOT short-circuit — that is
            // exactly the backfill-over-an-already-populated-cache case, and --no-cache would
            // wastefully re-download the binaries. Instead, acquire ONLY the missing tools leg into
            // the same dir (binaries/content untouched). The record is the AUTHORITATIVE check (a
            // dir/file-presence heuristic could not distinguish base DLLs from tools DLLs — they
            // share the same bin subtrees).
            if (!noCache && steamFreeOutDir is not null && CacheDirPopulated(steamFreeOutDir))
            {
                bool cachedHasTools = RecordListsToolsDepot(steamFreeOutDir);
                if (toolsWanted && !cachedHasTools)
                {
                    Console.Error.WriteLine(
                        $"acquire: cache-first HIT — build={buildArg} platform={platform} already in local cache " +
                        $"'{steamFreeOutDir}', but its manifest-record.json lists NO tools depot " +
                        $"{SteamAppIdMap.Cs2WorkshopToolsDepotId}; tools leg missing -> acquiring tools only " +
                        "(binaries/content untouched).");
                    // EXPLICIT --tools retrofits fail-loud (unchanged); a DEFAULT-implied want is
                    // best-effort — the cache hit itself still succeeds (return 0) if the retrofit
                    // can't complete anonymously.
                    if (explicitTools)
                    {
                        return await RunToolsRetrofitAsync(
                            acquirer, parsed, buildId, steamFreeOutDir).ConfigureAwait(false);
                    }
                    await TryRunToolsRetrofitAsync(
                        acquirer, parsed, buildId, steamFreeOutDir).ConfigureAwait(false);
                    return 0;
                }
                Console.Error.WriteLine(
                    $"acquire: cache-first HIT — build={buildArg} platform={platform} already in local cache '{steamFreeOutDir}'" +
                    (toolsWanted && cachedHasTools ? " (incl. tools — manifest-record lists depot 2347779)" : "") +
                    ". Pass --no-cache to force a fresh Steam download.");
                return 0;
            }

            // Key the DEFAULT output dir off the RESOLVED app-730 build_id, never
            // the literal "latest". When --build latest (buildId == 0) and no
            // explicit --out is given, resolve the current public-branch build_id
            // via a cheap PICS probe first so the on-disk path is the real build.
            // An explicit --out always wins verbatim (no probe needed).
            string pathBuildId = buildArg;
            if (explicitOutArg is null && buildId == 0)
            {
                var pics = await acquirer.ProbeCurrentPicsAsync(
                    plan.AppId, plan.DepotIds, CancellationToken.None).ConfigureAwait(false);
                pathBuildId = pics.CurrentBuildId.ToString(CultureInfo.InvariantCulture);
                Console.Error.WriteLine(
                    $"acquire: resolved 'latest' -> build_id {pathBuildId} (app {plan.AppId}); " +
                    $"output keyed off the resolved build_id.");
            }

            var outDir = Path.GetFullPath(explicitOutArg ?? DefaultOutDir(pathBuildId, platform));

            // Cache-first for the 'latest'-resolved path: now that the probe gave us the real
            // build_id (and thus the cache dir), honor a cache hit unless --no-cache. Same TOOLS
            // RETROFIT rule as above; this branch is only reachable for 'latest' (buildId == 0),
            // so the missing tools leg resolves PICS-current — the cached build IS the current one.
            if (!noCache && steamFreeOutDir is null && CacheDirPopulated(outDir))
            {
                bool cachedHasTools = RecordListsToolsDepot(outDir);
                if (toolsWanted && !cachedHasTools)
                {
                    Console.Error.WriteLine(
                        $"acquire: cache-first HIT — resolved build already in local cache '{outDir}', but its " +
                        $"manifest-record.json lists NO tools depot {SteamAppIdMap.Cs2WorkshopToolsDepotId}; " +
                        "tools leg missing -> acquiring tools only (binaries/content untouched).");
                    if (explicitTools)
                    {
                        await AcquireUnifiedToolsAsync(acquirer, outDir, CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        await TryAcquireUnifiedToolsAsync(acquirer, outDir, CancellationToken.None).ConfigureAwait(false);
                    }
                    return 0;
                }
                Console.Error.WriteLine(
                    $"acquire: cache-first HIT — resolved build already in local cache '{outDir}'" +
                    (toolsWanted && cachedHasTools ? " (incl. tools — manifest-record lists depot 2347779)" : "") +
                    ". Pass --no-cache to force a fresh Steam download.");
                return 0;
            }

            Console.Error.WriteLine(
                $"acquire: starting build={buildArg} platform={platform} appId={plan.AppId} " +
                $"depots=[{string.Join(",", plan.OrderedDepotIds)}] outDir='{outDir}'" +
                (noCache ? " (--no-cache: fresh download, cache refreshed)" : ""));
            // MINIMAL-FOOTPRINT BINARY LEG (default): route through the same loadable-binaries-only
            // filter (BinaryBinSelector) already used by --binaries-only, instead of the unfiltered
            // full-depot AcquireAsync. The per-OS binary depot ships several GB of shader VPKs and
            // (as of build ~24442510) default-installed community/workshop map addons under
            // game/csgo_community_addons/ + a duplicate game/csgo_core/ shader tree — none of which
            // the walker ever loads (it only dlopens game/bin/<os>/ + game/csgo/bin/<os>/). Filtering
            // by default turns a several-GB acquire back into the ~0.46 GB the walker actually needs;
            // the unfiltered full-depot leg remains on the acquirer for diagnostic/integration use.
            var result = await acquirer.AcquireBinariesOnlyAsync(
                plan.AppId, plan.DepotIds, buildId, outDir, platform,
                explicitSpec: null, CancellationToken.None).ConfigureAwait(false);
            PrintSummary(result);

            // PICS APPINFO CAPTURE (forward PICS-current binary acquire). PICS appinfo for app 730
            // is CURRENT-ONLY: capture it once here and preserve the rendered canonical body next to
            // manifest-record.json as pics-appinfo-capture.json. extract --commit later folds it into
            // the committed build-level pics-appinfo.json. Best-effort + non-fatal: a PICS hiccup
            // must NOT fail an otherwise-clean binary acquire (the artifact is optional). Only does
            // real work for the concrete anonymous acquirer (the only one exposing DumpAppInfoAsync).
            //
            // Runs for the whole forward PICS-current branch — whether requested as 'latest'
            // (buildId == 0) or as the concrete current build_id by number. Safe because anonymous
            // Steam can only resolve+fetch the CURRENT manifest: AcquireAsync above already
            // succeeded, so the requested build IS the current one. (Gating to buildId == 0 produced
            // a binaries-only dir for `acquire --build <current-id>` — the form extract's
            // auto-acquire passes — which would then emit a content-less set.)
            await TryCapturePicsAppInfoAsync(acquirer, plan.AppId, pathBuildId, outDir).ConfigureAwait(false);

            // UNIFIED ACQUIRE: co-locate the selective content pak in the SAME outDir as the
            // binaries so a single `extract` emits every content artifact — no separate `--content`
            // pass, no post-hoc injection. AcquireUnifiedContentAsync reuses the resolved outDir and
            // fetches the PICS-current content manifest (buildId: 0) WITHOUT re-probing PICS, so the
            // single-probe contract the 'latest' tests assert holds for both forms. A content
            // failure makes the set incomplete and aborts the acquire (the verified binaries remain
            // on disk for a re-run). Use --binaries-only to opt out of the content leg.
            await AcquireUnifiedContentAsync(acquirer, outDir, CancellationToken.None).ConfigureAwait(false);

            // UNIFIED tools leg (DEFAULT on windows; --no-tools opts out): co-locate the Workshop
            // Tools editor-DLL slice in the SAME outDir. PICS-current resolution (buildId 0) mirrors
            // the content leg — the outDir is already resolved, so no re-probe. An EXPLICIT --tools
            // failure aborts via the outer catch (unchanged); a DEFAULT-implied want is best-effort —
            // it needs an authenticated (DLC-gated) Steam session, so an anonymous/no-credentials run
            // logs a note and the acquire still succeeds.
            if (toolsWanted)
            {
                if (explicitTools)
                {
                    await AcquireUnifiedToolsAsync(acquirer, outDir, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await TryAcquireUnifiedToolsAsync(acquirer, outDir, CancellationToken.None).ConfigureAwait(false);
                }
            }
            return 0;
        }
        catch (SteamGuardRequiredException ex)
        {
            PrintGuardSeedInstructions(ex);
            return 77;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine($"acquire: cancelled before completion: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            // Hash mismatch / manifest-shape failure.
            Console.Error.WriteLine($"acquire: data verification failure: {ex.Message}");
            return 65;
        }
        catch (InvalidOperationException ex)
        {
            // Steam-side issue (no PICS data, depot key denied permanently, ...).
            Console.Error.WriteLine($"acquire: Steam acquisition failed: {ex.Message}");
            return 65;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"acquire: unexpected failure: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// PROBE mode: drive the cheap manifest-level feasibility check. The recorded build comes
    /// either from our seeded history (--build &lt;id&gt;, matched in KnownManifestHistory) or from
    /// an explicit spec file (--from-manifest). The process exits non-zero when the historical
    /// manifest is NOT fetchable for every depot.
    /// </summary>
    private static async Task<int> RunProbeAsync(
        ISteamAcquirer acquirer, Dictionary<string, string> parsed, string platform)
    {
        // CURRENT-BUILD PROBE (--probe with --build latest, or no --build and no --from-manifest):
        // resolve the public branch's CURRENT build_id + per-depot manifest GIDs via PICS.
        // Manifest-level only — fetches no content. Answers "what build is the public branch on
        // right now, and does its build_id resolve?" without a multi-GB pull. (The seeded-history /
        // explicit-spec branches below cover historical builds, which anonymous PICS can't resolve.)
        bool hasFromManifest = parsed.TryGetValue("from-manifest", out var fmArgCurrent) && !string.IsNullOrEmpty(fmArgCurrent);
        bool buildIsLatest = !parsed.TryGetValue("build", out var probeBuildArg)
            || string.Equals(probeBuildArg, "latest", StringComparison.OrdinalIgnoreCase);
        if (!hasFromManifest && buildIsLatest)
        {
            var currentPlan = PlatformToDepots.Resolve(platform);
            try
            {
                var pics = await acquirer.ProbeCurrentPicsAsync(
                    currentPlan.AppId, currentPlan.DepotIds, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine(
                    $"acquire --probe (current): platform={platform} app={pics.AppId} build_id={pics.CurrentBuildId}");
                foreach (var d in pics.Depots)
                {
                    Console.WriteLine($"  depot {d.DepotId} current manifest {d.ManifestId}");
                }
                // build_id == 0 means PICS returned no public-branch buildid — a resolution
                // failure, surfaced as non-zero.
                if (pics.CurrentBuildId == 0)
                {
                    Console.Error.WriteLine(
                        "acquire --probe (current): PICS returned build_id 0 — current build_id did NOT resolve.");
                    return 65;
                }
                return 0;
            }
            catch (SteamGuardRequiredException ex)
            {
                PrintGuardSeedInstructions(ex);
                return 77;
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine($"acquire --probe (current): cancelled: {ex.Message}");
                return 1;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"acquire --probe (current): Steam probe failed: {ex.Message}");
                return 65;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"acquire --probe (current): unexpected failure: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // Source the recorded build to probe: explicit spec file wins; else look
        // up our seeded history by --build id (+ the platform's app id).
        ManifestRecord record;
        if (parsed.TryGetValue("from-manifest", out var fmArg) && !string.IsNullOrEmpty(fmArg))
        {
            ManifestSpec spec;
            try
            {
                spec = ManifestSpec.ParseFile(fmArg);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"acquire --probe: --from-manifest spec invalid: {ex.Message}");
                return 64;
            }
            // Build a record from the spec (manifest_created_utc unknown until fetched).
            record = new ManifestRecord(
                spec.AppId, spec.BuildId,
                spec.OrderedDepots
                    .Select(d => new ManifestRecordDepot(d.DepotId, d.ManifestId, "1970-01-01T00:00:00Z"))
                    .ToList());
        }
        else
        {
            if (!parsed.TryGetValue("build", out var buildArg) ||
                !uint.TryParse(buildArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var buildId))
            {
                Console.Error.WriteLine(
                    "acquire --probe: provide --build <id> (matched against seeded history) or --from-manifest <file>.");
                return 64;
            }
            var plan = PlatformToDepots.Resolve(platform);
            var seeded = KnownManifestHistory.TryGet(buildId, plan.AppId);
            if (seeded is null)
            {
                Console.Error.WriteLine(
                    $"acquire --probe: build {buildId} (app {plan.AppId}) is not in our seeded manifest history. " +
                    $"Supply --from-manifest <file> with its per-depot GIDs.");
                return 64;
            }
            record = seeded;
        }

        bool probeChunk = parsed.ContainsKey("probe-chunk");

        try
        {
            var report = await ManifestProbeRunner.RunAsync(
                acquirer, record, probeChunk, CancellationToken.None)
                .ConfigureAwait(false);
            Console.WriteLine(report.ToHumanReport());
            // A NO verdict is a non-zero exit.
            return report.HistoricalManifestFetchable ? 0 : 65;
        }
        catch (SteamGuardRequiredException ex)
        {
            PrintGuardSeedInstructions(ex);
            return 77;   // EX_NOPERM — Steam Guard seed required (operator action).
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine($"acquire --probe: cancelled: {ex.Message}");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"acquire --probe: Steam probe failed: {ex.Message}");
            return 65;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"acquire --probe: unexpected failure: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// CONTENT mode (--content): minimal-footprint CONTENT-depot acquire for gameevents. Default
    /// is the two-phase minimal pak01 fetch; --full-pak fetches the whole game/csgo/pak01_*.vpk
    /// set. The content depot is cross-platform (the pak01 VPK is identical for win/linux), but
    /// --platform still selects the on-disk output tree so `extract` finds the VPK relative to the
    /// per-platform binaries dir.
    /// </summary>
    private static async Task<int> RunContentAsync(
        ISteamAcquirer acquirer, Dictionary<string, string> parsed, string platform, string? fromManifest)
    {
        bool minimal = !parsed.ContainsKey("full-pak");
        // --dir-only fetches ONLY game/csgo/pak01_dir.vpk (Phase A) and stops — the cheap (~7 MB)
        // per-content-manifest index pull used to read .gameevents CRC32s for fileset dedup.
        // dir-only never fetches archive chunks, so --full-pak is moot with it (we note, not error).
        bool dirOnly = parsed.ContainsKey("dir-only");
        if (dirOnly && parsed.ContainsKey("full-pak"))
        {
            Console.Error.WriteLine(
                "acquire --content: --full-pak is ignored with --dir-only (dir-only fetches no archive chunks).");
        }
        // --print-gameevents-crc: after the dir-only fetch, parse pak01_dir.vpk and print
        // `path crc32` lines for every .gameevents entry to stdout (the dedup key). Only meaningful
        // with --dir-only (the dir file carries the CRCs); harmless otherwise.
        bool printGameEventsCrc = parsed.ContainsKey("print-gameevents-crc");
        var explicitOutArg = parsed.TryGetValue("out", out var outArg) && !string.IsNullOrEmpty(outArg)
            ? outArg
            : null;

        uint appId = SteamAppIdMap.Cs2AppId;
        uint contentDepot = SteamAppIdMap.Cs2SharedContentDepotId;

        // --------------------------------------------------------------------
        // HISTORICAL content path: the 2347770 GID comes either from an explicit --from-manifest
        // spec or from our recorded KnownManifestHistory for a specific --build id. Anonymous PICS
        // exposes only the CURRENT 2347770 manifest, so re-fetching a prior build's pak01 VPK
        // requires that build's recorded content GID (+ --auth). The resolved spec is threaded into
        // AcquireContentPakAsync, which fetches THAT manifest's pak01_dir.vpk + chunks and still
        // merges the 2347770 identity into manifest-record.json (gameevents.json ⟺ 2347770 ∈
        // provenance).
        // --------------------------------------------------------------------
        ManifestSpec? contentSpec = null;
        if (fromManifest is not null)
        {
            try
            {
                contentSpec = ManifestSpec.ParseFile(fromManifest);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"acquire --content: --from-manifest spec invalid: {ex.Message}");
                return 64;
            }
            if (!contentSpec.Depots.Any(d => d.DepotId == contentDepot))
            {
                Console.Error.WriteLine(
                    $"acquire --content: --from-manifest spec for build {contentSpec.BuildId} does not list the " +
                    $"content depot {contentDepot}; refusing to fetch. Spec depots: " +
                    $"[{string.Join(",", contentSpec.OrderedDepotIds)}].");
                return 64;
            }
        }

        // Parse --build. Required UNLESS --from-manifest is supplied (the spec then
        // carries the build identity). When given without a spec, it selects the
        // historical 2347770 GID from KnownManifestHistory (specific build) or
        // PICS-current ('latest').
        string buildArg;
        uint buildId;
        if (contentSpec is not null)
        {
            // Spec carries the build identity. --build, if present, is ignored
            // (mirrors --from-manifest semantics in the other modes).
            buildArg = contentSpec.BuildId.ToString(CultureInfo.InvariantCulture);
            buildId = 0;   // never used for PICS in the explicit path
        }
        else if (!parsed.TryGetValue("build", out buildArg!) || string.IsNullOrEmpty(buildArg))
        {
            Console.Error.WriteLine(
                "acquire --content: --build <id|latest> is required (or use --from-manifest <spec> for a historical build). Run 'acquire --help'.");
            return 64;
        }
        else if (string.Equals(buildArg, "latest", StringComparison.OrdinalIgnoreCase))
        {
            buildId = 0;
        }
        else if (!uint.TryParse(buildArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out buildId))
        {
            Console.Error.WriteLine($"acquire --content: --build must be 'latest' or a non-negative integer (got '{buildArg}').");
            return 64;
        }

        // A SPECIFIC (non-'latest') build with no explicit spec resolves the
        // historical 2347770 GID from our recorded history. 'latest' stays on the
        // PICS-current path (contentSpec == null, buildId == 0).
        if (contentSpec is null && buildId != 0)
        {
            var seeded = KnownManifestHistory.TryGet(buildId, appId);
            if (seeded is null)
            {
                Console.Error.WriteLine(
                    $"acquire --content: build {buildId} (app {appId}) is not in our recorded manifest history, " +
                    $"so its historical content depot {contentDepot} GID is unknown. Supply --from-manifest <spec> " +
                    $"(e.g. that build's committed manifest-record.json) carrying the {contentDepot} GID.");
                return 64;
            }
            if (!seeded.Depots.Any(d => d.DepotId == contentDepot))
            {
                Console.Error.WriteLine(
                    $"acquire --content: recorded history for build {buildId} has no content depot {contentDepot} GID " +
                    $"(only [{string.Join(",", seeded.Depots.Select(d => d.DepotId))}]); cannot backfill gameevents. " +
                    $"Supply --from-manifest <spec> with the {contentDepot} GID.");
                return 64;
            }
            contentSpec = seeded.ToManifestSpec();
        }

        try
        {
            // Path build id: the spec's build_id (historical) wins; else the literal
            // --build, resolving 'latest' via a cheap PICS probe for the on-disk path.
            string pathBuildId = contentSpec?.BuildId.ToString(CultureInfo.InvariantCulture) ?? buildArg;
            if (contentSpec is null && explicitOutArg is null && buildId == 0)
            {
                var pics = await acquirer.ProbeCurrentPicsAsync(
                    appId, new[] { contentDepot }, CancellationToken.None).ConfigureAwait(false);
                pathBuildId = pics.CurrentBuildId.ToString(CultureInfo.InvariantCulture);
                Console.Error.WriteLine(
                    $"acquire --content: resolved 'latest' -> build_id {pathBuildId} (app {appId}).");
            }

            var outDir = Path.GetFullPath(explicitOutArg ?? DefaultOutDir(pathBuildId, platform));

            var contentGid = contentSpec?.OrderedDepots.First(d => d.DepotId == contentDepot).ManifestId;
            string modeLabel = dirOnly
                ? "DIR-ONLY (pak01_dir.vpk index only)"
                : (minimal ? "MINIMAL (gameevents pak01)" : "FULL pak01 set");
            Console.Error.WriteLine(
                $"acquire --content: starting build={pathBuildId} platform={platform} appId={appId} " +
                $"contentDepot={contentDepot} mode={modeLabel} " +
                $"source={(contentSpec is null ? "PICS-current" : $"HISTORICAL manifest {contentGid}")} " +
                $"outDir='{outDir}'");

            var result = await acquirer.AcquireContentPakAsync(
                appId, contentDepot, buildId, outDir, minimal, contentSpec, dirOnly, CancellationToken.None).ConfigureAwait(false);
            PrintSummary(result);

            if (printGameEventsCrc)
            {
                PrintGameEventsCrcs(outDir);
            }
            return 0;
        }
        catch (SteamGuardRequiredException ex)
        {
            PrintGuardSeedInstructions(ex);
            return 77;
        }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine($"acquire --content: cancelled before completion: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"acquire --content: data verification failure: {ex.Message}");
            return 65;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"acquire --content: Steam acquisition failed: {ex.Message}");
            return 65;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"acquire --content: unexpected failure: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// UNIFIED ACQUIRE content leg: fetch the selective content pak into <paramref name="outDir"/>
    /// — the SAME directory the binaries were acquired into — so a single <c>extract</c> emits
    /// every content artifact (gameevents, item_definitions, game_modes, surface_properties,
    /// prop_data, map_overviews, localization) without a separate <c>--content</c> pass or any
    /// post-hoc injection.
    ///
    /// PICS-current resolution (buildId 0): the forward PICS-current capture path for the CURRENT
    /// build — reached whether requested as 'latest' or as the concrete current build_id by number.
    /// The CLI has already resolved <paramref name="outDir"/>, so this never re-probes PICS (keeping
    /// the single-probe contract the tests assert). <c>minimalGameEvents:true</c> keeps the
    /// footprint to the byte ranges <see cref="ContentPakSelector.SelectContentByteRanges"/> selects
    /// (NOT the ~59 GB depot). Any content failure propagates to the caller's exit-code mapping; the
    /// unified set is never silently partial.
    /// </summary>
    private static async Task AcquireUnifiedContentAsync(
        ISteamAcquirer acquirer, string outDir, CancellationToken ct)
    {
        uint appId = SteamAppIdMap.Cs2AppId;
        uint contentDepot = SteamAppIdMap.Cs2SharedContentDepotId;
        Console.Error.WriteLine(
            $"acquire: UNIFIED content leg — fetching selective content pak (depot {contentDepot}) into " +
            $"'{outDir}' so a single extract emits all content artifacts. (Use --binaries-only to skip content.)");
        var result = await acquirer.AcquireContentPakAsync(
            appId, contentDepot, buildId: 0, outDir, minimalGameEvents: true,
            explicitSpec: null, dirOnly: false, ct).ConfigureAwait(false);
        PrintSummary(result);
    }

    /// <summary>
    /// UNIFIED ACQUIRE tools leg (--tools, windows-x86_64 only): fetch the Workshop Tools
    /// editor-DLL slice (depot 2347779; ".dll" under "game/", ~200 MB of the ~2.09 GB depot) into
    /// <paramref name="outDir"/> — the SAME directory the binaries were acquired into — so the
    /// walker can load the editor modules (hammer.dll, toolframework2.dll, …) and register their
    /// schema projects alongside the base game's (see CS2OpenDev-Docs
    /// SCHEMA_COVERAGE_GAP_EVALUATION.md).
    ///
    /// PICS-current resolution (buildId 0): the forward capture path for the CURRENT build. The CLI
    /// has already resolved <paramref name="outDir"/>, so this never re-probes PICS (the
    /// single-probe contract holds). The DLLs are stage-then-MERGED into the binaries dir
    /// (non-destructive — a wipe would destroy the co-located base binaries), and the 2347779
    /// identity accumulates into manifest-record.json. Any tools failure propagates to the caller's
    /// exit-code mapping; the unified set is never silently partial.
    /// </summary>
    private static async Task AcquireUnifiedToolsAsync(
        ISteamAcquirer acquirer, string outDir, CancellationToken ct)
    {
        uint appId = SteamAppIdMap.Cs2AppId;
        uint toolsDepot = SteamAppIdMap.Cs2WorkshopToolsDepotId;
        Console.Error.WriteLine(
            $"acquire: UNIFIED tools leg — fetching the Workshop Tools editor-DLL slice (depot {toolsDepot}) " +
            $"into '{outDir}' so the walker can register the editor modules' schema projects.");
        var result = await acquirer.AcquireToolsAsync(
            appId, toolsDepot, buildId: 0, outDir, explicitSpec: null, ct).ConfigureAwait(false);
        PrintSummary(result);
    }

    /// <summary>
    /// BEST-EFFORT wrapper around <see cref="AcquireUnifiedToolsAsync"/> for the DEFAULT-implied tools
    /// leg (schema-coverage: tools now rides the unified acquire automatically on windows, no --tools
    /// flag needed). The tools depot is DLC-gated (invisible to anonymous Steam logons, no free-
    /// license fallback), so an anonymous/no-credentials run is a GUARANTEED failure here, not a
    /// transient one — swallow it, log a one-line note, and let the otherwise-clean binaries+content
    /// acquire still succeed. An EXPLICIT --tools request does NOT use this wrapper (see
    /// <see cref="AcquireUnifiedToolsAsync"/> directly) — it keeps the fail-loud contract, since the
    /// operator asked for it by name.
    /// </summary>
    private static async Task TryAcquireUnifiedToolsAsync(
        ISteamAcquirer acquirer, string outDir, CancellationToken ct)
    {
        try
        {
            await AcquireUnifiedToolsAsync(acquirer, outDir, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "acquire: tools leg skipped (non-fatal, default-implied — the Workshop Tools depot needs " +
                $"an authenticated Steam logon; set STEAM_USERNAME/STEAM_PASSWORD or pass --auth to include " +
                $"it): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// BINARIES-ONLY mode (--binaries-only): loadable-binaries-only binary-depot acquire for the
    /// historical backfill. Fetches ONLY the per-OS bin-directory subtrees (the ~0.46 GB the walker
    /// loads, not the ~7.9 GB full depot). Two sourcing modes:
    ///   - EXPLICIT (--from-manifest spec.json): per-depot GIDs verbatim — the historical path. The
    ///     spec carries build_id/app_id/depots; --build is ignored.
    ///   - PICS-current (--build &lt;id|latest&gt;): resolves the current public branch.
    /// A specific historical build requires authenticated logon (handled by the auth selection
    /// upstream); anonymous Steam only issues a request code for the CURRENT manifest.
    /// </summary>
    private static async Task<int> RunBinariesOnlyAsync(
        ISteamAcquirer acquirer, Dictionary<string, string> parsed, string platform, string? fromManifest)
    {
        var explicitOutArg = parsed.TryGetValue("out", out var outArg) && !string.IsNullOrEmpty(outArg)
            ? outArg
            : null;

        // ---- EXPLICIT (historical) sourcing: --from-manifest GIDs ----
        if (fromManifest is not null)
        {
            ManifestSpec spec;
            try
            {
                spec = ManifestSpec.ParseFile(fromManifest);
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"acquire --binaries-only: --from-manifest spec invalid: {ex.Message}");
                return 64;
            }

            var explicitOut = Path.GetFullPath(
                explicitOutArg ?? DefaultOutDir(spec.BuildId.ToString(CultureInfo.InvariantCulture), platform));

            try
            {
                Console.Error.WriteLine(
                    $"acquire --binaries-only: starting EXPLICIT build={spec.BuildId} platform={platform} " +
                    $"appId={spec.AppId} depots=[{string.Join(",", spec.OrderedDepots.Select(d => $"{d.DepotId}:{d.ManifestId}"))}] " +
                    $"outDir='{explicitOut}'");
                var result = await acquirer.AcquireBinariesOnlyAsync(
                    spec.AppId, Array.Empty<uint>(), spec.BuildId, explicitOut, platform,
                    explicitSpec: spec, CancellationToken.None).ConfigureAwait(false);
                PrintSummary(result);
                return 0;
            }
            catch (SteamGuardRequiredException ex) { PrintGuardSeedInstructions(ex); return 77; }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine($"acquire --binaries-only: cancelled before completion: {ex.Message}");
                return 1;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"acquire --binaries-only: data verification failure: {ex.Message}");
                return 65;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"acquire --binaries-only: Steam acquisition failed: {ex.Message}");
                return 65;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"acquire --binaries-only: unexpected failure: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // ---- PICS-current sourcing: --build <id|latest> ----
        if (!parsed.TryGetValue("build", out var buildArg) || string.IsNullOrEmpty(buildArg))
        {
            Console.Error.WriteLine(
                "acquire --binaries-only: --build <id|latest> is required (or use --from-manifest <file> for a historical build). Run 'acquire --help'.");
            return 64;
        }
        uint buildId;
        if (string.Equals(buildArg, "latest", StringComparison.OrdinalIgnoreCase))
        {
            buildId = 0;
        }
        else if (!uint.TryParse(buildArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out buildId))
        {
            Console.Error.WriteLine($"acquire --binaries-only: --build must be 'latest' or a non-negative integer (got '{buildArg}').");
            return 64;
        }

        var plan = PlatformToDepots.Resolve(platform);

        try
        {
            string pathBuildId = buildArg;
            if (explicitOutArg is null && buildId == 0)
            {
                var pics = await acquirer.ProbeCurrentPicsAsync(
                    plan.AppId, plan.DepotIds, CancellationToken.None).ConfigureAwait(false);
                pathBuildId = pics.CurrentBuildId.ToString(CultureInfo.InvariantCulture);
                Console.Error.WriteLine(
                    $"acquire --binaries-only: resolved 'latest' -> build_id {pathBuildId} (app {plan.AppId}).");
            }

            var outDir = Path.GetFullPath(explicitOutArg ?? DefaultOutDir(pathBuildId, platform));

            Console.Error.WriteLine(
                $"acquire --binaries-only: starting build={buildArg} platform={platform} appId={plan.AppId} " +
                $"depots=[{string.Join(",", plan.OrderedDepotIds)}] outDir='{outDir}'");
            var result = await acquirer.AcquireBinariesOnlyAsync(
                plan.AppId, plan.DepotIds, buildId, outDir, platform,
                explicitSpec: null, CancellationToken.None).ConfigureAwait(false);
            PrintSummary(result);
            return 0;
        }
        catch (SteamGuardRequiredException ex) { PrintGuardSeedInstructions(ex); return 77; }
        catch (OperationCanceledException ex)
        {
            Console.Error.WriteLine($"acquire --binaries-only: cancelled before completion: {ex.Message}");
            return 1;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"acquire --binaries-only: data verification failure: {ex.Message}");
            return 65;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"acquire --binaries-only: Steam acquisition failed: {ex.Message}");
            return 65;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"acquire --binaries-only: unexpected failure: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// FROM-PROVENANCE mode (--from-provenance): re-acquire the exact inputs a committed
    /// provenance.json pins (steam.depots[].manifest_id), then SHA-256-verify every acquired file
    /// against inputs[].sha256. The re-acquire reuses the explicit-manifest machinery (the
    /// provenance's Steam identity becomes a ManifestSpec). This is the ONLY acquire mode that
    /// hash-verifies.
    ///
    /// Cache resolution (default cache-first; --cache-only / --no-cache override):
    ///   - --cache-only : the out dir must already hold every pinned input; never hit Steam.
    ///   - --no-cache   : always re-download from Steam, even if the cache is populated.
    ///   - default      : if every pinned input is already present, verify it in place
    ///                    (no Steam); else download then verify.
    /// Any mismatch / missing input after resolution -> fail-loud, exit 65, with a
    /// per-file report (path, expected, actual).
    /// </summary>
    private static async Task<int> RunFromProvenanceAsync(
        ISteamAcquirer acquirer, Dictionary<string, string> parsed, string platform,
        string fromProvenance, bool cacheOnly, bool noCache)
    {
        var provenancePath = Path.GetFullPath(fromProvenance);

        ManifestSpec spec;
        try
        {
            spec = ProvenanceReader.ReadSteamSpec(provenancePath);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine(
                $"acquire --from-provenance: provenance.json not found at '{provenancePath}'.");
            return 64;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"acquire --from-provenance: provenance.json invalid: {ex.Message}");
            return 64;
        }

        var outDir = Path.GetFullPath(
            parsed.TryGetValue("out", out var outArg) && !string.IsNullOrEmpty(outArg)
                ? outArg
                : DefaultOutDir(spec.BuildId.ToString(CultureInfo.InvariantCulture), platform));

        // Decide whether to download. Cache-first: if the out dir already holds every
        // pinned input we verify it in place without Steam. --cache-only forbids Steam
        // entirely; --no-cache forces a fresh download even on a cache hit.
        bool cachePopulated = AllInputsPresent(provenancePath, outDir);

        if (cacheOnly && !cachePopulated)
        {
            Console.Error.WriteLine(
                $"acquire --from-provenance --cache-only: one or more pinned inputs are NOT in the " +
                $"local cache '{outDir}'. No Steam access permitted. Drop --cache-only to download.");
            return 65;
        }

        bool downloaded = false;
        if (!cacheOnly && (noCache || !cachePopulated))
        {
            try
            {
                Console.Error.WriteLine(
                    $"acquire --from-provenance: re-acquiring pinned set build={spec.BuildId} platform={platform} " +
                    $"appId={spec.AppId} depots=[{string.Join(",", spec.OrderedDepots.Select(d => $"{d.DepotId}:{d.ManifestId}"))}] " +
                    $"outDir='{outDir}'" + (noCache ? " (--no-cache)" : ""));
                var result = await acquirer.AcquireExplicitAsync(spec, outDir, CancellationToken.None).ConfigureAwait(false);
                PrintSummary(result);
                downloaded = true;
            }
            catch (SteamGuardRequiredException ex)
            {
                PrintGuardSeedInstructions(ex);
                return 77;
            }
            catch (OperationCanceledException ex)
            {
                Console.Error.WriteLine($"acquire --from-provenance: cancelled before completion: {ex.Message}");
                return 1;
            }
            catch (InvalidDataException ex)
            {
                Console.Error.WriteLine($"acquire --from-provenance: data verification failure: {ex.Message}");
                return 65;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"acquire --from-provenance: Steam acquisition failed: {ex.Message}");
                return 65;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"acquire --from-provenance: unexpected failure: {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine(
                $"acquire --from-provenance: cache HIT — every pinned input present in '{outDir}'; verifying in place.");
        }

        // VERIFY (the load-bearing step): hash every acquired/cached file against the
        // provenance inputs[].sha256. Any mismatch OR missing file -> fail-loud (exit 65).
        InputVerificationResult verification;
        try
        {
            verification = InputBinaryVerifier.Verify(provenancePath, outDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"acquire --from-provenance: verification could not run: {ex.Message}");
            return 65;
        }

        if (!verification.Ok)
        {
            InputBinaryVerifier.WriteFailureReport(
                Console.Error,
                $"acquire --from-provenance: SHA-256 verification FAILED against '{provenancePath}'",
                verification);
            Console.Error.WriteLine(
                "acquire --from-provenance: the re-acquired inputs do not match the committed provenance " +
                ". Exit 65.");
            return 65;
        }

        Console.Error.WriteLine(
            $"acquire --from-provenance: SUCCESS — {verification.Verified} input(s) verified against the committed " +
            $"provenance" + (downloaded ? " (freshly acquired)." : " (from cache)."));
        return 0;
    }

    // ========================================================================
    // BATCH SELECTION MODE (--all / repeatable --build) — historical binary backfill. Loop every
    // selected (build, platform) from data/cs2-assets-inventory.json and acquire its recorded
    // BINARY manifest GID (per-build `acquire --from-manifest <spec> --binaries-only`). Mirrors
    // ExtractCommand's batch selection / fail-isolation / summary / exit semantics.
    // ========================================================================

    /// <summary>Per-(build, platform) batch outcome (drives the summary + exit code).</summary>
    private enum BatchStatus
    {
        /// <summary>Acquired successfully (a fresh download). Not a failure.</summary>
        Acquired,
        /// <summary>Already present (.acq-done marker) and --force not given. Not a failure.</summary>
        Skipped,
        /// <summary>The inventory has no binary manifest for this (build, platform). Not a failure (skip-of-record).</summary>
        NoManifest,
        /// <summary>Acquisition failed (verification / Steam / unexpected). A HARD failure.</summary>
        Failed,
    }

    private sealed record BatchResult(uint BuildId, string Platform, BatchStatus Status, string Detail);

    /// <summary>The resume marker the backfill writes into a completed (build, platform) output dir.</summary>
    internal const string AcqDoneMarker = ".acq-done";

    /// <summary>
    /// BATCH entry: parse the batch selection, resolve the inventory, and acquire each selected
    /// (build, platform) by its recorded binary manifest GID. Continue-on-failure (fail-loud is per
    /// item — one bad manifest never aborts the batch); a per-item HARD failure makes the whole run
    /// exit non-zero. Selection / usage errors exit 64 BEFORE any Steam contact.
    /// </summary>
    private static async Task<int> RunBatchAsync(string[] args, Func<ISteamAcquirer>? acquirerFactory)
    {
        var parsed = CliArgs.Parse(args);
        bool all = args.Any(a => a is "--all");
        var explicitBuilds = ExtractRepeated(args, "--build");

        // Mutual exclusion (usage errors, exit 64): a request cannot both batch-select AND name a
        // single explicit/provenance set.
        if (parsed.ContainsKey("from-manifest"))
        {
            Console.Error.WriteLine(
                "acquire: --from-manifest acquires ONE pinned set and is mutually exclusive with the " +
                "batch selection (--all / repeated --build). Drop one.");
            return 64;
        }
        if (parsed.ContainsKey("from-provenance"))
        {
            Console.Error.WriteLine(
                "acquire: --from-provenance re-acquires ONE pinned set and is mutually exclusive with the " +
                "batch selection (--all / repeated --build). Drop one.");
            return 64;
        }
        if (all && explicitBuilds.Count > 0)
        {
            Console.Error.WriteLine(
                "acquire: --all and an explicit --build list are mutually exclusive (choose 'every inventory " +
                "build' OR a specific set). Drop one.");
            return 64;
        }

        // --platform: optional in batch. Absent => every platform a build lists; present => that one.
        string? platformFilter = null;
        if (parsed.TryGetValue("platform", out var platArg) && !string.IsNullOrEmpty(platArg))
        {
            if (!PlatformToDepots.IsKnown(platArg))
            {
                Console.Error.WriteLine(
                    $"acquire: unknown platform '{platArg}'. Known: {string.Join(", ", PlatformToDepots.KnownPlatforms)}.");
                return 64;
            }
            platformFilter = platArg;
        }

        // UNIFIED tools leg (DEFAULT; --no-tools opts out): co-acquire each windows build's recorded
        // Workshop Tools editor-DLL slice (builds[].tools via ToolsTargetFor) into the same per-build
        // windows dir. The tools depot (2347779) is WINDOWS-ONLY, so an EXPLICIT --tools with a
        // non-windows --platform filter is a hard error (exit 2, BEFORE the inventory load / any
        // Steam contact); the tools leg otherwise rides ONLY the windows-x86_64 items (silently
        // omitted elsewhere, same as every other platform-scoped leg). An EXPLICIT --tools combined
        // with --binaries-only is a usage error (--binaries-only opts out of every co-located leg);
        // --no-tools + --binaries-only is a harmless no-op (binaries-only never had tools anyway).
        bool noToolsBatch = args.Any(a => a is "--no-tools");
        bool explicitToolsBatch = args.Any(a => a is "--tools");
        if (noToolsBatch && explicitToolsBatch)
        {
            Console.Error.WriteLine("acquire (batch): --tools and --no-tools are mutually exclusive.");
            return 64;
        }
        if (explicitToolsBatch && platformFilter is not null &&
            !string.Equals(platformFilter, "windows-x86_64", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"acquire (batch): --tools is windows-x86_64 only (the Workshop Tools depot " +
                $"{SteamAppIdMap.Cs2WorkshopToolsDepotId} ships windows editor DLLs; Valve publishes " +
                $"no Linux/mac Workshop Tools). Got --platform {platformFilter}.");
            return 2;
        }
        if (explicitToolsBatch && args.Any(a => a is "--binaries-only"))
        {
            Console.Error.WriteLine(
                "acquire (batch): --tools and --binaries-only are mutually exclusive " +
                "(--binaries-only opts out of every co-located leg; --tools requests one). Drop one.");
            return 64;
        }
        // DEFAULT: tools now rides the batch automatically for windows items (schema coverage) —
        // best-effort per build when not explicitly requested (see RunBatchOneAsync): the DLC-gated
        // depot needs an authenticated logon, so an anonymous/no-credentials run omits tools for that
        // build without marking it Failed. --binaries-only already opts out of every co-located leg.
        bool includeTools = !noToolsBatch && !args.Any(a => a is "--binaries-only");

        // Parse the explicit --build ids (each must be a concrete integer build_id — 'latest' is not a
        // batch target since the inventory is keyed by concrete build_id). Fail-loud on a bad id (64).
        var requestedBuilds = new List<uint>();
        foreach (var b in explicitBuilds)
        {
            if (!uint.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bid))
            {
                Console.Error.WriteLine(
                    $"acquire (batch): --build values must be concrete integer build ids (got '{b}'). " +
                    "'latest' is not a batch target.");
                return 64;
            }
            requestedBuilds.Add(bid);
        }

        // INVENTORY: the host-side source of every (build, platform) binary manifest GID.
        var inventoryPath = Path.GetFullPath(
            parsed.TryGetValue("inventory", out var invArg) && !string.IsNullOrEmpty(invArg)
                ? invArg
                : Path.Combine(EraWalkerResolverRepoRoot(), AssetsInventory.DefaultRelativePath));

        AssetsInventory inventory;
        try
        {
            inventory = AssetsInventory.Load(inventoryPath);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"acquire (batch): assets inventory could not be read: {ex.Message}");
            return 65;   // EX_DATAERR — the inventory metadata could not be read.
        }

        if (inventory.AppId != SteamAppIdMap.Cs2AppId)
        {
            Console.Error.WriteLine(
                $"acquire (batch): inventory app_id {inventory.AppId} is not the CS2 app {SteamAppIdMap.Cs2AppId}.");
            return 65;
        }

        // The platform set this run iterates: the explicit --platform, else every known platform the
        // inventory derived a binary depot for (stable order).
        var platforms = (platformFilter is not null
                ? new[] { platformFilter }
                : PlatformToDepots.KnownPlatforms.Where(inventory.HasBinaryDepotFor).ToArray())
            .Where(inventory.HasBinaryDepotFor)
            .ToArray();
        if (platforms.Length == 0)
        {
            Console.Error.WriteLine(
                "acquire (batch): the inventory derived no binary depot for the requested platform(s). " +
                "Nothing to do.");
            return platformFilter is not null ? 65 : 0;
        }

        // SELECTION: build the deterministic (build, platform) work list.
        //   --all: every inventory build with a binary manifest for the platform.
        //   --build ids: exactly those builds (each validated to be in the inventory; a missing id
        //                is a fail-loud usage error — the operator named a build we cannot acquire).
        var work = new List<(uint Build, string Platform)>();
        if (all)
        {
            foreach (var plat in platforms)
            {
                foreach (var bid in inventory.BuildsWithBinaryFor(plat))
                {
                    work.Add((bid, plat));
                }
            }
        }
        else
        {
            var missing = requestedBuilds.Where(b => !inventory.ContainsBuild(b)).Distinct().ToList();
            if (missing.Count > 0)
            {
                Console.Error.WriteLine(
                    $"acquire (batch): --build id(s) not in the inventory: {string.Join(", ", missing)}. " +
                    "Only builds the inventory records a binary manifest for can be batch-acquired.");
                return 64;
            }
            foreach (var bid in requestedBuilds.Distinct().OrderBy(b => b))
            {
                foreach (var plat in platforms)
                {
                    work.Add((bid, plat));
                }
            }
        }

        // Deterministic order: by build id, then platform name. De-dup defensively.
        work = work.Distinct().OrderBy(w => w.Build).ThenBy(w => w.Platform, StringComparer.Ordinal).ToList();

        if (work.Count == 0)
        {
            Console.Error.WriteLine("acquire (batch): no (build, platform) matched the selection. Nothing to do.");
            return 0;
        }

        // The output root the resume marker / per-build dir is keyed under. --out overrides the default
        // cache/binaries root; each (build, platform) lands in <root>/<build>/<platform>.
        string? outRootOverride = parsed.TryGetValue("out", out var outArg) && !string.IsNullOrEmpty(outArg)
            ? Path.GetFullPath(outArg)
            : null;
        bool force = args.Any(a => a is "--force");
        // UNIFIED ACQUIRE: the batch fetches binaries + co-located content per build by default, so
        // a single extract emits every artifact. --binaries-only opts out.
        bool includeContent = !args.Any(a => a is "--binaries-only");

        // The acquirer is built ONCE for the whole batch; the single Steam logon is enforced below by
        // the BeginSharedSession scope wrapping the per-build loop (building the acquirer once is NOT
        // enough — each acquire used to connect+logon on its own session, which tripped Steam's
        // AccountLoginDeniedThrottle after ~58 builds). The historical path needs auth (anonymous
        // Steam only issues a request code for the CURRENT manifest); auto-selected when creds are
        // present, or forced with --auth.
        ISteamAcquirer acquirer;
        if (acquirerFactory is not null)
        {
            acquirer = acquirerFactory();
        }
        else
        {
            bool explicitAuth = parsed.ContainsKey("auth");
            var guardCode = parsed.TryGetValue("guard-code", out var gc) && !string.IsNullOrEmpty(gc) ? gc : null;
            try
            {
                // historicalPath: true — the batch always re-fetches recorded (prior) manifests.
                acquirer = BuildRealAcquirer(explicitAuth, historicalPath: true, guardCode);
            }
            catch (CredentialsMissingException ex)
            {
                Console.Error.WriteLine($"acquire (batch): {ex.Message}");
                return 64;
            }
        }

        // PROBE MODE in batch (--probe): cheap manifest-level reachability per selected (build,
        // platform) — NO bulk download in any mode (the single-build --probe contract, extended to
        // the batch). Without this, `acquire --build A --build B --probe` would silently DOWNLOAD
        // both builds (the batch path used to ignore --probe), a dangerous multi-GB fetch. Probe
        // each build's recorded manifests via ManifestProbeRunner (manifest-level, no chunks unless
        // --probe-chunk); a NO for any build makes the run exit non-zero.
        if (parsed.ContainsKey("probe"))
        {
            bool probeChunk = parsed.ContainsKey("probe-chunk");
            Console.Error.WriteLine(
                $"acquire (batch --probe): {work.Count} (build, platform) selected " +
                $"(mode={(all ? "--all" : "--build")}, platforms=[{string.Join(",", platforms)}], " +
                $"probe-chunk={probeChunk}, inventory='{inventoryPath}'). Manifest-level only; NO bulk download.");

            var probeResults = new List<BatchProbeResult>();
            using (acquirer.BeginSharedSession())
            {
                int p = 0;
                foreach (var (buildId, plat) in work)
                {
                    p++;
                    Console.Error.WriteLine($"acquire (batch --probe): [{p}/{work.Count}] build {buildId} {plat}");
                    probeResults.Add(await RunBatchProbeOneAsync(
                        acquirer, inventory, buildId, plat, probeChunk).ConfigureAwait(false));
                }
            }

            return SummarizeBatchProbe(probeResults);
        }

        Console.Error.WriteLine(
            $"acquire (batch): {work.Count} (build, platform) selected " +
            $"(mode={(all ? "--all" : "--build")}, platforms=[{string.Join(",", platforms)}], " +
            $"force={force}, content={(includeContent ? "binaries+content" : "binaries-only")}, " +
            $"tools={(includeTools ? "on (windows items)" : "off")}, " +
            $"inventory='{inventoryPath}').");

        // ONE shared Steam session for the whole batch (per-build re-logon tripped
        // AccountLoginDeniedThrottle after ~58 builds). BeginSharedSession opens a single
        // connection+logon that every per-build AcquireBinariesOnlyAsync / AcquireContentPakAsync
        // call reuses; a 244-build run now does ONE logon, not 244. The scope tears the session down
        // when the loop completes. Per-build data failures still fail-isolate inside RunBatchOneAsync;
        // a shared-session drop reconnects once on the next build's lease.
        var results = new List<BatchResult>();
        using (acquirer.BeginSharedSession())
        {
            int i = 0;
            foreach (var (buildId, plat) in work)
            {
                i++;
                Console.Error.WriteLine($"acquire (batch): [{i}/{work.Count}] build {buildId} {plat}");
                results.Add(await RunBatchOneAsync(
                    acquirer, inventory, buildId, plat, outRootOverride, force,
                    includeContent, includeTools, explicitToolsBatch).ConfigureAwait(false));
            }
        }

        return SummarizeBatch(results);
    }

    /// <summary>
    /// Acquire ONE (build, platform) by its inventory binary manifest GID. Fail-isolated: any failure
    /// is classified <see cref="BatchStatus.Failed"/> and never throws out (so the batch continues).
    /// Resumable: a (build, platform) whose output dir already carries the <see cref="AcqDoneMarker"/>
    /// is skipped unless <paramref name="force"/>. On success the marker is written (a fixed
    /// sentinel string, not a timestamp, so it never perturbs determinism).
    /// </summary>
    private static async Task<BatchResult> RunBatchOneAsync(
        ISteamAcquirer acquirer, AssetsInventory inventory,
        uint buildId, string platform, string? outRootOverride, bool force,
        bool includeContent, bool includeTools, bool explicitTools)
    {
        var target = inventory.TargetFor(buildId, platform);
        if (target is null)
        {
            return new BatchResult(buildId, platform, BatchStatus.NoManifest,
                "no binary manifest recorded for this (build, platform)");
        }

        var outDir = outRootOverride is not null
            ? Path.Combine(outRootOverride, buildId.ToString(CultureInfo.InvariantCulture), platform)
            : Path.GetFullPath(DefaultOutDir(buildId.ToString(CultureInfo.InvariantCulture), platform));

        // Resolve the content target up front: it drives BOTH the skip semantics and the content leg.
        // A build with no recorded content GID (or a binaries-only request) has nothing to co-locate, so
        // a binaries-only marker is its COMPLETE state and it is skippable.
        var contentTarget = includeContent ? inventory.ContentTargetFor(buildId) : null;
        bool needContentMarker = contentTarget is not null;

        // The tools leg rides ONLY the windows items (the Workshop Tools depot ships windows editor
        // DLLs; the batch gate has already rejected a non-windows --platform filter). Like content,
        // a build with no recorded tools GID has nothing to co-acquire.
        var toolsTarget = includeTools && string.Equals(platform, "windows-x86_64", StringComparison.Ordinal)
            ? inventory.ToolsTargetFor(buildId)
            : null;

        // Resume marker governs the BASE legs (binaries[+content]) exactly as before: a build with
        // content to fetch is NOT satisfied by a binaries-only marker (re-running --all after a
        // binaries-only batch re-acquires to ADD content). The TOOLS decision is deliberately NOT
        // marker-based: whether the tools slice is present is decided by the dir's
        // manifest-record.json (depot 2347779 — the AUTHORITATIVE record every tools acquire merges;
        // see RecordListsToolsDepot), so a --tools re-run over an already-populated cache RETROFITS
        // only the missing tools leg, leaving the verified binaries/content untouched.
        var marker = Path.Combine(outDir, AcqDoneMarker);
        bool baseLegsDone = !force && MarkerSatisfies(marker, needContentMarker);

        var spec = target.ToManifestSpec(inventory.AppId);
        try
        {
            if (baseLegsDone)
            {
                string haveBase = needContentMarker ? "binaries+content" : "binaries";
                if (toolsTarget is null)
                {
                    if (includeTools && string.Equals(platform, "windows-x86_64", StringComparison.Ordinal))
                    {
                        // Loud skip-of-record: --tools was requested but this build predates tools
                        // tracking (no builds[].tools GID). Not an error; a later inventory update
                        // makes the next --tools run pick it up.
                        Console.Error.WriteLine(
                            $"acquire (batch): build {buildId} {platform} has NO tools GID recorded in the " +
                            "inventory (builds[].tools) — tools omitted for this build (a skip-of-record, not an error).");
                        return new BatchResult(buildId, platform, BatchStatus.Skipped,
                            $"already done ({haveBase} marker present; no tools GID — tools omitted)");
                    }
                    return new BatchResult(buildId, platform, BatchStatus.Skipped,
                        $"already done ({haveBase} marker present)");
                }

                // --tools over a completed base acquire: record-authoritative retrofit decision.
                // RecordListsToolsDepot fails loud on a corrupt record — caught below as a per-build
                // hard failure (fail-isolated, never a silent skip).
                if (RecordListsToolsDepot(outDir))
                {
                    Console.Error.WriteLine(
                        $"acquire (batch): build {buildId} {platform} cache HIT incl. tools — " +
                        "manifest-record.json already lists depot 2347779; nothing to fetch.");
                    return new BatchResult(buildId, platform, BatchStatus.Skipped,
                        $"already done ({haveBase} marker present; manifest-record lists tools depot)");
                }

                Console.Error.WriteLine(
                    $"acquire (batch): build {buildId} {platform} cache HIT; tools leg missing " +
                    "(manifest-record.json lists no depot 2347779) -> acquiring tools only " +
                    "(binaries/content untouched).");
                var retrofitSpec = toolsTarget.ToManifestSpec(inventory.AppId);
                // EXPLICIT --tools retrofits fail-loud (propagates to the catch below => Failed,
                // unchanged). A DEFAULT-implied want is best-effort: the DLC-gated depot needs an
                // authenticated logon, so an anonymous/no-credentials run must not mark hundreds of
                // otherwise-clean historical builds Failed — log a note and leave the build Skipped
                // (binaries/content untouched, re-tried automatically once creds are available).
                if (!explicitTools)
                {
                    try
                    {
                        var retrofit = await acquirer.AcquireToolsAsync(
                            retrofitSpec.AppId, toolsTarget.ToolsDepotId, buildId: 0, outDir,
                            explicitSpec: retrofitSpec, CancellationToken.None).ConfigureAwait(false);
                        AppendToolsMarkerToken(marker, buildId, platform);
                        return new BatchResult(buildId, platform, BatchStatus.Acquired,
                            $"tools-only retrofit(files={retrofit.Files.Count} fetched={retrofit.DownloadedBytes:N0}B; " +
                            "binaries/content untouched)");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"acquire (batch): build {buildId} {platform} tools retrofit skipped (non-fatal, " +
                            $"default-implied — needs an authenticated Steam logon): {ex.GetType().Name}: {ex.Message}");
                        return new BatchResult(buildId, platform, BatchStatus.Skipped,
                            $"already done ({haveBase} marker present; tools retrofit skipped, non-fatal)");
                    }
                }
                var explicitRetrofit = await acquirer.AcquireToolsAsync(
                    retrofitSpec.AppId, toolsTarget.ToolsDepotId, buildId: 0, outDir,
                    explicitSpec: retrofitSpec, CancellationToken.None).ConfigureAwait(false);
                AppendToolsMarkerToken(marker, buildId, platform);
                return new BatchResult(buildId, platform, BatchStatus.Acquired,
                    $"tools-only retrofit(files={explicitRetrofit.Files.Count} fetched={explicitRetrofit.DownloadedBytes:N0}B; " +
                    "binaries/content untouched)");
            }

            var result = await acquirer.AcquireBinariesOnlyAsync(
                spec.AppId, Array.Empty<uint>(), spec.BuildId, outDir, platform,
                explicitSpec: spec, CancellationToken.None).ConfigureAwait(false);

            // UNIFIED ACQUIRE (Gap A): co-locate the selective content pak with the binaries so a single
            // extract emits every artifact. The per-build content GID comes from the inventory
            // (builds[].content via ContentTargetFor). A build with no recorded content GID has content
            // OMITTED (a documented note, not a failure — the marker stays binaries-only so a later
            // inventory update re-acquires it). A content FETCH failure propagates to the catch below =>
            // the build is Failed and re-acquired next run (fail-loud, the set is incomplete).
            bool contentDone = false;
            string contentNote = "";
            if (includeContent)
            {
                if (contentTarget is null)
                {
                    contentNote = " (no content GID in inventory — content omitted)";
                }
                else
                {
                    var contentSpec = contentTarget.ToManifestSpec(inventory.AppId);
                    var contentResult = await acquirer.AcquireContentPakAsync(
                        contentSpec.AppId, contentTarget.ContentDepotId, buildId: 0, outDir,
                        minimalGameEvents: true, explicitSpec: contentSpec, dirOnly: false,
                        CancellationToken.None).ConfigureAwait(false);
                    contentDone = true;
                    contentNote =
                        $" +content(files={contentResult.Files.Count} fetched={contentResult.DownloadedBytes:N0}B)";
                }
            }

            // UNIFIED tools leg (--tools): co-acquire the Workshop Tools editor-DLL slice into the
            // same windows dir. The per-build tools GID comes from the inventory (builds[].tools
            // via ToolsTargetFor). A build with no recorded tools GID has tools OMITTED — a LOUD
            // stderr note, not a failure (the marker stays tools-less so a later inventory update
            // re-acquires it). A tools FETCH failure propagates to the catch below => the build is
            // Failed and re-acquired next run (fail-loud, the set is incomplete).
            bool toolsDone = false;
            string toolsNote = "";
            if (includeTools && string.Equals(platform, "windows-x86_64", StringComparison.Ordinal))
            {
                if (toolsTarget is null)
                {
                    Console.Error.WriteLine(
                        $"acquire (batch): build {buildId} {platform} has NO tools GID recorded in the " +
                        "inventory (builds[].tools) — tools omitted for this build (a skip-of-record, not an error).");
                    toolsNote = " (no tools GID in inventory — tools omitted)";
                }
                else if (explicitTools)
                {
                    // EXPLICIT --tools: unchanged fail-loud contract — a fetch failure propagates to
                    // the catch below (Failed, re-acquired next run).
                    var toolsSpec = toolsTarget.ToManifestSpec(inventory.AppId);
                    var toolsResult = await acquirer.AcquireToolsAsync(
                        toolsSpec.AppId, toolsTarget.ToolsDepotId, buildId: 0, outDir,
                        explicitSpec: toolsSpec, CancellationToken.None).ConfigureAwait(false);
                    toolsDone = true;
                    toolsNote =
                        $" +tools(files={toolsResult.Files.Count} fetched={toolsResult.DownloadedBytes:N0}B)";
                }
                else
                {
                    // DEFAULT-implied: best-effort. The DLC-gated depot needs an authenticated logon;
                    // an anonymous/no-credentials run must not fail an otherwise-clean binaries+content
                    // build over a bonus leg — log a note and leave tools omitted (retried automatically
                    // by the retrofit path once creds are available).
                    try
                    {
                        var toolsSpec = toolsTarget.ToManifestSpec(inventory.AppId);
                        var toolsResult = await acquirer.AcquireToolsAsync(
                            toolsSpec.AppId, toolsTarget.ToolsDepotId, buildId: 0, outDir,
                            explicitSpec: toolsSpec, CancellationToken.None).ConfigureAwait(false);
                        toolsDone = true;
                        toolsNote =
                            $" +tools(files={toolsResult.Files.Count} fetched={toolsResult.DownloadedBytes:N0}B)";
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"acquire (batch): build {buildId} {platform} tools leg skipped (non-fatal, " +
                            $"default-implied — needs an authenticated Steam logon): {ex.GetType().Name}: {ex.Message}");
                        toolsNote = " (tools skipped, non-fatal — needs an authenticated Steam logon)";
                    }
                }
            }

            // Mark done (resume sentinel). Leg-aware tokens so the skip check above can tell a
            // binaries-only marker from a binaries+content(+tools) one. Best-effort: a write failure
            // does not fail the acquisition (bytes are verified + promoted), only forces a
            // re-acquire next run.
            try
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllText(
                    marker,
                    "acquired" + (contentDone ? "+content" : "") + (toolsDone ? "+tools" : "") + "\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(
                    $"acquire (batch): build {buildId} {platform} acquired but the .acq-done marker " +
                    $"could not be written ({ex.Message}); it will be re-acquired next run.");
            }

            // Report the BINARY leg's actually-transferred bytes (DownloadedBytes), not the
            // on-disk total (TotalBytes): a cache-hit over already-acquired binaries shows
            // bin-fetched=0 even though files/total are the full tree, so a content backfill
            // is visibly content-only (the bug fix's observable signal).
            string binNote = result.DownloadedBytes == 0
                ? $"binaries=cache-hit(files={result.Files.Count}, bin-fetched=0)"
                : $"binaries(files={result.Files.Count}, bin-fetched={result.DownloadedBytes:N0}B, total={result.TotalBytes:N0}B)";
            return new BatchResult(buildId, platform, BatchStatus.Acquired, $"{binNote}{contentNote}{toolsNote}");
        }
        catch (SteamGuardRequiredException ex)
        {
            // A Guard prompt is operator action, not a per-build data failure — but it WILL recur for
            // every remaining build, so surface the seed instructions and classify Failed (hard).
            PrintGuardSeedInstructions(ex);
            return new BatchResult(buildId, platform, BatchStatus.Failed, "Steam Guard code required");
        }
        catch (InvalidDataException ex)
        {
            return new BatchResult(buildId, platform, BatchStatus.Failed, $"verification failure: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new BatchResult(buildId, platform, BatchStatus.Failed, $"Steam acquisition failed: {ex.Message}");
        }
        catch (OperationCanceledException ex)
        {
            return new BatchResult(buildId, platform, BatchStatus.Failed, $"cancelled: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Fail-isolation: one (build, platform)'s unexpected error never aborts the batch.
            return new BatchResult(buildId, platform, BatchStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Does the resume marker satisfy the requested BASE legs (binaries[+content])? A binaries-only
    /// request (<paramref name="includeContent"/> false) is satisfied by ANY marker (binaries are
    /// present). A unified request (true) is satisfied ONLY by a content-bearing marker
    /// ("acquired+content"), so a prior binaries-only run is correctly re-acquired to add content. A
    /// missing/unreadable marker is never satisfied (re-acquire). The TOOLS leg is deliberately NOT
    /// part of this check — tools presence is decided by manifest-record.json (depot 2347779; see
    /// <see cref="RecordListsToolsDepot"/>), which enables the tools-only RETROFIT over a completed
    /// base acquire.
    /// </summary>
    private static bool MarkerSatisfies(string marker, bool includeContent)
    {
        if (!File.Exists(marker))
            return false;
        if (!includeContent)
            return true;
        try
        {
            return File.ReadAllText(marker).Contains("content", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // unreadable marker -> re-acquire (never a silent skip).
        }
    }

    /// <summary>
    /// Best-effort: append the "+tools" token to an existing resume marker after a tools-only
    /// RETROFIT (observability only — the authoritative tools-presence signal is the 2347779 entry
    /// in manifest-record.json, already merged by the acquire). A write failure is surfaced but
    /// never fails the retrofit (bytes are verified + merged).
    /// </summary>
    private static void AppendToolsMarkerToken(string marker, uint buildId, string platform)
    {
        try
        {
            var text = File.Exists(marker) ? File.ReadAllText(marker).TrimEnd('\n') : "acquired";
            if (!text.Contains("tools", StringComparison.Ordinal))
            {
                File.WriteAllText(marker, text + "+tools\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"acquire (batch): build {buildId} {platform} tools retrofit succeeded but the .acq-done " +
                $"marker could not be updated ({ex.Message}); harmless — the manifest-record already lists the tools depot.");
        }
    }

    /// <summary>Print the batch summary and return the exit code (non-zero iff any HARD failure).</summary>
    private static int SummarizeBatch(IReadOnlyList<BatchResult> results)
    {
        int acquired = results.Count(r => r.Status == BatchStatus.Acquired);
        int skipped = results.Count(r => r.Status == BatchStatus.Skipped);
        int noManifest = results.Count(r => r.Status == BatchStatus.NoManifest);
        int failed = results.Count(r => r.Status == BatchStatus.Failed);

        Console.Error.WriteLine("acquire (batch): ==================== SUMMARY ====================");
        foreach (var r in results)
        {
            Console.Error.WriteLine(
                $"acquire (batch):   {r.BuildId} {r.Platform,-14} {r.Status,-10} {r.Detail}");
        }
        Console.Error.WriteLine(
            $"acquire (batch): acquired={acquired} skipped={skipped} no-manifest={noManifest} " +
            $"failed={failed} (of {results.Count})");

        if (failed > 0)
        {
            var failedIds = results.Where(r => r.Status == BatchStatus.Failed)
                .Select(r => $"{r.BuildId}/{r.Platform}");
            Console.Error.WriteLine(
                $"acquire (batch): {failed} (build, platform) FAILED: {string.Join(", ", failedIds)}.");
            return 70; // EX_SOFTWARE — at least one acquisition failed (at the run boundary).
        }
        return 0;
    }

    // ========================================================================
    // BATCH PROBE (--probe with batch selection) — cheap manifest-level
    // reachability per selected (build, platform). NEVER downloads content.
    // ========================================================================

    /// <summary>Per-(build, platform) batch-probe verdict (drives the summary + exit code).</summary>
    private enum BatchProbeStatus
    {
        /// <summary>Every depot's historical manifest fetched anonymously. Reachable.</summary>
        Fetchable,
        /// <summary>At least one depot's manifest was NOT fetchable (or the probe threw). A HARD failure.</summary>
        Unreachable,
        /// <summary>The inventory records no binary manifest for this (build, platform). Skip-of-record, not a failure.</summary>
        NoManifest,
    }

    private sealed record BatchProbeResult(uint BuildId, string Platform, BatchProbeStatus Status, string Detail);

    /// <summary>
    /// Probe ONE (build, platform) at manifest level: build a <see cref="ManifestRecord"/> from the
    /// inventory's recorded binary (and content, when present) manifest GIDs and run
    /// <see cref="ManifestProbeRunner"/> — which does a PICS-current resolve + an explicit-manifest
    /// fetch (and, with <paramref name="probeChunk"/>, ONE sample chunk per depot to confirm CDN
    /// residency). NO bulk download ever happens here (the bulk acquire entry points are never
    /// called). Fail-isolated: a probe failure is classified <see cref="BatchProbeStatus.Unreachable"/>
    /// and never throws out, so the whole batch probe completes.
    /// </summary>
    private static async Task<BatchProbeResult> RunBatchProbeOneAsync(
        ISteamAcquirer acquirer, AssetsInventory inventory, uint buildId, string platform, bool probeChunk)
    {
        var target = inventory.TargetFor(buildId, platform);
        if (target is null)
        {
            return new BatchProbeResult(buildId, platform, BatchProbeStatus.NoManifest,
                "no binary manifest recorded for this (build, platform)");
        }

        // Build the record from the recorded GIDs (manifest_created_utc is unknown until fetched —
        // a placeholder epoch, as the single-build --from-manifest probe path does). Include the
        // content depot too when the inventory carries one, so the probe covers the whole set.
        var depots = new List<ManifestRecordDepot>
        {
            new(target.BinaryDepotId, target.ManifestId, "1970-01-01T00:00:00Z"),
        };
        var contentTarget = inventory.ContentTargetFor(buildId);
        if (contentTarget is not null)
        {
            depots.Add(new ManifestRecordDepot(contentTarget.ContentDepotId, contentTarget.ManifestId, "1970-01-01T00:00:00Z"));
        }
        var record = new ManifestRecord(inventory.AppId, buildId, depots);

        try
        {
            var report = await ManifestProbeRunner.RunAsync(
                acquirer, record, probeChunk, CancellationToken.None).ConfigureAwait(false);
            return report.HistoricalManifestFetchable
                ? new BatchProbeResult(buildId, platform, BatchProbeStatus.Fetchable,
                    $"all {record.Depots.Count} depot manifest(s) fetchable" +
                    (report.RecordedBuildIsCurrent ? " (still current)" : " (previous build)"))
                : new BatchProbeResult(buildId, platform, BatchProbeStatus.Unreachable,
                    "one or more depot manifests NOT fetchable");
        }
        catch (SteamGuardRequiredException ex)
        {
            // A Guard prompt is operator action; it recurs for every remaining build, so surface the
            // seed instructions and classify Unreachable (hard — the run exits non-zero).
            PrintGuardSeedInstructions(ex);
            return new BatchProbeResult(buildId, platform, BatchProbeStatus.Unreachable, "Steam Guard code required");
        }
        catch (OperationCanceledException ex)
        {
            return new BatchProbeResult(buildId, platform, BatchProbeStatus.Unreachable, $"cancelled: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            return new BatchProbeResult(buildId, platform, BatchProbeStatus.Unreachable, $"Steam probe failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Fail-isolation: one probe's unexpected error never aborts the batch probe.
            return new BatchProbeResult(buildId, platform, BatchProbeStatus.Unreachable, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Print the batch-probe summary and return the exit code (non-zero iff any build is unreachable).</summary>
    private static int SummarizeBatchProbe(IReadOnlyList<BatchProbeResult> results)
    {
        int fetchable = results.Count(r => r.Status == BatchProbeStatus.Fetchable);
        int unreachable = results.Count(r => r.Status == BatchProbeStatus.Unreachable);
        int noManifest = results.Count(r => r.Status == BatchProbeStatus.NoManifest);

        Console.Error.WriteLine("acquire (batch --probe): ================ PROBE SUMMARY ================");
        foreach (var r in results)
        {
            Console.Error.WriteLine(
                $"acquire (batch --probe):   {r.BuildId} {r.Platform,-14} {r.Status,-11} {r.Detail}");
        }
        Console.Error.WriteLine(
            $"acquire (batch --probe): fetchable={fetchable} unreachable={unreachable} " +
            $"no-manifest={noManifest} (of {results.Count})");

        if (unreachable > 0)
        {
            var ids = results.Where(r => r.Status == BatchProbeStatus.Unreachable)
                .Select(r => $"{r.BuildId}/{r.Platform}");
            Console.Error.WriteLine(
                $"acquire (batch --probe): {unreachable} (build, platform) NOT fetchable: {string.Join(", ", ids)}.");
            return 65; // EX_DATAERR — a historical manifest was not reachable (at the run boundary).
        }
        return 0;
    }

    /// <summary>
    /// Collect every value of a repeated flag (e.g. all <c>--build</c> values) from the raw args,
    /// supporting both <c>--flag value</c> and <c>--flag=value</c> forms. The CliArgs dictionary
    /// collapses repeats, so the batch path scans the raw array instead.
    /// </summary>
    private static List<string> ExtractRepeated(string[] args, string flag)
    {
        var values = new List<string>();
        var eqPrefix = flag + "=";
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag)
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    values.Add(args[++i]);
                }
            }
            else if (args[i].StartsWith(eqPrefix, StringComparison.Ordinal))
            {
                values.Add(args[i][eqPrefix.Length..]);
            }
        }
        return values;
    }

    /// <summary>Repo root for locating the default inventory (mirrors EraWalkerResolver's discovery).</summary>
    private static string EraWalkerResolverRepoRoot()
        => Walker.EraWalkerResolver.DiscoverRepoRoot();

    /// <summary>
    /// True iff every input pinned by <paramref name="provenancePath"/> is already
    /// present on disk under <paramref name="binariesDir"/> (presence only — bytes are
    /// verified separately). A missing/unparseable provenance bubbles up to the caller's
    /// fail-loud handling, so here we treat any read failure as "not fully present".
    /// </summary>
    private static bool AllInputsPresent(string provenancePath, string binariesDir)
    {
        IReadOnlyList<ProvenanceBinaryRef> refs;
        try
        {
            refs = ProvenanceReader.ReadInputs(provenancePath);
        }
        catch
        {
            return false;
        }
        foreach (var r in refs)
        {
            string local;
            try
            {
                local = ProvenanceReader.ResolveLocal(binariesDir, r.Path);
            }
            catch
            {
                return false;
            }
            if (!File.Exists(local))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Cache-presence heuristic for the --build/--platform path (no provenance to enumerate):
    /// the build/platform-keyed cache dir exists and holds at least one file (recursively).
    /// </summary>
    private static bool CacheDirPopulated(string dir)
        => Directory.Exists(dir) &&
           Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any();

    /// <summary>
    /// AUTHORITATIVE tools-presence check for a cached tuple dir: does its manifest-record.json
    /// list the Workshop Tools depot (2347779)? Every tools acquire merges that depot entry into
    /// the record (<see cref="ManifestRecord.MergeIntoTupleDir"/>), so the record — not a dir/file
    /// heuristic — decides whether the slice is present (the tools DLLs land in the SAME bin
    /// subtrees as the base binaries, so file presence could never distinguish them). A missing
    /// record means no tools; a present-but-corrupt record fails loud via
    /// <see cref="ManifestRecord.ReadFromFile"/> (a real input problem, mapped to the caller's
    /// exit-code / fail-isolation handling).
    /// </summary>
    private static bool RecordListsToolsDepot(string tupleDir)
    {
        var path = Path.Combine(tupleDir, ManifestRecord.FileName);
        if (!File.Exists(path))
            return false;
        return ManifestRecord.ReadFromFile(path)
            .Depots.Any(d => d.DepotId == SteamAppIdMap.Cs2WorkshopToolsDepotId);
    }

    /// <summary>
    /// TOOLS RETROFIT (single-build path): acquire ONLY the missing Workshop Tools leg into an
    /// already-populated cache dir whose manifest-record.json lacks depot 2347779 — the
    /// backfill-over-an-existing-cache case the cache-first HIT must not short-circuit (and
    /// --no-cache must not be the answer: it would re-download the binaries).
    ///
    /// GID sourcing: a CONCRETE --build resolves the build's recorded tools GID from the assets
    /// inventory (builds[].tools via <see cref="AssetsInventory.ToolsTargetFor"/> — the historical
    /// path; needs the authenticated logon the caller's auth selection already engaged). A build
    /// with NO recorded tools GID keeps the loud skip-of-record semantics (exit 0 — tools omitted,
    /// documented on stderr, never a fabricated fetch). 'latest' (buildId 0 — only reachable here
    /// with an explicit --out) resolves PICS-current, exactly like the fresh-acquire tools leg.
    /// Runs inside the caller's try, so Steam/data failures map to the standard exit codes.
    /// </summary>
    private static async Task<int> RunToolsRetrofitAsync(
        ISteamAcquirer acquirer, Dictionary<string, string> parsed, uint buildId, string outDir)
    {
        if (buildId == 0)
        {
            await AcquireUnifiedToolsAsync(acquirer, outDir, CancellationToken.None).ConfigureAwait(false);
            return 0;
        }

        var inventoryPath = Path.GetFullPath(
            parsed.TryGetValue("inventory", out var invArg) && !string.IsNullOrEmpty(invArg)
                ? invArg
                : Path.Combine(EraWalkerResolverRepoRoot(), AssetsInventory.DefaultRelativePath));
        var inventory = AssetsInventory.Load(inventoryPath);   // fail-loud (InvalidDataException -> 65).

        var target = inventory.ToolsTargetFor(buildId);
        if (target is null)
        {
            Console.Error.WriteLine(
                $"acquire: build {buildId} has NO tools GID recorded in the inventory (builds[].tools, " +
                $"'{inventoryPath}') — tools omitted for this build (a skip-of-record, not an error).");
            return 0;
        }

        var spec = target.ToManifestSpec(inventory.AppId);
        Console.Error.WriteLine(
            $"acquire: TOOLS RETROFIT — fetching the editor-DLL slice of depot {target.ToolsDepotId} " +
            $"(historical manifest {target.ManifestId}) into '{outDir}'.");
        var result = await acquirer.AcquireToolsAsync(
            spec.AppId, target.ToolsDepotId, buildId: 0, outDir,
            explicitSpec: spec, CancellationToken.None).ConfigureAwait(false);
        PrintSummary(result);
        return 0;
    }

    /// <summary>
    /// BEST-EFFORT wrapper around <see cref="RunToolsRetrofitAsync"/> for the DEFAULT-implied tools
    /// leg over an already-cached (build, platform) dir. Same rationale as
    /// <see cref="TryAcquireUnifiedToolsAsync"/>: the tools depot is DLC-gated, so a retrofit attempt
    /// under an anonymous/no-credentials session is a guaranteed failure — swallow it, log a note, and
    /// leave the cache-hit's own success (binaries/content already verified) unaffected. An explicit
    /// --tools retrofit does NOT use this wrapper (calls <see cref="RunToolsRetrofitAsync"/> directly).
    /// </summary>
    private static async Task TryRunToolsRetrofitAsync(
        ISteamAcquirer acquirer, Dictionary<string, string> parsed, uint buildId, string outDir)
    {
        try
        {
            await RunToolsRetrofitAsync(acquirer, parsed, buildId, outDir).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "acquire: tools retrofit skipped (non-fatal, default-implied — the Workshop Tools depot needs " +
                $"an authenticated Steam logon; set STEAM_USERNAME/STEAM_PASSWORD or pass --auth to include " +
                $"it): {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the real (non-test) acquirer, selecting the auth mode.
    /// </summary>
    internal static SteamAnonymousAcquirer BuildRealAcquirer(bool explicitAuth, bool historicalPath, string? guardCode)
    {
        bool credsAvailable = SteamCredentials.AvailableInEnvironment();

        // Decide whether to authenticate.
        bool authenticate;
        if (explicitAuth)
        {
            // --auth is an explicit request: fail loud if creds are absent.
            if (!credsAvailable)
            {
                throw new CredentialsMissingException(
                    "--auth was requested but STEAM_USERNAME / STEAM_PASSWORD are not set " +
                    "(populate the gitignored repo-root .env or export them). " +
                    "Anonymous acquire needs no credentials; drop --auth for current-build capture.");
            }
            authenticate = true;
        }
        else if (historicalPath && credsAvailable)
        {
            // Auto-select auth for the historical/explicit-manifest path when creds
            // exist: anonymous Steam cannot fetch a prior manifest's request code.
            Console.Error.WriteLine(
                "acquire: historical/explicit-manifest path with credentials present — using AUTHENTICATED Steam logon.");
            authenticate = true;
        }
        else
        {
            authenticate = false;
        }

        if (!authenticate)
        {
            return new SteamAnonymousAcquirer();
        }

        var creds = SteamCredentials.FromEnvironment(guardCode)
            ?? throw new CredentialsMissingException(
                "Steam credentials unexpectedly unavailable for authenticated logon.");
        var store = new SteamSessionStore(Console.Error);
        return new SteamAnonymousAcquirer(SteamAuthMode.Authenticated, creds, store, Console.Error);
    }

    /// <summary>Thrown when --auth is requested but credentials are absent (maps to exit 64).</summary>
    private sealed class CredentialsMissingException : Exception
    {
        public CredentialsMissingException(string message) : base(message) { }
    }

    /// <summary>
    /// The DEFAULT per-(build, platform) acquire output dir when no explicit <c>--out</c> is given.
    /// Honors the binaries-store root (env <c>CS2_BINARIES_ROOT</c>, else appsettings BinariesRoot;
    /// env wins) so the acquired-binaries store can live off the repo volume — the SAME resolution
    /// <see cref="ExtractCommand"/> uses (its <c>&lt;root&gt;/&lt;build&gt;/&lt;platform&gt;/</c> convention),
    /// so an acquire and a later extract agree on the location. When the root is empty (unset), falls
    /// back to the in-repo <c>cache/binaries/&lt;build&gt;/&lt;platform&gt;/</c> convention. Precedence at the
    /// call sites: an explicit <c>--out</c> always WINS over this default (operator override).
    /// </summary>
    internal static string DefaultOutDir(string buildId, string platform)
        => Path.Combine(BinariesStoreRoot(), buildId, platform);

    /// <summary>
    /// The effective binaries STORE ROOT (the dir holding every <c>&lt;build&gt;/&lt;platform&gt;</c>
    /// tuple dir, plus the <c>_content</c> / <c>_pics</c> sidecar dirs): <see cref="Config.HostConfig.BinariesRoot"/>
    /// when set, else the in-repo <c>cache/binaries</c> fallback. Shared by <see cref="DefaultOutDir"/>
    /// and any sidecar writer (e.g. <c>PicsAppInfoCapture.WriteRawDump</c>) that needs the root rather
    /// than a specific tuple dir.
    /// </summary>
    internal static string BinariesStoreRoot()
    {
        var binariesRoot = Config.HostConfig.BinariesRoot;
        return string.IsNullOrEmpty(binariesRoot) ? Path.Combine("cache", "binaries") : binariesRoot;
    }

    /// <summary>
    /// gameevents-dedup helper: parse the just-fetched pak01_dir.vpk under
    /// <paramref name="outDir"/> and print one `path crc32` line per `.gameevents` entry to STDOUT
    /// (the directory entry's stored CRC32 — the dedup key). The CRC is read straight from the VPK
    /// directory tree; no chunk bytes are needed (pairs with --dir-only). Fail-loud if the directory
    /// file is absent.
    /// </summary>
    private static void PrintGameEventsCrcs(string outDir)
    {
        // Resolution order matches extract: the trimmed _content/<gid> store
        // (resolved from outDir's manifest-record.json) first, then a co-located pak (the --dir-only
        // path still co-locates the index). This keeps --print-gameevents-crc working after the
        // minimal/full content leg stopped co-locating the pak.
        var dirVpk = Path.Combine(
            outDir, ContentPakSelector.DirectoryFileRelPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(dirVpk) && ContentStore.TryResolveStoreDirVpk(outDir, out var storePath))
        {
            dirVpk = storePath;
        }
        if (!File.Exists(dirVpk))
        {
            Console.Error.WriteLine(
                $"acquire --content --print-gameevents-crc: '{dirVpk}' not found; cannot read .gameevents CRCs.");
            return;
        }
        var archive = VpkArchive.Open(dirVpk);
        // Ordinal-sorted (VpkArchive guarantees Entries order) ⇒ deterministic output.
        foreach (var e in archive.Entries.Where(
                     x => x.FullPath.EndsWith(".gameevents", StringComparison.Ordinal)))
        {
            // path crc32 (hex, 8-digit). Stable, machine-parseable.
            Console.WriteLine($"{e.FullPath} {e.Crc32:x8}");
        }
    }

    /// <summary>
    /// Fail-loud Steam Guard message: tell the operator EXACTLY what one-time
    /// command to run to seed the durable session. Never prints a secret.
    /// </summary>
    private static void PrintGuardSeedInstructions(SteamGuardRequiredException ex)
    {
        var codeSource = ex.Kind switch
        {
            SteamGuardKind.EmailCode => "the code emailed to the account",
            SteamGuardKind.DeviceCode => "the 5-character code from the Steam Mobile Authenticator",
            _ => "the Steam Guard code",
        };
        Console.Error.WriteLine();
        Console.Error.WriteLine("acquire: AUTHENTICATED logon needs a one-time Steam Guard code.");
        Console.Error.WriteLine($"  {ex.Message}");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  ONE-TIME operator action (run once; a durable session is then cached):");
        Console.Error.WriteLine($"    Read {codeSource}, then re-run the SAME command with --guard-code <CODE>");
        Console.Error.WriteLine("    e.g.  acquire --probe --auth --guard-code 1A2B3 --from-manifest <spec> --platform <P>");
        Console.Error.WriteLine("    (or export STEAM_GUARD_CODE=<CODE> for that single run).");
        Console.Error.WriteLine("  After success, cache/steam-session/ holds the refresh token; later runs need NO code.");
    }

    private static void PrintSummary(AcquireResult result)
    {
        Console.Error.WriteLine(
            $"acquire: success. build={result.ResolvedBuildId} files={result.Files.Count} " +
            $"totalBytes={result.TotalBytes:N0} outDir='{result.OutDir}'");
        foreach (var depot in result.Depots)
        {
            Console.Error.WriteLine(
                $"  depot {depot.DepotId} manifest={depot.ManifestId} created={depot.ManifestCreatedUtc}");
        }
    }

    /// <summary>
    /// Best-effort PICS appinfo capture for the forward (current-build) binary acquire. Fetches the
    /// app's CURRENT PICS appinfo via the anonymous acquirer, renders the VERBATIM canonical-JSON
    /// body (PicsAppInfoRenderer), and writes a pics-appinfo-capture.json sidecar into the acquire
    /// output dir (next to manifest-record.json). NON-FATAL: any failure is logged and swallowed —
    /// the binary acquire already succeeded, and the committed pics-appinfo.json is optional. Only
    /// the concrete <see cref="SteamAnonymousAcquirer"/> exposes DumpAppInfoAsync; a test/fake
    /// acquirer (or any other implementation) simply skips the capture.
    /// </summary>
    private static async Task TryCapturePicsAppInfoAsync(
        ISteamAcquirer acquirer, uint appId, string buildId, string outDir)
    {
        if (acquirer is not SteamAnonymousAcquirer anon)
        {
            return;   // capture is only wired for the anonymous current-build path.
        }
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(90));
            var (change, sha, kv) = await anon.DumpAppInfoAsync(appId, cts.Token).ConfigureAwait(false);
            var body = PicsAppInfoRenderer.RenderCanonicalBody(kv);
            var capture = PicsAppInfoCapture.FromFetch(appId, change, sha, body);

            // RAW SAFETY NET FIRST: capture the jsonified PICS response to
            // <binaries-store-root>/_pics/<buildId>.json UNCONDITIONALLY, before the curated
            // (build, platform) sidecar write below — so a fetch that succeeds is preserved even if
            // the curated write fails for some reason (e.g. a permissions issue on outDir).
            var rawDumpPath = Path.Combine(BinariesStoreRoot(), PicsAppInfoCapture.RawDumpDirName, buildId + ".json");
            capture.WriteRawDump(BinariesStoreRoot(), buildId);
            Console.Error.WriteLine($"acquire: captured raw PICS response -> {rawDumpPath}.");

            capture.WriteToCacheDir(outDir);
            Console.Error.WriteLine(
                $"acquire: captured PICS appinfo (change {change}) -> {Path.Combine(outDir, PicsAppInfoCapture.FileName)}.");
        }
        catch (Exception ex)
        {
            // Non-fatal: the binary acquire stands. The committed pics-appinfo.json is optional.
            Console.Error.WriteLine(
                $"acquire: PICS appinfo capture skipped (non-fatal): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"cs2-schema-tracker acquire — fetch CS2 binaries from Steam for one (build, platform).

Usage:
  PICS-current:  cs2-schema-tracker acquire --build <id|latest> --platform <platform> [--out <dir>] [--cache-only | --no-cache] [--binaries-only]
  explicit:      cs2-schema-tracker acquire --from-manifest <spec.json> --platform <platform> [--out <dir>]

UNIFIED ACQUIRE (Gap A): the default acquire fetches BINARIES + the selective CONTENT pak co-located in
ONE output dir, so a single `extract` emits EVERY artifact (entity_schema/convars/commands/... AND
gameevents/item_definitions/game_modes/localization/surface_properties/prop_data/map_overviews) — no
separate `--content` pass, no post-hoc injection. The content leg runs on the forward PICS-current path
for the CURRENT build — whether requested as 'latest' OR as the concrete current build_id by number —
and on any --from-manifest whose spec lists the 2347770 content depot. Pass --binaries-only to SKIP the
content leg (binaries only). `--content` (below) remains for content-only tooling (dir-only / CRC dedup).

SCHEMA-COVERAGE DEFAULT: on windows-x86_64 the unified acquire ALSO fetches the Workshop Tools
editor-DLL slice (depot 2347779) automatically — no --tools flag needed (see --tools / --no-tools
below). It is BEST-EFFORT when default-implied: the tools depot is DLC-gated (needs an authenticated
Steam logon), so an anonymous/no-credentials run logs a note and continues rather than failing an
otherwise-clean acquire. Pass --no-tools to opt out entirely.
  reproduce:     cs2-schema-tracker acquire --from-provenance <provenance.json> --platform <platform> [--out <dir>] [--cache-only | --no-cache]   (re-acquire pinned inputs + SHA-256 verify)
  content:       cs2-schema-tracker acquire --content --build <id|latest> --platform <platform> [--full-pak] [--auth] [--out <dir>]
  content (dir): cs2-schema-tracker acquire --content --dir-only --build <id> --platform <platform> [--print-gameevents-crc] [--out <dir>] (dedup: pak01_dir.vpk index only)
  content (hist):cs2-schema-tracker acquire --content --build <id> --platform <platform> [--out <dir>]               (historical: GID from recorded history; auto --auth)
                 cs2-schema-tracker acquire --content --from-manifest <spec.json> --platform <platform> --auth [--out <dir>]
  binaries-only: cs2-schema-tracker acquire --binaries-only --build <id|latest> --platform <platform> [--auth] [--out <dir>]
                 cs2-schema-tracker acquire --binaries-only --from-manifest <spec.json> --platform <platform> --auth [--out <dir>]
  batch (all):   cs2-schema-tracker acquire --all [--platform <platform>] [--auth] [--force] [--out <root>] [--inventory <file>]
  batch (ids):   cs2-schema-tracker acquire --build <id> --build <id> [...] [--platform <platform>] [--auth] [--force] [--out <root>]

Batch selection (historical binary backfill — replaces scripts/backfill-acquire.ps1):
  --all                 Acquire the loadable binaries for EVERY build in the assets inventory
                        (data/cs2-assets-inventory.json) that has a recorded binary manifest GID
                        for the target platform(s). Each (build, platform) is fetched by its
                        inventory binary manifest (equivalent to a per-build --binaries-only
                        --from-manifest). With no --platform, every platform a build lists is
                        acquired; with --platform <P>, just that one. UNIFIED (Gap A): each build
                        ALSO gets its co-located content pak (from builds[].content) so a single
                        extract emits every artifact; a build with no recorded content GID has
                        content omitted (a note, not a failure). The resume marker is content-aware
                        — re-running --all after a binaries-only batch re-acquires to ADD content.
                        Each windows build ALSO gets the Workshop Tools leg by DEFAULT (schema
                        coverage; see --tools/--no-tools) — best-effort per build unless --tools is
                        explicit, so an anonymous/no-credentials run never marks a build Failed just
                        for missing the DLC-gated tools depot. Pass --binaries-only to skip both the
                        content and tools legs; --no-tools skips tools only.
  --build <id> [...]    Repeating --build engages the same batch over SPECIFIC inventory builds
                        (each id must be a concrete integer recorded in the inventory). A SINGLE
                        --build with no --all is the unchanged single-(build, platform) acquire.
  --force               BATCH: re-acquire a (build, platform) even if its output dir already
                        carries the .acq-done resume marker (default: skip already-done items).
  --inventory <file>    BATCH: assets-inventory path (default: data/cs2-assets-inventory.json).
  Batch behavior: RESUMABLE (a (build, platform) whose output dir holds .acq-done is skipped
  unless --force); CONTINUE-ON-FAILURE (one bad manifest never aborts the run); an end SUMMARY
  reports acquired / skipped / no-manifest / failed counts + the failed ids; the process exits
  non-zero (70) iff any (build, platform) HARD-failed. The batch selection (--all / repeated
  --build) is mutually exclusive with --from-manifest / --from-provenance (those acquire ONE
  pinned set); violating that is a usage error (exit 64).
  With --probe the batch runs a manifest-level reachability check per selected (build, platform)
  and DOWNLOADS NOTHING (same no-bulk-download contract as single-build --probe); a per-build
  PROBE SUMMARY reports fetchable / unreachable / no-manifest counts and the run exits non-zero
  (65) iff any selected build's historical manifest is not reachable.

Arguments:
  --build <id>          Steam build ID, or 'latest' for current public-branch build.
                        Required unless --from-manifest is given. Repeatable for a batch (above).
  --platform <platform> One of: linux-x86_64, windows-x86_64. Always required.
                        CS2 is one app (730); the per-OS binary depot ships BOTH
                        client and server binaries, so there is no separate
                        client/server download — only a platform choice.
  --out <dir>           Output directory. Default: CS2_BINARIES_ROOT/<build_id>/<platform>
                        when the binaries-store root is set (env CS2_BINARIES_ROOT, else
                        appsettings BinariesRoot; env wins) — the SAME location `extract`
                        reads — else cache/binaries/<build_id>/<platform>. An explicit
                        --out always wins over the store root. With --build latest the
                        default path uses the RESOLVED build_id, not the literal 'latest'.
  --from-provenance <p> (redesign): provenance-driven RE-ACQUISITION + verify.
                        Reads a committed provenance.json, re-acquires the EXACT inputs
                        it pins (steam.depots[].manifest_id), then SHA-256-VERIFIES every
                        acquired file against inputs[].sha256. Any mismatch or missing
                        file is fail-loud (exit 65) with a per-file report. This is the
                        ONLY mode that hash-verifies (a bare --build/--platform acquire
                        has no expected hashes). Mutually exclusive with --from-manifest.
                        Honors --cache-only / --no-cache; default is cache-first.
  --cache-only (redesign): resolve ONLY from the local binary cache (the
                        build/platform-keyed dir under CS2_BINARIES_ROOT / --out); never
                        contact Steam. Exit 65 if a required binary is absent. Cannot
                        resolve --build latest (needs a Steam probe for the build_id).
  --no-cache (redesign): skip the local cache; force a FRESH Steam
                        download and refresh the cache on success. Overrides cache-first.
  --from-manifest <f>: explicit-manifest acquisition. JSON spec with
                        {buildId, appId, depots:[{depotId, manifestId}]}. Fetches
                        THOSE exact per-depot manifest GIDs, bypassing PICS-current.
                        This is the only way to re-fetch a SPECIFIC PRIOR build
                        (anonymous PICS exposes only the current manifest). The GIDs
                        must come from our own recorded history (manifest-record.json)
                        and the content must still be CDN-resident.
  --content: acquire the CS2 CONTENT depot (2347770) instead
                        of the per-platform binary depot, for gameevents.json. By
                        default this is a MINIMAL two-phase fetch: Phase A pulls only
                        game/csgo/pak01_dir.vpk, Phase B parses it and fetches
                        only the pak01_<NNN>.vpk chunk(s) backing the .gameevents
                        resources — a small slice of the ~59 GB depot. The pak01 VPK is
                        identical across platforms, but --platform still selects the
                        on-disk output tree so 'extract' finds the VPK under the
                        per-platform binaries dir. The fetched pak01 files are MERGED
                        non-destructively into --out (existing binaries are preserved),
                        so --out may safely point at an already-populated binaries dir.
                        A SPECIFIC (non-'latest') --build is a HISTORICAL manifest:
                        anonymous PICS exposes only the CURRENT 2347770 manifest, so
                        the content depot's manifest GID is resolved from our recorded
                        manifest history (KnownManifestHistory) for that build, OR
                        supplied via --from-manifest <spec> (the spec MUST list the
                        2347770 content depot). This is the gameevents backfill
                        path. It auto-selects --auth when creds exist (anonymous Steam
                        cannot issue a request code for a prior manifest). The 2347770
                        identity is merged into manifest-record.json so extract's
                        provenance lists it (: gameevents.json <-> 2347770).
  --full-pak            With --content: FALLBACK. Fetch the whole game/csgo/pak01_*.vpk
                        set (dir + all chunks) instead of the minimal gameevents slice.
                        Still far smaller than the full content depot.
  --dir-only With --content (gameevents dedup): Phase A ONLY. Fetch
                        game/csgo/pak01_dir.vpk into --out, merge the content depot
                        identity into manifest-record.json, and STOP — no archive
                        chunks. This is the cheap (~7 MB) per-content-manifest index
                        pull used to read .gameevents CRC32s for fileset dedup across
                        the distinct content manifests. --full-pak is ignored when
                        --dir-only is set. (For a runnable gameevents.json you still
                        need a full --content acquire + 'extract'.)
  --print-gameevents-crc  With --content (typically + --dir-only): after the fetch,
                        parse the on-disk pak01_dir.vpk and print one `path crc32`
                        line per .gameevents entry to STDOUT (the dedup key). The
                        CRC32 comes from the VPK directory tree (no chunk bytes
                        needed).
  --binaries-only: skip the co-located CONTENT (+ tools) leg — fetch ONLY the
                        binary depot. The binary depot itself is ALWAYS fetched
                        minimal-footprint (this is the DEFAULT for every acquire mode,
                        not just --binaries-only): only the loadable native binaries the
                        walker loads — every file under the per-OS bin-directory subtrees:
                          windows-x86_64: game/bin/win64/ + game/csgo/bin/win64/
                          linux-x86_64:   game/bin/linuxsteamrt64/ + game/csgo/bin/linuxsteamrt64/
                        The whole subtree is fetched (NOT just *.dll/*.so) so sibling
                        dependency binaries (Qt plugins, subtools, versioned .so.N) are
                        present for LoadLibrary/dlopen. This is ~0.46 GB vs the several-GB
                        full binary depot (shader VPKs, and — as of build ~24442510 —
                        default-installed community/workshop map addons the walker never
                        touches) — the difference between fitting the historical builds on
                        disk and not. Same per-chunk SHA verify / chunk resume / depot-key
                        rotation / manifest-record path as a full acquire. Works with
                        --build OR --from-manifest; a SPECIFIC historical build needs --auth.
  --tools (Workshop Tools): the Workshop Tools depot's (2347779)
                        editor-DLL slice — every manifest file ending '.dll' under
                        'game/' (hammer.dll, toolframework2.dll, …, plus
                        game/csgo/bin/win64/modtools.dll; ~200 MB of the ~2.09 GB
                        depot) — staged and MERGED non-destructively into the SAME
                        per-build windows binaries dir, so the walker can register
                        the editor modules' schema projects. windows-x86_64 ONLY
                        (exit 2 on any other platform; Valve publishes no
                        Linux/mac Workshop Tools).
                        DEFAULT ON for windows (schema coverage) — rides the
                        default unified acquire, a --from-manifest spec listing
                        2347779, or the batch (--all / repeated --build, where each
                        build's inventory builds[].tools GID drives the historical
                        fetch; a build without a tools GID is noted and omitted).
                        --tools is therefore a redundant-but-accepted EXPLICIT
                        request: it keeps a hard fail-loud contract (a fetch
                        failure aborts the acquire) and cannot combine with
                        --binaries-only / --content / --probe / --from-provenance
                        (those modes have no tools leg). The DEFAULT-implied leg
                        (no --tools flag) is instead BEST-EFFORT: the depot is
                        DLC-gated (needs an authenticated logon), so an
                        anonymous/no-credentials session logs a note and the
                        acquire still succeeds. A historical (non-current) build
                        needs --auth for the leg to succeed, exactly like binaries.
                        The 2347779 identity is merged into manifest-record.json
                        like every other depot.
  --no-tools            Opt out of the default Workshop Tools leg on windows
                        entirely (no attempt, no best-effort log line). Mutually
                        exclusive with --tools.
  --auth                Authenticated Steam logon. Anonymous is the
                        DEFAULT and is sufficient for current-build capture; --auth is
                        needed for HISTORICAL manifests (anonymous Steam only issues a
                        request code for the CURRENT manifest). Reads STEAM_USERNAME /
                        STEAM_PASSWORD from the env (or the gitignored repo-root .env).
                        Auto-selected for --from-manifest when those creds are present.
                        After first success a refresh token is cached under
                        cache/steam-session/ (gitignored) for non-interactive reuse.
  --guard-code <code>   One-time Steam Guard code to seed the FIRST authenticated
                        logon (or set STEAM_GUARD_CODE). Not needed once a session is
                        cached. Codes/credentials are never logged.

Every successful acquire (either mode) also writes a deterministic
manifest-record.json into the output directory — the seed of our re-fetchable
manifest history.

behavior:
  - Anonymous depot access (CS2 is free-to-play; no Steam credentials).
  - Verifies bytes against the manifest's content hashes.
  - Supports resume after interruption (chunks already on disk are skipped if
    their SHA-1 matches the manifest).
  - Handles depot-key rotation (one retry on AccessDenied / Expired).
  - Exits non-zero on any verification failure. On failure the
    .partial directory is left in place for forensic inspection but no
    final output directory is created.");
    }
}
