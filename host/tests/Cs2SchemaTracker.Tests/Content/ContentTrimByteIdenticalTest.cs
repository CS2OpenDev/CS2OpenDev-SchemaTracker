// Trimmed-VPK vs full-VPK byte-identical content JSON tests.
//
// Given a synthetic VPK fixture with representative entries (one .gameevents, items_game.txt, two
// csgo_<lang>.txt, a surfaceproperties file, propdata + collision, one overview), assert:
//   * all 7 content artifacts emitted from the TRIMMED VPK == those from the FULL VPK, byte-for-byte;
//   * VpkTrimWriter output re-parses cleanly and every entry's ReadEntryBytes CRC matches the source.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;

using Xunit;

namespace Cs2SchemaTracker.Tests.Content;

public class ContentTrimByteIdenticalTest
{
    private const string Build = "14446408";
    private const string Platform = "windows-x86_64";

    private static readonly string[] ExpectedArtifacts =
    {
        "game_modes.json", "gameevents.json", "item_definitions.json", "localization.json",
        "map_overviews.json", "prop_data.json", "surface_properties.json",
    };

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "content-trim-" + Guid.NewGuid().ToString("N"));
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
    public void All_Seven_Content_Artifacts_Are_Byte_Identical_Full_Vs_Trimmed()
    {
        var work = NewWorkDir();
        try
        {
            // FULL source VPK.
            var fullDirVpk = ContentVpkFixture.Write(Path.Combine(work, "full"), ContentSamples.StandardEntries());
            var full = VpkArchive.Open(fullDirVpk);

            // TRIMMED store copy over the SSOT required entries.
            var required = ContentPakSelector.EnumerateRequiredEntries(full);
            Assert.NotEmpty(required);
            var trimDirVpk = Path.Combine(work, "trim", "pak01_dir.vpk");
            VpkTrimWriter.Write(full, required, trimDirVpk);

            // Re-parse + per-entry CRC/byte match (the VpkTrimWriter round-trip half of).
            var trimmed = VpkArchive.Open(trimDirVpk);
            foreach (var re in required)
            {
                var te = trimmed.Find(re.FullPath);
                Assert.NotNull(te);
                Assert.Equal(re.Crc32, te!.Crc32);
                Assert.Equal(full.ReadEntryBytes(re), trimmed.ReadEntryBytes(te));
            }

            // Emit all 7 content artifacts from each and byte-compare.
            var outFull = Path.Combine(work, "emit-full");
            var outTrim = Path.Combine(work, "emit-trim");
            Directory.CreateDirectory(outFull);
            Directory.CreateDirectory(outTrim);

            ExtractCommand.EmitContentArtifactsFromVpk(fullDirVpk, Build, Platform, outFull);
            ExtractCommand.EmitContentArtifactsFromVpk(trimDirVpk, Build, Platform, outTrim);

            var fullFiles = Directory.EnumerateFiles(outFull).Select(Path.GetFileName)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            // All 7 content artifacts must have been produced (the fixture ships every family).
            Assert.Equal(ExpectedArtifacts.OrderBy(x => x, StringComparer.Ordinal), fullFiles);

            var trimFiles = Directory.EnumerateFiles(outTrim).Select(Path.GetFileName)
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(fullFiles, trimFiles);

            foreach (var name in fullFiles)
            {
                var a = File.ReadAllBytes(Path.Combine(outFull, name!));
                var b = File.ReadAllBytes(Path.Combine(outTrim, name!));
                Assert.True(a.AsSpan().SequenceEqual(b), $"{name} differs between full and trimmed VPK");
            }
        }
        finally
        {
            TryDelete(work);
        }
    }
}
