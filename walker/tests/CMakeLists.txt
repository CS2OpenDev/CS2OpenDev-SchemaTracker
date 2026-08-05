# Walker tests. CTest-driven.
#
# Tests fall into two camps:
#   1. Process-level smoke tests — invoke the walker binary itself with various
#      argv and assert exit code + stdout/stderr shape. These need no extra
#      dependencies and run on every CI build.
#   2. Unit tests (e.g. cli.cpp parser exercises, layout_probe stub round-trip)
#      compiled as separate executables. Added piecemeal as code lands.
#
# We do NOT pull in GoogleTest; the smoke surface below is sufficient for now. A real
# unit framework lands when the first non-trivial walker module needs it.

# --- Process-level smoke tests ----------------------------------------------------

# Exit-0: --version prints the version banner.
add_test(
  NAME walker-version
  COMMAND $<TARGET_FILE:cs2_schema_walker> --version
)
set_tests_properties(walker-version PROPERTIES
  PASS_REGULAR_EXPRESSION "^cs2-schema-walker "
)

# Exit-0: --print-signature prints the compile-time layout signature (binaries-free).
# The output must be exactly the signature line, of the well-formed shape
# `hl2sdk-cs2/<sha>/v1/<16 hex>`. This is the line the per-era build harness captures
# for the build-time signature gate, so its shape is load-bearing.
add_test(
  NAME walker-print-signature
  COMMAND $<TARGET_FILE:cs2_schema_walker> --print-signature
)
# CTest uses cmsys RegularExpression, which supports neither {n} interval quantifiers
# nor a CRLF-aware end anchor. So: no `{16}` (it is matched literally and always fails),
# and no trailing `\n?$` (Windows CRLF stdout puts \r\n after the hex). `[0-9a-f]+` after
# the `/v1/` prefix is the load-bearing shape check for this smoke test.
set_tests_properties(walker-print-signature PROPERTIES
  PASS_REGULAR_EXPRESSION "^hl2sdk-cs2/.+/v1/[0-9a-f]+"
)

# Exit-64: unknown subcommand fails with usage error.
add_test(
  NAME walker-bad-subcommand
  COMMAND $<TARGET_FILE:cs2_schema_walker> bogus-subcommand
)
set_tests_properties(walker-bad-subcommand PROPERTIES WILL_FAIL TRUE)

# Exit-64: no args at all fails with usage error.
add_test(
  NAME walker-no-args
  COMMAND $<TARGET_FILE:cs2_schema_walker>
)
set_tests_properties(walker-no-args PROPERTIES WILL_FAIL TRUE)

# Exit-64: walk missing required args.
add_test(
  NAME walker-walk-missing-args
  COMMAND $<TARGET_FILE:cs2_schema_walker> walk
)
set_tests_properties(walker-walk-missing-args PROPERTIES WILL_FAIL TRUE)

# Exit-65: probe-layout against a directory that doesn't exist.
add_test(
  NAME walker-probe-missing-dir
  COMMAND $<TARGET_FILE:cs2_schema_walker> probe-layout --binaries ${CMAKE_CURRENT_BINARY_DIR}/no-such-dir-12345
)
set_tests_properties(walker-probe-missing-dir PROPERTIES WILL_FAIL TRUE)

# Exit non-zero: walk against a directory that doesn't exist. Fail-loud
# BEFORE any output — the probe (run first inside walk) rejects the missing dir.
add_test(
  NAME walker-walk-missing-dir
  COMMAND $<TARGET_FILE:cs2_schema_walker> walk
          --binaries ${CMAKE_CURRENT_BINARY_DIR}/no-such-dir-67890
          --platform linux-x86_64
          --out ${CMAKE_CURRENT_BINARY_DIR}/should-not-be-created.bin
)
set_tests_properties(walker-walk-missing-dir PROPERTIES WILL_FAIL TRUE)

# Assert the failed walk above left ZERO output bytes on disk. CMake has
# no native "file must not exist" test op, so we drive it through cmake -E: the
# command succeeds (exit 0) only if the path is absent.
add_test(
  NAME walker-walk-missing-dir-no-output
  COMMAND ${CMAKE_COMMAND}
          -DWALKER_OUTPUT_THAT_MUST_NOT_EXIST=${CMAKE_CURRENT_BINARY_DIR}/should-not-be-created.bin
          -P ${CMAKE_CURRENT_SOURCE_DIR}/assert_absent.cmake
)
# Ordering: the no-output assertion must run AFTER the failing walk.
set_tests_properties(walker-walk-missing-dir-no-output PROPERTIES
  DEPENDS walker-walk-missing-dir
)

# --- Unit tests -------------------------------------------------------------------
#
# Both unit tests link the shared walker_core OBJECT library (defined in the parent
# walker/CMakeLists.txt) instead of re-listing + RECOMPILING the walker sources + the
# generated protobuf. This is the fresh-parallel-build fix: the walker binary and
# both tests used to each compile the same src/*.cpp + *.pb.cc independently, which both
# wasted ~3x work and RACED on the shared generated sources at high -j. walker_core
# compiles them exactly once; each test just adds its own .cpp and links the library.
#
# walker_core is an OBJECT library, so ALL its objects link into each test
# unconditionally — the exact same symbol set the tests got when they listed the sources
# directly (loader.cpp / layout_probe.cpp / schema_walk.cpp: ResolveBinaryDirs,
# DiscoverModules, SchemaSystemIsEmpty, DetectSchemaVariant, KnownLayoutSignatures, the
# record-validation core, etc.). walker_core's PUBLIC usage requirements — the src +
# hl2sdk include dirs, the WALKER_* / era-gated (WALKER_SCHEMA_TYPES_NEW_NAMES /
# WALKER_CONVAR_HAS_CONVARDATA_API / PLATFORM_64BITS) compile defs, protobuf, and on UNIX
# dl/pthread + the directory-scope GCC context — propagate to each test's own .cpp, so no
# per-target include/def/link plumbing is needed here. The tests test exactly what they
# did before; only the build graph changed.
#
# layout_probe_test. Validates ComputeLayoutSignature()
# determinism + shape and gate integrity (an arbitrary/unregistered signature is
# never "known").
add_executable(layout_probe_test layout_probe_test.cpp)
target_link_libraries(layout_probe_test PRIVATE walker_core)
add_test(NAME layout-probe-unit COMMAND $<TARGET_FILE:layout_probe_test>)

# --- inventory eras[] <-> kKnownLayoutSignatures consistency -----------------
#
# era_pins_consistency_test: asserts the compile-pin `layoutSignatures` values in the
# consolidated inventory (data/cs2-assets-inventory.json, top-level eras[] array) and
# the compiled-in kKnownLayoutSignatures allow-list agree EXACTLY (set equality), each
# era's hl2sdkSha is embedded in its own signature, and there are no duplicates. It links
# the REAL allow-list via the KnownLayoutSignatures() accessor (no source scraping);
# drift => non-zero exit. The former walker/era-pins.json has been folded into the
# inventory (single source of truth); the walker is its own CMake project so
# CMAKE_SOURCE_DIR is walker/, hence ../data/. INVENTORY_JSON_PATH is used ONLY by this
# test .cpp, so it stays a per-target PRIVATE define rather than going on walker_core.
# --- Windows import-shim recovery path ---------------------------------------
#
# pe_import_shim_test: covers the ERROR_PROC_NOT_FOUND recovery used by the nine
# 2023-03-22 limited-test builds (pulse_system.dll imports telemetry symbols the
# shipped tier0.dll never exported). Asserts the redirect allow-list is exact —
# the property that keeps this a targeted recovery rather than a blanket "stub
# whatever is missing" tolerance — and round-trips the import-table rewrite on a
# real PE (the test's own binary), so no broken fixture DLL is checked in.
# Registered on all platforms; on non-Windows the TU compiles to a passing no-op.
add_executable(pe_import_shim_test pe_import_shim_test.cpp)
target_link_libraries(pe_import_shim_test PRIVATE walker_core)
add_test(NAME pe-import-shim-unit COMMAND $<TARGET_FILE:pe_import_shim_test>)

add_executable(era_pins_consistency_test era_pins_consistency_test.cpp)
target_link_libraries(era_pins_consistency_test PRIVATE walker_core)
target_compile_definitions(era_pins_consistency_test PRIVATE
  INVENTORY_JSON_PATH="${CMAKE_SOURCE_DIR}/../data/cs2-assets-inventory.json")
add_test(NAME era-pins-consistency COMMAND $<TARGET_FILE:era_pins_consistency_test>)
