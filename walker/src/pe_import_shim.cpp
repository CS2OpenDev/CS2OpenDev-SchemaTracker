// pe_import_shim.cpp — see pe_import_shim.h for why this exists and the
// fail-loud contract it holds to.

#include "pe_import_shim.h"

#if defined(_WIN32)

#include <windows.h>

#include <cstdint>
#include <cstring>
#include <fstream>
#include <set>

namespace cs2_schema_walker {
namespace pe_import_shim {
namespace {

// Read the whole file. PE rewriting needs random access across headers,
// sections and the appended payload, so we hold it in one buffer.
bool ReadAll(const std::filesystem::path& p, std::vector<uint8_t>* out,
             std::string* err) {
  std::ifstream f(p, std::ios::binary);
  if (!f) {
    *err = "cannot open for read: " + p.string();
    return false;
  }
  f.seekg(0, std::ios::end);
  const std::streamoff n = f.tellg();
  if (n <= 0) {
    *err = "empty or unreadable file: " + p.string();
    return false;
  }
  out->resize(static_cast<size_t>(n));
  f.seekg(0, std::ios::beg);
  f.read(reinterpret_cast<char*>(out->data()), n);
  if (!f) {
    *err = "short read on " + p.string();
    return false;
  }
  return true;
}

template <typename T>
bool Peek(const std::vector<uint8_t>& d, size_t off, T* v) {
  if (off + sizeof(T) > d.size()) return false;
  std::memcpy(v, d.data() + off, sizeof(T));
  return true;
}

template <typename T>
void Poke(std::vector<uint8_t>& d, size_t off, T v) {
  std::memcpy(d.data() + off, &v, sizeof(T));
}

uint32_t AlignUp(uint32_t v, uint32_t a) { return a ? ((v + a - 1) / a) * a : v; }

// Minimal PE64 view: the offsets we need, resolved once and validated.
struct PeView {
  size_t coff = 0;        // file offset of the COFF header
  size_t opt = 0;         // file offset of the optional header
  size_t sect_table = 0;  // file offset of the section header table
  size_t data_dirs = 0;   // file offset of the data directory array
  uint16_t n_sections = 0;
  uint32_t n_rva_and_sizes = 0;
  uint32_t section_align = 0;
  uint32_t file_align = 0;
  uint32_t size_of_headers = 0;

  struct Section {
    uint32_t vsize, vaddr, raw_size, raw_ptr;
  };
  std::vector<Section> sections;

  // RVA -> file offset, honouring each section's raw extent. Returns SIZE_MAX
  // for an RVA that is mapped but has no file backing (zero-fill tail).
  size_t Offset(uint32_t rva) const {
    for (const Section& s : sections) {
      const uint32_t span = s.vsize > s.raw_size ? s.vsize : s.raw_size;
      if (rva >= s.vaddr && rva < s.vaddr + span) {
        const uint32_t delta = rva - s.vaddr;
        if (delta < s.raw_size) return s.raw_ptr + delta;
        return SIZE_MAX;
      }
    }
    return SIZE_MAX;
  }
};

bool ParsePe(const std::vector<uint8_t>& d, PeView* pe, std::string* err) {
  uint16_t mz = 0;
  if (!Peek(d, 0, &mz) || mz != 0x5A4D) {
    *err = "not a PE image (no MZ signature)";
    return false;
  }
  uint32_t pe_off = 0;
  if (!Peek(d, 0x3C, &pe_off)) {
    *err = "truncated DOS header";
    return false;
  }
  uint32_t sig = 0;
  if (!Peek(d, pe_off, &sig) || sig != 0x00004550) {
    *err = "not a PE image (no PE\\0\\0 signature)";
    return false;
  }
  pe->coff = pe_off + 4;
  if (!Peek(d, pe->coff + 2, &pe->n_sections)) {
    *err = "truncated COFF header";
    return false;
  }
  uint16_t opt_size = 0;
  if (!Peek(d, pe->coff + 16, &opt_size)) {
    *err = "truncated COFF header (optional header size)";
    return false;
  }
  pe->opt = pe->coff + 20;
  uint16_t magic = 0;
  if (!Peek(d, pe->opt, &magic) || magic != 0x20B) {
    *err = "not a PE32+ image (this recovery path is x64-only)";
    return false;
  }
  if (!Peek(d, pe->opt + 32, &pe->section_align) ||
      !Peek(d, pe->opt + 36, &pe->file_align) ||
      !Peek(d, pe->opt + 60, &pe->size_of_headers) ||
      !Peek(d, pe->opt + 108, &pe->n_rva_and_sizes)) {
    *err = "truncated optional header";
    return false;
  }
  if (pe->n_rva_and_sizes <= 12) {
    *err = "optional header has too few data directories";
    return false;
  }
  pe->data_dirs = pe->opt + 112;
  pe->sect_table = pe->opt + opt_size;
  for (uint16_t i = 0; i < pe->n_sections; ++i) {
    const size_t b = pe->sect_table + static_cast<size_t>(i) * 40;
    PeView::Section s{};
    if (!Peek(d, b + 8, &s.vsize) || !Peek(d, b + 12, &s.vaddr) ||
        !Peek(d, b + 16, &s.raw_size) || !Peek(d, b + 20, &s.raw_ptr)) {
      *err = "truncated section header table";
      return false;
    }
    pe->sections.push_back(s);
  }
  if (pe->sections.empty()) {
    *err = "image has no sections";
    return false;
  }
  return true;
}

bool ReadCString(const std::vector<uint8_t>& d, size_t off, std::string* out) {
  if (off == SIZE_MAX || off >= d.size()) return false;
  size_t e = off;
  while (e < d.size() && d[e] != 0) ++e;
  if (e >= d.size()) return false;
  out->assign(reinterpret_cast<const char*>(d.data()) + off, e - off);
  return true;
}

// One import descriptor as read from the file.
struct Descriptor {
  uint32_t oft = 0, time_date = 0, forwarder = 0, name_rva = 0, first_thunk = 0;
  std::string dll;
};

bool ReadDescriptors(const std::vector<uint8_t>& d, const PeView& pe,
                     std::vector<Descriptor>* out, std::string* err) {
  uint32_t imp_rva = 0;
  if (!Peek(d, pe.data_dirs + 8, &imp_rva) || imp_rva == 0) {
    *err = "image has no import directory";
    return false;
  }
  const size_t io = pe.Offset(imp_rva);
  if (io == SIZE_MAX) {
    *err = "import directory RVA is not backed by file data";
    return false;
  }
  for (size_t i = 0;; ++i) {
    const size_t b = io + i * 20;
    Descriptor de{};
    if (!Peek(d, b + 0, &de.oft) || !Peek(d, b + 4, &de.time_date) ||
        !Peek(d, b + 8, &de.forwarder) || !Peek(d, b + 12, &de.name_rva) ||
        !Peek(d, b + 16, &de.first_thunk)) {
      *err = "truncated import descriptor array";
      return false;
    }
    if (de.oft == 0 && de.name_rva == 0 && de.first_thunk == 0) break;
    if (!ReadCString(d, pe.Offset(de.name_rva), &de.dll)) {
      *err = "unreadable import DLL name";
      return false;
    }
    out->push_back(de);
  }
  return true;
}

// One entry of a descriptor's thunk array: the raw 64-bit value plus the symbol
// name it denotes (empty for an ordinal import).
struct Thunk {
  uint64_t raw = 0;
  std::string name;
};

bool ReadThunks(const std::vector<uint8_t>& d, const PeView& pe,
                const Descriptor& de, std::vector<Thunk>* out, std::string* err) {
  const uint32_t rva = de.oft ? de.oft : de.first_thunk;
  const size_t t = pe.Offset(rva);
  if (t == SIZE_MAX) {
    *err = "import thunk array for " + de.dll + " is not backed by file data";
    return false;
  }
  for (size_t j = 0;; ++j) {
    Thunk th{};
    if (!Peek(d, t + j * 8, &th.raw)) {
      *err = "truncated import thunk array for " + de.dll;
      return false;
    }
    if (th.raw == 0) break;
    if ((th.raw >> 63) == 0) {
      // Import-by-name: RVA of IMAGE_IMPORT_BY_NAME { WORD hint; char name[]; }
      const size_t ho = pe.Offset(static_cast<uint32_t>(th.raw & 0x7FFFFFFFu));
      if (ho == SIZE_MAX || !ReadCString(d, ho + 2, &th.name)) {
        *err = "unreadable import name entry for " + de.dll;
        return false;
      }
    }
    out->push_back(th);
  }
  return true;
}

}  // namespace

const char* const kShimmableImports[3] = {
    "g_tm_api",
    "?Register@VTm_Zone_Base@@QEAAXXZ",
    "?Unregister@VTm_Zone_Base@@QEAAXXZ",
};

const char* const kShimDllName = "cs2_tier0_tm_shim.dll";

bool IsShimmable(const std::string& symbol) {
  for (const char* s : kShimmableImports) {
    if (symbol == s) return true;
  }
  return false;
}

std::filesystem::path ResolveShimPath() {
  wchar_t buf[MAX_PATH * 2];
  const DWORD n = ::GetModuleFileNameW(nullptr, buf, static_cast<DWORD>(std::size(buf)));
  if (n == 0 || n >= std::size(buf)) return std::filesystem::path(kShimDllName);
  return std::filesystem::path(buf).parent_path() / kShimDllName;
}

namespace {

constexpr const char* kStagingPrefix = "cs2_schema_walker_shim_";

// Remove staging dirs left by walker processes that have since exited.
//
// The patched copy must outlive LoadLibrary, and Windows will not let us delete
// a mapped image, so a run cannot clean up after itself — it can only clean up
// after its predecessors. Without this, every sweep that touches the nine
// affected builds leaks ~9 MB of %TEMP% permanently.
//
// Only directories matching the exact prefix followed by an all-digits PID are
// considered, and only when that PID no longer names a live process — so a
// concurrently running walker's staging dir is never touched.
void SweepStaleStagingDirs(const std::filesystem::path& base) {
  std::error_code ec;
  std::filesystem::directory_iterator it(base, ec), end;
  if (ec) return;
  const DWORD self = ::GetCurrentProcessId();
  for (; it != end; it.increment(ec)) {
    if (ec) return;
    if (!it->is_directory(ec)) continue;
    const std::string name = it->path().filename().string();
    if (name.rfind(kStagingPrefix, 0) != 0) continue;
    const std::string pid_text = name.substr(std::strlen(kStagingPrefix));
    if (pid_text.empty() ||
        pid_text.find_first_not_of("0123456789") != std::string::npos) {
      continue;
    }
    const DWORD pid = static_cast<DWORD>(std::strtoul(pid_text.c_str(), nullptr, 10));
    if (pid == 0 || pid == self) continue;
    // A handle we can open means the process (or a PID-reuse successor) is still
    // around; leave its dir alone. Being conservative here only costs disk.
    HANDLE h = ::OpenProcess(SYNCHRONIZE, FALSE, pid);
    if (h != nullptr) {
      ::CloseHandle(h);
      continue;
    }
    std::error_code rm_ec;
    std::filesystem::remove_all(it->path(), rm_ec);  // best effort
  }
}

}  // namespace

std::filesystem::path ShimStagingDir() {
  std::error_code ec;
  std::filesystem::path base = std::filesystem::temp_directory_path(ec);
  if (ec) base = std::filesystem::current_path();
  // Sweep once per process, before handing out our own dir.
  static const bool swept = [&base]() {
    SweepStaleStagingDirs(base);
    return true;
  }();
  (void)swept;
  // Per-process so concurrent walks (the host may run several) never collide.
  return base / (std::string(kStagingPrefix) + std::to_string(::GetCurrentProcessId()));
}

bool ListImports(const std::filesystem::path& dll_path,
                 std::vector<MissingImport>* out, std::string* err) {
  std::vector<uint8_t> d;
  if (!ReadAll(dll_path, &d, err)) return false;
  PeView pe;
  if (!ParsePe(d, &pe, err)) {
    *err = dll_path.string() + ": " + *err;
    return false;
  }
  std::vector<Descriptor> descs;
  if (!ReadDescriptors(d, pe, &descs, err)) {
    *err = dll_path.string() + ": " + *err;
    return false;
  }
  for (const Descriptor& de : descs) {
    std::vector<Thunk> thunks;
    if (!ReadThunks(d, pe, de, &thunks, err)) {
      *err = dll_path.string() + ": " + *err;
      return false;
    }
    for (const Thunk& th : thunks) {
      if (!th.name.empty()) out->push_back(MissingImport{de.dll, th.name});
    }
  }
  return true;
}

bool FindUnresolvableImports(const std::filesystem::path& dll_path,
                             std::vector<MissingImport>* out, std::string* err) {
  std::vector<uint8_t> d;
  if (!ReadAll(dll_path, &d, err)) return false;
  PeView pe;
  if (!ParsePe(d, &pe, err)) {
    *err = dll_path.string() + ": " + *err;
    return false;
  }
  std::vector<Descriptor> descs;
  if (!ReadDescriptors(d, pe, &descs, err)) {
    *err = dll_path.string() + ": " + *err;
    return false;
  }

  for (const Descriptor& de : descs) {
    // Ask the OS the same question the loader asked. The dependency is already
    // resident in this process (the walker loads tier0 first, and the system
    // DLLs are always present), so GetModuleHandle is the accurate oracle; fall
    // back to a load for anything not yet resident.
    HMODULE dep = ::GetModuleHandleA(de.dll.c_str());
    if (dep == nullptr) {
      dep = ::LoadLibraryExA(de.dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
    }
    if (dep == nullptr) continue;  // unresolvable dependency is a different failure

    std::vector<Thunk> thunks;
    if (!ReadThunks(d, pe, de, &thunks, err)) {
      *err = dll_path.string() + ": " + *err;
      return false;
    }
    for (const Thunk& th : thunks) {
      if (th.name.empty()) continue;  // ordinal import: not shimmable, not inspected
      if (::GetProcAddress(dep, th.name.c_str()) == nullptr) {
        out->push_back(MissingImport{de.dll, th.name});
      }
    }
  }
  return true;
}

bool WriteShimmedCopy(const std::filesystem::path& dll_path,
                      const std::vector<MissingImport>& redirect,
                      const std::string& shim_dll_name,
                      const std::filesystem::path& out_path, std::string* err) {
  std::vector<uint8_t> d;
  if (!ReadAll(dll_path, &d, err)) return false;
  PeView pe;
  if (!ParsePe(d, &pe, err)) {
    *err = dll_path.string() + ": " + *err;
    return false;
  }
  std::vector<Descriptor> descs;
  if (!ReadDescriptors(d, pe, &descs, err)) {
    *err = dll_path.string() + ": " + *err;
    return false;
  }

  // One more section header must fit inside the existing header region; we never
  // relocate section raw data to make room.
  const size_t hdr_end = pe.sect_table + static_cast<size_t>(pe.n_sections) * 40;
  if (hdr_end + 40 > pe.size_of_headers) {
    *err = dll_path.string() +
           ": no room in the section header table for the shim import section";
    return false;
  }

  std::set<std::string> redirect_syms;
  for (const MissingImport& m : redirect) redirect_syms.insert(m.symbol);

  // A rebuilt descriptor: either an untouched copy, or one run of a split
  // dependency. `thunks` is empty for a copy (its arrays stay where they are).
  struct NewDesc {
    bool copy = false;
    Descriptor original{};
    std::vector<uint64_t> thunks;
    uint32_t first_thunk = 0;
    bool to_shim = false;
  };
  std::vector<NewDesc> planned;

  for (const Descriptor& de : descs) {
    std::vector<Thunk> thunks;
    if (!ReadThunks(d, pe, de, &thunks, err)) {
      *err = dll_path.string() + ": " + *err;
      return false;
    }
    bool touches = false;
    for (const Thunk& th : thunks) {
      if (!th.name.empty() && redirect_syms.count(th.name)) touches = true;
    }
    if (!touches) {
      NewDesc nd;
      nd.copy = true;
      nd.original = de;
      planned.push_back(nd);
      continue;
    }
    // Split into maximal runs of same destination so the IAT slots — whose
    // addresses are baked into the module's code — never move.
    size_t start = 0;
    bool cur = !thunks[0].name.empty() && redirect_syms.count(thunks[0].name) > 0;
    for (size_t k = 0; k <= thunks.size(); ++k) {
      const bool is_shim =
          k < thunks.size() && !thunks[k].name.empty() &&
          redirect_syms.count(thunks[k].name) > 0;
      if (k == thunks.size() || is_shim != cur) {
        NewDesc nd;
        nd.copy = false;
        nd.original = de;
        nd.to_shim = cur;
        nd.first_thunk = de.first_thunk + static_cast<uint32_t>(start) * 8;
        for (size_t j = start; j < k; ++j) nd.thunks.push_back(thunks[j].raw);
        planned.push_back(nd);
        start = k;
        cur = is_shim;
      }
    }
  }

  // Lay the payload out: descriptor array, then one null-terminated thunk array
  // per split run, then the shim DLL name.
  const uint32_t desc_bytes = static_cast<uint32_t>((planned.size() + 1) * 20);
  uint32_t cursor = desc_bytes;
  std::vector<uint32_t> thunk_off(planned.size(), 0);
  for (size_t i = 0; i < planned.size(); ++i) {
    if (planned[i].copy) continue;
    thunk_off[i] = cursor;
    cursor += static_cast<uint32_t>((planned[i].thunks.size() + 1) * 8);
  }
  const uint32_t name_off = cursor;
  cursor += static_cast<uint32_t>(shim_dll_name.size() + 1);

  const PeView::Section& last = pe.sections.back();
  const uint32_t last_span = last.vsize > last.raw_size ? last.vsize : last.raw_size;
  const uint32_t new_va = AlignUp(last.vaddr + last_span, pe.section_align);

  std::vector<uint8_t> payload(cursor, 0);
  for (size_t i = 0; i < planned.size(); ++i) {
    const NewDesc& nd = planned[i];
    const size_t b = i * 20;
    if (nd.copy) {
      std::memcpy(payload.data() + b + 0, &nd.original.oft, 4);
      std::memcpy(payload.data() + b + 4, &nd.original.time_date, 4);
      std::memcpy(payload.data() + b + 8, &nd.original.forwarder, 4);
      std::memcpy(payload.data() + b + 12, &nd.original.name_rva, 4);
      std::memcpy(payload.data() + b + 16, &nd.original.first_thunk, 4);
      continue;
    }
    for (size_t j = 0; j < nd.thunks.size(); ++j) {
      std::memcpy(payload.data() + thunk_off[i] + j * 8, &nd.thunks[j], 8);
    }
    const uint32_t oft_rva = new_va + thunk_off[i];
    const uint32_t name_rva = nd.to_shim ? (new_va + name_off) : nd.original.name_rva;
    std::memcpy(payload.data() + b + 0, &oft_rva, 4);
    std::memcpy(payload.data() + b + 4, &nd.original.time_date, 4);
    std::memcpy(payload.data() + b + 8, &nd.original.forwarder, 4);
    std::memcpy(payload.data() + b + 12, &name_rva, 4);
    std::memcpy(payload.data() + b + 16, &nd.first_thunk, 4);
  }
  std::memcpy(payload.data() + name_off, shim_dll_name.data(), shim_dll_name.size());

  // Append the section's raw data at a file-aligned offset.
  const uint32_t raw_ptr = AlignUp(static_cast<uint32_t>(d.size()), pe.file_align);
  const uint32_t raw_size = AlignUp(static_cast<uint32_t>(payload.size()), pe.file_align);
  d.resize(raw_ptr, 0);
  d.insert(d.end(), payload.begin(), payload.end());
  d.resize(static_cast<size_t>(raw_ptr) + raw_size, 0);

  // Section header. Field offsets: Name(0) VirtualSize(8) VirtualAddress(12)
  // SizeOfRawData(16) PointerToRawData(20) PointerToRelocations(24)
  // PointerToLinenumbers(28) NumberOfRelocations(32) NumberOfLinenumbers(34)
  // Characteristics(36) — the header is exactly 40 bytes.
  const size_t sh = hdr_end;
  static const char kName[8] = {'.', 'c', 's', '2', 't', 'm', 0, 0};
  std::memcpy(d.data() + sh, kName, 8);
  Poke<uint32_t>(d, sh + 8, static_cast<uint32_t>(payload.size()));
  Poke<uint32_t>(d, sh + 12, new_va);
  Poke<uint32_t>(d, sh + 16, raw_size);
  Poke<uint32_t>(d, sh + 20, raw_ptr);
  Poke<uint32_t>(d, sh + 24, 0);
  Poke<uint32_t>(d, sh + 28, 0);
  Poke<uint16_t>(d, sh + 32, 0);
  Poke<uint16_t>(d, sh + 34, 0);
  Poke<uint32_t>(d, sh + 36, IMAGE_SCN_CNT_INITIALIZED_DATA | IMAGE_SCN_MEM_READ);

  Poke<uint16_t>(d, pe.coff + 2, static_cast<uint16_t>(pe.n_sections + 1));
  Poke<uint32_t>(d, pe.opt + 56,
                 new_va + AlignUp(static_cast<uint32_t>(payload.size()),
                                  pe.section_align));  // SizeOfImage
  Poke<uint32_t>(d, pe.data_dirs + 8, new_va);         // Import directory RVA
  Poke<uint32_t>(d, pe.data_dirs + 12, desc_bytes);    // Import directory size
  // A bound-import table would describe the OLD descriptor array; drop it.
  Poke<uint32_t>(d, pe.data_dirs + 11 * 8, 0);
  Poke<uint32_t>(d, pe.data_dirs + 11 * 8 + 4, 0);

  std::error_code ec;
  std::filesystem::create_directories(out_path.parent_path(), ec);
  std::ofstream o(out_path, std::ios::binary | std::ios::trunc);
  if (!o) {
    *err = "cannot open for write: " + out_path.string();
    return false;
  }
  o.write(reinterpret_cast<const char*>(d.data()), static_cast<std::streamsize>(d.size()));
  if (!o) {
    *err = "short write to " + out_path.string();
    return false;
  }
  return true;
}

}  // namespace pe_import_shim
}  // namespace cs2_schema_walker

#endif  // defined(_WIN32)
