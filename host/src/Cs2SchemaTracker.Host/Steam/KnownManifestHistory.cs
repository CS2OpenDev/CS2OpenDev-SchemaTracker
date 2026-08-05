// aligned — seed of our OWN recorded manifest history (-clean).
//
// This is the in-code seed of the manifest history we accumulate ourselves.
// It records builds we have previously acquired (and whose per-depot manifest
// GIDs we therefore legitimately know without any third-party source). Each
// entry can be turned directly into a `--from-manifest` spec to re-fetch that
// build, PROVIDED the chunks are still CDN-resident (Valve purges old content
// over time; residency is verified empirically by the validate-manifest path,
// not assumed here).
//
// Why a code constant and not just a committed JSON file: the catalogue is the
// authoritative, reviewable record of "builds we can re-fetch from our own
// history". A committed manifest-spec JSON per build is the *interchange* form;
// this is the index. Going forward every acquire also drops a
// manifest-record.json into its tuple dir (ManifestRecord), so the on-disk
// history grows automatically — this seed simply backfills the one build we
// recorded before that mechanism existed.
//
// SEED ENTRY (verified from an earlier successful download, 2026-06-10):
//   build 23669931, app 730, windows-x86_64:
//     depot 2347770 manifest 5146470907583764090  (shared content — recorded GID)
//     depot 2347771 manifest 8287382081622299196  (windows binaries)
// NOTE: under the current binaries-only model, windows-x86_64 acquisition needs
// ONLY the 2347771 binary depot. The 2347770 GID is retained here because it is
// the genuine GID we observed for this build; the probe path keys off (build,
// app) and harmlessly probes both. Acquisition itself uses PlatformToDepots.

namespace Cs2SchemaTracker.Host.Steam;

internal static class KnownManifestHistory
{
    /// <summary>
    /// CS2 build 23669931, windows-x86_64, app 730. The first build we
    /// recorded manifest GIDs for. manifest_created_utc is the date observed at
    /// acquisition time (2026-06-10); the empirical creation timestamp is
    /// re-confirmed whenever the manifest is actually re-downloaded.
    /// </summary>
    public static readonly ManifestRecord Build23669931WindowsClient = new(
        AppId: SteamAppIdMap.Cs2AppId,
        BuildId: 23669931u,
        Depots: new[]
        {
            new ManifestRecordDepot(
                DepotId: SteamAppIdMap.Cs2SharedContentDepotId,        // 2347770
                ManifestId: 5146470907583764090UL,
                ManifestCreatedUtc: "2026-06-10T00:00:00Z"),
            new ManifestRecordDepot(
                DepotId: SteamAppIdMap.Cs2WindowsBinariesDepotId,       // 2347771
                ManifestId: 8287382081622299196UL,
                ManifestCreatedUtc: "2026-06-10T00:00:00Z"),
        });

    /// <summary>All seeded records, stable order (by build then depot).</summary>
    public static readonly IReadOnlyList<ManifestRecord> All = new[]
    {
        Build23669931WindowsClient,
    };

    /// <summary>
    /// Look up a seeded record by (buildId, appId) — used by the validate path so
    /// `validate-manifest --build 23669931 --platform windows-x86_64` can be
    /// driven from our own history with no hand-written spec file.
    /// </summary>
    public static ManifestRecord? TryGet(uint buildId, uint appId) =>
        All.FirstOrDefault(r => r.BuildId == buildId && r.AppId == appId);
}
