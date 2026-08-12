// schema_compat.h — clean-room schemasystem/schematypes.h enum + member era shim.
//
// WHY THIS EXISTS
// ----------------
// Valve renamed several schema-type enumerators and one CSchemaType subclass
// member across the 2024-2025 hl2sdk range the walker backfills against. The
// public/schemasystem/schematypes.h surface drifted between the OLDER era-9 pin
// (426ae7f3, 2024-04-26) and the NEWER era-8+ pins (f3b44f20 2025-01 and the
// current b8dcaf14). The SchemaTypeCategory_t / SchemaAtomicCategory_t
// enumerator SPELLINGS changed (the ORDINALS / ordering did not) and
// CSchemaType_Bitfield's single int member was renamed:
//
//   walker canonical (era-8/new)  ->  era-9 (426ae7f3) spelling
//   ----------------------------      -------------------------
//   SCHEMA_TYPE_POINTER               SCHEMA_TYPE_PTR
//   SCHEMA_TYPE_INVALID               SCHEMA_TYPE_NONE
//   SCHEMA_ATOMIC_PLAIN               SCHEMA_ATOMIC_BASIC
//   SCHEMA_ATOMIC_INVALID             SCHEMA_ATOMIC_NONE
//   SCHEMA_BUILTIN_TYPE_COUNT         SCHEMA_BUILTIN_COUNT
//   CSchemaType_Bitfield::            CSchemaType_Bitfield::
//     m_nBitfieldCount                  m_nSize
//
// Every OTHER enumerator the walk references keeps an identical spelling across
// the range (SCHEMA_TYPE_BUILTIN / _BITFIELD / _FIXED_ARRAY / _ATOMIC /
// _DECLARED_CLASS / _DECLARED_ENUM, and SCHEMA_ATOMIC_T / _COLLECTION_OF_T /
// _TT / _I), so they need no shim.
//
// The renamed enumerators occupy the SAME ordinal position in both eras (the
// enums were renamed, not reordered — verified by diffing the pinned headers),
// so substituting the era-9 spelling reads the identical category tag the walk
// expects. WSCHEMA_REQUIRE_ORDINAL() below static_asserts the ordinals so any
// future reorder fails the build loud instead of silently mis-categorizing a type.
//
// This header presents ONE uniform set of canonical names to schema_walk.cpp /
// layout_probe.cpp:
//
//     WSCHEMA_TYPE_POINTER  WSCHEMA_TYPE_INVALID
//     WSCHEMA_ATOMIC_PLAIN  WSCHEMA_ATOMIC_INVALID
//     WSCHEMA_BUILTIN_TYPE_COUNT
//     WSchemaBitfieldCount(const CSchemaType_Bitfield*)  -> the bit count member
//
// ON THE NEW ERA each canonical name resolves to the pinned header's own
// identifier verbatim, so the NEW-era build is byte-for-byte unchanged.
// ON THE OLD (era-9) the canonical name resolves to the 426ae7f3 spelling.
//
// The active era is selected by WALKER_SCHEMA_TYPES_NEW_NAMES, set by the CMake
// configure-time probe in walker/CMakeLists.txt (mirrors the existing
// WALKER_CONVAR_HAS_CONVARDATA_API probe). If the probe is somehow absent we
// fall back to the NEW era (current pin), keeping the default build identical.

#ifndef WALKER_SCHEMA_COMPAT_H_
#define WALKER_SCHEMA_COMPAT_H_

#include <cstdint>

#include "schemasystem/schematypes.h"

namespace cs2_schema_walker {
namespace schema_compat {

#if defined(WALKER_SCHEMA_TYPES_NEW_NAMES)

// ===========================================================================
// NEW ERA (f3b44f20, b8dcaf14, forward) — canonical names map to the pinned
// header's own identifiers verbatim. Byte-identical to the pre-shim code path.
// ===========================================================================
inline constexpr SchemaTypeCategory_t WSCHEMA_TYPE_POINTER = ::SCHEMA_TYPE_POINTER;
inline constexpr SchemaTypeCategory_t WSCHEMA_TYPE_INVALID = ::SCHEMA_TYPE_INVALID;
inline constexpr SchemaAtomicCategory_t WSCHEMA_ATOMIC_PLAIN = ::SCHEMA_ATOMIC_PLAIN;
inline constexpr SchemaAtomicCategory_t WSCHEMA_ATOMIC_INVALID = ::SCHEMA_ATOMIC_INVALID;
inline constexpr int WSCHEMA_BUILTIN_TYPE_COUNT = ::SCHEMA_BUILTIN_TYPE_COUNT;

inline int WSchemaBitfieldCount(const ::CSchemaType_Bitfield* b) {
  return b->m_nBitfieldCount;
}

// Byte offset of the bit-count member within CSchemaType_Bitfield, era-shimmed.
// Mirrors WSchemaBitfieldCount but yields the OFFSET (member-pointer arithmetic on
// a null-based object — the polymorphic-offsetof idiom the forensic dumper uses).
// NEW era: m_nBitfieldCount. Diagnostic-only consumer (stderr).
inline size_t WSchemaBitfieldCountOffset() {
  return reinterpret_cast<size_t>(
      &reinterpret_cast<::CSchemaType_Bitfield*>(0)->m_nBitfieldCount);
}

// NEW-era SchemaAtomicCategory_t ORDINALS. Era-8 REMOVED the TF/TTF atomic
// enumerators (and their CSchemaType subclasses), so the atomic ordinals here
// are NOT the same as era-9's — they are locked per-era. The walk references
// these tags BY NAME, so each era reads the correct ordinal for its own pinned
// header (and its own game binary). These asserts catch a reorder WITHIN the era.
static_assert(WSCHEMA_ATOMIC_PLAIN == 0, "new-era SchemaAtomicCategory_t reordered (PLAIN)");
static_assert(::SCHEMA_ATOMIC_T == 1, "new-era SchemaAtomicCategory_t reordered (T)");
static_assert(::SCHEMA_ATOMIC_COLLECTION_OF_T == 2, "new-era SchemaAtomicCategory_t reordered (COLLECTION_OF_T)");
static_assert(::SCHEMA_ATOMIC_TT == 3, "new-era SchemaAtomicCategory_t reordered (TT)");
static_assert(::SCHEMA_ATOMIC_I == 4, "new-era SchemaAtomicCategory_t reordered (I)");
static_assert(WSCHEMA_ATOMIC_INVALID == 5, "new-era SchemaAtomicCategory_t reordered (INVALID)");

#else  // !WALKER_SCHEMA_TYPES_NEW_NAMES

// ===========================================================================
// OLD ERA (426ae7f3) — canonical names map to the era-9 spellings. The
// TYPE-category ordinals match the new era (renamed, not reordered); the
// ATOMIC-category ordinals do NOT (era-9 still has TF/TTF), but the walk reads
// these tags by name so the right per-era ordinal is used. The static_asserts
// below lock both so a future reorder fails loud rather than mis-mapping.
// ===========================================================================
inline constexpr SchemaTypeCategory_t WSCHEMA_TYPE_POINTER = ::SCHEMA_TYPE_PTR;
inline constexpr SchemaTypeCategory_t WSCHEMA_TYPE_INVALID = ::SCHEMA_TYPE_NONE;
inline constexpr SchemaAtomicCategory_t WSCHEMA_ATOMIC_PLAIN = ::SCHEMA_ATOMIC_BASIC;
inline constexpr SchemaAtomicCategory_t WSCHEMA_ATOMIC_INVALID = ::SCHEMA_ATOMIC_NONE;
inline constexpr int WSCHEMA_BUILTIN_TYPE_COUNT = ::SCHEMA_BUILTIN_COUNT;

inline int WSchemaBitfieldCount(const ::CSchemaType_Bitfield* b) {
  return b->m_nSize;
}

// Byte offset of the bit-count member within CSchemaType_Bitfield, era-shimmed.
// Mirrors WSchemaBitfieldCount but yields the OFFSET (member-pointer arithmetic on
// a null-based object). OLD era (426ae7f3): the member is spelled m_nSize.
// Diagnostic-only consumer (stderr).
inline size_t WSchemaBitfieldCountOffset() {
  return reinterpret_cast<size_t>(
      &reinterpret_cast<::CSchemaType_Bitfield*>(0)->m_nSize);
}

// OLD-era (426ae7f3) SchemaAtomicCategory_t ORDINALS. This era still carries the
// TF (=3) and TTF (=5) atomic enumerators that era-8 later removed, so TT=4 and
// I=6 here (vs TT=3 / I=4 on the new era). The walk references these tags BY
// NAME, so it reads the correct per-era ordinal; these asserts lock the era-9
// ordering so a future re-pin to a reordered OLD header fails loud.
static_assert(WSCHEMA_ATOMIC_PLAIN == 0, "old-era SchemaAtomicCategory_t reordered (BASIC)");
static_assert(::SCHEMA_ATOMIC_T == 1, "old-era SchemaAtomicCategory_t reordered (T)");
static_assert(::SCHEMA_ATOMIC_COLLECTION_OF_T == 2, "old-era SchemaAtomicCategory_t reordered (COLLECTION_OF_T)");
static_assert(::SCHEMA_ATOMIC_TT == 4, "old-era SchemaAtomicCategory_t reordered (TT)");
static_assert(::SCHEMA_ATOMIC_I == 6, "old-era SchemaAtomicCategory_t reordered (I)");
static_assert(WSCHEMA_ATOMIC_INVALID == 7, "old-era SchemaAtomicCategory_t reordered (NONE)");

#endif  // WALKER_SCHEMA_TYPES_NEW_NAMES

// ---------------------------------------------------------------------------
// Canonical atomic-category CODE for the artifact (issue #8). The values are
// the entity_schema.proto SchemaType.AtomicCategory ordinals — ERA-INDEPENDENT
// by construction, unlike the raw SchemaAtomicCategory_t ordinals (era-8
// removed TF/TTF, shifting TT/I/INVALID; see the per-era asserts above). The
// mapping compares BY NAME through the per-era constants, so each era build
// reads its own header's ordinals and emits the same canonical code.
// 0 = unknown tag (emitted as ATOMIC_UNSPECIFIED, never guessed).
// ---------------------------------------------------------------------------
inline std::uint8_t WSchemaAtomicCategoryCode(std::uint8_t acat) {
  if (acat == static_cast<std::uint8_t>(WSCHEMA_ATOMIC_PLAIN)) return 1;
  if (acat == static_cast<std::uint8_t>(::SCHEMA_ATOMIC_T)) return 2;
  if (acat == static_cast<std::uint8_t>(::SCHEMA_ATOMIC_COLLECTION_OF_T)) return 3;
  if (acat == static_cast<std::uint8_t>(::SCHEMA_ATOMIC_TT)) return 4;
  if (acat == static_cast<std::uint8_t>(::SCHEMA_ATOMIC_I)) return 5;
  if (acat == static_cast<std::uint8_t>(WSCHEMA_ATOMIC_INVALID)) return 6;
#if !defined(WALKER_SCHEMA_TYPES_NEW_NAMES)
  // Pre-era-8 pins still declare the TF/TTF enumerators (and their subclasses).
  if (acat == static_cast<std::uint8_t>(::SCHEMA_ATOMIC_TF)) return 7;
  if (acat == static_cast<std::uint8_t>(::SCHEMA_ATOMIC_TTF)) return 8;
#endif
  return 0;
}

// ---------------------------------------------------------------------------
// COLLECTION_OF_T fixed-buffer capacity (issue #8):
// CSchemaType_Atomic_CollectionOfT.m_nFixedBufferCount — the `N` of
// CUtlVectorFixedGrowable< T, N >. The member exists on 8 of the 11 era pins;
// the three OLDEST compile pins (f3b44f20, 426ae7f3, 00644551) declare the
// class with only m_pfnManipulator + m_nElementSize. Presence is selected by
// the WALKER_SCHEMA_HAS_COLLECTION_FIXED_BUFFER_COUNT configure probe
// (walker/CMakeLists.txt, same pattern and cache caveats as the other probes);
// absent-member eras return 0 — their artifact status quo.
// ---------------------------------------------------------------------------
#if defined(WALKER_SCHEMA_HAS_COLLECTION_FIXED_BUFFER_COUNT)
inline std::uint64_t WSchemaCollectionFixedBufferCount(const ::CSchemaType* t) {
  return static_cast<std::uint64_t>(
      static_cast<const ::CSchemaType_Atomic_CollectionOfT*>(t)->m_nFixedBufferCount);
}
#else
inline std::uint64_t WSchemaCollectionFixedBufferCount(const ::CSchemaType*) {
  return 0;
}
#endif

// ---------------------------------------------------------------------------
// Cross-era SchemaTypeCategory_t ordinal locks. The TYPE-category
// enum keeps an IDENTICAL ordering on BOTH eras (only POINTER/PTR and
// INVALID/NONE were renamed, not reordered — verified by diffing the pinned
// headers), so these ordinals hold in both branches. The walk's category switch
// only reads the right subclass if these tags keep their value. The UNRENAMED
// tags are referenced directly (they exist in both eras); the RENAMED tags via
// the canonical aliases above. A reorder fails the build instead of silently
// downcasting to the wrong CSchemaType subclass.
//
// (The SchemaAtomicCategory_t ordinals DIFFER across eras — era-8 dropped the
// TF/TTF enumerators — so those asserts live inside each era branch above.)
// ---------------------------------------------------------------------------
static_assert(::SCHEMA_TYPE_BUILTIN == 0, "SchemaTypeCategory_t reordered (BUILTIN)");
static_assert(WSCHEMA_TYPE_POINTER == 1, "SchemaTypeCategory_t reordered (PTR/POINTER)");
static_assert(::SCHEMA_TYPE_BITFIELD == 2, "SchemaTypeCategory_t reordered (BITFIELD)");
static_assert(::SCHEMA_TYPE_FIXED_ARRAY == 3, "SchemaTypeCategory_t reordered (FIXED_ARRAY)");
static_assert(::SCHEMA_TYPE_ATOMIC == 4, "SchemaTypeCategory_t reordered (ATOMIC)");
static_assert(::SCHEMA_TYPE_DECLARED_CLASS == 5, "SchemaTypeCategory_t reordered (DECLARED_CLASS)");
static_assert(::SCHEMA_TYPE_DECLARED_ENUM == 6, "SchemaTypeCategory_t reordered (DECLARED_ENUM)");
static_assert(WSCHEMA_TYPE_INVALID == 7, "SchemaTypeCategory_t reordered (NONE/INVALID)");

}  // namespace schema_compat
}  // namespace cs2_schema_walker

#endif  // WALKER_SCHEMA_COMPAT_H_
