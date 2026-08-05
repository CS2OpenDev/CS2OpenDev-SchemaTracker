// demo_messages — DemoMessagesEmitter unit tests.
//

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.NetworkMessages;

using Xunit;

namespace Cs2SchemaTracker.Tests.NetworkMessages;

public class DemoMessagesEmitterTest
{
    private const string Platform = "windows-x86_64";

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "demo-msg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static DemoMessageRttiScanner.DemoEntry E(int id, string type) => new(id, type);

    [Fact]
    public void Sorts_By_Id_Then_Type_And_Keeps_The_Id15_Dual()
    {
        var dir = NewDir();
        try
        {
            // Supplied out of order; the id-15 dual must produce two adjacent rows sorted by type.
            var entries = new[]
            {
                E(15, "CDemoSpawnGroupsHLTVBroadcast"),
                E(7, "CDemoPacket"),
                E(15, "CDemoSpawnGroups"),
                E(0, "CDemoStop"),
            };

            var outPath = Path.Combine(dir, "demo_messages.json");
            new DemoMessagesEmitter(SchemaFamily.Version, "23517234", Platform).Emit(entries, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            Assert.Equal(SchemaFamily.Version, doc.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("23517234", doc.RootElement.GetProperty("buildId").GetString());
            Assert.Equal(Platform, doc.RootElement.GetProperty("platform").GetString());

            var msgs = doc.RootElement.GetProperty("messages");
            Assert.Equal(4, msgs.GetArrayLength());
            // (0,CDemoStop), (7,CDemoPacket), (15,CDemoSpawnGroups), (15,CDemoSpawnGroupsHLTVBroadcast)
            Assert.Equal(0, msgs[0].GetProperty("id").GetInt32());
            Assert.Equal("CDemoStop", msgs[0].GetProperty("protoMessageType").GetString());
            Assert.Equal(7, msgs[1].GetProperty("id").GetInt32());
            Assert.Equal(15, msgs[2].GetProperty("id").GetInt32());
            Assert.Equal("CDemoSpawnGroups", msgs[2].GetProperty("protoMessageType").GetString());
            Assert.Equal(15, msgs[3].GetProperty("id").GetInt32());
            Assert.Equal("CDemoSpawnGroupsHLTVBroadcast", msgs[3].GetProperty("protoMessageType").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewDir();
        try
        {
            var entries = new[] { E(13, "CDemoFullPacket"), E(7, "CDemoPacket") };
            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            new DemoMessagesEmitter(SchemaFamily.Version, "b", Platform).Emit(entries, pa);
            new DemoMessagesEmitter(SchemaFamily.Version, "b", Platform).Emit(entries, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Type_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "demo_messages.json");
            Assert.Throws<InvalidDataException>(() =>
                new DemoMessagesEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(new[] { E(1, "") }, outPath));
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
