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
    {
        var dir = Path.Combine(root, buildId, Platform);
        Directory.CreateDirectory(dir);
        var schema = new Schemas.EntitySchema { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform };
        foreach (var c in classNames)
        {
            schema.Classes.Add(new Schemas.SchemaClass { Name = c, Module = "client" });
        }
        Write(schema, Path.Combine(dir, "entity_schema.json"));
        Write(new Schemas.ConVars { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "convars.json"));
        Write(new Schemas.Commands { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "commands.json"));
        Write(new Schemas.EngineConstants { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "engine_constants.json"));
    }

    private static void Write(IMessage msg, string path)
        => Cs2SchemaTracker.Host.Serialization.AtomicWrite.WriteCanonical(msg, path);

    private static (int Code, string Out, string Err) RunCapture(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        { return (ReconcileChangelogCommand.Run(args), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

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
