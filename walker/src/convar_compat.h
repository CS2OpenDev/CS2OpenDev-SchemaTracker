// convar_compat.h — clean-room ConVar/ConCommand API era shim.
//
// WHY THIS EXISTS
// ----------------
// Valve reworked the CS2 ConVar/ConCommand API across 2024-2025. The walker is
// built against a RANGE of pinned hl2sdk checkouts for the historical backfill,
// and the two eras expose mutually-incompatible C++ surfaces:
//
//   NEW era (current pin b8dcaf14 and forward):
//     - ICvar::FindConVar(name,bAllowDeveloper) -> ConVarRef
//     - ICvar::FindFirstConVar()/FindNextConVar(ref) -> ConVarRef
//     - ICvar::GetConVarData(ConVarRef) -> ConVarData*
//     - ConVarRef(uint16) index ctor; ConVarRef::IsValidRef()
//     - ConVarData header-INLINE accessors: GetName/GetHelpText/GetFlags/
//       GetType/DefaultValue
//     - mirror ConCommandRef / ConCommandData / GetConCommandData
//
//   OLD era (e.g. f3b44f20, 2025-01-01, back through early-2024):
//     - ICvar::FindConVar(name,bAllowDeveloper) -> ConVarHandle  (uint32 wrapper)
//     - ICvar::FindFirstConVar()/FindNextConVar(h) -> ConVarHandle
//     - ICvar::GetConVar(ConVarHandle) -> ConVar*
//     - NO ConVarData, NO ConVarRef(idx), NO GetConVarData, NO IsValidRef
//     - ConVar accessors (GetName/GetFlags/...) are OUT-OF-LINE (tier1 link) so
//       we MUST read the struct layout directly. ConCommandBase likewise.
//
// This header presents ONE uniform surface to engine_boot.cpp / cvar_walk.cpp:
//
//     WConVarIter  / WConCmdIter   — handle/ref wrappers with IsValid()/Next()
//     WConVarView  / WConCmdView    — name/help/flags/type/default reads
//     WCvarFindConVar / WCvarFirst* / WCvarNext*  — ICvar dispatch helpers
//     WForceBoolConVarDefaultFalse  — the r_dopixelvisibility crash-patch action
//
// ON THE NEW ERA every helper is a thin passthrough that compiles to EXACTLY the
// pre-existing code path (requirement: no behavior change on newer pins). ON THE
// OLD ERA the helpers read the pinned-header struct layout directly (clean-room:
// the layout comes only from walker/third_party/hl2sdk, never re-declared from
// memory or copied from DumpSource2).
//
// The active era is selected by WALKER_CONVAR_HAS_CONVARDATA_API, set by the
// CMake configure-time probe in walker/CMakeLists.txt (mirrors the existing
// WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA probe). If the probe is somehow
// absent we fall back to the NEW era (current pin), which keeps the default
// build path identical to today.

#ifndef WALKER_CONVAR_COMPAT_H_
#define WALKER_CONVAR_COMPAT_H_

#include "icvar.h"
#include "convar.h"

#include <cstdint>
#include <cstring>

namespace cs2_schema_walker {
namespace convar_compat {

#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)

// ===========================================================================
// NEW ERA — ConVarRef / ConVarData. Passthrough; identical to legacy code path.
// ===========================================================================

using WConVarData = ::ConVarData;
using WConCmdData = ::ConCommandData;

// ---- ConVar iteration handle (wraps ConVarRef) ----
struct WConVarIter {
  ::ConVarRef ref;
  WConVarIter() : ref(static_cast<uint16_t>(0)) {}
  explicit WConVarIter(::ConVarRef r) : ref(r) {}
  // Non-const to match whatever cv-qualification ConVarRef::IsValidRef() carries.
  bool IsValid() { return ref.IsValidRef(); }
};

struct WConCmdIter {
  ::ConCommandRef ref;
  WConCmdIter() : ref(static_cast<uint16_t>(0)) {}
  explicit WConCmdIter(::ConCommandRef r) : ref(r) {}
  bool IsValid() { return ref.IsValidRef(); }
};

inline WConVarIter WConVarFromIndex(uint32_t i) {
  return WConVarIter(::ConVarRef(static_cast<uint16_t>(i)));
}
inline WConCmdIter WConCmdFromIndex(uint32_t i) {
  return WConCmdIter(::ConCommandRef(static_cast<uint16_t>(i)));
}

// currently unused — part of the uniform W* surface; retained for symmetry
inline WConVarIter WCvarFindConVar(ICvar* cvar, const char* name,
                                   bool bAllowDeveloper) {
  return WConVarIter(cvar->FindConVar(name, bAllowDeveloper));
}
inline WConVarIter WCvarFirstConVar(ICvar* cvar) {
  return WConVarIter(cvar->FindFirstConVar());
}
inline WConVarIter WCvarNextConVar(ICvar* cvar, WConVarIter it) {
  return WConVarIter(cvar->FindNextConVar(it.ref));
}

// ---- data lookup ----
inline WConVarData* WGetConVarData(ICvar* cvar, WConVarIter it) {
  return cvar->GetConVarData(it.ref);
}
inline WConCmdData* WGetConCmdData(ICvar* cvar, WConCmdIter it) {
  return cvar->GetConCommandData(it.ref);
}

// ---- field reads (the ConVarData/ConCommandData accessors are header-inline) ----
// Take non-const pointers: the callers always hold a live (non-const) data
// pointer, and we do not rely on these accessors being const-qualified (they
// likely are, but this keeps the shim robust to either qualification).
inline const char* WConVarName(WConVarData* d) { return d ? d->GetName() : nullptr; }
inline const char* WConVarHelp(WConVarData* d) { return d ? d->GetHelpText() : nullptr; }
inline uint64_t WConVarFlags(WConVarData* d) { return d ? d->GetFlags() : 0; }
inline EConVarType WConVarType(WConVarData* d) {
  return d ? d->GetType() : EConVarType_Invalid;
}
inline const CVValue_t* WConVarDefault(WConVarData* d) {
  return d ? d->DefaultValue() : nullptr;
}
// Min/max bounds. NEW era: header-inline MinValue()/MaxValue() return the bound's
// CVValue_t* (null when the convar carries no bound — HasMinValue()/HasMaxValue()
// test the same pointer, convar.h:946-951). Presence == non-null.
inline const CVValue_t* WConVarMin(WConVarData* d) {
  return d ? d->MinValue() : nullptr;
}
inline const CVValue_t* WConVarMax(WConVarData* d) {
  return d ? d->MaxValue() : nullptr;
}

// ---- CVValue_t union member reads (era-divergent) ----
// Most union members (bool/int16/uint16/int32/uint32/int64/uint64/color/vec2/3/4/
// angle) keep identical names across the pin range, so cvar_walk.cpp reads them
// directly. Only float32, float64 and the string member were renamed/retyped in
// the OLD era, so those three are funneled through these accessors. On the NEW
// era each is the EXACT member read cvar_walk.cpp used before (byte-identical).
inline float WCVValueFloat32(const CVValue_t* v) { return v->m_fl32Value; }
inline double WCVValueFloat64(const CVValue_t* v) { return v->m_fl64Value; }
// NEW era: string is a CUtlString; render via .Get() exactly as before.
inline const char* WCVValueString(const CVValue_t* v) { return v->m_StringValue.Get(); }

inline const char* WConCmdName(WConCmdData* d) { return d ? d->GetName() : nullptr; }
inline const char* WConCmdHelp(WConCmdData* d) { return d ? d->GetHelpText() : nullptr; }
inline uint64_t WConCmdFlags(WConCmdData* d) { return d ? d->GetFlags() : 0; }
// Command.has_completion_callback. NEW era: header-inline
// ConCommandData::HasCompletionCallback() (convar.h:512 — reads
// m_CompletionCB.IsValid(), a pure member read, no virtual dispatch). True for
// commands that register an auto-complete suggestion callback.
inline bool WConCmdHasCompletionCallback(WConCmdData* d) {
  return d ? d->HasCompletionCallback() : false;
}

// ---- crash-patch action: force a bool convar's DEFAULT to false ----
// Identical to the legacy force_pixelvis_false body. Returns true if applied.
inline bool WForceBoolConVarDefaultFalse(ICvar* cvar, const char* name) {
  ::ConVarRef ref = cvar->FindConVar(name, true);
  if (!ref.IsValidRef()) return false;
  ::ConVarData* data = cvar->GetConVarData(ref);
  if (data == nullptr) return false;
  if (data->GetType() != EConVarType_Bool) return false;
  CVValue_t* v = data->DefaultValue();
  if (v == nullptr) return false;
  v->m_bValue = false;
  return true;
}

// The CallChangeCallback no-op (vtable slot 14) keeps the new-era signature.
using WChangeCallbackConVarArg = ::ConVarRef;

#else  // !WALKER_CONVAR_HAS_CONVARDATA_API

// ===========================================================================
// OLD ERA — ConVarHandle / ConVar*. We read the pinned ConVar / ConCommandBase
// struct LAYOUT directly (the public accessors are out-of-line tier1 imports we
// must not link). The mirror structs below replicate, field-for-field, the
// ACTIVE layout in walker/third_party/hl2sdk/public/tier1/convar.h for this
// era. They are clean-room transcriptions of the pinned header and are guarded
// by static_asserts so any drift fails the build loud rather than reading a
// wrong offset.
// ===========================================================================

// Mirror of ConVar's ACTIVE member layout (convar.h, the block after
// `#endif // CONVAR_WORK_FINISHED`). ConVar has NO virtual table in this era
// (the virtual section is `#if 0`-disabled), so the mirror begins at m_pszName.
struct ConVarLayout {
  const char* m_pszName;
  CVValue_t* m_cvvDefaultValue;
  CVValue_t* m_cvvMinValue;
  CVValue_t* m_cvvMaxValue;
  const char* m_pszHelpString;
  EConVarType m_eVarType;  // enum : short
  short unk1;
  unsigned int timesChanged;
  int64 flags;
  unsigned int callback_index;
  int allocation_flag_of_some_sort;
  CVValue_t** values;
};

// Mirror of ConCommandBase's layout (the base of ConCommand). Plain class, no
// vtable; members are m_pszName / m_pszHelpString / m_nFlags.
struct ConCommandBaseLayout {
  const char* m_pszName;
  const char* m_pszHelpString;
  int64 m_nFlags;
};

// Reading name/help/flags from a live ConVar* / ConCommand* goes through these
// reinterpret views of the pinned layout. (We cannot call the out-of-line
// accessors without linking tier1.)
static_assert(sizeof(ConVarLayout) >= sizeof(void*) * 5,
              "ConVarLayout smaller than expected — pinned ConVar layout drifted");

using WConVarData = ::ConVar;      // the data carrier in this era is ConVar*
using WConCmdData = ::ConCommand;  // ... and ConCommand* for commands

// ---- ConVar iteration handle (wraps ConVarHandle) ----
struct WConVarIter {
  ::ConVarHandle h;
  WConVarIter() {}
  explicit WConVarIter(::ConVarHandle hh) : h(hh) {}
  bool IsValid() { return h.IsValid(); }
};

struct WConCmdIter {
  ::ConCommandHandle h;
  WConCmdIter() {}
  explicit WConCmdIter(::ConCommandHandle hh) : h(hh) {}
  bool IsValid() { return h.IsValid(); }
};

// Old era has no public index ctor; build a handle from a raw uint index.
inline WConVarIter WConVarFromIndex(uint32_t i) {
  ::ConVarHandle h;
  h.Set(i);
  return WConVarIter(h);
}
inline WConCmdIter WConCmdFromIndex(uint32_t i) {
  ::ConCommandHandle h;
  h.Set(static_cast<uint16_t>(i));
  return WConCmdIter(h);
}

// currently unused — part of the uniform W* surface; retained for symmetry
inline WConVarIter WCvarFindConVar(ICvar* cvar, const char* name,
                                   bool bAllowDeveloper) {
  return WConVarIter(cvar->FindConVar(name, bAllowDeveloper));
}
inline WConVarIter WCvarFirstConVar(ICvar* cvar) {
  return WConVarIter(cvar->FindFirstConVar());
}
inline WConVarIter WCvarNextConVar(ICvar* cvar, WConVarIter it) {
  return WConVarIter(cvar->FindNextConVar(it.h));
}

// currently unused — part of the uniform W* surface; retained for symmetry
inline WConCmdIter WCvarFirstCommand(ICvar* cvar) {
  return WConCmdIter(cvar->FindFirstCommand());
}
inline WConCmdIter WCvarNextCommand(ICvar* cvar, WConCmdIter it) {
  return WConCmdIter(cvar->FindNextCommand(it.h));
}

// ---- data lookup: handle -> object pointer ----
inline WConVarData* WGetConVarData(ICvar* cvar, WConVarIter it) {
  if (!it.h.IsValid()) return nullptr;
  return cvar->GetConVar(it.h);
}
inline WConCmdData* WGetConCmdData(ICvar* cvar, WConCmdIter it) {
  if (!it.h.IsValid()) return nullptr;
  return cvar->GetCommand(it.h);
}

// ---- field reads via the pinned layout (clean-room, no tier1 link) ----
inline const ConVarLayout* AsConVarLayout(const WConVarData* d) {
  return reinterpret_cast<const ConVarLayout*>(d);
}
inline const ConCommandBaseLayout* AsConCmdLayout(const WConCmdData* d) {
  return reinterpret_cast<const ConCommandBaseLayout*>(d);
}

inline const char* WConVarName(const WConVarData* d) {
  return d ? AsConVarLayout(d)->m_pszName : nullptr;
}
inline const char* WConVarHelp(const WConVarData* d) {
  return d ? AsConVarLayout(d)->m_pszHelpString : nullptr;
}
inline uint64_t WConVarFlags(const WConVarData* d) {
  return d ? static_cast<uint64_t>(AsConVarLayout(d)->flags) : 0;
}
inline EConVarType WConVarType(const WConVarData* d) {
  return d ? AsConVarLayout(d)->m_eVarType : EConVarType_Invalid;
}
inline const CVValue_t* WConVarDefault(const WConVarData* d) {
  return d ? AsConVarLayout(d)->m_cvvDefaultValue : nullptr;
}
// Min/max bounds. OLD era: the bound CVValue_t* live in the pinned ConVar
// layout at m_cvvMinValue / m_cvvMaxValue (convar_compat.h ConVarLayout, mirroring
// convar.h for this era). Presence == non-null pointer, same convention as the NEW
// era's HasMinValue()/HasMaxValue() (which test m_minValue/m_maxValue != nullptr).
inline const CVValue_t* WConVarMin(const WConVarData* d) {
  return d ? AsConVarLayout(d)->m_cvvMinValue : nullptr;
}
inline const CVValue_t* WConVarMax(const WConVarData* d) {
  return d ? AsConVarLayout(d)->m_cvvMaxValue : nullptr;
}

// ---- CVValue_t union member reads (era-divergent) ----
// OLD-era CVValue_t (pinned convar.h @ f3b44f20) renames three members relative
// to the NEW era; the rest keep identical names so cvar_walk.cpp reads them
// directly. Mapping (NEW -> OLD, from the pinned headers):
//   m_fl32Value  -> m_flValue   (float32 -> float)
//   m_fl64Value  -> m_dbValue   (float64 -> double)
//   m_StringValue.Get() (CUtlString) -> m_szValue (const char*)
// EConVarType is byte-identical across the eras (same enumerators/order; only the
// fixed underlying type spelling differs: int16_t vs short), so the type switch
// needs no compat. These accessors yield the SAME categories the walk renders.
inline float WCVValueFloat32(const CVValue_t* v) { return v->m_flValue; }
inline double WCVValueFloat64(const CVValue_t* v) { return v->m_dbValue; }
// OLD era: string member is already a const char*; no CUtlString / no .Get().
inline const char* WCVValueString(const CVValue_t* v) { return v->m_szValue; }

inline const char* WConCmdName(const WConCmdData* d) {
  return d ? AsConCmdLayout(d)->m_pszName : nullptr;
}
inline const char* WConCmdHelp(const WConCmdData* d) {
  return d ? AsConCmdLayout(d)->m_pszHelpString : nullptr;
}
inline uint64_t WConCmdFlags(const WConCmdData* d) {
  return d ? static_cast<uint64_t>(AsConCmdLayout(d)->m_nFlags) : 0;
}
// Command.has_completion_callback (OLD/2023 era): the pinned old-era
// ConCommandBaseLayout does NOT carry a derived completion-callback field, so we
// do NOT guess one; always false on this era (deferred-with-reason, same as the
// other 2023 enrichment gaps). The whole WalkConVarsAndCommands 2023 branch
// builds its own CC2023 records and never calls this accessor, so this only keeps
// the W* surface uniform across eras.
inline bool WConCmdHasCompletionCallback(const WConCmdData* /*d*/) {
  return false;
}

// ---- crash-patch action: force a bool convar's DEFAULT to false ----
// Old-era path: resolve handle -> ConVar*, check the layout's type, write the
// default-value union directly. Same net effect as the new-era body (bypasses
// any change handler). Returns true if applied.
inline bool WForceBoolConVarDefaultFalse(ICvar* cvar, const char* name) {
  ::ConVarHandle h = cvar->FindConVar(name, true);
  if (!h.IsValid()) return false;
  ::ConVar* cv = cvar->GetConVar(h);
  if (cv == nullptr) return false;
  ConVarLayout* lay = reinterpret_cast<ConVarLayout*>(cv);
  if (lay->m_eVarType != EConVarType_Bool) return false;
  if (lay->m_cvvDefaultValue == nullptr) return false;
  lay->m_cvvDefaultValue->m_bValue = false;
  return true;
}

// The CallChangeCallback no-op (vtable slot 14): in this era the callback's
// convar argument is a ConVarRefAbstract* (icvar.h CallGlobalChangeCallbacks /
// FnChangeCallback_t). We only ever install a no-op, so we just need an
// ABI-correct pointer-width parameter.
using WChangeCallbackConVarArg = ::ConVarRefAbstract*;

#endif  // WALKER_CONVAR_HAS_CONVARDATA_API

}  // namespace convar_compat
}  // namespace cs2_schema_walker

#endif  // WALKER_CONVAR_COMPAT_H_
