// host-owned artifact-set completeness validator CLI (`verify-artifacts`).
//
// It is a PURE read-only validator over the artifacts/ tree: given a set of build directories
// (whole-tree scan, an explicit --build list, or the --changed-paths CI passes from a git
// diff), it asserts each is a legal all-or-nothing shape and exits non-zero on any
// violation (fail-loud). It NEVER writes and NEVER invokes git — "which dirs changed"
// stays in the thin CI driver, which feeds them in via --changed-paths.
//
// Input selection (choose one; --changed-paths and --build may be combined):
//   --artifacts <root>      Artifacts root (default: artifacts). With NO --build/--changed-paths,
//                           validates EVERY build dir directly under the root.
//   --build <id>            Validate a specific build (repeatable).
//   --changed-paths <list>  Newline / comma / space separated repo-relative paths the CI diff
//                           touched; the build ids under <root>/<id>/... are extracted + deduped.
//                           (Repeatable; values accumulate.)
//
// Determinism: stable iteration order, no timestamps. Output: per-build PASS/FAIL with
// each specific violation on stderr (label `VIOLATION:` — grep-compatible with the old
// bash gate). Exit 0 = all legal; 1 = at least one violation; 64 = usage error.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Evolution;
using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host.Cli;

internal static class VerifyArtifactsCommand
{
    private const string DefaultArtifactsRoot = "artifacts";

    // Separators a single --changed-paths value may bundle (CI may pass a whole newline-separated
    // git-diff blob as one argv token).
    private static readonly char[] ChangedPathSeparators = { '\n', '\r', ',', ' ', '\t' };

    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker verify-artifacts — assert committed (build, platform) artifact
sets are a legal all-or-nothing shape. Pure, read-only; writes nothing.

Usage:
  cs2-schema-tracker verify-artifacts [--artifacts <root>]
  cs2-schema-tracker verify-artifacts [--artifacts <root>] --build <id> [--build <id> ...]
  cs2-schema-tracker verify-artifacts [--artifacts <root>] --changed-paths <paths>

Arguments:
  --artifacts <root>     Artifacts root directory (default: artifacts). With no --build /
                         --changed-paths, validates EVERY build dir directly under it.
  --build <id>           Validate a specific build directory (repeatable).
  --changed-paths <list> Newline / comma / space separated repo-relative paths a CI diff
                         touched; build ids under <root>/<id>/... are extracted + deduped
                         (repeatable; values accumulate). This is the CI commit-gating mode —
                         CI computes the diff and passes the touched paths; this command does
                         no git.

behavior (README.md):
  - A legal commit is EITHER one complete single-platform set, OR a full build where each
    canonical platform is present-and-complete OR accounted-for in omissions.json with a valid
    reason. A partial cross-platform set with an unaccounted-for platform is a violation.
  - A platform dir is complete when every required file is present, protos/ is non-empty, and
    (iff provenance.json lists content depot 2347770) every content-depot-gated file is present.
  - Deterministic given the same on-disk inputs. Exit non-zero on any violation.

Exit codes: 0 all legal · 1 at least one violation · 64 usage error.");
            return 0;
        }

        // Parse manually so --build / --changed-paths can repeat (CliArgs collapses repeats).
        string? artifactsRoot = null;
        string? inventoryPath = null;
        var explicitBuilds = new List<string>();
        var changedPaths = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"verify-artifacts: unexpected argument '{a}'. Run 'verify-artifacts --help'.");
                return 64;
            }

            string name;
            string? inlineValue = null;
            var eq = a.IndexOf('=');
            if (eq > 0)
            {
                name = a[2..eq];
                inlineValue = a[(eq + 1)..];
            }
            else
            {
                name = a[2..];
            }

            string? TakeValue()
            {
                if (inlineValue is not null)
                    return inlineValue;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return args[++i];
                }
                return null;
            }

            switch (name)
            {
                case "artifacts":
                    artifactsRoot = TakeValue();
                    if (string.IsNullOrEmpty(artifactsRoot))
                    {
                        Console.Error.WriteLine("verify-artifacts: --artifacts requires a directory value.");
                        return 64;
                    }
                    break;
                case "build":
                    var b = TakeValue();
                    if (string.IsNullOrEmpty(b))
                    {
                        Console.Error.WriteLine("verify-artifacts: --build requires a build id value.");
                        return 64;
                    }
                    explicitBuilds.Add(b);
                    break;
                case "changed-paths":
                    var cp = TakeValue();
                    if (string.IsNullOrEmpty(cp))
                    {
                        Console.Error.WriteLine("verify-artifacts: --changed-paths requires a value.");
                        return 64;
                    }
                    changedPaths.Add(cp);
                    break;
                case "inventory":
                    inventoryPath = TakeValue();
                    if (string.IsNullOrEmpty(inventoryPath))
                    {
                        Console.Error.WriteLine("verify-artifacts: --inventory requires a path value.");
                        return 64;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"verify-artifacts: unknown option '--{name}'. Run 'verify-artifacts --help'.");
                    return 64;
            }
        }

        var root = Path.GetFullPath(artifactsRoot ?? DefaultArtifactsRoot);
        var validator = new ArtifactSetValidator(root);

        // The repo-relative path segment that build-id-bearing --changed-paths sit under. CI
        // passes repo-relative paths (e.g. "artifacts/<id>/..."), independent of where --artifacts
        // physically resolves, so we match on the artifacts root's FINAL path segment (default
        // "artifacts"), not its absolute location.
        var rootSegment = LastSegment(artifactsRoot) ?? DefaultArtifactsRoot;
        var scopedBuilds = CollectScopedBuilds(explicitBuilds, changedPaths, rootSegment);
        bool scoped = explicitBuilds.Count > 0 || changedPaths.Count > 0;

        ArtifactSetReport report;
        if (scoped)
        {
            if (scopedBuilds.Count == 0)
            {
                // CI gave changed-paths but none were under the artifacts root — nothing to
                // gate (e.g. a tool-only PR). Clean pass, mirroring the bash gate.
                Console.WriteLine("verify-artifacts: OK — no artifacts/ build directories in scope (nothing to validate).");
                return 0;
            }
            report = validator.ValidateBuilds(scopedBuilds);
        }
        else
        {
            report = validator.ValidateAll();
            if (report.Builds.Count == 0)
            {
                Console.WriteLine("verify-artifacts: OK — no build directories to validate (artifacts/ empty or absent).");
                return 0;
            }
        }

        // Emit per-build verdicts deterministically; violations to stderr (grep label preserved).
        foreach (var verdict in report.Builds)
        {
            if (verdict.Passed)
            {
                Console.WriteLine($"verify-artifacts: PASS build '{verdict.BuildId}'");
            }
            else
            {
                Console.WriteLine($"verify-artifacts: FAIL build '{verdict.BuildId}' ({verdict.Violations.Count} violation(s))");
                foreach (var viol in verdict.Violations)
                {
                    Console.Error.WriteLine($"VIOLATION: {viol.Message}");
                }
            }
        }

        // Repo-level checks (NOT per-build): the fixed-path cumulative schema-evolution artifact must
        // be well-formed + current, and — when an inventory is supplied — the inventory predecessor
        // chain must agree with the on-disk numeric rule. These run in every mode (a commit touching
        // artifacts must leave the cumulative artifact consistent). Dormant when the artifact is absent.
        var repoViolations = RunRepoLevelChecks(root, validator, inventoryPath);
        foreach (var msg in repoViolations)
            Console.Error.WriteLine($"VIOLATION: {msg}");

        var totalProblems = report.AllViolations.Count + repoViolations.Count;
        if (totalProblems > 0)
        {
            Console.Error.WriteLine($"VIOLATION: {totalProblems} problem(s) found — see lines above. Refusing (fail-loud).");
            return 1;
        }

        Console.WriteLine("verify-artifacts: OK — all validated build directories are a legal all-or-nothing shape.");
        return 0;
    }

    /// <summary>
    /// Repo-level (per-platform, not per-build) checks: the fixed-path cumulative schema-evolution
    /// artifact is well-formed + current (dormant when absent), and — iff <paramref name="inventoryPath"/>
    /// is supplied and readable — the inventory predecessor chain agrees with the on-disk numeric rule.
    /// A missing/unreadable inventory is reported (drift cannot be checked), not silently skipped.
    /// </summary>
    private static List<string> RunRepoLevelChecks(
        string root, ArtifactSetValidator validator, string? inventoryPath)
    {
        var messages = new List<string>();

        AssetsInventory? inventory = null;
        if (!string.IsNullOrEmpty(inventoryPath))
        {
            try
            {
                inventory = AssetsInventory.Load(inventoryPath);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                messages.Add($"predecessor-drift check requested but the inventory could not be read: {ex.Message}");
            }
        }

        foreach (var platform in ArtifactSet.CanonicalPlatforms)
        {
            foreach (var v in validator.ValidateEvolution(platform))
                messages.Add(v.Message);

            if (inventory is not null)
            {
                foreach (var d in PredecessorDriftCheck.FindDisagreements(root, inventory, platform))
                    messages.Add(d);
            }
        }

        // Preserved-capture orphan check: data/pics-captures/ is a sibling tree of the artifacts
        // root (the preserved current-only PICS captures pending an artifact-set landing). Dormant
        // when absent.
        var picsCapturesDir = Path.Combine(
            Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? root,
            "data", "pics-captures");
        foreach (var v in validator.ValidatePreservedCaptures(picsCapturesDir))
            messages.Add(v.Message);

        return messages;
    }

    /// <summary>
    /// Build the deduped set of build ids from explicit --build values + any build ids extracted
    /// from --changed-paths under the artifacts root segment.
    /// </summary>
    private static List<string> CollectScopedBuilds(
        IReadOnlyList<string> explicitBuilds,
        IReadOnlyList<string> changedPaths,
        string rootSegment)
    {
        var builds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var b in explicitBuilds)
        {
            if (!string.IsNullOrEmpty(b))
                builds.Add(b);
        }

        foreach (var raw in changedPaths)
        {
            foreach (var p in raw.Split(ChangedPathSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var id = ExtractBuildId(p, rootSegment);
                if (id is not null)
                    builds.Add(id);
            }
        }

        return builds.ToList();
    }

    /// <summary>The final path segment of <paramref name="path"/> (forward/back slashes), or null.</summary>
    private static string? LastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var trimmed = path.Replace('\\', '/').TrimEnd('/');
        if (trimmed.Length == 0)
            return null;
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// Extract the build id from a repo-relative changed path of the form
    /// <c>&lt;rootSegment&gt;/&lt;buildId&gt;/...</c>. Returns null when the path is not under the
    /// artifacts root or names no build sub-directory (e.g. a top-level file).
    /// </summary>
    internal static string? ExtractBuildId(string path, string rootSegment)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var rootPrefix = rootSegment.Replace('\\', '/').Trim('/') + "/";
        if (!normalized.StartsWith(rootPrefix, StringComparison.Ordinal))
            return null;

        var rest = normalized[rootPrefix.Length..];
        var slash = rest.IndexOf('/');
        if (slash <= 0)
            return null;   // a file directly under the root (e.g. a README), no build dir.
        var segment = rest[..slash];
        // The fixed-path schema-evolution artifact (schema_evolution/<platform>.json) sits under the
        // artifacts root but is NOT a build dir — a commit touching it must not gate a bogus build id.
        if (string.Equals(segment, ArtifactSet.SchemaEvolutionDirName, StringComparison.Ordinal))
            return null;
        return segment;
    }
}
