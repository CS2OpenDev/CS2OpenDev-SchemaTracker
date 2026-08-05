// Argv parser implementation. See cli.h for surface + exit-code notes.
#include "cli.h"

#include <cstring>
#include <string_view>

namespace cs2_schema_walker {

namespace {

// Pull the value for a --flag: argv[i] is the flag, the value is argv[i+1].
// Advances *i on success. On failure (missing value), sets *err and returns false.
bool TakeFlagValue(int argc, char** argv, int* i, std::string_view flag,
                   std::string* out, std::string* err) {
  if (*i + 1 >= argc) {
    *err = "missing value for ";
    err->append(flag);
    return false;
  }
  *out = argv[*i + 1];
  *i += 1;  // consume the value; the loop's own ++ consumes the flag itself
  return true;
}

bool RequireNonEmpty(const std::string& v, std::string_view what, std::string* err) {
  if (v.empty()) {
    *err = "required argument missing: ";
    err->append(what);
    return false;
  }
  return true;
}

}  // namespace

const char* UsageBanner() {
  return "Usage:\n"
         "  cs2_schema_walker --version | -v\n"
         "  cs2_schema_walker --print-signature\n"
         "  cs2_schema_walker probe-layout --binaries <dir>\n"
         "  cs2_schema_walker dump-schema-bytes --binaries <dir>\n"
         "  cs2_schema_walker walk --binaries <dir> --platform <P> --out <file>\n"
         "                 P is windows-x86_64 | linux-x86_64\n"
         "\n"
         "Options:\n"
         "  --print-signature    Print the compile-time schema-system layout signature\n"
         "                       (ComputeLayoutSignature()) to stdout and exit 0.\n"
         "                       Requires NO --binaries / loaded modules. Output is\n"
         "                       exactly the signature string + a trailing newline, so\n"
         "                       a script can capture it as the only line. Used by the\n"
         "                       per-era build harness for the build-time gate.\n"
         "\n"
         "Subcommands:\n"
         "  probe-layout         Detect the schema-system memory-layout signature.\n"
         "  dump-schema-bytes    DIAGNOSTIC: dump the raw class-binding CONTAINER +\n"
         "                       sampled record bytes (hex + annotated slots) to stderr\n"
         "                       for each live type scope. Read-only; writes no output\n"
         "                       file. For the pre-2024 V1 container/record derivation.\n"
         "  walk                 Run a full schema/cvar/netmsg walk and write a\n"
         "                       walker_output.proto-shaped file to --out.\n";
}

ParsedArgs ParseArgs(int argc, char** argv) {
  ParsedArgs p;

  if (argc < 2) {
    p.error = "no subcommand";
    return p;
  }

  std::string_view first = argv[1];

  if (first == "--version" || first == "-v") {
    p.subcommand = Subcommand::kVersion;
    return p;
  }

  if (first == "--print-signature") {
    p.subcommand = Subcommand::kPrintSignature;
    return p;
  }

  if (first == "probe-layout") {
    p.subcommand = Subcommand::kProbeLayout;
  } else if (first == "dump-schema-bytes") {
    p.subcommand = Subcommand::kDumpSchemaBytes;
  } else if (first == "walk") {
    p.subcommand = Subcommand::kWalk;
  } else {
    p.error = "unknown subcommand: ";
    p.error.append(first);
    return p;
  }

  for (int i = 2; i < argc; ++i) {
    std::string_view a = argv[i];
    if (a == "--binaries") {
      if (!TakeFlagValue(argc, argv, &i, a, &p.binaries_dir, &p.error)) return p;
    } else if (a == "--platform") {
      if (!TakeFlagValue(argc, argv, &i, a, &p.platform, &p.error)) return p;
    } else if (a == "--out") {
      if (!TakeFlagValue(argc, argv, &i, a, &p.out_path, &p.error)) return p;
    } else {
      p.error = "unknown argument: ";
      p.error.append(a);
      return p;
    }
  }

  // Per-subcommand required-arg check.
  switch (p.subcommand) {
    case Subcommand::kProbeLayout:
    case Subcommand::kDumpSchemaBytes:
      if (!RequireNonEmpty(p.binaries_dir, "--binaries", &p.error)) return p;
      break;
    case Subcommand::kWalk:
      if (!RequireNonEmpty(p.binaries_dir, "--binaries", &p.error)) return p;
      if (!RequireNonEmpty(p.platform, "--platform", &p.error)) return p;
      if (!RequireNonEmpty(p.out_path, "--out", &p.error)) return p;
      break;
    default:
      break;
  }
  return p;
}

}  // namespace cs2_schema_walker
