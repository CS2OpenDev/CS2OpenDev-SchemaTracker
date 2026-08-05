// Steam acquisition: per-platform depot mapping.
//
// Maps each v1-scoped PLATFORM (README.md) to the Steam app + depot
// required to materialize the native binaries needed for that platform's
// extraction.
//
// CONFIRMED via Steam PICS (2026-06-12): CS2 is ONE app, 730. There is no
// separate dedicated-server app and no separate client/server download — the
// per-OS binary depot ships BOTH client and server binaries. So the model is
// TWO platforms, not four tuples:
//
//   windows-x86_64 -> app 730, depot 2347771 (client.dll + server.dll + ...)
//   linux-x86_64   -> app 730, depot 2347773 (client.so  + server.so  + ...)
//
// The shared content depot (2347770, ~59 GB) is NOT included: extraction is
// binaries-only and the walker never reads the content VPKs/maps. Each platform
// therefore resolves to exactly one binary depot.
//
// If a future schema-version bump adds a platform (e.g. `mac-arm64`), it lands
// here AND in README.md. Adding a platform is a non-breaking surface change;
// renaming or removing one is breaking.

using System.Collections.ObjectModel;

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// Compile-time mapping from v1 platform name → (appId, depotIds) needed to
/// acquire that platform's binaries from Steam.
/// </summary>
internal sealed record PlatformAcquisitionPlan(uint AppId, IReadOnlyList<uint> DepotIds)
{
    /// <summary>Stable order for serialization / display.</summary>
    public IReadOnlyList<uint> OrderedDepotIds => DepotIds.OrderBy(d => d).ToList();
}

internal static class PlatformToDepots
{
    /// <summary>The two v1-scoped platform names, in stable order.</summary>
    public static readonly IReadOnlyList<string> KnownPlatforms = new[]
    {
        "linux-x86_64",
        "windows-x86_64",
    };

    private static readonly ReadOnlyDictionary<string, PlatformAcquisitionPlan> Plans =
        new(
            new Dictionary<string, PlatformAcquisitionPlan>(StringComparer.Ordinal)
            {
                // Both platforms are scoped to app 730 (the one CS2 app). Each
                // resolves to exactly its per-OS binary depot, which ships BOTH
                // the client and server modules (proven via PICS). The shared
                // content depot is intentionally omitted (binaries-only).
                ["linux-x86_64"] = new(
                    AppId: SteamAppIdMap.Cs2AppId,
                    DepotIds: new[] { SteamAppIdMap.Cs2LinuxBinariesDepotId }),
                ["windows-x86_64"] = new(
                    AppId: SteamAppIdMap.Cs2AppId,
                    DepotIds: new[] { SteamAppIdMap.Cs2WindowsBinariesDepotId }),
            });

    /// <summary>
    /// Returns the acquisition plan (appId + depotIds) for <paramref name="platform"/>,
    /// or throws if it isn't a v1-scoped platform. The caller is responsible for
    /// surfacing a usage error (EX_USAGE = 64) — this method's contract is
    /// fail-loud on unknown platforms.
    /// </summary>
    public static PlatformAcquisitionPlan Resolve(string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(platform);
        if (!Plans.TryGetValue(platform, out var plan))
        {
            throw new ArgumentException(
                $"Unknown platform '{platform}'. Known platforms (README.md): {string.Join(", ", KnownPlatforms)}.",
                nameof(platform));
        }
        return plan;
    }

    /// <summary>True iff <paramref name="platform"/> is in the v1-scoped set.</summary>
    public static bool IsKnown(string platform) =>
        !string.IsNullOrEmpty(platform) && Plans.ContainsKey(platform);
}
