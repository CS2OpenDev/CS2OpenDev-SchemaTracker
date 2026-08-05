// fail-loud tests.

using Cs2SchemaTracker.Host.Modules;

namespace Cs2SchemaTracker.Tests.Modules;

public class FailLoudTest
{
    [Xunit.Fact]
    public void Missing_Input_Path_Throws_And_Writes_Nothing()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "fl-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var missing = Path.Combine(workDir, "does-not-exist.so");
            var outPath = Path.Combine(workDir, "modules.json");

            var emitter = new ModuleManifestEmitter("0.1.0", "b", "linux-x86_64");
            Xunit.Assert.Throws<FileNotFoundException>(() =>
                emitter.Emit(new[] { new ModuleInput(missing, 0) }, outPath));

            Xunit.Assert.False(File.Exists(outPath), "modules.json must not exist after a fail-loud throw");
            Xunit.Assert.False(File.Exists(outPath + ".tmp"), "no leftover .tmp file should remain");
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
    public void Unknown_Magic_Throws_Invalid_Data_Exception()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "fl-magic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Write a ZIP-magic file (PK\x03\x04) — not PE, not ELF.
            var zipPath = Path.Combine(binDir, "fake.zip");
            File.WriteAllBytes(zipPath, new byte[] { (byte)'P', (byte)'K', 0x03, 0x04, 0, 0, 0, 0 });

            var outPath = Path.Combine(workDir, "modules.json");
            var emitter = new ModuleManifestEmitter("0.1.0", "b", "linux-x86_64");
            var ex = Xunit.Assert.Throws<InvalidDataException>(() =>
                emitter.Emit(new[] { new ModuleInput(zipPath, 0) }, outPath));
            Xunit.Assert.Contains("unrecognized magic", ex.Message, StringComparison.Ordinal);

            Xunit.Assert.False(File.Exists(outPath));
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
    public void Pre_Existing_Output_Is_Preserved_When_Inspection_Throws()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "fl-preserve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var outPath = Path.Combine(workDir, "modules.json");
            // Seed an existing file. If the emitter throws mid-pipeline before
            // any rename, the existing file must still be intact.
            var existing = "previously-good-content";
            File.WriteAllText(outPath, existing);

            var emitter = new ModuleManifestEmitter("0.1.0", "b", "linux-x86_64");
            var missing = Path.Combine(workDir, "missing.dll");
            Xunit.Assert.Throws<FileNotFoundException>(() =>
                emitter.Emit(new[] { new ModuleInput(missing, 0) }, outPath));

            Xunit.Assert.Equal(existing, File.ReadAllText(outPath));
            Xunit.Assert.False(File.Exists(outPath + ".tmp"));
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
    public void Duplicate_Input_Path_Throws()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "fl-dupe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var binDir = Path.Combine(workDir, "binaries");
            Directory.CreateDirectory(binDir);

            // Write the smallest valid PE we can — just need a real magic so the
            // inspector doesn't throw on the FIRST entry before we get to the
            // duplicate check.
            var p = Path.Combine(binDir, "lib.dll");
            // Use the BuildPe helper from ModuleManifestEmitterTest via the
            // shared canonical builders in PortableExecutableInspectorTest.
            var pe = (byte[])typeof(PortableExecutableInspectorTest)
                .GetMethod("BuildMinimalPe",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { 1, false })!;
            File.WriteAllBytes(p, pe);

            var outPath = Path.Combine(workDir, "modules.json");
            var emitter = new ModuleManifestEmitter("0.1.0", "b", "linux-x86_64");
            Xunit.Assert.Throws<InvalidDataException>(() =>
                emitter.Emit(new[] { new ModuleInput(p, 0), new ModuleInput(p, 1) }, outPath));

            Xunit.Assert.False(File.Exists(outPath));
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
}
