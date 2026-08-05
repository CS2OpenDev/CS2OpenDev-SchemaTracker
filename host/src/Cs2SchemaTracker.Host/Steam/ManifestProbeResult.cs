// groundwork — manifest-level probe results (NO bulk download).
//
// These shapes back the `validate-manifest` command, which does CHEAP,
// manifest-level feasibility checks WITHOUT pulling a multi-GB depot:
//
//   - CurrentPicsResult: what the public branch is on RIGHT NOW (build id +
//     per-depot current GID). Answers "is our recorded build still current, or
//     is it now a genuine PREVIOUS build?".
//
//   - ExplicitManifestProbe: can a SPECIFIC PRIOR (depot, manifestId) still be
//     manifest-fetched anonymously, and (optionally) is at least one of its
//     chunks still CDN-resident? This is the feasibility verdict for
//     historical-build re-fetch.
//
// All fields are plain data; the command prints them deterministically.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>One depot's current public-branch manifest GID, from PICS.</summary>
internal sealed record CurrentDepotManifest(uint DepotId, ulong ManifestId);

/// <summary>
/// The current public-branch state for an app: the build id and the per-depot
/// current manifest GIDs (PICS-current). Depots are ordered by depot ID.
/// </summary>
internal sealed record CurrentPicsResult(
    uint AppId,
    uint CurrentBuildId,
    IReadOnlyList<CurrentDepotManifest> Depots);

/// <summary>
/// The result of probing ONE explicit (depot, manifestId) at manifest level.
/// <see cref="ManifestFetched"/> is the key feasibility bit: did the historical
/// manifest still download anonymously? <see cref="SampleChunkFetched"/> (when a
/// chunk probe was requested) additionally confirms chunk-level CDN residency.
/// </summary>
internal sealed record ExplicitDepotManifestProbe(
    uint DepotId,
    ulong ManifestId,
    bool ManifestFetched,
    string? ManifestCreatedUtc,
    int FileCount,
    long TotalUncompressedBytes,
    bool ChunkProbeAttempted,
    bool SampleChunkFetched,
    string? SampleChunkSha1,
    string? Error);

/// <summary>The full explicit-manifest probe across one build's depots.</summary>
internal sealed record ExplicitManifestProbe(
    uint AppId,
    uint BuildId,
    IReadOnlyList<ExplicitDepotManifestProbe> Depots)
{
    /// <summary>True iff EVERY probed depot's historical manifest was fetched.</summary>
    public bool AllManifestsFetched => Depots.Count > 0 && Depots.All(d => d.ManifestFetched);
}
