// CSchemaSystem object-graph traversal.
//
// Given a live, fully-registered in-process environment (loader.h), walks every
// registered type scope -> classes/structs/enums, and per class the fields
// (name, offset, recursive SchemaType), parent chain, and reflection metadata
// (carrying the RAW KV3 MGetKV3ClassDefaults string verbatim — no KV3 parse here;
// the host does the structural parse), and emits the result into the
// EntitySchemaWalk message.
//
// Determinism: every collection is sorted by a stable key before it is added to
// the proto (classes/enums by name Ordinal; fields by offset then name; parents
// by offset then name; enum members by value then name; metadata by name then
// index). The live schema-system hash maps have undefined iteration order, which
// we MUST NOT inherit into the output.
//
// Fail-loud: any structural inconsistency (null scope vector, a class binding
// whose data pointer is null, etc.) returns false + sets *err. The caller writes
// ZERO output bytes on failure.
//
// This is the ONE TU besides layout_probe.cpp that includes sdk_schema.h, so the
// HL2SDK coupling stays localized.
#pragma once

#include <string>
#include <vector>

// Forward-declare the proto message so this header has no protobuf include.
namespace cs2 {
namespace schema_tracker {
namespace v0 {
class EntitySchemaWalk;
}
}  // namespace schema_tracker
}  // namespace cs2

namespace cs2_schema_walker {

class InProcessEnvironment;

// Walk the live schema system reachable from `env` into `out`. `out` is cleared
// first. Returns true on success; on failure sets *err and leaves `out` in an
// unspecified (to-be-discarded) state — the caller must not serialize it.
bool WalkSchemaSystem(const InProcessEnvironment& env,
                      cs2::schema_tracker::v0::EntitySchemaWalk* out,
                      std::string* err);

// A live schema symbol (class or enum) named exactly as WalkSchemaSystem would
// emit it: `name` == SchemaClass/SchemaEnum.name, `module` == the same value
// WalkSchemaSystem stamps into SchemaClass/SchemaEnum.module (the owning type
// scope's name). registry_universe enumerates these INDEPENDENTLY of the
// extraction proto-assembly so a dropped artifact still surfaces as a gap.
struct SchemaSymbolRef {
  std::string name;
  std::string module;
};

// Enumerate EVERY registered class info (`classes_out`) and enum info
// (`enums_out`) in the live CSchemaSystem reachable from `env`, deriving each
// (name, module) with the SAME low-level readers WalkSchemaSystem uses, so the
// universe keys cannot drift from the extraction keys. Both vectors are cleared
// first; order is unspecified (the universe sorts deterministically itself).
//
// This is the SAME live object graph WalkSchemaSystem reads; it does NOT trigger
// LoadSchemaDataForModules (RunWalk runs WalkSchemaSystem first, which already
// drives registration). Fail-loud: a null CSchemaSystem is structural (-> false +
// *err). An empty schema system yields empty vectors and is valid.
bool EnumerateLiveSchemaSymbols(const InProcessEnvironment& env,
                                std::vector<SchemaSymbolRef>* classes_out,
                                std::vector<SchemaSymbolRef>* enums_out,
                                std::string* err);

// A single binary-named schema enumerator constant, read through the SAME
// era-stable scope enumeration (CollectTypeScopes via the ISchemaSystem vtable)
// and ERA-GATED enum/enumerator accessors WalkSchemaSystem/EmitEnum use. This is
// the ONE place the walker turns the live schema enum graph into named integer
// constants for both engine_constants extraction AND registry_universe; both
// consumers go through it so their era handling and (name, module, value)
// derivation cannot drift.
//
//   enum_name = CSchemaEnumInfo name        (era-gated; "" for an unnamed enum)
//   member    = SchemaEnumeratorInfoData_t name (era-gated; never empty — the
//               "binary must name the constant" filter is applied here)
//   module    = owning type-scope name      (era-gated EnumTypeScope -> ScopeName)
//   value     = SchemaEnumeratorInfoData_t value (era-gated int64)
//
// 2023 NOTE: on the 2023 build era the enum pool location is an OPEN GAP — the
// era-gated enum reader (ReadScopeEnums / ReadBindings2023 with typescope_sub==0)
// returns EMPTY by design (see tshash_compat.h), so this yields ZERO constants on
// 2023 WITHOUT faulting (it never reaches the un-located enum pool). Engine
// constants are therefore legitimately empty on 2023 for now (NOT corruption);
// when the 2023 enum pool is located, this path picks them up automatically.
//
// MODERN: identical scope set + identical accessors as the old direct
// m_TypeScopes/m_EnumBindings walk produced, so the constant set is byte-identical.
// Fail-loud: a null CSchemaSystem is structural (-> false + *err).
struct EnumeratorConstantRef {
  std::string enum_name;
  std::string member;
  std::string module;
  long long value;
};
bool EnumerateLiveEnumeratorConstants(
    const InProcessEnvironment& env,
    std::vector<EnumeratorConstantRef>* out, std::string* err);

// ERA-STABLE schema-empty probe for the POST-BOOT registration retry (see
// loader.h::RetrySchemaRegistrationIfEmpty). Returns true iff NO module type
// scope is registered in the live schema system reachable from `env`.
//
// WHY A SEPARATE PROBE (NOT m_TypeScopes.GetNumStrings()): the compiled
// m_TypeScopes data-member offset DRIFTS across eras (modern +0x190 vs the 2023
// baseline +0x198), so a raw compiled-offset read mis-reads the 2023
// pre-registration state. This probe instead uses ONLY vtable dispatch —
// ISchemaSystem::GlobalTypeScope() and ::FindTypeScopeForModule(name) — which is
// layout-STABLE across every CS2 era the loader admits: it returns a pointer (or
// null) and reads NO data member whose offset could drift. A module scope exists
// (non-null, distinct from the global scope) iff that module registered its
// bindings. Zero such scopes across all loaded modules == empty.
//
// Reads 0 on the 2023 pre-registration state (no module scope yet) AND >0 on a
// modern post-boot state (scopes already populated) — exactly the gate the retry
// needs so modern stays byte-identical (the retry is skipped on modern).
// A null schema_system is treated as empty (the caller's retry then runs; a
// genuinely null schema system still fails loud downstream as it does today).
bool SchemaSystemIsEmpty(const InProcessEnvironment& env);

// Which schema-system runtime layout the live DLLs present. This is the N-way
// generalization of the old single-shot 2023-vs-modern probe (the gate for the
// pre-2024 runtime layout variants):
//   kModern              - records validate under the compiled b8dcaf14 layout.
//   kKnownRuntimeVariant - records validate under a KNOWN pre-2024 runtime offset
//                          table (currently only variant 0, the 2023 table), AND that
//                          table's ComputeRuntimeLayoutSignature() is allow-listed.
//   kUnknown             - records match NEITHER modern NOR any known runtime variant.
//                          The caller MUST fail loud (never guess / never walk).
enum class SchemaLayoutVariant { kModern,
                                 kKnownRuntimeVariant,
                                 kUnknown };

struct SchemaVariantProbe {
  SchemaLayoutVariant variant = SchemaLayoutVariant::kModern;
  // True iff kKnownRuntimeVariant used the variant-0 (2023) offset table -> the walk
  // routes through Era::k2023 (env.set_schema_is_2023_era(true)). False on kModern.
  bool is_2023_offsets = false;
  // The variant-0 runtime-layout signature that was TRIED (the only derived table). On
  // kKnownRuntimeVariant it is the matched signature; on kUnknown it is the signature
  // that did NOT validate — the observed runtime signature the caller prints to stderr.
  std::string runtime_signature;
  // Build-level diagnostics observed under the variant-0 table (for the fail-loud msg).
  int observed_class_count = -1;
  bool observed_cbaseentity = false;
};

// Runtime layout detection: probes the live class records of well-known module scopes
// and classifies the build's layout (see SchemaLayoutVariant). Fault-safe — the modern
// interpretation is SEH-guarded and the 2023 interpretation is bounded + SEH-guarded.
// Intended to be called AFTER schema registration so scopes are populated. Modern is
// tried FIRST and short-circuits, so a modern build is classified kModern and stays
// byte-identical; build 10832117 (and its V0 family) classify as
// kKnownRuntimeVariant (variant 0); a pre-2024 build whose records fit NO known table
// (e.g. V1) classifies kUnknown so the caller fails loud.
SchemaVariantProbe DetectSchemaVariant(const InProcessEnvironment& env);

}  // namespace cs2_schema_walker
