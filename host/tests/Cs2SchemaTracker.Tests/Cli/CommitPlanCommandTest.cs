// CommitPlanCommand tests — the host-owned git-commit planner.
//
// Builds a synthetic promoted (build, platform) set in a temp artifacts root and asserts the plan
// (completeness gate + provenance-derived message + staging paths). The completeness check must use
// the SAME ArtifactSet source of truth as verify-artifacts (so it names demo_messages.json — the file
// the old hand-maintained script list had dropped).

using System.Text.Json;

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public class CommitPlanCommandTest
{
    private const string Platform = "windows-x86_64";

    // A provenance.json that parses as the Provenance proto with a schemaRevision + two BINARY
    // depots (deliberately NOT the content depot 2347770, so content-depot gating does not apply and
    // the binary-only RequiredFiles set is complete on its own).
    private const string Provenance = """
    { "cs2Build": { "schemaRevision": "rev-123" }, "steam": { "depots": [ { "depotId": 2347771 }, { "depotId": 2347773 } ] } }
    """;

    /// <summary>Write a COMPLETE (build, platform) set under a fresh temp artifacts root; returns the root.</summary>
    private static string NewCompleteSet(string build)
    {
        var root = Path.Combine(Path.GetTempPath(), "commitplan-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, build, Platform);
        Directory.CreateDirectory(dir);
        foreach (var f in ArtifactSet.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(dir, f), f == ArtifactSet.ProvenanceFileName ? Provenance : "{}");
        }
        var protos = Path.Combine(dir, "protos");
        Directory.CreateDirectory(protos);
        File.WriteAllText(Path.Combine(protos, "entity_schema.proto"), "syntax = \"proto3\";");
        return root;
    }

    private static (int Code, string Out, string Err) RunCapture(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        { return (CommitPlanCommand.Run(args), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    [Fact]
    public void Complete_Set_Emits_Plan_With_Message_And_Staging()
    {
        var root = NewCompleteSet("555");
        var (code, output, _) = RunCapture("--build", "555", "--platform", Platform, "--artifacts", root);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        var plan = doc.RootElement;
        Assert.Equal("555", plan.GetProperty("build").GetString());
        Assert.Equal($"{root}/555/{Platform}", plan.GetProperty("stagePaths")[0].GetString());
        Assert.Equal("build/555", plan.GetProperty("tagName").GetString());
        // Message carries the provenance schemaRevision + joined depot ids.
        Assert.Contains("schemaRevision=rev-123 depots=2347771,2347773", plan.GetProperty("commitMessage").GetString());
    }

    [Fact]
    public void Incomplete_Set_Fails_Loud_65_And_Names_Missing_Core_File()
    {
        // A set missing everything but entity_schema.json — the gate must name demo_messages.json,
        // the file the old hand-maintained script list omitted (the drift this command removes).
        var root = Path.Combine(Path.GetTempPath(), "commitplan-inc-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "666", Platform);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "entity_schema.json"), "{}");

        var (code, _, err) = RunCapture("--build", "666", "--platform", Platform, "--artifacts", root);

        Assert.Equal(65, code);
        Assert.Contains("demo_messages.json", err);
    }

    [Fact]
    public void Missing_Build_Arg_Is_Usage_Error()
    {
        var (code, _, _) = RunCapture("--platform", Platform);
        Assert.Equal(64, code);
    }

    [Fact]
    public void Help_Flag_Exits_Zero()
    {
        var (code, _, _) = RunCapture("--help");
        Assert.Equal(0, code);
    }
}
