// Command serializer (commands.json).
//
// Consumes the walker's WalkerOutput intermediate and lifts WalkerOutput.commands's Command[]
// straight into the public Commands message (schemas/commands.proto), then stamps host-only identity
// (schema_version, build_id, platform) and writes canonical proto3-JSON commands.json. Commands
// differ from convars only by having no default value (name, flags, description).
//
// Invariants:
//   Determinism: commands sorted by name (Ordinal); flags kept in declared order; canonical JSON
//     (sorted keys); LF; UTF-8 no BOM.
//   Fail-loud: missing/corrupt WalkerOutput, missing commands walk, or a command with an empty name
//     throws BEFORE any output bytes are written.
//   All-or-nothing: sibling .tmp then atomic rename.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Commands;

/// <summary>
/// Maps a walker <see cref="WalkerOutput"/>'s Command walk into the public
/// <see cref="Schemas.Commands"/> and writes the canonical commands.json.
/// </summary>
public sealed class CommandsEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public CommandsEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Read the walker output file, map it, and write commands.json. Fail-loud.
    /// </summary>
    public void EmitFromFile(string walkerOutputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(walkerOutputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!File.Exists(walkerOutputPath))
        {
            throw new FileNotFoundException(
                $"CommandsEmitter: walker output file not found: '{walkerOutputPath}'.", walkerOutputPath);
        }

        byte[] bytes = File.ReadAllBytes(walkerOutputPath);
        WalkerOutput walkerOutput;
        try
        {
            walkerOutput = WalkerOutput.Parser.ParseFrom(bytes);
        }
        catch (InvalidProtocolBufferException ex)
        {
            throw new InvalidDataException(
                $"CommandsEmitter: failed to parse walker output '{walkerOutputPath}' as WalkerOutput.", ex);
        }

        Emit(walkerOutput, outputPath);
    }

    /// <summary>
    /// Map an in-memory <see cref="WalkerOutput"/> and write commands.json. Validation +
    /// document build happen before any disk write.
    /// </summary>
    public void Emit(WalkerOutput walkerOutput, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(walkerOutput);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (walkerOutput.Commands is null)
        {
            throw new InvalidDataException(
                "CommandsEmitter: WalkerOutput.commands is unset — nothing to map.");
        }

        var document = new Schemas.Commands
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        foreach (Command src in walkerOutput.Commands.Commands.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(src.Name))
            {
                throw new InvalidDataException(
                    "CommandsEmitter: a Command has an empty name (requires a named command).");
            }

            var dst = new Command
            {
                Name = src.Name,
                Description = src.Description,
                // Additive: whether the command registered a tab-completion callback (ConCommand
                // FCVAR/completion bit). Opaque bool, verbatim copy-through.
                HasCompletionCallback = src.HasCompletionCallback,
            };
            dst.Flags.AddRange(src.Flags);
            document.Commands_.Add(dst);
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
