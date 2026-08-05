// NetMessages id->type table (integer message-ID -> proto type name).
//
// Emits, per channel, the integer message ID and the bound protobuf message type
// name into NetworkMessagesWalk (which reuses the public NetworkChannel shape).
//
// SOURCE (two-tier, see netmsg_walk.cpp):
//   1. PREFERRED (build-specific): the live INetworkMessages registry. Currently
//      EMPTY in the headless walk — populating it needs ~the whole engine Init,
//      which AVs on the partial boot (see engine_boot.cpp).
//   2. FALLBACK (PIN-STATIC): the id->type table DERIVED from the pinned
//      net-message .proto enums (netmsg_table.generated.inc, produced by
//      walker/tools/gen_netmsg_table.py — every entry verified to name a real
//      `message`). Applied when the runtime registry is empty.
//
// Determinism: channels sorted by name Ordinal, entries within a channel by
// (id, proto_message_type). Clean-room: the .protos are the only allowed input;
// no Valve struct layout re-declared beyond the netmsg_walk.cpp interface mirror.
// A null INetworkMessages handle is not corruption (the static fallback still
// applies); an empty result is not an error.
#pragma once

#include <string>
#include <vector>

// Forward-declare the proto message so this header has no protobuf include.
namespace cs2 {
namespace schema_tracker {
namespace v0 {
class NetworkMessagesWalk;
}
}  // namespace schema_tracker
}  // namespace cs2

namespace cs2_schema_walker {

class InProcessEnvironment;

// Walk the network-message id->type table (registry, else pinned-.proto fallback)
// reachable from `env` into `out`. `out` is cleared first. Returns true on success.
//
// This still populates the (retiring) WalkerOutput.network_messages field.
// The registry-audit network_message universe is NO LONGER derived here: that
// family is host-owned (the host's offline RTTI scan mints both network_messages.json
// and the audit universe's netmsg rows — see ExtractCommand.AssembleAuditUniverse).
// The former EnumerateLiveNetworkMessages / NetMsgRef universe hook was removed with
// its only caller (registry_universe_walk); recover from git history if ever needed.
bool WalkNetworkMessages(const InProcessEnvironment& env,
                         cs2::schema_tracker::v0::NetworkMessagesWalk* out,
                         std::string* err);

}  // namespace cs2_schema_walker
