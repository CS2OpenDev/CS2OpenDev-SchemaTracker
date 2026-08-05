//
// Full extraction pipeline, wired end-to-end against the walker seam:
//   resolve input binaries dir -> resolve the build's era to its archived per-era walker binary
//   (EraWalkerResolver) -> run that walker subprocess (one process per (build, platform)) with the
//   resolved era's expected layout signature arming the post-load second gate -> read the binary
//   WalkerOutput -> run every emitter (entity_schema, convars, commands, engine_constants, modules,
//   string_pools, network_messages, demo_messages, registry_audit, content artifacts, provenance)
//   into a staging dir -> atomic stage->promote to the out dir (off-repo by default;
//   artifacts/<build>/<platform>/ under --commit).
//
// The internal Run(..) overloads expose a FAKE IWalkerRunner as a TEST-ONLY seam so the
// orchestration can be exercised without a built walker; production always goes through the real
// per-era binary (runnerFactory null, eraResolver supplied).
//
// The undocumented --binaries <dir> hook below runs ONLY the descriptor extractor over a directory
// of input binaries; it's a development hook, not a public API.

using System.Security.Cryptography;

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cache;
using Cs2SchemaTracker.Host.Commands;
using Cs2SchemaTracker.Host.ConVars;
using Cs2SchemaTracker.Host.EngineConstants;
using Cs2SchemaTracker.Host.EntitySchema;
using Cs2SchemaTracker.Host.GameEvents;
using Cs2SchemaTracker.Host.GameModes;
using Cs2SchemaTracker.Host.Items;
using Cs2SchemaTracker.Host.Localization;
using Cs2SchemaTracker.Host.MapOverviews;
using Cs2SchemaTracker.Host.Modules;
using Cs2SchemaTracker.Host.NetworkMessages;
using Cs2SchemaTracker.Host.PropData;
using Cs2SchemaTracker.Host.ProtoDescriptors;
using Cs2SchemaTracker.Host.Provenance;
using Cs2SchemaTracker.Host.RegistryAudit;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.StringPools;
using Cs2SchemaTracker.Host.SurfaceProperties;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static partial class ExtractCommand
{
    /// <summary>
    /// Environment variable that overrides the acquired-binaries store root. Lets the store
    /// live off the repo volume (e.g. /data/cs2-binaries); the convention is
    /// <c>&lt;root&gt;/&lt;build&gt;/&lt;platform&gt;/</c>. Resolved via <see cref="Config.HostConfig.BinariesRoot"/>
    /// (env wins over appsettings).
    /// </summary>
    public const string BinariesRootEnvVar = "CS2_BINARIES_ROOT";

    public static int Run(string[] args)
        // Production entry: per-era walker selection. Resolves the build's era -> archived walker
        // binary (EraWalkerResolver over data/cs2-assets-inventory.json eras[] + builds[].era),
        // launches THAT binary, and arms the post-load second gate with the resolved era's expected
        // layout signature. Tests call the runnerFactory overload with a fake (no era resolution,
        // gate disabled).
        //
        // Auto-acquire: the production path injects the real acquirer so an extract whose input
        // binaries are absent fetches them in-process before walking (opt out with --no-acquire).
        // An injectable seam so tests NEVER touch real Steam, and gated on the production runner (a
        // fake runner never auto-acquires). Schema-coverage default: since AcquireCommand's default
        // unified acquire now fetches the Workshop Tools editor-DLL slice automatically on windows
        // (best-effort, no --tools flag needed), this seam gets it "for free" — no extra arg to pass.
        // repoRoot: CS2_REPO_ROOT (via HostConfig) when set — for a host binary living OUTSIDE the
        // repo tree (e.g. a container image with the repo bind-mounted) — else null, which keeps the
        // default DiscoverRepoRoot() walk-up-from-the-exe sentinel search unchanged.
        => Run(args, runnerFactory: null,
               eraResolver: new EraWalkerResolver(Cs2SchemaTracker.Host.Config.HostConfig.RepoRoot),
               gateFromResolver: false,
               acquire: static (b, p) => AcquireCommand.Run(new[] { "--build", b, "--platform", p }));

    /// <summary>
    /// Test seam: lets the suite inject a fake <see cref="IWalkerRunner"/> so the orchestration can
    /// be exercised without a built walker. The factory is only invoked after argument validation
    /// and the cross-OS guard pass. No era resolution and no post-load second gate run on this path.
    /// </summary>
    internal static int Run(string[] args, Func<IWalkerRunner> runnerFactory)
        => Run(args, runnerFactory, eraResolver: null, gateFromResolver: false);

    /// <summary>
    /// Test seam for the post-load second gate: inject a FAKE runner (no built walker) AND a
    /// fixture-rooted <see cref="EraWalkerResolver"/>. The resolver supplies ONLY the era's expected
    /// layout signature that arms the gate; the fake runner — not the resolved per-era binary —
    /// produces the WalkerOutput. Lets the suite exercise the signature-match / -mismatch paths over
    /// a fake index without a real exe.
    /// </summary>
    internal static int Run(string[] args, Func<IWalkerRunner> runnerFactory, EraWalkerResolver eraResolver)
        => Run(args, runnerFactory, eraResolver, gateFromResolver: true);

    /// <summary>
    /// Shared entry. <paramref name="runnerFactory"/> (test seam — fake runner) and/or
    /// <paramref name="eraResolver"/> (production — per-era binary + armed second gate) drive
    /// which runner runs and whether the gate is armed:
    ///   - runnerFactory only          : fake runner, gate OFF (orchestration suite).
    ///   - eraResolver only            : production — resolve per-era binary + arm gate.
    ///   - both (gateFromResolver=true): fake runner, gate armed from the resolver (gate suite).
    /// The full-extract path resolves the era only AFTER the cross-OS guard + binaries-dir check, so
    /// the inventory/era catalog is never required for the dev-hook mode.
    /// </summary>
    /// <param name="acquire">
    /// Auto-acquire seam (build, platform) -> exit code, invoked by RunExtract when the input
    /// binaries are absent on the PRODUCTION path. Null on the test seams so auto-acquire never
    /// fires (and even when supplied it is gated on a null runnerFactory).
    /// </param>
    private static int Run(
        string[] args, Func<IWalkerRunner>? runnerFactory, EraWalkerResolver? eraResolver, bool gateFromResolver,
        Func<string, string, int>? acquire = null)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            PrintHelp();
            return 0;
        }

        var parsed = CliArgs.Parse(args);

        // Internal development hook: --binaries <dir> --out <dir>
        // Runs only the descriptor extractor over every *.so / *.dll beneath <dir>. Not part of the
        // public surface; for development.
        if (parsed.TryGetValue("binaries", out var binariesHook))
        {
            if (!parsed.TryGetValue("out", out var outHook))
            {
                Console.Error.WriteLine("extract --binaries: --out <dir> is required.");
                return 64;
            }
            return RunDescriptorsOnly(binariesHook, outHook);
        }

        // Main extraction path: forward-extract one or more --build ids, OR batch over the
        // inventory builds selected by --all / --era / --pin. Selection parsing, the era-aware
        // class-count gate, --verify classification, --commit promotion (+ build-level hooks),
        // and fail-isolation all live in the RunSelection orchestration (ExtractCommand.Batch). The
        // auto-acquire seam threads through to RunExtract.
        return RunSelection(args, runnerFactory, eraResolver, gateFromResolver, acquire);
    }

    /// <summary>Default artifact directory per README.md: artifacts/&lt;build&gt;/&lt;platform&gt;/.</summary>
    internal static string DefaultOutDir(string build, string platform)
        => Path.Combine("artifacts", build, platform);

    /// <summary>
    /// Full per-platform orchestration for one (build, platform): resolve binaries -> walker ->
    /// run every applicable emitter (content emitters only when a content-depot VPK is present) into
    /// a STAGING dir, then atomically promote the whole set into <paramref name="outDir"/>.
    /// All-or-nothing: nothing lands unless every emitter succeeds — on any walker failure or emitter
    /// throw the staging dir is discarded, a pre-existing <paramref name="outDir"/> is left untouched,
    /// and the call exits non-zero.
    /// </summary>
    /// <param name="runnerFactory">
    /// Test seam: when supplied, this fake <see cref="IWalkerRunner"/> is used and no era resolution
    /// / no post-load second gate runs. Mutually exclusive with <paramref name="eraResolver"/>.
    /// </param>
    /// <param name="eraResolver">
    /// Production: when supplied, RunExtract resolves the per-era walker binary + that era's expected
    /// layout signature AFTER the binaries-dir check, builds the real
    /// <see cref="WalkerProcessRunner"/> for that binary, and arms the post-load second gate with
    /// the expected signature.
    /// </param>
    /// <param name="noAcquire">
    /// Opt out of auto-acquire (--no-acquire): when the input binaries are absent, restore the
    /// fail-loud-with-guidance behavior even on the production path.
    /// </param>
    /// <param name="acquire">
    /// Auto-acquire seam (build, platform) -> exit code. Invoked ONLY on the production path
    /// (runnerFactory null + eraResolver supplied) when the input binaries are absent and not
    /// --no-acquire. Never invoked on the fake-runner test seam — a hard invariant so tests never
    /// trigger a real Steam download.
    /// </param>
    /// <param name="noChangelog">
    /// Opt out of the inline changelog (--no-changelog): skip emitting changelog.json into the
    /// staged set even when a committed predecessor exists. Default OFF (the changelog IS produced).
    /// The floor build (no predecessor) never emits one regardless of this flag.
    /// </param>
    /// <param name="noLocalizationChangelog">
    /// Opt out of ONLY the content-derived localization changelog family (--no-localization-changelog):
    /// still emit the five binary families, but do NOT regenerate the predecessor's localization to
    /// diff it. For the forward-capture path (anonymous / ephemeral runners), where the predecessor
    /// build's content is not re-acquirable (anonymous Steam is current-build only). Default OFF (the
    /// localization family IS produced when both builds carry localization). This build's own
    /// localization.json is still produced + fingerprinted into provenance.localization either way.
    /// </param>
    /// <param name="doubleWalk">
    /// COMMIT-PATH DETERMINISM GATE: when true, the walker is run a SECOND time (same args) into a
    /// second temp file, and the two outputs are byte-compared BEFORE anything is parsed/staged. Any
    /// divergence aborts at exit 76 with NO artifacts written (see the "DETERMINISM GATE" block
    /// below). Only ever actually double-walks on the PRODUCTION runner (<paramref name="runnerFactory"/>
    /// null) — mirrors how <c>layoutGateArmed</c> is gated on the same condition, so the fake-runner
    /// test seam is always effectively single-walk regardless of this flag. The batch layer defaults
    /// this to true under --commit (false otherwise); --single-walk forces it off.
    /// </param>
    /// <param name="classCountGate">
    /// The era-aware entity_schema class-count sanity gate (--no-gate turns this OFF; on by
    /// default). Evaluated INSIDE RunExtract, AFTER EmitFullSet and BEFORE the no-clobber
    /// check/promote, so a violation aborts at exit 77 with NOTHING written — under --commit OR
    /// off-repo alike (see the "BLOCKING CLASS-BAND GATE" block below). Requires
    /// <paramref name="eraResolver"/> to resolve the band; a null resolver leaves the gate a no-op
    /// regardless of this flag (nothing to gate against).
    /// </param>
    internal static int RunExtract(
        string build, string platform, string outDir,
        Func<IWalkerRunner>? runnerFactory, EraWalkerResolver? eraResolver, bool gateFromResolver,
        bool noAcquire = false, Func<string, string, int>? acquire = null, bool noChangelog = false,
        bool noLocalizationChangelog = false, bool doubleWalk = false, bool classCountGate = true)
    {
        outDir = Path.GetFullPath(outDir);

        // 1. Resolve the input binaries directory.
        if (!TryResolveBinariesDir(build, platform, out var binariesDir, out var resolveError))
        {
            // Auto-acquire on the PRODUCTION path only (null runnerFactory with an eraResolver):
            // acquire the binaries in-process and re-resolve. The fake-runner test seam MUST NEVER
            // auto-acquire — a hard invariant so tests never trigger a real Steam download.
            // --no-acquire (or a null acquire seam) restores the fail-loud-with-guidance behavior.
            bool autoAcquire = runnerFactory is null && eraResolver is not null && !noAcquire && acquire is not null;
            if (!autoAcquire)
            {
                Console.Error.WriteLine(resolveError);
                return 65;   // EX_DATAERR — required input not present (auto-acquire disabled).
            }

            Console.Error.WriteLine(
                $"extract: input binaries absent for (build {build}, {platform}) — acquiring...");
            int acquireExit = acquire!(build, platform);
            if (acquireExit != 0)
            {
                // A failed acquire writes NO artifacts and surfaces the acquire's exit code
                // verbatim. Nothing partial.
                Console.Error.WriteLine(
                    $"extract: acquire FAILED (exit {acquireExit}) for (build {build}, {platform}). " +
                    "No artifacts written.");
                return acquireExit;
            }

            // Re-resolve AFTER a successful acquire. A still-absent dir is fail-loud: the acquire
            // claimed success but produced nothing usable — never proceed to a silent walk.
            if (!TryResolveBinariesDir(build, platform, out binariesDir, out resolveError))
            {
                Console.Error.WriteLine(resolveError);
                Console.Error.WriteLine(
                    "extract: acquire reported success but the input binaries are still not resolvable. " +
                    "No artifacts written.");
                return 65;   // EX_DATAERR
            }
        }

        // 1a. AT-USE input verification. IF a committed artifacts/<build>/<platform>/provenance.json
        //     exists for this (build, platform) — the re-extract / reproduce case — hash each
        //     RESOLVED input the walker is about to read and compare to that provenance's
        //     inputs[].sha256. A binary modified or corrupted between acquisition and use is caught
        //     HERE, before any walk. On a FRESH extract with no pre-existing provenance there is
        //     nothing to compare (the walk PRODUCES it) — skip. InputBinaryVerifier streams each
        //     listed input once, in path-ordinal order.
        if (!VerifyAgainstCommittedProvenance(build, platform, binariesDir))
        {
            return 65;   // EX_DATAERR — input bytes don't match the committed provenance.
        }

        // 1b. Resolve the per-era walker binary + the era's expected layout signature. Done AFTER
        //     the binaries-dir check so a binaries-not-found run still fails loud at EX_DATAERR
        //     before the inventory/era catalog is required. Any era-resolution failure (no
        //     archived binary for the era, unknown pin, corrupt manifest/index) aborts at exit 75 —
        //     before any walk or artifact byte. The fake test seam (runnerFactory) skips this; its
        //     gate stays disabled.
        //     Sibling gates further down this method reuse the same "abort before any artifact byte,
        //     nothing written" contract with their own codes: exit 76 = the commit-path DETERMINISM
        //     GATE (two back-to-back walks produced byte-differing output), exit 77 = the BLOCKING
        //     CLASS-BAND GATE (the parsed walk's class count falls outside the resolved era's band).
        //     75/76/77 are all "this build cannot be trusted as extracted" — never a walker crash
        //     (65/70), which is a distinct failure class. Exit 78 = the WALKER IDENTITY GATE
        //     (ExtractCommand.Batch.cs PreflightWalkerIdentity) — same failure class, but evaluated
        //     ONCE for the whole selection BEFORE this method is ever called for build 1 (a
        //     mixed/stale walker SET is a property of the run, not of any one build's walk).
        IWalkerRunner runner;
        string? expectedLayoutSignature = null;
        // Whether the second gate is ARMED for this run. Distinct from expectedLayoutSignature being
        // null: the gate is DISABLED only on the fake-runner test seam (below). When ARMED, a null
        // expected signature means the resolved era has no validated layout signature for THIS
        // platform — a fail-loud unvalidated layout, NOT a pass.
        bool layoutGateArmed = false;

        // Resolve the era when a resolver is supplied (production, OR the gate test seam where a
        // fake runner is also supplied). The resolution provides the era's expected signature
        // (arms the second gate) and — when no fake runner is injected — the per-era binary.
        EraResolution? resolution = null;
        if (eraResolver is not null)
        {
            try
            {
                resolution = eraResolver.Resolve(build, platform);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"extract: could not resolve a per-era walker for (build {build}, {platform}): " +
                    $"{ex.Message}");
                return 75;   // EX_PROTOCOL — era/layout-resolution failure (matches the walker self-gate).
            }
            Console.Error.WriteLine(
                $"extract: era='{resolution.Era}' pin={resolution.Pin} walker='{resolution.WalkerBinaryPath}'" +
                (resolution.FromExplicitOverride ? " (CS2_WALKER_BIN override)" : ""));
            // Arm the gate from the resolver in production, and in the gate test seam.
            if (gateFromResolver || runnerFactory is null)
            {
                layoutGateArmed = true;
                expectedLayoutSignature = resolution.ExpectedLayoutSignature;   // per-platform; may be null.
            }
        }

        // Walker identity chain: resolve the ACTUAL walker binary's self-reported identity (its
        // `--version` output — git SHA + src-fingerprint) so provenance.tool.walkerGitSha /
        // walkerSrcFingerprint record what genuinely ran, not what the operator assumed was built.
        // Production-only (mirrors layoutGateArmed / doWalkTwice): the fake-runner test seam launches
        // no real binary, so there is nothing to identify — the fields stay "" (see BuildProvenanceContext).
        // <1s (no CS2 binaries touched) and memoized per binary path (WalkerIdentity.Resolve), so a
        // batch over many builds sharing one era's binary pays this cost once.
        WalkerIdentity? walkerIdentity = null;
        if (runnerFactory is null && resolution is not null)
        {
            try
            {
                walkerIdentity = WalkerIdentity.Resolve(resolution.WalkerBinaryPath);
            }
            catch (Exception ex)
            {
                // Never block the extract on an identity-resolution hiccup — the walk itself is the
                // authoritative proof the binary works. But never guess either: leave the identity
                // fields empty and say loudly why, so a silently-empty provenance stamp is never
                // mistaken for "the walker reported nothing" (: fail-loud, but the walk is
                // the primary gate; identity is a defense-in-depth record on top of it).
                Console.Error.WriteLine(
                    $"extract: WARNING could not resolve walker identity for " +
                    $"'{resolution.WalkerBinaryPath}': {ex.GetType().Name}: {ex.Message}. " +
                    "provenance.tool.walkerGitSha/walkerSrcFingerprint will be empty.");
            }
        }

        if (runnerFactory is not null)
        {
            // Test seam: the fake runner produces the WalkerOutput (not the resolved binary).
            runner = runnerFactory();
        }
        else
        {
            // Gate off KV3 class-defaults recovery for eras whose accessor ABI is not validated
            // (resolution.Kv3ClassDefaults == false): the walker emits empty MGetKV3ClassDefaults
            // there (deferred-with-reason) instead of calling an ABI-invalid accessor that recovers
            // nothing on windows and CRASHES on linux. See InventoryEra.Kv3ClassDefaults.
            runner = new WalkerProcessRunner(
                resolution!.WalkerBinaryPath, disableKv3Defaults: !resolution.Kv3ClassDefaults);
        }

        // Whether this run ACTUALLY double-walks for the determinism gate: doubleWalk is what the
        // caller asked for, but — mirroring layoutGateArmed above — only the PRODUCTION runner
        // (runnerFactory null) ever really does it. The fake-runner test seam stays single-walk
        // regardless, so the orchestration suite never pays for (or has to fake) a second walk.
        bool doWalkTwice = doubleWalk && runnerFactory is null;

        // 2. Run the walker subprocess into a temp file. The walker output is an INTERMEDIATE, kept
        //    out of the artifact dir; the public artifact set is only materialized after a clean
        //    walk + clean emit of the WHOLE set.
        var walkTmp = Path.Combine(
            Path.GetTempPath(),
            $"cs2-walk-{Guid.NewGuid():N}.pb");   // Guid in a TEMP filename only — never in output.

        // Second walk for the determinism gate (only ever WRITTEN when doWalkTwice is true, but the
        // path is always allocated so the finally block's cleanup is unconditional and uniform with
        // walkTmp's).
        var walkTmp2 = Path.Combine(
            Path.GetTempPath(),
            $"cs2-walk2-{Guid.NewGuid():N}.pb");

        // Staging dir: every emitter writes here; the set is promoted to outDir only after all of
        // them succeed. A sibling of outDir keeps the final move on one volume.
        var stagingDir = outDir + ".staging-" + Guid.NewGuid().ToString("N");

        try
        {
            Console.Error.WriteLine(
                $"extract: walking build={build} platform={platform} binaries='{binariesDir}'");
            int walkerExit = runner.Run(binariesDir, platform, walkTmp, out var walkerStderr);

            if (walkerExit != 0)
            {
                // Any walker failure — including exit 75, the unknown-layout rejection — aborts the
                // extract with NO artifact bytes. Surface the walker's stderr verbatim so the layout
                // signature or load-failure reason reaches the operator.
                Console.Error.WriteLine(
                    $"extract: walker exited {walkerExit} for platform '{platform}'. No artifacts written.");
                if (!string.IsNullOrWhiteSpace(walkerStderr))
                {
                    Console.Error.WriteLine("--- walker stderr ---");
                    Console.Error.Write(walkerStderr);
                    if (!walkerStderr.EndsWith('\n'))
                        Console.Error.WriteLine();
                    Console.Error.WriteLine("---------------------");
                }
                return walkerExit;
            }

            if (!File.Exists(walkTmp))
            {
                // The walker reported success but produced no output: a contract violation, not
                // something to paper over.
                Console.Error.WriteLine(
                    $"extract: walker reported success but wrote no output to '{walkTmp}'. " +
                    "Aborting (contract violation).");
                return 70;   // EX_SOFTWARE
            }

            byte[] walkBytes = File.ReadAllBytes(walkTmp);

            // DETERMINISM GATE (commit-path only — see doubleWalk's doc comment). Re-run the SAME
            // walker binary against the SAME (binariesDir, platform) inputs into a second temp file
            // and byte-compare against the first. Any divergence means the walker's output depends on
            // something other than its declared inputs — uninitialized memory, pointer-derived
            // ordering, a timing-sensitive recovery path, etc. — and MUST NOT enter the corpus (this
            // is exactly how nondeterministic garbage got committed before: incident #9). The cost of
            // a second walk (~seconds) is only paid when it matters (committing).
            if (doWalkTwice)
            {
                Console.Error.WriteLine(
                    $"extract: determinism gate — walking a second time for (build {build}, {platform})...");
                int walkerExit2 = runner.Run(binariesDir, platform, walkTmp2, out var walkerStderr2);
                if (walkerExit2 != 0)
                {
                    // The second walk didn't even run cleanly — not a byte-mismatch, but still
                    // fail-loud (never trust a walk we can't reproduce). Mirrors the first walk's own
                    // failure handling above; exit 76 is reserved specifically for a CONFIRMED
                    // byte-level mismatch between two successful walks (below).
                    Console.Error.WriteLine(
                        $"extract: walker exited {walkerExit2} on the determinism gate's SECOND walk " +
                        $"for platform '{platform}'. No artifacts written.");
                    if (!string.IsNullOrWhiteSpace(walkerStderr2))
                    {
                        Console.Error.WriteLine("--- walker stderr (second walk) ---");
                        Console.Error.Write(walkerStderr2);
                        if (!walkerStderr2.EndsWith('\n'))
                            Console.Error.WriteLine();
                        Console.Error.WriteLine("---------------------");
                    }
                    return walkerExit2;
                }
                if (!File.Exists(walkTmp2))
                {
                    Console.Error.WriteLine(
                        $"extract: walker reported success but wrote no output to '{walkTmp2}' on the " +
                        "determinism gate's SECOND walk. Aborting (contract violation).");
                    return 70;   // EX_SOFTWARE
                }

                byte[] walkBytes2 = File.ReadAllBytes(walkTmp2);
                if (!BytesEqual(walkBytes, walkBytes2, out long firstDiffOffset))
                {
                    Console.Error.WriteLine(
                        $"extract: DETERMINISM GATE FAILED for (build {build}, {platform}): two walks " +
                        $"differ (sizes {walkBytes.Length}/{walkBytes2.Length}, first diff at byte " +
                        $"{firstDiffOffset}). No artifacts written.");
                    return 76;   // new — see the exit-75 comment block above for the sibling codes.
                }
                Console.Error.WriteLine("extract: determinism gate OK — two walks byte-identical.");
            }

            // Parse the walk ONCE; every emitter lifts its sub-message from this instance. Always
            // from the FIRST walk's bytes — the second walk (when performed) exists only to PROVE
            // determinism, never to supply data.
            WalkerOutput walk = WalkerOutput.Parser.ParseFrom(walkBytes);

            // SECOND GATE. Defense-in-depth on top of the walker's own exit-75 self-gate: assert
            // the EMITTED layout signature equals the resolved era's EXPECTED signature, BEFORE any
            // artifact byte is staged. A wrong/stale per-era binary (e.g. a current-pin exe
            // mislabeled as an older era) emits a signature that doesn't match the era's expectation
            // -> abort non-zero, no artifacts. (Disabled only on the fake-runner test seam, where
            // layoutGateArmed stays false.)
            if (layoutGateArmed)
            {
                var emitted = walk.SchemaSystemLayoutSignature ?? "";
                if (expectedLayoutSignature is null)
                {
                    // The resolved era has NO registered/validated layout signature for THIS platform
                    // (e.g. a linux-x86_64 build in an era whose linux layout is not yet validated).
                    // Never accept an unvalidated layout — fail loud, no artifacts.
                    Console.Error.WriteLine(
                        $"extract: second gate FAILED for (build {build}, {platform}). " +
                        $"The resolved era ('{resolution!.Era}', pin {resolution.Pin}) has NO validated " +
                        $"layout signature for platform '{platform}'. No artifacts written.");
                    Console.Error.WriteLine($"  emitted:  {(emitted.Length == 0 ? "<empty>" : emitted)}");
                    Console.Error.WriteLine(
                        $"Register the validated '{platform}' signature for this era in " +
                        "data/cs2-assets-inventory.json (eras[].layoutSignatures) + " +
                        "walker/src/layout_probe.cpp kKnownLayoutSignatures once a walk on a matching-OS " +
                        "host is validated end-to-end (: never guess an unvalidated layout).");
                    return 75;   // EX_PROTOCOL — unregistered-platform layout.
                }
                if (!string.Equals(emitted, expectedLayoutSignature, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        $"extract: second gate FAILED for (build {build}, {platform}). " +
                        "The selected per-era walker emitted a layout signature that does not match " +
                        "the resolved era's expected signature. No artifacts written.");
                    Console.Error.WriteLine($"  expected: {expectedLayoutSignature}");
                    Console.Error.WriteLine($"  emitted:  {(emitted.Length == 0 ? "<empty>" : emitted)}");
                    Console.Error.WriteLine(
                        "This means the wrong era binary ran for this build, or the build is a NEW " +
                        "layout that needs a new era pin + era-binary build (: never guess).");
                    return 75;   // EX_PROTOCOL — layout mismatch (matches the walker self-gate).
                }
            }

            // Stage the full artifact set. A clean staging dir each run (Guid suffix); any
            // pre-existing collision (impossible in practice) is removed first.
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            Directory.CreateDirectory(stagingDir);

            var contentOmissions = EmitFullSet(build, platform, binariesDir, walk, stagingDir, walkerIdentity);

            // BLOCKING CLASS-BAND GATE. Defense-in-depth alongside the layout-signature SECOND GATE
            // above: evaluate the resolved era's class-count band BEFORE the no-clobber check /
            // promote, so a violation aborts here with NOTHING written. This used to be a POST-HOC,
            // NON-BLOCKING check that ran only in the batch layer AFTER RunExtract had already
            // promoted the set under --commit ("promoted anyway for review") — that let out-of-band
            // garbage into the committed corpus (incident #9). Moving the enforcement here makes
            // "Gated but promoted" structurally impossible: it no longer matters whether the caller is
            // committing or off-repo, the count is checked before ANY artifact byte lands anywhere.
            // classCountGate is the --no-gate opt-out; eraResolver is required to resolve a band at
            // all (a null resolver leaves this a no-op, same as the layout gate's own arming).
            if (classCountGate && eraResolver is not null)
            {
                EffectiveClassBand effective = eraResolver.DetermineEffectiveClassBand(build, platform);
                if (effective.MinClasses is not null || effective.MaxClasses is not null)
                {
                    // Same count source the (now-simplified) batch-layer display uses: the classes in
                    // the freshly-STAGED entity_schema.json — a pure function of the parsed walk.
                    int count = CountClasses(Path.Combine(stagingDir, "entity_schema.json"));
                    bool tooFew = effective.MinClasses is int min && count < min;
                    bool tooMany = effective.MaxClasses is int max && count > max;
                    if (tooFew || tooMany)
                    {
                        var band =
                            $"[{effective.MinClasses?.ToString() ?? "-"},{effective.MaxClasses?.ToString() ?? "-"}]";
                        Console.Error.WriteLine(
                            $"extract: CLASS-BAND GATE FAILED for (build {build}, {platform}): class " +
                            $"count {count} outside era '{effective.Era}' band {band}. No artifacts written.");
                        return 77;   // new — see the exit-75 comment block above for the sibling codes.
                    }
                }
            }

            // 3a. NO-CLOBBER content protection. BEFORE the promote deletes a pre-existing outDir,
            //     refuse to destroy a content artifact the existing set HAS that the freshly-staged
            //     set OMITS. This guards committed backfilled content (gameevents.json /
            //     item_definitions.json / …): a binaries-only acquire that did NOT co-locate the
            //     content depot would otherwise clobber it away on a re-walk. A build where NEITHER
            //     side has the artifact is fine (a documented omission). Fail loud (NO promote, the
            //     existing set left intact) when staging would lose content.
            if (Directory.Exists(outDir))
            {
                var clobbered = ContentArtifactNames
                    .Where(n => File.Exists(Path.Combine(outDir, n)) &&
                                !File.Exists(Path.Combine(stagingDir, n)))
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList();
                if (clobbered.Count > 0)
                {
                    Console.Error.WriteLine(
                        $"extract: REFUSING to promote for (build {build}, {platform}): the freshly-walked " +
                        "set omits content artifact(s) the committed set already carries — " +
                        string.Join(", ", clobbered) + ".");
                    Console.Error.WriteLine(
                        "extract: the content depot was not co-located for this build, so the walk would " +
                        "DESTROY committed content. Acquire the content depot (pak01_dir.vpk + chunks) " +
                        "alongside the binaries, then re-run. No artifacts written.");
                    return 65;   // EX_DATAERR — refuse the content-less clobber.
                }
            }

            // 3b. Inline changelog. Diff THIS build's freshly-staged set (the `to` side — stagingDir
            //     always carries the 5 binary-derived families) against its immediate committed
            //     predecessor's set (the `from` side), writing changelog.json INTO stagingDir so it
            //     is promoted atomically with the rest. The predecessor is resolved by the shared
            //     rule (ChangelogPredecessor.Resolve) that verify-artifacts' gate uses, so extract's
            //     own output always satisfies that gate. On the FLOOR build (no committed
            //     predecessor) nothing is emitted — the file is correctly absent. An
            //     incomplete/unparseable predecessor set throws out of the emitter into the outer
            //     catch (exit 65, nothing promoted) — never a swallowed partial. Opt out:
            //     --no-changelog.
            //
            //     The 6th `localization` family is build-on-demand: the `to` side is THIS build's
            //     freshly-staged localization.json (present iff a content depot was co-located this
            //     dump); the `from` side must be REGENERATED from the predecessor's content, because
            //     the committed predecessor set no longer carries localization.json. It is emitted
            //     iff BOTH builds produced localization (else the changelog stays 5 families). If the
            //     predecessor produced localization but its content is NOT resolvable, we fail loud —
            //     consistent with the unparseable-predecessor-set behavior (exit 65, nothing promoted).
            if (!noChangelog)
            {
                var artifactsRoot = Path.GetDirectoryName(Path.GetDirectoryName(outDir));
                if (!string.IsNullOrEmpty(artifactsRoot))
                {
                    var predecessor = Changelog.ChangelogPredecessor.Resolve(artifactsRoot, build, platform);
                    if (predecessor is not null)
                    {
                        var stagedLocalization = Path.Combine(stagingDir, ArtifactSet.LocalizationFileName);
                        string? predecessorLocalizationTemp = null;
                        string? fromLoc = null;
                        string? toLoc = null;
                        try
                        {
                            var wouldEmitLocalizationFamily =
                                File.Exists(stagedLocalization)
                                && PredecessorProducedLocalization(artifactsRoot, predecessor, platform);
                            if (wouldEmitLocalizationFamily && noLocalizationChangelog)
                            {
                                // Forward-capture opt-out: the localization family's `from` side would
                                // need the PREDECESSOR build's content regenerated, which an anonymous /
                                // ephemeral runner cannot re-acquire (anonymous Steam is current-build
                                // only). Emit the five binary families only — a contract-valid shape,
                                // the same one a content-less build already produces. This build's own
                                // localization is still produced + fingerprinted (dropped below).
                                Console.Error.WriteLine(
                                    "extract: --no-localization-changelog set — skipping the localization "
                                    + $"changelog family (predecessor '{predecessor}' content is not "
                                    + "re-acquirable here). Emitting the five binary families only.");
                            }
                            else if (wouldEmitLocalizationFamily)
                            {
                                if (!TryResolveContentVpk(predecessor, platform, out var predVpk, out var predErr))
                                {
                                    throw new InvalidDataException(
                                        $"cannot regenerate predecessor '{predecessor}' localization for the " +
                                        $"changelog localization family — {predErr}");
                                }
                                predecessorLocalizationTemp = Path.Combine(
                                    Path.GetTempPath(), $"cs2-loc-pred-{Guid.NewGuid():N}.json");
                                new LocalizationEmitter(SchemaFamily.Version, predecessor, platform)
                                    .EmitFromVpk(predVpk, predecessorLocalizationTemp);
                                fromLoc = predecessorLocalizationTemp;
                                toLoc = stagedLocalization;
                            }

                            Console.Error.WriteLine(
                                $"extract: wrote changelog.json ({predecessor} -> {build})"
                                + (toLoc is not null ? " (with localization family)" : ""));
                            new Changelog.BuildChangelogEmitter(SchemaFamily.Version, platform, predecessor, build)
                                .Emit(
                                    fromSetDir: Path.Combine(artifactsRoot, predecessor, platform),
                                    toSetDir: stagingDir,
                                    outputPath: Path.Combine(stagingDir, ArtifactSet.ChangelogFileName),
                                    fromLocalizationPath: fromLoc,
                                    toLocalizationPath: toLoc);
                        }
                        finally
                        {
                            if (predecessorLocalizationTemp is not null
                                && File.Exists(predecessorLocalizationTemp))
                            {
                                try
                                { File.Delete(predecessorLocalizationTemp); }
                                catch { /* best effort */ }
                            }
                        }
                    }
                }
            }

            // 3c. Remove the build-on-demand localization.json from the staged set BEFORE the promote.
            //     It was produced (so extraction + determinism stay exercised), fingerprinted into
            //     provenance.localization, and diffed into the changelog above — but it is NOT
            //     committed (at ~199 MB/set it is 96% of the tree). A consumer regenerates it on
            //     demand via `emit-localization` and verifies it byte-for-byte against the fingerprint.
            var stagedLocalizationToDrop = Path.Combine(stagingDir, ArtifactSet.LocalizationFileName);
            if (File.Exists(stagedLocalizationToDrop))
            {
                File.Delete(stagedLocalizationToDrop);
                Console.Error.WriteLine(
                    "extract: localization.json produced + fingerprinted (provenance.localization) but "
                    + "NOT committed (build-on-demand); removed from the staged set before promote.");
            }

            // 4. Promote: atomically replace outDir with the fully-staged set via the TWO-STEP
            //    promote below — no PARTIAL set is ever promoted (every file in staging was
            //    written before this point), and — unlike the old delete-then-move, which had a crash
            //    window where NEITHER the old nor the new set existed — a crash mid-promote now always
            //    leaves SOME complete set on disk (old or new).
            PromoteStagingDir(stagingDir, outDir);

            // 4a. Build-level omissions.json — record (or clear) THIS platform's per-artifact content
            //     omissions AFTER the clean promote, so the build-level manifest matches what just
            //     landed. Reconciles only this platform's content-carrier entry; other platforms and
            //     any wholesale-platform omissions are preserved. Done post-promote so a failed
            //     extract never mutates the build manifest. The build-level dir is the PARENT of
            //     outDir (artifacts/<build>/ for --commit; <out-root>/<build>/ off-repo).
            var buildDir = Path.GetDirectoryName(outDir);
            if (!string.IsNullOrEmpty(buildDir))
            {
                BuildLevelOmissions.ReconcilePlatformContentOmissions(
                    buildDir, build, platform, contentOmissions);
            }

            Console.Error.WriteLine($"extract: wrote complete artifact set to {outDir}");
            return 0;
        }
        catch (Exception ex)
        {
            // Surface and fail non-zero. Nothing was promoted to outDir; the staging dir (with any
            // partial set) is discarded in the finally block.
            Console.Error.WriteLine(
                $"extract: failed for platform '{platform}': {ex.GetType().Name}: {ex.Message}");
            return 65;   // EX_DATAERR — input/intermediate could not be processed.
        }
        finally
        {
            // The walker intermediate(s) are transient; never leave them behind. walkTmp2 is only
            // ever written when the determinism gate actually ran (doWalkTwice), but the path is
            // always allocated so this cleanup is unconditional either way.
            if (File.Exists(walkTmp))
            {
                try
                { File.Delete(walkTmp); }
                catch { /* best effort cleanup */ }
            }
            if (File.Exists(walkTmp2))
            {
                try
                { File.Delete(walkTmp2); }
                catch { /* best effort cleanup */ }
            }
            // Discard the staging dir if it survived (i.e. promotion did not consume it).
            if (Directory.Exists(stagingDir))
            {
                try
                { Directory.Delete(stagingDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// TWO-STEP promote: atomically replace <paramref name="outDir"/> with the fully-staged
    /// <paramref name="stagingDir"/> set without a window where NEITHER set exists on disk. The prior
    /// delete-then-move promote deleted outDir, then moved staging into place — a crash in that gap
    /// (killed process, host power loss) left NO artifact set at all. This version instead:
    ///   1. renames a pre-existing outDir ASIDE to a sibling "<outDir>.old-&lt;guid&gt;" (if outDir
    ///      does not exist, this step is skipped — a fresh extract has nothing to preserve);
    ///   2. moves stagingDir into outDir's place (now the only rename that "counts" — from this point
    ///      on outDir IS the new set);
    ///   3. best-effort deletes the ".old-" sibling.
    /// A crash between 1 and 2 leaves the OLD set fully recoverable (just rename ".old-&lt;guid&gt;"
    /// back to outDir) instead of nothing. A crash between 2 and 3, or a step-3 delete failure (e.g.
    /// a file handle still open on Windows), leaves an orphaned ".old-&lt;guid&gt;" sibling — never
    /// fatal here; <see cref="SweepOrphanedStagingDirs"/> (ExtractCommand.Batch.cs) collects it at the
    /// next batch/commit start.
    /// </summary>
    internal static void PromoteStagingDir(string stagingDir, string outDir)
    {
        var parent = Path.GetDirectoryName(outDir);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }

        string? oldDir = null;
        if (Directory.Exists(outDir))
        {
            oldDir = outDir + ".old-" + Guid.NewGuid().ToString("N");
            Directory.Move(outDir, oldDir);
        }

        Directory.Move(stagingDir, outDir);

        if (oldDir is not null)
        {
            try
            { Directory.Delete(oldDir, recursive: true); }
            catch (Exception ex)
            {
                // Best effort: the new set is ALREADY live at outDir (the promote already succeeded),
                // so a failure here is loud-but-non-fatal — the orphan sweep picks it up next run.
                Console.Error.WriteLine(
                    $"extract: WARNING failed to remove superseded set {oldDir}: {ex.GetType().Name}: " +
                    $"{ex.Message}. It will be swept on the next commit/batch run.");
            }
        }
    }

    /// <summary>
    /// Byte-compare two buffers for the commit-path determinism gate. Returns true iff identical;
    /// otherwise <paramref name="firstDiffOffset"/> is the index of the first differing byte, or (when
    /// one buffer is a strict prefix of the other) the length of the shorter one.
    /// </summary>
    private static bool BytesEqual(byte[] a, byte[] b, out long firstDiffOffset)
    {
        long min = Math.Min(a.Length, b.Length);
        for (long i = 0; i < min; i++)
        {
            if (a[i] != b[i])
            {
                firstDiffOffset = i;
                return false;
            }
        }
        if (a.Length != b.Length)
        {
            firstDiffOffset = min;
            return false;
        }
        firstDiffOffset = -1;
        return true;
    }

    /// <summary>
    /// Emit every available artifact for one (build, platform) into <paramref name="stagingDir"/>.
    /// Each emitter throws on any validation failure BEFORE writing bytes; a throw here propagates
    /// to the caller, which discards the whole staging set. Emitters whose walk sub-message is
    /// absent are skipped with a note (the real walker always supplies the schema/convar/netmsg
    /// walks; gameevents is gated on a content-depot VPK the binaries-only acquire skips — its
    /// absence is a documented skip, not a failure).
    /// </summary>
    private static IReadOnlyList<ContentArtifactOmission> EmitFullSet(
        string build, string platform, string binariesDir, WalkerOutput walk, string stagingDir,
        WalkerIdentity? walkerIdentity = null)
    {
        // entity_schema.json. source_revision (Steam changelist) is left "" until the
        // provenance/acquire wiring threads it through (the emitter permits an empty changelist).
        new EntitySchemaEmitter(SchemaFamily.Version, build, platform, sourceRevision: "")
            .Emit(walk, Path.Combine(stagingDir, "entity_schema.json"));
        Console.Error.WriteLine("extract: wrote entity_schema.json");

        // convars.json / commands.json. The real walker always emits these walks.
        if (walk.Convars is not null)
        {
            new ConVarsEmitter(SchemaFamily.Version, build, platform)
                .Emit(walk, Path.Combine(stagingDir, "convars.json"));
            Console.Error.WriteLine("extract: wrote convars.json");
        }
        else
        {
            Console.Error.WriteLine("extract: SKIP convars.json — WalkerOutput carries no convars walk.");
        }

        if (walk.Commands is not null)
        {
            new CommandsEmitter(SchemaFamily.Version, build, platform)
                .Emit(walk, Path.Combine(stagingDir, "commands.json"));
            Console.Error.WriteLine("extract: wrote commands.json");
        }
        else
        {
            Console.Error.WriteLine("extract: SKIP commands.json — WalkerOutput carries no commands walk.");
        }

        // The descriptor / netmsg / module / provenance emitters all enumerate the input binaries.
        // Enumerate once (sorted, deterministic).
        var inputBinaries = EnumerateInputBinaries(binariesDir);

        // network_messages.json — the per-build REGISTERED network-message set, decoded HOST-SIDE by
        // the offline RTTI scanner (NetworkMessageRttiScanner) over the SAME input binaries. This
        // REPLACES the walker's pin-static generated table (walk.NetworkMessages):
        // the scanner reads each build's own CNetMessagePB<...> RTTI instantiations, so the table
        // is per-build (dead/unregistered proto enums excluded by construction) rather than
        // byte-identical across one hl2sdk pin. The host no longer depends on walk.NetworkMessages
        // (the walker's network_messages field stays one release as a documented retirement
        // follow-up). The scanner throws on an unsupported platform (windows-x86_64 only today) or
        // zero decoded messages, aborting the whole set.
        var networkChannels = NetworkMessageRttiScanner.Scan(inputBinaries, platform);
        new NetworkMessagesEmitter(SchemaFamily.Version, build, platform)
            .Emit(networkChannels, Path.Combine(stagingDir, "network_messages.json"));
        Console.Error.WriteLine(
            "extract: wrote network_messages.json (RTTI scan: " +
            $"{networkChannels.Sum(c => c.Messages.Count)} msgs / {networkChannels.Count} channels)");

        // demo_messages.json — the per-build `.dem` demo-command id->type table, decoded HOST-SIDE
        // by the offline RTTI scanner (DemoMessageRttiScanner) over the SAME input binaries (the
        // CDemoMessagePB<id, type> instantiations in engine2). CORE binary-derived: always present,
        // NOT content-gated. Fail-loud: unsupported platform or zero decoded entries.
        var demoMessages = DemoMessageRttiScanner.Scan(inputBinaries, platform);
        new DemoMessagesEmitter(SchemaFamily.Version, build, platform)
            .Emit(demoMessages, Path.Combine(stagingDir, "demo_messages.json"));
        Console.Error.WriteLine(
            $"extract: wrote demo_messages.json (RTTI scan: {demoMessages.Count} demo messages)");

        // engine_constants.json. The real walker supplies the named-constant-pool walk.
        if (walk.EngineConstants is not null)
        {
            new EngineConstantsEmitter(SchemaFamily.Version, build, platform)
                .Emit(walk, Path.Combine(stagingDir, "engine_constants.json"));
            Console.Error.WriteLine("extract: wrote engine_constants.json");
        }
        else
        {
            Console.Error.WriteLine(
                "extract: SKIP engine_constants.json — WalkerOutput carries no engine_constants walk.");
        }

        // string_pools.json. The real walker supplies the interned-string-pool walk.
        if (walk.StringPools is not null)
        {
            new StringPoolsEmitter(SchemaFamily.Version, build, platform)
                .Emit(walk, Path.Combine(stagingDir, "string_pools.json"));
            Console.Error.WriteLine("extract: wrote string_pools.json");
        }
        else
        {
            Console.Error.WriteLine(
                "extract: SKIP string_pools.json — WalkerOutput carries no string_pools walk.");
        }

        // (inputBinaries was enumerated above, before the RTTI scan, and is reused here.)

        // protobuf descriptors — protos/<descriptor>.proto (one per recovered FileDescriptorProto) +
        // a single protos.descriptorset (serialized FileDescriptorSet) over the SAME input binaries.
        // ProtoDescriptorExtractor owns the scan/dedupe/canonicalize/sort/emit. requireNonEmpty:true:
        // a real CS2 binary set always embeds FileDescriptorProtos, so zero descriptors is a
        // structural failure that aborts the whole set BEFORE promotion — never a silent empty
        // protos/. It does NOT abort on cross-binary same-name collisions: many CS2 DLLs statically
        // link their own protobuf runtime and embed byte-differing copies of the same well-known
        // dependency descriptor (google/protobuf/descriptor.proto, etc.), resolved deterministically
        // (Ordinal-first source wins) and surfaced as a stderr WARNING. The canonical copy for each
        // name is a pure function of the input set (Ordinal-min source path), and the extractor emits
        // Name-sorted .proto files + a Name-sorted descriptorset, so filenames, contents, and
        // descriptorset bytes are byte-identical across runs regardless of enumeration order.
        // The engine wire-message descriptors (netmessages, usermessages, gameevents, te, ...) are
        // NOT embedded in any shipped CS2 binary, so they are merged from the committed hl2sdk-sourced
        // set — this is what makes network_messages.json / demo_messages.json wire-ID joins resolve.
        // Fail-loud if the committed set is missing (see WireDescriptorSource). A wire file already
        // recovered from the binaries (none today, but future-proof) keeps its binary-derived copy.
        var repoRoot = Cs2SchemaTracker.Host.Config.HostConfig.RepoRoot
            ?? Walker.EraWalkerResolver.DiscoverRepoRoot();
        var wireDescriptors = WireDescriptorSource.Load(repoRoot);
        new ProtoDescriptorExtractor().Extract(
            inputBinaries, stagingDir, requireNonEmpty: true, supplementalDescriptors: wireDescriptors);
        Console.Error.WriteLine("extract: wrote protos/ + protos.descriptorset");

        // modules.json. Per-binary schema_registration_count is attributed HOST-SIDE from the walk's
        // entity_schema: the count for a binary is the number of SchemaClass + SchemaEnum
        // registrations whose `module` tag maps to that binary's file name. Pseudo-scopes like
        // "!GlobalTypes" have no backing shipped binary and are intentionally unattributed; a binary
        // with no matching registrations (tier0, Qt deps) legitimately gets 0. The count is a pure
        // function of the (already-sorted) entity_schema.
        var registrationsByModuleKey = SchemaRegistrationCounter.CountByModuleKey(walk.EntitySchema);
        // resolved_interfaces: the walker's ModulesWalk carries the boot-resolved CreateInterface
        // versions per module; merge them onto each Module row by the SAME module identity the
        // registration-count merge uses. Absent (older walks / not-yet-populated) ⇒ an empty map ⇒
        // every row's resolved_interfaces stays an empty repeated field.
        var resolvedByModuleKey = SchemaRegistrationCounter.ResolvedInterfacesByModuleKey(walk.Modules);
        new ModuleManifestEmitter(SchemaFamily.Version, build, platform)
            .Emit(inputBinaries.Select(p => new ModuleInput(
                      Path: p,
                      RegistrationCount: SchemaRegistrationCounter.CountForBinaryFileName(
                          registrationsByModuleKey, Path.GetFileName(p)),
                      // "path inside the depot": depot-relative, forward-slash. An absolute local
                      // path varies per machine and would break determinism.
                      RecordedPath: Path.GetRelativePath(binariesDir, p).Replace('\\', '/'),
                      ResolvedInterfaces: SchemaRegistrationCounter.ResolvedInterfacesForBinaryFileName(
                          resolvedByModuleKey, Path.GetFileName(p)))).ToList(),
                  Path.Combine(stagingDir, "modules.json"));
        Console.Error.WriteLine("extract: wrote modules.json");

        // Content artifacts — gated on a content-depot pak01_dir.vpk. The binaries-only acquire
        // skips the content depot, so the VPK is usually absent; that is a documented SKIP, not a
        // failure (fail-loud governs input-binary failures, not optional content). When the VPK IS
        // present, each emitter runs ONLY if its source genuinely ships in this build (HasSource).
        // Extract every artifact a build actually has; an artifact whose source the era simply never
        // shipped is a GRACEFUL OMISSION (collected here), not a fail-loud that kills the whole
        // extract. Genuine corruption — a malformed present source, a CRC mismatch, or a missing
        // backing chunk (content not fully co-located) — STILL fails loud inside the emitter, since
        // that signals an incompletely-acquired or damaged input rather than an era lacking the data.
        //
        // Emitted BEFORE provenance.json so the build-on-demand localization.json (staged here but
        // NOT promoted — see RunExtract) is on disk in time for provenance to fingerprint it.
        var contentResult = EmitContentArtifacts(build, platform, binariesDir, stagingDir);
        var contentOmissions = contentResult.Omissions;

        // provenance.json. Steam identity is read from the acquire's manifest-record.json when
        // present (binariesDir is the acquire output dir); absent fields stay at defaults. git_commit
        // is left "" deterministically here (no shelling out to git); a CI wiring can inject it later.
        // built_from_cl stays "" (content-depot-only — TODO). provenance.localization carries the
        // build-on-demand localization.json fingerprint (null when no localization was produced).
        var provenanceContext = BuildProvenanceContext(
            build, platform, binariesDir, walk, inputBinaries, contentResult.LocalizationFingerprint,
            walkerIdentity);
        ProvenanceEmitter.Emit(provenanceContext, Path.Combine(stagingDir, "provenance.json"));
        Console.Error.WriteLine("extract: wrote provenance.json");

        // registry_audit.json — synthesized AFTER the emitters above have written into the staging
        // dir, and BEFORE promotion, so a failure here aborts the whole set. Every observed symbol
        // becomes Extracted (paired with the artifact that received it) or Omitted (with a non-empty,
        // category-derived rationale). PATH A is the only path that can MINT Omitted rows; the
        // cross-check inside fails loud if any produced artifact carries a symbol the universe never
        // observed.
        //
        // The universe-of-record is assembled from its two OWNERS:
        //   - the WALKER's observed-symbol universe (walk.RegistryUniverse) owns every family it
        //     traverses live in-process — schema_class/schema_enum, convar, command, engine_constant;
        //   - the HOST's offline RTTI scan (networkChannels) owns the network_message family, because
        //     network_messages.json is itself the host RTTI scan and the walker no longer enumerates
        //     network_message rows into its registry_universe (see registry_universe_walk.cpp).
        // AssembleAuditUniverse takes the walker's non-netmsg universe and mints the network_message
        // rows from the SAME RTTI result the artifact was built from, so universe == artifact for
        // that family (the cross-check stays meaningful) while every other family stays the walker's.
        // This is the primary path, not a bridge: the host owns the netmsg audit family end to end,
        // just as it owns the netmsg artifact.
        var auditUniverse = AssembleAuditUniverse(
            walk.RegistryUniverse ?? new RegistryUniverse(), networkChannels);
        RegistryAuditEmitter.EmitFromUniverse(stagingDir, auditUniverse);
        Console.Error.WriteLine("extract: wrote registry_audit.json");

        return contentOmissions;
    }

    /// <summary>
    /// One content-artifact emitter binding: its output file name, a presence predicate over the
    /// opened content VPK (genuine-absence detection), the emit action, and a human description of
    /// the source family (for the omission <c>notes</c>). Bindings iterate in a fixed order.
    /// </summary>
    private readonly record struct ContentArtifactSpec(
        string FileName,
        Func<VpkArchive, bool> HasSource,
        Action<VpkArchive, string> Emit,
        string SourceDescription);

    /// <summary>
    /// The outcome of a content-artifact emit pass: the graceful per-artifact omissions PLUS the
    /// build-on-demand localization.json fingerprint (sha256/size/token_count). The fingerprint is
    /// null when localization was NOT produced (no content VPK, or the era shipped no localization
    /// tables); the caller then leaves provenance.localization absent.
    /// </summary>
    internal sealed record ContentEmitResult(
        IReadOnlyList<ContentArtifactOmission> Omissions,
        LocalizationOutput? LocalizationFingerprint);

    /// <summary>
    /// Emit the seven content artifacts into <paramref name="stagingDir"/>, gated on a co-located
    /// content-depot <c>pak01_dir.vpk</c>. The VPK is opened ONCE and shared
    /// across the emitters. For each artifact: if its source genuinely ships in this build
    /// (<c>HasSource</c>) the emitter runs (and still fails loud on corruption / a missing backing
    /// chunk); if the source is genuinely absent for this era it is a GRACEFUL OMISSION — logged and
    /// collected, never a throw. Returns one <see cref="ContentArtifactOmission"/> per genuinely
    /// absent artifact (reason <c>CONTENT_NOT_SHIPPED_THIS_ERA</c>), which the caller records in the
    /// build-level omissions.json AFTER a clean promote. When the whole content depot is absent (no
    /// VPK) the seven are the documented binaries-only skip and NO content omissions are returned
    /// (the content depot is not in provenance, so the validator does not require the files) and the
    /// localization fingerprint is null (provenance.localization stays absent).
    /// </summary>
    private static ContentEmitResult EmitContentArtifacts(
        string build, string platform, string binariesDir, string stagingDir)
    {
        if (!TryFindGameEventsVpk(binariesDir, out var vpkPath))
        {
            foreach (var name in ContentArtifactNames.Append(ArtifactSet.LocalizationFileName))
            {
                Console.Error.WriteLine(
                    $"extract: SKIP {name} — no pak01_dir.vpk in the binaries dir "
                    + "(content depot not acquired). This is a documented omission, not a failure.");
            }
            return new ContentEmitResult(Array.Empty<ContentArtifactOmission>(), null);
        }

        // The engine core pak (resource/core.gameevents) is OPTIONAL and additive: when present it
        // contributes the 79 engine events; when absent (old stores predating core-pak tracking, or a
        // build/era whose content manifest doesn't ship it) gameevents.json emits the csgo events only
        // with an explicit note (no regression). See ContentPak.Core.
        TryFindCorePakVpk(binariesDir, out var corePakPath);
        return EmitContentArtifactsFromVpk(vpkPath, build, platform, stagingDir, corePakPath);
    }

    /// <summary>
    /// Emit the seven content artifacts from an EXPLICIT content <c>pak01_dir.vpk</c> at
    /// <paramref name="vpkPath"/> into <paramref name="stagingDir"/> — the resolution-independent
    /// core of <see cref="EmitContentArtifacts"/>. Shared by the normal extract path (which resolves
    /// the VPK via <see cref="TryFindGameEventsVpk"/>) and the <c>content-store migrate</c> validation
    /// gate (which emits from BOTH a co-located pak and the trimmed store copy and byte-compares). The
    /// VPK is opened ONCE; per-artifact genuine-absence is a graceful omission, corruption fails loud.
    /// The bindings iterate in a fixed order.
    /// </summary>
    internal static ContentEmitResult EmitContentArtifactsFromVpk(
        string vpkPath, string build, string platform, string stagingDir, string? corePakPath = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(vpkPath);

        // Captured by the localization spec's Emit closure below. Non-null once localization.json is
        // produced this pass; carries the sha256/size/token_count fingerprint provenance records for
        // the build-on-demand artifact. token_count comes straight from the emitter (no re-parse).
        LocalizationOutput? localizationFingerprint = null;

        // Open the directory VPK once; a malformed/truncated _dir.vpk is corruption ⇒ fail loud.
        var archive = VpkArchive.Open(vpkPath);

        // The engine core pak is OPTIONAL: opened only when resolved (present in the store / co-located).
        // Its .gameevents entries are merged into gameevents.json alongside the csgo pak's; it carries
        // NOTHING the other six emitters read, so they stay on the csgo archive only.
        var coreArchive = string.IsNullOrEmpty(corePakPath) ? null : VpkArchive.Open(corePakPath);
        var gameEventArchives = coreArchive is null
            ? new[] { archive }
            : new[] { archive, coreArchive };
        if (coreArchive is null)
        {
            Console.Error.WriteLine(
                "extract: NOTE core.gameevents source absent (engine game/core pak not resolved) — "
                + "gameevents.json emitted from csgo game/mod sources only (the 79 engine events are "
                + "not included this build).");
        }

        var specs = new[]
        {
            new ContentArtifactSpec("gameevents.json",
                a => GameEventsEmitter.HasSource(a)
                    || (coreArchive is not null && GameEventsEmitter.HasSource(coreArchive)),
                (a, o) => new GameEventsEmitter(SchemaFamily.Version, build, platform).Emit(gameEventArchives, o),
                "no .gameevents files shipped in this build's content depot"),
            new ContentArtifactSpec("item_definitions.json", ItemDefinitionsEmitter.HasSource,
                (a, o) => new ItemDefinitionsEmitter(SchemaFamily.Version, build, platform).Emit(a, o),
                "scripts/items/items_game.txt absent from this build's content depot"),
            new ContentArtifactSpec("game_modes.json", GameModesEmitter.HasSource,
                (a, o) => new GameModesEmitter(SchemaFamily.Version, build, platform).Emit(a, o),
                "gamemodes.txt absent from this build's content depot"),
            // localization.json — build-on-demand: still emitted into stagingDir here (so it can be
            // fingerprinted + changelog-diffed), but RunExtract removes it before the promote so it is
            // never committed. The emit captures the token count and computes the fingerprint.
            new ContentArtifactSpec(ArtifactSet.LocalizationFileName, LocalizationEmitter.HasSource,
                (a, o) =>
                {
                    int tokenCount = new LocalizationEmitter(SchemaFamily.Version, build, platform).Emit(a, o);
                    localizationFingerprint = ComputeLocalizationFingerprint(o, (ulong)tokenCount);
                },
                "resource/csgo_<lang>.txt token tables absent from this build's content depot"),
            new ContentArtifactSpec("surface_properties.json", SurfacePropertiesEmitter.HasSource,
                (a, o) => new SurfacePropertiesEmitter(SchemaFamily.Version, build, platform).Emit(a, o),
                "scripts/surfaceproperties_*.txt absent from this build's content depot"),
            new ContentArtifactSpec("prop_data.json", PropDataEmitter.HasSource,
                (a, o) => new PropDataEmitter(SchemaFamily.Version, build, platform).Emit(a, o),
                "scripts/propdata.txt + scripts/collision_properties.txt absent from this build's content depot"),
            new ContentArtifactSpec("map_overviews.json", MapOverviewsEmitter.HasSource,
                (a, o) => new MapOverviewsEmitter(SchemaFamily.Version, build, platform).Emit(a, o),
                "resource/overviews/*.txt absent from this build's content depot"),
        };

        var omissions = new List<ContentArtifactOmission>();
        foreach (var spec in specs)
        {
            if (spec.HasSource(archive))
            {
                spec.Emit(archive, Path.Combine(stagingDir, spec.FileName));
                Console.Error.WriteLine($"extract: wrote {spec.FileName} (from {vpkPath})");
            }
            else
            {
                omissions.Add(new ContentArtifactOmission
                {
                    Artifact = spec.FileName,
                    Reason = PlatformOmission.Types.Reason.ContentNotShippedThisEra,
                    Notes = spec.SourceDescription,
                });
                Console.Error.WriteLine(
                    $"extract: OMIT {spec.FileName} — its source is genuinely absent from this build's "
                    + "content VPK (this era did not ship it). Graceful omission, not a failure.");
            }
        }

        if (omissions.Count > 0)
        {
            // The caller records these in the build-level omissions.json AFTER a clean promote, so
            // ArtifactSetValidator accepts the absent files (content depot present + recorded
            // omission). Deterministic order: artifact-name Ordinal.
            omissions.Sort(static (a, b) => string.CompareOrdinal(a.Artifact, b.Artifact));
            Console.Error.WriteLine(
                $"extract: {omissions.Count} content artifact(s) genuinely absent this build and omitted: "
                + string.Join(", ", omissions.Select(o => o.Artifact)) + ".");
        }
        return new ContentEmitResult(omissions, localizationFingerprint);
    }

    /// <summary>
    /// Fingerprint the build-on-demand localization.json at <paramref name="localizationJsonPath"/>:
    /// sha256 (hex, lowercase) + byte size over the EXACT canonical bytes written this dump, plus the
    /// caller-supplied <paramref name="tokenCount"/>. Streamed so the ~199 MB artifact is hashed
    /// without loading it whole. Deterministic: the bytes are the canonical proto3 JSON, so the hash
    /// is stable across re-runs of the same tool version against the same content.
    /// </summary>
    internal static LocalizationOutput ComputeLocalizationFingerprint(
        string localizationJsonPath, ulong tokenCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(localizationJsonPath);
        using var fs = new FileStream(
            localizationJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = fs.Length;
        byte[] hash = SHA256.HashData(fs);
        return new LocalizationOutput
        {
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
            SizeBytes = (ulong)size,
            TokenCount = tokenCount,
        };
    }

    /// <summary>
    /// Resolve the content-depot <c>pak01_dir.vpk</c> for (build, platform) — the shared resolution
    /// the standalone <c>emit-localization</c> / <c>diff</c> localization paths use. Resolves the
    /// build's binaries/tuple dir (which holds manifest-record.json), then the content VPK via
    /// <see cref="TryFindGameEventsVpk"/> (content-addressed store first, co-located fallback).
    /// Returns false with actionable guidance in <paramref name="error"/> when the binaries dir or
    /// the content VPK is absent — the caller fails loud (the localization family / rebuild requires
    /// the content depot).
    /// </summary>
    internal static bool TryResolveContentVpk(
        string build, string platform, out string vpkPath, out string error)
    {
        vpkPath = "";
        if (!TryResolveBinariesDir(build, platform, out var binariesDir, out var resolveError))
        {
            error = resolveError + Environment.NewLine +
                $"  (localization requires the content depot for build {build}; acquire it with:" +
                Environment.NewLine +
                $"   cs2-schema-tracker acquire --build {build} --platform {platform} --content --dir-only)";
            return false;
        }
        if (!TryFindGameEventsVpk(binariesDir, out vpkPath))
        {
            error =
                $"content depot pak01_dir.vpk not resolvable for (build {build}, {platform}) " +
                $"under '{binariesDir}' (no content-store GID and no co-located pak)." +
                Environment.NewLine +
                $"  Acquire the content depot:  cs2-schema-tracker acquire --build {build} " +
                $"--platform {platform} --content --dir-only";
            return false;
        }
        error = "";
        return true;
    }

    private static readonly JsonParser LenientProvenanceParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>
    /// True iff the committed predecessor set's provenance.json records a populated
    /// <c>localization</c> fingerprint (non-empty sha256) — i.e. the predecessor build DID produce a
    /// build-on-demand localization.json. Gates whether the changelog emits the localization family:
    /// only when BOTH sides produced localization is a token-level diff meaningful. A missing or
    /// unparseable predecessor provenance ⇒ false (no localization family; the 5-family changelog is
    /// still emitted, and any genuinely broken predecessor set fails loud in the emitter's own loads).
    /// </summary>
    private static bool PredecessorProducedLocalization(
        string artifactsRoot, string predecessor, string platform)
    {
        var prov = Path.Combine(artifactsRoot, predecessor, platform, ArtifactSet.ProvenanceFileName);
        if (!File.Exists(prov))
            return false;
        try
        {
            var p = LenientProvenanceParser.Parse<Schemas.Provenance>(File.ReadAllText(prov));
            return p.Localization is { } loc && !string.IsNullOrEmpty(loc.Sha256);
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidJsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Assemble the registry-audit universe-of-record from its two owners. The WALKER's
    /// observed-symbol universe (<paramref name="walkerUniverse"/>) supplies every family it
    /// traverses live in-process (schema_class/schema_enum, convar, command, engine_constant); the
    /// HOST's offline RTTI scan (<paramref name="channels"/>) supplies the <c>network_message</c>
    /// family. This split is the DESIGNED ownership: network_messages.json is itself the host RTTI
    /// scan, and the walker no longer enumerates network_message rows into its registry_universe — so
    /// the audit's netmsg universe MUST come from the same RTTI result the artifact did, or universe
    /// and artifact would describe unlike sets and the cross-check would be meaningless.
    ///
    /// Any <c>network_message</c> row carried by a LEGACY walker_output is dropped and the family is
    /// rebuilt from <paramref name="channels"/>, so an older walk still yields a host-owned netmsg
    /// family (forward-compatible, never a stale double-source). Every other category passes through
    /// untouched. The network_message rows are a pure function of <paramref name="channels"/>, and
    /// RegistryAuditEmitter re-sorts the whole universe canonically.
    /// Mirrors RegistryAuditEmitter's netmsg symbol derivation (symbol = proto_message_type, or
    /// "channel#id" when empty; module = channel; category = "network_message").
    /// </summary>
    private static RegistryUniverse AssembleAuditUniverse(
        RegistryUniverse walkerUniverse, IReadOnlyList<NetworkChannel> channels)
    {
        const string networkMessageCategory = "network_message";
        var universe = new RegistryUniverse();

        // Carry forward every NON-network_message family from the walker's in-process traversal.
        foreach (var symbol in walkerUniverse.Symbols)
        {
            if (string.Equals(symbol.Category, networkMessageCategory, StringComparison.Ordinal))
            {
                continue;   // host-owned family — (re)minted from the RTTI scan below.
            }
            universe.Symbols.Add(symbol);
        }

        // Mint the network_message family from the host RTTI scan — the SAME (proto_message_type,
        // channel) set network_messages.json is emitted from, keeping universe == artifact.
        foreach (var channel in channels)
        {
            foreach (var message in channel.Messages)
            {
                string sym = message.ProtoMessageType.Length != 0
                    ? message.ProtoMessageType
                    : $"{channel.Name}#{message.Id}";
                universe.Symbols.Add(new ObservedRegistrySymbol
                {
                    Symbol = sym,
                    Module = channel.Name,
                    Category = networkMessageCategory,
                });
            }
        }
        return universe;
    }

    /// <summary>
    /// Every *.so / *.dll under <paramref name="binariesDir"/>, sorted Ordinal for deterministic
    /// module + provenance ordering.
    /// </summary>
    private static List<string> EnumerateInputBinaries(string binariesDir)
        => Directory.EnumerateFiles(binariesDir, "*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var ext = Path.GetExtension(p);
                return string.Equals(ext, ".so", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Assemble the provenance context from the extract inputs. Steam identity comes from the
    /// acquire's manifest-record.json (next to the binaries) when present; the schema revision comes
    /// from the walk's schema-system layout signature; built_from_cl stays empty
    /// (content-depot-only). Every value is sourced from input, never synthesized.
    /// </summary>
    private static ProvenanceContext BuildProvenanceContext(
        string build, string platform, string binariesDir, WalkerOutput walk, List<string> inputBinaries,
        LocalizationOutput? localizationFingerprint, WalkerIdentity? walkerIdentity = null)
    {
        var inputs = inputBinaries
            .Select(p => new ProvenanceInput(
                Path: Path.GetRelativePath(binariesDir, p).Replace('\\', '/'),
                LocalFilePath: p,
                // mtime from the Steam manifest is threaded through once the acquire result is
                // available to the extract; until then it is "" (never a filesystem mtime, which
                // would be nondeterministic).
                MtimeUtc: ""))
            .ToList();

        // Steam identity. The acquire drops a manifest-record.json next to the binaries. When
        // present, populate provenance.steam from it. Absence is benign (a --binaries dev run has no
        // record): leave the steam block at proto3 defaults. A present-but-corrupt record fails loud
        // inside ReadFromFile — before any bytes.
        uint appId = 0;
        IReadOnlyList<ProvenanceDepot> depots = Array.Empty<ProvenanceDepot>();
        string manifestCreatedUtc = "";

        var manifestRecordPath = Path.Combine(binariesDir, ManifestRecord.FileName);
        if (File.Exists(manifestRecordPath))
        {
            ManifestRecord record = ManifestRecord.ReadFromFile(manifestRecordPath);

            appId = record.AppId;

            // Depots sorted by depotId. ReadFromFile already sorts; sort again here so the ordering
            // guarantee is explicit at the provenance seam and independent of the reader.
            depots = record.Depots
                .OrderBy(d => d.DepotId)
                .Select(d => new ProvenanceDepot(
                    d.DepotId,
                    d.ManifestId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .ToList();

            // Top-level manifest_created_utc: deterministic choice = the latest (max, Ordinal) of
            // the per-depot creation times. The times are ISO-8601 UTC ("...Z"), which sort
            // correctly as Ordinal strings; the latest is the most representative "this build's
            // content was published at" timestamp when depots differ. Empty entries are ignored
            // unless ALL are empty (then the result stays "").
            manifestCreatedUtc = record.Depots
                .Select(d => d.ManifestCreatedUtc ?? "")
                .Where(s => s.Length > 0)
                .DefaultIfEmpty("")
                .Max(StringComparer.Ordinal)!;
        }

        var ctx = new ProvenanceContext
        {
            SchemaVersion = SchemaFamily.Version,
            BuildId = build,
            Platform = platform,
            GitCommit = ToolBuildInfo.GitCommitId,            // build-baked SHA (nbgv) — deterministic, no runtime git shell-out.
            // Walker identity chain: the WALKER's own self-reported identity (distinct from GitCommit,
            // the HOST's SHA above). "" when unresolved this run (fake-runner test seam / a resolution
            // hiccup already warned about above) — never guessed.
            WalkerGitSha = walkerIdentity?.GitSha ?? "",
            WalkerSrcFingerprint = walkerIdentity?.SrcFingerprint ?? "",
            SchemaRevision = walk.SchemaSystemLayoutSignature ?? "",
            BuiltFromCl = "",                                 // content-depot-only — TODO.
            Inputs = inputs,
            AppId = appId,
            Depots = depots,
            ManifestCreatedUtc = manifestCreatedUtc,
            Localization = localizationFingerprint,
        };
        return ctx;
    }

    /// <summary>
    /// Resolve the content-depot <c>pak01_dir.vpk</c> for (build, platform). Resolution order:
    /// <list type="number">
    ///   <item>the content-addressed trimmed store — read the 2347770 GID from
    ///         <c>&lt;binariesDir&gt;/manifest-record.json</c> and resolve
    ///         <c>&lt;storeRoot&gt;/_content/&lt;gid&gt;/game/csgo/pak01_dir.vpk</c>
    ///         (both platforms of a build carry the same GID, so both resolve the ONE shared copy);</item>
    ///   <item>FALLBACK: a co-located <c>pak01_dir.vpk</c> under <paramref name="binariesDir"/>
    ///         (back-compat during migration and for dev <c>--out</c> trees).</item>
    /// </list>
    /// Returns false (documented skip) when neither is present. Everything downstream
    /// (<see cref="EmitContentArtifacts"/>, all 7 specs, <see cref="VpkArchive.Open"/>) is unchanged:
    /// the resolved pak is opened ONCE and emits all content artifacts. A PRESENT-but-corrupt
    /// manifest-record.json fails loud inside the store resolver, never a silent skip.
    /// </summary>
    internal static bool TryFindGameEventsVpk(string binariesDir, out string vpkPath)
    {
        if (ContentStore.TryResolveStoreDirVpk(binariesDir, out var storePath))
        {
            vpkPath = storePath;
            return true;
        }

        vpkPath = Directory.EnumerateFiles(binariesDir, "pak01_dir.vpk", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault() ?? "";
        return !string.IsNullOrEmpty(vpkPath);
    }

    /// <summary>
    /// Resolve the ENGINE CORE content pak (<c>resource/core.gameevents</c>) for (build, platform),
    /// mirroring <see cref="TryFindGameEventsVpk"/> but for <see cref="ContentPak.Core"/>: the trimmed
    /// store copy at <c>&lt;storeRoot&gt;/_content/&lt;gid&gt;/game/core/pak01_dir.vpk</c> first, else a
    /// co-located <c>game/core/pak01_dir.vpk</c> under <paramref name="binariesDir"/>. Returns false
    /// (a documented, non-fatal absence) when neither is present — an existing store built before the
    /// core pak was tracked, or a build/era whose content manifest doesn't ship it; the caller then
    /// emits csgo-only game events with an explicit note. A PRESENT-but-corrupt manifest-record.json
    /// still fails loud inside the store resolver.
    /// </summary>
    internal static bool TryFindCorePakVpk(string binariesDir, out string vpkPath)
    {
        if (ContentStore.TryResolveStorePak(binariesDir, ContentPak.Core, out var storePath))
        {
            vpkPath = storePath;
            return true;
        }

        var coLocated = Path.Combine(
            binariesDir, ContentPak.Core.DirectoryFileRelPath.Replace('/', Path.DirectorySeparatorChar));
        vpkPath = File.Exists(coLocated) ? coLocated : "";
        return !string.IsNullOrEmpty(vpkPath);
    }

    /// <summary>
    /// AT-USE input verification. When a committed
    /// <c>artifacts/&lt;build&gt;/&lt;platform&gt;/provenance.json</c> exists for this
    /// (build, platform), hash each RESOLVED input under <paramref name="binariesDir"/> and compare
    /// to that provenance's <c>inputs[].sha256</c>. Returns false (caller fails loud, NO walk) on any
    /// mismatch / missing file, after printing a per-file report. Returns true when there is no
    /// committed provenance to compare against (a FRESH extract that PRODUCES it) or when every input
    /// verifies. A present-but-unparseable provenance is itself fail-loud (returns false) — never a
    /// silent skip.
    /// </summary>
    private static bool VerifyAgainstCommittedProvenance(string build, string platform, string binariesDir)
    {
        var provenancePath = Path.GetFullPath(
            Path.Combine("artifacts", build, platform, "provenance.json"));
        if (!File.Exists(provenancePath))
        {
            // FRESH extract — nothing to compare; the walk produces the provenance.
            return true;
        }

        InputVerificationResult result;
        try
        {
            result = InputBinaryVerifier.Verify(provenancePath, binariesDir);
        }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException)
        {
            // A committed-but-unparseable provenance, or an input path that escapes the binaries
            // dir, is fail-loud — never a silent skip past the gate.
            Console.Error.WriteLine(
                $"extract: at-use verification could not read the committed provenance " +
                $"'{provenancePath}': {ex.Message}. No walk performed.");
            return false;
        }

        if (!result.Ok)
        {
            InputBinaryVerifier.WriteFailureReport(
                Console.Error,
                $"extract: at-use input verification FAILED for (build {build}, {platform}) against '{provenancePath}'",
                result);
            Console.Error.WriteLine(
                "extract: a resolved input binary does not match the committed provenance " +
                "(modified/corrupted since acquisition). No artifacts written.");
            return false;
        }

        if (result.Verified > 0)
        {
            Console.Error.WriteLine(
                $"extract: at-use input verification OK — {result.Verified} input(s) match the committed provenance.");
        }
        else
        {
            // A committed provenance with no input hashes (legacy/minimal record) carries nothing to
            // compare — a documented SKIP, not a failure (real input mismatches are caught above).
            Console.Error.WriteLine(
                "extract: at-use input verification SKIPPED — the committed provenance lists no input hashes.");
        }
        return true;
    }

    /// <summary>
    /// Resolve the directory of acquired CS2 binaries for (build, platform). Accepts the
    /// conventional cache path cache/binaries/&lt;build&gt;/&lt;platform&gt;/ if it exists.
    /// </summary>
    internal static bool TryResolveBinariesDir(string build, string platform, out string binariesDir, out string error)
    {
        // Operator override: the binaries-store root (CS2_BINARIES_ROOT env, or appsettings
        // BinariesRoot; env wins) lets the acquired-binaries store live off the repo volume
        // (e.g. /data/cs2-binaries) instead of the in-repo cache/. When set, the convention is
        // <root>\<build>\<platform>\ (matching the acquirer's --out layout). Checked BEFORE the
        // default cache path; a present override dir wins.
        var binariesRoot = Config.HostConfig.BinariesRoot;
        if (!string.IsNullOrEmpty(binariesRoot))
        {
            var overrideDir = Path.GetFullPath(Path.Combine(binariesRoot, build, platform));
            if (Directory.Exists(overrideDir))
            {
                binariesDir = overrideDir;
                error = "";
                return true;
            }
        }

        binariesDir = Path.GetFullPath(Path.Combine("cache", "binaries", build, platform));
        if (Directory.Exists(binariesDir))
        {
            error = "";
            return true;
        }

        // Not found. The production extract auto-acquires here (see RunExtract); this guidance is
        // what an operator sees when auto-acquire is OFF (--no-acquire) or unavailable — never a
        // silent empty extract.
        error =
            $"extract: input binaries not found at '{binariesDir}'." + Environment.NewLine +
            $"  Acquire them first:  cs2-schema-tracker acquire --build {build} --platform {platform}" + Environment.NewLine +
            "  (Or drop --no-acquire to let extract acquire them automatically.)";
        return false;
    }

    private static int RunDescriptorsOnly(string binariesDir, string outDir)
    {
        if (!Directory.Exists(binariesDir))
        {
            Console.Error.WriteLine($"extract --binaries: directory not found: '{binariesDir}'.");
            return 65;   // EX_DATAERR
        }

        // Sorted enumeration ⇒ deterministic scan order ⇒ deterministic dedupe
        // when two binaries embed the same FDP with byte-equal contents.
        var bins = Directory.EnumerateFiles(binariesDir, "*", SearchOption.AllDirectories)
            .Where(p =>
            {
                var ext = Path.GetExtension(p);
                return string.Equals(ext, ".so", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (bins.Count == 0)
        {
            Console.Error.WriteLine($"extract --binaries: no .so/.dll under '{binariesDir}'.");
            return 65;
        }

        // Merge the SDK-sourced wire descriptors here too so the dev hook's protos.descriptorset
        // matches the real extract's (network_messages/demo_messages joins resolve).
        var repoRoot = Cs2SchemaTracker.Host.Config.HostConfig.RepoRoot
            ?? Walker.EraWalkerResolver.DiscoverRepoRoot();
        var wireDescriptors = WireDescriptorSource.Load(repoRoot);
        var extractor = new ProtoDescriptorExtractor();
        extractor.Extract(bins, outDir, supplementalDescriptors: wireDescriptors);
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine(@"cs2-schema-tracker extract — full extraction for one or more (build, platform).

Usage: cs2-schema-tracker extract --build <id> [--build <id> ...] [--platform <platform>]
                                   [--out <dir>] [--commit] [--verify] [--no-gate] [--force] [--no-acquire]
                                   [--no-changelog] [--no-localization-changelog] [--single-walk]
                                   [--allow-mixed-walkers]
       cs2-schema-tracker extract (--all | --era <key> | --pin <sha>) [--platform <platform>]
                                   [--out <dir>] [--commit] [--verify] [--no-gate] [--force] [--no-acquire]
                                   [--no-changelog] [--no-localization-changelog] [--single-walk]
                                   [--allow-mixed-walkers]

A single extract produces the COMPLETE artifact set for the build (CORE + the content artifacts
whenever the content depot is co-located with the binaries). The retired single-artifact --*-only
content backfill modes are gone — a full extract emits them all.

Selection (exactly one family required for the full-extract path):
  --build <id>          Steam build ID, or 'latest' for current public-branch head. Repeatable:
                        a single id is the forward path; two or more is a batch. NOT required to
                        be already-committed (each is acquired/resolved -> walked -> emitted).
  --all                 BATCH over every build in the INVENTORY for the platform (whether or not
                        already walked/committed; data/cs2-assets-inventory.json builds[]).
  --era <key>           BATCH over every inventory build in the named era (eras[]).
  --pin <sha>           BATCH over every inventory build whose hl2sdk pin starts with <sha>.
                        --all / --era / --pin / --build / --backfill are mutually exclusive.
  --only-existing-builds
                        MODIFIER for --all/--era/--pin: restrict to builds already committed for the
                        platform (the legacy re-walk-only behavior). Not valid alone or with
                        --build / --backfill.

Arguments (stable per README.md):
  --platform <platform> One of: linux-x86_64, windows-x86_64 (or set ExtractPlatform in
                        appsettings.json). One walk per platform loads ALL modules; client/server
                        live in the per-class module tag.
  --out <dir>           Off-repo output ROOT; per-build sets go to <dir>/<build>/<platform>
                        (default: appsettings ExtractOutRoot, else ./extract-out/). IGNORED with
                        --commit (a warning is printed).
  --commit              Promote the produced CORE+content set INTO the repo at
                        artifacts/<build>/<platform>, clobbering any existing committed set via the
                        atomic stage->promote. Fires the build-level side-effects (optional
                        pics-appinfo.json from a forward-acquisition capture; inventory upsert).
                        Does NOT git-commit; review the diff and commit manually. The
                        layout/signature gate (exit 75), the commit-path determinism gate (exit 76,
                        on by default; see --single-walk), the class-count gate (exit 77), and the
                        walker identity gate (exit 78; see --allow-mixed-walkers) all stay HARD => NO
                        write. --verify stays a NON-BLOCKING review signal (promote proceeds
                        regardless of its verdict).
  --single-walk         Disable the commit-path determinism gate (armed by default under --commit):
                        walk once instead of twice, skipping the byte-compare that would otherwise
                        abort at exit 76 on any divergence. Escape hatch for a walker/era known to be
                        slow or for local iteration; never use it for a corpus-committing run.
  --allow-mixed-walkers Bypass the commit-path WALKER IDENTITY GATE (exit 78; on by default under
                        --commit): a mixed/unverified per-era walker set (differing src-fingerprints,
                        an older binary reporting fingerprint=unknown, or a binary that disagrees
                        with natives/<platform>/walker-manifest.json) is warned LOUDLY instead of
                        blocking the promote. Off-repo runs already only warn (never blocked). Never
                        use it for a corpus-committing run — rebuild the mismatched eras instead
                        (scripts/build-era-walkers.*). Every run also prints a one-line startup
                        banner: `extract: tool=<host sha> walkers=<fingerprint|mixed|unknown>`; set
                        CS2_EXPECT_FPRINT to a fingerprint prefix to hard-fail (exit 78,
                        unconditionally — never bypassed by this flag) when the resolved walker set
                        does not match (the stale-remote-image tripwire).
  --verify              Byte-compare the produced CORE set to the committed artifacts/ set
                        (schemaVersion + toolGitSha normalized); classify CORE-CLEAN / REGRESSION.
                        Off-repo a REGRESSION is a hard failure; with --commit it is a non-blocking
                        snapshot-before-clobber CHANGED/unchanged/new signal.
  --no-gate             Disable the era-aware entity_schema class-count sanity gate (on by default).
  --force               Re-walk builds whose off-repo output already exists (default: skip them).
  --no-acquire Do NOT auto-acquire missing input binaries. Default: when the input
                        binaries are absent the production extract acquires them in-process, then
                        walks. With this flag an absent input dir fails loud with guidance instead.
  --no-changelog Do NOT emit the build-to-build changelog.json inline. Default: a full
                        extract auto-produces changelog.json from this build's freshly-emitted set
                        against its immediate committed predecessor (resolved by the shared
                        rule), promoted atomically with the set. The floor build (no predecessor)
                        never emits one. Use the standalone `diff` command for out-of-order backfill.
  --no-localization-changelog
                        Emit the changelog WITHOUT the content-derived localization family (the five
                        binary families are still produced). The localization family's predecessor
                        side must be regenerated from the PREDECESSOR build's content, which the
                        forward-capture path (anonymous / ephemeral runners) cannot re-acquire —
                        anonymous Steam serves only the current build. This build's own
                        localization.json is still produced + fingerprinted into provenance.localization.

Per: the host can only extract the platform whose OS+arch matches its own.
Per: fail-loud; any input failure exits non-zero BEFORE any artifact bytes.
Per: a single (build, platform) extract produces either one complete artifact set or none.

BATCH fail-isolation: with several builds, one build's walker crash never aborts the run; a SUMMARY
prints and the process exits non-zero (1) iff any build ended Regression / Failed / Gated — a build
that was refused (gated, nothing written) still makes the batch's own exit non-zero, so a script/CI
check never mistakes ""nothing entered the corpus for this build"" for a clean run. A single forward
--build keeps the direct per-build exit-code behavior (the walker's / gate's code is surfaced
verbatim) instead of the flat batch 1.

WALKER IDENTITY GATE (exit 78): evaluated ONCE for the whole selection, BEFORE any build runs —
unlike the per-build gates above, a mixed/stale walker SET aborts the entire invocation (no partial
SUMMARY), since it means the run cannot be trusted from build 1. See --allow-mixed-walkers above.");
    }
}
