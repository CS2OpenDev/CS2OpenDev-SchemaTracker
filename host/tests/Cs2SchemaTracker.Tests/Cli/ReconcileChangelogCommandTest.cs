// ReconcileChangelogCommand: the commit job's stale-from_build repair. An in-sync changelog stays
// byte-untouched; a stale one is regenerated against the tree's true predecessor; the floor build
// requires (and tolerates) none.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cli;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public sealed class ReconcileChangelogCommandTest
{
    private const string Platform = "linux-x86_64";

    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static string NewRoot()
    {
        var work = Path.Combine(Path.GetTempPath(), "reconcile-cl-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void MakeSet(string root, string buildId, params string[] classNames)
        => Changelog.ChangelogTestSets.MakeSet(
            root, buildId, Platform, classNames.Select(c => ("client", c)).ToArray());

    private static (int Code, string Out, string Err) RunCapture(params string[] args)
        => ConsoleCapture.Run(() => ReconcileChangelogCommand.Run(args));

    [Fact]
    public void Stale_FromBuild_Is_Regenerated_Against_The_True_Predecessor()
    {
        var root = NewRoot();
        MakeSet(root, "1000", "C_BaseEntity");
        MakeSet(root, "1001", "C_BaseEntity");                       // landed after the leg checked out.
        MakeSet(root, "1002", "C_BaseEntity", "C_NewThing");

        // The leg emitted 1002's changelog against 1000 (its stale tree lacked 1001).
        var changelogPath = Path.Combine(root, "1002", Platform, ArtifactSet.ChangelogFileName);
        File.WriteAllText(changelogPath, """{ "fromBuild": "1000", "toBuild": "1002" }""");

        var (code, _, _) = RunCapture("--build", "1002", "--platform", Platform, "--artifacts", root);
        Assert.Equal(0, code);

        var changelog = Parser.Parse<Schemas.BuildChangelog>(File.ReadAllText(changelogPath));
        Assert.Equal("1001", changelog.FromBuild);
        Assert.Equal("1002", changelog.ToBuild);
        Assert.Contains(changelog.Families.Single(f => f.Family == "classes").Added, a => a.Contains("C_NewThing"));
    }

    [Fact]
    public void InSync_Changelog_Is_Left_Byte_Untouched()
    {
        var root = NewRoot();
        MakeSet(root, "1000", "C_BaseEntity");
        MakeSet(root, "1001", "C_BaseEntity", "C_NewThing");
        var changelogPath = Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName);
        var body = """{ "fromBuild": "1000", "toBuild": "1001" }""";
        File.WriteAllText(changelogPath, body);

        var (code, _, _) = RunCapture("--build", "1001", "--platform", Platform, "--artifacts", root);

        Assert.Equal(0, code);
        Assert.Equal(body, File.ReadAllText(changelogPath));
    }

    [Fact]
    public void Missing_Changelog_With_Predecessor_Is_Generated()
    {
        var root = NewRoot();
        MakeSet(root, "1000", "C_BaseEntity");
        MakeSet(root, "1001", "C_BaseEntity");

        var (code, _, _) = RunCapture("--build", "1001", "--platform", Platform, "--artifacts", root);

        Assert.Equal(0, code);
        var changelogPath = Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName);
        var changelog = Parser.Parse<Schemas.BuildChangelog>(File.ReadAllText(changelogPath));
        Assert.Equal("1000", changelog.FromBuild);
    }

    [Fact]
    public void Floor_Build_Needs_Nothing_And_Rejects_A_Present_Changelog()
    {
        var root = NewRoot();
        MakeSet(root, "1000", "C_BaseEntity");

        var (code, _, _) = RunCapture("--build", "1000", "--platform", Platform, "--artifacts", root);
        Assert.Equal(0, code);

        File.WriteAllText(
            Path.Combine(root, "1000", Platform, ArtifactSet.ChangelogFileName),
            """{ "fromBuild": "999", "toBuild": "1000" }""");
        var (code2, _, err) = RunCapture("--build", "1000", "--platform", Platform, "--artifacts", root);
        Assert.Equal(65, code2);
        Assert.Contains("floor", err);
    }
}
