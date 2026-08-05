// WalkerIdentity.Parse tests — the extractor identity chain, host side.
//
// Covers the two documented `<walker> --version` output shapes (walker/src/main.cpp RunVersion()):
//   line 1 (pre-existing, byte-stable): "cs2-schema-walker <ver> (git <sha>, schema <ver>)"
//   line 2 (may be absent on an older binary): "src-fingerprint <64-hex>"
// Pure parsing only — no process launch, no filesystem, no real walker binary required.

using Cs2SchemaTracker.Host.Walker;

using Xunit;

namespace Cs2SchemaTracker.Tests.Walker;

public class WalkerIdentityTest
{
    // A real 2-line sample captured from `cs2-2026-07-09.exe --version` on a walker that stamps a
    // src-fingerprint (see walker/src/main.cpp RunVersion()).
    private const string TwoLineSample =
        "cs2-schema-walker 0.2.0 (git b53b4844e82107eea833c1f5235a25e8d7424ede, schema 0.4.0)\n" +
        "src-fingerprint 3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc\n";

    // A real sample captured from a CURRENTLY-BUILT natives/windows-x86_64/*.exe on disk today — this
    // repo's binaries predate the src-fingerprint line, so `--version` prints ONLY line 1.
    private const string OneLineLegacySample =
        "cs2-schema-walker 0.2.0 (git b53b4844e82107eea833c1f5235a25e8d7424ede, schema 0.4.0)\n";

    [Fact]
    public void Parse_TwoLineForm_ReadsVersionGitShaAndFingerprint()
    {
        var id = WalkerIdentity.Parse(TwoLineSample);

        Assert.Equal("0.2.0", id.Version);
        Assert.Equal("b53b4844e82107eea833c1f5235a25e8d7424ede", id.GitSha);
        Assert.Equal(
            "3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc", id.SrcFingerprint);
    }

    [Fact]
    public void Parse_OneLineLegacyForm_FingerprintIsUnknown()
    {
        var id = WalkerIdentity.Parse(OneLineLegacySample);

        Assert.Equal("0.2.0", id.Version);
        Assert.Equal("b53b4844e82107eea833c1f5235a25e8d7424ede", id.GitSha);
        Assert.Equal(WalkerIdentity.UnknownFingerprint, id.SrcFingerprint);
    }

    [Fact]
    public void Parse_OneLineForm_NoTrailingNewline_StillParses()
    {
        // Exactly line 1, no trailing \n at all — the minimal valid one-line contract.
        var id = WalkerIdentity.Parse(
            "cs2-schema-walker 0.2.0 (git b53b4844e82107eea833c1f5235a25e8d7424ede, schema 0.4.0)");

        Assert.Equal("0.2.0", id.Version);
        Assert.Equal(WalkerIdentity.UnknownFingerprint, id.SrcFingerprint);
    }

    [Fact]
    public void Parse_NoGitBuild_GitShaLiteralUnknown_StillParses()
    {
        // version.h's WALKER_GIT_SHA fallback is the literal string "unknown" (no-git/archive build)
        // — the git-sha capture group must accept it (it is NOT constrained to hex).
        var id = WalkerIdentity.Parse("cs2-schema-walker 0.2.0 (git unknown, schema 0.4.0)\n");

        Assert.Equal("unknown", id.GitSha);
        Assert.Equal(WalkerIdentity.UnknownFingerprint, id.SrcFingerprint);
    }

    [Fact]
    public void Parse_TrailingBlankLine_AfterFingerprint_DoesNotBreakParsing()
    {
        var id = WalkerIdentity.Parse(TwoLineSample + "\n\n");

        Assert.Equal(
            "3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc", id.SrcFingerprint);
    }

    [Theory]
    // Garbage input: empty, whitespace-only, a plausible-but-wrong tool name, and genuinely random
    // text. All must fail LOUD (never guess a "close enough" identity from an unrecognized line 1).
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   \n")]
    [InlineData("not the walker at all\n")]
    [InlineData("cs2-schema-walker\n")]
    [InlineData("some other tool 1.0.0 (git abc123, schema 0.1.0)\n")]
    [InlineData("cs2-schema-walker 0.2.0 MISSING-PARENS git abc123 schema 0.4.0\n")]
    public void Parse_GarbageInput_ThrowsInvalidDataException(string garbage)
    {
        Assert.Throws<InvalidDataException>(() => WalkerIdentity.Parse(garbage));
    }

    [Fact]
    public void Parse_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => WalkerIdentity.Parse(null!));
    }

    [Fact]
    public void Parse_CrLfLineEndings_ParsesIdenticallyToLf()
    {
        var crlf = TwoLineSample.Replace("\n", "\r\n", StringComparison.Ordinal);
        var id = WalkerIdentity.Parse(crlf);

        Assert.Equal(
            "3f9a2c1d7e6b5a4938271605f4e3d2c1b0a9887766554433221100ffeeddcc", id.SrcFingerprint);
    }
}
