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
