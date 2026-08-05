# CTest helper: fail (non-zero) iff a path EXISTS.
#
# Used to assert that a fail-loud walker run left ZERO output bytes on disk.
# Invoked as:  cmake -DWALKER_OUTPUT_THAT_MUST_NOT_EXIST=<path> -P assert_absent.cmake
if(NOT DEFINED WALKER_OUTPUT_THAT_MUST_NOT_EXIST)
  message(FATAL_ERROR "assert_absent.cmake: WALKER_OUTPUT_THAT_MUST_NOT_EXIST not set")
endif()
if(EXISTS "${WALKER_OUTPUT_THAT_MUST_NOT_EXIST}")
  message(FATAL_ERROR
    " violation: output file exists after a failed walk: "
    "${WALKER_OUTPUT_THAT_MUST_NOT_EXIST}")
endif()
message(STATUS "OK: ${WALKER_OUTPUT_THAT_MUST_NOT_EXIST} is absent (honored)")
