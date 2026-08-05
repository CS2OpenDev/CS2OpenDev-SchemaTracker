// resolved_interfaces merge into modules.json.
//
// Drives the walker->host merge against a SYNTHETIC WalkerOutput.ModulesWalk (the walker may
// not yet populate this at runtime): asserts that ResolvedInterfacesByModuleKey joins onto each
// Module row by module identity (the SAME NormalizeKey identity the registration-count merge uses),
// that the emitter sorts + de-duplicates the interfaces, that a binary with no walk entry
// gets an empty repeated field, and that '!'-pseudo-modules are dropped.

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Modules;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.Modules;

public class ModuleResolvedInterfacesMergeTest
{
    private static readonly string[] ExpectedClientInterfaces = { "Source2Client001", "Source2Client002" };
    private static readonly string[] ExpectedServerInterfaces = { "Source2Server001" };

    private static byte[] BuildPe()
    {
        var mi = typeof(PortableExecutableInspectorTest).GetMethod(
            "BuildMinimalPe",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (byte[])mi.Invoke(null, new object[] { 1, false })!;
    }

    private static byte[] BuildElf()
    {
        var build = typeof(ElfInspectorTest).GetMethod(
            "BuildElf64",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var symBuilder = typeof(ElfInspectorTest).GetMethod(
            "BuildSym",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var sym = (byte[])symBuilder.Invoke(null, new object[] { (byte)1, (byte)2, (ushort)1 })!;
        return (byte[])build.Invoke(null, new object[] { new[] { sym }, true })!;
    }

    [Fact]
    public void Merges_ResolvedInterfaces_By_Module_Identity_Sorted_And_Deduped()
    {
        var work = Path.Combine(Path.GetTempPath(), "merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var binDir = Path.Combine(work, "binaries");
            Directory.CreateDirectory(binDir);
            var client = Path.Combine(binDir, "client.dll");      // identity "client"
            var server = Path.Combine(binDir, "libserver.so");    // identity "server" (lib + .so stripped)
            var tier0 = Path.Combine(binDir, "tier0.dll");       // no walk entry -> empty interfaces
            File.WriteAllBytes(client, BuildPe());
            File.WriteAllBytes(server, BuildElf());
            File.WriteAllBytes(tier0, BuildPe());

            // Synthetic ModulesWalk: keys use varied identity forms; deliberately unsorted +
            // duplicated to exercise the emitter's sort + dedup. "!GlobalTypes" must be dropped.
            var walk = new ModulesWalk();
            var clientMi = new ModuleInterfaces { Module = "client" };
            clientMi.ResolvedInterfaces.Add("Source2Client002");
            clientMi.ResolvedInterfaces.Add("Source2Client001");
            clientMi.ResolvedInterfaces.Add("Source2Client002");   // duplicate
            walk.Modules.Add(clientMi);

            var serverMi = new ModuleInterfaces { Module = "server" };   // bare; joins libserver.so
            serverMi.ResolvedInterfaces.Add("Source2Server001");
            walk.Modules.Add(serverMi);

            var pseudo = new ModuleInterfaces { Module = "!GlobalTypes" };
            pseudo.ResolvedInterfaces.Add("ShouldBeDropped001");
            walk.Modules.Add(pseudo);

            var resolved = SchemaRegistrationCounter.ResolvedInterfacesByModuleKey(walk);

            var inputs = new[] { client, server, tier0 }
                .Select(p => new ModuleInput(
                    Path: p,
                    RegistrationCount: 0,
                    RecordedPath: Path.GetFileName(p),
                    ResolvedInterfaces: SchemaRegistrationCounter.ResolvedInterfacesForBinaryFileName(
                        resolved, Path.GetFileName(p))))
                .ToList();

            var outPath = Path.Combine(work, "modules.json");
            new ModuleManifestEmitter(SchemaFamily.Version, "build", "windows-x86_64").Emit(inputs, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var mods = doc.RootElement.GetProperty("modules");

            // Sorted by recorded path Ordinal: client.dll, libserver.so, tier0.dll.
            var byPath = mods.EnumerateArray().ToDictionary(
                m => m.GetProperty("path").GetString()!,
                m => m);

            var clientIf = byPath["client.dll"].GetProperty("resolvedInterfaces")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            // Sorted Ordinal + de-duplicated.
            Assert.Equal(ExpectedClientInterfaces, clientIf);

            var serverIf = byPath["libserver.so"].GetProperty("resolvedInterfaces")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(ExpectedServerInterfaces, serverIf);

            // tier0 has no walk entry -> empty repeated field (present, empty).
            Assert.Equal(0, byPath["tier0.dll"].GetProperty("resolvedInterfaces").GetArrayLength());

            // The pseudo-module's interface never leaks onto any row.
            foreach (var m in mods.EnumerateArray())
            {
                foreach (var e in m.GetProperty("resolvedInterfaces").EnumerateArray())
                {
                    Assert.NotEqual("ShouldBeDropped001", e.GetString());
                }
            }
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public void Absent_ModulesWalk_Yields_Empty_Interfaces()
    {
        var work = Path.Combine(Path.GetTempPath(), "none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var p = Path.Combine(work, "client.dll");
            File.WriteAllBytes(p, BuildPe());

            var resolved = SchemaRegistrationCounter.ResolvedInterfacesByModuleKey(null);
            var ifaces = SchemaRegistrationCounter.ResolvedInterfacesForBinaryFileName(resolved, "client.dll");
            Assert.Empty(ifaces);

            var outPath = Path.Combine(work, "modules.json");
            new ModuleManifestEmitter(SchemaFamily.Version, "build", "windows-x86_64")
                .Emit(new[] { new ModuleInput(p, 0, RecordedPath: "client.dll", ResolvedInterfaces: ifaces) }, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.Equal(0, doc.RootElement.GetProperty("modules")[0]
                .GetProperty("resolvedInterfaces").GetArrayLength());
        }
        finally { Directory.Delete(work, recursive: true); }
    }
}
