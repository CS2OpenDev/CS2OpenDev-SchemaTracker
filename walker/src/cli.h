// Hand-rolled argv parser for the walker.
//
// No third-party CLI library is permitted. The surface is tiny — a subcommand +
// a handful of --key value pairs — so we hand-roll.
//
// Exit codes follow sysexits.h conventions where meaningful:
//   0  success
//   64 EX_USAGE      — usage error (bad/missing args, unknown subcommand)
//   65 EX_DATAERR    — input data invalid (e.g. binaries dir missing files)
//   69 EX_UNAVAILABLE— required interface/symbol not found in a loaded DLL
//   70 EX_SOFTWARE   — internal error (should not happen)
//   75 EX_TEMPFAIL   — schema-system layout signature unknown; the host is
//                      expected to surface this with the signature so a new
//                      layout dispatcher can be authored.
#pragma once

#include <string>

namespace cs2_schema_walker {

enum class Subcommand {
  kNone,
  kVersion,
  // Print the COMPILE-TIME layout signature (ComputeLayoutSignature()) to stdout
  // and exit 0. Requires NO --binaries / loaded modules — it is a pure function
  // of the pinned HL2SDK layout. Used by the per-era build harness for the
  // build-time signature gate when no CS2 binaries are on disk.
  kPrintSignature,
  kProbeLayout,
  kWalk,
  // DIAGNOSTIC: raw byte-dump of the live
  // CSchemaSystemTypeScope class-binding container + sampled records, to stderr.
  // Read-only; writes no output file; never on the normal walk path, so
  // committed-era output stays byte-identical. See schema_bytes_dump.h.
  kDumpSchemaBytes,
};

struct ParsedArgs {
  Subcommand subcommand = Subcommand::kNone;
  std::string binaries_dir;  // --binaries
  std::string platform;      // --platform (walk only)
  std::string out_path;      // --out   (walk only)
  std::string error;         // populated on parse failure
};

// Parse argc/argv. Returns ParsedArgs with .error set on failure.
// Callers print .error to stderr and exit 64 on failure.
ParsedArgs ParseArgs(int argc, char** argv);

// One-line usage banner for stderr.
const char* UsageBanner();

}  // namespace cs2_schema_walker
