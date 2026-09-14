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
    long DownloadedBytes = 0)
{
    /// <summary>
    /// The Phase-A directory-index transfer this result does NOT count, so a caller can report what
    /// Steam actually sent.
    /// <para>
    /// AcquireContentPakAsync fetches every content pak's <c>pak01_dir.vpk</c> WHOLE in Phase A to parse
    /// the required set, then — when the patch partition leaves body ranges to fetch — returns ONLY Phase
    /// B's result, dropping Phase A's transfer from <see cref="DownloadedBytes"/>. Phase B genuinely
    /// re-downloads that index (BuildByteRangePlan always lists the directory file as a whole file, and
    /// Phase B stages into a fresh <c>.partial</c> where the chunk-resume probe hits nothing), so the
    /// index is paid for TWICE and the acquire reports one of them. A whole-file fetch's
    /// <see cref="AcquiredFileInfo.SizeBytes"/> IS the number of bytes it transferred, which is why
    /// summing the result's directory files restores the unreported half.
    /// </para>
    /// <para>
    /// When Phase B was SKIPPED the acquirer hands back Phase A's own result, whose DownloadedBytes
    /// already counts the index and whose Files are directory files ONLY — the "carries a non-directory
    /// file" gate returns 0 there, so that shape is never double-counted. The addend is exact only while
    /// both of those hold: Phase B re-fetching the directory file whole, and Phase A's staging being
    /// fresh. If a later acquirer change lets Phase B reuse Phase A's staging, this over-reports.
    /// </para>
    /// <para>
    /// This derivation lives here, on the result itself, so there is exactly ONE definition rather than a
    /// copy per caller. It exists ONLY because AcquireResult carries no Phase-A field: if the acquirer
    /// ever folds Phase A into <see cref="DownloadedBytes"/>, DELETE this property and EVERY call site in
    /// the SAME change or the index is counted twice.
    /// </para>
    /// </summary>
    internal long PhaseAIndexBytes
    {
        get
        {
            static bool IsIndex(AcquiredFileInfo f)
                => ContentPak.All.Any(p => p.IsDirectoryFile(f.RelativePath));
            return Files.Any(f => !IsIndex(f))
                ? Files.Where(IsIndex).Sum(f => f.SizeBytes)
                : 0;
        }
    }
}
