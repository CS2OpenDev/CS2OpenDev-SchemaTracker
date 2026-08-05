// tier0_link_stubs.cpp — clean-room link stubs for tier0 thread-lock symbols
// that OLDER hl2sdk header inlines reference but that the walker never links
// against (the walker dlopen/LoadLibrary-loads the CS2 DLLs at runtime and does
// NOT link tier0.lib).
//
// WHY THIS EXISTS
// ---------------
// The walker enumerates the live schema class/enum bindings by calling the
// header-inline CUtlTSHash<>::GetElements (see schema_walk.cpp /
// engine_constants_walk.cpp). In some hl2sdk eras GetElements's read path takes
// a per-bucket reader lock:
//
//     bucket.m_AddLock.LockForRead ( __FILE__, __LINE__ );   // CThreadSpinRWLock
//     ...
//     bucket.m_AddLock.UnlockRead  ( __FILE__, __LINE__ );
//
// In those eras (e.g. hl2sdk a4fc170d, 2025-10-15) public/tier0/threadtools.h
// declares CThreadSpinRWLock::LockForRead / UnlockRead as PLATFORM_CLASS, which
// for a consumer that does not build tier0 expands to __declspec(dllimport):
//
//     PLATFORM_CLASS void LockForRead ( const char *pFileName = NULL, int nLine = -1 );
//     PLATFORM_CLASS void UnlockRead  ( const char *pFileName = NULL, int nLine = -1 );
//
// Those are extern declarations with no body. The walker calls them only from
// GetElements's read path, so at link time MSVC reports them as unresolved
// external symbols (LNK2019 -> LNK1120). We do not want to link tier0 to satisfy
// them (clean-room: we only READ the live object graph), and we do not need real
// locking: the walk is a single-threaded one-shot pass over a hash
// that the engine has finished populating. No-op definitions are semantically
// correct here.
//
// CONFLICT SAFETY (no duplicate-symbol risk)
// ------------------------------------------
// The CURRENT pin (hl2sdk b8dcaf14) does NOT declare these as dllimport. There,
// CThreadSpinRWLock is reimplemented on top of std::shared_mutex and every lock
// method is defined INLINE in the header:
//
//     void LockForRead( ... ) { m_mutex.lock_shared(); }     // has a body
//     void UnlockRead( ... )  { m_mutex.unlock_shared(); }   // has a body
//
// Providing an out-of-line definition of those members in that era would be a
// redefinition of an already-defined inline member -> a hard compile error.
//
// Therefore the out-of-line definitions below are emitted ONLY when CMake has
// detected the dllimport era and set WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS=1
// (see walker/CMakeLists.txt; the detection test-compiles a probe against the
// CHECKED-OUT submodule header, so it tracks whatever era is actually pinned).
// When that macro is unset (current pin and any future inline-era pin) this
// translation unit is empty and cannot collide with the header inlines.
//
// As a belt-and-suspenders guard against CMake mis-detection, when the macro IS
// set we also static_assert that the header is in fact the dllimport era, using
// a public, era-distinguishing trait: the dllimport-era CThreadSpinRWLock has a
// user-declared `CThreadSpinRWLock(const char* = NULL)` constructor (so it is
// constructible from const char*), whereas the std::shared_mutex-based inline
// era has only the implicit default constructor (std::shared_mutex is not
// constructible from const char*). If the trait disagrees with the macro we
// fail the build loudly rather than risk a silent double-definition.
//
// These are link-time concerns on BOTH platforms for the dllimport era. Originally
// this TU was guarded with _WIN32 on the assumption that g++ never emits external
// references to these methods — that assumption held only because the sole Linux era
// built at the time (the current inline-lock pin) does not declare them out-of-line.
// The a4fc170d-class dllimport era proves it false: g++ DOES emit undefined references
// to CThreadSpinRWLock::LockForRead/UnlockRead from the GetElements read path. So the
// guard is now purely WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS (set by the cross-platform
// CMake era probe); on the inline-lock era the macro is unset and this TU is empty on
// every platform, so it can never double-define the header inlines.

#if defined(WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS)

#include "tier0/threadtools.h"

#include <type_traits>

// Era guard: the dllimport era's CThreadSpinRWLock is constructible from
// `const char*` (its `CThreadSpinRWLock(const char* = NULL)` ctor). The inline
// std::shared_mutex era is not. If this fails, CMake set the stub macro for a
// header era that defines these methods inline, and emitting the definitions
// below would double-define them — so stop now (fail loud, never guess).
static_assert(
    std::is_constructible<CThreadSpinRWLock, const char*>::value,
    "WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS was set, but the pinned hl2sdk "
    "threadtools.h defines CThreadSpinRWLock lock methods inline (std::shared_mutex "
    "era). The out-of-line stubs below would be duplicate definitions. Re-run CMake "
    "configure so the era probe re-detects; do not force this macro by hand.");

// No-op definitions of the exact dllimport-declared overloads the read path
// (CUtlTSHash<>::GetElements) pulls in. Signatures must match the header
// declarations byte-for-byte so MSVC name-mangling resolves them.
//
// Single-threaded one-shot walk: acquiring/releasing a reader lock over an
// already-fully-populated hash is a no-op for our purposes.
void CThreadSpinRWLock::LockForRead(const char* /*pFileName*/, int /*nLine*/) {
}

void CThreadSpinRWLock::UnlockRead(const char* /*pFileName*/, int /*nLine*/) {
}

#endif  // WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS
