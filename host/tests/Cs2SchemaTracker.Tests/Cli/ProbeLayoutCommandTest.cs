// ProbeLayoutCommand tests (synthetic; no real walker, no real binaries).
//
// Mirrors the ExtractCommand fake-runner pattern: a FAKE IWalkerLayoutProber (the single
// walker seam for probe-layout) drives the host orchestration without a built walker binary.
//
// Coverage:
//   1. Known layout: prober returns a signature + exit 0 -> host prints the signature to
//      stdout and exits 0.
// 2. unknown layout: prober returns exit 75 + signature on stdout AND stderr ->
//      host exits 75 and surfaces the signature to stderr (never guesses/swallows).
//   3. Fail-loud: missing --binaries dir -> non-zero, prober never constructed.
//   4. Fail-loud: --binaries omitted -> EX_USAGE (64).
//   5. Other non-zero walker exit (65) is propagated verbatim.
//   6. Exit 0 with NO signature is treated as a contract violation (70), not a known layout.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

/// <summary>
/// A fake <see cref="IWalkerLayoutProber"/>: returns a configured exit code + stdout + stderr
/// and records the binaries dir it was asked to probe.
/// </summary>
internal sealed class FakeWalkerLayoutProber : IWalkerLayoutProber
{
    private readonly WalkerLayoutProbeResult _result;

    public int Calls { get; private set; }
    public string? LastBinariesDir { get; private set; }

    public FakeWalkerLayoutProber(int exitCode, string stdout, string stderr)
        => _result = new WalkerLayoutProbeResult(exitCode, stdout, stderr);

    public WalkerLayoutProbeResult Probe(string binariesDir)
    {
        Calls++;
        LastBinariesDir = binariesDir;
        return _result;
    }
}

[Collection("console-capturing")]
public class ProbeLayoutCommandTest
{
    // A throwaway dir that exists so the --binaries existence check passes; deleted after.
    private static void InTempBinariesDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "probe-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        { body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }

    [Fact]
    public void KnownLayout_Prints_Signature_And_Exits_Zero()
    {
        InTempBinariesDir(dir =>
        {
            const string Sig = "schema-system-layout:abc123";
            var fake = new FakeWalkerLayoutProber(exitCode: 0, stdout: Sig + "\n", stderr: "");

            var stdout = new StringWriter();
            var prevOut = Console.Out;
            Console.SetOut(stdout);
            int code;
            try
            {
                code = ProbeLayoutCommand.Run(new[] { "--binaries", dir }, () => fake);
            }
            finally
            {
                Console.SetOut(prevOut);
            }

            Assert.Equal(0, code);
            Assert.Equal(1, fake.Calls);
            Assert.Equal(Path.GetFullPath(dir), fake.LastBinariesDir);
            // The signature is printed to stdout, single line, trimmed.
            Assert.Equal(Sig, stdout.ToString().Trim());
        });
    }

    [Fact]
    public void UnknownLayout_Exit75_Surfaces_Signature_To_Stderr_And_Propagates_Code()
    {
        InTempBinariesDir(dir =>
        {
            const string Sig = "schema-system-layout:0xDEADBEEF (unknown)";
            // The walker prints the signature on stdout AND stderr for an unknown layout (exit 75).
            var fake = new FakeWalkerLayoutProber(
                exitCode: 75, stdout: Sig + "\n",
                stderr: "cs2_schema_walker: unknown schema-system layout signature: " + Sig + "\n");

            var stderr = new StringWriter();
            var prevErr = Console.Error;
            Console.SetError(stderr);
            int code;
            try
            {
                code = ProbeLayoutCommand.Run(new[] { "--binaries", dir }, () => fake);
            }
            finally
            {
                Console.SetError(prevErr);
            }

            // propagate the exit code verbatim and surface the signature to stderr.
            Assert.Equal(75, code);
            Assert.Equal(1, fake.Calls);
            Assert.Contains(Sig, stderr.ToString());
            Assert.Contains("UNKNOWN", stderr.ToString());
        });
    }

    [Fact]
    public void OtherNonZero_Walker_Exit_Is_Propagated_Verbatim()
    {
        InTempBinariesDir(dir =>
        {
            // e.g. exit 65 = the walker could not load the modules (EX_DATAERR). Propagate it.
            var fake = new FakeWalkerLayoutProber(
                exitCode: 65, stdout: "", stderr: "cs2_schema_walker: probe-layout failed: cannot load module\n");

            var prevErr = Console.Error;
            Console.SetError(new StringWriter());
            int code;
            try
            { code = ProbeLayoutCommand.Run(new[] { "--binaries", dir }, () => fake); }
            finally { Console.SetError(prevErr); }

            Assert.Equal(65, code);
        });
    }

    [Fact]
    public void Exit0_With_No_Signature_Is_Contract_Violation()
    {
        InTempBinariesDir(dir =>
        {
            var fake = new FakeWalkerLayoutProber(exitCode: 0, stdout: "", stderr: "");

            var prevErr = Console.Error;
            Console.SetError(new StringWriter());
            int code;
            try
            { code = ProbeLayoutCommand.Run(new[] { "--binaries", dir }, () => fake); }
            finally { Console.SetError(prevErr); }

            Assert.Equal(70, code);
        });
    }

    [Fact]
    public void Missing_Binaries_Dir_Fails_Loud_Without_Constructing_Prober()
    {
        var missing = Path.Combine(Path.GetTempPath(), "probe-layout-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(missing));

        var factoryInvoked = false;

        var prevErr = Console.Error;
        Console.SetError(new StringWriter());
        int code;
        try
        {
            code = ProbeLayoutCommand.Run(
                new[] { "--binaries", missing },
                () => { factoryInvoked = true; return new FakeWalkerLayoutProber(0, "x", ""); });
        }
        finally { Console.SetError(prevErr); }

        Assert.NotEqual(0, code);
        Assert.False(factoryInvoked, "prober must not be constructed when the binaries dir is missing");
    }

    [Fact]
    public void Missing_Binaries_Arg_Is_Usage_Error()
    {
        var prevErr = Console.Error;
        Console.SetError(new StringWriter());
        int code;
        try
        { code = ProbeLayoutCommand.Run(Array.Empty<string>(), () => new FakeWalkerLayoutProber(0, "x", "")); }
        finally { Console.SetError(prevErr); }

        Assert.Equal(64, code);   // EX_USAGE
    }

    [Fact]
    public void Help_Flag_Exits_Zero()
    {
        var prevOut = Console.Out;
        Console.SetOut(new StringWriter());
        int code;
        var helpArgs = new[] { "--help" };
        try
        { code = ProbeLayoutCommand.Run(helpArgs, () => new FakeWalkerLayoutProber(0, "x", "")); }
        finally { Console.SetOut(prevOut); }

        Assert.Equal(0, code);
    }
}
