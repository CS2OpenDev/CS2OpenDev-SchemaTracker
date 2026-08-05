// ContentBackfillPlanner tests — the pure enumeration/dedup/missing-core planning (no Steam).

using System.Globalization;
using System.Text;

using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentBackfillPlannerTest
{
    private static string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "backfill-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    // Write a committed tuple dir <root>/<build>/<platform> with a manifest-record carrying `gid` as
    // the 2347770 content depot GID.
    private static void WriteBuild(string root, string build, string platform, ulong gid)
    {
        var tupleDir = Path.Combine(root, build, platform);
        Directory.CreateDirectory(tupleDir);
        new ManifestRecord(730, uint.Parse(build, CultureInfo.InvariantCulture), new[]
        {
            new ManifestRecordDepot(ContentStore.ContentDepotId, gid, "2026-06-10T00:00:00Z"),
            new ManifestRecordDepot(2347771, 111UL, "2026-06-10T00:00:00Z"),
        }).WriteToTupleDir(tupleDir);
    }

    [Fact]
    public void Plan_Dedups_By_Gid_And_Counts_Builds()
    {
        var root = NewRoot();
        try
        {
            // Three builds: two share GID 100 (win+lin of the same content), one has GID 200.
            WriteBuild(root, "1000", "windows-x86_64", 100UL);
            WriteBuild(root, "1000", "linux-x86_64", 100UL);
            WriteBuild(root, "2000", "windows-x86_64", 200UL);

            var targets = ContentBackfillPlanner.Plan(root, ContentPak.Core);

            // Two unique GIDs, neither has a core pak yet ⇒ both are targets, Ordinal by GID.
            Assert.Equal(2, targets.Count);
            Assert.Equal(100UL, targets[0].ContentGid);
            Assert.Equal(2, targets[0].BuildCount);   // GID 100 covers win+lin of build 1000
            Assert.Equal(200UL, targets[1].ContentGid);
            Assert.Equal(1, targets[1].BuildCount);

            // Representative for GID 100 is the Ordinal-first tuple dir (…/1000/linux-x86_64 sorts
            // before …/1000/windows-x86_64).
            Assert.EndsWith(Path.Combine("1000", "linux-x86_64"), targets[0].RepresentativeTupleDir);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Plan_Skips_Gids_Whose_Core_Pak_Is_Already_Stored()
    {
        var root = NewRoot();
        try
        {
            WriteBuild(root, "1000", "windows-x86_64", 100UL);
            WriteBuild(root, "2000", "windows-x86_64", 200UL);
            // GID 100 already has a (complete) core pak — copy a REAL trimmed core store in.
            WriteRealCoreStore(root, 100UL);

            var targets = ContentBackfillPlanner.Plan(root, ContentPak.Core);

            // Only GID 200 remains.
            Assert.Single(targets);
            Assert.Equal(200UL, targets[0].ContentGid);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Plan_Ignores_Sidecar_Dirs_And_Builds_Without_Content_Gid()
    {
        var root = NewRoot();
        try
        {
            WriteBuild(root, "1000", "windows-x86_64", 100UL);
            // A _content sidecar and a build with a record but NO content depot must be ignored.
            Directory.CreateDirectory(Path.Combine(root, ContentStore.ContentDirName, "999", "game", "csgo"));
            var noContentDir = Path.Combine(root, "3000", "windows-x86_64");
            Directory.CreateDirectory(noContentDir);
            new ManifestRecord(730, 3000, new[]
            {
                new ManifestRecordDepot(2347771, 111UL, "2026-06-10T00:00:00Z"),
            }).WriteToTupleDir(noContentDir);

            var targets = ContentBackfillPlanner.Plan(root, ContentPak.Core);

            Assert.Single(targets);
            Assert.Equal(100UL, targets[0].ContentGid);
        }
        finally
        {
            TryDelete(root);
        }
    }

    // Write a REAL complete trimmed core store for `gid`: a self-contained pak01_dir.vpk (core.gameevents
    // body embedded, no external chunk) so ContentStore.IsCompleteTrimmedStore returns true.
    private static void WriteRealCoreStore(string root, ulong gid)
    {
        var storeDir = ContentStore.StoreDirForGid(
            Path.Combine(root, ContentStore.ContentDirName), gid, ContentPak.Core);
        Directory.CreateDirectory(storeDir);
        const string coreEvents =
            "\"GameEvents\"\n{\n\t\"player_connect\"\n\t{\n\t\t\"name\" \"string\"\n\t}\n}\n";
        var entries = new[]
        {
            new ContentVpkFixture.Entry("resource", "gameevents", "core",
                Encoding.UTF8.GetBytes(coreEvents), ArchiveIndex: ContentVpkFixture.Embedded),
        };
        // ContentVpkFixture.Write lays down pak01_dir.vpk (+ any external chunks) under the given dir.
        ContentVpkFixture.Write(storeDir, entries);
    }
}
