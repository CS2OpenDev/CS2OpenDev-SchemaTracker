// tests for the build-level omissions.json content-omission writer.
//

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Artifacts;

public sealed class BuildLevelOmissionsTest
{
    private const string Win = "windows-x86_64";
    private const string Linux = "linux-x86_64";

    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static string NewBuildDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omissions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static ContentArtifactOmission Omit(string artifact, string notes = "x") => new()
    {
        Artifact = artifact,
        Reason = PlatformOmission.Types.Reason.ContentNotShippedThisEra,
        Notes = notes,
    };

    private static Omissions Read(string buildDir)
        => Parser.Parse<Omissions>(File.ReadAllText(Path.Combine(buildDir, "omissions.json")));

    [Fact]
    public void CleanBuild_NoOmissions_NoFileExists_WritesNothing()
    {
        var dir = NewBuildDir();
        try
        {
            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Win, Array.Empty<ContentArtifactOmission>());
            Assert.False(File.Exists(Path.Combine(dir, "omissions.json")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CleanBuild_ExistingEmptyManifest_IsLeftUntouched()
    {
        var dir = NewBuildDir();
        try
        {
            var path = Path.Combine(dir, "omissions.json");
            var body = """{"buildId":"100","omissions":[],"schemaVersion":"0.2.0"}""";
            File.WriteAllText(path, body);

            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Win, Array.Empty<ContentArtifactOmission>());

            // No content-carrier change ⇒ no rewrite (byte-identical, schemaVersion not bumped).
            Assert.Equal(body, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Records_ContentOmissions_SortedByArtifact()
    {
        var dir = NewBuildDir();
        try
        {
            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Win, new[] { Omit("surface_properties.json"), Omit("localization.json") });

            var doc = Read(dir);
            var carrier = Assert.Single(doc.Omissions_);
            Assert.Equal(Win, carrier.Platform);
            Assert.Equal(PlatformOmission.Types.Reason.Unspecified, carrier.Reason);
            Assert.Equal(
                "localization.json,surface_properties.json",
                string.Join(",", carrier.ContentOmissions.Select(c => c.Artifact)));
            Assert.All(carrier.ContentOmissions,
                c => Assert.Equal(PlatformOmission.Types.Reason.ContentNotShippedThisEra, c.Reason));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Reconcile_PreservesOtherPlatform_AndClearsStaleCarrier()
    {
        var dir = NewBuildDir();
        try
        {
            // Linux already carries a content omission; Windows is about to record one.
            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Linux, new[] { Omit("prop_data.json") });
            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Win, new[] { Omit("localization.json") });

            var doc = Read(dir);
            Assert.Equal(2, doc.Omissions_.Count);
            Assert.Contains(doc.Omissions_, o => o.Platform == Linux);
            Assert.Contains(doc.Omissions_, o => o.Platform == Win);

            // Re-run Windows with NO omissions ⇒ its stale carrier is cleared; Linux preserved.
            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Win, Array.Empty<ContentArtifactOmission>());
            doc = Read(dir);
            var only = Assert.Single(doc.Omissions_);
            Assert.Equal(Linux, only.Platform);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WholesalePlatformOmission_IsNotTouched()
    {
        var dir = NewBuildDir();
        try
        {
            var path = Path.Combine(dir, "omissions.json");
            File.WriteAllText(path,
                """{"buildId":"100","omissions":[{"platform":"linux-x86_64","reason":"DEPOT_UNAVAILABLE"}]}""");

            BuildLevelOmissions.ReconcilePlatformContentOmissions(
                dir, "100", Win, new[] { Omit("localization.json") });

            var doc = Read(dir);
            Assert.Equal(2, doc.Omissions_.Count);
            var linux = Assert.Single(doc.Omissions_, o => o.Platform == Linux);
            Assert.Equal(PlatformOmission.Types.Reason.DepotUnavailable, linux.Reason);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void CorruptExistingManifest_FailsLoud()
    {
        var dir = NewBuildDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "omissions.json"), "{ not json");
            Assert.Throws<InvalidDataException>(() =>
                BuildLevelOmissions.ReconcilePlatformContentOmissions(
                    dir, "100", Win, new[] { Omit("localization.json") }));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
