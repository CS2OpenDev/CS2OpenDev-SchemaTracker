// End-to-end tests for extract's STALE CONTENT STORE guard.
//
// The store copy of a content pak is a TRIM: it holds only the resources the required set covered
// when it was written. Extract cannot re-trim one (it has no source pak), and the per-artifact
// HasSource probes cannot tell "this trim predates the resource" from "this era never shipped the
// resource" — so extracting from a stale store writes a FALSE CONTENT_NOT_SHIPPED_THIS_ERA omission
// into the committed corpus. The guard therefore ABORTS, before the walk and before any artifact
// byte, naming the GID, both generations and the `content-backfill --pak csgo --execute` remedy.
//
// The other half of the contract: a CO-LOCATED pak (the `content-store migrate` / live-install case)
// is the untrimmed original, carries no marker, and must keep extracting unchanged.

using System.Globalization;
using System.Runtime.InteropServices;

using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.Steam;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Tests.Content;

using Xunit;

namespace Cs2SchemaTracker.Tests.Cli;

[Collection("cwd-mutating")]
public sealed class ExtractStaleContentStoreTest
{
    private const string BuildId = "13390001";
    private const ulong Gid = 771100443UL;

    private static string? MatchingPlatform()
    {
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            return null;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-x86_64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "windows-x86_64";
        return null;
    }

    private static string TupleDir(string workDir, string platform)
        => Path.Combine(workDir, "cache", "binaries", BuildId, platform);

    private static void SetupBinaries(string workDir, string platform)
    {
        var binariesDir = TupleDir(workDir, platform);
        Directory.CreateDirectory(binariesDir);
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"),
            ExtractCommandTestShared.WithEmbeddedFdp(
                ExtractCommandTestShared.BuildElf(),
                ExtractCommandTestShared.BuildFdp("netmessages.proto")));
        File.WriteAllBytes(Path.Combine(binariesDir, "client.dll"),
            ExtractCommandTestShared.WithEmbeddedFdp(
                ExtractCommandTestShared.BuildPe(),
                ExtractCommandTestShared.BuildFdp("networkbasetypes.proto")));
    }

    private static void WriteManifestRecord(string workDir, string platform)
    {
        new ManifestRecord(730, uint.Parse(BuildId, CultureInfo.InvariantCulture), new[]
        {
            new ManifestRecordDepot(ContentStore.ContentDepotId, Gid, "2026-06-10T00:00:00Z"),
        }).WriteToTupleDir(TupleDir(workDir, platform));
    }

    /// <summary>
    /// Write a store trim for the GID through the production writer. <paramref name="stale"/> writes
    /// the generation-1 required set (no weapons.vdata_c) with NO marker — a proper, uncorrupted,
    /// pre-marker store, exactly the corpus's current shape.
    /// </summary>
    private static void WriteStoreCopy(string workDir, string platform, bool stale)
    {
        var contentRoot = ContentStore.RootForTupleDir(TupleDir(workDir, platform))!;
        var source = VpkArchive.Open(
            ContentVpkFixture.Write(Path.Combine(workDir, "src"), ContentSamples.StandardEntries()));
        var required = ContentPakSelector.EnumerateRequiredEntries(source);
        if (!stale)
        {
            ContentStore.EnsureTrimmedStore(source, required, contentRoot, Gid, force: false, out _);
            return;
        }
        VpkTrimWriter.Write(
            source,
            required.Where(e => !string.Equals(
                e.FullPath, ContentPakSelector.WeaponVDataRelPath, StringComparison.OrdinalIgnoreCase)).ToList(),
            ContentStore.ResolveDirVpk(contentRoot, Gid));
    }

    private static void InWorkDir(Action<string> body)
    {
        var workDir = Path.Combine(Path.GetTempPath(), "stale-store-" + Guid.NewGuid().ToString("N"));
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

    private static int RunExtract(string platform, out string stderr)
    {
        var fake = new FakeWalkerRunner(0, "", ExtractCommandTestShared.CannedWalkerOutput(platform));
        var (code, _, err) = ConsoleCapture.Run(
            () => ExtractCommand.Run(new[] { "--build", BuildId, "--platform", platform }, () => fake));
        stderr = err;
        return code;
    }

    [WindowsOnlyFact]
    public void Stale_Store_Aborts_The_Extract_With_No_Artifacts_And_The_Backfill_Remedy()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBinaries(workDir, platform);
            WriteManifestRecord(workDir, platform);
            WriteStoreCopy(workDir, platform, stale: true);

            int code = RunExtract(platform, out var stderr);

            Assert.NotEqual(0, code);
            Assert.Contains("STALE CONTENT STORE", stderr, StringComparison.Ordinal);
            // Names the GID, both generations, and the remedy.
            Assert.Contains(Gid.ToString(CultureInfo.InvariantCulture), stderr, StringComparison.Ordinal);
            Assert.Contains(
                $"generation {ContentPakSelector.RequiredSetGeneration - 1}", stderr, StringComparison.Ordinal);
            Assert.Contains(
                $"current is {ContentPakSelector.RequiredSetGeneration}", stderr, StringComparison.Ordinal);
            Assert.Contains("content-backfill --pak csgo --execute", stderr, StringComparison.Ordinal);
            Assert.Contains("CONTENT_NOT_SHIPPED_THIS_ERA", stderr, StringComparison.Ordinal);

            // NOTHING was written: no promoted set, no staging survivor, not even a build dir.
            var buildDir = Path.Combine(workDir, "extract-out", BuildId);
            Assert.False(Directory.Exists(Path.Combine(buildDir, platform)));
            if (Directory.Exists(buildDir))
            {
                Assert.Empty(Directory.GetDirectories(buildDir));
                Assert.Empty(Directory.GetFiles(buildDir));
            }
        });
    }

    [WindowsOnlyFact]
    public void Current_Store_Extracts_And_Emits_The_Content_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBinaries(workDir, platform);
            WriteManifestRecord(workDir, platform);
            WriteStoreCopy(workDir, platform, stale: false);

            Assert.Equal(0, RunExtract(platform, out _));

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Assert.True(File.Exists(Path.Combine(setDir, "gameevents.json")));
            Assert.True(File.Exists(Path.Combine(setDir, "weapon_vdata.json")));
        });
    }

    // A CO-LOCATED full pak carries no marker and must never be gated: this is the
    // `content-store migrate` / live-install shape. The manifest-record.json is present (with a GID
    // that has no store copy), so resolution falls through to the co-located pak.
    [WindowsOnlyFact]
    public void CoLocated_Pak_With_No_Marker_Still_Extracts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBinaries(workDir, platform);
            WriteManifestRecord(workDir, platform);
            ContentVpkFixture.Write(
                Path.Combine(TupleDir(workDir, platform), "game", "csgo"), ContentSamples.StandardEntries());

            // Precondition: no store copy at all for this GID, so the guard has nothing store-shaped
            // to gate and the co-located pak is what resolves.
            var contentRoot = ContentStore.RootForTupleDir(TupleDir(workDir, platform))!;
            Assert.False(ContentStore.GidExists(contentRoot, Gid));
            Assert.True(ExtractCommand.TryGuardContentStoreGeneration(
                BuildId, platform, TupleDir(workDir, platform), out _));

            Assert.Equal(0, RunExtract(platform, out _));

            var setDir = Path.Combine(workDir, "extract-out", BuildId, platform);
            Assert.True(File.Exists(Path.Combine(setDir, "gameevents.json")));
            Assert.True(File.Exists(Path.Combine(setDir, "weapon_vdata.json")));
        });
    }

    // No content at all is the documented binaries-only skip — also not gated.
    [Fact]
    public void No_Content_Pak_At_All_Is_Not_Gated()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InWorkDir(workDir =>
        {
            SetupBinaries(workDir, platform);
            WriteManifestRecord(workDir, platform);

            Assert.True(ExtractCommand.TryGuardContentStoreGeneration(
                BuildId, platform, TupleDir(workDir, platform), out var error));
            Assert.Equal("", error);
        });
    }
}
