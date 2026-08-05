// Serialize env-var-mutating tests.
//
// SteamCredentialsTest and AcquireAuthArgsTest both set/unset STEAM_* process env
// vars. xUnit parallelizes test classes by default; a shared collection forces
// these classes to run sequentially so they don't race on the process environment.

using Xunit;

namespace Cs2SchemaTracker.Tests.Steam;

[CollectionDefinition("env-mutating", DisableParallelization = true)]
public sealed class EnvMutatingTestGroup
{
}
