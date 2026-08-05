// VpkTrimWriter unit tests.
//
// Repack a source VpkArchive + required-entry list into a trimmed v1 pak (dir + single chunk) and
// assert: (a) it re-parses via VpkArchive.Open, (b) every trimmed entry's ReadEntryBytes CRC matches
// AND equals the source bytes, (c) only the required entries survive, (d) the output is deterministic
// (e) empty entries fail loud.

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

    // The SSOT the acquirer + repacker share.
    private static IReadOnlyList<VpkDirectoryEntry> ContentPakSelectorRequiredEntries(VpkArchive a)
        => Cs2SchemaTracker.Host.Steam.ContentPakSelector.EnumerateRequiredEntries(a);
}
