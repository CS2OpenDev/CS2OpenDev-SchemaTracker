// Parser-level coverage of the extract CLI surface.
//
// The rest of the suite drives ExtractCommand.Run(args) DIRECTLY — bypassing the System.CommandLine
// root parser. That means a flag the custom parser accepts but which is NOT declared on the
// System.CommandLine extract command parses fine through those seams yet is REJECTED by the real
// CLI with "Unrecognized command or argument" (System.CommandLine rejects unknown options by
// default). These tests drive the REAL root parser (Program.BuildRootCommand) so every documented
// extract flag is guaranteed to be declared + accepted end to end.

using System.CommandLine;
using System.CommandLine.Parsing;

using Cs2SchemaTracker.Host;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class ExtractCliParseTest
{
    private static List<string> ParseErrors(string commandLine)
        => Program.BuildRootCommand().Parse(commandLine).Errors.Select(e => e.Message).ToList();

    [Theory]
    // Every documented extract flag must be accepted by the REAL parser, in the forward-capture
    // shape scheduled-extract uses and in a batch shape carrying the whole flag set.
    [InlineData("extract --build 24248951 --platform windows-x86_64 --commit --no-localization-changelog")]
    [InlineData("extract --build 1 --platform linux-x86_64 --no-changelog")]
    [InlineData("extract --all --platform windows-x86_64 --verify --no-gate --force --no-acquire --no-changelog --no-localization-changelog")]
    [InlineData("extract --build 1 --platform windows-x86_64 --commit --single-walk")]
    [InlineData("extract --build 1 --platform windows-x86_64 --commit --allow-mixed-walkers")]
    // --allow-walker-change is batch-level (one flag authorises the whole run), so it must parse
    // alongside every selection family, not just --build.
    [InlineData("extract --build 1 --platform windows-x86_64 --commit --allow-walker-change")]
    [InlineData("extract --all --platform windows-x86_64 --commit --allow-walker-change")]
    [InlineData("extract --era cs2-2026-04-21 --platform windows-x86_64 --commit --allow-walker-change --allow-mixed-walkers")]
    [InlineData("extract --build 1 --platform windows-x86_64 --tools-content-root C:/cs2")]
    [InlineData("emit-tools-content --build 1 --out x --tools-content-root C:/cs2")]
    public void Documented_Extract_Flags_Are_Accepted_By_The_Real_Parser(string commandLine)
    {
        Assert.Empty(ParseErrors(commandLine));
    }

    [Fact]
    public void Real_Parser_Still_Rejects_An_Unknown_Extract_Flag()
    {
        // Sanity: the parser DOES reject genuinely unknown options — so the accept-tests above are a
        // real guard (not vacuously passing because the parser ignores everything).
        Assert.NotEmpty(ParseErrors("extract --build 1 --platform windows-x86_64 --totally-unknown-flag"));
    }
}

// --tools-content-root reads files from a local tree whose build is not the one being extracted,
// so the extract parser refuses it anywhere that could put those files into the corpus.
public class ExtractToolsContentRootGuardTest
{
    [Theory]
    [InlineData("--build", "1", "--platform", "windows-x86_64", "--commit", "--tools-content-root", "C:/cs2")]
    [InlineData("--build", "1", "--build", "2", "--platform", "windows-x86_64", "--tools-content-root", "C:/cs2")]
    [InlineData("--all", "--platform", "windows-x86_64", "--tools-content-root", "C:/cs2")]
    public void Tools_content_root_is_refused_with_commit_or_a_batch(params string[] args)
    {
        var fake = new FakeWalkerRunner(0, "", null);
        var code = Cs2SchemaTracker.Host.Cli.ExtractCommand.Run(args, () => fake);
        Assert.Equal(64, code);
        Assert.Equal(0, fake.Calls);
    }
}
