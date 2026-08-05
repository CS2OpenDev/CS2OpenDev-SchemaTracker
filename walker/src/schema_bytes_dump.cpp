// DIAGNOSTIC — see schema_bytes_dump.h. Raw byte-dump of the live schema class-
// binding container + sampled records, to stderr.
//
// Every Source 2 struct comes from the pinned hl2sdk headers via sdk_schema.h;
// no layout is re-declared. The dumper reads RAW BYTES at offsets it does NOT trust
// (that is the whole point — it is deriving the V1 offsets), always through the
// SEH-guarded tshash_compat readers, so a wrong offset can never fault.
// Reachable only from the `dump-schema-bytes` subcommand; the walk/emit path is
// untouched, so committed-era artifact bytes are unaffected.
#include "schema_bytes_dump.h"

#include "engine_boot.h"    // BootEngineForConVars
#include "loader.h"         // LoadInProcessEnvironment, RetrySchemaRegistrationIfEmpty
#include "sdk_schema.h"     // CSchemaSystem / CSchemaSystemTypeScope (HL2SDK)
#include "tshash_compat.h"  // SafeReadPtr2023 / SafeReadBytes2023 / SafeReadCString2023

#include <cinttypes>
#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace cs2_schema_walker {
namespace {

using tshash_compat::LooksLikePointer2023;
using tshash_compat::SafeReadBytes2023;
using tshash_compat::SafeReadCString2023;
using tshash_compat::SafeReadPtr2023;

// ---- tunables (bounded so the dump can never run away) -----------------------
constexpr std::size_t kContainerBackShift = 32;  // start the window this far BEFORE &m_ClassBindings
constexpr std::size_t kContainerWindow = 384;    // total bytes of the container head window
constexpr std::size_t kRecordWindow = 176;       // bytes dumped per sampled record
constexpr int kMaxRecordsDumped = 12;            // records dumped per focus scope
constexpr int kMaxRecordsCollect = 64;           // records collected before dumping
constexpr int kProbeDepth = 3;                   // pointer-chase depth to find records
constexpr int kProbeBudget = 60000;              // total guarded reads for the record hunt

void L(const char* fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  std::vfprintf(stderr, fmt, ap);
  va_end(ap);
}

// Copy an identifier-ish C-string (schema class/enum/field/type name) at `p`.
// Returns true iff it reads as a printable, 2..127-char, C-identifier-leading name.
bool PeekIdentifier(std::uint64_t p, char* out, std::size_t cap) {
  if (!LooksLikePointer2023(p)) return false;
  char raw[160];
  if (!SafeReadCString2023(reinterpret_cast<const char*>(static_cast<std::uintptr_t>(p)),
                           raw, sizeof(raw)))
    return false;
  std::size_t len = std::strlen(raw);
  if (len < 2 || len >= 128) return false;
  unsigned char c0 = static_cast<unsigned char>(raw[0]);
  if (!((c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z') || c0 == '_')) return false;
  for (std::size_t i = 0; i < len; ++i) {
    unsigned char c = static_cast<unsigned char>(raw[i]);
    if (c < 32 || c > 126) return false;
  }
  std::snprintf(out, cap, "%s", raw);
  return true;
}

// Looser: copy any printable C-string (type names carry spaces/<>/:: — e.g.
// "CUtlVector< thinkfunc_t >"). Used only for display annotation.
bool PeekPrintable(std::uint64_t p, char* out, std::size_t cap) {
  if (!LooksLikePointer2023(p)) return false;
  char raw[160];
  if (!SafeReadCString2023(reinterpret_cast<const char*>(static_cast<std::uintptr_t>(p)),
                           raw, sizeof(raw)))
    return false;
  std::size_t len = std::strlen(raw);
  if (len < 2 || len >= 128) return false;
  for (std::size_t i = 0; i < len; ++i) {
    unsigned char c = static_cast<unsigned char>(raw[i]);
    if (c < 32 || c > 126) return false;
  }
  std::snprintf(out, cap, "%s", raw);
  return true;
}

// Does the object at `p` have a name pointer at sub-offset {0,8,16}? If so it is a
// RECORD candidate (class/enum/field/base). Reports the winning sub-offset + name.
bool LooksLikeRecord(std::uint64_t p, std::size_t* name_sub, char* name, std::size_t cap) {
  for (std::size_t sub : {std::size_t(0), std::size_t(8), std::size_t(16)}) {
    std::uint64_t namep = 0;
    if (!SafeReadPtr2023(reinterpret_cast<const void*>(static_cast<std::uintptr_t>(p) + sub),
                         &namep))
      continue;
    if (PeekIdentifier(namep, name, cap)) {
      *name_sub = sub;
      return true;
    }
  }
  return false;
}

// Classic hex+ascii dump with a per-8-byte SLOT annotation (offset relative to
// `anchor`, u64 value, the two int32 halves, and — for pointer slots — a peek at the
// pointee: a resolved C-string, or a record name, or a "== scope" flag).
void DumpAnnotated(const char* label, std::uint64_t addr, std::size_t nbytes,
                   std::uint64_t anchor, std::uint64_t scope_addr) {
  L("    %s @ 0x%016" PRIx64 "  (len %zu; [+off] is relative to 0x%016" PRIx64 ")\n",
    label, addr, nbytes, anchor);

  // 16-byte hex+ascii rows.
  for (std::size_t off = 0; off < nbytes; off += 16) {
    unsigned char row[16];
    std::size_t got = 0;
    for (; got < 16 && (off + got) < nbytes; ++got) {
      if (!SafeReadBytes2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(addr) + off + got),
                             &row[got], 1)) {
        break;
      }
    }
    std::int64_t rel = static_cast<std::int64_t>(addr + off) - static_cast<std::int64_t>(anchor);
    L("      [%+5" PRId64 "] ", rel);
    for (std::size_t i = 0; i < 16; ++i) {
      if (i < got)
        L("%02x ", row[i]);
      else
        L("?? ");
      if (i == 7) L(" ");
    }
    L(" |");
    for (std::size_t i = 0; i < 16; ++i) {
      if (i < got) {
        unsigned char c = row[i];
        L("%c", (c >= 32 && c <= 126) ? c : '.');
      } else {
        L(" ");
      }
    }
    L("|\n");
  }

  // Per-8-byte slot annotation.
  for (std::size_t off = 0; off + 8 <= nbytes; off += 8) {
    std::uint64_t v = 0;
    std::int64_t rel = static_cast<std::int64_t>(addr + off) - static_cast<std::int64_t>(anchor);
    if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                             static_cast<std::uintptr_t>(addr) + off),
                         &v)) {
      L("      slot[%+5" PRId64 "] (unreadable)\n", rel);
      continue;
    }
    std::int32_t lo = static_cast<std::int32_t>(v & 0xffffffffu);
    std::int32_t hi = static_cast<std::int32_t>(v >> 32);
    char ann[256];
    ann[0] = '\0';
    if (scope_addr != 0 && v == scope_addr) {
      std::snprintf(ann, sizeof(ann), "  <== THIS SCOPE ptr (m_pTypeScope candidate)");
    } else if (LooksLikePointer2023(v)) {
      char s[160];
      std::size_t nsub = 0;
      char rn[160];
      if (PeekPrintable(v, s, sizeof(s))) {
        std::snprintf(ann, sizeof(ann), "  ptr-> STR \"%s\"", s);
      } else if (LooksLikeRecord(v, &nsub, rn, sizeof(rn))) {
        std::snprintf(ann, sizeof(ann),
                      "  ptr-> RECORD{name@+%zu=\"%s\"}", nsub, rn);
      } else {
        // Peek first 16 bytes of the pointee so raw structure is visible.
        unsigned char pk[16];
        std::size_t g = 0;
        for (; g < 16; ++g) {
          if (!SafeReadBytes2023(reinterpret_cast<const void*>(
                                     static_cast<std::uintptr_t>(v) + g),
                                 &pk[g], 1))
            break;
        }
        char hex[64];
        int hp = 0;
        for (std::size_t i = 0; i < g && hp < 60; ++i) hp += std::snprintf(hex + hp, 64 - hp, "%02x", pk[i]);
        std::snprintf(ann, sizeof(ann), "  ptr-> [%s%s]", hex, g == 16 ? "..." : "");
      }
    } else {
      std::snprintf(ann, sizeof(ann), "  int i32=(%d, %d)", lo, hi);
    }
    L("      slot[%+5" PRId64 "] = 0x%016" PRIx64 "  u64=%" PRIu64 "%s\n", rel, v, v, ann);
  }
}

// ---- record hunt: geometry-agnostic pointer-chase from the container window -----
// We do NOT yet know V1's bucket/pool geometry, so instead of assuming it we chase
// every pointer in the container window (bounded depth + read budget) and collect
// any object that presents a name pointer (LooksLikeRecord). Bucket heads reach
// records in ~2 hops (bucket -> HashFixedData_t -> record); pool blobs similar. This
// harvests real record bytes regardless of the exact (unknown) V1 container layout.
struct RecordHunt {
  std::vector<std::uint64_t> records;  // deduped record addresses (name-bearing)
  std::vector<std::uint64_t> visited;  // deduped pointers already chased
  int reads = 0;

  bool seen(std::vector<std::uint64_t>& v, std::uint64_t p) {
    for (std::uint64_t x : v)
      if (x == p) return true;
    v.push_back(p);
    return false;
  }

  void chase(std::uint64_t p, int depth) {
    if (depth > kProbeDepth) return;
    if (!LooksLikePointer2023(p)) return;
    if (reads++ > kProbeBudget) return;
    if (seen(visited, p)) return;

    std::size_t nsub = 0;
    char nm[160];
    if (LooksLikeRecord(p, &nsub, nm, sizeof(nm))) {
      // Record found. Collect it (deduped) up to the cap, then STOP — do NOT descend
      // into a record; its own fields carry names and would explode the hunt.
      bool already = false;
      for (std::uint64_t x : records)
        if (x == p) {
          already = true;
          break;
        }
      if (!already && static_cast<int>(records.size()) < kMaxRecordsCollect)
        records.push_back(p);
      return;
    }
    // Not a record itself — follow its leading pointer slots.
    for (std::size_t sub : {std::size_t(0), std::size_t(8), std::size_t(16),
                            std::size_t(24), std::size_t(32)}) {
      std::uint64_t q = 0;
      if (SafeReadPtr2023(reinterpret_cast<const void*>(static_cast<std::uintptr_t>(p) + sub),
                          &q))
        chase(q, depth + 1);
      if (reads > kProbeBudget) return;
    }
  }
};

bool NameIsRosetta(const char* n) {
  return std::strcmp(n, "CBaseEntity") == 0 || std::strcmp(n, "CEntityInstance") == 0 ||
         std::strcmp(n, "CBasePlayerController") == 0 ||
         std::strcmp(n, "CCSPlayerController") == 0;
}

bool FilenameContains(const std::string& fn, const char* needle) {
  return fn.find(needle) != std::string::npos;
}

// Dump ONE scope: the scope object head, the compiled member offsets (b8dcaf14
// baseline the V1 divergence is measured against), the container-head window, and —
// for a focus scope (server/client/engine2) — a sample of pointed-to records.
void DumpScope(CSchemaSystemTypeScope* scope, bool focus) {
  const auto scope_addr = reinterpret_cast<std::uint64_t>(scope);

  // Scope name lives at scope+0 (m_szScopeName[256]) on every era — read it fault-safe.
  char scope_name[256];
  if (!SafeReadCString2023(reinterpret_cast<const char*>(scope), scope_name, sizeof(scope_name)))
    std::snprintf(scope_name, sizeof(scope_name), "(unreadable)");

  // Compiled member ADDRESSES + offsets from the pinned (b8dcaf14) struct. These are
  // where the CURRENT compiled layout THINKS the members are; V1's real offsets are
  // measured relative to &m_ClassBindings below.
  const auto class_bindings_addr = reinterpret_cast<std::uint64_t>(&scope->m_ClassBindings);
  const auto enum_bindings_addr = reinterpret_cast<std::uint64_t>(&scope->m_EnumBindings);
  const std::size_t off_class = class_bindings_addr - scope_addr;
  const std::size_t off_enum = enum_bindings_addr - scope_addr;

  L("\n========================================================================\n");
  L("SCOPE \"%s\"  @ 0x%016" PRIx64 "%s\n", scope_name, scope_addr,
    focus ? "   [FOCUS: record sampling ON]" : "");
  L("  compiled(b8dcaf14) offsets within CSchemaSystemTypeScope:\n");
  L("    m_szScopeName @ +0\n");
  L("    m_ClassBindings @ +%zu  (&=0x%016" PRIx64 ")\n", off_class, class_bindings_addr);
  L("    m_EnumBindings  @ +%zu  (&=0x%016" PRIx64 ")\n", off_enum, enum_bindings_addr);
  L("  V0 container geometry (does NOT locate the pool/buckets on V1):\n");
  L("    real_base = &m_ClassBindings-8 ; count @ real_base+12 ; pool-blob head @ real_base+48\n");
  L("    bucket array @ real_base+160 ; lock 8 ; entry stride 24 ; block_size 24\n");

  // (1) scope object head — the members preceding/around the bindings.
  L("\n  -- (1) scope object head --\n");
  DumpAnnotated("scope-obj", scope_addr, 256, scope_addr, scope_addr);

  // (2) class-binding CONTAINER head window (the CUtlTSHash region). Anchored on
  // &m_ClassBindings so every slot's offset reads directly as the V1 derivation needs.
  const std::uint64_t cwin_base = class_bindings_addr - kContainerBackShift;
  L("\n  -- (2) m_ClassBindings CONTAINER head (CUtlTSHash region) --\n");
  DumpAnnotated("class-container", cwin_base, kContainerWindow, class_bindings_addr, scope_addr);

  // (3) m_EnumBindings container head (smaller window — enums expected 0 pre-Pulse).
  const std::uint64_t ewin_base = enum_bindings_addr - kContainerBackShift;
  L("\n  -- (3) m_EnumBindings CONTAINER head --\n");
  DumpAnnotated("enum-container", ewin_base, 128, enum_bindings_addr, scope_addr);

  if (!focus) return;

  // (4) sample pointed-to records — geometry-agnostic pointer-chase from the class
  // container window. Records are annotated so the operator can locate m_pszName /
  // m_nSize / m_nFieldCount / m_pFields / m_pBaseClasses / m_pTypeScope by pattern.
  RecordHunt hunt;
  for (std::size_t off = 0; off + 8 <= kContainerWindow; off += 8) {
    std::uint64_t v = 0;
    if (SafeReadPtr2023(reinterpret_cast<const void*>(
                            static_cast<std::uintptr_t>(cwin_base) + off),
                        &v))
      hunt.chase(v, 0);
  }
  L("\n  -- (4) sampled records (pointer-chase harvest from the class container; "
    "%zu found, %d reads) --\n",
    hunt.records.size(), hunt.reads);
  if (hunt.records.empty()) {
    L("    (NONE reachable by pointer-chase — container geometry fully divergent; the "
      "raw windows above are the derivation input)\n");
    return;
  }

  // Dump Rosetta-named records FIRST (CBaseEntity/CEntityInstance/... are the ground
  // truth the offset sweep validates against), then fill up to kMaxRecordsDumped.
  int dumped = 0;
  auto dump_one = [&](std::uint64_t rec) {
    std::size_t nsub = 0;
    char nm[160];
    LooksLikeRecord(rec, &nsub, nm, sizeof(nm));
    L("\n    RECORD @ 0x%016" PRIx64 "  name@+%zu = \"%s\"%s\n", rec, nsub, nm,
      NameIsRosetta(nm) ? "   <<< ROSETTA GROUND-TRUTH" : "");
    DumpAnnotated("record", rec, kRecordWindow, rec, scope_addr);
    ++dumped;
  };
  for (std::uint64_t rec : hunt.records) {
    if (dumped >= kMaxRecordsDumped) break;
    std::size_t nsub = 0;
    char nm[160];
    if (LooksLikeRecord(rec, &nsub, nm, sizeof(nm)) && NameIsRosetta(nm)) dump_one(rec);
  }
  for (std::uint64_t rec : hunt.records) {
    if (dumped >= kMaxRecordsDumped) break;
    std::size_t nsub = 0;
    char nm[160];
    if (LooksLikeRecord(rec, &nsub, nm, sizeof(nm)) && !NameIsRosetta(nm)) dump_one(rec);
  }
}

}  // namespace

bool DumpSchemaBytes(const std::filesystem::path& binaries_dir, std::string* err) {
  auto env_opt = LoadInProcessEnvironment(binaries_dir, err);
  if (!env_opt.has_value()) return false;  // *err set.
  InProcessEnvironment& env = **env_opt;

  // Boot the schema system the SAME way the walk does (post-boot handshake). Boot is
  // best-effort: it does NOT fault on the V1 reps, but if it ever
  // returns false we continue — the SchemaSystem_001 registration below still
  // populates the schema records we are here to dump.
  std::string boot_err;
  if (!BootEngineForConVars(env, &boot_err)) {
    L("[dump-schema-bytes] boot returned false (continuing to registration): %s\n",
      boot_err.c_str());
  }

  // Post-boot "SchemaSystem_001" registration handshake (loader.h). We IGNORE the
  // return: on a V1 build it registers every schema-bearing module and THEN fails the
  // runtime-layout variant gate (kUnknown -> false). The schema records are populated
  // regardless, which is exactly what this diagnostic needs. (On a KNOWN variant it
  // returns true; either way the scopes are populated afterwards.)
  std::string reg_err;
  if (!RetrySchemaRegistrationIfEmpty(env, &reg_err)) {
    L("[dump-schema-bytes] RetrySchemaRegistrationIfEmpty returned false "
      "(EXPECTED on an underived V1 build — schema is still populated; dumping): %s\n",
      reg_err.c_str());
  }

  auto* system = reinterpret_cast<CSchemaSystem*>(env.schema_system());
  if (system == nullptr) {
    *err = "schema system is null after registration (cannot dump)";
    return false;
  }

  // Enumerate the live per-module type scopes via the ISchemaSystem vtable (exactly
  // as schema_walk.cpp::CollectTypeScopes does): GlobalTypeScope() to exclude the
  // shared global scope, then FindTypeScopeForModule(filename) for every loaded
  // module, deduped by pointer.
  CSchemaSystemTypeScope* const global_scope = system->GlobalTypeScope();
  std::vector<CSchemaSystemTypeScope*> scopes;
  std::vector<bool> focus;
  auto push_unique = [&](CSchemaSystemTypeScope* s, bool is_focus) {
    if (s == nullptr || s == global_scope) return;
    for (CSchemaSystemTypeScope* e : scopes)
      if (e == s) return;
    scopes.push_back(s);
    focus.push_back(is_focus);
  };

  L("\n########################################################################\n");
  L("# dump-schema-bytes DIAGNOSTIC (V1 container/record derivation input)\n");
  L("# binaries: %s\n", binaries_dir.string().c_str());
  L("# CSchemaSystem @ 0x%016" PRIx64 "  GlobalTypeScope @ 0x%016" PRIx64 "\n",
    reinterpret_cast<std::uint64_t>(system),
    reinterpret_cast<std::uint64_t>(global_scope));
  L("# NOTE: all reads are SEH-guarded; an unmapped offset prints (unreadable), never faults.\n");
  L("########################################################################\n");

  for (const auto& m : env.modules()) {
    const std::string fn = m.filename();
    const bool is_focus = FilenameContains(fn, "server") ||
                          FilenameContains(fn, "client") ||
                          FilenameContains(fn, "engine2");
    push_unique(system->FindTypeScopeForModule(fn.c_str()), is_focus);
  }

  L("\n# %zu live per-module type scope(s) enumerated.\n", scopes.size());
  for (std::size_t i = 0; i < scopes.size(); ++i) {
    DumpScope(scopes[i], focus[i]);
  }

  L("\n# dump-schema-bytes complete (%zu scopes dumped).\n", scopes.size());
  std::fflush(stderr);
  return true;
}

}  // namespace cs2_schema_walker
