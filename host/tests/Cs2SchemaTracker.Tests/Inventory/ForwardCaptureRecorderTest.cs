// ForwardCaptureRecorder merge path: a build already in the inventory gains the facts its row is
// missing when the OTHER platform's set is recorded (the commit job records each landed platform
// against the tip's inventory), and a fully-recorded build stays byte-untouched.

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
              "era": "cs2-2026-04-21"
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

    private static (string RepoRoot, string InventoryPath) NewRepo()
    {
        var repo = Path.Combine(Path.GetTempPath(), "fwd-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(repo, "data"));
        var inv = Path.Combine(repo, "data", "cs2-assets-inventory.json");
        File.WriteAllText(inv, Inventory.Replace("\r\n", "\n"));
        var setDir = Path.Combine(repo, "artifacts", "24000000", "linux-x86_64");
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
}
