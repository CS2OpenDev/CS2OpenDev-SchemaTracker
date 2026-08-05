// ExtractCommand at-use input verification tests.
//
// Before launching the walker, RunExtract checks: IF a committed
// artifacts/<build>/<platform>/provenance.json exists for this (build, platform), hash each
// RESOLVED input binary the walker is about to read and compare to that provenance's
// inputs[].sha256. A binary modified/corrupted between acquisition and use is caught HERE
// BEFORE any walk. This covers BOTH the forward `extract --build` path and the
// batch / `extract --commit` re-walk path, which share RunExtract.
//
// Cases:
//   MATCH      : committed provenance inputs match the on-disk inputs -> walk proceeds.
//   MISMATCH   : a committed input hash != the on-disk bytes -> fail-loud exit 65 BEFORE the
//                walker runs (fake runner NEVER invoked, no artifacts written).
//   FRESH      : no committed provenance for the build -> at-use check SKIPPED, walk proceeds.
//   ZERO-HASH  : a legacy committed provenance with NO input hashes -> documented SKIP, walk
//                proceeds (pins the batch-re-walk dev-time fix so it can't regress to fail).
//
// Exercised through the FAKE IWalkerRunner seam (no built walker, no real CS2 binaries, no Steam).
// cwd is pinned to a throwaway temp dir (shared "cwd-mutating" collection) because both the binary
// resolution AND the committed-provenance lookup read paths relative to the process cwd.

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class ExtractAtUseVerificationTest
{
    private const string BuildId = "23669931";

    private static string? MatchingPlatform()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-x86_64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows-x86_64";
        return null;
    }

    // A counting fake walker: writes a full-enough canned WalkerOutput on a successful run so
    // EmitFullSet completes; records whether Run was ever invoked (the load-bearing at-use assertion
    // is that a MISMATCH never reaches the walker).
    private sealed class CountingFakeRunner : IWalkerRunner
    {
        public int Calls { get; private set; }

        public int Run(string binariesDir, string platform, string outPath, out string stderr)
        {
            Calls++;
            stderr = "";
            var payload = ExtractCommandTestShared.CannedWalkerOutput(platform);
            File.WriteAllBytes(outPath, payload.ToByteArray());
            return 0;
        }
    }

    // Pin cwd to a fresh temp dir holding cache/binaries/<build>/<platform>/ with two real,
    // inspectable input binaries (so the modules.json + provenance.json emitters succeed
    // on the happy paths). Returns the binaries dir's two (relativePath, bytes) inputs to the body so
    // a committed provenance can pin their exact hashes. Restores cwd + deletes the temp dir.
    private static void InPinnedWorkDir(
        string build, string platform,
        Action<string, IReadOnlyList<(string Rel, byte[] Bytes)>> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "atuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var binariesDir = Path.Combine(workDir, "cache", "binaries", build, platform);
        Directory.CreateDirectory(binariesDir);

        var soBytes = ExtractCommandTestShared.WithEmbeddedFdp(
            ExtractCommandTestShared.BuildElf(), ExtractCommandTestShared.BuildFdp("netmessages.proto"));
        var dllBytes = ExtractCommandTestShared.WithEmbeddedFdp(
            ExtractCommandTestShared.BuildPe(), ExtractCommandTestShared.BuildFdp("networkbasetypes.proto"));
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"), soBytes);
        File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"), dllBytes);

        var inputs = new[] { ("libserver.so", soBytes), ("client.dll", dllBytes) };

        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        { body(workDir, inputs); }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { }
        }
    }

    // Write a committed artifacts/<build>/<platform>/provenance.json with the given input rows.
    private static void WriteCommittedProvenance(
        string workDir, string build, string platform,
        IReadOnlyList<(string Path, string Sha256, int Size)> inputs)
    {
        var setDir = Path.Combine(workDir, "artifacts", build, platform);
        Directory.CreateDirectory(setDir);
        var inputsJson = string.Join(",", inputs.Select(i =>
            $$"""{ "path": "{{i.Path}}", "sha256": "{{i.Sha256}}", "fileSize": "{{i.Size}}", "mtimeUtc": "2026-06-10T12:00:00Z" }"""));
        var json = $$"""
        {
          "schemaVersion": "0.4.0",
          "buildId": "{{build}}",
          "platform": "{{platform}}",
          "steam": { "appId": 730, "steamBuildId": "{{build}}", "depots": [ { "depotId": 2347771, "manifestId": "5146470907583764090" } ] },
          "inputs": [ {{inputsJson}} ]
        }
        """;
        File.WriteAllText(Path.Combine(setDir, "provenance.json"), json);
    }

    private static int RunExtract(string build, string platform, string outDir, CountingFakeRunner runner)
        => ExtractCommand.RunExtract(
            build, platform, outDir, () => runner, eraResolver: null, gateFromResolver: false);

    [WindowsOnlyFact]
    public void C1_Matching_Committed_Provenance_Walk_Proceeds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, (workDir, inputs) =>
        {
            // Committed provenance pins the EXACT on-disk input hashes -> at-use verify passes.
            WriteCommittedProvenance(workDir, BuildId, platform,
                inputs.Select(i => (i.Rel, ExtractCommandTestShared.Sha256(i.Bytes), i.Bytes.Length)).ToList());

            var outDir = Path.Combine(workDir, "out");
            var runner = new CountingFakeRunner();
            var code = RunExtract(BuildId, platform, outDir, runner);

            Assert.Equal(0, code);
            Assert.Equal(1, runner.Calls);   // the walk ran (at-use verify passed).
            Assert.True(File.Exists(Path.Combine(outDir, "entity_schema.json")));
        });
    }

    [Fact]
    public void C2_Mismatched_Committed_Provenance_Fails_Loud_Before_Walker()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, (workDir, inputs) =>
        {
            // Tamper ONE recorded hash so the committed provenance no longer matches the on-disk bytes.
            var rows = inputs.Select(i => (i.Rel, ExtractCommandTestShared.Sha256(i.Bytes), i.Bytes.Length)).ToList();
            rows[0] = (rows[0].Rel, new string('a', 64), rows[0].Item3);   // valid-shaped but wrong SHA.
            WriteCommittedProvenance(workDir, BuildId, platform, rows);

            var outDir = Path.Combine(workDir, "out");
            var runner = new CountingFakeRunner();
            var code = RunExtract(BuildId, platform, outDir, runner);

            Assert.Equal(65, code); // fail-loud.
            Assert.Equal(0, runner.Calls);                // walker NEVER ran — caught BEFORE the walk.
            Assert.False(Directory.Exists(outDir), "no artifacts written on at-use mismatch");
        });
    }

    [WindowsOnlyFact]
    public void C3_Fresh_Extract_No_Committed_Provenance_Skips_Check_Walk_Proceeds()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, (workDir, _) =>
        {
            // No artifacts/<build>/<platform>/provenance.json exists — a FRESH extract that PRODUCES
            // the provenance. The at-use check is a documented SKIP; the walk proceeds.
            var outDir = Path.Combine(workDir, "out");
            var runner = new CountingFakeRunner();
            var code = RunExtract(BuildId, platform, outDir, runner);

            Assert.Equal(0, code);
            Assert.Equal(1, runner.Calls);
            Assert.True(File.Exists(Path.Combine(outDir, "provenance.json")));
        });
    }

    [WindowsOnlyFact]
    public void C4_Legacy_Provenance_With_Zero_Input_Hashes_Is_Documented_Skip()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(BuildId, platform, (workDir, _) =>
        {
            // A committed provenance carrying NO input hashes (legacy/minimal record) has nothing to
            // verify -> documented SKIP, NOT fail-loud. This pins the batch-re-walk dev-time fix.
            WriteCommittedProvenance(workDir, BuildId, platform,
                Array.Empty<(string, string, int)>());

            var outDir = Path.Combine(workDir, "out");
            var runner = new CountingFakeRunner();
            var code = RunExtract(BuildId, platform, outDir, runner);

            Assert.Equal(0, code);          // skip, not fail.
            Assert.Equal(1, runner.Calls);  // the walk proceeded.
        });
    }
}
