// the SINGLE source-of-truth predecessor rule shared by the two call sites.
//
// The changelog's predecessor is defined ONCE here so that:
//   - ExtractCommand emits changelog.json inline (from == predecessor, to == this build), and
// - ArtifactSetValidator's verify-artifacts gate requires/forbids that same file,
// compute the IDENTICAL predecessor. If the two rules could diverge, verify-artifacts would
// reject extract's own output. They cannot: both call Resolve().
//
// THE RULE (unchanged from the original ArtifactSetValidator.ImmediatePredecessor): the committed
// build with the greatest NUMERIC build id STRICTLY LESS than this one whose <platform> dir is
// present under the artifacts root; null when this is the earliest committed build for the
// platform (the "floor build has no changelog" invariant). Non-numeric build-dir names are
// ignored (the ordering is numeric). Deterministic: a pure function of the on-disk set.

using System.Globalization;

namespace Cs2SchemaTracker.Host.Changelog;

/// <summary>
/// The immediate-committed-predecessor rule, in one place. Both the inline extract emitter
/// and the verify-artifacts gate call <see cref="Resolve"/> so they never disagree about whether a
/// changelog.json is expected (and against which baseline).
/// </summary>
public static class ChangelogPredecessor
{
    /// <summary>
    /// The immediate committed predecessor of <paramref name="buildId"/> for
    /// <paramref name="platform"/>: the committed build with the greatest NUMERIC build id strictly
    /// less than this one that ALSO has the platform dir present under
    /// <paramref name="artifactsRoot"/>. Returns null when this is the earliest committed build for
    /// the platform (the floor build — no changelog). Non-numeric build ids are ignored.
    /// Deterministic.
    /// </summary>
    public static string? Resolve(string artifactsRoot, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRoot);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        if (!TryParseBuildId(buildId, out var self))
            return null;

        long bestId = long.MinValue;
        string? best = null;
        foreach (var (otherName, otherId) in CommittedBuildsForPlatform(artifactsRoot, platform))
        {
            if (otherId >= self)
                continue;
            if (otherId > bestId)
            {
                bestId = otherId;
                best = otherName;
            }
        }
        return best;
    }

    /// <summary>
    /// The full committed chain for <paramref name="platform"/> — every build with the platform dir
    /// present, ordered by ascending NUMERIC build id (the same numeric relation
    /// <see cref="Resolve"/> uses, generalized to the whole chain). This is the single ordering the
    /// schema-evolution walk consumes, so it can never disagree with the changelog predecessor rule.
    /// Empty when the platform has no committed builds. Deterministic.
    /// </summary>
    public static IReadOnlyList<string> OrderedChain(string artifactsRoot, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRoot);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        return CommittedBuildsForPlatform(artifactsRoot, platform)
            .OrderBy(b => b.Id)
            .Select(b => b.Name)
            .ToList();
    }

    /// <summary>
    /// Enumerate (buildDirName, numericId) for every build under <paramref name="artifactsRoot"/>
    /// whose <paramref name="platform"/> dir is present. Only numerically-parseable build ids are
    /// yielded (the predecessor rule is numeric).
    /// </summary>
    private static IEnumerable<(string Name, long Id)> CommittedBuildsForPlatform(
        string artifactsRoot, string platform)
    {
        if (!Directory.Exists(artifactsRoot))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(artifactsRoot))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                continue;
            if (!TryParseBuildId(name, out var id))
                continue;
            if (!Directory.Exists(Path.Combine(dir, platform)))
                continue;
            yield return (name, id);
        }
    }

    private static bool TryParseBuildId(string name, out long id)
        => long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out id);
}
