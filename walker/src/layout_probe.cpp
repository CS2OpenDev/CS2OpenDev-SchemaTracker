// Layout probe implementation. See layout_probe.h.
//
// The signature is a fingerprint of the exact HL2SDK struct layout the walker
// dereferences, plus the pinned HL2SDK SHA. It is computed from sizeof() and
// offsetof() of the layout-bearing structs in schematypes.h / schemasystem.h.
// A divergence in any field the walk touches changes the fingerprint, so a
// silently-incompatible HL2SDK bump cannot pass the probe unnoticed.
#include "layout_probe.h"

#include "loader.h"
#include "schema_compat.h"
#include "schema_record_layout_2023.h"  // rec2023::k...Off2023 (variant-0 runtime offset table)
#include "schema_record_layout_v1.h"    // prelayout::Pre2024LayoutOffsets / kVariant0 / kV1
#include "sdk_schema.h"
#include "tshash_compat.h"  // tshash_compat::k...Off2023 (pool/bucket offsets)

#include <array>
#include <cstddef>
#include <cstdint>
#include <span>
#include <sstream>

// HL2SDK_SUBMODULE_SHA is injected by CMakeLists.txt at configure time so the
// signature changes whenever the pinned HL2SDK commit changes.
#ifndef HL2SDK_SUBMODULE_SHA
#define HL2SDK_SUBMODULE_SHA "unknown"
#endif

namespace cs2_schema_walker {

namespace {

// --- FNV-1a 64-bit. A stable, dependency-free, deterministic hash. ----------
constexpr uint64_t kFnvOffset = 1469598103934665603ULL;
constexpr uint64_t kFnvPrime = 1099511628211ULL;

class Fnv1a {
 public:
  void Mix(uint64_t v) {
    for (int i = 0; i < 8; ++i) {
      h_ ^= static_cast<uint8_t>(v & 0xFF);
      h_ *= kFnvPrime;
      v >>= 8;
    }
  }
  void MixStr(const char* s) {
    for (; *s; ++s) {
      h_ ^= static_cast<uint8_t>(*s);
      h_ *= kFnvPrime;
    }
    h_ ^= 0;  // explicit terminator so "ab","c" != "a","bc"
    h_ *= kFnvPrime;
  }
  uint64_t value() const { return h_; }

 private:
  uint64_t h_ = kFnvOffset;
};

// Feed one named layout fact (struct tag + a size/offset value) into the hash.
// Naming each fact keeps the fingerprint sensitive to WHICH field moved, not
// just that some number changed, and makes the contributing set self-documenting.
void Fact(Fnv1a& h, const char* tag, uint64_t value) {
  h.MixStr(tag);
  h.Mix(value);
}

// The layout facts the WALK depends on. If any of these change, the walker
// reads memory differently and the signature MUST change. Keep this list in
// lockstep with what schema_walk.cpp actually dereferences.
uint64_t LayoutFingerprint() {
  Fnv1a h;

  // Top-level pointer width — the whole layout is x86_64-specific.
  Fact(h, "sizeof(void*)", sizeof(void*));

  // --- CSchemaType base + subclasses (recursive field type walk) ------------
  // CSchemaType is polymorphic; offsetof is not portable on it, but its size and
  // its data-member layout (m_sTypeName / m_pTypeScope / m_eTypeCategory /
  // m_eAtomicCategory follow the vtable) are what matter. We fold sizeof of the
  // base and every subclass the walk distinguishes.
  Fact(h, "sizeof(CSchemaType)", sizeof(CSchemaType));
  Fact(h, "sizeof(CSchemaType_Builtin)", sizeof(CSchemaType_Builtin));
  Fact(h, "sizeof(CSchemaType_Ptr)", sizeof(CSchemaType_Ptr));
  Fact(h, "sizeof(CSchemaType_Atomic)", sizeof(CSchemaType_Atomic));
  Fact(h, "sizeof(CSchemaType_Atomic_T)", sizeof(CSchemaType_Atomic_T));
  Fact(h, "sizeof(CSchemaType_Atomic_CollectionOfT)", sizeof(CSchemaType_Atomic_CollectionOfT));
  Fact(h, "sizeof(CSchemaType_Atomic_TT)", sizeof(CSchemaType_Atomic_TT));
  Fact(h, "sizeof(CSchemaType_Atomic_I)", sizeof(CSchemaType_Atomic_I));
  Fact(h, "sizeof(CSchemaType_DeclaredClass)", sizeof(CSchemaType_DeclaredClass));
  Fact(h, "sizeof(CSchemaType_DeclaredEnum)", sizeof(CSchemaType_DeclaredEnum));
  Fact(h, "sizeof(CSchemaType_FixedArray)", sizeof(CSchemaType_FixedArray));
  Fact(h, "sizeof(CSchemaType_Bitfield)", sizeof(CSchemaType_Bitfield));

  // The enum tags the walk switches on. If Valve reorders the category enums,
  // every category test the walk makes changes meaning.
  // Renamed enumerators (SCHEMA_TYPE_PTR/SCHEMA_TYPE_POINTER on era-9/era-8) are
  // fed through schema_compat canonical aliases so the probe COMPILES on both
  // eras. The TAG STRING and the numeric VALUE are unchanged on the new era
  // (WSCHEMA_TYPE_POINTER == ::SCHEMA_TYPE_POINTER == 1), so the new-era
  // fingerprint stays byte-identical. On era-9 the value is identical
  // numerically (1) but the OVERALL fingerprint still differs via the schema
  // struct sizes/offsets and the embedded HL2SDK SHA — era-9 is a distinct layout.
  Fact(h, "SCHEMA_TYPE_BUILTIN", SCHEMA_TYPE_BUILTIN);
  Fact(h, "SCHEMA_TYPE_POINTER", schema_compat::WSCHEMA_TYPE_POINTER);
  Fact(h, "SCHEMA_TYPE_BITFIELD", SCHEMA_TYPE_BITFIELD);
  Fact(h, "SCHEMA_TYPE_FIXED_ARRAY", SCHEMA_TYPE_FIXED_ARRAY);
  Fact(h, "SCHEMA_TYPE_ATOMIC", SCHEMA_TYPE_ATOMIC);
  Fact(h, "SCHEMA_TYPE_DECLARED_CLASS", SCHEMA_TYPE_DECLARED_CLASS);
  Fact(h, "SCHEMA_TYPE_DECLARED_ENUM", SCHEMA_TYPE_DECLARED_ENUM);
  Fact(h, "SCHEMA_ATOMIC_T", SCHEMA_ATOMIC_T);
  Fact(h, "SCHEMA_ATOMIC_COLLECTION_OF_T", SCHEMA_ATOMIC_COLLECTION_OF_T);
  Fact(h, "SCHEMA_ATOMIC_TT", SCHEMA_ATOMIC_TT);
  Fact(h, "SCHEMA_ATOMIC_I", SCHEMA_ATOMIC_I);

  // --- SchemaClassInfoData_t (the per-class record the walk reads) ----------
  Fact(h, "sizeof(SchemaClassInfoData_t)", sizeof(SchemaClassInfoData_t));
  Fact(h, "off(SchemaClassInfoData_t.m_pszName)", offsetof(SchemaClassInfoData_t, m_pszName));
  Fact(h, "off(SchemaClassInfoData_t.m_pszProjectName)", offsetof(SchemaClassInfoData_t, m_pszProjectName));
  Fact(h, "off(SchemaClassInfoData_t.m_nSize)", offsetof(SchemaClassInfoData_t, m_nSize));
  Fact(h, "off(SchemaClassInfoData_t.m_nFieldCount)", offsetof(SchemaClassInfoData_t, m_nFieldCount));
  Fact(h, "off(SchemaClassInfoData_t.m_nStaticMetadataCount)", offsetof(SchemaClassInfoData_t, m_nStaticMetadataCount));
  Fact(h, "off(SchemaClassInfoData_t.m_nBaseClassCount)", offsetof(SchemaClassInfoData_t, m_nBaseClassCount));
  Fact(h, "off(SchemaClassInfoData_t.m_pFields)", offsetof(SchemaClassInfoData_t, m_pFields));
  Fact(h, "off(SchemaClassInfoData_t.m_pBaseClasses)", offsetof(SchemaClassInfoData_t, m_pBaseClasses));
  Fact(h, "off(SchemaClassInfoData_t.m_pStaticMetadata)", offsetof(SchemaClassInfoData_t, m_pStaticMetadata));
  Fact(h, "off(SchemaClassInfoData_t.m_pTypeScope)", offsetof(SchemaClassInfoData_t, m_pTypeScope));
  Fact(h, "off(SchemaClassInfoData_t.m_nFlags1)", offsetof(SchemaClassInfoData_t, m_nFlags1));

  // --- SchemaClassFieldData_t -----------------------------------------------
  Fact(h, "sizeof(SchemaClassFieldData_t)", sizeof(SchemaClassFieldData_t));
  Fact(h, "off(SchemaClassFieldData_t.m_pszName)", offsetof(SchemaClassFieldData_t, m_pszName));
  Fact(h, "off(SchemaClassFieldData_t.m_pType)", offsetof(SchemaClassFieldData_t, m_pType));
  Fact(h, "off(SchemaClassFieldData_t.m_nSingleInheritanceOffset)", offsetof(SchemaClassFieldData_t, m_nSingleInheritanceOffset));
  Fact(h, "off(SchemaClassFieldData_t.m_nStaticMetadataCount)", offsetof(SchemaClassFieldData_t, m_nStaticMetadataCount));
  Fact(h, "off(SchemaClassFieldData_t.m_pStaticMetadata)", offsetof(SchemaClassFieldData_t, m_pStaticMetadata));

  // --- SchemaBaseClassInfoData_t (parents) ----------------------------------
  Fact(h, "sizeof(SchemaBaseClassInfoData_t)", sizeof(SchemaBaseClassInfoData_t));
  Fact(h, "off(SchemaBaseClassInfoData_t.m_nOffset)", offsetof(SchemaBaseClassInfoData_t, m_nOffset));
  Fact(h, "off(SchemaBaseClassInfoData_t.m_pClass)", offsetof(SchemaBaseClassInfoData_t, m_pClass));

  // --- SchemaMetadataEntryData_t (reflection metadata; KV3 carrier) ---
  Fact(h, "sizeof(SchemaMetadataEntryData_t)", sizeof(SchemaMetadataEntryData_t));
  Fact(h, "off(SchemaMetadataEntryData_t.m_pszName)", offsetof(SchemaMetadataEntryData_t, m_pszName));
  Fact(h, "off(SchemaMetadataEntryData_t.m_pData)", offsetof(SchemaMetadataEntryData_t, m_pData));

  // --- SchemaEnumInfoData_t + enumerator record -----------------------------
  Fact(h, "sizeof(SchemaEnumInfoData_t)", sizeof(SchemaEnumInfoData_t));
  Fact(h, "off(SchemaEnumInfoData_t.m_pszName)", offsetof(SchemaEnumInfoData_t, m_pszName));
  Fact(h, "off(SchemaEnumInfoData_t.m_nSize)", offsetof(SchemaEnumInfoData_t, m_nSize));
  Fact(h, "off(SchemaEnumInfoData_t.m_nEnumeratorCount)", offsetof(SchemaEnumInfoData_t, m_nEnumeratorCount));
  Fact(h, "off(SchemaEnumInfoData_t.m_nStaticMetadataCount)", offsetof(SchemaEnumInfoData_t, m_nStaticMetadataCount));
  Fact(h, "off(SchemaEnumInfoData_t.m_pEnumerators)", offsetof(SchemaEnumInfoData_t, m_pEnumerators));
  Fact(h, "off(SchemaEnumInfoData_t.m_pTypeScope)", offsetof(SchemaEnumInfoData_t, m_pTypeScope));
  Fact(h, "sizeof(SchemaEnumeratorInfoData_t)", sizeof(SchemaEnumeratorInfoData_t));
  Fact(h, "off(SchemaEnumeratorInfoData_t.m_pszName)", offsetof(SchemaEnumeratorInfoData_t, m_pszName));
  Fact(h, "off(SchemaEnumeratorInfoData_t.m_nValue)", offsetof(SchemaEnumeratorInfoData_t, m_nValue));
  Fact(h, "off(SchemaEnumeratorInfoData_t.m_nStaticMetadataCount)", offsetof(SchemaEnumeratorInfoData_t, m_nStaticMetadataCount));
  Fact(h, "off(SchemaEnumeratorInfoData_t.m_pStaticMetadata)", offsetof(SchemaEnumeratorInfoData_t, m_pStaticMetadata));

  // --- CSchemaSystem / type scope (scope + binding enumeration) -------------
  // Polymorphic; sizeof only. The walk reads m_TypeScopes and each scope's
  // CUtlTSHash bindings via the header-inline accessors, so sizeof changes here
  // are a strong proxy for any reorder of those members.
  Fact(h, "sizeof(CSchemaSystem)", sizeof(CSchemaSystem));
  Fact(h, "sizeof(CSchemaSystemTypeScope)", sizeof(CSchemaSystemTypeScope));
  Fact(h, "SCHEMA_BUILTIN_TYPE_COUNT", schema_compat::WSCHEMA_BUILTIN_TYPE_COUNT);

  return h.value();
}

// Append fp as exactly 16 lowercase, zero-padded hex digits. Extracted verbatim from the three
// signature builders below so their emitted bytes stay byte-identical — the layout-signature
// string is pinned per era in data/cs2-assets-inventory.json and asserted by the tests, so this
// is a pure de-duplication of the formatting, NOT a change to the produced characters.
void AppendHex16(std::ostringstream& sig, uint64_t fp) {
  static const char* kHex = "0123456789abcdef";
  char buf[17];
  for (int i = 15; i >= 0; --i) {
    buf[i] = kHex[fp & 0xF];
    fp >>= 4;
  }
  buf[16] = '\0';
  sig << buf;
}

}  // namespace

std::string ComputeLayoutSignature() {
  std::ostringstream sig;
  sig << "hl2sdk-cs2/" << HL2SDK_SUBMODULE_SHA << "/v1/";
  // 16-hex-digit fingerprint, zero-padded, lowercase. Stable formatting.
  AppendHex16(sig, LayoutFingerprint());
  return sig.str();
}

namespace {

// --- PRE-2024 RUNTIME LAYOUT VARIANT fingerprint (second allow-list) ----------
//
// Unlike LayoutFingerprint() (which hashes the COMPILED hl2sdk struct layout), this
// hashes the DERIVED RUNTIME OFFSET TABLE the k2023 reader dereferences — the exact
// k...Off2023 / record-head / pool-blob constants in schema_record_layout_2023.h
// (rec2023::) + tshash_compat.h (tshash_compat::). That table is what discriminates a
// pre-2024 runtime layout variant (they all ride the b8dcaf14 compile pin, so their
// COMPILE-time signature is indistinguishable from `current` — see the header comment
// on KnownRuntimeLayoutVariants()). Variant 0 == build 10832117 (the 2023 support
// floor) and its whole V0 family.
//
// The facts are the NAMED constants (not literals), so any edit to a variant-0 offset
// changes this fingerprint — the runtime gate then no longer matches the allow-listed
// string and fails loud: a changed variant-0 table MUST be re-derived, re-validated,
// and re-transcribed. Order + tag strings are STABLE; a "schema"
// domain-separator fact keeps this space disjoint from the compile-time fingerprint.
uint64_t RuntimeLayoutFingerprint() {
  Fnv1a h;
  // rec2023:: and tshash_compat:: are nested namespaces of cs2_schema_walker (this TU),
  // so the constants resolve directly. Fact() widens each to uint64_t.
  Fact(h, "re-runtime-layout-schema", 1);
  // ---- schema_record_layout_2023.h (rec2023::) — record-head + field/type offsets --
  Fact(h, "kClassNameOff2023", rec2023::kClassNameOff2023);
  Fact(h, "kClassSizeOff2023", rec2023::kClassSizeOff2023);
  Fact(h, "kClassFieldCountOff2023", rec2023::kClassFieldCountOff2023);
  Fact(h, "kClassFieldsOff2023", rec2023::kClassFieldsOff2023);
  Fact(h, "kClassBaseClassesOff2023", rec2023::kClassBaseClassesOff2023);
  Fact(h, "kClassTypeScopeOff2023", rec2023::kClassTypeScopeOff2023);
  Fact(h, "kClassBaseCountOff2023", rec2023::kClassBaseCountOff2023);
  Fact(h, "kClassMetaCountOff2023", rec2023::kClassMetaCountOff2023);
  Fact(h, "kClassMetaPtrOff2023", rec2023::kClassMetaPtrOff2023);
  Fact(h, "kFieldStride2023", rec2023::kFieldStride2023);
  Fact(h, "kFieldNameOff2023", rec2023::kFieldNameOff2023);
  Fact(h, "kFieldTypeOff2023", rec2023::kFieldTypeOff2023);
  Fact(h, "kFieldOffsetOff2023", rec2023::kFieldOffsetOff2023);
  Fact(h, "kBaseStride2023", rec2023::kBaseStride2023);
  Fact(h, "kBaseOffsetOff2023", rec2023::kBaseOffsetOff2023);
  Fact(h, "kBaseClassPtrOff2023", rec2023::kBaseClassPtrOff2023);
  Fact(h, "kTypeNameOff2023", rec2023::kTypeNameOff2023);
  Fact(h, "kTypeCategoryOff2023", rec2023::kTypeCategoryOff2023);
  Fact(h, "kTypeAtomicCatOff2023", rec2023::kTypeAtomicCatOff2023);
  Fact(h, "kTypePtrObjectOff2023", rec2023::kTypePtrObjectOff2023);
  Fact(h, "kTypeDeclClassOff2023", rec2023::kTypeDeclClassOff2023);
  Fact(h, "kTypeDeclEnumOff2023", rec2023::kTypeDeclEnumOff2023);
  Fact(h, "kTypeArrayCountOff2023", rec2023::kTypeArrayCountOff2023);
  Fact(h, "kTypeArrayElemOff2023", rec2023::kTypeArrayElemOff2023);
  Fact(h, "kTypeBitfieldCntOff2023", rec2023::kTypeBitfieldCntOff2023);
  Fact(h, "kTypeAtomicTplOff2023", rec2023::kTypeAtomicTplOff2023);
  Fact(h, "kTypeAtomicTpl2Off2023", rec2023::kTypeAtomicTpl2Off2023);
  Fact(h, "kTypeAtomicIntOff2023", rec2023::kTypeAtomicIntOff2023);
  Fact(h, "kEnumNameOff2023", rec2023::kEnumNameOff2023);
  Fact(h, "kEnumSizeOff2023", rec2023::kEnumSizeOff2023);
  Fact(h, "kEnumCountOff2023", rec2023::kEnumCountOff2023);
  Fact(h, "kEnumEnumeratorsOff2023", rec2023::kEnumEnumeratorsOff2023);
  Fact(h, "kEnumTypeScopeOff2023", rec2023::kEnumTypeScopeOff2023);
  Fact(h, "kEnumeratorStride2023", rec2023::kEnumeratorStride2023);
  Fact(h, "kEnumeratorNameOff2023", rec2023::kEnumeratorNameOff2023);
  Fact(h, "kEnumeratorValueOff2023", rec2023::kEnumeratorValueOff2023);
  // ---- tshash_compat.h (tshash_compat::) — CUtlTSHash + pool-blob offsets ----------
  Fact(h, "kCountOffset", tshash_compat::kCountOffset);
  Fact(h, "kBucketArrayOff2023", tshash_compat::kBucketArrayOff2023);
  Fact(h, "kLockSize2023", tshash_compat::kLockSize2023);
  Fact(h, "kBucketStride2023", tshash_compat::kBucketStride2023);
  Fact(h, "kFirstSub2023", tshash_compat::kFirstSub2023);
  Fact(h, "kFirstUncSub2023", tshash_compat::kFirstUncSub2023);
  Fact(h, "kEntryNextSub2023", tshash_compat::kEntryNextSub2023);
  Fact(h, "kEntryDataSub2023", tshash_compat::kEntryDataSub2023);
  Fact(h, "kBucketCount2023", tshash_compat::kBucketCount2023);
  Fact(h, "kPoolBlockSizeOff2023", tshash_compat::kPoolBlockSizeOff2023);
  Fact(h, "kPoolBlocksPerBlobOff2023", tshash_compat::kPoolBlocksPerBlobOff2023);
  Fact(h, "kPoolNumBlobsOff2023", tshash_compat::kPoolNumBlobsOff2023);
  Fact(h, "kPoolBlocksAllocOff2023", tshash_compat::kPoolBlocksAllocOff2023);
  Fact(h, "kPoolBlobHeadOff2023", tshash_compat::kPoolBlobHeadOff2023);
  Fact(h, "kBlobNextOff2023", tshash_compat::kBlobNextOff2023);
  Fact(h, "kBlobNumBytesOff2023", tshash_compat::kBlobNumBytesOff2023);
  Fact(h, "kBlobDataOff2023", tshash_compat::kBlobDataOff2023);
  Fact(h, "kPoolBlockSize2023", tshash_compat::kPoolBlockSize2023);
  Fact(h, "kEntryDataOff2023", tshash_compat::kEntryDataOff2023);
  Fact(h, "kClassTypeScopeSub2023", tshash_compat::kClassTypeScopeSub2023);
  return h.value();
}

}  // namespace

std::string ComputeRuntimeLayoutSignature() {
  std::ostringstream sig;
  sig << "re-2023lt/v1/";
  AppendHex16(sig, RuntimeLayoutFingerprint());
  return sig.str();
}

namespace {

// --- PRE-2024 RUNTIME LAYOUT VARIANT struct fingerprint (V1+) -------------------------
// Hashes the FULL prelayout::Pre2024LayoutOffsets struct: all 35 fields in DECLARATION
// order (tag == field name) after a "re-runtime-layout-v1-struct" domain-separator fact,
// using the SAME Fnv1a/Fact machinery as RuntimeLayoutFingerprint(). This is deliberately
// a DIFFERENT function + DIFFERENT prefix (re-<tag>) from the variant-0 fingerprint above
// so V0 keeps its golden re-2023lt/v1/69a8cb68432fca4f untouched. real_base_shift is the
// ONLY field distinguishing V1 from V0, so it MUST be in the hash — a fingerprint omitting
// it would collide V1 onto V0. It is a SIGNED ptrdiff_t (-40 on V1); mixed as its uint64
// bit pattern (0xFFFFFFFFFFFFFFD8), matching the derivation's Python-verified goldens
// (kVariant0 -> d3ebecefee6511e6, kV1 -> 55202ac2d6e7bfb9). Order/tags are STABLE;
// editing the struct's field set/order MUST update the layout-probe-unit golden in lockstep.
uint64_t RuntimeLayoutStructFingerprint(const prelayout::Pre2024LayoutOffsets& t) {
  Fnv1a h;
  Fact(h, "re-runtime-layout-v1-struct", 1);
  Fact(h, "real_base_shift", static_cast<uint64_t>(t.real_base_shift));
  Fact(h, "count_off", t.count_off);
  Fact(h, "bucket_array_off", t.bucket_array_off);
  Fact(h, "lock_size", t.lock_size);
  Fact(h, "entry_next_sub", t.entry_next_sub);
  Fact(h, "entry_data_sub", t.entry_data_sub);
  Fact(h, "bucket_count", static_cast<uint64_t>(t.bucket_count));
  Fact(h, "pool_block_size_off", t.pool_block_size_off);
  Fact(h, "pool_blobs_per_off", t.pool_blobs_per_off);
  Fact(h, "pool_num_blobs_off", t.pool_num_blobs_off);
  Fact(h, "pool_blocks_alloc_off", t.pool_blocks_alloc_off);
  Fact(h, "pool_blob_head_off", t.pool_blob_head_off);
  Fact(h, "blob_next_off", t.blob_next_off);
  Fact(h, "blob_numbytes_off", t.blob_numbytes_off);
  Fact(h, "blob_data_off", t.blob_data_off);
  Fact(h, "pool_block_size", static_cast<uint64_t>(t.pool_block_size));
  Fact(h, "class_typescope_sub", t.class_typescope_sub);
  Fact(h, "class_name_off", t.class_name_off);
  Fact(h, "class_size_off", t.class_size_off);
  Fact(h, "class_field_count_off", t.class_field_count_off);
  Fact(h, "class_fields_off", t.class_fields_off);
  Fact(h, "class_base_classes_off", t.class_base_classes_off);
  Fact(h, "class_base_count_off", t.class_base_count_off);
  Fact(h, "class_type_scope_off", t.class_type_scope_off);
  Fact(h, "field_stride", t.field_stride);
  Fact(h, "field_name_off", t.field_name_off);
  Fact(h, "field_type_off", t.field_type_off);
  Fact(h, "field_offset_off", t.field_offset_off);
  Fact(h, "base_stride", t.base_stride);
  Fact(h, "base_offset_off", t.base_offset_off);
  Fact(h, "base_class_ptr_off", t.base_class_ptr_off);
  Fact(h, "type_name_off", t.type_name_off);
  Fact(h, "type_category_off", t.type_category_off);
  Fact(h, "type_atomic_cat_off", t.type_atomic_cat_off);
  Fact(h, "enum_type_scope_off", t.enum_type_scope_off);
  return h.value();
}

}  // namespace

std::string ComputeRuntimeLayoutSignatureFor(const prelayout::Pre2024LayoutOffsets& table,
                                             const char* tag) {
  std::ostringstream sig;
  sig << "re-" << tag << "/v1/";
  AppendHex16(sig, RuntimeLayoutStructFingerprint(table));
  return sig.str();
}

namespace {

// KNOWN-LAYOUTS allow-list. Only signatures validated against a real CS2 binary
// on a matching-OS host and recorded here by a human are accepted; every other
// probe reports `known=false` and the walk fails loud — we never guess past an
// unverified layout.
//
// To register a validated layout: add its exact ComputeLayoutSignature()
// string below with a comment naming the CS2 build it was verified against.
//
// This array is the single source of truth for the allow-list: IsKnownLayout()
// iterates it (via the KnownLayoutSignatures() accessor) and the
// era-pins-consistency ctest asserts it agrees with the inventory eras[].
//
// PER-PLATFORM: the allow-list holds signatures for BOTH host platforms.
// ComputeLayoutSignature() is ABI-specific — a windows-x86_64 walker (MSVC struct
// offsets) computes a DIFFERENT fingerprint from a linux-x86_64 walker (g++ offsets)
// for the SAME era/hl2sdk pin. Each platform's walker computes its own signature and
// checks membership in this ONE flat list, so registering both platforms' validated
// signatures here is correct; the inventory eras[] carries them per-platform.
constexpr std::array<const char*, 22> kKnownLayoutSignatures{
    // July-2026 layout era (cs2-2026-07-09). CS2 builds 24116939 .. 24134959 (the
    // 2026-07-09/10 update). Pinned HL2SDK 5f891c9026230cce0fc0a3fc4b5fef1c467a1385
    // (cs2-branch HEAD, top of the 2026-07-08/09 alliedmodders cluster). The
    // b8dcaf14-compiled walker HARD-CRASHES (AV 0xC0000005) on these builds because the
    // live CS2 ICvar/CCvar vtable moved (hl2sdk 11089e87 'Update ICvar & CCvar' +
    // 1bc5a618 'Add VectorWS as a ConVar type'); recompiling the convar path against the
    // updated headers fixes it. The SCHEMA record headers are UNCHANGED vs b8dcaf14, so
    // the compile-time fingerprint HEX is identical on both platforms (windows
    // 3d1200e346019c59, linux 9f58d9a42d0dd174) — only the embedded SHA prefix differs,
    // which is what distinguishes this validated layout. VALIDATED (windows-x86_64)
    // against builds 24116939 (first) + 24134959 (newest/live): sane CS2 schema, ~1391
    // classes, enums/engine_constants present, byte-identical across runs.
    "hl2sdk-cs2/5f891c9026230cce0fc0a3fc4b5fef1c467a1385/v1/3d1200e346019c59",
    // linux-x86_64 sibling of the 5f891c90 (cs2-2026-07-09) era above — the g++/Itanium-ABI
    // fingerprint the LINUX walker computes for that pin. Hex (9f58d9a42d0dd174) matches the
    // b8dcaf14 linux era (schema layout compile-identical; distinct only by pin SHA + the
    // recompiled ICvar/CCvar convar path). VALIDATED against builds 24116939 + 24134959
    // linux-x86_64: classes/enums/engine_constants match the windows set; byte-identical
    // across runs. Not a guess.
    "hl2sdk-cs2/5f891c9026230cce0fc0a3fc4b5fef1c467a1385/v1/9f58d9a42d0dd174",
    // VALIDATED against CS2 build 23669931 (Steam manifests dated 2026-06-10),
    // windows-x86_64.client, pinned HL2SDK b8dcaf14c603076300cab3861c99b44878d65db4.
    //
    // Validation: a real walk of game/bin/win64 + game/csgo/bin/win64 produced
    // a 496 KB WalkerOutput with 6 populated type scopes (server.dll: 746
    // classes / 11 enums; client.dll: 504 / 4; engine2.dll: 4 classes) and
    // sane CS2 schema — C_BaseEntity, CBaseEntity, CCSPlayerController,
    // CBasePlayerController, C_CSPlayerPawn, enums MoveCollide_t / SolidType_t
    // / gear_slot_t — with plausible field offsets and correct nested template
    // type translation (CHandle< C_BaseModelEntity >, C_NetworkUtlVectorBase<>).
    // The output is byte-identical across runs. The HL2SDK struct
    // layout the walker dereferences therefore matches this build.
    //   ERA: 2026-04-21 .. present (current). hl2sdk change 06b60a9d.
    "hl2sdk-cs2/b8dcaf14c603076300cab3861c99b44878d65db4/v1/3d1200e346019c59",
    // VALIDATED against CS2 build 23773332, linux-x86_64, pinned HL2SDK
    // b8dcaf14c603076300cab3861c99b44878d65db4 (the SAME current era as the
    // windows entry above). This is the g++/Itanium-ABI fingerprint the LINUX
    // walker computes for that era — distinct from the windows hex (3d1200e3)
    // purely because the compiled struct offsets differ by ABI. A full modern-
    // Linux walk was validated END-TO-END: entity_schema (classes/enums),
    // convars, and engine_constants are byte-identical to the committed
    // windows-x86_64 set; network_messages/demo_messages match via the Itanium
    // decoder; commands differ only by 3 genuinely windows-only dev commands.
    // Registering this is recording a VALIDATED layout, not a guess. Only the
    // current era's linux signature is registered so far;
    // the other modern eras' linux signatures are added as they are walked.
    "hl2sdk-cs2/b8dcaf14c603076300cab3861c99b44878d65db4/v1/9f58d9a42d0dd174",
    // Q1-2026 layout era. CS2 builds 2026-01-22 .. 2026-04-02 (the hl2sdk
    // schema-system change landed 2026-01-22 in c58a50c1/4d6b2e31; the next
    // change is 06b60a9d on 2026-04-21). Pinned HL2SDK
    // 0da05cff57162fe8f950192cf73d89e77ab9ee00 (the cs2-branch commit
    // immediately before 06b60a9d). Validated against build 22202104
    // (2026-03-04) windows-x86_64 — sane, era-consistent class set.
    "hl2sdk-cs2/0da05cff57162fe8f950192cf73d89e77ab9ee00/v1/3e396404979881c9",
    // linux-x86_64 sibling of the 0da05cff (cs2-2026-01-22) era above — the
    // g++/Itanium-ABI fingerprint the LINUX walker computes for that pin (distinct
    // hex from the windows 3e396404 purely by ABI). VALIDATED END-TO-END against
    // build 22627914, linux-x86_64: entity_schema (classes/enums), convars, and
    // engine_constants are byte-identical to the committed windows-x86_64 set;
    // commands differ only by the known windows-only dev commands. Registering a
    // validated layout, not a guess.
    "hl2sdk-cs2/0da05cff57162fe8f950192cf73d89e77ab9ee00/v1/defa367b16bb69e4",
    // Late-2025 layout era. CS2 builds ~2025-10-15 .. 2026-01-21 (bounded
    // below by the 2025-10-15 convar change 3c33d8ab and above by the
    // 2026-01-22 schema/convar change 06357c14/c58a50c1). Pinned HL2SDK
    // e54b31c60a4a2034406895206bbeee9bf8c9aef0 (cs2-branch commit just before
    // the Jan-22 walked-layout change). Validated against builds in the era —
    // sane class set + convar-default coherence.
    "hl2sdk-cs2/e54b31c60a4a2034406895206bbeee9bf8c9aef0/v1/f16bfa576cd9ecd1",
    // linux-x86_64 sibling of the e54b31c6 (cs2-2025-10-16) era above — the
    // g++/Itanium-ABI fingerprint the LINUX walker computes for that pin.
    // VALIDATED against build 21529689 linux-x86_64: entity_schema (classes/
    // enums), convars, and engine_constants byte-match the committed windows set;
    // commands differ only by windows-only dev commands. Not a guess.
    "hl2sdk-cs2/e54b31c60a4a2034406895206bbeee9bf8c9aef0/v1/119ddce895e3d2c4",
    // Sep-Oct-2025 layout era. CS2 builds ~2025-09-17 .. 2025-10-13 (bounded
    // below by the 2025-09-17 schema change c2f232b9 and above by the
    // 2025-10-15 convar change 3c33d8ab). Pinned HL2SDK
    // a4fc170d18555b3478f25c447260b7a8839ecbda. This era's hl2sdk references
    // the dllimport CThreadSpinRWLock debug read-lock overloads (see
    // tier0_link_stubs.cpp). Validated against builds in the era.
    "hl2sdk-cs2/a4fc170d18555b3478f25c447260b7a8839ecbda/v1/f56239f2cc7ce9b1",
    // linux-x86_64 sibling of the a4fc170d (cs2-2025-09-17) era above — the
    // g++/Itanium-ABI fingerprint. This is the dllimport-CThreadSpinRWLock era:
    // the linux build needs walker/src/tier0_link_stubs.cpp too (g++ emits the
    // same LockForRead/UnlockRead externals MSVC does — cross-platform probe).
    // VALIDATED against build 20278147 linux-x86_64: classes/enums/convars/
    // engine_constants byte-match the committed windows set (not a guess).
    "hl2sdk-cs2/a4fc170d18555b3478f25c447260b7a8839ecbda/v1/d29067225e0051b4",
    // Aug-2025 layout era. CS2 builds ~2025-07-31 .. 2025-08-14 (post the
    // 2025-07-31 SchemaClassInfoData_t change 41085941, PRE the 2025-08-15
    // convar change e0e3380c). Pinned HL2SDK
    // 3525af9943da07536ba01ce86b54823b1b18ef00. NOTE: the schema fingerprint
    // hex (f56239f2) is IDENTICAL to the a4fc170d era — the layout fingerprint
    // covers schema structs but NOT the full convar layout, and the Aug-15
    // convar change DOES break pre-Aug-15 builds under the a4fc170d walker. So
    // this is a distinct era despite the matching hex; only the SHA differs.
    "hl2sdk-cs2/3525af9943da07536ba01ce86b54823b1b18ef00/v1/f56239f2cc7ce9b1",
    // linux-x86_64 sibling of the 3525af99 (cs2-2025-07-31) era above. Hex matches
    // the a4fc170d linux era (same schema layout, distinct by SHA). This era needs
    // the CCvar/ConCommand MEMORY-MIRROR; its scans probe garbage candidate pointers,
    // which requires the POSIX signal-guarded SafeRead*2023 (posix_crash_guard.h
    // SafeProbeCopy). VALIDATED against build 19605004 linux-x86_64.
    "hl2sdk-cs2/3525af9943da07536ba01ce86b54823b1b18ef00/v1/d29067225e0051b4",
    // Mar-Jul-2025 layout era. CS2 builds ~2025-03-20 .. 2025-07-27 (post the
    // 2025-03-19 change 17aca049, PRE the 2025-07-31 SchemaClassInfoData_t
    // change 41085941). Pinned HL2SDK 07f35e15477913484e7f5017390b75d99ce270fd.
    // (Older hl2sdk lacks SOURCE2MODTOOLS_INTERFACE_VERSION — engine_boot guards
    // it; subsystem schema registers lazily — schema_walk triggers it.)
    "hl2sdk-cs2/07f35e15477913484e7f5017390b75d99ce270fd/v1/e5995ba29396cdc9",
    // linux-x86_64 sibling of the 07f35e15 (cs2-2025-03-20) era above. NEW ConVar
    // API (convar_compat passthrough — standard convar path, no memory-mirror) +
    // dllimport-lock era (tier0_link_stubs). VALIDATED against build 19251152
    // linux-x86_64: classes 1058, enums 5, engine_constants 20 byte-match the
    // committed windows set. Convars differ by a purely RENDERER-specific set
    // (linux 3065 vs windows 3112): 86 windows-only DirectX convars (rtx_*,
    // mat_tonemap_*, volume_fog_*, lb_barnlight_*, csm_*) and 39 linux-only Vulkan
    // convars (sc_aggregate_*, sc_instanced_mesh_*, r_aoproxy_*) — the linux-only
    // set proves this is genuine platform variance, not an under-read. Not a
    // guess: the linux walk faithfully reports the linux binaries' registration.
    "hl2sdk-cs2/07f35e15477913484e7f5017390b75d99ce270fd/v1/aab3ab469f61223c",
    // Mid-Mar-2025 layout era. CS2 builds ~2025-03-12 .. 2025-03-18 (PRE the
    // 2025-03-19 change 17aca049). Pinned HL2SDK
    // f31e5fbbfe6d794b7c7b37977810e7457516a8b6. NOTE: schema hex (e5995ba2)
    // matches era-6, but the Mar-19 change makes the era-6 walker CRASH on
    // pre-Mar-19 builds (convar/other layout differs) — distinct era; SHA differs.
    "hl2sdk-cs2/f31e5fbbfe6d794b7c7b37977810e7457516a8b6/v1/e5995ba29396cdc9",
    // linux-x86_64 sibling of the f31e5fbb (cs2-2025-03-12) era above. Like its
    // windows sibling, the linux schema hex (aab3ab469f61223c) MATCHES the 07f35e15
    // linux era — same schema layout, distinct only by pin SHA. NEW ConVar API
    // (standard path, no memory-mirror) + dllimport-lock era (tier0_link_stubs).
    // VALIDATED against build 17732524 linux-x86_64: classes/enums/engine_constants
    // byte-match the committed windows set; convars differ only by renderer-specific
    // convars (as on 07f35e15). Not a guess.
    "hl2sdk-cs2/f31e5fbbfe6d794b7c7b37977810e7457516a8b6/v1/aab3ab469f61223c",
    // Mid-2024 .. Jan-2025 layout era. CS2 builds ~2024-06-04 .. 2025-01-21
    // (post the 2024-06-03 schema change cc207907 "Update CTSListBase & various
    // schema system naming", PRE the 2025-01-14 CSchemaType change 0ad4360c
    // which first reaches game builds ~2025-01-22). Pinned HL2SDK
    // f3b44f206d38d1b71164e558cd4087d84607d50c (the cs2-branch commit just
    // before 0ad4360c). This is the OLD ConVar API era (ConVarHandle/ConVar*,
    // no ConVarData) — see convar_compat.h layout-mirror path — and the
    // dllimport CThreadSpinRWLock era (tier0_link_stubs.cpp). New schema
    // fingerprint hex (7493eee9) — distinct from every later era, confirming a
    // genuine pre-2025 schema layout. Validated against build 17032840
    // (2025-01-17) windows-x86_64 — era-consistent class set; Pulse system
    // absent (pre-2025, as expected).
    //
    // The era-8 build defines PLATFORM_64BITS (CMakeLists OLD-era branch) so
    // CTSListBase aligns to 16 like the 64-bit game binary, fixing the
    // CSchemaSystemTypeScope binding-chain offsets (m_EnumBindings). This hex
    // (0228864684885e81) is the RE-PROBED, post-fix fingerprint, validated
    // against build 17032840 (2025-01-17): enums now extract correctly
    // (server.dll 4, client.dll 1) and convars/commands match the adjacent
    // era-7 build via the OLD-era index scan. The pre-fix hex was 7493eee9.
    "hl2sdk-cs2/f3b44f206d38d1b71164e558cd4087d84607d50c/v1/0228864684885e81",
    // linux-x86_64 sibling of the f3b44f20 (cs2-2024-06-04) era above. OLD ConVar
    // API (convar_compat layout-mirror) + dllimport-lock era + PLATFORM_64BITS. Its
    // GCC probes needed PLATFORM_64BITS added to their context (int128 typedef) so
    // schematypes/tier0 detect correctly. VALIDATED against build 17032840
    // linux-x86_64; the OLD-ConVar mirror reads correctly via the POSIX safe-read
    // guard (posix_crash_guard.h). Not a guess.
    "hl2sdk-cs2/f3b44f206d38d1b71164e558cd4087d84607d50c/v1/7bd295d1c3a41e34",
    // ~2024-04 .. mid-2024-05-23 layout era. CS2 builds up to 14446408
    // (2024-05-23, the build right below era-8's floor 14470938 of the same
    // day — the CSchemaSystem layout changed mid-day 2024-05-23; era-8's
    // f3b44f20 walker hits "negative type-scope count" on 14446408). Pinned
    // HL2SDK 426ae7f3b47932734656896b79cafd21a5a5e63c (= 5265052f^, the
    // cs2-branch commit just before the 2024-05-25 schema-system batch
    // 5265052f/c5867f9e/bce3bf5c/a96f1056). This era's schematypes.h uses the
    // OLD type-category names (SCHEMA_TYPE_PTR, m_nSize) and still carries the
    // SCHEMA_ATOMIC_TF/TTF atomics later removed — handled via schema_compat.h
    // (WALKER_SCHEMA_TYPES_NEW_NAMES gate). Also OLD ConVar API + dllimport
    // tier0 (same as era-8). New schema fingerprint hex (2ba83716) — distinct
    // layout. Validated against build 14446408.
    "hl2sdk-cs2/426ae7f3b47932734656896b79cafd21a5a5e63c/v1/2ba8371618ce459c",
    // linux-x86_64 sibling of the 426ae7f3 (cs2-2024-04-03) era above. OLD
    // schematypes (SCHEMA_TYPE_PTR/m_nSize -> schema_compat rename map) + OLD ConVar
    // API (convar_compat layout-mirror) + dllimport-lock + PLATFORM_64BITS. Builds
    // via the GCC probe fix (c34dfa7a); mirror reads via the POSIX safe-read guard.
    // VALIDATED against build 14446408 linux-x86_64. Not a guess.
    "hl2sdk-cs2/426ae7f3b47932734656896b79cafd21a5a5e63c/v1/d9d7617e5bab384a",
    // ~2024-03-18 .. 2024-04-01 layout era (pre the "4/2/2024 update"
    // CUtlMemoryPoolBase change aaaaaf04). CS2 builds up to 13829089
    // (2024-03-23); era-9's 426ae7f3 walker crashes on these. Pinned HL2SDK
    // 00644551e4fa9682bce94a556ee1a952b6a463d2 (= aaaaaf04^). Same OLD ConVar
    // API + dllimport tier0 + OLD schematypes naming as era-9 (no new walker
    // code needed — the existing era gates cover it). Lower floor is bounded by
    // ab21c708 (2024-03-18 "Add schemasystem") below which hl2sdk has no schema
    // headers. New schema fingerprint hex (dbd48898) — distinct layout (the
    // pre-4/2 memorypool size differs). Validated against build 13829089.
    "hl2sdk-cs2/00644551e4fa9682bce94a556ee1a952b6a463d2/v1/dbd48898806342d4",
    // linux-x86_64 sibling of the 00644551 (cs2-2024-02-07) era above — the OLDEST
    // modern pin. OLD schematypes + OLD ConVar API + dllimport-lock + PLATFORM_64BITS.
    // Its 2024-02 threadtools.h has an ill-formed injected-class-name copy-ctor that
    // strict GCC rejects (Valve fixed it upstream later); scripts/patch-hl2sdk-gcc-compat.sh
    // applies the semantically-identical syntax fix to the working tree at build time,
    // so the compiled layout/signature is unaffected. VALIDATED against build 13829089
    // linux-x86_64. Not a guess.
    "hl2sdk-cs2/00644551e4fa9682bce94a556ee1a952b6a463d2/v1/21b35e4da46c5389",
};

}  // namespace

std::span<const char* const> KnownLayoutSignatures() {
  return kKnownLayoutSignatures;
}

bool IsKnownLayout(const std::string& signature) {
  for (const char* known : KnownLayoutSignatures()) {
    if (signature == known) return true;
  }
  return false;
}

namespace {

// PRE-2024 RUNTIME LAYOUT VARIANT allow-list (default-deny). See the header
// comment on KnownRuntimeLayoutVariants(). These are RUNTIME-derived offset-table fingerprints
// (form `re-<tag>/v1/<fnv16>`), NOT compile-time `hl2sdk-cs2/...` signatures — the
// pre-2024 eras all ride the `current` (b8dcaf14) compile pin so their compile-time
// signature is indistinguishable from `current` and cannot gate them (that is why
// they are NOT in kKnownLayoutSignatures / the inventory compile-pin eras[]).
//
// Seeded with the VALIDATED variant-0 (V0) 2023 runtime offset table. Deriving + validating
// each further pre-2024 variant computes its runtime-offset fingerprint, whose exact string
// is added here. Still-unvalidated entries
// (documented, NOT yet validated -> NOT allow-listed):
//
//   "re-cs2rel/v1/<TODO fnv16>"  // V1 CS2 full release 2023-09-13+ (offsets TBD)
//
// Do NOT add a stub until its variant validates on a real 2023 binary (byte-identical
// to a committed baseline where one exists, else class-count band + convar canary). An
// unvalidated entry here would defeat the default-deny gate.
constexpr std::array<const char*, 4> kKnownRuntimeLayoutVariants{
    // VARIANT 0 — the 2023 "limited-test" runtime layout (V0), reused UNCHANGED across
    // the whole V0 family. This is the ComputeRuntimeLayoutSignature() of the derived
    // runtime offset table in schema_record_layout_2023.h + tshash_compat.h (the exact
    // 10832117 constants). VALIDATED read-only against build 10832117 (the committed
    // support-floor baseline, dee1524) and against the V0 family reps
    // 10834038 (2023-03-23, oldest window build — reads 1033/657/373/3 == baseline),
    // 11593506 (mid-V0a, 1048), and the V0b ready=0 reps 12083517 / 12126933 / 12147839
    // (2023-08-31 .. 09-09, 1062 each) — every rep reads CBaseEntity / CEntityInstance
    // with resolved base chains + sane field triples, 4/4 convar canaries, organic
    // class growth (no silent mis-read).
    // If any variant-0 offset in those headers changes, RuntimeLayoutFingerprint() moves
    // and the N-way probe fails loud until this string is re-derived + revalidated.
    //
    // RE-TRANSCRIBED f200e8a9a8f1afbb -> 69a8cb68432fca4f: kClassSizeOff2023
    // was corrected 12 -> 24 (the +12 read inside the m_pszName pointer; see
    // schema_record_layout_2023.h). kClassSizeOff2023 is folded into
    // RuntimeLayoutFingerprint() above, so the corrected V0 offset table produces a new
    // fingerprint — exactly the re-derivation this gate is designed to force. The gate
    // MECHANISM (fail-loud on unknown, default-deny) is unchanged; only the allow-listed
    // value moves to reflect the corrected table.
    "re-2023lt/v1/69a8cb68432fca4f",
    // VARIANT 0 — LINUX. Same 2023 V0 family, but the linux (g++) 2023 game DLL lays out
    // its CUtlTSHash/CUtlMemoryPoolBase container at DIFFERENT offsets than the windows
    // (MSVC) 2023 pool, so the platform-gated tshash_compat.h constants (#if __linux__:
    // kCountOffset=100, kBucketArrayOff2023=152, kLockSize2023=32, kPool*Off 88/92/96/100/136)
    // fingerprint to a DISTINCT runtime signature. The RECORD layout is identical cross-OS
    // (schema_record_layout_2023.h unchanged EXCEPT the two type-count offsets below). VALIDATED
    // against linux build 10832117: the variant-0 read recovers 1033 classes with CBaseEntity
    // present — byte-matching the committed windows artifact's class count (enums/engine_constants
    // legitimately EMPTY on 2023, pre-Pulse). The linux offsets themselves came from
    // dump-schema-bytes over that build's libserver.so 657-class scope; they live in
    // tshash_compat.h.
    //
    // RE-TRANSCRIBED e88cc163d1bd3523 -> 73c8566f1779a803 (FIXED_ARRAY/BITFIELD linux offset fix):
    // kTypeArrayCountOff2023 / kTypeBitfieldCntOff2023 were MSVC-only 32; on linux (Itanium ABI
    // tail-padding reuse) the derived `int` sits at +28, so schema_record_layout_2023.h now
    // platform-gates them to 28 (#if __linux__). Both are folded into RuntimeLayoutFingerprint()
    // above, so the corrected linux V0 table re-derives the fingerprint — the SAME re-transcribe-
    // on-offset-correction the windows 69a8cb68 entry documents. The gate mechanism (fail-loud on
    // unknown, default-deny) is unchanged; only the allow-listed value moves to reflect the
    // corrected table. Windows keeps 32 -> its 69a8cb68... entry is byte-identical.
    "re-2023lt/v1/73c8566f1779a803",
    // VARIANT 1 (V1) — the CS2 full-release-2023 runtime layout, covering all 53 builds
    // 12182426 (2023-09-13) .. 13240071 (2024-01-22). This is
    // ComputeRuntimeLayoutSignatureFor(prelayout::kV1, "cs2rel") — the 35-field struct hash
    // of schema_record_layout_v1.h::kV1. V1 differs from V0 by a SINGLE field:
    // real_base_shift = -40 (the class-binding container sits at &m_ClassBindings+40 vs V0's
    // -8; it moved +48 as a rigid unit — see schema_record_layout_v1.h). All
    // other container-pool + record-head offsets are IDENTICAL to V0. Because real_base_shift
    // is NOT in the variant-0 RuntimeLayoutFingerprint() (record/pool named constants only),
    // V1 would COLLIDE onto V0's 69a8cb68... on that hash — hence the distinct struct-hash
    // under the disjoint `re-cs2rel` prefix (kVariant0 hashes to d3ebecefee6511e6 there;
    // V1 to 55202ac2d6e7bfb9). The N-way DetectSchemaVariant probe selects V1 only when the
    // live read validates (CBaseEntity present + class count in band ~[999,1060]) AND this
    // signature is allow-listed.
    //
    // NOT YET BUILT-BINARY VALIDATED: derived + statically evidenced (builds 12182426
    // / 12299470 / 13024819 diffed vs the V0 10832117 reference) but pending validation of
    // the wired walk against a live V1 DLL (reps 12299470 + 13240071). If that
    // validation fails, REVERT this entry (and the inventory eras[] variantSignature) — an
    // unvalidated allow-list entry would defeat the default-deny gate.
    "re-cs2rel/v1/55202ac2d6e7bfb9",
    // VARIANT 1 (V1) — LINUX. Same cs2-2023-09-13 full-release family, but the linux V1
    // class-binding container moved +64 from linux V0 (not windows' +48), so kV1 carries
    // real_base_shift=-56 on linux (schema_record_layout_v1.h #if __linux__) instead of -40.
    // With the linux-gated pool constants (kPoolBlockSizeOff2023=88, kPoolBlobHeadOff2023=136)
    // that reads block_size @ &m_ClassBindings+144 and m_pBlobHead @ +192 (the CBlob with 256
    // blocks). The -56 shift feeds RuntimeLayoutStructFingerprint -> this distinct signature.
    // VALIDATED against linux build 12182426: 1061 classes with CBaseEntity present.
    "re-cs2rel/v1/dea336a9965346ad",
};

}  // namespace

std::span<const char* const> KnownRuntimeLayoutVariants() {
  return kKnownRuntimeLayoutVariants;
}

bool IsKnownRuntimeLayoutVariant(const std::string& signature) {
  for (const char* known : KnownRuntimeLayoutVariants()) {
    if (signature == known) return true;
  }
  return false;
}

std::optional<LayoutProbeResult>
ProbeLayout(const std::filesystem::path& binaries_dir, std::string* err) {
  // Validate the dir before anything else. The host may pass something
  // pathological (empty dir, a regular file). --binaries may be a platform root, so
  // resolve the real CS2 bin subdir(s) exactly as the loader does, then require
  // at least one module under them.
  auto dirs = ResolveBinaryDirs(binaries_dir, err);
  if (!dirs.has_value()) {
    return std::nullopt;  // *err set.
  }
  bool any_module = false;
  for (const auto& d : *dirs) {
    auto modules = DiscoverModules(d, err);
    if (!modules.has_value()) {
      return std::nullopt;  // *err set.
    }
    if (!modules->empty()) {
      any_module = true;
      break;
    }
  }
  if (!any_module) {
    *err = "no Source 2 modules found under " + binaries_dir.string();
    return std::nullopt;
  }

  LayoutProbeResult r;
  r.signature = ComputeLayoutSignature();
  r.known = IsKnownLayout(r.signature);
  return r;
}

}  // namespace cs2_schema_walker
