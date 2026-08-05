// one resolved VPK directory-tree entry.
//

namespace Cs2SchemaTracker.Host.Vpk;

/// <summary>
/// A single loose file recorded in a VPK directory tree, fully resolved from the
/// extension\path\filename triple into a logical path. Immutable.
/// </summary>
/// <remarks>
/// Field semantics follow the Valve VPK1/VPK2 on-disk format:
/// <list type="bullet">
///   <item><see cref="Crc32"/> — CRC-32 (IEEE) of the *complete* file bytes
///         (preload prefix + archive body).</item>
///   <item><see cref="PreloadBytes"/> — bytes stored inline in the directory tree,
///         immediately after the entry record. Prefix of the file.</item>
///   <item><see cref="ArchiveIndex"/> — which archive file holds the body:
///         <c>0x7FFF</c> means the body lives in <c>_dir.vpk</c> itself, after the
///         directory tree (and, for VPK2, before the trailing sections); any other
///         value <c>N</c> means <c>pak01_NNN.vpk</c>.</item>
///   <item><see cref="EntryOffset"/> / <see cref="EntryLength"/> — body location
///         within the resolved archive. A length of 0 means the file is entirely in
///         the preload section.</item>
/// </list>
/// </remarks>
internal sealed record VpkDirectoryEntry
{
    public const ushort EmbeddedArchiveIndex = 0x7FFF;

    /// <summary>Logical path, e.g. <c>resource/game.gameevents</c>. Lowercase as stored, '/'-separated.</summary>
    public required string FullPath { get; init; }

    /// <summary>
    /// The raw extension token from the VPK tree (the top grouping key), e.g. <c>gameevents</c>.
    /// A single space (<c>" "</c>) is the on-disk sentinel for "no extension". Preserved verbatim so
    /// <see cref="Cs2SchemaTracker.Host.Vpk.VpkTrimWriter"/> can rebuild the exact tree triple
    /// without lossy round-tripping through <see cref="FullPath"/>.
    /// </summary>
    public required string Extension { get; init; }

    /// <summary>The raw directory token from the VPK tree, e.g. <c>resource</c>. A single space means root.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>The raw filename token from the VPK tree (no extension), e.g. <c>game</c>.</summary>
    public required string FileName { get; init; }

    public required uint Crc32 { get; init; }

    public required ushort ArchiveIndex { get; init; }

    public required uint EntryOffset { get; init; }

    public required uint EntryLength { get; init; }

    /// <summary>Inline preload bytes (may be empty). Forms the prefix of the file.</summary>
    public required ReadOnlyMemory<byte> PreloadBytes { get; init; }

    /// <summary>Total decoded file length = preload + body.</summary>
    public long TotalLength => PreloadBytes.Length + EntryLength;

    /// <summary>True when the body lives in the _dir.vpk file rather than an external _NNN.vpk.</summary>
    public bool IsEmbedded => ArchiveIndex == EmbeddedArchiveIndex;
}
