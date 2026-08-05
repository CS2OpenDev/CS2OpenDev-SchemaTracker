// Tests for the `content-store migrate` dev command.
//
// Covers: build+validate (default, no reclaim), guarded reclaim (--reclaim after a validated trim),
// and the guard — a trimmed store copy that does NOT reproduce byte-identical content JSON is
// REFUSED for reclaim (the co-located source is kept, exit non-zero).

using System.Globalization;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class ContentStoreMigrateTest
{
    private const string Build = "14446408";
    private const string Platform = "windows-x86_64";
    private const ulong Gid = 42424242UL;

    private static string NewStoreRootWithCoLocated(out string tupleDir, out string coLocatedDirVpk)
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "migrate-" + Guid.NewGuid().ToString("N"));
        tupleDir = Path.Combine(storeRoot, Build, Platform);
        Directory.CreateDirectory(tupleDir);
        coLocatedDirVpk = ContentVpkFixture.Write(
            Path.Combine(tupleDir, "game", "csgo"), ContentSamples.StandardEntries());
        new ManifestRecord(730, uint.Parse(Build, CultureInfo.InvariantCulture), new[]
        {
            new ManifestRecordDepot(ContentStore.ContentDepotId, Gid, "2026-06-10T00:00:00Z"),
        }).WriteToTupleDir(tupleDir);
        return storeRoot;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Build_And_Validate_Default_Does_Not_Reclaim()
    {
        var root = NewStoreRootWithCoLocated(out _, out var coLocated);
        try
        {
            int rc = ContentStoreCommand.Run(new[] { "migrate", "--binaries-root", root });
            Assert.Equal(0, rc);

            // Store copy built + validated.
            var contentRoot = Path.Combine(root, "_content");
            Assert.True(ContentStore.GidExists(contentRoot, Gid));
            // Default (no --reclaim): the co-located pak is PRESERVED.
            Assert.True(File.Exists(coLocated));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Reclaim_Deletes_CoLocated_After_Validated_Trim()
    {
        var root = NewStoreRootWithCoLocated(out var tupleDir, out var coLocated);
        try
        {
            int rc = ContentStoreCommand.Run(new[] { "migrate", "--binaries-root", root, "--reclaim" });
            Assert.Equal(0, rc);

            var contentRoot = Path.Combine(root, "_content");
            Assert.True(ContentStore.GidExists(contentRoot, Gid));
            // Co-located pak01_*.vpk reclaimed; the rest of the tuple dir (manifest-record.json) intact.
            Assert.False(File.Exists(coLocated));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(tupleDir, "game", "csgo"), "pak01_*.vpk"));
            Assert.True(File.Exists(Path.Combine(tupleDir, ManifestRecord.FileName)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void AutoReTrims_Incomplete_Legacy_Store_Without_Force()
    {
        // Reproduces the real-migrate failure: a partial _content/<gid> from the OLD python
        // gameevents-only backfill ALREADY exists, and its pak01_dir.vpk references an ORIGINAL
        // external chunk (pak01_154.vpk) that was never fetched. A plain migrate (no --force) must
        // DETECT the store is not a complete self-contained trim and RE-TRIM it from the co-located
        // source, then validate + succeed.
        var root = NewStoreRootWithCoLocated(out _, out var coLocated);
        try
        {
            var contentRoot = Path.Combine(root, "_content");

            // Build an INCOMPLETE legacy store: items_game routed to external chunk 154, which we then
            // delete — leaving pak01_dir.vpk referencing a missing chunk (exactly the observed break).
            var legacy = ContentSamples.StandardEntries().ToList();
            int itemsIdx = legacy.FindIndex(e => e.Name == "items_game");
            legacy[itemsIdx] = legacy[itemsIdx] with { ArchiveIndex = 154 };
            var storeDir = ContentStore.StoreDirForGid(contentRoot, Gid);
            ContentVpkFixture.Write(storeDir, legacy);
            File.Delete(Path.Combine(storeDir, "pak01_154.vpk"));

            // Precondition: the pre-existing store is detected as INCOMPLETE.
            Assert.False(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out _));

            // Plain migrate — NO --force. Must self-heal and validate.
            int rc = ContentStoreCommand.Run(new[] { "migrate", "--binaries-root", root });
            Assert.Equal(0, rc);

            // The store is now a COMPLETE self-contained trim, and the stray legacy chunk is gone.
            Assert.True(ContentStore.IsCompleteTrimmedStore(contentRoot, Gid, out _));
            Assert.False(File.Exists(Path.Combine(storeDir, "pak01_154.vpk")));
            Assert.True(File.Exists(Path.Combine(storeDir, "pak01_000.vpk")));
            // Default (no --reclaim): the co-located source is preserved.
            Assert.True(File.Exists(coLocated));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Refuses_To_Reclaim_When_Trim_Not_Byte_Identical()
    {
        var root = NewStoreRootWithCoLocated(out _, out var coLocated);
        try
        {
            // Pre-place a BOGUS store copy for the GID whose content JSON differs from the co-located
            // pak (different overview body). migrate (no --force) validates the EXISTING store copy and
            // must find it does NOT reproduce byte-identical content JSON.
            var bogus = ContentSamples.StandardEntries().ToList();
            int idx = bogus.FindIndex(e => e.Name == "de_dust2");
            bogus[idx] = bogus[idx] with
            {
                Body = System.Text.Encoding.UTF8.GetBytes("\"de_dust2\" { \"material\" \"overviews/CHANGED\" \"scale\" \"9.9\" }"),
            };
            var contentRoot = Path.Combine(root, "_content");
            ContentVpkFixture.Write(ContentStore.StoreDirForGid(contentRoot, Gid), bogus);

            int rc = ContentStoreCommand.Run(new[] { "migrate", "--binaries-root", root, "--reclaim" });
            Assert.Equal(65, rc);

            // Guard held: the co-located source is NOT deleted.
            Assert.True(File.Exists(coLocated));
        }
        finally
        {
            TryDelete(root);
        }
    }
}
