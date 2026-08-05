// Provenance assembly (provenance.json).
//
// Every artifact set ships exactly one provenance.json (schemas/provenance.proto). This emitter
// assembles it from the extract context and writes the canonical proto3-JSON record.
//
// The record carries ALL of:
//   - Tool version: semver + git commit SHA of the dumper.
//   - For every input binary read: path, SHA-256, file size, file mtime (from the Steam manifest).
//   - Steam identity: appid, depotid + manifestid per depot, Steam build ID, manifest creation
//     time (UTC).
//   - CS2 build identity: schema revision (from the schema system), build_id (from Steam),
//     built_from_cl (from built_from_cl.txt).
//   - Target platform: (os, arch) — "windows-x86_64" | "linux-x86_64".
//   - Schema version emitted.
//
// What this emitter populates vs. leaves empty (and WHY):
//   - tool.semver       : the schemas/*.proto family version (SchemaFamily.Version). The host
//                         assembly version is NOT a separate semver line in the spec; the family
//                         version is the consumer-facing "version every output conforms to".
//   - tool.git_commit   : passed in by the caller (best-effort). The caller MUST resolve this
//                         deterministically (e.g. an env-injected SHA / a committed file) and pass
//                         "" when genuinely unavailable. This emitter NEVER shells out to git or
//                         bakes a nondeterministic value.
//   - steam.*           : from the acquire's manifest-record.json (app_id / per-depot
//                         depot_id+manifest_id / steam_build_id / manifest_created_utc) when present.
//                         Absent record => steam identity is left at its proto3 defaults with the
//                         build_id still echoed from the extract argument.
//   - cs2_build.schema_revision : from WalkerOutput.schema_system_layout_signature when supplied
//                         (the schema-system probe output is the closest reproducible "schema
//                         revision" the walk yields today).
//   - cs2_build.steam_build_id  : the build_id from the extract argument.
//   - cs2_build.built_from_cl   : LEFT EMPTY. built_from_cl.txt lives in the CONTENT depot, which
//                         the binaries-only acquire skips; populating it is gated on acquiring the
//                         content depot (TODO). Empty string, never a guess.
//   - inputs[]          : every input binary, with reproducible SHA-256 (ModuleInspector hash),
//                         file size, and mtime from the manifest-record/AcquiredFileInfo when the
//                         manifest carries one (else "").
//
// Invariants:
//   Every field above is populated from INPUT, not synthesized.
//   Determinism: inputs sorted by path (Ordinal); depots sorted by depot_id; every timestamp comes
//     from the input (manifest) — never DateTime.Now; canonical JSON.
//   Fail-loud: a missing input binary (cannot hash) throws BEFORE any output bytes.
//   All-or-nothing: sibling .tmp then atomic rename.

using System.Security.Cryptography;

using Cs2SchemaTracker.Host.Serialization;
using Cs2SchemaTracker.Schemas;

namespace Cs2SchemaTracker.Host.Provenance;

/// <summary>One input binary to record in provenance, plus its manifest-sourced mtime.</summary>
/// <param name="Path">Path recorded verbatim (relative to the depot / acquire dir).</param>
/// <param name="LocalFilePath">Local file to hash + size. May equal <see cref="Path"/>.</param>
/// <param name="MtimeUtc">ISO 8601 UTC mtime from the Steam manifest, or "" if unknown.</param>
public sealed record ProvenanceInput(string Path, string LocalFilePath, string MtimeUtc);

/// <summary>One depot's manifest identity (Steam-side provenance subset).</summary>
public sealed record ProvenanceDepot(uint DepotId, string ManifestId);

/// <summary>
/// Everything the provenance assembly needs that is NOT derivable from the input binaries
/// themselves. Steam-side fields are populated from the acquire's manifest-record.json when
/// present; absent fields stay at their documented defaults.
/// </summary>
public sealed class ProvenanceContext
{
    /// <summary>schemas/*.proto family version emitted (pass <see cref="SchemaFamily.Version"/>).</summary>
    public required string SchemaVersion { get; init; }

    /// <summary>Steam build ID, echoed into provenance.build_id and cs2_build.steam_build_id.</summary>
    public required string BuildId { get; init; }

    /// <summary>"windows-x86_64" | "linux-x86_64".</summary>
    public required string Platform { get; init; }

    /// <summary>Git SHA of CS2-Schema-Tracker at extraction time; "" if unavailable deterministically.</summary>
    public string GitCommit { get; init; } = "";

    /// <summary>
    /// Walker identity chain: the git SHA of the WALKER binary actually used this run (its
    /// <c>kWalkerGitSha</c>, from <c>&lt;walker&gt; --version</c>) — distinct from <see cref="GitCommit"/>
    /// (the HOST's own SHA). "" when the walker's identity could not be resolved this run (e.g. the
    /// fake-runner test seam, which launches no real binary).
    /// </summary>
    public string WalkerGitSha { get; init; } = "";

    /// <summary>
    /// Walker identity chain: the content fingerprint of the WALKER binary actually used this run
    /// (its <c>kWalkerSrcFingerprint</c>). "" when unresolved (see <see cref="WalkerGitSha"/>); the
    /// literal string <c>"unknown"</c> (never guessed away) when the walker WAS resolved but is old
    /// enough that it printed no <c>src-fingerprint</c> line.
    /// </summary>
    public string WalkerSrcFingerprint { get; init; } = "";

    /// <summary>Steam app id (730) when known from the manifest record; 0 if absent.</summary>
    public uint AppId { get; init; }

    /// <summary>Per-depot manifest identity from the manifest record; empty if absent.</summary>
    public IReadOnlyList<ProvenanceDepot> Depots { get; init; } = Array.Empty<ProvenanceDepot>();

    /// <summary>Manifest creation time (ISO 8601 UTC) from the record; "" if absent.</summary>
    public string ManifestCreatedUtc { get; init; } = "";

    /// <summary>Schema-system revision/probe signature from the walk; "" if absent.</summary>
    public string SchemaRevision { get; init; } = "";

    /// <summary>
    /// built_from_cl.txt contents. LEFT EMPTY by the binaries-only path: that file lives in the
    /// content depot the binaries-only acquire skips. Populating it is a future TODO gated on
    /// content-depot acquisition; never guessed.
    /// </summary>
    public string BuiltFromCl { get; init; } = "";

    /// <summary>The input binaries to record (each hashed + sized here).</summary>
    public required IReadOnlyList<ProvenanceInput> Inputs { get; init; }

    /// <summary>
    /// Fingerprint (sha256/size/token_count) of the build-on-demand localization.json produced this
    /// dump. Null when no localization was produced (no content depot for this build/era, or the era
    /// genuinely never shipped localization tables) — mirrors how a content artifact signals
    /// "content depot not acquired": the record is simply absent. When set, its
    /// <see cref="LocalizationOutput.Sha256"/> is over the canonical localization.json bytes so an
    /// emit-localization rebuild is byte-verifiable.
    /// </summary>
    public LocalizationOutput? Localization { get; init; }
}

/// <summary>
/// Assembles and writes provenance.json from a <see cref="ProvenanceContext"/>.
/// </summary>
public static class ProvenanceEmitter
{
    /// <summary>
    /// Build the <see cref="Schemas.Provenance"/> from <paramref name="context"/> (hashing every
    /// input binary) and write canonical provenance.json to <paramref name="outputPath"/>. Throws
    /// before any output bytes if an input binary is missing/unreadable.
    /// </summary>
    public static void Emit(ProvenanceContext context, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrEmpty(context.SchemaVersion);
        ArgumentException.ThrowIfNullOrEmpty(context.BuildId);
        ArgumentException.ThrowIfNullOrEmpty(context.Platform);
        ArgumentNullException.ThrowIfNull(context.Inputs);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        var document = new Schemas.Provenance
        {
            SchemaVersion = context.SchemaVersion,
            BuildId = context.BuildId,
            Platform = context.Platform,
            Tool = new ToolVersion
            {
                Semver = context.SchemaVersion,
                GitCommit = context.GitCommit ?? "",
                WalkerGitSha = context.WalkerGitSha ?? "",
                WalkerSrcFingerprint = context.WalkerSrcFingerprint ?? "",
            },
            Steam = new SteamIdentity
            {
                AppId = context.AppId,
                SteamBuildId = context.BuildId,
                ManifestCreatedUtc = context.ManifestCreatedUtc ?? "",
            },
            Cs2Build = new CS2BuildIdentity
            {
                SchemaRevision = context.SchemaRevision ?? "",
                SteamBuildId = context.BuildId,
                // built_from_cl lives in the content depot the binaries-only acquire skips.
                // TODO: populate from built_from_cl.txt once content-depot acquisition lands.
                BuiltFromCl = context.BuiltFromCl ?? "",
            },
        };

        // Build-on-demand localization fingerprint. Set only when localization.json was produced this
        // dump; absent otherwise (no content depot / era shipped no localization). The proto3 JSON
        // formatter omits an unset message field, so an absent record leaves provenance.localization
        // out entirely — the documented "content depot not acquired" signal.
        if (context.Localization is not null)
        {
            document.Localization = context.Localization;
        }

        // Depots sorted by depot_id.
        foreach (ProvenanceDepot depot in context.Depots.OrderBy(d => d.DepotId))
        {
            document.Steam.Depots.Add(new DepotManifest
            {
                DepotId = depot.DepotId,
                ManifestId = depot.ManifestId ?? "",
            });
        }

        // Inputs: hash + size every binary FIRST (any missing file fails loud BEFORE write),
        // recorded sorted by path (Ordinal) for determinism.
        var inputs = new List<InputBinary>();
        foreach (ProvenanceInput input in context.Inputs)
        {
            if (input is null)
            {
                throw new ArgumentException(
                    "ProvenanceEmitter: null entry in Inputs list.", nameof(context));
            }
            if (string.IsNullOrEmpty(input.Path) || string.IsNullOrEmpty(input.LocalFilePath))
            {
                throw new InvalidDataException(
                    "ProvenanceEmitter: an input has an empty Path/LocalFilePath (requires a path).");
            }
            if (!File.Exists(input.LocalFilePath))
            {
                throw new FileNotFoundException(
                    $"ProvenanceEmitter: input binary not found: '{input.LocalFilePath}'.", input.LocalFilePath);
            }

            byte[] hash;
            long size;
            using (var fs = new FileStream(input.LocalFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                size = fs.Length;
                hash = SHA256.HashData(fs);
            }

            inputs.Add(new InputBinary
            {
                Path = input.Path,
                Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
                FileSize = (ulong)size,
                MtimeUtc = input.MtimeUtc ?? "",
            });
        }

        foreach (InputBinary ib in inputs.OrderBy(i => i.Path, StringComparer.Ordinal))
        {
            document.Inputs.Add(ib);
        }

        AtomicWrite.WriteCanonical(document, outputPath);
    }
}
