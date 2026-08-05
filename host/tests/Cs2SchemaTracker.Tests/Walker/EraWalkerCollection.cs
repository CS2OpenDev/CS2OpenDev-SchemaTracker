// Walker multi-era build — serialize the era-resolution + second-gate
// tests that mutate process env vars (CS2_WALKER_BIN / CS2_WALKER_ERAS_ROOT) and, for the
// gate tests, the process working directory.
//
// xUnit parallelizes test classes by default; both EraWalkerResolverTest and
// ExtractCommandSecondGateTest set/unset the same process-global env vars, so a shared
// collection forces them to run sequentially (determinism — no cross-test env races).

using Xunit;

namespace Cs2SchemaTracker.Tests.Walker;

[CollectionDefinition("era-walker", DisableParallelization = true)]
public sealed class EraWalkerTestGroup
{
}
