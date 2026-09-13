// WalkerProcessRunner.ApplyKv3DefaultsGate tests — the host is authoritative over
// CS2_WALKER_NO_KV3_DEFAULTS in BOTH directions.
//
// The walker opts out of the MGetKV3ClassDefaults live recovery on the variable's PRESENCE
// (walker/src/schema_walk.cpp MaybeResolveSaveKv3Json: `std::getenv(...) != nullptr`), so a value
// inherited by the host process — an exported shell var, or any key the repo .env pushes in via
// DotEnv.LoadFromRepoRoot — used to reach the walker unchanged and silently empty every
// MGetKV3ClassDefaults value on a kv3ClassDefaults=true era. These tests pin that the gate REMOVES
// such an entry, and fence off the two wrong fixes (mutating the host's own environment; rebuilding
// the child's environment block from scratch).
//
// No walker binary is launched: the ProcessStartInfo is built and inspected in-process.

using System.Diagnostics;

using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Walker;

// Mutates a process-global env var, so it shares the serialized collection with the other
// env-mutating walker tests (no cross-test env races).
[Collection("era-walker")]
public class WalkerProcessRunnerEnvTest
{
    [Theory]
    [InlineData("1")]
    // "0" and "false" are the real-world shape and the reason presence-testing bites: they read to a
    // human as "leave the KV3 recovery ON" and do the exact opposite to the walker. (An empty string
    // is deliberately not a row — on Windows setting a variable to "" deletes it, so it cannot model
    // a stale inherited value.)
    [InlineData("0")]
    [InlineData("false")]
    public void ApplyKv3DefaultsGate_ValidatedEra_RemovesInheritedVariableWhateverItsValue(
        string inherited)
    {
        WithInheritedGateVar(inherited, () =>
        {
            // ProcessStartInfo.Environment is seeded LAZILY from this process on first touch, so the
            // env var must already be set before the instance is constructed and read.
            var psi = new ProcessStartInfo { UseShellExecute = false };

            WalkerProcessRunner.ApplyKv3DefaultsGate(psi, disableKv3Defaults: false);

            Assert.False(psi.Environment.ContainsKey(WalkerProcessRunner.DisableKv3DefaultsEnvVar));
        });
    }

    [Fact]
    public void ApplyKv3DefaultsGate_ValidatedEra_DoesNotMutateHostEnvironment()
    {
        // Fences off the wrong fix of clearing the variable on the HOST process, which in a batch
        // run would leak one era's decision into every later era in the same process.
        WithInheritedGateVar("1", () =>
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };

            WalkerProcessRunner.ApplyKv3DefaultsGate(psi, disableKv3Defaults: false);

            Assert.Equal(
                "1",
                Environment.GetEnvironmentVariable(WalkerProcessRunner.DisableKv3DefaultsEnvVar));
        });
    }

    [Fact]
    public void ApplyKv3DefaultsGate_ValidatedEra_LeavesRestOfInheritedEnvironmentIntact()
    {
        // Fences off the wrong fix of Clear()ing or rebuilding the child's environment block, which
        // would strip PATH and the LD_LIBRARY_PATH that Run() composes immediately afterwards.
        WithInheritedGateVar(null, () =>
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };
            var before = psi.Environment.Count;

            WalkerProcessRunner.ApplyKv3DefaultsGate(psi, disableKv3Defaults: false);

            Assert.NotEqual(0, before);
            Assert.Equal(before, psi.Environment.Count);
        });
    }

    [Fact]
    public void ApplyKv3DefaultsGate_UnvalidatedEra_SetsVariableToOne()
    {
        WithInheritedGateVar(null, () =>
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };

            WalkerProcessRunner.ApplyKv3DefaultsGate(psi, disableKv3Defaults: true);

            Assert.Equal("1", psi.Environment[WalkerProcessRunner.DisableKv3DefaultsEnvVar]);
        });
    }

    [Fact]
    public void ApplyKv3DefaultsGate_UnvalidatedEra_OverwritesInheritedValue()
    {
        // The other half of "authoritative in both directions": the child never sees a stale
        // operator string even on the eras the host does gate off.
        WithInheritedGateVar("0", () =>
        {
            var psi = new ProcessStartInfo { UseShellExecute = false };

            WalkerProcessRunner.ApplyKv3DefaultsGate(psi, disableKv3Defaults: true);

            Assert.Equal("1", psi.Environment[WalkerProcessRunner.DisableKv3DefaultsEnvVar]);
        });
    }

    /// <summary>
    /// Run <paramref name="body"/> with the gate variable set to <paramref name="value"/> (null to
    /// unset it) in THIS process, restoring whatever the runner actually inherited afterwards — the
    /// house env idiom, see ExtractWalkerDriftGuardTest.
    /// </summary>
    private static void WithInheritedGateVar(string? value, Action body)
    {
        var old = Environment.GetEnvironmentVariable(WalkerProcessRunner.DisableKv3DefaultsEnvVar);
        Environment.SetEnvironmentVariable(WalkerProcessRunner.DisableKv3DefaultsEnvVar, value);
        try
        {
            body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(WalkerProcessRunner.DisableKv3DefaultsEnvVar, old);
        }
    }
}
