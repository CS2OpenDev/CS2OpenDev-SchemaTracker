// String pool extraction (interned strings the schema system registers,
// deduplicated per pool, pool name preserved verbatim, e.g. "CUtlSymbolLarge").
//
// STATUS: VERIFIED-EMPTY is the correct output. Emits empty string_pools; does NOT
// block the walk. NOT a deferral: it is the verified source of record.
//
// Why verified-empty is correct — a premise mismatch, NOT a reachability gap:
//   The contract wants the interned-string *pools* the SCHEMA SYSTEM registers
//   (CUtlSymbolLarge / CUtlSymbolTableLarge), pool name preserved. RE of the
//   pinned headers + a live headless dump (dump-schema-bytes on build 23669931)
//   proved the schema system interns NOTHING through a symbol pool:
//   CSchemaSystem / CSchemaSystemTypeScope (schemasystem.h) hold zero
//   CUtlSymbolTableLarge members, and every name is a raw const char* / CUtlString
//   into the module's compiled-in .rdata (schematypes.h m_pszName / m_sTypeName).
//   The genuine CUtlSymbolTableLarge pools (g_DmxAttributeStrings,
//   CEntitySystem::m_Symbols, per-arena KV3 m_Symbols) belong to NON-schema
//   subsystems, are unpopulated at headless boot, and are unreachable from the
//   schema graph. A symbol pool is also a RUNTIME structure (contents from
//   maps/resources/KV3), so there is nothing static to offline-scan. The contract
//   as written has no target: pools:[] is verified-complete.
//
//   Synthesizing a pool from the schema's raw name strings and labelling it
//   "CUtlSymbolLarge" is forbidden: those strings are not interned through a
//   CUtlSymbolLarge table, so the pool NAME would be inferred — the walker never
//   presents an inferred provenance.
//
// TODO (only if reachability ever changes): land the real walk if a future build
// or hl2sdk pin exposes a schema-reachable interned pool WITHOUT re-declaring
// un-anchored layouts. The enumeration body to restore is sketched in
// string_pools_walk.cpp.
//
// When restored: within a pool, entries are deduplicated and emitted in stable
// (sorted) order; pools in stable pool-name order. A structural access failure
// (e.g. a non-null pool handle with a corrupt count) becomes false + *err; an
// empty-but-valid pool is NOT corruption and yields an empty entries list.
#pragma once

#include <string>

// Forward-declare the proto message so this header has no protobuf include.
namespace cs2 {
namespace schema_tracker {
namespace v0 {
class StringPoolsWalk;
}
}  // namespace schema_tracker
}  // namespace cs2

namespace cs2_schema_walker {

class InProcessEnvironment;

// Walk the reflection-reachable interned-string pools reachable from `env` into
// `out`. `out` is cleared first. Returns true on success; on failure sets *err
// and leaves `out` in an unspecified (to-be-discarded) state.
bool WalkStringPools(const InProcessEnvironment& env,
                     cs2::schema_tracker::v0::StringPoolsWalk* out,
                     std::string* err);

}  // namespace cs2_schema_walker
