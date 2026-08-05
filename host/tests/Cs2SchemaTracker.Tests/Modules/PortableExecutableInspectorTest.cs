// tests for the PE/COFF inspector.
//
// Two flavours of fixture:
//   1. A hand-rolled minimum-viable PE with an export directory declaring
//      NumberOfNames=3 — exact-count assertion.
//   2. The compiled Cs2SchemaTracker.Host.dll (or the .NET runtime PE we built
//      against) — smoke test that real PEs parse without throwing.

using System.Reflection;

using Cs2SchemaTracker.Host.Modules;

namespace Cs2SchemaTracker.Tests.Modules;

public class PortableExecutableInspectorTest
{
    [Xunit.Fact]
    public void Synthetic_Pe_With_Three_Exports_Reports_Three()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var pePath = Path.Combine(workDir, "fixture.dll");
            File.WriteAllBytes(pePath, BuildMinimalPe(numberOfNames: 3));

            var (sizeBytes, exportCount) = PortableExecutableInspector.Inspect(pePath);

            Xunit.Assert.Equal(new FileInfo(pePath).Length, sizeBytes);
            Xunit.Assert.Equal(3, exportCount);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Synthetic_Pe_With_No_Export_Directory_Reports_Zero()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "pe-noexp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var pePath = Path.Combine(workDir, "fixture.exe");
            File.WriteAllBytes(pePath, BuildMinimalPe(numberOfNames: 0, omitExportDirectory: true));

            var (_, exportCount) = PortableExecutableInspector.Inspect(pePath);
            Xunit.Assert.Equal(0, exportCount);
        }
        finally
        {
            if (Directory.Exists(workDir))
            {
                try
                { Directory.Delete(workDir, recursive: true); }
                catch { /* best effort */ }
            }
        }
    }

    [Xunit.Fact]
    public void Real_Host_Assembly_Parses_Without_Throwing()
    {
        // The PE we're running on Windows ships with the test runner; on a
        // cross-OS test pass (Linux runner) the same managed assembly is built
        // as a PE/COFF file too — PEReader is OS-agnostic.
        var hostAsmPath = typeof(Cs2SchemaTracker.Host.Modules.ModuleManifestEmitter).Assembly.Location;
        Xunit.Assert.True(File.Exists(hostAsmPath), $"host assembly missing: {hostAsmPath}");

        var (sizeBytes, exportCount) = PortableExecutableInspector.Inspect(hostAsmPath);
        Xunit.Assert.True(sizeBytes > 0);
        Xunit.Assert.True(exportCount >= 0, "exportCount must be non-negative");
    }

    // ---- minimum-viable PE builder ---------------------------------------
    //
    // Layout (all little-endian):
    //   [0x00..0x40)   DOS header — only 'MZ' + e_lfanew at 0x3C matter.
    //   [0x40..0x48)   PE signature "PE\0\0"
    //   [0x48..0x58)   COFF header (20 bytes)
    //   [0x58..0xD8)   Optional header (PE32+, 240 bytes incl. 16 data dirs)
    //   [0xD8..0x100)  One section header (.text, 40 bytes), padding to 0x200
    //   [0x200..)      Section raw data — we put the IMAGE_EXPORT_DIRECTORY here
    //                  with NumberOfFunctions=NumberOfNames=<arg>.
    //
    // We don't need a valid CLR header, valid relocations, or a real entry
    // point — PEReader is happy to parse a well-formed file structure.
    private static byte[] BuildMinimalPe(int numberOfNames, bool omitExportDirectory = false)
    {
        const int PeHeaderOffset = 0x80;          // arbitrary, beyond stub
        const int SectionVA = 0x1000;
        const int SectionFileOffset = 0x200;
        const int SectionRawSize = 0x200;
        const int FileSize = SectionFileOffset + SectionRawSize;

        var buf = new byte[FileSize];

        // --- DOS header ---
        buf[0] = (byte)'M';
        buf[1] = (byte)'Z';
        // e_lfanew at offset 0x3C points to the PE signature.
        BitConverter.GetBytes(PeHeaderOffset).CopyTo(buf, 0x3C);

        // --- PE signature ---
        buf[PeHeaderOffset + 0] = (byte)'P';
        buf[PeHeaderOffset + 1] = (byte)'E';
        // [2..4) are zero — already initialised.

        // --- COFF header (20 bytes, starts at PeHeaderOffset + 4) ---
        var coff = PeHeaderOffset + 4;
        // Machine = 0x8664 (AMD64)
        BitConverter.GetBytes((ushort)0x8664).CopyTo(buf, coff + 0);
        // NumberOfSections = 1
        BitConverter.GetBytes((ushort)1).CopyTo(buf, coff + 2);
        // TimeDateStamp, PointerToSymbolTable, NumberOfSymbols — leave zero.
        // SizeOfOptionalHeader = 240 (PE32+ standard + 16 data dirs * 8)
        BitConverter.GetBytes((ushort)240).CopyTo(buf, coff + 16);
        // Characteristics — IMAGE_FILE_EXECUTABLE_IMAGE (0x0002) | IMAGE_FILE_DLL (0x2000)
        BitConverter.GetBytes((ushort)(0x0002 | 0x2000)).CopyTo(buf, coff + 18);

        // --- Optional header (PE32+) ---
        var opt = coff + 20;
        // Magic = 0x20B (PE32+)
        BitConverter.GetBytes((ushort)0x20B).CopyTo(buf, opt + 0);
        // MajorLinkerVersion / MinorLinkerVersion — 14, 0 (arbitrary).
        buf[opt + 2] = 14;
        buf[opt + 3] = 0;
        // SizeOfCode = SectionRawSize
        BitConverter.GetBytes((uint)SectionRawSize).CopyTo(buf, opt + 4);
        // SizeOfInitializedData = 0
        // SizeOfUninitializedData = 0
        // AddressOfEntryPoint = SectionVA
        BitConverter.GetBytes((uint)SectionVA).CopyTo(buf, opt + 16);
        // BaseOfCode = SectionVA
        BitConverter.GetBytes((uint)SectionVA).CopyTo(buf, opt + 20);
        // ImageBase (ulong, PE32+ has no BaseOfData) at offset 24 = 0x140000000
        BitConverter.GetBytes((ulong)0x140000000UL).CopyTo(buf, opt + 24);
        // SectionAlignment = 0x1000, FileAlignment = 0x200
        BitConverter.GetBytes((uint)0x1000).CopyTo(buf, opt + 32);
        BitConverter.GetBytes((uint)0x200).CopyTo(buf, opt + 36);
        // MajorOperatingSystemVersion, MinorOSVersion, MajorImageVersion, MinorImageVersion,
        // MajorSubsystemVersion = 6, MinorSubsystemVersion = 0
        BitConverter.GetBytes((ushort)6).CopyTo(buf, opt + 48); // MajorOS
        BitConverter.GetBytes((ushort)6).CopyTo(buf, opt + 56); // MajorSubsystem
        // Win32VersionValue = 0
        // SizeOfImage = SectionVA + SectionAlignment
        BitConverter.GetBytes((uint)(SectionVA + 0x1000)).CopyTo(buf, opt + 64);
        // SizeOfHeaders = SectionFileOffset
        BitConverter.GetBytes((uint)SectionFileOffset).CopyTo(buf, opt + 68);
        // CheckSum = 0
        // Subsystem = IMAGE_SUBSYSTEM_WINDOWS_CUI (3)
        BitConverter.GetBytes((ushort)3).CopyTo(buf, opt + 76);
        // DllCharacteristics = 0
        // SizeOfStackReserve / Commit / Heap Reserve / Commit — leave 0.
        // LoaderFlags = 0
        // NumberOfRvaAndSizes = 16
        BitConverter.GetBytes((uint)16).CopyTo(buf, opt + 108);

        // Data directories start at opt + 112; each is (uint VirtualAddress, uint Size), 8 bytes.
        // Index 0 = Export Table.
        var dataDirs = opt + 112;
        if (!omitExportDirectory)
        {
            BitConverter.GetBytes((uint)SectionVA).CopyTo(buf, dataDirs + 0);
            BitConverter.GetBytes((uint)40).CopyTo(buf, dataDirs + 4);    // sizeof IMAGE_EXPORT_DIRECTORY
        }
        // All other directories remain zero.

        // --- Section header (40 bytes) ---
        var sectionHdr = opt + 240; // = coff + 20 + 240 = PeHeaderOffset + 4 + 260
        // Name ".text\0\0\0"
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(".text");
        nameBytes.CopyTo(buf, sectionHdr + 0);
        // VirtualSize
        BitConverter.GetBytes((uint)SectionRawSize).CopyTo(buf, sectionHdr + 8);
        // VirtualAddress
        BitConverter.GetBytes((uint)SectionVA).CopyTo(buf, sectionHdr + 12);
        // SizeOfRawData
        BitConverter.GetBytes((uint)SectionRawSize).CopyTo(buf, sectionHdr + 16);
        // PointerToRawData
        BitConverter.GetBytes((uint)SectionFileOffset).CopyTo(buf, sectionHdr + 20);
        // Characteristics — IMAGE_SCN_CNT_CODE | IMAGE_SCN_MEM_EXECUTE | IMAGE_SCN_MEM_READ
        BitConverter.GetBytes((uint)0x60000020).CopyTo(buf, sectionHdr + 36);

        // --- Export directory (40 bytes) inside the section raw data ---
        // We populate NumberOfFunctions == NumberOfNames == numberOfNames.
        var exp = SectionFileOffset;
        // Characteristics, TimeDateStamp, MajorVersion, MinorVersion = 0.
        // Name (RVA) = 0 (consumer doesn't dereference).
        // Base = 1 (conventional)
        BitConverter.GetBytes((uint)1).CopyTo(buf, exp + 16);
        // NumberOfFunctions
        BitConverter.GetBytes((uint)numberOfNames).CopyTo(buf, exp + 20);
        // NumberOfNames        <-- the value the inspector reads
        BitConverter.GetBytes((uint)numberOfNames).CopyTo(buf, exp + 24);

        return buf;
    }
}
