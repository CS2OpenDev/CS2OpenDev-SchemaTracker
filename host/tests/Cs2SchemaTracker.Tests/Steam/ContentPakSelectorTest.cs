// content-depot minimal-footprint selection tests.
//
// Covers the pure file-name predicate / chunk-path-formatting logic the two-phase
// content acquire relies on. No Steam, no network — pure string logic.
//
// NOTE: the SelectGameEventsFiles(VpkArchive) chunk-selection tests were removed
// when that selector method was retired. The synthetic v2-VPK builder + CRC/writer
// helpers those tests exclusively required were removed with them (they backed no
// other test in this file).

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentPakSelectorTest
{
    // Used only by SelectedPredicate_matches_only_selected_files_any_slash_style.
    private static readonly string[] PredicateSelected =
        { "game/csgo/pak01_dir.vpk", "game/csgo/pak01_007.vpk" };

    // ---- Phase A: directory-file predicate ----

    [Theory]
    [InlineData("game/csgo/pak01_dir.vpk", true)]
    [InlineData("game\\csgo\\pak01_dir.vpk", true)]   // backslash manifest style
    [InlineData("GAME/CSGO/PAK01_DIR.VPK", true)]     // case-insensitive
    [InlineData("game/csgo/pak01_000.vpk", false)]    // a chunk is not the dir file
    [InlineData("game/csgo/pak02_dir.vpk", false)]    // different archive set
    [InlineData("game/csgo/maps/de_dust.vpk", false)]
    [InlineData("", false)]
    public void DirectoryFile_predicate_matches_only_pak01_dir(string name, bool expected)
        => Assert.Equal(expected, ContentPakSelector.IsDirectoryFile(name));

    // ---- Phase B: chunk path formatting ----

    [Fact]
    public void ChunkFileRelPath_formats_three_digit_index()
    {
        Assert.Equal("game/csgo/pak01_000.vpk", ContentPakSelector.ChunkFileRelPath(0));
        Assert.Equal("game/csgo/pak01_007.vpk", ContentPakSelector.ChunkFileRelPath(7));
        Assert.Equal("game/csgo/pak01_135.vpk", ContentPakSelector.ChunkFileRelPath(135));
    }

    // ---- ContentPak descriptor (csgo + engine core) ----

    [Fact]
    public void Core_pak_paths_are_under_game_core_and_isolated_from_csgo()
    {
        Assert.Equal("game/core/pak01_dir.vpk", ContentPak.Core.DirectoryFileRelPath);
        Assert.Equal("game/core/pak01_000.vpk", ContentPak.Core.ChunkFileRelPath(0));
        Assert.Equal("game/core/pak01_042.vpk", ContentPak.Core.ChunkFileRelPath(42));

        // The core pak matches its own dir file, NOT the csgo one, and vice-versa (isolation).
        Assert.True(ContentPak.Core.IsDirectoryFile("game/core/pak01_dir.vpk"));
        Assert.False(ContentPak.Core.IsDirectoryFile("game/csgo/pak01_dir.vpk"));
        Assert.True(ContentPak.Csgo.IsDirectoryFile("game/csgo/pak01_dir.vpk"));
        Assert.False(ContentPak.Csgo.IsDirectoryFile("game/core/pak01_dir.vpk"));

        // IsAnyPakFile is likewise base-dir scoped.
        Assert.True(ContentPak.Core.IsAnyPakFile("game/core/pak01_007.vpk"));
        Assert.False(ContentPak.Core.IsAnyPakFile("game/csgo/pak01_007.vpk"));

        // Both are the SAME content depot; core is optional, csgo required.
        Assert.True(ContentPak.Csgo.Required);
        Assert.False(ContentPak.Core.Required);
        Assert.Equal(new[] { ContentPak.Csgo, ContentPak.Core }, ContentPak.All);
    }

    // ---- Phase B predicate built from the selected set ----

    [Fact]
    public void SelectedPredicate_matches_only_selected_files_any_slash_style()
    {
        var pred = ContentPakSelector.SelectedPredicate(PredicateSelected);

        Assert.True(pred("game/csgo/pak01_dir.vpk"));
        Assert.True(pred("game\\csgo\\pak01_007.vpk"));  // backslash manifest form
        Assert.False(pred("game/csgo/pak01_008.vpk"));   // not selected
        Assert.False(pred("game/csgo/maps/de_dust.vpk"));
    }

    // ---- Fallback: whole pak01 set ----

    [Theory]
    [InlineData("game/csgo/pak01_dir.vpk", true)]
    [InlineData("game/csgo/pak01_000.vpk", true)]
    [InlineData("game/csgo/pak01_135.vpk", true)]
    [InlineData("game\\csgo\\pak01_042.vpk", true)]
    [InlineData("game/csgo/pak02_000.vpk", false)]   // different set
    [InlineData("game/csgo/maps/de_dust.vpk", false)]
    [InlineData("game/csgo/pak01_notanumber.vpk", false)]
    [InlineData("", false)]
    public void AnyPak01File_matches_whole_pak01_set_only(string name, bool expected)
        => Assert.Equal(expected, ContentPakSelector.IsAnyPak01File(name));
}
