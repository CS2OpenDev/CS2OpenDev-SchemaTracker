// walker-subprocess seam.
//
// The host invokes the C++ walker (walker/) as one subprocess per (build, platform).
// ExtractCommand depends on this interface, not on the concrete process-launching
// implementation, so the orchestration is unit-testable against a FAKE runner
// without a built walker binary.
//
// Contract with the walker CLI (walker/src/cli.cpp, subcommand `walk`):
//   cs2_schema_walker walk --binaries <dir> --platform <P> --out <file>
//   - exit 0   ⇒ <out> holds a complete binary WalkerOutput proto (schemas/walker_output.proto)
// - exit !=0 ⇒ failure (incl. exit 75 = unknown schema-system layout); stderr
//                carries the human-readable reason / layout signature. No output bytes
//                are trustworthy on a non-zero exit.
//
// One walk per platform loads ALL modules (client+server+engine) of that per-OS
// depot; client/server is the per-class module tag, not a walker argument.

namespace Cs2SchemaTracker.Host.Walker;

/// <summary>
/// Launches the walker for one (binaries-dir, platform) and has it write a binary
/// <c>WalkerOutput</c> proto to <c>outPath</c>. The single seam the extract
/// orchestration crosses into the walker; a fake implementation drives the host tests.
/// </summary>
internal interface IWalkerRunner
{
    /// <summary>
    /// Run the walker's <c>walk</c> subcommand. Returns the walker's process exit code
    /// (0 = success). On a non-zero exit, <paramref name="stderr"/> carries the walker's
    /// captured standard-error text so the caller can surface it.
    /// </summary>
    /// <param name="binariesDir">Absolute path to the directory of acquired CS2 binaries.</param>
    /// <param name="platform">The target platform (<c>windows-x86_64</c> or <c>linux-x86_64</c>).</param>
    /// <param name="outPath">
    /// Absolute path the walker writes its binary WalkerOutput to on success. The caller
    /// owns this path; the walker writes it atomically (sibling .tmp then rename).
    /// </param>
    /// <param name="stderr">The walker's captured stderr (empty on a clean run with no diagnostics).</param>
    int Run(string binariesDir, string platform, string outPath, out string stderr);
}
