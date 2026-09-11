// Extract — WALKER FINGERPRINT DRIFT GUARD (commit path).
//
// The walker identity gate next door (ExtractCommand.Batch.cs PreflightWalkerIdentity) asks whether
// the resolved walker SET is internally coherent: one src-fingerprint across every selected era, no
// "unknown", no natives/<platform>/walker-manifest.json disagreement. It cannot see the one thing
// that actually protects the corpus — whether that coherent set is the SAME walker that produced
// the sets ALREADY COMMITTED. A uniform, self-consistent, perfectly healthy walker passes it and
// still rewrites every artifact it re-emits.
//
// That is not hypothetical. A natives/ tree left over from a pre-history-reset build (fingerprint
// f06f88f8…, gitSha b53b4844… — a commit that no longer resolves in this repo; natives/ is
// gitignored, so those binaries were never versioned) re-walked build 11418830 under --commit and
// rewrote entity_schema.json with 4,500+ semantically changed lines — thousands of ATOMIC_PLAIN
// turning into ATOMIC_UNSPECIFIED — then promoted it into artifacts/. The run warned and proceeded;
// it was caught only by reading the diff by hand. Over a 388-build batch the entire corpus would
// have been rewritten by an unidentifiable walker, every diff reading exactly like a real engine
// change.
//
// So: under --commit, ONCE for the whole selection and BEFORE build 1 resolves an era, walks
// anything, or creates a staging dir, every selected (build, platform) whose COMMITTED set records
// a provenance.tool.walkerSrcFingerprint is compared against the fingerprint of the walker this run
// would actually use. Fail loud: a definite mismatch REFUSES the whole run (exit 78) naming the
// build, the platform, both fingerprints, the walker's gitSha and binary, and the
// --allow-walker-change opt-in — with no era resolved, nothing walked, no staging dir, no promote,
// and never a half-rewritten corpus. A refusal here can only ever leave the corpus exactly as it
// was.
//
// It blocks ONLY on a KNOWN mismatch. A committed set that records no fingerprint, and a walker
// whose own identity does not resolve, each WARN and proceed: blocking on "unknown" would make the
// tool unusable against sets committed before the fingerprint line existed or on a host whose
// walker cannot be interrogated, while blocking on a known mismatch is the entire point.
//
// COMPLEMENTARY TO — never a duplicate of — CS2_EXPECT_FPRINT (see ExpectFingerprintEnvVar). That
// tripwire compares the resolved walker against a value the OPERATOR supplies ("this host must be
// running the image I built"): opt-in per run, and blind to what the corpus contains. This guard
// compares it against what the CORPUS ITSELF carries ("this rewalk must be the walker that wrote
// these sets"): no operator input, on by default under --commit. Either fires without the other —
// an operator can assert the right image and still be re-walking sets a different walker built, and
// a run with no CS2_EXPECT_FPRINT set is still held to the corpus's own record.

using System.Text;

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Walker;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Cli;

internal static partial class ExtractCommand
{
    /// <summary>
    /// Exit code for a REFUSED walker change. 78 is this file family's WALKER IDENTITY code, and
    /// this is a walker-identity refusal: the same failure class as PreflightWalkerIdentity's
    /// mixed/unverified set and the <see cref="ExpectFingerprintEnvVar"/> tripwire (evaluated ONCE
    /// for the whole selection, before build 1 — "this run cannot be trusted to write the corpus"),
    /// distinguished from those two by its own message banner exactly as they are distinguished from
    /// each other. Deliberately NOT 65: RunExtract's exit-code note reserves 65/70 for an input error
    /// or a walker crash — a DIFFERENT failure class — so a script that tells "the walker died" from
    /// "the run was refused" keeps working.
    /// </summary>
    internal const int WalkerDriftExitCode = 78;

    /// <summary>
    /// The file whose presence means a committed set EXISTS for a (build, platform) — the same marker
    /// <see cref="SelectBackfill"/> and RunOneBuild's skip-already-present check use. A build dir
    /// without it is one this run would CREATE, never one it would rewrite.
    /// </summary>
    private const string CommittedSetMarkerFileName = "entity_schema.json";

    /// <summary>What the committed set's recorded fingerprint and the resolved walker's fingerprint
    /// say when put side by side. Only <see cref="Drift"/> ever blocks.</summary>
    internal enum WalkerDriftDecision
    {
        /// <summary>The committed set records no usable fingerprint — nothing to compare against.
        /// WARN and proceed (a set committed before the fingerprint line existed is not evidence of
        /// anything).</summary>
        NoRecordedFingerprint,

        /// <summary>The walker reports no usable fingerprint (an unresolvable binary, or one that
        /// predates the src-fingerprint line). WARN and proceed — the guard could not run.</summary>
        WalkerUnidentified,

        /// <summary>Both sides known and equal: this run's walker is the one that built the set.</summary>
        Match,

        /// <summary>Both sides known and DIFFERENT: re-emitting would change which walker built the
        /// committed set. The only blocking verdict.</summary>
        Drift,
    }

    /// <summary>
    /// PURE decision core of the drift guard — no I/O, no process launches, no Console writes — so
    /// the block/warn/proceed matrix is unit-testable without a corpus or a walker binary. Internal
    /// (not private): the test suite exercises this directly via InternalsVisibleTo, exactly as it
    /// does <see cref="EvaluateWalkerIdentityGate"/>.
    /// </summary>
    internal static WalkerDriftDecision EvaluateWalkerDrift(
        string? recordedFingerprint, string? resolvedFingerprint)
    {
        if (!IsUsableFingerprint(recordedFingerprint))
        {
            return WalkerDriftDecision.NoRecordedFingerprint;
        }
        if (!IsUsableFingerprint(resolvedFingerprint))
        {
            return WalkerDriftDecision.WalkerUnidentified;
        }
        return string.Equals(recordedFingerprint, resolvedFingerprint, StringComparison.Ordinal)
            ? WalkerDriftDecision.Match
            : WalkerDriftDecision.Drift;
    }

    /// <summary>
    /// A fingerprint that can be COMPARED. Empty is "never recorded";
    /// <see cref="WalkerIdentity.UnknownFingerprint"/> is the documented "this binary predates the
    /// src-fingerprint line" sentinel and <see cref="ErrorFingerprintToken"/> the "identity would not
    /// resolve" one — both are the ABSENCE of a fingerprint spelled out loud, never a value, so
    /// comparing them would be blocking on unknown.
    /// </summary>
    private static bool IsUsableFingerprint(string? fingerprint)
        => !string.IsNullOrWhiteSpace(fingerprint)
           && !string.Equals(fingerprint, WalkerIdentity.UnknownFingerprint, StringComparison.Ordinal)
           && !string.Equals(fingerprint, ErrorFingerprintToken, StringComparison.Ordinal);

    /// <summary>
    /// Read what the COMMITTED set at <c>&lt;artifactsRoot&gt;/&lt;build&gt;/&lt;platform&gt;/</c>
    /// records as the walker that built it. Returns false when there is no committed set for this
    /// (build, platform) at all — a brand-new build, which this guard must NEVER block. Returns true
    /// with an EMPTY <paramref name="fingerprint"/> when the set exists but records nothing usable
    /// (no provenance.json, an unreadable one, or an empty tool.walkerSrcFingerprint): the
    /// warn-and-proceed degenerate case, never a refusal — an unreadable committed provenance is not
    /// evidence that the walker changed.
    /// </summary>
    internal static bool TryReadCommittedWalkerFingerprint(
        string artifactsRoot, string build, string platform, out string fingerprint)
    {
        fingerprint = "";
        var setDir = Path.Combine(artifactsRoot, build, platform);
        if (!File.Exists(Path.Combine(setDir, CommittedSetMarkerFileName)))
        {
            return false;
        }

        var provenancePath = Path.Combine(setDir, ArtifactSet.ProvenanceFileName);
        if (!File.Exists(provenancePath))
        {
            return true;
        }

        try
        {
            var provenance = LenientProvenanceParser.Parse<Schemas.Provenance>(
                File.ReadAllText(provenancePath));
            fingerprint = provenance.Tool?.WalkerSrcFingerprint ?? "";
        }
        catch (Exception ex) when (
            ex is InvalidProtocolBufferException or InvalidJsonException
                or IOException or UnauthorizedAccessException)
        {
            fingerprint = "";
        }
        return true;
    }

    /// <summary>One walker binary's resolved identity, or the reason it would not resolve.</summary>
    private sealed record ResolvedWalker(WalkerIdentity? Identity, string? Error);

    /// <summary>One (build, platform) whose committed set was built by a different walker than the
    /// one this run would use.</summary>
    private sealed record WalkerDriftRow(
        string Build, string Platform, string Recorded, string Resolved, string GitSha, string BinaryPath);

    /// <summary>
    /// WALKER FINGERPRINT DRIFT GUARD (see the file header for the incident it exists for). Runs ONCE
    /// for the whole selection, immediately after the walker identity gate and still BEFORE the
    /// per-build loop, so a corpus-rewriting rewalk is refused before build 1 resolves its era, walks
    /// anything, or stages a byte.
    ///
    /// It fires ONLY when every one of these holds, and behaves exactly as it did before this guard
    /// existed in every other combination:
    ///   1. the run is <c>--commit</c> (an off-repo run writes under OutRoot and can never rewrite a
    ///      committed set — a different walker there is precisely the experiment the operator asked
    ///      for, so it is not even evaluated);
    ///   2. a real walker binary will run — production wiring (<paramref name="eraResolver"/> plus an
    ///      <paramref name="identitySource"/>). The fake-runner test seam launches no binary, so
    ///      there is no walker identity to compare and the guard is inert;
    ///   3. the target set ALREADY EXISTS (a brand-new build is never blocked);
    ///   4. that set records a usable <c>provenance.tool.walkerSrcFingerprint</c>;
    ///   5. the resolved walker reports a usable fingerprint of its own;
    ///   6. the two DIFFER.
    /// 4 and 5 failing are the degenerate cases: each WARNS (naming what could not be compared) and
    /// proceeds. <c>--allow-walker-change</c> turns a refusal into one audit line per affected set
    /// recording the old -&gt; new transition, so an intentional rewalk is visible in the run log
    /// instead of silent.
    /// </summary>
    /// <param name="identitySource">
    /// Resolves a walker binary path to its self-reported identity — production binds this to
    /// <see cref="WalkerIdentity.Resolve"/> (&lt;1s, memoized per path). Null means no real walker
    /// will run (the fake-runner seam): the guard is inert, never a refusal about a walker that never
    /// runs.
    /// </param>
    /// <returns>The exit code to abort the whole run with, or null to proceed.</returns>
    private static int? PreflightWalkerFingerprintDrift(
        IReadOnlyList<string> builds, Options opts, string repoRoot, EraWalkerResolver? eraResolver,
        Func<string, WalkerIdentity>? identitySource)
    {
        if (!opts.Commit || eraResolver is null || identitySource is null)
        {
            return null;
        }

        var artifactsRoot = Path.Combine(repoRoot, "artifacts");
        var walkerByBinary = new Dictionary<string, ResolvedWalker>(StringComparer.OrdinalIgnoreCase);
        var drifted = new List<WalkerDriftRow>();
        var unrecorded = new List<string>();
        var unidentified = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var build in builds)
        {
            if (!TryReadCommittedWalkerFingerprint(artifactsRoot, build, opts.Platform, out var recorded))
            {
                continue;   // no committed set: this run CREATES it, it cannot change who built it.
            }

            // Which walker this build would be walked by. An era that will not resolve is
            // RunOneBuild's failure to report per-build (exit 75) when the loop reaches it — never
            // this guard's to turn into a drift refusal.
            string binaryPath;
            try
            {
                binaryPath = eraResolver.Resolve(build, opts.Platform).WalkerBinaryPath;
            }
            catch
            {
                continue;
            }

            if (!walkerByBinary.TryGetValue(binaryPath, out var walker))
            {
                try
                {
                    walker = new ResolvedWalker(identitySource(binaryPath), null);
                }
                catch (Exception ex)
                {
                    walker = new ResolvedWalker(null, $"{ex.GetType().Name}: {ex.Message}");
                }
                walkerByBinary[binaryPath] = walker;
            }

            switch (EvaluateWalkerDrift(recorded, walker.Identity?.SrcFingerprint))
            {
                case WalkerDriftDecision.NoRecordedFingerprint:
                    unrecorded.Add(build);
                    break;
                case WalkerDriftDecision.WalkerUnidentified:
                    unidentified[binaryPath] = walker.Error
                        ?? "the binary reports no src-fingerprint (it predates that line — rebuild it)";
                    break;
                case WalkerDriftDecision.Drift:
                    drifted.Add(new WalkerDriftRow(
                        build, opts.Platform, recorded, walker.Identity!.SrcFingerprint,
                        walker.Identity.GitSha, binaryPath));
                    break;
                case WalkerDriftDecision.Match:
                default:
                    break;   // the corpus and this run agree — silent, as a clean run must be.
            }
        }

        // Degenerate cases, aggregated so a 388-build batch warns in lines rather than screens.
        foreach (var (binaryPath, reason) in unidentified)
        {
            Console.Error.WriteLine(
                $"extract: WARNING walker drift guard could NOT run for '{Path.GetFileName(binaryPath)}': " +
                $"{reason}. A walker change against the committed set(s) that binary serves cannot be " +
                "detected this run.");
        }
        if (unrecorded.Count > 0)
        {
            Console.Error.WriteLine(
                $"extract: WARNING walker drift guard: {unrecorded.Count} committed set(s) record no " +
                $"tool.walkerSrcFingerprint ({JoinCapped(unrecorded, 8)}) — nothing to compare this run's " +
                "walker against; proceeding.");
        }

        if (drifted.Count == 0)
        {
            return null;
        }

        if (opts.AllowWalkerChange)
        {
            // AUDIT TRAIL. An intentional rewalk legitimately changes every set it touches, but it
            // must never be INVISIBLE: one line per affected set, full fingerprints, so the run log
            // alone answers "which walker wrote this set, and which one replaced it".
            foreach (var row in drifted)
            {
                Console.Error.WriteLine(
                    $"extract: WALKER CHANGE AUTHORISED (build {row.Build}, {row.Platform}): " +
                    $"{row.Recorded} -> {row.Resolved} (--allow-walker-change)");
            }
            return null;
        }

        Console.Error.WriteLine(BuildWalkerDriftRefusal(drifted));
        return WalkerDriftExitCode;
    }

    /// <summary>
    /// The refusal text. Shaped on <see cref="TryGuardContentStoreGeneration"/>'s STALE CONTENT STORE
    /// message — what is wrong, what it would silently do to the corpus, the remedy, and
    /// "No artifacts written." — with a per-set block naming the build, the platform, the fingerprint
    /// the committed set carries, the fingerprint of the walker about to be used, and that walker's
    /// gitSha and binary.
    /// </summary>
    private static string BuildWalkerDriftRefusal(IReadOnlyList<WalkerDriftRow> rows)
    {
        var text = new StringBuilder();
        text.Append(rows.Count == 1
            ? $"extract: WALKER FINGERPRINT DRIFT for (build {rows[0].Build}, {rows[0].Platform}) — the " +
              "committed set was built by a DIFFERENT walker than the one this run would use."
            : $"extract: WALKER FINGERPRINT DRIFT — {rows.Count} committed sets were built by a DIFFERENT " +
              "walker than the one this run would use.");

        foreach (var row in rows)
        {
            var gitSha = IsUsableFingerprint(row.GitSha) ? $"gitSha {row.GitSha}" : "gitSha not reported";
            if (rows.Count > 1)
            {
                // The one-set header already names the build and platform; a batch needs a per-set one.
                text.Append(Environment.NewLine).Append($"  (build {row.Build}, {row.Platform})");
            }
            text.Append(Environment.NewLine)
                .Append($"      artifacts/{row.Build}/{row.Platform}/{ArtifactSet.ProvenanceFileName} records " +
                        $"walkerSrcFingerprint {row.Recorded}")
                .Append(Environment.NewLine)
                .Append($"      this run would walk it with a walker reporting {row.Resolved} " +
                        $"({gitSha}, {row.BinaryPath})");
        }

        text.Append(Environment.NewLine)
            .Append("  A walker change rewrites the ARTIFACTS, not just the tool stamp: the same input ")
            .Append("binaries walked by a")
            .Append(Environment.NewLine)
            .Append("  different walker have silently rewritten thousands of semantic lines into the ")
            .Append("committed corpus")
            .Append(Environment.NewLine)
            .Append("  (ATOMIC_PLAIN -> ATOMIC_UNSPECIFIED), indistinguishable in the diff from a real ")
            .Append("engine change.")
            .Append(Environment.NewLine)
            .Append("  Walk with the walker the corpus was built with (scripts/build-era-walkers.*), or — ")
            .Append("if this rewalk")
            .Append(Environment.NewLine)
            .Append("  IS intentional — authorise it for the whole run:")
            .Append(Environment.NewLine)
            .Append("      cs2-schema-tracker extract … --commit --allow-walker-change")
            .Append(Environment.NewLine)
            .Append("  No artifacts written.");
        return text.ToString();
    }

    /// <summary>Join at most <paramref name="cap"/> items, summarizing the rest as "+N more", so a
    /// batch-sized list stays one readable line.</summary>
    private static string JoinCapped(List<string> items, int cap)
        => items.Count <= cap
            ? string.Join(", ", items)
            : string.Join(", ", items.Take(cap)) + $", +{items.Count - cap} more";
}
