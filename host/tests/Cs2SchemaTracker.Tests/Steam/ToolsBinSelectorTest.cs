// Workshop-Tools editor-DLL selection tests (--tools slice).
//
// Covers the pure file-selection logic the --tools acquire relies on: the predicate must keep
// every manifest file ending ".dll" (ordinal, case-insensitive) under the depot's own
// "game/" tree — the editor modules the walker LoadLibrary's (hammer.dll, toolframework2.dll,
// game/csgo/bin/win64/modtools.dll, …; ~200 MB of the ~2.09 GB depot) — and drop everything else
// (editor content, python tooling, PDBs, non-game trees). The slice also keeps a handful of loose
// tools-content files the host parses; those are covered by ToolsContentSelectorTest below, and
// none of the paths in this class is one. No Steam, no network: the paths are a hand-built fixture
// mirroring the real 2347779 depot layout.

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
        "content/csgo_addons/readme.txt",            // non-game tree (and not tools content either)
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

// The tools-content half of the slice: the loose files the HOST parses (entity_io.json from the
// FGDs, cs_script_api.json from Valve's declaration file). Paths mirror the real 2347779 layout,
// including the FGD copies that must NOT be selected because no include graph reaches them.
public class ToolsContentSelectorTest
{
    private static readonly string[] Kept =
    {
        "game/csgo/csgo.fgd",
        "game/csgo/postprocessing.fgd",
        "game/csgo/models_gamedata.fgd",
        "game/core/base.fgd",
        "game/core/lights_base.fgd",
        "game/core/vdata_base.fgd",
        "content/csgo_addons/cs_script_demo/maps/scripts/point_script.d.ts",
        "content/csgo_addons/cs_script_demo/maps/scripts/tsconfig.json",
    };

    private static readonly string[] Dropped =
    {
        "game/csgo/import_scripts/bin/base.fgd",              // importer's stale copy
        "game/csgo/import_scripts/bin/csgo.fgd",
        "game/csgo/tools/workshop_addoninfo.fgd",              // editor-tool FGDs, not entities
        "game/csgo/tools/cs2_item_editor/cs2_item_editor.fgd",
        "game/core/tools/workshop_addoninfo_base.fgd",
        "content/csgo/maps/editor/zoo/scripts/point_script.d.ts", // the one-line stub copy
        "content/csgo_addons/cs_script_demo/maps/scripts/hello.js",
        "content/csgo_addons/readme.txt",
        "game/csgo/gameinfo.gi",
        "game/sdk/csgo.fgd",
        "gamex/csgo/csgo.fgd",
    };

    [Fact]
    public void Keeps_root_fgds_and_the_cs_script_declarations()
    {
        foreach (var path in Kept)
        {
            Assert.True(ToolsBinSelector.IsToolsContentFile(path), $"expected KEPT: {path}");
            Assert.True(ToolsBinSelector.Predicate(path), $"expected the slice filter to keep: {path}");
            Assert.False(ToolsBinSelector.IsToolsDll(path), $"not a DLL: {path}");
        }
    }

    [Fact]
    public void Drops_nested_fgds_stub_copies_and_other_content()
    {
        foreach (var path in Dropped)
        {
            Assert.False(ToolsBinSelector.IsToolsContentFile(path), $"expected DROPPED: {path}");
            Assert.False(ToolsBinSelector.Predicate(path), $"expected the slice filter to drop: {path}");
        }
    }

    [Fact]
    public void Matches_backslash_and_mixed_case_paths()
    {
        Assert.True(ToolsBinSelector.IsToolsContentFile(@"game\csgo\CSGO.FGD"));
        Assert.True(ToolsBinSelector.IsToolsContentFile(@"Content\csgo_addons\cs_script_demo\maps\scripts\Point_Script.d.ts"));
        Assert.False(ToolsBinSelector.IsToolsContentFile(@"game\csgo\tools\workshop_addoninfo.fgd"));
    }

    [Fact]
    public void Slice_filter_still_keeps_every_editor_dll()
    {
        Assert.True(ToolsBinSelector.Predicate("game/bin/win64/tools/hammer.dll"));
        Assert.True(ToolsBinSelector.Predicate("game/csgo/bin/win64/modtools.dll"));
        Assert.False(ToolsBinSelector.Predicate("game/bin/win64/tools/hammer.pdb"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_null_path_never_matches(string? name)
    {
        Assert.False(ToolsBinSelector.IsToolsContentFile(name!));
    }
}
