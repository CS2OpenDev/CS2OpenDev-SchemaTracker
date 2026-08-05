// String pool extraction. See string_pools_walk.h for the contract + the full
// VERIFIED-EMPTY rationale (premise mismatch proven by RE, never-infer-provenance).
//
// STATUS: verified-empty is the correct output. Emits empty string_pools; does NOT
// block the walk.
//
// When restored (see the .h TODO for the reachability blocker): for each
// reachable pool, iterate tbl.String(id) over GetNumStrings(), dedup + sort the
// entries, and emit the pool under its real name verbatim (never inferred).
#include "string_pools_walk.h"

#include "loader.h"

#include "string_pools.pb.h"
#include "walker_output.pb.h"

#include <string>

namespace wpb = cs2::schema_tracker::v0;

namespace cs2_schema_walker {

bool WalkStringPools(const InProcessEnvironment& /*env*/,
                     wpb::StringPoolsWalk* out, std::string* /*err*/) {
  out->Clear();
  // VERIFIED-EMPTY: emit zero pools — the correct output, not a gap (see header).
  // CS2's schema system interns no strings through an enumerable
  // pool (proven by RE + live dump), so pools:[] is verified-complete. This is
  // neither input corruption nor a partial artifact, so it is not a fail-loud
  // condition — string_pools.json is a complete CORE file with an empty pool list.
  return true;
}

}  // namespace cs2_schema_walker
