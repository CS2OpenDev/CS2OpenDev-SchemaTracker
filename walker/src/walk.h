// Drives a full walk and writes a walker_output.proto-shaped file.
//
// The walk runs end to end: probe the schema-system layout (unknown layout
// aborts) -> load the CS2 modules in-process -> partially boot the engine (for
// ConVars) -> retry schema registration if the live system came up empty -> run
// the six extraction walks (schema system, convars/commands, network messages,
// engine constants, string pools, registry universe) -> serialize -> atomic
// write -> hard-exit to dodge the Source2 detach fault (see RunWalk in walk.cpp).
//
// Platform model: there are two platforms — windows-x86_64 and linux-x86_64
// (no .client/.server split). One walk loads ALL modules (client + server +
// engine) and carries the originating module per-class via SchemaClass.module.
//
// The walk fails loud and writes zero bytes on any error. Every collection
// emitted MUST be sorted by a stable key before serialization so the live
// registries' iteration order never leaks into output.
#pragma once

#include <filesystem>
#include <string>

namespace cs2_schema_walker {

struct WalkArgs {
  std::filesystem::path binaries_dir;
  std::string platform;
  std::filesystem::path out_path;
};

// Run the walk. Returns true on success; on failure, populates *err and
// guarantees that no bytes have been written to args.out_path.
bool RunWalk(const WalkArgs& args, std::string* err);

}  // namespace cs2_schema_walker
