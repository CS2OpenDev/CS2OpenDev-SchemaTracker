// The all-or-nothing artifact-set completeness validator (host-owned).
//
// Validates the FINAL ON-DISK STATE of one or more build directories: it asserts that what landed
// under artifacts/<build_id>/ is a LEGAL all-or-nothing shape. It does NOT reconstruct intent from
// a diff and it does NOT git-diff — "which build dirs changed" stays in the CI driver, which passes
// the changed build dirs in via the CLI.
//
// A commit to artifacts/ is legal in EXACTLY one of two shapes (README "What it produces"):
//   (A) single-platform: one complete (build, platform) set (a clean single-platform set needs
//       NO omissions.json; the other platform lands later); OR
//   (B) full-build: each canonical platform is either present-and-complete OR accounted-for in
//       the build-level omissions.json with a valid reason.
// Any partial cross-platform set with an unaccounted-for platform is FORBIDDEN.
//
// omissions.json is present ONLY for builds that have a recorded omission (a not-dumped platform
// or a content artifact absent for the era). A clean build carries none; an ABSENT omissions.json
// is treated as omissions:[] (absent = clean). The anti-silent-skip guarantee is preserved: a real
// hole (e.g. content depot acquired in provenance.json but a content-gated file missing, with no
// omission record) STILL fails loud, because contentOmitted is empty when the file is absent.
//
// Completeness of one platform dir (README "What it produces"):
//   - every ArtifactSet.RequiredFiles file present;
//   - every ArtifactSet.RequiredNonEmptyDirs directory present AND non-empty;
//   - provenance.json parses (it is also a required file); and IFF it lists the content depot
//     (ArtifactSet.ContentDepotId) under steam.depots[].depotId, every content-depot-gated file
//     (ArtifactSet.ContentDepotGatedFiles) is present too, AND provenance.localization is populated
//     (the build-on-demand localization.json is not committed, but its fingerprint must be recorded
//     so an emit-localization rebuild is byte-verifiable) — unless localization was omitted this era.
//
// Determinism: stable iteration order (sorted), no timestamps, no network. The valid omission
// reasons + the platform set + the file lists come from the proto / ArtifactSet (the source of
// truth), never re-hardcoded here.
//
// This type is a PURE validator: it only READS under the artifacts root; it never writes, and it
// never invokes git. Fail-loud is realized by the caller: a non-empty Violations list means a
// non-zero exit (there is nothing to write).

using System.Globalization;

using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Artifacts;

/// <summary>One completeness violation, with the specific reason.</summary>
public sealed record ArtifactSetViolation(string BuildId, string Message)
{
    public override string ToString() => $"VIOLATION: {Message}";
}

/// <summary>The verdict for one build directory.</summary>
public sealed record BuildVerdict(string BuildId, IReadOnlyList<ArtifactSetViolation> Violations)
{
    public bool Passed => Violations.Count == 0;
}

/// <summary>The aggregate verdict across every validated build directory.</summary>
public sealed record ArtifactSetReport(IReadOnlyList<BuildVerdict> Builds)
{
    /// <summary>True iff no build directory had any violation.</summary>
    public bool Passed => Builds.All(b => b.Passed);

    /// <summary>Every violation across every build, in build order.</summary>
    public IReadOnlyList<ArtifactSetViolation> AllViolations =>
        Builds.SelectMany(b => b.Violations).ToList();
}

/// <summary>
/// Validates that committed <c>(build, platform)</c> artifact sets are a legal all-or-nothing
/// shape. Pure + read-only; see file header.
/// </summary>
public sealed class ArtifactSetValidator
{
    // Strict parse so a genuinely malformed provenance.json / omissions.json fails loud (treated as
    // a violation, not swallowed). Unknown fields are tolerated (a newer-family file may carry fields
    // this build of the host does not know) — the gate reasons only about presence + depots +
    // omission reasons, not the full shape (that is the round-trip test's job).
    private static readonly JsonParser TolerantParser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private readonly string _artifactsRoot;

    /// <param name="artifactsRoot">The artifacts/ root directory (absolute or relative).</param>
    public ArtifactSetValidator(string artifactsRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRoot);
        _artifactsRoot = artifactsRoot;
    }

    /// <summary>
    /// Validate every build directory directly under the artifacts root (working-tree /
    /// belt-and-suspenders mode). Returns a clean report when the root does not exist or is empty.
    /// </summary>
    public ArtifactSetReport ValidateAll()
    {
        if (!Directory.Exists(_artifactsRoot))
        {
            return new ArtifactSetReport(Array.Empty<BuildVerdict>());
        }

        var buildIds = Directory.EnumerateDirectories(_artifactsRoot)
            .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(n => !string.IsNullOrEmpty(n))
            // The fixed-path schema-evolution dir sits directly under the artifacts root but is NOT a
            // build set (it holds one file per platform, checked by the repo-level evolution check).
            .Where(n => !string.Equals(n, ArtifactSet.SchemaEvolutionDirName, StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        return ValidateBuilds(buildIds);
    }

    /// <summary>
    /// Validate exactly the given build ids (commit-gating mode — CI passes the ids the diff
    /// touched). A build id whose directory is absent on disk (a pure deletion) is skipped:
    /// a deletion is not a partial-set landing.
    /// </summary>
    public ArtifactSetReport ValidateBuilds(IEnumerable<string> buildIds)
    {
        ArgumentNullException.ThrowIfNull(buildIds);

        var ordered = buildIds
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var verdicts = new List<BuildVerdict>(ordered.Count);
        foreach (var buildId in ordered)
        {
            verdicts.Add(ValidateBuild(buildId));
        }
        return new ArtifactSetReport(verdicts);
    }

    /// <summary>Validate one build directory. Always returns a verdict (never throws on shape).</summary>
    public BuildVerdict ValidateBuild(string buildId)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        var v = new List<ArtifactSetViolation>();
        void Violate(string msg) => v.Add(new ArtifactSetViolation(buildId, msg));

        var buildPath = Path.Combine(_artifactsRoot, buildId);
        if (!Directory.Exists(buildPath))
        {
            // Build dir named but gone on disk (pure deletion commit) — nothing to gate.
            return new BuildVerdict(buildId, Array.Empty<ArtifactSetViolation>());
        }

        // 1. omissions.json is ABSENT ⇒ clean: treat it as an empty omissions:[] document and
        //    CONTINUE validation. omissions.json is present only for builds with a recorded omission
        //    (a not-dumped platform or a content artifact absent for the era). The anti-silent-skip
        //    guarantee survives: a real hole (content depot acquired but a content-gated file missing
        //    with no omission record) still fails loud downstream, because contentOmittedByPlatform is
        //    empty when the file is absent. A PRESENT omissions.json is fully validated (step 2).
        var omissionsFile = Path.Combine(buildPath, ArtifactSet.OmissionsFileName);

        // 2. omissions.json (if present) must parse and yield (platform, reason) pairs with valid
        //    reasons. If absent, use an empty document (absent = clean).
        var omittedPlatforms = new List<string>();
        Omissions omissions;
        if (!File.Exists(omissionsFile))
        {
            omissions = new Omissions();
        }
        else
        {
            try
            {
                omissions = TolerantParser.Parse<Omissions>(File.ReadAllText(omissionsFile));
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or Google.Protobuf.InvalidJsonException)
            {
                Violate($"build '{buildId}' omissions.json does not parse as valid JSON: {ex.Message}");
                return new BuildVerdict(buildId, v);
            }
        }

        // Per-(build, platform) content-artifact omissions (a PRESENT platform that legitimately
        // lacks a content artifact because the era never shipped its source). Keyed by platform;
        // consulted by the content-depot gating in ValidatePlatformComplete. Distinct from a
        // WHOLESALE platform omission (which removes the platform entirely).
        var contentOmittedByPlatform =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var o in omissions.Omissions_)
        {
            var platform = o.Platform ?? "";
            if (string.IsNullOrEmpty(platform))
                continue;
            if (!ArtifactSet.CanonicalPlatforms.Contains(platform, StringComparer.Ordinal))
            {
                Violate($"build '{buildId}' omissions.json lists non-canonical platform '{platform}'");
            }

            if (o.Reason != PlatformOmission.Types.Reason.Unspecified)
            {
                // WHOLESALE platform omission: the platform dir is absent and accounted-for.
                omittedPlatforms.Add(platform);
                continue;
            }

            // reason == UNSPECIFIED. This is a CONTENT-CARRIER iff it lists content omissions (a
            // present platform annotating which content artifacts it genuinely lacks). With NO
            // content omissions it is an invalid empty entry — still a violation.
            if (o.ContentOmissions.Count == 0)
            {
                Violate($"build '{buildId}' omissions.json platform '{platform}' has invalid/empty reason " +
                        $"(must be one of: {ValidReasonNames()})");
                omittedPlatforms.Add(platform);
                continue;
            }

            var omittedArtifacts = contentOmittedByPlatform.TryGetValue(platform, out var set)
                ? set
                : (contentOmittedByPlatform[platform] = new HashSet<string>(StringComparer.Ordinal));
            foreach (var co in o.ContentOmissions)
            {
                var artifact = co.Artifact ?? "";
                if (!ArtifactSet.OmittableContentArtifacts.Contains(artifact, StringComparer.Ordinal))
                {
                    Violate($"build '{buildId}' omissions.json platform '{platform}' content omission names " +
                            $"'{artifact}', which is not a content-depot-gated artifact");
                    continue;
                }
                if (co.Reason == PlatformOmission.Types.Reason.Unspecified)
                {
                    Violate($"build '{buildId}' omissions.json platform '{platform}' content omission for " +
                            $"'{artifact}' has invalid/empty reason (expected CONTENT_NOT_SHIPPED_THIS_ERA)");
                }
                omittedArtifacts.Add(artifact);
            }
        }

        // 3. enumerate platform dirs actually present on disk.
        var presentPlatforms = Directory.EnumerateDirectories(buildPath)
            .Select(d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Any present platform dir must be canonical AND complete.
        foreach (var t in presentPlatforms)
        {
            if (!ArtifactSet.CanonicalPlatforms.Contains(t, StringComparer.Ordinal))
            {
                Violate($"build '{buildId}' contains non-canonical platform directory '{t}'");
                continue;
            }
            var contentOmitted = contentOmittedByPlatform.TryGetValue(t, out var co)
                ? co
                : EmptySet;
            ValidatePlatformComplete(buildId, t, buildPath, contentOmitted, Violate);
            // Changelog predecessor gate: the committed changelog.json under a (build,platform) dir
            // is REQUIRED iff that platform has an immediate committed predecessor, and FORBIDDEN
            // when this is the earliest committed build for the platform. See ValidateChangelogGate.
            ValidateChangelogGate(buildId, t, buildPath, Violate);
        }

        // A platform cannot be BOTH present-on-disk AND listed as omitted.
        foreach (var t in presentPlatforms)
        {
            if (omittedPlatforms.Contains(t, StringComparer.Ordinal))
            {
                Violate($"build '{buildId}' platform '{t}' is present on disk yet also listed in omissions.json");
            }
        }

        // 4. legal-shape decision.
        if (presentPlatforms.Count == 1 && omittedPlatforms.Count == 0)
        {
            // Shape (A): single-platform commit. Legal.
        }
        else
        {
            // Shape (B): full-build. Present ∪ omitted must cover BOTH canonical platforms.
            foreach (var ct in ArtifactSet.CanonicalPlatforms)
            {
                if (presentPlatforms.Contains(ct, StringComparer.Ordinal))
                    continue;
                if (omittedPlatforms.Contains(ct, StringComparer.Ordinal))
                    continue;
                Violate($"build '{buildId}' platform '{ct}' is UNACCOUNTED-FOR: neither present on disk " +
                        "nor recorded in omissions.json (partial cross-platform set forbidden)");
            }
            if (presentPlatforms.Count == 0)
            {
                Violate($"build '{buildId}' has no platform directories present at all " +
                        "(both platforms omitted is not a valid artifact set — surface for operator action)");
            }
        }

        return new BuildVerdict(buildId, v);
    }

    /// <summary>An empty content-omission set (no per-artifact omissions recorded for a platform).</summary>
    private static readonly HashSet<string> EmptySet = new(StringComparer.Ordinal);

    /// <summary>
    /// Validate ONE <c>(build, platform)</c> tuple's per-platform completeness (README "What it
    /// produces"): every <see cref="ArtifactSet.RequiredFiles"/> present, <c>protos/</c> non-empty,
    /// and each content-depot-gated file present iff this tuple's provenance lists the content depot
    /// (honoring any per-artifact omission recorded in the build's omissions.json). This is the exact
    /// per-tuple check <see cref="ValidateBuild"/> runs, exposed so the commit driver gates on the
    /// SAME source of truth — the committed-file list can never drift from a hand-maintained copy.
    /// Unlike <see cref="ValidateBuild"/> it does NOT enforce the cross-platform all-or-nothing shape,
    /// so an in-progress single-tuple commit (e.g. a linux backfill before the build's other platform)
    /// is validated on its own merits. The changelog predecessor gate DOES run here: a stale
    /// from_build committed anyway would fail verify-artifacts on every later push.
    /// </summary>
    public BuildVerdict ValidateTuple(string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        var v = new List<ArtifactSetViolation>();
        void Violate(string msg) => v.Add(new ArtifactSetViolation(buildId, msg));

        var buildPath = Path.Combine(_artifactsRoot, buildId);
        var dir = Path.Combine(buildPath, platform);
        if (!Directory.Exists(dir))
        {
            Violate($"build '{buildId}' platform '{platform}' has no artifact directory at '{dir}'");
            return new BuildVerdict(buildId, v);
        }

        ValidatePlatformComplete(buildId, platform, buildPath, ReadContentOmissions(buildPath, platform), Violate);
        // Changelog predecessor gate, same rule ValidateBuild applies: a stale from_build committed
        // here would wedge every later verify-artifacts run, so the commit driver must refuse it
        // (regenerate via `reconcile-changelog` / `diff` first).
        ValidateChangelogGate(buildId, platform, buildPath, Violate);
        return new BuildVerdict(buildId, v);
    }

    /// <summary>
    /// Best-effort read of the content artifacts a build's omissions.json records as legitimately
    /// absent for <paramref name="platform"/> (an era that never shipped a content source). Returns
    /// an empty set when omissions.json is absent or unparseable — <see cref="ValidateBuild"/> (via
    /// verify-artifacts) is the authoritative validator that reports a malformed omissions.json; here
    /// we only need the set so a legitimately-omitted content file is not flagged as missing.
    /// </summary>
    private static HashSet<string> ReadContentOmissions(string buildPath, string platform)
    {
        var omissionsFile = Path.Combine(buildPath, ArtifactSet.OmissionsFileName);
        if (!File.Exists(omissionsFile))
            return EmptySet;

        Omissions omissions;
        try
        {
            omissions = TolerantParser.Parse<Omissions>(File.ReadAllText(omissionsFile));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or Google.Protobuf.InvalidJsonException)
        {
            return EmptySet;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in omissions.Omissions_)
        {
            if (!string.Equals(o.Platform, platform, StringComparison.Ordinal))
                continue;
            foreach (var co in o.ContentOmissions)
            {
                if (!string.IsNullOrEmpty(co.Artifact))
                    set.Add(co.Artifact);
            }
        }
        return set;
    }

    // --- one platform directory completeness (README "What it produces") ---
    private static void ValidatePlatformComplete(
        string buildId, string platform, string buildPath,
        HashSet<string> contentOmitted, Action<string> violate)
    {
        var dir = Path.Combine(buildPath, platform);

        foreach (var f in ArtifactSet.RequiredFiles)
        {
            if (!File.Exists(Path.Combine(dir, f)))
            {
                violate($"build '{buildId}' platform '{platform}' is INCOMPLETE: missing file '{f}'");
            }
        }

        foreach (var d in ArtifactSet.RequiredNonEmptyDirs)
        {
            var sub = Path.Combine(dir, d);
            if (!Directory.Exists(sub))
            {
                violate($"build '{buildId}' platform '{platform}' is INCOMPLETE: missing directory '{d}/'");
            }
            else if (!Directory.EnumerateFileSystemEntries(sub).Any())
            {
                violate($"build '{buildId}' platform '{platform}' is INCOMPLETE: directory '{d}/' is empty");
            }
        }

        // Content-depot gating. provenance.json is in RequiredFiles above (already flagged if
        // missing). If present, parse it; if it lists the content depot, every content-gated file
        // is mandatory. A genuinely malformed provenance.json is a fail-loud violation.
        var prov = Path.Combine(dir, ArtifactSet.ProvenanceFileName);
        if (!File.Exists(prov))
            return; // already reported missing; cannot gate without it.

        Schemas.Provenance provenance;
        try
        {
            provenance = TolerantParser.Parse<Schemas.Provenance>(File.ReadAllText(prov));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or Google.Protobuf.InvalidJsonException)
        {
            violate($"build '{buildId}' platform '{platform}' provenance.json does not parse as valid JSON " +
                    $"(cannot evaluate content-depot gating): {ex.Message}");
            return;
        }

        var contentDepotAcquired = provenance.Steam is { } steam
            && steam.Depots.Any(d => d.DepotId == ArtifactSet.ContentDepotId);
        if (!contentDepotAcquired)
            return; // binary-only: content-gated files optional.

        foreach (var cf in ArtifactSet.ContentDepotGatedFiles)
        {
            if (File.Exists(Path.Combine(dir, cf)))
            {
                continue;
            }
            // ACCEPTABLE absence: the build genuinely lacks this artifact's source for this era and
            // it is recorded as a content omission in omissions.json. Otherwise the content depot was
            // acquired but the file is missing with no account ⇒ a completeness violation.
            if (contentOmitted.Contains(cf))
            {
                continue;
            }
            violate($"build '{buildId}' platform '{platform}' is INCOMPLETE: content depot " +
                    $"{ArtifactSet.ContentDepotId.ToString(CultureInfo.InvariantCulture)} was acquired " +
                    $"(in provenance.json steam.depots) but '{cf}' is missing");
        }

        // Build-on-demand localization.json is NOT a committed file (it is .gitignore'd), so it is
        // absent from ContentDepotGatedFiles above. Instead a content-depot set MUST carry the
        // provenance.localization fingerprint (sha256/size/token_count) so an on-demand rebuild via
        // `emit-localization` is byte-verifiable against what was dumped. A populated fingerprint has
        // a non-empty sha256. ACCEPTABLE absence: an era that genuinely never shipped localization
        // tables (e.g. the earliest CS2 builds) produces no localization.json and records a
        // 'localization.json' content omission — then the fingerprint is legitimately absent. Any
        // other absence (content depot acquired, localization produced, but no fingerprint recorded)
        // is a completeness violation.
        bool localizationFingerprintPopulated =
            provenance.Localization is { } loc && !string.IsNullOrEmpty(loc.Sha256);
        if (!localizationFingerprintPopulated
            && !contentOmitted.Contains(ArtifactSet.LocalizationFileName))
        {
            violate($"build '{buildId}' platform '{platform}' is INCOMPLETE: content depot " +
                    $"{ArtifactSet.ContentDepotId.ToString(CultureInfo.InvariantCulture)} was acquired " +
                    "(in provenance.json steam.depots) but provenance.localization is not populated " +
                    "(the build-on-demand localization.json fingerprint is required so an " +
                    "emit-localization rebuild is byte-verifiable)");
        }
    }

    // --- Build-to-build changelog predecessor gate ---
    //
    // The committed changelog.json under artifacts/<build>/<platform>/ is keyed to the build's
    // IMMEDIATE PREDECESSOR for that platform: the committed build with the next-lower NUMERIC build
    // id that ALSO has that platform present under artifacts/. Rules:
    //   - predecessor exists  => changelog.json REQUIRED; parse it and assert from_build == that
    //                            predecessor id AND to_build == this build id (a stale from_build,
    //                            e.g. an out-of-order backfill that was not regenerated, is a
    //                            reported violation).
    //   - no predecessor      => earliest committed build for the platform; changelog.json must be
    //                            ABSENT (its presence is a violation; its absence is NOT an
    //                            omissions.json entry).
    // Determinism: the per-platform committed-build set is enumerated from the artifacts root,
    // numerically ordered. Fail-soft on a malformed changelog.json (reported violation, no throw),
    // mirroring the provenance/omissions catch.
    private void ValidateChangelogGate(
        string buildId, string platform, string buildPath, Action<string> violate)
    {
        var predecessor = ImmediatePredecessor(buildId, platform);
        var changelogPath = Path.Combine(buildPath, platform, ArtifactSet.ChangelogFileName);
        var present = File.Exists(changelogPath);

        if (predecessor is null)
        {
            // Earliest committed build for the platform: changelog must be ABSENT.
            if (present)
            {
                violate($"build '{buildId}' platform '{platform}' carries {ArtifactSet.ChangelogFileName} " +
                        "but it is the EARLIEST committed build for this platform (no predecessor to diff) " +
                        "— the changelog must be absent");
            }
            return;
        }

        // A predecessor exists: changelog.json is REQUIRED here.
        if (!present)
        {
            violate($"build '{buildId}' platform '{platform}' is MISSING {ArtifactSet.ChangelogFileName} " +
                    $"(immediate committed predecessor is '{predecessor}'; a changelog is required)");
            return;
        }

        BuildChangelog changelog;
        try
        {
            changelog = TolerantParser.Parse<BuildChangelog>(File.ReadAllText(changelogPath));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or Google.Protobuf.InvalidJsonException)
        {
            violate($"build '{buildId}' platform '{platform}' {ArtifactSet.ChangelogFileName} does not " +
                    $"parse as valid JSON: {ex.Message}");
            return;
        }

        if (!string.Equals(changelog.FromBuild, predecessor, StringComparison.Ordinal))
        {
            violate($"build '{buildId}' platform '{platform}' {ArtifactSet.ChangelogFileName} has " +
                    $"from_build='{changelog.FromBuild}' but the immediate committed predecessor is " +
                    $"'{predecessor}' (stale changelog — regenerate after an out-of-order backfill)");
        }
        if (!string.Equals(changelog.ToBuild, buildId, StringComparison.Ordinal))
        {
            violate($"build '{buildId}' platform '{platform}' {ArtifactSet.ChangelogFileName} has " +
                    $"to_build='{changelog.ToBuild}' but it is committed under build '{buildId}'");
        }
    }

    /// <summary>
    /// Repo-level checks for preserved PICS captures (<c>&lt;picsCapturesDir&gt;/&lt;build&gt;.json</c>):
    /// ORPHANED when the build already has a committed build-level pics-appinfo.json (the landing
    /// commit should have dropped the copy; commit-plan names it in removePaths), and STRANDED
    /// when the build's set is committed WITHOUT a pics-appinfo.json (committed markers stop the
    /// legs, so nothing would ever promote the capture; the fix is <c>emit-pics</c> from the
    /// preserved file). Dormant when the directory is absent. A preserved capture for a build with
    /// NO committed set is the intended pending state and raises nothing.
    /// </summary>
    public IReadOnlyList<ArtifactSetViolation> ValidatePreservedCaptures(string picsCapturesDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(picsCapturesDir);
        var issues = new List<ArtifactSetViolation>();
        if (!Directory.Exists(picsCapturesDir))
            return issues;

        foreach (var file in Directory.EnumerateFiles(picsCapturesDir, "*.json")
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var build = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(build))
                continue;
            if (File.Exists(Path.Combine(_artifactsRoot, build, PicsAppInfo.PicsAppInfoEmitter.FileName)))
            {
                issues.Add(new ArtifactSetViolation(build,
                    $"preserved PICS capture '{file}' is ORPHANED: build '{build}' already has a " +
                    "committed pics-appinfo.json. Remove the preserved copy (the landing commit " +
                    "should have dropped it; commit-plan names it in removePaths)."));
                continue;
            }
            bool setCommitted = ArtifactSet.CanonicalPlatforms.Any(p =>
                File.Exists(Path.Combine(_artifactsRoot, build, p, "entity_schema.json")));
            if (setCommitted)
            {
                issues.Add(new ArtifactSetViolation(build,
                    $"preserved PICS capture '{file}' is STRANDED: build '{build}' has a committed " +
                    "set but no pics-appinfo.json, and committed markers keep the legs from ever " +
                    "promoting it. Run emit-pics from the preserved file and remove the copy."));
            }
        }
        return issues;
    }

    /// <summary>
    /// Repo-level check for the fixed-path cumulative schema-evolution artifact
    /// (<c>schema_evolution/&lt;platform&gt;.json</c>) — NOT a per-build gate (the artifact lives once
    /// per platform, outside the build dirs). Returns a violation for each problem; empty when clean.
    ///
    /// DORMANT until seeded: a platform whose fixed artifact is ABSENT raises no violation (pre-seed).
    /// Once present it must be well-formed and current: parse must succeed, <c>platform</c> must match,
    /// <c>baseline_build</c> must equal the platform's floor, <c>latest_build</c> must equal the latest
    /// committed build, and <c>transitions.Count</c> must equal chain length − 1 (a short count catches
    /// a stale artifact not refreshed after a build landed). Fail-soft on malformed JSON (reported, no
    /// throw). A platform with no committed builds is skipped.
    /// </summary>
    public IReadOnlyList<ArtifactSetViolation> ValidateEvolution(string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(platform);
        var issues = new List<ArtifactSetViolation>();
        void Violate(string msg) => issues.Add(new ArtifactSetViolation(platform, msg));

        var chain = Changelog.ChangelogPredecessor.OrderedChain(_artifactsRoot, platform);
        if (chain.Count == 0)
            return issues;

        var path = Path.Combine(_artifactsRoot, ArtifactSet.SchemaEvolutionRelativePath(platform));
        if (!File.Exists(path))
            return issues;   // dormant: not yet seeded for this platform

        SchemaEvolution evolution;
        try
        {
            evolution = TolerantParser.Parse<SchemaEvolution>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or Google.Protobuf.InvalidJsonException)
        {
            Violate($"{ArtifactSet.SchemaEvolutionRelativePath(platform)} does not parse as valid JSON: {ex.Message}");
            return issues;
        }

        if (!string.Equals(evolution.Platform, platform, StringComparison.Ordinal))
        {
            Violate($"{ArtifactSet.SchemaEvolutionRelativePath(platform)} has platform='{evolution.Platform}' " +
                    $"but is stored as the '{platform}' artifact");
        }
        if (!string.Equals(evolution.BaselineBuild, chain[0], StringComparison.Ordinal))
        {
            Violate($"{ArtifactSet.SchemaEvolutionRelativePath(platform)} has baseline_build=" +
                    $"'{evolution.BaselineBuild}' but the committed floor is '{chain[0]}'");
        }
        if (!string.Equals(evolution.LatestBuild, chain[^1], StringComparison.Ordinal))
        {
            Violate($"{ArtifactSet.SchemaEvolutionRelativePath(platform)} has latest_build=" +
                    $"'{evolution.LatestBuild}' but the latest committed build is '{chain[^1]}' " +
                    "(stale — re-run `evolution`)");
        }
        if (evolution.Transitions.Count != chain.Count - 1)
        {
            Violate($"{ArtifactSet.SchemaEvolutionRelativePath(platform)} has {evolution.Transitions.Count} " +
                    $"transition(s) but the committed chain has {chain.Count} builds ({chain.Count - 1} " +
                    "expected — stale; re-run `evolution --full` after a backfill)");
        }
        return issues;
    }

    /// <summary>
    /// The immediate committed predecessor of <paramref name="buildId"/> for
    /// <paramref name="platform"/>. Delegates to <see cref="Changelog.ChangelogPredecessor.Resolve"/>
    /// — the SINGLE source-of-truth predecessor rule the inline extract emitter also uses, so the
    /// gate never rejects extract's own output. See that helper for the rule.
    /// </summary>
    private string? ImmediatePredecessor(string buildId, string platform)
        => Changelog.ChangelogPredecessor.Resolve(_artifactsRoot, buildId, platform);

    /// <summary>The valid omission reason names (from the proto enum), excluding UNSPECIFIED.</summary>
    private static string ValidReasonNames()
    {
        var names = Enum.GetValues<PlatformOmission.Types.Reason>()
            .Where(r => r != PlatformOmission.Types.Reason.Unspecified)
            .Select(r => r.ToString())
            .OrderBy(n => n, StringComparer.Ordinal);
        return string.Join(", ", names);
    }
}
