// Serialize tests that capture process-global Console.Out / Console.Error.
//
// Several command tests redirect Console.Out/Console.Error (via Console.SetOut/SetError) to a
// StringWriter to assert a subcommand's stdout/stderr. That redirection is PROCESS-GLOBAL: xUnit
// parallelizes test classes by default, so two such classes running concurrently clobber each
// other's capture and produce flaky assertion failures. A shared collection with parallelization
// disabled forces every Console-capturing command test to run sequentially.

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[CollectionDefinition("console-capturing", DisableParallelization = true)]
public sealed class ConsoleCapturingTestGroup
{
}

/// <summary>
/// Shared stdout/stderr capture for command tests: swaps Console.Out/Console.Error around the
/// invocation and restores them. The swap is process-global, so callers must sit in the
/// "console-capturing" collection (or another parallelization-disabled one).
/// </summary>
public static class ConsoleCapture
{
    public static (int Code, string Out, string Err) Run(Func<int> body)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        { return (body(), stdout.ToString(), stderr.ToString()); }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }
}
