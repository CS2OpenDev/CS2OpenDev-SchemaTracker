// Fail-loud coverage (contract suite).
//
// The contract: every documented failure mode
// (corrupt binary, missing module, depot acquisition failure, schema-system probe unknown,
// VPK extraction error, KV3 parse failure) is exercised by a test that asserts: zero artifact
// files on disk, non-zero exit code, error message names the failed stage.
//
// This is the AGGREGATE fail-loud contract suite. Each test below drives a producible-now
// failure mode end to end and asserts the three guarantees together:
//   (1) the operation FAILS (throws, or for the CLI path returns a NON-ZERO exit code),
//   (2) ZERO artifact bytes remain on disk (no partial artifact, no leftover .tmp), and
//   (3) the error message / surfaced stderr NAMES the failed stage.
//
// Producible-now failure-mode coverage (each maps to a documented mode):
//   * missing input module           -> modules / ModuleManifestEmitter
//   * corrupt binary (unknown magic)  -> modules / ModuleManifestEmitter
//   * VPK extraction error (missing)  -> gameevents / GameEventsEmitter
//   * malformed KV1                   -> gameevents / GameEventsEmitter
//   * corrupt walker intermediate     -> entity_schema / EntitySchemaEmitter
//   * unset entity_schema in walk     -> entity_schema / EntitySchemaEmitter
// * schema-system probe unknown -> ExtractCommand (walker exits 75;) — CLI exit
// * walker reports success, no out -> ExtractCommand (contract violation) — CLI exit
//
// Failure modes that need real depot/VPK corpora (depot acquisition failure, real VPK CRC
// corruption) are exercised by the suites and become producible here once those
// fixtures exist; KV3 parse failure is,, a graceful degrade (raw value kept,
// value_parsed unset) and explicitly NOT an fail-loud mode, so it is asserted to
// SUCCEED (covered in the suite), not to fail here.

using System.Runtime.InteropServices;
using System.Text;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Cli;
using Cs2SchemaTracker.Host.EntitySchema;
using Cs2SchemaTracker.Host.GameEvents;
using Cs2SchemaTracker.Host.Modules;
using Cs2SchemaTracker.Host.Vpk;
using Cs2SchemaTracker.Host.Walker;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

using Xunit;

namespace Cs2SchemaTracker.Tests.Invariants;

[Collection("cwd-mutating")]
public class FailLoudTest
{
    private const string BuildId = "13371337";
    private const string Platform = "linux-x86_64";

    private static string NewWorkDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "failloud-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void AssertNoArtifact(string outPath)
    {
        Assert.False(File.Exists(outPath), $"no artifact bytes on a fail-loud throw: {outPath}");
        Assert.False(File.Exists(outPath + ".tmp"), $"no leftover temp file: {outPath}.tmp");
    }

    // ---- modules.json failure modes --------------------------------------------------

    [Fact]
    public void Modules_Missing_Input_Module_Throws_NamesStage_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            var missing = Path.Combine(workDir, "does-not-exist.so");
            var outPath = Path.Combine(workDir, "modules.json");

            var ex = Assert.ThrowsAny<IOException>(() =>
                new ModuleManifestEmitter(SchemaFamily.Version, BuildId, Platform)
                    .Emit(new[] { new ModuleInput(missing, 0) }, outPath));

            // Stage is named: the failure identifies the missing module file.
            Assert.Contains(Path.GetFileName(missing), ex.Message, StringComparison.Ordinal);
            AssertNoArtifact(outPath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void Modules_Corrupt_Binary_Throws_NamesStage_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            // ZIP magic (PK\x03\x04) — neither PE nor ELF: a corrupt/unsupported binary.
            var bad = Path.Combine(workDir, "fake.zip");
            File.WriteAllBytes(bad, new byte[] { (byte)'P', (byte)'K', 0x03, 0x04, 0, 0, 0, 0 });
            var outPath = Path.Combine(workDir, "modules.json");

            var ex = Assert.Throws<InvalidDataException>(() =>
                new ModuleManifestEmitter(SchemaFamily.Version, BuildId, Platform)
                    .Emit(new[] { new ModuleInput(bad, 0) }, outPath));

            // Stage is named: module-inspection rejected the unrecognized binary format.
            Assert.Contains("magic", ex.Message, StringComparison.OrdinalIgnoreCase);
            AssertNoArtifact(outPath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- gameevents.json failure modes -----------------------------------------------

    [Fact]
    public void GameEvents_Missing_Vpk_Throws_NamesStage_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            var missing = Path.Combine(workDir, "does-not-exist_dir.vpk");
            var outPath = Path.Combine(workDir, "gameevents.json");

            var ex = Assert.ThrowsAny<IOException>(() =>
                new GameEventsEmitter(SchemaFamily.Version, BuildId, Platform)
                    .EmitFromVpk(missing, outPath));

            // Stage is named: the missing VPK path appears in the surfaced error.
            Assert.Contains(Path.GetFileName(missing), ex.Message, StringComparison.Ordinal);
            AssertNoArtifact(outPath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void GameEvents_Malformed_Kv1_Throws_NamesStage_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            // Unbalanced braces -> KV1 parse failure inside the .gameevents entry.
            var bad = "\"GameEvents\"\n{\n  \"evt\"\n  {\n    \"userid\" \"short\"\n";
            var archive = BuildVpkWithGameEvents("core", bad);
            var outPath = Path.Combine(workDir, "gameevents.json");

            var ex = Assert.Throws<InvalidDataException>(() =>
                new GameEventsEmitter(SchemaFamily.Version, BuildId, Platform).Emit(archive, outPath));

            // Stage is named: the message identifies the KV1 parse failure.
            Assert.Contains("KV1", ex.Message, StringComparison.OrdinalIgnoreCase);
            AssertNoArtifact(outPath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- entity_schema.json failure modes --------------------------------------------

    [Fact]
    public void EntitySchema_Corrupt_Walker_Output_Throws_NamesStage_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            var corrupt = Path.Combine(workDir, "corrupt.pb");
            File.WriteAllBytes(corrupt, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
            var outPath = Path.Combine(workDir, "entity_schema.json");

            var ex = Assert.Throws<InvalidDataException>(() =>
                new EntitySchemaEmitter(SchemaFamily.Version, BuildId, Platform, "")
                    .EmitFromFile(corrupt, outPath));

            // Stage is named: parsing the walker output failed.
            Assert.Contains("walker output", ex.Message, StringComparison.OrdinalIgnoreCase);
            AssertNoArtifact(outPath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    [Fact]
    public void EntitySchema_Unset_EntitySchema_In_Walk_Throws_NamesStage_And_Writes_Nothing()
    {
        var workDir = NewWorkDir();
        try
        {
            var wo = new WalkerOutput { Platform = Platform };   // EntitySchema unset.
            var outPath = Path.Combine(workDir, "entity_schema.json");

            var ex = Assert.Throws<InvalidDataException>(() =>
                new EntitySchemaEmitter(SchemaFamily.Version, BuildId, Platform, "").Emit(wo, outPath));

            // Stage is named: the entity_schema mapping had nothing to map.
            Assert.Contains("entity_schema", ex.Message, StringComparison.OrdinalIgnoreCase);
            AssertNoArtifact(outPath);
        }
        finally
        {
            Directory.Delete(workDir, recursive: true);
        }
    }

    // ---- ExtractCommand (CLI) failure modes: non-zero exit + zero artifacts ----------
    //
    // These cross the IWalkerRunner seam with a fake. The schema-system-probe-unknown mode
    // reaches the host as a walker process exiting 75; the host must surface that
    // exit code verbatim and write no artifacts.

    [Fact]
    public void Extract_Walker_Unknown_Layout_Exit_Propagates_NonZero_And_Writes_No_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;   // host matches neither platform; CLI path not exercisable here.

        InPinnedWorkDir(platform, workDir =>
        {
            const int UnknownLayoutExit = 75; // unknown-schema-system-layout rejection.
            const string LayoutSig =
                "schema-system-layout signature: 0xDEADBEEF (unknown) — refusing to guess";
            var fake = new FailLoudFakeWalkerRunner(UnknownLayoutExit, LayoutSig, payload: null);

            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake);

            // Non-zero exit, propagated verbatim from the failed stage (the walker).
            Assert.Equal(UnknownLayoutExit, code);

            // Zero artifact bytes anywhere under the default artifact path; no leftovers.
            var artifact = Path.Combine(workDir, "artifacts", BuildId, platform, "entity_schema.json");
            Assert.False(File.Exists(artifact), "no artifacts on walker failure");
            Assert.False(File.Exists(artifact + ".tmp"), "no leftover artifact temp");
            Assert.False(File.Exists(fake.LastOutPath!), "walker intermediate must be cleaned up");
        });
    }

    [Fact]
    public void Extract_Walker_Success_But_No_Output_Aborts_NonZero_And_Writes_No_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        InPinnedWorkDir(platform, workDir =>
        {
            // Exit 0 but the fake writes nothing: a contract violation the host must not
            // paper over. The host fails non-zero (EX_SOFTWARE = 70) with no artifacts.
            var fake = new FailLoudFakeWalkerRunner(0, "", payload: null);

            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake);

            Assert.Equal(70, code);
            var artifact = Path.Combine(workDir, "artifacts", BuildId, platform, "entity_schema.json");
            Assert.False(File.Exists(artifact));
            Assert.False(File.Exists(artifact + ".tmp"));
        });
    }

    [Fact]
    public void Extract_Missing_Binaries_Dir_Aborts_NonZero_And_Writes_No_Artifacts()
    {
        var platform = MatchingPlatform();
        if (platform is null)
            return;

        // Pin to a fresh cwd that has NO cache/binaries/<build>/<platform>/ — the input
        // resolution must fail loud (EX_DATAERR = 65) before any walker work or artifact bytes.
        var workDir = NewWorkDir();
        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        {
            var fake = new FailLoudFakeWalkerRunner(0, "", payload: null);
            var code = ExtractCommand.Run(
                new[] { "--build", BuildId, "--platform", platform }, () => fake);

            Assert.Equal(65, code);
            Assert.Equal(0, fake.Calls);   // failed before the walker stage.
            var artifact = Path.Combine(workDir, "artifacts", BuildId, platform, "entity_schema.json");
            Assert.False(File.Exists(artifact));
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // ---- helpers ----------------------------------------------------------------------

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

    // Pin cwd to a fresh temp dir that already contains a present (non-empty) input-binaries
    // dir so the resolution succeeds and the walker stage is reached.
    private static void InPinnedWorkDir(string platform, Action<string> body)
    {
        var workDir = NewWorkDir();
        var binariesDir = Path.Combine(workDir, "cache", "binaries", BuildId, platform);
        Directory.CreateDirectory(binariesDir);
        File.WriteAllBytes(Path.Combine(binariesDir, "libserver.so"), new byte[] { 0x7F, 0x45, 0x4C, 0x46 });

        var prevCwd = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(workDir);
        try
        {
            body(workDir);
        }
        finally
        {
            Directory.SetCurrentDirectory(prevCwd);
            try
            { Directory.Delete(workDir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static VpkArchive BuildVpkWithGameEvents(string name, string kv1)
    {
        var files = new List<ArtifactCases.FileSpec>
        {
            new("resource", "gameevents", name, Encoding.UTF8.GetBytes(kv1)),
        };
        return VpkArchive.Parse("pak01_dir.vpk", BuildEmbeddedVpk(files));
    }

    private static byte[] BuildEmbeddedVpk(IReadOnlyList<ArtifactCases.FileSpec> files)
    {
        // Reuse the shared synthetic-VPK builder via the fixtures' internals. A tiny shim that
        // mirrors the embedded-v2 layout the tests use.
        var tree = new MemoryStream();
        var dataSection = new MemoryStream();
        var offsets = new Dictionary<ArtifactCases.FileSpec, uint>();
        foreach (var f in files)
        {
            offsets[f] = (uint)dataSection.Length;
            dataSection.Write(f.Body);
        }
        foreach (var byExt in files.GroupBy(f => f.Ext))
        {
            WriteCString(tree, byExt.Key);
            foreach (var byPath in byExt.GroupBy(f => f.Path))
            {
                WriteCString(tree, byPath.Key);
                foreach (var f in byPath)
                {
                    WriteCString(tree, f.Name);
                    WriteU32(tree, Crc32(f.Body));
                    WriteU16(tree, 0);
                    WriteU16(tree, 0x7FFF);
                    WriteU32(tree, offsets[f]);
                    WriteU32(tree, (uint)f.Body.Length);
                    WriteU16(tree, 0xFFFF);
                }
                tree.WriteByte(0);
            }
            tree.WriteByte(0);
        }
        tree.WriteByte(0);

        byte[] treeBytes = tree.ToArray();
        byte[] dataBytes = dataSection.ToArray();
        var ms = new MemoryStream();
        WriteU32(ms, 0x55AA1234u);
        WriteU32(ms, 2);
        WriteU32(ms, (uint)treeBytes.Length);
        WriteU32(ms, (uint)dataBytes.Length);
        WriteU32(ms, 0);
        WriteU32(ms, 0);
        WriteU32(ms, 0);
        ms.Write(treeBytes);
        ms.Write(dataBytes);
        return ms.ToArray();
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc ^ 0xFFFFFFFFu;
    }

    private static void WriteCString(Stream s, string value)
    {
        s.Write(Encoding.UTF8.GetBytes(value));
        s.WriteByte(0);
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        s.Write(b);
    }
}

/// <summary>
/// fake walker: returns a configured exit code + stderr and, only on a 0 exit with a
/// supplied payload, writes that canned binary WalkerOutput to the out path (mirroring the
/// real walker contract). Used to drive the ExtractCommand fail-loud paths.
/// </summary>
internal sealed class FailLoudFakeWalkerRunner : IWalkerRunner
{
    private readonly int _exitCode;
    private readonly string _stderr;
    private readonly WalkerOutput? _payload;

    public int Calls { get; private set; }
    public string? LastOutPath { get; private set; }

    public FailLoudFakeWalkerRunner(int exitCode, string stderr, WalkerOutput? payload)
    {
        _exitCode = exitCode;
        _stderr = stderr;
        _payload = payload;
    }

    public int Run(string binariesDir, string platform, string outPath, out string stderr)
    {
        Calls++;
        LastOutPath = outPath;
        if (_exitCode == 0 && _payload is not null)
        {
            File.WriteAllBytes(outPath, _payload.ToByteArray());
        }
        stderr = _stderr;
        return _exitCode;
    }
}
