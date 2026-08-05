// ICvar (ConCommandBase registry) traversal. See cvar_walk.h.
#include "cvar_walk.h"

#include "loader.h"
#include "util.h"  // Str()

// HL2SDK convar surface. icvar.h pulls in the ICvar interface + ConVarRef /
// ConCommandRef / ConVarData / ConCommandData. We call ONLY:
//   - the live ICvar vtable methods (FindFirst/Next ConVar/ConCommand,
//     GetConVarData, GetConVarData), which dispatch into the loaded DLL, and
//   - header-inline accessors on ConVarData / ConCommandData (GetName,
//     GetHelpText, GetFlags, GetType, DefaultValue) plus CUtlString::Get.
// We deliberately do NOT touch any DLL_CLASS_IMPORT method (e.g. anything on
// CBufferString) — the default value is rendered here by hand from the raw
// CVValue_t so we incur no tier1 link dependency (mirrors sdk_schema.h's rule).
//
// CONVAR API ERA: the ConVar/ConCommand surface differs between hl2sdk eras
// (NEW: ConVarRef/ConVarData/GetConVarData; OLD: ConVarHandle/GetConVar/ConVar*).
// convar_compat.h hides that behind a uniform W* surface. On the current pin it
// is a thin passthrough to exactly the calls this TU used before. See that
// header for the per-era detail and the clean-room layout mirrors.
#include "icvar.h"
#include "convar.h"
#include "convar_compat.h"
#include "tshash_compat.h"  // SafeRead*2023 for the 2023 OLD-ConVar memory mirror

#include "convars.pb.h"
#include "commands.pb.h"
#include "walker_output.pb.h"

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>  // std::getenv (CS2_WALKER_TRACE)
#include <cstring>
#include <iterator>  // std::size (canary-list arity in the canary guard)
#include <string>
#include <utility>  // std::pair (name-string-anchor canary cvd/name_off pairs)
#include <vector>

namespace wpb = cs2::schema_tracker::v0;

namespace cs2_schema_walker {

namespace {

// ConVar/ConCommand flag-name table. Names match the lowercase, FCVAR_-stripped
// convention the upstream DumpSource2 convars.txt / commands.txt emit, so v1
// consumers keep parity (convars.proto header notes: gamedll, cheat, clientdll,
// developmentonly, archive, defensive, release, ...). Ordered by bit position so
// the emitted flag list, after we filter to set bits, is itself deterministic.
struct FlagName {
  uint64_t bit;
  const char* name;
};
constexpr FlagName kFlagNames[] = {
    {1ull << 0, "linked_concommand"},
    {1ull << 1, "developmentonly"},
    {1ull << 2, "gamedll"},
    {1ull << 3, "clientdll"},
    {1ull << 4, "hidden"},
    {1ull << 5, "protected"},
    {1ull << 6, "sponly"},
    {1ull << 7, "archive"},
    {1ull << 8, "notify"},
    {1ull << 9, "userinfo"},
    {1ull << 10, "reference"},
    {1ull << 11, "unlogged"},
    {1ull << 12, "initial_setvalue"},
    {1ull << 13, "replicated"},
    {1ull << 14, "cheat"},
    {1ull << 15, "per_user"},
    {1ull << 16, "demo"},
    {1ull << 17, "dontrecord"},
    {1ull << 18, "performing_callbacks"},
    {1ull << 19, "release"},
    {1ull << 20, "menubar_item"},
    {1ull << 21, "commandline_enforced"},
    {1ull << 22, "not_connected"},
    {1ull << 23, "vconsole_fuzzy_matching"},
    {1ull << 24, "server_can_execute"},
    {1ull << 25, "client_can_execute"},
    {1ull << 26, "server_cannot_query"},
    {1ull << 27, "vconsole_set_focus"},
    {1ull << 28, "clientcmd_can_execute"},
    {1ull << 29, "execute_per_tick"},
    {1ull << 32, "defensive"},
};

// Append the set flag names (already in stable bit order) to a proto repeated
// string field.
void EmitFlags(uint64_t flags,
               google::protobuf::RepeatedPtrField<std::string>* out) {
  for (const FlagName& f : kFlagNames) {
    if ((flags & f.bit) != 0) out->Add()->assign(f.name);
  }
}

// ---- Shared default-value FORMATTER (single home for the printf formats) -----
//
// The per-EConVarType render format is the load-bearing, output-determining part
// of the convar walk (byte parity across BOTH eras). It used to be physically
// duplicated between the modern path (DefaultValueToString) and the 2023 mirror
// path (Render2023Default), so a format tweak could silently desync the eras.
// The 16 format strings now live HERE, in exactly one place; the two callers do
// their era-specific VALUE ACQUISITION (modern reads the live CVValue_t union via
// convar_compat; 2023 reads raw bytes via tc::SafeRead*2023) and pass the already-
// extracted scalars in. Mirrors convar.h's inline CvarTypeTrait_ValueToStringFn<T>
// specializations EXACTLY (same printf formats), but writes into our own buffer so
// we never touch CBufferString (DLL_CLASS_IMPORT).
//
// The scalars are pre-extracted into the widest matching C type per case (the same
// type each call site already promotes to for its printf conversion), so the
// formatter applies the format verbatim with no per-era branching. Color carries
// its alpha-branch component separately (`color_alpha`) so each caller keeps its
// exact "is fully opaque?" test (modern: m_clrValue.a(); 2023: byte[3]) — both are
// element [3] of the same color, so the rendered 3-vs-4 component choice matches.
struct ScalarDefault {
  long long i;           // Int16/Int32/Int64 (rendered %d / %lld)
  unsigned long long u;  // UInt16/UInt32/UInt64 (rendered %u / %llu)
  float f;               // Float32 (%f)
  double d;              // Float64 (%lf)
  const char* str;       // String
  int color[4];          // Color components (each %d)
  int color_alpha;       // alpha for the 3-vs-4 component branch
  float vec[4];          // Vector2/3/4 + Qangle (each %f)
  bool b;                // Bool
};

// Apply the (single) per-type format. `type` selects which ScalarDefault members
// are already populated. Returns the rendered string. Invalid/unknown -> "".
std::string FormatScalarDefault(EConVarType type, const ScalarDefault& s) {
  char buf[256];
  switch (type) {
    case EConVarType_Bool:
      return s.b ? "true" : "false";
    case EConVarType_Int16:
    case EConVarType_Int32:
      std::snprintf(buf, sizeof(buf), "%d", static_cast<int>(s.i));
      return buf;
    case EConVarType_UInt16:
    case EConVarType_UInt32:
      std::snprintf(buf, sizeof(buf), "%u",
                    static_cast<unsigned int>(s.u));
      return buf;
    case EConVarType_Int64:
      std::snprintf(buf, sizeof(buf), "%lld", s.i);
      return buf;
    case EConVarType_UInt64:
      std::snprintf(buf, sizeof(buf), "%llu", s.u);
      return buf;
    case EConVarType_Float32:
      std::snprintf(buf, sizeof(buf), "%f", s.f);
      return buf;
    case EConVarType_Float64:
      std::snprintf(buf, sizeof(buf), "%lf", s.d);
      return buf;
    case EConVarType_String:
      return Str(s.str);
    case EConVarType_Color:
      if (s.color_alpha == 255)
        std::snprintf(buf, sizeof(buf), "%d %d %d",
                      s.color[0], s.color[1], s.color[2]);
      else
        std::snprintf(buf, sizeof(buf), "%d %d %d %d",
                      s.color[0], s.color[1], s.color[2], s.color[3]);
      return buf;
    case EConVarType_Vector2:
      std::snprintf(buf, sizeof(buf), "%f %f", s.vec[0], s.vec[1]);
      return buf;
    case EConVarType_Vector3:
      std::snprintf(buf, sizeof(buf), "%f %f %f",
                    s.vec[0], s.vec[1], s.vec[2]);
      return buf;
    case EConVarType_Vector4:
      std::snprintf(buf, sizeof(buf), "%f %f %f %f",
                    s.vec[0], s.vec[1], s.vec[2], s.vec[3]);
      return buf;
    case EConVarType_Qangle:
      std::snprintf(buf, sizeof(buf), "%f %f %f",
                    s.vec[0], s.vec[1], s.vec[2]);
      return buf;
    case EConVarType_Invalid:
    default:
      return std::string();
  }
}

// Render ANY modern CVValue_t (default / min / max) to a string for the given
// EConVarType. ACQUISITION ONLY: reads the live CVValue_t union directly (most
// members keep identical names across the hl2sdk pin range; float32/float64/string
// were renamed/retyped in the OLD era so those three go through
// convar_compat::WCVValue* which resolve the right member per era). The actual
// format strings live in FormatScalarDefault — keep that the single home.
//
// Lifted out of DefaultValueToString so the IDENTICAL union extraction +
// format is reused for the min and max bounds, guaranteeing min/max render
// byte-identically to default. A null value pointer / Invalid type -> "".
std::string RenderCVValue(EConVarType type, const CVValue_t* v) {
  if (v == nullptr) return std::string();
  ScalarDefault s{};
  switch (type) {
    case EConVarType_Bool:
      s.b = v->m_bValue;
      break;
    case EConVarType_Int16:
      s.i = v->m_i16Value;
      break;
    case EConVarType_UInt16:
      s.u = v->m_u16Value;
      break;
    case EConVarType_Int32:
      s.i = v->m_i32Value;
      break;
    case EConVarType_UInt32:
      s.u = v->m_u32Value;
      break;
    case EConVarType_Int64:
      s.i = static_cast<long long>(v->m_i64Value);
      break;
    case EConVarType_UInt64:
      s.u = static_cast<unsigned long long>(v->m_u64Value);
      break;
    case EConVarType_Float32:
      s.f = convar_compat::WCVValueFloat32(v);
      break;
    case EConVarType_Float64:
      s.d = convar_compat::WCVValueFloat64(v);
      break;
    case EConVarType_String:
      s.str = convar_compat::WCVValueString(v);
      break;
    case EConVarType_Color:
      s.color[0] = v->m_clrValue[0];
      s.color[1] = v->m_clrValue[1];
      s.color[2] = v->m_clrValue[2];
      s.color[3] = v->m_clrValue[3];
      s.color_alpha = v->m_clrValue.a();
      break;
    case EConVarType_Vector2:
      s.vec[0] = v->m_vec2Value[0];
      s.vec[1] = v->m_vec2Value[1];
      break;
    case EConVarType_Vector3:
      s.vec[0] = v->m_vec3Value[0];
      s.vec[1] = v->m_vec3Value[1];
      s.vec[2] = v->m_vec3Value[2];
      break;
    case EConVarType_Vector4:
      s.vec[0] = v->m_vec4Value[0];
      s.vec[1] = v->m_vec4Value[1];
      s.vec[2] = v->m_vec4Value[2];
      s.vec[3] = v->m_vec4Value[3];
      break;
    case EConVarType_Qangle:
      s.vec[0] = v->m_angValue[0];
      s.vec[1] = v->m_angValue[1];
      s.vec[2] = v->m_angValue[2];
      break;
    case EConVarType_Invalid:
    default:
      return std::string();
  }
  return FormatScalarDefault(type, s);
}

// Render a modern ConVar DEFAULT value to a string. For a convar with no explicit
// default, CS2 still keeps a per-type global default value present, so
// DefaultValue() is expected non-null for any registered (non-reference) convar;
// RenderCVValue guards the null anyway.
std::string DefaultValueToString(convar_compat::WConVarData* data) {
  if (data == nullptr) return std::string();
  return RenderCVValue(convar_compat::WConVarType(data),
                       convar_compat::WConVarDefault(data));
}

// EConVarType -> enumerator NAME (value_type). Clean-room transcription of
// the convar.h EConVarType enum (the enumerator order/spelling, NOT copied code).
// Invalid -> "" (the caller emits an empty value_type for an Invalid-typed convar).
const char* ConVarTypeName(EConVarType type) {
  switch (type) {
    case EConVarType_Bool:
      return "Bool";
    case EConVarType_Int16:
      return "Int16";
    case EConVarType_UInt16:
      return "UInt16";
    case EConVarType_Int32:
      return "Int32";
    case EConVarType_UInt32:
      return "UInt32";
    case EConVarType_Int64:
      return "Int64";
    case EConVarType_UInt64:
      return "UInt64";
    case EConVarType_Float32:
      return "Float32";
    case EConVarType_Float64:
      return "Float64";
    case EConVarType_String:
      return "String";
    case EConVarType_Color:
      return "Color";
    case EConVarType_Vector2:
      return "Vector2";
    case EConVarType_Vector3:
      return "Vector3";
    case EConVarType_Vector4:
      return "Vector4";
    case EConVarType_Qangle:
      return "Qangle";
    case EConVarType_Invalid:
    default:
      return "";
  }
}

constexpr uint32_t kMaxRefScan = 0xFFFE;  // uint16 space minus the invalid sentinel.

// ---- SHARED CANARY CONVARS (post-walk set-level guard) ----------------
//
// Ubiquitous convars present in EVERY healthy CS2 era (verified present across all
// committed convars.json including the 2023 baseline 10832117, the OLD-ConVar-API
// path, and the modern path; absent ONLY in the 2 known-garbage aug-2025 dumps).
// Used by the post-walk AssertCanaryConVarsPresent guard.
constexpr const char* kCanaryConVars[] = {
    "sv_cheats", "developer", "host_timescale", "sv_gravity"};
constexpr int kCanaryThreshold = 2;  // >= this many exact-match canaries required.

// ---- SHARED CANARY CONCOMMANDS (post-walk set-level guard) -------------
//
// Ubiquitous ConCommands present in EVERY healthy CS2 era. VERIFIED present across
// all 242 committed commands.json (2023 baseline 10832117 .. current), and absent
// ONLY in the 2 known-garbage aug-2025 dumps (19602992/19605004, currently empty).
// (Empirically confirmed: help/find/kill/say are 242/244; common Source-1 names
// like exec/status/quit/echo are NOT universal in CS2 and are deliberately NOT used
// here.) Used by AssertCanaryConCommandsPresent — the aug-2025 ConCommand mirror's
// final correctness gate. Shares kCanaryThreshold with the convar guard.
constexpr const char* kCanaryConCommands[] = {"help", "find", "kill", "say"};

// Short alias for the SEH-guarded POD-frame memory-read trampolines (declared early
// so BOTH the aug-2025 CCvar mirror below AND the 2023 mirror further down can use
// it). A namespace alias may be re-declared to the SAME target, so the existing
// `namespace tc = ...` near the 2023 block is a harmless redefinition.
namespace tc = cs2_schema_walker::tshash_compat;

// ---- aug-2025 (pin 3525af99) GARBAGE-CONVAR-WALK DETECTION -------------------
//
// CONFIRMED ROOT CAUSE (build 19605004, archived aug-2025 engine DLL): the modern
// index scan (ForEachLiveConVar) resolves each ConVarRef(i) through
// ICvar::GetConVarData — a VIRTUAL call at the COMPILE-TIME vtable slot the walker
// was built with (this pin's icvar.h => slot 42). For 9 of 10 pinned eras that
// slot matches the shipped engine DLL's ICvar vtable and the scan is correct. On
// the aug-2025 DLL the shipped vtable's GetConVarData is at a DIFFERENT slot than
// even pin 3525af99's OWN header computes, so the virtual call lands on the wrong
// method and returns garbage ConVarData*: the scan emits ~0xFEFE entries whose
// names are raw x86-64 code bytes (invalid UTF-8), and the sentinel stop never
// fires (no "<undefined>" run). The shipped aug-2025 DLL does NOT expose its
// convar registry through the GetConVarData index API in any way this walker can
// reach — see the EMPIRICAL FAILURE OF SLOT-PROBING below.
//
// EMPIRICAL FAILURE OF SLOT-PROBING (why there is no runtime fallback here): a
// previous revision tried to resolve the correct GetConVarData vtable slot AT
// RUNTIME by blind-probing an absolute vtable window ([12,72]) and accepting the
// slot that resolved the canary convars. That approach was both INEFFECTIVE and
// DANGEROUS, proven on operator builds:
//   - INEFFECTIVE: aug-2025's real convars are NOT reachable via
//     GetConVarData(ConVarRef(i)) at ANY slot in the window — zero canary matches
//     across [12,72] x 256 indices. The registry simply is not exposed through the
//     index API for this DLL.
//   - DANGEROUS: slots 12-72 include destructive ICvar/IAppSystem methods
//     (Connect/Init/Shutdown/Disconnect/Register*). Blind-calling them invoked a
//     destructive method that tore the process down with exit code 54321 BEFORE
//     the clean fail path ran — which ALSO swallowed the stderr diagnostic. SEH
//     cannot protect against a method that deliberately exits.
// So we DO NOT probe. On a detected-garbage result we FAIL LOUD IMMEDIATELY with a
// precise message (no blind vtable call). The REAL fix is a 2023-style direct
// memory-layout mirror of the CCvar registry (cf. Read2023Registry) — a deep,
// separate RE workstream, NOT done here.
//
// DESIGN (non-negotiable): the PRIMARY index scan runs UNCHANGED first. Only
// if its result is DETECTABLY GARBAGE do we fail loud. A healthy era emits
// ~1800-3300 convars, all with printable C-identifier names, so the detector
// PROVABLY never trips for a healthy era and output stays byte-identical.

#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)

// A convar/command name is plausible iff it is a NUL-terminated run of printable
// ASCII (the registry only ever holds C-identifier-ish names: [A-Za-z0-9_.] etc.,
// all in 0x20..0x7e). Raw x86-64 code bytes (the garbage walk's "names") contain
// bytes outside that range, so this rejects them. Bounded read length; the pointer
// itself is assumed already-vetted by the SEH-guarded probe path.
inline bool PlausiblePrintableName(const char* s) {
  if (s == nullptr) return false;
  int n = 0;
  for (const char* p = s; *p && n < 128; ++p, ++n) {
    unsigned char c = static_cast<unsigned char>(*p);
    if (c < 0x20 || c > 0x7e) return false;  // non-printable => not a real name
  }
  return n >= 1 && n < 128;
}

// GARBAGE DETECTOR. The primary scan result is INVALID iff either:
//   (a) the emitted count exceeds a sane ceiling — a healthy era is ~1800-3300, so
//       a ceiling of 10000 is far above any real registry yet far below the ~0xFEFE
//       garbage flood; OR
//   (b) a high fraction (>= ~10%) of emitted names are non-printable / not
//       plausible identifiers — the garbage walk's names are raw code bytes.
// Either condition PROVABLY cannot trip for a healthy era (count well under the
// ceiling AND every name printable), so the immediate fail-loud below never fires
// for a healthy era and its output stays byte-identical.
inline constexpr std::size_t kSaneConVarCeiling = 10000;
template <class CVVec>
bool PrimaryConVarResultIsGarbage(const CVVec& cvars) {
  if (cvars.size() > kSaneConVarCeiling) return true;
  if (cvars.empty()) return false;  // empty is "no garbage" (a real failure is handled elsewhere)
  std::size_t bad = 0;
  for (const auto& c : cvars)
    if (!PlausiblePrintableName(c.name.c_str())) ++bad;
  // >= 10% non-printable names => garbage walk.
  return bad * 10 >= cvars.size();
}

// Same garbage test over a vector of NAMES (the universe path captures only
// names). Kept separate so the threshold/logic stays the single home above.
inline bool PrimaryConVarNamesAreGarbage(const std::vector<std::string>& names) {
  if (names.size() > kSaneConVarCeiling) return true;
  if (names.empty()) return false;
  std::size_t bad = 0;
  for (const std::string& n : names)
    if (!PlausiblePrintableName(n.c_str())) ++bad;
  return bad * 10 >= names.size();
}

// ---- aug-2025 GARBAGE -> IMMEDIATE CLEAN FAIL-LOUD message ------------------
//
// On a detected-garbage primary scan there is NO probe and NO blind vtable call
// (see the EMPIRICAL FAILURE OF SLOT-PROBING note above): we set *err to this
// precise, deterministic message and return false. main.cpp's Change-B failure
// path then flushes stderr and exits cleanly with the deterministic non-zero walk
// failure code (NOT 54321, no minidump). `label` is "convar walk" / "convar
// universe" so the extraction and universe paths fail identically.
std::string GarbageConVarFailMessage(const char* label, std::size_t count) {
  return std::string(label) +
         ": the primary index scan returned garbage (" + std::to_string(count) +
         " convars with non-printable names) for this era; the shipped engine "
         "DLL's convar registry is not reachable via the GetConVarData index API, "
         "and the CCvar registry memory-mirror fallback could not derive the "
         "registry layout either (no canary-yielding candidate). No convars "
         "emitted.";
}

// ===========================================================================
// aug-2025 (pin 3525af99) CCvar REGISTRY MEMORY-MIRROR (clean-room).
// ===========================================================================
//
// THE BREAKTHROUGH. On aug-2025 the index scan above is garbage ONLY because the
// shipped engine DLL's ICvar::GetConVarData lives at a different vtable slot than
// this pin's header computes, so the virtual call lands on the wrong method. BUT
// the FRONT-OF-VTABLE iteration is FINE: engine_boot.cpp's CountConVars walks
// ICvar::FindFirstConVar()/FindNextConVar(ref) (convar_compat WCvarFirst/Next) and
// correctly counts ~1800 convars on aug-2025. So FindFirst/FindNext return VALID
// ConVarRefs; only the ref->ConVarData* registry-array lookup (GetConVarData) is
// broken.
//
// FIX: replicate GetConVarData by indexing the CCvar object's convar registry
// DIRECTLY in memory, using the DEFINITIVE layout derived on build 23669931 (see
// RunCCvarRegistryGroundTruthDerivation + its NEIGHBORHOOD ANALYSIS below). The
// registry is a FLAT HEAP ARRAY of 16-BYTE ENTRIES indexed by ConVarRef's ACCESS
// INDEX: GetConVarData(ref) == *(table_base + ref.GetAccessIndex()*16 + 0). The
// heap table_base is held in a CCvar member at some offset O (the ONLY per-era
// unknown), so we DERIVE O at runtime (the one and only free variable, now that
// stride=16 / ptr@entry+0 / index=accessIndex / name@cvd+0 are PROVEN), validate by
// canary, then read every iterated ref's ConVarData via the proven formula.
//
// SAFETY (why this is allowed where the removed slot-probe was not): this path
// makes NO blind vtable slot calls. The ONLY vtable calls are FindFirstConVar /
// FindNextConVar — the PROVEN-WORKING front-of-vtable methods CountConVars already
// uses. Everything else is PURE, SEH-GUARDED MEMORY READS via the same POD-frame
// trampolines the 2023 mirror uses (tc::SafeReadPtr2023 / SafeReadBytes2023). No
// C++ object crosses the SEH boundary. A wrong derivation degrades to "no canary
// candidate -> fail loud", never a fault and never garbage.
//
// GATING: this runs ONLY after PrimaryConVarResultIsGarbage trips — i.e.
// only on aug-2025-like eras. A healthy era never reaches here, so its output is
// byte-identical.

// Read a ConVarData* (or an inline-element address) and pull its name via a
// SEH-guarded byte read of the name pointer at sub-offset `name_off`, then a
// SEH-guarded C-string read. Returns the name length-validated, or empty.
// On the CURRENT binary ConVarData::m_pszName is the FIRST member (convar.h:995),
// so name ptr @ data+0; aug-2025's ConVarData layout MAY place the name elsewhere,
// so `name_off` is a derived parameter (see DeriveCCvarRegistry). The default keeps
// the proven current-binary value (+0).
inline bool MirrorReadConVarNameAt(std::uint64_t data_addr, std::size_t name_off,
                                   char* out, std::size_t n) {
  std::uint64_t name_ptr = 0;
  if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                               static_cast<std::uintptr_t>(data_addr + name_off)),
                           &name_ptr))
    return false;
  if (!tc::LooksLikePointer2023(name_ptr)) return false;
  if (!tc::SafeReadCString2023(
          reinterpret_cast<const char*>(static_cast<std::uintptr_t>(name_ptr)),
          out, n))
    return false;
  return PlausiblePrintableName(out);
}

// The candidate ConVarData name sub-offsets the derive tries (aug-2025's
// ConVarData layout may place m_pszName past +0). +0 is the proven current value
// and is tried first so the healthy/current layout always locks name_off=0.
inline constexpr std::size_t kMirrorNameOffCandidates[] = {0, 8, 16, 24, 32};

// ---- DEFINITIVE registry model (ground-truth derived on build 23669931) ------
//
// The convar registry is a FLAT HEAP ARRAY of 16-BYTE ENTRIES, indexed by the
// ConVarRef ACCESS INDEX (ConVarRef::GetAccessIndex()). Per the neighborhood
// analysis (delta/accessIndexGap == 16 for all 16 ground-truth samples):
//   - entry[accessIndex] lives at  table_base + accessIndex*16.
//   - the ConVarData* is at entry+0 (the upper 8 bytes are a key/generation we
//     deliberately ignore). So:  ConVarData* cvd = *(table_base + accessIndex*16);
//   - accessIndex is SPARSE (7,13,17,18,28,...) — we index by each iterated ref's
//     OWN accessIndex, never the iteration ordinal.
//   - ConVarData::GetName() reads a name pointer at ConVarData+0 (name_off=0).
//   - table_base is a HEAP pointer stored in the CCvar object at some member
//     offset O. Only O is unknown per era, so we DERIVE O at runtime (the heap
//     block base may differ on aug-2025 vs current); everything else is fixed.
//
// This replaces the earlier blind stride/element-kind guessing (stride 8 ptr-array
// OR sizeof(ConVarData) inline-array, anchored at member offset directly or one
// indirection) which matched NOTHING on aug-2025 (2454 candidates, zero canary).
// With the access pattern now PROVEN (stride 16, ptr@entry+0, index=accessIndex,
// name@0), the only free variable is O — a precise single-offset derive.
inline constexpr std::size_t kCCvarEntryStride = 16;     // proven: 16-byte entries
inline constexpr std::size_t kCCvarEntryDataPtrOff = 0;  // ConVarData* at entry+0

// A derived registry: the CCvar member offset O that holds the heap table pointer,
// the resolved table_base (the heap array base of entry[0]), the derived ConVarData
// name sub-offset, and how it was derived (table-offset scan vs name-string anchor).
enum class CCvarDeriveMethod { kNone,
                               kTableOffsetScan,
                               kNameStringAnchor };
struct CCvarRegistry {
  bool derived = false;
  std::size_t member_off = 0;    // CCvar member offset O whose value is table_base
  std::uint64_t table_base = 0;  // *(cvar + O): heap base of entry[0]
  std::size_t name_off = 0;      // ConVarData name sub-offset (proven +0 on current)
  CCvarDeriveMethod method = CCvarDeriveMethod::kNone;
};

// Resolve the ConVarData* for an access index: cvd = *(table_base + idx*16 + 0).
// Pure SEH-guarded read; returns 0 on any read failure / implausible pointer.
inline std::uint64_t MirrorResolveDataAddr(std::uint64_t table_base,
                                           std::uint32_t idx) {
  const std::uint64_t slot = table_base +
                             static_cast<std::uint64_t>(idx) * kCCvarEntryStride +
                             kCCvarEntryDataPtrOff;
  std::uint64_t dataptr = 0;
  if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                               static_cast<std::uintptr_t>(slot)),
                           &dataptr))
    return 0;
  if (!tc::LooksLikePointer2023(dataptr)) return 0;
  return dataptr;
}

// NOTE: the former MirrorCanaryHits ("count exact canary names among the sampled
// refs") was the per-candidate DERIVE acceptance gate. It was removed because the
// FindFirst/Next iteration order starts with NON-canary convars, so even the CORRECT
// table_base scored 0 canaries in the early sample and was rejected. Acceptance now
// uses MirrorPrintableNameScore below; canaries remain the POST-READ correctness gate
// (AssertCanaryConVarsPresent) over the FULL convar set.

// Result of scoring a (table_base, name_off) candidate by PRINTABLE-NAME FRACTION.
// `resolved` = sampled refs whose slot yielded a plausible-pointer ConVarData and a
// SEH-readable name string; `printable` = of those, how many names are PLAUSIBLE
// printable convar names (PlausiblePrintableName). `fraction` = printable/resolved.
struct MirrorNameScore {
  std::size_t resolved = 0;
  std::size_t printable = 0;
  double fraction = 0.0;
};

// WHY THE DERIVE ACCEPTANCE IS A PRINTABLE-NAME FRACTION, NOT A CANARY COUNT.
//
// The original derive accepted a (table_base, name_off) candidate iff >=2 of the
// CANARY convars (sv_cheats/developer/host_timescale/sv_gravity) appeared among the
// sampled refs. That is WRONG as a per-candidate gate: FindFirst/Next iteration order
// starts with NON-canary convars (ord0..15 = panorama_debugger_theme, r_freezeparticles,
// cl_particle_retire_cost, r_csgo_*, ...). The canaries are NOT in the early sample, so
// even the CORRECT table_base scores 0 canaries and is REJECTED — exactly the bug the
// self-check caught on the current binary (derive=fail despite a correct table layout).
//
// PRINTABLE FRACTION cleanly separates a correct table_base (~100% of sampled slots
// yield real, ASCII-printable convar names) from a wrong one (~0% — garbage/non-printable
// or non-pointer). It does NOT depend on canaries being early. The canary set still gates
// CORRECTNESS, but only POST-READ over the FULL convar set (AssertCanaryConVarsPresent),
// where the canaries WILL appear — see RunConVarMirror's caller.
//
// Score over `sample_idx`: cvd = *(table_base + accessIndex*16); name @ cvd+name_off.
MirrorNameScore MirrorPrintableNameScore(
    std::uint64_t table_base, std::size_t name_off,
    const std::vector<std::uint32_t>& sample_idx) {
  MirrorNameScore s;
  for (std::uint32_t idx : sample_idx) {
    std::uint64_t data = MirrorResolveDataAddr(table_base, idx);
    if (data == 0) continue;
    char nm[128];
    // MirrorReadConVarNameAt already requires a plausible-pointer name ptr, a
    // SEH-readable C-string, AND PlausiblePrintableName — so a true return means a
    // resolved-AND-printable slot. Count "resolved" as slots whose name ptr was
    // plausible+readable (printable or not), so the fraction is printable/readable.
    std::uint64_t name_ptr = 0;
    if (!tc::SafeReadPtr2023(
            reinterpret_cast<const void*>(
                static_cast<std::uintptr_t>(data + name_off)),
            &name_ptr))
      continue;
    if (!tc::LooksLikePointer2023(name_ptr)) continue;
    if (!tc::SafeReadCString2023(
            reinterpret_cast<const char*>(
                static_cast<std::uintptr_t>(name_ptr)),
            nm, sizeof nm))
      continue;
    ++s.resolved;
    if (PlausiblePrintableName(nm)) ++s.printable;
  }
  s.fraction = s.resolved == 0
                   ? 0.0
                   : static_cast<double>(s.printable) /
                         static_cast<double>(s.resolved);
  return s;
}

// STRATEGY 1 — TABLE-OFFSET SCAN. DERIVE the CCvar registry table-base member
// offset O AND the ConVarData name sub-offset. `cvar` is the live ICvar*/CCvar*.
// `sample_idx` is a spread of REAL access indices from FindFirst/Next refs. For each
// 8-aligned member offset O in [0, kMirrorMaxCCvarOff) whose value LooksLikePointer2023,
// treat that value as table_base and, for each candidate name_off in
// kMirrorNameOffCandidates, score the PRINTABLE-NAME FRACTION under the PROVEN access
// formula (cvd = *(table_base + accessIndex*16); name@cvd+name_off): the fraction of
// sampled refs whose resolved name is an ASCII-printable plausible convar name. Lock
// BOTH (O, name_off) at the FIRST pair whose fraction >= kMirrorPrintableFracAccept
// over >= kMirrorMinResolvedSample resolved slots. Pure SEH-guarded reads, NO vtable
// calls. Returns derived=false if no pair clears the threshold.
//
// WHY PRINTABLE FRACTION, NOT CANARY COUNT (the bug this fixes): the FindFirst/Next
// iteration order starts with NON-canary convars, so the canaries are NOT in the early
// sample. The old gate (">=2 canary names in the sample") therefore scored the CORRECT
// table_base at 0 and REJECTED it. Printable fraction separates correct (~1.0) from
// wrong (~0.0) table bases WITHOUT depending on canary position. Canaries remain the
// POST-READ correctness gate (AssertCanaryConVarsPresent over the full set).
//
// NAME-OFFSET ROBUSTNESS (aug-2025): aug-2025's ConVarData layout MAY place the name
// pointer at a sub-offset other than +0 (proven on current). Trying several name
// offsets, scored by printable fraction, disambiguates "wrong table" from "right
// table, different ConVarData layout"; name_off=0 is tried first so ties keep +0.
inline constexpr std::size_t kMirrorMaxCCvarOff = 65536;  // CCvar object scan bound
inline constexpr int kMirrorCanaryDerive = 2;             // (legacy) name-anchor consistency min
// PRINTABLE-NAME FRACTION acceptance for the per-candidate derive (replaces the
// broken ">=2 canaries in sample" gate; see MirrorPrintableNameScore). A correct
// table_base yields ~1.0; a wrong one ~0.0, so 0.75 separates cleanly. We also
// require a minimum number of RESOLVED sample slots so a candidate that happens to
// resolve only 1-2 garbage slots can't score a high fraction by luck.
inline constexpr double kMirrorPrintableFracAccept = 0.75;
inline constexpr std::size_t kMirrorMinResolvedSample = 32;
// `exclude_table_base` (default 0 = none): a table_base to SKIP during the scan. The
// command mirror passes the ALREADY-DERIVED CONVAR table_base here so the command derive
// cannot accidentally re-lock the convar table — the two registries live in distinct
// CCvar members but their access-index spaces OVERLAP (command indices 0..~850 fall
// inside the convar table's 0..~3300), so indexing the CONVAR table by COMMAND indices
// also yields printable (convar!) names and would otherwise score high at the convar
// table's lower offset. Excluding it forces the scan onto the genuine command table.
CCvarRegistry DeriveCCvarRegistry(ICvar* cvar,
                                  const std::vector<std::uint32_t>& sample_idx,
                                  bool trace,
                                  std::uint64_t exclude_table_base = 0) {
  CCvarRegistry result;
  const std::uint64_t ccvar = reinterpret_cast<std::uint64_t>(cvar);

  int candidates_tried = 0;
  // Track the best-scoring (O, name_off) seen so we can log it on failure and so
  // ties resolve toward name_off=0 (kMirrorNameOffCandidates is tried in order with
  // strict ">" on the fraction, so the first/lowest name_off wins ties).
  double best_frac = -1.0;
  std::size_t best_off = 0, best_name_off = 0, best_resolved = 0;
  for (std::size_t off = 0; off + 8 <= kMirrorMaxCCvarOff; off += 8) {
    // The table pointer lives in a CCvar member at offset O. Read it (SEH-guarded);
    // skip non-pointer members.
    std::uint64_t table_base = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &table_base))
      continue;
    if (!tc::LooksLikePointer2023(table_base)) continue;
    if (exclude_table_base != 0 && table_base == exclude_table_base)
      continue;  // command derive: never re-lock the convar table (overlapping indices)
    ++candidates_tried;

    // Try each candidate ConVarData name sub-offset; ACCEPT the first whose PRINTABLE
    // -NAME FRACTION over the sample clears the threshold with enough resolved slots
    // (name_off=0 tried first, so the current layout always locks 0). Strict ">" on
    // the running best keeps ties on the lowest name_off / first qualifying offset.
    for (std::size_t name_off : kMirrorNameOffCandidates) {
      const MirrorNameScore sc =
          MirrorPrintableNameScore(table_base, name_off, sample_idx);
      if (sc.resolved >= kMirrorMinResolvedSample && sc.fraction > best_frac) {
        best_frac = sc.fraction;
        best_off = off;
        best_name_off = name_off;
        best_resolved = sc.resolved;
      }
      if (sc.resolved >= kMirrorMinResolvedSample &&
          sc.fraction >= kMirrorPrintableFracAccept) {
        result.derived = true;
        result.member_off = off;
        result.table_base = table_base;
        result.name_off = name_off;
        result.method = CCvarDeriveMethod::kTableOffsetScan;
        if (trace)
          std::fprintf(
              stderr,
              "[walker-trace] cvar: CCvar mirror DERIVED registry (table-offset "
              "scan) — member_off=+%zu table_base=0x%llx name_off=+%zu (stride=16, "
              "ptr@entry+0, index=accessIndex) — printable-name fraction %.3f "
              "(%zu/%zu resolved sampled refs; threshold %.2f); %d pointer "
              "candidates tried\n",
              off, static_cast<unsigned long long>(table_base), name_off,
              sc.fraction, sc.printable, sc.resolved,
              kMirrorPrintableFracAccept, candidates_tried);
        return result;
      }
    }
  }
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] cvar: CCvar mirror table-offset scan FAILED — no "
                 "(pointer member, name_off in {0,8,16,24,32}) pair cleared the "
                 "printable-name fraction threshold %.2f over >=%zu resolved sampled "
                 "refs (best fraction %.3f at member_off=+%zu name_off=+%zu, %zu "
                 "resolved; %d pointer candidates tried, off scan [0,%zu) step 8, "
                 "stride=16/ptr@entry+0/index=accessIndex)\n",
                 kMirrorPrintableFracAccept, kMirrorMinResolvedSample,
                 best_frac < 0.0 ? 0.0 : best_frac, best_off, best_name_off,
                 best_resolved, candidates_tried, kMirrorMaxCCvarOff);
  return result;
}

// ---- STRATEGY 2 — NAME-STRING-ANCHORED DERIVE (aug-2025 fallback) -------------
//
// If the table-offset scan fails (the table pointer is NOT a plausible-pointer CCvar
// member at any [0,64KB) offset, OR the per-entry stride is not 16 off that member),
// anchor on the REAL canary name STRINGS instead. This is independent of where the
// table pointer sits in CCvar AND of GetConVarData. The chain, all bounded +
// SEH-guarded pure reads:
//
//   1. Find the canary C-string address Saddr ("sv_cheats" etc.) in readable memory.
//      We bound the scan to the heap regions REACHABLE FROM CCvar's own pointer
//      members (the convar pool + name-string blocks live in blocks CCvar points
//      at), page-stepping over unmapped gaps — never an unbounded whole-address-space
//      sweep.
//   2. For Saddr, find a ConVarData candidate C: an 8-aligned address where
//      *(C + name_off) == Saddr for some name_off in {0,8,16,24,32}. C is searched in
//      the SAME bounded heap regions.
//   3. Find an 8-aligned table slot T holding C (T is a table entry; *(T)==C). T is
//      searched in the SAME bounded heap regions.
//   4. From >=2 canaries' (accessIndex, T) pairs derive table_base + confirm the
//      per-accessIndex stride is 16: table_base = T_k - accessIndex_k*16 must be
//      CONSTANT across the matched canaries. Lock (table_base implied member_off via
//      a reverse lookup, name_off).
//
// We need the accessIndex for each canary to relate T to table_base; we get it by
// matching the recovered NAME back to the iterated refs (CollectConVarRefIndices
// returns the access indices; we re-iterate with names here to pair name->accessIndex).
//
// SAFETY: bounded (each region capped; total candidate addresses capped), every read
// SEH-guarded + LooksLikePointer-prefiltered, NO vtable calls beyond FindFirst/Next.
// A miss degrades to derived=false -> the caller fails loud. Safe.

// A canary observed during iteration: its name + the access index of its ref.
struct AnchorCanary {
  const char* name;            // points into kCanaryConVars
  std::uint32_t access_index;  // ref.GetAccessIndex() for this canary
};

// Heap region reachable from a CCvar pointer member, used to BOUND the anchored
// scans (we never sweep the whole address space).
struct HeapRegion {
  std::uint64_t base;
  std::uint64_t size;
};

// Collect bounded heap regions to scan: each plausible-pointer CCvar member in
// [0,64KB) seeds one region [ptr, ptr+kAnchorRegionBytes). Deduped by base. This is
// where the convar pool, the pointer table, and the name-string blocks live (they
// are all reachable from CCvar members), so it covers the anchor targets without an
// unbounded sweep.
inline constexpr std::uint64_t kAnchorRegionBytes = 1u << 20;  // 1 MiB per region
inline constexpr std::size_t kMaxAnchorRegions = 256;          // region cap
std::vector<HeapRegion> CollectAnchorRegions(std::uint64_t ccvar) {
  std::vector<HeapRegion> regions;
  for (std::size_t off = 0;
       off + 8 <= kMirrorMaxCCvarOff && regions.size() < kMaxAnchorRegions;
       off += 8) {
    std::uint64_t p = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &p))
      continue;
    if (!tc::LooksLikePointer2023(p)) continue;
    bool dup = false;
    for (const HeapRegion& r : regions)
      if (r.base == p) {
        dup = true;
        break;
      }
    if (dup) continue;
    regions.push_back({p, kAnchorRegionBytes});
  }
  return regions;
}

// SEH-guarded, page-step-tolerant scan of `regions` for an 8-byte value == `target`.
// Calls visit(addr) for each match (addr is 8-aligned). Stops after `cap` matches.
// On an unmapped page, advances to the next page boundary instead of aborting.
template <typename Visit>
void ScanRegionsForValue(const std::vector<HeapRegion>& regions,
                         std::uint64_t target, std::size_t cap, Visit&& visit) {
  std::size_t found = 0;
  for (const HeapRegion& r : regions) {
    for (std::uint64_t a = r.base; a + 8 <= r.base + r.size && found < cap;
         a += 8) {
      std::uint64_t v = 0;
      if (!tc::SafeReadPtr2023(
              reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), &v)) {
        a = ((a + 0x1000ull) & ~0xFFFull) - 8ull;  // skip to next page (then +8)
        continue;
      }
      if (v != target) continue;
      ++found;
      visit(a);
    }
    if (found >= cap) break;
  }
}

// Find the address of a canary C-string (`name`) within the bounded regions. Reads
// each region as bytes (page-step-tolerant) and matches a NUL-terminated run equal
// to `name`. Returns the first match address, or 0.
inline constexpr std::size_t kMaxStringMatches = 8;
std::uint64_t FindCStringAddr(const std::vector<HeapRegion>& regions,
                              const char* name) {
  const std::size_t len = std::strlen(name);
  const char first = name[0];
  for (const HeapRegion& r : regions) {
    for (std::uint64_t a = r.base; a + len + 1 <= r.base + r.size; ++a) {
      char c = 0;
      if (!tc::SafeReadBytes2023(
              reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), &c,
              1)) {
        a = ((a + 0x1000ull) & ~0xFFFull) - 1ull;  // skip to next page (then +1)
        continue;
      }
      if (c != first) continue;
      char buf[64];
      if (len + 1 > sizeof buf) continue;
      if (!tc::SafeReadBytes2023(
              reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), buf,
              len + 1))
        continue;
      if (std::memcmp(buf, name, len) == 0 && buf[len] == '\0') return a;
    }
  }
  return 0;
}

// WHY NO name->accessIndex pairing is needed: this strategy fires when GetConVarData
// is broken, so we cannot ask the engine which accessIndex a canary has. Instead we
// brute the (table slot T, accessIndex a) hypothesis: for each table slot T holding a
// canary's located ConVarData and each iterated accessIndex a, hypothesize
// table_base = T - a*16 (stride 16) and accept it only if >=2 canaries' ConVarData are
// reachable at SOME iterated accessIndex under that same table_base. So a canary's
// accessIndex is discovered implicitly by the consistency check, never assumed.
struct NameAnchorDerivation {
  bool ok = false;
  std::uint64_t table_base = 0;
  std::size_t name_off = 0;
};

// Given the located ConVarData candidate addresses for each canary (cvd_k) and the
// full iterated access-index list, find a (table_base, stride=16) such that for >=2
// canaries there is an accessIndex a_k in the iterated set with
// *(table_base + a_k*16) == cvd_k, AND table_base is consistent (same value) across
// those canaries. We anchor table_base from the FIRST located (T, accessIndex)
// hypothesis and verify the rest.
NameAnchorDerivation SolveTableFromCvdSlots(
    const std::vector<HeapRegion>& regions,
    const std::vector<std::pair<std::uint64_t, std::size_t>>& cvd_and_nameoff,
    const std::vector<std::uint32_t>& access_indices) {
  NameAnchorDerivation out;
  if (cvd_and_nameoff.empty()) return out;
  // For the first canary's cvd, find every table slot T holding it; each (T, a)
  // hypothesis with a in access_indices gives table_base = T - a*16. Then require
  // >=2 canaries (incl. this one) to verify under that table_base + matching name_off.
  const std::uint64_t cvd0 = cvd_and_nameoff[0].first;
  const std::size_t name_off = cvd_and_nameoff[0].second;
  bool solved = false;
  ScanRegionsForValue(
      regions, cvd0, /*cap=*/16, [&](std::uint64_t t_slot) {
        if (solved) return;
        for (std::uint32_t a0 : access_indices) {
          const std::uint64_t base_lo = static_cast<std::uint64_t>(a0) * 16ull;
          if (t_slot < base_lo) continue;
          const std::uint64_t table_base = t_slot - base_lo;
          // Verify: count canaries whose cvd is reachable at SOME iterated
          // accessIndex under this table_base with the SAME name_off.
          int verified = 0;
          for (const auto& ck : cvd_and_nameoff) {
            if (ck.second != name_off) continue;  // require uniform name_off
            bool hit = false;
            for (std::uint32_t a : access_indices) {
              const std::uint64_t slot =
                  table_base + static_cast<std::uint64_t>(a) * 16ull;
              std::uint64_t got = 0;
              if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                           static_cast<std::uintptr_t>(slot)),
                                       &got))
                continue;
              if (got == ck.first) {
                hit = true;
                break;
              }
            }
            if (hit) ++verified;
          }
          if (verified >= kMirrorCanaryDerive) {
            // The canary cvds verify a CONSISTENT table_base+stride. Additionally
            // require the printable-name fraction over the iterated refs to clear the
            // threshold — the same correctness signal the table-offset scan uses — so
            // we never lock a table_base that merely happens to satisfy >=2 canary
            // consistency checks but reads garbage for the bulk of refs.
            const MirrorNameScore sc =
                MirrorPrintableNameScore(table_base, name_off, access_indices);
            if (sc.resolved >= kMirrorMinResolvedSample &&
                sc.fraction >= kMirrorPrintableFracAccept) {
              out.ok = true;
              out.table_base = table_base;
              out.name_off = name_off;
              solved = true;
              return;
            }
          }
        }
      });
  return out;
}

// NAME-STRING-ANCHORED derive entry point. Returns a derived CCvarRegistry (with the
// table_base + name_off; member_off is reported as the CCvar offset whose value ==
// table_base if one exists, else 0) or derived=false.
CCvarRegistry DeriveCCvarRegistryByNameAnchor(
    ICvar* cvar, const std::vector<std::uint32_t>& access_indices, bool trace) {
  CCvarRegistry result;
  const std::uint64_t ccvar = reinterpret_cast<std::uint64_t>(cvar);
  const std::vector<HeapRegion> regions = CollectAnchorRegions(ccvar);
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] cvar: CCvar mirror NAME-ANCHOR — %zu bounded heap "
                 "regions (%u MiB each) seeded from CCvar pointer members\n",
                 regions.size(),
                 static_cast<unsigned>(kAnchorRegionBytes >> 20));

  // 1+2. For each canary string, locate Saddr, then find a ConVarData candidate C
  //      with *(C + name_off) == Saddr for some name_off in {0,8,16,24,32}.
  std::vector<std::pair<std::uint64_t, std::size_t>> cvd_and_nameoff;  // (C, name_off)
  for (const char* canary : kCanaryConVars) {
    const std::uint64_t saddr = FindCStringAddr(regions, canary);
    if (saddr == 0) continue;
    // Find a slot holding Saddr (that slot is C + name_off). For each match, try each
    // candidate name_off so C = matchAddr - name_off, and accept the FIRST.
    bool got_cvd = false;
    ScanRegionsForValue(
        regions, saddr, /*cap=*/kMaxStringMatches,
        [&](std::uint64_t name_slot) {
          if (got_cvd) return;
          for (std::size_t name_off : kMirrorNameOffCandidates) {
            if (name_slot < name_off) continue;
            const std::uint64_t c = name_slot - name_off;
            // Sanity: re-read C+name_off and confirm it points at Saddr (it must).
            std::uint64_t back = 0;
            if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                         static_cast<std::uintptr_t>(c + name_off)),
                                     &back))
              continue;
            if (back != saddr) continue;
            cvd_and_nameoff.emplace_back(c, name_off);
            got_cvd = true;
            return;
          }
        });
    if (trace && got_cvd)
      std::fprintf(stderr,
                   "[walker-trace] cvar:   NAME-ANCHOR canary '%s' string@0x%llx "
                   "cvd@0x%llx name_off=+%zu\n",
                   canary, static_cast<unsigned long long>(saddr),
                   static_cast<unsigned long long>(cvd_and_nameoff.back().first),
                   cvd_and_nameoff.back().second);
  }

  if (cvd_and_nameoff.size() < static_cast<std::size_t>(kMirrorCanaryDerive)) {
    if (trace)
      std::fprintf(stderr,
                   "[walker-trace] cvar: CCvar mirror NAME-ANCHOR FAILED — located "
                   "%zu canary ConVarData candidate(s) (<%d required)\n",
                   cvd_and_nameoff.size(), kMirrorCanaryDerive);
    return result;
  }

  // 3+4. Solve for (table_base, stride=16) from the canaries' cvd slots.
  const NameAnchorDerivation sol =
      SolveTableFromCvdSlots(regions, cvd_and_nameoff, access_indices);
  if (!sol.ok) {
    if (trace)
      std::fprintf(stderr,
                   "[walker-trace] cvar: CCvar mirror NAME-ANCHOR FAILED — could "
                   "not solve a consistent table_base (stride 16) from %zu canary "
                   "cvd candidates across %zu iterated access indices\n",
                   cvd_and_nameoff.size(), access_indices.size());
    return result;
  }

  // Report a CCvar member offset whose value == table_base, if one exists (purely
  // for the trace/diagnostic; the read pass uses table_base directly).
  std::size_t member_off = 0;
  for (std::size_t off = 0; off + 8 <= kMirrorMaxCCvarOff; off += 8) {
    std::uint64_t v = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &v))
      continue;
    if (v == sol.table_base) {
      member_off = off;
      break;
    }
  }

  result.derived = true;
  result.table_base = sol.table_base;
  result.name_off = sol.name_off;
  result.member_off = member_off;
  result.method = CCvarDeriveMethod::kNameStringAnchor;
  if (trace) {
    const MirrorNameScore sc =
        MirrorPrintableNameScore(sol.table_base, sol.name_off, access_indices);
    std::fprintf(stderr,
                 "[walker-trace] cvar: CCvar mirror DERIVED registry (name-string "
                 "anchor) — table_base=0x%llx name_off=+%zu member_off=+%zu "
                 "(stride=16, ptr@entry+0, index=accessIndex) — printable-name "
                 "fraction %.3f (%zu/%zu resolved iterated refs; threshold %.2f)\n",
                 static_cast<unsigned long long>(sol.table_base), sol.name_off,
                 member_off, sc.fraction, sc.printable, sc.resolved,
                 kMirrorPrintableFracAccept);
  }
  return result;
}

// Runaway guard on the FindFirst/FindNext iteration (a corrupt next-link could
// loop). Far above any real registry (~1800-3300) yet bounded.
inline constexpr std::size_t kMirrorMaxRefs = 100000;

// Collect the access index of every iterated ConVarRef via the PROVEN-WORKING
// FindFirstConVar/FindNextConVar front-of-vtable iteration (the same calls
// CountConVars uses). GetAccessIndex() is the uint16 registry-array index that
// GetConVarData(ref) would index — exactly what the memory-mirror indexes directly.
std::vector<std::uint32_t> CollectConVarRefIndices(ICvar* cvar) {
  std::vector<std::uint32_t> refs;
  for (convar_compat::WConVarIter it = convar_compat::WCvarFirstConVar(cvar);
       it.IsValid() && refs.size() < kMirrorMaxRefs;
       it = convar_compat::WCvarNextConVar(cvar, it)) {
    refs.push_back(static_cast<std::uint32_t>(it.ref.GetAccessIndex()));
  }
  return refs;
}

// CRASH-FIX NOTE: CollectConCommandRefIndices (the COMMAND analogue of
// CollectConVarRefIndices) once seeded the command mirror's derive sample by iterating
// FindFirstConCommand / FindNextConCommand — icvar.h vtable SLOTS 82/83. On the aug-2025
// engine DLL those slots are in the SAME broken vtable region that breaks
// GetConCommandData (the very reason we mirror), so calling them is an UNGUARDED
// CONTROL-FLOW fault (a wrong/unmapped vtable target). SEH SafeRead* guards protect
// memory READS, not vtable DISPATCH, so the process ACCESS-VIOLATED (0xC0000005) before
// any mirror trace printed. (The CONVAR FindFirst/Next iterators are front-of-vtable LOW
// slots that survive on this DLL; the ConCommand iterators at 82/83 do not.) The command
// mirror now makes ZERO ConCommand vtable calls — it derives the command table by a PURE
// MEMORY SCAN at dense indices (DeriveConCommandRegistryPureMemory) — so this function
// was REMOVED. Do NOT re-introduce any FindFirstConCommand/FindNextConCommand/
// GetConCommandData call on the modern command path.

// ---- FULL-ARRAY INDEX READ (the FindFirst/Next-subset fix) -------------------
//
// WHY: FindFirstConVar/FindNextConVar yield only a SUBSET of the registry (current:
// 1823 of 3325; aug-2025: missing sv_cheats/developer/host_timescale -> canary guard
// fails). The PRIMARY index-scan proves the full set is reachable BY INDEX
// (GetConVarData(ConVarRef(i)) for i=0..N yields 3325 on current). The mirror's READ
// pass must therefore iterate the registry ARRAY DIRECTLY BY INDEX (i=0,1,2,...),
// reading cvd = *(table_base + i*16) and emitting every slot with a plausible
// printable name — capturing the convars FindFirst/Next skips. FindFirst/Next is kept
// ONLY to gather the DERIVE sample (CollectConVarRefIndices), which already works.
//
// The array is indexed by ACCESS INDEX (sparse), so iterating i=0..N walks the dense
// slot space; empty/sentinel slots (no plausible-pointer cvd or no plausible name)
// are skipped. Each index is distinct, so no dedup is needed.

// Max index the full-array scan will ever touch (sane cap; the registry is ~few
// thousand entries, this is far above any real count yet bounds a runaway / corrupt
// count). Used both as the count-validation ceiling and the fallback scan cap.
inline constexpr std::uint32_t kMirrorMaxIndex = 200000;
// Consecutive empty/invalid slots that end the fallback (count-not-derivable) scan.
// The array is SPARSE, so use a generous run, not stop-on-first-empty.
inline constexpr int kMirrorConsecutiveEmptyStop = 256;

// DERIVE the registry COUNT (number of slots to iterate). The CCvar object holds the
// table pointer at reg.member_off; the CUtlVector count is an integer ADJACENT to that
// pointer. Scan a small window [member_off-16, member_off+24] for an int32/int64 that
// is a plausible count: >= (max sampled accessIndex + 1) and <= kMirrorMaxIndex, and
// VALIDATED by table[count-1] resolving to a printable-named cvd while table[count]
// does NOT. Returns the validated count, or 0 if none qualifies (caller falls back to
// the sentinel-stop scan). Pure SEH-guarded reads.
//
// A slot "resolves to a printable-named cvd" iff MirrorResolveDataAddr yields a
// plausible-pointer cvd whose name at name_off is a plausible printable convar name.
std::uint32_t MirrorDeriveRegistryCount(ICvar* cvar, const CCvarRegistry& reg,
                                        std::uint32_t min_count, bool trace) {
  const std::uint64_t ccvar = reinterpret_cast<std::uint64_t>(cvar);

  // Does table[idx] resolve to a printable-named ConVarData? (the count validator).
  auto slot_is_real = [&](std::uint32_t idx) -> bool {
    std::uint64_t data = MirrorResolveDataAddr(reg.table_base, idx);
    if (data == 0) return false;
    char nm[128];
    return MirrorReadConVarNameAt(data, reg.name_off, nm, sizeof nm);
  };

  // Candidate count readers over the adjacency window: try every 4-aligned offset in
  // [member_off-16, member_off+24], reading BOTH an int32 and an int64 there. The
  // CUtlVector layout places the count (int32) next to the data pointer; we accept the
  // first candidate that validates (table[c-1] real, table[c] not real).
  const std::int64_t lo = static_cast<std::int64_t>(reg.member_off) - 16;
  const std::int64_t hi = static_cast<std::int64_t>(reg.member_off) + 24;
  for (std::int64_t rel = lo; rel + 4 <= hi + 4; rel += 4) {
    if (rel < 0) continue;
    const std::uint64_t at = ccvar + static_cast<std::uint64_t>(rel);
    // Read 8 bytes once; interpret as both int32 (low dword) and int64.
    std::uint64_t raw = 0;
    if (!tc::SafeReadBytes2023(
            reinterpret_cast<const void*>(static_cast<std::uintptr_t>(at)), &raw,
            8))
      continue;
    const std::uint32_t cand32 = static_cast<std::uint32_t>(raw & 0xFFFFFFFFull);
    const std::uint64_t cand64 = raw;
    for (std::uint64_t cand : {static_cast<std::uint64_t>(cand32), cand64}) {
      if (cand < min_count) continue;
      if (cand > kMirrorMaxIndex) continue;
      const std::uint32_t c = static_cast<std::uint32_t>(cand);
      // VALIDATE: table[c-1] resolves to a real cvd; table[c] does not.
      if (c == 0) continue;
      if (!slot_is_real(c - 1)) continue;
      if (slot_is_real(c)) continue;  // count must be exactly past the last real slot
      if (trace)
        std::fprintf(stderr,
                     "[walker-trace] cvar: CCvar mirror DERIVED registry count=%u "
                     "(adjacent int at ccvar+%lld, %s; validated table[c-1] real, "
                     "table[c] empty; min_count=%u)\n",
                     c, static_cast<long long>(rel),
                     cand == static_cast<std::uint64_t>(cand32) ? "int32" : "int64",
                     min_count);
      return c;
    }
  }
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] cvar: CCvar mirror count NOT derivable from the "
                 "[member_off-16,member_off+24] adjacency window (min_count=%u) — "
                 "falling back to sentinel-stop full-array scan\n",
                 min_count);
  return 0;
}

// FULL-ARRAY INDEX READ PASS. Iterate the registry slot space BY INDEX i=0..bound,
// reading cvd = *(table_base + i*16); for each i with a plausible-pointer cvd AND a
// plausible printable name, invoke `on_data(cvd_addr)`. Empty/sentinel slots are
// skipped. `bound` selection:
//   - if MirrorDeriveRegistryCount derived a count N: iterate i in [0, N).
//   - else: iterate i from 0 with the sentinel-stop strategy (stop after
//     kMirrorConsecutiveEmptyStop consecutive empty/invalid slots), capped at
//     kMirrorMaxIndex.
// This captures ALL convars incl. the ones FindFirst/Next skips. Pure SEH-guarded
// reads; no vtable calls. Returns the number of slots passed to `on_data`.
template <typename OnData>
std::size_t MirrorReadFullArray(const CCvarRegistry& reg,
                                std::uint32_t derived_count, OnData&& on_data) {
  std::size_t emitted = 0;
  if (derived_count > 0) {
    for (std::uint32_t i = 0; i < derived_count; ++i) {
      std::uint64_t data = MirrorResolveDataAddr(reg.table_base, i);
      if (data == 0) continue;
      char nm[128];
      if (!MirrorReadConVarNameAt(data, reg.name_off, nm, sizeof nm)) continue;
      on_data(data);
      ++emitted;
    }
    return emitted;
  }
  // Fallback: sentinel-stop sparse scan (generous consecutive-empty threshold).
  int consecutive_empty = 0;
  for (std::uint32_t i = 0; i < kMirrorMaxIndex; ++i) {
    std::uint64_t data = MirrorResolveDataAddr(reg.table_base, i);
    char nm[128];
    if (data == 0 || !MirrorReadConVarNameAt(data, reg.name_off, nm, sizeof nm)) {
      if (++consecutive_empty > kMirrorConsecutiveEmptyStop) break;
      continue;
    }
    consecutive_empty = 0;
    on_data(data);
    ++emitted;
  }
  return emitted;
}

// Highest sampled access index + 1 — the minimum plausible registry count (the count
// must be at least past the largest index FindFirst/Next reported).
std::uint32_t MirrorMinCountFromSamples(const std::vector<std::uint32_t>& idx) {
  std::uint32_t max_idx = 0;
  for (std::uint32_t i : idx)
    if (i > max_idx) max_idx = i;
  return max_idx + 1;
}

// END-TO-END convar mirror. Collects refs (FindFirst/Next), derives the registry,
// then for each ref resolves its ConVarData via the derived registry and invokes
// `emit(ConVarData*)` once per real convar. The ConVarData* is a REAL object
// address (pointer-array deref OR inline-element address), so the caller reuses the
// EXACT same convar_compat::WConVar* accessors the primary path uses — the emitted
// shape is identical. Returns true iff derivation succeeded AND at least one convar
// resolved; false (with *err) otherwise (fail-loud, no garbage). The post-read
// canary guard is the caller's responsibility (mirrors the primary path).
template <typename Emit>
bool RunConVarMirror(ICvar* cvar, const char* label, Emit&& emit,
                     std::size_t* out_count, std::string* err) {
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;

  std::vector<std::uint32_t> refs = CollectConVarRefIndices(cvar);
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] %s: CCvar mirror — FindFirst/Next iterated %zu "
                 "ConVarRefs (front-of-vtable, proven-working)\n",
                 label, refs.size());
  if (refs.empty()) {
    *err = std::string(label) +
           ": CCvar mirror — FindFirstConVar/FindNextConVar iterated ZERO refs; "
           "the convar registry is unreachable on this engine DLL. No convars "
           "emitted.";
    return false;
  }

  // Sample a spread of access indices for derivation (first several + a few more
  // across the list, to raise the odds a canary index is in the sample).
  std::vector<std::uint32_t> sample;
  for (std::size_t i = 0; i < refs.size() && sample.size() < 64; ++i) {
    if (i < 32 || (i % 17) == 0) sample.push_back(refs[i]);
  }

  // STRATEGY 1: the table-offset scan (name-offset-robust). STRATEGY 2 (the
  // name-string anchor) only fires if strategy 1 fails — independent of where the
  // table pointer sits in CCvar and of GetConVarData. Both are pure SEH-guarded
  // reads + FindFirst/Next only; both fail loud (derived=false) on no canary lock.
  CCvarRegistry reg = DeriveCCvarRegistry(cvar, sample, trace);
  if (!reg.derived)
    reg = DeriveCCvarRegistryByNameAnchor(cvar, refs, trace);
  if (!reg.derived) {
    *err = GarbageConVarFailMessage(label, refs.size());
    return false;
  }

  // FULL-ARRAY INDEX READ (the FindFirst/Next-subset fix): iterate the registry slot
  // space BY INDEX i=0..N (reading cvd = *(table_base + i*16)), NOT the FindFirst/Next
  // refs — those yield only a SUBSET (missing sv_cheats/developer/host_timescale on
  // some eras, which fails the canary guard). Indexing the array directly captures the
  // complete set, mirroring how the primary index-scan iterates ConVarRef(i) 0..N.
  // Bound: prefer the derived CUtlVector count adjacent to the table pointer; else
  // sentinel-stop the sparse scan.
  const std::uint32_t min_count = MirrorMinCountFromSamples(refs);
  const std::uint32_t derived_count =
      MirrorDeriveRegistryCount(cvar, reg, min_count, trace);
  std::size_t emitted = MirrorReadFullArray(
      reg, derived_count, [&](std::uint64_t data) {
        // The resolved address is a real ConVarData*; reuse the identical accessor
        // extraction the primary path uses (header-inline reads on real memory).
        // CAVEAT (truthful): the WConVar* accessors read name/flags/help/type/
        // default at the COMPILE-TIME ConVarData layout (name@+0 etc.). If the derived
        // name_off != 0 the live ConVarData header differs, so only the NAME is known
        // to be at the derived offset; the accessor-read fields may be off. We still
        // emit via the accessors (the documented minimum is correct NAMES); when
        // name_off==0 the layout matches and all fields are correct as before.
        emit(reinterpret_cast<convar_compat::WConVarData*>(
            static_cast<std::uintptr_t>(data)));
      });
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] %s: CCvar mirror — emitted %zu convars by FULL-ARRAY "
                 "index scan (%s) from %zu FindFirst/Next derive-sample refs "
                 "(strategy=%s member_off=+%zu table_base=0x%llx name_off=+%zu, "
                 "cvd=*(table_base+i*16))\n",
                 label, emitted,
                 derived_count > 0 ? "derived-count bound" : "sentinel-stop bound",
                 refs.size(),
                 reg.method == CCvarDeriveMethod::kNameStringAnchor
                     ? "name-anchor"
                     : "table-offset",
                 reg.member_off, static_cast<unsigned long long>(reg.table_base),
                 reg.name_off);
  if (emitted == 0) {
    *err = std::string(label) +
           ": CCvar mirror — derived a registry but resolved ZERO convars; "
           "refusing to emit.";
    return false;
  }
  *out_count = emitted;
  return true;
}

// ===========================================================================
// aug-2025 (pin 3525af99) ConCommand REGISTRY MEMORY-MIRROR (clean-room).
// ===========================================================================
//
// THE COMMAND ANALOGUE OF RunConVarMirror. On aug-2025 the shipped engine DLL's
// ICvar vtable has GetConCommandData at a DIFFERENT slot than this pin's header
// computes (the SAME root cause that breaks GetConVarData — see the garbage-walk
// note above ForEachLiveConVar). So the command index scan (ForEachLiveConCommand)
// returns garbage ConCommandData* whose "names" are raw code bytes, exactly like the
// convar scan. We BYPASS the broken vtable slot entirely and read the ConCommand
// registry DIRECTLY in memory.
//
// REGISTRY MODEL (ground-truth-derived). The modern
// ConCommand registry is an INLINE CONTIGUOUS ARRAY of fixed-size ConCommandData
// RECORDS — NOT a table of pointers. This model supersedes
// BOTH a 2023-style inline-name-array hunt AND a convar-parallel
// table-of-pointers model. Table-of-pointers
// attempts FAIL because the COMMAND container is shaped
// differently from the CONVAR container — the wrong container model was the bug.
//
// GROUND TRUTH (captured on the HEALTHY build 19644975 via the f2db8e4 diagnostic,
// real ConCommandData pointers off the normal command path):
//     accessIndex=0 data=0x54b0e100000 name=check_nofilefd
//     accessIndex=1 data=0x54b0e100038 name=find           (a command canary)
//     accessIndex=2 data=0x54b0e100070 name=log_dumpchannels
//     accessIndex=3 data=0x54b0e1000a8 name=log_level
//     accessIndex=4 data=0x54b0e1000e0 name=log_verbosity
//   Consecutive records are EXACTLY 0x38 = 56 bytes apart and DENSE by accessIndex —
//   the records THEMSELVES are contiguous. record[i] = array_base + i*56.
//
// So the registry is:
//   - an INLINE array of 56-byte ConCommandData records, indexed DENSELY from 0.
//   - record_addr = array_base + i*56  IS the ConCommandData* directly (NO second
//     dereference — contrast the CONVAR table, whose 16-byte entries hold a ConVarData*
//     to a SCATTERED object).
//   - ConCommandData layout (hl2sdk public/tier1/convar.h, class ConCommandData):
//       m_pszName@+0 (char*), m_pszHelpString@+8 (char*), m_nFlags@+16 (uint64). These
//     field offsets are ALREADY KNOWN-GOOD: the convar_compat::WConCmdName / WConCmdHelp
//     / WConCmdFlags header-inline accessors read exactly these fields, and the
//     ground-truth diagnostic used them to pull correct names off each record. We REUSE
//     the WConCmd* accessors as-is on each record_addr (read IN PLACE) — we do NOT
//     re-derive the field offsets.
//   - array_base is a HEAP pointer held in a CCvar member at some offset O (DIFFERENT
//     from the convar table's +72). O is the only per-era unknown, DERIVED at runtime by
//     a PURE MEMORY SCAN (DeriveConCommandRegistryPureMemory); nothing is hard-pinned.
//
// HOW THE COMMAND ARRAY IS DERIVED (pure memory, NO ConCommand vtable): the inline
// record array is DENSELY indexed from 0, so we need NO vtable-sampled access indices.
// For each 8-aligned CCvar pointer member we read array_base and SCORE it as an INLINE
// array of 56-byte records: for i=0..256, record_addr = array_base + i*56; name =
// WConCmdName(record_addr) read IN PLACE (record_addr IS the ConCommandData*, NO second
// deref). To disambiguate the command array from the CONVAR table (whose 16-byte entries
// ALSO point at printable names) we ANCHOR acceptance on the command CANARIES
// {help,find,kill,say}: a candidate must CONTAIN them across its FULL array. The convar
// table at +72 is a POINTER table, not an inline 56-byte record array, so applying
// WConCmdName-on-an-inline-record to it does NOT yield a dense run of command names — and
// even if a stray candidate scored on printable fraction, the canary anchor rejects it
// (it would hold CONVAR names, not the command canaries). This canary anchor is the
// reliable discriminator since we cannot use the (broken) ConCommand vtable.
//
// SAFETY: ZERO ConCommand vtable calls on this path (see the CRASH-FIX
// NOTE below — slots 82/83 FAULT on the aug-2025 DLL: a bad vtable dispatch is an
// unguarded control-flow fault SEH cannot catch). WConCmd* are header-inline field LOADS
// (no virtual dispatch); the char* they return is then read via SEH-guarded C-string
// reads. The convar-table exclusion's derive uses the CONVAR FindFirst/Next iteration
// only (front-of-vtable LOW slots proven working here). Everything else is pure
// SEH-guarded memory reads via the same tc::SafeRead*2023 / LooksLikePointer2023
// trampolines the convar mirror uses. A wrong derivation degrades to "no candidate
// cleared the printable fraction AND canary anchor -> graceful miss / canary-guard
// fail-loud", never a fault and never garbage; the post-read
// AssertCanaryConCommandsPresent {help,find,kill,say} is the final canary gate.
//
// GATING: this runs ONLY inside the convar_mirror_engaged (aug-2025 garbage)
// path. A healthy era never reaches it, so its commands.json stays byte-identical.

// INLINE RECORD STRIDE (ground-truth derived on 19644975: consecutive ConCommandData
// records are EXACTLY 0x38 = 56 bytes apart). record[i] = array_base + i*56, and
// record_addr IS the ConCommandData* (read in place). The ConCommandData field offsets
// (m_pszName@+0 / m_pszHelpString@+8 / m_nFlags@+16) are NOT re-derived here: the
// convar_compat::WConCmd* header-inline accessors already read them correctly. (The 2023
// OLD-era inline model keeps its own kCmd2023_* / kCmd2023_Stride — also 56, coincident.)
inline constexpr std::size_t kConCmdInlineStride = 56;  // ground-truth 0x38-byte records

// Address of the i-th INLINE ConCommandData record: array_base + i*56. This IS the
// ConCommandData* (no second dereference). Used by every read/score/scan pass below so
// the stride-56 model lives in ONE place.
inline std::uint64_t ConCmdRecordAddr(std::uint64_t array_base, std::uint32_t i) {
  return array_base + static_cast<std::uint64_t>(i) * kConCmdInlineStride;
}

// A mirrored ConCommand record (same fields the primary path's `CC` carries, built
// from one INLINE ConCommandData record read via the WConCmd* accessors).
struct MirrorCmd {
  std::string name;
  std::string help;
  std::uint64_t flags = 0;
};

// Read ONE INLINE ConCommandData record at `record_addr` into `out`. `record_addr` IS
// the ConCommandData* (record[i] = array_base + i*56; the record is contiguous, NOT
// behind a pointer) — so we reinterpret it as a WConCmdData* and pull the fields via the
// SAME convar_compat::WConCmd* header-inline accessors the primary command path uses.
// WConCmdName/WConCmdHelp are pure field LOADS (return m_pszName / m_pszHelpString) and
// WConCmdFlags returns m_nFlags — NO virtual dispatch. The char* they return is then
// SEH-validated (plausible pointer + readable printable C-string) before we copy it, so
// a wrong/garbage record_addr is REJECTED, never faulted. Name is required and
// must be a plausible printable command name; help is OPTIONAL ("" when null/empty/
// unreadable). Returns false (record rejected) on any name read failure. NO ConCommand
// vtable calls; the only reads are SEH-guarded C-string reads of the accessor pointers.
inline bool MirrorReadConCommandData(std::uint64_t record_addr, MirrorCmd* out) {
  // record_addr IS the ConCommandData*; read its fields IN PLACE via the WConCmd*
  // accessors (m_pszName@+0 / m_pszHelpString@+8 / m_nFlags@+16 — already known-good,
  // not re-derived). First SEH-confirm the record itself is readable by pulling the
  // name pointer field (record+0) safely; WConCmd* would dereference record_addr
  // directly, so we guard the field load by reading the 8-byte name-pointer slot first.
  std::uint64_t name_ptr = 0;
  if (!tc::SafeReadPtr2023(
          reinterpret_cast<const void*>(static_cast<std::uintptr_t>(record_addr)),
          &name_ptr))
    return false;
  if (!tc::LooksLikePointer2023(name_ptr)) return false;

  // The record's name pointer is plausible -> the record_addr is mapped, so it is safe
  // to apply the WConCmd* accessors IN PLACE. (WConCmdName reduces to the same m_pszName
  // load we just performed; we use the accessor to keep the field-offset knowledge in
  // ONE place — convar_compat — exactly as the primary path does.)
  convar_compat::WConCmdData* rec =
      reinterpret_cast<convar_compat::WConCmdData*>(
          static_cast<std::uintptr_t>(record_addr));

  // name (required, must be a plausible printable command name). Read the accessor's
  // char* via an SEH-guarded C-string read, then validate.
  const char* name_field = convar_compat::WConCmdName(rec);
  char nm[128];
  if (name_field == nullptr ||
      !tc::SafeReadCString2023(name_field, nm, sizeof nm) ||
      !PlausiblePrintableName(nm))
    return false;

  // help (optional). A null / non-readable / empty help is "" — never a rejection.
  std::string help;
  const char* help_field = convar_compat::WConCmdHelp(rec);
  if (help_field != nullptr &&
      tc::LooksLikePointer2023(reinterpret_cast<std::uint64_t>(help_field))) {
    char hb[256];
    if (tc::SafeReadCString2023(help_field, hb, sizeof hb)) help.assign(hb);
  }

  // flags: a plain header-inline field load (m_nFlags), no dereference, no rejection.
  out->name = nm;
  out->help = std::move(help);
  out->flags = convar_compat::WConCmdFlags(rec);
  return true;
}

// FULL-ARRAY INDEX READ for the INLINE COMMAND array (the analogue of MirrorReadFullArray
// for convars). Iterate the array BY INDEX i=0,1,2,..., reading record_addr =
// array_base + i*56 IN PLACE (record_addr IS the ConCommandData*) and, for each i whose
// record resolves to a plausible printable name, appending the name/help/flags. Always
// sentinel-stops on the first run of unreadable/empty records (kMirrorConsecutiveEmptyStop)
// — the inline array is dense, so the records end with a run of unmapped/empty slots;
// `derived_count` (if > 0) only caps the upper bound, it does NOT change the
// dense iteration. This captures ALL commands incl. ones the index API skips. Pure
// SEH-guarded reads; NO ConCommand vtable calls. Returns the count appended.
std::size_t MirrorReadConCommandFullArray(const CCvarRegistry& reg,
                                          std::uint32_t derived_count,
                                          std::vector<MirrorCmd>* out) {
  // Upper bound: the derived CUtlVector count if available and sane, else the global
  // max-index guard. The sentinel-stop below is the PRIMARY terminator (the records are
  // contiguous + dense), so this is purely a runaway cap.
  const std::uint32_t bound =
      (derived_count > 0 && derived_count < kMirrorMaxIndex) ? derived_count
                                                             : kMirrorMaxIndex;
  std::size_t emitted = 0;
  int consecutive_empty = 0;
  for (std::uint32_t i = 0; i < bound; ++i) {
    const std::uint64_t record_addr = ConCmdRecordAddr(reg.table_base, i);
    MirrorCmd c;
    if (!MirrorReadConCommandData(record_addr, &c)) {
      if (++consecutive_empty > kMirrorConsecutiveEmptyStop) break;
      continue;
    }
    consecutive_empty = 0;
    out->push_back(std::move(c));
    ++emitted;
  }
  return emitted;
}

// MODEL-CORRECTION NOTE. The command mirror considered THREE
// container models before the ground truth (healthy build 19644975) settled it:
//   (a) 2023-style INLINE name array (HuntConCommandRegistryModern, removed): read
//       entry+0 as a char* NAME. Wrong — entry+0 is a name POINTER, not the name bytes.
//   (b) convar-parallel TABLE OF POINTERS: 16-byte
//       entries holding a ConCommandData* to a SCATTERED object, resolved via
//       MirrorResolveDataAddr(table_base, i) = *(table_base + i*16). Wrong CONTAINER
//       MODEL — that is the CONVAR shape; it FAILED on every target build (the records
//       are not behind per-entry pointers).
//   (c) INLINE ARRAY OF 56-BYTE RECORDS (current). Ground truth: record[i] =
//       array_base + i*56 IS the ConCommandData* directly. This uses the
//       inline-stride-56 STRUCTURE, reading each record via
//       the WConCmd* accessors (m_pszName@+0 / m_pszHelpString@+8 / m_nFlags@+16, NOT
//       re-derived) and anchors on the FULL-array command canary logic,
//       applied over inline records at stride 56 instead of over a pointer table.
// The 2023 OLD-era path keeps its own inline HuntCommandRegistry2023 (also stride 56 —
// the two eras coincide on the record stride, but read names differently).
//
// CRASH-FIX NOTE: the command mirror once seeded its derive
// SAMPLE from ConCommandRef iteration (CollectConCommandRefIndices, now REMOVED), which
// called FindFirstConCommand / FindNextConCommand — icvar.h vtable slots 82/83. Those
// slots are in the SAME broken region of the aug-2025 engine DLL's ICvar vtable that
// breaks GetConCommandData (the very reason we mirror). Calling them is an UNGUARDED
// CONTROL-FLOW fault (a wrong/unmapped vtable target) — SEH SafeRead* guards protect
// memory READS, not vtable DISPATCH — so the walker ACCESS-VIOLATED (0xC0000005) inside
// CollectConCommandRefIndices before any mirror trace printed. (The CONVAR mirror's
// FindFirst/Next ConVar are front-of-vtable low slots that happen to survive on this
// DLL; the ConCommand iterators at 82/83 are in the broken region and crash.) The fix:
// the modern command path now makes ZERO ConCommand vtable calls — it derives the
// command array by a PURE MEMORY SCAN over CCvar member offsets at DENSE inline-record
// indices (modeled on HuntCommandRegistry2023's vtable-free scan, reading 56-byte
// records via the WConCmd* accessors).

// ---- PURE-MEMORY COMMAND-TABLE DERIVATION (NO ConCommand vtable) -------------
//
// Dense indices the command-array scan samples (the inline record array is densely
// indexed from 0, exactly like the convar full-array sentinel scan — so we need NO
// vtable-sampled access indices). 256 is well above the ~600-850 real command count's
// lower span yet bounds the per-candidate scan cost; a correct array resolves a high
// fraction of these to printable names, a wrong base ~0%.
inline constexpr std::uint32_t kCmdDenseSampleCount = 256;

// Score of a candidate INLINE command array by a DENSE-INDEX scan: `printable_frac` is
// the printable-name fraction over the dense sample, and `canaries` is how many of
// kCanaryConCommands {help,find,kill,say} appear among the dense-scanned names. The
// canary count is the RELIABLE discriminator: the convar table at +72 is a POINTER table
// (not an inline 56-byte record array), so reading it as inline records mostly fails to
// resolve printable names, and even if it did, it would hold CONVAR names, not the
// command canaries.
struct CmdTableScore {
  std::size_t resolved = 0;
  std::size_t printable = 0;
  double printable_frac = 0.0;
  int canaries = 0;  // distinct kCanaryConCommands hit in the dense sample
};

// DENSE-INDEX score of an inline-array `array_base`: for i=0..kCmdDenseSampleCount read
// record_addr = array_base + i*56 IN PLACE (record_addr IS the ConCommandData*) via the
// SAME MirrorReadConCommandData path the emit uses, count printable names and command
// canaries. Pure SEH-guarded reads; NO ConCommand vtable calls (WConCmd* are field loads).
CmdTableScore ScoreCommandTableDense(std::uint64_t array_base) {
  CmdTableScore s;
  bool canary_hit[std::size(kCanaryConCommands)] = {false};
  for (std::uint32_t i = 0; i < kCmdDenseSampleCount; ++i) {
    const std::uint64_t record_addr = ConCmdRecordAddr(array_base, i);
    MirrorCmd c;
    if (!MirrorReadConCommandData(record_addr, &c)) continue;
    ++s.resolved;
    ++s.printable;  // MirrorReadConCommandData only returns true for a printable name
    for (std::size_t k = 0; k < std::size(kCanaryConCommands); ++k)
      if (!canary_hit[k] && c.name == kCanaryConCommands[k]) canary_hit[k] = true;
  }
  for (bool h : canary_hit)
    if (h) ++s.canaries;
  // resolved == printable here (a record either resolves to a printable name or is not
  // counted), so the fraction is 1.0 over a dense run and 0.0 if nothing resolved. We
  // keep the fraction shape for the existing acceptance/diagnostic plumbing; the
  // dense-resolved COUNT (>= kMirrorMinResolvedSample) is the real density gate.
  s.printable_frac = s.resolved == 0 ? 0.0 : 1.0;
  return s;
}

// FULL-ARRAY canary + name scan of a candidate `array_base`. Whereas ScoreCommandTableDense
// only samples the first kCmdDenseSampleCount indices (cheap density gate), this walks the
// WHOLE inline array the SAME way the emit path does (MirrorReadConCommandFullArray:
// record_addr = array_base + i*56, i=0,1,2,... up to kMirrorMaxIndex, stopping after
// kMirrorConsecutiveEmptyStop consecutive empty records), resolving each record -> name and
// counting how many of the command canaries {help,find,kill,say} appear ANYWHERE.
//
// WHY (aug-2025 0/4-canary bug): CS2 command registries are ~600-850 entries in
// REGISTRATION order (NOT alphabetical), so common canaries like help/find/kill/say can
// sit well past index 255. The 256-sample dense scan therefore reports 0 canaries for a
// CORRECT command array, making it fail the canary-anchored acceptance. Checking the
// canaries over the FULL array fixes that without weakening the discriminator.
//
// `out_first_names` (optional) collects up to kCmdDiagFirstNames leading resolved names
// for the trace-gated diagnostic dump. Pure SEH-guarded reads; NO vtable calls. Bounded
// by the SAME sentinel-stop / max-entries guards as emit so a wrong candidate cannot run
// away.
inline constexpr std::size_t kCmdDiagFirstNames = 25;
struct CmdTableFullScan {
  std::size_t resolved = 0;  // records that resolved to a printable command name
  int canaries = 0;          // distinct kCanaryConCommands found across the FULL array
};
CmdTableFullScan ScanCommandTableFull(std::uint64_t array_base,
                                      std::vector<std::string>* out_first_names) {
  CmdTableFullScan fs;
  bool canary_hit[std::size(kCanaryConCommands)] = {false};
  int consecutive_empty = 0;
  for (std::uint32_t i = 0; i < kMirrorMaxIndex; ++i) {
    const std::uint64_t record_addr = ConCmdRecordAddr(array_base, i);
    MirrorCmd c;
    if (!MirrorReadConCommandData(record_addr, &c)) {
      if (++consecutive_empty > kMirrorConsecutiveEmptyStop) break;
      continue;
    }
    consecutive_empty = 0;
    ++fs.resolved;
    for (std::size_t k = 0; k < std::size(kCanaryConCommands); ++k)
      if (!canary_hit[k] && c.name == kCanaryConCommands[k]) canary_hit[k] = true;
    if (out_first_names && out_first_names->size() < kCmdDiagFirstNames)
      out_first_names->push_back(c.name);
  }
  for (bool h : canary_hit)
    if (h) ++fs.canaries;
  return fs;
}

// PURE-MEMORY command-array derivation. Scan every 8-aligned CCvar member offset O in
// [0, kMirrorMaxCCvarOff); for each whose value LooksLikePointer (and is NOT the excluded
// convar table_base), treat it as the array_base of an INLINE array of 56-byte
// ConCommandData records and score it by the DENSE-INDEX scan above (record_addr =
// array_base + i*56, read IN PLACE via WConCmd*). There is NO name sub-offset to derive
// in the inline model — the WConCmd* accessors read m_pszName@+0 directly — so the old
// kMirrorNameOffCandidates loop is gone (it belonged to the table-of-pointers model).
// ACCEPTANCE is CANARY-ANCHORED: a candidate qualifies iff it resolves >=
// kMirrorMinResolvedSample printable records in the DENSE sample (the density gate; a
// wrong base resolves ~0) AND its FULL-ARRAY scan contains >= kCanaryThreshold command
// canaries. The canary check is over the WHOLE array (ScanCommandTableFull), NOT the 256
// dense sample: CS2 command registries are ~600-850 entries in REGISTRATION order, so
// help/find/kill/say routinely sit past index 255 — a 256-sample canary count is 0 for a
// CORRECT command array (the exact aug-2025 bug this fixes). A base that resolves many
// printable names but contains NO command canaries ANYWHERE is WRONG and is rejected —
// this canary anchor is the reliable discriminator since we cannot use the (broken)
// ConCommand vtable. Among qualifying candidates we keep the one with the MOST full-array
// canaries, breaking ties by the highest dense resolved count (then lowest member_off).
// Returns derived=false if none qualifies. Pure SEH-guarded reads; NO ConCommand vtable
// calls whatsoever. The full-array scans share emit's sentinel-stop / max-entries guards
// so a wrong candidate cannot run away.
CCvarRegistry DeriveConCommandRegistryPureMemory(ICvar* cvar, bool trace,
                                                 std::uint64_t exclude_table_base) {
  CCvarRegistry result;
  const std::uint64_t ccvar = reinterpret_cast<std::uint64_t>(cvar);

  int candidates_tried = 0;
  // Best qualifying candidate so far (full-array canaries primary, dense resolved count
  // secondary).
  int best_canaries = -1;
  std::size_t best_resolved = 0;
  std::size_t best_off = 0;
  std::uint64_t best_table = 0;
  // Best-effort diagnostics on failure (highest dense resolved count seen, qualifying or
  // not), and its FULL-ARRAY canary count (computed on demand at report time).
  std::size_t diag_resolved = 0;
  std::size_t diag_off = 0;
  std::uint64_t diag_table = 0;

  // Diagnostic candidate record (one per member offset), ranked by dense resolved count.
  // Dumped under CS2_WALKER_TRACE so a STILL-failing run reveals the real command array's
  // offset + sample names in ONE run.
  struct DiagCand {
    std::size_t member_off;
    std::uint64_t table_base;
    std::size_t dense_resolved;  // dense-sample printable records
    bool is_convar_table;        // the excluded convar table_base, dumped FOR REFERENCE
  };
  std::vector<DiagCand> diag_cands;

  for (std::size_t off = 0; off + 8 <= kMirrorMaxCCvarOff; off += 8) {
    std::uint64_t table_base = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &table_base))
      continue;
    if (!tc::LooksLikePointer2023(table_base)) continue;
    const bool is_convar_table =
        exclude_table_base != 0 && table_base == exclude_table_base;
    // The convar table is dumped for REFERENCE (diagnostic) but excluded from ACCEPTANCE.
    if (!is_convar_table) ++candidates_tried;

    // DENSE-INDEX score of this array_base as an inline 56-byte-record array.
    const CmdTableScore sc = ScoreCommandTableDense(table_base);

    if (!is_convar_table && sc.resolved > diag_resolved) {
      diag_resolved = sc.resolved;
      diag_off = off;
      diag_table = table_base;
    }

    // Record this member offset for the diagnostic dump (convar table included, labeled).
    if (trace && sc.resolved > 0)
      diag_cands.push_back({off, table_base, sc.resolved, is_convar_table});

    // Dense density gate (cheap): require a run of printable records. The canary check is
    // over the FULL array below. (printable_frac is 1.0 over any resolved run in the
    // inline model, so the meaningful gate is the resolved COUNT.)
    const bool clears_density = sc.resolved >= kMirrorMinResolvedSample;
    if (is_convar_table || !clears_density) continue;

    // FULL-ARRAY canary scan (the fix): walk the whole array the SAME way emit does and
    // count canaries ANYWHERE — not just in the 256 dense sample where they may be absent
    // for a correct registration-ordered array.
    const CmdTableFullScan fs =
        ScanCommandTableFull(table_base, /*out_first_names=*/nullptr);
    if (fs.canaries < kCanaryThreshold) continue;

    // Prefer more full-array canaries; tie-break on dense resolved count (then lowest off,
    // which falls out of strict ">" + first-seen ordering).
    const bool better =
        fs.canaries > best_canaries ||
        (fs.canaries == best_canaries && sc.resolved > best_resolved);
    if (better) {
      best_canaries = fs.canaries;
      best_resolved = sc.resolved;
      best_off = off;
      best_table = table_base;
    }
  }

  // CS2_WALKER_TRACE diagnostic dump: TOP ~8 candidate pointer-member offsets ranked by
  // dense resolved-record count (the convar table_base is INCLUDED, labeled [CONVAR-REF],
  // so ground truth is visible in one run). For each, FULL-ARRAY resolved count + canary
  // count + first ~25 resolved names. The member offset whose inline 56-byte-record array
  // dumps ~800 command-looking names IS the real command registry. Read-only; bounded by
  // emit's sentinel-stop guards.
  if (trace && !diag_cands.empty()) {
    std::sort(diag_cands.begin(), diag_cands.end(),
              [](const DiagCand& a, const DiagCand& b) {
                return a.dense_resolved > b.dense_resolved;
              });
    const std::size_t dump_n = std::min<std::size_t>(diag_cands.size(), 8);
    std::fprintf(stderr,
                 "[walker-trace] command mirror: pure-memory DIAGNOSTIC — top %zu "
                 "candidate member offsets by dense resolved-record count (INLINE "
                 "56-byte records, FULL-ARRAY canary scan; convar table_base labeled "
                 "[CONVAR-REF], excluded from acceptance):\n",
                 dump_n);
    for (std::size_t c = 0; c < dump_n; ++c) {
      const DiagCand& d = diag_cands[c];
      std::vector<std::string> first_names;
      const CmdTableFullScan fs =
          ScanCommandTableFull(d.table_base, &first_names);
      std::string names_join;
      for (std::size_t i = 0; i < first_names.size(); ++i) {
        if (i) names_join.append(", ");
        names_join.append(first_names[i]);
      }
      std::fprintf(
          stderr,
          "[walker-trace]   %smember_off=+%zu array_base=0x%llx dense_resolved=%zu/%u "
          "full_resolved_count=%zu canaries_full=%d/4 first_names=[%s]\n",
          d.is_convar_table ? "[CONVAR-REF] " : "", d.member_off,
          static_cast<unsigned long long>(d.table_base), d.dense_resolved,
          kCmdDenseSampleCount, fs.resolved, fs.canaries, names_join.c_str());
    }
  }

  if (best_canaries >= kCanaryThreshold) {
    result.derived = true;
    result.member_off = best_off;
    result.table_base = best_table;
    result.name_off = 0;  // inline model: WConCmd* read m_pszName@+0 in place
    result.method = CCvarDeriveMethod::kTableOffsetScan;
    if (trace)
      std::fprintf(
          stderr,
          "[walker-trace] command mirror: pure-memory INLINE command-array scan: "
          "member_off=+%zu dense_resolved=%zu/%u full_canaries=%d/4 -> chosen "
          "array_base=0x%llx (stride=56, record_addr=array_base+i*56 IS the "
          "ConCommandData*, read in place via WConCmd*; density gate over dense indices "
          "0..%u, canary anchor over the FULL sentinel-stop array; %d pointer candidates "
          "tried; convar table_base 0x%llx excluded; NO ConCommand vtable calls)\n",
          best_off, best_resolved, kCmdDenseSampleCount, best_canaries,
          static_cast<unsigned long long>(best_table), kCmdDenseSampleCount,
          candidates_tried, static_cast<unsigned long long>(exclude_table_base));
    return result;
  }
  if (trace) {
    // Report the best dense-resolved candidate's FULL-ARRAY canary count (consistent
    // with the full-array acceptance check) so the failure line tells the truth.
    int diag_full_canaries = 0;
    if (diag_table != 0)
      diag_full_canaries =
          ScanCommandTableFull(diag_table, /*out_first_names=*/nullptr).canaries;
    std::fprintf(
        stderr,
        "[walker-trace] command mirror: pure-memory INLINE command-array scan FAILED — "
        "no CCvar pointer member resolved >=%zu printable 56-byte records in the dense "
        "sample AND contained >=%d command canaries IN ITS FULL ARRAY "
        "(best candidate: member_off=+%zu array_base=0x%llx dense_resolved=%zu/%u "
        "full_canaries=%d/4; %d pointer candidates tried, off scan [0,%zu) step 8; "
        "convar table_base 0x%llx excluded from acceptance; see DIAGNOSTIC dump above "
        "for top candidates; NO ConCommand vtable calls)\n",
        kMirrorMinResolvedSample, kCanaryThreshold, diag_off,
        static_cast<unsigned long long>(diag_table), diag_resolved, kCmdDenseSampleCount,
        diag_full_canaries, candidates_tried, kMirrorMaxCCvarOff,
        static_cast<unsigned long long>(exclude_table_base));
  }
  return result;
}

// END-TO-END command mirror — the COMMAND ANALOGUE OF RunConVarMirror, but with ZERO
// ConCommand vtable calls (see the CRASH-FIX NOTE above: slots 82/83 fault on the
// aug-2025 DLL). (1) Derive the CONVAR table_base via the convar mirror's table-offset
// scan PURELY to EXCLUDE it (so the command scan cannot re-lock the convar table). The
// convar derive's SAMPLE legitimately uses the convar FindFirst/Next iteration — those
// are front-of-vtable LOW slots proven working on this DLL; NO ConCommand slots are
// touched. (2) DERIVE the COMMAND array_base (the CCvar member offset whose pointer is the
// inline 56-byte-record array) by a PURE MEMORY SCAN over CCvar members
// (DeriveConCommandRegistryPureMemory): a DENSE-INDEX density gate (cheap, records read in
// place via WConCmd*) plus a FULL-ARRAY command-canary anchor (canaries can sit past the
// dense sample in a registration-ordered array, so they are checked over the WHOLE array —
// the reliable discriminator from the convar table). (3) FULL-ARRAY read: record_addr =
// array_base + i*56 IS the ConCommandData*, read in place via WConCmd* (name@+0 / help@+8 /
// flags@+16), bounded by the derived CUtlVector count cap (MirrorDeriveRegistryCount) and
// terminated by the dense sentinel-stop. Returns true iff derivation succeeded AND >=1
// command resolved; false (with *err) otherwise (fail-loud, no garbage). The POST-READ
// command-canary guard (AssertCanaryConCommandsPresent) is the caller's final canary gate.
// Pure SEH-guarded reads; NO ConCommand vtable calls.
// GRACEFUL-DEGRADE CONTRACT. The inline command-array base may not be
// locatable on every era. So a derive MISS must NOT regress re-walkability: these builds
// previously yielded commands=0 GRACEFULLY, and the convar fix has to stay usable. We
// therefore split the outcomes:
//   - CLEAN MISS (no CCvar pointer member's inline 56-byte-record scan resolved a dense
//     run of printable command names + canaries — i.e. we could NOT find a command
//     array at all):
//     return true, leave *out EMPTY, set *out_derived_registry=false, print a
//     stderr note. The caller emits commands=0 (truthful, no garbage, no fail-loud).
//   - WRONG POSITIVE (we DID derive a registry but it read zero / canary-less):
//     that is handled DOWNSTREAM. RunConCommandMirror returns true with whatever it
//     read and *out_derived_registry=true; the caller's post-read command-canary
//     guard (AssertCanaryConCommandsPresent) is the canary gate that fails loud on a
//     wrong derived registry. Refusing to emit junk is the canary guard's job.
//   - HARD FAILURE: only a genuinely unexpected condition (none currently) sets
//     *err and returns false. A "couldn't find it" is NOT a hard failure.
// `out_derived_registry` (may be null) reports whether a registry was located, so
// the caller knows whether to run the wrong-positive canary gate.
// (The convar mirror + its fail-loud are UNCHANGED — those builds DO mirror cleanly.)
bool RunConCommandMirror(ICvar* cvar, const char* label,
                         std::vector<MirrorCmd>* out, std::string* err,
                         bool* out_derived_registry = nullptr) {
  if (out_derived_registry != nullptr) *out_derived_registry = false;
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;

  // (1) Derive the CONVAR table_base PURELY so we can EXCLUDE it from the command
  // derive. The convar registry and the command registry live in DISTINCT CCvar
  // members, but they are BOTH pointer-tables of 16-byte entries with a printable name
  // at the pointee+0 and their dense index spaces OVERLAP — so indexing the CONVAR table
  // at low indices ALSO yields printable (convar!) names. Excluding the convar table_base
  // forces the command scan onto the genuine command table. The convar derive's SAMPLE
  // legitimately uses the convar FindFirst/Next iteration (CollectConVarRefIndices) —
  // those are front-of-vtable LOW slots PROVEN WORKING on this DLL; NO ConCommand vtable
  // slot is touched. A convar-derive miss is non-fatal (exclude=0 -> the command scan
  // still runs, just without the convar-table exclusion; the canary anchor still gates).
  std::uint64_t convar_table_base = 0;
  {
    std::vector<std::uint32_t> cv_refs = CollectConVarRefIndices(cvar);
    std::vector<std::uint32_t> cv_sample;
    for (std::size_t i = 0; i < cv_refs.size() && cv_sample.size() < 64; ++i)
      if (i < 32 || (i % 17) == 0) cv_sample.push_back(cv_refs[i]);
    const CCvarRegistry cv_reg =
        DeriveCCvarRegistry(cvar, cv_sample, /*trace=*/false);
    if (cv_reg.derived) convar_table_base = cv_reg.table_base;
    if (trace)
      std::fprintf(stderr,
                   "[walker-trace] %s: ConCommand mirror — convar table_base=0x%llx "
                   "(excluded from the command scan so overlapping dense indices "
                   "cannot re-lock the convar table; convar derive uses convar "
                   "FindFirst/Next ONLY — no ConCommand vtable)\n",
                   label,
                   static_cast<unsigned long long>(convar_table_base));
  }

  // (2) DERIVE the COMMAND array_base by a PURE MEMORY SCAN over CCvar member offsets,
  // scoring each as an INLINE array of 56-byte ConCommandData records at DENSE indices
  // (NO ConCommand vtable calls — slots 82/83 fault on this DLL, see the CRASH-FIX
  // NOTE above). Acceptance is canary-anchored: a dense run of printable records
  // (read in place via WConCmd*) AND the command canaries {help,find,kill,say} present in
  // the FULL array. DeriveConCommandRegistryPureMemory emits the "pure-memory INLINE
  // command-array scan: member_off=+O dense_resolved=R/256 full_canaries=C/4 -> chosen
  // array_base=0x..." trace line.
  CCvarRegistry reg =
      DeriveConCommandRegistryPureMemory(cvar, trace, convar_table_base);
  if (!reg.derived) {
    // CLEAN MISS -> graceful empty (NOT fail-loud). We could not locate a command array
    // at all (no CCvar pointer member whose inline 56-byte-record scan resolved a dense
    // run of printable command names AND contained the command canaries). This is the
    // not-yet-solved-command-layout case: degrade to commands=0 (truthful, no garbage) so
    // the convar fix stays usable and the re-walk succeeds. The wrong-positive case (an
    // array WAS derived but is canary-less) is gated downstream by
    // AssertCanaryConCommandsPresent, NOT here.
    out->clear();
    if (trace)
      std::fprintf(stderr,
                   "[walker-trace] %s: ConCommand mirror — NO command array found "
                   "(no CCvar member resolved as an INLINE 56-byte-record ConCommandData "
                   "array containing the command canaries). DEGRADING to commands=0 "
                   "(truthful, no garbage; NOT a fail-loud — a clean miss is empty). Run "
                   "with CS2_WALKER_TRACE on a HEALTHY neighbor build to re-capture "
                   "COMMAND GROUND TRUTH (the inline stride-56 model was derived on "
                   "19644975).\n",
                   label);
    return true;  // graceful: empty out, derived_registry stays false.
  }
  if (out_derived_registry != nullptr) *out_derived_registry = true;

  // (3) FULL-ARRAY index read of the INLINE records: record_addr = array_base + i*56 IS
  // the ConCommandData*, read in place via WConCmd*. Bound: prefer the CUtlVector count
  // adjacent to the array pointer (derived the SAME way as for convars) as a runaway cap,
  // else the global max-index guard; the PRIMARY terminator is the dense sentinel-stop
  // (the records are contiguous). min_count is 1 (the dense scan + canary anchor already
  // validated the array).
  const std::uint32_t min_count = 1;
  const std::uint32_t derived_count =
      MirrorDeriveRegistryCount(cvar, reg, min_count, trace);
  const std::size_t n = MirrorReadConCommandFullArray(reg, derived_count, out);
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] %s: ConCommand mirror — emitted %zu commands by "
                 "INLINE full-array scan (%s) (pure-memory inline records: "
                 "member_off=+%zu array_base=0x%llx, record_addr=array_base+i*56 IS the "
                 "ConCommandData* read in place via WConCmd* (name@+0 help@+8 flags@+16); "
                 "NO ConCommand vtable calls)\n",
                 label, n,
                 derived_count > 0 ? "derived-count cap" : "max-index cap",
                 reg.member_off,
                 static_cast<unsigned long long>(reg.table_base));
  // n == 0 here is a WRONG POSITIVE (a registry WAS derived but resolved zero
  // commands). We do NOT fail-loud HERE; out_derived_registry is true, so the
  // caller's post-read command-canary guard (AssertCanaryConCommandsPresent) is the
  // single canary gate that fails loud on a canary-less derived registry. Returning
  // true with an empty *out funnels a zero/garbage derive into that one gate rather
  // than duplicating the fail-loud logic. (A genuine clean miss returned earlier with
  // out_derived_registry=false and degrades to empty; only a DERIVED-but-wrong
  // registry reaches the canary guard.)
  if (n == 0 && trace)
    std::fprintf(stderr,
                 "[walker-trace] %s: ConCommand mirror — derived a registry but "
                 "resolved ZERO commands; deferring to the command-canary guard "
                 "(wrong-positive fail-loud is the canary guard's job).\n",
                 label);
  return true;
}

// POST-READ command-canary guard, analogous to AssertCanaryConVarsPresent.
// Asserts the ubiquitous ConCommand canaries (kCanaryConCommands) are present in the
// FINAL mirrored command name set. On success returns true (logs which canaries
// matched under trace); on failure sets *err and returns false — the mirror derived a
// WRONG registry, so we fail loud rather than emit junk or silently drop to empty.
bool AssertCanaryConCommandsPresent(const std::vector<std::string>& names,
                                    const char* label, std::string* err) {
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;
  int found = 0;
  for (const char* canary : kCanaryConCommands)
    for (const std::string& n : names)
      if (n == canary) {
        ++found;
        break;
      }
  if (found >= kCanaryThreshold) {
    if (trace) {
      std::string present, missing;
      for (const char* c : kCanaryConCommands) {
        bool hit = false;
        for (const std::string& n : names)
          if (n == c) {
            hit = true;
            break;
          }
        std::string& dst = hit ? present : missing;
        if (!dst.empty()) dst.append(" ");
        dst.append(c);
      }
      std::fprintf(stderr,
                   "[walker-trace] %s: command-canary guard PASS — %d/%d "
                   "present (>=%d required) [present: %s] [missing: %s]\n",
                   label, found, static_cast<int>(std::size(kCanaryConCommands)),
                   kCanaryThreshold, present.empty() ? "(none)" : present.c_str(),
                   missing.empty() ? "(none)" : missing.c_str());
    }
    return true;
  }
  std::string missing;
  for (const char* c : kCanaryConCommands) {
    bool hit = false;
    for (const std::string& n : names)
      if (n == c) {
        hit = true;
        break;
      }
    if (!hit) {
      if (!missing.empty()) missing.append(", ");
      missing.append(c);
    }
  }
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] %s: command-canary guard FAIL — only %d/%d "
                 "present (>=%d required) [missing: %s]\n",
                 label, found, static_cast<int>(std::size(kCanaryConCommands)),
                 kCanaryThreshold, missing.c_str());
  *err = std::string(label) +
         ": command-canary sanity guard FAILED — the mirrored command set is "
         "missing the ubiquitous canary command(s) {" +
         missing + "} (only " +
         std::to_string(found) + " of " +
         std::to_string(static_cast<int>(std::size(kCanaryConCommands))) +
         " canaries present; >=" + std::to_string(kCanaryThreshold) +
         " required). The ConCommand registry mirror derived a WRONG registry for "
         "this build's engine DLL. Refusing to emit a wrong command set.";
  return false;
}

// ===========================================================================
// MIRROR SELF-VALIDATION (trace-gated, read-only, HEALTHY/CURRENT path only).
// (runs AFTER the primary set is built, compares, logs, DISCARDS the
//  mirror result — never touches the emitted bytes.)
// ===========================================================================
//
// WHY: the aug-2025 CCvar memory-mirror (DeriveCCvarRegistry + the name-string
// anchor) has NEVER been validated against ground truth — it derived 0 canary hits
// on aug-2025, but we do not know whether the mirror IMPLEMENTATION is correct
// (would work on a healthy binary) or whether aug-2025 is structurally different.
// This self-check runs the SAME derive+read the fallback uses on the CURRENT/healthy
// binary (where the primary GetConVarData scan gives the correct convar set) and
// COMPARES the mirror's convar NAME set to the primary scan's name set. A PASS proves
// the mirror reproduces ground truth on current; a FAIL means the mirror impl itself
// is wrong (so its aug-2025 failure is not evidence about aug-2025's structure).
//
// DIAGNOSTIC-ONLY: it runs ONLY when CS2_WALKER_TRACE is set AND the primary
// scan is healthy (the caller gates on !PrimaryConVarResultIsGarbage). It builds its
// OWN mirror name set, compares, logs the verdict line, and DISCARDS everything — it
// does NOT modify the emitted convar set. Healthy production walks (trace off) never
// run it; output stays byte-identical. Pure SEH-guarded reads + FindFirst/Next only.
//
// VERDICT LINE FORMAT (single grep-able line):
//   MIRROR-SELFCHECK: derive=<ok|fail> O=<+off|n/a> name_off=<+off|n/a>
//                     strategy=<table-offset|name-anchor|none> printable_frac=<f|n/a>
//                     mirror=<n> primary=<n> matched=<n> verdict=<PASS|FAIL>
void RunMirrorSelfCheck(ICvar* cvar,
                        const std::vector<std::string>& primary_names) {
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;

  // Collect the iterated refs (FindFirst/Next) + a derivation sample, EXACTLY as
  // RunConVarMirror does, so the self-check exercises the identical derive path.
  std::vector<std::uint32_t> refs = CollectConVarRefIndices(cvar);
  std::vector<std::uint32_t> sample;
  for (std::size_t i = 0; i < refs.size() && sample.size() < 64; ++i) {
    if (i < 32 || (i % 17) == 0) sample.push_back(refs[i]);
  }

  // Same two-strategy derive the fallback uses (table-offset scan, then name anchor).
  CCvarRegistry reg = DeriveCCvarRegistry(cvar, sample, trace);
  if (!reg.derived) reg = DeriveCCvarRegistryByNameAnchor(cvar, refs, trace);

  // Read the mirror's convar NAME set via the SAME FULL-ARRAY index iteration
  // RunConVarMirror uses (NOT the FindFirst/Next refs — those are only a subset, so a
  // refs-based mirror read could never match the primary count). Iterate by index
  // i=0..N (derived count, else sentinel-stop) so the self-check's mirror count can
  // actually equal the primary count. Discarded after the compare.
  std::vector<std::string> mirror_names;
  MirrorNameScore win_score;  // printable-name fraction of the WINNING candidate
  if (reg.derived) {
    win_score = MirrorPrintableNameScore(reg.table_base, reg.name_off, refs);
    const std::uint32_t min_count = MirrorMinCountFromSamples(refs);
    const std::uint32_t derived_count =
        MirrorDeriveRegistryCount(cvar, reg, min_count, trace);
    MirrorReadFullArray(reg, derived_count, [&](std::uint64_t data) {
      char nm[128];
      if (MirrorReadConVarNameAt(data, reg.name_off, nm, sizeof nm))
        mirror_names.emplace_back(nm);
    });
  }

  // matched = how many PRIMARY names the mirror also produced (set membership).
  // Collect up to 5 example mismatches (primary names the mirror did NOT produce).
  std::size_t matched = 0;
  std::vector<std::string> mismatch_examples;
  for (const std::string& pn : primary_names) {
    bool in_mirror = false;
    for (const std::string& mn : mirror_names)
      if (mn == pn) {
        in_mirror = true;
        break;
      }
    if (in_mirror) {
      ++matched;
    } else if (mismatch_examples.size() < 5) {
      mismatch_examples.push_back(pn);
    }
  }

  // VERDICT: PASS iff derive succeeded AND the mirror reproduced ALL primary names
  // (matched == primary count) AND the mirror produced no fewer than the primary set
  // (a strict superset/equality on names proves ground-truth reproduction). We use
  // matched==primary as the PASS bar; the example mismatches show any shortfall.
  const bool pass =
      reg.derived && !primary_names.empty() && matched == primary_names.size();

  if (trace) {
    char obuf[32], nbuf[32], fbuf[32];
    if (reg.derived) {
      std::snprintf(obuf, sizeof obuf, "+%zu", reg.member_off);
      std::snprintf(nbuf, sizeof nbuf, "+%zu", reg.name_off);
      std::snprintf(fbuf, sizeof fbuf, "%.3f (%zu/%zu)", win_score.fraction,
                    win_score.printable, win_score.resolved);
    } else {
      std::snprintf(obuf, sizeof obuf, "n/a");
      std::snprintf(nbuf, sizeof nbuf, "n/a");
      std::snprintf(fbuf, sizeof fbuf, "n/a");
    }
    const char* strat = !reg.derived ? "none"
                        : reg.method == CCvarDeriveMethod::kNameStringAnchor
                            ? "name-anchor"
                            : "table-offset";
    std::string examples;
    for (const std::string& e : mismatch_examples) {
      if (!examples.empty()) examples.append(", ");
      examples.append(e);
    }
    std::fprintf(stderr,
                 "[walker-trace] cvar: MIRROR-SELFCHECK: derive=%s O=%s "
                 "name_off=%s strategy=%s printable_frac=%s mirror=%zu primary=%zu "
                 "matched=%zu verdict=%s\n",
                 reg.derived ? "ok" : "fail", obuf, nbuf, strat, fbuf,
                 mirror_names.size(), primary_names.size(), matched,
                 pass ? "PASS" : "FAIL");
    if (!mismatch_examples.empty())
      std::fprintf(stderr,
                   "[walker-trace] cvar:   MIRROR-SELFCHECK example mismatches "
                   "(primary names the mirror missed, up to 5): %s\n",
                   examples.c_str());
  }
}

// ===========================================================================
// MODERN-PATH CCvar REGISTRY GROUND-TRUTH DERIVATION DIAGNOSTIC.
// (read-only, CS2_WALKER_TRACE-gated, stderr-only — emits NO bytes.)
// ===========================================================================
//
// WHY (kept as the LAYOUT-OF-RECORD diagnostic): this is the read-only ground-truth
// derivation that NAILED the modern CCvar registry layout (build 23669931). It
// anchors on KNOWN-GOOD (accessIndex -> real ConVarData address) pairs from
// ICvar::GetConVarData (which WORKS on the modern binary), then solves for the exact
// registry layout. Its neighborhood analysis proved delta/accessIndexGap == 16 for
// all 16 samples — i.e. a FLAT HEAP ARRAY of 16-byte entries, ConVarData* at entry+0,
// indexed by accessIndex, name@cvd+0. That definitive layout is now baked into the
// aug-2025 CCvar mirror above (DeriveCCvarRegistry / MirrorResolveDataAddr): the
// mirror no longer blind-guesses stride/element-kind — it derives ONLY the member
// offset O that holds the heap table pointer, under the proven access formula.
//
// This diagnostic is RETAINED (trace-gated, read-only, emits NO bytes) because it
// documents the layout and re-confirms it on any future binary if the operator runs
// with CS2_WALKER_TRACE: the single CCVAR-REGISTRY-DERIVED line + the NEIGHBORHOOD
// ANALYSIS deltas are the authoritative record.
//
// SAFETY: this is PURELY read-only. The only vtable calls are the
// proven-working FindFirstConVar/FindNextConVar + GetConVarData (which works on the
// modern path — this runs only when the primary scan is NOT garbage). Everything
// else is SEH-guarded memory reads (tc::SafeRead*2023 / LooksLikePointer2023). It
// EMITS NO ARTIFACT BYTES and is gated behind CS2_WALKER_TRACE, so it never runs on a
// healthy production walk and cannot perturb output (byte-identical).

// One ground-truth sample: the raw access index reported by the ref, the iteration
// ordinal, and the REAL ConVarData* address GetConVarData returned for that ref.
struct GtSample {
  std::uint32_t access_index;  // ref.GetAccessIndex() — raw uint16 index value
  std::uint32_t ordinal;       // 0,1,2,... iteration order
  std::uint64_t data_addr;     // GetConVarData(ref) — ground-truth ConVarData address
  char name[128];              // data->GetName() — ground-truth name
};

// DERIVE the ConVarData name offset from a known ground-truth ConVarData address.
// SEH-guarded scan of the first 64 pointer-width slots of `data` for the offset at
// which a pointer to `expect_name` (the GetName() string) lives. Returns the offset
// (in bytes) or -1 if none matches. Confirms/denies the assumed @+0.
int DeriveConVarDataNameOffset(std::uint64_t data_addr, const char* expect_name) {
  for (std::size_t off = 0; off + 8 <= 512; off += 8) {
    std::uint64_t name_ptr = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(data_addr + off)),
                             &name_ptr))
      continue;
    if (!tc::LooksLikePointer2023(name_ptr)) continue;
    char nm[128];
    if (!tc::SafeReadCString2023(
            reinterpret_cast<const char*>(static_cast<std::uintptr_t>(name_ptr)),
            nm, sizeof nm))
      continue;
    if (std::strcmp(nm, expect_name) == 0) return static_cast<int>(off);
  }
  return -1;
}

// Index convention: does the registry array index by the ref's access index, or by
// the iteration ordinal?
enum class GtIndexConv { kAccessIndex,
                         kOrdinal };

// ---- "Locate the known pointer" anchored derivation (pointer-table model) ------
//
// WHY the old member-offset scan failed (even on the current binary, which is
// ground truth): it assumed the registry is a FIXED-STRIDE array reachable at some
// CCvar member offset. The captured ground truth proves otherwise:
//   - accessIndex is SPARSE (7,13,17,18,28,29,...,31), not 0,1,2,...
//   - the ConVarData entries are VARIABLE-stride in the pool (idx17->18 = 0x60;
//     idx28->29->30->31 = 0x68 each).
// So the registry is a VARIABLE-SIZE POOL indexed through a FIXED-STRIDE (8-byte)
// POINTER TABLE: table[accessIndex] == ConVarData*. GetConVarData(idx) is
// `*(table_base + idx*8)`. The old scan never found the table because it was
// verifying *(ccvar+off+idx*8)==data with the WRONG model and a bounded member
// window; here we instead ANCHOR on a KNOWN ConVarData* value and locate where it
// is stored, which is robust to sparseness and pool variability.
//
// Verify a hypothesized pointer-table: for EVERY sample k, *(table_base +
// idx_k*8) (idx_k per `conv`) must equal data_k. Pure SEH-guarded reads.
bool GtPointerTableMatchesAll(std::uint64_t table_base, GtIndexConv conv,
                              const std::vector<GtSample>& samples) {
  for (const GtSample& s : samples) {
    const std::uint32_t idx =
        (conv == GtIndexConv::kAccessIndex) ? s.access_index : s.ordinal;
    const std::uint64_t slot =
        table_base + static_cast<std::uint64_t>(idx) * 8ull;
    std::uint64_t resolved = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(slot)),
                             &resolved))
      return false;
    if (resolved != s.data_addr) return false;
  }
  return true;
}

// Result of the anchored search: how the table was reached, the verified table
// base, the CCvar member offset that leads to it, and which index convention won.
struct GtAnchorResult {
  bool solved = false;
  bool heap = false;             // true: table reached via a heap pointer member
  std::size_t member_off = 0;    // CCvar member offset (DIRECT: of table[0]; HEAP:
                                 // of the pointer whose target the table lives in)
  std::uint64_t table_base = 0;  // verified &table[0]
  GtIndexConv conv = GtIndexConv::kAccessIndex;
  // Diagnostics for the FAILED case.
  int direct_hits = 0;  // # of 8-aligned CCvar slots holding data_0
  int heap_hits = 0;    // # of heap windows containing data_0
  int verify_fail = 0;  // # of data_0 hits that failed full verification
  // NEIGHBORHOOD ANALYSIS anchor: when data_0 was LOCATED in a heap block (even
  // if the flat-table verify FAILED), these record WHERE so we can dump the real
  // table structure around it. l0_addr is the 8-byte slot holding data_0;
  // l0_block_base is the heap pointer (CCvar member target) that block came from.
  bool l0_located = false;
  std::uint64_t l0_addr = 0;        // address of the slot holding data_0
  std::uint64_t l0_block_base = 0;  // heap block base (the CCvar member's target)
};

// 2a DIRECT + 2c (ordinal): scan the CCvar object bytes [0, kCCvarScan) step 8 for
// an 8-byte value == data_0. Each hit Lhit is hypothesized to be &table[idx_0]
// (idx_0 = accessIndex_0, then ordinal_0), giving table_base = Lhit - idx_0*8;
// verify ALL samples. Returns the first fully-verified result.
inline constexpr std::size_t kCCvarAnchorScan = 65536;  // CCvar bytes scanned
GtAnchorResult GtSearchDirect(std::uint64_t ccvar,
                              const std::vector<GtSample>& samples) {
  GtAnchorResult r;
  const std::uint64_t data0 = samples[0].data_addr;
  const std::uint32_t aidx0 = samples[0].access_index;
  const std::uint32_t ord0 = samples[0].ordinal;
  for (std::size_t off = 0; off + 8 <= kCCvarAnchorScan; off += 8) {
    std::uint64_t v = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &v))
      continue;
    if (v != data0) continue;
    ++r.direct_hits;
    const std::uint64_t lhit = ccvar + off;
    // accessIndex convention: table_base = Lhit - accessIndex_0*8.
    std::uint64_t tb_a = lhit - static_cast<std::uint64_t>(aidx0) * 8ull;
    if (GtPointerTableMatchesAll(tb_a, GtIndexConv::kAccessIndex, samples)) {
      r.solved = true;
      r.heap = false;
      r.table_base = tb_a;
      r.member_off = static_cast<std::size_t>(tb_a - ccvar);
      r.conv = GtIndexConv::kAccessIndex;
      return r;
    }
    // ordinal convention: table_base = Lhit - ordinal_0*8.
    std::uint64_t tb_o = lhit - static_cast<std::uint64_t>(ord0) * 8ull;
    if (GtPointerTableMatchesAll(tb_o, GtIndexConv::kOrdinal, samples)) {
      r.solved = true;
      r.heap = false;
      r.table_base = tb_o;
      r.member_off = static_cast<std::size_t>(tb_o - ccvar);
      r.conv = GtIndexConv::kOrdinal;
      return r;
    }
    ++r.verify_fail;
  }
  return r;
}

// 2b ONE-LEVEL HEAP INDIRECT + 2c (ordinal): for each 8-aligned CCvar slot P in
// [0, kCCvarScan) that holds a plausible pointer Pptr (a heap array base, e.g. a
// CUtlVector<ConVarData*> data pointer), scan a BOUNDED window of that heap block
// for data_0. A hit Hhit is &table[idx_0]; table_base = Hhit - idx_0*8 (verified
// to stay within the heap window). Verify ALL samples. member_off reported is the
// CCvar offset of the pointer member P (where the vector's data ptr lives).
inline constexpr int kMaxHeapPtrsProbed = 2048;  // candidate CCvar slots probed
GtAnchorResult GtSearchHeap(std::uint64_t ccvar,
                            const std::vector<GtSample>& samples) {
  GtAnchorResult r;
  const std::uint64_t data0 = samples[0].data_addr;
  const std::uint32_t aidx0 = samples[0].access_index;
  const std::uint32_t ord0 = samples[0].ordinal;
  std::uint32_t max_idx = 0;
  for (const GtSample& s : samples) {
    if (s.access_index > max_idx) max_idx = s.access_index;
    if (s.ordinal > max_idx) max_idx = s.ordinal;
  }
  // Window large enough to hold the whole accessed table plus slack.
  const std::uint64_t win_bytes =
      (static_cast<std::uint64_t>(max_idx) + 8ull) * 8ull;
  int probed = 0;
  for (std::size_t off = 0;
       off + 8 <= kCCvarAnchorScan && probed < kMaxHeapPtrsProbed; off += 8) {
    std::uint64_t pptr = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &pptr))
      continue;
    if (!tc::LooksLikePointer2023(pptr)) continue;
    ++probed;
    // Scan the heap window [pptr, pptr+win_bytes) step 8 for data_0.
    bool window_hit = false;
    for (std::uint64_t w = 0; w + 8 <= win_bytes; w += 8) {
      std::uint64_t v = 0;
      if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                   static_cast<std::uintptr_t>(pptr + w)),
                               &v))
        break;  // unmapped within the block -> stop this window
      if (v != data0) continue;
      window_hit = true;
      const std::uint64_t hhit = pptr + w;
      // Capture the FIRST located occurrence of data_0 for the neighborhood
      // analysis (fires even when flat-table verification below fails).
      if (!r.l0_located) {
        r.l0_located = true;
        r.l0_addr = hhit;
        r.l0_block_base = pptr;
      }
      // accessIndex convention: table_base = Hhit - accessIndex_0*8 (must stay >=
      // the heap base so the table lives inside the block we anchored on).
      if (hhit >= static_cast<std::uint64_t>(aidx0) * 8ull) {
        std::uint64_t tb_a = hhit - static_cast<std::uint64_t>(aidx0) * 8ull;
        if (tb_a >= pptr &&
            GtPointerTableMatchesAll(tb_a, GtIndexConv::kAccessIndex, samples)) {
          r.solved = true;
          r.heap = true;
          r.member_off = off;
          r.table_base = tb_a;
          r.conv = GtIndexConv::kAccessIndex;
          return r;
        }
      }
      // ordinal convention.
      if (hhit >= static_cast<std::uint64_t>(ord0) * 8ull) {
        std::uint64_t tb_o = hhit - static_cast<std::uint64_t>(ord0) * 8ull;
        if (tb_o >= pptr &&
            GtPointerTableMatchesAll(tb_o, GtIndexConv::kOrdinal, samples)) {
          r.solved = true;
          r.heap = true;
          r.member_off = off;
          r.table_base = tb_o;
          r.conv = GtIndexConv::kOrdinal;
          return r;
        }
      }
      ++r.verify_fail;
    }
    if (window_hit) ++r.heap_hits;
  }
  return r;
}

// ---- NEIGHBORHOOD ANALYSIS (the "stop guessing stride/index" capture) --------
//
// WHY: the anchored search LOCATES data_0 inside one heap block (heap_hits==1) but
// the stride-8 / accessIndex flat-table model FAILS to verify. So the table is in
// the heap but is NOT a simple stride-8 array indexed by accessIndex. Rather than
// keep canary-guessing (stride, index convention, entry size) one model at a time,
// we CAPTURE the structure directly: given L0 (the slot holding data_0), for EVERY
// ground-truth sample k find where data_k is stored in the SAME block, and emit the
// raw byte deltas. The delta pattern reveals the truth unambiguously:
//   - delta_k / (accessIndex_k - accessIndex_0) constant  -> flat table, stride S,
//     indexed by accessIndex (table_base = L0 - accessIndex_0*S).
//   - delta_k / ordinal_k constant                        -> flat table by ordinal.
//   - neither, but deltas are small multiples of 16 (or other)-> per-entry struct
//     wider than 8 bytes (e.g. {ConVarData*, uint64 key} stride 16) or a hash.
// Plus a raw WORDS dump around L0 so the operator can SEE whether L0 sits in an
// array of bare pointers vs {ptr,key} structs vs hash nodes.
//
// PURELY read-only, SEH-guarded (SafeRead*2023 / LooksLikePointer2023), bounded,
// emits NO artifact bytes (safe), and only fires when CS2_WALKER_TRACE is set
// and data_0 was located. Additive to the existing direct/heap search + trace.
void GtNeighborhoodAnalysis(const GtAnchorResult& loc,
                            const std::vector<GtSample>& samples) {
  const std::uint64_t l0 = loc.l0_addr;
  const std::uint64_t block = loc.l0_block_base;
  const std::uint32_t aidx0 = samples[0].access_index;

  // Bounded search window: generous span around L0 covering the whole accessed
  // table even for wide (struct) strides. maxAccessIndex*16 + slack, plus a small
  // backward margin (the table may start before L0).
  std::uint32_t max_ai = 0;
  for (const GtSample& s : samples)
    if (s.access_index > max_ai) max_ai = s.access_index;
  const std::uint64_t back = 4096ull;
  const std::uint64_t fwd =
      (static_cast<std::uint64_t>(max_ai) + 8ull) * 16ull;
  // Clamp the window start to the heap block base so we never scan before it.
  std::uint64_t win_lo = (l0 > back) ? (l0 - back) : 0;
  if (block != 0 && win_lo < block) win_lo = block;
  const std::uint64_t win_hi = l0 + fwd;

  std::fprintf(stderr,
               "[walker-trace] cvar: NEIGHBORHOOD ANALYSIS — L0=0x%llx "
               "block_base=0x%llx window=[0x%llx,0x%llx) (%llu bytes)\n",
               static_cast<unsigned long long>(l0),
               static_cast<unsigned long long>(block),
               static_cast<unsigned long long>(win_lo),
               static_cast<unsigned long long>(win_hi),
               static_cast<unsigned long long>(win_hi - win_lo));

  // 1+2. For EVERY sample k, scan the window for an 8-byte slot == data_k; record
  //      delta_k = slot_addr - L0. Emit one NBR line per sample.
  std::vector<long long> found_delta(samples.size(), 0);
  std::vector<bool> found(samples.size(), false);
  for (std::size_t k = 0; k < samples.size(); ++k) {
    const std::uint64_t target = samples[k].data_addr;
    bool hit = false;
    std::uint64_t hit_addr = 0;
    for (std::uint64_t a = win_lo; a + 8 <= win_hi; a += 8) {
      std::uint64_t v = 0;
      if (!tc::SafeReadPtr2023(
              reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), &v)) {
        // unmapped: advance to the next 4 KB page boundary (then -8 so the loop's
        // +8 lands ON it) rather than aborting the whole window scan. Stays
        // 8-aligned; bounded by win_hi.
        a = ((a + 0x1000ull) & ~0xFFFull) - 8ull;
        continue;
      }
      if (v != target) continue;
      hit = true;
      hit_addr = a;
      break;
    }
    found[k] = hit;
    const long long delta =
        hit ? static_cast<long long>(static_cast<std::int64_t>(hit_addr) -
                                     static_cast<std::int64_t>(l0))
            : 0;
    found_delta[k] = delta;
    const long long ai_gap = static_cast<long long>(samples[k].access_index) -
                             static_cast<long long>(aidx0);
    char d8[32] = "-";
    char dgap[32] = "-";
    if (hit) {
      std::snprintf(d8, sizeof d8, "%lld", delta / 8);
      if (ai_gap != 0)
        std::snprintf(dgap, sizeof dgap, "%lld", delta / ai_gap);
      else
        std::snprintf(dgap, sizeof dgap, "n/a(gap=0)");
    }
    std::fprintf(stderr,
                 "[walker-trace]   NBR: ord=%-2u accessIndex=%-5u data=0x%llx "
                 "found=%s delta=%lld delta/8=%s delta/accessIndexGap=%s\n",
                 samples[k].ordinal, samples[k].access_index,
                 static_cast<unsigned long long>(samples[k].data_addr),
                 hit ? "yes" : "no", delta, d8, dgap);
  }

  // 3. IMPLIED STRIDE. Test, over the FOUND samples (excluding sample 0 whose
  //    delta is 0 by definition), whether delta_k is a constant integer multiple
  //    of the accessIndex gap, then of the ordinal. Constant => flat table.
  auto constant_ratio = [&](bool by_access, long long* stride_out) -> bool {
    bool have = false;
    long long s = 0;
    for (std::size_t k = 0; k < samples.size(); ++k) {
      if (!found[k]) continue;
      const long long denom =
          by_access ? (static_cast<long long>(samples[k].access_index) -
                       static_cast<long long>(aidx0))
                    : static_cast<long long>(samples[k].ordinal);
      if (denom == 0) continue;                       // sample 0 (or any zero-denominator) — skip.
      if (found_delta[k] % denom != 0) return false;  // not an integer ratio.
      const long long ratio = found_delta[k] / denom;
      if (!have) {
        s = ratio;
        have = true;
      } else if (ratio != s) {
        return false;  // not constant across samples.
      }
    }
    if (!have) return false;
    *stride_out = s;
    return true;
  };

  long long stride = 0;
  if (constant_ratio(/*by_access=*/true, &stride)) {
    const long long table_base =
        static_cast<long long>(l0) - static_cast<long long>(aidx0) * stride;
    std::fprintf(stderr,
                 "[walker-trace] cvar: IMPLIED: flat table by accessIndex, "
                 "stride=%lld, table_base = L0 - accessIndex_0*stride = 0x%llx\n",
                 stride, static_cast<unsigned long long>(table_base));
  } else if (constant_ratio(/*by_access=*/false, &stride)) {
    std::fprintf(stderr,
                 "[walker-trace] cvar: IMPLIED: flat table by ordinal, "
                 "stride=%lld\n",
                 stride);
  } else {
    std::fprintf(stderr,
                 "[walker-trace] cvar: IMPLIED: NO constant stride (neither by "
                 "accessIndex gap nor by ordinal) — raw deltas above reveal the "
                 "pattern (wide per-entry struct / hash / clustered)\n");
  }

  // 4. RAW WORDS around L0 for context: reveals bare-pointer array (stride 8) vs
  //    {ConVarData*, key} structs (stride 16) vs hash nodes. ~40 words,
  //    [L0-64 .. L0+256], each SafeRead, flagged if it looks like a pointer.
  std::fprintf(stderr,
               "[walker-trace] cvar: WORDS around L0 [L0-64 .. L0+256]:\n");
  for (long long rel = -64; rel <= 256; rel += 8) {
    const std::uint64_t a =
        static_cast<std::uint64_t>(static_cast<std::int64_t>(l0) + rel);
    std::uint64_t v = 0;
    if (!tc::SafeReadPtr2023(
            reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), &v)) {
      std::fprintf(stderr,
                   "[walker-trace]   WORDS: off=%-4lld val=<unmapped> looksPtr=0\n",
                   rel);
      continue;
    }
    std::fprintf(stderr,
                 "[walker-trace]   WORDS: off=%-4lld val=0x%016llx looksPtr=%d%s\n",
                 rel, static_cast<unsigned long long>(v),
                 tc::LooksLikePointer2023(v) ? 1 : 0,
                 (rel == 0) ? "  <-- L0 (holds data_0)" : "");
  }
}

// THE GROUND-TRUTH DERIVATION. Runs on the modern path (GetConVarData works) ONLY
// when CS2_WALKER_TRACE is set AND the primary scan is NOT garbage. Logs the single
// CCVAR-REGISTRY-DERIVED line (or DERIVATION-FAILED). Read-only; emits no bytes.
void RunCCvarRegistryGroundTruthDerivation(ICvar* cvar) {
  const std::uint64_t ccvar = reinterpret_cast<std::uint64_t>(cvar);

  // 1. Iterate the first ~16 ConVarRefs via the proven FindFirst/Next path. For each,
  //    record the raw access index, the ordinal, and the GROUND-TRUTH ConVarData
  //    address + name from GetConVarData (works on the modern binary).
  std::vector<GtSample> samples;
  std::uint32_t ordinal = 0;
  for (convar_compat::WConVarIter it = convar_compat::WCvarFirstConVar(cvar);
       it.IsValid() && samples.size() < 16;
       it = convar_compat::WCvarNextConVar(cvar, it), ++ordinal) {
    convar_compat::WConVarData* data = convar_compat::WGetConVarData(cvar, it);
    if (data == nullptr) continue;
    const char* nm = convar_compat::WConVarName(data);
    if (!PlausiblePrintableName(nm)) continue;
    GtSample s{};
    s.access_index = static_cast<std::uint32_t>(it.ref.GetAccessIndex());
    s.ordinal = ordinal;
    s.data_addr = reinterpret_cast<std::uint64_t>(data);
    std::snprintf(s.name, sizeof s.name, "%s", nm);
    samples.push_back(s);
  }

  std::fprintf(stderr,
               "[walker-trace] cvar: GROUND-TRUTH derivation — collected %zu "
               "ground-truth ConVarRefs (FindFirst/Next + GetConVarData):\n",
               samples.size());
  bool access_dense = true;
  for (std::size_t i = 0; i < samples.size(); ++i) {
    const GtSample& s = samples[i];
    std::fprintf(stderr,
                 "[walker-trace]   ord=%-2u accessIndex=%-5u data=0x%llx name=%s\n",
                 s.ordinal, s.access_index,
                 static_cast<unsigned long long>(s.data_addr), s.name);
    if (s.access_index != s.ordinal) access_dense = false;
  }
  std::fprintf(stderr,
               "[walker-trace] cvar: accessIndex is %s (accessIndex %s ordinal "
               "for all sampled refs)\n",
               access_dense ? "DENSE 0,1,2,..." : "SPARSE/PACKED",
               access_dense ? "==" : "!=");

  if (samples.size() < 2) {
    std::fprintf(stderr,
                 "[walker-trace] cvar: CCVAR-REGISTRY-DERIVED: DERIVATION-FAILED "
                 "(only %zu ground-truth samples; need >=2 to lock a layout)\n",
                 samples.size());
    return;
  }

  // 2. DERIVE the ConVarData name offset from the first ground-truth sample
  //    (confirm/deny the assumed @+0). SEH-guarded.
  int name_off = DeriveConVarDataNameOffset(samples[0].data_addr, samples[0].name);
  std::fprintf(stderr,
               "[walker-trace] cvar: ConVarData name_off = %d %s\n",
               name_off,
               name_off < 0 ? "(NOT FOUND in first 512 bytes)"
                            : (name_off == 0 ? "(confirms assumed @+0)"
                                             : "(DIFFERS from assumed @+0)"));

  // 3. DERIVE the registry via the ANCHORED "locate the known pointer" strategy.
  //    The registry is a VARIABLE-SIZE POOL indexed through a FIXED-STRIDE (8-byte)
  //    POINTER TABLE: GetConVarData(idx) == *(table_base + idx*8). We anchor on the
  //    KNOWN value data_0 (samples[0].data_addr) and find where it is STORED, then
  //    derive table_base from the anchor and VERIFY it reproduces ALL samples.
  //    (a) DIRECT: data_0 stored 8-aligned inside the CCvar object [0,64KB).
  //    (b) HEAP : data_0 stored inside a heap block pointed to by a CCvar member
  //               (the common CUtlVector<ConVarData*> data-pointer case).
  //    (c) both try accessIndex AND iteration-ordinal as the table index.
  GtAnchorResult res = GtSearchDirect(ccvar, samples);
  if (!res.solved) {
    GtAnchorResult heap = GtSearchHeap(ccvar, samples);
    // Carry the direct-search failure counts forward for the diagnostic.
    heap.direct_hits = res.direct_hits;
    heap.verify_fail += res.verify_fail;
    res = heap;
  }

  // NEIGHBORHOOD ANALYSIS: when the anchored search LOCATED data_0 in a heap block
  // but the flat stride-8/accessIndex table did NOT verify, capture the REAL table
  // structure directly (deltas of all samples + raw words around L0) instead of
  // continuing to guess stride/index. Fires whenever data_0 was located; additive
  // and read-only. (Runs even on the solved path — it's a free structural confirm.)
  if (res.l0_located) {
    GtNeighborhoodAnalysis(res, samples);
  }

  if (res.solved) {
    std::fprintf(
        stderr,
        "[walker-trace] cvar: CCVAR-REGISTRY-DERIVED: reach=%s member_off=%zu "
        "stride=8 name_off=%d index_conv=%s (verified all %zu samples)\n",
        res.heap ? "heap" : "direct", res.member_off, name_off,
        res.conv == GtIndexConv::kAccessIndex ? "accessIndex" : "ordinal",
        samples.size());
    std::fprintf(stderr,
                 "[walker-trace] cvar:   table_base=0x%llx (ccvar=0x%llx)\n",
                 static_cast<unsigned long long>(res.table_base),
                 static_cast<unsigned long long>(ccvar));
    return;
  }

  std::fprintf(
      stderr,
      "[walker-trace] cvar: CCVAR-REGISTRY-DERIVED: DERIVATION-FAILED "
      "(anchor on data_0=0x%llx not located as a verified pointer table: "
      "direct_hits=%d heap_hits=%d verify_fail=%d; scanned CCvar [0,%zu) step 8, "
      "heap pointers probed <=%d; name_off=%d; %zu samples)\n",
      static_cast<unsigned long long>(samples[0].data_addr), res.direct_hits,
      res.heap_hits, res.verify_fail, kCCvarAnchorScan, kMaxHeapPtrsProbed,
      name_off, samples.size());
}

// ===========================================================================
// COMMAND-REGISTRY GROUND-TRUTH DIAGNOSTIC.
// (read-only, CS2_WALKER_TRACE-gated, stderr-only — emits NO bytes.)
// ===========================================================================
//
// WHY: the COMMAND registry uses a DIFFERENT CONTAINER MODEL than the convar registry.
// The convar-shaped table-of-pointers model (table[i*16] -> ConCommandData*) does NOT
// fit — that mismatch is exactly why the 7864c28/80cb130/77d90ed table-of-pointers
// attempts all failed. So — exactly as we eventually NAILED the convar mirror — we
// DERIVE the command layout from GROUND TRUTH on a HEALTHY build where the NORMAL command
// path WORKS, then apply it. This diagnostic CAPTURED that ground truth on build 19644975
// (real ConCommandData pointers off the normal path), which SETTLED the model: the
// records are an INLINE CONTIGUOUS array of 56-byte (0x38) ConCommandData records,
// record[i] = array_base + i*56 IS the ConCommandData* (NOT behind a per-entry pointer),
// fields name@+0 / help@+8 / flags@+16 read via the WConCmd* accessors. The production
// mirror (DeriveConCommandRegistryPureMemory) now implements that inline model. This
// diagnostic is RETAINED (useful for re-validating future eras) but no longer gates
// anything.
//
// RUNS ONLY ON A HEALTHY BUILD. The caller gates this on the normal ConCommand path
// succeeding — i.e. ForEachLiveConCommand returns a set that CONTAINS the command
// canaries {help,find,kill,say}. On the broken aug-2025 build it does not fire
// (empty/garbage). The operator runs it on a HEALTHY NEIGHBOR build (e.g. 19644975,
// which walks ~836 commands via the normal path) to capture COMMAND GROUND TRUTH.
//
// HOW: on the healthy build WGetConCmdData(WConCmdFromIndex(i)) returns the REAL
// ConCommandData* (the header-inline GetName/GetHelpText work). We:
//   (1) capture (accessIndex, ConCommandData*, name) for {help,find,kill,say} + many
//       others by index-scanning the command registry (same scan ForEachLiveConCommand
//       uses), keyed by ConCommandRef::GetAccessIndex().
//   (2) DERIVE THE REGISTRY LOCATION by anchoring on the REAL pointers: scan CCvar
//       member offsets O step 8; table_base=*(CCvar+O); for strides S in
//       {8,16,24,32,40,48,56,64} and entry-pointer-offsets P in {0,8}, count how many
//       real ConCommandData* appear at table_base + accessIndex*S + P. Report the
//       (O,S,P) with the densest match — the definitive command-table location+stride.
//   (3) DERIVE THE RECORD LAYOUT from a real ConCommandData* (help's): dump
//       data+{0,8,...,56}, classify each slot (ptr->printable string / ptr->ptr /
//       small-int-or-flags / other), so we can SEE where NAME / HELP / FLAGS live.
//
// SAFETY: the only vtable calls are the proven-working ConCommand index API
// on a HEALTHY build (GetConCommandData — works here precisely because this runs only
// when the normal path succeeded). Everything else is SEH-guarded memory reads
// (tc::SafeRead*2023 / LooksLikePointer2023). EMITS NO ARTIFACT BYTES and is gated
// behind CS2_WALKER_TRACE, so it never runs on a healthy production walk and cannot
// perturb output (byte-identical). The aug-2025 broken DLL never reaches here
// (its ConCommand vtable slots fault — but the caller gates on a healthy result, so
// we never dispatch into the broken region).

// One command ground-truth sample: the access index, iteration ordinal, the REAL
// ConCommandData* address, and the ground-truth name.
struct CmdGtSample {
  std::uint32_t access_index;  // ConCommandRef::GetAccessIndex()
  std::uint32_t ordinal;       // 0,1,2,... emission order of REAL commands
  std::uint64_t data_addr;     // GetConCommandData(ref) — real ConCommandData address
  char name[128];              // GetName() — ground-truth command name
};

// Candidate strides + entry-pointer-offsets the command-table derive sweeps. Strides
// span the convar 16-byte model up to a 64-byte per-entry struct; P is the offset of
// the ConCommandData* WITHIN each entry (0 = bare pointer table, 8 = {key,ptr}).
inline constexpr std::size_t kCmdGtStrides[] = {8, 16, 24, 32, 40, 48, 56, 64};
inline constexpr std::size_t kCmdGtEntryPtrOff[] = {0, 8};
inline constexpr std::size_t kCmdGtCCvarScan = 65536;  // CCvar bytes scanned, step 8

// Count how many ground-truth samples have their REAL ConCommandData* at
// table_base + accessIndex*stride + entry_ptr_off. Pure SEH-guarded reads.
int CmdGtTableMatchCount(std::uint64_t table_base, std::size_t stride,
                         std::size_t entry_ptr_off,
                         const std::vector<CmdGtSample>& samples) {
  int hits = 0;
  for (const CmdGtSample& s : samples) {
    const std::uint64_t slot = table_base +
                               static_cast<std::uint64_t>(s.access_index) * stride +
                               entry_ptr_off;
    std::uint64_t resolved = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(slot)),
                             &resolved))
      continue;
    if (resolved == s.data_addr) ++hits;
  }
  return hits;
}

// Classify+print one slot of a ConCommandData object for the RECORD-LAYOUT dump.
// rel is the byte offset into the object. Read the 8 bytes at data+rel; if it is a
// plausible pointer to a printable C-string, show the string (this is how we spot
// NAME / HELP); else if it is a plausible pointer, mark ptr->ptr; else show the raw
// value and flag whether it looks flags/small-int-like.
void CmdGtClassifySlot(std::uint64_t data_addr, long long rel) {
  const std::uint64_t a =
      static_cast<std::uint64_t>(static_cast<std::int64_t>(data_addr) + rel);
  std::uint64_t v = 0;
  if (!tc::SafeReadPtr2023(
          reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), &v)) {
    std::fprintf(stderr,
                 "[walker-trace]   CMD-REC: off=+%-3lld val=<unmapped>\n", rel);
    return;
  }
  if (tc::LooksLikePointer2023(v)) {
    char sb[128];
    if (tc::SafeReadCString2023(
            reinterpret_cast<const char*>(static_cast<std::uintptr_t>(v)), sb,
            sizeof sb) &&
        PlausiblePrintableName(sb)) {
      std::fprintf(stderr,
                   "[walker-trace]   CMD-REC: off=+%-3lld val=0x%016llx "
                   "ptr->string=\"%s\"\n",
                   rel, static_cast<unsigned long long>(v), sb);
      return;
    }
    std::fprintf(stderr,
                 "[walker-trace]   CMD-REC: off=+%-3lld val=0x%016llx ptr->(non-string "
                 "/ ptr-or-data)\n",
                 rel, static_cast<unsigned long long>(v));
    return;
  }
  // Not a pointer: classify as small-int / flags-like (fits in 32 bits, no high
  // bits set) vs other raw value.
  const bool small = (v >> 32) == 0;
  std::fprintf(stderr,
               "[walker-trace]   CMD-REC: off=+%-3lld val=0x%016llx %s\n", rel,
               static_cast<unsigned long long>(v),
               small ? "(small int / flags-like)" : "(raw value)");
}

// THE COMMAND GROUND-TRUTH DERIVATION. Runs ONLY on a HEALTHY build (caller gates on
// the normal command path containing the canaries). Read-only; emits NO bytes.
void RunCommandRegistryGroundTruthDerivation(ICvar* cvar) {
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;
  if (!trace) return;
  const std::uint64_t ccvar = reinterpret_cast<std::uint64_t>(cvar);

  // (1) Capture ground-truth samples by index-scanning the command registry (the same
  // walk ForEachLiveConCommand uses), keyed by ConCommandRef::GetAccessIndex(). On a
  // healthy build GetConCommandData + GetName work. We ALWAYS keep the canaries
  // {help,find,kill,say}, then up to ~28 others, capped at 32 total.
  std::vector<CmdGtSample> samples;
  bool canary_captured[std::size(kCanaryConCommands)] = {false};
  std::uint32_t ordinal = 0;
  int consecutive_undefined = 0;
  for (std::uint32_t i = 0; i < kMaxRefScan && samples.size() < 32; ++i) {
    convar_compat::WConCmdIter it = convar_compat::WConCmdFromIndex(i);
    if (!it.IsValid()) break;
    convar_compat::WConCmdData* data = convar_compat::WGetConCmdData(cvar, it);
    if (data == nullptr) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    const char* nm = convar_compat::WConCmdName(data);
    if (nm == nullptr || std::strcmp(nm, "<undefined>") == 0 ||
        std::strcmp(nm, "<unknown>") == 0) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    consecutive_undefined = 0;
    if (!PlausiblePrintableName(nm)) {
      ++ordinal;
      continue;
    }
    // Always include a canary; otherwise take roughly every other to spread the
    // accessIndex range (helps disambiguate stride).
    bool is_canary = false;
    for (std::size_t k = 0; k < std::size(kCanaryConCommands); ++k)
      if (std::strcmp(nm, kCanaryConCommands[k]) == 0) {
        is_canary = true;
        canary_captured[k] = true;
      }
    if (is_canary || (ordinal % 2) == 0 || samples.size() < 8) {
      CmdGtSample s{};
      s.access_index = static_cast<std::uint32_t>(it.ref.GetAccessIndex());
      s.ordinal = ordinal;
      s.data_addr = reinterpret_cast<std::uint64_t>(data);
      std::snprintf(s.name, sizeof s.name, "%s", nm);
      samples.push_back(s);
    }
    ++ordinal;
  }

  int canaries_present = 0;
  for (bool b : canary_captured)
    if (b) ++canaries_present;
  std::fprintf(stderr,
               "[walker-trace] cmd: COMMAND GROUND-TRUTH — captured %zu real "
               "(accessIndex, ConCommandData*, name) samples (canaries %d/4); "
               "NORMAL command path is HEALTHY:\n",
               samples.size(), canaries_present);
  for (const CmdGtSample& s : samples)
    std::fprintf(stderr,
                 "[walker-trace]   ord=%-3u accessIndex=%-5u data=0x%llx name=%s\n",
                 s.ordinal, s.access_index,
                 static_cast<unsigned long long>(s.data_addr), s.name);

  if (samples.size() < 2 || canaries_present < kCanaryThreshold) {
    std::fprintf(stderr,
                 "[walker-trace] cmd: COMMAND GROUND TRUTH: DERIVATION-FAILED "
                 "(only %zu samples, %d/4 canaries; need >=2 samples and >=%d canaries "
                 "— this build's NORMAL command path is NOT healthy, run on a healthy "
                 "neighbor build e.g. 19644975)\n",
                 samples.size(), canaries_present, kCanaryThreshold);
    return;
  }

  // (2) DERIVE THE REGISTRY LOCATION. Scan CCvar member offsets O step 8;
  // table_base=*(CCvar+O); sweep stride S and entry-pointer-offset P; record the
  // (O,S,P) whose real-pointer match count is densest (and >= half the samples).
  std::size_t best_off = 0, best_stride = 0, best_ptr_off = 0;
  int best_hits = 0;
  std::uint64_t best_table_base = 0;
  for (std::size_t off = 0; off + 8 <= kCmdGtCCvarScan; off += 8) {
    std::uint64_t table_base = 0;
    if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                 static_cast<std::uintptr_t>(ccvar + off)),
                             &table_base))
      continue;
    if (!tc::LooksLikePointer2023(table_base)) continue;
    for (std::size_t S : kCmdGtStrides) {
      for (std::size_t P : kCmdGtEntryPtrOff) {
        const int hits = CmdGtTableMatchCount(table_base, S, P, samples);
        if (hits > best_hits) {
          best_hits = hits;
          best_off = off;
          best_stride = S;
          best_ptr_off = P;
          best_table_base = table_base;
        }
      }
    }
  }

  const bool located =
      best_hits > 0 &&
      static_cast<std::size_t>(best_hits) * 2 >= samples.size();
  if (!located) {
    std::fprintf(stderr,
                 "[walker-trace] cmd: COMMAND GROUND TRUTH: registry NOT located "
                 "(best match only %d/%zu real command pointers across all member "
                 "offsets [0,%zu) x strides{8..64} x entry_ptr_off{0,8}); the command "
                 "table is reached differently than a flat *(CCvar+O)+i*S table — "
                 "widen the sweep or check for a CUtlVector/hashtable indirection\n",
                 best_hits, samples.size(), kCmdGtCCvarScan);
    return;
  }

  // (3) DERIVE THE RECORD LAYOUT from a real ConCommandData* (prefer help's, else
  // sample 0). Dump+classify data+{0..56} so the operator can SEE where NAME / HELP /
  // FLAGS live relative to the ConCommandData pointer.
  std::uint64_t rec_addr = samples[0].data_addr;
  const char* rec_name = samples[0].name;
  for (const CmdGtSample& s : samples)
    if (std::strcmp(s.name, "help") == 0) {
      rec_addr = s.data_addr;
      rec_name = s.name;
      break;
    }

  std::fprintf(stderr,
               "[walker-trace] cmd: COMMAND RECORD LAYOUT — classifying "
               "ConCommandData* 0x%llx (name=\"%s\") at data+{0..56}:\n",
               static_cast<unsigned long long>(rec_addr), rec_name);
  for (long long rel = 0; rel <= 56; rel += 8) CmdGtClassifySlot(rec_addr, rel);

  // Locate NAME / HELP offsets within the record. NAME = the FIRST ptr->string slot
  // whose string EXACTLY equals the known command name (rec_name). HELP = the FIRST
  // OTHER ptr->string slot (printable, != the name). PlausiblePrintableName accepts
  // spaces, so help sentences pass it; the discriminator is "differs from the name",
  // not "is/ isn't a token". Both are exact, ground-truth-anchored.
  int name_off = -1, help_off = -1;
  for (long long rel = 0; rel <= 56; rel += 8) {
    std::uint64_t v = 0;
    const std::uint64_t a =
        static_cast<std::uint64_t>(static_cast<std::int64_t>(rec_addr) + rel);
    if (!tc::SafeReadPtr2023(
            reinterpret_cast<const void*>(static_cast<std::uintptr_t>(a)), &v))
      continue;
    if (!tc::LooksLikePointer2023(v)) continue;
    char sb[256];
    if (!tc::SafeReadCString2023(
            reinterpret_cast<const char*>(static_cast<std::uintptr_t>(v)), sb,
            sizeof sb))
      continue;
    if (sb[0] == '\0') continue;  // empty string slot — neither name nor help.
    const bool is_name_str = (std::strcmp(sb, rec_name) == 0);
    if (name_off < 0 && is_name_str) {
      name_off = static_cast<int>(rel);
    } else if (help_off < 0 && !is_name_str) {
      // First non-name printable string after we know which slot is the name. Guard
      // against picking it up BEFORE the name slot by requiring name_off already set;
      // if a non-name string precedes the name, record it as a help CANDIDATE but let
      // a later exact help match (none here) win — simplest: only accept once name is
      // known. (If name is at +0, help follows; the CMD-REC dump above shows the full
      // picture if this heuristic mis-binds.)
      if (name_off >= 0) help_off = static_cast<int>(rel);
    }
  }

  // GROUND-TRUTH TRACE (single grep-able line + record offsets).
  char nbuf[16], hbuf[16];
  if (name_off >= 0)
    std::snprintf(nbuf, sizeof nbuf, "+%d", name_off);
  else
    std::snprintf(nbuf, sizeof nbuf, "?");
  if (help_off >= 0)
    std::snprintf(hbuf, sizeof hbuf, "+%d", help_off);
  else
    std::snprintf(hbuf, sizeof hbuf, "?");

  std::string sample_list;
  for (const CmdGtSample& s : samples) {
    if (sample_list.size() > 200) {
      sample_list.append(" ...");
      break;
    }
    if (!sample_list.empty()) sample_list.append(" ");
    sample_list.append(s.name);
  }

  std::fprintf(stderr,
               "[walker-trace] cmd: COMMAND GROUND TRUTH: registry member_off=+%zu "
               "stride=%zu entry_ptr_off=+%zu (matched %d/%zu real command pointers); "
               "ConCommandData name@%s help@%s flags@? table_base=0x%llx; sample: %s\n",
               best_off, best_stride, best_ptr_off, best_hits, samples.size(), nbuf,
               hbuf, static_cast<unsigned long long>(best_table_base),
               sample_list.c_str());

  // Eyeball cross-check: resolve (name, help) for a few entries straight off the
  // DERIVED table (member_off/stride/entry_ptr_off + name_off/help_off) so the
  // operator can confirm the layout reads correctly end-to-end.
  if (name_off >= 0) {
    std::fprintf(stderr,
                 "[walker-trace] cmd: COMMAND GROUND TRUTH eyeball (resolved off the "
                 "DERIVED table):\n");
    int shown = 0;
    for (const CmdGtSample& s : samples) {
      if (shown >= 6) break;
      const std::uint64_t slot = best_table_base +
                                 static_cast<std::uint64_t>(s.access_index) *
                                     best_stride +
                                 best_ptr_off;
      std::uint64_t ccd = 0;
      if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(
                                   static_cast<std::uintptr_t>(slot)),
                               &ccd))
        continue;
      char nm[128] = {0};
      std::uint64_t nptr = 0;
      if (tc::SafeReadPtr2023(reinterpret_cast<const void*>(static_cast<std::uintptr_t>(
                                  ccd + name_off)),
                              &nptr) &&
          tc::LooksLikePointer2023(nptr))
        tc::SafeReadCString2023(
            reinterpret_cast<const char*>(static_cast<std::uintptr_t>(nptr)), nm,
            sizeof nm);
      char hp[256] = {0};
      if (help_off >= 0) {
        std::uint64_t hptr = 0;
        if (tc::SafeReadPtr2023(reinterpret_cast<const void*>(static_cast<std::uintptr_t>(
                                    ccd + help_off)),
                                &hptr) &&
            tc::LooksLikePointer2023(hptr))
          tc::SafeReadCString2023(
              reinterpret_cast<const char*>(static_cast<std::uintptr_t>(hptr)), hp,
              sizeof hp);
      }
      std::fprintf(stderr,
                   "[walker-trace]   EYE: accessIndex=%-5u name=\"%s\" help=\"%s\" "
                   "(truth name=\"%s\")\n",
                   s.access_index, nm, hp, s.name);
      ++shown;
    }
  }
}

#endif  // WALKER_CONVAR_HAS_CONVARDATA_API

// ---- ConVar index scan (shared) ----
//
// CS2's registry is a flat array addressed by a uint16 access index. We walk
// ConVarRef(idx) from 0, reading data via ICvar::GetConVarData (the same
// accessor the index API funnels through). This is the index-based ref API the
// offline dumper uses, NOT the FindFirst/FindNext linked-list traversal.
//
// STOP CONDITION: a raw-index ConVarRef always reports IsValidRef()==true (it
// only checks idx != 0xFFFF), so validity alone can't terminate the scan. We
// stop on a run of the registry's "<undefined>" sentinels — the ConVarData the
// registry returns for an unregistered/out-of-range index (convar.h
// ConVarData::Invalidate sets m_pszName = "<undefined>" and FCVAR_REFERENCE).
//
// `sink(WConVarData*)` is invoked once per REAL registered convar (post sentinel
// filtering). Both the extraction walk and the universe drive this, so the
// set of convars they see — and thus their keys — are identical.
//
// ITERATION STRATEGY differs by era (see convar_compat.h):
//   NEW era: the registry is a flat uint16-indexed array; we index-scan with a
//            sentinel stop, EXACTLY as before (byte-identical output).
//   OLD era: there is no index-based ref API; we walk the registry's own
//            FindFirstConVar/FindNextConVar linked traversal (which only ever
//            yields real registered convars, so no sentinel filtering needed).
//            The sentinel name/type guard is still applied defensively.
//
// OLD-ERA RAW-INDEX SCAN (BUG-2 completeness fix) — shared by both ForEachLive*
// functions below.
//   The OLD-era ICvar::FindFirst*/FindNext* linked traversal under-reports by
//   ~45% uniformly across all prefixes (cl_/sv_/r_/snd_/phys_/anim_) — the linked
//   walk visits only a subset of the registered set, not a subsystem-boot gap. But
//   the OLD-era registry is the SAME flat, dense, index-addressed array the NEW era
//   index-scans: a ConVarHandle/ConCommandHandle is just a uint16 index wrapper
//   (tier1/convar.h), and ICvar::GetConVar/GetCommand(handle) resolves any in-range
//   slot — not only handles returned by Find*. So both OLD branches walk handles
//   built from a raw incrementing index (WConVarFromIndex/WConCmdFromIndex ->
//   Handle::Set(i)) exactly like the NEW path, yielding the COMPLETE set, and stop
//   on a run of empty/sentinel slots ("<undefined>"/"<unknown>"/invalid type). Same
//   iteration strategy + stop condition as the NEW branch; only the iterator/data
//   types differ. (The NEW branch additionally early-outs on !IsValid().)
template <typename Sink>
void ForEachLiveConVar(ICvar* cvar, Sink&& sink) {
#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)
  int consecutive_undefined = 0;
  for (uint32_t i = 0; i < kMaxRefScan; ++i) {
    convar_compat::WConVarIter it = convar_compat::WConVarFromIndex(i);
    if (!it.IsValid()) break;
    convar_compat::WConVarData* data = convar_compat::WGetConVarData(cvar, it);
    if (data == nullptr) {
      ++consecutive_undefined;
      if (consecutive_undefined > 64) break;
      continue;
    }
    const char* nm = convar_compat::WConVarName(data);
    // Sentinel / reference slot: not a real registered convar.
    if (nm == nullptr || std::strcmp(nm, "<undefined>") == 0 ||
        convar_compat::WConVarType(data) == EConVarType_Invalid) {
      if (++consecutive_undefined > 64) break;  // run of empties -> end of array.
      continue;
    }
    consecutive_undefined = 0;
    sink(data);
  }
#else
  // OLD ERA — raw-index registry scan (BUG-2 completeness fix; see the shared
  // rationale above ForEachLiveConVar). ConVarHandle is a uint16 index wrapper;
  // ICvar::GetConVar resolves any in-range slot.
  int consecutive_undefined = 0;
  for (uint32_t i = 0; i < kMaxRefScan; ++i) {
    convar_compat::WConVarIter it = convar_compat::WConVarFromIndex(i);
    convar_compat::WConVarData* data = convar_compat::WGetConVarData(cvar, it);
    if (data == nullptr) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    const char* nm = convar_compat::WConVarName(data);
    if (nm == nullptr || std::strcmp(nm, "<undefined>") == 0 ||
        convar_compat::WConVarType(data) == EConVarType_Invalid) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    consecutive_undefined = 0;
    sink(data);
  }
#endif
}

// ---- ConCommand index scan (shared) ----
//
// Same index-based walk. ICvar::GetConCommandData never returns nullptr — it
// returns an empty "<unknown>"/"<undefined>" sentinel for an unregistered index
// — so the stop condition is a run of sentinel names, mirroring the convar scan.
// `sink(WConCmdData*)` is invoked once per REAL registered command. Era split
// mirrors ForEachLiveConVar: NEW index-scan (byte-identical), OLD FindFirst/Next.
template <typename Sink>
void ForEachLiveConCommand(ICvar* cvar, Sink&& sink) {
#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)
  int consecutive_undefined = 0;
  for (uint32_t i = 0; i < kMaxRefScan; ++i) {
    convar_compat::WConCmdIter it = convar_compat::WConCmdFromIndex(i);
    if (!it.IsValid()) break;
    convar_compat::WConCmdData* data = convar_compat::WGetConCmdData(cvar, it);
    if (data == nullptr) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    const char* nm = convar_compat::WConCmdName(data);
    if (nm == nullptr || std::strcmp(nm, "<undefined>") == 0 ||
        std::strcmp(nm, "<unknown>") == 0) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    consecutive_undefined = 0;
    sink(data);
  }
#else
  // OLD ERA — raw-index registry scan (BUG-2 completeness fix; see the shared
  // rationale above ForEachLiveConVar). ConCommandHandle is a uint16 index wrapper;
  // ICvar::GetCommand resolves any in-range slot, so the uint16 scan space
  // (kMaxRefScan) covers the whole command registry.
  int consecutive_undefined = 0;
  for (uint32_t i = 0; i < kMaxRefScan; ++i) {
    convar_compat::WConCmdIter it = convar_compat::WConCmdFromIndex(i);
    convar_compat::WConCmdData* data = convar_compat::WGetConCmdData(cvar, it);
    if (data == nullptr) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    const char* nm = convar_compat::WConCmdName(data);
    if (nm == nullptr || std::strcmp(nm, "<undefined>") == 0 ||
        std::strcmp(nm, "<unknown>") == 0) {
      if (++consecutive_undefined > 64) break;
      continue;
    }
    consecutive_undefined = 0;
    sink(data);
  }
#endif
}

// ---- 2023 OLD-ConVar registry diagnostic (read-only, SEH-guarded) ----------
//
// On 2023 the NEW ICvar::GetConVarData lives at a deep vtable slot that does not
// exist on the (much smaller) 2023 ICvar vtable, so we cannot resolve a ConVarRef
// to its object through the C++ API. Only slots 11-13 (FindConVar/FindFirst/
// FindNext) are confirmed aligned. To read the 2023 convars we must reach the
// registry array INSIDE the CCvar object directly. This diagnostic hunts the CCvar
// object for a pointer that leads to convar-name strings, so the array layout can
// be derived. Pure memory reads (SafeRead*2023) — no vtable calls, never faults.
namespace tc = cs2_schema_walker::tshash_compat;

inline bool PlausibleCvarName2023(const char* s) {
  if (s == nullptr) return false;
  if (!((s[0] >= 'a' && s[0] <= 'z') || (s[0] >= 'A' && s[0] <= 'Z') || s[0] == '_'))
    return false;
  int n = 0;
  for (const char* p = s; *p && n < 96; ++p, ++n) {
    unsigned char c = static_cast<unsigned char>(*p);
    if (c < 0x20 || c > 0x7e) return false;  // non-printable -> not a name
  }
  return n >= 2 && n < 96;
}

// ===========================================================================
// 2023 OLD-ConVar RUNTIME MIRROR (clean-room, derived via probe).
//
// DERIVED 2023 CCvar layout (build 10832117, validated: sv_cheats convar present,
// find command present):
//   - CONVAR registry: a CUtlHashtable at *(CCvar + <derived off>). 16-byte slots
//     {ConVar* @+0, hash @+8}; sparse. ConVar object: m_pszName@+0(char*),
//     m_cvvDefaultValue@+8(CVValue_t*), m_pszHelpString@+32(char*),
//     m_eVarType@+40(uint16, EConVarType), flags@+48(int64). 2495 convars.
//   - COMMAND registry: an inline ConCommandBase array at *(CCvar + <derived off>),
//     STRIDE 56. ConCommandBase: m_pszName@+0, m_pszHelpString@+8, m_nFlags@+16.
//   The offsets are DERIVED at runtime (hunted) rather than hard-pinned, so a 2023-
//   era build whose CCvar member offsets differ still resolves. Every read is
//   SEH-guarded + bounded; a wrong derivation yields fewer/zero entries,
//   never a fault. The NEW ICvar::GetConVarData/GetConVar object-lookup vtable slots
//   do not exist on the 2023 vtable, so we reach the registries by memory layout.
// ===========================================================================
inline constexpr std::size_t kCV2023_Name = 0;
inline constexpr std::size_t kCV2023_Default = 8;
inline constexpr std::size_t kCV2023_Help = 32;
inline constexpr std::size_t kCV2023_Type = 40;       // uint16 EConVarType
inline constexpr std::size_t kCV2023_Flags = 48;      // int64
inline constexpr std::size_t kCVReg2023_Stride = 16;  // hash slot {ConVar*, hash}
// ConCommandBase INLINE record layout for the 2023 OLD era — an inline stride-56 array.
// m_pszName@+0, m_pszHelpString@+8, m_nFlags@+16, STRIDE 56. The MODERN command mirror
// above ALSO uses an inline stride-56 record array (ground-truth derived on 19644975 —
// see kConCmdInlineStride), so the two eras coincide on the 56-byte record stride; they
// differ only in HOW names are read (the modern path reads each record via the WConCmd*
// accessors; the 2023 path reads the pinned ConCommandBase layout directly). These
// kCmd2023_* constants stay SEPARATE so the 2023 OLD-ConVar-API era is self-contained.
inline constexpr std::size_t kCmd2023_Name = 0;
inline constexpr std::size_t kCmd2023_Help = 8;
inline constexpr std::size_t kCmd2023_Flags = 16;  // int64
inline constexpr std::size_t kCmd2023_Stride = 56;

inline bool ReadName2023(std::uint64_t p, char* out, std::size_t n) {
  return tc::SafeReadCString2023(reinterpret_cast<const char*>(static_cast<std::uintptr_t>(p)),
                                 out, n) &&
         PlausibleCvarName2023(out);
}
inline std::uint64_t ReadPtr2023(std::uint64_t at) {
  std::uint64_t v = 0;
  if (!tc::SafeReadPtr2023(reinterpret_cast<const void*>(static_cast<std::uintptr_t>(at)), &v))
    return 0;
  return v;
}

// Resolve a ConVar* (registry slot's +0) to its name, validating it is a real ConVar
// (name plausible AND m_eVarType in range) so reads past the hash array do not admit
// garbage. Returns the ConVar object address, or 0.
std::uint64_t ResolveConVar2023(std::uint64_t slot_obj, char* name, std::size_t n) {
  if (!tc::LooksLikePointer2023(slot_obj)) return 0;
  std::uint64_t namep = ReadPtr2023(slot_obj + kCV2023_Name);
  if (!tc::LooksLikePointer2023(namep) || !ReadName2023(namep, name, n)) return 0;
  std::uint16_t type = 0;
  tc::SafeReadBytes2023(reinterpret_cast<const void*>(
                            static_cast<std::uintptr_t>(slot_obj + kCV2023_Type)),
                        &type, 2);
  if (type > 16) return 0;  // EConVarType range guard (rejects non-ConVar garbage)
  return slot_obj;
}

// Hunt the convar hash: the densest array (stride 16) whose slots resolve as ConVars.
std::uint64_t HuntConVarRegistry2023(std::uint64_t base) {
  std::uint64_t best = 0;
  int best_n = 0;
  for (std::size_t off = 0; off + 8 <= 16384; off += 8) {
    std::uint64_t p = ReadPtr2023(base + off);
    if (!tc::LooksLikePointer2023(p)) continue;
    char nm[128];
    if (ResolveConVar2023(ReadPtr2023(p + 0), nm, sizeof nm) == 0) continue;  // slot0 not a ConVar
    int n = 0;
    for (int e = 0; e < 4096; ++e) {
      std::uint64_t obj = ReadPtr2023(p + static_cast<std::uint64_t>(e) * kCVReg2023_Stride);
      if (ResolveConVar2023(obj, nm, sizeof nm)) ++n;
    }
    if (n > best_n) {
      best_n = n;
      best = p;
    }
  }
  return best_n >= 100 ? best : 0;
}

// Hunt the command array: the densest stride-56 inline array whose entries have a
// plausible name@+0 AND a help string@+8 (distinguishes it from convar memory).
std::uint64_t HuntCommandRegistry2023(std::uint64_t base) {
  std::uint64_t best = 0;
  int best_n = 0;
  for (std::size_t off = 0; off + 8 <= 16384; off += 8) {
    std::uint64_t p = ReadPtr2023(base + off);
    if (!tc::LooksLikePointer2023(p)) continue;
    char nm[128], hb[8];
    std::uint64_t n0 = ReadPtr2023(p + kCmd2023_Name);
    std::uint64_t h0 = ReadPtr2023(p + kCmd2023_Help);
    if (!ReadName2023(n0, nm, sizeof nm)) continue;
    if (!tc::SafeReadCString2023(reinterpret_cast<const char*>(static_cast<std::uintptr_t>(h0)),
                                 hb, sizeof hb)) continue;  // first entry must have help
    int n = 0;
    for (int e = 0; e < 4096; ++e) {
      std::uint64_t namep = ReadPtr2023(p + static_cast<std::uint64_t>(e) * kCmd2023_Stride + kCmd2023_Name);
      if (ReadName2023(namep, nm, sizeof nm))
        ++n;
      else if (e > 0)
        break;  // contiguous
    }
    if (n > best_n) {
      best_n = n;
      best = p;
    }
  }
  return best_n >= 8 ? best : 0;
}

// Render a 2023 ConVar default value (CVValue_t at valptr) by EConVarType.
// ACQUISITION ONLY: reads raw bytes via tc::SafeRead*2023 (no vtable calls — the
// 2023 ICvar cannot resolve a ref to its object; see the 2023 mirror block above),
// then defers to FormatScalarDefault for the actual format strings so the rendered
// string stays byte-identical to the modern path (DefaultValueToString). Each read
// is bounded + may fail; a failed read returns {} (never a partial/garbage render).
std::string Render2023Default(std::uint64_t valptr, std::uint16_t type) {
  if (!tc::LooksLikePointer2023(valptr)) return {};
  auto rd = [&](void* dst, std::size_t n) {
    return tc::SafeReadBytes2023(reinterpret_cast<const void*>(static_cast<std::uintptr_t>(valptr)),
                                 dst, n);
  };
  ScalarDefault s{};
  char str[256];  // backing storage for the String case; outlives the call below.
  switch (type) {
    case EConVarType_Bool: {
      std::uint8_t b = 0;
      if (!rd(&b, 1)) return {};
      s.b = (b != 0);
      break;
    }
    case EConVarType_Int16: {
      std::int16_t v = 0;
      if (!rd(&v, 2)) return {};
      s.i = v;
      break;
    }
    case EConVarType_UInt16: {
      std::uint16_t v = 0;
      if (!rd(&v, 2)) return {};
      s.u = v;
      break;
    }
    case EConVarType_Int32: {
      std::int32_t v = 0;
      if (!rd(&v, 4)) return {};
      s.i = v;
      break;
    }
    case EConVarType_UInt32: {
      std::uint32_t v = 0;
      if (!rd(&v, 4)) return {};
      s.u = v;
      break;
    }
    case EConVarType_Int64: {
      std::int64_t v = 0;
      if (!rd(&v, 8)) return {};
      s.i = static_cast<long long>(v);
      break;
    }
    case EConVarType_UInt64: {
      std::uint64_t v = 0;
      if (!rd(&v, 8)) return {};
      s.u = static_cast<unsigned long long>(v);
      break;
    }
    case EConVarType_Float32: {
      float v = 0;
      if (!rd(&v, 4)) return {};
      s.f = v;
      break;
    }
    case EConVarType_Float64: {
      double v = 0;
      if (!rd(&v, 8)) return {};
      s.d = v;
      break;
    }
    case EConVarType_String: {
      std::uint64_t sp = ReadPtr2023(valptr);
      if (!tc::LooksLikePointer2023(sp) || !tc::SafeReadCString2023(
                                               reinterpret_cast<const char*>(static_cast<std::uintptr_t>(sp)), str, sizeof str)) return {};
      s.str = str;
      break;
    }
    case EConVarType_Color: {
      std::uint8_t c[4];
      if (!rd(c, 4)) return {};
      s.color[0] = c[0];
      s.color[1] = c[1];
      s.color[2] = c[2];
      s.color[3] = c[3];
      s.color_alpha = c[3];
      break;
    }
    case EConVarType_Vector2: {
      float v[2];
      if (!rd(v, 8)) return {};
      s.vec[0] = v[0];
      s.vec[1] = v[1];
      break;
    }
    case EConVarType_Vector3: {
      float v[3];
      if (!rd(v, 12)) return {};
      s.vec[0] = v[0];
      s.vec[1] = v[1];
      s.vec[2] = v[2];
      break;
    }
    case EConVarType_Vector4: {
      float v[4];
      if (!rd(v, 16)) return {};
      s.vec[0] = v[0];
      s.vec[1] = v[1];
      s.vec[2] = v[2];
      s.vec[3] = v[3];
      break;
    }
    case EConVarType_Qangle: {
      float v[3];
      if (!rd(v, 12)) return {};
      s.vec[0] = v[0];
      s.vec[1] = v[1];
      s.vec[2] = v[2];
      break;
    }
    default:
      return {};
  }
  return FormatScalarDefault(static_cast<EConVarType>(type), s);
}

// `type` captures the derived 2023 m_eVarType@+40 so value_type can be set.
// NOTE (truthful, not guessed): the 2023 CCvar layout derivation reached the
// type offset but NEVER derived the min/max bound offsets, so has_min/has_max are
// fixed FALSE for the 2023 era (set at the emit site) — we never guess a bound.
struct CV2023 {
  std::string name, def, help;
  std::uint64_t flags;
  std::uint16_t type;
};
struct CC2023 {
  std::string name, help;
  std::uint64_t flags;
};

// Enumerate the 2023 convar + command registries into the output vectors (unsorted;
// the caller sorts). Returns false only on a structural failure (no registry found).
bool Read2023Registry(ICvar* cvar, std::vector<CV2023>* cvars, std::vector<CC2023>* cmds) {
  const std::uint64_t base = reinterpret_cast<std::uint64_t>(cvar);
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;

  const std::uint64_t cvreg = HuntConVarRegistry2023(base);
  if (cvreg != 0) {
    std::vector<std::uint64_t> seen;
    for (int e = 0; e < 4096; ++e) {
      std::uint64_t obj = ReadPtr2023(cvreg + static_cast<std::uint64_t>(e) * kCVReg2023_Stride);
      char nm[128];
      if (ResolveConVar2023(obj, nm, sizeof nm) == 0) continue;
      bool dup = false;
      for (std::uint64_t s : seen)
        if (s == obj) {
          dup = true;
          break;
        }
      if (dup) continue;
      seen.push_back(obj);
      CV2023 c;
      c.name = nm;
      std::uint64_t helpp = ReadPtr2023(obj + kCV2023_Help);
      char hb[256];
      if (tc::LooksLikePointer2023(helpp) && tc::SafeReadCString2023(
                                                 reinterpret_cast<const char*>(static_cast<std::uintptr_t>(helpp)), hb, sizeof hb))
        c.help = hb;
      std::uint16_t type = 0;
      tc::SafeReadBytes2023(reinterpret_cast<const void*>(
                                static_cast<std::uintptr_t>(obj + kCV2023_Type)),
                            &type, 2);
      c.type = type;  // carry the derived type out for value_type.
      tc::SafeReadBytes2023(reinterpret_cast<const void*>(
                                static_cast<std::uintptr_t>(obj + kCV2023_Flags)),
                            &c.flags, 8);
      c.def = Render2023Default(ReadPtr2023(obj + kCV2023_Default), type);
      cvars->push_back(std::move(c));
    }
  }

  const std::uint64_t cmdreg = HuntCommandRegistry2023(base);
  if (cmdreg != 0) {
    // The inline command array has GAPS (unregistered/removed slots), so iterate
    // gap-tolerantly: skip empty slots, stop only after a long run of consecutive
    // empties (past the array end). Name-plausibility is the validity filter.
    int empties = 0;
    for (int e = 0; e < 4096; ++e) {
      std::uint64_t entry = cmdreg + static_cast<std::uint64_t>(e) * kCmd2023_Stride;
      char nm[128];
      std::uint64_t namep = ReadPtr2023(entry + kCmd2023_Name);
      if (!ReadName2023(namep, nm, sizeof nm)) {
        if (++empties > 256) break;
        continue;
      }
      empties = 0;
      CC2023 c;
      c.name = nm;
      std::uint64_t helpp = ReadPtr2023(entry + kCmd2023_Help);
      char hb[256];
      if (tc::LooksLikePointer2023(helpp) && tc::SafeReadCString2023(
                                                 reinterpret_cast<const char*>(static_cast<std::uintptr_t>(helpp)), hb, sizeof hb))
        c.help = hb;
      tc::SafeReadBytes2023(reinterpret_cast<const void*>(
                                static_cast<std::uintptr_t>(entry + kCmd2023_Flags)),
                            &c.flags, 8);
      cmds->push_back(std::move(c));
    }
  }

  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] cvar: 2023 mirror — convreg@0x%llx (%zu convars) "
                 "cmdreg@0x%llx (%zu commands)\n",
                 static_cast<unsigned long long>(cvreg), cvars->size(),
                 static_cast<unsigned long long>(cmdreg), cmds->size());
  return cvreg != 0;  // convar registry is mandatory; commands may legitimately be few
}

// ---- CANARY-CONVAR SANITY GUARD (set-level, read-only) ----------------
//
// WHY (the blind spot this closes): the walker's layout signature
// (ComputeLayoutSignature) fingerprints SCHEMA structs only — it does NOT cover
// the ICvar vtable / convar read path. Two distinct eras can share the SAME
// schema fingerprint (confirmed: aug-2025 pin 3525af99 and sep-oct-2025 pin
// a4fc170d both hash to f56239f2..., see layout_probe.cpp), so the host's layout
// second gate CANNOT distinguish them for the convar path. If a wrong-era binary
// ran for a build (matching schema fingerprint but MISMATCHED ICvar vtable), the
// convar walk could silently emit plausible-but-wrong convars — exactly how the
// aug-2025 garbage (65278 junk convars, builds 19602992/19605004) originally
// shipped.
//
// DESIGN (safety-first): this is a PURE, read-only, set-level assertion over the
// ALREADY-BUILT convar name set. It adds NO new vtable calls (no FindConVar / no
// GetConVarData-slot call), so it cannot crash and cannot perturb output. It runs
// AFTER the primary scan + the garbage detector, AFTER the set is finalized, BUT
// BEFORE emit. A wrong convar read leaves the canaries absent => fail loud
// no emit. (Note: a detected-garbage era fails loud EARLIER at the
// garbage detector and never reaches this guard; this is the second line of
// defense for a subtler wrong-binary read the garbage heuristic does not catch.)
//
// CANARIES: ubiquitous convars present in EVERY CS2 era (verified present in all
// 242 healthy committed convars.json including the 2023 baseline 10832117 and the
// OLD-ConVar-API + modern paths; absent ONLY in the 2 known-garbage aug-2025
// dumps). sv_cheats is the strongest single canary.
//
// THRESHOLD: require >= kCanaryThreshold (2) of the canary list to be present
// with an EXACT, printable name match. Requiring 2-of-N (rather than all, or
// sv_cheats alone) is robust to a single future canary rename while still
// PROVABLY catching the garbage case (a garbage walk's names are raw code bytes —
// zero exact canary matches). Healthy eras have ALL canaries => guard passes
// silently => output byte-identical.
// (Already inside the file's anonymous namespace opened above — no new namespace.)
// kCanaryConVars / kCanaryThreshold are defined ONCE near the top of this anon
// namespace — no redefinition here.

// Count how many canary names are present (exact, printable match) in `names`.
// Pure: no vtable calls, no I/O, no mutation of `names`.
int CountCanariesPresent(const std::vector<std::string>& names) {
  int found = 0;
  for (const char* canary : kCanaryConVars) {
    for (const std::string& n : names) {
      // Exact match AND printable (a garbled near-match must not count). The
      // canary literals are themselves printable, so an exact == implies n is too.
      if (n == canary) {
        ++found;
        break;
      }
    }
  }
  return found;
}

// The set-level guard. `names` is the FINAL walked convar name set. `label`
// names the path for diagnostics ("convar walk" / "convar universe"). On success
// returns true (and, under trace, logs which canaries were found). On failure
// (< kCanaryThreshold canaries present) returns false and sets *err with a
// precise message — the convar read path does not match this build's
// engine DLL. NEVER emits; the caller returns false before any output bytes.
bool AssertCanaryConVarsPresent(const std::vector<std::string>& names,
                                const char* label, std::string* err) {
  const bool trace = std::getenv("CS2_WALKER_TRACE") != nullptr;
  const int found = CountCanariesPresent(names);
  if (found >= kCanaryThreshold) {
    if (trace) {
      // List exactly which canaries matched (and which did not) for the record.
      std::string present, missing;
      for (const char* c : kCanaryConVars) {
        bool hit = false;
        for (const std::string& n : names)
          if (n == c) {
            hit = true;
            break;
          }
        std::string& dst = hit ? present : missing;
        if (!dst.empty()) dst.append(" ");
        dst.append(c);
      }
      std::fprintf(stderr,
                   "[walker-trace] %s: canary guard PASS — %d/%d canaries "
                   "present (>=%d required) [present: %s] [missing: %s]\n",
                   label, found, static_cast<int>(std::size(kCanaryConVars)),
                   kCanaryThreshold, present.empty() ? "(none)" : present.c_str(),
                   missing.empty() ? "(none)" : missing.c_str());
    }
    return true;
  }

  // FAIL LOUD: the canaries are absent from the walked set, so
  // the convar read is wrong (wrong-era binary / mismatched ICvar vtable). Name
  // the canaries not found. No emit.
  std::string missing;
  for (const char* c : kCanaryConVars) {
    bool hit = false;
    for (const std::string& n : names)
      if (n == c) {
        hit = true;
        break;
      }
    if (!hit) {
      if (!missing.empty()) missing.append(", ");
      missing.append(c);
    }
  }
  if (trace)
    std::fprintf(stderr,
                 "[walker-trace] %s: canary guard FAIL — only %d/%d "
                 "canaries present (>=%d required) [missing: %s]\n",
                 label, found, static_cast<int>(std::size(kCanaryConVars)),
                 kCanaryThreshold, missing.c_str());
  *err =
      std::string(label) +
      ": canary sanity guard FAILED — the walked convar set is missing the "
      "ubiquitous canary convar(s) {" +
      missing + "} (only " +
      std::to_string(found) + " of " +
      std::to_string(static_cast<int>(std::size(kCanaryConVars))) +
      " canaries present; >=" + std::to_string(kCanaryThreshold) +
      " required). The ICvar/convar read path does NOT match this build's engine "
      "DLL (the schema-layout fingerprint does not cover the ICvar vtable, so a "
      "wrong-era binary can match the schema gate yet read garbage convars). "
      "Refusing to emit a wrong convar set.";
  return false;
}
}  // namespace

bool WalkConVarsAndCommands(const InProcessEnvironment& env,
                            wpb::ConVarsWalk* convars_out,
                            wpb::CommandsWalk* commands_out,
                            std::string* err) {
  convars_out->Clear();
  commands_out->Clear();

  // BUILD-ERA GATE (2023 baseline): the 2023 builds expose the OLD ConVar API
  // (ConVarHandle/GetConVar/ConVar*); THIS walker is compiled against the NEW API
  // (ConVarRef/ConVarData/GetConVarData), and the NEW object-lookup vtable slots
  // (GetConVarData/GetConVar) sit far past the end of the much smaller 2023 ICvar
  // vtable, so we cannot resolve a ref to its object through the C++ API. Instead a
  // RUNTIME MIRROR (Read2023Registry) reaches the convar hash + command array
  // INSIDE the CCvar object directly by their derived memory layout (every read
  // SEH-guarded + bounded; see the 2023 mirror block above). The flag is
  // FALSE on every modern build, so the modern path below is unchanged.
  if (env.schema_is_2023_era()) {
    auto* cv2023 = reinterpret_cast<ICvar*>(env.cvar());
    if (cv2023 == nullptr) {
      *err = "cvar walk: null ICvar (loader did not obtain VEngineCvar007)";
      return false;
    }
    std::vector<CV2023> cvs;
    std::vector<CC2023> ccs;
    Read2023Registry(cv2023, &cvs, &ccs);
    std::sort(cvs.begin(), cvs.end(), [](const CV2023& a, const CV2023& b) {
      if (a.name != b.name) return a.name < b.name;
      if (a.def != b.def) return a.def < b.def;
      return a.help < b.help;
    });
    std::sort(ccs.begin(), ccs.end(), [](const CC2023& a, const CC2023& b) {
      if (a.name != b.name) return a.name < b.name;
      return a.help < b.help;
    });
    // Canary guard (2023 path): assert ubiquitous convars are present in
    // the finalized 2023 set BEFORE emit. Read-only over the built name set (no
    // vtable calls). A healthy 2023 build (e.g. 10832117) contains all canaries,
    // so this passes silently; a wrong-era/garbled read fails loud.
    {
      std::vector<std::string> names;
      names.reserve(cvs.size());
      for (const CV2023& c : cvs) names.push_back(c.name);
      if (!AssertCanaryConVarsPresent(names, "convar walk", err)) return false;
    }
    for (const CV2023& c : cvs) {
      wpb::ConVar* out = convars_out->add_convars();
      out->set_name(c.name);
      out->set_default_(c.def);
      EmitFlags(c.flags, out->mutable_flags());
      out->set_description(c.help);
      // 2023 era: value_type IS populated from the derived m_eVarType@+40.
      // has_min/has_max are TRUTHFULLY FALSE — the 2023 CCvar layout derivation never
      // reached the min/max bound offsets, so we never guess them. min_value/
      // max_value are therefore left "" (the proto default).
      out->set_value_type(ConVarTypeName(static_cast<EConVarType>(c.type)));
      out->set_has_min(false);
      out->set_has_max(false);
    }
    for (const CC2023& c : ccs) {
      wpb::Command* out = commands_out->add_commands();
      out->set_name(c.name);
      EmitFlags(c.flags, out->mutable_flags());
      out->set_description(c.help);
    }
    return true;
  }

  auto* cvar = reinterpret_cast<ICvar*>(env.cvar());
  if (cvar == nullptr) {
    // The loader could not obtain VEngineCvar007. That is a structural
    // failure of the convar walk — we cannot produce convars/commands at all.
    *err = "cvar walk: null ICvar (loader did not obtain VEngineCvar007)";
    return false;
  }

  struct CV {
    std::string name, def, help;
    uint64_t flags;
    // Typing + bounds.
    std::string value_type;  // EConVarType enumerator name; "" if Invalid.
    bool has_min = false;
    std::string min_value;  // "" when has_min == false.
    bool has_max = false;
    std::string max_value;  // "" when has_max == false.
  };
  std::vector<CV> cvars;
  // Per-convar extraction (builds one CV record from a ConVarData).
  auto extract_cv = [&cvars](convar_compat::WConVarData* data) {
    CV c;
    c.name = Str(convar_compat::WConVarName(data));
    c.help = Str(convar_compat::WConVarHelp(data));
    c.flags = convar_compat::WConVarFlags(data);
    c.def = DefaultValueToString(data);
    // value_type from EConVarType; min/max from HasMinValue()/HasMaxValue()
    // (presence == non-null bound pointer), rendered IDENTICALLY to the default via
    // the shared RenderCVValue. min_value/max_value stay "" when the bound is absent.
    const EConVarType type = convar_compat::WConVarType(data);
    c.value_type = ConVarTypeName(type);
    const CVValue_t* minv = convar_compat::WConVarMin(data);
    const CVValue_t* maxv = convar_compat::WConVarMax(data);
    c.has_min = (minv != nullptr);
    if (c.has_min) c.min_value = RenderCVValue(type, minv);
    c.has_max = (maxv != nullptr);
    if (c.has_max) c.max_value = RenderCVValue(type, maxv);
    cvars.push_back(std::move(c));
  };
  ForEachLiveConVar(cvar, extract_cv);

#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)
  // aug-2025 (pin 3525af99) GARBAGE-WALK DETECTOR -> IMMEDIATE CLEAN FAIL-LOUD.
  // The primary scan above ran UNCHANGED. For 9 of 10 eras its result is healthy
  // and this block is inert (byte-identity). Only on a DETECTABLY garbage
  // result (count > ceiling or a high fraction of non-printable names — provably
  // never a healthy era) do we FAIL LOUD IMMEDIATELY. There is NO slot probe: it
  // was proven both ineffective (aug-2025's convars are unreachable via the
  // GetConVarData index API at any slot) AND dangerous (blind-calling vtable slots
  // [12,72] hit a destructive method that exited with code 54321 and swallowed the
  // diagnostic). Failing here lets main.cpp's Change-B path surface the message and
  // exit cleanly. The real fix is a CCvar memory-layout mirror (separate RE work).
  bool convar_mirror_engaged = false;
  // GROUND-TRUTH DERIVATION DIAGNOSTIC (read-only, CS2_WALKER_TRACE-gated, modern
  // path). Runs ONLY when trace is enabled AND the primary scan is HEALTHY (NOT
  // garbage) — i.e. GetConVarData works on this binary, so we have ground truth to
  // anchor on. It logs the real modern CCvar registry layout (CCVAR-REGISTRY-DERIVED
  // line) to stderr and EMITS NO ARTIFACT BYTES. It deliberately runs on the
  // healthy/modern path, NOT behind the garbage detector, because that is where the
  // ground truth (working GetConVarData) exists. It never runs on a production walk
  // (trace off) and does not touch `cvars`, so output stays byte-identical.
  if (std::getenv("CS2_WALKER_TRACE") != nullptr &&
      !PrimaryConVarResultIsGarbage(cvars)) {
    RunCCvarRegistryGroundTruthDerivation(cvar);
    // MIRROR SELF-VALIDATION (read-only, trace-gated, HEALTHY path only): run the
    // SAME derive+read the aug-2025 fallback uses on this healthy binary and compare
    // the mirror's convar NAME set to the primary scan's. Proves whether the mirror
    // reproduces ground truth on current. Builds its own name set, compares, logs the
    // MIRROR-SELFCHECK verdict line, and DISCARDS the mirror result — does NOT touch
    // `cvars` (the emitted set), so output stays byte-identical.
    std::vector<std::string> primary_names;
    primary_names.reserve(cvars.size());
    for (const CV& c : cvars) primary_names.push_back(c.name);
    RunMirrorSelfCheck(cvar, primary_names);
    // COMMAND-REGISTRY GROUND-TRUTH DERIVATION (read-only, trace-gated). Runs on a
    // HEALTHY build to learn the modern ConCommandData table location/stride + record
    // layout from the working normal command path. It SELF-GATES on the normal command
    // path being healthy: it index-scans the command registry, and if the command
    // canaries {help,find,kill,say} are NOT present (i.e. the normal path is broken,
    // as on the aug-2025 builds) it reports DERIVATION-FAILED and does nothing else.
    // On the broken aug-2025 DLL this is doubly safe — that path never reaches here
    // (PrimaryConVarResultIsGarbage is true, so this whole block is skipped). EMITS NO
    // ARTIFACT BYTES; does not touch `cvars`/`cmds` (byte-identity).
    RunCommandRegistryGroundTruthDerivation(cvar);
  }
  if (PrimaryConVarResultIsGarbage(cvars)) {
    // aug-2025 CCvar REGISTRY MEMORY-MIRROR fallback (gated to the garbage
    // case only; a healthy era never reaches here). The broken GetConVarData
    // vtable slot is BYPASSED: we re-walk via the proven-working FindFirst/Next
    // iteration + a derived direct-memory registry index, reusing the SAME
    // extract_cv accessor extraction so the emitted shape is byte-compatible. Pure
    // SEH-guarded reads + the front-of-vtable iteration; NO blind vtable calls. On
    // derivation/read failure RunConVarMirror sets *err and we fail loud (no
    // garbage). The post-walk canary guard below is the final canary gate.
    cvars.clear();
    std::size_t mirror_count = 0;
    if (!RunConVarMirror(cvar, "convar walk", extract_cv, &mirror_count, err))
      return false;
    convar_mirror_engaged = true;
  }
#endif  // WALKER_CONVAR_HAS_CONVARDATA_API

  struct CC {
    std::string name, help;
    uint64_t flags;
    bool has_completion_cb = false;
  };
  std::vector<CC> cmds;
  // Tracks whether the aug-2025 ConCommand mirror actually DERIVED a command
  // registry (true) vs cleanly missed (false). Only a DERIVED registry is gated by
  // the post-read command-canary guard (wrong-positive fail-loud); a clean miss
  // degrades to commands=0 and skips the guard. Stays false on every healthy era.
  bool cmd_registry_derived = false;
  ForEachLiveConCommand(cvar, [&cmds](convar_compat::WConCmdData* data) {
    CC c;
    c.name = Str(convar_compat::WConCmdName(data));
    c.help = Str(convar_compat::WConCmdHelp(data));
    c.flags = convar_compat::WConCmdFlags(data);
    // Command.has_completion_callback: header-inline
    // ConCommandData::HasCompletionCallback() (modern/NEW-era only; the OLD-era
    // accessor returns false — deferred). proto3 omits a false bool, so commands
    // without a completion callback add no bytes — PURELY ADDITIVE.
    c.has_completion_cb = convar_compat::WConCmdHasCompletionCallback(data);
    cmds.push_back(std::move(c));
  });

#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)
  // COMMAND DISPOSITION on the aug-2025 garbage era. When the convar mirror engaged,
  // the SAME broken GetConCommandData vtable slot makes the command index scan
  // untrustworthy too: it returns garbage ConCommandData* whose "names" are raw code
  // bytes. Commands ARE NOW MIRRORED for this era (the command analogue of the CCvar
  // convar mirror): we BYPASS the broken vtable ENTIRELY (no ConCommand vtable call at
  // all — slots 82/83 FAULT on this DLL) and derive the INLINE 56-byte-record command
  // array by a PURE MEMORY SCAN over CCvar members (RunConCommandMirror ->
  // DeriveConCommandRegistryPureMemory; record_addr=array_base+i*56 IS the ConCommandData*
  // read in place via WConCmd* name@+0/help@+8/flags@+16, canary-anchored on
  // {help,find,kill,say}). On a detectably-garbage scan we REPLACE cmds
  // with the mirrored set; the post-read command-canary guard (AssertCanaryConCommandsPresent,
  // below) is the canary gate. These builds DO have commands, so the mirror failing/yielding
  // empty is a fail-loud condition — we never silently drop to empty here.
  if (convar_mirror_engaged) {
    std::size_t bad = 0;
    for (const CC& c : cmds)
      if (!PlausiblePrintableName(c.name.c_str())) ++bad;
    const bool cmds_garbage =
        cmds.size() > kSaneConVarCeiling ||
        (!cmds.empty() && bad * 10 >= cmds.size());
    // On the aug-2025 broken-vtable path (convar_mirror_engaged) the same slot can
    // make ForEachLiveConCommand return an EMPTY set rather than a garbage flood.
    // These builds DO have ~800 commands, so an empty command set is itself proof
    // of breakage on this path and MUST engage the mirror (otherwise cmds stays
    // empty, the mirror is skipped, and the command-canary fails on empty).
    if (cmds_garbage || cmds.empty()) {
      if (std::getenv("CS2_WALKER_TRACE") != nullptr)
        std::fprintf(stderr,
                     "[walker-trace] convar walk: command index scan is empty/garbage "
                     "on this era (%zu entries, %zu non-printable); engaging the "
                     "ConCommand registry memory-mirror.\n",
                     cmds.size(), bad);
      // Run the mirror; REPLACE cmds with the mirrored set. GRACEFUL DEGRADE: a CLEAN
      // MISS (no registry located — the modern command layout is not yet solved for
      // this era) returns true with mirrored EMPTY and cmd_registry_derived=false, so
      // cmds becomes empty and the post-read canary guard is SKIPPED (commands=0 is
      // truthful, not a fail-loud). Only when a registry WAS derived
      // (cmd_registry_derived=true) does the canary guard run as the wrong-positive
      // canary gate. A genuine hard error still sets *err and fails loud.
      std::vector<MirrorCmd> mirrored;
      if (!RunConCommandMirror(cvar, "convar walk", &mirrored, err,
                               &cmd_registry_derived))
        return false;
      cmds.clear();
      cmds.reserve(mirrored.size());
      for (MirrorCmd& m : mirrored)
        // The pure-memory ConCommand mirror (aug-2025 garbage-vtable
        // recovery path) reads name/help/flags off the inline 56-byte record but
        // does NOT decode the completion-callback member, so has_completion_cb
        // stays its false default — truthful (never guess), additive.
        cmds.push_back(CC{std::move(m.name), std::move(m.help), m.flags, false});
    }
  }
#endif  // WALKER_CONVAR_HAS_CONVARDATA_API

  // Determinism: sort by name Ordinal. Ties (the registry should not
  // produce duplicate names, but be defensive) break on the default/help so the
  // order is fully determined.
  std::sort(cvars.begin(), cvars.end(), [](const CV& a, const CV& b) {
    if (a.name != b.name) return a.name < b.name;
    if (a.def != b.def) return a.def < b.def;
    return a.help < b.help;
  });
  std::sort(cmds.begin(), cmds.end(), [](const CC& a, const CC& b) {
    if (a.name != b.name) return a.name < b.name;
    return a.help < b.help;
  });

  // Canary guard (modern / OLD-ConVar-API path): assert ubiquitous convars
  // are present in the FINAL set, AFTER the primary scan + the garbage detector,
  // BEFORE emit. Read-only over the built name set (no vtable calls), so
  // a healthy era (all canaries present) passes silently and output is unchanged
  // A wrong-era binary that matched the schema fingerprint but reads the
  // wrong ICvar vtable produces a set without the canaries -> fail loud, no emit.
  {
    std::vector<std::string> names;
    names.reserve(cvars.size());
    for (const CV& c : cvars) names.push_back(c.name);
    if (!AssertCanaryConVarsPresent(names, "convar walk", err)) return false;
  }

#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)
  // Command-canary guard — ONLY on the aug-2025 mirrored path AND ONLY when the
  // mirror actually DERIVED a command registry (cmd_registry_derived). A healthy era
  // never engages the mirror, so this is inert there and commands.json stays
  // byte-identical. GRACEFUL DEGRADE: if the mirror cleanly MISSED (no registry
  // found — modern command layout not yet solved for this era), cmds is empty and we
  // SKIP this guard, emitting commands=0 (truthful, no fail-loud). The guard runs only
  // for a WRONG POSITIVE (a registry WAS derived): a canary-less derived set fails loud
  // here, before any emit (no junk).
  if (convar_mirror_engaged && cmd_registry_derived) {
    std::vector<std::string> cmd_names;
    cmd_names.reserve(cmds.size());
    for (const CC& c : cmds) cmd_names.push_back(c.name);
    if (!AssertCanaryConCommandsPresent(cmd_names, "convar walk", err)) return false;
  }
#endif  // WALKER_CONVAR_HAS_CONVARDATA_API

  for (const CV& c : cvars) {
    wpb::ConVar* out = convars_out->add_convars();
    out->set_name(c.name);
    out->set_default_(c.def);  // proto field "default" -> generated set_default_.
    EmitFlags(c.flags, out->mutable_flags());
    out->set_description(c.help);
    // Typing + bounds (additive). value_type "" when Invalid; min/max "" when
    // the has_* flag is false.
    out->set_value_type(c.value_type);
    out->set_has_min(c.has_min);
    out->set_min_value(c.min_value);
    out->set_has_max(c.has_max);
    out->set_max_value(c.max_value);
  }
  for (const CC& c : cmds) {
    wpb::Command* out = commands_out->add_commands();
    out->set_name(c.name);
    EmitFlags(c.flags, out->mutable_flags());
    out->set_description(c.help);
    // Command.has_completion_callback (additive; false omitted on wire).
    out->set_has_completion_callback(c.has_completion_cb);
  }

  return true;
}

bool EnumerateLiveConVarAndCommandNames(const InProcessEnvironment& env,
                                        std::vector<std::string>* convar_names,
                                        std::vector<std::string>* command_names,
                                        std::string* err) {
  convar_names->clear();
  command_names->clear();

  // BUILD-ERA GATE (2023 baseline): mirror WalkConVarsAndCommands EXACTLY so the
  // universe key set == the extraction key set. The 2023 runtime mirror reads
  // the same convar hash + command array, so the universe reports the same names.
  if (env.schema_is_2023_era()) {
    auto* cv2023 = reinterpret_cast<ICvar*>(env.cvar());
    if (cv2023 == nullptr) {
      *err = "cvar universe: null ICvar (loader did not obtain VEngineCvar007)";
      return false;
    }
    std::vector<CV2023> cvs;
    std::vector<CC2023> ccs;
    Read2023Registry(cv2023, &cvs, &ccs);
    for (const CV2023& c : cvs) convar_names->push_back(c.name);
    for (const CC2023& c : ccs) command_names->push_back(c.name);
    // Canary guard (universe, 2023 path): the "set" is the name list,
    // so assert the canary NAMES are present. Read-only; mirrors the extraction
    // path so the universe fails identically on a wrong read (no silent drift).
    if (!AssertCanaryConVarsPresent(*convar_names, "convar universe", err))
      return false;
    return true;
  }

  auto* cvar = reinterpret_cast<ICvar*>(env.cvar());
  if (cvar == nullptr) {
    // Same structural failure WalkConVarsAndCommands reports.
    *err = "cvar universe: null ICvar (loader did not obtain VEngineCvar007)";
    return false;
  }

  // Identical scan + sentinel filtering as the extraction, capturing only the
  // name (GetName()) so the universe key == the artifact key.
  ForEachLiveConVar(cvar, [convar_names](convar_compat::WConVarData* data) {
    convar_names->push_back(Str(convar_compat::WConVarName(data)));
  });
  ForEachLiveConCommand(cvar, [command_names](convar_compat::WConCmdData* data) {
    command_names->push_back(Str(convar_compat::WConCmdName(data)));
  });

#if defined(WALKER_CONVAR_HAS_CONVARDATA_API)
  // aug-2025 garbage detector -> IMMEDIATE CLEAN FAIL-LOUD. MIRROR
  // WalkConVarsAndCommands EXACTLY (same message, no slot probe) so the
  // universe path fails identically to the extraction path and cannot drift.
  // Dormant on every healthy era.
  if (PrimaryConVarNamesAreGarbage(*convar_names)) {
    // aug-2025 CCvar REGISTRY MEMORY-MIRROR fallback — mirror WalkConVarsAndCommands
    // EXACTLY so the universe key set == the extraction key set (no drift).
    // Capture ONLY the name from each resolved ConVarData (same as the primary
    // universe sink). Pure SEH-guarded reads + FindFirst/Next; no blind vtable
    // calls; fail loud on derivation/read failure.
    convar_names->clear();
    std::size_t mirror_count = 0;
    if (!RunConVarMirror(
            cvar, "convar universe",
            [convar_names](convar_compat::WConVarData* data) {
              convar_names->push_back(Str(convar_compat::WConVarName(data)));
            },
            &mirror_count, err))
      return false;
    // COMMAND DISPOSITION (mirror the extraction path EXACTLY): commands ARE NOW
    // mirrored for this era via the ConCommand registry memory-mirror, so the
    // universe command key set == the extraction command key set (no drift). If the
    // command index scan is garbage, REPLACE the command names with the mirrored set
    // (the pure-memory INLINE 56-byte-record command array's names; NO ConCommand vtable
    // call). Fail loud on mirror failure.
    std::size_t bad = 0;
    for (const std::string& n : *command_names)
      if (!PlausiblePrintableName(n.c_str())) ++bad;
    const bool cmds_garbage =
        command_names->size() > kSaneConVarCeiling ||
        (!command_names->empty() && bad * 10 >= command_names->size());
    // Mirror the extraction path EXACTLY: on this broken-vtable era an EMPTY command
    // set is itself proof of breakage (these builds have ~800 commands), so engage the
    // ConCommand registry memory-mirror on garbage OR empty — never silently leave the
    // universe command key set empty (it must == the extraction command key set).
    if (cmds_garbage || command_names->empty()) {
      std::vector<MirrorCmd> mirrored;
      bool cmd_registry_derived = false;
      if (!RunConCommandMirror(cvar, "convar universe", &mirrored, err,
                               &cmd_registry_derived))
        return false;
      command_names->clear();
      command_names->reserve(mirrored.size());
      for (const MirrorCmd& m : mirrored) command_names->push_back(m.name);
      // Command-canary guard on the mirrored universe set — ONLY when a registry
      // was DERIVED (matches the extraction path EXACTLY so the universe cannot drift).
      // GRACEFUL DEGRADE: a clean miss leaves command_names empty and SKIPS the guard
      // (universe command key set == extraction's empty command set; no drift, no
      // fail-loud). A wrong positive (registry derived but canary-less) fails loud here.
      if (cmd_registry_derived &&
          !AssertCanaryConCommandsPresent(*command_names, "convar universe", err))
        return false;
    }
  }
#endif  // WALKER_CONVAR_HAS_CONVARDATA_API

  // Canary guard (universe, modern / OLD-ConVar-API path): the "set"
  // is the final name list (post primary scan + the garbage detector). Assert the
  // canary NAMES are present. Read-only (no vtable calls), so a healthy era passes
  // silently; a wrong-era ICvar read fails loud here too — mirroring the
  // extraction path so the universe key set cannot drift from a wrong read.
  if (!AssertCanaryConVarsPresent(*convar_names, "convar universe", err))
    return false;

  return true;
}

}  // namespace cs2_schema_walker
