// ICvar (ConCommandBase registry) traversal.
//
// Given a live, fully-loaded in-process environment (loader.h) whose ICvar
// handle the loader already obtained (VEngineCvar007), enumerates every
// registered ConVar and ConCommand and emits them into the
// ConVarsWalk / CommandsWalk messages (which reuse the public ConVar / Command
// shapes verbatim).
//
// Per ConVar we emit: name, default value (rendered as a string), the set of
// flag names, and the help/description string.
// Per ConCommand we emit: name, the set of flag names, and the description.
//
// Determinism: both collections are sorted by name Ordinal before they are added
// to the proto. The live registry's iteration order (a linked-list / hashtable)
// MUST NOT leak into the output.
//
// Fail-loud: a null ICvar handle (the loader could not obtain VEngineCvar007) is
// a STRUCTURAL failure and returns false + sets *err. An empty-but-valid registry
// is NOT corruption and yields empty collections.
//
// This TU is one of the few that includes the HL2SDK convar headers; the loader
// hands the interface across as an opaque void* so loader.h stays HL2SDK-free.
#pragma once

#include <string>
#include <vector>

// Forward-declare the proto messages so this header has no protobuf include.
namespace cs2 {
namespace schema_tracker {
namespace v0 {
class ConVarsWalk;
class CommandsWalk;
}  // namespace v0
}  // namespace schema_tracker
}  // namespace cs2

namespace cs2_schema_walker {

class InProcessEnvironment;

// Walk the live ICvar registry reachable from `env` into `convars_out` /
// `commands_out`. Both are cleared first. Returns true on success; on failure
// sets *err and leaves the outputs in an unspecified (to-be-discarded) state.
bool WalkConVarsAndCommands(const InProcessEnvironment& env,
                            cs2::schema_tracker::v0::ConVarsWalk* convars_out,
                            cs2::schema_tracker::v0::CommandsWalk* commands_out,
                            std::string* err);

// Enumerate the NAMES of every registered ConVar (`convar_names`) and ConCommand
// (`command_names`) in the live ICvar registry reachable from `env`, using the
// SAME index-based ref scan + sentinel filtering WalkConVarsAndCommands uses, so
// the universe keys cannot drift from the extraction keys. Both vectors are
// cleared first; order is unspecified (the universe sorts deterministically).
//
// ConVars/commands carry no module, so the universe pairs them with module ""
// (the caller does this) — matching how the host derives the audit key from a
// ConVar / Command artifact (name + ""). Fail-loud: a null ICvar handle is
// structural (-> false + *err); an empty-but-valid registry yields empty vectors
// and is not an error.
bool EnumerateLiveConVarAndCommandNames(const InProcessEnvironment& env,
                                        std::vector<std::string>* convar_names,
                                        std::vector<std::string>* command_names,
                                        std::string* err);

}  // namespace cs2_schema_walker
