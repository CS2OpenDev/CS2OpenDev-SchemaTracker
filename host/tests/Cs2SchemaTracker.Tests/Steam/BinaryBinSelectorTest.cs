// loadable-binaries-only binary-depot selection tests (backfill).
//
// Covers the pure file-selection logic the --binaries-only acquire relies on,
// plus the fail-loud cases. No Steam, no network: the manifest
// file paths are a hand-built fixture mirroring the REAL CS2 binary-depot layout
// inspected from the cached full depot at <binaries-root>/23669931/ (build
// 23669931), so we can assert EXACTLY which files the loadable-binary filter
// keeps and which ~7.35 GB of shader/content/support data it drops.
//
// The load-bearing invariant under test — CRITICAL, do NOT under-fetch:
//   the filter MUST keep every file under the per-OS bin-directory subtrees —
//   including subdir DLLs (Qt plugins, subtools), versioned linux .so.N files,
//   and non-binary support files — because LoadLibrary/dlopen needs the sibling
//   dependencies present. The walker loads 99 DLLs from the win64 bin dirs; the
//   filter is a strict superset of those by construction (whole subtree).

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class BinaryBinSelectorTest
{
    // ---- Real-layout fixtures (from the cached build-23669931 full binary depot) ----

    // WINDOWS: every file under game/bin/win64/ + game/csgo/bin/win64/ is loadable.
    // Includes top-level DLLs, exes, .signatures, and the qt5_plugins/ + subtools/
    // SUBDIRECTORY DLLs (sibling deps LoadLibrary needs). 99 win64 *.dll total.
    private static readonly string[] WindowsLoadablePaths =
    {
        // game/bin/win64 — top-level engine/runtime modules
        "game/bin/win64/tier0.dll",
        "game/bin/win64/engine2.dll",
        "game/bin/win64/schemasystem.dll",
        "game/bin/win64/cs2.exe",
        "game/bin/win64/foreign.signatures",
        "game/bin/win64/system.signatures",
        // game/bin/win64 — SUBDIRECTORY deps (must NOT be dropped)
        "game/bin/win64/qt5_plugins/imageformats/qjpeg.dll",
        "game/bin/win64/qt5_plugins/platforms/qwindows.dll",
        "game/bin/win64/subtools/vprof_subtool.dll",
        // game/csgo/bin/win64 — game modules
        "game/csgo/bin/win64/client.dll",
        "game/csgo/bin/win64/server.dll",
        "game/csgo/bin/win64/host.dll",
        "game/csgo/bin/win64/matchmaking.dll",
    };

    // WINDOWS: representative files OUTSIDE the bin subtrees — the ~7.35 GB the
    // walker never touches. Must all be DROPPED.
    private static readonly string[] WindowsNonLoadablePaths =
    {
        "game/csgo/pak01_dir.vpk",
        "game/csgo/pak01_037.vpk",
        "game/csgo/maps/de_dust2.vpk",
        "game/csgo/resource/overviews/de_mirage.txt",
        "game/core/tools/demoinfo2.dll",                 // a DLL, but NOT under a bin/win64 subtree
        "game/bin/win64x/decoy.dll",                     // win64x is NOT win64 (boundary)
        "game/bin/linuxsteamrt64/libtier0.so",           // wrong platform's bin dir
        "steam.inf",
    };

    // LINUX: every file under game/bin/linuxsteamrt64/ + game/csgo/bin/linuxsteamrt64/.
    // Includes the no-extension executable (cs2), the .flt support file, and the
    // VERSIONED shared objects (libavcodec.so.58, libSDL3.so.0) whose names do NOT
    // end in ".so" — an extension filter would silently drop these.
    private static readonly string[] LinuxLoadablePaths =
    {
        "game/bin/linuxsteamrt64/libtier0.so",
        "game/bin/linuxsteamrt64/libschemasystem.so",
        "game/bin/linuxsteamrt64/cs2",                   // no extension
        "game/bin/linuxsteamrt64/fltlnx64.flt",          // support file
        "game/bin/linuxsteamrt64/libavcodec.so.58",      // versioned .so (not *.so)
        "game/bin/linuxsteamrt64/libSDL3.so.0",          // versioned .so (not *.so)
        "game/csgo/bin/linuxsteamrt64/client.so",
        "game/csgo/bin/linuxsteamrt64/server.so",
    };

    private static readonly string[] LinuxNonLoadablePaths =
    {
        "game/csgo/pak01_dir.vpk",
        "game/csgo/pak01_037.vpk",
        "game/bin/win64/tier0.dll",                      // wrong platform's bin dir
        "game/bin/linuxsteamrt64x/decoy.so",             // boundary: not linuxsteamrt64
    };

    // ---- Windows predicate ----

    [Fact]
    public void Windows_predicate_keeps_every_loadable_bin_file()
    {
        var pred = BinaryBinSelector.PredicateFor("windows-x86_64");
        foreach (var path in WindowsLoadablePaths)
        {
            Assert.True(pred(path), $"expected KEPT (loadable): {path}");
        }
    }

    [Fact]
    public void Windows_predicate_drops_everything_outside_bin_subtrees()
    {
        var pred = BinaryBinSelector.PredicateFor("windows-x86_64");
        foreach (var path in WindowsNonLoadablePaths)
        {
            Assert.False(pred(path), $"expected DROPPED (not loadable): {path}");
        }
    }

    [Fact]
    public void Windows_predicate_matches_backslash_manifest_paths_and_is_case_insensitive()
    {
        var pred = BinaryBinSelector.PredicateFor("windows-x86_64");
        // Windows depots are packed with backslashes and arbitrary casing.
        Assert.True(pred(@"game\bin\win64\tier0.dll"));
        Assert.True(pred(@"GAME\CSGO\BIN\WIN64\CLIENT.DLL"));
        Assert.True(pred(@"game\bin\win64\subtools\vprof_subtool.dll"));
        Assert.False(pred(@"game\csgo\pak01_dir.vpk"));
    }

    // ---- Linux predicate ----

    [Fact]
    public void Linux_predicate_keeps_every_loadable_bin_file_including_versioned_so()
    {
        var pred = BinaryBinSelector.PredicateFor("linux-x86_64");
        foreach (var path in LinuxLoadablePaths)
        {
            Assert.True(pred(path), $"expected KEPT (loadable): {path}");
        }
    }

    [Fact]
    public void Linux_predicate_drops_everything_outside_bin_subtrees()
    {
        var pred = BinaryBinSelector.PredicateFor("linux-x86_64");
        foreach (var path in LinuxNonLoadablePaths)
        {
            Assert.False(pred(path), $"expected DROPPED (not loadable): {path}");
        }
    }

    // ---- Prefix-set surface (documents EXACTLY which subtrees are fetched) ----

    private static readonly string[] ExpectedWindowsPrefixes =
        { "game/bin/win64/", "game/csgo/bin/win64/" };
    private static readonly string[] ExpectedLinuxPrefixes =
        { "game/bin/linuxsteamrt64/", "game/csgo/bin/linuxsteamrt64/" };

    [Fact]
    public void Windows_prefixes_are_the_two_win64_bin_subtrees()
        => Assert.Equal(ExpectedWindowsPrefixes, BinaryBinSelector.PrefixesFor("windows-x86_64"));

    [Fact]
    public void Linux_prefixes_are_the_two_linuxsteamrt64_bin_subtrees()
        => Assert.Equal(ExpectedLinuxPrefixes, BinaryBinSelector.PrefixesFor("linux-x86_64"));

    [Fact]
    public void Trailing_slash_prefix_prevents_sibling_directory_false_match()
    {
        // game/bin/win64/ must NOT match game/bin/win64x/... (directory boundary).
        var pred = BinaryBinSelector.PredicateFor("windows-x86_64");
        Assert.True(pred("game/bin/win64/a.dll"));
        Assert.False(pred("game/bin/win64x/a.dll"));
        Assert.False(pred("game/bin/win64.txt"));   // a file named win64.txt next to the dir
    }

    // ---- Fail-loud surface ----

    [Theory]
    [InlineData("mac-arm64")]
    [InlineData("windows")]
    [InlineData("linux-x86_64.server")]   // a retired 4-tuple name
    public void Unknown_platform_throws_fail_loud(string platform)
    {
        Assert.Throws<ArgumentException>(() => BinaryBinSelector.PrefixesFor(platform));
        Assert.Throws<ArgumentException>(() => BinaryBinSelector.PredicateFor(platform));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_or_null_manifest_path_never_matches(string? name)
    {
        Assert.False(BinaryBinSelector.IsLoadableBinaryFile(name!, BinaryBinSelector.WindowsBinPrefixes));
        Assert.False(BinaryBinSelector.IsLoadableBinaryFile(name!, BinaryBinSelector.LinuxBinPrefixes));
    }
}
