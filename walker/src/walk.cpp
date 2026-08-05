// Walk implementation. See walk.h.
#include "walk.h"

#include "cvar_walk.h"
#include "engine_boot.h"
#include "engine_constants_walk.h"
#include "layout_probe.h"
#include "loader.h"
#include "netmsg_walk.h"
#include "registry_universe_walk.h"
#include "schema_walk.h"
#include "string_pools_walk.h"
#include "version.h"
#include "walker_output.pb.h"

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <memory>
#include <system_error>
#include <vector>

// Teardown mitigation needs a hard process exit that skips module teardown.
// On Windows that means TerminateProcess (ExitProcess/_Exit still run DLL detach).
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace cs2_schema_walker {

namespace {
// Flushed phase-boundary trace. Each phase prints its name and flushes IMMEDIATELY
// before running, so the LAST line before a crash names the culprit phase (the
// 2023 baseline walk can crash in a phase after the schema walk emits). Gated on
// CS2_WALKER_TRACE, stderr only — never into the artifact.
void TracePhase(const char* name) {
  if (std::getenv("CS2_WALKER_TRACE") != nullptr) {
    std::fprintf(stderr, "[walker-trace] walk: PHASE %s\n", name);
    std::fflush(stderr);
  }
}

// Drain the boot's per-module resolved-interface store (recorded on the
// environment during the CONNECT pass; see InProcessEnvironment::RecordResolved-
// Interface) into WalkerOutput.modules. The host joins resolved_interfaces[] onto
// modules.json's Module rows by module identity (bare module name).
//
// resolved_interfaces is sorted lexicographically per module and the modules list
// by module name, so output is deterministic regardless of boot iteration order.
// A module that resolved nothing simply doesn't appear; the host treats an absent
// row's resolved_interfaces as empty, which is the same meaning.
void WalkModulesInterfaces(const InProcessEnvironment& env,
                           cs2::schema_tracker::v0::ModulesWalk* out) {
  // Copy into a local so we can sort without mutating the environment.
  std::vector<InProcessEnvironment::ModuleInterfaceRecord> recs =
      env.resolved_module_interfaces();

  std::sort(recs.begin(), recs.end(),
            [](const InProcessEnvironment::ModuleInterfaceRecord& a,
               const InProcessEnvironment::ModuleInterfaceRecord& b) {
              return a.first < b.first;
            });

  for (auto& rec : recs) {
    std::sort(rec.second.begin(), rec.second.end());
    auto* mi = out->add_modules();
    mi->set_module(rec.first);
    for (const std::string& v : rec.second) {
      mi->add_resolved_interfaces(v);
    }
  }
}
}  // namespace

bool RunWalk(const WalkArgs& args, std::string* err) {
  // Probe first — never extract against an unknown layout.
  auto probe = ProbeLayout(args.binaries_dir, err);
  if (!probe.has_value()) {
    return false;  // *err already populated.
  }
  if (!probe->known) {
    *err = "unknown schema-system layout signature: " + probe->signature;
    return false;
  }

  // Load the platform's Source 2 modules into THIS process, obtain the live
  // CSchemaSystem, and force per-module schema registration. One walk loads ALL
  // modules (client + server + engine); client/server is carried per-class via
  // SchemaClass.module, not by separate invocations. Any load / CreateInterface /
  // registration failure aborts here, before any output bytes.
  auto env = LoadInProcessEnvironment(args.binaries_dir, err);
  if (!env.has_value()) {
    return false;  // *err set.
  }

  // Partial Source 2 engine boot. ConVars/ConCommands are not static lists; each
  // game-config module's IAppSystem::Init() runs ConVar_Register() which flushes
  // its convars into the live ICvar. BootEngineForConVars Connects every loaded
  // module (incremental real-interface factory) and Init()s the game-config set
  // (host/matchmaking/server/client). An empty registry after Init fails loud
  // here, BEFORE any output bytes — we never emit an empty convars.json silently.
  if (!BootEngineForConVars(**env, err)) {
    return false;  // *err set; no bytes written.
  }

  // POST-BOOT schema-registration retry (PRE-2024 baseline fix; see
  // loader.h::RetrySchemaRegistrationIfEmpty). On the 2023 baseline the partial
  // boot does NOT populate schema, so the schema system is still empty here; this
  // drives each module's InstallSchemaBindings with the "SchemaSystem_001" first
  // arg (the older era's registration handshake) to populate the scopes. It is
  // GATED on the schema system still being empty (an era-stable vtable probe), so
  // on MODERN builds — where the boot already populated scopes — it is a strict
  // no-op and the output stays byte-identical. It MUST run AFTER the boot (doing
  // it pre-boot perturbs the boot's convar registration and drops ~581 subsystem
  // convars) and BEFORE WalkSchemaSystem reads the scopes.
  //
  // It fails loud (returns false + sets *err) when the fallback repopulated the
  // schema but the live records match NEITHER modern NOR any KNOWN pre-2024 runtime
  // layout variant (e.g. an underived V1 build). We abort HERE, before any output
  // bytes, rather than silently emit 0 classes under the wrong offsets.
  if (!RetrySchemaRegistrationIfEmpty(**env, err)) {
    return false;  // *err set; no bytes written.
  }

  // Build the proto in memory. Do not open out_path until the proto is fully
  // built + serialized — partial files are forbidden.
  cs2::schema_tracker::v0::WalkerOutput out;
  out.set_schema_version(kSchemaVersion);
  out.set_walker_version(kWalkerVersion);
  out.set_platform(args.platform);
  out.set_schema_system_layout_signature(probe->signature);

  // Drain the boot's per-module resolved CreateInterface versions into
  // WalkerOutput.modules. Pure in-memory move of data the CONNECT pass already
  // observed; no live calls. Cannot fail (an empty store yields an empty
  // ModulesWalk, which is valid). Runs after the boot (which populated the store)
  // and is independent of the schema/convar walks below.
  TracePhase("modules (WalkModulesInterfaces) []");
  WalkModulesInterfaces(**env, out.mutable_modules());

  // Walk the live CSchemaSystem object graph into the entity sub-message.
  // WalkSchemaSystem sorts every collection by a stable key before adding it to
  // the proto.
  TracePhase("schema (WalkSchemaSystem)");
  if (!WalkSchemaSystem(**env, out.mutable_entity_schema(), err)) {
    return false;  // *err set; no bytes written.
  }
  TracePhase("schema returned OK");

  // Walk the live ICvar registry into convars/commands. The loader has already
  // obtained the ICvar handle (env->cvar()). A null handle / failed access is a
  // structural failure that aborts BEFORE any output bytes; a simply-empty
  // registry yields empty collections and is not an error.
  TracePhase("convars+commands (WalkConVarsAndCommands)");
  if (!WalkConVarsAndCommands(**env, out.mutable_convars(),
                              out.mutable_commands(), err)) {
    return false;  // *err set; no bytes written.
  }

  // network_messages. WalkNetworkMessages emits real channels from the pin-static
  // kNetMsgTable (the live INetworkMessages registry is empty headless — see
  // netmsg_walk.cpp). This populates ONLY WalkerOutput.network_messages, now a
  // RETIRING field: the host no longer lifts it for the network_messages.json
  // artifact (it runs its own offline RTTI scan) NOR for the registry_universe
  // netmsg symbols (that family is host-owned too — see registry_universe_walk.cpp
  // and ExtractCommand.AssembleAuditUniverse). A null INetworkMessages handle is
  // not corruption, so the call returns success on an empty registry.
  TracePhase("network_messages (WalkNetworkMessages)");
  if (!WalkNetworkMessages(**env, out.mutable_network_messages(), err)) {
    return false;  // *err set; no bytes written.
  }

  // Engine constants, from the schema-enumerator surface: every registered enum's
  // enumerators are binary-named integer constants (name + value both off
  // SchemaEnumeratorInfoData_t, nothing inferred). A null CSchemaSystem is a
  // structural failure; an empty schema system yields zero constants and is not an
  // error. WalkEngineConstants sorts the emitted set by (name, source).
  TracePhase("engine_constants (WalkEngineConstants)");
  if (!WalkEngineConstants(**env, out.mutable_engine_constants(), err)) {
    return false;  // *err set; no bytes written.
  }

  // String pools. WalkStringPools emits an empty payload and returns success (no
  // reflection-reachable CUtlSymbolLarge pool is exposed from the schema-system
  // object graph without re-declaring layouts; see string_pools_walk.cpp). This is
  // NOT input corruption, so it must not abort the walk. Wired now so the call
  // site is ready and the empty sub-message is always present.
  TracePhase("string_pools (WalkStringPools)");
  if (!WalkStringPools(**env, out.mutable_string_pools(), err)) {
    return false;  // *err set; no bytes written.
  }

  // registry_universe. Built from an INDEPENDENT traversal of the LIVE systems
  // (the same CSchemaSystem / ICvar / schema-enumerator graphs the extraction
  // walks read), NOT from the extraction sub-messages just populated above.
  // Because it enumerates ALL live registrations it is a SUPERSET-OR-EQUAL of
  // everything extracted; and because the extraction proto-assembly is a SEPARATE
  // step, a symbol the live registry has but extraction dropped still lands here —
  // so the host's completeness audit (universe − artifacts → Omitted) is a genuine
  // check, not circular. Key alignment with the host's audit keys is guaranteed by
  // reusing the extraction walks' own name/module derivation (shared enumeration
  // helpers in schema_walk / cvar_walk / engine_constants_walk). The deferred
  // categories (network_message, string_pool) emit nothing — the walker has no
  // reachable registry for them, and fabricating symbols is forbidden. A null
  // CSchemaSystem / null ICvar is structural and aborts here before any output
  // bytes (the extraction walks already carry the same contract, so in practice
  // they aborted first).
  TracePhase("registry_universe (BuildRegistryUniverse) []");
  if (!BuildRegistryUniverse(**env, out.mutable_registry_universe(), err)) {
    return false;  // *err set; no bytes written.
  }

  TracePhase("serialize");
  std::string serialized;
  if (!out.SerializeToString(&serialized)) {
    *err = "proto serialization failed (this should not happen)";
    return false;
  }

  // Atomic-ish write: write to a sibling temp file, then rename. On Windows
  // std::filesystem::rename over an existing target is allowed when both are
  // on the same volume; on POSIX it's atomic.
  TracePhase("write");
  auto tmp_path = args.out_path;
  tmp_path += ".tmp";

  {
    std::ofstream f(tmp_path, std::ios::binary | std::ios::trunc);
    if (!f) {
      *err = "failed to open output for writing: " + tmp_path.string();
      return false;
    }
    f.write(serialized.data(), static_cast<std::streamsize>(serialized.size()));
    if (!f) {
      *err = "failed to write output bytes to " + tmp_path.string();
      return false;
    }
  }

  std::error_code ec;
  std::filesystem::rename(tmp_path, args.out_path, ec);
  if (ec) {
    // Best-effort cleanup of the temp file. Don't propagate the cleanup error
    // — the rename failure is what the caller needs to see.
    std::error_code ignore;
    std::filesystem::remove(tmp_path, ignore);
    *err = "failed to rename " + tmp_path.string() + " -> " +
           args.out_path.string() + ": " + ec.message();
    return false;
  }

  // HARD EXIT here, before `env` is destroyed. BootEngineForConVars Init's the
  // game-config modules; `env`'s destructor FreeLibrary's them, and that teardown
  // FAULTS while the engine unregisters ConVar change callbacks (a known Source2
  // headless-teardown issue — DumpSource2 hard-exits for exactly this). The fault
  // would corrupt the exit code after a fully successful walk, making the host
  // discard a complete, valid artifact set. The output is already written +
  // atomically renamed above, so exiting now loses nothing.
  std::fflush(nullptr);
#if defined(_WIN32)
  // std::_Exit / ExitProcess still run DllMain(DLL_PROCESS_DETACH) for every
  // module, and the Init'd engine modules FAULT in detach — tier0 then ends the
  // process with exit code 1. TerminateProcess skips DLL detach entirely (no
  // DllMain, no atexit, no static dtors), giving a clean, deterministic exit 0.
  // Same mechanism DumpSource2 uses.
  ::TerminateProcess(::GetCurrentProcess(), 0u);
#else
  // POSIX: _Exit skips atexit + C++ static destructors; .so finalizers aren't run
  // at _Exit, so the equivalent detach-time fault never executes.
  std::_Exit(0);
#endif
  return true;  // unreachable; keeps the bool signature valid.
}

}  // namespace cs2_schema_walker
