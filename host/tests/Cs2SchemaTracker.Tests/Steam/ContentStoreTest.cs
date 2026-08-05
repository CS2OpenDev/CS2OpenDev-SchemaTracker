// ContentStore GID-path resolver + SSOT parity tests.

using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentStoreTest
{
    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "content-store-" + Guid.NewGuid().ToString("N"));
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
    public void StoreDir_And_DirVpk_Compose_The_Gid_Layout()
    {
        var root = Path.Combine("D:", "cs2-binaries", "_content");
        var storeDir = ContentStore.StoreDirForGid(root, 12345UL);
        var dirVpk = ContentStore.ResolveDirVpk(root, 12345UL);

        Assert.EndsWith(Path.Combine("12345", "game", "csgo"), storeDir);
        Assert.Equal(Path.Combine(storeDir, "pak01_dir.vpk"), dirVpk);
    }

    [Fact]
    public void Core_Pak_Store_Layout_Sits_Beside_Csgo_Under_The_Same_Gid()
    {
        var root = Path.Combine("D:", "cs2-binaries", "_content");
        var csgoDir = ContentStore.StoreDirForGid(root, 999UL, ContentPak.Csgo);
        var coreDir = ContentStore.StoreDirForGid(root, 999UL, ContentPak.Core);

        Assert.EndsWith(Path.Combine("999", "game", "csgo"), csgoDir);
        Assert.EndsWith(Path.Combine("999", "game", "core"), coreDir);
        // Same GID root, different pak subtree — they never collide.
        Assert.NotEqual(csgoDir, coreDir);
        Assert.Equal(
            Path.Combine(coreDir, "pak01_dir.vpk"),
            ContentStore.ResolveDirVpk(root, 999UL, ContentPak.Core));
    }

    [Fact]
    public void RootForTupleDir_Walks_Two_Levels_To_Store_Root_Then_Content()
    {
        // <storeRoot>/<build>/<platform> -> <storeRoot>/_content
        var tuple = Path.Combine("D:", "cs2-binaries", "14446408", "windows-x86_64");
        var root = ContentStore.RootForTupleDir(tuple);
        Assert.Equal(Path.GetFullPath(Path.Combine("D:", "cs2-binaries", "_content")), root);
    }

    [Fact]
    public void RootForTupleDir_Null_When_Too_Shallow()
    {
        // A bare single-segment path has no store root two levels up.
        Assert.Null(ContentStore.RootForTupleDir(Path.GetPathRoot(Path.GetFullPath("."))!));
    }

    [Fact]
    public void TryResolve_Reads_Gid_From_Record_And_Finds_Store_Copy()
    {
        var storeRoot = NewWorkDir();
        try
        {
            const ulong gid = 987654321UL;
            var tupleDir = Path.Combine(storeRoot, "14446408", "windows-x86_64");
            Directory.CreateDirectory(tupleDir);

            // manifest-record.json carrying the 2347770 content depot GID.
            new ManifestRecord(730, 14446408, new[]
            {
                new ManifestRecordDepot(ContentStore.ContentDepotId, gid, "2026-06-10T00:00:00Z"),
                new ManifestRecordDepot(2347771, 111UL, "2026-06-10T00:00:00Z"),
            }).WriteToTupleDir(tupleDir);

            // No store copy yet -> resolver returns false (extract would fall back).
            Assert.False(ContentStore.TryResolveStoreDirVpk(tupleDir, out _));

            // Create the trimmed store copy at _content/<gid>/game/csgo.
            var contentRoot = ContentStore.RootForTupleDir(tupleDir)!;
            Assert.Equal(Path.Combine(storeRoot, "_content"), contentRoot);
            ContentVpkFixture.Write(ContentStore.StoreDirForGid(contentRoot, gid), ContentSamples.StandardEntries());

            Assert.True(ContentStore.TryResolveStoreDirVpk(tupleDir, out var resolved));
            Assert.Equal(ContentStore.ResolveDirVpk(contentRoot, gid), resolved);
            Assert.True(ContentStore.TryReadContentGid(tupleDir, out var readGid));
            Assert.Equal(gid, readGid);
        }
        finally
        {
            TryDelete(storeRoot);
        }
    }

    [Fact]
    public void TryResolve_False_When_No_Record()
    {
        var dir = NewWorkDir();
        try
        {
            Assert.False(ContentStore.TryResolveStoreDirVpk(dir, out _));
            Assert.False(ContentStore.TryReadContentGid(dir, out _));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Ssot_Parity_Every_Required_Entrys_Backing_File_Is_In_The_Fetch_Plan()
    {
        var work = NewWorkDir();
        try
        {
            var dirVpk = ContentVpkFixture.Write(Path.Combine(work, "csgo"), ContentSamples.StandardEntries());
            var archive = VpkArchive.Open(dirVpk);

            var required = ContentPakSelector.EnumerateRequiredEntries(archive);
            var plan = ContentPakSelector.SelectContentByteRanges(archive);

            Assert.NotEmpty(required);
            Assert.False(plan.IsEmpty);

            // The fetch plan's file set must be a SUPERSET of every backing chunk of every required
            // entry (the plan and the trimmed entries derive from the same SSOT) — plan ⊇ trimmed.
            foreach (var e in required)
            {
                if (e.ArchiveIndex == ContentPakSelector.EmbeddedArchiveIndex || e.EntryLength == 0)
                {
                    continue; // rides in the dir index / preload — no external chunk needed.
                }
                var backing = ContentPakSelector.ChunkFileRelPath(e.ArchiveIndex);
                Assert.Contains(backing, plan.AllFiles);
            }
        }
        finally
        {
            TryDelete(work);
        }
    }
}
