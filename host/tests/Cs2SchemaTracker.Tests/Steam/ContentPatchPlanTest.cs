// Incremental ("patch") content-refresh tests — the reuse-vs-fetch partition and, above all, the
// proof that patching produces the SAME store copy as a full re-fetch.
//
// THE SAVING these pin down: adding a resource to ContentPakSelector.EnumerateRequiredEntries marks
// every stored trim stale, but the bytes in those trims are still exactly right for their content
// GID — the GID is the content identity. Re-fetching the whole required set to add one small
// resource re-downloads the localization tables (93.9% of the required bytes, measured across 13
// builds spanning every era) that are already on disk in the very trim about to be overwritten.
// ContentPatchPlan partitions the FRESH required set against the store copy so only what is
// genuinely missing is fetched.
//
// THE RISK these pin down: a patched trim that is not byte-identical to the full one would quietly
// fork the corpus. So the central case here writes a store copy the cheap way and a reference copy
// the expensive way and compares the finished files byte-for-byte — including the
// trim-generation.json that rides the same atomic move.
//
// And the fail-loud: under ONE content GID an entry cannot legitimately disagree on CRC / length /
// preload, so a store copy that does is corrupt or mis-keyed and the partition throws naming both
// CRCs rather than silently re-fetching (which hides it) or silently trusting the store (which bakes
// it in). An ABSENT or UNPARSEABLE store copy is a different thing and degrades to a full fetch.

using System.Globalization;
using System.Text;

using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentPatchPlanTest
{
    private const ulong Gid = 4473487723012660428UL;

    /// <summary>The two external chunk files the split fixture below spreads its bodies across.</summary>
    private static readonly string[] BothChunkFiles =
        ["game/csgo/pak01_000.vpk", "game/csgo/pak01_001.vpk"];

    /// <summary>The ONE chunk file a patched refresh of that fixture still has to touch.</summary>
    private static readonly string[] VdataChunkFileOnly = ["game/csgo/pak01_001.vpk"];

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "content-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try
        { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    /// <summary>The "freshly fetched" pak — the full required surface, current generation.</summary>
    private static VpkArchive FreshPak(string work, IReadOnlyList<ContentVpkFixture.Entry>? entries = null)
        => VpkArchive.Open(ContentVpkFixture.Write(
            Path.Combine(work, "staging"), entries ?? ContentSamples.StandardEntries()));

    /// <summary>
    /// The required set MINUS the compiled weapons.vdata_c — exactly what EnumerateRequiredEntries
    /// selected at generation 1, i.e. how every real stale store copy came to be.
    /// </summary>
    private static List<VpkDirectoryEntry> GenerationOneRequired(VpkArchive source)
        => ContentPakSelector.EnumerateRequiredEntries(source)
            .Where(e => !string.Equals(
                e.FullPath, ContentPakSelector.WeaponVDataRelPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// Lay down a generation-1 store copy for <see cref="Gid"/> through the PRODUCTION writer, so it
    /// is a genuine proper-but-old trim rather than a hand-rolled approximation.
    /// </summary>
    private static string WriteStaleStore(string root, VpkArchive source, ContentPak? pak = null)
    {
        var storeDirVpk = ContentStore.ResolveDirVpk(root, Gid, pak);
        VpkTrimWriter.Write(source, GenerationOneRequired(source), storeDirVpk);
        ContentStore.WriteTrimGenerationMarker(
            root, Gid, ContentPakSelector.RequiredSetGeneration - 1, pak);
        return storeDirVpk;
    }

    [Fact]
    public void No_Store_Copy_Yields_A_Full_Plan()
    {
        var work = NewWorkDir();
        try
        {
            var fresh = FreshPak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);

            var plan = ContentPatchPlan.Create(
                fresh, required, Path.Combine(work, "_content"), Gid, ContentPak.Csgo);

            Assert.False(plan.IsPatch);
            Assert.Equal(0, plan.ReusedCount);
            Assert.Equal(required.Count, plan.Fetch.Count);
            Assert.Equal(required.Count, plan.TrimSources.Count);
            Assert.Contains("no store copy", plan.Reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Stale_Store_Reuses_Everything_Except_The_Newly_Required_Entry()
    {
        var work = NewWorkDir();
        try
        {
            var root = Path.Combine(work, "_content");
            var fresh = FreshPak(work);
            WriteStaleStore(root, fresh);

            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);
            var plan = ContentPatchPlan.Create(fresh, required, root, Gid, ContentPak.Csgo);

            Assert.True(plan.IsPatch);
            Assert.Equal(required.Count - 1, plan.ReusedCount);
            Assert.Equal(required.Count, plan.TrimSources.Count);
            Assert.Equal(
                new[] { ContentPakSelector.WeaponVDataRelPath },
                plan.Fetch.Select(e => e.FullPath).ToArray());
            Assert.True(plan.ReusedBytes > 0);
            Assert.Contains("PATCH", plan.Describe(), StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// A store copy that is ALREADY complete has nothing left to fetch — the degenerate end of the
    /// partition, and what lets the acquire skip Phase B outright.
    /// </summary>
    [Fact]
    public void Complete_Store_Copy_Leaves_Nothing_To_Fetch()
    {
        var work = NewWorkDir();
        try
        {
            var root = Path.Combine(work, "_content");
            var fresh = FreshPak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);
            VpkTrimWriter.Write(fresh, required, ContentStore.ResolveDirVpk(root, Gid));

            var plan = ContentPatchPlan.Create(fresh, required, root, Gid, ContentPak.Csgo);

            Assert.True(plan.IsPatch);
            Assert.Equal(required.Count, plan.ReusedCount);
            Assert.Empty(plan.Fetch);
            Assert.Equal(0, plan.FetchBytes);

            // ... and therefore no external body range left to fetch, which is the acquirer's
            // Phase-B-skip condition.
            var fetchPlan = ContentPakSelector.BuildByteRangePlan(ContentPak.Csgo, plan.Fetch);
            Assert.Empty(fetchPlan.ChunkRanges);
            Assert.False(fetchPlan.IsEmpty);   // the directory index is still a whole-file entry
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// THE correctness requirement: the store copy a PATCH writes is byte-for-byte the store copy a
    /// full re-fetch would have written — same pak01_dir.vpk, same pak01_000.vpk, same
    /// trim-generation.json. Everything else here is optimization; this is the safety argument.
    /// </summary>
    [Fact]
    public void Patched_Store_Copy_Is_Byte_Identical_To_A_Full_Retrim()
    {
        var work = NewWorkDir();
        try
        {
            var fresh = FreshPak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);

            // (1) The expensive path: trim the whole required set straight from the fresh pak.
            var fullRoot = Path.Combine(work, "_content-full");
            var fullAction = ContentStore.EnsureTrimmedStore(
                fresh, required, fullRoot, Gid, force: false, out _);
            Assert.Equal(ContentStore.StoreEnsureAction.Built, fullAction);

            // (2) The cheap path: a stale generation-1 store copy, patched.
            var patchRoot = Path.Combine(work, "_content-patch");
            WriteStaleStore(patchRoot, fresh);
            var plan = ContentPatchPlan.Create(fresh, required, patchRoot, Gid, ContentPak.Csgo);
            Assert.True(plan.IsPatch);
            Assert.Single(plan.Fetch);
            var patchAction = ContentStore.EnsureTrimmedStore(
                plan.TrimSources, patchRoot, Gid, force: false, out _);
            Assert.Equal(ContentStore.StoreEnsureAction.ReTrimmedIncomplete, patchAction);

            foreach (var name in new[] { "pak01_dir.vpk", "pak01_000.vpk", ContentStore.TrimMarkerFileName })
            {
                Assert.Equal(
                    File.ReadAllBytes(Path.Combine(ContentStore.StoreDirForGid(fullRoot, Gid), name)),
                    File.ReadAllBytes(Path.Combine(ContentStore.StoreDirForGid(patchRoot, Gid), name)));
            }

            // The patched copy is also a complete, current-generation trim in its own right.
            Assert.True(ContentStore.IsCompleteTrimmedStore(patchRoot, Gid, out var reason), reason);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// The fetch half narrows the byte-range plan: with the newly-required resource living in its own
    /// external chunk, a patched refresh fetches THAT chunk's range and not the chunk backing the
    /// entries the store copy already holds.
    /// </summary>
    [Fact]
    public void Fetch_Half_Narrows_The_Byte_Range_Plan()
    {
        var work = NewWorkDir();
        try
        {
            // Put weapons.vdata_c in its OWN external chunk (1); gameevents + items stay in chunk 0.
            var entries = ContentSamples.StandardEntries()
                .Select(e => string.Equals(e.Name, "weapons", StringComparison.Ordinal)
                    ? e with { ArchiveIndex = 1 }
                    : e)
                .ToList();

            var root = Path.Combine(work, "_content");
            var fresh = FreshPak(work, entries);
            WriteStaleStore(root, fresh);

            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);
            var plan = ContentPatchPlan.Create(fresh, required, root, Gid, ContentPak.Csgo);

            var fullFetch = ContentPakSelector.BuildByteRangePlan(ContentPak.Csgo, required);
            var patchFetch = ContentPakSelector.BuildByteRangePlan(ContentPak.Csgo, plan.Fetch);

            Assert.Equal(
                BothChunkFiles,
                fullFetch.ChunkRanges.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
            Assert.Equal(VdataChunkFileOnly, patchFetch.ChunkRanges.Keys.ToArray());

            long fullBytes = fullFetch.ChunkRanges.Values.Sum(rs => rs.Sum(r => r.Length));
            long patchBytes = patchFetch.ChunkRanges.Values.Sum(rs => rs.Sum(r => r.Length));
            Assert.True(patchBytes < fullBytes, $"patch {patchBytes} should be under full {fullBytes}");
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// One content GID is one set of bytes. A store copy whose entry disagrees with the fresh index
    /// is corrupt or was written under the wrong GID, and re-fetching it silently would hide a fault
    /// affecting every build that shares that GID. Throw, naming the entry and BOTH CRCs.
    /// </summary>
    [Fact]
    public void Store_Entry_Disagreeing_With_The_Fresh_Index_Fails_Loud()
    {
        var work = NewWorkDir();
        try
        {
            var root = Path.Combine(work, "_content");

            // A store copy built from DIFFERENT items_game bytes — same path, different CRC.
            var wrongEntries = ContentSamples.StandardEntries()
                .Select(e => string.Equals(e.Name, "items_game", StringComparison.Ordinal)
                    ? e with { Body = Encoding.UTF8.GetBytes("a different items_game body") }
                    : e)
                .ToList();
            var wrongPak = VpkArchive.Open(
                ContentVpkFixture.Write(Path.Combine(work, "wrong"), wrongEntries));
            WriteStaleStore(root, wrongPak);

            var fresh = FreshPak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);

            var ex = Assert.Throws<InvalidDataException>(
                () => ContentPatchPlan.Create(fresh, required, root, Gid, ContentPak.Csgo));

            Assert.Contains(ContentPakSelector.ItemsGameRelPath, ex.Message, StringComparison.Ordinal);
            Assert.Contains(Gid.ToString(CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);

            var storedEntry = VpkArchive.Open(ContentStore.ResolveDirVpk(root, Gid))
                .Find(ContentPakSelector.ItemsGameRelPath);
            var freshEntry = fresh.Find(ContentPakSelector.ItemsGameRelPath);
            Assert.NotNull(storedEntry);
            Assert.NotNull(freshEntry);
            Assert.Contains($"0x{storedEntry!.Crc32:X8}", ex.Message, StringComparison.Ordinal);
            Assert.Contains($"0x{freshEntry!.Crc32:X8}", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// An UNPARSEABLE store copy is a different thing: there is simply nothing to re-use, so the plan
    /// degrades to a full fetch with the reason recorded — matching ContentStore's existing
    /// fault-safe probe semantics, where an unreadable store is the signal to rebuild it.
    /// </summary>
    [Fact]
    public void Unparseable_Store_Copy_Degrades_To_A_Full_Plan()
    {
        var work = NewWorkDir();
        try
        {
            var root = Path.Combine(work, "_content");
            var storeDirVpk = ContentStore.ResolveDirVpk(root, Gid);
            Directory.CreateDirectory(Path.GetDirectoryName(storeDirVpk)!);
            File.WriteAllBytes(storeDirVpk, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 });

            var fresh = FreshPak(work);
            var required = ContentPakSelector.EnumerateRequiredEntries(fresh);
            var plan = ContentPatchPlan.Create(fresh, required, root, Gid, ContentPak.Csgo);

            Assert.False(plan.IsPatch);
            Assert.Equal(required.Count, plan.Fetch.Count);
            Assert.Contains("unreadable", plan.Reason, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }
}
