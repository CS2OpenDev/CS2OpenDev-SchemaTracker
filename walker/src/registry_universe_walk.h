// Observed-registry-symbol universe.
//
// Emits the FULL universe of named registry symbols the walker can OBSERVE in
// the loaded binaries' LIVE systems — every symbol, whether or not it was
// extracted into an artifact. The host cross-references this universe against the
// produced artifacts to synthesize registry_audit.json: a symbol in the universe
// but absent from every artifact becomes an `Omitted` row (silent drops are
// forbidden).
//
// INDEPENDENT live traversal (not derived from extraction)
// --------------------------------------------------------
// The universe is built from a SEPARATE traversal of the live Source 2 systems —
// the SAME systems the extraction walks read, but enumerated here independently:
//   - the live CSchemaSystem scopes  -> ALL registered class infos + enum infos
//   - the live ICvar registry        -> ALL registered convars + commands
//   - the live schema enumerators    -> ALL binary-named engine constants
// It deliberately does NOT read the already-built extraction sub-messages. If the
// extraction proto-assembly ever DROPS a symbol the live registry actually has,
// the universe still contains it and the host mints a real Omitted row — the
// completeness audit is therefore a genuine check, not circular.
//
// universe >= extracted (both read the same live systems)
// -------------------------------------------------------
// The universe enumerates ALL live registrations; the extraction reads the same
// systems, so the universe is a superset-or-equal of everything extracted. To
// guarantee the KEYS never drift, the independent traversal REUSES the exact
// low-level name/module derivation the extraction walks use, via shared
// enumeration helpers exported from schema_walk / cvar_walk / engine_constants_walk:
//   - EnumerateLiveSchemaSymbols       (schema_class / schema_enum keys)
//   - EnumerateLiveConVarAndCommandNames (convar / command keys)
//   - EnumerateLiveEngineConstants     (engine_constant keys, parsed module)
//
// CRITICAL key alignment with the host's RegistryAuditEmitter:
//   - schema class/enum: symbol = name, module = owning-scope name (verbatim, ==
//     SchemaClass/SchemaEnum.module the extraction emits).
//   - convar/command:    symbol = name, module = "" (these shapes carry no module).
//   - engine_constant:   symbol = "<Enum>::<Member>", module = the PARSED owning
//     module (e.g. "server.dll") — the same value the host's
//     ModuleFromConstantSource recovers from EngineConstant.source. NOT the raw
//     "schema_enum:..." source.
//
// Categories emitted: schema_class, schema_enum, engine_constant, convar, command.
// Categories DEFERRED (the walker has no reachable registry for these, so NOTHING
// is emitted — never fabricated): network_message, string_pool.
//
// Determinism: symbols are sorted by (category, module, symbol) Ordinal before
// emission, yielding a stable total order independent of live iteration.
//
// Fail-loud: a null CSchemaSystem / null ICvar is a STRUCTURAL failure
// (-> false + *err), surfaced via the shared enumeration helpers. (In practice
// the extraction walks already aborted on these before this step runs, but the
// independent traversal carries the same contract directly.)
//
// This TU includes NO HL2SDK header: the live-graph reads stay localized in the
// schema_walk / cvar_walk / engine_constants_walk TUs; this TU only orchestrates
// their exported enumeration helpers + emits the proto.
#pragma once

#include <string>

// Forward-declare the proto message so this header has no protobuf include.
namespace cs2 {
namespace schema_tracker {
namespace v0 {
class RegistryUniverse;
}
}  // namespace schema_tracker
}  // namespace cs2

namespace cs2_schema_walker {

class InProcessEnvironment;

// Build the observed-symbol universe by INDEPENDENTLY enumerating the live
// systems reachable from `env` into `out`. `out` is cleared first. Returns true
// on success; on failure sets *err and leaves `out` in an unspecified
// (to-be-discarded) state.
//
// `env` MUST be the same live environment the extraction walks read, so the
// universe stays a superset-or-equal of what was extracted.
bool BuildRegistryUniverse(const InProcessEnvironment& env,
                           cs2::schema_tracker::v0::RegistryUniverse* out,
                           std::string* err);

}  // namespace cs2_schema_walker
