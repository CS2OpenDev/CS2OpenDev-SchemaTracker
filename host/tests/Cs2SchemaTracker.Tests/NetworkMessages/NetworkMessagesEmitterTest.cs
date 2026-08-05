// NetworkMessagesEmitter unit tests (RTTI-scanner-shaped NetworkChannel[] input).
//

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.NetworkMessages;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.NetworkMessages;

public class NetworkMessagesEmitterTest
{
    private const string Platform = "linux-x86_64";

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "netmsg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Lifts_Faithfully_Sorts_Channels_And_Entries_And_Keeps_Unresolved_Ids()
    {
        var dir = NewDir();
        try
        {
            var net = new NetworkChannel { Name = "NetMessages" };
            net.Messages.Add(new NetworkMessageEntry { Id = 7, ProtoMessageType = "CNETMsg_Tick" });
            net.Messages.Add(new NetworkMessageEntry { Id = 4, ProtoMessageType = "CNETMsg_SignonState" });
            net.Messages.Add(new NetworkMessageEntry { Id = 99, ProtoMessageType = "" }); // unresolved — kept
            var ge = new NetworkChannel { Name = "GameEvents" };
            ge.Messages.Add(new NetworkMessageEntry { Id = 1, ProtoMessageType = "CMsgGameEvent" });

            var outPath = Path.Combine(dir, "network_messages.json");
            // Channels supplied out of name order: the emitter sorts them.
            new NetworkMessagesEmitter(SchemaFamily.Version, "b1", Platform)
                .Emit(new[] { net, ge }, outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var channels = doc.RootElement.GetProperty("channels");
            Assert.Equal(2, channels.GetArrayLength());
            // Channels sorted by name: GameEvents before NetMessages.
            Assert.Equal("GameEvents", channels[0].GetProperty("name").GetString());
            Assert.Equal("NetMessages", channels[1].GetProperty("name").GetString());

            // NetMessages entries sorted by id: 4, 7, 99 — and the unresolved id 99 is present.
            var msgs = channels[1].GetProperty("messages");
            Assert.Equal(3, msgs.GetArrayLength());
            Assert.Equal(4, msgs[0].GetProperty("id").GetInt32());
            Assert.Equal(7, msgs[1].GetProperty("id").GetInt32());
            Assert.Equal(99, msgs[2].GetProperty("id").GetInt32());
            // Unresolved entry has an empty proto_message_type (kept, not dropped —).
            Assert.Equal("", msgs[2].GetProperty("protoMessageType").GetString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_Runs_Byte_Identical()
    {
        var dir = NewDir();
        try
        {
            var ch = new NetworkChannel { Name = "NetMessages" };
            ch.Messages.Add(new NetworkMessageEntry { Id = 9, ProtoMessageType = "B" });
            ch.Messages.Add(new NetworkMessageEntry { Id = 2, ProtoMessageType = "A" });
            var channels = new[] { ch };

            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            new NetworkMessagesEmitter(SchemaFamily.Version, "b", Platform).Emit(channels, pa);
            new NetworkMessagesEmitter(SchemaFamily.Version, "b", Platform).Emit(channels, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Empty_Channel_Name_Writes_Nothing()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "network_messages.json");
            Assert.Throws<InvalidDataException>(() =>
                new NetworkMessagesEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(new[] { new NetworkChannel { Name = "" } }, outPath));
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
