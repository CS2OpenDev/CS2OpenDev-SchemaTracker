// groundwork — cheap historical-build feasibility runner.
//
// Drives the two CHEAP, manifest-level checks (NO bulk download) that prove out
// historical-build acquisition before the orchestrator commits to a ~60 GB pull:
//
//   (3a) Resolve the CURRENT public build + per-depot GIDs via PICS, and report
//        whether a given recorded build is still current or now a PREVIOUS build.
//   (3b) Attempt to fetch the DEPOT MANIFEST for a recorded build by its EXPLICIT
//        GIDs, optionally pulling ONE chunk to confirm CDN residency.
//
// This is the `acquire --probe ...` entry. It is a diagnostic: it captures
// per-depot failures into the report rather than failing loud mid-probe, and the
// CLI maps the aggregate verdict to a process exit code (at the command
// boundary — a NO verdict exits non-zero).

using System.Globalization;
using System.Text;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>Aggregate result of a probe run, for the CLI to print + exit on.</summary>
internal sealed record ManifestProbeReport(
    CurrentPicsResult Current,
    ManifestRecord ProbedRecord,
    ExplicitManifestProbe Explicit,
    bool RecordedBuildIsCurrent)
{
    /// <summary>
    /// The feasibility verdict: did EVERY depot of the recorded build still fetch
    /// its historical manifest anonymously?
    /// </summary>
    public bool HistoricalManifestFetchable => Explicit.AllManifestsFetched;

    public string ToHumanReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== validate-manifest report (manifest-level only; NO bulk download) ===");
        sb.AppendLine();
        sb.AppendLine($"(3a) CURRENT public branch for app {Current.AppId}:");
        sb.AppendLine($"      build id = {Current.CurrentBuildId}");
        foreach (var d in Current.Depots)
        {
            sb.AppendLine($"      depot {d.DepotId} current manifest = {d.ManifestId}");
        }
        sb.AppendLine();
        sb.AppendLine($"      Recorded build {ProbedRecord.BuildId} is "
            + (RecordedBuildIsCurrent
                ? "STILL CURRENT (not yet a previous build)."
                : $"a PREVIOUS build (current is {Current.CurrentBuildId})."));
        sb.AppendLine();
        sb.AppendLine($"(3b) EXPLICIT historical manifest fetch for build {Explicit.BuildId} (app {Explicit.AppId}):");
        foreach (var d in Explicit.Depots)
        {
            if (d.ManifestFetched)
            {
                sb.AppendLine($"      depot {d.DepotId} manifest {d.ManifestId}: FETCHED "
                    + $"(created={d.ManifestCreatedUtc}, files={d.FileCount}, "
                    + $"bytes={d.TotalUncompressedBytes.ToString("N0", CultureInfo.InvariantCulture)})");
                if (d.ChunkProbeAttempted)
                {
                    sb.AppendLine($"        sample chunk: "
                        + (d.SampleChunkFetched
                            ? $"CDN-RESIDENT (sha1={d.SampleChunkSha1})"
                            : "NOT fetched"));
                }
            }
            else
            {
                sb.AppendLine($"      depot {d.DepotId} manifest {d.ManifestId}: NOT FETCHABLE — {d.Error}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("VERDICT: historical manifest "
            + (HistoricalManifestFetchable ? "IS" : "IS NOT")
            + " anonymously fetchable for every depot of the recorded build.");
        return sb.ToString();
    }
}

internal static class ManifestProbeRunner
{
    /// <summary>
    /// Run (3a) current-PICS resolution + (3b) explicit-manifest fetch for the
    /// given recorded build, optionally pulling one sample chunk per depot.
    /// </summary>
    public static async Task<ManifestProbeReport> RunAsync(
        ISteamAcquirer acquirer,
        ManifestRecord recordedBuild,
        bool probeOneChunk,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(acquirer);
        ArgumentNullException.ThrowIfNull(recordedBuild);

        var depotIds = recordedBuild.Depots.Select(d => d.DepotId).OrderBy(x => x).ToList();

        // (3a) current public branch.
        var current = await acquirer.ProbeCurrentPicsAsync(
            recordedBuild.AppId, depotIds, ct).ConfigureAwait(false);

        // (3b) explicit historical manifest fetch.
        var spec = recordedBuild.ToManifestSpec();
        var explicitProbe = await acquirer.ProbeExplicitManifestAsync(
            spec, probeOneChunk, ct).ConfigureAwait(false);

        bool isCurrent = current.CurrentBuildId == recordedBuild.BuildId;
        return new ManifestProbeReport(current, recordedBuild, explicitProbe, isCurrent);
    }
}
