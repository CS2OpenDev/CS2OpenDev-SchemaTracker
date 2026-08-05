// Serialize the config-no-leak determinism test.
//
// ConfigNoLeakTest mutates BOTH the process-global CS2_BINARIES_ROOT env var AND the process
// working directory across two extraction runs, so it must not race any other env/cwd-mutating
// class. xUnit runs members of a named collection sequentially; DisableParallelization makes the
// guarantee explicit (no other collection runs concurrently with this one either).

using Xunit;

namespace Cs2SchemaTracker.Tests.Config;

[CollectionDefinition("config-no-leak", DisableParallelization = true)]
public sealed class ConfigNoLeakTestGroup
{
}
