// ValidatePreservedCaptures: a preserved data/pics-captures/<build>.json is a PENDING state while
// the build has no committed pics-appinfo.json, and an ORPHAN (violation) once it does.

using Cs2SchemaTracker.Host.Artifacts;

using Xunit;

namespace Cs2SchemaTracker.Tests.Artifacts;

public sealed class PreservedCaptureOrphanTest
{
    [Fact]
    public void Orphaned_And_Stranded_Captures_Are_Flagged_Pending_Is_Not()
    {
        var repo = Path.Combine(Path.GetTempPath(), "pics-orphan-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(repo, "artifacts");
        Directory.CreateDirectory(Path.Combine(root, "100"));
        File.WriteAllText(Path.Combine(root, "100", "pics-appinfo.json"), "{}");
        // Build 300: a committed set WITHOUT pics-appinfo.json (nothing will ever promote).
        Directory.CreateDirectory(Path.Combine(root, "300", "windows-x86_64"));
        File.WriteAllText(Path.Combine(root, "300", "windows-x86_64", "entity_schema.json"), "{}");
        var captures = Path.Combine(repo, "data", "pics-captures");
        Directory.CreateDirectory(captures);
        File.WriteAllText(Path.Combine(captures, "100.json"), "{}");   // orphaned: pics committed.
        File.WriteAllText(Path.Combine(captures, "200.json"), "{}");   // pending: no build 200 set.
        File.WriteAllText(Path.Combine(captures, "300.json"), "{}");   // stranded: set, no pics.

        var issues = new ArtifactSetValidator(root).ValidatePreservedCaptures(captures);

        Assert.Equal(2, issues.Count);
        Assert.Contains("ORPHANED", issues.Single(v => v.BuildId == "100").Message);
        Assert.Contains("STRANDED", issues.Single(v => v.BuildId == "300").Message);
    }

    [Fact]
    public void Absent_Captures_Dir_Is_Dormant()
    {
        var repo = Path.Combine(Path.GetTempPath(), "pics-orphan-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(repo, "artifacts");
        Directory.CreateDirectory(root);

        var issues = new ArtifactSetValidator(root)
            .ValidatePreservedCaptures(Path.Combine(repo, "data", "pics-captures"));

        Assert.Empty(issues);
    }
}
