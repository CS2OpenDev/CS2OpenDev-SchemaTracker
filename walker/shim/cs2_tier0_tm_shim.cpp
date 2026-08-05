// cs2_tier0_tm_shim — tier0 telemetry compatibility shim (Windows, x64).
//
// Supplies the three RAD Telemetry symbols that the 2023-03-22 limited-test
// pulse_system.dll imports from tier0.dll but that the tier0.dll Valve actually
// shipped does not export. Without them the Windows loader rejects the module
// with ERROR_PROC_NOT_FOUND and ~78 CPulse* schema types are lost on those nine
// builds. See walker/src/pe_import_shim.h for the full story and the fail-loud
// rules that decide when this shim is allowed to be used at all.
//
// SEMANTICS: these are the "telemetry compiled out" definitions.
//   * g_tm_api stays null — every RAD tmZone macro is guarded on this pointer,
//     so a null API is exactly how an uninstrumented build behaves. It is
//     deliberately NOT a zeroed dummy struct: that would read as "telemetry is
//     active" and send the caller through null function pointers.
//   * Register/Unregister are the profiling zone hooks. Doing nothing is the
//     correct uninstrumented behaviour; they have no other side effects.
//
// The exported names are supplied by cs2_tier0_tm_shim.def, which maps the
// C++-mangled member-function names onto these plain C definitions. The mangled
// names decode as `public: void VTm_Zone_Base::Register(void)` / `::Unregister`,
// i.e. x64 member calls taking only `this` in RCX — matching the void* parameter.

#include <windows.h>

extern "C" {

// g_tm_api — data export. Null means "no telemetry API installed".
void* cs2shim_tm_api = nullptr;

// VTm_Zone_Base::Register(void)
void cs2shim_tm_zone_register(void* /*self*/) {}

// VTm_Zone_Base::Unregister(void)
void cs2shim_tm_zone_unregister(void* /*self*/) {}

}  // extern "C"

BOOL APIENTRY DllMain(HMODULE, DWORD, LPVOID) { return TRUE; }
