// schema_record_layout_2023.h — clean-room era-gated SCHEMA-RECORD field accessors.
//
// WHY THIS EXISTS
// ----------------
// tshash_compat.h already era-gates the CONTAINER walk: on the 2023 CS2 layout it
// recovers the SAME CSchemaClassInfo* / CSchemaEnumInfo* binding pointers the
// compiled path would yield (via the pinned pool-blob walk). But the RECORDS those
// pointers reference — SchemaClassInfoData_t, SchemaClassFieldData_t, CSchemaType
// (+ subclasses), SchemaEnumInfoData_t, SchemaEnumeratorInfoData_t,
// SchemaBaseClassInfoData_t — have DIFFERENT member offsets on 2023 than the
// b8dcaf14-compiled layout the walker links against. EmitClass / EmitEnum /
// TranslateType read those records by direct C++ member access at the COMPILED
// offsets, so on a 2023 binding they read the wrong bytes (and fault on the
// pointer members). This header routes every such raw read through an ERA-GATED
// accessor:
//
//   - kModern: read the COMPILED struct member straight through. The accessor is a
//     thin inline wrapper over the exact same `ci->m_pszName` / `fd->m_pType` /
//     `t->m_eTypeCategory` member access EmitClass used before — so the modern
//     output is BYTE-IDENTICAL. The accessor never even computes an offset
//     on kModern; it returns the compiler-resolved member.
//   - k2023: read at the DERIVED 2023 offset (the k2023... constants below), via a
//     bounded, SEH-guarded raw read (Read2023*). A wrong offset degrades to an
//     empty/zero value, never a fault.
//
// SINGLE TRANSCRIPTION POINT
// --------------------------
// The k2023* offset constants below are the ONE place the empirically-derived 2023
// record layout is transcribed (mirroring tshash_compat.h's kPool...Off2023 / bucket
// constants for the container layout). They were derived + validated read-only against
// build 10832117, cross-checked against committed ground truth from the adjacent-era
// build 13385739 (which the modern path walks correctly). To change them, re-derive against
// a 2023 binary and transcribe the verdict here — never guess.
//
// VALIDATED / DERIVED 2023 OFFSETS (build 10832117)
// -------------------------------------------------
// SchemaClassInfoData_t (validated 3/3 — CBaseEntity / CEntityInstance /
//   CCSPlayerController): m_pszName@+8, m_nSize@+24(int32; corrected from a wrong +12
//   that read inside the name pointer — see kClassSizeOff2023 for the derivation),
//   m_nFieldCount@+28(u16), m_pFields@+40, m_pBaseClasses@+56, m_nBaseClassCount@+35,
//   m_pTypeScope@+80.
// SchemaClassFieldData_t (validated): stride 32, m_pszName@+0, m_pType@+8,
//   m_nSingleInheritanceOffset@+16. (== modern layout; no delta.)
// SchemaBaseClassInfoData_t: m_nOffset@+0(u32), m_pClass@+8. (== modern.)
// CSchemaType: vtable@+0, m_sTypeName(char*)@+8, m_eTypeCategory@+?,
//   m_eAtomicCategory@+?, subclass ptrs (m_pObjectType / m_pClassInfo / m_pEnumInfo /
//   m_pElementType / m_pTemplateType / ...) at their derived sub-offsets.
// SchemaEnumInfoData_t: m_pszName@+8, m_nSize@+?, m_nEnumeratorCount@+?,
//   m_pEnumerators@+?, m_pTypeScope@+?.
// SchemaEnumeratorInfoData_t: m_pszName@+0, m_nValue(i64)@+8.
//
// VALIDATION STATE: the class-info, field-record, base-class, base-count,
// enum-record, and CSchemaType + subclass sub-offsets below are all VALIDATED
// against build 10832117 — each per-constant comment records how it was derived.
// The only remaining gaps are the class/enumerator static-METADATA offsets
// (kClassMetaCountOff2023 / kClassMetaPtrOff2023 == 0, the "do not emit" sentinel —
// see their note) and the enum-POOL location, which lives on the container side
// (tshash_compat.h), not here.

#ifndef WALKER_SCHEMA_RECORD_LAYOUT_2023_H_
#define WALKER_SCHEMA_RECORD_LAYOUT_2023_H_

#include "sdk_schema.h"     // SchemaClassInfoData_t, CSchemaType, ... (compiled)
#include "schema_compat.h"  // schema_compat::WSchemaBitfieldCount (era-shimmed)
#include "tshash_compat.h"  // tshash_compat::Era + SafeReadPtr2023/SafeReadBytes2023

#include <cstddef>
#include <cstdint>
#include <cstring>
#include <type_traits>  // std::void_t / std::true_type (enrichment member-presence trait)
#include <utility>      // std::declval (enrichment member-presence trait)

namespace cs2_schema_walker {
namespace rec2023 {

using tshash_compat::Era;
using tshash_compat::LooksLikePointer2023;
using tshash_compat::SafeReadBytes2023;
using tshash_compat::SafeReadPtr2023;

// ===========================================================================
// PINNED / DERIVED 2023 RECORD OFFSETS (build 10832117). SINGLE TRANSCRIPTION POINT.
// ===========================================================================

// ---- SchemaClassInfoData_t (VALIDATED 3/3) ----
inline constexpr std::size_t kClassNameOff2023 = 8;  // const char* m_pszName
// m_nSize (int32): +24, not +12. A +12 read falls inside the 8-byte m_pszName
// pointer (name@+8), so Read2023I32(ci,12) returns the name pointer's HIGH 32 bits —
// an ASLR'd DLL-base high word (0x00007FF8 == 32760 on Windows), UNIFORM across all
// 1033 classes and DIFFERENT per walker/DLL load (build-dependent, breaks
// byte-identical output). With +12, all 943 emitted sizes read as 32760; the modern
// neighbor 13385739 shows real varying sizes (CEntityInstance 56, CBaseEntity 1216,
// CBasePlayerController 1720, ...).
//
// HOW +24 WAS DERIVED (never guess): the 2023 class head drops exactly ONE
// leading const char* vs the b8dcaf14-compiled head (binding@0, name@8, then a
// SINGLE trailing name pointer @16, then m_nSize@24, m_nFieldCount@28) — a uniform
// -8 shift of the head that is INDEPENDENTLY PINNED by the already-VALIDATED
// m_nFieldCount@+28 (delta -8 from compiled +36) and m_pFields@+40 (delta
// -8 from compiled +48). On the compiled layout m_nSize is the int32 immediately
// preceding m_nFieldCount (size@+32, fieldcount@+36, gap 4); the 2023 head keeps
// that adjacency, so m_nSize sits at fieldcount-4 = +28-4 = +24. It is the UNIQUE
// int-typed slot between the name-pointer region (+8..+23, two 8-byte pointers) and
// the validated field-count (+28); +12/+16/+20 all fall inside those two pointers
// (which is why +12 read pointer bytes). m_nSize is a schema-compiler CONSTANT baked
// into the record (not a relocated pointer), so reading it targets input-DLL record
// memory and is build-INDEPENDENT by construction. Confirmed at +24: 10832117 sizes
// are real varying per-class values.
inline constexpr std::size_t kClassSizeOff2023 = 24;         // int32  m_nSize
inline constexpr std::size_t kClassFieldCountOff2023 = 28;   // uint16 m_nFieldCount
inline constexpr std::size_t kClassFieldsOff2023 = 40;       // SchemaClassFieldData_t* m_pFields
inline constexpr std::size_t kClassBaseClassesOff2023 = 56;  // SchemaBaseClassInfoData_t* m_pBaseClasses
inline constexpr std::size_t kClassTypeScopeOff2023 = 80;    // CSchemaSystemTypeScope* m_pTypeScope
// m_nBaseClassCount (u8): derived by a ground-truth sweep == +35, NOT the
// compiled +41 and NOT +33.
//
// HOW +35 WAS DERIVED (never guess): compute each class's TRUE parent
// count structurally — the number of LEADING m_pBaseClasses[] entries whose m_pClass is
// itself an ENUMERATED CLASS BINDING (membership test against the pool-walk binding set;
// this rejects the adjacent enum/metadata pointers like "DamageTypes_t" /
// "MPropertyFriendlyName" that resolve as NAMES but are not class parents) — then sweeps
// the small-int block for the byte whose u8 equals that count for the most classes.
// Result on build 10832117 server.dll: +35 resolves 587/641 nonzero-parent classes; the
// next-best offset resolves only 50 (the ~8% miss at +35 is the freed pool blocks — 769
// blobs enumerated vs 657 live — whose count byte is garbage). Every other small offset
// is a constant-0-ish field (0/641 nonzero). Ground-truth parents confirmed: CBaseEntity
// ->CEntityInstance, CBaseModelEntity->CBaseEntity, CBaseAnimGraph->CBaseModelEntity,
// CCSPlayerController->CBasePlayerController, and CEntityInstance->IHandleEntity (NOT a
// root — a "+33 because CEntityInstance==0" assumption is doubly wrong: wrong
// expected count AND wrong offset; +33 is a constant-0 field, which is why it emits
// ZERO parents for every class).
//
// WHY +35 (delta -6 from compiled +41, not a uniform -8): the 2023 head compresses
// m_nFieldCount +36->+28 (-8) and m_pFields +48->+40 (-8), but the m_nBaseClassCount
// byte sits -6 from compiled, so the small-field sub-block is NOT a uniform shift — it
// is derived empirically, not extrapolated, and confirmed at runtime. The
// EmitClass k2023 path additionally drops
// any empty-name parent defensively, so the freed-block garbage counts can never inject
// an empty parent into the output.
inline constexpr std::size_t kClassBaseCountOff2023 = 35;  // uint8 m_nBaseClassCount
// Class-level static metadata count/ptr: the offsets are not yet derived on 2023 and
// are unsafe to guess, so we read 0 entries (the host re-parses metadata, which is
// best-effort). The 0 here is the sentinel "do not emit class metadata on 2023".
inline constexpr std::size_t kClassMetaCountOff2023 = 0;  // 0 == "do not emit class metadata on 2023"
inline constexpr std::size_t kClassMetaPtrOff2023 = 0;

// ---- SchemaClassFieldData_t (VALIDATED — == modern) ----
inline constexpr std::size_t kFieldStride2023 = 32;
inline constexpr std::size_t kFieldNameOff2023 = 0;     // const char* m_pszName
inline constexpr std::size_t kFieldTypeOff2023 = 8;     // CSchemaType*  m_pType
inline constexpr std::size_t kFieldOffsetOff2023 = 16;  // int m_nSingleInheritanceOffset

// ---- SchemaBaseClassInfoData_t (== modern) ----
inline constexpr std::size_t kBaseStride2023 = 16;
inline constexpr std::size_t kBaseOffsetOff2023 = 0;    // uint m_nOffset
inline constexpr std::size_t kBaseClassPtrOff2023 = 8;  // CSchemaClassInfo* m_pClass

// ---- CSchemaType + subclasses ----
// CSchemaType is polymorphic: vtable@0, then m_sTypeName (CUtlString == char* at +8),
// m_pTypeScope, m_eTypeCategory (u8), m_eAtomicCategory (u8). On the modern compiled
// layout: m_sTypeName@8, m_pTypeScope@16, m_eTypeCategory@24, m_eAtomicCategory@25.
inline constexpr std::size_t kTypeNameOff2023 = 8;        // CUtlString m_sTypeName (char*)
inline constexpr std::size_t kTypeCategoryOff2023 = 24;   // SchemaTypeCategory_t (u8)
inline constexpr std::size_t kTypeAtomicCatOff2023 = 25;  // SchemaAtomicCategory_t (u8)
// Subclass payload offsets (each subclass appends after the CSchemaType base; the base
// is COMPILED-SAME on 2023 — confirmed m_sTypeName@+8 / m_eTypeCategory@+24 — so
// every subclass member that simply trails the base is also compiled-same UNLESS an
// intermediate (CSchemaType_Atomic) differs). Each pointer offset is validated by
// following it and resolving the pointed-to type/binding name against ground truth
// (CBaseEntity.m_CBodyComponent "CBodyComponent*" ->
// Ptr.m_pObjectType -> DeclaredClass "CBodyComponent"; m_aThinkFunctions
// "CUtlVector< thinkfunc_t >" -> Atomic_T.m_pTemplateType -> "thinkfunc_t").
//
// VALIDATED COMPILED-SAME (build 10832117; base @+24 confirmed compiled-same):
//   Ptr.m_pObjectType@+32, DeclaredClass.m_pClassInfo@+32, DeclaredEnum.m_pEnumInfo@+32,
//   FixedArray{m_nElementCount@+32, m_pElementType@+40}, Bitfield.m_nBitfieldCount@+32,
//   Atomic_T.m_pTemplateType@+48, Atomic_TT.m_pTemplateType2@+56.
// These derive from the b8dcaf14 schematypes.h compiled layout (CSchemaType base padded
// to 32; CSchemaType_Atomic base padded to 48), re-confirmed on 2023.
inline constexpr std::size_t kTypePtrObjectOff2023 = 32;  // CSchemaType_Ptr::m_pObjectType
inline constexpr std::size_t kTypeDeclClassOff2023 = 32;  // CSchemaType_DeclaredClass::m_pClassInfo
inline constexpr std::size_t kTypeDeclEnumOff2023 = 32;   // CSchemaType_DeclaredEnum::m_pEnumInfo
// FixedArray: int m_nElementCount, then m_pElementType after element-size/align.
// PLATFORM-GATED (BUG FIX): the CSchemaType base is { vptr@0; CUtlString m_sTypeName@8;
// CSchemaSystemTypeScope* m_pTypeScope@16; SchemaTypeCategory_t m_eTypeCategory(u8)@24;
// SchemaAtomicCategory_t m_eAtomicCategory(u8)@25 } — data ends at 26, alignment 8, and
// the base is NON-POD (virtual dtor). MSVC pads sizeof(CSchemaType) to 32 and places the
// derived `int m_nElementCount` / `int m_nBitfieldCount` at +32. The Itanium ABI (linux
// g++) REUSES the base's tail padding: the derived int lands at the first 4-aligned
// offset >= dsize(26) == +28. This is the SAME offset the kModern hl2sdk accessor
// (static_cast<CSchemaType_FixedArray*>(t)->m_nElementCount) resolves to per-compiler, so
// the modern path was always correct on both OSes — only this raw-offset k2023 branch was
// MSVC-only. Empirically confirmed on linux 2023 (m_iszPlayerName FIXED_ARRAY count==128).
// m_pElementType stays @40 on BOTH (m_nElementSize u16 + m_nElementAlignment u8 then
// 8-align → 40), so only the count moves; windows keeps 32 → byte-identical.
#if defined(__linux__)
inline constexpr std::size_t kTypeArrayCountOff2023 = 28;  // LINUX: CSchemaType_FixedArray::m_nElementCount (Itanium tail-padding reuse)
#else
inline constexpr std::size_t kTypeArrayCountOff2023 = 32;  // CSchemaType_FixedArray::m_nElementCount (MSVC)
#endif
inline constexpr std::size_t kTypeArrayElemOff2023 = 40;  // CSchemaType_FixedArray::m_pElementType (same both OSes)
#if defined(__linux__)
inline constexpr std::size_t kTypeBitfieldCntOff2023 = 28;  // LINUX: CSchemaType_Bitfield::m_nBitfieldCount (Itanium tail-padding reuse)
#else
inline constexpr std::size_t kTypeBitfieldCntOff2023 = 32;  // CSchemaType_Bitfield::m_nBitfieldCount (MSVC)
#endif
// Atomic_T / Atomic_TT template ptrs (after the CSchemaType_Atomic base block @+48).
inline constexpr std::size_t kTypeAtomicTplOff2023 = 48;   // CSchemaType_Atomic_T::m_pTemplateType
inline constexpr std::size_t kTypeAtomicTpl2Off2023 = 56;  // CSchemaType_Atomic_TT::m_pTemplateType2
// Atomic_I::m_nInteger trails the CSchemaType_Atomic base (@+48), NOT @+40 — the previous
// +40 placeholder was wrong (it pointed inside the Atomic base's m_nAtomicID region). The
// compiled member is @+48 (first int after the 48-byte CSchemaType_Atomic base); 2023 is
// compiled-same since the Atomic base is compiled-same (base confirmed).
inline constexpr std::size_t kTypeAtomicIntOff2023 = 48;  // CSchemaType_Atomic_I::m_nInteger

// ---- SchemaEnumInfoData_t ----
inline constexpr std::size_t kEnumNameOff2023 = 8;          // const char* m_pszName
inline constexpr std::size_t kEnumSizeOff2023 = 24;         // uint8 m_nSize
inline constexpr std::size_t kEnumCountOff2023 = 28;        // uint16 m_nEnumeratorCount
inline constexpr std::size_t kEnumEnumeratorsOff2023 = 32;  // SchemaEnumeratorInfoData_t* m_pEnumerators
inline constexpr std::size_t kEnumTypeScopeOff2023 = 48;    // CSchemaSystemTypeScope* m_pTypeScope

// ---- SchemaEnumeratorInfoData_t ----
inline constexpr std::size_t kEnumeratorStride2023 = 32;
inline constexpr std::size_t kEnumeratorNameOff2023 = 0;   // const char* m_pszName
inline constexpr std::size_t kEnumeratorValueOff2023 = 8;  // int64 m_nValue

// ===========================================================================
// Bounded, SEH-guarded raw readers off a 2023 record base + literal sub-offset.
// All return a zero/empty value on an unreadable address. They reuse the
// same SEH-guarded primitives tshash_compat already exposes, so a 2023 read can
// never fault the walk.
// ===========================================================================
inline const char* Read2023CharPtr(const void* base, std::size_t off) {
  std::uint64_t p = 0;
  if (!SafeReadPtr2023(reinterpret_cast<const unsigned char*>(base) + off, &p)) return nullptr;
  if (!LooksLikePointer2023(p)) return nullptr;
  return reinterpret_cast<const char*>(static_cast<std::uintptr_t>(p));
}
inline void* Read2023Ptr(const void* base, std::size_t off) {
  std::uint64_t p = 0;
  if (!SafeReadPtr2023(reinterpret_cast<const unsigned char*>(base) + off, &p)) return nullptr;
  if (!LooksLikePointer2023(p)) return nullptr;
  return reinterpret_cast<void*>(static_cast<std::uintptr_t>(p));
}
inline std::uint8_t Read2023U8(const void* base, std::size_t off) {
  std::uint8_t v = 0;
  if (!SafeReadBytes2023(reinterpret_cast<const unsigned char*>(base) + off, &v, 1)) return 0;
  return v;
}
inline std::uint16_t Read2023U16(const void* base, std::size_t off) {
  std::uint16_t v = 0;
  if (!SafeReadBytes2023(reinterpret_cast<const unsigned char*>(base) + off, &v, 2)) return 0;
  return v;
}
inline std::int32_t Read2023I32(const void* base, std::size_t off) {
  std::int32_t v = 0;
  if (!SafeReadBytes2023(reinterpret_cast<const unsigned char*>(base) + off, &v, 4)) return 0;
  return v;
}
inline std::int64_t Read2023I64(const void* base, std::size_t off) {
  std::int64_t v = 0;
  if (!SafeReadBytes2023(reinterpret_cast<const unsigned char*>(base) + off, &v, 8)) return 0;
  return v;
}

// ===========================================================================
// ERA-GATED ACCESSORS. Each takes the live record pointer + the per-scope Era.
// On kModern: return the compiled struct member (BYTE-IDENTICAL). On k2023:
// read at the derived offset via the guarded readers above.
// ===========================================================================

// ---- SchemaClassInfoData_t ----
inline const char* ClassName(const CSchemaClassInfo* ci, Era era) {
  if (era == Era::kModern) return ci->m_pszName;
  return Read2023CharPtr(ci, kClassNameOff2023);
}
inline std::uint32_t ClassSize(const CSchemaClassInfo* ci, Era era) {
  if (era == Era::kModern) return static_cast<std::uint32_t>(ci->m_nSize);
  return static_cast<std::uint32_t>(Read2023I32(ci, kClassSizeOff2023));
}
inline int ClassFieldCount(const CSchemaClassInfo* ci, Era era) {
  if (era == Era::kModern) return ci->m_nFieldCount;
  return Read2023U16(ci, kClassFieldCountOff2023);
}
inline const SchemaClassFieldData_t* ClassFields(const CSchemaClassInfo* ci, Era era) {
  if (era == Era::kModern) return ci->m_pFields;
  return reinterpret_cast<const SchemaClassFieldData_t*>(
      Read2023Ptr(ci, kClassFieldsOff2023));
}
inline int ClassBaseCount(const CSchemaClassInfo* ci, Era era) {
  if (era == Era::kModern) return ci->m_nBaseClassCount;
  return Read2023U8(ci, kClassBaseCountOff2023);
}
inline const SchemaBaseClassInfoData_t* ClassBaseClasses(const CSchemaClassInfo* ci,
                                                         Era era) {
  if (era == Era::kModern) return ci->m_pBaseClasses;
  return reinterpret_cast<const SchemaBaseClassInfoData_t*>(
      Read2023Ptr(ci, kClassBaseClassesOff2023));
}
inline const CSchemaSystemTypeScope* ClassTypeScope(const CSchemaClassInfo* ci,
                                                    Era era) {
  if (era == Era::kModern) return ci->m_pTypeScope;
  return reinterpret_cast<const CSchemaSystemTypeScope*>(
      Read2023Ptr(ci, kClassTypeScopeOff2023));
}

// ---- SchemaClassFieldData_t (indexed; stride is era-gated) ----
inline const SchemaClassFieldData_t* FieldAt(const SchemaClassFieldData_t* base,
                                             int i, Era era) {
  if (era == Era::kModern) return &base[i];
  return reinterpret_cast<const SchemaClassFieldData_t*>(
      reinterpret_cast<const unsigned char*>(base) +
      static_cast<std::size_t>(i) * kFieldStride2023);
}
inline const char* FieldName(const SchemaClassFieldData_t* fd, Era era) {
  if (era == Era::kModern) return fd->m_pszName;
  return Read2023CharPtr(fd, kFieldNameOff2023);
}
inline const CSchemaType* FieldType(const SchemaClassFieldData_t* fd, Era era) {
  if (era == Era::kModern) return fd->m_pType;
  return reinterpret_cast<const CSchemaType*>(Read2023Ptr(fd, kFieldTypeOff2023));
}
inline int FieldOffset(const SchemaClassFieldData_t* fd, Era era) {
  if (era == Era::kModern) return fd->m_nSingleInheritanceOffset;
  return Read2023I32(fd, kFieldOffsetOff2023);
}

// ---- SchemaBaseClassInfoData_t (indexed) ----
inline const SchemaBaseClassInfoData_t* BaseAt(const SchemaBaseClassInfoData_t* base,
                                               int i, Era era) {
  if (era == Era::kModern) return &base[i];
  return reinterpret_cast<const SchemaBaseClassInfoData_t*>(
      reinterpret_cast<const unsigned char*>(base) +
      static_cast<std::size_t>(i) * kBaseStride2023);
}
inline std::uint32_t BaseOffset(const SchemaBaseClassInfoData_t* bc, Era era) {
  if (era == Era::kModern) return bc->m_nOffset;
  return static_cast<std::uint32_t>(Read2023I32(bc, kBaseOffsetOff2023));
}
inline const CSchemaClassInfo* BaseClassPtr(const SchemaBaseClassInfoData_t* bc,
                                            Era era) {
  if (era == Era::kModern) return bc->m_pClass;
  return reinterpret_cast<const CSchemaClassInfo*>(
      Read2023Ptr(bc, kBaseClassPtrOff2023));
}

// ---- CSchemaType ----
inline const char* TypeName(const CSchemaType* t, Era era) {
  if (era == Era::kModern) return t->m_sTypeName.Get();
  return Read2023CharPtr(t, kTypeNameOff2023);
}
inline std::uint8_t TypeCategory(const CSchemaType* t, Era era) {
  if (era == Era::kModern) return static_cast<std::uint8_t>(t->m_eTypeCategory);
  return Read2023U8(t, kTypeCategoryOff2023);
}
inline std::uint8_t TypeAtomicCategory(const CSchemaType* t, Era era) {
  if (era == Era::kModern) return static_cast<std::uint8_t>(t->m_eAtomicCategory);
  return Read2023U8(t, kTypeAtomicCatOff2023);
}
// Subclass payload accessors. On kModern they downcast + return the compiled member;
// on k2023 they read at the derived sub-offset.
inline const CSchemaType* TypePtrObject(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<const CSchemaType_Ptr*>(t)->m_pObjectType;
  return reinterpret_cast<const CSchemaType*>(Read2023Ptr(t, kTypePtrObjectOff2023));
}
inline const CSchemaClassInfo* TypeDeclClass(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<const CSchemaType_DeclaredClass*>(t)->m_pClassInfo;
  return reinterpret_cast<const CSchemaClassInfo*>(Read2023Ptr(t, kTypeDeclClassOff2023));
}
inline const CSchemaEnumInfo* TypeDeclEnum(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<const CSchemaType_DeclaredEnum*>(t)->m_pEnumInfo;
  return reinterpret_cast<const CSchemaEnumInfo*>(Read2023Ptr(t, kTypeDeclEnumOff2023));
}
inline std::uint64_t TypeArrayCount(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<std::uint64_t>(
        static_cast<const CSchemaType_FixedArray*>(t)->m_nElementCount);
  return static_cast<std::uint64_t>(Read2023I32(t, kTypeArrayCountOff2023));
}
inline const CSchemaType* TypeArrayElem(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<const CSchemaType_FixedArray*>(t)->m_pElementType;
  return reinterpret_cast<const CSchemaType*>(Read2023Ptr(t, kTypeArrayElemOff2023));
}
inline std::uint64_t TypeBitfieldCount(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<std::uint64_t>(schema_compat::WSchemaBitfieldCount(
        static_cast<const CSchemaType_Bitfield*>(t)));
  return static_cast<std::uint64_t>(Read2023I32(t, kTypeBitfieldCntOff2023));
}
inline const CSchemaType* TypeAtomicTemplate(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<const CSchemaType_Atomic_T*>(t)->m_pTemplateType;
  return reinterpret_cast<const CSchemaType*>(Read2023Ptr(t, kTypeAtomicTplOff2023));
}
inline const CSchemaType* TypeAtomicTemplate2(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<const CSchemaType_Atomic_TT*>(t)->m_pTemplateType2;
  return reinterpret_cast<const CSchemaType*>(Read2023Ptr(t, kTypeAtomicTpl2Off2023));
}
inline std::uint64_t TypeAtomicInteger(const CSchemaType* t, Era era) {
  if (era == Era::kModern)
    return static_cast<std::uint64_t>(
        static_cast<const CSchemaType_Atomic_I*>(t)->m_nInteger);
  return static_cast<std::uint64_t>(Read2023I32(t, kTypeAtomicIntOff2023));
}

// ---- SchemaEnumInfoData_t ----
inline const char* EnumName(const CSchemaEnumInfo* ei, Era era) {
  if (era == Era::kModern) return ei->m_pszName;
  return Read2023CharPtr(ei, kEnumNameOff2023);
}
inline std::uint8_t EnumSize(const CSchemaEnumInfo* ei, Era era) {
  if (era == Era::kModern) return ei->m_nSize;
  return Read2023U8(ei, kEnumSizeOff2023);
}
inline int EnumCount(const CSchemaEnumInfo* ei, Era era) {
  if (era == Era::kModern) return ei->m_nEnumeratorCount;
  return Read2023U16(ei, kEnumCountOff2023);
}
inline const SchemaEnumeratorInfoData_t* EnumEnumerators(const CSchemaEnumInfo* ei,
                                                         Era era) {
  if (era == Era::kModern) return ei->m_pEnumerators;
  return reinterpret_cast<const SchemaEnumeratorInfoData_t*>(
      Read2023Ptr(ei, kEnumEnumeratorsOff2023));
}
inline const CSchemaSystemTypeScope* EnumTypeScope(const CSchemaEnumInfo* ei, Era era) {
  if (era == Era::kModern) return ei->m_pTypeScope;
  return reinterpret_cast<const CSchemaSystemTypeScope*>(
      Read2023Ptr(ei, kEnumTypeScopeOff2023));
}

// ---- SchemaEnumeratorInfoData_t (indexed) ----
inline const SchemaEnumeratorInfoData_t* EnumeratorAt(
    const SchemaEnumeratorInfoData_t* base, int i, Era era) {
  if (era == Era::kModern) return &base[i];
  return reinterpret_cast<const SchemaEnumeratorInfoData_t*>(
      reinterpret_cast<const unsigned char*>(base) +
      static_cast<std::size_t>(i) * kEnumeratorStride2023);
}
inline const char* EnumeratorName(const SchemaEnumeratorInfoData_t* e, Era era) {
  if (era == Era::kModern) return e->m_pszName;
  return Read2023CharPtr(e, kEnumeratorNameOff2023);
}
inline std::int64_t EnumeratorValue(const SchemaEnumeratorInfoData_t* e, Era era) {
  if (era == Era::kModern) return e->m_nValue;
  return Read2023I64(e, kEnumeratorValueOff2023);
}

// ===========================================================================
// COMPILE-TIME MEMBER-PRESENCE ACCESSORS for the entity-schema enrichment fields.
// ===========================================================================
//
// WHY THIS EXISTS (multi-era compile)
// -----------------------------------
// The era-gated accessors above abstract the RUNTIME record LAYOUT (kModern vs
// k2023 offset). They do NOT abstract a SECOND, orthogonal axis: whether the
// PINNED hl2sdk struct even DECLARES the member. The enrichment (SchemaClass
// alignment/flags/depths/identity-strings, SchemaEnum flags, per-field/-class/
// -enumerator static metadata) reads several SchemaClassInfoData_t /
// SchemaEnumInfoData_t / ... members that exist in the CURRENT hl2sdk pin
// (b8dcaf14) but NOT in every older era pin the inventory eras[] admits — e.g.
// SchemaClassInfoData_t::m_pszCPPName is absent in all nine older pins. EmitClass /
// EmitEnum used to read those members DIRECTLY off the record (`ci->m_pszCPPName`),
// gated only on a RUNTIME `if (era == kModern)`, which does NOT prevent
// COMPILATION — so the walker failed to build against the older pins
// (schema_walk.cpp:531 C2039 'm_pszCPPName' is not a member of 'CSchemaClassInfo').
//
// THE FIX (detection idiom — auto-adapts, no per-pin #ifdef matrix)
// ----------------------------------------------------------------
// Each accessor below pairs a `has_member`-style trait (SFINAE over
// std::void_t<decltype(...member...)>) with an `if constexpr` switch:
//   - member DECLARED by the pin's struct  -> return the compiled member (the
//     EXACT same `obj->member` read as before, so the current pin's emit is
//     BYTE-IDENTICAL — the load-bearing regression guard);
//   - member ABSENT                          -> return the supplied DEFAULT.
// Because the `if constexpr` condition is type-dependent, the discarded branch is
// NOT instantiated, so `obj->member` is never even compiled on a pin that lacks
// the member. This adapts to WHATEVER each of the 10 pins declares with no
// enumeration of which pin has which field.
//
// WHY DEFAULT == CORRECT SEMANTICS (not a silent loss): an older-era Source2
// schema GENUINELY lacks these fields — the record struct does not carry them — so
// emitting the proto field's default (0 / empty, which proto3 omits on the wire)
// is the truthful "the source has nothing here" answer. This is the SAME
// deferred-with-reason posture as the k2023 alignment/flags gap above (those
// offsets are underived, so they stay 0). The layout gate and fail-loud contract
// are unaffected: this is a compile-time presence switch, not a runtime layout
// guess or a swallowed error.
//
// These are intentionally member-presence ONLY (no Era param): every call site
// below sits inside the existing `if (era == Era::kModern)` enrichment block, so
// the runtime layout is already kModern when these run. The two axes compose: era
// gates the runtime offset (above), presence gates whether the member exists at
// all (here).
//
// WALKER_REC_MEMBER_OR(FN, MEMBER, DEFAULT) defines `FN##_present_<T>` (the trait)
// and `inline auto FN(const T* obj)` returning obj->MEMBER where present else
// DEFAULT. T is deduced from the call (CSchemaClassInfo / CSchemaEnumInfo / ...),
// and the inherited-member lookup sees the *Data_t base members.
#define WALKER_REC_MEMBER_OR(FN, MEMBER, DEFAULT)                              \
  template <typename T, typename = void>                                       \
  struct FN##_present_ : std::false_type {};                                   \
  template <typename T>                                                        \
  struct FN##_present_<T,                                                      \
                       std::void_t<decltype(std::declval<const T&>().MEMBER)>> \
      : std::true_type {};                                                     \
  template <typename T>                                                        \
  inline auto FN(const T* obj) {                                               \
    if constexpr (FN##_present_<T>::value) {                                   \
      return obj->MEMBER;                                                      \
    } else {                                                                   \
      return (DEFAULT);                                                        \
    }                                                                          \
  }

// ---- SchemaClassInfoData_t enrichment members ----
// (m_pszCPPName is the one genuinely absent in all nine older pins; the rest are
// present in every current pin but are wrapped uniformly so a future pin dropping
// any of them auto-adapts. Where present, every accessor returns the identical
// compiled member -> byte-identical current-pin emit.)
WALKER_REC_MEMBER_OR(ClassAlignment, m_nAlignment, std::uint8_t{0})
WALKER_REC_MEMBER_OR(ClassFlags1, m_nFlags1, std::uint32_t{0})
WALKER_REC_MEMBER_OR(ClassFlags2, m_nFlags2, std::uint32_t{0})
WALKER_REC_MEMBER_OR(ClassSingleInheritanceDepth, m_nSingleInheritanceDepth,
                     std::uint16_t{0})
WALKER_REC_MEMBER_OR(ClassMultipleInheritanceDepth, m_nMultipleInheritanceDepth,
                     std::uint16_t{0})
WALKER_REC_MEMBER_OR(ClassProjectName, m_pszProjectName,
                     static_cast<const char*>(nullptr))
WALKER_REC_MEMBER_OR(ClassCppName, m_pszCPPName,
                     static_cast<const char*>(nullptr))
WALKER_REC_MEMBER_OR(ClassStaticMetadata, m_pStaticMetadata,
                     static_cast<const SchemaMetadataEntryData_t*>(nullptr))
WALKER_REC_MEMBER_OR(ClassStaticMetadataCount, m_nStaticMetadataCount, 0)

// ---- SchemaEnumInfoData_t enrichment members ----
WALKER_REC_MEMBER_OR(EnumFlags, m_nFlags, std::uint8_t{0})

// ---- SchemaClassFieldData_t enrichment members ----
WALKER_REC_MEMBER_OR(FieldStaticMetadata, m_pStaticMetadata,
                     static_cast<const SchemaMetadataEntryData_t*>(nullptr))
WALKER_REC_MEMBER_OR(FieldStaticMetadataCount, m_nStaticMetadataCount, 0)

// ---- SchemaEnumeratorInfoData_t enrichment members ----
WALKER_REC_MEMBER_OR(EnumeratorStaticMetadata, m_pStaticMetadata,
                     static_cast<const SchemaMetadataEntryData_t*>(nullptr))
WALKER_REC_MEMBER_OR(EnumeratorStaticMetadataCount, m_nStaticMetadataCount, 0)

#undef WALKER_REC_MEMBER_OR

}  // namespace rec2023
}  // namespace cs2_schema_walker

#endif  // WALKER_SCHEMA_RECORD_LAYOUT_2023_H_
