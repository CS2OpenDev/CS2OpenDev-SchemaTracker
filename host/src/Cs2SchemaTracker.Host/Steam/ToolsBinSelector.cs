// Workshop-Tools-depot minimal-footprint (editor-DLLs-only) file selection.
//
// The CS2 Workshop Tools depot (2347779 — windows-only, gated behind the free
// DLC 2279721) is ~2.09 GB/build, but only ~200 MB of it is the editor tool
// DLLs the walker actually LoadLibrary's to register their schema projects
// (hammer.dll, toolframework2.dll, meshsystem.dll, … under game/bin/win64/ and
// game/bin/win64/tools/, plus modtools.dll under game/csgo/bin/win64/ — see
// CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md for the coverage these
// modules add). The remaining ~1.9 GB is editor content/support data (asset
// templates, addon scaffolding, python tooling) the walker never touches.
//
// This is the TOOLS-depot sibling of BinaryBinSelector / ContentPakSelector:
// the SAME file-filter machinery (request only the manifest files whose path
// matches a predicate), applied to the Workshop Tools depot. Unlike the binary
// depot's whole-subtree filter, the slice here IS an extension filter:
//
//   keep  <=>  the path ends with ".dll" AND starts with "game/"
//
// WHY an extension filter works here (where the binary depot needed subtrees):
//   - The tools DLLs' load-time dependencies (tier0.dll, Qt plugins, …) come
//     from the BASE binary depot, which is already co-located in the same
//     per-build windows dir by the binaries acquire — the tools depot ships no
//     versioned-suffix binaries and no sibling non-.dll load dependencies.
//   - The depot's own layout roots everything the walker needs under "game/"
//     (game/bin/win64/, game/bin/win64/tools/, game/csgo/bin/win64/); DLLs
//     outside "game/" (none observed today) would be non-game tooling.
//
// Determinism: the predicate is a pure function of the manifest file path; the
// acquire core already sorts the matched files Ordinal before writing.
//
// Fail-loud: a filtered acquire that matches ZERO manifest files is a
// fail-loud condition enforced by the shared download core (the depot layout
// changed or the wrong depot was named) — never a silent empty acquire.
// AcquireToolsAsync re-asserts it after staging as well.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>
/// Pure helpers that decide which Workshop-Tools-depot (2347779) manifest files
/// are the editor DLLs the walker loads (minimal-footprint tools acquire). No
/// Steam, no I/O — operates on manifest file paths only. The binary-depot
/// analogue is <see cref="BinaryBinSelector"/>; the content-depot analogue is
/// <see cref="ContentPakSelector"/>.
/// </summary>
internal static class ToolsBinSelector
{
    /// <summary>
    /// The depot-relative prefix everything the walker needs lives under.
    /// Forward-slash, trailing-slash so the StartsWith match is a
    /// directory-boundary match (game/ never matches gamex/).
    /// </summary>
    public const string GamePrefix = "game/";

    /// <summary>The extension the slice admits (the walker loads DLLs only).</summary>
    public const string DllExtension = ".dll";

    /// <summary>
    /// True iff <paramref name="manifestFileName"/> is a Workshop Tools editor
    /// DLL: it ends with ".dll" AND lives under the depot's "game/" tree.
    /// Manifest file names may use either slash style (windows depots are packed
    /// with backslashes) and any case, so both sides are normalized before
    /// comparison.
    /// </summary>
    public static bool IsToolsDll(string manifestFileName)
    {
        if (string.IsNullOrEmpty(manifestFileName))
        {
            return false;
        }
        var n = Normalize(manifestFileName);
        return n.EndsWith(DllExtension, StringComparison.OrdinalIgnoreCase)
            && n.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The file-name predicate form of <see cref="IsToolsDll"/> — the form
    /// <see cref="SteamAnonymousAcquirer"/> passes as its file filter.
    /// </summary>
    public static Func<string, bool> Predicate => IsToolsDll;

    /// <summary>
    /// Normalize a manifest file name to forward-slash, no leading slash, for
    /// stable comparison. Does NOT lower-case (comparisons are
    /// OrdinalIgnoreCase) so the original casing is preserved for display.
    /// </summary>
    private static string Normalize(string name)
        => name.Replace('\\', '/').TrimStart('/');
}
