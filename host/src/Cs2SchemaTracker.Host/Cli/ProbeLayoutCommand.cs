// Schema-system layout probe (host-side).
//
// Invokes the walker's `probe-layout` subcommand against a directory of CS2 binaries and
// reports the LIVE schema-system memory-layout signature. The walker LOADS the modules and
// computes the signature from the live object graph (unlike --print-signature, which is the
// pure compile-time signature). Exit-code semantics are propagated 1:1 from the walker:
//   0  = known/recognized layout  -> signature printed to stdout, host exits 0.
// 75 = UNKNOWN layout -> signature surfaced to stderr, host exits 75 (never guess).
//   other non-zero (e.g. 65)      -> probe failure (missing dir, load error) -> host exits same.
//
// The walker binary is resolved the same way the default extract path resolves it
// (WalkerProcessRunner.ResolveWalkerBinary via the WalkerProcessLayoutProber); the
// invocation goes through the IWalkerLayoutProber seam, so the orchestration is
// unit-testable against a FAKE prober without a built walker (mirrors extract's runner seam).
//
// README.md surface (stable): `probe-layout --binaries <dir>`. No --print-signature
// mode is exposed here (that would be a new public-surface arg); the host's required
// surface is the LIVE --binaries probe, which is what this command implements.
//
// fail-loud + no side effects: a missing --binaries dir, an unresolvable/unspawnable
// walker, or any non-zero walker exit -> non-zero host exit with a clear stderr message. This
// command writes NO artifact bytes; it is read-only + a single process invocation.

using Cs2SchemaTracker.Host.Walker;

namespace Cs2SchemaTracker.Host.Cli;

internal static class ProbeLayoutCommand
{
    /// <summary>
    /// Production entry: probe the LIVE layout via the real walker (resolved through
    /// <see cref="WalkerProcessRunner.ResolveWalkerBinary"/>).
    /// </summary>
    public static int Run(string[] args)
        => Run(args, proberFactory: null);

    /// <summary>
    /// Shared entry. <paramref name="proberFactory"/> is the test seam: when supplied, that fake
    /// <see cref="IWalkerLayoutProber"/> is used instead of launching the real walker, so the
    /// known / unknown / failure paths are exercised without a built walker binary. The
    /// factory is only invoked AFTER argument validation and the --binaries existence check.
    /// </summary>
    internal static int Run(string[] args, Func<IWalkerLayoutProber>? proberFactory)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker probe-layout — report the schema-system layout signature of
binaries in a directory.

Usage: cs2-schema-tracker probe-layout --binaries <dir>

Arguments (stable per README.md):
  --binaries <dir>  Directory containing the Source 2 DLLs to probe.

behavior:
  - Loads the binaries via the walker and prints the LIVE layout signature on stdout.
  - Exits 0 if the layout is known and supported by the walker's extractor.
  - Exits 75 with the signature on stderr if the layout is UNKNOWN.
  - Never silently falls back to a guessed extractor.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("binaries", out var binaries) || string.IsNullOrEmpty(binaries))
        {
            Console.Error.WriteLine("probe-layout: --binaries <dir> is required. Run 'probe-layout --help'.");
            return 64;   // EX_USAGE
        }

        // fail-loud: the binaries dir must exist before we launch the walker. A missing
        // dir is an operator error, not a layout-unknown condition; report it as a usage/data
        // error (EX_DATAERR) rather than letting it masquerade as a probe failure.
        binaries = Path.GetFullPath(binaries);
        if (!Directory.Exists(binaries))
        {
            Console.Error.WriteLine(
                $"probe-layout: binaries directory not found: '{binaries}'. " +
                "Pass an already-acquired CS2 binaries directory.");
            return 65;   // EX_DATAERR
        }

        // Resolve the prober. Production: the real walker (path resolved via
        // WalkerProcessRunner.ResolveWalkerBinary). Test seam: the injected fake.
        IWalkerLayoutProber prober;
        try
        {
            prober = proberFactory is not null ? proberFactory() : new WalkerProcessLayoutProber();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe-layout: could not construct the walker prober: {ex.Message}");
            return 70;   // EX_SOFTWARE
        }

        WalkerLayoutProbeResult result;
        try
        {
            Console.Error.WriteLine($"probe-layout: probing binaries='{binaries}'");
            result = prober.Probe(binaries);
        }
        catch (Exception ex)
        {
            // Fail loud: the walker could not be spawned at all (e.g. binary not found).
            Console.Error.WriteLine($"probe-layout: walker invocation failed: {ex.Message}");
            return 70;   // EX_SOFTWARE
        }

        // The walker prints the signature to stdout on both the known AND unknown paths.
        var signature = FirstNonEmptyLine(result.Stdout);

        if (result.ExitCode == 0)
        {
            // Known layout: print the signature to stdout in a stable, single-line form.
            if (string.IsNullOrEmpty(signature))
            {
                // Exit 0 but no signature is a walker contract violation, not a known layout.
                Console.Error.WriteLine(
                    "probe-layout: walker reported success (exit 0) but printed no layout signature. " +
                    "Aborting (contract violation).");
                return 70;   // EX_SOFTWARE
            }
            Console.WriteLine(signature);
            Console.Error.WriteLine("probe-layout: layout KNOWN.");
            return 0;
        }

        // Non-zero walker exit.: an UNKNOWN layout (exit 75) must surface the signature to
        // stderr and propagate the exit code; we NEVER guess or swallow it. Other non-zero exits
        // (e.g. 65 = load/data error) are propagated verbatim too.
        if (result.ExitCode == 75)
        {
            Console.Error.WriteLine(
                "probe-layout: UNKNOWN schema-system layout signature: " +
                (string.IsNullOrEmpty(signature) ? "<none on stdout>" : signature));
        }
        else
        {
            Console.Error.WriteLine(
                $"probe-layout: walker exited {result.ExitCode} (probe failed). No layout reported.");
        }

        // Surface the walker's stderr verbatim so the operator sees the full reason / signature.
        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            Console.Error.WriteLine("--- walker stderr ---");
            Console.Error.Write(result.Stderr);
            if (!result.Stderr.EndsWith('\n'))
                Console.Error.WriteLine();
            Console.Error.WriteLine("---------------------");
        }

        return result.ExitCode;
    }

    /// <summary>
    /// The first non-empty, trimmed line of the walker's stdout. The walker prints the layout
    /// signature as a single line; this tolerates trailing newlines / CRLF without depending on
    /// the exact line-ending the child emits.
    /// </summary>
    private static string FirstNonEmptyLine(string stdout)
    {
        if (string.IsNullOrEmpty(stdout))
            return "";
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0)
                return line;
        }
        return "";
    }
}
