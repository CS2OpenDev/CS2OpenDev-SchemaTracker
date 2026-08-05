// ELF64 inspector for module manifest export-count.
//
// Hand-rolled minimal ELF64 parser (no third-party dep). Counts global
// function symbols in the `.dynsym` section — i.e. symbols that would be
// resolvable when another module dlopen()s this one. This matches the spirit
// of "exports" on Linux: defined functions visible to dynamic linkage.
//
// References:
//   System V ABI, ELF specification §1.4 (file header), §1.7 (section header),
//   §1.8 (symbol table). All numeric fields are little-endian on x86_64.
//
// Structures (offsets in bytes):
//   Elf64_Ehdr (64 bytes):
//     0  e_ident[16]      (magic, class, data, version, ...)
//     16 uint16 e_type
//     18 uint16 e_machine
//     20 uint32 e_version
//     24 uint64 e_entry
//     32 uint64 e_phoff
//     40 uint64 e_shoff       <-- section header table file offset
//     48 uint32 e_flags
//     52 uint16 e_ehsize
//     54 uint16 e_phentsize
//     56 uint16 e_phnum
//     58 uint16 e_shentsize
//     60 uint16 e_shnum
//     62 uint16 e_shstrndx
//
//   Elf64_Shdr (64 bytes):
//     0  uint32 sh_name
//     4  uint32 sh_type        <-- SHT_DYNSYM == 11
//     8  uint64 sh_flags
//     16 uint64 sh_addr
//     24 uint64 sh_offset      <-- file offset of the section data
//     32 uint64 sh_size        <-- size in bytes
//     40 uint32 sh_link
//     44 uint32 sh_info
//     48 uint64 sh_addralign
//     56 uint64 sh_entsize     <-- 24 for symtab entries
//
//   Elf64_Sym (24 bytes):
//     0  uint32 st_name
//     4  uint8  st_info        <-- bind = (st_info >> 4), type = (st_info & 0xF)
//     5  uint8  st_other
//     6  uint16 st_shndx       <-- 0 (SHN_UNDEF) ⇒ undefined import, not an export
//     8  uint64 st_value
//     16 uint64 st_size

namespace Cs2SchemaTracker.Host.Modules;

internal static class ElfInspector
{
    private const int ElfHeaderSize = 64;
    private const int SectionHeaderSize = 64;
    private const int Sym64Size = 24;

    // Section types
    private const uint SHT_DYNSYM = 11;

    // Symbol bindings (high nibble of st_info)
    private const byte STB_GLOBAL = 1;

    // Symbol types (low nibble of st_info)
    private const byte STT_FUNC = 2;

    // Special section index
    private const ushort SHN_UNDEF = 0;

    /// <summary>
    /// Read the ELF64 file at <paramref name="path"/> and return its on-disk
    /// size plus the count of global, defined function symbols in the
    /// <c>.dynsym</c> section. Throws <see cref="InvalidDataException"/> on
    /// malformed input.
    /// </summary>
    public static (long SizeBytes, int ExportCount) Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        var bytes = File.ReadAllBytes(path);
        var sizeBytes = (long)bytes.Length;

        if (bytes.Length < ElfHeaderSize)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' is {bytes.Length} bytes, smaller than the ELF header (64).");
        }

        var span = bytes.AsSpan();

        // Validate magic 7F 'E' 'L' 'F' and ELFCLASS64 (e_ident[4] == 2).
        if (span[0] != 0x7F || span[1] != (byte)'E' || span[2] != (byte)'L' || span[3] != (byte)'F')
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' missing ELF magic.");
        }
        var elfClass = span[4];
        if (elfClass != 2)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' ELFCLASS={elfClass}; only ELFCLASS64 (2) is supported.");
        }
        var elfData = span[5];
        if (elfData != 1)
        {
            // ELFDATA2LSB == 1. All current CS2 Linux binaries are little-endian
            // x86_64; refuse big-endian to keep parsing simple.
            throw new InvalidDataException(
                $"ElfInspector: '{path}' ELFDATA={elfData}; only little-endian (1) is supported.");
        }

        var eShoff = BitConverter.ToUInt64(span.Slice(40, 8));
        var eShentsize = BitConverter.ToUInt16(span.Slice(58, 2));
        var eShnum = BitConverter.ToUInt16(span.Slice(60, 2));

        if (eShentsize != SectionHeaderSize)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' e_shentsize={eShentsize}, expected {SectionHeaderSize}.");
        }

        // Validate that the section header table fits in the file.
        var shTableEnd = eShoff + ((ulong)eShnum * eShentsize);
        if (shTableEnd > (ulong)bytes.Length)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' section header table extends past EOF " +
                $"(end=0x{shTableEnd:X}, file size=0x{bytes.Length:X}).");
        }

        // Scan section headers for the (unique) SHT_DYNSYM section.
        ulong? dynsymOffset = null;
        ulong dynsymSize = 0;
        ulong dynsymEntsize = 0;
        for (var i = 0; i < eShnum; i++)
        {
            var shdrStart = (int)(eShoff + (ulong)((long)i * SectionHeaderSize));
            var shdr = span.Slice(shdrStart, SectionHeaderSize);
            var shType = BitConverter.ToUInt32(shdr.Slice(4, 4));
            if (shType != SHT_DYNSYM)
            {
                continue;
            }
            if (dynsymOffset is not null)
            {
                throw new InvalidDataException(
                    $"ElfInspector: '{path}' has multiple SHT_DYNSYM sections.");
            }
            dynsymOffset = BitConverter.ToUInt64(shdr.Slice(24, 8));
            dynsymSize = BitConverter.ToUInt64(shdr.Slice(32, 8));
            dynsymEntsize = BitConverter.ToUInt64(shdr.Slice(56, 8));
        }

        if (dynsymOffset is null)
        {
            // ELF object with no .dynsym (e.g. statically-linked, or a stripped
            // shared library) → zero exports. Not an error.
            return (sizeBytes, 0);
        }

        if (dynsymEntsize != Sym64Size)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' .dynsym sh_entsize={dynsymEntsize}, expected {Sym64Size}.");
        }
        if (dynsymSize % Sym64Size != 0)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' .dynsym sh_size={dynsymSize} is not a multiple of {Sym64Size}.");
        }
        var dynsymEnd = dynsymOffset.Value + dynsymSize;
        if (dynsymEnd > (ulong)bytes.Length)
        {
            throw new InvalidDataException(
                $"ElfInspector: '{path}' .dynsym section extends past EOF " +
                $"(end=0x{dynsymEnd:X}, file size=0x{bytes.Length:X}).");
        }

        var entryCount = dynsymSize / Sym64Size;
        var exportCount = 0;
        for (ulong i = 0; i < entryCount; i++)
        {
            var symStart = (int)(dynsymOffset.Value + (i * Sym64Size));
            var sym = span.Slice(symStart, Sym64Size);
            var stInfo = sym[4];
            var stShndx = BitConverter.ToUInt16(sym.Slice(6, 2));

            var bind = (byte)(stInfo >> 4);
            var type = (byte)(stInfo & 0x0F);

            if (bind == STB_GLOBAL && type == STT_FUNC && stShndx != SHN_UNDEF)
            {
                exportCount++;
            }
        }

        return (sizeBytes, exportCount);
    }
}
