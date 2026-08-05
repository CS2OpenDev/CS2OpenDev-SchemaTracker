// Reconciles the two predecessor authorities so they can never silently diverge.
//
// There are two descriptions of "what came before build B":
//   1. the on-disk numeric rule (ChangelogPredecessor.Resolve) — greatest committed build_id < B
//      that ALSO has the platform dir present; this is what the evolution walk actually consumes; and
//   2. the inventory's declared chain (builds[].predecessor, platform-AGNOSTIC).
// They CAN disagree because the inventory chain may pass through a build that was never committed for
// a given platform. This check walks the inventory chain skipping builds not committed for the
// platform, and asserts the result equals the on-disk predecessor for every committed build. An empty
// result means no drift; a non-empty result names each disagreement.

using Cs2SchemaTracker.Host.Changelog;
using Cs2SchemaTracker.Host.Steam;

namespace Cs2SchemaTracker.Host.Evolution;

/// <summary>
/// Asserts the inventory <c>predecessor</c> chain agrees with the on-disk numeric predecessor rule
/// for a platform (see file header). Pure; reads only the artifacts tree + the already-loaded
/// inventory.
/// </summary>
internal static class PredecessorDriftCheck
{
    /// <summary>
    /// Return a human-readable disagreement for every committed build whose inventory-derived
    /// predecessor (walking <see cref="AssetsInventory.PredecessorOf"/>, skipping builds not committed
    /// for <paramref name="platform"/>) differs from <see cref="ChangelogPredecessor.Resolve"/>. Empty
    /// when the two authorities agree everywhere.
    /// </summary>
    public static IReadOnlyList<string> FindDisagreements(
        string artifactsRoot, AssetsInventory inventory, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(artifactsRoot);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrEmpty(platform);

        var chain = ChangelogPredecessor.OrderedChain(artifactsRoot, platform);
        var committed = new HashSet<uint>();
        foreach (var b in chain)
        {
            if (uint.TryParse(b, out var id))
                committed.Add(id);
        }

        var issues = new List<string>();
        foreach (var b in chain)
        {
            if (!uint.TryParse(b, out var buildId))
                continue;

            var onDisk = ChangelogPredecessor.Resolve(artifactsRoot, b, platform);

            // Walk the inventory predecessor chain to the nearest ancestor committed for this platform.
            uint? invPred = inventory.PredecessorOf(buildId);
            while (invPred is uint p && !committed.Contains(p))
                invPred = inventory.PredecessorOf(p);

            var invStr = invPred?.ToString();
            if (!string.Equals(invStr, onDisk, StringComparison.Ordinal))
            {
                issues.Add(
                    $"{platform} build {b}: on-disk predecessor '{onDisk ?? "(floor)"}' disagrees with " +
                    $"inventory-derived predecessor '{invStr ?? "(floor)"}'");
            }
        }
        return issues;
    }
}
