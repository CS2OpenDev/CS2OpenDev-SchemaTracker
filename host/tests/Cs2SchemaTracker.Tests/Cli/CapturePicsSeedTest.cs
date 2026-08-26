// CapturePicsCommand seed-from-preserved: a committed data/pics-captures/<build>.json writes the
// sidecar directly (no PICS fetch, so no network flake can lose a build whose capture is already
// safe), and a corrupt preserved file fails loud. Network-free: the seed path returns before any
// Steam session is created.

using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class CapturePicsSeedTest
{
    private const string Platform = "windows-x86_64";

    private const string Preserved = """
    {
      "appId": 730,
      "changeNumber": "37000001",
      "appInfoSha1": "AB12",
      "appInfoJson": "{\"depots\":{\"branches\":{\"public\":{\"buildid\":\"555\"}}}}"
    }
    """;

    private static (int Code, string Out, string Err) RunInWorkDir(string workDir, params string[] args)
    {
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        { return ConsoleCapture.Run(() => CapturePicsCommand.Run(args)); }
        finally { Directory.SetCurrentDirectory(prevCwd); }
    }

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "capture-seed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Preserved_Capture_Seeds_The_Sidecar_Without_A_Fetch()
    {
        var work = NewWorkDir();
        var capturesDir = Path.Combine(work, "data", "pics-captures");
        Directory.CreateDirectory(capturesDir);
        File.WriteAllText(Path.Combine(capturesDir, "555.json"), Preserved);

        var (code, output, err) = RunInWorkDir(work, "--build", "555", "--platform", Platform);

        Assert.Equal(0, code);
        Assert.Contains("seeded", err);
        var sidecar = Path.Combine(work, "cache", "pics", "555", Platform, "pics-appinfo-capture.json");
        Assert.True(File.Exists(sidecar));
        Assert.Contains(sidecar, output);
        Assert.Contains("37000001", File.ReadAllText(sidecar));
    }

    [Fact]
    public void Corrupt_Preserved_Capture_Fails_Loud()
    {
        var work = NewWorkDir();
        var capturesDir = Path.Combine(work, "data", "pics-captures");
        Directory.CreateDirectory(capturesDir);
        File.WriteAllText(Path.Combine(capturesDir, "555.json"), "not json");

        var (code, _, err) = RunInWorkDir(work, "--build", "555", "--platform", Platform);

        Assert.Equal(1, code);
        Assert.Contains("preserved", err);
    }
}
