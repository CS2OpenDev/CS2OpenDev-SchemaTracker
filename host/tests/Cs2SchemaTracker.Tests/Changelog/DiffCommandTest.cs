// DiffCommand happy-path + fail-loud unit coverage.
//
// Builds a throwaway artifacts/<build>/<platform>/ tree with the five binary-derived source
// artifacts (entity_schema.json carrying classes+enums, convars.json, commands.json,
// engine_constants.json), drives DiffCommand.Run against it via the artifactsRootOverride seam,
// and asserts: (1) a happy-path diff writes a changelog.json that parses + carries the expected
// from_build/to_build and an added-class delta; (2) a missing set dir / unaccounted-for source
// file is a fail-loud non-zero exit with NO changelog written.

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cli;

using Google.Protobuf;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Changelog;

public sealed class DiffCommandTest
{
    private const string Platform = "linux-x86_64";

    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static readonly string[] OneClass = { "C_BaseEntity" };
    private static readonly string[] TwoClasses = { "C_BaseEntity", "C_NewThing" };
    private static readonly string[] ExpectedFamilyOrder =
        { "classes", "enums", "convars", "commands", "engine_constants" };

    // (module, name) pairs for the per-module name-reuse regression (CA1861: static readonly).
    private static readonly (string Module, string Name)[] FromIdentityClasses =
        { ("client.dll", "CEntityIdentity"), ("server.dll", "CEntityIdentity") };
    private static readonly (string Module, string Name)[] ToIdentityClasses =
        { ("server.dll", "CEntityIdentity"), ("engine2.dll", "CEntityIdentity") };
    private static readonly string[] ExpectedIdentityAdded = { "engine2.dll/CEntityIdentity" };
    private static readonly string[] ExpectedIdentityRemoved = { "client.dll/CEntityIdentity" };

    /// <summary>A fresh throwaway artifacts root (final path segment "artifacts").</summary>
    private static string NewArtifactsRoot()
    {
        var work = Path.Combine(Path.GetTempPath(), "diff-cmd-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void InRoot(Action<string> body)
    {
        var root = NewArtifactsRoot();
        var work = Directory.GetParent(root)!.FullName;
        try
        { body(root); }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }

    /// <summary>
    /// Write a minimal (build,platform) set with the four required source files. The classes list
    /// is supplied so the happy-path test can assert an added-class delta.
    /// </summary>
    private static void MakeSet(string root, string buildId, string[] classNames)
    {
        var dir = Path.Combine(root, buildId, Platform);
        Directory.CreateDirectory(dir);

        var schema = new Schemas.EntitySchema { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform };
        foreach (var c in classNames)
        {
            schema.Classes.Add(new Schemas.SchemaClass { Name = c, Module = "client" });
        }
        WriteCanonical(schema, Path.Combine(dir, "entity_schema.json"));
        WriteCanonical(new Schemas.ConVars { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "convars.json"));
        WriteCanonical(new Schemas.Commands { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "commands.json"));
        WriteCanonical(new Schemas.EngineConstants { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "engine_constants.json"));
    }

    private static void WriteCanonical(IMessage msg, string path)
        => Cs2SchemaTracker.Host.Serialization.AtomicWrite.WriteCanonical(msg, path);

    [Fact]
    public void HappyPath_WritesChangelog_WithExpectedFromToAndAddedClass()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", OneClass);
            MakeSet(root, "1001", TwoClasses);

            var code = DiffCommand.Run(
                new[] { "--from", "1000", "--to", "1001", "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);
            Assert.Equal(0, code);

            var changelogPath = Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName);
            Assert.True(File.Exists(changelogPath), "changelog.json should be written under the newer build");

            var changelog = Parser.Parse<Schemas.BuildChangelog>(File.ReadAllText(changelogPath));
            Assert.Equal("1000", changelog.FromBuild);
            Assert.Equal("1001", changelog.ToBuild);
            Assert.Equal(Platform, changelog.Platform);

            // All five families emitted, in the fixed declared order.
            Assert.Equal(ExpectedFamilyOrder, changelog.Families.Select(f => f.Family).ToArray());

            // Added-class key is the qualified "<module>/<name>" identity (changelog key).
            var classes = changelog.Families.Single(f => f.Family == "classes");
            Assert.Contains("client/C_NewThing", classes.Added);
            Assert.Empty(classes.Removed);
        });
    }

    [Fact]
    public void SameClassNameAcrossTwoModules_ProducesTwoDistinctEntries_NotFailLoud()
    {
        // Regression for: a class name reused across modules (the real CEntityIdentity case,
        // which appears in client.dll, engine2.dll, server.dll) previously fail-looded on the
        // name-only key ("duplicate record name 'CEntityIdentity'"). With the composite
        // "<module>/<name>" key the two records are distinct entries — and an add/remove of one
        // module's copy does NOT touch the other module's copy.
        InRoot(root =>
        {
            // FROM: CEntityIdentity exists in BOTH modules.
            MakeSetWithClasses(root, "1000", FromIdentityClasses);
            // TO: client.dll copy removed; engine2.dll copy added; server.dll copy unchanged.
            MakeSetWithClasses(root, "1001", ToIdentityClasses);

            var code = DiffCommand.Run(
                new[] { "--from", "1000", "--to", "1001", "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);
            Assert.Equal(0, code);   // composite key means no duplicate-key fail-loud.

            var changelogPath = Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName);
            var changelog = Parser.Parse<Schemas.BuildChangelog>(File.ReadAllText(changelogPath));
            var classes = changelog.Families.Single(f => f.Family == "classes");

            // engine2.dll copy added; client.dll copy removed; server.dll copy untouched.
            Assert.Equal(ExpectedIdentityAdded, classes.Added.ToArray());
            Assert.Equal(ExpectedIdentityRemoved, classes.Removed.ToArray());
            Assert.DoesNotContain(classes.Added, k => k == "server.dll/CEntityIdentity");
            Assert.DoesNotContain(classes.Removed, k => k == "server.dll/CEntityIdentity");
        });
    }

    /// <summary>
    /// Write a (build,platform) set whose entity_schema.json classes carry explicit (module, name)
    /// pairs, plus the other three empty required source files.
    /// </summary>
    private static void MakeSetWithClasses(string root, string buildId, (string Module, string Name)[] classes)
    {
        var dir = Path.Combine(root, buildId, Platform);
        Directory.CreateDirectory(dir);

        var schema = new Schemas.EntitySchema { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform };
        foreach (var (module, name) in classes)
        {
            schema.Classes.Add(new Schemas.SchemaClass { Name = name, Module = module });
        }
        WriteCanonical(schema, Path.Combine(dir, "entity_schema.json"));
        WriteCanonical(new Schemas.ConVars { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "convars.json"));
        WriteCanonical(new Schemas.Commands { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "commands.json"));
        WriteCanonical(new Schemas.EngineConstants { SchemaVersion = "0.4.0", BuildId = buildId, Platform = Platform },
            Path.Combine(dir, "engine_constants.json"));
    }

    [Fact]
    public void MissingFromSetDir_FailsLoud_NoChangelogWritten()
    {
        InRoot(root =>
        {
            MakeSet(root, "1001", OneClass);   // only the --to side exists

            var code = DiffCommand.Run(
                new[] { "--from", "1000", "--to", "1001", "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);
            Assert.NotEqual(0, code);

            Assert.False(File.Exists(Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName)),
                "no changelog must be written when an input set is missing");
        });
    }

    [Fact]
    public void MissingRequiredSourceFile_Unaccounted_FailsLoud()
    {
        InRoot(root =>
        {
            MakeSet(root, "1000", OneClass);
            MakeSet(root, "1001", OneClass);
            // Remove a required source artifact from the --from side with no omissions accounting.
            File.Delete(Path.Combine(root, "1000", Platform, "convars.json"));

            var code = DiffCommand.Run(
                new[] { "--from", "1000", "--to", "1001", "--platform", Platform, "--artifacts", root },
                artifactsRootOverride: root);
            Assert.Equal(65, code);
            Assert.False(File.Exists(Path.Combine(root, "1001", Platform, ArtifactSet.ChangelogFileName)));
        });
    }

    [Fact]
    public void SameFromAndTo_UsageError()
    {
        var code = DiffCommand.Run(
            new[] { "--from", "1000", "--to", "1000", "--platform", Platform },
            artifactsRootOverride: null);
        Assert.Equal(64, code);
    }

    [Fact]
    public void MissingFrom_UsageError()
    {
        var code = DiffCommand.Run(
            new[] { "--to", "1001", "--platform", Platform },
            artifactsRootOverride: null);
        Assert.Equal(64, code);
    }
}
