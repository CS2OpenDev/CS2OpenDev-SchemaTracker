// Engine constants (named int/string constants the binary names). See
// engine_constants_walk.h.
//
// Read from the schema-enumerator surface: every registered CSchemaEnumInfo's
// enumerators are binary-named integer constants reachable through the same
// CSchemaSystem object graph schema_walk.cpp walks. We do NOT infer any name —
// every emitted constant's name + value come straight off
// SchemaEnumeratorInfoData_t (m_pszName / m_nValue), and `source` ties the row
// back to the enum binding (module + enum name) so the registry audit can reach
// it.
//
// Why enumerators (and not, say, the schema string metadata blobs): the hard rule
// is "only constants the binary itself names." Enumerators carry an explicit
// binary name AND an explicit value in a typed field — no guessing the encoding.
// Schema metadata m_pData blobs are name-dependent opaque pointers whose value
// encoding the walker deliberately does NOT dereference (see schema_walk.cpp
// EmitMetadata), so they are not a safe source of named *values* here.
#include "engine_constants_walk.h"

#include "loader.h"
#include "schema_walk.h"  // EnumerateLiveEnumeratorConstants — the ERA-STABLE source

#include "engine_constants.pb.h"
#include "walker_output.pb.h"

#include <algorithm>
#include <cstdint>
#include <string>
#include <vector>

namespace wpb = cs2::schema_tracker::v0;

namespace cs2_schema_walker {

namespace {

// The constant NAME for an enumerator: "<Enum>::<Member>", or just the member
// when the enum is unnamed. Both halves are binary-provided (nothing inferred).
// Centralized so the extraction emit and the universe build the identical name
// and cannot drift on the scoping form.
std::string ConstantName(const std::string& enum_name,
                         const std::string& member) {
  return enum_name.empty() ? member : (enum_name + "::" + member);
}

// Visit every binary-named enumerator constant in the live schema system,
// invoking `sink(name, enum_name, module, value)` per constant. Both
// WalkEngineConstants (extraction) and EnumerateLiveEngineConstants (universe)
// drive it, so their keys are guaranteed identical.
//
// ERA-STABLE: the actual schema-scope/enum traversal is delegated to
// schema_walk.cpp's EnumerateLiveEnumeratorConstants, which uses the SAME
// vtable-based CollectTypeScopes + build-era-gated ReadScopeEnums + rec2023
// enumerator accessors as WalkSchemaSystem/EmitEnum. This TU therefore reads NO
// schema layout directly (no more m_TypeScopes / m_EnumBindings at compiled
// offsets — those drift on the 2023 baseline and previously faulted here). On the
// 2023 build era the enum reader returns EMPTY by design (open 2023 enum-pool
// gap), so this yields ZERO engine constants WITHOUT faulting — legitimately empty
// engine_constants on 2023 for now, never a crash. On modern the scope set +
// accessors are identical to the old direct walk, so the constant set is
// byte-identical. `module` is the owning scope name — exactly the value the host
// recovers by parsing EngineConstant.source ("schema_enum:<module>/..").
// Returns false + sets *err on a structural failure (null CSchemaSystem).
template <typename Sink>
bool ForEachLiveConstant(const InProcessEnvironment& env, Sink&& sink,
                         std::string* err) {
  std::vector<EnumeratorConstantRef> constants;
  if (!EnumerateLiveEnumeratorConstants(env, &constants, err)) {
    return false;  // structural (null CSchemaSystem)
  }
  for (const EnumeratorConstantRef& c : constants) {
    sink(ConstantName(c.enum_name, c.member), c.enum_name, c.module,
         static_cast<int64_t>(c.value));
  }
  return true;
}

}  // namespace

bool WalkEngineConstants(const InProcessEnvironment& env,
                         wpb::EngineConstantsWalk* out, std::string* err) {
  out->Clear();

  // The schema walk already drives LoadSchemaDataForModules when the type-scope
  // set is empty; by the time RunWalk reaches us that has run, so we simply
  // enumerate whatever scopes are registered. An empty set is valid (yields zero
  // constants), not corruption.

  // (name, source, value) accumulated across every scope, then sorted for
  // determinism.
  struct Constant {
    std::string name;
    std::string source;
    int64_t value;
  };
  std::vector<Constant> constants;

  const bool ok = ForEachLiveConstant(
      env,
      [&constants](const std::string& name, const std::string& enum_name,
                   const std::string& module, int64_t value) {
        Constant c;
        c.name = name;
        // The source ties this row back to the enum binding it came from so the
        // audit can reach it; non-empty per the engine_constants.proto rule.
        // The host re-derives the module by parsing this exact source string.
        c.source = "schema_enum:" + module + "/" + enum_name;
        c.value = value;
        constants.push_back(std::move(c));
      },
      err);
  if (!ok) return false;

  // Stable order: (name, source). Pointer/hash order must not leak.
  std::sort(constants.begin(), constants.end(),
            [](const Constant& a, const Constant& b) {
              if (a.name != b.name) return a.name < b.name;
              return a.source < b.source;
            });

  for (const auto& c : constants) {
    auto* ec = out->add_constants();
    ec->set_name(c.name);
    ec->set_source(c.source);
    ec->set_int_value(c.value);
  }
  return true;
}

bool EnumerateLiveEngineConstants(const InProcessEnvironment& env,
                                  std::vector<EngineConstantRef>* out,
                                  std::string* err) {
  out->clear();
  // Same traversal + same name derivation as WalkEngineConstants, but the
  // emitted module is the PARSED owning module directly (the scope name) — which
  // is byte-for-byte what the host's ModuleFromConstantSource recovers from the
  // "schema_enum:<module>/<EnumName>" source the extraction emits. So the
  // universe key == the host's audit key after its source-parse.
  return ForEachLiveConstant(
      env,
      [out](const std::string& name, const std::string& /*enum_name*/,
            const std::string& module, int64_t /*value*/) {
        out->push_back({name, module});
      },
      err);
}

}  // namespace cs2_schema_walker
