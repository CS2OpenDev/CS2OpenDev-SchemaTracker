// ConVar serializer (convars.json).
//
// Consumes the walker's per-(binary-dir, platform) intermediate (WalkerOutput, a binary protobuf;
// schemas/walker_output.proto) and lifts WalkerOutput.convars's ConVar[] straight into the public
// ConVars message (schemas/convars.proto), then stamps the host-only identity fields the walker
// cannot know (schema_version, build_id, platform) and writes the canonical proto3-JSON convars.json.
//
// The lift is mechanical: WalkerOutput.ConVarsWalk reuses the public ConVar message verbatim, so the
// host copies the repeated field, validates each record, orders deterministically, and serializes
// via JsonFormatter + CanonicalJson — the same path the other emitters use and the round-trip test
// verifies.
//
// Invariants:
//   Determinism: convars sorted by name (Ordinal); flags within a convar kept in declared order
//     (flag order is meaningful — it mirrors the registry); canonical JSON (sorted keys); LF; UTF-8
//     no BOM.
//   Fail-loud: missing/corrupt WalkerOutput, missing convars walk, or a convar with an empty name
//     throws BEFORE any output bytes are written. No catch-and-continue.
//   All-or-nothing: write to a sibling .tmp then atomically rename.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.ConVars;

/// <summary>
/// Maps a walker <see cref="WalkerOutput"/>'s ConVar walk into the public
/// <see cref="Schemas.ConVars"/> and writes the canonical convars.json.
/// </summary>
public sealed class ConVarsEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public ConVarsEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Read the walker output file (binary protobuf <see cref="WalkerOutput"/>), map it,
    /// and write convars.json to <paramref name="outputPath"/>. Fail-loud.
    /// </summary>
    public void EmitFromFile(string walkerOutputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(walkerOutputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!File.Exists(walkerOutputPath))
        {
            throw new FileNotFoundException(
                $"ConVarsEmitter: walker output file not found: '{walkerOutputPath}'.", walkerOutputPath);
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
                $"ConVarsEmitter: failed to parse walker output '{walkerOutputPath}' as WalkerOutput.", ex);
        }

        Emit(walkerOutput, outputPath);
    }

    /// <summary>
    /// Map an in-memory <see cref="WalkerOutput"/> and write convars.json. Validates, builds
    /// the full document, then atomically writes. No bytes hit disk before validation passes.
    /// </summary>
    public void Emit(WalkerOutput walkerOutput, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(walkerOutput);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (walkerOutput.Convars is null)
        {
            throw new InvalidDataException(
                "ConVarsEmitter: WalkerOutput.convars is unset — nothing to map.");
        }

        var document = new Schemas.ConVars
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // Sort by name (Ordinal). A walker MAY emit registry order, but the host must not depend on
        // that for determinism. Flags keep declared order.
        foreach (ConVar src in walkerOutput.Convars.Convars.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(src.Name))
            {
                throw new InvalidDataException(
                    "ConVarsEmitter: a ConVar has an empty name (requires a named convar).");
            }

            var dst = new ConVar
            {
                Name = src.Name,
                Default = src.Default,
                Description = src.Description,
                // Typing + bounds (additive): carried through verbatim from the walker's ConVarsWalk.
                // value_type is the EConVarType enumerator name ("" when Invalid); min_value /
                // max_value are rendered identically to `default` and are "" when the corresponding
                // has_* flag is false. The walker is the source of truth — the host does not derive
                // or guess these.
                ValueType = src.ValueType,
                HasMin = src.HasMin,
                MinValue = src.MinValue,
                HasMax = src.HasMax,
                MaxValue = src.MaxValue,
            };
            dst.Flags.AddRange(src.Flags);
            document.Convars.Add(dst);
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
