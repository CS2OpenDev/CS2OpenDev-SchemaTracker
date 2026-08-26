// EmitPicsCommand: build-level pics-appinfo.json from an explicit capture file, with the
// embedded-head-buildid guard (a capture describing another build is refused) and provenance-framed
// captured_utc (never wall clock).

using System.Text.Json;

using Cs2SchemaTracker.Host.Cli;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("console-capturing")]
public sealed class EmitPicsCommandTest
{
    private const string Platform = "windows-x86_64";

    private const string Provenance = """
    { "steam": { "manifestCreatedUtc": "2026-07-01T00:00:00Z", "depots": [] } }
    """;

    private static string CaptureJson(string embeddedBuild) => """
    {
      "appId": 730,
      "changeNumber": "37000001",
      "appInfoSha1": "AB12",
      "appInfoJson": "{\"depots\":{\"branches\":{\"public\":{\"buildid\":\"EMBEDDED\"}}}}"
    }
    """.Replace("EMBEDDED", embeddedBuild);

    private static (string Root, string CapturePath) NewFixture(string build, string embeddedBuild)
    {
        var work = Path.Combine(Path.GetTempPath(), "emit-pics-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        var dir = Path.Combine(root, build, Platform);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "provenance.json"), Provenance);
        var capturePath = Path.Combine(work, "pics-appinfo-capture.json");
        File.WriteAllText(capturePath, CaptureJson(embeddedBuild));
        return (root, capturePath);
    }

    private static (int Code, string Out, string Err) RunCapture(params string[] args)
        => ConsoleCapture.Run(() => EmitPicsCommand.Run(args));

    [Fact]
    public void Matching_Capture_Emits_With_Provenance_Framed_CapturedUtc()
    {
        var (root, capturePath) = NewFixture("555", "555");

        var (code, _, _) = RunCapture("--build", "555", "--capture", capturePath, "--artifacts", root);
        Assert.Equal(0, code);

        var outPath = Path.Combine(root, "555", "pics-appinfo.json");
        Assert.True(File.Exists(outPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
        Assert.Equal("555", doc.RootElement.GetProperty("buildId").GetString());
        Assert.Equal("37000001", doc.RootElement.GetProperty("changeNumber").GetString());
        Assert.Equal("2026-07-01T00:00:00Z", doc.RootElement.GetProperty("capturedUtc").GetString());

        // Re-emitting the same capture is byte-identical (deterministic).
        var first = File.ReadAllText(outPath);
        var (code2, _, _) = RunCapture("--build", "555", "--capture", capturePath, "--artifacts", root);
        Assert.Equal(0, code2);
        Assert.Equal(first, File.ReadAllText(outPath));
    }

    [Fact]
    public void NonObject_AppInfo_Body_Is_Refused_Not_Crashed()
    {
        // A valid-JSON body that is not the expected object tree must land in the documented
        // exit-65 refusal (TryGetEmbeddedBuildId returns null), never an unhandled throw.
        var work = Path.Combine(Path.GetTempPath(), "emit-pics-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(Path.Combine(root, "555", Platform));
        File.WriteAllText(Path.Combine(root, "555", Platform, "provenance.json"), Provenance);
        var capturePath = Path.Combine(work, "pics-appinfo-capture.json");
        File.WriteAllText(capturePath, """
        { "appId": 730, "changeNumber": "1", "appInfoSha1": "", "appInfoJson": "[]" }
        """);

        var (code, _, err) = RunCapture("--build", "555", "--capture", capturePath, "--artifacts", root);

        Assert.Equal(65, code);
        Assert.Contains("mis-associated", err);
    }

    [Fact]
    public void Empty_ManifestTime_Falls_Through_To_The_Other_Platform()
    {
        var work = Path.Combine(Path.GetTempPath(), "emit-pics-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(Path.Combine(root, "555", "windows-x86_64"));
        Directory.CreateDirectory(Path.Combine(root, "555", "linux-x86_64"));
        // The preferred platform's provenance carries no steam block; the other has the time.
        File.WriteAllText(Path.Combine(root, "555", "windows-x86_64", "provenance.json"), "{}");
        File.WriteAllText(Path.Combine(root, "555", "linux-x86_64", "provenance.json"), Provenance);
        var capturePath = Path.Combine(work, "pics-appinfo-capture.json");
        File.WriteAllText(capturePath, CaptureJson("555"));

        var (code, _, _) = RunCapture("--build", "555", "--capture", capturePath, "--artifacts", root);

        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "555", "pics-appinfo.json")));
        Assert.Equal("2026-07-01T00:00:00Z", doc.RootElement.GetProperty("capturedUtc").GetString());
    }

    [Fact]
    public void Mismatched_Embedded_BuildId_Is_Refused()
    {
        var (root, capturePath) = NewFixture("666", "555");

        var (code, _, err) = RunCapture("--build", "666", "--capture", capturePath, "--artifacts", root);

        Assert.Equal(65, code);
        Assert.Contains("mis-associated", err);
        Assert.False(File.Exists(Path.Combine(root, "666", "pics-appinfo.json")));
    }

    [Fact]
    public void No_Promoted_Set_Is_Refused()
    {
        var work = Path.Combine(Path.GetTempPath(), "emit-pics-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(work, "artifacts");
        Directory.CreateDirectory(root);
        var capturePath = Path.Combine(work, "pics-appinfo-capture.json");
        File.WriteAllText(capturePath, CaptureJson("555"));

        var (code, _, err) = RunCapture("--build", "555", "--capture", capturePath, "--artifacts", root);

        Assert.Equal(65, code);
        Assert.Contains("no promoted platform set", err);
    }
}
