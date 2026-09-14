// VpkTrimWriter unit tests.
//
// Repack a source VpkArchive + required-entry list into a trimmed v1 pak (dir + single chunk) and
// assert: (a) it re-parses via VpkArchive.Open, (b) every trimmed entry's ReadEntryBytes CRC matches
// AND equals the source bytes, (c) only the required entries survive, (d) the output is deterministic
// (e) empty entries fail loud.
//
// Plus the MULTI-SOURCE equivalence that the incremental content refresh rests on: the same entries
// drawn from TWO archives must produce a byte-identical pak01_dir.vpk + pak01_000.vpk to the same
// entries drawn from one. If that ever stops holding, a patched store copy stops being interchangeable
// with a fully re-fetched one and the whole cheap path is unsafe — so it is asserted on the finished
// files on disk, not just the in-memory buffers. Two sources claiming the SAME path is the one way to
// hand the writer an ambiguous set, and fails loud.

using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Vpk;

public class VpkTrimWriterTest
{
    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vpk-trim-" + Guid.NewGuid().ToString("N"));
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
    public void Trimmed_ReParses_And_Every_Entry_Crc_And_Bytes_Match_Source()
    {
        var work = NewWorkDir();
        try
        {
            // Source pak with a MIX of embedded + external entries.
            var srcCsgo = Path.Combine(work, "src");
            var srcDirVpk = ContentVpkFixture.Write(srcCsgo, ContentSamples.StandardEntries());
            var source = VpkArchive.Open(srcDirVpk);

            // Trim to the content-required subset (the SSOT the acquirer/repacker use).
            var required = ContentPakSelectorRequiredEntries(source);
            Assert.NotEmpty(required);

            var outCsgo = Path.Combine(work, "trim");
            Directory.CreateDirectory(outCsgo);
            var trimDirVpk = Path.Combine(outCsgo, "pak01_dir.vpk");
            VpkTrimWriter.Write(source, required, trimDirVpk);

            Assert.True(File.Exists(trimDirVpk));
            Assert.True(File.Exists(Path.Combine(outCsgo, "pak01_000.vpk")));

            // (a) re-parses; (c) exactly the required entries survive.
            var trimmed = VpkArchive.Open(trimDirVpk);
            var trimmedPaths = trimmed.Entries.Select(e => e.FullPath).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            var requiredPaths = required.Select(e => e.FullPath).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            Assert.Equal(requiredPaths, trimmedPaths);

            // (b) every entry's ReadEntryBytes CRC-verifies AND equals the source bytes.
            foreach (var re in required)
            {
                var te = trimmed.Find(re.FullPath);
                Assert.NotNull(te);
                Assert.Equal(re.Crc32, te!.Crc32);
                Assert.Equal(source.ReadEntryBytes(re), trimmed.ReadEntryBytes(te));
            }

            // Every trimmed entry is remapped to the single external chunk 0.
            Assert.All(trimmed.Entries, e => Assert.Equal((ushort)0, e.ArchiveIndex));
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Trimmed_Output_Is_Byte_Deterministic()
    {
        var work = NewWorkDir();
        try
        {
            var srcDirVpk = ContentVpkFixture.Write(Path.Combine(work, "src"), ContentSamples.StandardEntries());
            var source = VpkArchive.Open(srcDirVpk);
            var required = ContentPakSelectorRequiredEntries(source);

            var (dirA, chunkA) = VpkTrimWriter.Build(source, required);
            var (dirB, chunkB) = VpkTrimWriter.Build(source, required);

            Assert.Equal(dirA, dirB);
            Assert.Equal(chunkA, chunkB);
        }
        finally
        {
            TryDelete(work);
        }
    }

    [Fact]
    public void Empty_Entry_List_Fails_Loud()
    {
        var work = NewWorkDir();
        try
        {
            var srcDirVpk = ContentVpkFixture.Write(Path.Combine(work, "src"), ContentSamples.StandardEntries());
            var source = VpkArchive.Open(srcDirVpk);
            Assert.Throws<ArgumentException>(() =>
                VpkTrimWriter.Write(source, Array.Empty<VpkDirectoryEntry>(), Path.Combine(work, "x", "pak01_dir.vpk")));
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// THE equivalence the incremental ("patch") content refresh depends on: entries taken from TWO
    /// source archives repack to exactly the same bytes as the same entries taken from one. Both
    /// halves here are trims of the SAME original pak, which is precisely the real situation — a
    /// stored trim and a freshly-fetched pak under one content-depot GID hold identical bytes for
    /// every entry they share.
    /// </summary>
    [Fact]
    public void Entries_Split_Across_Two_Sources_Repack_Byte_Identically()
    {
        var work = NewWorkDir();
        try
        {
            var srcDirVpk = ContentVpkFixture.Write(Path.Combine(work, "src"), ContentSamples.StandardEntries());
            var source = VpkArchive.Open(srcDirVpk);
            var required = ContentPakSelectorRequiredEntries(source);
            Assert.True(required.Count >= 4, "need enough entries to split meaningfully");

            // "Archive A" holds everything EXCEPT weapons.vdata_c; "archive B" holds the whole set.
            // That mirrors a generation-1 store copy plus a freshly-fetched index.
            var withoutVdata = required
                .Where(e => !string.Equals(e.FullPath, "scripts/weapons.vdata_c", StringComparison.Ordinal))
                .ToList();
            Assert.Equal(required.Count - 1, withoutVdata.Count);

            var aDir = Path.Combine(work, "a");
            Directory.CreateDirectory(aDir);
            var aDirVpk = Path.Combine(aDir, "pak01_dir.vpk");
            VpkTrimWriter.Write(source, withoutVdata, aDirVpk);
            var archiveA = VpkArchive.Open(aDirVpk);

            // SINGLE source: every entry out of the original pak.
            var singleDir = Path.Combine(work, "single");
            Directory.CreateDirectory(singleDir);
            VpkTrimWriter.Write(source, required, Path.Combine(singleDir, "pak01_dir.vpk"));

            // SPLIT: the shared entries out of archive A (by A's OWN entry records, whose offsets
            // point into A's chunk), the one new entry out of the original pak.
            var split = new List<VpkTrimSource>();
            foreach (var e in withoutVdata)
            {
                var inA = archiveA.Find(e.FullPath);
                Assert.NotNull(inA);
                split.Add(new VpkTrimSource(archiveA, inA!));
            }
            split.Add(new VpkTrimSource(
                source, source.Find("scripts/weapons.vdata_c")
                    ?? throw new InvalidOperationException("fixture lost weapons.vdata_c")));

            var splitDir = Path.Combine(work, "split");
            Directory.CreateDirectory(splitDir);
            VpkTrimWriter.Write(split, Path.Combine(splitDir, "pak01_dir.vpk"));

            // The finished files on disk, not just the in-memory buffers.
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(singleDir, "pak01_dir.vpk")),
                File.ReadAllBytes(Path.Combine(splitDir, "pak01_dir.vpk")));
            Assert.Equal(
                File.ReadAllBytes(Path.Combine(singleDir, "pak01_000.vpk")),
                File.ReadAllBytes(Path.Combine(splitDir, "pak01_000.vpk")));
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// Shuffling the source list must not move a byte: the body layout is FullPath-Ordinal, never
    /// input order. A partition that emitted its re-used and fetched halves in any other order would
    /// otherwise produce a different (but still valid-looking) pak.
    /// </summary>
    [Fact]
    public void Source_Order_Does_Not_Affect_The_Output_Bytes()
    {
        var work = NewWorkDir();
        try
        {
            var srcDirVpk = ContentVpkFixture.Write(Path.Combine(work, "src"), ContentSamples.StandardEntries());
            var source = VpkArchive.Open(srcDirVpk);
            var required = ContentPakSelectorRequiredEntries(source);

            var forward = VpkTrimWriter.FromSingleSource(source, required);
            var reversed = forward.Reverse().ToList();

            var (dirA, chunkA) = VpkTrimWriter.Build(forward);
            var (dirB, chunkB) = VpkTrimWriter.Build(reversed);

            Assert.Equal(dirA, dirB);
            Assert.Equal(chunkA, chunkB);
        }
        finally
        {
            TryDelete(work);
        }
    }

    /// <summary>
    /// Two sources claiming the same logical path is the one genuinely ambiguous input multi-source
    /// repacking admits — the tree would carry both entries but only one body offset. Fail loud.
    /// </summary>
    [Fact]
    public void Same_Path_From_Two_Sources_Fails_Loud()
    {
        var work = NewWorkDir();
        try
        {
            var srcDirVpk = ContentVpkFixture.Write(Path.Combine(work, "src"), ContentSamples.StandardEntries());
            var source = VpkArchive.Open(srcDirVpk);
            var required = ContentPakSelectorRequiredEntries(source);

            var doubled = VpkTrimWriter.FromSingleSource(source, required).ToList();
            doubled.Add(doubled[0]);

            var ex = Assert.Throws<InvalidDataException>(() => VpkTrimWriter.Build(doubled));
            Assert.Contains("more than one source", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(work);
        }
    }

    // The SSOT the acquirer + repacker share.
    private static IReadOnlyList<VpkDirectoryEntry> ContentPakSelectorRequiredEntries(VpkArchive a)
        => Cs2SchemaTracker.Host.Steam.ContentPakSelector.EnumerateRequiredEntries(a);
}
