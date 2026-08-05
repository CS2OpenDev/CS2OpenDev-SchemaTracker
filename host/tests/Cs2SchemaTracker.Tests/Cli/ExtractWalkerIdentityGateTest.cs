// Walker identity gate decision-matrix tests.
//
// ExtractCommand.EvaluateWalkerIdentityGate is the PURE decision core of the WALKER IDENTITY GATE
// (PreflightWalkerIdentity in ExtractCommand.Batch.cs): given the de-duplicated set of resolved
// walker src-fingerprints + a violation count, it decides fail (exit 78) / warn / proceed WITHOUT
// launching any process or touching disk. This suite exercises that matrix directly — the
// surrounding I/O (real `--version` subprocess launches, era resolution) is exercised by the
// existing fake-runner orchestration suites (which never reach this gate — see
// PreflightWalkerIdentity's "runnerFactory is not null" skip) and by a manual smoke run against the
// real natives/ binaries, since that path needs a real walker exe.

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

public class ExtractWalkerIdentityGateTest
{
    private static SortedSet<string> Fingerprints(params string[] values)
        => new(values, StringComparer.Ordinal);

    [Fact]
    public void UniformKnownFingerprint_NonCommit_Proceeds()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 0, commit: false, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Null(verdict.ExitCode);
        Assert.False(verdict.MixedOrUnverified);
        Assert.Equal("3f9a2c1d7e6b", verdict.WalkersDisplay);   // first 12 chars
    }

    [Fact]
    public void UniformKnownFingerprint_Commit_NoViolations_Proceeds()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 0, commit: true, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Null(verdict.ExitCode);
        Assert.False(verdict.MixedOrUnverified);
    }

    [Fact]
    public void MixedFingerprints_NonCommit_WarnsButProceeds()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("aaaa000000000000000000000000000000000000000000000000000000000000",
                         "bbbb000000000000000000000000000000000000000000000000000000000000"),
            violationCount: 0, commit: false, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Null(verdict.ExitCode);
        Assert.True(verdict.MixedOrUnverified);
        Assert.Equal("mixed", verdict.WalkersDisplay);
    }

    [Fact]
    public void MixedFingerprints_Commit_NoAllowFlag_HardFails78()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("aaaa000000000000000000000000000000000000000000000000000000000000",
                         "bbbb000000000000000000000000000000000000000000000000000000000000"),
            violationCount: 0, commit: true, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Equal(78, verdict.ExitCode);
        Assert.True(verdict.MixedOrUnverified);
        Assert.Equal("mixed", verdict.WalkersDisplay);
    }

    [Fact]
    public void MixedFingerprints_Commit_AllowMixedWalkers_WarnsButProceeds()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("aaaa000000000000000000000000000000000000000000000000000000000000",
                         "bbbb000000000000000000000000000000000000000000000000000000000000"),
            violationCount: 0, commit: true, allowMixedWalkers: true, expectFingerprintEnv: null);

        Assert.Null(verdict.ExitCode);
        Assert.True(verdict.MixedOrUnverified);
    }

    [Fact]
    public void SingleUnknownFingerprint_PreWp3Binary_Commit_HardFails78()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints(WalkerIdentity.UnknownFingerprint),
            violationCount: 1, commit: true, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Equal(78, verdict.ExitCode);
        Assert.True(verdict.MixedOrUnverified);
        Assert.Equal("unknown", verdict.WalkersDisplay);
    }

    [Fact]
    public void SingleUnknownFingerprint_NonCommit_WarnsButProceeds()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints(WalkerIdentity.UnknownFingerprint),
            violationCount: 1, commit: false, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Null(verdict.ExitCode);
        Assert.True(verdict.MixedOrUnverified);
        Assert.Equal("unknown", verdict.WalkersDisplay);
    }

    [Fact]
    public void SingleErrorToken_ResolutionFailed_Commit_HardFails78()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints(ExtractCommand.ErrorFingerprintToken),
            violationCount: 1, commit: true, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Equal(78, verdict.ExitCode);
        Assert.Equal("unknown", verdict.WalkersDisplay);
    }

    [Fact]
    public void UniformFingerprint_ButManifestViolation_Commit_StillHardFails78()
    {
        // Two binaries happen to report the SAME fingerprint (fingerprints.Count == 1) but a
        // walker-manifest.json disagreement was folded into violationCount — non-uniformity is not
        // the ONLY thing that makes a set untrustworthy.
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 1, commit: true, allowMixedWalkers: false, expectFingerprintEnv: null);

        Assert.Equal(78, verdict.ExitCode);
        Assert.True(verdict.MixedOrUnverified);
    }

    [Fact]
    public void ExpectFingerprint_PrefixMatches_Proceeds()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 0, commit: false, allowMixedWalkers: false, expectFingerprintEnv: "3f9a2c1d7e6b");

        Assert.Null(verdict.ExitCode);
    }

    [Fact]
    public void ExpectFingerprint_Mismatch_NonCommit_StillHardFails78()
    {
        // Unconditional tripwire: fires even on a non-commit run where the mixed-set check alone
        // would only warn.
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 0, commit: false, allowMixedWalkers: false, expectFingerprintEnv: "deadbeef");

        Assert.Equal(78, verdict.ExitCode);
    }

    [Fact]
    public void ExpectFingerprint_Mismatch_AllowMixedWalkersDoesNotBypassIt()
    {
        // --allow-mixed-walkers bypasses the GENERIC mixed-set check, never the explicit
        // CS2_EXPECT_FPRINT operator assertion.
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 0, commit: true, allowMixedWalkers: true, expectFingerprintEnv: "deadbeef");

        Assert.Equal(78, verdict.ExitCode);
    }

    [Fact]
    public void ExpectFingerprint_EmptyString_HasNoEffect()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc"),
            violationCount: 0, commit: true, allowMixedWalkers: false, expectFingerprintEnv: "");

        Assert.Null(verdict.ExitCode);
    }

    [Fact]
    public void ExpectFingerprint_MatchesOneOfAMixedSet_ButNotTheOther_StillFails()
    {
        var verdict = ExtractCommand.EvaluateWalkerIdentityGate(
            Fingerprints("aaaa000000000000000000000000000000000000000000000000000000000000",
                         "bbbb000000000000000000000000000000000000000000000000000000000000"),
            violationCount: 0, commit: false, allowMixedWalkers: false, expectFingerprintEnv: "aaaa");

        Assert.Equal(78, verdict.ExitCode);   // not ALL fingerprints match the expected prefix.
    }
}
