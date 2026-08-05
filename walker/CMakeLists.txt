# C++ schema-walker kernel
#
# Builds a native binary per (OS, arch) host that the C# host invokes as a subprocess.
# Loads CS2 Source 2 DLLs in-process via dlopen/LoadLibrary, walks the live
# CSchemaSystem / ICvar / NetMessages object graphs using HL2SDK struct layouts,
# and emits one walker_output.proto-shaped file per invocation.
#
# Source files cover the schema / convar / netmsg / engine-constant / string-pool
# walks and the layout probe.
# The HL2SDK submodule under third_party/hl2sdk is pinned and required.
# Run `git submodule update --init walker/third_party/hl2sdk` before configuring.

cmake_minimum_required(VERSION 3.20)
project(cs2_schema_walker
        VERSION 0.2.0
        LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

# --- HL2SDK (cs2 branch, pinned submodule) ---------------------------------------------
# Default: the in-tree pinned submodule. Exposed as a CACHE PATH so a per-era build
# can point at an external checkout via -DHL2SDK_ROOT=<dir>.
# The default value is byte-identical to the previous unconditional set(), so the
# default build is unchanged. Everything downstream reads ${HL2SDK_ROOT}.
set(HL2SDK_ROOT "${CMAKE_SOURCE_DIR}/third_party/hl2sdk"
    CACHE PATH "Path to the hl2sdk (cs2 branch) checkout the walker builds against")
if(NOT EXISTS "${HL2SDK_ROOT}/.git" AND NOT EXISTS "${HL2SDK_ROOT}/public")
  message(FATAL_ERROR
    "HL2SDK submodule not initialized at ${HL2SDK_ROOT}.\n"
    "Run: git submodule update --init walker/third_party/hl2sdk")
endif()

# Capture the HL2SDK pinned SHA so the layout-probe stub embeds it in
# its signature. The signature is opaque to the host but is canonical input to
# the schema-system version table.
find_package(Git QUIET)
set(HL2SDK_SUBMODULE_SHA "unknown")
if(Git_FOUND)
  execute_process(
    COMMAND ${GIT_EXECUTABLE} -C "${HL2SDK_ROOT}" rev-parse HEAD
    OUTPUT_VARIABLE HL2SDK_SUBMODULE_SHA
    OUTPUT_STRIP_TRAILING_WHITESPACE
    ERROR_QUIET
    RESULT_VARIABLE _hl2sdk_rev_rc
  )
  if(NOT _hl2sdk_rev_rc EQUAL 0)
    set(HL2SDK_SUBMODULE_SHA "unknown")
  endif()
endif()
message(STATUS "HL2SDK pinned at: ${HL2SDK_SUBMODULE_SHA}")

# Capture the tool repo's git SHA for embedding in --version output.
set(WALKER_GIT_SHA "unknown")
if(Git_FOUND)
  execute_process(
    COMMAND ${GIT_EXECUTABLE} -C "${CMAKE_SOURCE_DIR}/.." rev-parse HEAD
    OUTPUT_VARIABLE WALKER_GIT_SHA
    OUTPUT_STRIP_TRAILING_WHITESPACE
    ERROR_QUIET
    RESULT_VARIABLE _walker_rev_rc
  )
  if(NOT _walker_rev_rc EQUAL 0)
    set(WALKER_GIT_SHA "unknown")
  endif()
endif()
message(STATUS "Walker tool git SHA: ${WALKER_GIT_SHA}")

# Capture the source-tree CONTENT fingerprint for embedding in --version output and
# consumed by the per-era build harness's content-keyed resumability skip (see
# walker/tools/src_fingerprint.py for exactly what is covered and why). Unlike
# WALKER_GIT_SHA above, this is stable across uncommitted local edits going IN
# (it changes the moment a covered byte changes, commit or not) and stable across
# unrelated commits going OUT (a commit that touches nothing covered leaves it
# unchanged) -- the git SHA has neither property, which is what let an uncommitted
# walker fix rebuild zero binaries.
# Tries python3 first (the canonical POSIX/CI name), then python (the Windows `py`
# launcher shim's usual alias); mirrors the WALKER_GIT_SHA fallback above -- any
# failure (no interpreter found, non-zero exit, empty output) degrades to
# "unknown" rather than failing the configure step.
find_program(WALKER_PYTHON_EXE NAMES python3 python)
set(WALKER_SRC_FPRINT "unknown")
if(WALKER_PYTHON_EXE)
  execute_process(
    COMMAND ${WALKER_PYTHON_EXE} "${CMAKE_SOURCE_DIR}/tools/src_fingerprint.py"
    OUTPUT_VARIABLE WALKER_SRC_FPRINT
    OUTPUT_STRIP_TRAILING_WHITESPACE
    ERROR_QUIET
    RESULT_VARIABLE _walker_fprint_rc
  )
  if(NOT _walker_fprint_rc EQUAL 0 OR WALKER_SRC_FPRINT STREQUAL "")
    set(WALKER_SRC_FPRINT "unknown")
  endif()
else()
  message(STATUS "No python3/python interpreter found on PATH; walker source fingerprint unavailable")
endif()
message(STATUS "Walker source fingerprint: ${WALKER_SRC_FPRINT}")

# --- Protobuf -------------------------------------------------------------------------
# schemas/ is the single source of truth for output shapes. The walker
# emits walker_output.proto-shaped bytes; generate the C++ bindings here from
# the schemas/ tree so a schema bump auto-rebuilds the walker.
#
# We support both the legacy `find_package(Protobuf)` (FindProtobuf.cmake,
# ships with CMake) and the modern `find_package(protobuf CONFIG)` (shipped
# by protobuf >= 3.5 when installed via vcpkg / conan / system pkg-config).
# Prefer CONFIG when available so newer protobuf abseil linking is handled
# correctly.
find_package(protobuf CONFIG QUIET)
if(NOT protobuf_FOUND)
  find_package(Protobuf REQUIRED)
endif()

set(WALKER_PROTO_DIR "${CMAKE_SOURCE_DIR}/../schemas")
# walker_output.proto imports entity_schema.proto (which imports
# google/protobuf/struct.proto) plus convars.proto, commands.proto and
# network_messages.proto (the public ConVar / Command /
# NetworkChannel shapes the walk reuses verbatim). Generate bindings for every
# imported .proto so their .pb.cc objects are compiled + linked; protoc resolves
# the well-known struct.proto from its bundled include path.
set(WALKER_PROTO_FILES
  "${WALKER_PROTO_DIR}/walker_output.proto"
  "${WALKER_PROTO_DIR}/entity_schema.proto"
  "${WALKER_PROTO_DIR}/convars.proto"
  "${WALKER_PROTO_DIR}/commands.proto"
  "${WALKER_PROTO_DIR}/network_messages.proto"
  "${WALKER_PROTO_DIR}/engine_constants.proto"
  "${WALKER_PROTO_DIR}/string_pools.proto"
)
set(WALKER_PROTO_GEN_DIR "${CMAKE_BINARY_DIR}/generated")
file(MAKE_DIRECTORY "${WALKER_PROTO_GEN_DIR}")

# Resolve the protoc executable across both find_package flavors.
if(TARGET protobuf::protoc)
  set(_PROTOC_BIN "$<TARGET_FILE:protobuf::protoc>")
elseif(Protobuf_PROTOC_EXECUTABLE)
  set(_PROTOC_BIN "${Protobuf_PROTOC_EXECUTABLE}")
else()
  message(FATAL_ERROR "Could not locate the protoc executable from the protobuf package.")
endif()

set(WALKER_PROTO_SRCS
  "${WALKER_PROTO_GEN_DIR}/walker_output.pb.cc"
  "${WALKER_PROTO_GEN_DIR}/entity_schema.pb.cc"
  "${WALKER_PROTO_GEN_DIR}/convars.pb.cc"
  "${WALKER_PROTO_GEN_DIR}/commands.pb.cc"
  "${WALKER_PROTO_GEN_DIR}/network_messages.pb.cc"
  "${WALKER_PROTO_GEN_DIR}/engine_constants.pb.cc"
  "${WALKER_PROTO_GEN_DIR}/string_pools.pb.cc"
)
set(WALKER_PROTO_HDRS
  "${WALKER_PROTO_GEN_DIR}/walker_output.pb.h"
  "${WALKER_PROTO_GEN_DIR}/entity_schema.pb.h"
  "${WALKER_PROTO_GEN_DIR}/convars.pb.h"
  "${WALKER_PROTO_GEN_DIR}/commands.pb.h"
  "${WALKER_PROTO_GEN_DIR}/network_messages.pb.h"
  "${WALKER_PROTO_GEN_DIR}/engine_constants.pb.h"
  "${WALKER_PROTO_GEN_DIR}/string_pools.pb.h"
)

add_custom_command(
  OUTPUT  ${WALKER_PROTO_SRCS} ${WALKER_PROTO_HDRS}
  COMMAND ${_PROTOC_BIN}
          --proto_path=${WALKER_PROTO_DIR}
          --cpp_out=${WALKER_PROTO_GEN_DIR}
          ${WALKER_PROTO_FILES}
  DEPENDS ${WALKER_PROTO_FILES}
  COMMENT "Generating C++ bindings for walker_output.proto + 6 imported protos"
  VERBATIM
)

# --- tier0 thread-lock link stubs (clean-room) ---------------------------------
# Some hl2sdk eras (e.g. a4fc170d, 2025-10-15) declare
# CThreadSpinRWLock::LockForRead/UnlockRead as PLATFORM_CLASS (== __declspec(dllimport)
# for a consumer that does not build tier0). The walker calls these only via the
# header-inline CUtlTSHash<>::GetElements read path (schema_walk.cpp /
# engine_constants_walk.cpp), but never links tier0.lib (it dlopen/LoadLibrary-loads
# the CS2 DLLs at runtime), so MSVC reports them as unresolved externals. The current
# pin (b8dcaf14) instead defines those methods INLINE on top of std::shared_mutex, so it
# links cleanly and must NOT receive an out-of-line definition (that would double-define).
#
# walker/src/tier0_link_stubs.cpp provides no-op out-of-line definitions, gated on
# WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS. We compute that macro here by test-compiling a
# probe against the ACTUALLY CHECKED-OUT submodule header: the dllimport-era class is
# constructible from `const char*` (its `CThreadSpinRWLock(const char* = NULL)` ctor); the
# std::shared_mutex inline era is not. This tracks whatever era is pinned with no manual
# per-era flag, and the stub TU re-asserts the same trait so a mis-detection fails loud
# rather than double-defining.
# NOTE: the tier0 spin-rwlock dllimport-era probe that computes
# WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA lives just AFTER the shared per-OS probe-context
# block below (WALKER_HL2SDK_PROBE_DEFS/_FLAGS), because it now runs on BOTH platforms and
# reuses that context. It was previously an inline WIN32-only probe here.

# --- HL2SDK header-probe compile context (clean-room) ------------------
# SINGLE SOURCE OF TRUTH for the preprocessor context every configure-time era probe
# below uses. Each cross-platform probe isolate-compiles an hl2sdk header
# (schemasystem/schematypes.h, convar.h) to decide which era shim the walker builds.
# For a probe's verdict to match how the REAL walker target compiles that same header,
# the probe's context must MIRROR the target's — otherwise the header chain fails to
# compile IN ISOLATION on GCC and the probe false-reports the wrong era. That was the
# first-ever Linux build bug: on the current (MODERN) pin b8dcaf14 the leaner probe
# context could not compile schematypes.h on GCC, so WALKER_SCHEMA_TYPES_NEW_NAMES (and
# WALKER_CONVAR_HAS_CONVARDATA_API) false-detected OLD and selected the wrong branch.
#
# WINDOWS (MSVC): byte-identical to the historical inline probe defines — a verbatim
# restatement of the exact list each cross-platform probe already used. The Windows
# walker binary and its committed artifacts are frozen; no compiler flag is
# added on this path (WALKER_HL2SDK_PROBE_FLAGS is empty), so the MSVC try-compile
# invocation and every probe's verdict are provably unchanged.
#
# LINUX (GCC): the historical four defines PLUS three that the MSVC CRT / Valve's VPC
# supply implicitly but the leaner probe context did not, and without which the hl2sdk
# header chain (tier0/platform.h, tier0/logging.h, tier1/utlrbtree.h) will not compile
# AT ALL on GCC:
#   LINUX             bare (Valve VPC defines it beside _LINUX). Selects platform.h's
#                     CReuseVaList va_copy branch; without it GCC hard-errors on the
#                     `#else` invalid array-assignment of va_list.
#   stricmp / strcmpi hl2sdk maps bare stricmp only under TIER0_DLL_EXPORT
#                     (public/tier0/platform.h ~L1816); MSVC otherwise gets it from the
#                     CRT. Map it to POSIX strcasecmp — identical case-insensitive,
#                     sign-returning semantics, and exactly what platform.h already does
#                     for _stricmp (~L723) — so tier0/logging.h + tier1/utlrbtree.h
#                     compile WITHOUT pulling tier0's dllexport machinery. Preferred over
#                     -DTIER0_DLL_EXPORT because it introduces no unresolved
#                     V_tier0_stricmp symbol and no DLL-visibility flip (link-safe for
#                     the real target too).
# -std=c++20 mirrors the target (CMAKE_CXX_STANDARD 20, extensions off) so no probe can
# false-fail on a header needing C++20. These GCC additions do NOT change which era a
# probe detects — an OLD-name symbol (SCHEMA_TYPE_PTR / m_nSize) still fails to compile
# on a NEW header, so discrimination is intact — and they never touch the Windows path.
if(WIN32)
  set(WALKER_HL2SDK_PROBE_DEFS
    -D_WIN32 -DCOMPILER_MSVC -DCOMPILER_MSVC64 -DNOMINMAX -DWIN32_LEAN_AND_MEAN)
  set(WALKER_HL2SDK_PROBE_FLAGS "")
else()
  set(WALKER_HL2SDK_PROBE_DEFS
    -D_LINUX -DPOSIX -DCOMPILER_GCC -DGNUC
    -DLINUX -Dstricmp=strcasecmp -Dstrcmpi=strcasecmp)
  set(WALKER_HL2SDK_PROBE_FLAGS "-std=c++20")
endif()

# NOTE: the tier0 spin-rwlock dllimport-era probe runs just AFTER the ConVar API probe
# below, not here — it needs the OLD-ConVar verdict (WALKER_CONVAR_HAS_CONVARDATA_API) to
# decide whether to add PLATFORM_64BITS to its context (the OLD-ConVar eras' threadtools.h
# int128/__m128i typedef is gated on it; without it the isolated compile fails on GCC and
# the probe false-reports "header-inline" -> the dllimport link stubs are wrongly disabled).

# --- ConVar / ConCommand API era probe (clean-room) ----------------------------
# Valve reworked the CS2 ConVar/ConCommand API across 2024-2025. Two mutually
# incompatible surfaces appear across the pinned-hl2sdk range we backfill against:
#
#   NEW era (current pin b8dcaf14, forward): ICvar::FindConVar -> ConVarRef,
#     ICvar::GetConVarData(ConVarRef) -> ConVarData* (header-inline accessors),
#     ConVarRef(uint16) index ctor + IsValidRef().
#   OLD era (e.g. f3b44f20, 2025-01-01, back through early-2024): FindConVar ->
#     ConVarHandle, GetConVar(ConVarHandle) -> ConVar*; NO ConVarData / ConVarRef
#     index / GetConVarData at all.
#
# walker/src/convar_compat.h adapts both behind one surface, selected by
# WALKER_CONVAR_HAS_CONVARDATA_API. We compute that macro here by test-compiling a
# probe against the ACTUALLY CHECKED-OUT submodule header: the NEW era is the one
# whose ICvar declares GetConVarData(ConVarRef) and exposes ConVarData. If the
# probe compiles, we are on the NEW era and define WALKER_CONVAR_HAS_CONVARDATA_API
# so the compat shim takes its passthrough (byte-identical) path. If it fails, we
# are on the OLD era and the shim reads the pinned struct layout instead.
#
# This probe runs on BOTH platforms (the drift is OS-independent, unlike the
# Windows-only tier0 link-stub probe above).
#
# CACHE NOTE (IMPORTANT FOR PIN SWITCHING): check_cxx_source_compiles caches its
# result in the named variable across reconfigures. When you switch the pinned
# submodule to a different era and reconfigure the SAME build dir, the stale
# result persists and you will build the wrong path. Clear it explicitly between
# era switches:  cmake -U WALKER_CONVAR_HAS_CONVARDATA_API  (alongside
# -U WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA). See README notes.
include(CheckCXXSourceCompiles)
set(CMAKE_REQUIRED_INCLUDES
  "${HL2SDK_ROOT}/public"
  "${HL2SDK_ROOT}/public/tier0"
  "${HL2SDK_ROOT}/public/tier1"
  "${HL2SDK_ROOT}/public/interfaces"
  "${HL2SDK_ROOT}/public/mathlib")
# Mirror the real target's per-OS compile context (see WALKER_HL2SDK_PROBE_DEFS above)
# so this isolated try-compile compiles the hl2sdk header chain exactly as a real TU
# does. WIN32 value is byte-identical to the historical inline defines.
set(CMAKE_REQUIRED_DEFINITIONS ${WALKER_HL2SDK_PROBE_DEFS})
set(CMAKE_REQUIRED_FLAGS "${WALKER_HL2SDK_PROBE_FLAGS}")
# COMPILE-ONLY probe (no link). Including convar.h emits a FILE-SCOPE static
# initializer (GetInvalidConVarData()'s s_InvalidConVar[] -> Vector2D/Vector
# ctors -> the tier0 import LoggingSystem_LogAssert) in EVERY TU regardless of
# what our probe body does — so check_cxx_source_compiles' default EXECUTABLE
# link step fails with LNK2019 on the (otherwise correct) NEW pin and the probe
# false-reports OLD. Building the try-compile as a STATIC_LIBRARY skips the link
# entirely, so the probe reflects COMPILE success only — exactly what we want
# (the OLD era still hard-fails to compile: no ConVarRef/ConVarData types).
# Save/restore the global so later try-compiles are unaffected.
set(_WALKER_SAVED_TRYCOMPILE_TARGET_TYPE "${CMAKE_TRY_COMPILE_TARGET_TYPE}")
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
check_cxx_source_compiles("
  #include \"icvar.h\"
  #include \"convar.h\"
  // NEW era: ICvar::GetConVarData(ConVarRef) returns a ConVarData* whose
  // header-inline GetType()/DefaultValue() we use. ConVarRef (uint16-indexed),
  // ConVarData, and GetConVarData are all absent in the OLD (ConVarHandle) era,
  // so this TU COMPILES ONLY on the NEW era.
  //
  // IMPORTANT (see ROOT CAUSE for this probe's history): every reference below
  // is in an UNEVALUATED operand (sizeof / decltype). Nothing is ODR-used, so
  // NO ctor body and NO out-of-line / dllimport symbol is emitted. That matters
  // because check_cxx_source_compiles also LINKS: actually constructing a
  // ConVarRef or touching convar.h's inline ConVarData machinery instantiates
  // GetInvalidConVarData()'s static s_InvalidConVar[] (Vector2D/Vector ctors ->
  // tier0 import LoggingSystem_LogAssert), which the probe links nothing for ->
  // LNK2019 -> false-OLD on the NEW pin. Unevaluated contexts dodge that link
  // entirely while still hard-failing to COMPILE on the OLD (no such types) era.
  int main() {
    // Discriminator 1+2: the NEW types exist (sizeof requires complete types).
    (void)sizeof(ConVarRef);
    (void)sizeof(ConVarData);
    // Discriminator 3: ConVarRef has the uint16 index ctor (NEW); decltype of a
    // braced ctor call is unevaluated and constructs nothing at runtime.
    using RefT = decltype(ConVarRef(static_cast<unsigned short>(0)));
    (void)sizeof(RefT);
    // Discriminator 4: ICvar::GetConVarData(ConVarRef) -> ConVarData* (NEW).
    using DataPtrT = decltype(static_cast<ICvar*>(nullptr)
                                ->GetConVarData(*static_cast<ConVarRef*>(nullptr)));
    (void)sizeof(DataPtrT);
    // Discriminator 5+6: ConVarData::GetType()/DefaultValue() accessors (NEW).
    using TypeT  = decltype(static_cast<ConVarData*>(nullptr)->GetType());
    using ValueT = decltype(static_cast<ConVarData*>(nullptr)->DefaultValue());
    (void)sizeof(TypeT);
    (void)sizeof(ValueT);
    return 0;
  }
" WALKER_CONVAR_HAS_CONVARDATA_API)
set(CMAKE_TRY_COMPILE_TARGET_TYPE "${_WALKER_SAVED_TRYCOMPILE_TARGET_TYPE}")
unset(_WALKER_SAVED_TRYCOMPILE_TARGET_TYPE)
unset(CMAKE_REQUIRED_INCLUDES)
unset(CMAKE_REQUIRED_DEFINITIONS)
unset(CMAKE_REQUIRED_FLAGS)
if(WALKER_CONVAR_HAS_CONVARDATA_API)
  message(STATUS "hl2sdk era: ConVar API is NEW (ConVarRef/ConVarData) -> convar_compat passthrough")
else()
  message(STATUS "hl2sdk era: ConVar API is OLD (ConVarHandle/ConVar*) -> convar_compat layout-mirror path")
endif()

# --- tier0 spin-rwlock dllimport-era probe (clean-room) — CROSS-PLATFORM --------
# Some hl2sdk eras declare CThreadSpinRWLock::LockForRead/UnlockRead as PLATFORM_CLASS (an
# out-of-line/dllimport symbol) that the header-inline CUtlTSHash<>::GetElements read path
# references; the walker never links tier0, so those become unresolved externals at link
# time. walker/src/tier0_link_stubs.cpp provides no-op out-of-line definitions gated on
# WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS. Historically WIN32-only, on the (false, for the
# dllimport-era on Linux) assumption that g++ never emits those references — it now runs on
# BOTH platforms using the shared per-OS probe context. The discriminator is era-distinguishing
# on both compilers: only the dllimport era's CThreadSpinRWLock has a
# `CThreadSpinRWLock(const char* = NULL)` ctor (constructible from const char*).
#
# Runs AFTER the ConVar API probe (not with the other early probes) because it must add
# PLATFORM_64BITS for the OLD-ConVar eras exactly like the schematypes probe below: this
# probe COMPILES threadtools.h, whose int128/__m128i typedef is gated on PLATFORM_64BITS on
# those eras. Without it the isolated GCC compile fails ("int128 not declared") and the probe
# false-reports "header-inline", wrongly DISABLING the dllimport stubs -> a link error on
# f3b44f20 (undefined reference to CThreadSpinRWLock::LockForRead). PLATFORM_64BITS does not
# affect the constructible-from-const-char* discriminator, so the verdict is unchanged for
# every era that already compiled — it just lets the OLD-era header compile.
include(CheckCXXSourceCompiles)
set(CMAKE_REQUIRED_INCLUDES
  "${HL2SDK_ROOT}/public"
  "${HL2SDK_ROOT}/public/tier0"
  "${HL2SDK_ROOT}/public/tier1")
set(CMAKE_REQUIRED_DEFINITIONS ${WALKER_HL2SDK_PROBE_DEFS})
if(NOT WALKER_CONVAR_HAS_CONVARDATA_API)
  list(APPEND CMAKE_REQUIRED_DEFINITIONS -DPLATFORM_64BITS=1)
endif()
set(CMAKE_REQUIRED_FLAGS "${WALKER_HL2SDK_PROBE_FLAGS}")
check_cxx_source_compiles("
  #include \"tier0/threadtools.h\"
  #include <type_traits>
  static_assert(std::is_constructible<CThreadSpinRWLock, const char*>::value,
                \"dllimport-era CThreadSpinRWLock\");
  int main() { return 0; }
" WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA)
unset(CMAKE_REQUIRED_INCLUDES)
unset(CMAKE_REQUIRED_DEFINITIONS)
unset(CMAKE_REQUIRED_FLAGS)
if(WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA)
  message(STATUS "hl2sdk era: CThreadSpinRWLock locks are dllimport -> enabling tier0 read-lock link stubs")
else()
  message(STATUS "hl2sdk era: CThreadSpinRWLock locks are header-inline -> tier0 read-lock link stubs disabled")
endif()

# --- schemasystem/schematypes.h enum + member naming era probe (clean-room) ---
# Valve renamed several SchemaTypeCategory_t / SchemaAtomicCategory_t enumerators
# and the CSchemaType_Bitfield count member across the pinned-hl2sdk range:
#
#   NEW era (f3b44f20 2025-01, current b8dcaf14, forward): SCHEMA_TYPE_POINTER,
#     SCHEMA_TYPE_INVALID, SCHEMA_ATOMIC_PLAIN, SCHEMA_ATOMIC_INVALID,
#     SCHEMA_BUILTIN_TYPE_COUNT, CSchemaType_Bitfield::m_nBitfieldCount.
#   OLD era (426ae7f3, 2024-04-26 — the era-9 backfill pin): the SAME concepts
#     spelled SCHEMA_TYPE_PTR, SCHEMA_TYPE_NONE, SCHEMA_ATOMIC_BASIC,
#     SCHEMA_ATOMIC_NONE, SCHEMA_BUILTIN_COUNT, CSchemaType_Bitfield::m_nSize.
#
# walker/src/schema_compat.h adapts both behind canonical names, selected by
# WALKER_SCHEMA_TYPES_NEW_NAMES. We compute that macro by test-compiling a probe
# against the ACTUALLY CHECKED-OUT submodule header: SCHEMA_TYPE_POINTER exists
# only on the NEW era (the OLD era spells it SCHEMA_TYPE_PTR), so the TU compiles
# only on the NEW era and the macro is defined there -> schema_compat resolves
# its canonical names to the pinned identifiers verbatim (byte-identical).
# If it fails, we are on the OLD (era-9) era and schema_compat maps to the
# 426ae7f3 spellings.
#
# This drift is OS-independent (the enum rename is platform-agnostic), so the
# probe runs on both platforms.
#
# CACHE NOTE (era switching): like the convar probe, the result is cached. Clear
# it when switching the pinned submodule across this rename boundary:
#   cmake -U WALKER_SCHEMA_TYPES_NEW_NAMES
include(CheckCXXSourceCompiles)
set(CMAKE_REQUIRED_INCLUDES
  "${HL2SDK_ROOT}/public"
  "${HL2SDK_ROOT}/public/tier0"
  "${HL2SDK_ROOT}/public/tier1"
  "${HL2SDK_ROOT}/public/schemasystem")
# Mirror the real target's per-OS compile context (see WALKER_HL2SDK_PROBE_DEFS above)
# so this isolated try-compile compiles schematypes.h exactly as a real TU does. WIN32
# value is byte-identical to the historical inline defines.
set(CMAKE_REQUIRED_DEFINITIONS ${WALKER_HL2SDK_PROBE_DEFS})
# The OLD-ConVar eras (f3b44f20/426ae7f3/00644551) get PLATFORM_64BITS on the real
# target (see the else() branch near the bottom of this file), and the pinned
# threadtools.h int128/__m128i typedef is gated on it. schematypes.h pulls in
# threadtools.h, so WITHOUT PLATFORM_64BITS this isolated compile fails on GCC with
# "int128 was not declared" and the probe FALSE-reports OLD schematypes — which broke
# f3b44f20 (a NEW-schematypes era) on Linux. The convar probe already ran, so gate on
# its verdict to mirror the real target exactly. PLATFORM_64BITS affects alignment/
# int128 only, not the SCHEMA_TYPE_POINTER-vs-PTR naming the probe discriminates on,
# so the NEW/OLD verdict for these eras is unchanged — it just lets the header compile.
if(NOT WALKER_CONVAR_HAS_CONVARDATA_API)
  list(APPEND CMAKE_REQUIRED_DEFINITIONS -DPLATFORM_64BITS=1)
endif()
set(CMAKE_REQUIRED_FLAGS "${WALKER_HL2SDK_PROBE_FLAGS}")
# COMPILE-ONLY probe (no link). schematypes.h pulls in the CUtl container chain
# (CUtlMap/CUtlString/CUtlVector etc.) which can emit tier0 import references at
# LINK on some pins, so building the try-compile as a STATIC_LIBRARY skips the
# link entirely and the probe reflects COMPILE success only — mirroring the
# convar probe idiom. The renamed enumerator is referenced in an UNEVALUATED
# operand so nothing is ODR-used regardless.
set(_WALKER_SAVED_TRYCOMPILE_TARGET_TYPE "${CMAKE_TRY_COMPILE_TARGET_TYPE}")
set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
check_cxx_source_compiles("
  #include \"schemasystem/schematypes.h\"
  // NEW era spells the pointer category SCHEMA_TYPE_POINTER and the bitfield
  // count member m_nBitfieldCount; the OLD (426ae7f3) era spells them
  // SCHEMA_TYPE_PTR / m_nSize, so this TU COMPILES ONLY on the NEW era.
  int main() {
    (void)sizeof(int[(int)SCHEMA_TYPE_POINTER + 1]);
    (void)sizeof(decltype(((CSchemaType_Bitfield*)nullptr)->m_nBitfieldCount));
    return 0;
  }
" WALKER_SCHEMA_TYPES_NEW_NAMES)
set(CMAKE_TRY_COMPILE_TARGET_TYPE "${_WALKER_SAVED_TRYCOMPILE_TARGET_TYPE}")
unset(_WALKER_SAVED_TRYCOMPILE_TARGET_TYPE)
unset(CMAKE_REQUIRED_INCLUDES)
unset(CMAKE_REQUIRED_DEFINITIONS)
unset(CMAKE_REQUIRED_FLAGS)
if(WALKER_SCHEMA_TYPES_NEW_NAMES)
  message(STATUS "hl2sdk era: schematypes naming is NEW (SCHEMA_TYPE_POINTER/m_nBitfieldCount) -> schema_compat passthrough")
else()
  message(STATUS "hl2sdk era: schematypes naming is OLD (SCHEMA_TYPE_PTR/m_nSize, era-9) -> schema_compat rename map")
endif()

# --- UNIX (GCC) hl2sdk compile context — DIRECTORY SCOPE --------------
# The Source2/hl2sdk platform selection PLUS the GCC portability shims are hoisted to
# DIRECTORY scope here (add_compile_definitions) rather than set per-target, so BOTH the
# cs2_schema_walker binary AND every walker/tests/ target (layout_probe_test,
# era_pins_consistency_test — which compile the same hl2sdk header chain via
# schema_record_layout_2023.h -> sdk_schema.h) inherit ONE identical context. Without
# this the test targets re-hit the CReuseVaList va_list error (platform.h:1335) and the
# undeclared bare `stricmp` (logging.h:655, utlrbtree.h:60/127) that the walker target
# already resolves.
#
# PLACEMENT is load-bearing: this runs BEFORE add_executable(cs2_schema_walker) and
# BEFORE add_subdirectory(tests) at the end of this file, so (a) the walker target picks
# it up and (b) the tests subdirectory snapshots it at add_subdirectory time.
#
# WINDOWS IS UNTOUCHED: the block is UNIX-gated (if(UNIX AND NOT WIN32) — false on MSVC,
# where WIN32 is set and UNIX is not), so no directory-scope define is ever added on the
# Windows build. The MSVC targets keep their own per-target _WIN32/COMPILER_MSVC defines
# verbatim; the Windows walker binary + its committed artifacts stay byte-identical.
#
#   _LINUX POSIX COMPILER_GCC GNUC  base Source2/hl2sdk platform selection.
#   LINUX                           platform.h CReuseVaList va_copy path (else GCC
#                                   hard-errors on an invalid va_list array-assignment).
#   stricmp / strcmpi               map bare stricmp -> POSIX strcasecmp (see the
#                                   WALKER_HL2SDK_PROBE_DEFS note above): identical
#                                   case-insensitive semantics, link-safe (no unresolved
#                                   V_tier0_stricmp), no DLL-visibility flip.
# PLATFORM_64BITS stays era-gated per-target below (it is not needed to compile here and
# the NEW-era build deliberately omits it).
if(UNIX AND NOT WIN32)
  add_compile_definitions(
    _LINUX POSIX COMPILER_GCC GNUC
    LINUX stricmp=strcasecmp strcmpi=strcasecmp)
endif()

# --- Protoc codegen ordering target (fresh parallel-build race fix) -------------------
# The generated protobuf sources/headers (WALKER_PROTO_SRCS/HDRS) are produced by the
# add_custom_command above. Several walker_core TUs (schema_walk.cpp etc.) #include the
# generated *.pb.h. On a FRESH high-`-j` build nothing told make/ninja that those object
# compiles must wait for protoc to finish writing the headers, so a consumer could start
# before codegen completed -> transient rc=2 abort with ZERO compile errors (a pure
# race; an incremental rerun completed cleanly). Wrapping the codegen outputs in one
# custom target and making walker_core depend on it serializes protoc BEFORE any
# walker_core compile. (Never observed on MSBuild/Windows, which schedules generation
# differently; this fix is OS-agnostic and changes no compiler input.)
add_custom_target(walker_protoc_gen DEPENDS ${WALKER_PROTO_SRCS} ${WALKER_PROTO_HDRS})

# --- walker_core: the walker sources + generated protobuf, compiled ONCE --------------
# Build fix. The walker executable AND both walker/tests/ targets used to each
# re-list and RECOMPILE the full src/*.cpp set + the generated *.pb.cc. That triple
# recompile (~3x wasted work) of the SAME generated sources across three independent
# targets is what raced on a fresh -j build. We now compile the shared sources EXACTLY
# ONCE into an OBJECT library and link it into all three consumers.
#
# OBJECT (not STATIC) is deliberate: an OBJECT library links ALL its objects into every
# consumer unconditionally — identical to the previous "list every source in the target"
# behavior — so no unreferenced symbol the tests rely on can be dropped (a STATIC .a
# could gc unreferenced members). Consumers see byte-identical objects and the exact
# same symbol set as before; only main.cpp (walker) / the test .cpp files are
# per-consumer. tier0_link_stubs.cpp is picked up by this GLOB; it self-disables (empty
# TU) unless the era probe set WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS below.
#
# The shared objects compile with the SAME include dirs + compile definitions the
# walker executable used before — carried here as PUBLIC so the executable's own main.cpp
# and the test .cpp files inherit the IDENTICAL set. No compiler flag, define, or source
# feeding the walker changes on either OS; only the compile is deduplicated. The
# per-OS platform split below is unchanged (WIN32 defs verbatim; UNIX compile defs still
# come from the directory-scope add_compile_definitions above).
file(GLOB WALKER_CORE_SOURCES CONFIGURE_DEPENDS "src/*.cpp")
list(FILTER WALKER_CORE_SOURCES EXCLUDE REGEX "/main\\.cpp$")
if(NOT WALKER_CORE_SOURCES)
  message(FATAL_ERROR "walker/src/ has no non-main .cpp files; expected at least one.")
endif()

add_library(walker_core OBJECT
  ${WALKER_CORE_SOURCES}
  ${WALKER_PROTO_SRCS}
)
add_dependencies(walker_core walker_protoc_gen)

# --- Release version stamp (bundle) ----------------------------------------------------
# The walker's version defaults to PROJECT_VERSION (0.2.0, from project() above). The
# release/bundle build injects the Nerdbank.GitVersioning version (e.g. 1.0.1) via
# -DWALKER_VERSION_OVERRIDE=<ver>; when unset, PROJECT_VERSION stands so dev/test builds
# are unaffected. WALKER_VERSION_VALUE is the single source that feeds BOTH the generated
# version header (CS2_WALKER_VERSION, consumed by --version) and the WALKER_VERSION
# compile define below (kWalkerVersion), so they can never disagree.
set(WALKER_VERSION_OVERRIDE "" CACHE STRING "Override the walker version string (release build passes the Nerdbank.GitVersioning version, e.g. 1.0.1)")
if(WALKER_VERSION_OVERRIDE)
  set(WALKER_VERSION_VALUE "${WALKER_VERSION_OVERRIDE}")
else()
  set(WALKER_VERSION_VALUE "${PROJECT_VERSION}")
endif()
# Generate the version header into the build tree from src/version.h.in. Kept under a
# distinct name (walker_version.h) so it never shadows the hand-written src/version.h,
# which still owns the git-SHA / schema-family defines.
# TODO(bundle): also emit a Windows VERSIONINFO resource stamped with WALKER_VERSION_VALUE.
set(WALKER_GEN_INCLUDE_DIR "${CMAKE_CURRENT_BINARY_DIR}/generated")
configure_file(
  "${CMAKE_SOURCE_DIR}/src/version.h.in"
  "${WALKER_GEN_INCLUDE_DIR}/walker_version.h"
  @ONLY)

target_include_directories(walker_core PUBLIC
  "${CMAKE_SOURCE_DIR}/src"
  "${WALKER_GEN_INCLUDE_DIR}"
  "${WALKER_PROTO_GEN_DIR}"
  "${HL2SDK_ROOT}/public"
  "${HL2SDK_ROOT}/public/tier0"
  "${HL2SDK_ROOT}/public/tier1"
  "${HL2SDK_ROOT}/public/schemasystem"
  # icvar.h + networksystem/inetworkmessages.h drag in the
  # appframework / interfaces / mathlib / networksystem header chains.
  "${HL2SDK_ROOT}/public/interfaces"
  "${HL2SDK_ROOT}/public/mathlib"
)

target_compile_definitions(walker_core PUBLIC
  WALKER_VERSION="${WALKER_VERSION_VALUE}"
  WALKER_GIT_SHA="${WALKER_GIT_SHA}"
  WALKER_SRC_FPRINT="${WALKER_SRC_FPRINT}"
  HL2SDK_SUBMODULE_SHA="${HL2SDK_SUBMODULE_SHA}"
  # walker<->host lockstep: the EMITTED walker_output.schema_version. Kept in
  # sync with the schemas/ family (schemas/walker_output.proto, host SchemaFamily.cs)
  # at 0.4.0. INTERNAL only — the host re-stamps every committed public
  # artifact's schemaVersion to SchemaFamily.Version, so this does not affect committed
  # artifact bytes; it is the version a raw walker_output carries for provenance.
  WALKER_SCHEMA_VERSION="0.4.0"
)

# ConVar API era (see the probe above and walker/src/convar_compat.h). Defined on
# the NEW (ConVarRef/ConVarData) era only; absent => the OLD (ConVarHandle) path.
# schematypes.h enum/member naming era (see the probe above and
# walker/src/schema_compat.h). Defined on the NEW (SCHEMA_TYPE_POINTER /
# m_nBitfieldCount) era only; absent => the OLD (era-9, SCHEMA_TYPE_PTR /
# m_nSize) rename map. Both platform-independent.
if(WALKER_SCHEMA_TYPES_NEW_NAMES)
  target_compile_definitions(walker_core PUBLIC
    WALKER_SCHEMA_TYPES_NEW_NAMES=1)
endif()

if(WALKER_CONVAR_HAS_CONVARDATA_API)
  target_compile_definitions(walker_core PUBLIC
    WALKER_CONVAR_HAS_CONVARDATA_API=1)
else()
  # --- PLATFORM_64BITS (OLD-era schema-binding layout fix; BUG-1) ----------------
  # hl2sdk gates several tier0 layout decisions on PLATFORM_64BITS but NEVER
  # defines it in-header — Valve's own VPC build supplies it for every x64 config.
  # The walker never defined it, so on this OLD era CTSListBase
  # (CUtlMemoryPoolBase::m_FreeBlocks, inside CUtlTSHash::m_EntryMemory) compiled
  # with 8-byte instead of 16-byte alignment (tier0/tslist.h:37). That changes the
  # pad before m_FreeBlocks and hence sizeof(CUtlTSHash), shifting the OFFSET of
  # CSchemaSystemTypeScope::m_EnumBindings while leaving m_ClassBindings.Count()
  # (read before m_FreeBlocks) correct — exactly the observed "classes fine, enums
  # garbage" symptom. Defining PLATFORM_64BITS makes the walker's struct alignment
  # match the 64-bit game binary. This is a build-input correctness fix, NOT a
  # guessed offset: the value comes straight from the pinned hl2sdk headers.
  #
  # ERA-GATED to the OLD (ConVarHandle) path so the NEW-era build is byte-for-byte
  # unchanged. The OLD-era schema fingerprint WILL change once this lands
  # (the layout it dereferences shifted to the correct one), so the era-8 KNOWN
  # signature in layout_probe.cpp must be re-probed against the corrected build.
  #
  # NOTE: PLATFORM_64BITS also enables the int128/__m128i typedef used by
  # tier0/tslist.h's TSLHead_t and threadtools.h — required for the 16-byte
  # CTSListBase head; with it absent the head fell back to int64.
  target_compile_definitions(walker_core PUBLIC
    PLATFORM_64BITS=1)
endif()

# protobuf: PUBLIC so every consumer that links walker_core also links libprotobuf and
# sees its headers (the generated *.pb.cc live in walker_core; consumers just link).
if(TARGET protobuf::libprotobuf)
  target_link_libraries(walker_core PUBLIC protobuf::libprotobuf)
else()
  target_link_libraries(walker_core PUBLIC ${Protobuf_LIBRARIES})
  target_include_directories(walker_core PUBLIC ${Protobuf_INCLUDE_DIRS})
endif()

if(WIN32)
  target_compile_definitions(walker_core PUBLIC
    _WIN32 COMPILER_MSVC COMPILER_MSVC64
    NOMINMAX WIN32_LEAN_AND_MEAN
  )
  # Enable the tier0 read-lock link stubs only for the dllimport era (see the era
  # probe above and walker/src/tier0_link_stubs.cpp — it lives in walker_core).
  if(WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA)
    target_compile_definitions(walker_core PUBLIC
      WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS=1)
  endif()
elseif(UNIX)
  # Compile defs (_LINUX POSIX COMPILER_GCC GNUC + LINUX + stricmp shims) come from the
  # directory-scope add_compile_definitions above. dl/pthread are PUBLIC link libs so the
  # walker + both test executables that link walker_core inherit them.
  # Enable the tier0 read-lock link stubs for the dllimport era on Linux too — g++ emits
  # the same unresolved externals MSVC does (see the cross-platform probe above and
  # walker/src/tier0_link_stubs.cpp). For the current inline-lock era the probe is false
  # and this is unset, so the stub TU stays empty (no double-definition).
  if(WALKER_TIER0_SPINRWLOCK_IS_DLLIMPORT_ERA)
    target_compile_definitions(walker_core PUBLIC
      WALKER_TIER0_SPINRWLOCK_DLLIMPORT_STUBS=1)
  endif()
  target_link_libraries(walker_core PUBLIC dl pthread)
endif()

# --- Walker binary --------------------------------------------------------------------
# Just main.cpp + the shared walker_core objects (which carry all include dirs, compile
# defs and the protobuf/dl/pthread link as PUBLIC usage requirements). main.cpp does not
# #include any generated *.pb.h, and it links walker_core (which depends on
# walker_protoc_gen), so codegen ordering is covered transitively.
add_executable(cs2_schema_walker src/main.cpp)
target_link_libraries(cs2_schema_walker PRIVATE walker_core)

# --- tier0 telemetry compatibility shim (Windows only) --------------------------------
# Supplies the three telemetry symbols the 2023-03-22 limited-test pulse_system.dll
# imports from a tier0 Valve never shipped. The loader's ERROR_PROC_NOT_FOUND recovery
# path (walker/src/pe_import_shim.cpp) redirects exactly those imports here, so that
# module loads and contributes its ~78 CPulse* schema types instead of aborting the walk.
#
# It MUST sit next to the era walker binary: ResolveShimPath() looks beside the running
# executable, and build-era-walkers.ps1 installs every *.dll sibling of the walker into
# natives/{platform}/. Forcing the output dir to the walker's own keeps both true without
# touching the install script. Not a link-time dependency of the walker (it is loaded
# with LoadLibrary at need), so an explicit add_dependencies keeps it built in step.
if(WIN32)
  add_library(cs2_tier0_tm_shim SHARED shim/cs2_tier0_tm_shim.cpp)
  set_target_properties(cs2_tier0_tm_shim PROPERTIES
    OUTPUT_NAME "cs2_tier0_tm_shim"
    LINK_FLAGS "/DEF:\"${CMAKE_CURRENT_SOURCE_DIR}/shim/cs2_tier0_tm_shim.def\"")
  set_target_properties(cs2_tier0_tm_shim PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "$<TARGET_FILE_DIR:cs2_schema_walker>")
  add_dependencies(cs2_schema_walker cs2_tier0_tm_shim)
endif()

# --- Portable-bundle RPATH (Linux) ----------------------------------------------------
# In the native bundle, the bundled libprotobuf.so is co-located ONCE per platform dir
# (natives/linux-x86_64/) and shared by every era ELF in that dir (dedup). Setting
# $ORIGIN as the ELF's RPATH makes each era walker find that sibling .so relative to its
# own location, with NO LD_LIBRARY_PATH needed at invocation time. BUILD_WITH_INSTALL_RPATH
# bakes it in at link time (we never `make install`); INSTALL_RPATH_USE_LINK_PATH FALSE
# keeps the vcpkg/apt build-tree paths OUT of the RPATH so the ELF stays portable.
# No-op on Windows (DLL search is dir-relative already), so this is guarded to UNIX.
if(UNIX AND NOT APPLE)
  set_target_properties(cs2_schema_walker PROPERTIES
    INSTALL_RPATH "$ORIGIN"
    BUILD_WITH_INSTALL_RPATH TRUE
    INSTALL_RPATH_USE_LINK_PATH FALSE)
endif()

# Optional era-named output for the native bundle (natives/{platform}/{era}).
# When -DWALKER_ERA_NAME=<era> is passed, the emitted binary is named literally
# after the era (e.g. cs2-2026-07-09 on Linux, cs2-2026-07-09.exe on Windows) so
# the portable per-era bundle can lay each walker out at natives/{platform}/{era}
# without a script-side rename. The TARGET name stays cs2_schema_walker (so tests/
# and existing CI are unaffected); only OUTPUT_NAME changes. When the var is UNSET
# (the default + tests + dev build) the binary is the unchanged cs2_schema_walker —
# the default build is byte-identical. The per-era build script passes this so the
# fresh per-era build emits the era-named binary directly.
set(WALKER_ERA_NAME "" CACHE STRING "Optional era name to use as the walker OUTPUT_NAME (per-era native bundle: natives/{platform}/{era})")
if(WALKER_ERA_NAME)
  set_target_properties(cs2_schema_walker PROPERTIES
    OUTPUT_NAME "${WALKER_ERA_NAME}")
  message(STATUS "Walker OUTPUT_NAME (era-named for native bundle): ${WALKER_ERA_NAME}")
endif()

# --- Tests ----------------------------------------------------------------------------
enable_testing()
if(EXISTS "${CMAKE_SOURCE_DIR}/tests/CMakeLists.txt")
  add_subdirectory(tests)
endif()
