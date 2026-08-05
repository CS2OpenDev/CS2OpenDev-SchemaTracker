// Workshop-Tools editor-DLL selection tests (--tools slice).
//
// Covers the pure file-selection logic the --tools acquire relies on: the predicate must keep
// EXACTLY the manifest files ending ".dll" (ordinal, case-insensitive) under the depot's own
// "game/" tree — the editor modules the walker LoadLibrary's (hammer.dll, toolframework2.dll,
// game/csgo/bin/win64/modtools.dll, …; ~200 MB of the ~2.09 GB depot) — and drop everything else
// (editor content, python tooling, PDBs, non-game trees). No Steam, no network: the paths are a
// hand-built fixture mirroring the real 2347779 depot layout.

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ToolsBinSelectorTest
{
    // Editor DLLs the walker loads — every one must be KEPT.
    private static readonly string[] ToolsDllPaths =
    {
        "game/bin/win64/hammer.dll",
        "game/bin/win64/toolframework2.dll",
        "game/bin/win64/meshsystem.dll",
        "game/bin/win64/tools/hammer.dll",           // the tools/ SUBDIR (must not be dropped)
        "game/bin/win64/tools/model_editor.dll",
        "game/csgo/bin/win64/modtools.dll",          // the one game/csgo tools module
    };

    // Representative non-DLL / non-game files of the tools depot — all must be DROPPED.
    private static readonly string[] NonToolsPaths =
    {
        "game/bin/win64/tools/hammer.pdb",           // not a .dll
        "game/bin/win64/hammer.exe",                 // not a .dll
        "game/csgo/gameinfo.gi",                     // editor content
        "game/core/tools_asset_info.bin",            // support data
        "game/sdk_content/maps/de_dust2_d.vmap",     // asset templates
        "content/csgo_addons/readme.txt",            // non-game tree
        "sdktools/python/python3.dll",               // a DLL, but NOT under game/
        "gamex/bin/win64/decoy.dll",                 // boundary: gamex/ is not game/
        "steam.inf",
    };

    [Fact]
    public void Predicate_keeps_every_editor_dll_under_game()
    {
        foreach (var path in ToolsDllPaths)
        {
            Assert.True(ToolsBinSelector.Predicate(path), $"expected KEPT (editor DLL): {path}");
        }
    }

    [Fact]
    public void Predicate_drops_non_dll_and_non_game_paths()
    {
        foreach (var path in NonToolsPaths)
        {
            Assert.False(ToolsBinSelector.Predicate(path), $"expected DROPPED (not an editor DLL): {path}");
        }
    }

    [Fact]
    public void Predicate_matches_backslash_manifest_paths_and_is_case_insensitive()
    {
        // Windows depots are packed with backslashes and arbitrary casing.
        Assert.True(ToolsBinSelector.IsToolsDll(@"game\bin\win64\tools\hammer.dll"));
        Assert.True(ToolsBinSelector.IsToolsDll(@"GAME\CSGO\BIN\WIN64\MODTOOLS.DLL"));
        Assert.False(ToolsBinSelector.IsToolsDll(@"game\bin\win64\tools\hammer.pdb"));
        Assert.False(ToolsBinSelector.IsToolsDll(@"sdktools\python\python3.dll"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_null_manifest_path_never_matches(string? name)
    {
        Assert.False(ToolsBinSelector.IsToolsDll(name!));
    }
}
