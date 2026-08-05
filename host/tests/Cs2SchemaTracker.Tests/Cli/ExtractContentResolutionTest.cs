// ExtractCommand.TryFindGameEventsVpk resolution-order tests.
//
// Path 1: read the 2347770 GID from manifest-record.json and resolve the trimmed _content/<gid> copy
//         (takes precedence even when a co-located pak also exists).
// Path 2: FALLBACK to a co-located pak01_dir.vpk under the binaries dir (no store copy / no record).

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class ExtractContentResolutionTest
{
    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "extract-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Resolves_Trimmed_Store_Copy_Over_CoLocated_When_Record_Present()
    {
        var storeRoot = NewWorkDir();
        try
        {
            const ulong gid = 555UL;
            var tupleDir = Path.Combine(storeRoot, "14446408", "windows-x86_64");
            Directory.CreateDirectory(tupleDir);

            // A co-located pak ALSO exists — the store copy must still win.
            ContentVpkFixture.Write(Path.Combine(tupleDir, "game", "csgo"), ContentSamples.StandardEntries());

            new ManifestRecord(730, 14446408, new[]
            {
                new ManifestRecordDepot(ContentStore.ContentDepotId, gid, "2026-06-10T00:00:00Z"),
            }).WriteToTupleDir(tupleDir);

            var contentRoot = ContentStore.RootForTupleDir(tupleDir)!;
            ContentVpkFixture.Write(ContentStore.StoreDirForGid(contentRoot, gid), ContentSamples.StandardEntries());

            Assert.True(ExtractCommand.TryFindGameEventsVpk(tupleDir, out var resolved));
            Assert.Equal(ContentStore.ResolveDirVpk(contentRoot, gid), resolved);
        }
        finally
        {
            TryDelete(storeRoot);
        }
    }

    [Fact]
    public void Falls_Back_To_CoLocated_When_No_Store_Copy()
    {
        var storeRoot = NewWorkDir();
        try
        {
            var tupleDir = Path.Combine(storeRoot, "14446408", "linux-x86_64");
            var csgo = Path.Combine(tupleDir, "game", "csgo");
            var coLocated = ContentVpkFixture.Write(csgo, ContentSamples.StandardEntries());

            // No manifest-record.json / no _content store -> fall back to the co-located glob.
            Assert.True(ExtractCommand.TryFindGameEventsVpk(tupleDir, out var resolved));
            Assert.Equal(coLocated, resolved);
        }
        finally
        {
            TryDelete(storeRoot);
        }
    }

    [Fact]
    public void False_When_No_Vpk_Anywhere()
    {
        var dir = NewWorkDir();
        try
        {
            Assert.False(ExtractCommand.TryFindGameEventsVpk(dir, out var resolved));
            Assert.Equal("", resolved);
        }
        finally
        {
            TryDelete(dir);
        }
    }
}
