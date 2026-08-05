// Partial Source 2 engine bootstrap. See engine_boot.h.
//
// CLEAN-ROOM: every interface-version string, vtable slot and call here is
// re-derived from the pinned hl2sdk cs2 headers, NOT copied from DumpSource2.
// Header evidence is cited inline at each decision point so a reviewer can
// confirm against walker/third_party/hl2sdk.
#include "engine_boot.h"

#include "loader.h"
#include "posix_crash_guard.h"  // POSIX SIGSEGV guard (empty on _WIN32)
#include "util.h"               // EqCi() ASCII case-insensitive compare

// HL2SDK surface used here:
//   - tier0/interface.h        : CreateInterfaceFn typedef.
//   - appframework/IAppSystem.h: IAppSystem vtable (Connect @0, Init @3) order.
//   - interfaces/interfaces.h  : every *_INTERFACE_VERSION string + g_p* globals.
//   - icvar.h / tier1/convar.h : ICvar vtable (CallChangeCallback @14), the
//                                index-based ConVarRef / ConVarData accessors.
// We call ONLY the front-of-vtable IAppSystem slots (Connect/Init) — stable
// across every Source2 build — and header-inline ConVar accessors. We never
// touch a DLL_CLASS_IMPORT method (same rule as sdk_schema.h / cvar_walk.cpp).
#include "tier0/interface.h"
// sdk_schema.h is the ONE inclusion point for the schema-system headers.
// We need CSchemaSystem::FindTypeScopeForModule + m_ClassBindings/m_EnumBindings
// here ONLY for the idempotency gate on the data-subsystem Connect+Init below
// (the idempotency gate skips the extra Init whenever the module's scope is already
// populated, so newer eras stay byte-identical). This is the SAME header-inline
// accessor surface schema_walk.cpp::ScopeHasBindings uses — no new layout is
// declared, and the live runtime call dispatches through the loaded DLL's vtable.
#include "sdk_schema.h"
// appframework/IAppSystem.h gives us CTier0AppSystem<IInterface>, IAppSystem,
// and the BuildType_t / kBuildTypeRelease enum. We derive our application object
// from CTier0AppSystem<IAppSystem> so the compiler emits the real IAppSystem
// vtable PREFIX for us (Connect/Disconnect/QueryInterface/Init/Shutdown/
// PreShutdown/GetDependencies/GetTier/Reconnect/IsSingleton/GetBuildType) — we
// never hand-count that prefix. See WalkerApplication below.
#include "appframework/IAppSystem.h"
#include "icvar.h"
#include "convar.h"
// CONVAR API ERA shim (see convar_compat.h). The ConVar/ConCommand surface
// changed across hl2sdk eras; this header presents a uniform W* surface that, on
// the current pin, compiles to exactly the calls this TU used before.
#include "convar_compat.h"

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <string>
#include <unordered_map>
#include <vector>

namespace cs2_schema_walker {

namespace {

// ---- tracing -----------------------------------------------------------------
// Rich, opt-in via CS2_WALKER_TRACE=1. Prefix "[walker-boot]" so the live-run
// trace is greppable. Default runs stay quiet (stderr is not the output file,
// but we keep it off anyway).
void BTrace(const char* stage, const std::string& detail = {}) {
  static const bool on = (std::getenv("CS2_WALKER_TRACE") != nullptr);
  if (!on) return;
  std::fprintf(stderr, "[walker-boot] %s%s%s\n", stage,
               detail.empty() ? "" : ": ", detail.c_str());
  std::fflush(stderr);
}

// ---- the incremental real-interface factory ----------------------------------
//
// A version-string -> real-IAppSystem* map, populated INCREMENTALLY in
// dependency order. When a module's Connect()/Init() queries an interface, this
// factory hands back the REAL pointer of an already-Connected module (or the
// real ICvar / CSchemaSystem / IApplication). Returning null for everything is
// exactly what access-violated the prior attempt; we minimize nulls.
//
// The factory is a plain C function pointer (CreateInterfaceFn) so it can be
// passed straight to IAppSystem::Connect(factory). It reads a process-global
// table because CreateInterfaceFn carries no user context argument.
struct FactoryTable {
  // version-string -> interface pointer. Stable strings (interfaces.h), pointers
  // are the live module interfaces.
  std::unordered_map<std::string, void*> map;
  // Names we were asked for but could not satisfy — surfaced in the trace so the
  // live run shows exactly which null we returned if a crash follows.
  std::unordered_map<std::string, int> misses;
};

FactoryTable* g_table = nullptr;

void* BootFactory(const char* name, int* return_code) {
  if (return_code) *return_code = 0;  // IFACE_OK; adjusted below on miss.
  if (g_table == nullptr || name == nullptr) {
    if (return_code) *return_code = 1;  // IFACE_FAILED.
    return nullptr;
  }
  auto it = g_table->map.find(name);
  if (it != g_table->map.end() && it->second != nullptr) {
    return it->second;
  }
  // A miss. Record it (count) so the trace can show the full miss set once.
  g_table->misses[name] += 1;
  if (return_code) *return_code = 1;  // IFACE_FAILED.
  return nullptr;
}

// ---- the IApplication object (WalkerApplication) ------------------------------
//
// WHY A REAL TYPED CLASS (clean-room): the pinned hl2sdk only
// forward-declares `class IApplication;` (interfaces/interfaces.h:142) and never
// defines its vtable, so there is no header type to inherit for the modern
// app-management surface that live CS2 modules call (the trace shows calls up to
// ordinal 61). We therefore reconstruct the object as a normal C++ class. We
// derive from CTier0AppSystem<IAppSystem> so the COMPILER emits the real
// IAppSystem vtable PREFIX (Connect/Disconnect/QueryInterface/Init/Shutdown/
// PreShutdown/GetDependencies/GetTier/Reconnect/IsSingleton/GetBuildType) — that
// prefix comes straight from OUR appframework/IAppSystem.h, never hand-counted.
// On top of that prefix we declare the modern app-management virtuals IN THE
// EXACT ABI ORDER the real CS2 modules expect.
//
// ABI-INTEROP, NOT COPIED SOURCE: replicating a vtable's slot ORDER and each
// slot's return-register WIDTH is a binary-interface contract — the same kind of
// factual layout matching we already do for SchemaClassFieldData_t etc. The slot
// NAMES, trivial bodies, and comments here are our own; no GPL source is pasted.
//
// CROSS-PLATFORM: on the Itanium C++ ABI (Linux/g++) a class with a virtual
// destructor emits TWO destructor slots (complete-object + deleting); on MSVC it
// emits ONE. The base class here has no virtual dtor, so we model the
// destructor(s) explicitly with the platform-conditional second slot below; on
// Linux every later slot shifts +1 automatically because the extra method is
// physically present in the class.
//
// PER-SLOT RETURN-WIDTH RATIONALE for the trivial bodies: each slot returns the
// register-width-correct benign value for its declared type (void / bool false /
// int 0 / pointer null / -1u). A real run with CS2_WALKER_TRACE pins which slot,
// if any, still needs a non-trivial value.

// The content/game directory the application getters return. Filled at boot from
// the loaded game module path (same derivation SetGameWorkingDirectory uses).
// Static storage so any returned const char* outlives every Connect/Init call.
char g_app_content_dir[1024] = {0};    // e.g. <root>/game/csgo
char g_app_game_bin_dir[1024] = {0};   // e.g. <root>/game/csgo/bin/win64
char g_app_gameinfo_path[1024] = {0};  // e.g. <root>/game/csgo (dir holding gameinfo.gi)

class WalkerApplication : public CTier0AppSystem<IAppSystem> {
 public:
  // --- IAppSystem prefix overrides --------------------------------------------
  // CTier0AppSystem (via CBaseAppSystem) supplies trivial bodies for MOST of the
  // IAppSystem prefix, but leaves PreShutdown() and GetBuildType() pure-virtual,
  // so we MUST implement those two to make the class concrete. We also override
  // Connect/Init so the value handed to querying modules is well-defined. These
  // all live in the IAppSystem prefix (their slots come from the base class
  // layout) — the modern app-management virtuals declared further below start
  // only after this whole prefix.
  bool Connect(CreateInterfaceFn /*factory*/) override { return true; }
  InitReturnVal_t Init() override { return INIT_OK; }
  void PreShutdown() override {}
  BuildType_t GetBuildType() override { return kBuildTypeRelease; }
  // The base CBaseAppSystem::Reconnect forwards to a free ReconnectInterface()
  // helper that the SDK defines in a .cpp we do not link, so the default body
  // pulls an unresolved external. Override it with a trivial body to keep this
  // prefix slot self-contained (we never need real reconnection in a dump boot).
  void Reconnect(CreateInterfaceFn /*factory*/,
                 const char* /*name*/) override {}

  // --- modern app-management surface, in exact ABI slot order -----------------
  // Slot order is the binary contract; names/bodies are ours. The first virtual
  // declared here lands immediately AFTER the IAppSystem prefix the base class
  // emitted (which is why we never hand-count the prefix).

  // 1) Object destructor. Pure-virtual-style explicit slot (the real object's
  //    vtable carries a destructor here). Trivial.
  virtual void OnAppDestruct() {}
#ifndef _WIN32
  // 2) Itanium ABI second (deleting) destructor slot. Windows MSVC has only one
  //    destructor slot, so this MUST be Linux-only — its presence shifts every
  //    later slot +1 on Linux, matching the real module's expectation there.
  virtual void OnAppDestructDeleting() {}
#endif
  // 3) Late pre-shutdown hook (distinct from the IAppSystem PreShutdown above).
  virtual void OnPreShutdown() {}
  // 4) Build type — release. Must match BuildType_t width (int-enum).
  virtual BuildType_t GetAppBuildType() { return kBuildTypeRelease; }
  // 5) Reconnect a single interface by name.
  virtual void ReconnectInterface(CreateInterfaceFn /*factory*/,
                                  const char* /*name*/) {}
  // 6) Register an already-constructed app system object.
  virtual int RegisterSystem(IAppSystem* /*sys*/, const char* /*name*/,
                             bool /*require*/) { return 0; }
  // 7) Register an app system by module + interface name.
  virtual int RegisterSystemByName(const char* /*module*/, const char* /*iface*/,
                                   bool /*require*/) { return 0; }
  // 8) Register an app system object (no require flag overload).
  virtual int RegisterSystemNamed(IAppSystem* /*sys*/, const char* /*name*/) {
    return 0;
  }
  // 9) Remove a registered app system.
  virtual void UnregisterSystem(IAppSystem* /*sys*/) {}
  // 10) Bulk-register an array of app systems.
  virtual int RegisterSystems(int /*count*/, void** /*systems*/) { return 0; }
  // 11) Look up an app system / interface by name. Null = not found.
  virtual void* LookupSystem(const char* /*name*/) { return nullptr; }
  // 12) Return the game-info handle/object. Null is tolerated here.
  virtual void* GetGameInfoObject() { return nullptr; }
  // 13) Unknown getter (DumpSource2 "unk1"); returns an invalid index sentinel.
  virtual unsigned int GetUnknownIndexA() { return static_cast<unsigned>(-1); }
  // 14) UI language id for the given slot.
  virtual int GetUiLanguage(int /*slot*/) { return 0; }
  // 15) Audio language id for the given slot.
  virtual int GetAudioLanguage(int /*slot*/) { return 0; }
  // 16) Tools-mode predicate. Headless dump is NOT tools mode.
  virtual bool IsRunningInToolsMode() { return false; }
  // 17-19) Unknown bool predicates (DumpSource2 unnamed) — benign false.
  virtual bool GetUnknownFlagB() { return false; }
  virtual bool GetUnknownFlagC() { return false; }
  virtual bool GetUnknownFlagD() { return false; }
  // 20-24) Unknown pointer getters — null (callers that deref these are the next
  //        thing to inspect via the trace if a crash recurs).
  virtual void* GetUnknownPtrE() { return nullptr; }
  virtual void* GetUnknownPtrF() { return nullptr; }
  virtual void* GetUnknownPtrG() { return nullptr; }
  virtual void* GetUnknownPtrH() { return nullptr; }
  virtual void* GetUnknownPtrI() { return nullptr; }
  // 25) Identity passthrough (DumpSource2 "unk10"): returns its argument. The
  //     real method forwards an object pointer back to the caller unchanged.
  virtual void* PassthroughIdentity(void* a) { return a; }
  // 26) Unknown pointer getter — null.
  virtual void* GetUnknownPtrK() { return nullptr; }
  // 27) Register an app system but skip loading its startup manifests.
  virtual void* AddSystemSkipStartupManifests(const char* /*module*/,
                                              const char* /*iface*/) {
    return nullptr;
  }
  // 28-29) Trailing unknown pointer getters — null.
  virtual void* GetUnknownPtrL() { return nullptr; }
  virtual void* GetUnknownPtrM() { return nullptr; }
};

// One process-wide instance; we hand &it through the factory under
// APPLICATION_INTERFACE_VERSION ("VApplication001").
WalkerApplication* GetWalkerApplication() {
  static WalkerApplication app;
  return &app;
}

// ---- ICvar CallChangeCallback (vtable slot 14) crash-patch -------------------
//
// CRASH (a): during init, some module (scenesystem/SceneUtils) sets a convar
// value whose change-handler lazily loads a heavy subsystem and crashes
// headless. The change is dispatched through ICvar::CallChangeCallback.
//
// SLOT DERIVATION (from OUR icvar.h — do NOT trust DumpSource2's "14" blindly):
//   ICvar : public IAppSystem.
//   IAppSystem vtable (appframework/IAppSystem.h):
//     0 Connect 1 Disconnect 2 QueryInterface 3 Init 4 Shutdown 5 PreShutdown
//     6 GetDependencies 7 GetTier 8 Reconnect 9 IsSingleton 10 GetBuildType
//   ICvar own methods begin at 11 (icvar.h):
//     11 FindConVar 12 FindFirstConVar 13 FindNextConVar 14 CallChangeCallback
//   => slot 14 == CallChangeCallback. This matches the DumpSource2 number, and
//      we VERIFIED it from the pinned header rather than assuming it.
//
// We overwrite slot 14 in the live ICvar vtable with a no-op for the boot
// window, then restore it. Reversible + narrow. Active only during Init().
//
// LIVE-RUN ASSUMPTION: that IAppSystem here carries exactly 11 slots (the cs2
// header adds PreShutdown @5 and GetBuildType @10 vs older Source2). If the real
// runtime ICvar omits one of those, slot 14 shifts. The trace prints the
// function pointer we replaced; if convar enumeration later reads garbage the
// pointer in the trace lets us re-derive the true slot.
constexpr int kCvarCallChangeCallbackSlot = 14;

const void** CvarVTable(void* cvar) {
  // The object's first word is its vtable pointer.
  return *reinterpret_cast<const void***>(cvar);
}

// A no-op with the CallChangeCallback signature
// (icvar.h: void(<convar-arg>, CSplitScreenSlot, const CVValue_t*,
//  const CVValue_t*, void*)). The convar argument type differs by era — new:
// ConVarRef (by value, pointer-width); old: ConVarRefAbstract* — so we take it
// via the compat alias WChangeCallbackConVarArg (both pointer-width, so the
// vtable-slot ABI is identical). Returns void; all-args-ignored.
void NoopCallChangeCallback(void* /*self*/,
                            convar_compat::WChangeCallbackConVarArg /*cvar*/,
                            const CSplitScreenSlot /*nSlot*/,
                            const CVValue_t* /*pNewValue*/,
                            const CVValue_t* /*pOldValue*/, void* /*unk*/) {
  // Intentionally empty: suppress lazy-subsystem change handlers during boot.
}

// RAII patch: make the ICvar vtable's CallChangeCallback a no-op for the boot
// window, restoring the original on scope exit. Operates on the SHARED vtable in
// the loaded module's read-only data; we VirtualProtect / mprotect it writable.
class CvarChangeCallbackPatch {
 public:
  explicit CvarChangeCallbackPatch(void* cvar) : cvar_(cvar) {
    if (cvar_ == nullptr) return;
    vtbl_ = const_cast<const void**>(CvarVTable(cvar_));
    slot_ = &vtbl_[kCvarCallChangeCallbackSlot];
    original_ = *slot_;
    if (MakeWritable(slot_, sizeof(void*))) {
      BTrace("crash-patch.install",
             "ICvar slot 14 (CallChangeCallback) original=" +
                 PtrHex(original_));
      *slot_ = reinterpret_cast<const void*>(&NoopCallChangeCallback);
      installed_ = true;
    } else {
      BTrace("crash-patch.FAILED", "could not make ICvar vtable writable");
    }
  }
  ~CvarChangeCallbackPatch() {
    if (installed_ && slot_ != nullptr) {
      *slot_ = original_;
      BTrace("crash-patch.revert", "ICvar slot 14 restored");
    }
  }
  CvarChangeCallbackPatch(const CvarChangeCallbackPatch&) = delete;
  CvarChangeCallbackPatch& operator=(const CvarChangeCallbackPatch&) = delete;

 private:
  static std::string PtrHex(const void* p) {
    char buf[20];
    std::snprintf(buf, sizeof(buf), "0x%llx",
                  static_cast<unsigned long long>(
                      reinterpret_cast<uintptr_t>(p)));
    return buf;
  }
  static bool MakeWritable(void* addr, size_t len);

  void* cvar_ = nullptr;
  const void** vtbl_ = nullptr;
  const void** slot_ = nullptr;
  const void* original_ = nullptr;
  bool installed_ = false;
};

}  // namespace
}  // namespace cs2_schema_walker

// Platform memory-protection for the vtable patch. Kept in its own #if block.
#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
namespace cs2_schema_walker {
bool CvarChangeCallbackPatch::MakeWritable(void* addr, size_t len) {
  DWORD old = 0;
  return ::VirtualProtect(addr, len, PAGE_EXECUTE_READWRITE, &old) != 0;
  // We leave the page RWX for the boot window; the dtor restores the original
  // value but not the protection. The page is the module's vtable section; this
  // is a short-lived headless dump process, so leaving it writable is benign.
}
}  // namespace cs2_schema_walker
#else
#include <sys/mman.h>
#include <unistd.h>
namespace cs2_schema_walker {
bool CvarChangeCallbackPatch::MakeWritable(void* addr, size_t len) {
  const uintptr_t page = static_cast<uintptr_t>(::sysconf(_SC_PAGESIZE));
  uintptr_t start = reinterpret_cast<uintptr_t>(addr) & ~(page - 1);
  uintptr_t end = (reinterpret_cast<uintptr_t>(addr) + len + page - 1) & ~(page - 1);
  return ::mprotect(reinterpret_cast<void*>(start), end - start,
                    PROT_READ | PROT_WRITE | PROT_EXEC) == 0;
}
}  // namespace cs2_schema_walker
#endif

namespace cs2_schema_walker {
namespace {

// ---- IAppSystem front-of-vtable calls (Connect @0, Init @3) ------------------
//
// We invoke ONLY slots 0 and 3, which are Connect / Init in EVERY Source2
// IAppSystem (appframework/IAppSystem.h) — the front of the vtable is the part
// that has never drifted. Calling through a raw vtable avoids needing the full
// IAppSystem type (whose later slots we never touch).
using ConnectFn = bool (*)(void* self, CreateInterfaceFn factory);
using InitFn = int (*)(void* self);  // returns InitReturnVal_t (INIT_OK == 1).

bool CallConnect(void* appsystem, CreateInterfaceFn factory) {
  const void** vt = *reinterpret_cast<const void***>(appsystem);
  auto fn = reinterpret_cast<ConnectFn>(const_cast<void*>(vt[0]));
  return fn(appsystem, factory);
}

int CallInit(void* appsystem) {
  const void** vt = *reinterpret_cast<const void***>(appsystem);
  auto fn = reinterpret_cast<InitFn>(const_cast<void*>(vt[3]));
  return fn(appsystem);
}

// ---- crash-safe data-subsystem Connect+Init (older-build lazy-schema fix) -----
//
// WHY (per-era backfill, CS2 build 18451221 / 2025-05-13 windows): the data
// subsystems animationsystem / particles / vphysics2 (and scenesystem /
// soundsystem) register their schema LAZILY on OLDER builds. Neither raw
// LoadLibrary, the per-module InstallSchemaBindings export, nor the schema
// system's own LoadSchemaDataForModules({module},1) (the prior attempt in
// schema_walk.cpp) drives them on that era — confirmed empirically
// (animationsystem=0, particles=1, vphysics2=0 after that path). On that era the
// modules only register their bindings on the FULL AppSystem lifecycle, i.e.
// IAppSystem::Connect(factory) followed by IAppSystem::Init(). These are pure
// data subsystems (far fewer engine deps than the game-config modules), so their
// Init is lower-risk than client/server/host — but still treated as fallible.
//
// CRASH-SAFETY (non-negotiable): the walker tears down with
// TerminateProcess, and a hard AV mid-Init cannot be caught by C++ try/catch on
// Windows. So on Windows we wrap the Connect+Init of EACH data subsystem in an
// SEH __try/__except: a faulting subsystem is SKIPPED, the boot CONTINUES, and
// the eras that already work are never regressed. SEH demands a frame with NO C++
// objects that need unwinding, so this lives in its own leaf helper that touches
// only POD locals and raw vtable calls (no std::string, no RAII). On the Itanium
// ABI (Linux) there is no SEH; a data-subsystem fault there would SIGSEGV — which
// is why the call site GATES this whole path behind "scope still empty after the
// normal boot": on every era that already registers eagerly
// (current era, and Linux generally) the gate is false and this code never runs,
// so it cannot regress those eras. The documented residual risk is a NEW Linux-
// only old-era SIGSEGV inside a data-subsystem Init; if that surfaces, the gate
// keeps it confined to old Linux builds (which are not in the committed set).
//
// Returns: 0 = Connect+Init ran to completion (Init return value ignored — the
// observable success signal is the scope populating, checked by the caller),
// 1 = Connect returned false (we skip Init), 2 = a fault was caught (Windows).
enum class SubsysBootResult { kRan = 0,
                              kConnectFailed = 1,
                              kFaulted = 2 };

#if defined(_WIN32)
// SEH leaf: NO C++ unwinding objects in this frame (MSVC requirement). All locals
// are POD; the only calls are the raw front-of-vtable Connect/Init helpers.
SubsysBootResult ConnectInitSubsystemGuarded(void* appsystem,
                                             CreateInterfaceFn factory) {
  __try {
    if (!CallConnect(appsystem, factory)) {
      return SubsysBootResult::kConnectFailed;
    }
    (void)CallInit(appsystem);
    return SubsysBootResult::kRan;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    // A structured exception (AV, etc.) inside this module's Connect/Init. Skip
    // it and let the boot continue — the module simply stays un-registered, no
    // worse than the pre-fix state for that era.
    return SubsysBootResult::kFaulted;
  }
}
#else
// POSIX (Itanium ABI): no SEH. Mirror the Windows __try/__except leaf with the
// sigaction + sigsetjmp/siglongjmp guard in posix_crash_guard.h. A SIGSEGV/SIGBUS/
// SIGABRT/SIGFPE inside this module's Connect/Init jumps back out and yields
// kFaulted, so the subsystem stays un-registered and the boot CONTINUES — exactly
// like Windows. (This branch used to be an UNGUARDED direct call under the
// assumption the scope-empty gate is always false on current-era Linux; the live
// linux walk of build 23773332 disproved that — particles' schema scope is still
// empty after the normal boot, so its lazy Init ran and V_qsort_s SIGSEGV'd,
// killing the process before the schema/netmsg walk. The guard turns that into the
// same "FAULTED-skipped" continuation Windows already had.)
//
// POD-frame constraint (same as the SEH leaf): the guarded work runs in a leaf
// callback whose frame a siglongjmp may abandon, so it touches only POD locals + the
// raw vtable Connect/Init helpers — no C++ objects needing destruction.
namespace {
struct ConnectInitCtx {
  void* appsystem;
  CreateInterfaceFn factory;
  SubsysBootResult result;
};
void ConnectInitPodCallback(void* p) {
  ConnectInitCtx* c = static_cast<ConnectInitCtx*>(p);
  if (!CallConnect(c->appsystem, c->factory)) {
    c->result = SubsysBootResult::kConnectFailed;
    return;
  }
  (void)CallInit(c->appsystem);
  c->result = SubsysBootResult::kRan;
}
}  // namespace

SubsysBootResult ConnectInitSubsystemGuarded(void* appsystem,
                                             CreateInterfaceFn factory) {
  ConnectInitCtx ctx;
  ctx.appsystem = appsystem;
  ctx.factory = factory;
  ctx.result = SubsysBootResult::kFaulted;  // stays kFaulted if a fault is caught
  if (!posix_crash_guard::RunGuarded(&ConnectInitPodCallback, &ctx)) {
    return SubsysBootResult::kFaulted;
  }
  return ctx.result;
}
#endif

// Guard JUST an Init() call for the game-config Init loop (that loop already
// Connected each module separately in the Connect pass). Mirrors the
// Connect+Init guard above but wraps only Init(), so a module whose Init()
// access-violates against the partial headless boot is SKIPPED — its convars
// stay unregistered — instead of killing the whole walk. Transparent for the
// modules that already Init cleanly (client/host/server): no fault => returns
// true, identical to the prior unguarded call. This is what makes it safe to
// mark additional modules Init in kBootPlan — a bad one degrades gracefully.
// POD-only frame (a longjmp may abandon it): the only call is CallInit.
#if defined(_WIN32)
bool CallInitGuarded(void* appsystem) {  // true = Init ran, false = faulted
  __try {
    (void)CallInit(appsystem);
    return true;
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#else
namespace {
struct InitOnlyCtx {
  void* appsystem;
};
void InitOnlyPodCallback(void* p) {
  (void)CallInit(static_cast<InitOnlyCtx*>(p)->appsystem);
}
}  // namespace
bool CallInitGuarded(void* appsystem) {
  InitOnlyCtx ctx;
  ctx.appsystem = appsystem;
  return posix_crash_guard::RunGuarded(&InitOnlyPodCallback, &ctx);
}
#endif

// Guard a Connect() call the same way. A module newly added to kBootPlan may
// access-violate in Connect() against the partial headless boot (matchmaking is
// the known case: its Connect dereferences an IApplication sub-object the stub
// returns null for). Guarding it means such a module is SKIPPED — it stays out
// of the factory, no worse than not listing it — instead of killing the walk.
// Returns false on either a Connect that returned false OR a caught fault (the
// caller treats both as "not connected"). Transparent for modules that Connect
// cleanly. POD-only frame.
#if defined(_WIN32)
bool CallConnectGuarded(void* appsystem, CreateInterfaceFn factory) {
  __try {
    return CallConnect(appsystem, factory);
  } __except (EXCEPTION_EXECUTE_HANDLER) {
    return false;
  }
}
#else
namespace {
struct ConnectOnlyCtx {
  void* appsystem;
  CreateInterfaceFn factory;
  bool ok;
};
void ConnectOnlyPodCallback(void* p) {
  ConnectOnlyCtx* c = static_cast<ConnectOnlyCtx*>(p);
  c->ok = CallConnect(c->appsystem, c->factory);
}
}  // namespace
bool CallConnectGuarded(void* appsystem, CreateInterfaceFn factory) {
  ConnectOnlyCtx ctx;
  ctx.appsystem = appsystem;
  ctx.factory = factory;
  ctx.ok = false;
  if (!posix_crash_guard::RunGuarded(&ConnectOnlyPodCallback, &ctx)) {
    return false;
  }
  return ctx.ok;
}
#endif

// ---- the module boot plan ----------------------------------------------------
//
// Each entry maps a loaded module (bare name) to the interface-version string
// its primary IAppSystem is exposed under (interfaces.h), plus whether we Init()
// it. CONNECT ALL; INIT ONLY THE GAME-CONFIG SET — Init() is what runs
// ConVar_Register() and flushes convars into ICvar.
//
// ORDER is dependency order (foundation -> services -> game). The factory is
// populated incrementally as each Connect succeeds, so a later module's query
// for an earlier interface is answered with the real pointer.
//
// LIVE-RUN ASSUMPTIONS (most likely to need correction; all gated by trace):
//   * The exact interface-version string each module exposes. Taken verbatim
//     from interfaces.h; if a module's primary appsystem uses a different string
//     than listed there, Connect-ALL still proceeds (we resolve via the module's
//     OWN CreateInterface by trying each known string), but the factory entry
//     would be keyed wrong. The trace prints which string resolved per module.
//   * The Init() set. We Init the *config* app systems of host/server/client/
//     modtools/matchmaking (the convar-bearing game-config modules) rather than
//     the heavy main game interfaces. If a real run shows another module must
//     Init for its
//     convars (e.g. engine2 for engine convars), add it — the trace prints the
//     registry delta after each Init so the contributing set is observable.
struct BootModule {
  const char* module;         // bare module name (matches loader allow-list).
  const char* iface_version;  // primary IAppSystem version string (interfaces.h).
  bool init;                  // Init() this module (runs ConVar_Register)?
};

// The candidate interface strings we try, per module, to obtain its primary
// IAppSystem via its own CreateInterface. interfaces.h is the source.
const BootModule kBootPlan[] = {
    // --- filesystem: CONNECT ONLY, do NOT Init (live-validated + DumpSource2-
    //     confirmed). Init'ing filesystem_stdio access-violates because its Init
    //     mounts the game filesystem from gameinfo, which needs a real
    //     IApplication::GetGameInfo() (we return null). The game-config convar/
    //     schema walk does NOT need a live filesystem — DumpSource2 never Inits
    //     filesystem/resourcesystem/engine2 and never mounts gameinfo by design;
    //     it even skips filesystem-dependent modules rather than mount one. So we
    //     Connect filesystem (keeps it in the factory map for queries) but leave
    //     it un-Init'd. VFileSystem017 (interfaces.h). -----------------------------
    // filesystem_stdio: CONNECT + INIT (guarded). An earlier comment left this
    // Connect-only, believing its Init AVs on a null gameinfo mount — but
    // DumpSource2 Init's it with an equally-null GetGameInfo(), so retest under the
    // crash guard: if the Init faults it is skipped (no worse than connect-only),
    // and if it survives it flushes the fs_* convars.
    {"filesystem_stdio", "VFileSystem017", true},
    // --- engine2: CONNECT + INIT. engine2's Init runs ConVar_Register() for the
    //     engine console + GOTV/demo/host convars (tv_*, demo_*, host_*, engine_*)
    //     and core commands (bind/connect/exec/record/…) — ~116 convars + ~74
    //     commands otherwise unreachable. The primary version MUST be an
    //     IAppSystem-derived interface (Connect@0/Init@3 valid): EngineServiceMgr001
    //     (IEngineServiceMgr : IAppSystem); Source2EngineToClient/Server001 are
    //     QUERY interfaces (NOT app systems) and must never be Connect()'d — they
    //     are only registered into the factory map for other modules to query.
    //     (An earlier comment claimed DumpSource2 leaves engine2 un-Init'd "by
    //     design"; inspecting DumpSource2's actual appframework showed the OPPOSITE
    //     — it connect+inits engine2 — so that assumption was wrong. The Init is
    //     crash-guarded (CallInitGuarded): if it AVs on some era it is skipped, not
    //     fatal.)
    {"engine2", "EngineServiceMgr001", true},
    // resourcesystem: CONNECT + INIT (guarded) for its dev-diagnostic commands
    // (resource_list, resource_leaks, rs_dump_stats, …). DumpSource2 Init's it too.
    // If its Init AVs headless (it may want filesystem state) the guard skips it.
    {"resourcesystem", "ResourceSystem013", true},
    // NETMSG NOTE: Init'ing networksystem here (to register net messages into
    // INetworkMessages) ACCESS-VIOLATES on the partial boot — its Init needs more
    // engine bring-up than is available (same class of wall the convar boot cleared
    // for the config modules). Left Connect-only; the net-message REGISTRY therefore
    // stays empty headless (the netmsg reader below is ready for when a non-crashing
    // registration trigger is found). See netmsg_walk.cpp.
    {"networksystem", "NetworkSystemVersion001", false},
    {"animationsystem", "AnimationSystem_001", false},
    {"particles", "ParticleSystemMgr003", false},
    {"vphysics2", "VPhysics2_Interface_001", false},
    {"scenesystem", "SceneSystem_002", false},
    {"soundsystem", "SoundSystem001", false},
    // --- panorama + materialsystem2: CONNECT + INIT for their convars/commands.
    //     Both were already loaded for schema but never in the boot plan, so they
    //     were never Connected and their convars never registered. Adding them takes
    //     panorama_ 23 -> 101 and mat_ 28 -> 44 (both to full parity with the
    //     DumpSource2 upstream) on build 24134959. Measured notes:
    //       * panorama's convars register during Connect/static-init, not Init — the
    //         per-Init registry delta is 0 but the final registry gains ~78 panorama_.
    //       * materialsystem2's Init target is TextLayout_001, NOT VMaterialSystem2_001:
    //         the latter RESOLVES but its Init access-violates (it wants a live render
    //         device); TextLayout_001 Init's cleanly and flushes the mat_ convars.
    //     Both Connect and Init are crash-guarded, so if either faults on some era the
    //     module is skipped, not fatal.
    {"panorama", "PanoramaUIEngine001", true},
    {"materialsystem2", "TextLayout_001", true},
    // --- inputsystem + panoramauiclient: CONNECT + INIT for joy_/input_ and any
    //     residual panorama_ convars. Not schema modules; added to the load set +
    //     boot plan solely for their convar registrations. Crash-guarded.
    {"inputsystem", "InputSystemVersion001", true},
    {"panoramauiclient", "PanoramaUIClient001", true},
    // --- pulse_system + worldrenderer: CONNECT + INIT (guarded) for their
    //     dev-diagnostic commands — pulse_* (pulse_list_graphs, pulse_debug_print,
    //     …) and world_*/entity_lump_* (world_layer_list, entity_lump_spew, …).
    //     Loaded for schema but never booted. DumpSource2 Init's worldrenderer;
    //     worldrenderer's Init MAY want the render device (guarded -> skipped if so).
    {"pulse_system", "IPulseSystem_001", true},
    {"worldrenderer", "WorldRendererMgr001", true},
    // render backends: CONNECT here, but Init is DEFERRED to a dedicated final phase
    // AFTER the data-subsystem Connect+Init (see the deferred-render-init block below).
    // The Init's ConVar_Register flushes the r_*/r_dx11_*/r_vulkan_* render convars — and
    // needs NO GPU or software rasterizer (measured: they register regardless of whether
    // a device is created). It is deferred only because Init ALSO tries to create a live
    // device as a side effect, which enters a frame-update state that would make the
    // data-subsystem phase's resource-manifest load reentrant and fatally abort inline.
    // Both backends are listed; only the platform's is loaded (the other's module file is
    // absent and is skipped). init=false here (deferred).
    {"rendersystemdx11", "RenderDeviceMgr001", false},
    {"rendersystemvulkan", "RenderDeviceMgr001", false},
    // schemasystem is deliberately NOT Init'd here. Its Init WOULD register the six
    // schema_* dev-diagnostic commands (schema_stats, schema_dump_binding, …), but
    // it repopulates the type scopes and flips the projectName/typeModule
    // attribution of 183 classes registered under both server and client — measured.
    // The schema is the primary product; perturbing 183 classes' attribution to gain
    // six developmentonly commands is a bad trade, so we forgo them. (If wanted, the
    // clean path is a LATE schemasystem Init after the schema walk but before the
    // command walk — a separate, more invasive change.)
    // --- game-config set: CONNECT + INIT the LIGHTER *config* app systems, not
    //     the heavy main game interfaces. Init runs ConVar_Register; the config
    //     app systems flush the game convars without dragging the full client/
    //     server runtime online. Version strings verified against interfaces.h
    //     where a macro exists; "Source2ClientConfig001"/"GameSystem2HostHook"
    //     have no macro in the pinned header and are used as literals (the boot
    //     trace prints which string actually resolves per module).
    // Init ORDER matches DumpSource2's g_appSystems: client-config FIRST, then
    // host, then server-config. host's Init appears to expect client's game
    // systems already Init'd (host AV'd when Init'd before client).
    {"client", "Source2ClientConfig001", true},
    {"host", "GameSystem2HostHook", true},
    {"server", SOURCE2SERVERCONFIG_INTERFACE_VERSION, true},  // "Source2ServerConfig001"
                                                              // modtools: optional, skipped if the module isn't loaded (FindLoaded null ->
                                                              // connect.skip/init.skip). Source2ModTools001 config app system.
                                                              //
                                                              // HL2SDK API-DRIFT GUARD (per-era backfill, analogous to tier0_link_stubs.cpp):
                                                              // SOURCE2MODTOOLS_INTERFACE_VERSION was ADDED to hl2sdk after mid-2025 — it is
                                                              // present in the current pin (b8dcaf14) but ABSENT in older pins (e.g.
                                                              // 07f35e15, 2025-07-18). This is a COMPILE-TIME availability check, NOT a
                                                              // runtime fallback: when the macro is undefined we
                                                              // simply omit this one boot-plan element so kBootPlan stays a well-formed,
                                                              // size-deduced initialized array. The range-fors over kBootPlan are unaffected.
                                                              //
                                                              // DOCUMENTED PER-ERA LIMITATION: omitting modtools on older hl2sdk means the
                                                              // modtools module's convars/commands are NOT registered for those eras. This
                                                              // is acceptable and intentional — modtools is an OPTIONAL config app system
                                                              // (already skipped at runtime when the module isn't loaded), and the core
                                                              // schema-bearing modules plus the cvar core + client/server/host config (the
                                                              // bulk of sv_/mp_/cl_/r_/bot_ convars) are unaffected. The alternative — no
                                                              // extraction at all for the era — is strictly worse.
#ifdef SOURCE2MODTOOLS_INTERFACE_VERSION
    {"modtools", SOURCE2MODTOOLS_INTERFACE_VERSION, true},  // "Source2ModTools001"
#endif
// matchmaking (CS2 lobby/matchmaking, mm_* convars): its Connect dereferences
// the RESULT of an IApplication sub-object lookup our stub returns null for, so
// it historically access-violated and was DEFERRED. Now that both Connect and
// Init are crash-guarded (CallConnectGuarded/CallInitGuarded), listing it is
// safe: if the Connect still AVs on an era it is caught and the module skipped,
// never fatal. #ifdef-guarded on the interface macro (present in current + mid-
// 2025 pins, may be absent in even-older ones) so the per-era backfill build
// stays well-formed, exactly like modtools above.
#ifdef MATCHFRAMEWORK_INTERFACE_VERSION
    {"matchmaking", MATCHFRAMEWORK_INTERFACE_VERSION, true},  // "MATCHFRAMEWORK_001"
#endif
};

// Every interface-version string a given module is known to expose, so we can
// register ALL of them into the factory (a later module may query any). Keyed by
// bare module name. Strings verbatim from interfaces.h. Where a module exposes
// many interfaces we list the ones other modules are most likely to query.
struct ModuleInterfaces {
  const char* module;
  const char* versions[8];  // null-terminated.
};
const ModuleInterfaces kModuleInterfaces[] = {
    {"filesystem_stdio",
     {"VFileSystem017", "VAsyncFileSystem2_001", nullptr}},
    // EngineServiceMgr001 FIRST (the IAppSystem-derived primary); the rest are
    // query interfaces registered into the factory only, never Connect()'d.
    {"engine2",
     {"EngineServiceMgr001", "Source2EngineToClient001", "Source2EngineToServer001",
      "HostStateMgr001", "NetworkServerService_001",
      "NetworkClientService_001", "GameResourceServiceClientV001", nullptr}},
    {"resourcesystem", {"ResourceSystem013", nullptr}},
    {"networksystem",
     {"NetworkSystemVersion001", "NetworkMessagesVersion001",
      "SerializedEntitiesVersion001", "FlattenedSerializersVersion001",
      nullptr}},
    {"animationsystem", {"AnimationSystem_001", "AnimationSystemUtils_001", nullptr}},
    {"particles", {"ParticleSystemMgr003", nullptr}},
    {"vphysics2", {"VPhysics2_Interface_001", "VPhysics2_Handle_Interface_001", nullptr}},
    {"scenesystem", {"SceneSystem_002", "SceneUtils_001", nullptr}},
    {"soundsystem", {"SoundSystem001", nullptr}},
    // panorama: app system first, then query interfaces. materialsystem2: the
    // material app system first, TextLayout_001 as a fallback (DumpSource2 uses
    // TextLayout as materialsystem2's Init target on some builds).
    {"panorama", {"PanoramaUIEngine001", nullptr}},
    {"materialsystem2", {"TextLayout_001", "VMaterialSystem2_001", nullptr}},
    {"inputsystem", {"InputSystemVersion001", nullptr}},
    {"panoramauiclient", {"PanoramaUIClient001", "PanoramaUIEngine001", nullptr}},
    {"pulse_system", {"IPulseSystem_001", "PulseSystem_001", nullptr}},
    {"worldrenderer", {"WorldRendererMgr001", nullptr}},
    {"rendersystemdx11", {"RenderDeviceMgr001", "RenderUtils_001", "RenderDevice003", nullptr}},
    {"rendersystemvulkan", {"RenderDeviceMgr001", "RenderUtils_001", "RenderDevice003", nullptr}},
    // host: the config app system we Init ("GameSystem2HostHook") first, then the
    // main host interface for other modules to query.
    {"host", {"GameSystem2HostHook", "Source2Host001", nullptr}},
    {"matchmaking", {"MATCHFRAMEWORK_001", nullptr}},
    // modtools: optional config app system; only used when the module is loaded.
    {"modtools", {"Source2ModTools001", nullptr}},
    // server: config app system first (the Init target), main + game query
    // interfaces after so other modules can still pull them.
    {"server", {"Source2ServerConfig001", "Source2Server001", "Source2GameClients001", "Source2GameEntities001", nullptr}},
    // client: config app system first (the Init target), main + UI/prediction
    // query interfaces after.
    {"client", {"Source2ClientConfig001", "Source2Client002", "Source2ClientUI001", "Source2ClientPrediction001", nullptr}},
};

const LoadedModule* FindLoaded(const InProcessEnvironment& env, const char* bare) {
  for (const auto& m : env.modules()) {
    // ASCII case-insensitive compare (shared helper; same lowering as before).
    if (EqCi(m.module_name(), bare)) return &m;
  }
  return nullptr;
}

const ModuleInterfaces* InterfacesFor(const char* bare) {
  for (const auto& mi : kModuleInterfaces) {
    if (std::strcmp(mi.module, bare) == 0) return &mi;
  }
  return nullptr;
}

// Resolve a module's primary IAppSystem from its CreateInterface factory.
// TRY-ORDER (load-bearing): the planned `primary` version string FIRST; if that
// is null / rc!=0, fall back to every string in InterfacesFor(module)->versions[]
// in declaration order, taking the FIRST that returns non-null with rc==0.
// Returns nullptr if nothing resolves. When `resolved_version` is non-null it is
// set to the version string that produced the returned appsystem (the Connect
// pass needs it for tracing and the single-interface factory fallback; the Init
// and data-subsystem passes pass nullptr and discard it). This is the exact
// sequence the three boot passes share — see callers below.
void* ResolveModuleAppSystem(CreateInterfaceFn factory, const char* module,
                             const char* primary,
                             std::string* resolved_version = nullptr) {
  int rc = 0;
  void* appsystem = factory(primary, &rc);
  if (appsystem != nullptr && rc == 0) {
    if (resolved_version != nullptr) *resolved_version = primary;
    return appsystem;
  }
  const ModuleInterfaces* mi = InterfacesFor(module);
  if (mi != nullptr) {
    for (int i = 0; mi->versions[i] != nullptr; ++i) {
      int rc2 = 0;
      void* a = factory(mi->versions[i], &rc2);
      if (a != nullptr && rc2 == 0) {
        if (resolved_version != nullptr) *resolved_version = mi->versions[i];
        return a;
      }
    }
  }
  return nullptr;
}

// ---- data-subsystem lazy-schema Connect+Init plan ----------------------------
//
// The pure data subsystems whose schema client/server embed by value but which
// register LAZILY on older builds (see ConnectInitSubsystemGuarded). Each entry's
// interface-version string is the macro from interfaces.h — #ifdef-guarded so the
// list stays well-formed across hl2sdk pins that lack a given macro (same per-era
// availability discipline as the SOURCE2MODTOOLS_INTERFACE_VERSION boot-plan
// entry). All five macros are present in the current pin; the guards are forward/
// backward insurance for other pins.
//
// These are NOT init=true in kBootPlan: kBootPlan Inits the convar-bearing game-
// config modules unconditionally, whereas these run ONLY when their scope is
// still empty after the normal boot (the idempotency gate below), so newer eras
// stay byte-identical.
struct SubsystemModule {
  const char* module;         // bare module name (loader allow-list).
  const char* iface_version;  // primary IAppSystem version string (interfaces.h).
};
const SubsystemModule kDataSubsystems[] = {
#ifdef ANIMATIONSYSTEM_INTERFACE_VERSION
    {"animationsystem", ANIMATIONSYSTEM_INTERFACE_VERSION},  // "AnimationSystem_001"
#endif
#ifdef PARTICLESYSTEMMGR_INTERFACE_VERSION
    {"particles", PARTICLESYSTEMMGR_INTERFACE_VERSION},  // "ParticleSystemMgr003"
#endif
#ifdef VPHYSICS2_INTERFACE_VERSION
    {"vphysics2", VPHYSICS2_INTERFACE_VERSION},  // "VPhysics2_Interface_001"
#endif
#ifdef SCENESYSTEM_INTERFACE_VERSION
    {"scenesystem", SCENESYSTEM_INTERFACE_VERSION},  // "SceneSystem_002"
#endif
#ifdef SOUNDSYSTEM_INTERFACE_VERSION
    {"soundsystem", SOUNDSYSTEM_INTERFACE_VERSION},  // "SoundSystem001"
#endif
};

// True if `module` has a registered type scope with at least one class or enum
// binding in the LIVE schema system. This is the idempotency gate: the extra
// Connect+Init below runs ONLY when this is false. Uses the same header-inline
// accessors as schema_walk.cpp::ScopeHasBindings (no new layout). A null
// schema_system (boot reached with the loader not having populated it) is treated
// as "no bindings" — but the boot's own empty-registry gate at the end is the real check;
// here we only decide whether to attempt the lazy Init.
bool SubsystemScopeHasBindings(void* schema_system, const char* module) {
  if (schema_system == nullptr) return false;
  auto* system = reinterpret_cast<CSchemaSystem*>(schema_system);
  CSchemaSystemTypeScope* scope = system->FindTypeScopeForModule(module);
  if (scope == nullptr) return false;
  return (scope->m_ClassBindings.Count() > 0) ||
         (scope->m_EnumBindings.Count() > 0);
}

// Set the process working directory to the game CONTENT dir (game/csgo) so the
// filesystem + game-module Init can resolve gameinfo.gi and search paths
// relative to CWD. DumpSource2 runs from the real game bin dir;
// we reproduce that by deriving the content dir from a loaded game module path.
//
// A game module (client/server) lives at <root>/game/csgo/bin/<plat>/<mod>.dll,
// so the content dir is three parents up from the .dll. We chdir there; the
// filesystem then bootstraps its search paths from gameinfo.gi as the real game
// would. Best-effort: a failure is traced, not fatal — the boot may still work
// if the modules locate files another way, and the final empty-registry check
// is the real fail-loud gate.
// Copy a std::string into a fixed char[] global, NUL-terminated, never
// overflowing. Used to publish the directory strings the IApplication getters
// return (they must outlive every Connect/Init call, hence static storage).
void PublishDir(char* dst, size_t cap, const std::string& s) {
  if (cap == 0) return;
  size_t n = s.size();
  if (n >= cap) n = cap - 1;
  std::memcpy(dst, s.data(), n);
  dst[n] = '\0';
}

void SetGameWorkingDirectory(const InProcessEnvironment& env) {
  const LoadedModule* game = FindLoaded(env, "client");
  if (game == nullptr) game = FindLoaded(env, "server");
  if (game == nullptr) {
    BTrace("cwd.skip", "no client/server module to derive content dir from");
    return;
  }
  std::error_code ec;
  std::filesystem::path p(game->path());             // .../game/csgo/bin/<plat>/client.dll
  std::filesystem::path game_bin = p.parent_path();  // .../game/csgo/bin/<plat>
  std::filesystem::path content =
      p.parent_path().parent_path().parent_path();  // .../game/csgo
  if (content.empty()) {
    BTrace("cwd.skip", "could not derive content dir from " + game->path());
    return;
  }

  // Publish the directory strings the IApplication getters hand back. The
  // gameinfo path getter in Source2 returns the DIRECTORY that holds gameinfo.gi,
  // which is the content (game/csgo) dir itself.
  PublishDir(g_app_content_dir, sizeof(g_app_content_dir), content.string());
  PublishDir(g_app_game_bin_dir, sizeof(g_app_game_bin_dir), game_bin.string());
  PublishDir(g_app_gameinfo_path, sizeof(g_app_gameinfo_path), content.string());
  BTrace("app.dirs",
         "content=" + std::string(g_app_content_dir) +
             " gameBin=" + std::string(g_app_game_bin_dir));

  std::filesystem::current_path(content, ec);
  if (ec) {
    BTrace("cwd.FAILED", content.string() + ": " + ec.message());
  } else {
    BTrace("cwd", content.string());
  }
}

// Count registered convars via the index-based ref API (same mechanism the convar
// walk uses) so we can report the registry delta after each Init.
int CountConVars(ICvar* cvar) {
  int n = 0;
  for (convar_compat::WConVarIter it = convar_compat::WCvarFirstConVar(cvar);
       it.IsValid(); it = convar_compat::WCvarNextConVar(cvar, it)) {
    ++n;
  }
  return n;
}

}  // namespace

bool BootEngineForConVars(InProcessEnvironment& env, std::string* err) {
  void* schema_system = env.schema_system();
  void* cvar_void = env.cvar();
  if (cvar_void == nullptr) {
    *err = "engine boot: null ICvar (loader did not obtain VEngineCvar007)";
    return false;
  }
  auto* cvar = reinterpret_cast<ICvar*>(cvar_void);

  BTrace("begin", "partial engine boot for convar/command extraction");

  // 0) Point the process CWD at the game content dir so filesystem + game-module
  //    Init can resolve gameinfo.gi / search paths.
  SetGameWorkingDirectory(env);

  // 1) Build the incremental factory table, seeded with the REAL foundational
  //    interfaces the loader already obtained: ICvar, CSchemaSystem, and our
  //    fuller IApplication (slot-classified, deref-safe by default — see the
  //    IApplication section). Everything else is added as modules Connect.
  FactoryTable table;
  table.map[CVAR_INTERFACE_VERSION] = cvar_void;              // "VEngineCvar007"
  table.map[SCHEMASYSTEM_INTERFACE_VERSION] = schema_system;  // "SchemaSystem_001"
  table.map[APPLICATION_INTERFACE_VERSION] =
      reinterpret_cast<void*>(GetWalkerApplication());  // "VApplication001"
  // Seed INetworkMessages so each module's ConnectInterfaces wires its tier2/3
  // g_pNetworkMessages to the SAME instance the loader holds. Without this the boot
  // factory returns null for "NetworkMessagesVersion001", g_pNetworkMessages stays
  // null in every module, and the modules' protobuf message bindings have nowhere to
  // register — leaving INetworkMessages::FindNetworkMessageById empty (the
  // "registry not populated headless" gap). Harmless when null (older boot path).
  if (env.network_messages() != nullptr) {
    table.map["NetworkMessagesVersion001"] = env.network_messages();
  }
  g_table = &table;
  BTrace("factory.seed",
         "ICvar + CSchemaSystem + WalkerApplication + INetworkMessages registered");

  // 1b) Connect+Init the CVAR CORE itself against TIER0's factory.
  //
  // ROOT CAUSE (symbol-resolved cdb dump): client.dll's Init()
  // calls into tier0's ICVar-factory import machinery
  // (tier0!Import_GetICVarFactory), which invoked a NULL function pointer
  // (rip=0). The boot had obtained `cvar = env.cvar()` and Connect+Init'd the
  // game-config MODULES, but it NEVER Connect+Init'd the cvar CORE against
  // tier0's own factory. tier0 keeps an INTERNAL cvar-factory import pointer
  // that is only wired when the ICvar is Connect()'d with tier0's
  // CreateInterfaceFn. Until that happens the pointer stays null, so the first
  // module Init that reaches into tier0's cvar machinery calls null -> AV.
  //
  // The equivalent in DumpSource2's InitializeCoreModules is:
  //     cvar->Connect(tier0->GetFactory());  // wires tier0's cvar import
  //     cvar->Init();                        // inits the cvar system
  // We do ONLY this cvar core Connect+Init here. We deliberately do NOT add
  // DumpSource2's schemaSystem Connect+Init from the same core step: our schema
  // path is already wired by the loader (InstallSchemaBindings /
  // LoadSchemaDataForModules), and re-Connecting/Init'ing it here would
  // double-init it.
  //
  // tier0's factory == the "CreateInterface" export of the loaded tier0 module
  // (kCreateInterfaceSymbol), resolved exactly the way the module Connect loop
  // below resolves each module's appsystem factory. This is the clean-room
  // equivalent of DumpSource2's `Modules::tier0->GetFactory()`.
  {
    const LoadedModule* tier0 = FindLoaded(env, "tier0");
    if (tier0 == nullptr) {
      // tier0 is a REQUIRED module (loader allow-list); its absence here means
      // the loader contract changed. Fail loud rather than boot a cvar core with
      // no tier0 factory and crash later in module Init.
      *err =
          "engine boot: tier0 module not loaded; cannot wire the cvar core "
          "against tier0's CreateInterface factory (its cvar-factory import "
          "would stay null and module Init would call through a null pointer)";
      g_table = nullptr;
      return false;
    }
    auto tier0_factory = reinterpret_cast<CreateInterfaceFn>(
        tier0->ResolveSymbol(kCreateInterfaceSymbol));
    if (tier0_factory == nullptr) {
      *err =
          "engine boot: tier0 module exposes no CreateInterface export; cannot "
          "wire the cvar core against tier0's factory (: refusing to "
          "proceed with an un-wired cvar core that would null-deref in module "
          "Init)";
      g_table = nullptr;
      return false;
    }

    // ICvar derives IAppSystem, so Connect@0 / Init@3 are valid on cvar_void
    // via our existing front-of-vtable helpers. Connect against TIER0's factory
    // (NOT our incremental BootFactory) — it is tier0's own import pointer we
    // are wiring. Note: there is no clean way from here to set the DLL-SIDE
    // tier1 g_pCVar global (it lives inside tier1, statically linked into each
    // module, and we hold only the live ICvar*). We rely on the
    // Connect(tier0Factory) path, which sets tier0's cvar import — the missing
    // step the cdb dump pinpointed. If a later trace shows a tier1 inline
    // convar registration still missing g_pCVar, that global must be set on the
    // DLL side, which this boot cannot reach.
    BTrace("cvar.connect", "ICvar->Connect(tier0 CreateInterface factory)");
    bool cvar_connected = CallConnect(cvar_void, tier0_factory);
    BTrace("cvar.connect.result", cvar_connected ? "true" : "false");

    BTrace("cvar.init", "ICvar->Init()");
    int cvar_init_ret = CallInit(cvar_void);
    int cvar_count = CountConVars(cvar);
    BTrace("cvar.init.result",
           "ret=" + std::to_string(cvar_init_ret) +
               " convars=" + std::to_string(cvar_count));
  }

  // 2) Connect every planned module IN ORDER, registering each module's
  //    interfaces into the factory as we go, so a later Connect's queries hit
  //    real pointers. Foundation first, game-config last.
  for (const BootModule& bm : kBootPlan) {
    const LoadedModule* lm = FindLoaded(env, bm.module);
    if (lm == nullptr) {
      // Module not loaded (optional + absent on this platform). Skip — the
      // loader already enforced REQUIRED presence; absence here is tolerated.
      BTrace("connect.skip", std::string(bm.module) + " (not loaded)");
      continue;
    }
    auto factory = reinterpret_cast<CreateInterfaceFn>(
        lm->ResolveSymbol(kCreateInterfaceSymbol));
    if (factory == nullptr) {
      BTrace("connect.skip", std::string(bm.module) + " (no CreateInterface)");
      continue;
    }

    // Obtain the module's primary IAppSystem. Try the planned version first,
    // then every known version for the module, taking the first that resolves.
    // resolved_version is captured here for the trace below and the
    // single-interface factory fallback at the bottom of this block.
    std::string resolved_version;
    void* appsystem = ResolveModuleAppSystem(factory, bm.module,
                                             bm.iface_version, &resolved_version);
    if (appsystem == nullptr) {
      // Could not obtain ANY known interface from this module. Non-fatal here:
      // we proceed; the module simply isn't in the factory. A required module
      // that the game-config set depends on will surface as a crash or empty
      // registry, which we fail loud on at the end.
      BTrace("connect.no-iface", bm.module);
      continue;
    }

    // Register ALL of this module's known interfaces into the factory, pointing
    // at the same primary appsystem object where the module returns it. We query
    // each known version through the module's own factory so the pointer is the
    // module's REAL interface (a module often returns distinct objects per
    // version; we store whatever it returns, never a guess).
    // Module key the host attributes by: the bare module name
    // (LoadedModule::module_name(), the same key schema_registration_count uses).
    const std::string module_key = lm->module_name();
    const ModuleInterfaces* mi = InterfacesFor(bm.module);
    if (mi != nullptr) {
      for (int i = 0; mi->versions[i] != nullptr; ++i) {
        int rc3 = 0;
        void* a = factory(mi->versions[i], &rc3);
        if (a != nullptr && rc3 == 0) {
          table.map[mi->versions[i]] = a;
          // A non-null factory return with rc==0 is a RESOLVED interface version
          // this module exposes. Record it on the environment (it outlives
          // this function; the local FactoryTable does not). CONNECT pass only —
          // the Init / data-subsystem re-resolves below intentionally do NOT record.
          env.RecordResolvedInterface(module_key, mi->versions[i]);
        }
      }
    } else {
      table.map[resolved_version] = appsystem;
      // No curated interface list for this module, but ResolveModuleAppSystem
      // returned a real appsystem under resolved_version — that IS a resolved version.
      if (!resolved_version.empty())
        env.RecordResolvedInterface(module_key, resolved_version);
    }

    // Connect through the front-of-vtable slot 0. Pass our incremental factory
    // so the module's Connect can pull the interfaces it needs. Guarded: a module
    // whose Connect access-violates against the partial boot is skipped, not fatal.
    BTrace("connect", std::string(bm.module) + " via " + resolved_version);
    bool ok = CallConnectGuarded(appsystem, &BootFactory);
    BTrace("connect.result",
           std::string(bm.module) + "=" + (ok ? "true" : "false"));
  }

  int before = CountConVars(cvar);
  BTrace("registry.before-init", std::to_string(before) + " convars");

  // 3) Install the crash-patches for the Init() window, then Init() ONLY the
  //    game-config set. Init runs each module's ConVar_Register(), flushing its
  //    convars into our ICvar.
  {
    // Crash-patch (a): no-op ICvar::CallChangeCallback (slot 14) during Init so
    // a lazy change-handler can't drag in a heavy subsystem and crash.
    // (Not the cause of the client-config Init AV — disabling it leaves the crash at
    // the identical point. Kept because it's still required once Init proceeds past the
    // current wall.)
    CvarChangeCallbackPatch change_patch(cvar_void);

    // Determinism-patch: BELT-AND-SUSPENDERS per-Init re-seed of the engine's
    // global uniform random stream to a FIXED constant. The authoritative seed +
    // the full root-cause rationale (cl_color baked at client.dll static-init,
    // the shared-stream / RandomSeed-export proof) live in
    // loader.cpp's post-tier0-load RandomSeed block — that one runs first and is
    // what actually makes cl_color reproducible. This re-seed only covers RandomInt
    // calls made DURING a game-config Init() (not at static-init), so it has NO
    // effect on cl_color; do NOT re-chase cl_color here. It is kept because reading
    // a faithful-but-randomized registration default is legitimate only if the
    // engine's OWN registration is reproducible — we re-seed, we never fabricate a
    // value. (Aliasing ruled out: hl2sdk DefaultValue() returns the registered
    // default slot m_defaultValue (convar.h:949/1000), a separate pointer from the
    // live per-slot m_Values[] (convar.h:1030) — so we are not reading the current
    // value by mistake.)
    using RandomSeedFn = void (*)(int);
    RandomSeedFn random_seed = nullptr;
    {
      const LoadedModule* tier0 = FindLoaded(env, "tier0");
      if (tier0 != nullptr) {
        random_seed =
            reinterpret_cast<RandomSeedFn>(tier0->ResolveSymbol("RandomSeed"));
      }
      BTrace("rng.seed.resolve",
             random_seed != nullptr
                 ? "tier0!RandomSeed resolved (will fix-seed before each Init)"
                 : "tier0!RandomSeed NOT resolved (randomized convar defaults "
                   "like cl_color may be non-deterministic — risk)");
    }
    // Fixed seed applied before each game-config Init. Constant is arbitrary; 0
    // is fine. Re-applied per Init so a self-seed inside any earlier module Init
    // is always overridden right before the registrant (client) runs.
    constexpr int kFixedRandomSeed = 0;

    // Crash-patch (b): force r_dopixelvisibility = false. The convar may not be
    // registered until a module Inits, so we (re)apply it after each Init below.
    // The helper writes the bool default directly via the index API (no SetValue
    // vtable call, so it bypasses any change-handler entirely).
    // Era-neutral (convar_compat.h): NEW era resolves ConVarRef -> ConVarData
    // and writes DefaultValue()->m_bValue (exactly the prior code path); OLD era
    // resolves ConVarHandle -> ConVar* and writes the same default-value union
    // via the pinned struct layout. If the convar is missing or not a bool the
    // helper is a safe no-op (the patch is a crash mitigation, not correctness).
    auto force_pixelvis_false = [&]() {
      if (convar_compat::WForceBoolConVarDefaultFalse(cvar,
                                                      "r_dopixelvisibility")) {
        BTrace("crash-patch", "r_dopixelvisibility forced false");
      }
    };

    for (const BootModule& bm : kBootPlan) {
      if (!bm.init) continue;
      const LoadedModule* lm = FindLoaded(env, bm.module);
      if (lm == nullptr) {
        BTrace("init.skip", std::string(bm.module) + " (not loaded)");
        continue;
      }
      // Re-resolve the module's primary appsystem the same way as the Connect
      // pass (its factory returns the same singleton).
      auto factory = reinterpret_cast<CreateInterfaceFn>(
          lm->ResolveSymbol(kCreateInterfaceSymbol));
      if (factory == nullptr) continue;
      void* appsystem =
          ResolveModuleAppSystem(factory, bm.module, bm.iface_version);
      if (appsystem == nullptr) {
        BTrace("init.no-iface", bm.module);
        continue;
      }

      force_pixelvis_false();  // before, in case Init reads it.
      // Fix-seed the global uniform stream right before this Init so any
      // RandomInt-based convar default registered here (e.g. client's cl_color)
      // is reproducible across runs.
      if (random_seed != nullptr) {
        random_seed(kFixedRandomSeed);
        BTrace("rng.seed.apply",
               std::string(bm.module) + " seeded=" +
                   std::to_string(kFixedRandomSeed));
      }
      BTrace("init", bm.module);
      bool ran = CallInitGuarded(appsystem);
      int after = CountConVars(cvar);
      if (ran) {
        BTrace("init.result",
               std::string(bm.module) + " convars=" + std::to_string(after) +
                   " (+" + std::to_string(after - before) + ")");
      } else {
        // Init access-violated against the partial boot; skipped (its convars
        // stay unregistered). No worse than leaving the module Connect-only.
        BTrace("init.faulted",
               std::string(bm.module) + " (Init AV caught; module skipped)");
      }
      before = after;
      force_pixelvis_false();  // after, in case Init reset it.
    }
  }  // crash-patch reverted here.

  // 3b) DATA-SUBSYSTEM LAZY-SCHEMA Connect+Init (older-build regression fix +
  //     WINDOWS 2023 CONVAR/COMMAND under-read fix).
  //
  // On older builds (e.g. 18451221 / 2025-05-13) animationsystem / particles /
  // vphysics2 / scenesystem / soundsystem register their schema only on the full
  // AppSystem lifecycle (Connect+Init), not on load / InstallSchemaBindings /
  // LoadSchemaDataForModules. We drive that here. On LINUX this runs ONLY for a
  // loaded subsystem whose schema scope is STILL EMPTY at this point — so on every
  // era that already registers eagerly the gate is false and this phase is a no-op,
  // keeping the emitted schema byte-identical.
  //
  // WINDOWS 2023 CONVAR ASYMMETRY. The subsystem Connect+Init's Init()
  // also runs the subsystem's ConVar_Register, which flushes ~479 data-subsystem
  // convars (adsp_/snd_/animgraph_/cloth_/csm_/ik_/...) and ~100 commands into the
  // live ICvar. On LINUX (ELF) a loaded subsystem's schema scope stays EMPTY until
  // this Init runs, so the scope-empty gate fires the Init and those convars flush
  // (2974 convars on build 10832117). On WINDOWS (PE) each subsystem module registers
  // its schema EAGERLY at LoadLibrary (static init), so its scope is ALREADY POPULATED
  // here and the scope-empty gate SKIPS the Init — its convars/commands NEVER flush,
  // leaving windows 2023 short by ~479 convars / ~100 commands (2495 vs linux 2974).
  // The convars/commands ARE real (present on linux and in every modern build), so this
  // is a genuine windows under-read, NOT a mirror-geometry bug (the 2023 convar mirror
  // reads every convar that IS registered; the missing ones were never registered).
  //
  // FIX: on WINDOWS, ATTEMPT the guarded Connect+Init for every loaded data subsystem
  // regardless of the scope-empty gate, so its convars/commands flush the way they do
  // on linux. This is SAFE and platform-gated:
  //   - windows 2023: the subsystems' scopes are populated (skip pre-fix); forcing runs
  //     their Init -> flushes the missing convars/commands -> 2975 convars / 736 commands
  //     matching linux (2974/735 + one windows-only each). Schema stays byte-identical
  //     (re-registration into an already-populated scope is idempotent — validated: the
  //     entity_schema class/enum stream is unchanged). Deterministic across runs even
  //     though a few subsystems SEH-fault mid-Init (they flush their convars before the
  //     fault; the fault point is stable).
  //   - windows MODERN: forcing is a proven NO-OP (byte-identical output validated on
  //     build 23669931) — the data-subsystem convars are already registered, so their
  //     re-Init changes nothing.
  //   - LINUX: this force is #if-gated OUT; linux keeps EXACTLY the original scope-empty
  //     gate (its scopes are empty on 2023 so the Init already runs; byte-identical).
  // We cannot gate on env.schema_is_2023_era() here because the era flag is set later
  // (RetrySchemaRegistrationIfEmpty runs AFTER this boot), so the windows force is
  // unconditional — which is correct because it is a byte-identical no-op on modern.
  //
  // Each subsystem's Connect+Init is SEH-guarded on Windows: a
  // faulting subsystem is caught and skipped, the boot continues, and the working
  // eras are not regressed. See ConnectInitSubsystemGuarded. The factory table
  // (g_table) is still installed here, so a subsystem's Connect can pull the real
  // foundation interfaces it queries.
  //
  // This is BELT-AND-SUSPENDERS WITH the schema_walk.cpp per-module
  // LoadSchemaDataForModules trigger: that trigger is the cheap,
  // crash-free first attempt (and remains the only thing that runs on eras where
  // it suffices). Kept separate because the schema walk runs AFTER this boot in
  // RunWalk, so its trigger also covers the case where this boot is skipped/changed.
  {
    // WINDOWS: force the Connect+Init even when the schema scope is already populated,
    // to flush the data-subsystem convars/commands (see block comment). LINUX: false —
    // keep the original scope-empty gate exactly (byte-identical).
#if defined(_WIN32)
    const bool force_subsys = true;
#else
    const bool force_subsys = false;
#endif
    void* schema_system_for_gate = env.schema_system();
    for (const SubsystemModule& sm : kDataSubsystems) {
      // Idempotency gate: skip if this subsystem already has bindings (newer
      // eras, or an earlier phase already populated it). Strict no-op there.
      // On WINDOWS the gate is bypassed (force_subsys) so subsystem convars flush.
      if (!force_subsys && SubsystemScopeHasBindings(schema_system_for_gate, sm.module)) {
        BTrace("subsys.skip",
               std::string(sm.module) + " (scope already populated)");
        continue;
      }
      const LoadedModule* lm = FindLoaded(env, sm.module);
      if (lm == nullptr) {
        BTrace("subsys.skip", std::string(sm.module) + " (not loaded)");
        continue;
      }
      auto factory = reinterpret_cast<CreateInterfaceFn>(
          lm->ResolveSymbol(kCreateInterfaceSymbol));
      if (factory == nullptr) {
        BTrace("subsys.skip", std::string(sm.module) + " (no CreateInterface)");
        continue;
      }
      // Resolve the subsystem's primary IAppSystem (same fallback search as the
      // Connect/Init passes above).
      void* appsystem =
          ResolveModuleAppSystem(factory, sm.module, sm.iface_version);
      if (appsystem == nullptr) {
        BTrace("subsys.no-iface", sm.module);
        continue;
      }

      BTrace("subsys.connect-init",
             std::string(sm.module) + " scope empty; guarded Connect+Init");
      SubsysBootResult r = ConnectInitSubsystemGuarded(appsystem, &BootFactory);
      const bool now = SubsystemScopeHasBindings(schema_system_for_gate, sm.module);
      const char* rs = (r == SubsysBootResult::kRan)             ? "ran"
                       : (r == SubsysBootResult::kConnectFailed) ? "connect-failed"
                                                                 : "FAULTED-skipped";
      BTrace("subsys.connect-init.result",
             std::string(sm.module) + " result=" + rs +
                 " populated=" + (now ? "1" : "0"));
    }
  }

  // 3c) DEFERRED RENDER-SYSTEM INIT — LAST engine-heavy step.
  //
  // The render backend (rendersystemdx11 on windows, rendersystemvulkan on linux) was
  // Connected in the pass above but intentionally NOT Init'd there. Its Init() runs
  // ConVar_Register, which is what flushes the r_texture_*/r_dx11_* (or r_vulkan_*)/
  // multigpu_*/r_gpu_mem_stats/r_renderdoc_* convars+commands otherwise unreachable.
  //
  // NO GPU OR SOFTWARE RASTERIZER IS NEEDED for those convars. MEASURED: the render
  // module has no hard graphics-driver dependency (it dlopens the driver lazily) and its
  // ConVar_Register runs regardless of whether a device is ever created — a linux run
  // with NO Vulkan driver present is byte-identical to one with lavapipe. (An earlier
  // version of this comment claimed WARP/lavapipe were required; that was wrong.)
  //
  // WHY IT IS STILL DEFERRED: Init() ALSO attempts to create a live render device as a
  // side effect (WARP is always present on windows so one is created there; a Vulkan
  // driver, if present, is used on linux). A created device puts the engine into a
  // frame-update state. Running this Init BEFORE the data-subsystem Connect+Init above
  // made that phase's resource-manifest load REENTRANT — a FATAL engine Error()/exit
  // (CResourceSystem::BlockUntilManifestLoaded) the crash guards cannot catch. Deferring
  // it to here, after every engine-heavy Connect/Init, means nothing afterward re-enters
  // the resource system during a frame update; the remaining schema/cvar/command/netmsg
  // walks are read-only memory traversal that never loads a manifest. Device creation
  // FAILING (no driver) is benign — ConVar_Register already ran.
  //
  // Only ONE backend is present per platform; the other's module file is absent
  // (FindLoaded null) and is skipped. Guarded (a fault is skipped, not fatal) and wrapped
  // in its own CallChangeCallback stub so an r_* change-handler can't cascade.
  for (const char* render_module : {"rendersystemdx11", "rendersystemvulkan"}) {
    const LoadedModule* rmod = FindLoaded(env, render_module);
    if (rmod == nullptr) continue;  // not this platform's backend — skip.
    auto factory = reinterpret_cast<CreateInterfaceFn>(
        rmod->ResolveSymbol(kCreateInterfaceSymbol));
    void* appsystem =
        factory ? ResolveModuleAppSystem(factory, render_module, "RenderDeviceMgr001")
                : nullptr;
    if (appsystem == nullptr) {
      BTrace("render-init.no-iface", render_module);
      continue;
    }
    CvarChangeCallbackPatch render_change_patch(cvar_void);
    int before_render = CountConVars(cvar);
    BTrace("render-init",
           std::string(render_module) + " (deferred render-device Init)");
    bool ran = CallInitGuarded(appsystem);
    int after_render = CountConVars(cvar);
    BTrace(ran ? "render-init.result" : "render-init.faulted",
           std::string(render_module) +
               (ran ? (" convars=" + std::to_string(after_render) + " (+" +
                       std::to_string(after_render - before_render) + ")")
                    : " (Init fault caught; module skipped)"));
  }

  // 4) Report the miss set (interfaces we returned null for). If a crash
  //    happened it already aborted; reaching here means boot survived, but the
  //    miss set is still useful to widen the factory if the registry is short.
  if (!table.misses.empty()) {
    std::string misses;
    for (const auto& kv : table.misses) {
      if (!misses.empty()) misses += ", ";
      misses += kv.first + "(x" + std::to_string(kv.second) + ")";
    }
    BTrace("factory.misses", misses);
  }

  g_table = nullptr;  // factory table goes out of scope after this function.

  int final_count = CountConVars(cvar);
  BTrace("registry.final", std::to_string(final_count) + " convars");

  // 5) Fail loud if the game-config Init produced nothing. An empty
  //    registry after Init means the boot did not actually run ConVar_Register —
  //    do NOT let the caller emit an empty convars.json silently.
  if (final_count == 0) {
    *err =
        "engine boot: ICvar registry still empty after Connect+Init of the "
        "game-config modules (host/server/client/modtools/matchmaking). The partial "
        "engine boot did not flush any convars — convar/command extraction "
        "cannot proceed. Re-run with CS2_WALKER_TRACE=1 to see per-module "
        "Connect/Init results, factory misses and the registry delta.";
    return false;
  }

  BTrace("done", std::to_string(final_count) + " convars registered");
  return true;
}

}  // namespace cs2_schema_walker
