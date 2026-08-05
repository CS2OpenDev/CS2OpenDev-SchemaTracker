// Per-binary schema-registration attribution unit tests.
//
// SchemaRegistrationCounter attributes each SchemaClass/SchemaEnum in the walk's entity_schema
// to a binary by reducing both the schema `module` tag and the binary file name to a common key.
// These tests pin: the count = classes + enums per module; both bare ("client") and file-name
// ("client.dll" / "libserver.so") module forms map to the same key; "!GlobalTypes" (and any
// '!'-prefixed pseudo-scope) is unattributed; a binary with no matching registrations -> 0.

using Cs2SchemaTracker.Host.Modules;
using Cs2SchemaTracker.Schemas;

using Xunit;

namespace Cs2SchemaTracker.Tests.Modules;

public class SchemaRegistrationCounterTest
{
    private static EntitySchemaWalk Walk(
        (string name, string module)[] classes,
        (string name, string module)[] enums)
    {
        var walk = new EntitySchemaWalk();
        foreach (var (name, module) in classes)
        {
            walk.Classes.Add(new SchemaClass { Name = name, Module = module });
        }
        foreach (var (name, module) in enums)
        {
            walk.Enums.Add(new SchemaEnum { Name = name, Module = module });
        }
        return walk;
    }

    [Fact]
    public void Counts_Classes_Plus_Enums_Per_Module()
    {
        // client.dll: 3 classes + 1 enum -> 4; server.dll: 2 classes + 0 enums -> 2.
        var walk = Walk(
            classes: new[]
            {
                ("C_A", "client.dll"), ("C_B", "client.dll"), ("C_C", "client.dll"),
                ("CServerA", "server.dll"), ("CServerB", "server.dll"),
            },
            enums: new[] { ("EClient", "client.dll") });

        var map = SchemaRegistrationCounter.CountByModuleKey(walk);

        Assert.Equal(4, SchemaRegistrationCounter.CountForBinaryFileName(map, "client.dll"));
        Assert.Equal(2, SchemaRegistrationCounter.CountForBinaryFileName(map, "server.dll"));
    }

    [Fact]
    public void Bare_Module_Tag_Matches_Dll_FileName()
    {
        // Synthetic walks use the BARE module tag ("client"); the file is "client.dll".
        var walk = Walk(
            classes: new[] { ("C_A", "client"), ("C_B", "client") },
            enums: System.Array.Empty<(string, string)>());

        var map = SchemaRegistrationCounter.CountByModuleKey(walk);
        Assert.Equal(2, SchemaRegistrationCounter.CountForBinaryFileName(map, "client.dll"));
    }

    [Fact]
    public void Linux_Lib_Prefixed_File_Matches_Bare_Module_Tag()
    {
        // Walker tags "server"; the Linux file ships as "libserver.so". lib + .so strip => "server".
        var walk = Walk(
            classes: new[] { ("CServerA", "server") },
            enums: new[] { ("EServer", "server") });

        var map = SchemaRegistrationCounter.CountByModuleKey(walk);
        Assert.Equal(2, SchemaRegistrationCounter.CountForBinaryFileName(map, "libserver.so"));
    }

    [Fact]
    public void GlobalTypes_PseudoScope_Is_Unattributed()
    {
        // "!GlobalTypes" registrations have no backing shipped binary: dropped, never a fake row.
        var walk = Walk(
            classes: new[] { ("CGlobal", "!GlobalTypes"), ("C_Client", "client.dll") },
            enums: new[] { ("EGlobal", "!GlobalTypes") });

        var map = SchemaRegistrationCounter.CountByModuleKey(walk);

        // The pseudo-scope produced no key.
        Assert.False(map.ContainsKey("!globaltypes"));
        Assert.False(map.ContainsKey("globaltypes"));
        // The real binary still counts (1 class).
        Assert.Equal(1, SchemaRegistrationCounter.CountForBinaryFileName(map, "client.dll"));
    }

    [Fact]
    public void Binary_With_No_Registrations_Gets_Zero()
    {
        var walk = Walk(
            classes: new[] { ("C_Client", "client.dll") },
            enums: System.Array.Empty<(string, string)>());

        var map = SchemaRegistrationCounter.CountByModuleKey(walk);

        // tier0.dll registers no schema -> 0, not an error.
        Assert.Equal(0, SchemaRegistrationCounter.CountForBinaryFileName(map, "tier0.dll"));
    }

    [Fact]
    public void Null_EntitySchema_Yields_Empty_Map()
    {
        var map = SchemaRegistrationCounter.CountByModuleKey(null);
        Assert.Empty(map);
        Assert.Equal(0, SchemaRegistrationCounter.CountForBinaryFileName(map, "client.dll"));
    }

    [Fact]
    public void Empty_Or_Untagged_Module_Is_Dropped()
    {
        // An empty module tag is not attributable to any file; it must not crash and must not
        // pollute the map with an empty key.
        var walk = Walk(
            classes: new[] { ("C_Untagged", ""), ("C_Client", "client.dll") },
            enums: System.Array.Empty<(string, string)>());

        var map = SchemaRegistrationCounter.CountByModuleKey(walk);
        Assert.False(map.ContainsKey(""));
        Assert.Equal(1, SchemaRegistrationCounter.CountForBinaryFileName(map, "client.dll"));
    }

    [Theory]
    [InlineData("client.dll", "client")]
    [InlineData("client", "client")]
    [InlineData("CLIENT.DLL", "client")]
    [InlineData("libserver.so", "server")]
    [InlineData("server", "server")]
    [InlineData("animationsystem.dll", "animationsystem")]
    [InlineData("lib", "lib")]   // too short to strip "lib" -> would leave empty; left as-is
    public void NormalizeKey_Reduces_Both_Forms(string input, string expected)
    {
        Assert.Equal(expected, SchemaRegistrationCounter.NormalizeKey(input));
    }
}
