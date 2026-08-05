// Steam acquisition: shape of a successful acquire result.
//
// This is the return shape SteamAcquirer hands back to AcquireCommand. It
// carries everything the eventual `provenance.json` will need from the
// Steam side:
//
//   - manifestid + manifest_created_utc per depot (Provenance.depots)
//   - SHA-256 + size per acquired file (Provenance.inputs)
//
// SHA-256 is computed locally by the acquirer because Steam manifests only
// carry SHA-1 chunk hashes, not whole-file SHA-256. Doing the hash during
// acquisition (vs. a second post-pass) avoids a redundant disk read for the
// extraction pipeline.
//
// All collections are immutable and stably-ordered.

namespace Cs2SchemaTracker.Host.Steam;

/// <summary>One depot's manifest identity, captured for provenance.</summary>
internal sealed record AcquiredDepotInfo(
    uint AppId,
    uint DepotId,
    ulong ManifestId,
    /// <summary>ISO 8601 UTC, derived from the Steam manifest's creation time.</summary>
    string ManifestCreatedUtc);

/// <summary>One acquired binary's local-disk identity, captured for provenance.</summary>
internal sealed record AcquiredFileInfo(
    /// <summary>Path relative to the acquire output directory, forward-slash separated.</summary>
    string RelativePath,
    /// <summary>SHA-256 in lowercase hex (matches Provenance.InputBinary.sha256).</summary>
    string Sha256Hex,
    long SizeBytes,
    /// <summary>ISO 8601 UTC of the manifest-recorded mtime, or null if the manifest doesn't carry one.</summary>
    string? MtimeUtc);

/// <summary>
/// What <see cref="ISteamAcquirer.AcquireAsync"/> hands back on success. On
/// failure, the method throws and writes nothing —.
/// </summary>
internal sealed record AcquireResult(
    /// <summary>Absolute path to the output directory (after final rename from .partial).</summary>
    string OutDir,
    /// <summary>Build ID that was actually acquired (resolved from 'latest' if applicable).</summary>
    uint ResolvedBuildId,
    /// <summary>Per-depot manifest identity, ordered by depot ID for determinism.</summary>
    IReadOnlyList<AcquiredDepotInfo> Depots,
    /// <summary>Every binary written, ordered by relative path (ordinal) for determinism.</summary>
    IReadOnlyList<AcquiredFileInfo> Files,
    /// <summary>Sum of <see cref="AcquiredFileInfo.SizeBytes"/> across <see cref="Files"/>.</summary>
    long TotalBytes,
    /// <summary>
    /// Bytes actually transferred from the CDN this acquire (excludes chunks that
    /// resume-probed valid on disk and were NOT re-downloaded). 0 == a full
    /// cache-hit (every file was already present + hash-valid). This is the
    /// "did we hit the network?" signal the batch reports so a binary-cache reuse
    /// run can be seen to transfer content-only, not re-fetch cached binaries.
    /// </summary>
    long DownloadedBytes = 0);
