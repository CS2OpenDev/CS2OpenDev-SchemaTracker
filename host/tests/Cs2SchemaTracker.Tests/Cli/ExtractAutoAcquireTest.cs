// extract auto-acquire-on-missing-inputs seam.
//
// When the input binaries are absent, the PRODUCTION extract path (real per-era walker
// selection: a null runnerFactory + an eraResolver) acquires them in-process via the
// injectable acquire seam, then re-resolves. These tests exercise that seam with a FAKE
// acquire func (NEVER real Steam) over a pinned, empty work dir:
//   - the acquire func IS invoked (with the same build + platform) when binaries are absent;
// a non-zero acquire exit fails loud with that code and writes NO artifacts;
//   - a "successful" acquire that still leaves the dir unresolvable fails loud (exit 65);
//   - --no-acquire suppresses auto-acquire and restores the old fail-loud-with-guidance (65);
//   - the FAKE-RUNNER seam (runnerFactory != null) NEVER auto-acquires — the hard invariant
//     that keeps tests off real Steam.
//
// cwd is pinned to a throwaway temp dir with NO cache/binaries/<build>/<platform>, serialized
// through the shared "cwd-mutating" collection.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class ExtractAutoAcquireTest
{
    private const string BuildId = "55667788";
    private const string Platform = "windows-x86_64";

    // A walker runner that fails the test if ever invoked. None of these tests should reach the
    // walk: acquire either fails or leaves the dir unresolvable, and the suppressed paths abort first.
    private sealed class ThrowingWalkerRunner : IWalkerRunner
    {
        public int Run(string binariesDir, string platform, string outPath, out string stderr)
            => throw new Xunit.Sdk.XunitException("the walker must NOT run in an auto-acquire fail/suppress path.");
    }

    // Pin cwd to a fresh temp dir holding NO acquired binaries, so TryResolveBinariesDir's
    // conventional cache/binaries/<build>/<platform> probe (relative to cwd) misses.
    private static void InEmptyWorkDir(Action<string> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "acq-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        { body(workDir); }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public void Production_Path_Absent_Binaries_Invokes_Acquire_With_Same_Build_And_Platform()
    {
        InEmptyWorkDir(workDir =>
        {
            int calls = 0;
            string? gotBuild = null, gotPlatform = null;

            // Production seam: null runnerFactory + a (non-null) eraResolver. The fake acquire
            // returns a non-zero code, so RunExtract fails loud with it BEFORE constructing any
            // real walker (era resolution / WalkerProcessRunner are never reached).
            int code = ExtractCommand.RunExtract(
                BuildId, Platform, Path.Combine(workDir, "out"),
                runnerFactory: null, eraResolver: new EraWalkerResolver(workDir), gateFromResolver: false,
                noAcquire: false,
                acquire: (b, p) => { calls++; gotBuild = b; gotPlatform = p; return 42; });

            Assert.Equal(1, calls);                 // auto-acquire fired exactly once.
            Assert.Equal(BuildId, gotBuild);        // SAME build extract received.
            Assert.Equal(Platform, gotPlatform);    // SAME platform extract received.
            Assert.Equal(42, code);                 // acquire's exit code surfaced verbatim (fail loud).
            Assert.False(Directory.Exists(Path.Combine(workDir, "out")), "no artifacts on a failed acquire.");
        });
    }

    [Fact]
    public void Production_Path_Acquire_Succeeds_But_Still_Unresolvable_Fails_Loud()
    {
        InEmptyWorkDir(workDir =>
        {
            int calls = 0;
            // Acquire "succeeds" (exit 0) but produces nothing the resolver can find: the re-resolve
            // after acquire still misses -> fail loud at EX_DATAERR, never a silent walk.
            int code = ExtractCommand.RunExtract(
                BuildId, Platform, Path.Combine(workDir, "out"),
                runnerFactory: null, eraResolver: new EraWalkerResolver(workDir), gateFromResolver: false,
                noAcquire: false,
                acquire: (b, p) => { calls++; return 0; });

            Assert.Equal(1, calls);
            Assert.Equal(65, code);
            Assert.False(Directory.Exists(Path.Combine(workDir, "out")));
        });
    }

    [Fact]
    public void NoAcquire_Suppresses_AutoAcquire_And_Fails_Loud()
    {
        InEmptyWorkDir(workDir =>
        {
            int calls = 0;
            // --no-acquire: even on the production seam, an absent input dir restores the old
            // fail-loud-with-guidance behavior; the acquire func is NEVER called.
            int code = ExtractCommand.RunExtract(
                BuildId, Platform, Path.Combine(workDir, "out"),
                runnerFactory: null, eraResolver: new EraWalkerResolver(workDir), gateFromResolver: false,
                noAcquire: true,
                acquire: (b, p) => { calls++; return 0; });

            Assert.Equal(0, calls);                 // suppressed — never invoked.
            Assert.Equal(65, code);                 // EX_DATAERR fail-loud (old behavior).
            Assert.False(Directory.Exists(Path.Combine(workDir, "out")));
        });
    }

    [Fact]
    public void FakeRunner_Seam_Never_AutoAcquires()
    {
        InEmptyWorkDir(workDir =>
        {
            int calls = 0;
            // Fake-runner test seam (runnerFactory != null): auto-acquire is HARD-disabled regardless
            // of the injected acquire func, so tests never trigger a real Steam download. The absent
            // input dir simply fails loud (65); the acquire func is never called.
            int code = ExtractCommand.RunExtract(
                BuildId, Platform, Path.Combine(workDir, "out"),
                runnerFactory: () => new ThrowingWalkerRunner(), eraResolver: null, gateFromResolver: false,
                noAcquire: false,
                acquire: (b, p) => { calls++; return 0; });

            Assert.Equal(0, calls);                 // fake-runner seam never auto-acquires.
            Assert.Equal(65, code);
            Assert.False(Directory.Exists(Path.Combine(workDir, "out")));
        });
    }
}
