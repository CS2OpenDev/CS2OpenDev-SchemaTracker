// End-to-end emitter tests.
//
// Builds a small set of fixture binaries (mixed PE + ELF), runs the emitter,
// then asserts:
//   * file exists, valid JSON, sorted by path (Ordinal)
//   * sha256 is independently reproducible via SHA256.HashData
// * two runs produce byte-identical output
//   * file_size matches FileInfo.Length

using System.Security.Cryptography;
using System.Text.Json;

using Cs2SchemaTracker.Host.Modules;

namespace Cs2SchemaTracker.Tests.Modules;

public class ModuleManifestEmitterTest
{
    [Xunit.Fact]
    public void Emits_Sorted_Entries_With_Correct_Sha_And_Size()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "emit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            // Three fixtures with intentionally non-alphabetical input order.
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            var pathZ = Path.Combine(binDir, "z.dll");
            var pathA = Path.Combine(binDir, "a.so");
            var pathM = Path.Combine(binDir, "m.dll");

            File.WriteAllBytes(pathZ, BuildPe());
            File.WriteAllBytes(pathA, BuildElf());
            File.WriteAllBytes(pathM, BuildPe());

            var inputs = new[]
            {
                new ModuleInput(pathZ, 17),
                new ModuleInput(pathA,  3),
                new ModuleInput(pathM,  5),
            };

            var outPath = Path.Combine(workDir, "modules.json");
            var emitter = new ModuleManifestEmitter("0.1.0", "12345", "linux-x86_64");
            emitter.Emit(inputs, outPath);

            Xunit.Assert.True(File.Exists(outPath), $"expected output at {outPath}");
            var bytes = File.ReadAllBytes(outPath);
            // No BOM, no CRLF.
            Xunit.Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "modules.json must not have a UTF-8 BOM");
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Xunit.Assert.DoesNotContain("\r", text);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            Xunit.Assert.Equal("12345", root.GetProperty("buildId").GetString());
            Xunit.Assert.Equal("0.1.0", root.GetProperty("schemaVersion").GetString());
            Xunit.Assert.Equal("linux-x86_64", root.GetProperty("platform").GetString());

            var mods = root.GetProperty("modules");
            Xunit.Assert.Equal(3, mods.GetArrayLength());

            // Sorted by path Ordinal: a.so < m.dll < z.dll (Ordinal: 'a' < 'm' < 'z').
            Xunit.Assert.Equal(pathA, mods[0].GetProperty("path").GetString());
            Xunit.Assert.Equal(pathM, mods[1].GetProperty("path").GetString());
            Xunit.Assert.Equal(pathZ, mods[2].GetProperty("path").GetString());

            // Spot-check the SHA-256 for each entry against independent computation.
            foreach (var entry in mods.EnumerateArray())
            {
                var path = entry.GetProperty("path").GetString()!;
                var expectedSha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                Xunit.Assert.Equal(expectedSha, entry.GetProperty("sha256").GetString());

                var expectedSize = new FileInfo(path).Length;
                // proto3 uint64 in JSON: emitted as a JSON number here (POCO); accept either.
                var fileSizeProp = entry.GetProperty("fileSize");
                long actualSize = fileSizeProp.ValueKind == JsonValueKind.String
                    ? long.Parse(fileSizeProp.GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                    : fileSizeProp.GetInt64();
                Xunit.Assert.Equal(expectedSize, actualSize);
            }

            // Registration counts attributed correctly per-input.
            Xunit.Assert.Equal(3u, mods[0].GetProperty("schemaRegistrationCount").GetUInt32());  // a.so → 3
            Xunit.Assert.Equal(5u, mods[1].GetProperty("schemaRegistrationCount").GetUInt32());  // m.dll → 5
            Xunit.Assert.Equal(17u, mods[2].GetProperty("schemaRegistrationCount").GetUInt32());  // z.dll → 17
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Two_Runs_Produce_Byte_Identical_Output()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "det-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            var p1 = Path.Combine(binDir, "one.dll");
            var p2 = Path.Combine(binDir, "two.so");
            File.WriteAllBytes(p1, BuildPe());
            File.WriteAllBytes(p2, BuildElf());

            var inputs = new[] { new ModuleInput(p1, 1), new ModuleInput(p2, 2) };

            var outA = Path.Combine(workDir, "a.json");
            var outB = Path.Combine(workDir, "b.json");

            new ModuleManifestEmitter("0.1.0", "build", "linux-x86_64").Emit(inputs, outA);
            new ModuleManifestEmitter("0.1.0", "build", "linux-x86_64").Emit(inputs, outB);

            Xunit.Assert.Equal(File.ReadAllBytes(outA), File.ReadAllBytes(outB));
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Json_Keys_Are_Sorted_Alphabetically_Per_Object()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);
            var p = Path.Combine(binDir, "one.dll");
            File.WriteAllBytes(p, BuildPe());

            var outPath = Path.Combine(workDir, "modules.json");
            new ModuleManifestEmitter("0.1.0", "build", "linux-x86_64").Emit(new[] { new ModuleInput(p, 0) }, outPath);

            var text = File.ReadAllText(outPath);

            // Top-level keys appear in alphabetical order: buildId, modules, platform, schemaVersion.
            var iBuild = text.IndexOf("\"buildId\"", StringComparison.Ordinal);
            var iMod = text.IndexOf("\"modules\"", StringComparison.Ordinal);
            var iPlatform = text.IndexOf("\"platform\"", StringComparison.Ordinal);
            var iSchema = text.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
            Xunit.Assert.True(iBuild < iMod, "buildId must precede modules");
            Xunit.Assert.True(iMod < iPlatform, "modules must precede platform");
            Xunit.Assert.True(iPlatform < iSchema, "platform must precede schemaVersion");

            // Per-module keys: exportCount < fileSize < path < schemaRegistrationCount < sha256.
            var iExport = text.IndexOf("\"exportCount\"", StringComparison.Ordinal);
            var iFile = text.IndexOf("\"fileSize\"", StringComparison.Ordinal);
            var iPath = text.IndexOf("\"path\"", StringComparison.Ordinal);
            var iReg = text.IndexOf("\"schemaRegistrationCount\"", StringComparison.Ordinal);
            var iSha = text.IndexOf("\"sha256\"", StringComparison.Ordinal);
            Xunit.Assert.True(iExport < iFile);
            Xunit.Assert.True(iFile < iPath);
            Xunit.Assert.True(iPath < iReg);
            Xunit.Assert.True(iReg < iSha);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Empty_Input_List_Emits_Empty_Modules_Array()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var outPath = Path.Combine(workDir, "modules.json");
            new ModuleManifestEmitter("0.1.0", "build", "linux-x86_64").Emit(Array.Empty<ModuleInput>(), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Xunit.Assert.Equal(0, doc.RootElement.GetProperty("modules").GetArrayLength());
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    // Reuse the PE/ELF fixture builders via internal access (visible to tests
    // through InternalsVisibleTo on the host project... but these are in the
    // test assembly itself). To avoid duplicating ~200 lines of fixture-builder
    // code across three test files, we re-import the helpers via reflection
    // friend.
    private static byte[] BuildPe()
    {
        // Borrow from PortableExecutableInspectorTest via reflection — keeps the
        // canonical builder in one place. The method is private/static; we use
        // the BindingFlags overload to reach it without changing visibility.
        var mi = typeof(PortableExecutableInspectorTest).GetMethod(
            "BuildMinimalPe",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (byte[])mi.Invoke(null, new object[] { 1, false })!;
    }

    private static byte[] BuildElf()
    {
        var mi = typeof(ElfInspectorTest).GetMethod(
            "BuildElf64",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        // Single global function symbol so the manifest sees export_count = 1.
        var symBuilder = typeof(ElfInspectorTest).GetMethod(
            "BuildSym",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sym = (byte[])symBuilder.Invoke(null, new object[] { (byte)1, (byte)2, (ushort)1 })!;
        return (byte[])mi.Invoke(null, new object[] { new[] { sym }, true })!;
    }
}
