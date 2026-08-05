// Engine constants serializer (engine_constants.json).
//
// Consumes the walker's WalkerOutput intermediate and lifts WalkerOutput.engine_constants's
// EngineConstant[] straight into the public EngineConstants message
// (schemas/engine_constants.proto), then stamps host-only identity (schema_version, build_id,
// platform) and writes canonical proto3-JSON engine_constants.json.
//
// HARD RULE: only constants the binary itself names. Both `name` and `source` are non-empty (the
// registry audit reaches each row as `extracted` via `source`), so an empty name OR an empty source
// is malformed structure and fails loud — the host never infers or coerces. The value is a oneof
// (int_value | string_value); a constant that carries neither is malformed (the walker must declare
// the value in its native form) and fails loud.
//
// Invariants:
//   Determinism: constants sorted by name (Ordinal); within an equal name, by source (Ordinal) so
//     the ordering is total; canonical JSON (sorted keys); LF; UTF-8 no BOM.
//   Fail-loud: missing/corrupt WalkerOutput, missing engine_constants walk, an empty name, an empty
//     source, or an unset value oneof throws BEFORE any output bytes are written. No
//     catch-and-continue.
//   All-or-nothing: sibling .tmp then atomic rename (via AtomicWrite).

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.EngineConstants;

/// <summary>
/// Maps a walker <see cref="WalkerOutput"/>'s named-constant-pool walk into the public
/// <see cref="Schemas.EngineConstants"/> and writes the canonical engine_constants.json.
/// </summary>
public sealed class EngineConstantsEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public EngineConstantsEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Read the walker output file, map it, and write engine_constants.json. Fail-loud.
    /// </summary>
    public void EmitFromFile(string walkerOutputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(walkerOutputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!File.Exists(walkerOutputPath))
        {
            throw new FileNotFoundException(
                $"EngineConstantsEmitter: walker output file not found: '{walkerOutputPath}'.", walkerOutputPath);
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
                $"EngineConstantsEmitter: failed to parse walker output '{walkerOutputPath}' as WalkerOutput.", ex);
        }

        Emit(walkerOutput, outputPath);
    }

    /// <summary>
    /// Map an in-memory <see cref="WalkerOutput"/> and write engine_constants.json. Validation +
    /// document build happen before any disk write.
    /// </summary>
    public void Emit(WalkerOutput walkerOutput, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(walkerOutput);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (walkerOutput.EngineConstants is null)
        {
            throw new InvalidDataException(
                "EngineConstantsEmitter: WalkerOutput.engine_constants is unset — nothing to map.");
        }

        var document = new Schemas.EngineConstants
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // Sort by (name, source) Ordinal for a stable, total ordering. The walker MAY emit pool
        // order, but the host must not depend on it.
        foreach (EngineConstant src in walkerOutput.EngineConstants.Constants
                     .OrderBy(c => c.Name, StringComparer.Ordinal)
                     .ThenBy(c => c.Source, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(src.Name))
            {
                throw new InvalidDataException(
                    "EngineConstantsEmitter: an EngineConstant has an empty name — emits only "
                    + "constants the binary itself names, never inferred.");
            }

            if (string.IsNullOrEmpty(src.Source))
            {
                throw new InvalidDataException(
                    $"EngineConstantsEmitter: EngineConstant '{src.Name}' has an empty source — "
                    + "requires a non-empty source so the audit can reach it as `extracted`.");
            }

            var dst = new EngineConstant
            {
                Name = src.Name,
                Source = src.Source,
            };

            // The value is a oneof matching how the binary declares it; carry it verbatim, no
            // coercion. An unset value is malformed structure.
            switch (src.ValueCase)
            {
                case EngineConstant.ValueOneofCase.IntValue:
                    dst.IntValue = src.IntValue;
                    break;
                case EngineConstant.ValueOneofCase.StringValue:
                    dst.StringValue = src.StringValue;
                    break;
                default:
                    throw new InvalidDataException(
                        $"EngineConstantsEmitter: EngineConstant '{src.Name}' carries no value "
                        + "(neither int_value nor string_value) — requires the value in its "
                        + "native form.");
            }

            document.Constants.Add(dst);
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
