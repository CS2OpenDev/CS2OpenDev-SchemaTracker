// Host-native re-walk orchestration — committed-build discovery.
//
// Enumerates the committed artifact sets under <repoRoot>/artifacts/<build>/<platform>/ and
// buckets them by era/pin for the `extract` command's batch selection options (--all / --era /
// --pin). The era/pin for each build comes from the SAME logic the extract path uses
// (EraWalkerResolver.DetermineEraOnly, which maps the build to its exact era via the single-source
// inventory builds[].era) — SELECTION does not need a walker binary path, so it routes through
// DetermineEraOnly, never Resolve.
//
// This is operator/orchestration input over the repo, NOT a public artifact and NOT in
// the public surface (README.md) — no proto round-trip. Deterministic ordering (Ordinal build id) keeps the run
// summary stable (ethos, though no artifact bytes are produced here).

using Cs2SchemaTracker.Host.Walker;

namespace Cs2SchemaTracker.Host.Config;

/// <summary>One committed (build, platform) artifact set, with its resolved era + pin.</summary>
internal sealed record CommittedBuild(string Build, string Platform, string Era, string Pin);

/// <summary>
/// Discovers committed artifact sets under <c>artifacts/&lt;build&gt;/&lt;platform&gt;/</c> for a
/// given platform and maps each to its era/pin via <see cref="EraWalkerResolver.DetermineEraOnly"/>.
/// </summary>
internal static class CommittedBuilds
{
    /// <summary>
    /// Every committed build for <paramref name="platform"/> under <paramref name="repoRoot"/>'s
    /// <c>artifacts/</c>, sorted by build id (Ordinal). A directory counts as a committed build
    /// only when it carries an <c>entity_schema.json</c> (the CORE artifact the gate re-counts) —
    /// stray dirs / build-level files (omissions.json) are skipped. Each build's era/pin is
    /// resolved via <paramref name="resolver"/>; a build whose era cannot be resolved (e.g. its
    /// provenance pin is not in the manifest) fails loud rather than being silently
    /// dropped — selection over an inconsistent repo must not guess.
    /// </summary>
    public static IReadOnlyList<CommittedBuild> Discover(
        string repoRoot, string platform, EraWalkerResolver resolver)
    {
        ArgumentException.ThrowIfNullOrEmpty(repoRoot);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        ArgumentNullException.ThrowIfNull(resolver);

        var artifactsRoot = Path.Combine(repoRoot, "artifacts");
        if (!Directory.Exists(artifactsRoot))
        {
            return Array.Empty<CommittedBuild>();
        }

        var result = new List<CommittedBuild>();
        foreach (var buildDir in Directory.EnumerateDirectories(artifactsRoot)
                     .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal))
        {
            var build = Path.GetFileName(buildDir);
            var setDir = Path.Combine(buildDir, platform);
            if (!File.Exists(Path.Combine(setDir, "entity_schema.json")))
            {
                continue;   // not a committed (build, platform) set for this platform.
            }

            EraSelection sel = resolver.DetermineEraOnly(build, platform);
            result.Add(new CommittedBuild(build, platform, sel.Era, sel.Pin));
        }

        return result;
    }
}
