// ForwardCaptureRecorder merge path: a build already in the inventory gains the facts its row is
// missing when the OTHER platform's set is recorded (the commit job records each landed platform
// against the tip's inventory), and a fully-recorded build stays byte-untouched.
//
// Also covers the two facts a forward-capture row used to go without: change_number, read from the
// build-level pics-appinfo.json the same capture committed, and the derived
// "Build <id> on <d MMMM yyyy>" title. Both flow through the append AND merge paths, and neither
// ever overwrites a value a hand-curated row already carries.

using Cs2SchemaTracker.Host.Inventory;
using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Inventory;

public sealed class ForwardCaptureRecorderTest
{
    private const string Inventory = """
        {
          "app": { "app_id": 730 },
          "eras": [
            {
              "era": "cs2-2026-04-21",
              "kind": "compile-pin",
              "hl2sdkSha": "0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f"
            }
          ],
          "depots": [
            { "depot_id": 2347771, "role": "binary",  "platforms": ["windows-x86_64"] },
            { "depot_id": 2347773, "role": "binary",  "platforms": ["linux-x86_64"] },
            { "depot_id": 2347770, "role": "content", "platforms": ["windows-x86_64","linux-x86_64"] },
            { "depot_id": 2347779, "role": "tools",   "platforms": ["windows-x86_64"] }
          ],
          "builds": [
            {
              "build_id": 24000000,
              "predecessor": null,
              "date_utc": "2026-07-01T00:00:00Z",
              "era": "cs2-2026-04-21",
              "binaries": {
                "windows-x86_64": "222"
              },
              "tools": "777"
            }
          ]
        }

        """;

    private const string LinuxProvenance = """
        {
          "cs2Build": { "schemaRevision": "rev-1" },
          "steam": {
            "manifestCreatedUtc": "2026-07-01T00:00:00Z",
            "depots": [
              { "depotId": 2347773, "manifestId": "444" },
              { "depotId": 2347770, "manifestId": "999" }
            ]
          }
        }
        """;

    private static (string RepoRoot, string InventoryPath) NewRepo(string build = "24000000")
    {
        var repo = Path.Combine(Path.GetTempPath(), "fwd-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "data"));
        var inv = Path.Combine(repo, "data", "cs2-assets-inventory.json");
        File.WriteAllText(inv, Inventory.Replace("\r\n", "\n"));
        var setDir = Path.Combine(repo, "artifacts", build, "linux-x86_64");
        Directory.CreateDirectory(setDir);
        File.WriteAllText(Path.Combine(setDir, "provenance.json"), LinuxProvenance);
        return (repo, inv);
    }

    [Fact]
    public void Existing_Row_Gains_Missing_Linux_Gid_And_Content()
    {
        var (repo, inv) = NewRepo();
        var resolver = new EraWalkerResolver(repo);

        var outcome = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24000000", "linux-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.Merged, outcome);

        var after = File.ReadAllText(inv);
        Assert.Contains("\"linux-x86_64\": \"444\"", after);
        Assert.Contains("\"windows-x86_64\": \"222\"", after);
        Assert.Contains("\"content\": \"999\"", after);
        Assert.Contains("\"tools\": \"777\"", after);

        // A second record of the same platform is a no-op (idempotent).
        var again = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24000000", "linux-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.AlreadyPresent, again);
        Assert.Equal(after, File.ReadAllText(inv));
    }

    [Fact]
    public void Existing_Row_Without_Provenance_Is_AlreadyPresent()
    {
        var (repo, inv) = NewRepo();
        var resolver = new EraWalkerResolver(repo);
        var before = File.ReadAllText(inv);

        var outcome = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24000000", "windows-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.AlreadyPresent, outcome);
        Assert.Equal(before, File.ReadAllText(inv));
    }

    /// <summary>Write the build-level PICS capture the change_number is read back out of.</summary>
    private static void WritePicsCapture(string repo, string build, string changeNumber)
    {
        var dir = Path.Combine(repo, "artifacts", build);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "pics-appinfo.json"),
            $$"""
            {
              "schemaVersion": "0.10.0",
              "buildId": "{{build}}",
              "appId": 730,
              "changeNumber": "{{changeNumber}}",
              "capturedUtc": "2026-07-01T00:00:00Z",
              "appinfoSha1": "",
              "appinfoJson": "{}"
            }
            """);
    }

    [Fact]
    public void Merge_Backfills_ChangeNumber_From_Capture_And_Derived_Title()
    {
        var (repo, inv) = NewRepo();
        WritePicsCapture(repo, "24000000", "38420961");
        var resolver = new EraWalkerResolver(repo);

        var outcome = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24000000", "linux-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.Merged, outcome);

        var after = File.ReadAllText(inv);
        // change_number is the captured fact, unquoted (a JSON number, like the curated rows).
        Assert.Contains("\"change_number\": 38420961", after);
        // title is derived from the build's own manifest date, in the corpus's existing wording.
        Assert.Contains("\"title\": \"Build 24000000 on 1 July 2026\"", after);

        // Re-recording changes nothing: both facts are present now.
        var again = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24000000", "linux-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.AlreadyPresent, again);
        Assert.Equal(after, File.ReadAllText(inv));
    }

    [Fact]
    public void Merge_Never_Overwrites_A_Curated_Title_Or_ChangeNumber()
    {
        var (repo, inv) = NewRepo();
        WritePicsCapture(repo, "24000000", "38420961");
        // A hand-curated row already carrying the real SteamDB title + its own change number.
        var seeded = File.ReadAllText(inv).Replace(
            "\"date_utc\": \"2026-07-01T00:00:00Z\",",
            "\"date_utc\": \"2026-07-01T00:00:00Z\",\n      \"change_number\": 111,\n      "
                + "\"title\": \"Counter-Strike 2 Update\",",
            StringComparison.Ordinal);
        File.WriteAllText(inv, seeded);
        var resolver = new EraWalkerResolver(repo);

        ForwardCaptureRecorder.RecordIfNew(inv, repo, "24000000", "linux-x86_64", resolver);

        var after = File.ReadAllText(inv);
        Assert.Contains("\"title\": \"Counter-Strike 2 Update\"", after);
        Assert.Contains("\"change_number\": 111", after);
        Assert.DoesNotContain("38420961", after);
        Assert.DoesNotContain("Build 24000000 on", after);
    }

    [Fact]
    public void Append_Carries_ChangeNumber_And_Derived_Title()
    {
        // 24500000 is absent from the seed inventory -> the APPEND path.
        var (repo, inv) = NewRepo("24500000");
        WritePicsCapture(repo, "24500000", "38999999");
        var resolver = new EraWalkerResolver(repo);

        var outcome = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24500000", "linux-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.Appended, outcome);

        var after = File.ReadAllText(inv);
        Assert.Contains("\"build_id\": 24500000", after);
        Assert.Contains("\"change_number\": 38999999", after);
        Assert.Contains("\"title\": \"Build 24500000 on 1 July 2026\"", after);
    }

    [Fact]
    public void Append_Without_A_Capture_Records_No_ChangeNumber()
    {
        // No pics-appinfo.json (a historical / re-walked build): the row is honest about it rather
        // than carrying a zero, but the date-derived title still lands.
        var (repo, inv) = NewRepo("24500000");
        var resolver = new EraWalkerResolver(repo);

        var outcome = ForwardCaptureRecorder.RecordIfNew(inv, repo, "24500000", "linux-x86_64", resolver);
        Assert.Equal(ForwardCaptureRecorder.Outcome.Appended, outcome);

        var after = File.ReadAllText(inv);
        Assert.Contains("\"title\": \"Build 24500000 on 1 July 2026\"", after);
        Assert.DoesNotContain("\"change_number\"", after);
    }
}
