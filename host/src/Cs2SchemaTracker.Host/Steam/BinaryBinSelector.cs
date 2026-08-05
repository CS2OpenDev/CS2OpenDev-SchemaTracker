// binary-depot minimal-footprint (loadable-binaries-only) file selection.
//
// The CS2 per-OS BINARY depot (2347771 windows / 2347773 linux) is ~7.9 GB/build.
// Of that, only ~0.46 GB is the loadable native binaries the walker actually
// dlopen/LoadLibrary's (the DLLs/.so under the per-OS `bin` directories). The
// remaining ~7.35 GB is shader/content VPKs and support data the walker never
// touches. For the historical backfill (337 windows + 330 linux manifests),
// pulling the FULL depot for every build is ~5.3 TB; the loadable-binaries-only
// slice is ~307 GB total — the difference between "fits on the disk" and "does
// not".
//
// This is the BINARY-depot sibling of ContentPakSelector: the SAME file-filter
// machinery (request only the manifest files whose path matches a predicate),
// applied to the per-platform binary depot instead of the content depot. The
// filter is a WHOLE-SUBTREE prefix match on the per-OS bin directories — NOT an
// extension filter:
//
//   windows-x86_64:
//     game/bin/win64/         (cs2.exe, tier0.dll, schemasystem.dll, … + qt5_plugins/, subtools/)
//     game/csgo/bin/win64/    (client.dll, server.dll, host.dll, matchmaking.dll)
//
//   linux-x86_64:
//     game/bin/linuxsteamrt64/        (cs2, libtier0.so, … + versioned libavcodec.so.58 etc.)
//     game/csgo/bin/linuxsteamrt64/   (client.so, server.so, host.so, matchmaking.so)
//
// WHY whole-subtree (every file, not just *.dll/*.so):
//   - LoadLibrary/dlopen needs SIBLING dependency binaries present (e.g. the Qt
//     image-format/platform plugin DLLs under win64/qt5_plugins/, the subtools/
//     DLLs) or a module load fails.
//   - The walker loads 99 DLLs from the win64 bin dirs (83 top-level + 12 under
//     qt5_plugins/ + subtools/ in game/bin/win64, + 4 in game/csgo/bin/win64).
//     The win64 *.dll count under exactly these two prefixes is 99 — capturing
//     the whole subtree is a strict superset of the walker's 99 modules, so the
//     filter CANNOT under-fetch them.
//   - Linux ships versioned shared objects (libavcodec.so.58, libSDL3.so.0, …)
//     whose names do NOT end in ".so"; an extension filter would silently drop
//     them. The subtree filter keeps every file regardless of name.
//   - Support files (foreign.signatures / system.signatures on windows,
//     fltlnx64.flt on linux) ride along for free — tiny, and safer to include
//     than to reason about per-build.
//
// Determinism: the predicate is a pure function of the manifest file
// path; the acquire core already sorts the matched files Ordinal before writing.
//
// Fail-loud: a filtered acquire that matches ZERO manifest files is a
// fail-loud condition enforced by the shared download core (the depot layout
// changed or the wrong depot/platform was named) — never a silent empty acquire.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// Pure helpers that decide which BINARY-depot manifest files are the loadable
/// native binaries the walker needs (minimal-footprint binary acquire for
/// the historical backfill). No Steam, no I/O — operates on manifest file
/// paths only. The content-depot analogue is <see cref="ContentPakSelector"/>.
/// </summary>
internal static class BinaryBinSelector
{
    /// <summary>
    /// The depot-relative bin-directory prefixes whose ENTIRE subtree is fetched,
    /// per platform. Forward-slash, trailing-slash so the StartsWith match is a
    /// directory-boundary match (game/bin/win64/ never matches game/bin/win64x/).
    /// </summary>
    public static readonly IReadOnlyList<string> WindowsBinPrefixes = new[]
    {
        "game/bin/win64/",
        "game/csgo/bin/win64/",
    };

    public static readonly IReadOnlyList<string> LinuxBinPrefixes = new[]
    {
        "game/bin/linuxsteamrt64/",
        "game/csgo/bin/linuxsteamrt64/",
    };

    /// <summary>
    /// Returns the loadable-binary bin-directory prefixes for <paramref name="platform"/>.
    /// Fail-loud on an unknown platform — never default to "fetch nothing"
    /// or "fetch everything".
    /// </summary>
    public static IReadOnlyList<string> PrefixesFor(string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(platform);
        return platform switch
        {
            "windows-x86_64" => WindowsBinPrefixes,
            "linux-x86_64" => LinuxBinPrefixes,
            _ => throw new ArgumentException(
                $"Unknown platform '{platform}'. Known platforms (README.md): " +
                $"{string.Join(", ", PlatformToDepots.KnownPlatforms)}.",
                nameof(platform)),
        };
    }

    /// <summary>
    /// True iff <paramref name="manifestFileName"/> lives under one of the given
    /// loadable-binary bin-directory subtrees. Manifest file names may use either
    /// slash style (windows depots are packed with backslashes) and any case, so
    /// both sides are normalized before comparison.
    /// </summary>
    public static bool IsLoadableBinaryFile(string manifestFileName, IReadOnlyList<string> prefixes)
    {
        ArgumentNullException.ThrowIfNull(prefixes);
        if (string.IsNullOrEmpty(manifestFileName))
        {
            return false;
        }
        var n = Normalize(manifestFileName);
        foreach (var prefix in prefixes)
        {
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Build the file-name predicate for a platform's loadable-binary subtrees —
    /// the form <see cref="SteamAnonymousAcquirer"/> passes as its file filter.
    /// </summary>
    public static Func<string, bool> PredicateFor(string platform)
    {
        var prefixes = PrefixesFor(platform);
        return name => IsLoadableBinaryFile(name, prefixes);
    }

    /// <summary>
    /// Normalize a manifest file name to forward-slash, no leading slash, for
    /// stable prefix comparison. Does NOT lower-case (comparisons are
    /// OrdinalIgnoreCase) so the original casing is preserved for display.
    /// </summary>
    private static string Normalize(string name)
        => name.Replace('\\', '/').TrimStart('/');
}
