// schema_record_layout_v1.h — pre-2024 runtime-layout variant table (not yet wired).
// ============================================================================
// This header is not #included by any translation unit yet and is deliberately absent
// from CMakeLists.txt: it is the additive offset table for the pre-2024 RUNTIME LAYOUT
// VARIANT "V1" (the CS2-full-release-2023 window, builds 12182426 2023-09-13 ..
// 13240071 2024-01-22). The struct, the variant-selection design, and the signature
// design are settled here; wiring it into the probe is a separate, later step.
//
// INVARIANTS THIS TABLE PRESERVES
// -------------------------------
//   * Variant-0 / V0 / 10832117 / current stay BYTE-IDENTICAL: `kVariant0` below is
//     constructed ENTIRELY from the EXISTING rec2023:: / tshash_compat:: named
//     constants — it re-types NO value. When wiring routes
//     RuntimeLayoutFingerprint()/DetectSchemaVariant() through this table, feeding
//     `kVariant0` MUST reproduce the current `re-2023lt/v1/69a8cb68432fca4f` hash
//     exactly (the field SET + ORDER + tag strings here mirror
//     layout_probe.cpp:RuntimeLayoutFingerprint() 1:1). The V1 tables are APPENDED,
//     never substituted for variant 0. The generalization is scoped to the pre-2024
//     k2023 reader only; it never touches the modern GetElements path.
//   * Default-deny, never guess: any offset not yet derived is `kTBD`; a table with
//     any `kTBD` is `!fully_derived()` and MUST NOT be offered to the runtime probe
//     nor allow-listed. Until V1's real fingerprint is computed, validated, and added
//     to kKnownRuntimeLayoutVariants, the N-way probe fails loud on a V1 build (as it
//     does now — confirmed: 12299470 / 13024819 / 12182426 each exit 75).
//   * Clean-room / hl2sdk-only: the struct stores integer offsets only; every
//     underlying type comes from the pinned hl2sdk headers via the two includes below.
//
// WHY V1 DIFFERS FROM V0:
//   V1's failure is a POOL/CONTAINER-STRUCTURE difference, NOT a record-head mis-read.
//   The variant-0 container geometry (real_base = &m_ClassBindings-8, bucket array @
//   +160, lock 8, stride 24, pool-blob head @ +48, block_size @ +0 == 24) locates
//   NOTHING on V1: the SCOPE-FILTERED pool walk recovers 0, AND the UNFILTERED
//   bucket-walk fallback (which would harvest hundreds of raw binding pointers if the
//   container geometry were intact and only the record head had moved) ALSO recovers
//   0 -> observed_class_count == 0 on every V1 tier. Therefore V1 must re-derive the
//   CONTAINER geometry FIRST (real_base_shift / bucket_array_off / lock_size /
//   pool_blob_head_off / pool_block_size) and only then the record-head offsets — a
//   full container-and-pool re-derivation, not the lighter record-only pass V0 reused.
//
// ============================================================================
#ifndef WALKER_SCHEMA_RECORD_LAYOUT_V1_H_
#define WALKER_SCHEMA_RECORD_LAYOUT_V1_H_

#include "schema_record_layout_2023.h"  // rec2023:: variant-0 record constants
#include "tshash_compat.h"              // tshash_compat:: variant-0 container constants

#include <cstddef>
#include <cstdint>
#include <limits>

namespace cs2_schema_walker {
namespace prelayout {

// Sentinel for an offset that has NOT YET been derived. Any table carrying a
// kTBD field is `!fully_derived()` and is INELIGIBLE for the runtime probe /
// allow-list (default-deny).
inline constexpr std::size_t kTBD = std::numeric_limits<std::size_t>::max();

// -----------------------------------------------------------------------------
// The variant-selected pre-2024 runtime offset table. ONE instance per DISTINCT
// runtime layout. Field order + names mirror layout_probe.cpp:RuntimeLayoutFingerprint()
// so a per-variant `ComputeRuntimeLayoutSignatureFor(table, tag)` reproduces
// the existing variant-0 hash when fed `kVariant0`.
//
// GROUPS:
//   (A) CONTAINER geometry  (tshash_compat::)  — the CUtlTSHash / CUtlMemoryPoolBase /
//       HashBucket geometry that LOCATES the binding pool+buckets. This is what V1
//       breaks. `real_base_shift` is the &compiled_member - shift.
//   (B) RECORD-HEAD offsets (rec2023::)        — SchemaClassInfoData_t / field / base /
//       CSchemaType / enum member offsets read once a binding is located.
// -----------------------------------------------------------------------------
struct Pre2024LayoutOffsets {
  // ---- (A) CONTAINER geometry -------------------------------------------------
  // SIGNED byte delta subtracted from &m_ClassBindings: real_base = &m_ClassBindings -
  // real_base_shift. V0 = +8 -> compiled-8 (byte-identical to the original hard-coded
  // walk). V1 = -40 -> compiled+40 (the container moved +48 as a rigid unit). ptrdiff_t
  // (not size_t) so the -40 subtraction is signed at the wiring site (tshash_compat.h
  // ReadBindings2023*). The runtime fingerprint mixes its uint64 bit pattern, so the
  // (size_t)(-40) and (ptrdiff_t)(-40) forms hash identically (0xFFFFFFFFFFFFFFD8).
  std::ptrdiff_t real_base_shift;     // SIGNED (V0: +8 -> compiled-8; V1: -40 -> compiled+40)
  std::size_t count_off;              // m_BlocksAllocated within real_base (V0: 12)
  std::size_t bucket_array_off;       // m_aBuckets[0] @ real_base+X (V0: 160)
  std::size_t lock_size;              // HashBucket_t::m_AddLock size (V0: 8)
  std::size_t entry_next_sub;         // HashFixedData_t::m_pNext (V0: 8)
  std::size_t entry_data_sub;         // HashFixedData_t::m_Data (V0: 16)
  int bucket_count;                   // BUCKET_COUNT (V0: 256)
  std::size_t pool_block_size_off;    // m_BlockSize @ real_base+X (V0: 0)
  std::size_t pool_blobs_per_off;     // m_BlocksPerBlob (V0: 4)
  std::size_t pool_num_blobs_off;     // m_NumBlobs (V0: 8)
  std::size_t pool_blocks_alloc_off;  // m_BlocksAllocated (V0: 16)
  std::size_t pool_blob_head_off;     // m_pBlobHead @ real_base+X (V0: 48)
  std::size_t blob_next_off;          // CBlob::m_pNext (V0: 0)
  std::size_t blob_numbytes_off;      // CBlob::m_NumBytes (V0: 8)
  std::size_t blob_data_off;          // CBlob::m_Data[] (V0: 16)
  int pool_block_size;                // m_BlockSize VALUE / block stride (V0: 24)
  std::size_t class_typescope_sub;    // SchemaClassInfoData_t::m_pTypeScope, scope-filter (V0: 80)
  // ---- (B) RECORD-HEAD offsets -----------------------------------------------
  std::size_t class_name_off;          // (V0: 8)
  std::size_t class_size_off;          // (V0: 24)
  std::size_t class_field_count_off;   // (V0: 28)
  std::size_t class_fields_off;        // (V0: 40)
  std::size_t class_base_classes_off;  // (V0: 56)
  std::size_t class_base_count_off;    // (V0: 35)
  std::size_t class_type_scope_off;    // (V0: 80)   (== class_typescope_sub on V0)
  std::size_t field_stride;            // (V0: 32)
  std::size_t field_name_off;          // (V0: 0)
  std::size_t field_type_off;          // (V0: 8)
  std::size_t field_offset_off;        // (V0: 16)
  std::size_t base_stride;             // (V0: 16)
  std::size_t base_offset_off;         // (V0: 0)
  std::size_t base_class_ptr_off;      // (V0: 8)
  std::size_t type_name_off;           // (V0: 8)
  std::size_t type_category_off;       // (V0: 24)
  std::size_t type_atomic_cat_off;     // (V0: 25)
  // Enum record offsets are only meaningful once a V1 ENUM pool is located.
  // Expected to stay UNUSED on V1 (pre-Pulse: enums == 0).
  std::size_t enum_type_scope_off;  // (V0: 48)

  // A table is eligible for the runtime probe ONLY when every offset is derived.
  constexpr bool fully_derived() const {
    const std::size_t fields[] = {
        // real_base_shift is a SIGNED value (never the kTBD sentinel); cast to size_t
        // for the "any field still kTBD?" scan. -40 -> 0xFFFFFFFFFFFFFFD8 != kTBD.
        static_cast<std::size_t>(real_base_shift),
        count_off, bucket_array_off, lock_size, entry_next_sub,
        entry_data_sub, pool_block_size_off, pool_blobs_per_off, pool_num_blobs_off,
        pool_blocks_alloc_off, pool_blob_head_off, blob_next_off, blob_numbytes_off,
        blob_data_off, class_typescope_sub, class_name_off, class_size_off,
        class_field_count_off, class_fields_off, class_base_classes_off,
        class_base_count_off, class_type_scope_off, field_stride, field_name_off,
        field_type_off, field_offset_off, base_stride, base_offset_off,
        base_class_ptr_off, type_name_off, type_category_off, type_atomic_cat_off,
        enum_type_scope_off};
    for (std::size_t f : fields)
      if (f == kTBD) return false;
    return bucket_count > 0 && pool_block_size > 0;
  }
};

// VARIANT 0 (V0 / 10832117 family) — constructed FROM the existing named constants so
// NO value is re-typed here (single source of truth stays schema_record_layout_2023.h +
// tshash_compat.h). Feeding this to ComputeRuntimeLayoutSignatureFor() MUST
// reproduce re-2023lt/v1/69a8cb68432fca4f byte-for-byte (regression guard).
inline constexpr Pre2024LayoutOffsets kVariant0{
    /*real_base_shift*/ 8,
    /*count_off*/ tshash_compat::kCountOffset,
    /*bucket_array_off*/ tshash_compat::kBucketArrayOff2023,
    /*lock_size*/ tshash_compat::kLockSize2023,
    /*entry_next_sub*/ tshash_compat::kEntryNextSub2023,
    /*entry_data_sub*/ tshash_compat::kEntryDataSub2023,
    /*bucket_count*/ tshash_compat::kBucketCount2023,
    /*pool_block_size_off*/ tshash_compat::kPoolBlockSizeOff2023,
    /*pool_blobs_per_off*/ tshash_compat::kPoolBlocksPerBlobOff2023,
    /*pool_num_blobs_off*/ tshash_compat::kPoolNumBlobsOff2023,
    /*pool_blocks_alloc_off*/ tshash_compat::kPoolBlocksAllocOff2023,
    /*pool_blob_head_off*/ tshash_compat::kPoolBlobHeadOff2023,
    /*blob_next_off*/ tshash_compat::kBlobNextOff2023,
    /*blob_numbytes_off*/ tshash_compat::kBlobNumBytesOff2023,
    /*blob_data_off*/ tshash_compat::kBlobDataOff2023,
    /*pool_block_size*/ tshash_compat::kPoolBlockSize2023,
    /*class_typescope_sub*/ tshash_compat::kClassTypeScopeSub2023,
    /*class_name_off*/ rec2023::kClassNameOff2023,
    /*class_size_off*/ rec2023::kClassSizeOff2023,
    /*class_field_count_off*/ rec2023::kClassFieldCountOff2023,
    /*class_fields_off*/ rec2023::kClassFieldsOff2023,
    /*class_base_classes_off*/ rec2023::kClassBaseClassesOff2023,
    /*class_base_count_off*/ rec2023::kClassBaseCountOff2023,
    /*class_type_scope_off*/ rec2023::kClassTypeScopeOff2023,
    /*field_stride*/ rec2023::kFieldStride2023,
    /*field_name_off*/ rec2023::kFieldNameOff2023,
    /*field_type_off*/ rec2023::kFieldTypeOff2023,
    /*field_offset_off*/ rec2023::kFieldOffsetOff2023,
    /*base_stride*/ rec2023::kBaseStride2023,
    /*base_offset_off*/ rec2023::kBaseOffsetOff2023,
    /*base_class_ptr_off*/ rec2023::kBaseClassPtrOff2023,
    /*type_name_off*/ rec2023::kTypeNameOff2023,
    /*type_category_off*/ rec2023::kTypeCategoryOff2023,
    /*type_atomic_cat_off*/ rec2023::kTypeAtomicCatOff2023,
    /*enum_type_scope_off*/ rec2023::kEnumTypeScopeOff2023,
};

// -----------------------------------------------------------------------------
// V1 SUB-VARIANT TABLES. Static evidence (schemasystem.dll identity, which defines the
// CONTAINER geometry AND constructs the records) splits V1 into AT MOST THREE container
// tiers; each is an UPPER BOUND (per the V0 lesson that container geometry can stay
// constant across large schemasystem.dll size changes — V0 held constant 253K->352K).
// One rep per tier is derived, and any tiers whose derived geometry is identical MERGE
// (they then share ONE re-cs2rel signature + ONE allow-list entry, exactly as V0a/V0b
// share variant 0). All three tiers show the IDENTICAL failure (variant-0 reads 0
// classes, kUnknown), so ALL need derivation.
//
//   tier / rep build          schemasystem.dll   builds (measured)          convar sub-signal
//   ---------------------------------------------------------------------------------------
//   kV1a  12182426 (or 12192623)  size 365928     12182426 .. 12192623 (3)   2915  (V1-a)
//   kV1bEarly 12299470            size 366440     12299470 .. 12358457 (9)   2860  (V1-b, step)
//   kV1bLate  13024819(==13240071) size 363880    12377892 .. 13240071 (~40) 2860->2896 (drift)
//
// NOTE: the rep set (12299470 / 13024819 / 13240071) covers kV1bEarly + kV1bLate
// (13024819 and 13240071 are schemasystem-IDENTICAL, sha bf15a76b382a, so they collapse
// to kV1bLate) but MISSES kV1a (365928). Deriving kV1a needs a 12182426 rep.
//
// Each per-rep derivation sweeps, IN ORDER (container first), grounding on server.dll
// with CBaseEntity / CEntityInstance / CBasePlayerController as the Rosetta stone:
//   1. real_base_shift  — sweep {0,8,16,-8,-16...} for the &m_ClassBindings offset at
//      which real_base+pool_block_size_off reads a sane m_BlockSize and real_base+
//      pool_blob_head_off reads a heap pointer whose blob validates. (V0=8.)
//   2. pool_block_size / pool_block_size_off — m_BlockSize value (== sizeof
//      HashFixedData_t; V0=24). If the 2023 key/next/data widths changed this moves.
//   3. bucket_array_off / lock_size — cross-scope sweep (bucket_off {72..200},
//      lock {0..64}, m_pNext/m_Data sub, bucket_count {256,512}); accept the combo that
//      recovers server/client/engine2 chains with CLEAN termination + no fault.
//   4. pool_blob_head_off / blob_* — blob-chain walk, validated by CBaseEntity present.
//   5. class_typescope_sub — the scope-filter offset (binding->m_pTypeScope == scope);
//      REQUIRED before EmitClass can drop freed/other-scope blocks.
//   6. record-head offsets (class_*/field_*/base_*/type_*) — value-match
//      against the located Rosetta classes (sane VARYING m_nSize per the m_nSize lesson,
//      ascending field offsets, resolved base chains, field[0] name begins 'm').
//   Cross-check field COUNTS against the V0 neighbor where a class is stable.
//
// Until a tier is fully derived + validated, its table stays all-kTBD and is NEVER
// offered to the probe. Merge tiers with identical derived geometry.

// ============================================================================
// DERIVED V1 LAYOUT (from reps 12182426 / 12299470 / 13024819, grounded against a
// V0 10832117 reference byte dump).
// ----------------------------------------------------------------------------
// RESULT: exactly ONE distinct V1 runtime layout. All three schemasystem.dll size
// tiers (365928 / 366440 / 363880) share IDENTICAL container geometry AND record head
// -> the three tables below are byte-identical (kV1a == kV1bEarly == kV1bLate == kV1).
// This is the V0a/V0b lesson repeated: schemasystem.dll SIZE changed, geometry did not.
//
// THE ONLY DELTA vs kVariant0 (V0): the class-binding CONTAINER moved +48 bytes as a
// RIGID UNIT. On V0 the pool head (m_BlockSize=24 / m_BlocksPerBlob=1) sits at
// &m_ClassBindings-8 (== real_base, real_base_shift = +8 "subtract"); on V1 the SAME
// pool head sits at &m_ClassBindings+40. Evidence (identical across all 12 scopes of
// all 3 reps, and diffed against the V0 reference): the fixed structural marker
// 0xffffffff0000ffff is at slot[-32] on V0 and slot[+16] on V1 (Δ+48); the pool
// signature 0x0000010000000018 is at slot[-8] on V0 and slot[+40] on V1 (Δ+48); the
// live class count reads cleanly at real_base+... : V1-a server=667/client=390/eng2=3,
// V1-b-early 619/376/4, V1-b-late 628/384/4 (V0 ref 657/373/3). Because ONLY real_base
// moved, the V0 walk (real_base = compiled-8) reads the pool AND the buckets at empty
// memory -> BOTH the scope-filtered pool walk and the unfiltered bucket fallback
// recover 0 (a rigid shift, not a structural rewrite). Every pool sub-offset,
// bucket/lock/stride/block-size, and EVERY record-head offset is IDENTICAL to V0
// (validated on Rosetta-grade records: CBasePlayerPawn
// m_nSize@24=2888 varying, fieldCount@28=24, baseCount@35=1, fields@40, baseClasses@56
// {m_nOffset@0=0, m_pClass@8 -> CBaseEntity}, typeScope@80 -> server scope;
// CRangeFloat m_nSize@24=8; CCSGO_TeamIntroTerroristPosition m_nSize@24=1864).
//
// real_base_shift ENCODING: V1 needs real_base = compiled + 40, i.e. "subtract -40".
// The field is std::size_t and the current wiring computes
// `real_base = compiled_bytes - 8` (tshash_compat.h). Stored here as (size_t)(-40) so a
// SIGNED interpretation `compiled - (std::ptrdiff_t)real_base_shift` yields +40. Wiring
// V1 in requires: (a) making the real_base subtraction signed (ptrdiff_t) at the wiring
// site; and (b) including real_base_shift in the runtime fingerprint —
// RuntimeLayoutFingerprint() hashes only the record/pool NAMED CONSTANTS (all identical
// V0<->V1), so the strict mirror gives V1 == V0 == 69a8cb68432fca4f (a COLLISION). The
// re-cs2rel signature below is therefore computed over the FULL Pre2024LayoutOffsets
// struct (incl. real_base_shift).
//
// re-cs2rel/v1 SIGNATURE (ONE, all tiers merge): FNV-1a over the 35 struct fields in
// declaration order (tag = field name) + a "re-runtime-layout-v1-struct" domain-separator
// fact, mirroring layout_probe.cpp Fnv1a/Fact machinery (verified to reproduce the
// V0 golden 69a8cb68432fca4f under the existing record-only fact list):
//     re-cs2rel/v1/55202ac2d6e7bfb9      <-- the ONE V1 layout (do NOT allow-list yet)
// (for reference, the same struct-hash over kVariant0 = re-cs2rel/v1/d3ebecefee6511e6;
//  V0 keeps its EXISTING re-2023lt/v1/69a8cb68432fca4f via the untouched
//  RuntimeLayoutFingerprint — the two functions/prefixes stay disjoint, golden preserved.)
// NOT added to kKnownRuntimeLayoutVariants — waits for built-binary validation.
// ----------------------------------------------------------------------------

// The ONE derived V1 layout. Written from the SAME named constants as kVariant0 so the
// "only real_base_shift differs" fact is self-evident; the single literal is the shift.
inline constexpr Pre2024LayoutOffsets kV1{
#if defined(__linux__)
    // LINUX V1: the V1 class-binding CONTAINER moved +64 (not windows' +48) from linux V0,
    // so real_base = &m_ClassBindings + 56 (shift -56). With the linux-gated pool constants
    // (kPoolBlockSizeOff2023=88, kPoolBlobHeadOff2023=136) this reads block_size @ B+144 and
    // m_pBlobHead @ B+192 (the CBlob with 256 blocks) — both validated on build 12182426.
    // (The pool-blob walk enumerates via block_size + blob-chain, not m_BlocksAllocated, so a
    // ~4-byte drift of the count field vs a pure shift is immaterial.) All other fields come
    // from the same linux-gated constants as kVariant0. Windows keeps -40 (byte-identical).
    /*real_base_shift*/ -56,
#else
    /*real_base_shift*/ -40,  // real_base = &m_ClassBindings + 40 (V0: +8 -> compiled-8)
#endif
    /*count_off*/ tshash_compat::kCountOffset,
    /*bucket_array_off*/ tshash_compat::kBucketArrayOff2023,
    /*lock_size*/ tshash_compat::kLockSize2023,
    /*entry_next_sub*/ tshash_compat::kEntryNextSub2023,
    /*entry_data_sub*/ tshash_compat::kEntryDataSub2023,
    /*bucket_count*/ tshash_compat::kBucketCount2023,
    /*pool_block_size_off*/ tshash_compat::kPoolBlockSizeOff2023,
    /*pool_blobs_per_off*/ tshash_compat::kPoolBlocksPerBlobOff2023,
    /*pool_num_blobs_off*/ tshash_compat::kPoolNumBlobsOff2023,
    /*pool_blocks_alloc_off*/ tshash_compat::kPoolBlocksAllocOff2023,
    /*pool_blob_head_off*/ tshash_compat::kPoolBlobHeadOff2023,
    /*blob_next_off*/ tshash_compat::kBlobNextOff2023,
    /*blob_numbytes_off*/ tshash_compat::kBlobNumBytesOff2023,
    /*blob_data_off*/ tshash_compat::kBlobDataOff2023,
    /*pool_block_size*/ tshash_compat::kPoolBlockSize2023,
    /*class_typescope_sub*/ tshash_compat::kClassTypeScopeSub2023,
    /*class_name_off*/ rec2023::kClassNameOff2023,
    /*class_size_off*/ rec2023::kClassSizeOff2023,
    /*class_field_count_off*/ rec2023::kClassFieldCountOff2023,
    /*class_fields_off*/ rec2023::kClassFieldsOff2023,
    /*class_base_classes_off*/ rec2023::kClassBaseClassesOff2023,
    /*class_base_count_off*/ rec2023::kClassBaseCountOff2023,
    /*class_type_scope_off*/ rec2023::kClassTypeScopeOff2023,
    /*field_stride*/ rec2023::kFieldStride2023,
    /*field_name_off*/ rec2023::kFieldNameOff2023,
    /*field_type_off*/ rec2023::kFieldTypeOff2023,
    /*field_offset_off*/ rec2023::kFieldOffsetOff2023,
    /*base_stride*/ rec2023::kBaseStride2023,
    /*base_offset_off*/ rec2023::kBaseOffsetOff2023,
    /*base_class_ptr_off*/ rec2023::kBaseClassPtrOff2023,
    /*type_name_off*/ rec2023::kTypeNameOff2023,
    /*type_category_off*/ rec2023::kTypeCategoryOff2023,
    /*type_atomic_cat_off*/ rec2023::kTypeAtomicCatOff2023,
    /*enum_type_scope_off*/ rec2023::kEnumTypeScopeOff2023,
};

// The three size tiers all resolve to the SINGLE derived layout above (MERGED).
// Kept as named aliases so the host/era catalog can key on the tier it detects statically;
// they are the SAME table -> ONE re-cs2rel signature -> ONE allow-list entry.
inline constexpr Pre2024LayoutOffsets kV1a = kV1;       // schemasystem 365928; 12182426..12192623
inline constexpr Pre2024LayoutOffsets kV1bEarly = kV1;  // schemasystem 366440; 12299470..12358457
inline constexpr Pre2024LayoutOffsets kV1bLate = kV1;   // schemasystem 363880; 12377892..13240071

// -----------------------------------------------------------------------------
// WIRING DESIGN (not yet done):
//
//   * layout_probe.cpp: add ComputeRuntimeLayoutSignatureFor(const Pre2024LayoutOffsets&,
//     const char* tag) that hashes the 35 struct fields in THIS declaration order (tag =
//     field name) after a "re-runtime-layout-v1-struct" domain-separator fact. V1 tables
//     get tag "re-cs2rel" -> the ONE derived layout hashes to re-cs2rel/v1/55202ac2d6e7bfb9.
//     Note: the struct's field SET is NOT 1:1 with RuntimeLayoutFingerprint()'s
//     named-constant fact list (the struct adds real_base_shift + container-position
//     fields and omits the type-subclass / enum-record / bucket-stride constants), so
//     feeding kVariant0 to the struct-hash does NOT reproduce 69a8cb68432fca4f (it yields
//     d3ebecefee6511e6). Do NOT try to force that equality. Instead keep V0 on the
//     EXISTING ComputeRuntimeLayoutSignature()/RuntimeLayoutFingerprint() (untouched ->
//     re-2023lt/v1/69a8cb68432fca4f golden preserved) and put V1+ on the NEW struct-hash under
//     the disjoint "re-cs2rel" prefix. The two are separate functions with separate prefixes;
//     no collision, golden intact. real_base_shift MUST be in the V1 fingerprint: it is the
//     ONLY field distinguishing V1 from V0, so a fingerprint that omits it collides V1 onto V0.
//   * WIRING: real_base_shift is stored as (size_t)(-40); the ReadBindings2023* sites that
//     currently hard-code `real_base = compiled_bytes - 8` MUST become
//     `real_base = compiled_bytes - static_cast<std::ptrdiff_t>(shift)` (signed) so V1 lands at
//     compiled+40. Retyping Pre2024LayoutOffsets::real_base_shift to std::ptrdiff_t is the
//     cleaner form (kVariant0's value stays 8; V0 artifacts + golden unaffected).
//   * layout_probe.cpp: append each VALIDATED V1 tier's real signature to
//     kKnownRuntimeLayoutVariants. NEVER add a stub for an all-kTBD table.
//   * schema_walk.cpp DetectSchemaVariant: generalize the "modern -> variant-0" ladder
//     into "modern -> for each fully_derived() variant table {validate; build-level
//     confirm CBaseEntity + class floor; if its signature is allow-listed -> select}".
//     Thread the SELECTED table's container+record offsets into ReadBindings2023* /
//     rec2023 accessors instead of the hard-coded variant-0 constants (the k2023 reader
//     becomes table-parameterized; the modern path is untouched -> byte-identical).
//   * inventory eras[] runtime-variant "cs2-2023-09-13": set its variantSignature to the ONE
//     derived value re-cs2rel/v1/55202ac2d6e7bfb9 (currently a placeholder) ONLY on
//     validation. The three tiers MERGED (one distinct layout) -> ONE era entry, not three;
//     the whole 53-build V1 window maps to this single variant (builds carry
//     era="cs2-2023-09-13").
// -----------------------------------------------------------------------------

}  // namespace prelayout
}  // namespace cs2_schema_walker

#endif  // WALKER_SCHEMA_RECORD_LAYOUT_V1_H_
