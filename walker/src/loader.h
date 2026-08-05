// In-process module loader. Loads CS2 Source 2 DLLs via dlopen on Linux /
// LoadLibrary on Windows, resolves the `CreateInterface` factory each Source 2
// module exports, obtains a live CSchemaSystem*, and forces per-module schema
// registration via each module's `InstallSchemaBindings` C export.
//
// Every failure path on this surface returns std::nullopt / false + sets an error
// string the caller MUST propagate. Never silently fall back.
#pragma once

#include <filesystem>
#include <optional>
#include <string>
#include <utility>
#include <vector>

namespace cs2_schema_walker {

// Opaque handle returned by LoadModule. The destructor unloads via dlclose /
// FreeLibrary. We hold these in a vector for the lifetime of the walker process
// so the live CSchemaSystem* graph stays valid while we walk it.
class LoadedModule {
 public:
  LoadedModule() = default;
  LoadedModule(const std::string& path, void* handle);
  ~LoadedModule();

  // Movable, not copyable: the handle is unique.
  LoadedModule(LoadedModule&& o) noexcept;
  LoadedModule& operator=(LoadedModule&& o) noexcept;
  LoadedModule(const LoadedModule&) = delete;
  LoadedModule& operator=(const LoadedModule&) = delete;

  const std::string& path() const { return path_; }
  void* handle() const { return handle_; }

  // The bare module file name (e.g. "schemasystem.dll", "libschemasystem.so").
  std::string filename() const;

  // The Source 2 "module name" the schema system keys on: the file name with
  // the platform prefix/suffix stripped (e.g. "schemasystem", "client",
  // "server"). This is what InstallSchemaBindings / FindTypeScopeForModule key
  // on.
  std::string module_name() const;

  // Resolve a symbol. Returns nullptr on miss (caller decides if that's fatal).
  void* ResolveSymbol(const char* name) const;

 private:
  std::string path_;
  void* handle_ = nullptr;
};

// List every Source 2 module file under `dir`. Filters by platform-appropriate
// suffix (.so on Linux, .dll on Windows) and returns paths sorted for
// determinism. Returns std::nullopt + populates *err if `dir` doesn't exist.
std::optional<std::vector<std::filesystem::path>>
DiscoverModules(const std::filesystem::path& dir, std::string* err);

// Resolve the set of directories that hold the CS2 Source 2 modules, given the
// single dir the host passes via --binaries (Option A: --binaries points at a
// platform root and the loader discovers CS2's real bin subdirs).
//
// CS2 ships its schema-registering DLLs across TWO directories:
//   <root>/game/bin/win64          (engine: schemasystem, engine2, tier0, ...)
//   <root>/game/csgo/bin/win64     (game:   client, host, server, matchmaking)
// On Linux the leaf is `linuxsteamrt64` and modules are `lib*.so`.
//
// Resolution rules (in order), so the existing flat-dir tests + host stay
// backward-compatible:
//   1. If `root` itself directly contains the schema-system module, treat it as
//      a single flat dir (legacy / unit-test layout) -> {root}.
//   2. Else if the CS2 nested layout exists under `root`, return the engine dir
//      first then the game dir (dependency-friendly order).
//   3. Else return {root} so DiscoverModules can fail loud with a precise error.
//
// The FIRST entry in the returned vector is the directory that must contain the
// schema-system module (the engine dir under the nested layout). Returns
// std::nullopt + sets *err only on a hard filesystem error.
std::optional<std::vector<std::filesystem::path>>
ResolveBinaryDirs(const std::filesystem::path& root, std::string* err);

// Load a single module. Returns std::nullopt + populates *err on failure
// (file missing, ABI mismatch, dlopen error, etc.).
std::optional<LoadedModule>
LoadModule(const std::filesystem::path& path, std::string* err);

// The standard Source 2 factory export name (a C export each module ships).
inline constexpr const char* kCreateInterfaceSymbol = "CreateInterface";

// The CreateInterface version string the loader uses to obtain the live
// CSchemaSystem* (SCHEMASYSTEM_INTERFACE_VERSION). Surfaced as an accessor so
// diagnostics can report exactly what the loader resolved the schema system with
// WITHOUT re-declaring the constant (single source of truth lives in loader.cpp).
const char* SchemaSystemInterfaceVersion();

// The standard per-module schema-registration export. Each Source 2 game module
// exports this; calling it forces that module's static schema bindings to
// register against the live schema system. Not declared by HL2SDK (it is a
// game-module export, resolved by name at runtime).
inline constexpr const char* kInstallSchemaBindingsSymbol = "InstallSchemaBindings";

// Signatures of the two C exports we resolve. CreateInterfaceFn matches
// HL2SDK's tier0/interface.h typedef; we re-state it here so loader.h has no
// HL2SDK include dependency (only the .cpp that touches live interfaces does).
using CreateInterfaceFn = void* (*)(const char* name, int* return_code);
using InstallSchemaBindingsFn = bool (*)(const char* module_name, void* schema_system);

// An in-process environment: every Source 2 module under a binaries dir loaded
// into THIS process, with the live CSchemaSystem* obtained and per-module schema
// bindings installed. Holds the LoadedModule handles for its whole lifetime so
// the live object graph the walker traverses stays valid.
//
// `schema_system()` returns a void* deliberately: loader.h stays HL2SDK-free, so
// the schema-walk TU reinterpret_casts it to CSchemaSystem* behind sdk_schema.h.
// `cvar()` and `network_messages()` are likewise returned as void* and
// reinterpret_cast to ICvar* / INetworkMessages* in their own walk TUs.
class InProcessEnvironment {
 public:
  InProcessEnvironment() = default;

  // Non-copyable, non-movable: holds raw module handles + interface pointers
  // whose validity is tied to this object's lifetime.
  InProcessEnvironment(const InProcessEnvironment&) = delete;
  InProcessEnvironment& operator=(const InProcessEnvironment&) = delete;

  void* schema_system() const { return schema_system_; }
  void* cvar() const { return cvar_; }                          // ICvar.
  void* network_messages() const { return network_messages_; }  // INetworkMessages.

  const std::vector<LoadedModule>& modules() const { return modules_; }

  // BUILD-LEVEL era determination (PRE-2024 baseline, e.g. build 10832117).
  //
  // The schema-system memory layout (CUtlTSHash bindings table shape, the
  // CSchemaClassInfo/SchemaClassFieldData_t record offsets) is a property of the
  // WHOLE BUILD, not of an individual type scope. The schema walk MUST decide one
  // era for ALL scopes; deciding it per-scope from a compiled Count() heuristic
  // mis-detects scopes whose compiled count happens to read a small/plausible
  // value (e.g. client.dll=159, engine2.dll=1 on the 2023 baseline) and then reads
  // the 2023-layout records through the modern path -> wrong counts AND a fault in
  // EmitClass.
  //
  // This flag is the AUTHORITATIVE build-era signal. It is set TRUE only by
  // RetrySchemaRegistrationIfEmpty, and only when the post-boot "SchemaSystem_001"
  // registration handshake actually registered >0 modules — a path entered ONLY on
  // the 2023 baseline (modern builds populate schema during the engine boot and
  // never enter that fallback). It therefore stays FALSE on every modern build, so
  // the modern walk routes through kModern exactly as before (byte-identical).
  bool schema_is_2023_era() const { return schema_is_2023_era_; }
  void set_schema_is_2023_era(bool v) { schema_is_2023_era_ = v; }

  // Per-module boot-resolved CreateInterface version strings.
  //
  // The Source 2 boot (BootEngineForConVars) is the ONLY place that can observe
  // which CreateInterface versions each module actually exposes (a non-null factory
  // return with return-code 0). The boot's own factory table (engine_boot.cpp's
  // function-local FactoryTable g_table) is torn down when the boot returns, so it
  // cannot carry this out to the walk. The boot therefore RECORDS each resolved
  // (module, version) here, on the environment, which OUTLIVES the boot.
  //
  // Shape: one entry per module that resolved >=1 interface; each entry's vector
  // holds the resolved version strings for that module (unsorted, de-duped by the
  // recorder). A module that resolved NONE is meaningful (it gets an empty list in
  // the emitted ModulesWalk) but is simply not recorded here — WalkModulesInterfaces
  // emits empty lists by iterating loaded modules, not this structure. The walk
  // sorts everything for determinism; this raw store is insertion-ordered.
  using ModuleInterfaceRecord = std::pair<std::string, std::vector<std::string>>;
  const std::vector<ModuleInterfaceRecord>& resolved_module_interfaces() const {
    return resolved_module_interfaces_;
  }
  // Record one resolved (module, version). Called from the CONNECT pass only (never
  // the Init / data-subsystem re-resolves). De-dupes defensively: a (module,version)
  // already present is not added again, and a brand-new module appends a fresh entry.
  void RecordResolvedInterface(const std::string& module,
                               const std::string& version) {
    for (auto& rec : resolved_module_interfaces_) {
      if (rec.first == module) {
        for (const std::string& v : rec.second) {
          if (v == version) return;  // already recorded for this module.
        }
        rec.second.push_back(version);
        return;
      }
    }
    resolved_module_interfaces_.push_back({module, {version}});
  }

  // Population is performed by LoadInProcessEnvironment (the only legitimate
  // builder). Exposed as plain members under a private section reachable only
  // through that friend so callers cannot half-build an environment.
 private:
  friend std::optional<std::unique_ptr<InProcessEnvironment>>
  LoadInProcessEnvironment(const std::filesystem::path&, std::string*);

  std::vector<LoadedModule> modules_;
  void* schema_system_ = nullptr;
  void* cvar_ = nullptr;
  void* network_messages_ = nullptr;
  // Authoritative build-era flag; see schema_is_2023_era() above. Defaults FALSE
  // (modern); flipped TRUE only by the 2023-only RetrySchemaRegistrationIfEmpty
  // fallback path. Mutable through the public setter (the registration retry takes
  // InProcessEnvironment& and is not the friend builder).
  bool schema_is_2023_era_ = false;

  // Per-module resolved CreateInterface versions, recorded by the boot's CONNECT
  // pass (see RecordResolvedInterface above). Drained by WalkModulesInterfaces
  // into WalkerOutput.modules after the boot returns.
  std::vector<ModuleInterfaceRecord> resolved_module_interfaces_;
};

// Load every module under `binaries_dir`, obtain the live CSchemaSystem (plus the
// ICvar and INetworkMessages handles), and install per-module schema bindings.
//
// Fail-loud: a missing schemasystem module, a missing CreateInterface export, a
// null CSchemaSystem, or a CreateInterface return code != 0 all return
// std::nullopt with *err populated, BEFORE any extraction begins.
//
// Returns a heap-owned environment (the caller owns it via unique_ptr) so the
// loaded modules stay resident for the walk.
std::optional<std::unique_ptr<InProcessEnvironment>>
LoadInProcessEnvironment(const std::filesystem::path& binaries_dir, std::string* err);

// POST-BOOT, schema-empty-gated registration retry (the PRE-2024 baseline fix,
// e.g. CS2 build 10832117 / 2023-03-22).
//
// WHAT IT DOES: if the live schema system reachable from `env` is still EMPTY
// (SchemaSystemIsEmpty(env) — an era-stable VTABLE probe, NOT a compiled-offset
// read), it loops over every loaded module that exports InstallSchemaBindings and
// calls the 2-ARG form `InstallSchemaBindings("SchemaSystem_001", schema_system)`
// — passing the SCHEMA INTERFACE VERSION string as the first arg (the registration
// handshake the older era expects), SEH-guarded via CallInstall2ArgGuarded.
//
// WHY POST-BOOT AND GATED: on MODERN builds the partial engine boot
// (BootEngineForConVars) populates BOTH the schema and the full convar set,
// including ~581 subsystem convars. Forcing "SchemaSystem_001" registration
// BEFORE that boot perturbs it and drops those convars (a confirmed determinism
// regression). So this retry runs AFTER the boot and ONLY when the schema is
// still empty — which is true on the 2023 baseline (the boot doesn't populate
// schema there) and FALSE on modern (boot already populated scopes). On modern it
// is therefore a strict no-op: SchemaSystemIsEmpty returns false and the retry is
// skipped entirely, keeping modern output byte-identical.
//
// MUST be called AFTER BootEngineForConVars and BEFORE the schema walk reads
// scopes. The loader's primary GetEnvironment install pass (module-name first arg)
// is unchanged and runs at load time as before.
//
// Returns true on success (including the modern no-op skip and the known runtime
// variant case). Returns FALSE and sets *err ONLY on the fail-loud path: the
// schema was empty and the SchemaSystem_001 fallback registered records, but their
// live layout matched NEITHER modern NOR any KNOWN pre-2024 runtime layout variant —
// the caller MUST abort before any output bytes (never guess). A per-module
// registration false / fault is still benign (the schema-empty signal remains the
// post-walk type-scope gate). Emits tracing under CS2_WALKER_TRACE.
//
// SIDE EFFECT (build-layout determination): when this retry registers >0 modules via
// "SchemaSystem_001", it runs the N-way DetectSchemaVariant probe and sets the
// authoritative build layout: kModern -> set_schema_is_2023_era(false) (byte-identical);
// kKnownRuntimeVariant (variant 0 == the 2023 table, e.g. build 10832117 + its
// V0 family) -> set_schema_is_2023_era(true); kUnknown -> fail loud (return false + err,
// print the observed runtime signature to stderr). On modern this path is never entered
// (schema is non-empty post-boot -> early success return), so the flag stays FALSE and
// the walk routes through kModern, byte-identical.
bool RetrySchemaRegistrationIfEmpty(InProcessEnvironment& env, std::string* err);

}  // namespace cs2_schema_walker
