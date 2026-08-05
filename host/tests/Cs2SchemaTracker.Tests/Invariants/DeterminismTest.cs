// Determinism (cross-artifact aggregate suite).
//
// The contract: two consecutive extractions against the same fixture binaries produce
// byte-identical output. The per-feature emitter tests already assert this individually;
// this suite asserts the determinism contract UNIFORMLY across every producible artifact,
// driven off the single ArtifactCases.All table, so the guarantee is expressed as one
// cross-artifact contract rather than scattered per-artifact checks. Adding a future artifact
// to ArtifactCases.All extends this coverage with no edits here.
//
// "diff -r is empty" (the CI form of) is the byte-for-byte file comparison done here in
// process: emit each artifact twice into two sibling dirs and assert the bytes are identical.

using Xunit;

namespace Cs2SchemaTracker.Tests.Invariants;

public class DeterminismTest
{
    public static TheoryData<string> AllArtifacts()
    {
        var data = new TheoryData<string>();
        foreach (var c in ArtifactCases.All)
        {
            data.Add(c.FileName);
        }
        return data;
    }

    private static ArtifactCase Case(string fileName)
        => ArtifactCases.All.Single(c => c.FileName == fileName);

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "determinism-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Theory]
    [MemberData(nameof(AllArtifacts))]
    public void Every_Artifact_Is_Byte_Identical_Across_Two_Runs(string fileName)
    {
        var c = Case(fileName);
        var workDir = NewWorkDir();
        try
        {
            // Two independent emissions of the same logical fixture into separate dirs — the
            // in-process equivalent of `diff -r` over two extraction runs.
            var dirA = Path.Combine(workDir, "run-a");
            var dirB = Path.Combine(workDir, "run-b");
            Directory.CreateDirectory(dirA);
            Directory.CreateDirectory(dirB);

            var outA = Path.Combine(dirA, c.FileName);
            var outB = Path.Combine(dirB, c.FileName);
            c.Emit(outA);
            c.Emit(outB);

            var bytesA = File.ReadAllBytes(outA);
            var bytesB = File.ReadAllBytes(outB);
            Assert.Equal(bytesA, bytesB);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // The determinism contract is byte-exact: no UTF-8 BOM, no CR (LF-only), and the bytes are
    // stable. Asserted uniformly so a future emitter cannot regress the encoding rules
    // and pass silently.
    [Theory]
    [MemberData(nameof(AllArtifacts))]
    public void Every_Artifact_Has_No_Bom_And_No_Cr(string fileName)
    {
        var c = Case(fileName);
        var workDir = NewWorkDir();
        try
        {
            var outPath = Path.Combine(workDir, c.FileName);
            c.Emit(outPath);

            var bytes = File.ReadAllBytes(outPath);
            Assert.False(
                bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                $"{c.FileName} must not carry a UTF-8 BOM");
            Assert.DoesNotContain((byte)'\r', bytes);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }
}
