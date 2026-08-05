// pe_import_shim.h — Windows-only recovery path for a present-but-unloadable
// schema module whose ONLY unresolvable imports are a known, semantically inert
// set.
//
// WHY THIS EXISTS
// ---------------
// The nine oldest archived CS2 builds (the 2023-03-22 limited-test era,
// 10832117 .. 11081546) ship a `pulse_system.dll` that was compiled against a
// RAD-Telemetry-instrumented tier0 which Valve never shipped. It imports three
// telemetry symbols the retail tier0.dll does not export:
//
//     g_tm_api                              (data:     telemetry API pointer)
//     ?Register@VTm_Zone_Base@@QEAAXXZ      (function: profiling zone register)
//     ?Unregister@VTm_Zone_Base@@QEAAXXZ    (function: profiling zone unregister)
//
// The Windows loader resolves the whole import table at load time, so LoadLibrary
// fails with ERROR_PROC_NOT_FOUND (127) and the module contributes NO schema —
// costing ~78 CPulse* types (the whole CPulseCell_* hierarchy) on those builds.
// The ELF loader binds lazily, which is why the same builds walk fine on Linux
// (where the module is in fact absent from the depot entirely).
//
// WHAT THIS DOES
// --------------
// Writes a PATCHED COPY of the module to a private temp dir in which those three
// imports are redirected to a tiny first-party shim DLL that supplies a null
// telemetry API pointer and two no-op zone hooks — i.e. exactly the
// "telemetry compiled out" semantics retail tier0 implies. Everything else still
// binds to the real tier0. The copy is then loaded by the NORMAL OS loader, so
// relocations, TLS index allocation, the exception directory and DllMain/static
// initialisers are all handled by Windows exactly as for any other module.
//
// The rewrite splits the dependency's import-thunk range into maximal runs and
// emits one descriptor per run, so the IAT slots stay at their original addresses
// and no code fixups are required. Original IMAGE_IMPORT_BY_NAME entries are
// reused verbatim; only a new descriptor array, the per-run thunk arrays and the
// shim's name string are appended in a new read-only section.
//
// FAIL-LOUD (this is a recovery path, never a tolerance policy)
// ------------------------------------------------------------
//   * Engaged ONLY after a real LoadLibrary failure with ERROR_PROC_NOT_FOUND.
//   * EVERY unresolvable import must be in kShimmableImports. A single symbol
//     outside that fixed set aborts with the exact dll!symbol named — we never
//     stub an unknown import, because that would silently change behaviour.
//   * Any PE-structure surprise (no room for a section header, absent import
//     directory, unexpected magic) aborts. No partial or best-effort patching.
//   * The patched copy keeps the ORIGINAL FILE NAME so LoadedModule::module_name()
//     — which is what InstallSchemaBindings and the type-scope lookup key on —
//     is unchanged.
//
// Artifact fidelity is unaffected: modules.json is emitted by the C# host from an
// independent scan of the real binaries dir, so it always records the original
// file's path, size and sha256. The patched copy exists only inside this process.
#pragma once

#if defined(_WIN32)

#include <filesystem>
#include <string>
#include <vector>

namespace cs2_schema_walker {
namespace pe_import_shim {

// The fixed set of imports this recovery path is allowed to redirect. Keeping
// this an explicit allow-list (rather than "stub whatever is missing") is what
// makes the path safe: each entry is a symbol whose absence is semantically
// equivalent to a disabled profiler.
extern const char* const kShimmableImports[3];

// File name of the first-party shim DLL, expected next to the walker binary.
extern const char* const kShimDllName;

// True if `symbol` is in kShimmableImports.
bool IsShimmable(const std::string& symbol);

// Absolute path to the shim DLL that ships beside this executable.
std::filesystem::path ResolveShimPath();

// Every (dll, symbol) pair `dll_path` imports that the corresponding dependency
// does NOT export. Resolution is done against the modules as the OS sees them
// (GetModuleHandle + GetProcAddress), so it answers exactly the question the
// loader asked. Returns false + sets *err if the file cannot be parsed.
struct MissingImport {
  std::string dll;
  std::string symbol;
};
bool FindUnresolvableImports(const std::filesystem::path& dll_path,
                             std::vector<MissingImport>* out, std::string* err);

// Every named (dll, symbol) pair `dll_path` imports, in import-table order.
// Ordinal-only imports are skipped (they are never shimmable). Returns false +
// sets *err if the file cannot be parsed. Exposed for diagnostics and to let the
// unit test round-trip a rewrite without needing a deliberately-broken fixture.
bool ListImports(const std::filesystem::path& dll_path,
                 std::vector<MissingImport>* out, std::string* err);

// Write a copy of `dll_path` to `out_path` with every import listed in `redirect`
// re-pointed at `shim_dll_name`. Returns false + sets *err on any structural
// surprise. Caller guarantees every entry of `redirect` is shimmable.
bool WriteShimmedCopy(const std::filesystem::path& dll_path,
                      const std::vector<MissingImport>& redirect,
                      const std::string& shim_dll_name,
                      const std::filesystem::path& out_path, std::string* err);

// Directory this process uses for patched copies (created on first use).
//
// The path is per-PID so concurrent walks never collide. A patched copy cannot be
// deleted by the run that made it — Windows keeps the mapped image locked until
// the process exits — so each run instead sweeps away the staging dirs of walker
// processes that have already exited. Dirs belonging to live PIDs are left alone.
std::filesystem::path ShimStagingDir();

}  // namespace pe_import_shim
}  // namespace cs2_schema_walker

#endif  // defined(_WIN32)
