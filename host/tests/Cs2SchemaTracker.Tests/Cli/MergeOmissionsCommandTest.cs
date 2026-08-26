// MergeOmissionsCommand: one platform's carrier from a leg file merges into the build manifest
// without touching the other platform's carrier (the whole-file first-wins wedge this replaces).

using Cs2SchemaTracker.Host.Artifacts;
using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public sealed class MergeOmissionsCommandTest
{
    private const string Win = "windows-x86_64";
    private const string Linux = "linux-x86_64";

    private static readonly JsonParser Parser =
        new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    private static ContentArtifactOmission Omit(string artifact) => new()
    {
        Artifact = artifact,
        Reason = PlatformOmission.Types.Reason.ContentNotShippedThisEra,
        Notes = "x",
    };

    private static (int Code, string Out, string Err) RunCapture(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        { return (MergeOmissionsCommand.Run(args), stdout.ToString(), stderr.ToString()); }
        finally { Console.SetOut(prevOut); Console.SetError(prevErr); }
    }

    [Fact]
    public void Leg_Carrier_Merges_Without_Dropping_The_Other_Platform()
    {
        var work = Path.Combine(Path.GetTempPath(), "merge-om-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        var buildDir = Path.Combine(root, "555");
        Directory.CreateDirectory(buildDir);

        // The checkout's manifest already carries the LINUX carrier (landed earlier).
        BuildLevelOmissions.ReconcilePlatformContentOmissions(
            buildDir, "555", Linux, new[] { Omit("localization.json") });

        // The windows leg's uploaded file carries only ITS carrier (reconciled on a stale tree).
        var legDir = Path.Combine(work, "leg");
        Directory.CreateDirectory(legDir);
        BuildLevelOmissions.ReconcilePlatformContentOmissions(
            legDir, "555", Win, new[] { Omit("map_overviews.json") });

        var (code, _, _) = RunCapture(
            "--build", "555", "--platform", Win,
            "--from", Path.Combine(legDir, "omissions.json"), "--artifacts", root);
        Assert.Equal(0, code);

        var doc = Parser.Parse<Omissions>(File.ReadAllText(Path.Combine(buildDir, "omissions.json")));
        Assert.Equal(2, doc.Omissions_.Count);
        var linux = doc.Omissions_.Single(o => o.Platform == Linux);
        Assert.Equal("localization.json", Assert.Single(linux.ContentOmissions).Artifact);
        var win = doc.Omissions_.Single(o => o.Platform == Win);
        Assert.Equal("map_overviews.json", Assert.Single(win.ContentOmissions).Artifact);
    }

    [Fact]
    public void Absent_Leg_File_Is_An_Empty_Carrier_NoOp()
    {
        var work = Path.Combine(Path.GetTempPath(), "merge-om-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        var buildDir = Path.Combine(root, "555");
        Directory.CreateDirectory(buildDir);
        BuildLevelOmissions.ReconcilePlatformContentOmissions(
            buildDir, "555", Linux, new[] { Omit("localization.json") });
        var before = File.ReadAllText(Path.Combine(buildDir, "omissions.json"));

        var (code, _, _) = RunCapture(
            "--build", "555", "--platform", Win,
            "--from", Path.Combine(work, "leg", "omissions.json"), "--artifacts", root);

        Assert.Equal(0, code);
        Assert.Equal(before, File.ReadAllText(Path.Combine(buildDir, "omissions.json")));
    }
}
