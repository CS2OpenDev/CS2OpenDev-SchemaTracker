// content acquire NON-DESTRUCTIVE merge into the binaries dir.
//
// The two-phase content acquire stages the pak01 files in a throwaway dir and
// then merges them into --out, which may already hold the per-platform binaries
// acquired earlier. This test proves the merge ADDS/overwrites only the staged
// pak01 files and PRESERVES every pre-existing file (the binaries must survive),
// laying the VPK at game/csgo/pak01_dir.vpk where extract's recursive search
// finds it.

using Cs2SchemaTracker.Host.Steam;

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

public class ContentMergeTest
{
    private static AcquireResult StageFiles(string stageDir, params (string Rel, string Body)[] files)
    {
        var infos = new List<AcquiredFileInfo>();
        foreach (var (rel, body) in files)
        {
            var p = Path.Combine(stageDir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, body);
            infos.Add(new AcquiredFileInfo(rel, Sha256OfNothing, body.Length, null));
        }
        return new AcquireResult(stageDir, 23669931u, Array.Empty<AcquiredDepotInfo>(), infos, 0);
    }

    private const string Sha256OfNothing = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void Merge_preserves_existing_binaries_and_adds_pak01()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2-merge-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(root, "out");
        var stageDir = Path.Combine(root, "stage");
        try
        {
            // Pre-existing binaries already in outDir/game/csgo (must survive).
            var binDir = Path.Combine(outDir, "game", "csgo", "bin", "win64");
            Directory.CreateDirectory(binDir);
            var existingDll = Path.Combine(binDir, "client.dll");
            File.WriteAllText(existingDll, "ORIGINAL-BINARY-BYTES");

            var staged = StageFiles(stageDir,
                ("game/csgo/pak01_dir.vpk", "DIRVPK"),
                ("game/csgo/pak01_007.vpk", "CHUNK7"));

            var result = SteamAnonymousAcquirer.MergeStagedFiles(staged, outDir, TextWriter.Null);

            // Existing binary untouched.
            Assert.True(File.Exists(existingDll));
            Assert.Equal("ORIGINAL-BINARY-BYTES", File.ReadAllText(existingDll));

            // pak01 files landed at the path extract searches.
            var dirVpk = Path.Combine(outDir, "game", "csgo", "pak01_dir.vpk");
            var chunk = Path.Combine(outDir, "game", "csgo", "pak01_007.vpk");
            Assert.True(File.Exists(dirVpk));
            Assert.True(File.Exists(chunk));
            Assert.Equal("DIRVPK", File.ReadAllText(dirVpk));

            Assert.Equal(outDir, result.OutDir);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public void Merge_overwrites_a_stale_pak01_in_place()
    {
        var root = Path.Combine(Path.GetTempPath(), "cs2-merge-" + Guid.NewGuid().ToString("N"));
        var outDir = Path.Combine(root, "out");
        var stageDir = Path.Combine(root, "stage");
        try
        {
            var csgo = Path.Combine(outDir, "game", "csgo");
            Directory.CreateDirectory(csgo);
            File.WriteAllText(Path.Combine(csgo, "pak01_dir.vpk"), "STALE");

            var staged = StageFiles(stageDir, ("game/csgo/pak01_dir.vpk", "FRESH"));
            SteamAnonymousAcquirer.MergeStagedFiles(staged, outDir, TextWriter.Null);

            Assert.Equal("FRESH", File.ReadAllText(Path.Combine(csgo, "pak01_dir.vpk")));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string dir)
    {
        if (Directory.Exists(dir))
        {
            try
            { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
