// CSchemaSystem object-graph traversal. See schema_walk.h.
#include "schema_walk.h"

#include "layout_probe.h"  // ComputeRuntimeLayoutSignature / IsKnownRuntimeLayoutVariant
#include "loader.h"
#include "schema_compat.h"
#include "schema_record_layout_2023.h"  // era-gated record accessors
#include "schema_record_layout_v1.h"    // Pre2024LayoutOffsets / kVariant0 / kV1 (variant tables)
#include "sdk_schema.h"
#include "tshash_compat.h"

#include "entity_schema.pb.h"
#include "walker_output.pb.h"

#include "posix_crash_guard.h"  // POSIX SIGSEGV guard for the KV3-defaults accessor call (no-op on Windows)

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <map>
#include <memory>
#include <mutex>
#include <regex>
#include <string>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#if !defined(_WIN32)
#include <fcntl.h>     // open/O_* for the fork-isolation checkpoint/results files
#include <sys/wait.h>  // waitpid
#include <unistd.h>    // fork/_exit/read/write/lseek/usleep/unlink
#include <csignal>     // kill/SIGKILL
#endif

// Alias must NOT be `pb`: protobuf's own headers declare a global-scope `pb`
// (google/protobuf/extension_set.h), and redefining the alias triggers C2386.
namespace wpb = cs2::schema_tracker::v0;

namespace cs2_schema_walker {

// --- PRE-2024 RUNTIME LAYOUT VARIANT selection (definitions; decl in tshash_compat.h) ---
// This is the ONE place the selected Pre2024LayoutOffsets table is stored. The k2023
// binding-pool readers (tshash_compat.h ReadBindings2023*) read ActivePre2024RealBaseShift()
// to locate real_base. DEFAULT (nothing selected) == V0's +8, so a modern build and every
// V0 build reproduce the exact original `compiled - 8` walk (byte-identical). Only a
// confirmed V1 build sets kV1 (shift -40 -> compiled + 40).
namespace tshash_compat {
namespace {
const prelayout::Pre2024LayoutOffsets* g_active_pre2024_layout = nullptr;
}  // namespace
std::ptrdiff_t ActivePre2024RealBaseShift() {
  // Source the default from kVariant0 (single source of truth) rather than a bare 8 —
  // kVariant0.real_base_shift IS +8, so this stays byte-identical to the original walk.
  return g_active_pre2024_layout ? g_active_pre2024_layout->real_base_shift
                                 : prelayout::kVariant0.real_base_shift;
}
void SetActivePre2024Layout(const prelayout::Pre2024LayoutOffsets* table) {
  g_active_pre2024_layout = table;
}
}  // namespace tshash_compat

namespace {

using Era = tshash_compat::Era;
namespace rec = rec2023;

// Null-safe const char* -> std::string. Valve carries plenty of null name
// pointers on transient/internal records; we treat null as empty rather than
// crashing (fail-loud is for STRUCTURAL corruption, not for an optional
// string being absent).
//
// On the MODERN path the char* came from a compiled member of a record the
// container walk guaranteed is live, so a plain std::string(p) is safe and
// byte-identical. On the 2023 path the char* was resolved by
// Read2023CharPtr, which validated only the POINTER VALUE (LooksLikePointer2023)
// — the BYTES may be unmapped (a freed/garbage record, or a wrong subclass
// downcast landing on a non-pointer). std::string(p) would strlen() those bytes
// and FAULT. So 2023 routes through the SEH-guarded SafeReadCString2023, which
// degrades an unreadable string to "" instead of crashing.
constexpr std::size_t kMaxNameLen = 1024;  // schema names are short; cap the read
std::string Str(const char* p, Era era) {
  if (p == nullptr) return {};
  if (era == Era::kModern) return std::string(p);
  char buf[kMaxNameLen];
  if (!tshash_compat::SafeReadCString2023(p, buf, sizeof(buf))) return {};
  return std::string(buf);
}
// Back-compat overload for the MODERN-only call sites that never see a 2023
// pointer (metadata names off compiled records, etc.).
std::string Str(const char* p) { return p ? std::string(p) : std::string(); }

// The module (type-scope) name a type scope carries. m_szScopeName is a raw
// fixed char buffer at offset 0 of CSchemaSystemTypeScope (after the vtable) — a
// direct field read, no DLL_CLASS_IMPORT method involved.
//
// On MODERN this is a direct buffer read of a scope the vtable walk handed us
// (byte-identical). On 2023 the scope pointer may have come from a
// declared-type subclass downcast (TypeDeclClass -> m_pTypeScope), so it can be a
// garbage-but-canonical-looking pointer; reading the 256-byte buffer raw would
// fault. The 2023 path therefore copies the name through SEH-guarded
// SafeReadCString2023 (m_szScopeName is at scope+0 on every era — the vtable ptr
// is part of CSchemaSystemTypeScope's ISchemaSystemTypeScope base, and
// m_szScopeName is its first data member; the modern compiled access proves the
// +0-relative layout, and the 2023 scope-filter already relies on it).
std::string ScopeName(const CSchemaSystemTypeScope* scope, Era era) {
  if (scope == nullptr) return {};
  if (era == Era::kModern) return Str(scope->m_szScopeName);
  // The char buffer begins at the same address the compiler resolves m_szScopeName
  // to on modern; reinterpret the scope base + that member's compiled offset.
  const char* name =
      reinterpret_cast<const char*>(&scope->m_szScopeName[0]);
  return Str(name, era);  // SEH-guarded byte read on 2023
}

// Module name for a declared class/enum reference: the scope the referenced
// binding lives in. The m_pTypeScope MEMBER read is era-gated (its offset differs
// on 2023); ScopeName itself reads m_szScopeName @ scope+0, which is era-stable.
std::string ClassModule(const CSchemaClassInfo* ci, Era era) {
  if (ci == nullptr) return {};
  return ScopeName(rec::ClassTypeScope(ci, era), era);
}
std::string EnumModule(const CSchemaEnumInfo* ei, Era era) {
  if (ei == nullptr) return {};
  return ScopeName(rec::EnumTypeScope(ei, era), era);
}

// ---- Shared (era, name, module) derivation -------------------------------
//
// The build-level era and the (name, module) key for a class/enum binding are
// read the SAME way at every entry point (DoSchemaEmit's accumulate pass,
// EnumerateLiveSchemaSymbols, EnumerateLiveEnumeratorConstants). Audit correctness
// depends on the universe key being byte-for-byte the extraction key, so these
// derivations are factored here ONCE — a single edit point instead of verbatim
// copies that could silently drift. Pure extraction; same calls, same order, same
// values as the inline copies they replace (byte-identical).

// BUILD-LEVEL era (authoritative env flag, applied to ALL scopes). TRUE iff the
// 2023-only post-boot "SchemaSystem_001" fallback registered modules. Modern
// leaves it FALSE -> kModern everywhere. See loader.h / WalkSchemaSystem.
Era BuildEra(const InProcessEnvironment& env) {
  return env.schema_is_2023_era() ? Era::k2023 : Era::kModern;
}

// (name, module) for a class binding: era-gated rec::ClassName / rec::ClassTypeScope,
// the SAME accessors EmitClass uses (so universe key == artifact key).
SchemaSymbolRef ClassKey(const CSchemaClassInfo* ci, Era era) {
  return {Str(rec::ClassName(ci, era), era),
          ScopeName(rec::ClassTypeScope(ci, era), era)};
}

// (name, module) for an enum binding: era-gated rec::EnumName / rec::EnumTypeScope,
// the SAME accessors EmitEnum uses (so universe key == artifact key).
SchemaSymbolRef EnumKey(const CSchemaEnumInfo* ei, Era era) {
  return {Str(rec::EnumName(ei, era), era),
          ScopeName(rec::EnumTypeScope(ei, era), era)};
}

// ---- Recursive SchemaType translation ------------------------------------
//
// Reads Valve's CSchemaType polymorphic hierarchy by category + atomic-category
// tags and downcasts to the matching subclass, mirroring schematypes.h's
// SCHEMATYPE_ENTRY table. Recurses into inner element/template types so the
// emitted SchemaType faithfully represents CUtlVector<CHandle<T>>, pointers,
// fixed arrays, and bitfields exactly as the binary declares them.
// Bound on recursion through the CSchemaType element/template chain. A correctly
// formed type tree is only a few levels deep (CUtlVector<CHandle<T>> ~3); a CYCLE
// or a wild subclass downcast on 2023 could otherwise recurse until the stack
// overflows (an uncatchable fault). Past the bound we stop descending and emit the
// node as unspecified rather than risk a stack-overflow crash.
inline constexpr int kMaxTypeDepth = 16;

void TranslateTypeDepth(const CSchemaType* t, wpb::SchemaType* out, Era era,
                        int depth);

void TranslateType(const CSchemaType* t, wpb::SchemaType* out, Era era) {
  TranslateTypeDepth(t, out, era, 0);
}

void TranslateTypeDepth(const CSchemaType* t, wpb::SchemaType* out, Era era,
                        int depth) {
  if (t == nullptr || depth > kMaxTypeDepth) {
    out->set_category(wpb::SchemaType::CATEGORY_UNSPECIFIED);
    return;
  }

  // m_sTypeName: kModern reads the compiled CUtlString (Get(), header-inline, no DLL
  // import); k2023 reads the char* at the derived offset (guarded). Both yield the
  // raw type-name string. On 2023 the byte read is SEH-guarded (Str(.,era)).
  const char* tn = rec::TypeName(t, era);
  const std::string type_name = Str(tn, era);

  // The category tag is an era-gated u8 read; compare against the schema_compat
  // ordinals (which are ordinal-locked across eras, see schema_compat.h asserts).
  const std::uint8_t cat = rec::TypeCategory(t, era);

  // Dispatch on the era-stable category ordinal (schema_compat ordinal-locks these
  // via static_assert, so the case labels are distinct compile-time constants).
  // Mechanical 1:1 translation of the prior if-return ladder — same cases, same
  // handling, same default (byte-identical).
  switch (cat) {
    case static_cast<std::uint8_t>(SCHEMA_TYPE_BUILTIN):
      out->set_category(wpb::SchemaType::BUILTIN);
      out->set_name(type_name);
      return;
    case static_cast<std::uint8_t>(schema_compat::WSCHEMA_TYPE_POINTER):
      out->set_category(wpb::SchemaType::PTR);
      out->set_name(type_name);
      TranslateTypeDepth(rec::TypePtrObject(t, era), out->mutable_inner(), era, depth + 1);
      return;
    case static_cast<std::uint8_t>(SCHEMA_TYPE_BITFIELD):
      out->set_category(wpb::SchemaType::BITFIELD);
      out->set_name(type_name);
      out->set_count(rec::TypeBitfieldCount(t, era));
      return;
    case static_cast<std::uint8_t>(SCHEMA_TYPE_FIXED_ARRAY):
      out->set_category(wpb::SchemaType::FIXED_ARRAY);
      out->set_name(type_name);
      out->set_count(rec::TypeArrayCount(t, era));
      TranslateTypeDepth(rec::TypeArrayElem(t, era), out->mutable_inner(), era, depth + 1);
      return;
    case static_cast<std::uint8_t>(SCHEMA_TYPE_DECLARED_CLASS):
      out->set_category(wpb::SchemaType::DECLARED_CLASS);
      out->set_name(type_name);
      out->set_module(ClassModule(rec::TypeDeclClass(t, era), era));
      return;
    case static_cast<std::uint8_t>(SCHEMA_TYPE_DECLARED_ENUM):
      out->set_category(wpb::SchemaType::DECLARED_ENUM);
      out->set_name(type_name);
      out->set_module(EnumModule(rec::TypeDeclEnum(t, era), era));
      return;
    case static_cast<std::uint8_t>(SCHEMA_TYPE_ATOMIC): {
      out->set_category(wpb::SchemaType::ATOMIC);
      out->set_name(type_name);
      // Template arguments depend on the atomic sub-category (era-gated u8 read).
      const std::uint8_t acat = rec::TypeAtomicCategory(t, era);
      if (acat == static_cast<std::uint8_t>(SCHEMA_ATOMIC_T)) {
        TranslateTypeDepth(rec::TypeAtomicTemplate(t, era), out->mutable_inner(), era, depth + 1);
      } else if (acat == static_cast<std::uint8_t>(SCHEMA_ATOMIC_COLLECTION_OF_T)) {
        // CollectionOfT derives from Atomic_T; the template ptr sits at the same
        // sub-offset as Atomic_T::m_pTemplateType.
        TranslateTypeDepth(rec::TypeAtomicTemplate(t, era), out->mutable_inner(), era, depth + 1);
      } else if (acat == static_cast<std::uint8_t>(SCHEMA_ATOMIC_TT)) {
        TranslateTypeDepth(rec::TypeAtomicTemplate(t, era), out->mutable_inner(), era, depth + 1);
        TranslateTypeDepth(rec::TypeAtomicTemplate2(t, era), out->mutable_inner2(), era, depth + 1);
      } else if (acat == static_cast<std::uint8_t>(SCHEMA_ATOMIC_I)) {
        out->set_count(rec::TypeAtomicInteger(t, era));
      }
      // WSCHEMA_ATOMIC_PLAIN / WSCHEMA_ATOMIC_INVALID / others: no template payload.
      return;
    }
    default:
      // WSCHEMA_TYPE_INVALID and any unrecognized tag.
      out->set_category(wpb::SchemaType::CATEGORY_UNSPECIFIED);
      out->set_name(type_name);
      return;
  }
}

// ---- Declared-type module resolution --------------------------------------
//
// The host EntitySchemaEmitter requires SchemaField.type_module to be set
// whenever the field's type tree references a declared class/enum ANYWHERE,
// not only when the field's DIRECT type is declared. Its check
// (ReferencesDeclaredType) returns true if a SchemaType node's category is
// DECLARED_CLASS/DECLARED_ENUM, or recursively any of inner/inner2/inner3 is.
//
// We mirror that recursion EXACTLY over the BUILT proto tree (the same tree the
// emitter recurses), returning the module() of the FIRST declared node found in
// the same visitation order the emitter uses (self, then inner, inner2, inner3).
// TranslateType already stamped module() onto each declared node (see the
// DECLARED_CLASS / DECLARED_ENUM cases above), so no HL2SDK re-traversal is
// needed — this stays purely structural, deterministic, and clean-room.
//
// Returns "" when no declared node exists anywhere in the tree (builtins,
// atomics with no declared element, etc.), matching the documented contract for
// type_module ("'' otherwise").
std::string FirstDeclaredModule(const wpb::SchemaType& t) {
  if (t.category() == wpb::SchemaType::DECLARED_CLASS ||
      t.category() == wpb::SchemaType::DECLARED_ENUM) {
    return t.module();
  }
  if (t.has_inner()) {
    std::string m = FirstDeclaredModule(t.inner());
    if (!m.empty()) return m;
  }
  if (t.has_inner2()) {
    std::string m = FirstDeclaredModule(t.inner2());
    if (!m.empty()) return m;
  }
  if (t.has_inner3()) {
    std::string m = FirstDeclaredModule(t.inner3());
    if (!m.empty()) return m;
  }
  return {};
}

// ---- declared-ref module fallback (name index) ------------------------------
//
// WHY THIS EXISTS. A by-value declared-type reference (e.g. a CHandle<T>
// template arg, an embedded struct) resolves its NAME reliably (read from the
// era-stable CSchemaType base m_sTypeName@+8), but its MODULE —
// ScopeName(ClassTypeScope(m_pClassInfo)) — can come back empty: the
// CSchemaType_DeclaredClass::m_pClassInfo a referenced type points at is
// sometimes a forward-decl / cross-module stub whose m_pTypeScope is null or
// unreadable. The host's schema gate REQUIRES type_module to be non-empty for
// any by-value declared ref, so an empty module fails the extract.
//
// Originally a k2023-only fix (modern's direct pointer-chase always succeeded
// on the narrow per-module walk). Since schema family 0.5.0 the walk includes
// the global "!GlobalTypes" scope, which surfaces stub-referenced declared
// types on MODERN too (measured on cs2-2026-07-09: CEntityAttributeTable's
// m_Attributes), so the index now runs on every era.
//
// FIX: every class/enum we ENUMERATE already has its module resolved successfully
// (the accumulate pass reads ClassTypeScope on the live pool binding, which IS
// readable). So we build a NAME -> {modules} index from that enumerated set and,
// when the direct resolution is empty, look the declared node's name up in it.
// This is pointer-free (so it is immune to the stub-pointer problem), clean-room
// (derived only from bindings the walk already enumerated), and deterministic
// (the modules vector is sorted+deduped; the owner module is preferred when
// present, else the lexicographically-smallest — a stable choice).
// The host only requires type_module to be PRESENT (it does not cross-check that
// the named module actually defines the type), so any module that genuinely
// contains a type of this name is a valid attribution.
using DeclaredModuleIndex = std::map<std::string, std::vector<std::string>>;

// First BY-VALUE declared node's name, using the SAME traversal the host's
// ReferencesDeclaredType uses (self, then inner/inner2/inner3, STOPPING at PTR —
// a pointer is a reference, not an embedding, so the host does not require module
// attribution through it). Returns nullptr when no by-value declared node exists.
const std::string* FirstByValueDeclaredName(const wpb::SchemaType& t) {
  if (t.category() == wpb::SchemaType::DECLARED_CLASS ||
      t.category() == wpb::SchemaType::DECLARED_ENUM) {
    return &t.name();
  }
  if (t.category() == wpb::SchemaType::PTR) return nullptr;  // host stops at ptr
  if (t.has_inner()) {
    const std::string* n = FirstByValueDeclaredName(t.inner());
    if (n != nullptr) return n;
  }
  if (t.has_inner2()) {
    const std::string* n = FirstByValueDeclaredName(t.inner2());
    if (n != nullptr) return n;
  }
  if (t.has_inner3()) {
    const std::string* n = FirstByValueDeclaredName(t.inner3());
    if (n != nullptr) return n;
  }
  return nullptr;
}

// Resolve a declared type NAME to a module via the enumerated-name index. Prefer
// the owner class's own module when the name lives there (the common intra-module
// case); otherwise the lexicographically-smallest module that defines the name
// (deterministic). Empty when the name is not in the index.
std::string ResolveModuleByName(const DeclaredModuleIndex& index,
                                const std::string& name,
                                const std::string& owner_module) {
  auto it = index.find(name);
  if (it == index.end() || it->second.empty()) return {};
  const std::vector<std::string>& mods = it->second;  // sorted + deduped
  for (const std::string& m : mods) {
    if (m == owner_module) return m;  // prefer the owner's module
  }
  return mods.front();  // deterministic fallback (smallest)
}

// ---- Metadata --------------------------------------------------------------
//
// Each SchemaMetadataEntryData_t carries a name and an opaque m_pData blob whose
// interpretation depends on the metadata kind. The walker carries the RAW value
// verbatim and does NOT structurally PARSE it (the host performs the structural
// KV3 parse, populating SchemaMetadata.value_parsed). But the m_pData blob for the
// COMMON scalar/string metadata kinds is a typed value we CAN decode to its raw
// string form, which the host then uses directly. This decode recovers the
// friendly-names / descriptions / numeric ranges the binary carries instead of
// dropping them.
//
// SOURCE2GEN-PROVEN MODEL: the value's type is a typed union keyed by the metadata
// NAME. m_pData points at the value (a string is reached as a const char* AT the
// blob, i.e. *(const char**)m_pData; an int/float is the scalar AT the blob). We
// classify a known NAME into one of {string, int, float, float-range} and decode;
// an UNKNOWN name leaves `value` EMPTY — never a guess, never a fault.
// MGetKV3ClassDefaults is the notable kUnknown: its m_pData is a generated accessor
// (a thunk that serializes a default-constructed instance into a live KeyValues3),
// NOT a resident KV3 string, so there is nothing to copy out headless. See the
// KNOWN-LIMITATION block at the class-metadata emit site below
// for why value/value_parsed are empty on every build. All reads are SEH-bounded
// (tshash_compat Safe* helpers: guarded on Windows, plain on the matching-OS POSIX
// host), so a stale/garbage blob pointer degrades to an empty value rather than
// crashing the walk.

// Classify a metadata NAME into the value encoding of its m_pData blob.
//
// The classification is the AUTHORITATIVE source2gen / DumpSource2 table
// (g_mapMetadataNameToValue in ValveResourceFormat/DumpSource2's metadatalist.h,
// itself sourced from neverlosecc/source2gen) — not a per-attribute guess. Every
// name below carries the type source2gen validated against the live schema system,
// so the decode reads the correct shape. Names NOT listed decode to kUnknown ->
// empty value (safe default; marker metadata with no payload, or anything the
// authoritative table does not cover). MGetKV3ClassDefaults (kKv3Defaults) is a
// generated accessor thunk handled by a live call, NOT a resident read — see
// DecodeKv3Defaults / the class-metadata emit site.
enum class MetaValueKind { kUnknown,
                           kString,           // const char* AT the blob: *(const char**)m_pData
                           kInlineString,     // up to 8 inline chars AT the blob (null-terminated fourcc/token)
                           kInt,              // int32 AT the blob
                           kFloat,            // float AT the blob
                           kVarName,          // CSchemaVarName{ const char* m_pszType; const char* m_pszName; } AT the blob
                           kSendProxyFilter,  // CSchemaSendProxyRecipientsFilter{ const char* m_pszName; ... } AT the blob
                           kKv3Defaults };    // generated GetKV3Defaults accessor thunk (call + serialize)

MetaValueKind ClassifyMetaValue(const std::string& name) {
  // STRING — m_pData holds a const char*; the value is *(const char**)m_pData.
  static const char* const kStringKeys[] = {
      // -- source2gen STRING set --
      "MAlternateSemanticName",
      "MCellForDomain",
      "MCustomFGDMetadata",
      "MEntitySubclassScopeFile",
      "MFgdHelper",
      "MFieldVerificationName",
      "MKV3TransferName",
      "MNetworkAlias",
      "MNetworkChangeCallback",
      "MNetworkChangePointerCallback",
      "MNetworkEncoder",
      "MNetworkExcludeByName",
      "MNetworkExcludeByUserGroup",
      "MNetworkIncludeByName",
      "MNetworkIncludeByUserGroup",
      "MNetworkReplayCompatField",
      "MNetworkSerializer",
      "MNetworkTypeAlias",
      "MNetworkUserGroup",
      "MNetworkUserGroupProxy",
      "MParticleReplacementOp",
      "MPropertyArrayElementNameKey",
      "MPropertyAttributeChoiceName",
      "MPropertyAttributeEditor",
      // MPropertyAttributeRange is a STRING range annotation (MPropertyAttributeRange("min max")):
      // m_pData holds a const char*, NOT an inline two-float [min,max] pair. An earlier kFloatRange
      // path read the 8-byte char* AS two floats and printed build-dependent garbage (the pointer's
      // low dword as a float, high dword 0x00007FF8 as a denormal that prints "0.000000"). Reading it
      // as a string recovers the real range text, a compiler constant in rodata (build-INDEPENDENT).
      // Distinct from MPropertyAttributeRange{Low,High}Inclusive below (genuine inline ints).
      "MPropertyAttributeRange",
      "MPropertyAttributeSuggestionName",
      "MPropertyCustomEditor",
      "MPropertyCustomFGDType",
      "MPropertyDescription",
      "MPropertyExtendedEditor",
      "MPropertyFriendlyName",
      "MPropertyGroupName",
      "MPropertyIconName",
      "MPropertyProvidesEditContextString",
      "MPropertyStartGroup",
      "MPropertySuppressBaseClassField",
      "MPropertySuppressExpr",
      "MPulseEditorCanvasItemSpecKV3",
      "MPulseEditorHeaderIcon",
      "MPulseEditorHeaderText",
      "MPulseRequirementSummaryExpr",
      "MPulseSelectorAllowRequirementCriteria",
      "MResourceBlockType",
      "MScriptDescription",
      "MSrc1ImportAttributeName",
      "MSrc1ImportDmElementType",
      "MVDataAssociatedFile",
      "MVDataFileExtension",
      "MVDataOutlinerIcon",
      "MVDataOutlinerIconExpr",
      "MVDataUniqueMonotonicInt",
      // MVectorIsSometimesCoordinate is a STRING (source2gen), NOT an int — its marker sibling
      // MVectorIsCoordinate has no payload and is correctly left kUnknown.
      "MVectorIsSometimesCoordinate",
      "MKV3TransferSaveOpsForField",
      "MSaveOpsForField",
      "MPropertyReadonlyExpr",
      "MPulseEditorSubHeaderText",
      "MPropertyEditContextOverrideKey",
      "MPropertyEditContextOverrideValue",
      "MVDataClassGroup",
      "MVDataOutlinerDetailExpr",
      "MVDataOutlinerLabelExpr",
      "MVDataOutlinerNameExpr",
      "MVDataPreviewWidget",
      "MWorkshopEnumeratorTagName",
      // -- walker-observed extras not in the source2gen table (kept; plausibly string) --
      "MPropertySuppressField",
  };
  for (const char* k : kStringKeys)
    if (name == k) return MetaValueKind::kString;
  // INLINE_STRING — up to 8 chars stored INLINE at the blob (a fourcc/type token), NOT a pointer.
  static const char* const kInlineStringKeys[] = {
      "MDiskDataForResourceType",
      "MResourceTypeForInfoType",
  };
  for (const char* k : kInlineStringKeys)
    if (name == k) return MetaValueKind::kInlineString;
  // INTEGER — int32 at the blob.
  static const char* const kIntKeys[] = {
      "MAlignment",
      "MGenerateArrayKeynamesFirstIndex",
      "MNetworkBitCount",
      "MNetworkEncodeFlags",
      "MNetworkPriority",
      "MNetworkVarEmbeddedFieldOffsetDelta",
      "MParticleMaxVersion",
      "MParticleMinVersion",
      "MParticleOperatorType",
      "MPropertySortPriority",
      "MResourceVersion",
      "MSaveFlags",
      "MSmartPropClassVersion",
      "MVDataNodeType",
      "MVDataOverlayType",
      "MVDataPromoteField",
      // -- walker-observed extras not in the source2gen table (kept; inline ints) --
      "MPropertyAttributeRangeLowInclusive",
      "MPropertyAttributeRangeHighInclusive",
  };
  for (const char* k : kIntKeys)
    if (name == k) return MetaValueKind::kInt;
  // FLOAT — single float at the blob.
  static const char* const kFloatKeys[] = {
      "MNetworkMinValue",
      "MNetworkMaxValue",
  };
  for (const char* k : kFloatKeys)
    if (name == k) return MetaValueKind::kFloat;
  // VARNAME — CSchemaVarName{ const char* m_pszType; const char* m_pszName; } at the blob. Reading it
  // as a plain string (as the walker did for MNetworkOverride / MNetworkVarTypeOverride) drops the
  // name half; decode both and render "type name".
  static const char* const kVarNameKeys[] = {
      "MNetworkOverride",
      "MNetworkVarNames",
      "MNetworkVarTypeOverride",
      "MParticleDomainTag",
  };
  for (const char* k : kVarNameKeys)
    if (name == k) return MetaValueKind::kVarName;
  // SEND_PROXY_RECIPIENTS_FILTER — CSchemaSendProxyRecipientsFilter{ const char* m_pszName; ... }.
  if (name == "MNetworkSendProxyRecipientsFilter")
    return MetaValueKind::kSendProxyFilter;
  // KV3 class defaults — the generated accessor thunk. Recovered by a live call, not a resident read.
  if (name == "MGetKV3ClassDefaults")
    return MetaValueKind::kKv3Defaults;
  return MetaValueKind::kUnknown;
}

// Decode one metadata entry's m_pData into its raw string form per the keyed
// classification. Returns "" (no value) on kUnknown, a null/garbage blob, or any
// guarded read failure — NEVER a fault, NEVER a guess. MODERN-only
// call sites guarantee a live compiled record, but the blob pointer is still an
// engine pointer so every dereference is SEH-bounded.
std::string DecodeMetaValue(const std::string& name, const void* p_data) {
  if (p_data == nullptr) return {};
  const MetaValueKind kind = ClassifyMetaValue(name);
  switch (kind) {
    case MetaValueKind::kString: {
      // The string is a const char* located AT the blob: read the pointer, then
      // the C string it addresses (both guarded).
      std::uint64_t str_ptr = 0;
      if (!tshash_compat::SafeReadPtr2023(p_data, &str_ptr)) return {};
      if (str_ptr == 0) return {};
      char buf[kMaxNameLen];
      if (!tshash_compat::SafeReadCString2023(
              reinterpret_cast<const char*>(static_cast<std::uintptr_t>(str_ptr)),
              buf, sizeof(buf)))
        return {};
      return std::string(buf);
    }
    case MetaValueKind::kInt: {
      std::int32_t v = 0;
      if (!tshash_compat::SafeReadBytes2023(p_data, &v, sizeof(v))) return {};
      char buf[32];
      std::snprintf(buf, sizeof(buf), "%d", v);
      return std::string(buf);
    }
    case MetaValueKind::kFloat: {
      float v = 0.0f;
      if (!tshash_compat::SafeReadBytes2023(p_data, &v, sizeof(v))) return {};
      char buf[32];
      // Match the convar value-rendering convention (%f) for cross-artifact
      // consistency + determinism.
      std::snprintf(buf, sizeof(buf), "%f", static_cast<double>(v));
      return std::string(buf);
    }
    case MetaValueKind::kInlineString: {
      // Up to 8 chars stored INLINE at the blob (a resource/type fourcc token), NUL-terminated
      // within the 8 bytes. Read 8 bytes guarded, then take the prefix up to the first NUL.
      char raw[8] = {0};
      if (!tshash_compat::SafeReadBytes2023(p_data, raw, sizeof(raw))) return {};
      std::size_t n = 0;
      while (n < sizeof(raw) && raw[n] != '\0') ++n;
      return std::string(raw, n);
    }
    case MetaValueKind::kVarName: {
      // CSchemaVarName{ const char* m_pszType; const char* m_pszName; } at the blob: two consecutive
      // pointers. Render "type name" (either half may be absent). Guard the -1 sentinel Valve has
      // used in place of nullptr (source2gen note).
      const auto read_cstr = [](std::uint64_t ptr, char* buf, std::size_t cap) -> bool {
        if (ptr == 0 || ptr == static_cast<std::uint64_t>(-1)) return false;
        return tshash_compat::SafeReadCString2023(
            reinterpret_cast<const char*>(static_cast<std::uintptr_t>(ptr)), buf, cap);
      };
      std::uint64_t type_ptr = 0;
      std::uint64_t name_ptr = 0;
      char type_buf[kMaxNameLen];
      char name_buf[kMaxNameLen];
      bool has_type = tshash_compat::SafeReadPtr2023(p_data, &type_ptr) && read_cstr(type_ptr, type_buf, sizeof(type_buf));
      bool has_name = tshash_compat::SafeReadPtr2023(
                          reinterpret_cast<const void*>(
                              reinterpret_cast<std::uintptr_t>(p_data) + sizeof(void*)),
                          &name_ptr) &&
                      read_cstr(name_ptr, name_buf, sizeof(name_buf));
      if (!has_type && !has_name) return {};
      std::string out;
      if (has_type) out += type_buf;
      if (has_name) {
        if (has_type) out += ' ';
        out += name_buf;
      }
      return out;
    }
    case MetaValueKind::kSendProxyFilter: {
      // CSchemaSendProxyRecipientsFilter{ const char* m_pszName; ... } — the name is the first
      // member, so this reads exactly like kString (a const char* at the blob).
      std::uint64_t str_ptr = 0;
      if (!tshash_compat::SafeReadPtr2023(p_data, &str_ptr)) return {};
      if (str_ptr == 0) return {};
      char buf[kMaxNameLen];
      if (!tshash_compat::SafeReadCString2023(
              reinterpret_cast<const char*>(static_cast<std::uintptr_t>(str_ptr)),
              buf, sizeof(buf)))
        return {};
      return std::string(buf);
    }
    case MetaValueKind::kKv3Defaults:
      // The KV3 class-defaults accessor is NOT a resident read — it is a generated thunk that must be
      // CALLED against a live KeyValues3 and serialized (tier0 SaveKV3AsJSON). That live-call path is
      // handled at the class-metadata emit site (DecodeKv3Defaults), not here; the raw m_pData is an
      // ASLR'd function pointer with no readable text, so the plain decode yields "".
      return {};
    case MetaValueKind::kUnknown:
    default:
      return {};
  }
}

// ---- MGetKV3ClassDefaults live recovery -----------------------------------
//
// MGetKV3ClassDefaults' m_pData is a generated ACCESSOR THUNK: calling it default-constructs an
// instance of the class and serializes its field defaults into a live KeyValues3. We recover the
// value exactly as ValveResourceFormat/DumpSource2 does headless
// (src/main/dumpers/schemas/metadata_stringifier.cpp): call the thunk, then tier0's SaveKV3AsJSON.
// The whole call is crash-guarded (SEH on Windows, sigaction/siglongjmp on POSIX) so a class whose
// accessor faults degrades to an empty value instead of aborting the walk; a hand-curated denylist
// skips the handful known to fault; and a determinism filter blanks the non-deterministic auto-id
// fields so the artifact stays byte-identical across runs (the walker's determinism gate).
//
// VALIDATION (must be confirmed on a real per-era build): (1) tier0 actually exports SaveKV3AsJSON
// under the mangled name below; (2) the accessor ABI (call -> void*, then *(void**) -> KeyValues3*)
// holds for the pinned era; (3) the determinism filter list is COMPLETE for CS2's class defaults —
// any not-yet-listed varying field trips the byte-identical gate and must be added. Until the gate
// passes on a real build, treat the recovered value as provisional.

// ABI shim: CUtlString's first member is a char* to the (heap) string; a no-destructor shim reads it
// without pulling in CUtlString's allocator/destructor ABI (matches DumpSource2's SimpleCUtlString).
// The tier0-allocated result string is intentionally NOT freed (leaked) — the walker is a one-shot
// process, and freeing via a mismatched destructor would be the real hazard.
struct WalkerCUtlString {
  const char* m_pString = nullptr;
};

using GetKv3DefaultsFn = void* (*)();
using SaveKv3AsJsonFn = int (*)(void* kv3, WalkerCUtlString& err, WalkerCUtlString& out);

// Resolved once per process from the loaded tier0 (set by MaybeResolveSaveKv3Json at walk start).
SaveKv3AsJsonFn g_save_kv3_as_json = nullptr;

// Classes whose GetKV3Defaults accessor faults OR hangs — skipped up front so they cost neither a
// crash nor a watchdog deadline (each abandoned hanger otherwise leaks a spinning thread for the rest
// of the process). Seeded from DumpSource2 g_classWithBrokenDefaults (faulters) plus the hangers this
// walker observed via the watchdog on CS2 (the CBodyComponent/scene-node/skeleton family + a few
// tool-only info structs). The watchdog remains the backstop for any NEW hanger a future build adds.
bool IsBrokenKv3DefaultsClass(const char* name) {
  static const std::unordered_set<std::string> kBroken = {
      // -- DumpSource2 faulters --
      "CastSphereSATParams_t", "Dop26_t", "FourCovMatrices3", "VMixVocoderDesc_t",
      "CCitadelPlayerPawn_GraphController2", "RTProxyBLAS_t", "vphysics_save_ragdoll_control_t",
      "CAnimAttachment", "CBlockSelectionMetricEvaluator", "HitReactFixedSettings_t",
      "RnSoftbodySpring_t", "CAnimGraphDoc_GroupNode",
      // -- hangers observed by the watchdog (accessor never returns) --
      "CBodyComponentPoint", "CBodyComponentSkeletonInstance", "CGameSceneNode",
      "CSkeletonInstance", "GameAmmoTypeInfo_t"};
  return name != nullptr && kBroken.count(name) != 0;
}

// Zero a large stack region so the accessor's default-constructed instance reads zeros for any
// members its constructor leaves uninitialized (matches DumpSource2's CleanStack — determinism +
// avoids a fault from a garbage member). Must NOT be inlined so the zeroing isn't optimized away.
#if defined(_WIN32)
__declspec(noinline)
#else
__attribute__((noinline))
#endif
void
CleanKv3Stack() {
  volatile char stack[0x10000];
  for (size_t i = 0; i < sizeof(stack); ++i) stack[i] = 0;
}

// POD-only guarded leaf: call the accessor + SaveKV3AsJSON. Writes the (borrowed, leaked) result
// pointer + ok flag into the POD ctx. No std::string / RAII here (a siglongjmp may abandon this
// frame — jumping past destructors is UB).
struct Kv3CallCtx {
  const void* accessor_data;  // in: m_pData (points AT the thunk fn ptr)
  const char* result;         // out: SaveKV3AsJSON's CUtlString.m_pString (borrowed)
  bool ok;                    // out
};
void Kv3CallLeaf(void* p) {
  auto* c = static_cast<Kv3CallCtx*>(p);
  c->ok = false;
  c->result = nullptr;
  void* thunk = *reinterpret_cast<void* const*>(c->accessor_data);  // the GetKV3Defaults fn ptr
  if (thunk == nullptr) return;
  CleanKv3Stack();
  void* kv3wrap = reinterpret_cast<GetKv3DefaultsFn>(thunk)();
  if (kv3wrap == nullptr) return;
  WalkerCUtlString err;
  WalkerCUtlString out;
  int res = g_save_kv3_as_json(*reinterpret_cast<void**>(kv3wrap), err, out);
  if (res != 0) {
    c->result = out.m_pString;
    c->ok = true;
  }
}

#if defined(_WIN32)
bool CallKv3DefaultsGuarded(Kv3CallCtx* ctx) {
  __try {
    Kv3CallLeaf(ctx);
    return ctx->ok;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#else
bool CallKv3DefaultsGuarded(Kv3CallCtx* ctx) {
  if (!posix_crash_guard::RunGuarded(&Kv3CallLeaf, ctx)) return false;
  return ctx->ok;
}
#endif

// Blank the values of the non-deterministic auto-id fields SaveKV3AsJSON emits (m_id / seeds / pin
// ids / ...), so the same build serializes byte-identically across runs. Mirrors DumpSource2's
// g_regexFilters. Applied to the JSON text AFTER the guarded call (std::regex is not signal-safe, so
// it must run outside the guard). NOTE: this makes the text no longer strictly valid JSON — it is a
// diff-stable blob carried verbatim in SchemaMetadata.value (value_parsed is left unset).
std::string FilterKv3Nondeterminism(const std::string& json) {
  static const std::regex kFilters[] = {
      std::regex(R"#(("m_id":) .*)#"),
      // m_ID / m_nRandomSeed / m_seed / m_outputPinID used to require a literal trailing comma
      // (".*,") to match, unlike every other entry here. That silently let the raw value through
      // whenever the field was the LAST property in its KV3 object (no comma before the closing
      // brace) — verify-corpus.py's corpus-wide audit caught this live at HEAD (e.g. m_ID as the
      // sole/last field of CNmSyncTrack::EventMarker_t). Switched to the same no-comma greedy
      // `.*` form used everywhere else: it matches to end-of-line regardless of a trailing comma,
      // and the replacement below re-appends exactly one comma either way, so both the mid-struct
      // (comma) and last-property (no comma) cases come out identical and diff-stable.
      std::regex(R"#(("m_ID":) .*)#"),
      std::regex(R"#(("m_nControlPointCount":) .*)#"),
      std::regex(R"#(("m_nControlPointStart":) .*)#"),
      std::regex(R"#(("m_nRandomSeed":) .*)#"),
      std::regex(R"#(("m_seed":) .*)#"),
      std::regex(R"#(("m_outputPinID":) .*)#"),
      std::regex(R"#(("m_stateID":) .*)#"),
      std::regex(R"#(("m_pinID":) .*)#"),
      std::regex(R"#(("m_entryStateID":) .*)#"),
      // Physics/collision defaults whose MGetKV3ClassDefaults accessor reads
      // uninitialized instance memory in a schema-only walk (no live entity exists),
      // so the value varies per process run (e.g. m_flMassInv seen as 0.0, 1.6e10,
      // -1.66e24 across runs; m_nCollisionGroupNumber as unrelated 32-bit garbage).
      // Both appear as the sole KV3 default of their class (no trailing comma), so the
      // no-comma `.*` form is used, matching m_nControlPointCount above. Present only in
      // the newest KV3-bearing eras (cs2-2026-01-22 onward).
      std::regex(R"#(("m_nCollisionGroupNumber":) .*)#"),
      std::regex(R"#(("m_flMassInv":) .*)#"),
      // Two more non-real KV3 defaults surfaced by a full-corpus field-level audit
      // (baseline walker vs a rebuilt walker == a 2-point cross-build determinism check):
      //   * valueB   — a handle/value pair type's payload; reads uninitialized instance
      //                memory, so it varies RUN-TO-RUN (238 vs 40 across two walks).
      //   * pitchfrac — an audio-params struct field derived from a load address; STABLE
      //                run-to-run within one boot but changes when the walker is recompiled
      //                (an address-derived leak, not a real default). Both are garbage in a
      //                schema-only walk; blank them so the value is diff-stable + rebuild-
      //                stable. pitchfrac sits mid-struct (trailing comma) and valueB is the
      //                last field (no comma); the greedy `.*` form handles both.
      std::regex(R"#(("valueB":) .*)#"),
      std::regex(R"#(("pitchfrac":) .*)#"),
  };
  std::string out = json;
  for (const auto& re : kFilters) out = std::regex_replace(out, re, "$1 <HIDDEN FOR DIFF>,");
  return out;
}

// Per-accessor deadline. A legitimate GetKV3Defaults returns in well under 100ms; a pathological one
// INFINITE-LOOPS. This value sits far above the former and far below the latter, so a class is STABLY
// either recovered or abandoned across runs (no borderline flip) — the determinism gate holds.
constexpr int kKv3AccessorTimeoutSec = 3;

#if !defined(_WIN32)
// ============================ FORK-ISOLATED KV3 RECOVERY (POSIX) ============================
// Some builds ship a class whose LINUX .so GetKV3Defaults accessor recurses without bound and
// CORRUPTS memory (observed: cs2-2026-07-09 build 24116939, CBodyComponentBaseAnimGraph — 4 of the
// era's 6 builds). It is an UNCATCHABLE crash: the corruption spreads and the main thread later dies
// in unrelated code with a smashed stack, so neither the fault guard nor an altstack traps it. It is
// BUILD-SPECIFIC (the same class recovers a correct 2158-byte value in build 24304127), so a static
// denylist would wrongly drop good data. To both AVOID the crash and KEEP every recoverable value,
// the accessor calls run in a FORKED CHILD: a crash/hang only kills the child, and the parent
// re-forks past the offending accessor. fork() hands the child a copy-on-write image of the parent
// (every CS2 .so mapped, accessor pointers valid), so it can call the accessors while its corruption
// never reaches the parent. WINDOWS keeps the in-process worker+watchdog path (it does not crash).
//
// Determinism holds: each child is forked from the SAME parent image, so its execution — and thus
// the crash/hang point — is identical across runs; the recovered JSON VALUES (keyed logically by
// class, not by the ASLR'd pointer) are therefore byte-stable.
//
// Protocol (same-machine, temp files):
//   * results file    — append-only [uintptr_t key][u32 len][json] records; the child->parent
//     transport, and it survives a child crash (finished records are already flushed).
//   * checkpoint file — the accessor the child is CURRENTLY attempting (rewritten+fsync'd before
//     each call). On a child crash/hang the parent reads it to learn which accessor to skip.
//   * g_kv3_results   — the parent's accumulated recoveries, inherited by each re-forked child via
//     COW so it skips already-done accessors; g_kv3_fork_skip holds the crashers/hangers.
std::unordered_map<uintptr_t, std::string> g_kv3_results;
std::unordered_set<uintptr_t> g_kv3_fork_skip;
bool g_kv3_isolated_ran = false;

struct Kv3Req {
  const void* accessor_data;
  std::string class_name;
};

// POD scan leaf (RunGuarded-able): collect a class's MGetKV3ClassDefaults accessor pointers. Modern
// layout only (the sole layout that carries this metadata) — a direct member read, so no probe-guard
// nesting. A garbage class binding faults here and RunGuarded turns it into "count stays 0" -> skip.
struct Kv3ScanCtx {
  const CSchemaClassInfo* ci;
  const void* out[8];
  int count;
};
void Kv3ScanLeaf(void* p) {
  auto* c = static_cast<Kv3ScanCtx*>(p);
  c->count = 0;
  const SchemaMetadataEntryData_t* md = rec::ClassStaticMetadata(c->ci);
  const int n = rec::ClassStaticMetadataCount(c->ci);
  if (md == nullptr || n <= 0) return;
  for (int i = 0; i < n && c->count < 8; ++i) {
    const char* name = md[i].m_pszName;
    if (name != nullptr && std::strcmp(name, "MGetKV3ClassDefaults") == 0) {
      c->out[c->count++] = md[i].m_pData;
    }
  }
}

void WriteKv3Checkpoint(int fd, uintptr_t key) {
  if (fd < 0) return;
  ::lseek(fd, 0, SEEK_SET);
  ssize_t w = ::write(fd, &key, sizeof(key));
  (void)w;
  ::fsync(fd);
}
uintptr_t ReadKv3Checkpoint(const char* path) {
  int fd = ::open(path, O_RDONLY);
  if (fd < 0) return 0;
  uintptr_t key = 0;
  ssize_t r = ::read(fd, &key, sizeof(key));
  ::close(fd);
  return (r == static_cast<ssize_t>(sizeof(key))) ? key : 0;
}
void LoadKv3Results(const char* path, std::unordered_map<uintptr_t, std::string>* out) {
  FILE* f = std::fopen(path, "rb");
  if (f == nullptr) return;
  for (;;) {
    uintptr_t key = 0;
    std::uint32_t len = 0;
    if (std::fread(&key, sizeof(key), 1, f) != 1) break;
    if (std::fread(&len, sizeof(len), 1, f) != 1) break;
    std::string v;
    if (len > 0) {
      v.resize(len);
      if (std::fread(&v[0], 1, len, f) != len) break;
    }
    (*out)[key] = std::move(v);
  }
  std::fclose(f);
}

// CHILD: recover every request not already done (COW-inherited g_kv3_results) or skipped, appending
// results and checkpointing before each call. Never returns.
[[noreturn]] void RunKv3RecoveryChild(const std::vector<Kv3Req>& reqs,
                                      const char* results_path, const char* checkpoint_path) {
  FILE* res = std::fopen(results_path, "ab");
  int cp = ::open(checkpoint_path, O_WRONLY | O_TRUNC);
  for (const Kv3Req& r : reqs) {
    uintptr_t key = reinterpret_cast<uintptr_t>(r.accessor_data);
    if (key == 0) continue;
    if (g_kv3_results.count(key) != 0 || g_kv3_fork_skip.count(key) != 0) continue;
    WriteKv3Checkpoint(cp, key);
    Kv3CallCtx ctx;
    ctx.accessor_data = r.accessor_data;
    ctx.result = nullptr;
    ctx.ok = false;
    std::string json;
    if (CallKv3DefaultsGuarded(&ctx) && ctx.result != nullptr) {
      json = FilterKv3Nondeterminism(std::string(ctx.result));
    }
    if (res != nullptr) {
      std::uint32_t len = static_cast<std::uint32_t>(json.size());
      std::fwrite(&key, sizeof(key), 1, res);
      std::fwrite(&len, sizeof(len), 1, res);
      if (len > 0) std::fwrite(json.data(), 1, len, res);
      std::fflush(res);
    }
  }
  if (res != nullptr) std::fclose(res);
  if (cp >= 0) ::close(cp);
  _exit(0);  // skip C++ destructors — the child shares the parent's COW state
}

// PARENT: wait for `pid`, detecting a HANG via checkpoint stall (the accessor timeout). Returns true
// iff the child exited cleanly (status 0); false on crash (signal / nonzero) or kill-for-hang.
bool WaitKv3ChildClean(pid_t pid, const char* checkpoint_path) {
  using clock = std::chrono::steady_clock;
  uintptr_t last_cp = 0;
  auto last_change = clock::now();
  for (;;) {
    int status = 0;
    pid_t r = ::waitpid(pid, &status, WNOHANG);
    if (r == pid) return WIFEXITED(status) && WEXITSTATUS(status) == 0;
    if (r < 0) return false;
    uintptr_t cp = ReadKv3Checkpoint(checkpoint_path);
    auto now = clock::now();
    if (cp != last_cp) {
      last_cp = cp;
      last_change = now;
    } else if (cp != 0 &&
               std::chrono::duration_cast<std::chrono::seconds>(now - last_change).count() >=
                   kKv3AccessorTimeoutSec) {
      ::kill(pid, SIGKILL);
      int st = 0;
      ::waitpid(pid, &st, 0);
      return false;  // hung on `cp`
    }
    ::usleep(20 * 1000);  // 20ms
  }
}

// Orchestrate: fork a child to recover as many requests as it can; on a crash/hang, record the
// in-flight accessor as skipped and re-fork from where it left off. Fills g_kv3_results +
// g_kv3_fork_skip. Bounded re-forks (one per genuinely-broken accessor + slack).
void RecoverKv3DefaultsIsolated(const std::vector<Kv3Req>& reqs, bool trace) {
  g_kv3_isolated_ran = true;
  if (reqs.empty() || g_save_kv3_as_json == nullptr) return;

  char results_path[] = "/tmp/cs2walk-kv3res-XXXXXX";
  char checkpoint_path[] = "/tmp/cs2walk-kv3cp-XXXXXX";
  int rfd = ::mkstemp(results_path);
  int cfd = ::mkstemp(checkpoint_path);
  if (rfd < 0 || cfd < 0) {
    if (rfd >= 0) ::close(rfd);
    if (cfd >= 0) ::close(cfd);
    return;
  }
  ::close(rfd);
  ::close(cfd);

  const int kMaxReforks = static_cast<int>(reqs.size()) + 8;
  for (int forks = 0; forks <= kMaxReforks; ++forks) {
    ::fflush(nullptr);  // flush stdio so the fork doesn't duplicate buffered output
    pid_t pid = ::fork();
    if (pid == 0) {
      RunKv3RecoveryChild(reqs, results_path, checkpoint_path);  // never returns
    }
    if (pid < 0) break;  // fork failed -> keep partial results
    bool clean = WaitKv3ChildClean(pid, checkpoint_path);
    LoadKv3Results(results_path, &g_kv3_results);  // fold in whatever completed
    if (clean) break;
    uintptr_t bad = ReadKv3Checkpoint(checkpoint_path);
    if (bad == 0) break;  // can't identify the culprit -> stop, keep what we have
    if (g_kv3_fork_skip.insert(bad).second && trace) {
      std::fprintf(stderr, "[kv3] ISOLATED skip (crash/hang) accessor=%p\n",
                   reinterpret_cast<void*>(bad));
      std::fflush(stderr);
    }
  }
  ::unlink(results_path);
  ::unlink(checkpoint_path);
  if (trace) {
    std::fprintf(stderr, "[kv3] ISOLATED done: %zu recovered, %zu skipped (of %zu requests)\n",
                 g_kv3_results.size(), g_kv3_fork_skip.size(), reqs.size());
    std::fflush(stderr);
  }
}
#endif  // !defined(_WIN32)

// Recover MGetKV3ClassDefaults for one class: guarded accessor call + serialize + determinism filter,
// UNDER A WATCHDOG. The crash guard traps faults but NOT infinite loops, so the guarded call runs on a
// worker thread with a deadline; if it overruns, the thread is ABANDONED (it keeps spinning until the
// one-shot process exits) and this class emits an empty value. Returns "" when the export isn't
// resolved, the class is denylisted, the blob is null, the call faults, OR the call overran the
// deadline — never a fault, never a hang, never a guess.
std::string DecodeKv3Defaults(const void* accessor_data, const char* class_name) {
  if (g_save_kv3_as_json == nullptr || accessor_data == nullptr) return {};
  if (IsBrokenKv3DefaultsClass(class_name)) return {};
#if !defined(_WIN32)
  // POSIX: the fork-isolated pre-pass (RecoverKv3DefaultsIsolated, run before the emit loop)
  // already recovered every accessor it could WITHOUT risking the walk — a crash/hang in one
  // accessor was contained to a child and skipped. Just look up its result (absent == skipped or
  // failed == empty). We never call the accessor in-process on linux, so a corrupting accessor can
  // never crash the emit. If the pre-pass somehow didn't run, fall through to the in-process path.
  if (g_kv3_isolated_ran) {
    auto it = g_kv3_results.find(reinterpret_cast<uintptr_t>(accessor_data));
    return it != g_kv3_results.end() ? it->second : std::string();
  }
#endif
  static const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;
  if (trace) {
    std::fprintf(stderr, "[kv3] GetKV3Defaults %s\n", class_name ? class_name : "?");
    std::fflush(stderr);
  }

  // Heap-owned shared state so a DETACHED (hung) worker can still safely write after we've moved on.
  struct Kv3Work {
    const void* accessor_data = nullptr;
    std::mutex m;
    std::condition_variable cv;
    bool done = false;
    std::string result;
  };
  auto w = std::make_shared<Kv3Work>();
  w->accessor_data = accessor_data;

  std::thread worker([w]() {
    Kv3CallCtx ctx;
    ctx.accessor_data = w->accessor_data;
    ctx.result = nullptr;
    ctx.ok = false;
    std::string r;
    if (CallKv3DefaultsGuarded(&ctx) && ctx.result != nullptr) {
      r = FilterKv3Nondeterminism(std::string(ctx.result));
    }
    {
      std::lock_guard<std::mutex> lk(w->m);
      w->result = std::move(r);
      w->done = true;
    }
    w->cv.notify_one();
  });

  bool done = false;
  {
    std::unique_lock<std::mutex> lk(w->m);
    done = w->cv.wait_for(lk, std::chrono::seconds(kKv3AccessorTimeoutSec), [&] { return w->done; });
  }
  if (done) {
    worker.join();
    return std::move(w->result);
  }
  // Overran the deadline — a hanging accessor. Abandon the worker (leaked, spinning until process
  // exit) and emit empty. If a specific class trips this every run, add it to IsBrokenKv3DefaultsClass.
  if (trace) {
    std::fprintf(stderr, "[kv3] ABANDONED (deadline) %s\n", class_name ? class_name : "?");
    std::fflush(stderr);
  }
  worker.detach();
  return {};
}

// Resolve tier0's SaveKV3AsJSON once (idempotent). Called at walk start with the live environment.
// Absence is non-fatal: MGetKV3ClassDefaults values then stay empty (the prior behavior).
//
// ON BY DEFAULT now that DecodeKv3Defaults runs each accessor under a WATCHDOG (worker thread +
// deadline): a hanging thunk is abandoned after kKv3AccessorTimeoutSec and that class emits "", so the
// walk always completes and stays deterministic (validated byte-identical). Set CS2_WALKER_NO_KV3_DEFAULTS
// to opt OUT (e.g. to reproduce the pre-feature output) — then g_save_kv3_as_json stays null and every
// MGetKV3ClassDefaults value is empty, exactly as before.
void MaybeResolveSaveKv3Json(const InProcessEnvironment& env) {
  if (g_save_kv3_as_json != nullptr) return;
  if (std::getenv("CS2_WALKER_NO_KV3_DEFAULTS") != nullptr) return;  // opt-out; see note above.
#if defined(_WIN32)
  const char* kSym = "?SaveKV3AsJSON@@YA_NPEBVKeyValues3@@PEAVCUtlString@@1@Z";
#else
  const char* kSym = "_Z13SaveKV3AsJSONPK10KeyValues3P10CUtlStringS3_";
#endif
  for (const auto& m : env.modules()) {
    if (m.module_name().find("tier0") == std::string::npos) continue;
    if (void* sym = m.ResolveSymbol(kSym)) {
      g_save_kv3_as_json = reinterpret_cast<SaveKv3AsJsonFn>(sym);
      return;
    }
  }
}

void EmitMetadata(const SchemaMetadataEntryData_t* md, int count,
                  google::protobuf::RepeatedPtrField<wpb::SchemaMetadata>* out,
                  const char* class_name_for_kv3 = nullptr) {
  if (md == nullptr || count <= 0) return;
  // Build then sort by (name, original index) for determinism.
  struct Entry {
    std::string name;
    std::string value;
    int idx;
  };
  std::vector<Entry> entries;
  entries.reserve(static_cast<size_t>(count));
  for (int i = 0; i < count; ++i) {
    Entry e;
    e.name = Str(md[i].m_pszName);
    e.idx = i;
    // Decode the typed m_pData value for the KNOWN scalar/string metadata kinds
    // (keyed by name). An unknown name / null blob / guarded-read failure yields
    // "" (the entry still emits NAME-only, exactly as before, so the host knows it
    // exists). value_parsed remains the host's job.
    //
    // MGetKV3ClassDefaults is the one entry whose value is a LIVE CALL, not a resident read: only
    // class metadata (class_name_for_kv3 != null) carries it, and only when tier0's SaveKV3AsJSON
    // resolved. Field/enum metadata pass class_name_for_kv3 == null, so their (nonexistent) defaults
    // stay empty. The guarded call degrades to "" on any fault.
    if (class_name_for_kv3 != nullptr && e.name == "MGetKV3ClassDefaults") {
      e.value = DecodeKv3Defaults(md[i].m_pData, class_name_for_kv3);
    } else {
      e.value = DecodeMetaValue(e.name, md[i].m_pData);
    }
    entries.push_back(std::move(e));
  }
  std::sort(entries.begin(), entries.end(), [](const Entry& a, const Entry& b) {
    if (a.name != b.name) return a.name < b.name;  // Ordinal
    return a.idx < b.idx;
  });
  for (auto& e : entries) {
    auto* m = out->Add();
    m->set_name(e.name);
    if (!e.value.empty()) m->set_value(e.value);
  }
}

// ---- Class ----------------------------------------------------------------
//
// All raw record reads are ERA-GATED via rec2023:: accessors. On kModern each
// accessor returns the compiled struct member (byte-identical); on k2023 it
// reads at the derived 2023 offset (SEH-guarded). The emit/sort logic below
// is unchanged across eras.
bool EmitClass(const CSchemaClassInfo* ci, wpb::SchemaClass* out, std::string* err,
               Era era, const DeclaredModuleIndex* decl_index) {
  if (ci == nullptr) {
    *err = "schema walk: null class binding encountered";
    return false;
  }
  out->set_name(Str(rec::ClassName(ci, era), era));
  out->set_module(ScopeName(rec::ClassTypeScope(ci, era), era));
  out->set_size(static_cast<uint64_t>(rec::ClassSize(ci, era)));

  // Enrichment — struct alignment + raw class-info flags (SchemaClass.alignment
  // / SchemaClass.flags). Both are compiled members of SchemaClassInfoData_t in the
  // pinned hl2sdk (schematypes.h): m_nAlignment (uint8 — a byte boundary 1/4/8/16)
  // and the two flags words m_nFlags1 / m_nFlags2 (uint32 each). The proto carries a
  // primary uint32 flags slot, so we emit m_nFlags1 — the primary class-info flags
  // word — verbatim (opaque bits, NOT interpreted; the second word m_nFlags2 is now
  // surfaced separately via SchemaClass.flags2, see the enrichment block below). proto3
  // omits a zero uint32 on the wire, so a class with alignment 0 / flags 0 adds no
  // bytes — PURELY ADDITIVE.
  //
  // MODERN ONLY: these are read as compiled members. On k2023 the SchemaClassInfoData_t
  // alignment/flags offsets are NOT independently derived (schema_record_layout_2023.h
  // derives only name/size/field-count/fields/bases/typescope; the small-int sub-block
  // is only partially mapped — m_nBaseClassCount@+35 — and alignment/flags are not in
  // it). Reading an underived 2023 offset would emit garbage, so we leave both 0 on
  // 2023 (deferred-with-reason). The existing 2023 class bytes are unchanged.
  if (era == Era::kModern) {
    // Each member is read through a COMPILE-TIME member-presence accessor
    // (rec::Class*; see schema_record_layout_2023.h). Where the pin's
    // SchemaClassInfoData_t declares the member, the accessor returns the exact
    // compiled member — byte-identical to the old direct `ci->m_...` read.
    // Where the member is ABSENT (older era pins lack e.g. m_pszCPPName), it
    // returns the field's default and the proto field stays unset — the truthful
    // "this Source2 era's schema record has no such field" answer (same
    // deferred-with-reason posture as the k2023 underived-offset gap below).
    out->set_alignment(static_cast<uint32_t>(rec::ClassAlignment(ci)));
    out->set_flags(static_cast<uint32_t>(rec::ClassFlags1(ci)));

    // Remaining SchemaClassInfoData_t scalar attributes (modern only).
    // The second flags word m_nFlags2 (uint32, often 0), the two inheritance-depth
    // counters m_nSingleInheritanceDepth / m_nMultipleInheritanceDepth (uint16
    // each), and the two identity strings m_pszProjectName / m_pszCPPName (const
    // char*, the source project + the C++ type spelling). proto3 omits zero
    // uint32/uint16 and empty strings on the wire, so a class with flags2==0 /
    // depth==0 / null name pointers adds no bytes — PURELY ADDITIVE. The string
    // reads use the MODERN Str() overload (compiled member off a live record;
    // byte-identical). On k2023 these offsets are NOT independently
    // derived (same gap as alignment/flags), so they stay unset on 2023
    // (deferred-with-reason; existing 2023 class bytes unchanged). NOTE
    // m_pszCPPName is absent in all nine older hl2sdk pins, so rec::ClassCppName
    // returns null there and cpp_name stays unset for those eras.
    out->set_flags2(static_cast<uint32_t>(rec::ClassFlags2(ci)));
    out->set_single_inheritance_depth(
        static_cast<uint32_t>(rec::ClassSingleInheritanceDepth(ci)));
    out->set_multiple_inheritance_depth(
        static_cast<uint32_t>(rec::ClassMultipleInheritanceDepth(ci)));
    out->set_project_name(Str(rec::ClassProjectName(ci)));
    out->set_cpp_name(Str(rec::ClassCppName(ci)));
  }

  // Parents (single + multiple inheritance). Sort by (offset, name).
  const SchemaBaseClassInfoData_t* bases = rec::ClassBaseClasses(ci, era);
  const int base_count = rec::ClassBaseCount(ci, era);
  if (bases != nullptr && base_count > 0) {
    struct P {
      std::string name;
      std::string module;
      uint32_t off;
    };
    std::vector<P> parents;
    for (int i = 0; i < base_count; ++i) {
      const SchemaBaseClassInfoData_t* bc = rec::BaseAt(bases, i, era);
      const CSchemaClassInfo* pclass = rec::BaseClassPtr(bc, era);
      P p;
      p.off = rec::BaseOffset(bc, era);
      p.name = pclass ? Str(rec::ClassName(pclass, era), era) : std::string();
      p.module = pclass ? ScopeName(rec::ClassTypeScope(pclass, era), era) : std::string();
      // 2023-ONLY DEFENSIVE EMPTY-PARENT SKIP (kModern path unchanged).
      // A base-class entry whose resolved m_pClass->m_pszName is empty is either an
      // over-read past the real m_nBaseClassCount (a slightly-off derived count) or an
      // unresolved forward-decl binding. Emitting it would yield a SchemaClassParent
      // with an empty name, which the host's EntitySchemaEmitter rejects
      // ("parent with empty name"). Drop it rather than emit garbage; the modern
      // path never hits this branch, so byte-identical modern output is preserved.
      if (era != Era::kModern && p.name.empty()) continue;
      parents.push_back(std::move(p));
    }
    std::sort(parents.begin(), parents.end(), [](const P& a, const P& b) {
      if (a.off != b.off) return a.off < b.off;
      return a.name < b.name;
    });
    for (auto& p : parents) {
      auto* sp = out->add_parents();
      sp->set_name(p.name);
      sp->set_module(p.module);
      // SchemaClassParent.offset: the base-class subobject byte offset within the
      // derived class (SchemaBaseClassInfoData_t.m_nOffset, read via rec::BaseOffset
      // into p.off above — previously read only to SORT parents, then discarded).
      // Now emitted verbatim. Era-stable: BaseOffset is read the same way on both
      // eras (used for the sort on both), so this is ADDITIVE on every era. proto3
      // omits a zero uint32, so single-inheritance bases (offset 0) add no bytes —
      // PURELY ADDITIVE. Nonzero only on multiple-inheritance / non-leading bases.
      sp->set_offset(p.off);
    }
  }

  // Fields. Sort by (offset, name) for determinism.
  const SchemaClassFieldData_t* field_arr = rec::ClassFields(ci, era);
  const int field_count = rec::ClassFieldCount(ci, era);
  if (field_arr != nullptr && field_count > 0) {
    struct F {
      const SchemaClassFieldData_t* fd;
      std::string name;
      int off;
    };
    std::vector<F> fields;
    for (int i = 0; i < field_count; ++i) {
      const SchemaClassFieldData_t* fd = rec::FieldAt(field_arr, i, era);
      fields.push_back({fd, Str(rec::FieldName(fd, era), era), rec::FieldOffset(fd, era)});
    }
    std::sort(fields.begin(), fields.end(), [](const F& a, const F& b) {
      if (a.off != b.off) return a.off < b.off;
      return a.name < b.name;
    });
    for (auto& f : fields) {
      auto* sf = out->add_fields();
      sf->set_name(f.name);
      sf->set_offset(f.off);
      TranslateType(rec::FieldType(f.fd, era), sf->mutable_type(), era);
      // type_module hint for declared_class/declared_enum refs. The emitter
      // requires it whenever the field's type tree references a declared
      // class/enum ANYWHERE (including nested inside a container/pointer/array
      // element type), so we resolve it by recursing the BUILT type proto for
      // the first declared node's module — the depth-0 case subsumes the old
      // direct-only assignment. Stays "" when no declared ref exists anywhere.
      std::string type_module = FirstDeclaredModule(sf->type());
      // Fallback (all eras since 0.5.0): the direct pointer-chase can leave a
      // by-value declared ref with no module (stub m_pClassInfo — on 2023 a
      // layout artifact, on modern a global-scope-surfaced forward stub).
      // Resolve by NAME against the enumerated index instead.
      if (type_module.empty() && decl_index != nullptr) {
        const std::string* dn = FirstByValueDeclaredName(sf->type());
        if (dn != nullptr && !dn->empty()) {
          type_module = ResolveModuleByName(*decl_index, *dn, out->module());
          // Last resort: a by-value declared ref to a type with NO registered body
          // (a forward-decl stub — e.g. CHandle< C_PropVRHand >, where
          // C_PropVRHand is referenced but never enumerated and whose m_pClassInfo
          // stub carries a null m_pTypeScope) is absent from the index. Attribute it
          // to the referencing class's OWN module: the type is reached by value from
          // within that module's object graph, so that is where a consumer resolves
          // it. Deterministic (owner module is already set) and satisfies the
          // host's type_module presence requirement.
          if (type_module.empty()) type_module = out->module();
        }
      }
      if (!type_module.empty()) sf->set_type_module(type_module);

      // Enrichment — per-field reflection metadata (SchemaField.metadata).
      // SchemaClassFieldData_t carries the SAME static-metadata pair as the class
      // record (m_pStaticMetadata / m_nStaticMetadataCount; see hl2sdk
      // schematypes.h), so we reuse the exact EmitMetadata walk used for class and
      // enum-member metadata: raw name + raw value, value_parsed left UNSET (the
      // host fills it, same split as class metadata). Declaration order is
      // normalized to (name, original-index) by EmitMetadata for determinism.
      // MODERN ONLY: on k2023 the field-record's static-metadata layout is NOT
      // independently derived (same gap as class metadata — see
      // kClassMetaCountOff2023 == 0 in schema_record_layout_2023.h), so we emit no
      // field metadata on 2023 rather than read an underived offset (deferred-with-
      // reason; the existing 2023 field bytes are unchanged). This is PURELY
      // ADDITIVE on modern: a field with zero metadata adds no bytes.
      if (era == Era::kModern) {
        EmitMetadata(rec::FieldStaticMetadata(f.fd),
                     rec::FieldStaticMetadataCount(f.fd),
                     sf->mutable_metadata());
      }
    }
  }

  // Class-level reflection metadata (incl. MGetKV3ClassDefaults). On kModern read
  // the compiled m_pStaticMetadata/count; on k2023 the metadata-record layout is
  // not yet independently derived, so we emit no class metadata (the host re-parses
  // it and SchemaClass metadata is best-effort — see schema_record_layout_2023.h).
  //
  // MGetKV3ClassDefaults.value is currently emitted EMPTY (kKv3Defaults ->
  // DecodeMetaValue returns ""), but this is a NOT-YET-IMPLEMENTED recovery, NOT a
  // fundamental "impossible headless" wall — the earlier framing here was wrong.
  // m_pData is a generated accessor thunk: a function that default-constructs an
  // instance of this class and serializes its field defaults into a live KeyValues3.
  // ValveResourceFormat/DumpSource2 recovers it HEADLESS by CALLING that thunk and
  // serializing the result:
  //     kv3 = (*(GetKV3DefaultsFn*)m_pData)();            // default-construct + fill
  //     SaveKV3AsJSON(*(KeyValues3**)kv3, &err, &out);    // tier0 export
  // (see DumpSource2 src/main/dumpers/schemas/metadata_stringifier.cpp, KV3DEFAULTS).
  // The two prerequisites once cited as blockers are ALREADY satisfied in this walker:
  //   (1) the tier0 heap allocator is live (every booted subsystem Init() already
  //       allocates through it), so the thunk's default-construct + KeyValues3 alloc
  //       work with no extra setup; and
  //   (2) the accessor IS the KV3 producer — we only additionally need tier0's
  //       SaveKV3AsJSON / SaveKV3Text_ToString export (resolvable from env.modules()'s
  //       tier0 handle, exactly like the RandomSeed export the boot already resolves).
  // The remaining work is a guarded live-call (the DEFERRED-RENDER pattern:
  // Connect-but-defer + a late, crash-guarded, correctly-ordered call — so a
  // faulting accessor degrades to an empty value and resource-manifest reentrancy is
  // avoided), a broken-defaults denylist, and a determinism filter over the
  // nondeterministic auto-id fields (m_id / m_nRandomSeed / ...). None of that is
  // implemented, so the value is deliberately left "".
  if (era == Era::kModern) {
    // Pass the class name so EmitMetadata can recover MGetKV3ClassDefaults via the live accessor call
    // (the ONLY metadata surface that carries it). out->name() is already set by this point.
    EmitMetadata(rec::ClassStaticMetadata(ci), rec::ClassStaticMetadataCount(ci),
                 out->mutable_metadata(), out->name().c_str());
  }

  // Enrichment — static fields (SchemaClass.static_fields). UNREACHABLE via the
  // pinned hl2sdk struct, so this is emitted EMPTY (deferred-with-reason). The
  // pinned hl2sdk SchemaClassInfoData_t (walker/third_party/hl2sdk/public/
  // schemasystem/schematypes.h) carries NO m_pStaticFields / m_nStaticFieldsCount
  // member — its members are { m_pSchemaBinding, m_pszName, m_pszProjectName,
  // m_pszCPPName, m_nSize, m_nFieldCount, m_nStaticMetadataCount, m_nAlignment,
  // m_nBaseClassCount, m_nMultipleInheritanceDepth, m_nSingleInheritanceDepth,
  // m_pFields, m_pBaseClasses, m_pDataDescMap, m_pStaticMetadata, m_pTypeScope,
  // m_pDeclaredClass, m_nFlags1, m_nFlags2, m_pfnManipulator }. A separate
  // SchemaStaticFieldData_t struct IS declared in the same header, but the class-info
  // record has no pointer to an array of it, so there is no compiled member to walk and
  // no offset the pinned headers vouch for. We do NOT guess a raw offset
  // for a member the pinned struct does not name. The proto field stays (emitted empty);
  // if a later hl2sdk pin adds the member, walk it here with the same per-field loop
  // above (FieldName/FieldType/FieldOffset + EmitMetadata) into out->add_static_fields().
  // The one-shot CS2_WALKER_TRACE note for this gap is emitted from WalkSchemaSystem
  // (once per walk, not per class) so the absence is auditable without log spam.
  return true;
}

// ---- Enum -----------------------------------------------------------------
//
// Era-gated like EmitClass: kModern reads the compiled members (byte-identical);
// k2023 reads at the derived enum-record offsets (SEH-guarded).
bool EmitEnum(const CSchemaEnumInfo* ei, wpb::SchemaEnum* out, std::string* err,
              Era era) {
  if (ei == nullptr) {
    *err = "schema walk: null enum binding encountered";
    return false;
  }
  out->set_name(Str(rec::EnumName(ei, era), era));
  out->set_module(ScopeName(rec::EnumTypeScope(ei, era), era));
  // Underlying width as a name string, matching the artifact's "alignment" field
  // (the upstream cs2.json uses the underlying integer type name there).
  switch (rec::EnumSize(ei, era)) {
    case 1:
      out->set_alignment("uint8_t");
      break;
    case 2:
      out->set_alignment("uint16_t");
      break;
    case 4:
      out->set_alignment("uint32_t");
      break;
    case 8:
      out->set_alignment("uint64_t");
      break;
    default:
      out->set_alignment(std::string());
      break;
  }

  // Raw enum-info scalars (modern only). SchemaEnum.flags is the opaque enum-info
  // flags bitmask (SchemaEnumInfoData_t.m_nFlags, uint8 — widened to the proto's
  // uint32) and SchemaEnum.size is the raw byte width (m_nSize, uint8 — the integer
  // the existing derived `alignment` type-name string is computed from; that string
  // is KEPT as-is). proto3 omits zero uint32, so flags==0 / size==0 add no bytes —
  // PURELY ADDITIVE. MODERN ONLY: on k2023 the
  // enum-record m_nFlags / m_nSize offsets are not independently derived beyond
  // the size used for the alignment string, and the 2023 enum table is itself
  // empty by design (open enum-pool gap; ReadScopeEnums returns empty on 2023), so
  // these are never reached there. Left unset on 2023 (deferred-with-reason).
  if (era == Era::kModern) {
    // m_nFlags via the member-presence accessor (rec::EnumFlags); m_nSize via the
    // existing era-gated rec::EnumSize (which returns ei->m_nSize on kModern — the
    // same raw byte the `alignment` type-name string above is computed from). Both
    // byte-identical to the prior direct reads where the member is present;
    // EnumFlags falls back to 0 on any future pin lacking m_nFlags.
    out->set_flags(static_cast<uint32_t>(rec::EnumFlags(ei)));
    out->set_size(static_cast<uint32_t>(rec::EnumSize(ei, era)));
    // Owning project (m_pszProjectName, @+16 in SchemaEnumInfoData_t) — the enum-side
    // counterpart of the class record's project_name, read through the same
    // member-presence accessor idiom (rec::EnumProjectName falls back to nullptr on any
    // pin lacking the member, and the 1-arg Str() maps that to ""). `module` above is the
    // scope's BINARY, which collapses every globally-registered enum into "!GlobalTypes";
    // this is the field that keeps per-project attribution for enums. proto3 omits an
    // empty string on the wire — PURELY ADDITIVE.
    out->set_project_name(Str(rec::EnumProjectName(ei)));
  }

  const SchemaEnumeratorInfoData_t* enumerators = rec::EnumEnumerators(ei, era);
  const int enum_count = rec::EnumCount(ei, era);
  if (enumerators != nullptr && enum_count > 0) {
    struct M {
      std::string name;
      int64_t value;
      const SchemaEnumeratorInfoData_t* e;
    };
    std::vector<M> members;
    for (int i = 0; i < enum_count; ++i) {
      const SchemaEnumeratorInfoData_t* e = rec::EnumeratorAt(enumerators, i, era);
      members.push_back({Str(rec::EnumeratorName(e, era), era),
                         rec::EnumeratorValue(e, era), e});
    }
    std::sort(members.begin(), members.end(), [](const M& a, const M& b) {
      if (a.value != b.value) return a.value < b.value;
      return a.name < b.name;  // Ordinal
    });
    for (auto& m : members) {
      auto* sm = out->add_members();
      sm->set_name(m.name);
      sm->set_value(m.value);
      // Enumerator metadata: compiled members on kModern; not derived on k2023.
      if (era == Era::kModern) {
        EmitMetadata(rec::EnumeratorStaticMetadata(m.e),
                     rec::EnumeratorStaticMetadataCount(m.e),
                     sm->mutable_metadata());
      }
    }
  }
  return true;
}

// ---- per-module full-registration drive (schema family 0.5.0) ----------------
//
// Drive LoadSchemaDataForModules for EVERY loaded module, unconditionally.
//
// HISTORY — two predecessor mechanisms this generalizes:
//   (1) The global fallback: LoadSchemaDataForModules for everything, but ONLY
//       when the WHOLE schema system was empty (never fires once client/server
//       register eagerly).
//   (2) The per-module lazy trigger (older-build regression fix, CS2 build
//       18451221 / 2025-05-13 windows): on OLDER builds the subsystem data
//       modules (animationsystem/particles/vphysics2/...) register lazily, so
//       their scopes stayed empty; the trigger drove exactly those five modules,
//       and only when their scope had ZERO bindings.
//
// WHY UNCONDITIONAL NOW (coverage-gap closure, 2026-07): a PARTIALLY-populated
// scope is the common case, not a corner. client/server eagerly register only a
// subset at static-init (measured on cs2-2026-07-09: 462 of ~800 client-project
// classes; `CTakeDamageInfo` et al. absent) and the remainder — plus the global
// "!GlobalTypes" scope content the walk now includes — is only installed by an
// explicit LoadSchemaDataForModules for the owning module. The zero-bindings
// gate is therefore the exact mechanism that kept the walked universe at ~1.1k
// classes / 15 enums; dropping it (and driving ALL loaded modules) takes the
// walk to ~3.6k classes / ~590 enums. See CS2OpenDev-Docs
// SCHEMA_COVERAGE_GAP_EVALUATION.md for the measured breakdown.
//
// THE TRIGGER (clean-room, pinned hl2sdk only): ISchemaSystem exposes
//   virtual void LoadSchemaDataForModules(const char** ppszModules, int nModules)
// (schemasystem/schemasystem.h:131) and
//   virtual CSchemaSystemTypeScope* FindTypeScopeForModule(const char*, ...)
// (schemasystem/schemasystem.h:119). Both are vtable methods on the ALREADY-
// obtained live schema system — NOT AppSystem lifecycle. We never Connect/Init a
// subsystem module here (that is the fragile engine-boot path that AVs); we only
// ask the schema system to load schema data for a module already resident in this
// process. This is the SAME low-risk surface the existing global fallback uses.
//
// IDEMPOTENT: re-driving an already-populated scope is safe — the schema system
// de-dupes via m_LoadedModules / m_nRedundant, so driving a fully-registered
// module is a no-op and driving a partially-registered one installs exactly the
// missing remainder. Measured on cs2-2026-07-09: the unconditional drive is what
// recovers the non-eager client/server bindings.
//
// CRASH-SAFE: no module Init, no vtable patching. The only
// failure mode is the schema system declining to register (scope stays empty),
// which we tolerate and TRACE rather than crash — it leaves the module exactly as
// the old global fallback would have (no worse than today). Fail-loud is
// unaffected: a genuinely empty WHOLE schema system after all triggers still
// fails downstream the same way it does today; this only ADDS registrations.
//
// LoadSchemaDataForModules / FindTypeScopeForModule are vtable methods on
// ISchemaSystem, NOT gated by any *_INTERFACE_VERSION macro — so there is no
// interface-version macro to #ifdef-guard here (contrast the engine_boot
// SOURCE2MODTOOLS guard, which keys on a per-era CreateInterface string). The
// methods exist in every CS2 schema-system layout the probe admits.
//
// The drive set is every module the loader brought into the process — the
// loader allow-list IS the schema-bearing set, and the idempotence above makes
// over-driving free.

// ---- ERA-GATED per-scope binding enumeration ------------------------------
//
// Reads ONE scope's class bindings (`ReadScopeClasses`) / enum bindings
// (`ReadScopeEnums`) via the runtime era gate (tshash_compat). The MODERN binary
// reads the compiled CUtlTSHash member straight through (byte-identical);
// a 2023-layout game DLL falls back to the 2023 reader (bindings member at
// compiled-8, bucket array at pool_base+88).
//
// The era is now decided ONCE for the WHOLE BUILD (loader.h:
// InProcessEnvironment::schema_is_2023_era(), set by the 2023-only post-boot
// registration fallback) and threaded into these readers as an explicit `era`
// parameter — it is NO LONGER detected per-scope from the compiled Count(). The
// per-scope heuristic mis-detected scopes whose compiled count happened to read a
// plausible small value (client.dll=159, engine2.dll=1 on the 2023 baseline):
// those scopes were wrongly classified kModern and then read through the modern
// record path on 2023-layout records -> wrong counts AND a fault in EmitClass.
// Era is a property of the build, so all scopes now share the build-level era.
//
// On 2023 the SAME era is applied to BOTH the class and enum tables, so a
// zero-enum module still reads enums via the 2023 reader. The 2023 hash base is
// derived as the COMPILED member address minus 8, so the -8 shift auto-tracks the
// b8dcaf14 compiled offset (m_ClassBindings 1376->1368; m_EnumBindings
// 1376+6256->1368+6248=7616) — no hard-coded scope offset. Downstream
// (Element->binding, m_pszName, fields, types) is the STANDARD compiled layout,
// unchanged on 2023.
//
// `trace` (CS2_WALKER_TRACE) reports, per scope, whether the 2023 reader fired.
void TraceScopeEra(CSchemaSystemTypeScope* scope, Era era, bool trace) {
  if (trace && era == Era::k2023 && scope != nullptr) {
    std::fprintf(stderr,
                 "[walker-trace] era-gate: scope=%s -> 2023 layout reader "
                 "(build-level 2023 era)\n",
                 scope->m_szScopeName);
    std::fflush(stderr);
  }
}

std::vector<CSchemaClassInfo*> ReadScopeClasses(CSchemaSystemTypeScope* scope,
                                                Era era, bool trace) {
  std::vector<CSchemaClassInfo*> out;
  if (scope == nullptr) return out;
  TraceScopeEra(scope, era, trace);
  // ReadBindingsForEra derives the 2023 base from the live compiled member address
  // itself (&m_ClassBindings - 8), so the -8 shift auto-tracks the pin's compiled
  // offset. On 2023 the SCOPE-FILTERED pool-blob walk keeps ONLY bindings whose
  // m_pTypeScope (@ the derived 2023 +80) == THIS scope's address — the exact filter
  // the probe's WalkPoolBlobs uses to recover 657/373/3 and drop freed/stale/other-
  // scope blocks so EmitClass never faults. We thread the OWNING scope address (the
  // `scope` we are reading m_ClassBindings of) + the class m_pTypeScope sub-offset.
  tshash_compat::ReadBindingsForEra(
      scope->m_ClassBindings, era,
      offsetof(SchemaClassInfoData_t, m_pszName),
      reinterpret_cast<std::uint64_t>(scope),
      tshash_compat::kClassTypeScopeSub2023, &out);
  return out;
}

std::vector<CSchemaEnumInfo*> ReadScopeEnums(CSchemaSystemTypeScope* scope,
                                             Era era, bool trace) {
  std::vector<CSchemaEnumInfo*> out;
  if (scope == nullptr) return out;
  TraceScopeEra(scope, era, trace);
  // 2023 ENUM GAP: the 2023 m_EnumBindings pool has not been located (the -8 base that
  // works for m_ClassBindings does not find the enum pool), so there is no validated
  // scope-filter / pool base for enums on 2023.
  // We pass typescope_sub == 0, which makes ReadBindings2023 return EMPTY for the
  // enum table on 2023 rather than walk an un-located pool and fault EmitEnum. On
  // modern this argument is unused (compiled GetElements path). Classes are the
  // priority; 2023 emits 0 enums for now (see tshash_compat.h ReadBindings2023).
  tshash_compat::ReadBindingsForEra(
      scope->m_EnumBindings, era,
      offsetof(SchemaEnumInfoData_t, m_pszName),
      reinterpret_cast<std::uint64_t>(scope),
      /*typescope_sub=*/0, &out);
  return out;
}

// ---- RUNTIME ERA DETECTION (record-layout probe) --------------------------
//
// Decide whether a scope's live class records are in the 2023 layout. This is the
// ROBUST replacement for the old "the post-boot SchemaSystem_001 fallback fired =>
// 2023" heuristic, which mis-fired on MODERN builds whose engine boot left the
// schema empty (a boot fault): the fallback re-registers those modules with MODERN-
// layout records, so "fallback fired" does NOT imply a 2023 layout. We instead read
// the actual records.
//
// FAULT-SAFE BY CONSTRUCTION: we ONLY ever interpret records via the k2023 path,
// whose every read is SEH-guarded + bounded (the pool-blob walk caps blobs/blocks
// and validates each pointer). We NEVER run the modern compiled CUtlTSHash path
// here — that path is unguarded and would fault on a 2023-layout CUtlTSHash. So on a
// MODERN build the 2023 pool walk simply reads garbage (guarded) that fails the
// validity test below, and we correctly fall back to "modern" (return false).
//
// VALIDITY: of the first few recovered bindings, a majority must read as a sane
// class record FOR THE GIVEN ERA — non-empty class name, an in-range field count,
// and (when the class has fields) a first field whose name begins with 'm' (every
// CS2 schema field is m_*). Garbage from interpreting one layout's records through
// the OTHER layout's offsets essentially never produces an "m_*" first field name,
// so the test cleanly separates the two layouts in BOTH directions.
bool RecordsValidateAsEra(const std::vector<CSchemaClassInfo*>& bindings, Era era) {
  int checked = 0, ok = 0;
  for (CSchemaClassInfo* ci : bindings) {
    if (ci == nullptr) continue;
    if (checked >= 8) break;
    ++checked;
    const std::string name = Str(rec::ClassName(ci, era), era);
    if (name.empty()) continue;
    const int fc = rec::ClassFieldCount(ci, era);
    if (fc < 0 || fc > 8000) continue;
    if (fc > 0) {
      const SchemaClassFieldData_t* fa = rec::ClassFields(ci, era);
      if (fa == nullptr) continue;
      const std::string fn =
          Str(rec::FieldName(rec::FieldAt(fa, 0, era), era), era);
      if (fn.empty() || fn[0] != 'm') continue;  // CS2 fields are m_*
    }
    ++ok;
  }
  // Require a solid majority AND an absolute floor so a lone fluke can't decide it.
  return ok >= 3 && ok * 2 >= checked;
}

// SEH-guarded MODERN validation. Unlike the 2023 pool-blob walk (which is bounded +
// SEH-guarded internally), ReadScopeClasses(.., kModern) drives the compiled
// CUtlTSHash GetElements path, which is UNGUARDED and ACCESS-VIOLATES if the live
// CUtlTSHash is actually a 2023 layout. So we run the whole read+validate inside a
// SEH trampoline (POD ctx): a fault => "modern does not validate" (valid stays
// false) and the caller falls through to the 2023 interpretation. On a true modern
// build the read succeeds and the records validate cleanly.
struct ModernValidateCtx {
  CSchemaSystemTypeScope* scope;
  bool valid;
};
bool ModernValidateTrampoline(void* p) {
  auto* c = static_cast<ModernValidateCtx*>(p);
  c->valid = RecordsValidateAsEra(ReadScopeClasses(c->scope, Era::kModern, false),
                                  Era::kModern);
  return true;
}
bool RecordsValidateAsModernGuarded(CSchemaSystemTypeScope* scope) {
  ModernValidateCtx ctx{scope, false};
  // SehGuardedCall == false => faulted (treat as not-modern). On success, ctx.valid.
  if (!tshash_compat::SehGuardedCall(&ModernValidateTrampoline, &ctx)) return false;
  return ctx.valid;
}

// True if `module` has a registered type scope with at least one class or enum
// binding. Uses only header-inline accessors already used elsewhere in this TU.
bool ScopeHasBindings(CSchemaSystem* system, const char* module, Era era) {
  CSchemaSystemTypeScope* scope = system->FindTypeScopeForModule(module);
  if (scope == nullptr) return false;
  // Era-gated with the BUILD-LEVEL era (loader.h schema_is_2023_era()): on the
  // 2023 baseline a raw `> 0` compiled-count test would mis-classify a populated
  // subsystem scope, so we read via the same era the walk uses, so the emptiness
  // check agrees with what the walk will actually enumerate.
  const bool trace = false;  // emptiness probe is quiet; the walk traces.
  return !ReadScopeClasses(scope, era, trace).empty() ||
         !ReadScopeEnums(scope, era, trace).empty();
}

// ---- ERA-STABLE type-scope enumeration (VTABLE-ONLY) -----------------------
//
// REPLACES the old `for (s in m_TypeScopes) ...` data-member walk. The compiled
// offset of CSchemaSystem::m_TypeScopes DRIFTS across CS2 eras (modern +0x190 vs
// the 2023 baseline +0x198), so reading it as a data member mis-reads (or misses)
// the registered scopes on older builds even after registration succeeds. We
// instead discover the live type scopes through ISchemaSystem's VTABLE — pointer-
// returning methods whose slot index is stable across every era the loader admits
// and whose return value is NOT a layout-sensitive field read:
//
//   - ISchemaSystem::GlobalTypeScope()              (schemasystem.h:117, vslot)
//   - ISchemaSystem::FindTypeScopeForModule(name)   (schemasystem.h:119, vslot)
//
// FindTypeScopeForModule is the NON-creating lookup (cf.
// FindOrCreateTypeScopeForModule); it returns a scope iff `name`'s module
// registered its bindings. We probe it for every LOADED module (the same set the
// loader brought into the process), keyed by the module's FULL filename WITH
// platform suffix, and dedup by pointer.
//
// Scope set = every registered per-MODULE scope PLUS the global "!GlobalTypes"
// scope. The global scope was excluded until schema family 0.5.0 (byte-compat
// with the original entity walk), but the 2026-07 coverage-gap analysis measured
// that it holds the BULK of the registered universe — the lib projects
// (particles, animlib/animgraphlib, smartprops, modellib, physicslib,
// sound*, worldrenderer, materialsystem2, the *doclib set, ...) and nearly all
// enums (15 emitted vs 545 registered). Including it (plus the wider loader
// allow-list and forced registration below) closes that gap; see
// CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md. Classes found in the
// global scope keep their own binding's scope attribution at emit time
// (EmitClass reads ClassTypeScope(ci), i.e. module "!GlobalTypes") plus the
// binding's project_name (animlib, smartprops, ...), which is the meaningful
// grouping key for globally-registered types.
// The downstream emit sorts the whole binding set by (name, module) Ordinal, so
// the ORDER in which scopes are visited here is irrelevant to the output bytes.
// We dedup by scope pointer (FindTypeScopeForModule may legitimately resolve a
// never-registered module to a shared scope on some eras; deduping keeps each
// scope's bindings counted exactly once). The visitation order is itself made
// deterministic: env.modules() is already sorted (loader DiscoverModules sorts
// for determinism).
//
// HL2SDK-ONLY: both methods are declared pure-virtual on ISchemaSystem in
// the pinned hl2sdk header and are dispatched through the live object's vtable —
// no symbol from the (unlinked) CS2 DLL is referenced, so there is no link
// dependency. This is the same surface SchemaSystemIsEmpty() already uses.
std::vector<CSchemaSystemTypeScope*> CollectTypeScopes(
    CSchemaSystem* system, const InProcessEnvironment& env) {
  std::vector<CSchemaSystemTypeScope*> scopes;
  // The global "!GlobalTypes" scope is a first-class member of the walk set as
  // of schema family 0.5.0 (see the function comment): it is where the lib
  // projects and nearly all enums register. It is pushed explicitly below —
  // FindTypeScopeForModule also returns it as the fallback for a loaded module
  // that registered no scope of its own, and the pointer dedup keeps it counted
  // exactly once either way.
  CSchemaSystemTypeScope* const global_scope = system->GlobalTypeScope();
  auto push_unique = [&scopes](CSchemaSystemTypeScope* s) {
    if (s == nullptr) return;
    for (CSchemaSystemTypeScope* existing : scopes) {
      if (existing == s) return;  // already collected (dedup by pointer).
    }
    scopes.push_back(s);
  };

  // Every loaded module's scope, keyed by the FULL module filename WITH its
  // platform suffix (e.g. "server.dll" on Windows, "libserver.so" on Linux).
  // This is the same string Valve stores in m_szScopeName and that
  // FindTypeScopeForModule is keyed on — m.module_name() strips the suffix
  // (and the leading "lib" on POSIX), so it always MISSES the lookup. Use
  // m.filename() to match. env.modules() is pre-sorted (loader DiscoverModules
  // sorts for determinism), so visitation order is itself deterministic. This yields
  // every registered per-MODULE scope (server.dll / client.dll / engine2.dll /
  // particles.dll + any other loaded module carrying a registered scope); the
  // global "!GlobalTypes" scope is excluded in push_unique (see its rationale
  // above).
  for (const auto& m : env.modules()) {
    const std::string name = m.filename();
    push_unique(system->FindTypeScopeForModule(name.c_str()));
  }
  // The global scope is not keyed to any single module — add it explicitly
  // (no-op if a fallback lookup above already resolved to it).
  push_unique(global_scope);
  return scopes;
}

// Drive full schema registration for EVERY loaded module (unconditional,
// idempotent — see the "per-module full-registration drive" comment block).
// Only loaded modules are driven: registration cannot conjure the descriptors of
// a module that was never brought into the process.
void ForceFullSchemaRegistration(CSchemaSystem* system,
                                 const InProcessEnvironment& env,
                                 bool trace) {
  // Build-level era (authoritative; see loader.h schema_is_2023_era()). Used
  // only for the post-drive trace probe so it agrees with the walk.
  const Era era = env.schema_is_2023_era() ? Era::k2023 : Era::kModern;
  for (const auto& m : env.modules()) {
    const std::string bare = m.module_name();
    const char* one[] = {bare.c_str()};
    // Method on the already-obtained schema system; no module Init (crash-safe).
    system->LoadSchemaDataForModules(one, 1);

    if (trace) {
      const bool now = ScopeHasBindings(system, bare.c_str(), era);
      std::fprintf(stderr,
                   "[walker-trace] full-reg: %s driven; scope populated=%d\n",
                   bare.c_str(), now ? 1 : 0);
      std::fflush(stderr);
    }
  }
}

// ---- SEH-GUARDED per-record emit (2023 fault containment) ------------------
//
// On the 2023 era a single binding the pool-blob walk recovered MAY still be a
// freed/stale/partly-initialized record whose field array, type chain, parent
// array, or m_pTypeScope dereferences into unmapped memory. The per-field byte
// reads are already SEH-guarded (rec2023:: accessors + Str(.,era) +
// ScopeName(.,era)), but a wild VALUE (e.g. a field count of 40000 against a
// 3-entry array, or a subclass downcast landing on a non-pointer that passes the
// LooksLikePointer2023 pre-filter yet points at a guard page) can still fault a
// read this layer did not anticipate. Rather than abort the WHOLE walk (which
// would lose the hundreds of clean classes), we run EACH record's emit under an
// SEH trampoline: a fault on ONE record discards that record's partial proto and
// is COUNTED as a skip (traced), and the walk continues. The MODERN path also
// runs through the trampoline but never faults (live compiled records), so its
// output is byte-identical — the trampoline is transparent on success.
//
// Fail-loud is preserved at the WALK granularity: a structurally-broken whole schema
// system still yields zero clean records and fails downstream exactly as before;
// this only prevents one garbage 2023 record from taking the rest down.
struct EmitClassCtx {
  const CSchemaClassInfo* ci;
  wpb::SchemaClass* out;
  std::string* err;
  Era era;
  const DeclaredModuleIndex* decl_index;  // name->module fallback (all eras since 0.5.0)
  bool ok;
};
struct EmitEnumCtx {
  const CSchemaEnumInfo* ei;
  wpb::SchemaEnum* out;
  std::string* err;
  Era era;
  bool ok;
};
// The trampoline RETURN value means "the emit ran to completion WITHOUT a hardware
// fault". The emit's own success/failure (e.g. null-binding -> *err set) lands in
// c->ok. SehGuardedCall returns false ONLY on a fault; the caller distinguishes:
//   SehGuardedCall == false           -> faulted mid-record (2023): SKIP + count.
//   SehGuardedCall == true, ok == false -> legit emit failure (*err set): see caller.
//   SehGuardedCall == true, ok == true  -> clean record.
bool EmitClassTrampoline(void* p) {
  auto* c = static_cast<EmitClassCtx*>(p);
  c->ok = EmitClass(c->ci, c->out, c->err, c->era, c->decl_index);
  return true;
}
bool EmitEnumTrampoline(void* p) {
  auto* c = static_cast<EmitEnumCtx*>(p);
  c->ok = EmitEnum(c->ei, c->out, c->err, c->era);
  return true;
}

// ---- TOP-LEVEL 2023 EMIT CONTAINMENT (diagnostic-grade) --------------------
//
// LOCALIZE + CONTAIN the 2023 emit-pass crash (build 10832117). Background: on the
// 2023 baseline, enumeration (the per-scope era-gate counting pass) completes
// cleanly for EVERY scope, but the process then dies in the EMIT pass with
// 0xC0000096 (STATUS_PRIVILEGED_INSTRUCTION) and NO output — somewhere in the
// accumulate -> sort -> EmitClass-loop -> (FirstDeclaredModule lives inside
// EmitClass). The PER-RECORD SehGuardedCall trampoline above did NOT contain it:
// MSVC __except does not unwind the C++ objects EmitClass constructs, and parts of
// the emit (the std::sort comparators, and the accumulate loop's own name/module
// key reads) run OUTSIDE any trampoline.
//
// STRATEGY (two-level SEH, POD outer frame): a function that contains __try/__except
// may NOT also contain C++ objects requiring unwinding (MSVC C2712). So the EMIT is
// split:
//   - DoSchemaEmit(SchemaEmitCtx*)  — contains ALL the C++ objects (vectors of
//     accumulated entries, std::sort, the EmitClass/EmitEnum loops). NO __try here.
//   - SchemaEmitTrampoline(void*)   — POD-only leaf; just calls DoSchemaEmit.
//   - RunSchemaEmitGuarded(...)     — POD-only; wraps the trampoline call in ONE
//     top-level __try/__except. On a hardware fault anywhere in DoSchemaEmit, the
//     SEH filter abandons the C++ frames WITHOUT running their dtors (we LEAK — an
//     acceptable trade for a 2023-only crash-containment diagnostic) and reports
//     FAULTED. Whatever classes/enums DoSchemaEmit already appended to `out` survive
//     (protobuf arena/heap memory the abandoned frames do not own), so the walk can
//     still WRITE A PARTIAL artifact (goal: get OUTPUT + the culprit name).
//
// MODERN PATH UNCHANGED: WalkSchemaSystem calls DoSchemaEmit DIRECTLY on
// kModern (no outer SEH wrapper), so modern is byte-identical. The outer guard is
// ENGAGED ONLY on the k2023 build era. Per-record fail-loud on modern (a legit
// EmitClass failure -> *err set -> abort) is preserved inside DoSchemaEmit.
struct SchemaEmitCtx {
  // Inputs (the accumulated, pre-sorted-key entries are built INSIDE DoSchemaEmit;
  // we only thread the raw scope set + era + sinks here so the outer frame is POD).
  const std::vector<CSchemaSystemTypeScope*>* scopes;
  Era era;
  bool trace;
  wpb::EntitySchemaWalk* out;
  std::string* err;
  // Outputs.
  bool ok;           // DoSchemaEmit ran to completion (no fault); *err valid on false
  int classes_done;  // classes for which EmitClass returned (clean or skipped)
  int enums_done;    // enums for which EmitEnum returned
  int skipped_classes;
  int skipped_enums;
  bool aborted_modern;  // a legit modern EmitClass/EmitEnum failure (*err set) -> abort
};

// All C++ unwinding objects live here. NO __try/__except in this function.
bool DoSchemaEmit(SchemaEmitCtx* c);

// POD-only leaf trampoline: the only thing the outer __try wraps.
bool SchemaEmitTrampoline(void* p) {
  return DoSchemaEmit(static_cast<SchemaEmitCtx*>(p));
}

// POD-only outer frame: ONE top-level SEH guard around the whole 2023 emit. Returns
// true iff the emit ran WITHOUT a hardware fault; false iff it faulted (the caller
// then keeps the partial `out` and traces the culprit). MUST contain no C++ objects
// requiring unwinding (only the POD `p` arg), so __try/__except is well-formed.
#ifdef _WIN32
bool RunSchemaEmitGuarded(void* p) {
  __try {
    return SchemaEmitTrampoline(p);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;  // faulted mid-emit; C++ frames abandoned (leak), `out` retained
  }
}
#else
bool RunSchemaEmitGuarded(void* p) { return SchemaEmitTrampoline(p); }
#endif

// ---- DoSchemaEmit: the whole post-enumeration emit (accumulate->sort->emit) ----
//
// Moved verbatim out of WalkSchemaSystem so the 2023 path can run it under the
// top-level POD-frame SEH guard above (this function freely uses std::vector,
// std::sort, std::string, protobuf — it can NOT contain __try itself). On kModern
// WalkSchemaSystem calls this DIRECTLY (no guard), so the output is byte-identical.
// All trace lines are gated on c->trace AND, for the per-class/per-phase
// localization, on the 2023 era (so modern is untouched).
bool DoSchemaEmit(SchemaEmitCtx* c) {
  const Era build_era = c->era;
  const bool trace = c->trace;
  wpb::EntitySchemaWalk* out = c->out;
  std::string* err = c->err;
  const bool k2023 = (build_era == Era::k2023);

  c->ok = true;
  c->aborted_modern = false;
  c->classes_done = 0;
  c->enums_done = 0;
  c->skipped_classes = 0;
  c->skipped_enums = 0;

  // Accumulate classes/enums across all scopes, then sort the whole set by name
  // Ordinal for determinism. Sort KEYS (name, module) are era-gated record
  // reads computed up front so the comparator is a pure string compare.
  struct ClassEntry {
    const CSchemaClassInfo* ci;
    Era era;
    std::string name, module;
  };
  struct EnumEntry {
    const CSchemaEnumInfo* ei;
    Era era;
    std::string name, module;
  };
  std::vector<ClassEntry> all_classes;
  std::vector<EnumEntry> all_enums;

  if (trace && k2023) {
    std::fprintf(stderr, "[walker-trace] emit: PHASE accumulate (key reads)\n");
    std::fflush(stderr);
  }
  for (CSchemaSystemTypeScope* scope : *c->scopes) {
    if (scope == nullptr) continue;  // defensive; CollectTypeScopes drops nulls.
    const Era era = build_era;
    for (CSchemaClassInfo* ci : ReadScopeClasses(scope, era, trace)) {
      SchemaSymbolRef key = ClassKey(ci, era);
      all_classes.push_back({ci, era, std::move(key.name), std::move(key.module)});
    }
    for (CSchemaEnumInfo* ei : ReadScopeEnums(scope, era, trace)) {
      SchemaSymbolRef key = EnumKey(ei, era);
      all_enums.push_back({ei, era, std::move(key.name), std::move(key.module)});
    }
  }

  if (trace && k2023) {
    std::fprintf(stderr,
                 "[walker-trace] emit: PHASE sort (%zu classes, %zu enums)\n",
                 all_classes.size(), all_enums.size());
    std::fflush(stderr);
  }
  std::sort(all_classes.begin(), all_classes.end(),
            [](const ClassEntry& a, const ClassEntry& b) {
              const int cmp = a.name.compare(b.name);
              if (cmp != 0) return cmp < 0;
              return a.module < b.module;
            });
  std::sort(all_enums.begin(), all_enums.end(),
            [](const EnumEntry& a, const EnumEntry& b) {
              const int cmp = a.name.compare(b.name);
              if (cmp != 0) return cmp < 0;
              return a.module < b.module;
            });

  // Build the declared-ref module fallback index (NAME -> sorted unique modules)
  // from the enumerated set whose modules already resolved. Consulted on every
  // era since 0.5.0 (the global-scope walk surfaces stub refs on modern too).
  DeclaredModuleIndex decl_index;
  {
    auto add = [&decl_index](const std::string& name, const std::string& module) {
      if (name.empty() || module.empty()) return;
      std::vector<std::string>& v = decl_index[name];
      if (std::find(v.begin(), v.end(), module) == v.end()) v.push_back(module);
    };
    for (const ClassEntry& e : all_classes) add(e.name, e.module);
    for (const EnumEntry& e : all_enums) add(e.name, e.module);
    for (auto& kv : decl_index) std::sort(kv.second.begin(), kv.second.end());
  }
  const DeclaredModuleIndex* decl_index_ptr = &decl_index;

#if !defined(_WIN32)
  // FORK-ISOLATED KV3 pre-pass (POSIX). Collect every class-level MGetKV3ClassDefaults accessor and
  // recover them in a child process, so a build-specific corrupting accessor kills only a child (and
  // is skipped) instead of the whole walk. The emit loop's DecodeKv3Defaults then just reads the
  // resulting map. Runs only when KV3 is ENABLED (g_save_kv3_as_json != null — i.e. the era is not
  // gated off) and on the MODERN layout (the only one carrying this metadata). On k2023 / gated eras
  // g_kv3_isolated_ran stays false and DecodeKv3Defaults returns empty exactly as before.
  if (g_save_kv3_as_json != nullptr && build_era == Era::kModern) {
    std::vector<Kv3Req> kv3_reqs;
    std::unordered_set<uintptr_t> kv3_seen;
    for (const ClassEntry& e : all_classes) {
      Kv3ScanCtx sctx;
      sctx.ci = e.ci;
      sctx.count = 0;
      if (!posix_crash_guard::RunGuarded(&Kv3ScanLeaf, &sctx)) continue;  // scan faulted -> skip class
      for (int i = 0; i < sctx.count; ++i) {
        uintptr_t key = reinterpret_cast<uintptr_t>(sctx.out[i]);
        if (key == 0 || !kv3_seen.insert(key).second) continue;
        if (IsBrokenKv3DefaultsClass(e.name.c_str())) continue;  // denylisted -> empty (never called)
        kv3_reqs.push_back({sctx.out[i], e.name});
      }
    }
    if (trace) {
      std::fprintf(stderr, "[kv3] ISOLATED pre-pass: %zu accessor(s) to recover\n", kv3_reqs.size());
      std::fflush(stderr);
    }
    RecoverKv3DefaultsIsolated(kv3_reqs, trace);
  }
#endif

  if (trace && k2023) {
    std::fprintf(stderr, "[walker-trace] emit: PHASE emit-classes (%zu)\n",
                 all_classes.size());
    std::fflush(stderr);
  }
  for (const ClassEntry& e : all_classes) {
    // LOCALIZE (2023-only): print the class name+module IMMEDIATELY BEFORE
    // EmitClass touches it, flushed, so the LAST printed name is the culprit when
    // the process faults inside EmitClass / TranslateType / FirstDeclaredModule.
    if (trace && k2023) {
      std::fprintf(stderr, "[walker-trace] emit: -> class[%d] name='%s' module='%s'\n",
                   c->classes_done, e.name.c_str(), e.module.c_str());
      std::fflush(stderr);
    }
    wpb::SchemaClass* slot = out->add_classes();
    EmitClassCtx ctx{e.ci, slot, err, e.era, decl_index_ptr, false};
    const bool no_fault = tshash_compat::SehGuardedCall(&EmitClassTrampoline, &ctx);
    ++c->classes_done;
    if (no_fault && ctx.ok) continue;  // clean record
    if (no_fault && !ctx.ok && e.era == Era::kModern) {
      c->ok = false;
      c->aborted_modern = true;  // *err set by EmitClass; modern fail-loud
      return false;
    }
    out->mutable_classes()->RemoveLast();  // discard the partial/garbage record
    ++c->skipped_classes;
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] emit: SKIP class (%s) name='%s' module='%s'\n",
                   no_fault ? "null-binding" : "fault", e.name.c_str(),
                   e.module.c_str());
      std::fflush(stderr);
    }
  }

  if (trace && k2023) {
    std::fprintf(stderr, "[walker-trace] emit: PHASE emit-enums (%zu)\n",
                 all_enums.size());
    std::fflush(stderr);
  }
  for (const EnumEntry& e : all_enums) {
    if (trace && k2023) {
      std::fprintf(stderr, "[walker-trace] emit: -> enum[%d] name='%s' module='%s'\n",
                   c->enums_done, e.name.c_str(), e.module.c_str());
      std::fflush(stderr);
    }
    wpb::SchemaEnum* slot = out->add_enums();
    EmitEnumCtx ctx{e.ei, slot, err, e.era, false};
    const bool no_fault = tshash_compat::SehGuardedCall(&EmitEnumTrampoline, &ctx);
    ++c->enums_done;
    if (no_fault && ctx.ok) continue;
    if (no_fault && !ctx.ok && e.era == Era::kModern) {
      c->ok = false;
      c->aborted_modern = true;  // modern fail-loud
      return false;
    }
    out->mutable_enums()->RemoveLast();
    ++c->skipped_enums;
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] emit: SKIP enum (%s) name='%s' module='%s'\n",
                   no_fault ? "null-binding" : "fault", e.name.c_str(),
                   e.module.c_str());
      std::fflush(stderr);
    }
  }

  if (trace && (c->skipped_classes > 0 || c->skipped_enums > 0)) {
    std::fprintf(stderr,
                 "[walker-trace] emit: skipped %d/%zu classes, %d/%zu enums "
                 "(fault-safe 2023 containment)\n",
                 c->skipped_classes, all_classes.size(),
                 c->skipped_enums, all_enums.size());
    std::fflush(stderr);
  }
  c->ok = true;
  return true;
}

}  // namespace

// ERA-STABLE schema-empty probe (see schema_walk.h). VTABLE-ONLY: it never reads
// a data member whose compiled offset could drift across eras (the whole reason
// the 2023 baseline can't use m_TypeScopes.GetNumStrings()). It asks the live
// ISchemaSystem, through its vtable, whether any loaded module owns a registered
// type scope:
//   - GlobalTypeScope()                  (schemasystem.h:117) — the always-present
//                                        global scope; used to exclude it from the
//                                        per-module count below.
//   - FindTypeScopeForModule(name)       (schemasystem.h:119) — NON-creating lookup
//                                        (cf. FindOrCreateTypeScopeForModule). It
//                                        returns a scope iff that module registered.
// Both return a bare pointer; neither reads a layout-sensitive field. A module
// scope that is non-null and not the global scope means schema is registered ->
// NOT empty. Zero such scopes across every loaded module == empty.
bool SchemaSystemIsEmpty(const InProcessEnvironment& env) {
  auto* system = reinterpret_cast<ISchemaSystem*>(env.schema_system());
  if (system == nullptr) {
    // No live schema system: treat as empty so the caller's retry runs. A
    // genuinely null schema system still fails loud later (WalkSchemaSystem).
    return true;
  }

  // GlobalTypeScope() always exists; we use it only to exclude it from the
  // per-module count (FindTypeScopeForModule for a never-registered module may
  // legitimately resolve to the global scope on some eras).
  CSchemaSystemTypeScope* global = system->GlobalTypeScope();

  for (const auto& m : env.modules()) {
    const std::string name = m.module_name();
    CSchemaSystemTypeScope* scope = system->FindTypeScopeForModule(name.c_str());
    if (scope != nullptr && scope != global) {
      return false;  // at least one module scope registered -> not empty.
    }
  }
  return true;
}

bool WalkSchemaSystem(const InProcessEnvironment& env,
                      wpb::EntitySchemaWalk* out, std::string* err) {
  out->Clear();

  auto* system = reinterpret_cast<CSchemaSystem*>(env.schema_system());
  if (system == nullptr) {
    *err = "schema walk: null CSchemaSystem (loader did not populate it)";
    return false;
  }

  // Resolve tier0's SaveKV3AsJSON once so per-class MGetKV3ClassDefaults recovery can serialize the
  // accessor's KeyValues3. Absence is non-fatal (those values stay empty, as before).
  MaybeResolveSaveKv3Json(env);

  const bool trace = (std::getenv("CS2_WALKER_TRACE") != nullptr);

  // BUILD-LEVEL era (authoritative; loader.h InProcessEnvironment). The era is a
  // property of the WHOLE BUILD, decided once: TRUE iff the 2023-only post-boot
  // "SchemaSystem_001" registration fallback registered modules
  // (RetrySchemaRegistrationIfEmpty). Every scope (server/client/engine2/...) is
  // walked under this ONE era — never the per-scope compiled-count heuristic that
  // mis-detected client.dll/engine2.dll on the 2023 baseline. Modern builds leave
  // the flag FALSE -> kModern everywhere -> byte-identical.
  const Era build_era = BuildEra(env);
  if (trace) {
    std::fprintf(stderr, "[walker-trace] schema-walk: build_era=%s\n",
                 build_era == Era::k2023 ? "2023" : "modern");
    // One-shot static-fields note (see EmitClass): SchemaClass.static_fields is
    // emitted EMPTY on every era because the pinned hl2sdk SchemaClassInfoData_t names
    // no m_pStaticFields member to walk (deferred-with-reason; grep this line).
    std::fprintf(stderr,
                 "[walker-trace] static-fields: SchemaClass.static_fields emitted "
                 "EMPTY — pinned hl2sdk SchemaClassInfoData_t has no m_pStaticFields "
                 "member (deferred-with-reason)\n");
    // One-shot enrichment summary: the modern path now emits per-field metadata +
    // class alignment/flags; 2023 emits none of these (underived offsets).
    std::fprintf(stderr,
                 "[walker-trace] enrichment: per-field metadata + class "
                 "alignment/flags %s (modern); 2023 emits empty (underived)\n",
                 build_era == Era::kModern ? "ENABLED" : "skipped");
    // One-shot enrichment summary (additive fields). MODERN: parent.offset
    // (era-stable), enum flags/size, class flags2 + single/multiple-inheritance
    // depth + project_name/cpp_name, decoded metadata VALUES (string/int/float/
    // float-range, keyed by name), and Command.has_completion_callback (cvar walk).
    // 2023: only parent.offset rides along (the rest are underived/empty — same
    // deferred-with-reason as the prior enrichment).
    std::fprintf(stderr,
                 "[walker-trace] batch1: parent.offset%s; enum flags/size, class "
                 "flags2/inherit-depth/project_name/cpp_name, metadata VALUES, "
                 "command.has_completion_callback %s (modern)\n",
                 build_era == Era::kModern ? "" : " (2023: parent.offset only)",
                 build_era == Era::kModern ? "ENABLED" : "skipped");
    std::fflush(stderr);
  }

  // If no type scope has registered yet (stock CS2 modules register schema via
  // the Source 2 IAppSystem lifecycle, not at raw DLL load), drive registration
  // through the schema system's own LoadSchemaDataForModules vtable entry. This
  // is a method on the ALREADY-obtained schema system (no new module init), so
  // it is far lower risk than bootstrapping a full AppSystem Connect chain. We
  // pass the bare names of the loaded, schema-bearing modules.
  //
  // EMPTINESS is now probed via the ERA-STABLE vtable check (SchemaSystemIsEmpty),
  // NOT m_TypeScopes.GetNumStrings(): the m_TypeScopes data-member offset drifts
  // across eras, so the raw-offset count mis-reads the older-build pre/post
  // registration state. SchemaSystemIsEmpty asks the schema system through its
  // vtable (GlobalTypeScope + FindTypeScopeForModule) whether any module scope
  // registered — the same layout-stable surface the scope enumeration below uses.
  // On modern this returns false (scopes already populated post-boot), so the
  // fallback is skipped exactly as the old GetNumStrings()!=0 path skipped it —
  // byte-identical.
  if (SchemaSystemIsEmpty(env)) {
    std::vector<std::string> names;
    for (const auto& m : env.modules()) names.push_back(m.module_name());
    std::vector<const char*> cnames;
    for (const auto& n : names) cnames.push_back(n.c_str());
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] schema-walk: empty; LoadSchemaDataForModules(%zu)\n",
                   cnames.size());
      std::fflush(stderr);
    }
    system->LoadSchemaDataForModules(cnames.data(),
                                     static_cast<int>(cnames.size()));
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] schema-walk: after load empty=%d ready=%d\n",
                   SchemaSystemIsEmpty(env) ? 1 : 0,
                   system->SchemaSystemIsReady() ? 1 : 0);
      std::fflush(stderr);
    }
  }

  // Per-module full-registration drive (unconditional, idempotent). Eager
  // static-init registration is PARTIAL on every era (client/server install only
  // a subset; the lib projects + the global scope's content need an explicit
  // LoadSchemaDataForModules per owning module). Driving every loaded module
  // here is what fills the scopes the walk below then enumerates — see the
  // function's comment block for the measured coverage impact.
  ForceFullSchemaRegistration(system, env, trace);

  // Enumerate every registered type scope via the ERA-STABLE vtable surface
  // (GlobalTypeScope + FindTypeScopeForModule), NOT the m_TypeScopes data member
  // whose compiled offset drifts across eras. CollectTypeScopes yields the same
  // scope set m_TypeScopes held (global + every registered module scope), deduped
  // by pointer, in a deterministic order (see CollectTypeScopes).
  std::vector<CSchemaSystemTypeScope*> scopes = CollectTypeScopes(system, env);
  const int scope_count = static_cast<int>(scopes.size());

  if (trace) {
    std::fprintf(stderr,
                 "[walker-trace] schema-walk: scope_count=%d "
                 "SchemaSystemIsReady=%d\n",
                 scope_count, system->SchemaSystemIsReady() ? 1 : 0);
    for (int s = 0; s < scope_count; ++s) {
      CSchemaSystemTypeScope* scope = scopes[s];
      // Era-gated counts: a 2023-layout scope's compiled Count() is garbage, so
      // print what the era gate will actually enumerate (matches the emit).
      const int nc = scope ? static_cast<int>(ReadScopeClasses(scope, build_era, false).size()) : -1;
      const int ne = scope ? static_cast<int>(ReadScopeEnums(scope, build_era, false).size()) : -1;
      std::fprintf(stderr, "[walker-trace]   scope[%d]=%s classes=%d enums=%d\n",
                   s, scope ? scope->m_szScopeName : "<null>", nc, ne);
    }
    std::fflush(stderr);
  }

  // EMIT PASS. Accumulate -> sort -> EmitClass/EmitEnum. This whole pass is now in
  // DoSchemaEmit (it uses std::vector/std::sort/std::string/protobuf, so it cannot
  // itself contain __try). Two dispatch modes:
  //
  //   kModern: call DoSchemaEmit DIRECTLY — no outer SEH guard, so the modern path
  //            is byte-identical to the prior inline emit. A legit
  //            EmitClass/EmitEnum failure sets *err and aborts (fail-loud).
  //
  //   k2023:   call DoSchemaEmit through RunSchemaEmitGuarded, a POD-frame top-level
  //            SEH guard. The prior per-record trampoline did NOT contain the
  //            0xC0000096 emit-pass fault (the std::sort comparators and the
  //            accumulate-pass key reads run outside any per-record trampoline, and
  //            __except does not unwind EmitClass's C++ objects). This ONE outer
  //            guard wraps the ENTIRE emit. On a fault we KEEP whatever classes were
  //            already appended to `out`, trace how far we got + the culprit (the
  //            last per-class line printed above the fault localizes it), and return
  //            SUCCESS so RunWalk still WRITES the (partial) artifact. This is a
  //            2023-only diagnostic-grade containment; it leaks the abandoned C++
  //            frames (acceptable for a crash-containment diagnostic — we leak, not
  //            crash). Modern is never routed here, so its output is unaffected.
  SchemaEmitCtx emit_ctx{};
  emit_ctx.scopes = &scopes;
  emit_ctx.era = build_era;
  emit_ctx.trace = trace;
  emit_ctx.out = out;
  emit_ctx.err = err;

  if (build_era == Era::kModern) {
    if (!DoSchemaEmit(&emit_ctx)) {
      // Modern: only a legit EmitClass/EmitEnum failure returns false; *err is set.
      return false;  // fail-loud.
    }
    return true;
  }

  // k2023: top-level containment.
  const bool no_fault = RunSchemaEmitGuarded(&emit_ctx);
  if (!no_fault) {
    // A hardware fault propagated out of DoSchemaEmit to the outer __except. The
    // C++ frames were abandoned (leak), but the classes already appended to `out`
    // survive. emit_ctx.classes_done is the count we reached; the LAST
    // "emit: -> class[..]" / "emit: -> enum[..]" line printed before this is the
    // culprit. Keep the partial output and let RunWalk write it (exit 0).
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] emit: 2023 emit FAULTED after %d classes / %d "
                   "enums — emitting PARTIAL (%d classes, %d enums in output)\n",
                   emit_ctx.classes_done, emit_ctx.enums_done,
                   out->classes_size(), out->enums_size());
      std::fflush(stderr);
    }
    return true;  // partial artifact is intentional 2023 diagnostic containment.
  }
  // DoSchemaEmit ran to completion under the guard. On 2023 a "modern fail-loud"
  // abort cannot happen (every entry carries k2023), so emit_ctx.ok is true here;
  // guard against a future change by surfacing a genuine abort if one ever occurs.
  if (!emit_ctx.ok && emit_ctx.aborted_modern) {
    return false;  // *err set (defensive; not reachable on the pure-2023 path).
  }
  return true;
}

// Runtime era detection (see RecordsValidateAsEra). Probes the live class records of
// a well-known module scope and reports whether they are in the 2023 layout. Called
// by the loader AFTER the post-boot "SchemaSystem_001" fallback registers, so the
// schema is populated.
//
// TWO-WAY check (this is the fix for the false-positive that mis-classified the
// 0da05cff-pin era as 2023): a 2023-only test is not enough — interpreting a MODERN
// build's records through the bounded 2023 pool-blob walk can recover a handful of
// blobs that incidentally pass the permissive 2023 validity test. So per probe scope
// we try BOTH interpretations and let MODERN win:
//   - if the MODERN interpretation validates  -> build is modern  (return false),
//   - else if the 2023 interpretation validates -> build is 2023 (return true),
//   - else -> inconclusive for this scope, try the next probe module.
// Modern is tried first and short-circuits, so we NEVER apply 2023 offsets to a
// build whose records read cleanly as modern (keeps modern byte-identical).
// Both reads are fault-safe: the 2023 pool walk is bounded + SEH-guarded internally,
// and the modern compiled path is wrapped in RecordsValidateAsModernGuarded.
// Build-level confirmation of the variant-0 (2023) offset table: walk EVERY scope under
// the k2023 reader, sum the class count, and note whether CBaseEntity is present. This
// is the stronger acceptance the N-way probe applies before selecting variant 0 — it
// rejects V1's class-dead read (0 classes, no CBaseEntity) that a single scope's
// permissive RecordsValidateAsEra could not distinguish alone. Every read is the same
// bounded + SEH-guarded k2023 pool walk the extraction uses, so it never faults.
// Scope set for the PRE-2024 LAYOUT VALIDATION reads only: the game/engine probe
// scopes (server/client/engine2), NOT CollectTypeScopes. The validation's
// calibration — CBaseEntity present + the class-count floors/bands — was derived
// from these scopes, and they are where CBaseEntity actually lives. Since 0.5.0
// CollectTypeScopes also yields the global "!GlobalTypes" scope (plus the wider
// module set's scopes); feeding those into the bounded 2023 pool-recovery read
// perturbs the validator (measured on V1 build 13240071: 655 recovered records,
// CBaseEntity absent -> spurious exit 75), while the post-detection WALK handles
// them fine (V0 build 12147839 emits 2704 classes). Detection basis and walk
// basis are deliberately different sets.
std::vector<CSchemaSystemTypeScope*> CollectProbeScopes(CSchemaSystem* system) {
  static const char* const kProbeScopeModules[] = {
      "server.dll",
      "client.dll",
      "engine2.dll",
      "libserver.so",
      "libclient.so",
      "libengine2.so",
  };
  std::vector<CSchemaSystemTypeScope*> scopes;
  CSchemaSystemTypeScope* const global_scope = system->GlobalTypeScope();
  for (const char* mod : kProbeScopeModules) {
    CSchemaSystemTypeScope* s = system->FindTypeScopeForModule(mod);
    if (s == nullptr) continue;
    if (s == global_scope) continue;  // fallback resolution; not a probe scope.
    bool seen = false;
    for (CSchemaSystemTypeScope* existing : scopes) {
      if (existing == s) {
        seen = true;
        break;
      }
    }
    if (!seen) scopes.push_back(s);
  }
  return scopes;
}

static void Validate2023BuildLevel(CSchemaSystem* system,
                                   const InProcessEnvironment& env, int* total_out,
                                   bool* has_cbaseentity_out) {
  *total_out = 0;
  *has_cbaseentity_out = false;
  (void)env;  // era comes from the fixed k2023 reads below; env kept for call-site symmetry.
  std::vector<CSchemaSystemTypeScope*> scopes = CollectProbeScopes(system);
  for (CSchemaSystemTypeScope* scope : scopes) {
    if (scope == nullptr) continue;
    std::vector<CSchemaClassInfo*> classes = ReadScopeClasses(scope, Era::k2023, false);
    *total_out += static_cast<int>(classes.size());
    if (!*has_cbaseentity_out) {
      for (CSchemaClassInfo* ci : classes) {
        if (ci == nullptr) continue;
        if (Str(rec::ClassName(ci, Era::k2023), Era::k2023) == "CBaseEntity") {
          *has_cbaseentity_out = true;
          break;
        }
      }
    }
  }
}

// N-way runtime layout probe. Generalizes the former single-shot
// DetectSchemaIs2023Layout into a three-way classification (see SchemaLayoutVariant):
//
//   1. MODERN first (SEH-guarded compiled path). If any probe scope validates as
//      modern -> kModern, short-circuit. A modern build (and a modern build whose boot
//      merely left the schema empty, re-registered by the SchemaSystem_001 fallback)
//      returns here, so its output is byte-identical — UNCHANGED from before.
//   2. Else, if a probe scope validates under the variant-0 (2023) offset table,
//      CONFIRM at build level (CBaseEntity present AND total class count >= a floor, to
//      reject V1's class-dead read), compute the variant-0 runtime signature, and check
//      it against the allow-list. If known -> kKnownRuntimeVariant (is_2023
//      offsets). Build 10832117 and its whole V0 family land here == the prior `true`
//      path, so env.set_schema_is_2023_era(true) is selected exactly as before.
//   3. Else -> kUnknown. The live records fit NEITHER modern NOR any known runtime
//      variant (e.g. a V1 build). The caller MUST fail loud and print the observed
//      runtime signature to stderr — never guess, never emit 0 classes silently.
//      This CLOSES the latent hole where such a build previously fell through to
//      the modern path.
//
// Both reads are fault-safe: the 2023 pool walk is bounded + SEH-guarded internally,
// and the modern compiled path is wrapped in RecordsValidateAsModernGuarded.
SchemaVariantProbe DetectSchemaVariant(const InProcessEnvironment& env) {
  SchemaVariantProbe result;
  auto* system = reinterpret_cast<CSchemaSystem*>(env.schema_system());
  if (system == nullptr) {
    // No live system: keep the modern default so the loader's existing post-walk
    // schema-empty gate (not this probe) surfaces the structural failure.
    result.variant = SchemaLayoutVariant::kModern;
    return result;
  }
  const bool trace = (std::getenv("CS2_WALKER_TRACE") != nullptr);
  // The schema-bearing modules whose scope reliably holds many real classes. Probe
  // by FULL filename (what FindTypeScopeForModule is keyed on), both platforms.
  static const char* const kProbeModules[] = {
      "server.dll",
      "client.dll",
      "engine2.dll",
      "libserver.so",
      "libclient.so",
      "libengine2.so",
  };
  bool saw_variant0_candidate = false;
  for (const char* mod : kProbeModules) {
    CSchemaSystemTypeScope* scope = system->FindTypeScopeForModule(mod);
    if (scope == nullptr) continue;
    // MODERN first (SEH-guarded compiled path). If it validates, the build is modern.
    if (RecordsValidateAsModernGuarded(scope)) {
      if (trace) {
        std::fprintf(stderr,
                     "[walker-trace] era-detect: scope '%s' records validate as MODERN "
                     "layout -> build is modern\n",
                     mod);
        std::fflush(stderr);
      }
      result.variant = SchemaLayoutVariant::kModern;
      return result;
    }
    // Modern failed for this scope; try the bounded/guarded variant-0 (2023) read.
    if (RecordsValidateAsEra(ReadScopeClasses(scope, Era::k2023, false), Era::k2023)) {
      if (trace) {
        std::fprintf(stderr,
                     "[walker-trace] era-detect: scope '%s' records validate under the "
                     "variant-0 (2023) offset table (modern did not) -> confirming at "
                     "build level\n",
                     mod);
        std::fflush(stderr);
      }
      saw_variant0_candidate = true;
      break;
    }
    // Neither interpretation validated for this scope — inconclusive, keep probing.
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] era-detect: scope '%s' validated as neither modern "
                   "nor variant-0 -> inconclusive, trying next probe module\n",
                   mod);
      std::fflush(stderr);
    }
  }

  // Build-level confirmation + allow-list gate. runtime_signature is the variant-0
  // candidate signature (a pure function of the compiled-in offset table, so it is the
  // signature we TRIED regardless of the live DLL). observed_* localize a fail-loud.
  Validate2023BuildLevel(system, env, &result.observed_class_count,
                         &result.observed_cbaseentity);
  result.runtime_signature = ComputeRuntimeLayoutSignature();

  constexpr int kVariant0ClassFloor = 900;  // rejects V1's class-dead read (~0 classes)
  const bool variant0_validates = saw_variant0_candidate &&
                                  result.observed_cbaseentity &&
                                  result.observed_class_count >= kVariant0ClassFloor;
  if (variant0_validates &&
      IsKnownRuntimeLayoutVariant(result.runtime_signature)) {
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] era-detect: variant-0 (cs2-2023-03-22) confirmed — %d classes, "
                   "CBaseEntity present, runtime signature %s IS allow-listed -> "
                   "kKnownRuntimeVariant (2023 offsets)\n",
                   result.observed_class_count, result.runtime_signature.c_str());
      std::fflush(stderr);
    }
    result.variant = SchemaLayoutVariant::kKnownRuntimeVariant;
    result.is_2023_offsets = true;
    return result;
  }

  // ---- V1+ : try each fully_derived() NON-V0 pre-2024 runtime layout table ----------
  // We reach here because V0 did NOT validate: a V1 build reads ~0 classes under the V0
  // shift (+8), so variant0_validates is false. (A modern build short-circuited above; a
  // V0/current build returned above — so this loop NEVER runs on them: output preserved.)
  // For each derived non-V0 table, TRY its container geometry (real_base shift), read at
  // build level, and select it iff CBaseEntity is present, the class count is in band, AND
  // its struct signature is allow-listed. The active shift stays set to the winner
  // so the subsequent extraction walk uses it; if none match, restore the V0 default.
  struct DerivedVariant {
    const prelayout::Pre2024LayoutOffsets* table;
    const char* tag;   // ComputeRuntimeLayoutSignatureFor prefix (re-<tag>/v1/<hex>)
    const char* name;  // trace label
  };
  static const DerivedVariant kDerivedVariants[] = {
      {&prelayout::kV1, "cs2rel", "cs2-2023-09-13"},
  };
  // Band: reject the V0-empty read (~0) and an obviously-garbage huge read, while admitting
  // every measured V1 tier (server+client+engine2 totals: V1-a 1060, V1-b-early 999,
  // V1-b-late 1016). Floor mirrors the V0 floor (>=900 + CBaseEntity); the real gate
  // is the allow-listed signature below. [The tighter host band [1000,1120] is applied by
  // the host on the EMITTED class count, not here.]
  constexpr int kV1ClassFloor = 900;
  constexpr int kV1ClassCeil = 1200;
  for (const DerivedVariant& dv : kDerivedVariants) {
    if (!dv.table->fully_derived()) continue;         // never offer an incomplete table
    tshash_compat::SetActivePre2024Layout(dv.table);  // TRY this table's container geometry
    int v_classes = 0;
    bool v_cbe = false;
    Validate2023BuildLevel(system, env, &v_classes, &v_cbe);
    const std::string sig = ComputeRuntimeLayoutSignatureFor(*dv.table, dv.tag);
    const bool reads_ok =
        v_cbe && v_classes >= kV1ClassFloor && v_classes <= kV1ClassCeil;
    if (reads_ok && IsKnownRuntimeLayoutVariant(sig)) {
      if (trace) {
        std::fprintf(stderr,
                     "[walker-trace] era-detect: %s confirmed — %d classes, CBaseEntity "
                     "present, runtime signature %s IS allow-listed -> kKnownRuntimeVariant "
                     "(2023 record offsets, real_base shift %lld)\n",
                     dv.name, v_classes, sig.c_str(),
                     static_cast<long long>(dv.table->real_base_shift));
        std::fflush(stderr);
      }
      // Keep the active shift SET to the winning table so extraction reads compiled+shift.
      result.variant = SchemaLayoutVariant::kKnownRuntimeVariant;
      result.is_2023_offsets = true;  // V1 uses the SAME 2023 record-head offsets as V0.
      result.runtime_signature = sig;
      result.observed_class_count = v_classes;
      result.observed_cbaseentity = v_cbe;
      return result;
    }
    if (trace) {
      std::fprintf(stderr,
                   "[walker-trace] era-detect: %s did NOT confirm (read %d classes, "
                   "CBaseEntity %s; signature %s %s allow-listed) -> trying next table\n",
                   dv.name, v_classes, v_cbe ? "present" : "absent", sig.c_str(),
                   IsKnownRuntimeLayoutVariant(sig) ? "IS" : "is NOT");
      std::fflush(stderr);
    }
  }
  // No derived non-V0 variant matched — restore the V0 default shift so nothing downstream
  // reads at a stale V1 base (we fail loud below regardless, but keep the global clean).
  tshash_compat::SetActivePre2024Layout(&prelayout::kVariant0);

  // The live records fit NEITHER modern NOR any KNOWN runtime layout variant.
  if (trace) {
    std::fprintf(stderr,
                 "[walker-trace] era-detect: NO known layout matched (modern failed; "
                 "variant-0 read %d classes, CBaseEntity=%s; runtime signature %s %s "
                 "allow-listed) -> kUnknown (fail-loud)\n",
                 result.observed_class_count,
                 result.observed_cbaseentity ? "present" : "absent",
                 result.runtime_signature.c_str(),
                 IsKnownRuntimeLayoutVariant(result.runtime_signature) ? "IS"
                                                                       : "is NOT");
    std::fflush(stderr);
  }
  result.variant = SchemaLayoutVariant::kUnknown;
  return result;
}

bool EnumerateLiveSchemaSymbols(const InProcessEnvironment& env,
                                std::vector<SchemaSymbolRef>* classes_out,
                                std::vector<SchemaSymbolRef>* enums_out,
                                std::string* err) {
  classes_out->clear();
  enums_out->clear();

  auto* system = reinterpret_cast<CSchemaSystem*>(env.schema_system());
  if (system == nullptr) {
    // Same structural failure WalkSchemaSystem reports.
    *err = "schema universe: null CSchemaSystem (loader did not populate it)";
    return false;
  }

  // Mirror WalkSchemaSystem's scope/binding traversal EXACTLY (same ERA-STABLE
  // vtable scope enumeration, same accessors, same null tolerance), but capture
  // only (name, module) per binding. We deliberately do NOT call
  // LoadSchemaDataForModules here: RunWalk runs WalkSchemaSystem before this,
  // which already drove registration when needed.
  std::vector<CSchemaSystemTypeScope*> scopes = CollectTypeScopes(system, env);

  const bool trace = (std::getenv("CS2_WALKER_TRACE") != nullptr);

  // BUILD-LEVEL era — MUST match WalkSchemaSystem exactly (so the universe key
  // set == the extraction key set). Same authoritative env flag, applied to all
  // scopes. Modern: kModern everywhere -> byte-identical with the extraction keys.
  const Era build_era = BuildEra(env);

  for (CSchemaSystemTypeScope* scope : scopes) {
    if (scope == nullptr) continue;  // defensive; CollectTypeScopes drops nulls.

    // ERA-GATED enumeration — MUST match WalkSchemaSystem exactly. Same build-level
    // era, same per-scope readers, same null tolerance. ClassKey/EnumKey derive
    // (name, module) with the SAME era-gated accessors EmitClass/EmitEnum use, so
    // the universe key == the artifact key on every era, including the 2023 fallback.
    const Era era = build_era;
    for (CSchemaClassInfo* ci : ReadScopeClasses(scope, era, trace)) {
      classes_out->push_back(ClassKey(ci, era));
    }
    for (CSchemaEnumInfo* ei : ReadScopeEnums(scope, era, trace)) {
      enums_out->push_back(EnumKey(ei, era));
    }
  }

  return true;
}

bool EnumerateLiveEnumeratorConstants(
    const InProcessEnvironment& env,
    std::vector<EnumeratorConstantRef>* out, std::string* err) {
  out->clear();

  auto* system = reinterpret_cast<CSchemaSystem*>(env.schema_system());
  if (system == nullptr) {
    // Same structural failure WalkSchemaSystem / EnumerateLiveSchemaSymbols
    // report (the engine-constants + universe callers both surface it).
    *err = "engine-constants walk: null CSchemaSystem (loader did not populate it)";
    return false;
  }

  // ERA-STABLE scope enumeration (VTABLE-only; CollectTypeScopes) — NOT the
  // m_TypeScopes data member, whose compiled offset drifts on the 2023 baseline.
  // This is the SAME scope set WalkSchemaSystem walks.
  std::vector<CSchemaSystemTypeScope*> scopes = CollectTypeScopes(system, env);

  const bool trace = (std::getenv("CS2_WALKER_TRACE") != nullptr);

  // BUILD-LEVEL era — MUST match WalkSchemaSystem/EmitEnum exactly so the
  // constant set and the universe agree with the extraction. On k2023 the
  // era-gated enum reader returns EMPTY (documented 2023 enum-pool gap), so this
  // loop yields zero constants WITHOUT touching an un-located pool -> no fault.
  //
  // NOTE: module here is the SCOPE name (ScopeName(scope, era)), NOT the enum's
  // EnumTypeScope; this loop intentionally does NOT use EnumKey (which would read
  // rec::EnumTypeScope) — only BuildEra and the shared enum-name read are shared.
  const Era build_era = BuildEra(env);

  for (CSchemaSystemTypeScope* scope : scopes) {
    if (scope == nullptr) continue;  // defensive; CollectTypeScopes drops nulls.
    const Era era = build_era;
    const std::string module = ScopeName(scope, era);

    // ERA-GATED enum-binding read (ReadScopeEnums): kModern reads the compiled
    // CUtlTSHash; k2023 returns EMPTY (typescope_sub==0 -> 2023 enum gap). Either
    // way we only iterate VALID enum infos.
    for (CSchemaEnumInfo* ei : ReadScopeEnums(scope, era, trace)) {
      if (ei == nullptr) continue;
      const std::string enum_name = Str(rec::EnumName(ei, era), era);

      // ERA-GATED enumerator array + count (same accessors EmitEnum uses).
      const SchemaEnumeratorInfoData_t* enumerators = rec::EnumEnumerators(ei, era);
      const int enum_count = rec::EnumCount(ei, era);
      if (enumerators == nullptr || enum_count <= 0) continue;
      for (int e = 0; e < enum_count; ++e) {
        const SchemaEnumeratorInfoData_t* en = rec::EnumeratorAt(enumerators, e, era);
        const std::string member = Str(rec::EnumeratorName(en, era), era);
        // Hard rule: the binary must NAME the constant; skip the unnamed.
        if (member.empty()) continue;
        out->push_back({enum_name, member, module,
                        static_cast<long long>(rec::EnumeratorValue(en, era))});
      }
    }
  }
  return true;
}

}  // namespace cs2_schema_walker
