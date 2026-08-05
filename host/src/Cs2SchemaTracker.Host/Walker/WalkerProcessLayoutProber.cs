// real walker layout-probe runner.
//
// Sibling of WalkerProcessRunner (the `walk` launcher). Launches the C++ walker binary
// and runs its `probe-layout` subcommand against a binaries dir, capturing stdout (the
// layout signature) + stderr + exit code. The walker binary path is RESOLVED, never
// hardcoded absolute, via the SAME WalkerProcessRunner.ResolveWalkerBinary precedence
// (CS2_WALKER_BIN / appsettings WalkerBin override -> explicit per-era path -> walker/build
// default), so probe-layout selects the same walker the default extract path would.
//
// fail-loud: this prober does NOT interpret the walker's exit code — it returns it
// verbatim with captured stdout/stderr, and ProbeLayoutCommand decides. A non-zero exit
// (including 75 = unknown layout) is surfaced, not swallowed. A walker binary that
// cannot be found throws (FileNotFoundException) so the command fails loud before any work.

using System.Diagnostics;
using System.Text;

namespace Cs2SchemaTracker.Host.Walker;

/// <summary>
/// <see cref="IWalkerLayoutProber"/> backed by launching the real walker executable's
/// <c>probe-layout</c> subcommand. The binary path is resolved by
/// <see cref="WalkerProcessRunner.ResolveWalkerBinary"/>; it is never a hardcoded absolute path.
/// </summary>
internal sealed class WalkerProcessLayoutProber : IWalkerLayoutProber
{
    private readonly string? _explicitBinaryPath;

    /// <summary>
    /// Default prober: resolves the walker binary from the override env/appsettings or the
    /// <c>walker/build</c> default (mirrors the single-era WalkerProcessRunner ctor).
    /// </summary>
    public WalkerProcessLayoutProber()
    {
    }

    /// <summary>
    /// Per-era prober: launch the explicitly-resolved <paramref name="explicitBinaryPath"/>.
    /// The CS2_WALKER_BIN / appsettings override STILL wins over it (same precedence as the
    /// walk runner).
    /// </summary>
    public WalkerProcessLayoutProber(string explicitBinaryPath)
        => _explicitBinaryPath = explicitBinaryPath;

    public WalkerLayoutProbeResult Probe(string binariesDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(binariesDir);

        var walkerBin = WalkerProcessRunner.ResolveWalkerBinary(_explicitBinaryPath);
        if (!File.Exists(walkerBin))
        {
            // Fail loud: the walker is a hard dependency of probe-layout. No fallback or guess
            //. Tests inject a fake prober and never reach this path.
            throw new FileNotFoundException(
                $"walker binary not found at '{walkerBin}'. Set {WalkerProcessRunner.BinaryPathEnvVar} to " +
                "the built cs2_schema_walker executable, or build the walker (walker/build). " +
                "The host cannot probe a layout without the walker.",
                walkerBin);
        }

        var psi = new ProcessStartInfo
        {
            FileName = walkerBin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("probe-layout");
        psi.ArgumentList.Add("--binaries");
        psi.ArgumentList.Add(binariesDir);

        using var proc = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdoutBuilder.AppendLine(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderrBuilder.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        proc.WaitForExit();

        return new WalkerLayoutProbeResult(
            proc.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }
}
