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
// PLUS a small loose-content slice the HOST parses rather than the walker loads
// (IsToolsContentFile): the Hammer FGDs directly under game/csgo/ and game/core/
// (entity_io.json) and Valve's cs_script declaration file + tsconfig.json under
// content/csgo_addons/cs_script_demo/maps/scripts/ (cs_script_api.json) — a few
// hundred KB. They land at their depot-relative paths in the same tuple dir; none
// sits under a bin/ directory, so the walker's module enumeration never sees them.
// The tools acquire stamps tools-content.json (ToolsContentGeneration) beside
// manifest-record.json so an older cache that predates this slice is detectable.
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
    /// The loose tools-content files the host parses (as opposed to loads): the Hammer FGDs that
    /// entity_io.json is built from, and Valve's cs_script declaration file + its tsconfig.json
    /// that cs_script_api.json is built from. Depot-relative, forward-slash, compared
    /// case-insensitively. The FGD rule is a directory rule (see <see cref="IsToolsContentFile"/>);
    /// the cs_script files are exact paths.
    /// </summary>
    public static readonly IReadOnlyList<string> ToolsContentExactPaths = new[]
    {
        "content/csgo_addons/cs_script_demo/maps/scripts/point_script.d.ts",
        "content/csgo_addons/cs_script_demo/maps/scripts/tsconfig.json",
    };

    /// <summary>
    /// The directories whose DIRECT <c>*.fgd</c> children are kept. Subdirectories are not:
    /// game/csgo/import_scripts/bin/ carries stale copies of base.fgd / csgo.fgd for an importer, and
    /// game/csgo/tools/ + game/core/tools/ carry editor-tool FGDs (workshop_addoninfo.fgd,
    /// cs2_item_editor.fgd) that are not entity definitions. None of them is reachable from
    /// game/csgo/csgo.fgd's include graph.
    /// </summary>
    public static readonly IReadOnlyList<string> FgdDirs = new[] { "game/csgo/", "game/core/" };

    /// <summary>
    /// Selection generation of the tools-content slice. A tools acquire stamps it into
    /// <see cref="ToolsContentMarkerFileName"/> in the tuple dir; extract reads tools content only
    /// from a tuple dir whose marker is at this generation, and the cache-first acquire treats an
    /// older (or missing) marker as "tools leg incomplete" and re-fetches the slice. Bump it whenever
    /// <see cref="IsToolsContentFile"/> starts selecting something it did not before — otherwise an
    /// older cache would be read as "this build never shipped the file".
    /// </summary>
    public const int ToolsContentGeneration = 1;

    /// <summary>The marker a tools acquire writes beside manifest-record.json.</summary>
    public const string ToolsContentMarkerFileName = "tools-content.json";

    /// <summary>
    /// True iff <paramref name="manifestFileName"/> is one of the loose tools-content files the
    /// host parses: a <c>*.fgd</c> DIRECTLY under game/csgo/ or game/core/, or one of
    /// <see cref="ToolsContentExactPaths"/>. Either slash style, any case.
    /// </summary>
    public static bool IsToolsContentFile(string manifestFileName)
    {
        if (string.IsNullOrEmpty(manifestFileName))
        {
            return false;
        }
        var n = Normalize(manifestFileName);
        foreach (var exact in ToolsContentExactPaths)
        {
            if (string.Equals(n, exact, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!n.EndsWith(".fgd", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var dir in FgdDirs)
        {
            if (n.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                && n.IndexOf('/', dir.Length) < 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The whole tools-depot slice: the editor DLLs the walker loads PLUS the tools-content files
    /// the host parses. This is the file filter <see cref="SteamAnonymousAcquirer"/> passes — the
    /// selector stays the single source of truth for what a tools acquire keeps.
    /// </summary>
    public static bool IsToolsSliceFile(string manifestFileName)
        => IsToolsDll(manifestFileName) || IsToolsContentFile(manifestFileName);

    /// <summary>
    /// The file-name predicate form of <see cref="IsToolsSliceFile"/> — the form
    /// <see cref="SteamAnonymousAcquirer"/> passes as its file filter.
    /// </summary>
    public static Func<string, bool> Predicate => IsToolsSliceFile;

    /// <summary>
    /// Normalize a manifest file name to forward-slash, no leading slash, for
    /// stable comparison. Does NOT lower-case (comparisons are
    /// OrdinalIgnoreCase) so the original casing is preserved for display.
    /// </summary>
    private static string Normalize(string name)
        => name.Replace('\\', '/').TrimStart('/');
}
