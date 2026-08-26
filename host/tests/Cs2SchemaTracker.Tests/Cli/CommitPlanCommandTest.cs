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
    public void Plan_Carries_Provenance_Fields_And_RemovePaths_For_Preserved_Capture()
    {
        // Repo-shaped layout: <repo>/artifacts/<build>/... with the preserved capture in the
        // sibling <repo>/data/pics-captures/, plus a staged build-level pics-appinfo.json.
        var repo = Path.Combine(Path.GetTempPath(), "commitplan-rp-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(repo, "artifacts");
        var dir = Path.Combine(root, "555", Platform);
        Directory.CreateDirectory(dir);
        foreach (var f in ArtifactSet.RequiredFiles)
        {
            File.WriteAllText(Path.Combine(dir, f), f == ArtifactSet.ProvenanceFileName ? Provenance : "{}");
        }
        Directory.CreateDirectory(Path.Combine(dir, "protos"));
        File.WriteAllText(Path.Combine(dir, "protos", "entity_schema.proto"), "syntax = \"proto3\";");
        File.WriteAllText(Path.Combine(root, "555", "pics-appinfo.json"), "{}");
        var preservedDir = Path.Combine(repo, "data", "pics-captures");
        Directory.CreateDirectory(preservedDir);
        File.WriteAllText(Path.Combine(preservedDir, "555.json"), "{}");

        var (code, output, _) = RunCapture("--build", "555", "--platform", Platform, "--artifacts", root);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        var plan = doc.RootElement;
        Assert.Equal("rev-123", plan.GetProperty("schemaRevision").GetString());
        Assert.Equal("2347771,2347773", plan.GetProperty("depots").GetString());
        var removePaths = plan.GetProperty("removePaths");
        Assert.Equal(1, removePaths.GetArrayLength());
        Assert.EndsWith("data/pics-captures/555.json", removePaths[0].GetString());
    }

    [Fact]
    public void RemovePaths_Empty_When_No_Preserved_Capture_Exists()
    {
        var root = NewCompleteSet("557");
        var (code, output, _) = RunCapture("--build", "557", "--platform", Platform, "--artifacts", root);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(0, doc.RootElement.GetProperty("removePaths").GetArrayLength());
    }

    [Fact]
    public void Stale_Or_Missing_Changelog_Refused_With_Predecessor_Committed()
    {
        // A committed predecessor (platform dir present) makes changelog.json REQUIRED with
        // from_build == that predecessor; the plan must refuse (65) until it is reconciled.
        var root = NewCompleteSet("556");
        Directory.CreateDirectory(Path.Combine(root, "444", Platform));

        var (code, _, err) = RunCapture("--build", "556", "--platform", Platform, "--artifacts", root);
        Assert.Equal(65, code);
        Assert.Contains("changelog", err);

        File.WriteAllText(
            Path.Combine(root, "556", Platform, ArtifactSet.ChangelogFileName),
            """{ "fromBuild": "444", "toBuild": "556" }""");
        var (code2, _, _) = RunCapture("--build", "556", "--platform", Platform, "--artifacts", root);
        Assert.Equal(0, code2);
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
