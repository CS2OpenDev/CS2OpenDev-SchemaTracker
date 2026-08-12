// Serialize process-CWD-mutating tests — against the WHOLE suite, not just each other.
//
// The extract/acquire CWD-pinning tests (ExtractAtUseVerificationTest and the other
// [Collection("cwd-mutating")] classes) point the process working directory at temp
// fixture trees holding populated cache/binaries/<build>/<platform> dirs. This
// definition was MISSING: an xUnit [Collection] with no [CollectionDefinition] still
// groups its classes sequentially, but runs IN PARALLEL with every other collection —
// so a pinned CWD leaked into any concurrently-running test that resolves CWD-relative
// defaults. Observed: AcquireCommandArgsTest.Latest_unified_also_fetches_colocated_content
// resolved its default outdir inside ExtractAtUseVerificationTest's pinned workdir
// (both use build 23669931 and the running OS's platform), hit the populated dir on
// the cache-first path, and exited 0 with zero acquirer invocations (ubuntu CI,
// run 31552729060). DisableParallelization gives this collection the same
// whole-suite isolation the env-mutating / config-no-leak / console-capturing /
// era-walker definitions already have.

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[CollectionDefinition("cwd-mutating", DisableParallelization = true)]
public sealed class CwdMutatingTestGroup
{
}
