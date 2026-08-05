// ProvenanceEmitter unit tests (synthetic input binaries + synthetic Steam identity).
//
// Asserts: every field is populated from the supplied context (tool semver+git SHA, Steam
// identity, CS2 build identity, per-input sha256/size/mtime), inputs+depots are sorted,
// uint64 file_size uses the proto3 string mapping, two runs are byte-identical, and a missing
// input binary fails loud with no output bytes.

using System.Security.Cryptography;
using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Provenance;

using Xunit;

namespace Cs2SchemaTracker.Tests.Provenance;

public class ProvenanceEmitterTest
{
    private const string Platform = "linux-x86_64";

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "prov-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static byte[] BuildElf()
    {
        var build = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildElf64", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var symB = typeof(Cs2SchemaTracker.Tests.Modules.ElfInspectorTest).GetMethod(
            "BuildSym", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sym = (byte[])symB.Invoke(null, new object[] { (byte)1, (byte)2, (ushort)1 })!;
        return (byte[])build.Invoke(null, new object[] { new[] { sym }, true })!;
    }

    private static ProvenanceContext ContextWith(params ProvenanceInput[] inputs) => new()
    {
        SchemaVersion = SchemaFamily.Version,
        BuildId = "13371337",
        Platform = Platform,
        GitCommit = "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
        AppId = 730,
        ManifestCreatedUtc = "2026-06-10T12:00:00Z",
        SchemaRevision = "sig-rev",
        Depots = new[]
        {
            new ProvenanceDepot(2347773, "8287382081622299196"),
            new ProvenanceDepot(2347770, "5146470907583764090"),
        },
        Inputs = inputs,
    };

    [Fact]
    public void Populates_All_Fields_From_Context()
    {
        var dir = NewDir();
        try
        {
            var elf = Path.Combine(dir, "libserver.so");
            var bytes = BuildElf();
            File.WriteAllBytes(elf, bytes);

            var outPath = Path.Combine(dir, "provenance.json");
            ProvenanceEmitter.Emit(ContextWith(new ProvenanceInput("libserver.so", elf, "2026-06-10T12:00:00Z")), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = doc.RootElement;
            Assert.Equal(SchemaFamily.Version, root.GetProperty("schemaVersion").GetString());
            Assert.Equal("13371337", root.GetProperty("buildId").GetString());
            Assert.Equal(Platform, root.GetProperty("platform").GetString());

            // tool version: semver + git SHA.
            var tool = root.GetProperty("tool");
            Assert.Equal(SchemaFamily.Version, tool.GetProperty("semver").GetString());
            Assert.Equal("deadbeefdeadbeefdeadbeefdeadbeefdeadbeef", tool.GetProperty("gitCommit").GetString());

            // steam: appId, build, manifest time, depots (proto3 uint32 appId is a number).
            var steam = root.GetProperty("steam");
            Assert.Equal(730u, steam.GetProperty("appId").GetUInt32());
            Assert.Equal("13371337", steam.GetProperty("steamBuildId").GetString());
            Assert.Equal("2026-06-10T12:00:00Z", steam.GetProperty("manifestCreatedUtc").GetString());

            // cs2_build: schema revision from the walk; built_from_cl empty (content-depot-only).
            var cs2 = root.GetProperty("cs2Build");
            Assert.Equal("sig-rev", cs2.GetProperty("schemaRevision").GetString());
            Assert.Equal("13371337", cs2.GetProperty("steamBuildId").GetString());
            Assert.Equal("", cs2.GetProperty("builtFromCl").GetString());

            // inputs: sha256 reproducible, file_size is the proto3 STRING mapping, mtime carried.
            var inputs = root.GetProperty("inputs");
            Assert.Equal(1, inputs.GetArrayLength());
            var input0 = inputs[0];
            Assert.Equal("libserver.so", input0.GetProperty("path").GetString());
            var expectedSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            Assert.Equal(expectedSha, input0.GetProperty("sha256").GetString());
            // proto3 uint64 -> JSON string.
            Assert.Equal(JsonValueKind.String, input0.GetProperty("fileSize").ValueKind);
            Assert.Equal(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                input0.GetProperty("fileSize").GetString());
            Assert.Equal("2026-06-10T12:00:00Z", input0.GetProperty("mtimeUtc").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Sorts_Depots_By_Id_And_Inputs_By_Path()
    {
        var dir = NewDir();
        try
        {
            var z = Path.Combine(dir, "z.so");
            var a = Path.Combine(dir, "a.so");
            File.WriteAllBytes(z, BuildElf());
            File.WriteAllBytes(a, BuildElf());

            var outPath = Path.Combine(dir, "provenance.json");
            ProvenanceEmitter.Emit(
                ContextWith(
                    new ProvenanceInput("z.so", z, ""),
                    new ProvenanceInput("a.so", a, "")),
                outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var root = doc.RootElement;

            var depots = root.GetProperty("steam").GetProperty("depots");
            Assert.Equal(2347770u, depots[0].GetProperty("depotId").GetUInt32());
            Assert.Equal(2347773u, depots[1].GetProperty("depotId").GetUInt32());

            var inputs = root.GetProperty("inputs");
            Assert.Equal("a.so", inputs[0].GetProperty("path").GetString());
            Assert.Equal("z.so", inputs[1].GetProperty("path").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewDir();
        try
        {
            var elf = Path.Combine(dir, "libserver.so");
            File.WriteAllBytes(elf, BuildElf());
            var ctx = ContextWith(new ProvenanceInput("libserver.so", elf, "2026-06-10T12:00:00Z"));

            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            ProvenanceEmitter.Emit(ctx, pa);
            ProvenanceEmitter.Emit(ctx, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Input_Binary_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "provenance.json");
            var missing = Path.Combine(dir, "does-not-exist.so");
            Assert.Throws<FileNotFoundException>(() =>
                ProvenanceEmitter.Emit(ContextWith(new ProvenanceInput("does-not-exist.so", missing, "")), outPath));
            Assert.False(File.Exists(outPath), "no bytes on fail-loud");
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
