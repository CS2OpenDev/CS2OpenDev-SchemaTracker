// tests for the ELF64 inspector.
//
// Hand-rolls a minimum-viable ELF64 file containing one SHT_DYNSYM section
// with mixed symbol kinds; asserts that only global+func+defined entries are
// counted.

using Cs2SchemaTracker.Host.Modules;

namespace Cs2SchemaTracker.Tests.Modules;

public class ElfInspectorTest
{
    // Symbol bindings
    private const byte STB_LOCAL = 0;
    private const byte STB_GLOBAL = 1;
    private const byte STB_WEAK = 2;

    // Symbol types
    private const byte STT_NOTYPE = 0;
    private const byte STT_OBJECT = 1;
    private const byte STT_FUNC = 2;

    private const ushort SHN_UNDEF = 0;
    private const ushort SHN_TEXT = 1;

    [Xunit.Fact]
    public void Counts_Only_Global_Defined_Function_Symbols()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "elf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var elfPath = Path.Combine(workDir, "fixture.so");

            // Six symbols. Only four should be counted:
            //   1. STB_GLOBAL STT_FUNC, shndx = 1 (defined)          ← count
            //   2. STB_GLOBAL STT_FUNC, shndx = 1 (defined)          ← count
            //   3. STB_GLOBAL STT_FUNC, shndx = 1 (defined)          ← count
            //   4. STB_GLOBAL STT_FUNC, shndx = 1 (defined)          ← count
            //   5. STB_WEAK   STT_FUNC, shndx = 1 (defined, weak)    skip (binding)
            //   6. STB_GLOBAL STT_FUNC, shndx = 0 (undefined import) skip (undef)
            var syms = new[]
            {
                BuildSym(STB_GLOBAL, STT_FUNC, SHN_TEXT),
                BuildSym(STB_GLOBAL, STT_FUNC, SHN_TEXT),
                BuildSym(STB_GLOBAL, STT_FUNC, SHN_TEXT),
                BuildSym(STB_GLOBAL, STT_FUNC, SHN_TEXT),
                BuildSym(STB_WEAK,   STT_FUNC, SHN_TEXT),
                BuildSym(STB_GLOBAL, STT_FUNC, SHN_UNDEF),
                // Also throw in non-function symbols that must be ignored.
                BuildSym(STB_GLOBAL, STT_OBJECT, SHN_TEXT),
                BuildSym(STB_LOCAL,  STT_FUNC,   SHN_TEXT),
            };
            File.WriteAllBytes(elfPath, BuildElf64(syms));

            var (sizeBytes, exportCount) = ElfInspector.Inspect(elfPath);

            Xunit.Assert.Equal(new FileInfo(elfPath).Length, sizeBytes);
            Xunit.Assert.Equal(4, exportCount);
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
    public void Elf_Without_Dynsym_Reports_Zero_Exports()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "elf-nodynsym-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var elfPath = Path.Combine(workDir, "fixture.so");
            File.WriteAllBytes(elfPath, BuildElf64(Array.Empty<byte[]>(), includeDynsym: false));

            var (_, exportCount) = ElfInspector.Inspect(elfPath);
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
    public void Bad_Magic_Throws()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "elf-badmagic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            var path = Path.Combine(workDir, "not-elf.bin");
            // 80 bytes — bigger than the 64-byte header — but no ELF magic.
            File.WriteAllBytes(path, new byte[80]);
            Xunit.Assert.Throws<InvalidDataException>(() => ElfInspector.Inspect(path));
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

    // ---- helpers ----------------------------------------------------------

    private static byte[] BuildSym(byte binding, byte type, ushort shndx)
    {
        var sym = new byte[24];
        // st_name (uint32) at 0 — leave 0
        sym[4] = (byte)((binding << 4) | (type & 0xF));      // st_info
        sym[5] = 0;                                          // st_other
        BitConverter.GetBytes(shndx).CopyTo(sym, 6);         // st_shndx
        // st_value (8), st_size (8) — leave 0
        return sym;
    }

    /// <summary>
    /// Build a minimal ELF64 with up to two sections:
    ///   [0] SHT_NULL (mandatory — first SHT entry is always a null entry)
    ///   [1] SHT_DYNSYM containing the supplied symbols (omitted if
    ///       <paramref name="includeDynsym"/> is false).
    /// </summary>
    private static byte[] BuildElf64(IReadOnlyList<byte[]> symbols, bool includeDynsym = true)
    {
        const int EhdrSize = 64;
        const int ShdrSize = 64;
        const uint SHT_NULL = 0;
        const uint SHT_DYNSYM = 11;

        var sectionCount = includeDynsym ? 2 : 1;
        var symbolBlobSize = symbols.Sum(s => s.Length);

        // Layout: [Ehdr][Section header table][.dynsym data]
        var ehdrEnd = EhdrSize;
        var shTableOffset = ehdrEnd;
        var shTableEnd = shTableOffset + (sectionCount * ShdrSize);
        var dynsymOffset = shTableEnd;
        var totalSize = dynsymOffset + symbolBlobSize;

        var buf = new byte[totalSize];

        // --- ELF header ---
        // e_ident[0..4] = 7F 45 4C 46
        buf[0] = 0x7F;
        buf[1] = (byte)'E';
        buf[2] = (byte)'L';
        buf[3] = (byte)'F';
        buf[4] = 2; // EI_CLASS = ELFCLASS64
        buf[5] = 1; // EI_DATA  = ELFDATA2LSB
        buf[6] = 1; // EI_VERSION = EV_CURRENT
        // OSABI, ABIVERSION, padding = 0

        BitConverter.GetBytes((ushort)3).CopyTo(buf, 16);        // e_type = ET_DYN
        BitConverter.GetBytes((ushort)0x3E).CopyTo(buf, 18);     // e_machine = EM_X86_64
        BitConverter.GetBytes((uint)1).CopyTo(buf, 20);          // e_version
        // e_entry, e_phoff = 0
        BitConverter.GetBytes((ulong)shTableOffset).CopyTo(buf, 40);   // e_shoff
        // e_flags = 0
        BitConverter.GetBytes((ushort)EhdrSize).CopyTo(buf, 52); // e_ehsize
        // e_phentsize = 0, e_phnum = 0
        BitConverter.GetBytes((ushort)ShdrSize).CopyTo(buf, 58);     // e_shentsize
        BitConverter.GetBytes((ushort)sectionCount).CopyTo(buf, 60); // e_shnum
        BitConverter.GetBytes((ushort)0).CopyTo(buf, 62);            // e_shstrndx

        // --- Section header [0]: SHT_NULL (all zeros, already initialised) ---
        // sh_type at offset 4 of the section header:
        BitConverter.GetBytes(SHT_NULL).CopyTo(buf, shTableOffset + 4);

        if (includeDynsym)
        {
            // --- Section header [1]: SHT_DYNSYM ---
            var shdr = shTableOffset + ShdrSize;
            // sh_name = 0 (we don't ship a string table; the inspector doesn't read names)
            BitConverter.GetBytes(SHT_DYNSYM).CopyTo(buf, shdr + 4);                          // sh_type
            // sh_flags = 0 (8 bytes)
            // sh_addr = 0 (8 bytes)
            BitConverter.GetBytes((ulong)dynsymOffset).CopyTo(buf, shdr + 24);                // sh_offset
            BitConverter.GetBytes((ulong)symbolBlobSize).CopyTo(buf, shdr + 32);              // sh_size
            // sh_link = 0 (string table index — inspector doesn't need it)
            // sh_info = 0
            // sh_addralign = 8
            BitConverter.GetBytes((ulong)8).CopyTo(buf, shdr + 48);
            BitConverter.GetBytes((ulong)24).CopyTo(buf, shdr + 56);                          // sh_entsize

            // Symbol data
            var cursor = dynsymOffset;
            foreach (var sym in symbols)
            {
                sym.CopyTo(buf, cursor);
                cursor += sym.Length;
            }
        }

        return buf;
    }
}
