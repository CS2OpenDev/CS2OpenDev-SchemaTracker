// emit-localization CLI: on-demand rebuild of the build-on-demand localization.json + the --verify
// byte-verifiable round-trip against a committed provenance.localization fingerprint.
//
// FIXTURE-SIZED (never real Steam / never a 199 MB emit): a tiny synthetic content pak01_dir.vpk
// (ContentSamples.StandardEntries, which carries resource/csgo_english.txt + csgo_german.txt) is
// dropped in the conventional co-located content location (cache/binaries/<build>/<platform>/game/
// csgo/) so ExtractCommand.TryResolveContentVpk resolves it from cwd — the same seam the content
// emitter tests use. cwd is pinned through the shared "cwd-mutating" collection.
//
// Coverage:
//   * usage errors: missing --build (64), non-canonical --platform (64);
//   * unresolvable content -> 65 (no VPK anywhere);
//   * happy path (no --verify): rebuilds localization.json, exit 0;
//   * --verify MATCH: provenance.localization == the rebuilt fingerprint -> exit 0;
//   * --verify MISMATCH: a wrong recorded sha256 -> exit 65 (fail-loud, no false-OK);
//   * --verify with NO committed fingerprint -> 65.

using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class EmitLocalizationCommandTest
{
    private const string BuildId = "24680246";

    // Use the running OS's canonical platform so nothing is skipped on either CI leg. (The command
    // itself is OS-agnostic; only the platform NAME must be canonical.)
    private static string Platform =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows-x86_64" : "linux-x86_64";

    private static void InWorkDir(Action<string> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "emit-loc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        { body(workDir); }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    /// <summary>Drop the fixture-sized content pak in the conventional co-located location under cwd.</summary>
    private static void SetupContent(string workDir)
    {
        var csgo = Path.Combine(workDir, "cache", "binaries", BuildId, Platform, "game", "csgo");
        ContentVpkFixture.Write(csgo, ContentSamples.StandardEntries());
    }

    private static string LocalizationPath(string workDir)
        => Path.Combine(workDir, "artifacts", BuildId, Platform, "localization.json");

    private static string ProvenancePath(string workDir)
        => Path.Combine(workDir, "artifacts", BuildId, Platform, "provenance.json");

    /// <summary>Write a provenance.json carrying the given localization fingerprint (or none).</summary>
    private static void WriteProvenance(string workDir, LocalizationOutput? fingerprint)
    {
        var prov = new Schemas.Provenance();
        if (fingerprint is not null)
            prov.Localization = fingerprint;
        var path = ProvenancePath(workDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicWrite.WriteCanonical(prov, path);
    }

    [Fact]
    public void MissingBuild_UsageError_Exit64()
    {
        var code = EmitLocalizationCommand.Run(new[] { "--platform", Platform });
        Assert.Equal(64, code);
    }

    [Fact]
    public void NonCanonicalPlatform_UsageError_Exit64()
    {
        var code = EmitLocalizationCommand.Run(new[] { "--build", BuildId, "--platform", "macos-arm64" });
        Assert.Equal(64, code);
    }

    [Fact]
    public void UnresolvableContent_Exit65()
    {
        InWorkDir(_ =>
        {
            // No cache/binaries content anywhere under cwd -> unresolvable content depot.
            var code = EmitLocalizationCommand.Run(new[] { "--build", BuildId, "--platform", Platform });
            Assert.Equal(65, code);
            Assert.False(File.Exists(LocalizationPath(Directory.GetCurrentDirectory())));
        });
    }

    [Fact]
    public void HappyPath_RebuildsLocalization_Exit0()
    {
        InWorkDir(workDir =>
        {
            SetupContent(workDir);

            var code = EmitLocalizationCommand.Run(new[] { "--build", BuildId, "--platform", Platform });
            Assert.Equal(0, code);
            Assert.True(File.Exists(LocalizationPath(workDir)), "localization.json must be regenerated");
        });
    }

    [Fact]
    public void Verify_Match_Exit0()
    {
        InWorkDir(workDir =>
        {
            SetupContent(workDir);

            // 1. Rebuild once to obtain the canonical bytes, then fingerprint them with the SAME
            //    production function emit-localization uses.
            Assert.Equal(0, EmitLocalizationCommand.Run(new[] { "--build", BuildId, "--platform", Platform }));
            var fingerprint = ExtractCommand.ComputeLocalizationFingerprint(LocalizationPath(workDir), tokenCount: 0);

            // 2. Record that fingerprint as the committed provenance.localization.
            WriteProvenance(workDir, fingerprint);

            // 3. --verify re-emits deterministically and must MATCH the committed fingerprint.
            var code = EmitLocalizationCommand.Run(
                new[] { "--build", BuildId, "--platform", Platform, "--verify" });
            Assert.Equal(0, code);
        });
    }

    [Fact]
    public void Verify_Mismatch_Exit65()
    {
        InWorkDir(workDir =>
        {
            SetupContent(workDir);
            Assert.Equal(0, EmitLocalizationCommand.Run(new[] { "--build", BuildId, "--platform", Platform }));
            var real = ExtractCommand.ComputeLocalizationFingerprint(LocalizationPath(workDir), tokenCount: 0);

            // A wrong recorded sha256 (right length, wrong bytes) must fail-loud.
            WriteProvenance(workDir, new LocalizationOutput
            {
                Sha256 = new string('a', 64),
                SizeBytes = real.SizeBytes,
                TokenCount = real.TokenCount,
            });

            var code = EmitLocalizationCommand.Run(
                new[] { "--build", BuildId, "--platform", Platform, "--verify" });
            Assert.Equal(65, code);
        });
    }

    [Fact]
    public void Verify_NoCommittedFingerprint_Exit65()
    {
        InWorkDir(workDir =>
        {
            SetupContent(workDir);
            // Provenance exists but carries NO localization fingerprint -> nothing to verify against.
            WriteProvenance(workDir, fingerprint: null);

            var code = EmitLocalizationCommand.Run(
                new[] { "--build", BuildId, "--platform", Platform, "--verify" });
            Assert.Equal(65, code);
        });
    }
}
