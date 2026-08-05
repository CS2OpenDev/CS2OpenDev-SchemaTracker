// CommandsEmitter unit tests (synthetic WalkerOutput).
//

using System.Text.Json;

using Cs2SchemaTracker.Host;
using Cs2SchemaTracker.Host.Commands;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.Commands;

public class CommandsEmitterTest
{
    private const string Platform = "windows-x86_64";

    private static WalkerOutput WalkWith(params Command[] commands)
    {
        var walk = new CommandsWalk();
        walk.Commands.AddRange(commands);
        return new WalkerOutput { Platform = Platform, Commands = walk };
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "commands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void Lifts_Faithfully_And_Sorts_By_Name()
    {
        var dir = NewDir();
        try
        {
            var kill = new Command { Name = "kill", Description = "suicide", HasCompletionCallback = true };
            kill.Flags.Add("gamedll");
            var jump = new Command { Name = "+jump", Description = "" };
            jump.Flags.Add("clientdll");

            var outPath = Path.Combine(dir, "commands.json");
            new CommandsEmitter(SchemaFamily.Version, "b1", Platform).Emit(WalkWith(kill, jump), outPath);

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var cmds = doc.RootElement.GetProperty("commands");
            Assert.Equal(2, cmds.GetArrayLength());
            // Ordinal sort: '+' (0x2B) < 'k' (0x6B), so +jump precedes kill.
            Assert.Equal("+jump", cmds[0].GetProperty("name").GetString());
            Assert.Equal("kill", cmds[1].GetProperty("name").GetString());
            // Command carries no 'default' field.
            Assert.False(cmds[0].TryGetProperty("default", out _));
            // Batch-1 additive: has_completion_callback copies through. kill set it true;
            // +jump left it default false (FormatDefaultValues emits the bool either way).
            Assert.True(cmds[1].GetProperty("hasCompletionCallback").GetBoolean());
            Assert.False(cmds[0].GetProperty("hasCompletionCallback").GetBoolean());
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
                new Command { Name = "z_cmd" },
                new Command { Name = "a_cmd" });
            var pa = Path.Combine(dir, "a.json");
            var pb = Path.Combine(dir, "b.json");
            new CommandsEmitter(SchemaFamily.Version, "b", Platform).Emit(walk, pa);
            new CommandsEmitter(SchemaFamily.Version, "b", Platform).Emit(walk, pb);
            Assert.Equal(File.ReadAllBytes(pa), File.ReadAllBytes(pb));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailLoud_Missing_Commands_Walk()
    {
        var dir = NewDir();
        try
        {
            var outPath = Path.Combine(dir, "commands.json");
            Assert.Throws<InvalidDataException>(() =>
                new CommandsEmitter(SchemaFamily.Version, "b", Platform)
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
            var outPath = Path.Combine(dir, "commands.json");
            Assert.Throws<InvalidDataException>(() =>
                new CommandsEmitter(SchemaFamily.Version, "b", Platform)
                    .Emit(WalkWith(new Command { Name = "" }), outPath));
            Assert.False(File.Exists(outPath));
            Assert.False(File.Exists(outPath + ".tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
