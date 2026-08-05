// Observed-registry-symbol universe. See registry_universe_walk.h.
//
// INDEPENDENT live traversal: this builds the universe by separately enumerating
// the live Source 2 systems (CSchemaSystem, ICvar, schema enumerators) via the
// shared enumeration helpers the extraction walks expose — NOT by re-reading the
// already-built extraction sub-messages. That makes the host's completeness audit
// a real check: a symbol dropped during extraction proto-assembly still appears
// here, so the host mints an Omitted row instead of the gap vanishing.
#include "registry_universe_walk.h"

#include "cvar_walk.h"
#include "engine_constants_walk.h"
#include "loader.h"
#include "schema_walk.h"

#include "walker_output.pb.h"

#include <algorithm>
#include <string>
#include <vector>

namespace wpb = cs2::schema_tracker::v0;

namespace cs2_schema_walker {

namespace {

// Category tags. Kept as constants so the sort key and the emitted value are the
// same literal (no chance of a typo desyncing them).
constexpr const char* kCatSchemaClass = "schema_class";
constexpr const char* kCatSchemaEnum = "schema_enum";
constexpr const char* kCatEngineConstant = "engine_constant";
constexpr const char* kCatConVar = "convar";
constexpr const char* kCatCommand = "command";
// NOTE: there is intentionally NO kCatNetworkMessage here — the network_message
// audit family is host-owned (see the network_message section in BuildRegistryUniverse).

struct Row {
  std::string category;
  std::string module;
  std::string symbol;
};

}  // namespace

bool BuildRegistryUniverse(const InProcessEnvironment& env,
                           wpb::RegistryUniverse* out, std::string* err) {
  out->Clear();

  std::vector<Row> rows;

  // ---- schema_class / schema_enum — live CSchemaSystem scopes ----
  // EnumerateLiveSchemaSymbols reuses schema_walk's own (name, module) readers,
  // so each pair == SchemaClass/SchemaEnum.{name,module} the extraction emits.
  {
    std::vector<SchemaSymbolRef> classes, enums;
    if (!EnumerateLiveSchemaSymbols(env, &classes, &enums, err)) {
      return false;  // structural (null CSchemaSystem)
    }
    rows.reserve(rows.size() + classes.size() + enums.size());
    for (const SchemaSymbolRef& c : classes) {
      rows.push_back({kCatSchemaClass, c.module, c.name});
    }
    for (const SchemaSymbolRef& e : enums) {
      rows.push_back({kCatSchemaEnum, e.module, e.name});
    }
  }

  // ---- engine_constant — live schema enumerators ----
  // EnumerateLiveEngineConstants reuses engine_constants_walk's name derivation
  // and emits the PARSED owning module (== the host's ModuleFromConstantSource of
  // the extraction's source string). symbol = "<Enum>::<Member>".
  {
    std::vector<EngineConstantRef> constants;
    if (!EnumerateLiveEngineConstants(env, &constants, err)) {
      return false;  // structural (null CSchemaSystem)
    }
    rows.reserve(rows.size() + constants.size());
    for (const EngineConstantRef& ec : constants) {
      rows.push_back({kCatEngineConstant, ec.module, ec.name});
    }
  }

  // ---- convar / command — live ICvar registry ----
  // EnumerateLiveConVarAndCommandNames reuses cvar_walk's index scan + sentinel
  // filter. ConVar/Command carry no module, so module = "" (the host derives the
  // same key: name + "").
  {
    std::vector<std::string> convar_names, command_names;
    if (!EnumerateLiveConVarAndCommandNames(env, &convar_names, &command_names,
                                            err)) {
      return false;  // structural (null ICvar)
    }
    rows.reserve(rows.size() + convar_names.size() + command_names.size());
    for (const std::string& n : convar_names) {
      rows.push_back({kCatConVar, std::string(), n});
    }
    for (const std::string& n : command_names) {
      rows.push_back({kCatCommand, std::string(), n});
    }
  }

  // ---- network_message — HOST-OWNED, intentionally NOT enumerated here ----
  // The network_message family of the registry-audit universe is owned by the HOST,
  // not the walker. network_messages.json is produced by the host's offline RTTI
  // scan (NetworkMessageRttiScanner over each build's CNetMessagePB<...>
  // instantiations), and the host mints the audit universe's network_message rows from
  // that SAME scan (ExtractCommand.AssembleAuditUniverse), so universe == artifact for
  // that family. Emitting pin-static netmsg rows here would create a SECOND, divergent
  // source (the pinned kNetMsgTable is per-hl2sdk-pin, not per-build) that the host
  // would then have to strip — so the walker deliberately emits NONE. kNetMsgTable +
  // WalkNetworkMessages still feed WalkerOutput.network_messages (a retiring field);
  // only the universe contribution moved to the host.

  // NOTE: string_pool is still a DEFERRED category — the walker has no reachable
  // registry for it (string_pools emits an empty payload), so we emit NOTHING here.
  // Do not fabricate. When it lands its live traversal, add an enumeration here.

  // Determinism: stable total order by (category, module, symbol) Ordinal.
  // std::string::operator< is a byte-wise (Ordinal) compare.
  std::sort(rows.begin(), rows.end(), [](const Row& a, const Row& b) {
    if (a.category != b.category) return a.category < b.category;
    if (a.module != b.module) return a.module < b.module;
    return a.symbol < b.symbol;
  });

  for (const Row& r : rows) {
    wpb::ObservedRegistrySymbol* s = out->add_symbols();
    s->set_symbol(r.symbol);
    s->set_module(r.module);
    s->set_category(r.category);
  }
  return true;
}

}  // namespace cs2_schema_walker
