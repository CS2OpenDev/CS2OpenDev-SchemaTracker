// Registry audit aggregation CLI.

using Cs2SchemaTracker.Host.RegistryAudit;

namespace Cs2SchemaTracker.Host.Cli;

internal static class AuditCommand
{
    public static int Run(string[] args)
    {
        if (CliArgs.HasHelpFlag(args))
        {
            Console.WriteLine(@"cs2-schema-tracker audit — regenerate registry_audit.json deterministically for
an existing artifact set.

Usage: cs2-schema-tracker audit --artifacts <dir>

Arguments (stable per README.md):
  --artifacts <dir> Path to the (build, tuple) artifact directory.

behavior:
  - Enumerates every named registry symbol present in the binaries.
  - Each is labeled extracted (with the producing artifact filename) or
    omitted (with rationale).
  - CI fails (in the calling workflow) if any symbol is neither extracted
    nor omitted-with-rationale.
  - Output is deterministic given the same inputs.");
            return 0;
        }

        var parsed = CliArgs.Parse(args);
        if (!parsed.TryGetValue("artifacts", out var artifacts) || string.IsNullOrEmpty(artifacts))
        {
            Console.Error.WriteLine("audit: --artifacts <dir> is required. Run 'audit --help'.");
            return 64;
        }

        var artifactsDir = Path.GetFullPath(artifacts);

        // fail-loud: any parse/consistency failure throws out of the emitter; we let it
        // propagate to a non-zero exit and write no partial artifact (AtomicWrite is all-or-nothing).
        RegistryAuditEmitter.EmitForDirectory(artifactsDir);

        Console.WriteLine(
            $"audit: wrote {RegistryAuditEmitter.OutputFileName} to {artifactsDir}");
        return 0;
    }
}
