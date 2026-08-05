// Engine constants extraction (named integer/string constants the binary itself
// names, never inferred).
//
// Source (what is cleanly reachable from the pinned HL2SDK schema-system graph):
// the schema enumerators. Every CSchemaEnumInfo the schema system registers
// carries a set of SchemaEnumeratorInfoData_t entries, each with a binary-provided
// name (m_pszName) and an int64 value (m_nValue) — precisely "named integer
// constants exposed via the schema system": the name is the symbol the binary
// itself emitted into the schema enum binding, never inferred, and the value is
// carried verbatim. Each emitted EngineConstant therefore carries:
//   - name   = "<EnumName>::<MemberName>"   (scoped so it is unique + traceable)
//   - source = "schema_enum:<module>/<EnumName>"  (the registry/pool it came from,
//              so the registry audit can reach this row as `extracted`)
//   - int_value = m_nValue
//
// This is the same object graph schema_walk.cpp already traverses, read through
// the same pinned headers (sdk_schema.h) — no layout is re-declared.
//
// Determinism: constants are sorted by (name, source) before they are added to
// the proto; the schema-system hash maps have undefined iteration order which
// MUST NOT leak into the output.
//
// Fail-loud: a null CSchemaSystem is a STRUCTURAL failure (-> false + *err). An
// empty-but-valid schema system (no enums registered) yields zero constants and
// is NOT corruption.
#pragma once

#include <string>
#include <vector>

// Forward-declare the proto message so this header has no protobuf include.
namespace cs2 {
namespace schema_tracker {
namespace v0 {
class EngineConstantsWalk;
}
}  // namespace schema_tracker
}  // namespace cs2

namespace cs2_schema_walker {

class InProcessEnvironment;

// Walk the live schema system reachable from `env` for named constants into
// `out`. `out` is cleared first. Returns true on success; on failure sets *err
// and leaves `out` in an unspecified (to-be-discarded) state.
bool WalkEngineConstants(const InProcessEnvironment& env,
                         cs2::schema_tracker::v0::EngineConstantsWalk* out,
                         std::string* err);

// A live engine-constant symbol named exactly as the host derives its audit key
// from an EngineConstant artifact: `name` == EngineConstant.name (the
// "<Enum>::<Member>" form), `module` == the host's ModuleFromConstantSource of
// EngineConstant.source — i.e. the PARSED owning module (e.g. "server.dll"), NOT
// the raw "schema_enum:..." source. (This exact parsed-vs-raw mismatch has bitten
// before.) registry_universe enumerates these INDEPENDENTLY of the extraction.
struct EngineConstantRef {
  std::string name;
  std::string module;
};

// Enumerate EVERY binary-named schema enumerator constant in the live schema
// system reachable from `env`, deriving each (name, module) with the SAME
// readers WalkEngineConstants uses (so the universe key == the artifact key
// after the host's source-parse). `out` is cleared first; order is unspecified
// (the universe sorts deterministically itself). Fail-loud: a null CSchemaSystem
// is structural (-> false + *err); an empty schema system yields an empty vector
// and is valid.
bool EnumerateLiveEngineConstants(const InProcessEnvironment& env,
                                  std::vector<EngineConstantRef>* out,
                                  std::string* err);

}  // namespace cs2_schema_walker
