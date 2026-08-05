// tshash_compat.h — clean-room era-gated CUtlTSHash<T*,256,uint> binding accessor.
//
// WHY THIS EXISTS
// ----------------
// The walk enumerates each CSchemaSystemTypeScope's class/enum bindings through
// the schema-system's two CUtlTSHash members:
//
//     CUtlTSHash<CSchemaClassInfo*, 256, uint> m_ClassBindings;
//     CUtlTSHash<CSchemaEnumInfo*,  256, uint> m_EnumBindings;
//
// On the MODERN CS2 layout (the b8dcaf14 pin the walker compiles against) these
// are read straight through the header-inline CUtlTSHash<>::Count/GetElements/
// Element templates — byte-identical to the pre-existing code path.
//
// On the 2023 CS2 layout (e.g. build 10832117, 2023-03-22) the SAME b8dcaf14
// binary must still walk the 2023 game DLLs in-process. Two layout deltas vs the
// b8dcaf14-compiled layout, FULLY CHARACTERIZED and validated against 10832117:
//
//   (1) CSchemaSystemTypeScope::m_ClassBindings sits 8 bytes EARLIER than the
//       b8dcaf14-compiled member offset (a member before it in the scope is
//       8 bytes smaller on 2023). So m_EnumBindings is also -8 vs compiled.
//   (2) The CUtlMemoryPoolBase embedded as the CUtlTSHash's m_EntryMemory and the
//       HashBucket_t lock differ in size from the b8dcaf14-compiled layout, so the
//       HashBucket_t m_aBuckets[256] array starts at a DIFFERENT offset within the
//       CUtlTSHash and each HashBucket_t has a DIFFERENT stride. Both are pinned by
//       the kBucketArrayOff2023 / kLockSize2023 constants below, validated
//       empirically by the cross-scope bucket sweep (see "VALIDATED 2023
//       BUCKET LAYOUT" below).
//
// EVERYTHING ELSE IS STANDARD on 2023:
//   - the count (CUtlMemoryPoolBase::m_BlocksAllocated) is at CUtlTSHash_base+12;
//   - HashBucket_t = { CThreadSpinRWLock m_AddLock; HashFixedData_t* m_pFirst;
//                      HashFixedData_t* m_pFirstUncommitted; }  (m_pFirst BEFORE
//     m_pFirstUncommitted — see tier1/utltshash.h); both head pointers sit AFTER
//     the lock, i.e. m_pFirst @ bucket+lock_size, m_pFirstUncommitted @
//     bucket+lock_size+8;
//   - HashFixedData_t = { uint m_uiKey; HashFixedData_t* m_pNext; T m_Data; }
//     with m_pNext@+8, m_Data@+16 (m_uiKey<=8, pointer-aligned);
//   - the element T (CSchemaClassInfo*/CSchemaEnumInfo*) and everything it points
//     to (CSchemaClassInfo::m_pszName, fields, types, enums) are unchanged.
//
// VALIDATED 2023 BUCKET LAYOUT (build 10832117, cross-scope bucket sweep)
// ------------------------------------------------------------------------
// The 2023 game DLL's HashBucket_t lock is NOT the b8dcaf14 std::shared_mutex
// (8 bytes). It is the OLD dllimport debug read/write spin-lock, a DIFFERENT size,
// so both the bucket-array START (real_base + 2023-pool-sizeof) and the per-bucket
// STRIDE (lock_size + 2*sizeof(ptr)) differ from the compiled layout. AND the 2023
// game-allocated HashFixedData_t (key + m_pNext + m_Data) and the BUCKET_COUNT may
// also differ from the compiled assumption (8/16, 256). The widened sweep
// brute-forces (bucket_off relative to real_base) x (lock_size up to 64) x
// (m_pNext_sub {8,16,24}) x (m_Data_sub {8,16,24,32}) x (bucket_count {256,512}),
// walking BOTH bucket heads and rejecting any combo that faults. It also reports
// per-combo chain-depth stats (clean-termination vs cut-by-non-pointer) so a
// truncating-chain layout (wrong m_pNext) is distinguishable from a misaligned-
// bucket layout (wrong stride/bucket_off). The combo that recovers server.dll=657 /
// client.dll=373 / engine2.dll=3 distinct real class names with clean chain
// termination and NO fault is transcribed below as kBucketArrayOff2023 /
// kLockSize2023 / kEntryNextSub2023 / kEntryDataSub2023 / kBucketCount2023.
//
// To (re)validate or change these: re-derive against a 2023 binary using the cross-scope
// CLASS sweep methodology. The walk itself is
// SEH-guarded per chain step, so even a stale constant degrades to fewer names —
// never a crash.
//
// 2023 ENUMERATION VIA A FAULT-SAFE BOTH-HEADS MANUAL WALK
// --------------------------------------------------------
// The compiled CUtlTSHash<>::GetElements cannot be reused on 2023: it follows ONLY
// each bucket's m_pFirstUncommitted chain AND it reads the bucket array at the
// COMPILED pool sizeof — both wrong on 2023 (m_pFirstUncommitted-only recovered
// ~41% of entries; the compiled pool/lock sizes differ). So the 2023 reader does a
// manual walk, anchored on real_base:
//
//   - real 2023 CUtlTSHash base = &compiled_member - 8            (delta 1 above)
//   - real 2023 element count   = uint32 @ (real_base + 12)       (sanity bound)
//   - bucket array begins at real_base + kBucketArrayOff2023      (delta 2 above)
//   - HashBucket_t stride = kLockSize2023 + 2*sizeof(ptr); within each bucket the
//     two head pointers are m_pFirst @ +kLockSize2023 and m_pFirstUncommitted @
//     +kLockSize2023+8 (m_pFirst precedes m_pFirstUncommitted per the header).
//   - walk BOTH head chains of all kBucketCount2023 buckets (m_pNext @ entry+8, m_Data @
//     entry+16), dedup entries across both heads, append each non-null T to `out`.
//
// Every pointer read in the walk is SEH-guarded (Windows) — a wrong/garbage chain
// pointer becomes "stop this chain", never a fault. The constants below
// are validated by the cross-scope bucket sweep (server=657/client=373/engine2=3).
//
// RUNTIME ERA GATE (no per-era pin): for each scope the walk tries the COMPILED
// read first (modern: m_ClassBindings@compiled-offset, pool 96, count@Count()). If
// that count is IMPLAUSIBLE for a schema bindings table, it uses the 2023 read
// above. On the modern game binary the compiled read is always plausible, so the
// 2023 path never fires and the modern output stays byte-identical.

#ifndef WALKER_TSHASH_COMPAT_H_
#define WALKER_TSHASH_COMPAT_H_

#include "sdk_schema.h"         // CSchemaSystemTypeScope, CSchemaClassInfo, ...
#include "tier1/utltshash.h"    // CUtlTSHash, UtlTSHashHandle_t, CUtlMemoryPoolBase
#include "tier0/threadtools.h"  // CThreadSpinRWLock (HashBucket_t lock member)

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <vector>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
// NOMINMAX: this header is included by schema_walk.cpp BEFORE <algorithm> and the
// protobuf-generated headers; without it windows.h's min/max object-like macros
// would clash with std::min/std::max and protobuf identifiers in that TU.
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
// POSIX: the SafeRead*2023 leaves below are backed by a SIGSEGV/SIGBUS signal guard
// (the equivalent of the Windows SEH __try/__except) so the convar/command memory-
// mirror can probe garbage candidate pointers without crashing. See posix_crash_guard.h.
#include "posix_crash_guard.h"
#endif

namespace cs2_schema_walker {

// Forward declaration only — the full struct lives in schema_record_layout_v1.h, which
// INCLUDES this header, so completing it here would be a cycle. tshash_compat needs only
// a pointer/reference (never a member access in this header), so a forward decl suffices;
// the setter's argument and the getter's return are resolved in schema_walk.cpp where the
// full definition is visible.
namespace prelayout {
struct Pre2024LayoutOffsets;
}

namespace tshash_compat {

// --- PRE-2024 RUNTIME LAYOUT VARIANT selection ---------------------------------------
// The pre-2024 class/enum binding pool lives at
//     real_base = &m_ClassBindings - ActivePre2024RealBaseShift()
// where the shift is a SIGNED byte delta selected by the detected runtime layout variant
// (schema_record_layout_v1.h / Pre2024LayoutOffsets::real_base_shift). V0 (the 2023
// support-floor family + every committed pre-2024 build) uses shift == +8, i.e.
// real_base = compiled - 8 — BYTE-IDENTICAL to the walk's original hard-coded value. V1
// (the CS2 full-release-2023 window, builds 12182426..13240071) uses shift == -40, i.e.
// real_base = compiled + 40 (the container moved +48 as a rigid unit; see the derived
// V1 layout in schema_record_layout_v1.h). DetectSchemaVariant selects the table once
// (SetActivePre2024Layout) on a confirmed pre-2024 build; the DEFAULT (nothing selected —
// modern build / probe start) is V0's +8, so nothing changes until a V1 build is confirmed.
//
// DECLARED here, DEFINED in schema_walk.cpp (the TU that sees the full struct). The
// ReadBindings2023* templates below call it as a non-dependent free function, so it needs
// no struct completeness in this header and is resolved at link time.
std::ptrdiff_t ActivePre2024RealBaseShift();

// Select the active pre-2024 layout variant table. DetectSchemaVariant calls this once on
// a confirmed pre-2024 build; nullptr / kVariant0 restores the V0 (compiled-8) walk.
void SetActivePre2024Layout(const prelayout::Pre2024LayoutOffsets* table);

// True iff `v` is a canonical user-mode x86-64 pointer (non-null, high 16 bits 0,
// > 64 KiB). Cheap pre-filter so the 2023 chain walk never even attempts a read on
// an obviously-garbage chain pointer (a wrong bucket stride lands here).
inline bool LooksLikePointer2023(std::uint64_t v) {
  return v != 0 && (v >> 48) == 0 && v >= 0x10000ull;
}

// LEAF, SEH-guarded raw pointer read for the 2023 manual bucket walk. Returns true
// and fills *out iff [src, src+8) was fully readable; false on a fault. POD-only
// frame (no C++ unwinding objects), so __try/__except is well-formed. On POSIX the
// 2023 probe/walk runs on the matching-OS host; the unguarded read there is
// acceptable because the 2023 path only fires on a real 2023 game DLL.
#ifdef _WIN32
inline bool SafeReadPtr2023(const void* src, std::uint64_t* out) {
  __try {
    std::memcpy(out, src, sizeof(*out));
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#else
inline bool SafeReadPtr2023(const void* src, std::uint64_t* out) {
  // Signal-guarded on POSIX (see posix_crash_guard.h): a fault on an unmapped/garbage
  // `src` returns false rather than crashing — the memory-mirror scan relies on this.
  return posix_crash_guard::SafeProbeCopy(out, src, sizeof(*out));
}
#endif

// LEAF, SEH-guarded raw byte-range read for the 2023 pool-blob walk (pool header +
// blob header reads, > 8 bytes). Returns true and fills [out,out+n) iff [src,src+n)
// was fully readable; false on a fault. Same POD-frame discipline as SafeReadPtr2023.
#ifdef _WIN32
inline bool SafeReadBytes2023(const void* src, void* out, std::size_t n) {
  __try {
    std::memcpy(out, src, n);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#else
inline bool SafeReadBytes2023(const void* src, void* out, std::size_t n) {
  // Signal-guarded on POSIX (see posix_crash_guard.h).
  return posix_crash_guard::SafeProbeCopy(out, src, n);
}
#endif

// SEH-guarded C-string copy for the 2023 emit path. Copies up to `max_len-1`
// bytes from [src) into out (NUL-terminated), STOPPING at the first NUL or the
// first byte whose page is unmapped. Returns true iff `src` was a readable C
// string within the window (at least the first byte read without faulting);
// false iff the very first byte faulted (caller treats as empty/unreadable).
//
// WHY: on 2023 a record's m_pszName / a CSchemaType's m_sTypeName is resolved by
// Read2023CharPtr, which only checks the POINTER VALUE looks canonical
// (LooksLikePointer2023). The bytes it points at may still be unmapped (a freed
// record, a wrong subclass downcast that landed on a non-pointer interpreted as a
// char*). A raw strlen()/std::string ctor on such a pointer FAULTS. This reader
// makes the byte read itself fault-safe by copying one page-safe chunk
// under SEH and scanning it for the terminator. The walk degrades a bad string to
// "" and continues, never crashes.
#ifdef _WIN32
inline bool SafeReadCString2023(const char* src, char* out, std::size_t max_len) {
  if (src == nullptr || max_len == 0) {
    if (max_len) out[0] = '\0';
    return false;
  }
  std::size_t i = 0;
  __try {
    for (; i < max_len - 1; ++i) {
      char c = src[i];
      out[i] = c;
      if (c == '\0') {
        return true;
      }
    }
    out[i] = '\0';
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    out[i] = '\0';
    return i > 0;  // got at least one byte before the fault
  }
}
#else
inline bool SafeReadCString2023(const char* src, char* out, std::size_t max_len) {
  if (src == nullptr || max_len == 0) {
    if (max_len) out[0] = '\0';
    return false;
  }
  // POSIX equivalent of the Windows SEH loop: run the whole char-by-char copy under
  // one probe-guard sigsetjmp so a fault mid-string terminates cleanly at the last
  // byte read (returning the partial string), never crashing. `i` is volatile so its
  // value is well-defined after a siglongjmp out of the loop.
  posix_crash_guard::EnsureProbeHandlerInstalled();
  volatile std::size_t i = 0;
  if (sigsetjmp(posix_crash_guard::ProbeJmpBuf(), 1) == 0) {
    posix_crash_guard::ProbeActive() = 1;
    for (; i < max_len - 1; ++i) {
      char c = src[i];
      out[i] = c;
      if (c == '\0') {
        posix_crash_guard::ProbeActive() = 0;
        return true;
      }
    }
    posix_crash_guard::ProbeActive() = 0;
    out[i] = '\0';
    return true;
  }
  posix_crash_guard::ProbeActive() = 0;  // faulted mid-read
  out[i] = '\0';
  return i > 0;  // got at least one byte before the fault
}
#endif

// SEH trampoline for the 2023 emit path. Calls `fn(ctx)` under a structured
// exception handler and returns its bool result; returns false if `fn` faulted
// (access violation on a garbage 2023 record/type/scope pointer). This is a LEAF
// frame with NO C++ unwinding objects of its own (the only locals are POD), so
// __try/__except is well-formed even though `fn`'s body freely uses std::string /
// std::vector / protobuf objects — those live in fn's OWN frame, which the C++
// compiler unwinds normally on the non-exception path, and which the SEH filter
// here simply abandons on a hardware fault (the per-record protobuf message the
// caller passed is then discarded by the caller as a skipped record).
//
// WHY a trampoline: __try/__except may not appear in a function that needs C++
// object unwinding (C2712 on MSVC). EmitClass/EmitEnum build rich C++ objects, so
// we cannot wrap their bodies directly. Routing the call through this POD-frame
// leaf lets the whole per-record emit run under SEH without that restriction. The
// fault is contained to ONE record (still fail-loud at the WALK level —
// a structurally-broken WHOLE schema system still produces zero clean records and
// fails downstream; this only prevents ONE garbage 2023 record from aborting the
// rest).
#ifdef _WIN32
inline bool SehGuardedCall(bool (*fn)(void*), void* ctx) {
  __try {
    return fn(ctx);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#else
// POSIX has no SEH. Guard the call with the SAME sigaction+sigsetjmp mechanism the
// engine boot uses (posix_crash_guard::RunGuarded) so a fault inside fn is CAUGHT and
// reported as "did not run" — NOT left to kill the process.
//
// WHY THIS MATTERS (the bug this replaces): the only caller that faults here is
// RecordsValidateAsModernGuarded (schema_walk.cpp), which drives the compiled MODERN
// CUtlTSHash read over a scope that may be 2023-layout — an INTENTIONALLY garbage
// pointer chase whose whole point is "fault => not modern => fall through to 2023".
// The Windows branch above contains that fault; this POSIX branch previously did
// `return fn(ctx)` with NO guard, so on Linux the garbage read SIGSEGV'd the entire
// walk (non-deterministically, depending where the garbage pointer landed — it often
// chased into KeyValues/tier0 memory and crashed in KeyValues::FindKeyAndParent). A
// clean run and a crashing run both MEAN "not modern"; guarding makes the OUTCOME
// deterministic instead of a coin-flip between "fall through" and "process dies".
//
// fn stores its real result in *ctx and returns a bool only as a ran-sentinel;
// RunGuarded takes a void(*)(void*), so a POD adapter discards the bool. RunGuarded's
// true=ran / false=faulted maps exactly onto SehGuardedCall's contract. The guarded
// frame is POD (this thunk + the adapter); fn itself is the same callback the Windows
// __except abandons on fault, so the containment semantics match across platforms.
inline bool SehGuardedCall(bool (*fn)(void*), void* ctx) {
  struct Adapter {
    bool (*fn)(void*);
    void* ctx;
  } adapter{fn, ctx};
  auto thunk = +[](void* p) {
    auto* a = static_cast<Adapter*>(p);
    a->fn(a->ctx);  // real result is written into ctx by the trampoline; bool discarded
  };
  return posix_crash_guard::RunGuarded(thunk, &adapter);
}
#endif

// True iff the binding at `binding` has a readable, plausible class/enum name at its
// m_pszName sub-offset (the ONE member at the standard b8dcaf14 offset on 2023). Used
// as the pool-blob validation oracle so a freed/garbage slot is rejected, never
// emitted. `name_sub` is offsetof(SchemaClassInfoData_t, m_pszName) (==0 on every era).
inline bool BindingHasPlausibleName2023(std::uint64_t binding, std::size_t name_sub) {
  std::uint64_t name_ptr = 0;
  if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                           static_cast<std::uintptr_t>(binding) + name_sub),
                       &name_ptr))
    return false;
  if (!LooksLikePointer2023(name_ptr)) return false;
  char raw[128];
  if (!SafeReadBytes2023(reinterpret_cast<const void*>(
                             static_cast<std::uintptr_t>(name_ptr)),
                         raw, sizeof(raw)))
    return false;
  // Must be NUL-terminated within the window and a C-identifier-ish first char.
  std::size_t len = 0;
  while (len < sizeof(raw) && raw[len] != '\0') ++len;
  if (len < 2 || len == sizeof(raw)) return false;
  unsigned char c0 = static_cast<unsigned char>(raw[0]);
  return (c0 >= 'A' && c0 <= 'Z') || (c0 >= 'a' && c0 <= 'z') || c0 == '_';
}

// Number of hash buckets in the schema bindings tables (BUCKET_COUNT template
// arg on both CUtlTSHash members in schemasystem.h). Constant across every era.
// currently unused (the 2023 walk uses kBucketCount2023); retained as
// documentation of the CUtlTSHash template arg.
inline constexpr int kBucketCount = 256;

// Plausibility test for a schema-bindings element count read off ONE CUtlTSHash.
// A real per-module bindings table on CS2 carries from a handful (engine2.dll: 3)
// up to the high hundreds (server.dll: ~657 on 2023, ~746 on modern). We reject
// anything that cannot be a real registered count: a negative/huge value (a
// mis-read heap pointer fragment lands here), or an absurdly large positive value
// from reading the wrong member. The threshold is generous on the high side so a
// future build with more classes still passes the COMPILED read; the 2023
// fallback only ever fires when the COMPILED read produced garbage.
//
// We deliberately accept 0 as plausible on the COMPILED path: a genuinely empty
// per-module scope (e.g. a loaded module that registered no class of a given
// kind) legitimately reads 0 on the modern layout, and must NOT trigger the 2023
// fallback (which would mis-read the same empty table). The 2023 layout never
// produces a "0 but really N" situation for a populated module — on 2023 the
// COMPILED count for a populated table reads a wild value (it reads 8 bytes into
// the wrong place), not a small plausible one — so 0-on-compiled is always a
// genuine empty, never a disguised 2023 table.
inline bool PlausibleCompiledCount(int count) {
  return count >= 0 && count <= 200000;
}

// ---------------------------------------------------------------------------
// The count member (CUtlMemoryPoolBase::m_BlocksAllocated, what Count() returns)
// is at CUtlTSHash_base + 12 on BOTH eras — it precedes the 8-byte member the 2023
// pool drops. The 2023 reader reads it RAW from the real 2023 base (rather than
// calling Count()) because Count() reads through the COMPILED pool offset, which
// is correct here anyway, but the raw read keeps the "real base" arithmetic in one
// place and independent of the compiled member's address.
// PLATFORM-GATED 2023 container geometry. The 2023 game DLL's CUtlTSHash/CUtlMemoryPoolBase
// is laid out by Valve's per-OS toolchain; the LINUX (g++) 2023 pool sits at DIFFERENT
// offsets than the windows (MSVC) 2023 pool, derived from dump-schema-bytes on the linux
// build 10832117 (libserver.so 657-class scope).
// Both are relative to real_base = &m_ClassBindings - 8 (the shift is unchanged cross-OS).
// The RECORD layout (schema_record_layout_2023.h) is IDENTICAL cross-OS — only the container
// differs. These constants feed ComputeRuntimeLayoutSignature(), so the linux values yield a
// DISTINCT runtime-variant signature (added to kKnownRuntimeLayoutVariants); windows keeps
// its 69a8cb68 fingerprint byte-identical.
#if defined(__linux__)
inline constexpr std::size_t kCountOffset = 100;  // LINUX: m_BlocksAllocated @ real_base+100 (&m_ClassBindings+92 = 657)
#else
inline constexpr std::size_t kCountOffset = 12;  // m_BlocksAllocated (standard)
#endif

// VALIDATED 2023 BUCKET-ARRAY LAYOUT (cross-scope bucket sweep, build 10832117).
// =================================================================================
// These are the SINGLE transcription point for the empirical sweep result. Change
// ONLY these two constants when the sweep re-validates a different combo; everything
// else (stride, head offsets) is derived from them.
//
//   kBucketArrayOff2023 : where m_aBuckets[0] begins, RELATIVE TO real_base. Equal
//                         to the 2023 CUtlMemoryPoolBase sizeof. The sweep covers
//                         real_base+{72..200 step 4}; the cross-scope CLASS verdict
//                         (server=657 / client=373 / engine2=3, no fault, clean
//                         chain termination) selects this value.
//   kLockSize2023       : the size of the 2023 HashBucket_t::m_AddLock
//                         (CThreadSpinRWLock — the OLD dllimport debug spin-lock,
//                         NOT the b8dcaf14 std::shared_mutex). The sweep covers
//                         {0,4,8,12,16,24,32,40,48,56,64}; the cross-scope verdict
//                         selects this value. (kEntryNextSub2023/kEntryDataSub2023/
//                         kBucketCount2023 are swept jointly — see those below.)
//
// To re-validate, re-derive against a 2023 binary using the cross-scope CLASS sweep
// and set both (bucket_off=, lock=). The walk is
// SEH-guarded per chain step (ReadBindings2023 below), so a stale value degrades to
// fewer recovered names — it can NEVER crash.
// VALIDATED against build 10832117: bucket_off=+160,
// stride=24, lock=8 yields server.dll/client.dll class chains that terminate
// PERFECTLY CLEANLY (clean_term==chains_walked, cut_nonptr=0) — the layout is
// correct. (Recovers 412 server / 240 client real classes this way; whether that
// is the COMPLETE live count or ~62% of a 657 peak/capacity field is unresolved.
// The clean chains prove these constants read the container
// correctly regardless.)
#if defined(__linux__)
inline constexpr std::size_t kBucketArrayOff2023 = 152;  // LINUX: m_aBuckets[0] @ real_base+152 (&m_ClassBindings+144)
inline constexpr std::size_t kLockSize2023 = 32;         // LINUX: HashBucket_t::m_AddLock size (heads at bucket+32/+40)
#else
inline constexpr std::size_t kBucketArrayOff2023 = 160;  // m_aBuckets[0] @ real_base+THIS
inline constexpr std::size_t kLockSize2023 = 8;          // HashBucket_t::m_AddLock size
#endif

// Derived: HashBucket_t stride and the two head-pointer sub-offsets within a bucket.
// m_pFirst precedes m_pFirstUncommitted (tier1/utltshash.h), both after the lock.
inline constexpr std::size_t kBucketStride2023 =
    kLockSize2023 + 2 * sizeof(void*);                              // lock + m_pFirst + m_pFirstUncommitted
inline constexpr std::size_t kFirstSub2023 = kLockSize2023;         // m_pFirst
inline constexpr std::size_t kFirstUncSub2023 = kLockSize2023 + 8;  // m_pFirstUncommitted

// HashFixedData_t = { KEYTYPE m_uiKey; HashFixedData_t* m_pNext; T m_Data; }.
// These were ASSUMED era-invariant (m_uiKey<=8, pointer-aligned) but the widened
// sweep treats them as sweep axes: the 2023 game-allocated HashFixedData_t
// may have a wider key or extra fields. Transcribe the cross-scope verdict
// here (m_pNext_sub=, m_Data_sub=). If the verdict reports 8/16 the modern
// assumption held; otherwise set the 2023 values reported.
inline constexpr std::size_t kEntryNextSub2023 = 8;   // HashFixedData_t::m_pNext
inline constexpr std::size_t kEntryDataSub2023 = 16;  // HashFixedData_t::m_Data

// Number of HashBucket_t the 2023 schema hash walks. The template arg is 256 on the
// b8dcaf14 schemasystem.h, but the 2023 game DLL's CUtlTSHash may use a different
// BUCKET_COUNT. The sweep covers {256,512}; transcribe the verdict's bucket_count here.
// The 2023 walk uses THIS constant (not the compiled BUCKETS template arg) so a 512
// result is honored without recompiling against a different schemasystem.h.
inline constexpr int kBucketCount2023 = 256;

// ===========================================================================
// PINNED 2023 POOL-BLOB LAYOUT (validated on build 10832117, server.dll).
// ===========================================================================
// The 2023 CUtlTSHash begins with its m_EntryMemory CUtlMemoryPoolBase. Walking the
// pool's blob chain (m_pBlobHead -> CBlob::m_pNext) enumerates EVERY allocated
// HashFixedData_t block regardless of bucket reachability — recovering the full class
// set the bucket walk truncates (incl. CBaseEntity / CEntityInstance).
//
// These offsets were DERIVED + VALIDATED against server.dll on build 10832117 via the
// pool-blob chain walk: server.dll recovered 769 distinct class names INCLUDING
// CBaseEntity + CEntityInstance. The pool struct is IDENTICAL across scopes (it's the
// same CUtlMemoryPoolBase the same schemasystem.dll allocates for every scope), so we
// PIN the server-validated offsets and apply them UNIFORMLY to client.dll / engine2.dll
// rather than re-deriving per scope (per-scope auto-derivation mis-fired: it picked a
// wrong validating candidate — m_pBlobHead@+40 — for client/engine2, truncating them).
//
// All offsets are RELATIVE TO real_base (= &compiled_member - 8 — the 2023 member sits
// 8 bytes earlier than the b8dcaf14-compiled member; same delta the bucket walk uses).
//
//   CUtlMemoryPoolBase (real_base+...):
//     m_BlockSize       @ +0   (int)   == 24  (= sizeof HashFixedData_t: key8+next8+data8)
//     m_BlocksPerBlob   @ +4   (int)   == 256
//     m_NumBlobs        @ +8   (int)
//     m_BlocksAllocated @ +16  (int)   (the live entry count)
//     m_pBlobHead       @ +48  (CBlob*)
//   CBlob (at *m_pBlobHead, chained via m_pNext):
//     m_pNext           @ +0   (CBlob*)
//     m_NumBytes        @ +8   (int)
//     m_Data[]          @ +16  (array of HashFixedData_t, stride = m_BlockSize)
//   HashFixedData_t (each block):
//     m_uiKey           @ +0
//     m_pNext           @ +8
//     m_Data (T)        @ +16  (the CSchemaClassInfo* / CSchemaEnumInfo* binding)
//
// blocks-in-a-blob = m_NumBytes / m_BlockSize (the last blob may be partial). Dedup is
// BY BINDING POINTER (two distinct classes never share a binding ptr; dedup-by-name
// could wrongly drop a legitimately-duplicated name). Freed/null slots are skipped.
#if defined(__linux__)
// LINUX 2023 CUtlMemoryPoolBase members (contiguous 4-byte ints @ &m_ClassBindings+80..92,
// m_pBlobHead @ +128; all expressed relative to real_base = &m_ClassBindings-8).
inline constexpr std::size_t kPoolBlockSizeOff2023 = 88;      // m_BlockSize (int)  (=24)
inline constexpr std::size_t kPoolBlocksPerBlobOff2023 = 92;  // m_BlocksPerBlob (int) (=256)
inline constexpr std::size_t kPoolNumBlobsOff2023 = 96;       // m_NumBlobs (int)
inline constexpr std::size_t kPoolBlocksAllocOff2023 = 100;   // m_BlocksAllocated (int) (=657)
inline constexpr std::size_t kPoolBlobHeadOff2023 = 136;      // m_pBlobHead (CBlob*)
#else
inline constexpr std::size_t kPoolBlockSizeOff2023 = 0;      // m_BlockSize (int)
inline constexpr std::size_t kPoolBlocksPerBlobOff2023 = 4;  // m_BlocksPerBlob (int)
inline constexpr std::size_t kPoolNumBlobsOff2023 = 8;       // m_NumBlobs (int)
inline constexpr std::size_t kPoolBlocksAllocOff2023 = 16;   // m_BlocksAllocated (int)
inline constexpr std::size_t kPoolBlobHeadOff2023 = 48;      // m_pBlobHead (CBlob*)
#endif
inline constexpr std::size_t kBlobNextOff2023 = 0;      // CBlob::m_pNext
inline constexpr std::size_t kBlobNumBytesOff2023 = 8;  // CBlob::m_NumBytes (int)
inline constexpr std::size_t kBlobDataOff2023 = 16;     // CBlob::m_Data[]
inline constexpr int kPoolBlockSize2023 = 24;           // m_BlockSize value (stride)
inline constexpr std::size_t kEntryDataOff2023 = 16;    // HashFixedData_t::m_Data

// SCOPE-FILTER offset (build 10832117): a LIVE binding for a scope
// has binding->m_pTypeScope == the CSchemaSystemTypeScope* being walked. A freed/stale
// pool block may still hold a non-null, plausibly-named binding pointer that belongs to
// ANOTHER scope (or a stale object) — the name oracle alone does NOT reject it, which
// inflates/garbles the per-scope set (engine2.dll over-counts; EmitClass then faults on
// the stale/garbage record). Requiring binding->m_pTypeScope@+80 == scope_addr is the
// SAME filter WalkPoolBlobs applies to recover the exact 657/373/3, and it is
// what drops every freed/other-scope block so EmitClass only ever sees real records.
// For SchemaClassInfoData_t this is +80; for SchemaEnumInfoData_t the 2023 m_pTypeScope
// offset differs (kEnumTypeScopeOff2023 in schema_record_layout_2023.h) — the caller
// passes the right sub-offset per binding kind.
inline constexpr std::size_t kClassTypeScopeSub2023 = 80;  // SchemaClassInfoData_t::m_pTypeScope

// ---------------------------------------------------------------------------
// 2023 reader: enumerate every element of the 2023-layout CUtlTSHash whose live
// COMPILED member reference is `compiled`, via a fault-safe both-heads manual walk
// anchored on real_base (see file header "2023 ENUMERATION VIA A FAULT-SAFE
// BOTH-HEADS MANUAL WALK"):
//
//   real_base    = &compiled - 8                 (the 2023 member sits -8)
//   count        = uint32 @ real_base+12         (m_BlocksAllocated; sanity bound)
//   m_aBuckets[0]= real_base + kBucketArrayOff2023
//   stride       = kBucketStride2023 (== kLockSize2023 + 2 ptrs)
//   m_pFirst @ bucket+kFirstSub2023, m_pFirstUncommitted @ bucket+kFirstUncSub2023
//   per entry: m_pNext @ entry+kEntryNextSub2023, m_Data @ entry+kEntryDataSub2023
//
// The combo (kBucketArrayOff2023, kLockSize2023) is the cross-scope CLASS sweep
// verdict (server=657 / client=373 / engine2=3, no fault).
//
// EVERY chain pointer read is SEH-guarded (SafeReadPtr2023) and pre-filtered
// (LooksLikePointer2023), so a stale/wrong combo degrades to fewer recovered names
// and NEVER faults — the crash the previous unguarded walk hit is gone.
// Appends each non-null element (a T) to `out`. Order unspecified (caller sorts).
//
// NOTE: this BUCKET-CHAIN walk reaches only ~412/657 server.dll classes on build
// 10832117 (~63 of kBucketCount2023 buckets read empty; CBaseEntity / CEntityInstance missing).
// The chains it DOES walk terminate cleanly (the bucket layout is read correctly),
// but not every allocated entry is bucket-reachable the way we traverse the two
// heads. ReadBindings2023 below prefers the POOL-BLOB walk (which enumerates EVERY
// allocated block via m_EntryMemory regardless of bucket reachability) and only
// falls back to this bucket walk if the pool derivation fails. Kept as the
// fault-safe fallback so a pool-layout surprise degrades to "fewer names", never a
// crash and never zero.
template <class T, int BUCKETS, class KEY>
void ReadBindings2023Buckets(::CUtlTSHash<T, BUCKETS, KEY>& compiled,
                             std::vector<T>* out) {
  auto* compiled_bytes = reinterpret_cast<unsigned char*>(&compiled);

  // real_base = &m_ClassBindings - active variant shift (SIGNED). V0: -8 (compiled-8,
  // byte-identical to the original constant); V1: -(-40) == +40. See the header note.
  unsigned char* real_base = compiled_bytes - ActivePre2024RealBaseShift();

  // Real element count: m_BlocksAllocated @ real_base+12 (sanity bound only).
  std::int32_t count = 0;
  std::memcpy(&count, real_base + kCountOffset, sizeof(count));
  if (count <= 0) return;

  unsigned char* buckets = real_base + kBucketArrayOff2023;

  // Dedup by entry pointer: m_pFirst and m_pFirstUncommitted chains overlap once
  // entries are committed, so the same entry can appear on both heads.
  std::vector<std::uint64_t> seen;
  seen.reserve(static_cast<std::size_t>(count) + 16);
  auto already = [&seen](std::uint64_t e) {
    for (std::uint64_t s : seen)
      if (s == e) return true;
    return false;
  };

  constexpr int kMaxChain = 200000;  // cycle guard per chain
  // Total chain-iteration cap across ALL buckets. `bound` below is derived from `count`
  // (m_BlocksAllocated), which on a WRONG/mis-located offset table can be garbage-huge —
  // then the out->size()<bound guard never trips and this fallback walks up to
  // kBucketCount2023 * 2 * kMaxChain (~10^8) guarded reads, a multi-minute hang on a
  // non-matching layout. This independent cap bails fast; the correct 2023 walk touches
  // only ~count entries (a few thousand), far below it, so it is byte-identical.
  constexpr long kMaxTotalIters2023 = 262144;
  long total_iters = 0;
  const std::size_t bound = static_cast<std::size_t>(count) + 1024;  // safety cap
  // Walk kBucketCount2023 buckets (the 2023 BUCKET_COUNT, transcribed from the sweep),
  // NOT the compiled BUCKETS template arg — they may differ (e.g. 512 vs 256).
  for (int b = 0; b < kBucketCount2023 && out->size() < bound; ++b) {
    unsigned char* bucket =
        buckets + static_cast<std::size_t>(b) * kBucketStride2023;
    const std::size_t heads[2] = {kFirstSub2023, kFirstUncSub2023};
    for (std::size_t h = 0; h < 2; ++h) {
      std::uint64_t entry = 0;
      if (!SafeReadPtr2023(bucket + heads[h], &entry)) continue;  // unreadable head
      int guard = 0;
      while (LooksLikePointer2023(entry) && guard++ < kMaxChain &&
             out->size() < bound) {
        if (++total_iters > kMaxTotalIters2023) return;  // wrong-offset bail (see cap note)
        if (!already(entry)) {
          seen.push_back(entry);
          std::uint64_t data = 0;
          if (SafeReadPtr2023(reinterpret_cast<const void*>(
                                  static_cast<std::uintptr_t>(entry) +
                                  kEntryDataSub2023),
                              &data) &&
              data != 0) {
            T elem = reinterpret_cast<T>(static_cast<std::uintptr_t>(data));
            out->push_back(elem);
          }
        }
        std::uint64_t next = 0;
        if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(entry) +
                                 kEntryNextSub2023),
                             &next))
          break;                   // unreadable m_pNext -> stop this chain (no fault)
        if (next == entry) break;  // self-loop guard
        entry = next;
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 2023 POOL-BLOB reader. A CUtlTSHash begins with its m_EntryMemory
// CUtlMemoryPoolBase, which holds every HashFixedData_t block contiguously in a
// CBlob chain (CBlob { CBlob* m_pNext; int m_NumBytes; char m_Data[...]; }). Walking
// the blob chain enumerates EVERY allocated block regardless of bucket reachability,
// recovering the full entry set the bucket walk above truncates.
//
// The 2023 game pool's field OFFSETS differ from the b8dcaf14 CUtlMemoryPoolBase, but
// they are PINNED (validated on server.dll build 10832117 — see the kPool...Off2023
// constants above) and applied UNIFORMLY to every scope: the CUtlMemoryPoolBase is the
// same struct schemasystem.dll allocates for server/client/engine2 alike, so per-scope
// re-derivation is both unnecessary and harmful (it previously mis-picked a wrong
// validating m_pBlobHead@+40 for client/engine2, truncating them). m_pBlobHead @
// real_base+48, m_BlockSize @ real_base+0 (==24), blob m_Data @ +16, entry m_Data @
// +16. Every read is SEH-guarded + bounded; if real_base+0 does not read the
// pinned block size or real_base+48 is not a valid blob head, this returns false so
// ReadBindings2023 falls back to the bucket walk.
//
// `name_sub` is offsetof(SchemaClassInfoData_t, m_pszName) (== 8: the record begins
// with m_pSchemaBinding) — the one member at the standard offset on 2023 (the same
// oracle the offset derivation relied on).
// On a SUCCESS
// the recovered T pointers are appended to `out` (deduped by binding pointer; freed/
// empty slots — null m_Data — are skipped, never emitted). Returns true iff the pool
// layout was derived and the blob chain walked.
//
// SCOPE-FILTER (matches the validated WalkPoolBlobs): `scope_addr` is the
// owning CSchemaSystemTypeScope* (threaded from the WalkSchemaSystem call site), and
// `typescope_sub` is the binding record's m_pTypeScope sub-offset (+80 for classes;
// kEnumTypeScopeOff2023 for enums). A block is kept ONLY when its binding's
// m_pTypeScope @ +typescope_sub == scope_addr — this is EXACTLY the filter
// WalkPoolBlobs applies to recover 657/373/3 and is what drops freed/stale/
// other-scope blocks (which the name oracle alone passes through). Pass typescope_sub
// == 0 to disable the filter (name-only). Every read is SEH-guarded; a block whose
// m_pTypeScope is unreadable is dropped (treated as not-this-scope), never faulted.
template <class T, int BUCKETS, class KEY>
bool ReadBindings2023PoolBlobs(::CUtlTSHash<T, BUCKETS, KEY>& compiled,
                               std::size_t name_sub, std::uint64_t scope_addr,
                               std::size_t typescope_sub, std::vector<T>* out) {
  auto* compiled_bytes = reinterpret_cast<unsigned char*>(&compiled);
  // real_base = &m_ClassBindings - active variant shift (SIGNED). V0: -8 (compiled-8,
  // byte-identical to the original constant); V1: -(-40) == +40. See the header note.
  const std::uint64_t real_base =
      reinterpret_cast<std::uint64_t>(compiled_bytes - ActivePre2024RealBaseShift());

  // PINNED pool layout (validated on server.dll build 10832117; the CUtlMemoryPoolBase
  // is identical across scopes, so the SAME offsets apply to client.dll / engine2.dll
  // — no per-scope re-derivation). Read m_BlockSize + m_pBlobHead at the pinned offsets.
  std::int32_t block_size = 0;
  if (!SafeReadBytes2023(reinterpret_cast<const void*>(
                             static_cast<std::uintptr_t>(real_base +
                                                         kPoolBlockSizeOff2023)),
                         &block_size, 4))
    return false;
  // The pinned block size is the schema-pool invariant (24). Accept the read value if
  // it matches; otherwise this scope's real_base does not point at the expected pool
  // (e.g. a base-arithmetic surprise) — bail so the caller falls back rather than
  // walking a mis-located pool. A small tolerance band guards a future minor stride.
  if (block_size != kPoolBlockSize2023) {
    if (block_size < 16 || block_size > 64) return false;
  }
  const int bs = block_size;

  std::uint64_t blob_head = 0;
  if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                           static_cast<std::uintptr_t>(real_base +
                                                       kPoolBlobHeadOff2023)),
                       &blob_head))
    return false;
  if (!LooksLikePointer2023(blob_head)) return false;  // empty/unrecognized pool

  // Walk the blob chain (m_pBlobHead -> CBlob::m_pNext); within each blob iterate its
  // blocks (count = m_NumBytes / m_BlockSize), collect the binding @ block+16, dedup
  // by binding POINTER, skip freed/empty (null) slots.
  constexpr int kMaxBlobs = 4096;
  constexpr int kMaxBlocksPerBlob = 200000;
  // TOTAL block-iteration cap across ALL blobs in this call. The per-blob / per-blob-count
  // caps above are individually loose (a wrong offset table yields garbage m_NumBytes up to
  // 16 MB -> ~200k blocks per blob, over thousands of blobs -> ~10^8 guarded reads, which at
  // the POSIX signal-guard cost (~1us/read) reads as a multi-minute HANG on a NON-matching
  // layout — e.g. probing the V0 offsets against a linux 2023 scope during DetectSchemaVariant).
  // The CORRECT 2023 pool for any one scope holds only its live classes + freed slots (a few
  // thousand blocks: 657 server / 373 client / 3 engine2 on 10832117), FAR below this cap, so
  // the real walk (windows today, linux once derived) is byte-identical; only a mis-located /
  // wrong-offset pool hits the cap and bails fast so detection can reject it (degrade
  // quickly, never hang).
  constexpr int kMaxTotalBlocks2023 = 262144;
  int total_blocks = 0;
  std::vector<std::uint64_t> seen;        // dedup bindings (by ptr, not name)
  std::vector<std::uint64_t> seen_blobs;  // cycle guard
  std::uint64_t blob = blob_head;
  for (int bi = 0; bi < kMaxBlobs && LooksLikePointer2023(blob); ++bi) {
    bool cyc = false;
    for (std::uint64_t s : seen_blobs)
      if (s == blob) {
        cyc = true;
        break;
      }
    if (cyc) break;
    seen_blobs.push_back(blob);

    std::uint64_t next = 0;
    if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                             static_cast<std::uintptr_t>(blob + kBlobNextOff2023)),
                         &next))
      break;
    std::uint32_t num_bytes = 0;
    if (!SafeReadBytes2023(reinterpret_cast<const void*>(
                               static_cast<std::uintptr_t>(blob + kBlobNumBytesOff2023)),
                           &num_bytes, 4))
      break;
    int blocks = 0;
    if (num_bytes > 0 && num_bytes <= (16u << 20))
      blocks = static_cast<int>(num_bytes) / bs;
    if (blocks <= 0 || blocks > kMaxBlocksPerBlob) blocks = 0;

    for (int i = 0; i < blocks; ++i) {
      // Total-iteration guard: bail fast on a wrong/mis-located pool (see kMaxTotalBlocks2023).
      // Returns what was collected so far; on a NON-matching layout that is (near-)empty, so
      // the caller's build-level validation rejects it — never a hang.
      if (++total_blocks > kMaxTotalBlocks2023) return true;
      const std::uint64_t block =
          blob + kBlobDataOff2023 + static_cast<std::uint64_t>(i) * bs;
      std::uint64_t binding = 0;
      if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                               static_cast<std::uintptr_t>(block + kEntryDataOff2023)),
                           &binding))
        break;                                       // blob end / fault
      if (!LooksLikePointer2023(binding)) continue;  // freed/empty slot
      bool dup = false;
      for (std::uint64_t s : seen)
        if (s == binding) {
          dup = true;
          break;
        }
      if (dup) continue;
      if (!BindingHasPlausibleName2023(binding, name_sub)) continue;
      // SCOPE-FILTER (RECONCILED to probe WalkPoolBlobs): keep ONLY bindings owned by
      // the scope being walked (binding->m_pTypeScope @ +typescope_sub == scope_addr).
      // This rejects freed/stale/other-scope blocks the name oracle passes through —
      // the exact discipline that recovers 657/373/3 and stops EmitClass faulting on
      // garbage records. typescope_sub==0 disables the filter (name-only fallback).
      if (typescope_sub != 0) {
        std::uint64_t binding_scope = 0;
        if (!SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(binding) + typescope_sub),
                             &binding_scope))
          continue;                                 // unreadable m_pTypeScope -> not this scope (drop, never fault)
        if (binding_scope != scope_addr) continue;  // stale/other-scope block
      }
      seen.push_back(binding);
      out->push_back(reinterpret_cast<T>(static_cast<std::uintptr_t>(binding)));
    }
    if (next == blob) break;  // self-loop guard
    blob = next;
  }
  return true;
}

// 2023 enumeration entry point: the SCOPE-FILTERED pool-blob walk (RECONCILED to the
// probe's validated WalkPoolBlobs) recovers exactly the scope-owned allocated entries
// via m_EntryMemory — the full 657 server.dll / 373 client.dll / 3 engine2.dll classes
// incl. CBaseEntity / CEntityInstance, with freed/stale/other-scope blocks dropped by
// the m_pTypeScope==scope_addr filter (so EmitClass never faults on a garbage record).
//
// `scope_addr` is the owning CSchemaSystemTypeScope* (threaded from the walk call site);
// `typescope_sub` is the binding's m_pTypeScope sub-offset (+80 for classes). When
// `typescope_sub` is non-zero the scope-filter is applied; when 0 the walk is name-only.
//
// `name_sub` is the record's m_pszName sub-offset (== 8 for both SchemaClassInfoData_t
// and SchemaEnumInfoData_t — the record begins with the m_pSchemaBinding pointer), the
// binding-name oracle the pool walk uses to reject freed/garbage slots.
//
// FALLBACK: if the pool layout cannot be derived for a CLASS table (typescope_sub != 0,
// an unexpected pool surprise), fall back to the fault-safe bucket walk so we still
// recover what we can — never a crash, never zero. For the ENUM table
// (typescope_sub == 0 — the 2023 enum pool has not been located: the -8 base that works
// for m_ClassBindings does not locate the m_EnumBindings pool),
// we do NOT run the bucket fallback: walking an un-located enum pool/bucket layout
// yields garbage that would fault EmitEnum. Per the documented 2023 enum gap we instead
// emit ZERO enums for 2023 (classes are the priority) and the enum path never faults.
template <class T, int BUCKETS, class KEY>
void ReadBindings2023(::CUtlTSHash<T, BUCKETS, KEY>& compiled, std::size_t name_sub,
                      std::uint64_t scope_addr, std::size_t typescope_sub,
                      std::vector<T>* out) {
  // typescope_sub == 0 marks the ENUM table (no validated 2023 scope-filter / pool
  // base). Per the documented 2023 enum gap, return EMPTY rather than walk an
  // un-located pool and fault EmitEnum. Classes (typescope_sub == +80) proceed.
  if (typescope_sub == 0) return;

  const std::size_t before = out->size();
  if (ReadBindings2023PoolBlobs<T, BUCKETS, KEY>(compiled, name_sub, scope_addr,
                                                 typescope_sub, out)) {
    // Pool walk succeeded. If it somehow recovered nothing (e.g. a derivation that
    // validated on a stray blob but walked empty), fall back to the bucket walk so
    // an empty result is never silently accepted for a populated table.
    if (out->size() > before) return;
  }
  ReadBindings2023Buckets<T, BUCKETS, KEY>(compiled, out);
}

// Which layout a scope's bindings tables follow.
enum class Era { kModern,
                 k2023 };

// ---------------------------------------------------------------------------
// Enumerate ONE bindings table under a CALLER-DECIDED era. The
// caller decides the era once from the scope's class table and passes the SAME
// era for both the class and enum tables, so a zero-enum module on 2023 still
// reads through the 2023 reader rather than ambiguously through the compiled one.
//
// `compiled` is the live b8dcaf14-compiled CUtlTSHash member reference. On kModern
// we read it straight through (Count + GetElements + Element) — byte-identical to
// the pre-existing path. On k2023 we read it through the fault-safe both-heads
// manual walk (ReadBindings2023): real base = &compiled-8, real count @
// real_base+12, bucket array @ real_base+kBucketArrayOff2023, both head chains of
// all kBucketCount2023 buckets walked with SEH-guarded reads.
//
// Appends non-null elements to `out`. Order unspecified (caller sorts).
//
// `name_sub` is the m_pszName sub-offset within the element record (the binding-name
// oracle the 2023 pool walk uses to reject freed/garbage slots); it is unused on the
// modern path. For class bindings pass offsetof(SchemaClassInfoData_t, m_pszName);
// for enum bindings offsetof(SchemaEnumInfoData_t, m_pszName) (both 0 on every era).
//
// `scope_addr` is the owning CSchemaSystemTypeScope* (the address of the scope whose
// m_ClassBindings / m_EnumBindings `compiled` is); `typescope_sub` is the binding's
// m_pTypeScope sub-offset for the 2023 SCOPE-FILTER (+80 for SchemaClassInfoData_t; 0
// for the enum table, which disables the 2023 enum read per the documented gap). Both
// are unused on the modern path. Threaded from the WalkSchemaSystem call site so the
// 2023 pool walk can reject freed/other-scope blocks (RECONCILED to probe WalkPoolBlobs).
template <class T, int BUCKETS, class KEY>
void ReadBindingsForEra(::CUtlTSHash<T, BUCKETS, KEY>& compiled, Era era,
                        std::size_t name_sub, std::uint64_t scope_addr,
                        std::size_t typescope_sub, std::vector<T>* out) {
  if (era == Era::kModern) {
    const int n = compiled.Count();
    if (n <= 0) return;
    std::vector<UtlTSHashHandle_t> handles(static_cast<std::size_t>(n));
    const int got = compiled.GetElements(0, n, handles.data());
    for (int i = 0; i < got; ++i) {
      T elem = compiled.Element(handles[i]);
      if (elem != nullptr) out->push_back(elem);
    }
    return;
  }
  ReadBindings2023<T, BUCKETS, KEY>(compiled, name_sub, scope_addr, typescope_sub, out);
}

}  // namespace tshash_compat
}  // namespace cs2_schema_walker

#endif  // WALKER_TSHASH_COMPAT_H_
