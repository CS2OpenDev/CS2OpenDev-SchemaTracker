// walker layout-probe seam.
//
// Sibling of IWalkerRunner (the `walk` seam). The host's `probe-layout` subcommand
// invokes the walker's `probe-layout` subcommand, which LOADS the CS2 modules from a
// binaries dir and reports the LIVE schema-system memory-layout signature:
//
//   cs2_schema_walker probe-layout --binaries <dir>
//   - exit 0   ⇒ KNOWN/recognized layout; signature printed to stdout.
// - exit 75 ⇒ UNKNOWN layout; signature printed to stdout AND stderr.
//   - exit !=0 (other, e.g. 65) ⇒ probe failed (missing dir, load failure); stderr
//                carries the reason. No trustworthy signature.
//
// The host depends on this interface, not the concrete process launcher, so the
// probe-layout orchestration is unit-testable against a FAKE prober without a built
// walker binary (mirrors the IWalkerRunner fake-runner pattern used by extract).

namespace Cs2SchemaTracker.Host.Walker;

/// <summary>The outcome of one walker <c>probe-layout</c> invocation.</summary>
/// <param name="ExitCode">
/// The walker's process exit code, verbatim. 0 = known layout, 75 = unknown
/// layout, other non-zero = probe failure. The host maps this 1:1 to its own exit.
/// </param>
/// <param name="Stdout">
/// The walker's captured stdout. On a recognized OR unknown layout the walker prints
/// the layout signature here (one line). May be empty on a hard probe failure.
/// </param>
/// <param name="Stderr">
/// The walker's captured stderr. On an unknown layout (exit 75) and on hard failures
/// this carries the human-readable reason / signature.
/// </param>
internal readonly record struct WalkerLayoutProbeResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Launches the walker's <c>probe-layout</c> subcommand for one binaries dir and returns
/// its exit code + captured stdout/stderr. The single seam the host's probe-layout command
/// crosses into the walker; a fake implementation drives the host tests.
/// </summary>
internal interface IWalkerLayoutProber
{
    /// <summary>
    /// Run <c>cs2_schema_walker probe-layout --binaries &lt;binariesDir&gt;</c>.
    /// Returns the walker's exit code and captured stdout/stderr verbatim — the host
    /// interprets the exit code (: never swallowed). Throws (fail-loud) only if the
    /// walker binary cannot be spawned at all.
    /// </summary>
    /// <param name="binariesDir">Absolute path to the directory of CS2 binaries to probe.</param>
    WalkerLayoutProbeResult Probe(string binariesDir);
}
