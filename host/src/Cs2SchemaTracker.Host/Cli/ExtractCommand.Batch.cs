// Extract — multi-build selection + batch orchestration (formerly the `rewalk` subcommand).
//
// The `rewalk` verb was folded INTO `extract` (one verb, the extraction core is shared). This
// partial carries the selection + post-processing layer that sits OVER ExtractCommand.RunExtract:
//   - SELECTION: forward-extract one or more --build ids (a single id is the forward path, two or
//     more a batch; NOT required to be already-committed), OR batch over the COMMITTED builds
//     discovered for --all / --era <key> / --pin <sha>.
//   - OUTPUT TARGET: off-repo by default (<OutRoot>/<build>/<platform>, OutRoot = --out, else
//     appsettings ExtractOutRoot, else ./extract-out/); --commit promotes (clobbers) straight into
//     the repo's artifacts/<build>/<platform> via RunExtract's atomic stage->promote and fires the
//     build-level side-effects (optional pics-appinfo.json + inventory upsert).
//   - GATE: the era-aware entity_schema class-count sanity gate (on by default; --no-gate off), and
//     the commit-path determinism gate (on by default under --commit; --single-walk off). BOTH are
//     now evaluated INSIDE RunExtract, BEFORE any promote — off-repo OR --commit alike, a violation
//     writes NOTHING (exit 77 / exit 76). "Gated but promoted" is structurally impossible; this
//     layer only classifies the exit code into a Status.Gated result for the summary.
//   - --verify: byte-compare the produced CORE set (including provenance.json) to committed
//     (tool-stamp fields normalized: schemaVersion, tool.gitCommit, tool.semver, walkerGitSha,
//     walkerSrcFingerprint). Off-repo a regression is HARD; under --commit it is a
//     snapshot-before-clobber CHANGED/unchanged/new review signal.
//   - FAIL-ISOLATION: in BATCH mode one build's crash never aborts the run; a SUMMARY prints and the
//     process exits non-zero (flat 1) iff any build ended Regression / Failed / Gated. A SINGLE
//     forward (non-commit) build instead surfaces RunExtract's own raw exit code verbatim.
//
// Determinism: config/out paths only LOCATE inputs/outputs and never reach an artifact byte
// (RunExtract already relativizes provenance/modules paths).

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Cs2SchemaTracker.Host.Config;
using Cs2SchemaTracker.Host.PicsAppInfo;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Walker;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static partial class ExtractCommand
{
    /// <summary>Parsed, validated full-extract request (selection + flags).</summary>
    private sealed record Options(
        bool All,
        bool Backfill,
        bool OnlyExistingBuilds,
        string? Era,
        string? Pin,
        IReadOnlyList<string> Builds,
        string Platform,
        string OutRoot,
        bool Gate,
        bool Force,
        bool Verify,
        bool Commit,
        bool NoAcquire,
        bool NoChangelog,
        bool NoLocalizationChangelog,
        bool SingleWalk,
        bool AllowMixedWalkers);

    /// <summary>Per-build outcome classification (drives the summary + exit code).</summary>
    internal enum Status
    {
        /// <summary>Extract succeeded (and, with --verify, the CORE set was clean).</summary>
        Ok,
        /// <summary>
        /// --commit mode: the set was PROMOTED into artifacts/, clobbering the prior records. Any
        /// gate / --verify result is carried in the detail LOUDLY but the promote still proceeded.
        /// Not a failure (exit 0).
        /// </summary>
        Committed,
        /// <summary>
        /// A layout gate (exit 75) or the class-count-band gate (exit 77) rejected the walk BEFORE
        /// any promote — nothing was written. Not a walker crash, but DOES make the batch's own exit
        /// non-zero (see <c>Summarize</c>) — a build with nothing in the corpus is never "clean".
        /// </summary>
        Gated,
        /// <summary>Skipped: already-present off-repo output without --force. Not a failure.</summary>
        Skipped,
        /// <summary>--verify (off-repo): the CORE set differs from committed (a regression). HARD failure.</summary>
        Regression,
        /// <summary>Walker crash / non-zero extract / unexpected error. A HARD failure.</summary>
        Failed,
    }

    private sealed record BuildResult(string Build, string Era, Status Status, string Detail);

    /// <summary>A build's classified result plus the raw exit code RunExtract returned (0 on a clean
    /// walk). The raw code lets a SINGLE forward build surface the walker's code (75/70/65) verbatim.</summary>
    private sealed record BuildOutcome(BuildResult Result, int RawExtractCode);

    /// <summary>The CORE artifact set the gate / verify operate on. provenance.json is included:
    /// its non-tool-stamp content — buildId, cs2Build, inputs[] hashes, platform,
    /// steam depot/manifest ids — is fully deterministic per (build, platform), so genuine drift
    /// there (e.g. a changed input file hash) IS a real CORE regression worth catching. The tool-
    /// stamp fields (schemaVersion, tool.gitCommit, tool.semver, tool.walkerGitSha,
    /// tool.walkerSrcFingerprint) are normalized away in <see cref="NormalizedJsonSha"/> exactly
    /// like the other CoreJson files, so re-running with a newer host/walker build never counts as
    /// drift on its own.</summary>
    private static readonly string[] CoreJson =
    {
        "entity_schema.json", "convars.json", "commands.json", "engine_constants.json",
        "modules.json", "string_pools.json", "network_messages.json", "demo_messages.json",
        "registry_audit.json", "provenance.json",
    };

    private const string CoreBin = "protos.descriptorset";

    /// <summary>
    /// The content artifacts the no-clobber promote guard protects. localization.json is NOT here:
    /// it is build-on-demand (produced every dump but never committed), so there is no committed
    /// localization.json for a re-walk to clobber.
    /// </summary>
    private static readonly string[] ContentArtifactNames =
    {
        "gameevents.json", "item_definitions.json", "game_modes.json",
        "surface_properties.json", "prop_data.json", "map_overviews.json",
    };

    // schemaVersion / tool.gitCommit / tool.semver are EXPECTED to drift (family-version stamp +
    // per-commit git SHA + host semver); all are normalized away before the CORE byte-compare so
    // --verify reports semantic CORE drift, not version noise.
    [GeneratedRegex("\"schemaVersion\":\\s*\"[^\"]*\"")]
    private static partial Regex SchemaVersionRegex();

    // provenance.json's ToolVersion message serializes the host's git SHA as a NESTED "gitCommit"
    // key (tool.gitCommit — see schemas/provenance.proto), never as a top-level "toolGitSha" key.
    // This regex previously read `"toolGitSha":...` and so never matched anything in any file this
    // normalizer ever ran against (dead from the day it was written — provenance.json wasn't in
    // CoreJson yet, and none of the other CoreJson files have a toolGitSha/gitCommit field). Fixed to
    // match the field that actually exists now that provenance.json is a CoreJson member.
    [GeneratedRegex("\"gitCommit\":\\s*\"[^\"]*\"")]
    private static partial Regex ToolGitShaRegex();

    // tool.semver (provenance.proto ToolVersion.semver) is the same kind of tool-identity stamp as
    // gitCommit above — it changes whenever the HOST's version bumps, independent of whether the
    // extracted data itself changed — so it must be normalized the same way, or every provenance.json
    // comparison would flag a spurious regression on the next host release.
    [GeneratedRegex("\"semver\":\\s*\"[^\"]*\"")]
    private static partial Regex ToolSemverRegex();

    // walkerGitSha / walkerSrcFingerprint (provenance.tool — the walker identity chain) are, like
    // toolGitSha above, tool-STAMP fields — they identify what ran, not what the data says — so a
    // --verify byte-compare must never count their drift as a CORE regression. Mirrors the existing
    // pattern exactly (same [^"]* capture, same normalization call site in NormalizedJsonSha).
    [GeneratedRegex("\"walkerGitSha\":\\s*\"[^\"]*\"")]
    private static partial Regex WalkerGitShaRegex();

    [GeneratedRegex("\"walkerSrcFingerprint\":\\s*\"[^\"]*\"")]
    private static partial Regex WalkerSrcFingerprintRegex();

    /// <summary>
    /// Main extraction orchestration (the dispatch from <see cref="Run(string[], Func{IWalkerRunner}?, EraWalkerResolver?, bool)"/>
    /// after the dev-hook + single-artifact modes). Parses the selection, resolves the build list,
    /// runs each through <see cref="RunExtract"/> with fail-isolation, and returns the process exit
    /// code (single-build direct, batch via the summary).
    /// </summary>
    private static int RunSelection(
        string[] args, Func<IWalkerRunner>? runnerFactory, EraWalkerResolver? eraResolver, bool gateFromResolver,
        Func<string, string, int>? acquire)
    {
        if (!TryParseSelection(args, out var opts, out var parseError))
        {
            Console.Error.WriteLine(parseError);
            return 64;   // EX_USAGE
        }

        // Cross-OS guard. The walker loads the platform's CS2 binaries into THIS process, so the
        // host OS+arch must match. Fail loud before any work (a fake-runner batch test always uses
        // the host-matching platform).
        if (!HostPlatform.TryGetRequiredHost(opts.Platform, out var required))
        {
            Console.Error.WriteLine(HostPlatform.UnknownPlatformMessage(opts.Platform));
            return 64;   // EX_USAGE — invalid platform value.
        }
        if (!HostPlatform.CanExtractPlatform(opts.Platform))
        {
            Console.Error.WriteLine(HostPlatform.CrossOsGuidanceMessage(opts.Platform, required));
            return 70;   // EX_SOFTWARE — host environment cannot satisfy this request.
        }

        // ONE repo-root source: the resolver's discovered root (production) or fixture root (tests);
        // else the current working directory (the fake-runner seam without a resolver). --commit's
        // artifacts/ target and the committed-build discovery both derive from this.
        var repoRoot = eraResolver?.RepoRoot ?? Directory.GetCurrentDirectory();

        // ORPHAN SWEEP: this is BOTH the batch entry point and the single-build --commit entry
        // point (RunSelection is shared), so gating on opts.Commit here covers "at batch start (and
        // single-build commit start)" with one call site. Runs ONCE, BEFORE selection, so a prior
        // crash's leftovers are gone before this run's own commit-path promotes start writing
        // ".staging-"/".old-" siblings of their own. Off-repo (non-commit) runs never target
        // artifacts/, so there is nothing to sweep there.
        if (opts.Commit)
        {
            SweepOrphanedStagingDirs(Path.Combine(repoRoot, "artifacts"));
        }

        List<string> builds;
        try
        {
            builds = SelectBuilds(opts, repoRoot, eraResolver);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"extract: selection failed: {ex.GetType().Name}: {ex.Message}");
            return 65;   // EX_DATAERR — the repo's committed metadata could not be read.
        }

        if (builds.Count == 0)
        {
            Console.Error.WriteLine("extract: no builds matched the selection. Nothing to do.");
            return 0;
        }

        // BATCH iff more than one build, or a committed-set / backfill selection family was used. A
        // single forward --build keeps today's direct exit-code behavior.
        bool batch = builds.Count > 1 || opts.All || opts.Backfill || opts.Era is not null || opts.Pin is not null;

        var targetDesc = opts.Commit
            ? "artifacts/ (COMMIT: clobbers committed records, NO git)"
            : $"out='{opts.OutRoot}'";
        Console.Error.WriteLine(
            $"extract: {builds.Count} build(s) selected (platform={opts.Platform}, " +
            $"{targetDesc}, gate={opts.Gate}, verify={opts.Verify}).");

        // WALKER IDENTITY GATE — BEFORE the per-build loop, so a mixed/stale walker set aborts
        // the WHOLE run before build 1 rather than partway through a long batch. Prints the startup
        // identity banner unconditionally (production path only); hard-fails (exit 78) ONLY under
        // --commit with a genuinely mixed/unverified set, unless --allow-mixed-walkers. See
        // PreflightWalkerIdentity's own doc comment for the full contract.
        if (PreflightWalkerIdentity(builds, opts, runnerFactory, eraResolver) is int identityExit)
        {
            return identityExit;
        }

        var outcomes = new List<BuildOutcome>();
        int i = 0;
        foreach (var b in builds)
        {
            i++;
            Console.Error.WriteLine($"extract: [{i}/{builds.Count}] build {b}");
            outcomes.Add(RunOneBuild(b, opts, repoRoot, runnerFactory, eraResolver, gateFromResolver, batch, acquire));
        }

        int code = Summarize(outcomes.Select(o => o.Result).ToList());

        // SINGLE forward build (no --commit): surface the RunExtract failure code verbatim (65 =
        // input error, 70 = contract violation / walker crash, 75 = layout gate, 76 = commit-path
        // determinism gate, 77 = class-band gate). A successful walk (RawExtractCode 0) falls through
        // to the summary instead — which also covers an off-repo --verify Regression, since THAT
        // classification is derived AFTER a successful RunExtract (RawExtractCode stays 0 there), so
        // it surfaces as the summary's flat exit 1, not a distinct code. Under --commit the raw
        // override never applies (any gate there still goes through the summary too), matching how a
        // --commit run is always evaluated as a batch-shaped outcome even when it names one build.
        if (!batch && !opts.Commit && outcomes[0].RawExtractCode != 0)
        {
            return outcomes[0].RawExtractCode;
        }
        return code;
    }

    /// <summary>
    /// Orphan sweep. A killed extract process (Ctrl-C, OOM-kill, host crash) skips
    /// RunExtract's <c>finally</c> cleanup, leaving an abandoned
    /// <c>artifacts/&lt;build&gt;/&lt;platform&gt;.staging-&lt;guid&gt;</c> (crashed before promote)
    /// or <c>artifacts/&lt;build&gt;/&lt;platform&gt;.old-&lt;guid&gt;</c> (crashed in the sliver
    /// between <see cref="PromoteStagingDir"/>'s rename-aside and its best-effort delete) sibling
    /// dir behind. Staging/old dirs are always siblings of the platform dir they shadow (one level
    /// under <paramref name="artifactsRoot"/>/&lt;build&gt;/), never the platform dir itself, so a
    /// name-substring match on the immediate children of each build dir is sufficient and can never
    /// touch a real committed platform set. Best-effort per candidate: a locked/permission-denied
    /// leftover is warned, never fatal — it will be picked up by the next sweep.
    /// </summary>
    internal static void SweepOrphanedStagingDirs(string artifactsRoot)
    {
        if (!Directory.Exists(artifactsRoot))
        {
            return;
        }

        foreach (var buildDir in Directory.EnumerateDirectories(artifactsRoot))
        {
            foreach (var candidate in Directory.EnumerateDirectories(buildDir))
            {
                var name = Path.GetFileName(candidate);
                if (name is null)
                {
                    continue;
                }
                bool isOrphan = name.Contains(".staging-", StringComparison.Ordinal)
                    || name.Contains(".old-", StringComparison.Ordinal);
                if (!isOrphan)
                {
                    continue;
                }

                try
                {
                    Directory.Delete(candidate, recursive: true);
                    Console.Error.WriteLine($"extract: removed orphaned staging dir {candidate}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"extract: WARNING failed to remove orphaned staging dir {candidate}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Env var: the operator's expected walker src-fingerprint — the stale-remote-image
    /// tripwire. Set on a remote/CI runner to the fingerprint the operator BUILT there; a
    /// mismatch means the running image/binaries are not what was intended.</summary>
    internal const string ExpectFingerprintEnvVar = "CS2_EXPECT_FPRINT";

    /// <summary>One resolved walker binary's identity (or resolution error) plus every era it serves.</summary>
    private sealed record WalkerBinaryRow(
        string BinaryPath, IReadOnlyList<string> Eras, WalkerIdentity? Identity, string? Error);

    /// <summary>Pseudo-fingerprint standing in for a binary whose identity could not be resolved at
    /// all (missing file / --version failure) — distinct from <see cref="WalkerIdentity.UnknownFingerprint"/>
    /// (a binary that DID resolve but predates the <c>src-fingerprint</c> line).</summary>
    internal const string ErrorFingerprintToken = "<error>";

    /// <summary>The walker identity gate's verdict: the banner's <c>walkers=</c> value, whether the
    /// resolved set was mixed/unverified/violating, and the exit code to abort with (null = proceed).</summary>
    internal readonly record struct WalkerIdentityVerdict(string WalkersDisplay, bool MixedOrUnverified, int? ExitCode);

    /// <summary>
    /// PURE decision core of the walker identity gate — no process launches, no I/O, no Console
    /// writes — so the fail/warn/proceed matrix is unit-testable without a real walker binary.
    /// Internal (not private): the test suite exercises this directly via InternalsVisibleTo.
    /// <paramref name="fingerprints"/> is the de-duplicated set of every resolved binary's
    /// src-fingerprint (<see cref="ErrorFingerprintToken"/> standing in for a resolution failure);
    /// <paramref name="violationCount"/> folds in EVERY reason the set is untrustworthy beyond raw
    /// non-uniformity (an "unknown"/error fingerprint, or a walker-manifest.json disagreement) even
    /// when there happens to be only one distinct fingerprint value.
    /// </summary>
    internal static WalkerIdentityVerdict EvaluateWalkerIdentityGate(
        SortedSet<string> fingerprints, int violationCount, bool commit, bool allowMixedWalkers,
        string? expectFingerprintEnv)
    {
        bool uniformKnown = fingerprints.Count == 1
            && !string.Equals(fingerprints.Single(), WalkerIdentity.UnknownFingerprint, StringComparison.Ordinal)
            && !string.Equals(fingerprints.Single(), ErrorFingerprintToken, StringComparison.Ordinal);
        bool mixedOrUnverified = !uniformKnown || violationCount > 0;

        string walkersDisplay = uniformKnown
            ? ShortToken(fingerprints.Single(), 12)
            : fingerprints.Count > 1 ? "mixed" : "unknown";

        if (mixedOrUnverified && commit && !allowMixedWalkers)
        {
            return new WalkerIdentityVerdict(walkersDisplay, true, 78);
        }

        // CS2_EXPECT_FPRINT — the stale-remote-image tripwire. Unconditional: an explicit operator
        // assertion is never silently bypassed by --allow-mixed-walkers or a non-commit run (unlike
        // the mixed-set check above, which non-commit runs only ever warn about).
        if (!string.IsNullOrEmpty(expectFingerprintEnv))
        {
            bool allMatch = fingerprints.Count > 0
                && fingerprints.All(fp => fp.StartsWith(expectFingerprintEnv, StringComparison.Ordinal));
            if (!allMatch)
            {
                return new WalkerIdentityVerdict(walkersDisplay, mixedOrUnverified, 78);
            }
        }

        return new WalkerIdentityVerdict(walkersDisplay, mixedOrUnverified, null);
    }

    /// <summary>
    /// WALKER IDENTITY GATE. A mixed-vintage walker set (some eras rebuilt, some stale) and a
    /// stale Docker image both produced corpus-scale damage undetected before this existed
    /// (incident #8) — nothing ever compared what actually ran against what the operator believed was
    /// built. This runs ONCE, before the per-build loop:
    ///   1. resolve every SELECTED build's era -> walker binary (de-duplicated: many builds share one
    ///      era's binary), and that binary's self-reported identity (WalkerIdentity.Resolve, &lt;1s,
    ///      memoized per binary);
    ///   2. print the one-line startup banner: <c>extract: tool={host sha} walkers={fingerprint|mixed|unknown}</c>;
    ///   3. a genuinely mixed/unverified set (differing fingerprints, ANY "unknown" — i.e. a binary
    ///      predating the <c>src-fingerprint</c> line — or a co-located
    ///      <c>natives/&lt;platform&gt;/walker-manifest.json</c> disagreeing
    ///      with what a binary actually reports) is a HARD failure (exit 78) under <c>--commit</c>
    ///      UNLESS <c>--allow-mixed-walkers</c>; a non-commit run only warns and proceeds;
    ///   4. <see cref="ExpectFingerprintEnvVar"/>, when set, is an unconditional tripwire (prefix-match
    ///      against every resolved fingerprint) — NOT bypassed by --allow-mixed-walkers or a non-commit
    ///      run, because it is an explicit operator assertion ("this run must be THIS exact walker
    ///      set"), not a generic mixed-set warning.
    /// Skipped entirely (no banner) on the fake-runner test seam (no real binary to identify) and when
    /// CS2_WALKER_BIN / appsettings WalkerBin is set (an explicit single-binary override makes every
    /// build resolve to the SAME binary by construction — trivially uniform, nothing to compare).
    /// Returns the exit code to abort with, or null to proceed.
    /// </summary>
    private static int? PreflightWalkerIdentity(
        IReadOnlyList<string> builds, Options opts, Func<IWalkerRunner>? runnerFactory, EraWalkerResolver? eraResolver)
    {
        // Fake-runner test seams (orchestration / gate suites) never launch a real binary — nothing to
        // preflight. eraResolver null is the same "no real production wiring" case.
        if (runnerFactory is not null || eraResolver is null)
        {
            return null;
        }

        string toolSha = ShortToken(ToolBuildInfo.GitCommitId, 7);

        var explicitOverride = HostConfig.WalkerBin;
        if (!string.IsNullOrWhiteSpace(explicitOverride))
        {
            Console.Error.WriteLine($"extract: tool={toolSha} walkers=override ({explicitOverride})");
            return null;
        }

        // Resolve every selected build's era -> walker binary, de-duplicated. An individual
        // resolution failure here is NOT this gate's job to report — RunOneBuild reports it per-build
        // (exit 75) when the batch loop actually reaches that build.
        var erasByBinary = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var build in builds)
        {
            EraResolution resolution;
            try
            {
                resolution = eraResolver.Resolve(build, opts.Platform);
            }
            catch
            {
                continue;
            }
            if (!erasByBinary.TryGetValue(resolution.WalkerBinaryPath, out var eras))
            {
                erasByBinary[resolution.WalkerBinaryPath] = eras = new List<string>();
            }
            if (!eras.Contains(resolution.Era, StringComparer.Ordinal))
            {
                eras.Add(resolution.Era);
            }
        }

        if (erasByBinary.Count == 0)
        {
            return null;   // nothing resolvable — RunOneBuild will report each failure individually.
        }

        // Optional natives/<platform>/walker-manifest.json cross-check: all binaries for one
        // platform live in the same directory, so any resolved binary's directory is the right one.
        var manifestDir = Path.GetDirectoryName(erasByBinary.Keys.First());
        var manifestPath = manifestDir is not null
            ? Path.Combine(manifestDir, "walker-manifest.json")
            : null;
        var manifestFingerprintByEra = manifestPath is not null && File.Exists(manifestPath)
            ? LoadWalkerManifestFingerprints(manifestPath)
            : null;

        var rows = new List<WalkerBinaryRow>();
        foreach (var (binary, eras) in erasByBinary)
        {
            try
            {
                rows.Add(new WalkerBinaryRow(binary, eras, WalkerIdentity.Resolve(binary), null));
            }
            catch (Exception ex)
            {
                rows.Add(new WalkerBinaryRow(binary, eras, null, $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        var fingerprints = new SortedSet<string>(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var row in rows)
        {
            if (row.Error is not null)
            {
                fingerprints.Add(ErrorFingerprintToken);
                violations.Add(
                    $"era(s) {string.Join(",", row.Eras)}  {Path.GetFileName(row.BinaryPath)}  " +
                    $"ERROR resolving identity: {row.Error}");
                continue;
            }

            var id = row.Identity!;
            fingerprints.Add(id.SrcFingerprint);
            if (string.Equals(id.SrcFingerprint, WalkerIdentity.UnknownFingerprint, StringComparison.Ordinal))
            {
                violations.Add(
                    $"era(s) {string.Join(",", row.Eras)}  {Path.GetFileName(row.BinaryPath)}  " +
                    $"git={id.GitSha}  fingerprint=unknown (binary predates src-fingerprint — rebuild it)");
            }

            if (manifestFingerprintByEra is not null)
            {
                foreach (var era in row.Eras)
                {
                    if (manifestFingerprintByEra.TryGetValue(era, out var expected) &&
                        !string.IsNullOrEmpty(expected) &&
                        !string.Equals(expected, id.SrcFingerprint, StringComparison.Ordinal))
                    {
                        violations.Add(
                            $"era {era}  {Path.GetFileName(row.BinaryPath)}  binary reports " +
                            $"fingerprint={id.SrcFingerprint} but walker-manifest.json expects {expected} " +
                            "(the binary on disk does not match what the build harness produced)");
                    }
                }
            }
        }

        var expectFprint = Environment.GetEnvironmentVariable(ExpectFingerprintEnvVar);
        var verdict = EvaluateWalkerIdentityGate(
            fingerprints, violations.Count, opts.Commit, opts.AllowMixedWalkers, expectFprint);

        Console.Error.WriteLine($"extract: tool={toolSha} walkers={verdict.WalkersDisplay}");

        if (verdict.MixedOrUnverified)
        {
            Console.Error.WriteLine("extract: walker identity table (era(s)  binary  detail):");
            foreach (var row in rows)
            {
                var detail = row.Identity is { } id
                    ? $"git={id.GitSha}  fingerprint={id.SrcFingerprint}"
                    : $"ERROR: {row.Error}";
                Console.Error.WriteLine(
                    $"extract:   {string.Join(",", row.Eras)}  {Path.GetFileName(row.BinaryPath)}  {detail}");
            }
            foreach (var v in violations)
            {
                Console.Error.WriteLine($"extract:   VIOLATION: {v}");
            }
        }

        if (verdict.ExitCode == 78)
        {
            // Two DISTINCT reasons land here (EvaluateWalkerIdentityGate): the commit-path
            // mixed/unverified-set hard-fail, or the (unconditional) CS2_EXPECT_FPRINT tripwire. Pick
            // the message that matches which one actually fired so the operator isn't told to rebuild
            // eras when the real problem is a stale deployed image (or vice versa).
            if (opts.Commit && !opts.AllowMixedWalkers && verdict.MixedOrUnverified)
            {
                Console.Error.WriteLine(
                    "extract: WALKER IDENTITY GATE FAILED — mixed or unverified walker set. Rebuild all eras "
                    + "(scripts/build-era-walkers.*) and retry, or pass --allow-mixed-walkers.");
            }
            else
            {
                Console.Error.WriteLine(
                    $"extract: WALKER IDENTITY GATE FAILED — {ExpectFingerprintEnvVar}='{expectFprint}' does "
                    + $"not prefix-match the resolved walker fingerprint(s) [{string.Join(", ", fingerprints)}]. "
                    + "This host is running a stale or wrong walker image/binary set.");
            }
            return 78;
        }

        if (verdict.MixedOrUnverified)
        {
            Console.Error.WriteLine(
                opts.Commit
                    ? "extract: WARNING walker identity gate would have FAILED (--allow-mixed-walkers set; "
                      + "proceeding — never use this for a corpus-committing run)."
                    : "extract: WARNING mixed/unverified walker set (non-commit run; proceeding).");
        }

        return null;
    }

    /// <summary>
    /// Best-effort load of <c>natives/&lt;platform&gt;/walker-manifest.json</c> (written by
    /// scripts/build-era-walkers.* — may not exist yet): era id -&gt; its recorded
    /// <c>srcFingerprint</c>. A missing/malformed file is a documented
    /// SKIP of the cross-check (never a fail — the manifest is an ADDITIONAL check on top of the
    /// binary's own self-report, not a required input), surfaced as a warning so a genuinely corrupt
    /// manifest is not silently ignored.
    /// </summary>
    private static Dictionary<string, string?>? LoadWalkerManifestFingerprints(string manifestPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var result = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var era in doc.RootElement.EnumerateObject())
            {
                string? fprint = era.Value.TryGetProperty("srcFingerprint", out var v)
                    && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
                result[era.Name] = fprint;
            }
            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"extract: WARNING could not read '{manifestPath}' (skipping the manifest cross-check): "
                + $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>First <paramref name="length"/> chars of <paramref name="token"/> for display, or
    /// "unknown" when empty/null (never an empty banner segment).</summary>
    private static string ShortToken(string? token, int length)
        => string.IsNullOrEmpty(token) ? "unknown" : (token.Length <= length ? token : token[..length]);

    /// <summary>
    /// Resolve the selection to a concrete, Ordinal-ordered, de-duplicated build-id list.
    /// --build ids are forward builds (NOT required to be in the inventory); --all / --era / --pin
    /// select over the INVENTORY's <c>builds[]</c> — the full known corpus for the platform, whether
    /// or not each build has been walked/committed — via <see cref="EraWalkerResolver.EnumerateInventoryBuilds"/>.
    /// <c>--only-existing-builds</c> intersects that with the COMMITTED sets under
    /// <paramref name="repoRoot"/>'s artifacts/ (the legacy re-walk-only behavior). --backfill keeps
    /// its own artifacts/-driven rule.
    /// </summary>
    private static List<string> SelectBuilds(Options opts, string repoRoot, EraWalkerResolver? resolver)
    {
        if (opts.Builds.Count > 0)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var b in opts.Builds)
            {
                if (seen.Add(b))
                    list.Add(b);
            }
            return list.OrderBy(b => b, StringComparer.Ordinal).ToList();
        }

        if (opts.Backfill)
        {
            return SelectBackfill(repoRoot, opts.Platform);
        }

        if (resolver is null)
        {
            throw new InvalidOperationException(
                "--all / --era / --pin require an era resolver for inventory-build selection.");
        }

        // INVENTORY-driven: every build the inventory records for the platform, filtered by era/pin.
        IEnumerable<InventoryBuildRef> selected = resolver.EnumerateInventoryBuilds(opts.Platform);
        if (opts.Era is not null)
        {
            selected = selected.Where(r => string.Equals(r.Era, opts.Era, StringComparison.Ordinal));
        }
        else if (opts.Pin is not null)
        {
            // The provided pin is a prefix of the era's full hl2sdk SHA (pins may be truncated).
            selected = selected.Where(r => r.Pin.StartsWith(opts.Pin, StringComparison.Ordinal));
        }
        // opts.All -> no era/pin filter (every inventory build for the platform).

        IEnumerable<string> ids = selected.Select(r => r.Build);

        // --only-existing-builds: restrict to builds ALREADY committed for this platform. This is the
        // legacy "re-walk only what exists" behavior, now an explicit modifier on --all/--era/--pin.
        if (opts.OnlyExistingBuilds)
        {
            var committed = new HashSet<string>(
                CommittedBuilds.Discover(repoRoot, opts.Platform, resolver).Select(c => c.Build),
                StringComparer.Ordinal);
            ids = ids.Where(committed.Contains);
        }

        return ids.Distinct(StringComparer.Ordinal).OrderBy(b => b, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// BACKFILL selection: every build directory under <paramref name="repoRoot"/>'s artifacts/ that
    /// does NOT yet have a committed set for <paramref name="platform"/> AND whose input binaries are
    /// available. This is the "produce the missing-platform set for every already-committed build"
    /// case (formerly scripts/extract-linux-all.sh's loop): a build dir exists because some platform
    /// is already committed, so a dir lacking this platform's entity_schema.json needs backfilling;
    /// builds whose binaries are not on disk are skipped (they cannot be walked under --no-acquire).
    /// Ordinal-ordered, so the batch summary is stable.
    /// </summary>
    private static List<string> SelectBackfill(string repoRoot, string platform)
    {
        var artifactsRoot = Path.Combine(repoRoot, "artifacts");
        if (!Directory.Exists(artifactsRoot))
        {
            return new List<string>();
        }

        var result = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(artifactsRoot))
        {
            var build = Path.GetFileName(dir);
            if (build.Length == 0 || !build.All(char.IsDigit))
            {
                continue;   // only real numeric build dirs (skip stray dirs).
            }
            if (File.Exists(Path.Combine(dir, platform, "entity_schema.json")))
            {
                continue;   // this platform's set is already committed — nothing to backfill.
            }
            if (!TryResolveBinariesDir(build, platform, out _, out _))
            {
                continue;   // input binaries not on disk — cannot walk this build (skip, not fail).
            }
            result.Add(build);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Extract ONE build. Fail-isolated (an unexpected throw is classified Failed, never aborting a
    /// batch run). The extract itself is the full fail-loud <see cref="RunExtract"/> path (it never
    /// leaves a partial set).
    /// </summary>
    private static BuildOutcome RunOneBuild(
        string build, Options opts, string repoRoot,
        Func<IWalkerRunner>? runnerFactory, EraWalkerResolver? eraResolver, bool gateFromResolver, bool batch,
        Func<string, string, int>? acquire)
    {
        // TARGET. --commit -> the repo's artifacts/<build>/<platform> (clobber, no git); otherwise
        // the off-repo <OutRoot>/<build>/<platform>.
        var outDir = opts.Commit
            ? Path.GetFullPath(Path.Combine(repoRoot, "artifacts", build, opts.Platform))
            : Path.GetFullPath(Path.Combine(opts.OutRoot, build, opts.Platform));

        string era = "";
        try
        {
            // SKIP-already-present: BATCH off-repo only. A single forward --build always re-extracts
            // (today's behavior); --commit always clobbers (--force implied).
            if (batch && !opts.Commit && !opts.Force &&
                File.Exists(Path.Combine(outDir, "entity_schema.json")))
            {
                return new BuildOutcome(
                    new BuildResult(build, era, Status.Skipped, "output already present (use --force)"), 0);
            }

            // SNAPSHOT-BEFORE-CLOBBER (--commit + --verify): capture the EXISTING committed CORE's
            // normalized SHAs IN MEMORY before RunExtract promotes over them, so --verify there is a
            // meaningful prior-vs-promoted compare (a self-compare after the clobber is always clean).
            CoreSnapshot? priorCommitted = (opts.Commit && opts.Verify) ? SnapshotCore(outDir) : null;

            int code = RunExtract(
                build, opts.Platform, outDir, runnerFactory, eraResolver, gateFromResolver,
                opts.NoAcquire, acquire, opts.NoChangelog, opts.NoLocalizationChangelog,
                doubleWalk: opts.Commit && !opts.SingleWalk, classCountGate: opts.Gate);

            if (code == 75)
            {
                // Layout-signature second-gate rejection. HARD inside RunExtract (it wrote NOTHING —
                // no clobber), classified Gated (not a run-level crash). Carries the raw code so a
                // single forward build surfaces it verbatim.
                return new BuildOutcome(
                    new BuildResult(build, era, Status.Gated, "layout gate (exit 75) — NOT promoted"), 75);
            }
            if (code == 77)
            {
                // Class-count-band gate rejection — now evaluated INSIDE RunExtract, BEFORE any
                // promote (see its "BLOCKING CLASS-BAND GATE" comment). HARD inside RunExtract (it
                // wrote NOTHING — no clobber under --commit OR off-repo alike), classified Gated. It
                // is structurally impossible to reach "Gated but promoted" anymore. Carries the raw
                // code so a single forward build surfaces it verbatim.
                return new BuildOutcome(
                    new BuildResult(build, era, Status.Gated, "class-band gate (exit 77) — NOT promoted"), 77);
            }
            if (code != 0)
            {
                return new BuildOutcome(new BuildResult(build, era, Status.Failed, $"extract exit {code}"), code);
            }

            // BUILD-LEVEL PROMOTE HOOK (--commit only): optional pics-appinfo.json + inventory record.
            // Idempotent + non-fatal (surfaced loudly, never reverts the promote).
            if (opts.Commit)
            {
                PromoteBuildLevel(build, opts, repoRoot, eraResolver);
            }

            // ERA (for the summary display only). The class-count GATE ITSELF now runs INSIDE
            // RunExtract (exit 77 above, before any promote); this just resolves the era name so the
            // per-build summary row still names it — a side effect the old post-hoc band lookup used
            // to provide incidentally. Because builds[].era names the EXACT era (a variant build
            // already resolves to its variant era), this is exact too.
            if (eraResolver is not null)
            {
                era = eraResolver.DetermineEffectiveClassBand(build, opts.Platform).Era;
            }

            // --verify.
            if (opts.Verify)
            {
                if (opts.Commit)
                {
                    // NON-BLOCKING snapshot compare: prior committed CORE (pre-clobber) vs the freshly
                    // promoted set. Review signal only — never a failure (exit 0).
                    var verifyNote = CompareToSnapshot(priorCommitted, outDir, out bool changed);
                    if (changed)
                        Console.Error.WriteLine($"extract: NOTE build {build}: {verifyNote}");
                    return new BuildOutcome(CommittedResult(build, era, verifyNote), 0);
                }
                return new BuildOutcome(Verify(build, era, repoRoot, opts.Platform, outDir), 0);
            }

            if (opts.Commit)
            {
                return new BuildOutcome(CommittedResult(build, era, verifyNote: null), 0);
            }

            return new BuildOutcome(new BuildResult(build, era, Status.Ok, "full"), 0);
        }
        catch (Exception ex)
        {
            // Fail-isolation: one build's unexpected error never aborts a batch run.
            return new BuildOutcome(new BuildResult(build, era, Status.Failed, $"{ex.GetType().Name}: {ex.Message}"), 0);
        }
    }

    /// <summary>
    /// Build the <see cref="Status.Committed"/> result for a promoted build, folding any non-blocking
    /// --verify note into the detail so the summary surfaces it LOUDLY. Always exit-0 territory (the
    /// class-band / layout gates never reach here — a violation returns straight out of RunOneBuild
    /// as Gated, before this is ever called).
    /// </summary>
    private static BuildResult CommittedResult(string build, string era, string? verifyNote)
    {
        var bits = new List<string> { "full" };
        if (verifyNote is not null)
            bits.Add(verifyNote);
        return new BuildResult(build, era, Status.Committed, string.Join("; ", bits));
    }

    /// <summary>
    /// --verify (off-repo): compare the freshly produced CORE artifacts under <paramref name="outDir"/>
    /// to the committed set under artifacts/&lt;build&gt;/&lt;platform&gt;/. JSON CORE files
    /// (including provenance.json) are compared tool-stamp-normalized (schemaVersion, tool.gitCommit,
    /// tool.semver, walkerGitSha, walkerSrcFingerprint); protos.descriptorset raw. CORE-CLEAN => Ok;
    /// any CORE difference => Regression (a hard failure).
    /// </summary>
    private static BuildResult Verify(string build, string era, string repoRoot, string platform, string outDir)
    {
        var committedDir = Path.Combine(repoRoot, "artifacts", build, platform);
        if (!Directory.Exists(committedDir))
        {
            return new BuildResult(build, era, Status.Regression, "no committed set to verify against");
        }

        var diffs = new List<string>();
        foreach (var f in CoreJson)
        {
            var committed = Path.Combine(committedDir, f);
            var produced = Path.Combine(outDir, f);
            if (!File.Exists(committed) && !File.Exists(produced))
                continue;
            if (NormalizedJsonSha(committed) != NormalizedJsonSha(produced))
                diffs.Add(f);
        }
        if (FileSha(Path.Combine(committedDir, CoreBin)) != FileSha(Path.Combine(outDir, CoreBin)))
        {
            diffs.Add(CoreBin);
        }

        return diffs.Count == 0
            ? new BuildResult(build, era, Status.Ok, "CORE-CLEAN")
            : new BuildResult(build, era, Status.Regression, "CORE diffs: " + string.Join(", ", diffs));
    }

    /// <summary>In-memory snapshot of a build's CORE artifacts' normalized SHAs, captured before a
    /// --commit promote clobbers them. <c>Existed</c> distinguishes "no prior committed set".</summary>
    private sealed record CoreSnapshot(bool Existed, IReadOnlyDictionary<string, string?> Shas);

    /// <summary>Capture the normalized CORE SHAs of the set currently at <paramref name="dir"/>.</summary>
    private static CoreSnapshot SnapshotCore(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return new CoreSnapshot(Existed: false, Shas: new Dictionary<string, string?>());
        }
        var shas = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var f in CoreJson)
        {
            shas[f] = NormalizedJsonSha(Path.Combine(dir, f));
        }
        shas[CoreBin] = FileSha(Path.Combine(dir, CoreBin));
        return new CoreSnapshot(Existed: true, Shas: shas);
    }

    /// <summary>
    /// Compare the freshly-promoted CORE set under <paramref name="outDir"/> to the pre-clobber
    /// <paramref name="prior"/> snapshot. Reports "new", "unchanged vs prior", or "CHANGED vs prior:
    /// &lt;files&gt;". NON-BLOCKING: <paramref name="changed"/> is informational only.
    /// </summary>
    private static string CompareToSnapshot(CoreSnapshot? prior, string outDir, out bool changed)
    {
        changed = false;
        if (prior is null || !prior.Existed)
        {
            return "new";
        }

        var diffs = new List<string>();
        foreach (var f in CoreJson)
        {
            prior.Shas.TryGetValue(f, out var before);
            var after = NormalizedJsonSha(Path.Combine(outDir, f));
            if (before is null && after is null)
                continue;
            if (before != after)
                diffs.Add(f);
        }
        prior.Shas.TryGetValue(CoreBin, out var binBefore);
        var binAfter = FileSha(Path.Combine(outDir, CoreBin));
        if (binBefore != binAfter)
            diffs.Add(CoreBin);

        if (diffs.Count == 0)
        {
            return "unchanged vs prior";
        }
        changed = true;
        return "CHANGED vs prior: " + string.Join(", ", diffs);
    }

    /// <summary>Print the run summary and return the exit code (non-zero iff any hard failure).</summary>
    private static int Summarize(IReadOnlyList<BuildResult> results)
    {
        int ok = results.Count(r => r.Status == Status.Ok);
        int committed = results.Count(r => r.Status == Status.Committed);
        int gated = results.Count(r => r.Status == Status.Gated);
        int skipped = results.Count(r => r.Status == Status.Skipped);
        int regressed = results.Count(r => r.Status == Status.Regression);
        int failed = results.Count(r => r.Status == Status.Failed);

        Console.Error.WriteLine("extract: ==================== SUMMARY ====================");
        foreach (var r in results)
        {
            Console.Error.WriteLine($"extract:   {r.Build}  {r.Status,-10} {r.Era}  {r.Detail}");
        }
        Console.Error.WriteLine(
            $"extract: ok={ok} committed={committed} gated={gated} skipped={skipped} " +
            $"regression={regressed} failed={failed} (of {results.Count})");

        // BATCH EXIT TRUTH: Regression / Failed / GATED all make the whole run exit non-zero. Gated
        // used to be silently exit-0 here — a build that had NOTHING promoted for it still reported
        // the batch as clean, so a Gated build could sail through a script/CI check unnoticed (the
        // exact incident class this WP closes: "Gated but promoted" AND "Gated but ignored"). The
        // per-build reason (65/70/75/76/77) is already in the printed per-build detail above; the
        // process exit is deliberately a flat 1 — "this batch needs attention" — not one of those
        // codes, since a single batch can mix multiple failure flavors across its builds.
        int hard = regressed + failed + gated;
        if (hard > 0)
        {
            Console.Error.WriteLine(
                $"extract: {hard} build(s) had a hard failure or were gated (regression/walker-fail/gate).");
            return 1;
        }
        return 0;
    }

    /// <summary>
    /// Build-level side effects that ride a successful per-platform --commit promote:
    ///   1. EMIT the optional build-level pics-appinfo.json IFF a forward-acquisition PICS capture
    ///      (pics-appinfo-capture.json) is present in the build's binaries/cache dir. captured_utc is
    ///      the freshly-promoted provenance's manifest time (never DateTime.Now). Absent capture =>
    ///      SKIP (historical builds have none; the artifact is optional).
    ///   2. FORWARD-CAPTURE the build into data/cs2-assets-inventory.json: append a new builds[] row
    ///      (era + content/binaries + date, from the promoted provenance) when the build is not
    ///      already in the inventory (Inventory/ForwardCaptureRecorder). A build already present is a
    ///      no-op — never mutates an existing row.
    /// Neither is a gate: a failure is surfaced LOUDLY but never reverts the promote. Idempotent.
    /// </summary>
    private static void PromoteBuildLevel(string build, Options opts, string repoRoot, EraWalkerResolver? eraResolver)
    {
        // (1) pics-appinfo.json — optional, capture-gated.
        try
        {
            var capturePath = FindPicsCapture(build, opts.Platform);
            if (capturePath is not null)
            {
                var capture = PicsAppInfoCapture.ReadFromFile(capturePath);
                var capturedUtc = ReadManifestCreatedUtc(repoRoot, build, opts.Platform);
                var outPath = Path.Combine(repoRoot, "artifacts", build, PicsAppInfoEmitter.FileName);
                new PicsAppInfoEmitter(SchemaFamily.Version).Emit(build, capturedUtc, capture, outPath);
                Console.Error.WriteLine(
                    $"extract: build {build}: wrote build-level {PicsAppInfoEmitter.FileName} (from {capturePath}).");
            }
            // Absent capture: SKIP (it is optional). No omissions entry, no failure.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"extract: WARNING build {build}: pics-appinfo emit failed (non-fatal, promote stands): " +
                $"{ex.GetType().Name}: {ex.Message}");
        }

        // (2) forward-capture the build into the inventory — idempotent, deterministic. Only a
        //     never-before-seen build is appended; a build already in builds[] is a no-op.
        if (eraResolver is null)
        {
            return;   // no resolver -> cannot resolve the era to record (test seam without a resolver).
        }
        try
        {
            var inventoryPath = Path.Combine(repoRoot, Inventory.InventoryCatalog.DefaultRelativePath);
            if (File.Exists(inventoryPath))
            {
                var outcome = Inventory.ForwardCaptureRecorder.RecordIfNew(
                    inventoryPath, repoRoot, build, opts.Platform, eraResolver);
                Console.Error.WriteLine(
                    $"extract: build {build}: inventory forward-capture {outcome} " +
                    $"({Inventory.InventoryCatalog.DefaultRelativePath}).");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"extract: WARNING build {build}: inventory forward-capture failed (non-fatal, promote stands): " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Locate the forward-acquisition PICS capture sidecar for (build, platform). Checked in order:
    /// the acquire-immune forward-capture dir (<c>cache/pics/&lt;build&gt;/&lt;platform&gt;</c>, written by
    /// the explicit <c>capture-pics</c> command — survives a binary-depot acquire wipe), then the
    /// operator override root (HostConfig.BinariesRoot), then the conventional
    /// cache/binaries/&lt;build&gt;/&lt;platform&gt; (where the best-effort auto-acquire capture lands).
    /// Null when none exists (capture is current-only).
    /// </summary>
    private static string? FindPicsCapture(string build, string platform)
    {
        var candidates = new List<string>
        {
            Path.Combine(PicsAppInfoCapture.ForwardCaptureDir(build, platform), PicsAppInfoCapture.FileName),
        };
        var binariesRoot = HostConfig.BinariesRoot;
        if (!string.IsNullOrEmpty(binariesRoot))
        {
            candidates.Add(Path.Combine(binariesRoot, build, platform, PicsAppInfoCapture.FileName));
        }
        candidates.Add(Path.GetFullPath(Path.Combine("cache", "binaries", build, platform, PicsAppInfoCapture.FileName)));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Read the freshly-promoted provenance.json's steam.manifest_created_utc as the pics-appinfo
    /// captured_utc (a build-input timestamp, never DateTime.Now). "" when absent.
    /// </summary>
    private static string ReadManifestCreatedUtc(string repoRoot, string build, string platform)
        => Cache.ProvenanceReader.ReadManifestCreatedUtc(
            Path.Combine(repoRoot, "artifacts", build, platform, "provenance.json"));

    /// <summary>
    /// Count classes in an emitted entity_schema.json by parsing it through the generated proto3
    /// message (the single source of the artifact's shape).
    /// </summary>
    private static int CountClasses(string entitySchemaPath)
    {
        var parser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        var doc = parser.Parse<Schemas.EntitySchema>(File.ReadAllText(entitySchemaPath));
        return doc.Classes.Count;
    }

    private static string? FileSha(string path)
        => File.Exists(path) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) : null;

    /// <summary>
    /// SHA-256 of a JSON CORE artifact with the schemaVersion / tool.gitCommit / tool.semver /
    /// walkerGitSha / walkerSrcFingerprint values normalized to a placeholder, so version-stamp /
    /// git-SHA / walker-identity drift is never reported as a CORE regression.
    /// </summary>
    private static string? NormalizedJsonSha(string path)
    {
        if (!File.Exists(path))
            return null;
        var text = File.ReadAllText(path);
        text = SchemaVersionRegex().Replace(text, "\"schemaVersion\": \"<NORM>\"");
        text = ToolGitShaRegex().Replace(text, "\"gitCommit\": \"<NORM>\"");
        text = ToolSemverRegex().Replace(text, "\"semver\": \"<NORM>\"");
        text = WalkerGitShaRegex().Replace(text, "\"walkerGitSha\": \"<NORM>\"");
        text = WalkerSrcFingerprintRegex().Replace(text, "\"walkerSrcFingerprint\": \"<NORM>\"");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>
    /// Parse + validate the full-extract selection args. Exactly one selection family is required
    /// (--build... | --all | --era | --pin); --platform is required (or appsettings ExtractPlatform).
    /// --out defaults to ExtractOutRoot, else ./extract-out/. --gate is on by default (--no-gate off).
    /// </summary>
    private static bool TryParseSelection(string[] args, out Options opts, out string error)
    {
        opts = null!;
        error = "";

        var builds = new List<string>();
        bool all = false, backfill = false, onlyExistingBuilds = false, force = false, verify = false,
             noGate = false, commit = false, noAcquire = false, noChangelog = false, noLocalizationChangelog = false,
             singleWalk = false, allowMixedWalkers = false;
        string? era = null, pin = null, platform = null, outRoot = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "--all":
                    all = true;
                    break;
                case "--backfill":
                    backfill = true;
                    break;
                case "--only-existing-builds":
                    onlyExistingBuilds = true;
                    break;
                case "--force":
                    force = true;
                    break;
                case "--verify":
                    verify = true;
                    break;
                case "--no-gate":
                    noGate = true;
                    break;
                case "--commit":
                    commit = true;
                    break;
                case "--no-acquire":
                    noAcquire = true;
                    break;
                case "--no-changelog":
                    noChangelog = true;
                    break;
                case "--no-localization-changelog":
                    noLocalizationChangelog = true;
                    break;
                case "--single-walk":
                    // Escape hatch: forces the commit-path determinism gate OFF (default ON under
                    // --commit). See RunExtract's doubleWalk doc comment.
                    singleWalk = true;
                    break;
                case "--allow-mixed-walkers":
                    // Escape hatch: bypasses the commit-path WALKER IDENTITY GATE (exit 78) —
                    // a mixed/unverified per-era walker set is warned LOUDLY instead of blocking the
                    // commit. See PreflightWalkerIdentity. Never use it for a corpus-committing run;
                    // it exists for local iteration against a deliberately partial era rebuild.
                    allowMixedWalkers = true;
                    break;
                case "--era":
                    if (!NextSel(args, ref i, a, out era, out error))
                        return false;
                    break;
                case "--pin":
                    if (!NextSel(args, ref i, a, out pin, out error))
                        return false;
                    break;
                case "--platform":
                    if (!NextSel(args, ref i, a, out platform, out error))
                        return false;
                    break;
                case "--out":
                    if (!NextSel(args, ref i, a, out outRoot, out error))
                        return false;
                    break;
                case "--build":
                    if (!NextSel(args, ref i, a, out var b, out error))
                        return false;
                    builds.Add(b!);
                    break;
                default:
                    error = $"extract: unknown argument '{a}'. Run 'extract --help'.";
                    return false;
            }
        }

        int modes = (all ? 1 : 0) + (backfill ? 1 : 0) + (era is not null ? 1 : 0) + (pin is not null ? 1 : 0) + (builds.Count > 0 ? 1 : 0);
        if (modes == 0)
        {
            error = "extract: a selection is required — one of --build <id> (repeatable) | --all | --backfill | --era <key> | --pin <sha>.";
            return false;
        }
        if (modes > 1)
        {
            error = "extract: --build / --all / --backfill / --era / --pin are mutually exclusive; pass exactly one selection family.";
            return false;
        }

        // --only-existing-builds is a MODIFIER on the inventory selectors, not a selector itself: it
        // restricts --all/--era/--pin to builds already committed for the platform. It is meaningless
        // with an explicit --build list (those are named directly) or --backfill (already scoped to
        // NOT-yet-committed builds).
        if (onlyExistingBuilds && !(all || era is not null || pin is not null))
        {
            error = "extract: --only-existing-builds is a modifier for --all / --era / --pin " +
                    "(restricts them to builds already committed for the platform); it cannot be used " +
                    "alone or with --build / --backfill.";
            return false;
        }

        // --platform: explicit wins, else config default (no env key reserved — operator input).
        platform ??= HostConfig.ExtractPlatform;
        if (string.IsNullOrEmpty(platform))
        {
            error = "extract: --platform <linux-x86_64|windows-x86_64> is required (or set ExtractPlatform in appsettings.json).";
            return false;
        }

        // --commit overrides --out toward artifacts/: the set is promoted into the repo's
        // artifacts/<build>/<platform> (derived from repoRoot, NOT OutRoot), so --out is ignored.
        if (commit && outRoot is not null)
        {
            Console.Error.WriteLine(
                "extract: --out is ignored with --commit (sets are promoted into artifacts/<build>/<platform>).");
            outRoot = null;
        }

        // --out: explicit wins, else config ExtractOutRoot, else a cwd-relative default. Irrelevant
        // (never read) in --commit mode, where RunOneBuild targets artifacts/ via repoRoot.
        outRoot ??= HostConfig.ExtractOutRoot ?? "extract-out";

        opts = new Options(
            All: all, Backfill: backfill, OnlyExistingBuilds: onlyExistingBuilds, Era: era, Pin: pin,
            Builds: builds, Platform: platform, OutRoot: Path.GetFullPath(outRoot),
            Gate: !noGate, Force: force, Verify: verify, Commit: commit, NoAcquire: noAcquire,
            NoChangelog: noChangelog, NoLocalizationChangelog: noLocalizationChangelog,
            SingleWalk: singleWalk, AllowMixedWalkers: allowMixedWalkers);
        return true;
    }

    private static bool NextSel(string[] args, ref int i, string flag, out string? value, out string error)
    {
        error = "";
        if (i + 1 >= args.Length)
        {
            value = null;
            error = $"extract: {flag} requires a value.";
            return false;
        }
        value = args[++i];
        return true;
    }
}
