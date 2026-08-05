// Minimal argument-parsing helper shared by every subcommand stub.
//
// Recognizes `--name value` and `--name=value`. Required-arg validation is the
// caller's responsibility (so error messages stay specific to the subcommand).
//
// Intentionally tiny and dependency-free. When the host adopts a CLI library
// (System.CommandLine, McMaster.Extensions.CommandLineUtils, ...) this file goes
// away wholesale — the contract is the subcommand names + arg names in
// README.md, not this implementation.

namespace Cs2SchemaTracker.Host.Cli;

internal static class CliArgs
{
    public static Dictionary<string, string> Parse(string[] args)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                continue;

            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                parsed[arg[2..eq]] = arg[(eq + 1)..];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parsed[arg[2..]] = args[i + 1];
                i++;
            }
            else
            {
                parsed[arg[2..]] = "";        // flag with no value
            }
        }
        return parsed;
    }

    public static bool HasHelpFlag(string[] args) =>
        args.Any(a => a is "-h" or "--help");
}
