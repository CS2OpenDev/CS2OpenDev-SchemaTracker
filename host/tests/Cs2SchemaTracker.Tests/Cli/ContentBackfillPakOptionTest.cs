// `content-backfill --pak <csgo|core>` option tests.
//
// The option picks WHICH content pak the run plans for. `core` (the default, and the historical
// behaviour before the option existed) targets the engine resource/core.gameevents pak; `csgo`
// targets the primary content pak, which is what refreshes store copies trimmed under an older
// required-set generation. An unrecognized value is a usage error (exit 64) naming the accepted set
// — never a silent fall back to a pak the operator did not ask for.
//
// The dry-run cases contact no Steam: with no committed builds under the root the planner returns
// nothing and the command reports which pak it planned for.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public class ContentBackfillPakOptionTest
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backfill-pak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Theory]
    [InlineData("csgo", "game/csgo")]
    [InlineData("core", "game/core")]
    [InlineData("CSGO", "game/csgo")]   // case-insensitive
    public void Pak_Option_Selects_The_Named_Pak(string value, string expectedBaseRelDir)
    {
        Assert.True(ContentBackfillCommand.TryResolvePak(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["pak"] = value }, out var pak));
        Assert.Equal(expectedBaseRelDir, pak.BaseRelDir);
    }

    [Fact]
    public void Absent_Pak_Option_Defaults_To_Core()
    {
        Assert.True(ContentBackfillCommand.TryResolvePak(
            new Dictionary<string, string>(StringComparer.Ordinal), out var pak));
        Assert.Equal(ContentPak.Core, pak);
        Assert.Equal(ContentBackfillCommand.DefaultPakName, pak.Name);
    }

    [Theory]
    [InlineData("tools")]
    [InlineData("game/csgo")]
    [InlineData("csgo2")]
    public void Unknown_Pak_Option_Is_Rejected(string value)
    {
        Assert.False(ContentBackfillCommand.TryResolvePak(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["pak"] = value }, out _));
    }

    [Fact]
    public void Dry_Run_Plans_For_The_Requested_Pak()
    {
        var root = NewRoot();
        try
        {
            var (csgoCode, _, csgoErr) = ConsoleCapture.Run(() =>
                ContentBackfillCommand.RunAsync(
                    new[] { "--binaries-root", root, "--pak", "csgo" }, acquirerFactory: null)
                    .GetAwaiter().GetResult());
            Assert.Equal(0, csgoCode);
            Assert.Contains("game/csgo", csgoErr, StringComparison.Ordinal);

            var (coreCode, _, coreErr) = ConsoleCapture.Run(() =>
                ContentBackfillCommand.RunAsync(
                    new[] { "--binaries-root", root }, acquirerFactory: null).GetAwaiter().GetResult());
            Assert.Equal(0, coreCode);
            Assert.Contains("game/core", coreErr, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Invalid_Pak_Value_Exits_64_Naming_The_Accepted_Set()
    {
        var root = NewRoot();
        try
        {
            var (code, _, err) = ConsoleCapture.Run(() =>
                ContentBackfillCommand.RunAsync(
                    new[] { "--binaries-root", root, "--pak", "tools" }, acquirerFactory: null)
                    .GetAwaiter().GetResult());

            Assert.Equal(64, code);
            Assert.Contains("unknown --pak 'tools'", err, StringComparison.Ordinal);
            Assert.Contains(ContentPak.NamesForHelp, err, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(root);
        }
    }
}
