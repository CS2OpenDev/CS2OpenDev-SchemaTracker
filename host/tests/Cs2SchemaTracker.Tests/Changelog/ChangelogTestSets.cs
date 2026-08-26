// Shared minimal (build, platform) set builder for the changelog tests: exactly the four
// binary-family source files the diff engine reads (entity_schema / convars / commands /
// engine_constants), written canonically. One copy of the source-file list, used by
// DiffCommandTest and ReconcileChangelogCommandTest.

using Google.Protobuf;

using Schemas = Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Tests.Changelog;

internal static class ChangelogTestSets
{
    public static void MakeSet(
        string root, string buildId, string platform, params (string Module, string Name)[] classes)
    {
        var dir = Path.Combine(root, buildId, platform);
        Directory.CreateDirectory(dir);

        var schema = new Schemas.EntitySchema { SchemaVersion = "0.4.0", BuildId = buildId, Platform = platform };
        foreach (var (module, name) in classes)
        {
            schema.Classes.Add(new Schemas.SchemaClass { Name = name, Module = module });
        }
        Write(schema, Path.Combine(dir, "entity_schema.json"));
        Write(new Schemas.ConVars { SchemaVersion = "0.4.0", BuildId = buildId, Platform = platform },
            Path.Combine(dir, "convars.json"));
        Write(new Schemas.Commands { SchemaVersion = "0.4.0", BuildId = buildId, Platform = platform },
            Path.Combine(dir, "commands.json"));
        Write(new Schemas.EngineConstants { SchemaVersion = "0.4.0", BuildId = buildId, Platform = platform },
            Path.Combine(dir, "engine_constants.json"));
    }

    public static void Write(IMessage msg, string path)
        => Cs2SchemaTracker.Host.Serialization.AtomicWrite.WriteCanonical(msg, path);
}
