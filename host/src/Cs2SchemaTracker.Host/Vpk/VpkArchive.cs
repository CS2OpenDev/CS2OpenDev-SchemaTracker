// VPK1/VPK2 container parser. Lists and extracts loose files (notably
// `.gameevents`) from a `pak01_dir.vpk` and its sibling `_NNN.vpk` archives.
//
// === Implementation choice (deferred per spec, decided here) ===
// We parse VPK ourselves (no ValveResourceFormat / ValveKeyValue runtime
// dependency). Rationale:
// * independence — VRF is a CS2-data-extracting project and is explicitly
//     banned as a dependency. ValveKeyValue does KV parsing, not VPK containers.
//   * The VPK container format is tiny, stable (v1 since 2012, v2 since ~2013), and
//     fully documented (Valve Developer Wiki "VPK File Format"). Hand-rolling it is
// ~200 lines and keeps all fail-loud error handling in our own code,
//     unit-testable against synthetic fixtures.
//   * CRC32 is in-house (see Crc32.cs) since System.IO.Hashing isn't referenced.
//
// === On-disk layout (both versions) ===
//   Header:
//     uint32 Signature  = 0x55AA1234
//     uint32 Version    = 1 or 2
//     uint32 TreeSize   = byte length of the directory tree
//   VPK2 adds, after TreeSize:
//     uint32 FileDataSectionSize
//     uint32 ArchiveMd5SectionSize
//     uint32 OtherMd5SectionSize
//     uint32 SignatureSectionSize
//   Directory tree (TreeSize bytes), then the embedded file-data section.
//
//   Directory tree = repeated, NUL-terminated-string keyed:
//     for each extension (string; "" terminates the extension list):
//       for each path (string; "" terminates this extension's paths):
//         for each filename (string; "" terminates this path's files):
//           uint32  CRC32
//           uint16  PreloadBytes
//           uint16  ArchiveIndex      (0x7FFF => data in _dir.vpk)
//           uint32  EntryOffset
//           uint32  EntryLength
//           uint16  Terminator        (always 0xFFFF)
//           <PreloadBytes> bytes of inline preload
//   Logical path = "<path>/<filename>.<extension>"; path " " (single space) means
//   root, and an extension of " " means no extension.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Cs2SchemaTracker.Host.Vpk;

internal sealed class VpkArchive
{
    public const uint Signature = 0x55AA1234u;
    private const ushort EntryTerminator = 0xFFFF;

    private readonly string _dirVpkPath;
    private readonly string _archiveBaseName; // e.g. "pak01" from "pak01_dir.vpk"
    private readonly string _archiveDir;
    private readonly byte[] _dirBytes;        // full bytes of the _dir.vpk file
    private readonly uint _treeSize;
    private readonly int _headerSize;         // bytes consumed by header (12 for v1, 28 for v2)
    private readonly List<VpkDirectoryEntry> _entries;

    public uint Version { get; }

    /// <summary>Entries sorted Ordinal by <see cref="VpkDirectoryEntry.FullPath"/> (deterministic order).</summary>
    public IReadOnlyList<VpkDirectoryEntry> Entries => _entries;

    private VpkArchive(
        string dirVpkPath,
        byte[] dirBytes,
        uint version,
        uint treeSize,
        int headerSize,
        List<VpkDirectoryEntry> entries)
    {
        _dirVpkPath = dirVpkPath;
        _dirBytes = dirBytes;
        Version = version;
        _treeSize = treeSize;
        _headerSize = headerSize;
        _entries = entries;

        string fileName = Path.GetFileName(dirVpkPath);
        _archiveDir = Path.GetDirectoryName(Path.GetFullPath(dirVpkPath)) ?? ".";
        // "pak01_dir.vpk" -> base "pak01". Tolerate any "<base>_dir.vpk".
        const string suffix = "_dir.vpk";
        _archiveBaseName = fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? fileName[..^suffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// Open and fully parse a <c>*_dir.vpk</c>. Throws fail-loud on bad
    /// signature, truncated header/tree, or unknown version. Does not yet touch any
    /// external <c>_NNN.vpk</c> — those are validated lazily at extract time.
    /// </summary>
    public static VpkArchive Open(string dirVpkPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(dirVpkPath);
        if (!File.Exists(dirVpkPath))
        {
            throw new FileNotFoundException($"VPK directory file not found: {dirVpkPath}", dirVpkPath);
        }

        byte[] bytes = File.ReadAllBytes(dirVpkPath);
        return Parse(dirVpkPath, bytes);
    }

    /// <summary>Parse from an in-memory buffer (used by tests with synthetic fixtures).</summary>
    public static VpkArchive Parse(string dirVpkPath, byte[] dirBytes)
    {
        ArgumentNullException.ThrowIfNull(dirBytes);

        if (dirBytes.Length < 12)
        {
            throw new InvalidDataException(
                $"VPK truncated: file is {dirBytes.Length} bytes, smaller than the 12-byte v1 header ('{dirVpkPath}').");
        }

        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(0, 4));
        if (signature != Signature)
        {
            throw new InvalidDataException(
                $"VPK bad signature: expected 0x{Signature:X8}, got 0x{signature:X8} ('{dirVpkPath}').");
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(4, 4));
        uint treeSize = BinaryPrimitives.ReadUInt32LittleEndian(dirBytes.AsSpan(8, 4));

        int headerSize = version switch
        {
            1 => 12,
            2 => 28,
            _ => throw new InvalidDataException(
                $"VPK unknown version {version}: only v1 and v2 are supported ('{dirVpkPath}')."),
        };

        if (dirBytes.Length < headerSize)
        {
            throw new InvalidDataException(
                $"VPK truncated: file is {dirBytes.Length} bytes, smaller than the {headerSize}-byte v{version} header ('{dirVpkPath}').");
        }

        long treeEnd = (long)headerSize + treeSize;
        if (treeEnd > dirBytes.Length)
        {
            throw new InvalidDataException(
                $"VPK truncated directory tree: header declares TreeSize={treeSize} ending at offset {treeEnd}, " +
                $"but file is only {dirBytes.Length} bytes ('{dirVpkPath}').");
        }

        var entries = ParseTree(dirVpkPath, dirBytes, headerSize, (int)treeSize);

        // stable, deterministic order independent of tree iteration order.
        entries.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        return new VpkArchive(dirVpkPath, dirBytes, version, treeSize, headerSize, entries);
    }

    private static List<VpkDirectoryEntry> ParseTree(string path, byte[] bytes, int treeStart, int treeSize)
    {
        var entries = new List<VpkDirectoryEntry>();
        int pos = treeStart;
        int treeEnd = treeStart + treeSize;

        while (true)
        {
            string extension = ReadString(bytes, ref pos, treeEnd, path);
            if (extension.Length == 0)
            {
                break; // end of extension list
            }

            while (true)
            {
                string dirPath = ReadString(bytes, ref pos, treeEnd, path);
                if (dirPath.Length == 0)
                {
                    break; // end of paths for this extension
                }

                while (true)
                {
                    string fileName = ReadString(bytes, ref pos, treeEnd, path);
                    if (fileName.Length == 0)
                    {
                        break; // end of files for this path
                    }

                    var entry = ReadEntry(bytes, ref pos, treeEnd, path, extension, dirPath, fileName);
                    entries.Add(entry);
                }
            }
        }

        return entries;
    }

    private static VpkDirectoryEntry ReadEntry(
        byte[] bytes,
        ref int pos,
        int treeEnd,
        string path,
        string extension,
        string dirPath,
        string fileName)
    {
        // 18-byte fixed entry record.
        const int RecordSize = 4 + 2 + 2 + 4 + 4 + 2;
        if (pos + RecordSize > treeEnd)
        {
            throw new InvalidDataException(
                $"VPK truncated directory entry for '{fileName}': record runs past the declared tree end ('{path}').");
        }

        uint crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
        pos += 4;
        ushort preloadBytes = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos, 2));
        pos += 2;
        ushort archiveIndex = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos, 2));
        pos += 2;
        uint entryOffset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
        pos += 4;
        uint entryLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos, 4));
        pos += 4;
        ushort terminator = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(pos, 2));
        pos += 2;

        if (terminator != EntryTerminator)
        {
            throw new InvalidDataException(
                $"VPK corrupt directory entry for '{fileName}': expected terminator 0x{EntryTerminator:X4}, " +
                $"got 0x{terminator:X4} ('{path}').");
        }

        ReadOnlyMemory<byte> preload = ReadOnlyMemory<byte>.Empty;
        if (preloadBytes > 0)
        {
            if (pos + preloadBytes > treeEnd)
            {
                throw new InvalidDataException(
                    $"VPK truncated preload for '{fileName}': {preloadBytes} preload bytes run past the tree end ('{path}').");
            }
            preload = bytes.AsMemory(pos, preloadBytes);
            pos += preloadBytes;
        }

        return new VpkDirectoryEntry
        {
            FullPath = BuildLogicalPath(dirPath, fileName, extension),
            Extension = extension,
            DirectoryPath = dirPath,
            FileName = fileName,
            Crc32 = crc,
            ArchiveIndex = archiveIndex,
            EntryOffset = entryOffset,
            EntryLength = entryLength,
            PreloadBytes = preload,
        };
    }

    private static string BuildLogicalPath(string dirPath, string fileName, string extension)
    {
        // In the VPK tree a single space means "empty": root path or no extension.
        bool hasDir = dirPath != " ";
        bool hasExt = extension != " ";

        var sb = new StringBuilder();
        if (hasDir)
        {
            sb.Append(dirPath);
            sb.Append('/');
        }
        sb.Append(fileName);
        if (hasExt)
        {
            sb.Append('.');
            sb.Append(extension);
        }
        return sb.ToString();
    }

    private static string ReadString(byte[] bytes, ref int pos, int treeEnd, string path)
    {
        int start = pos;
        while (pos < treeEnd && bytes[pos] != 0)
        {
            pos++;
        }
        if (pos >= treeEnd)
        {
            throw new InvalidDataException(
                $"VPK truncated directory tree: unterminated string starting at offset {start} ('{path}').");
        }
        string s = Encoding.UTF8.GetString(bytes, start, pos - start);
        pos++; // consume NUL
        return s;
    }

    /// <summary>
    /// Resolve and return the complete bytes of one entry: preload prefix followed
    /// by the archive-chunk body. Verifies the stored CRC32 and throws on
    /// mismatch, a missing external <c>_NNN.vpk</c>, or a body that runs past the
    /// backing file. Never returns partial bytes.
    /// </summary>
    public byte[] ReadEntryBytes(VpkDirectoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var result = new byte[entry.TotalLength];
        entry.PreloadBytes.Span.CopyTo(result);

        if (entry.EntryLength > 0)
        {
            ReadOnlySpan<byte> body = ReadBody(entry);
            body.CopyTo(result.AsSpan(entry.PreloadBytes.Length));
        }

        uint actual = Crc32.Compute(result);
        if (actual != entry.Crc32)
        {
            throw new InvalidDataException(
                $"VPK CRC32 mismatch for '{entry.FullPath}': directory records 0x{entry.Crc32:X8}, " +
                $"computed 0x{actual:X8}. Refusing to return corrupt bytes.");
        }

        return result;
    }

    private ReadOnlySpan<byte> ReadBody(VpkDirectoryEntry entry)
    {
        if (entry.IsEmbedded)
        {
            // Body lives in _dir.vpk, after the header+tree (the embedded file-data section).
            long baseOffset = (long)_headerSize + _treeSize;
            long start = baseOffset + entry.EntryOffset;
            long end = start + entry.EntryLength;
            if (end > _dirBytes.Length)
            {
                throw new InvalidDataException(
                    $"VPK embedded body for '{entry.FullPath}' runs past end of directory file: " +
                    $"needs bytes [{start}, {end}) but file is {_dirBytes.Length} bytes ('{_dirVpkPath}').");
            }
            return _dirBytes.AsSpan((int)start, (int)entry.EntryLength);
        }

        // External archive: pak01_<NNN>.vpk
        string archiveName = $"{_archiveBaseName}_{entry.ArchiveIndex.ToString("D3", CultureInfo.InvariantCulture)}.vpk";
        string archivePath = Path.Combine(_archiveDir, archiveName);
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException(
                $"VPK references external archive index {entry.ArchiveIndex} for '{entry.FullPath}', " +
                $"but '{archivePath}' is missing.",
                archivePath);
        }

        long bodyEnd = (long)entry.EntryOffset + entry.EntryLength;
        long archiveLen = new FileInfo(archivePath).Length;
        if (bodyEnd > archiveLen)
        {
            throw new InvalidDataException(
                $"VPK body for '{entry.FullPath}' runs past end of archive '{archivePath}': " +
                $"needs bytes [{entry.EntryOffset}, {bodyEnd}) but archive is {archiveLen} bytes.");
        }

        var body = new byte[entry.EntryLength];
        using var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(entry.EntryOffset, SeekOrigin.Begin);
        fs.ReadExactly(body);
        return body;
    }

    /// <summary>Find one entry by logical path (Ordinal). Returns null if absent.</summary>
    public VpkDirectoryEntry? Find(string fullPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        foreach (var e in _entries)
        {
            if (string.Equals(e.FullPath, fullPath, StringComparison.Ordinal))
            {
                return e;
            }
        }
        return null;
    }
}
