// Tests for the tools-content leg of extract (ExtractCommand.ToolsContent): which absence is an
// omission, which is a documented skip, and what reaches provenance.inputs.
//
//   linux-x86_64                     -> both artifacts omitted, TOOLS_DEPOT_NOT_ON_PLATFORM
//   windows, record lacks 2347779    -> skip: no files, no omissions
//   windows, record lists it, marker missing or stale -> skip (a pre-slice cache must not produce
//                                       a false "never shipped" omission)
//   windows, record + current marker -> both emitted; every file read is returned for provenance
//   windows, marker current, no d.ts -> cs_script_api.json omitted CONTENT_NOT_SHIPPED_THIS_ERA
//   --tools-content-root             -> read from that tree regardless of the tuple dir's record

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.ToolsContent;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public sealed class ExtractToolsContentTest : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs2-tools-extract-" + Guid.NewGuid().ToString("N"));

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "ToolsContent", "fixtures");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (string Tuple, string Staging) Seed(bool recordListsTools, bool marker, bool withFixtureFiles, bool withDts = true)
    {
        var tuple = Path.Combine(_root, "tuple");
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(tuple);
        Directory.CreateDirectory(staging);
        var depots = recordListsTools
            ? """[{"depotId":2347771,"manifestId":"111"},{"depotId":2347779,"manifestId":"777"}]"""
            : """[{"depotId":2347771,"manifestId":"111"}]""";
        File.WriteAllText(Path.Combine(tuple, ManifestRecord.FileName), $$"""{"appId":730,"buildId":1,"depots":{{depots}}}""");
        if (marker)
            ToolsContentMarker.Write(tuple);
        if (withFixtureFiles)
        {
            foreach (var src in Directory.EnumerateFiles(FixtureRoot, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(FixtureRoot, src);
                if (!withDts && rel.EndsWith("point_script.d.ts", StringComparison.Ordinal))
                    continue;
                var dst = Path.Combine(tuple, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst);
            }
        }
        return (tuple, staging);
    }

    [Fact]
    public void Linux_records_both_artifacts_as_tools_depot_not_on_platform()
    {
        var (tuple, staging) = Seed(recordListsTools: false, marker: false, withFixtureFiles: false);
        var r = ExtractCommand.EmitToolsContentArtifacts("1", "linux-x86_64", tuple, staging, toolsContentRoot: null);
        Assert.Equal("cs_script_api.json,entity_io.json", string.Join(",", r.Omissions.Select(o => o.Artifact)));
        Assert.All(r.Omissions, o => Assert.Equal(PlatformOmission.Types.Reason.ToolsDepotNotOnPlatform, o.Reason));
        Assert.Empty(r.InputsRead);
        Assert.Empty(Directory.EnumerateFiles(staging));
    }

    [Fact]
    public void Windows_without_the_tools_depot_is_a_silent_skip()
    {
        var (tuple, staging) = Seed(recordListsTools: false, marker: false, withFixtureFiles: true);
        var r = ExtractCommand.EmitToolsContentArtifacts("1", "windows-x86_64", tuple, staging, toolsContentRoot: null);
        Assert.Empty(r.Omissions);
        Assert.Null(r.Tree);
        Assert.Empty(Directory.EnumerateFiles(staging));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_with_a_pre_slice_tools_cache_fails_loud(bool staleMarker)
    {
        var (tuple, staging) = Seed(recordListsTools: true, marker: false, withFixtureFiles: true);
        if (staleMarker)
            ToolsContentMarker.Write(tuple, ToolsBinSelector.ToolsContentGeneration - 1);
        var ex = Assert.Throws<InvalidDataException>(() =>
            ExtractCommand.EmitToolsContentArtifacts("1", "windows-x86_64", tuple, staging, toolsContentRoot: null));
        Assert.Contains("acquire --tools", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(staging));
    }

    [Fact]
    public void Windows_with_a_current_tools_slice_emits_both_and_reports_inputs()
    {
        var (tuple, staging) = Seed(recordListsTools: true, marker: true, withFixtureFiles: true);
        var r = ExtractCommand.EmitToolsContentArtifacts("1", "windows-x86_64", tuple, staging, toolsContentRoot: null);
        Assert.Empty(r.Omissions);
        Assert.True(File.Exists(Path.Combine(staging, "cs_script_api.json")));
        Assert.True(File.Exists(Path.Combine(staging, "entity_io.json")));
        Assert.Equal(
            "content/csgo_addons/cs_script_demo/maps/scripts/point_script.d.ts,"
            + "content/csgo_addons/cs_script_demo/maps/scripts/tsconfig.json,"
            + "game/core/base.fgd,game/core/markup_volumes.fgd,game/csgo/csgo.fgd",
            string.Join(",", r.InputsRead));
        Assert.All(r.InputsRead, p => Assert.True(ToolsBinSelector.IsToolsContentFile(p), p));
    }

    [Fact]
    public void Windows_slice_without_the_declaration_file_omits_only_cs_script_api()
    {
        var (tuple, staging) = Seed(recordListsTools: true, marker: true, withFixtureFiles: true, withDts: false);
        var r = ExtractCommand.EmitToolsContentArtifacts("1", "windows-x86_64", tuple, staging, toolsContentRoot: null);
        var o = Assert.Single(r.Omissions);
        Assert.Equal("cs_script_api.json", o.Artifact);
        Assert.Equal(PlatformOmission.Types.Reason.ContentNotShippedThisEra, o.Reason);
        Assert.True(File.Exists(Path.Combine(staging, "entity_io.json")));
        Assert.False(File.Exists(Path.Combine(staging, "cs_script_api.json")));
    }

    [Fact]
    public void Override_root_is_read_even_when_the_tuple_has_no_tools_depot()
    {
        var (tuple, staging) = Seed(recordListsTools: false, marker: false, withFixtureFiles: false);
        var r = ExtractCommand.EmitToolsContentArtifacts("1", "windows-x86_64", tuple, staging, toolsContentRoot: FixtureRoot);
        Assert.Empty(r.Omissions);
        Assert.NotNull(r.Tree);
        Assert.True(File.Exists(Path.Combine(staging, "entity_io.json")));
    }

    [Fact]
    public void Override_root_that_does_not_exist_fails_loud()
    {
        var (tuple, staging) = Seed(recordListsTools: false, marker: false, withFixtureFiles: false);
        Assert.Throws<DirectoryNotFoundException>(() => ExtractCommand.EmitToolsContentArtifacts(
            "1", "windows-x86_64", tuple, staging, toolsContentRoot: Path.Combine(_root, "nope")));
    }

    [Fact]
    public void Malformed_source_fails_loud()
    {
        var (tuple, staging) = Seed(recordListsTools: true, marker: true, withFixtureFiles: true);
        File.AppendAllText(Path.Combine(tuple, "game", "csgo", "csgo.fgd"), "\n@PointClass base(Missing) = broken []\n");
        Assert.Throws<InvalidDataException>(() =>
            ExtractCommand.EmitToolsContentArtifacts("1", "windows-x86_64", tuple, staging, toolsContentRoot: null));
    }

    [Fact]
    public void Emit_command_refuses_an_override_root_without_an_explicit_out()
    {
        Assert.Equal(64, EmitToolsContentCommand.Run(["--build", "1", "--tools-content-root", FixtureRoot]));
    }
}
