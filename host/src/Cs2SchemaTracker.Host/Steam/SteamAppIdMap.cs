// Steam acquisition: Steam app/depot identity constants for CS2.
//
// These IDs are public knowledge (visible on SteamDB, in the Steam client's
// `appmanifest_*.acf` files, in Valve's published depot listings, and via the
// anonymous Steam PICS product-info endpoint). They are embedded here as
// constants rather than fetched at runtime so the acquisition flow is
// reproducible without a SteamDB roundtrip.
//
// CONFIRMED via the anonymous Steam PICS endpoint
// (https://api.steamcmd.net/v1/info/730) on 2026-06-12:
//   CS2 is ONE app, 730. Its depots are:
//     2347770  shared content (~59 GB cross-platform game data; VPKs, maps, assets)
//     2347771  Windows binaries (~8 GB; ships BOTH client.dll AND server.dll)
//     2347773  Linux binaries   (ships client.so AND server.so — see note below)
//   There is NO separate dedicated-server app. CS2 does not split client and
//   server into different downloads: the per-OS binary depot carries both.
//
// HISTORICAL NOTE (bug fixed 2026-06-12): an earlier revision of this file
// FABRICATED a "Cs2DedicatedServerAppId = 2347780" plus depots
// 2347780/2347781/2347783 and scoped the *.server tuples to that non-existent
// app. 2347780 is not a CS2 depot at all; PICS against it returns no product
// info for anonymous users, which is exactly why "server acquisition" used to
// fail. Those constants are deleted. The acquisition model is now 2 PLATFORMS
// (windows-x86_64, linux-x86_64), each app 730 + its per-OS binary depot.
//
// If Valve introduces a new depot or retires one of these IDs, this file is
// the single point of change for the host-side acquirer. Walker-side code does
// not consult these constants; it operates on whatever binaries the host puts
// on disk.

namespace Cs2SchemaTracker.Host.Steam;

internal static class SteamAppIdMap
{
    /// <summary>
    /// Counter-Strike 2 app ID. CS2 is ONE app — there is no separate
    /// dedicated-server app. The game is free-to-play, so anonymous depot
    /// access is permitted. Every platform's acquisition is scoped to
    /// this app.
    /// </summary>
    public const uint Cs2AppId = 730;

    // --- Depot IDs (all under app 730) ---------------------------------------

    /// <summary>
    /// Counter-Strike 2 shared content depot (~59 GB cross-platform game data —
    /// VPKs, maps, common assets). NOT NEEDED for schema extraction: the walker
    /// only loads native binaries, which live in the per-OS binary depots below.
    /// Retained as a documented constant (and consumed by the seeded manifest
    /// history) but deliberately NOT fetched by default — extraction is
    /// binaries-only.
    /// </summary>
    public const uint Cs2SharedContentDepotId = 2347770;

    /// <summary>
    /// Counter-Strike 2 Windows binaries depot (~8 GB). Ships BOTH client.dll
    /// AND server.dll (engine2.dll, schemasystem.dll, tier0.dll, etc.) — proven
    /// via PICS. This single depot covers the `windows-x86_64` platform; there
    /// is no separate client/server download.
    /// </summary>
    public const uint Cs2WindowsBinariesDepotId = 2347771;

    /// <summary>
    /// Counter-Strike 2 Linux binaries depot. Mirrors the Windows depot: ships
    /// client.so AND server.so (engine2.so, schemasystem.so, tier0.so, etc.).
    /// This single depot covers the `linux-x86_64` platform.
    /// </summary>
    public const uint Cs2LinuxBinariesDepotId = 2347773;

    /// <summary>
    /// Counter-Strike 2 Workshop Tools depot (gated behind the FREE DLC app
    /// 2279721 "Counter-Strike 2 Workshop Tools"; the depot itself lives under
    /// app 730 like every other CS2 depot). WINDOWS-ONLY — Valve ships no Linux
    /// or macOS Workshop Tools. Carries the editor tool binaries (hammer.dll,
    /// toolframework2.dll, meshsystem.dll, … under game/bin/win64/ +
    /// game/bin/win64/tools/, and modtools.dll under game/csgo/bin/win64/) whose
    /// schema projects the walker registers on top of the base-game modules
    /// (see CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md). Acquired via the
    /// `--tools` slice (DLLs only) and merged into the per-build windows
    /// binaries dir next to the base binaries.
    /// </summary>
    public const uint Cs2WorkshopToolsDepotId = 2347779;

    // --- Out of v1 scope (declared only to prevent ID confusion) -------------

    /// <summary>
    /// Counter-Strike 2 macOS binaries depot. OUT OF v1 SCOPE (mac is excluded
    /// per the requirements doc). Declared here ONLY so future maintainers don't
    /// mistake this ID for a Linux depot. Not referenced by any platform plan.
    /// </summary>
    public const uint Cs2MacBinariesDepotId = 2347772;
}
