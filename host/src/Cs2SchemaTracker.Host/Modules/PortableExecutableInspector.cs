// PE/COFF inspector for module manifest export-count.
//
// Reads the export directory of a Windows PE binary (.dll / .exe) using
// System.Reflection.Metadata.PEReader (BCL, compatible — no third-party
// PE parsing dependency).
//
// Export directory format (IMAGE_EXPORT_DIRECTORY, 40 bytes total):
//   Offset 0   uint32 Characteristics
//   Offset 4   uint32 TimeDateStamp
//   Offset 8   uint16 MajorVersion
//   Offset 10  uint16 MinorVersion
//   Offset 12  uint32 Name (RVA to ASCII module name)
//   Offset 16  uint32 Base
//   Offset 20  uint32 NumberOfFunctions
//   Offset 24  uint32 NumberOfNames        <-- we read this
//   Offset 28  uint32 AddressOfFunctions   (RVA)
//   Offset 32  uint32 AddressOfNames       (RVA)
//   Offset 36  uint32 AddressOfNameOrdinals(RVA)
// Reference: PE/COFF Specification §6.3 (Microsoft, Aug 2024 revision).

using System.Reflection.PortableExecutable;

namespace Cs2SchemaTracker.Host.Modules;

internal static class PortableExecutableInspector
{
    /// <summary>
    /// Read the PE file at <paramref name="path"/> and return its on-disk size
    /// plus the number of named exports declared in its export directory.
    /// </summary>
    /// <remarks>
    /// A PE file with no export directory (data directory entry is zero) has
    /// <c>exportCount == 0</c>. This is not an error — most .exe files have no
    /// exports. Throws <see cref="InvalidDataException"/> on malformed PE.
    /// </remarks>
    public static (long SizeBytes, int ExportCount) Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var sizeBytes = fs.Length;

        // PEReader takes ownership of the stream; we wrap in a using to dispose.
        using var pe = new PEReader(fs, PEStreamOptions.LeaveOpen);

        var headers = pe.PEHeaders;
        if (headers.PEHeader is null)
        {
            throw new InvalidDataException(
                $"PortableExecutableInspector: '{path}' has no PE optional header.");
        }

        var exportDir = headers.PEHeader.ExportTableDirectory;
        if (exportDir.RelativeVirtualAddress == 0 || exportDir.Size == 0)
        {
            // No export directory at all → zero exports. Common for .exe files.
            return (sizeBytes, 0);
        }

        // Translate the RVA to a file offset via the section that contains it.
        var sectionIndex = headers.GetContainingSectionIndex(exportDir.RelativeVirtualAddress);
        if (sectionIndex < 0)
        {
            throw new InvalidDataException(
                $"PortableExecutableInspector: '{path}' export-directory RVA " +
                $"0x{exportDir.RelativeVirtualAddress:X8} is not contained in any section.");
        }

        var section = headers.SectionHeaders[sectionIndex];
        var fileOffset = section.PointerToRawData
            + (exportDir.RelativeVirtualAddress - section.VirtualAddress);

        // Require enough bytes to read through the NumberOfNames field (offset
        // 24, 4 bytes ⇒ need at least 28 bytes from `fileOffset`).
        const int NumberOfNamesOffset = 24;
        const int RequiredBytes = NumberOfNamesOffset + sizeof(uint);
        if (fileOffset < 0 || fileOffset + RequiredBytes > sizeBytes)
        {
            throw new InvalidDataException(
                $"PortableExecutableInspector: '{path}' export directory at file offset " +
                $"{fileOffset} is truncated (need {RequiredBytes} bytes, file size {sizeBytes}).");
        }

        fs.Position = fileOffset + NumberOfNamesOffset;
        Span<byte> buf = stackalloc byte[sizeof(uint)];
        var read = fs.Read(buf);
        if (read != sizeof(uint))
        {
            throw new InvalidDataException(
                $"PortableExecutableInspector: '{path}' short read at export-directory NumberOfNames.");
        }
        var numberOfNames = BitConverter.ToUInt32(buf);

        // uint32 → int: PE export counts in practice are well under int.MaxValue.
        if (numberOfNames > int.MaxValue)
        {
            throw new InvalidDataException(
                $"PortableExecutableInspector: '{path}' NumberOfNames={numberOfNames} exceeds int.MaxValue.");
        }

        return (sizeBytes, (int)numberOfNames);
    }
}
