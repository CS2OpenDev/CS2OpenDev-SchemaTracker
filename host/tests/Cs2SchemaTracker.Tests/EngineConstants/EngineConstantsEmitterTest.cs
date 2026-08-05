// EngineConstantsEmitter unit tests (synthetic WalkerOutput).
//

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.EngineConstants;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.EngineConstants;

public class EngineConstantsEmitterTest
{
    private const string Platform = "linux-x86_64";

    private static WalkerOutput WalkWith(params EngineConstant[] constants)
    {
        var walk = new EngineConstantsWalk();
        walk.Constants.AddRange(constants);
        return new WalkerOutput { Platform = Platform, EngineConstants = walk };
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "engineconst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Lifts_Faithfully_Sorts_By_Name_And_Carries_Both_Value_Kinds()
    {
        var dir = NewDir();
        try
        {
            // Out-of-order (S before M) so the sort is exercised; one int, one string value.
            // Source uses the REAL walker form "schema_enum:<module>/<EnumName>"
            // (engine_constants_walk.cpp is the only producer of engine-constant sources).
            var walk = WalkWith(
                new EngineConstant { Name = "SOURCE_ENGINE_NAME", Source = "schema_enum:engine2.dll/SourceEngineBuild", StringValue = "Source2" },
                new EngineConstant { Name = "MAX_PLAYERS", Source = "schema_enum:server.dll/CGameRules", IntValue = 64 });

            var outPath = Path.Combine(dir, "engine_constants.json");
            new EngineConstantsEmitter(SchemaFamily.Version, "b1", Platform).Emit(walk, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.Equal(SchemaFamily.Version, doc.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("b1", doc.RootElement.GetProperty("buildId").GetString());
            Assert.Equal(Platform, doc.RootElement.GetProperty("platform").GetString());

            var constants = doc.RootElement.GetProperty("constants");
            Assert.Equal(2, constants.GetArrayLength());

            // Sorted by name: MAX_PLAYERS before SOURCE_ENGINE_NAME.
            Assert.Equal("MAX_PLAYERS", constants[0].GetProperty("name").GetString());
            Assert.Equal("schema_enum:server.dll/CGameRules", constants[0].GetProperty("source").GetString());
            // proto3 JSON maps int64 to a string.
            Assert.Equal("64", constants[0].GetProperty("intValue").GetString());

            Assert.Equal("SOURCE_ENGINE_NAME", constants[1].GetProperty("name").GetString());
            Assert.Equal("Source2", constants[1].GetProperty("stringValue").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewDir();
        try
        {
            var walk = WalkWith(
                new EngineConstant { Name = "B", Source = "s", IntValue = 2 },
                new EngineConstant { Name = "A", Source = "s", StringValue = "x" });

            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            new EngineConstantsEmitter(SchemaFamily.Version, "b", Platform).Emit(walk, pa);
            new EngineConstantsEmitter(SchemaFamily.Version, "b", Platform).Emit(walk, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Walk()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "engine_constants.json");
            Assert.Throws<InvalidDataException>(() =>
                new EngineConstantsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(new WalkerOutput { Platform = Platform }, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Name_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "engine_constants.json");
            Assert.Throws<InvalidDataException>(() =>
                new EngineConstantsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(new EngineConstant { Name = "", Source = "s", IntValue = 1 }), outPath));
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Source_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "engine_constants.json");
            Assert.Throws<InvalidDataException>(() =>
                new EngineConstantsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(new EngineConstant { Name = "X", Source = "", IntValue = 1 }), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Unset_Value_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "engine_constants.json");
            // No int_value and no string_value set — the oneof is None.
            Assert.Throws<InvalidDataException>(() =>
                new EngineConstantsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(new EngineConstant { Name = "X", Source = "s" }), outPath));
            Assert.False(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
