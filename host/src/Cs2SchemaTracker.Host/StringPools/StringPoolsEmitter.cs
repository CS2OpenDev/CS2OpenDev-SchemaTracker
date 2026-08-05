// String pools serializer (string_pools.json).
//
// Consumes the walker's WalkerOutput intermediate and lifts WalkerOutput.string_pools's StringPool[]
// straight into the public StringPools message (schemas/string_pools.proto), then stamps host-only
// identity (schema_version, build_id, platform) and writes canonical proto3-JSON string_pools.json.
//
// Every interned string the schema system registers appears, DEDUPLICATED by pool, with the pool
// name preserved verbatim. The walker already dedupes + sorts each pool's entries, but the host does
// NOT depend on that for determinism — it re-deduplicates and re-sorts here (defence in depth), so a
// walker that emitted duplicate or out-of-order entries still yields byte-identical, canonical output.
//
// Invariants:
//   Determinism: pools sorted by name (Ordinal); entries within a pool deduplicated and sorted
//     (Ordinal); canonical JSON (sorted keys); LF; UTF-8 no BOM.
//   Fail-loud: missing/corrupt WalkerOutput, missing string_pools walk, an empty pool name, or an
//     empty entry string throws BEFORE any output bytes are written.
//   All-or-nothing: sibling .tmp then atomic rename (via AtomicWrite).

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.StringPools;

/// <summary>
/// Maps a walker <see cref="WalkerOutput"/>'s interned-string-pool walk into the public
/// <see cref="Schemas.StringPools"/> and writes the canonical string_pools.json.
/// </summary>
public sealed class StringPoolsEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    public StringPoolsEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Read the walker output file, map it, and write string_pools.json. Fail-loud.
    /// </summary>
    public void EmitFromFile(string walkerOutputPath, string outputPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(walkerOutputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (!File.Exists(walkerOutputPath))
        {
            throw new FileNotFoundException(
                $"StringPoolsEmitter: walker output file not found: '{walkerOutputPath}'.", walkerOutputPath);
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
                $"StringPoolsEmitter: failed to parse walker output '{walkerOutputPath}' as WalkerOutput.", ex);
        }

        Emit(walkerOutput, outputPath);
    }

    /// <summary>
    /// Map an in-memory <see cref="WalkerOutput"/> and write string_pools.json. Validation +
    /// document build happen before any disk write.
    /// </summary>
    public void Emit(WalkerOutput walkerOutput, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(walkerOutput);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (walkerOutput.StringPools is null)
        {
            throw new InvalidDataException(
                "StringPoolsEmitter: WalkerOutput.string_pools is unset — nothing to map.");
        }

        var document = new Schemas.StringPools
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // Pools sorted by name (Ordinal). The host re-dedupes + re-sorts entries rather than
        // trusting the walker's ordering, so determinism holds regardless of walk order.
        foreach (StringPool src in walkerOutput.StringPools.Pools
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(src.Name))
            {
                throw new InvalidDataException(
                    "StringPoolsEmitter: a StringPool has an empty name — preserves the pool "
                    + "name verbatim.");
            }

            var dst = new StringPool { Name = src.Name };

            // Dedupe within the pool and sort Ordinal. An empty interned string is malformed — fail
            // loud rather than emit a blank entry.
            foreach (string entry in src.Entries.Distinct(StringComparer.Ordinal)
                         .OrderBy(e => e, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(entry))
                {
                    throw new InvalidDataException(
                        $"StringPoolsEmitter: pool '{src.Name}' has an empty interned string "
                        + "(registers named interned strings).");
                }
                dst.Entries.Add(entry);
            }

            document.Pools.Add(dst);
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
