// Module loader implementation. See loader.h.
//
// This TU stays HL2SDK-free on purpose: it manipulates the schema system only as
// an opaque void*. The single interface call it makes — CreateInterface — uses
// the locally-restated CreateInterfaceFn typedef (matching tier0/interface.h),
// so no HL2SDK header is pulled in here. The schema-walk TU (schema_walk.cpp) is
// the only place the void* is reinterpreted to CSchemaSystem*.
#include "loader.h"

#include "pe_import_shim.h"     // Windows import-shim recovery (empty on non-Windows)
#include "posix_crash_guard.h"  // POSIX SIGSEGV guard (empty on _WIN32; no HL2SDK leak)
#include "schema_walk.h"        // SchemaSystemIsEmpty (forward decl only; no HL2SDK leak)
#include "util.h"               // EqCi() ASCII case-insensitive compare

#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <memory>
#include <string>
#include <string_view>
#include <system_error>

#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <dlfcn.h>
#endif

namespace cs2_schema_walker {

namespace {

#if defined(_WIN32)
constexpr const char* kModuleSuffix = ".dll";
constexpr const char* kModulePrefix = "";
#else
constexpr const char* kModuleSuffix = ".so";
constexpr const char* kModulePrefix = "lib";
#endif

// The Source 2 interface version string for the schema system. Stable across
// every CS2 build to date (the layout behind it changes; the string does not —
// that is exactly why the layout probe exists separately).
constexpr const char* kSchemaSystemInterface = "SchemaSystem_001";

}  // namespace

const char* SchemaSystemInterfaceVersion() { return kSchemaSystemInterface; }

namespace {

// The console-variable manager interface (ICvar walk).
constexpr const char* kCvarInterface = "VEngineCvar007";

// The network-messages registry interface. Matches
// NETWORKMESSAGES_INTERFACE_VERSION in networksystem/inetworkmessages.h. Exposed
// by the networksystem module; absence is non-fatal here (the netmsg walk decides).
constexpr const char* kNetworkMessagesInterface = "NetworkMessagesVersion001";

// The schema system lives in this module. We must load it and resolve its
// factory regardless of what else is in the binaries dir.
#if defined(_WIN32)
constexpr const char* kSchemaSystemModuleFile = "schemasystem.dll";
#else
constexpr const char* kSchemaSystemModuleFile = "libschemasystem.so";
#endif

// The relative bin subdirs CS2 ships its Source 2 modules under, relative to a
// platform root (the dir the host passes as --binaries). The engine dir is listed
// first because it carries the schema system + tier0; the game dir carries the
// schema-REGISTERING modules (client/server/host) that depend on the engine.
#if defined(_WIN32)
constexpr const char* kEngineBinSubdir = "game/bin/win64";
constexpr const char* kGameBinSubdir = "game/csgo/bin/win64";
#else
constexpr const char* kEngineBinSubdir = "game/bin/linuxsteamrt64";
constexpr const char* kGameBinSubdir = "game/csgo/bin/linuxsteamrt64";
#endif

// The schema-module ALLOW-LIST, in dependency load order. We deliberately do
// NOT load every DLL in the CS2 bin dirs — only the modules known to register
// schema. The list was originally the minimal entity-walk set; the 2026-07
// coverage-gap analysis (CS2OpenDev-Docs SCHEMA_COVERAGE_GAP_EVALUATION.md)
// measured that the wider Source 2 module set — renderers, sound, pulse, the
// resourcecompiler (which carries the *doclib schema projects) — BOTH loads
// cleanly headless AND registers the majority of the schema universe (the old
// assumption that these "fail to load / carry no schema" was tested false on
// era cs2-2026-07-09). They are OPTIONAL below: absence on any era's layout is
// tolerated, but present-but-broken stays fatal (no silent gaps).
//
// Design directive: a single per-platform walk loads ALL schema-bearing modules
// into its own process and walks them in one pass. A schema type is only ever
// registered into the live CSchemaSystem if its OWNING module is loaded into
// THIS process and its InstallSchemaBindings/static-init runs — the descriptors
// are static data inside that module's image. LoadSchemaDataForModules() can
// only drive registration for modules already resident; it cannot conjure the
// descriptors of a module that was never loaded. Therefore widening schema
// coverage REQUIRES adding the owning module to this load set (a full
// LoadLibrary), not merely naming it to LoadSchemaDataForModules.
//
// Root cause this set fixes (CS2 build 23669931): client/server schema embed
// value-typed fields/enums OWNED by the physics/particle/animation subsystem
// modules (HSequence, CParticleProperty, IPhysicsBody, JointMotion_t, ...). With
// only client/server/engine2 loaded, those owning scopes never exist, so the
// referenced CSchemaClassInfo/CSchemaEnumInfo carry an empty m_pTypeScope and the
// walker emits them with an EMPTY module — which the host's schema emitter rejects.
// Loading the owning data modules creates their type scopes and resolves the
// attribution.
//
// LOAD POLICY (deterministic, fail-loud):
//   - REQUIRED set: every module marked `.required` below MUST be present on
//     disk AND load+register cleanly. A required module missing on disk or
//     failing to LoadLibrary aborts the whole walk with a precise stderr error.
//     We NEVER silently skip a required schema module (that is the forbidden
//     silent gap). The three subsystem modules whose types client/server embed
//     by value — animationsystem, particles, vphysics2 — are REQUIRED: they are
//     core, headless-safe, and shipped in every CS2 layout.
//   - OPTIONAL set: modules that register schema but whose presence varies by
//     layout or that may carry heavier deps (networksystem, scenesystem,
//     soundsystem, host). Missing-on-disk is skipped; but if present on disk it
//     MUST still load+register cleanly (a present-but-broken module is fatal —
//     we do not catch-and-continue). Optional only relaxes the on-disk presence
//     requirement, never the "if attempted it must succeed" rule. pulse_system is
//     OPTIONAL and present on every windows layout; on the nine 2023-03-22 builds
//     it needs the import-shim recovery described below to satisfy that rule.
//
// If a module we believe is schema-bearing genuinely cannot load headless on a
// real host, that is a real constraint to surface: the walk fails loud here with
// the module name + OS error rather than emitting partial schema, and we revisit
// whether it belongs in REQUIRED. We do not paper over it with a silent skip.
//
// The ONE narrow exception is the Windows ERROR_PROC_NOT_FOUND recovery below
// (TryLoadViaImportShim / pe_import_shim.h). It does not relax the rule above —
// it FIXES a module that is genuinely loadable. The 2023-03-22 limited-test
// pulse_system.dll imports three RAD Telemetry symbols from a tier0 build Valve
// never shipped; redirecting exactly those to inert first-party stubs lets the
// real module load and register its ~78 CPulse* types. The redirect set is a
// fixed allow-list, so an unresolvable import outside it still aborts the walk
// naming the exact dll!symbol — the module is never loaded in a degraded state
// and a schema gap is never silently accepted.
//
// Each entry is a bare module name (no prefix/suffix), matched case-insensitively.
// Order is load order: foundation first (so the schema system + its deps exist),
// then the schema-registering engine/subsystem/game modules.
struct SchemaModule {
  const char* name;
  bool required;  // present-on-disk + clean load mandatory when true.
};
constexpr SchemaModule kSchemaModulesInLoadOrder[] = {
    // --- foundation: must load before anything references the schema system ---
    {"tier0", true},
    {"vstdlib", false},  // CS2 (Source2) folds vstdlib into tier0 — no separate vstdlib.dll ships.
    {"filesystem_stdio", true},
    {"schemasystem", true},
    {"resourcesystem", true},
    {"networksystem", false},  // also exposes INetworkMessages.
    {"engine2", true},
    // --- subsystem data modules whose types client/server embed BY VALUE.
    //     REQUIRED: their absence is the loader gap we are closing, and a
    //     missing scope produces empty-module declared types the host rejects. ---
    {"animationsystem", true},  // HSequence, AttachmentHandle_t, ScriptedMoveTo_t, ...
    {"particles", true},        // CParticleProperty, ParticleAttachment_t
    {"vphysics2", true},        // IPhysicsBody, JointMotion_t, DynamicContinuousContactBehavior_t, ...
    // --- additional schema-bearing modules. Optional: present-but-broken is
    //     still fatal, but absence is tolerated (layout-dependent / heavier). ---
    {"scenesystem", false},
    {"soundsystem", false},
    // inputsystem / panoramauiclient: NOT schema-bearing, loaded so the convar
    // boot can Connect+Init them for their joy_/input_ and panorama_ convars
    // (they are present in every depot but were never in the load set). OPTIONAL.
    {"inputsystem", false},
    {"panoramauiclient", false},
    // --- game modules that register entity schema + game-config convars.
    //     host/matchmaking are the game-config set the partial engine boot
    //     Init()s to flush ConVar_Register; they are OPTIONAL on
    //     disk (layout-dependent) but loaded here so the boot can reach them. ---
    {"host", false},         // game host module; registers some schema + convars.
    {"matchmaking", false},  // MATCHFRAMEWORK_001; registers matchmaking convars.
    {"server", true},
    {"client", true},
    // --- wider schema-bearing module set (coverage-gap closure, 2026-07).
    //     Together with the global-scope walk + forced registration
    //     (schema_walk.cpp) these take the walked universe from ~1.1k to ~3.6k
    //     classes and 15 to ~590 enums on era cs2-2026-07-09 — recovering the
    //     particles/anim*/smartprops/modellib/physicslib/sound*/worldrenderer/
    //     materialsystem2/pulse/resourcesystem/*doclib projects. All OPTIONAL
    //     (presence varies across eras; measured headless-clean on the current
    //     era, older eras verified per-era during backfill). Loaded AFTER
    //     client/server on purpose: this is the measured configuration, and
    //     emit order is name-sorted so load order never reaches the output. ---
    {"pulse_system", false},        // pulse_system project (35 classes).
    {"worldrenderer", false},       // worldrenderer project (World_t, Aggregate*...).
    {"materialsystem2", false},     // materialsystem2 project (MaterialResourceData_t...).
    {"meshsystem", false},          // modellib types referenced by render meshes.
    {"navsystem", false},           // navlib project (CNavVolume*...).
    {"steamaudio", false},          // steamaudio project (CSteamAudio*...).
    {"resourcecompiler", false},    // carries the *doclib projects (animdoclib,
                                    // animgraphdoclib, sounddoc_lib, texturelib,
                                    // toolutils2, pulsedoc_lib, soundsystem_lowlevel...).
    {"rendersystemdx11", false},    // rendersystemdx11 project (Sampler*...). WINDOWS render
                                    // backend; its deferred Init's ConVar_Register flushes the
                                    // r_*/r_dx11_* convars (no GPU/WARP needed for the convars).
    {"rendersystemvulkan", false},  // LINUX render backend (there is no dx11 on Linux). Same
                                    // IRenderDeviceMgr; its deferred Init flushes the
                                    // r_*/r_vulkan_* convars (no GPU/Vulkan driver needed —
                                    // measured byte-identical with/without one). Absent on
                                    // windows (skipped); rendersystemdx11 absent on linux.
    {"toolframework2", false},      // tool schema still shipped in the depot.
    {"assetpreview", false},        // assetpreview/doclib types (CNmClipDocument...).
    {"propertyeditor", false},      // editor-side schema shipped in the depot.
    {"physicsbuilder", false},      // physics build-time schema.
    {"visbuilder", false},          // vis build-time schema.
    {"helpsystem", false},          // help/UI schema.
    {"panorama", false},            // panorama_content project.
    {"localize", false},            // localization schema.
    {"vscript", false},             // script-system schema.
    {"scenefilecache", false},      // scene cache schema.
    // --- Workshop Tools editor modules (depot 2347779; acquire --tools). All
    //     OPTIONAL: present iff the build's tools slice was acquired (windows
    //     only). Measured on the live cs2-2026-07-09-era install: every module
    //     loads headless and together they register the hammer / qcontrols /
    //     mapdoclib / met / modtools / modeldoc_editor schema projects
    //     (+16 classes / +19 enums over the base-depot walk). ---
    {"assetbrowser", false},  // qcontrols project (GraphCanvas* enums).
    {"assetsystem", false},
    {"assetrename", false},
    {"exportsystem", false},
    {"modeldoc_utils", false},
    {"toolscenenodes", false},
    {"vrad3", false},
    {"modtools", false},  // modtools project (game bin dir).
    // tools/ subdir editors (the tools search dir is added iff present):
    {"toolutils2", false},
    {"toolscene", false},
    {"hammer", false},           // hammer project (ToolsOptionsEditableData_t...).
    {"met", false},              // met project.
    {"modeldoc_editor", false},  // modeldoc_editor project (CMotionAnalysisSettings...).
    {"cs2_item_editor", false},
    {"cs2_workshop_manager", false},
    {"pet", false},
    {"postprocessingeditor", false},
    {"sfm", false},
    {"subrecteditor", false},
};

// Opt-in load tracing. Emits stage breadcrumbs to stderr ONLY when
// CS2_WALKER_TRACE is set in the environment, so default runs stay quiet (the
// output FILE is unaffected either way; trace goes only to stderr). Used to
// localize crashes when loading real CS2 DLLs.
void Trace(const char* stage, const std::string& detail = {}) {
  static const bool on = (std::getenv("CS2_WALKER_TRACE") != nullptr);
  if (!on) return;
  std::fprintf(stderr, "[walker-trace] %s%s%s\n", stage,
               detail.empty() ? "" : ": ", detail.c_str());
  std::fflush(stderr);
}

bool EndsWith(const std::string& s, std::string_view suffix) {
  if (s.size() < suffix.size()) return false;
  return std::equal(suffix.rbegin(), suffix.rend(), s.rbegin());
}

bool StartsWith(const std::string& s, std::string_view prefix) {
  if (s.size() < prefix.size()) return false;
  return std::equal(prefix.begin(), prefix.end(), s.begin());
}

}  // namespace

LoadedModule::LoadedModule(const std::string& path, void* handle)
    : path_(path), handle_(handle) {}

LoadedModule::~LoadedModule() {
  if (handle_ == nullptr) return;
#if defined(_WIN32)
  ::FreeLibrary(reinterpret_cast<HMODULE>(handle_));
#else
  ::dlclose(handle_);
#endif
}

LoadedModule::LoadedModule(LoadedModule&& o) noexcept
    : path_(std::move(o.path_)), handle_(o.handle_) {
  o.handle_ = nullptr;
}

LoadedModule& LoadedModule::operator=(LoadedModule&& o) noexcept {
  if (this != &o) {
    if (handle_ != nullptr) {
#if defined(_WIN32)
      ::FreeLibrary(reinterpret_cast<HMODULE>(handle_));
#else
      ::dlclose(handle_);
#endif
    }
    path_ = std::move(o.path_);
    handle_ = o.handle_;
    o.handle_ = nullptr;
  }
  return *this;
}

std::string LoadedModule::filename() const {
  return std::filesystem::path(path_).filename().string();
}

std::string LoadedModule::module_name() const {
  std::string name = filename();
  // Strip ".dll" / ".so".
  if (EndsWith(name, kModuleSuffix)) {
    name.resize(name.size() - std::string_view(kModuleSuffix).size());
  }
  // Strip the leading "lib" on POSIX.
  std::string_view prefix(kModulePrefix);
  if (!prefix.empty() && StartsWith(name, prefix)) {
    name.erase(0, prefix.size());
  }
  return name;
}

void* LoadedModule::ResolveSymbol(const char* name) const {
  if (handle_ == nullptr) return nullptr;
#if defined(_WIN32)
  return reinterpret_cast<void*>(
      ::GetProcAddress(reinterpret_cast<HMODULE>(handle_), name));
#else
  return ::dlsym(handle_, name);
#endif
}

std::optional<std::vector<std::filesystem::path>>
DiscoverModules(const std::filesystem::path& dir, std::string* err) {
  std::error_code ec;
  if (!std::filesystem::exists(dir, ec) || !std::filesystem::is_directory(dir, ec)) {
    *err = "binaries directory not found or not a directory: " + dir.string();
    return std::nullopt;
  }

  std::vector<std::filesystem::path> out;
  for (const auto& entry : std::filesystem::directory_iterator(dir, ec)) {
    if (ec) {
      *err = "directory iteration failed: " + ec.message();
      return std::nullopt;
    }
    if (!entry.is_regular_file(ec)) continue;
    auto name = entry.path().filename().string();
    if (EndsWith(name, kModuleSuffix)) {
      out.push_back(entry.path());
    }
  }

  // Determinism: sort by lexicographic path so iteration order is stable across
  // machines/filesystems.
  std::sort(out.begin(), out.end());
  return out;
}

std::optional<std::vector<std::filesystem::path>>
ResolveBinaryDirs(const std::filesystem::path& root, std::string* err) {
  std::error_code ec;
  if (!std::filesystem::exists(root, ec) ||
      !std::filesystem::is_directory(root, ec)) {
    *err = "binaries directory not found or not a directory: " + root.string();
    return std::nullopt;
  }

  // Rule 1: flat dir (legacy / unit-test layout) — the schema-system module sits
  // directly under `root`. Use root as the single dir.
  std::error_code ec2;
  if (std::filesystem::exists(root / kSchemaSystemModuleFile, ec2)) {
    return std::vector<std::filesystem::path>{root};
  }

  // Rule 2: CS2 nested layout — engine dir + game dir under the platform root.
  auto engine_dir = root / std::filesystem::path(kEngineBinSubdir);
  auto game_dir = root / std::filesystem::path(kGameBinSubdir);
  std::error_code ec3, ec4;
  bool have_engine =
      std::filesystem::is_directory(engine_dir, ec3) &&
      std::filesystem::exists(engine_dir / kSchemaSystemModuleFile, ec3);
  if (have_engine) {
    std::vector<std::filesystem::path> dirs;
    dirs.push_back(engine_dir);  // FIRST: holds the schema system.
    if (std::filesystem::is_directory(game_dir, ec4)) {
      dirs.push_back(game_dir);
    }
    // Workshop Tools bin dir (depot 2347779, windows-only) — searched iff present.
    // Presence is INPUT-DRIVEN: the dir exists exactly when the build's tools
    // slice was acquired into the cache (acquire --tools), so the walk stays a
    // pure function of its acquired inputs (same build + same inputs -> same
    // bytes on any machine). The tool editor modules registered from here carry
    // the *doclib/hammer/qcontrols schema projects.
    auto tools_dir = engine_dir / "tools";
    std::error_code ec5;
    if (std::filesystem::is_directory(tools_dir, ec5)) {
      dirs.push_back(tools_dir);
    }
    return dirs;
  }

  // Rule 3: neither shape matched. Hand back root so DiscoverModules / the
  // schema-system lookup fail loud with a precise, root-relative message.
  return std::vector<std::filesystem::path>{root};
}

namespace {

// The platform module file name for a bare module name (e.g. "client" ->
// "client.dll" / "libclient.so").
std::string ModuleFileName(const char* bare) {
  return std::string(kModulePrefix) + bare + kModuleSuffix;
}

// Find the on-disk path of `bare` module across the resolved dirs (first hit
// wins; dirs are in dependency order so the engine dir is searched first).
// Returns std::nullopt if not present anywhere — the caller decides if that is
// fatal (only schemasystem is REQUIRED).
std::optional<std::filesystem::path>
FindModule(const std::vector<std::filesystem::path>& dirs, const char* bare) {
  std::string file = ModuleFileName(bare);
  for (const auto& d : dirs) {
    auto candidate = d / file;
    std::error_code ec;
    if (std::filesystem::exists(candidate, ec) &&
        std::filesystem::is_regular_file(candidate, ec)) {
      return candidate;
    }
  }
  return std::nullopt;
}

#if defined(_WIN32)
// Put a directory on the process DLL search path so inter-module imports
// resolve. We use AddDllDirectory (requires LOAD_LIBRARY_SEARCH_* semantics)
// with a SetDllDirectory fallback. Returns false + sets *err on hard failure.
bool AddSearchDir(const std::filesystem::path& dir, std::string* err) {
  // Ensure the default-dirs search strategy includes user-added dirs.
  ::SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  std::wstring wdir = dir.wstring();
  if (::AddDllDirectory(wdir.c_str()) == nullptr) {
    // Fall back to SetDllDirectory (single dir). Last writer wins, but combined
    // with the modules being loaded by absolute path this is sufficient for the
    // first dir; AddDllDirectory normally succeeds so this is a rare path.
    if (::SetDllDirectoryW(wdir.c_str()) == 0) {
      *err = "failed to register DLL search directory " + dir.string() +
             " (GetLastError=" + std::to_string(::GetLastError()) + ")";
      return false;
    }
  }
  return true;
}
#endif

}  // namespace

// ---- InstallSchemaBindings call leaf (crash-contained) -----------------------
//
// CRASH-SAFETY (mirrors engine_boot.cpp's ConnectInitSubsystemGuarded): both the
// loader's primary load-time pass and the post-boot retry below call each module's
// 2-ARG `InstallSchemaBindings(const char*, ISchemaSystem*)` export by raw fnptr.
// Calling through the wrong ABI / into a module that bails can fault; on Windows a
// hard AV cannot be caught by C++ try/catch, so the raw call lives in its own SEH
// leaf with NO C++ unwinding objects (POD only, raw fnptr call). A fault is
// reported as "this call did not work", never a process abort. On the Itanium ABI
// (Linux) there is no SEH, so POSIX mirrors it with the sigaction +
// sigsetjmp/siglongjmp guard in posix_crash_guard.h (same POD-frame constraint).
//
// `*faulted` is set true iff the call faulted. Return value is the install
// function's bool when it ran, false when it faulted.
#if defined(_WIN32)
bool CallInstall2ArgGuarded(InstallSchemaBindingsFn fn, const char* module_name,
                            void* schema_system, bool* faulted) {
  __try {
    *faulted = false;
    return fn(module_name, schema_system);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    *faulted = true;
    return false;
  }
}
#else
// POSIX crash guard mirroring the Windows SEH leaf. The raw InstallSchemaBindings
// fnptr call runs in a POD-only leaf callback; a SIGSEGV/SIGBUS/SIGABRT/SIGFPE there
// jumps back out, *faulted is set true, and the call is reported as "did not work"
// instead of aborting the process — exactly the SEH behavior.
namespace {
struct Install2ArgCtx {
  InstallSchemaBindingsFn fn;
  const char* module_name;
  void* schema_system;
  bool ret;
};
void Install2ArgPodCallback(void* p) {
  Install2ArgCtx* c = static_cast<Install2ArgCtx*>(p);
  c->ret = c->fn(c->module_name, c->schema_system);
}
}  // namespace

bool CallInstall2ArgGuarded(InstallSchemaBindingsFn fn, const char* module_name,
                            void* schema_system, bool* faulted) {
  Install2ArgCtx ctx;
  ctx.fn = fn;
  ctx.module_name = module_name;
  ctx.schema_system = schema_system;
  ctx.ret = false;
  const bool ran = posix_crash_guard::RunGuarded(&Install2ArgPodCallback, &ctx);
  *faulted = !ran;
  return ran ? ctx.ret : false;
}
#endif

// Drive InstallSchemaBindings across every loaded module, passing `first_arg`
// as the registration handshake's first argument, and return the count of
// modules that registered (the install_ok tally). Shared body of the two schema
// registration passes; see the two callers for the WHY of each.
//
// LOAD-BEARING ASYMMETRY: the load-time pass SKIPS the schema system module
// itself (it never self-registers), whereas the post-boot retry pass drives
// EVERY module including the schema module. The caller selects this via `skip`:
// the load-time call passes the schema module, the retry call passes nullptr.
// Both behaviors are reproduced exactly here.
//
// `first_arg` is the other axis that differs. A NULL `first_arg` means "send each
// module's OWN NAME as the first arg" (the load-time historical behavior); a
// non-null `first_arg` is sent verbatim for every module (the retry pass sends
// kSchemaSystemInterface == "SchemaSystem_001", the pre-2024 baseline handshake).
// Trace stage strings are passed in so each caller's stderr trace text stays
// byte-identical (the call stages differ in form: "InstallSchemaBindings" vs
// "SchemaRegistrationRetry.call").
int DriveInstallSchemaBindings(const std::vector<LoadedModule>& mods,
                               const LoadedModule* skip, const char* first_arg,
                               const char* call_stage, const char* result_stage,
                               void* schema_system) {
  int install_ok_count = 0;
  for (const auto& m : mods) {
    if (&m == skip) continue;  // load-time: the schema system never self-registers.
    auto* install = reinterpret_cast<InstallSchemaBindingsFn>(
        m.ResolveSymbol(kInstallSchemaBindingsSymbol));
    if (install == nullptr) continue;  // module carries no schema; fine.
    const std::string mod_name = m.module_name();
    Trace(call_stage, mod_name);
    bool faulted = false;
    const char* arg = (first_arg != nullptr) ? first_arg : mod_name.c_str();
    bool ok = CallInstall2ArgGuarded(install, arg, schema_system, &faulted);
    Trace(result_stage,
          mod_name + "=" + (ok ? "true" : "false") + (faulted ? " (faulted)" : ""));
    if (ok) ++install_ok_count;
  }
  return install_ok_count;
}

#if defined(_WIN32)
// Recovery for a module that is present, whose dependencies all resolved, but
// which imports symbols they do not export (ERROR_PROC_NOT_FOUND). Succeeds only
// when EVERY unresolvable import is in pe_import_shim's fixed inert allow-list;
// otherwise returns nullopt so the caller keeps the original fatal error.
//
// The returned LoadedModule carries the ORIGINAL path (so module_name(), which
// InstallSchemaBindings and the type-scope lookup key on, and every trace line
// are unchanged) but the handle of the patched copy.
std::optional<LoadedModule>
TryLoadViaImportShim(const std::filesystem::path& abs, std::string* err) {
  namespace shim = pe_import_shim;

  std::vector<shim::MissingImport> missing;
  if (!shim::FindUnresolvableImports(abs, &missing, err)) return std::nullopt;
  if (missing.empty()) {
    *err = "no unresolvable named imports found (the failure is elsewhere)";
    return std::nullopt;
  }
  for (const auto& m : missing) {
    if (!shim::IsShimmable(m.symbol)) {
      // Fail loud and name it: this is a genuinely new constraint to surface,
      // never something to stub blindly.
      *err = "unresolvable import outside the inert allow-list: " + m.dll + "!" +
             m.symbol;
      return std::nullopt;
    }
  }

  const std::filesystem::path shim_path = shim::ResolveShimPath();
  std::error_code ec;
  if (!std::filesystem::exists(shim_path, ec)) {
    *err = "compatibility shim not found next to the walker: " + shim_path.string();
    return std::nullopt;
  }
  HMODULE shim_mod = ::LoadLibraryExW(shim_path.wstring().c_str(), nullptr,
                                      LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (shim_mod == nullptr) {
    *err = "failed to load compatibility shim " + shim_path.string() +
           " (GetLastError=" + std::to_string(::GetLastError()) + ")";
    return std::nullopt;
  }
  // The shim must actually supply every symbol we are about to redirect to it,
  // or we would just trade this failure for the same one.
  for (const auto& m : missing) {
    if (::GetProcAddress(shim_mod, m.symbol.c_str()) == nullptr) {
      *err = "compatibility shim does not export " + m.symbol;
      return std::nullopt;
    }
  }

  // Keep the original file name so module_name() is unchanged.
  const std::filesystem::path staged = shim::ShimStagingDir() / abs.filename();
  if (!shim::WriteShimmedCopy(abs, missing, shim::kShimDllName, staged, err)) {
    return std::nullopt;
  }

  HMODULE h = ::LoadLibraryExW(staged.wstring().c_str(), nullptr,
                               LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (h == nullptr) {
    *err = "patched copy still failed to load (GetLastError=" +
           std::to_string(::GetLastError()) + ")";
    return std::nullopt;
  }

  std::string detail = abs.filename().string() + " via import shim (";
  for (size_t i = 0; i < missing.size(); ++i) {
    if (i) detail += ", ";
    detail += missing[i].dll + "!" + missing[i].symbol;
  }
  detail += ")";
  Trace("import-shim", detail);

  return LoadedModule(abs.string(), reinterpret_cast<void*>(h));
}
#endif  // defined(_WIN32)

std::optional<LoadedModule>
LoadModule(const std::filesystem::path& path, std::string* err) {
  std::error_code ec;
  if (!std::filesystem::exists(path, ec)) {
    *err = "module file not found: " + path.string();
    return std::nullopt;
  }

  // Canonicalize to an absolute path. A relative path makes the OS loader's
  // dependency resolution depend on the process CWD, and on Windows the added
  // DLL search dirs are only consulted for dependency resolution of a module
  // loaded by full path. Determinism also benefits.
  std::error_code abs_ec;
  std::filesystem::path abs = std::filesystem::absolute(path, abs_ec);
  if (abs_ec) abs = path;  // best-effort; load below will still try.

#if defined(_WIN32)
  // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS makes the OS resolve this module's imports
  // against the AddDllDirectory()-registered dirs (engine + game bin dirs) plus
  // the app/system dirs — exactly what CS2's cross-module imports need.
  HMODULE h = ::LoadLibraryExW(abs.wstring().c_str(), nullptr,
                               LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
  if (h == nullptr) {
    DWORD gle = ::GetLastError();
    *err = "LoadLibrary failed for " + abs.string() +
           " (GetLastError=" + std::to_string(gle) + ")";
    if (gle == 126 /*ERROR_MOD_NOT_FOUND*/) {
      *err +=
          " [ERROR_MOD_NOT_FOUND: a dependency of this module could not be "
          "resolved on the DLL search path]";
      return std::nullopt;
    }
    if (gle == 127 /*ERROR_PROC_NOT_FOUND*/) {
      // The module is present and its dependencies resolved, but it imports a
      // symbol they do not export. This is real on the 2023-03-22 limited-test
      // builds, whose pulse_system.dll was compiled against a telemetry-enabled
      // tier0 Valve never shipped. Recover ONLY if every unresolvable import is
      // in the fixed inert allow-list; see pe_import_shim.h. Any other missing
      // symbol keeps the original fail-loud behaviour.
      std::string shim_err;
      auto shimmed = TryLoadViaImportShim(abs, &shim_err);
      if (shimmed.has_value()) return std::move(*shimmed);
      *err += " [import-shim recovery not applicable: " + shim_err + "]";
      return std::nullopt;
    }
    return std::nullopt;
  }
  return LoadedModule(abs.string(), reinterpret_cast<void*>(h));
#else
  void* h = ::dlopen(path.c_str(), RTLD_NOW | RTLD_LOCAL);
  if (h == nullptr) {
    const char* e = ::dlerror();
    *err = "dlopen failed for " + path.string() + ": " + (e ? e : "<no dlerror>");
    return std::nullopt;
  }
  return LoadedModule(path.string(), h);
#endif
}

std::optional<std::unique_ptr<InProcessEnvironment>>
LoadInProcessEnvironment(const std::filesystem::path& binaries_dir, std::string* err) {
  // Option A: --binaries is a platform root; resolve the real CS2 bin subdirs
  // (engine dir + game dir), or treat it as a flat dir for the legacy layout.
  auto dirs = ResolveBinaryDirs(binaries_dir, err);
  if (!dirs.has_value()) {
    return std::nullopt;  // *err set.
  }

#if defined(_WIN32)
  // Register EVERY resolved dir on the process DLL search path so cross-module
  // imports (client.dll -> engine2.dll/tier0.dll, etc.) resolve regardless of
  // which dir a given dependency lives in. A hard failure here aborts.
  for (const auto& d : *dirs) {
    if (!AddSearchDir(d, err)) {
      return std::nullopt;  // *err set.
    }
  }
#endif

  auto env = std::make_unique<InProcessEnvironment>();

  // Load ONLY the curated schema-module allow-list, in dependency order. We do
  // not load the whole dir: the CS2 bin dirs carry rendering/audio/codec/Qt
  // modules with external deps the headless walker can't satisfy and that hold
  // no entity schema. A module present on disk that fails to load IS fatal — but
  // only allow-listed modules are ever attempted. All loaded
  // modules stay resident so the cross-referenced live object graph is valid.
  // Track the schema-system module by INDEX, not pointer: env->modules_ is a
  // vector and later push_back()s reallocate, which would dangle a raw pointer
  // captured mid-loop (and crash on the next deref). Resolve to a pointer only
  // after the vector is fully populated.
  std::optional<size_t> schema_module_idx;
  for (const SchemaModule& sm : kSchemaModulesInLoadOrder) {
    const char* bare = sm.name;
    auto path = FindModule(*dirs, bare);
    if (!path.has_value()) {
      // REQUIRED module absent on disk is fatal (never a silent schema gap).
      // OPTIONAL module absent on disk is tolerated — its presence
      // varies by layout — but if it IS present it must still load cleanly (the
      // LoadModule failure path below is fatal for both kinds).
      if (sm.required) {
        *err = std::string("required schema module not found under binaries dir: ") +
               ModuleFileName(bare) + " (searched " +
               std::to_string(dirs->size()) + " dir(s) below " +
               binaries_dir.string() + ")";
        return std::nullopt;
      }
      continue;  // optional + not present on disk for this platform; skip.
    }
    Trace("load", path->string());
    auto m = LoadModule(*path, err);
    if (!m.has_value()) {
      // A load failure of an allow-listed, present module aborts the whole run
      // regardless of required/optional. No partial state. A module we
      // listed as schema-bearing that cannot load headless is a real constraint
      // to surface, NOT a silent skip. *err carries the module + OS error.
      return std::nullopt;  // env (and prior handles) unwind cleanly.
    }
    Trace("loaded", bare);
    env->modules_.push_back(std::move(*m));
    if (EqCi(bare, "schemasystem")) {
      schema_module_idx = env->modules_.size() - 1;
    }

    // Determinism: fix-seed the engine's GLOBAL uniform random stream the instant
    // tier0 is resident, BEFORE any game/schema module is loaded.
    //
    // ROOT CAUSE (two back-to-back determinism runs, build 23669931 windows):
    // exactly ONE field varied across the whole artifact set — cl_color's
    // "default" (one of CS2's 5 teammate colors, 0..4). client.dll constructs
    // cl_color as a global CConVar<int>(name, ..., RandomInt(0,4), ...), whose
    // default argument is evaluated by client.dll's STATIC INITIALIZERS — i.e.
    // at LoadLibrary time inside the module-load loop below — strictly BEFORE the
    // engine boot's Init phase. A seed applied in BootEngineForConVars (the
    // engine_boot.cpp belt-and-suspenders seed) therefore runs too late: the
    // randomized default is already baked at client.dll load. Seeding HERE, right
    // after tier0 loads and before client.dll loads, makes that static-init
    // RandomInt draw from an already-fixed stream every run.
    //
    // SHARED-STATE PROOF (pinned hl2sdk tier1/random.h:91-104): the free
    // functions RandomSeed(int) and RandomInt(int,int) are documented as
    // accessing "the library's global uniform stream", and InstallUniformRandomStream
    // "affect[s] the Random functions above" — so RandomSeed and RandomInt
    // definitively share one library-global IUniformRandomStream. tier0 (CS2
    // folds vstdlib into tier0) owns + exports both. client.dll statically links
    // tier1, so its RandomInt resolves to that same global stream once tier0 is
    // installed — which it is, because tier0's own static init ran at THIS module's
    // LoadLibrary above, before we get here.
    //
    // SYMBOL RESOLUTION (clean-room): tier1/random.h:94 declares
    // `DLL_IMPORT void RandomSeed(int)`, and DLL_IMPORT expands to extern "C" on
    // both platforms, so the export is the unmangled C symbol "RandomSeed".
    // Resolved by name via the SAME ResolveSymbol path used for "CreateInterface".
    // This reads an EXPORTED SYMBOL by name, not a struct member/offset, so the
    // schema-system layout signature is untouched.
    //
    // A failure to resolve RandomSeed is NOT an input-corruption / partial
    // condition — it only risks one cosmetic convar's determinism. We trace it
    // under CS2_WALKER_TRACE and continue; we do NOT abort the walk.
    if (EqCi(bare, "tier0")) {
      using RandomSeedFn = void (*)(int);
      auto* random_seed = reinterpret_cast<RandomSeedFn>(
          env->modules_.back().ResolveSymbol("RandomSeed"));
      if (random_seed != nullptr) {
        constexpr int kFixedRandomSeed = 0;
        random_seed(kFixedRandomSeed);
        Trace("rng.seed",
              "tier0!RandomSeed(0) applied post-tier0-load, pre-game-load "
              "(: fixes static-init RandomInt defaults like cl_color)");
      } else {
        Trace("rng.seed",
              "tier0!RandomSeed NOT resolved; static-init randomized convar "
              "defaults (e.g. cl_color) may be non-deterministic (risk) "
              "-- continuing (non-fatal: not an input-corruption condition)");
      }
    }
  }

  if (!schema_module_idx.has_value()) {
    // Unreachable given the required-check above, but keep the guard explicit.
    *err = std::string("required module not loaded: ") + kSchemaSystemModuleFile;
    return std::nullopt;
  }
  // Safe now: the vector is fully populated, so this pointer is stable.
  LoadedModule* schema_module = &env->modules_[*schema_module_idx];

  Trace("all-modules-loaded", "resolving schema factory");
  // Resolve the schema-system module's CreateInterface factory.
  auto* schema_factory = reinterpret_cast<CreateInterfaceFn>(
      schema_module->ResolveSymbol(kCreateInterfaceSymbol));
  if (schema_factory == nullptr) {
    *err = std::string("CreateInterface export not found in ") +
           schema_module->filename();
    return std::nullopt;
  }

  Trace("CreateInterface", kSchemaSystemInterface);
  int rc = 0;
  void* schema_system = schema_factory(kSchemaSystemInterface, &rc);
  if (schema_system == nullptr || rc != 0) {
    *err = std::string("CreateInterface(\"") + kSchemaSystemInterface +
           "\") failed (returned null=" +
           (schema_system == nullptr ? "yes" : "no") +
           ", return_code=" + std::to_string(rc) + ")";
    return std::nullopt;
  }
  env->schema_system_ = schema_system;
  Trace("schema_system", "obtained");

  // Obtain the ICvar and INetworkMessages interfaces. Whichever loaded module's
  // CreateInterface factory answers first wins; both are probed in the SAME pass
  // over the modules. Their absence is NOT fatal HERE: each respective walk
  // (cvar_walk / netmsg_walk) is the authority on whether a null handle is a
  // structural failure at walk time. Not every binaries dir
  // necessarily carries the engine cvar / networksystem module.
  for (const auto& m : env->modules_) {
    auto* factory =
        reinterpret_cast<CreateInterfaceFn>(m.ResolveSymbol(kCreateInterfaceSymbol));
    if (factory == nullptr) continue;
    if (env->cvar_ == nullptr) {
      int rc_iface = 0;
      void* iface = factory(kCvarInterface, &rc_iface);
      if (iface != nullptr && rc_iface == 0) {
        env->cvar_ = iface;
        Trace("ICvar", "obtained from " + m.filename());
      }
    }
    if (env->network_messages_ == nullptr) {
      int rc_iface = 0;
      void* iface = factory(kNetworkMessagesInterface, &rc_iface);
      if (iface != nullptr && rc_iface == 0) {
        env->network_messages_ = iface;
        Trace("INetworkMessages", "obtained from " + m.filename());
      }
    }
    if (env->cvar_ != nullptr && env->network_messages_ != nullptr) break;
  }

  // Force per-module schema registration. Each game module exports
  // InstallSchemaBindings(const char* firstArg, ISchemaSystem*). We pass the
  // MODULE NAME as the first arg (historical behavior; every committed artifact
  // was produced this way). On MODERN builds this typically returns false for
  // every module — that is BENIGN: schema AND the full convar set are populated by
  // the partial engine boot (BootEngineForConVars) the walk runs after this. We
  // therefore do NOT gate anything on the return value here, and we do NOT attempt
  // any pre-boot "SchemaSystem_001" retry: registering schema here (pre-boot)
  // perturbs the boot's convar registration and drops ~581 subsystem convars on
  // modern. The PRE-2024 schema-empty case (the 2023 baseline, where the boot
  // does not populate schema) is handled POST-BOOT in the walk. A per-module
  // `false` is never fatal; the only corruption signal is an empty schema system
  // after the full walk (the post-walk type-scope gate).
  // LOAD-TIME pass: skip the schema module itself (never self-registers), and
  // send each module's OWN NAME as the first arg (first_arg == nullptr).
  int install_ok_count = DriveInstallSchemaBindings(
      env->modules_, /*skip=*/schema_module, /*first_arg=*/nullptr,
      "InstallSchemaBindings", "InstallSchemaBindings.result",
      env->schema_system_);
  Trace("InstallSchemaBindings.summary",
        std::to_string(install_ok_count) + " module(s) registered bindings (2arg)");
  // NOTE: on MODERN builds this primary pass typically registers 0 here (the
  // modules reject the module-name first arg) — that is BENIGN: the schema and
  // the full convar set are populated by the partial engine boot
  // (BootEngineForConVars) that the walk runs next. The PRE-2024 schema-empty
  // case (e.g. 2023-03-22 baseline, where the boot does NOT populate schema) is
  // handled POST-BOOT in the walk via a "SchemaSystem_001" retry gated on the
  // schema system still being empty — doing it HERE (pre-boot) perturbs the
  // boot's convar registration and loses ~581 subsystem convars on modern.

  Trace("environment", "ready");
  return env;
}

bool RetrySchemaRegistrationIfEmpty(InProcessEnvironment& env, std::string* err) {
  // GATE: only act when the schema system is STILL EMPTY after the boot.
  // SchemaSystemIsEmpty is an era-stable VTABLE probe (schema_walk.cpp) — it does
  // NOT read the compiled m_TypeScopes offset, which drifts on the 2023 baseline.
  // On MODERN builds the boot already populated scopes -> not empty -> we return
  // success and change nothing (modern stays byte-identical).
  if (!SchemaSystemIsEmpty(env)) {
    Trace("SchemaRegistrationRetry",
          "schema non-empty after boot; skipping (modern path)");
    return true;
  }

  Trace("SchemaRegistrationRetry",
        std::string("schema EMPTY after boot; retrying with first-arg \"") +
            kSchemaSystemInterface + "\" (pre-2024 baseline handshake)");

  // The PRE-2024 registration handshake (verified empirically on build 10832117,
  // and matching neverlosecc/source2gen's startup): each module's
  // InstallSchemaBindings expects the SCHEMA INTERFACE VERSION string
  // ("SchemaSystem_001") as its first arg — NOT the module name. Driving every
  // loaded module with that arg registers all schema-bearing modules' scopes.
  void* schema_system = env.schema_system();
  // RETRY pass: drive EVERY module (skip == nullptr — including the schema module,
  // unlike the load-time pass) with the fixed "SchemaSystem_001" first arg.
  int install_ok_count = DriveInstallSchemaBindings(
      env.modules(), /*skip=*/nullptr, /*first_arg=*/kSchemaSystemInterface,
      "SchemaRegistrationRetry.call", "SchemaRegistrationRetry.result",
      schema_system);
  Trace("SchemaRegistrationRetry.summary",
        std::to_string(install_ok_count) +
            " module(s) registered bindings via \"SchemaSystem_001\"");

  // BUILD-LEVEL LAYOUT SIGNAL (authoritative): reaching this point means the
  // schema system was EMPTY after the engine boot (the gate above) and we drove the
  // "SchemaSystem_001" registration handshake. That handshake repopulates the schema
  // on a true pre-2024 build (2023-layout records), on a modern build whose engine boot
  // happened to leave the schema empty (a boot fault — the modules then re-register
  // their MODERN-layout records), OR on a pre-2024 build whose runtime layout we have
  // NOT derived (e.g. a V1 build). So "the fallback fired" does NOT by itself imply
  // a known layout. We DECIDE by reading the ACTUAL records via the N-way
  // DetectSchemaVariant probe (modern tried first + short-circuiting; then the
  // variant-0 (2023) offset table gated by the runtime-variant allow-list). Each
  // read is fault-safe.
  if (install_ok_count > 0) {
    const SchemaVariantProbe probe = DetectSchemaVariant(env);
    switch (probe.variant) {
      case SchemaLayoutVariant::kModern:
        env.set_schema_is_2023_era(false);
        Trace("SchemaRegistrationRetry.layout",
              "records are MODERN layout (boot left schema empty but the "
              "SchemaSystem_001 fallback re-registered modern records) -> build stays "
              "modern; byte-identical");
        break;
      case SchemaLayoutVariant::kKnownRuntimeVariant:
        env.set_schema_is_2023_era(probe.is_2023_offsets);
        Trace("SchemaRegistrationRetry.layout",
              "records validate under a KNOWN pre-2024 runtime layout variant (" +
                  probe.runtime_signature +
                  ") -> schema walk uses the 2023 record offsets for ALL scopes");
        break;
      case SchemaLayoutVariant::kUnknown:
        // FAIL-LOUD: the live layout matched neither modern nor any KNOWN runtime
        // variant. Never guess / never emit garbage or 0 classes silently. Print the
        // observed runtime signature to stderr and abort BEFORE any output bytes. The
        // "unknown schema-system layout signature:" prefix maps to exit 75 (main.cpp).
        std::fprintf(stderr,
                     "[walker] fail-loud: pre-2024 schema runtime layout is not a "
                     "known variant. Observed: variant-0 candidate signature %s did NOT "
                     "validate (read %d classes, CBaseEntity %s). No known runtime layout "
                     "variant matched — refusing to walk (never guess). Derive + validate "
                     "this variant "
                     "before it can be extracted.\n",
                     probe.runtime_signature.c_str(), probe.observed_class_count,
                     probe.observed_cbaseentity ? "present" : "absent");
        std::fflush(stderr);
        Trace("SchemaRegistrationRetry.layout",
              "UNKNOWN pre-2024 runtime layout (" + probe.runtime_signature +
                  ") -> fail-loud");
        *err = "unknown schema-system layout signature: " + probe.runtime_signature +
               " (no known pre-2024 runtime layout variant matched; observed " +
               std::to_string(probe.observed_class_count) + " classes, CBaseEntity " +
               (probe.observed_cbaseentity ? "present" : "absent") + ") []";
        return false;
    }
  }
  return true;
}

}  // namespace cs2_schema_walker
