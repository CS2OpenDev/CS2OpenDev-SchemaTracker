// Module manifest emitter (modules.json).
//
// One manifest per (build, platform). One walk per platform loads ALL modules
// (client+server+engine) of that per-OS depot, so a single modules.json lists every binary read
// for that platform.
//
// Given a list of binary paths plus per-module schema-registration counts (computed host-side from
// the walk's entity_schema by SchemaRegistrationCounter), write a canonical-form modules.json
// containing one entry per module: path, SHA-256, file_size, export_count,
// schema_registration_count.
//
// Serialization: the public Modules/Module messages (schemas/modules.proto) are serialized via
// Google.Protobuf.JsonFormatter (canonical proto3 JSON mapping), then post-sorted through
// CanonicalJson — the same path the other emitters use and what the round-trip test verifies
// byte-identically. Critically, the proto3 JSON mapping renders the uint64 `file_size` as a STRING
// ("1024"), not a JSON number, which is what a strict proto3 JSON parser expects (a prior POCO
// emitted it as a number and broke the byte-identical round-trip).
//
// Invariants:
//   Determinism: modules sorted by Path Ordinal; canonical JSON; LF; UTF-8 no BOM; SHA-256
//     lowercase hex.
//   Fail-loud: any inspection failure throws before output is written.
//   All-or-nothing: writes to a sibling temp file then atomically renames into place; on mid-write
//     throw, the temp file is deleted and any pre-existing target is left untouched.

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

using Google.Protobuf;

namespace Cs2SchemaTracker.Host.Modules;

/// <summary>
/// One module to include in the manifest. <paramref name="Path"/> is the LOCAL file to inspect
/// (hash/size/exports). <paramref name="RegistrationCount"/> is the count of schema-system
/// registrations attributed to this binary, computed host-side by
/// <see cref="SchemaRegistrationCounter"/> from the walk's entity_schema; 0 is legitimate for a
/// binary that registers no schema. <paramref name="RecordedPath"/> is the path written into
/// modules.json — the "path inside the depot" (depot-relative, forward-slash). When null,
/// <paramref name="Path"/> is recorded verbatim (callers wanting a deterministic, machine-independent
/// record MUST pass a depot-relative RecordedPath — an absolute local path varies per machine and
/// breaks determinism).
/// </summary>
/// <param name="ResolvedInterfaces">
/// Boot-resolved CreateInterface version strings this binary exposed (from the walker's ModulesWalk,
/// joined by module identity host-side). Carried through verbatim into the Module row's
/// resolved_interfaces; the emitter sorts them Ordinal and de-duplicates. Null or empty when the
/// walker resolved none for this binary (a legitimate empty repeated field).
/// </param>
public sealed record ModuleInput(
    string Path,
    int RegistrationCount,
    string? RecordedPath = null,
    IReadOnlyList<string>? ResolvedInterfaces = null);

public sealed class ModuleManifestEmitter
{
    private readonly string _schemaVersion;
    private readonly string _buildId;
    private readonly string _platform;

    /// <summary>
    /// Construct an emitter parameterised by the framing fields shared by every
    /// per-platform artifact (cf. <c>schemas/modules.proto</c>).
    /// <paramref name="platform"/> is one of "windows-x86_64" | "linux-x86_64".
    /// </summary>
    public ModuleManifestEmitter(string schemaVersion, string buildId, string platform)
    {
        ArgumentException.ThrowIfNullOrEmpty(schemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(buildId);
        ArgumentException.ThrowIfNullOrEmpty(platform);
        _schemaVersion = schemaVersion;
        _buildId = buildId;
        _platform = platform;
    }

    /// <summary>
    /// Inspect every input in <paramref name="inputs"/>, sort the results by
    /// path (Ordinal), and write the canonical JSON manifest to
    /// <paramref name="outputPath"/>. Throws on the first input failure
    /// without writing any output bytes.
    /// </summary>
    public void Emit(IReadOnlyList<ModuleInput> inputs, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        // 1. Inspect everything FIRST. Any failure aborts before we touch disk.
        //    Use a Dictionary to detect duplicate paths (caller bug, fail loud).
        var byPath = new Dictionary<string, (ModuleInput Input, ModuleInspector.InspectionResult Result)>(
            StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (input is null)
            {
                throw new ArgumentException(
                    "ModuleManifestEmitter: null entry in inputs list.", nameof(inputs));
            }
            if (byPath.ContainsKey(input.Path))
            {
                throw new InvalidDataException(
                    $"ModuleManifestEmitter: duplicate input path '{input.Path}'.");
            }
            var result = ModuleInspector.Inspect(input.Path);
            byPath[input.Path] = (input, result);
        }

        var document = new Schemas.Modules
        {
            SchemaVersion = _schemaVersion,
            BuildId = _buildId,
            Platform = _platform,
        };

        // 2. Sort by the RECORDED path Ordinal for deterministic output. The recorded path is what
        //    lands in modules.json (depot-relative when supplied).
        foreach (var (input, inspection) in byPath.Values
                     .Select(x => (x.Input, x.Result))
                     .OrderBy(x => x.Input.RecordedPath ?? x.Input.Path, StringComparer.Ordinal))
        {
            // The generated repeated-field accessor is Modules_ (trailing underscore): the
            // proto field name `modules` collides with the enclosing message type name
            // `Modules`, so protoc disambiguates the property.
            var module = new Module
            {
                Path = input.RecordedPath ?? input.Path,
                Sha256 = Convert.ToHexString(inspection.Sha256).ToLowerInvariant(),
                FileSize = (ulong)inspection.SizeBytes,
                ExportCount = (uint)inspection.ExportCount,
                SchemaRegistrationCount = (uint)input.RegistrationCount,
            };

            // resolved_interfaces (additive). Merge the walker-observed boot-resolved CreateInterface
            // versions onto this row, sorted Ordinal + de-duplicated. Empty when none — a legitimate
            // empty repeated field, never a fabricated entry.
            if (input.ResolvedInterfaces is { Count: > 0 })
            {
                module.ResolvedInterfaces.AddRange(
                    input.ResolvedInterfaces
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(s => s, StringComparer.Ordinal));
            }

            document.Modules_.Add(module);
        }

        // 3. Serialize via canonical proto3 JSON, then sort keys.
        string json = SerializeCanonical(document);

        // 4. Atomic-ish write: sibling .tmp then File.Move with overwrite.
        var fullPath = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
        var tmpPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(tmpPath, System.Text.Encoding.UTF8.GetBytes(json));
            File.Move(tmpPath, fullPath, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmpPath))
            {
                try
                { File.Delete(tmpPath); }
                catch { /* best effort */ }
            }
            throw;
        }
    }

    // ---- Canonical proto3 JSON --------------------------------------------------------
    //
    // Same settings the other proto3 emitters use: FormatDefaultValues so the record is complete and
    // stable across runs (zero counts emitted), two-space indent, then the shared CanonicalJson
    // sorter for sorted keys + LF + UTF-8 no BOM.

    private static readonly JsonFormatter Formatter = new(
        JsonFormatter.Settings.Default
            .WithFormatDefaultValues(true)
            .WithIndentation("  "));

    internal static string SerializeCanonical(IMessage message)
    {
        string formatted = Formatter.Format(message);
        return CanonicalJson.SerializeRawJson(formatted);
    }
}
