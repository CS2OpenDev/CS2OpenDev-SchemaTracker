// Walker version constants. Semver + git SHA are embedded at build time from
// CMakeLists.txt (WALKER_VERSION / WALKER_GIT_SHA compile definitions).
//
// The walker is versioned in lockstep with schemas/walker_output.proto. Bump
// WALKER_VERSION when the emitted proto shape changes; coordinate the bump with
// the public output schemas.
#pragma once

namespace cs2_schema_walker {

#ifndef WALKER_VERSION
#define WALKER_VERSION "0.2.0"
#endif

#ifndef WALKER_GIT_SHA
#define WALKER_GIT_SHA "unknown"
#endif

// Content fingerprint of the walker's build inputs (walker/src/**, CMakeLists.txt,
// gen_netmsg_table.py, schemas/*.proto) -- see walker/tools/src_fingerprint.py.
// Unlike WALKER_GIT_SHA, this changes the instant a covered byte changes, committed
// or not, and is what the host's walker-identity gate and the per-era build
// harness's content-keyed resumability skip key on instead of the git SHA.
#ifndef WALKER_SRC_FPRINT
#define WALKER_SRC_FPRINT "unknown"
#endif

// The schemas/ family version this walker emits.
// Keep in sync with schemas/walker_output.proto (the schema-family version).
#ifndef WALKER_SCHEMA_VERSION
#define WALKER_SCHEMA_VERSION "0.4.0"
#endif

inline constexpr const char* kWalkerVersion = WALKER_VERSION;
inline constexpr const char* kWalkerGitSha = WALKER_GIT_SHA;
inline constexpr const char* kWalkerSrcFingerprint = WALKER_SRC_FPRINT;
inline constexpr const char* kSchemaVersion = WALKER_SCHEMA_VERSION;

}  // namespace cs2_schema_walker
