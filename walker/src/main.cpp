// Walker entry point.
//
// CLI surface (see cli.h for exit codes):
//   cs2_schema_walker --version
//   cs2_schema_walker --print-signature
//   cs2_schema_walker probe-layout --binaries <dir>
//   cs2_schema_walker walk --binaries <dir> --platform <P> --out <file>
//
// Every error path exits non-zero BEFORE writing any output bytes to --out. An
// unknown schema-system layout signature is exit 75 with the signature printed
// to stderr.
#include "cli.h"
#include "layout_probe.h"
#include "schema_bytes_dump.h"
#include "version.h"
#include "walker_version.h"  // generated: CS2_WALKER_VERSION (release version stamp)
#include "walk.h"

#include <cstdio>
#include <cstdlib>
#include <iostream>
#include <string>

// RunWalk Loads the CS2 modules; on Windows their DLL detach faults during
// teardown (see the rationale in walk.cpp), so we hard-exit via TerminateProcess
// to keep the exit code deterministic.
#if defined(_WIN32)
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

namespace {

int RunVersion() {
  // The host parses this line; keep the prefix stable. CS2_WALKER_VERSION is the
  // generated single-source stamp (release override or PROJECT_VERSION); it equals
  // kWalkerVersion by construction (both derive from WALKER_VERSION_VALUE).
  std::cout << "cs2-schema-walker " << CS2_WALKER_VERSION
            << " (git " << cs2_schema_walker::kWalkerGitSha << ", schema "
            << cs2_schema_walker::kSchemaVersion << ")\n";
  // Line 1 above is BYTE-IDENTICAL to the output from before the src-fingerprint
  // line existed -- the per-era build harness and the host's --version parser both
  // key on it. This second line is new: the host's walker-identity gate reads it to
  // detect mixed/stale walker sets across eras/platforms; a binary predating the
  // line simply doesn't print it, which the host's parser treats as fingerprint
  // "unknown" (see WalkerIdentity.cs).
  std::cout << "src-fingerprint " << cs2_schema_walker::kWalkerSrcFingerprint << "\n";
  return 0;
}

int RunPrintSignature() {
  // Emit the COMPILE-TIME layout signature and exit 0. ComputeLayoutSignature()
  // is a pure function of the pinned HL2SDK struct layout + submodule SHA — it
  // touches no binaries and loads no modules (unlike probe-layout). The per-era
  // build harness captures this as the only stdout line for the build-time
  // signature gate, so we print EXACTLY the signature + one newline.
  std::cout << cs2_schema_walker::ComputeLayoutSignature() << "\n";
  return 0;
}

int RunProbeLayout(const cs2_schema_walker::ParsedArgs& args) {
  std::string err;
  auto result = cs2_schema_walker::ProbeLayout(args.binaries_dir, &err);
  if (!result.has_value()) {
    std::cerr << "cs2_schema_walker: probe-layout failed: " << err << "\n";
    return 65;  // EX_DATAERR
  }
  // Print the signature to stdout. The host captures stdout; stderr stays for
  // errors. The signature must appear even on the known path, so the host can
  // record it in provenance.
  std::cout << result->signature << "\n";
  if (!result->known) {
    std::cerr << "cs2_schema_walker: unknown schema-system layout signature: "
              << result->signature << "\n";
    return 75;  // EX_TEMPFAIL — unknown-layout exit code.
  }
  return 0;
}

// DIAGNOSTIC. Loads the modules, boots + registers the
// schema system, and dumps the raw class-binding CONTAINER + sampled record bytes to
// stderr. Writes NO output file. Like `walk`, it loads (and here boots) the CS2
// modules into this process, so it HARD-EXITS to dodge the same Source2 headless-
// teardown fault that would corrupt the exit code (see RunWalk in walk.cpp).
int RunDumpSchemaBytes(const cs2_schema_walker::ParsedArgs& args) {
  std::string err;
  const bool ok = cs2_schema_walker::DumpSchemaBytes(args.binaries_dir, &err);
  int code = 0;
  if (!ok) {
    std::cerr << "cs2_schema_walker: dump-schema-bytes failed: " << err << "\n";
    code = 65;  // EX_DATAERR
  }
  std::cout.flush();
  std::cerr.flush();
  std::fflush(nullptr);
  // Hard-exit to dodge the Source2 detach fault — see RunWalk in walk.cpp. No output
  // file is written by this subcommand, so this loses nothing.
#if defined(_WIN32)
  ::TerminateProcess(::GetCurrentProcess(), static_cast<UINT>(code));
#else
  std::_Exit(code);
#endif
  return code;  // unreachable.
}

int RunWalk(const cs2_schema_walker::ParsedArgs& args) {
  cs2_schema_walker::WalkArgs w;
  w.binaries_dir = args.binaries_dir;
  w.platform = args.platform;
  w.out_path = args.out_path;

  std::string err;
  const bool ok = cs2_schema_walker::RunWalk(w, &err);
  int code;
  if (!ok) {
    std::cerr << "cs2_schema_walker: walk failed: " << err << "\n";
    // Distinguish the unknown-layout exit code from other data errors.
    code = (err.rfind("unknown schema-system layout signature:", 0) == 0) ? 75 : 65;
  } else {
    code = 0;
  }

  // Hard-exit to dodge the Source2 detach fault — see RunWalk in walk.cpp for the
  // full rationale. On success RunWalk has already written + atomically renamed
  // the output (and hard-exits ITSELF, so a successful walk never reaches here);
  // on failure nothing was written. The hard exit preserves the deterministic
  // exit code computed above.
  //
  // The failure path bypasses DLL detach too: std::_Exit / ExitProcess still run
  // DllMain(DLL_PROCESS_DETACH), and that detach FAULTS (0xC0000005) while the
  // engine unregisters ConVar change callbacks — a spurious minidump on an
  // otherwise expected fail-loud. TerminateProcess(Windows) / _Exit(POSIX) skips
  // it, AFTER the stderr error is flushed. The exit code is unchanged.
  std::cout.flush();
  std::cerr.flush();
  std::fflush(nullptr);
#if defined(_WIN32)
  // TerminateProcess skips DLL detach entirely (no DllMain, no atexit, no static
  // dtors), so the detach fault never executes — no 0xC0000005, no minidump. The
  // exit code is the deterministic `code` above (>0 on failure). This is the same
  // mechanism the success path (walk.cpp) uses, just carrying a non-zero code.
  ::TerminateProcess(::GetCurrentProcess(), static_cast<UINT>(code));
#else
  // POSIX: _Exit skips atexit + C++ static destructors; .so finalizers aren't run,
  // so the equivalent detach-time fault never executes.
  std::_Exit(code);
#endif
  return code;  // unreachable; keeps the signature well-formed.
}

}  // namespace

int main(int argc, char** argv) {
  auto parsed = cs2_schema_walker::ParseArgs(argc, argv);
  if (!parsed.error.empty()) {
    std::cerr << "cs2_schema_walker: " << parsed.error << "\n\n"
              << cs2_schema_walker::UsageBanner();
    return 64;  // EX_USAGE
  }

  switch (parsed.subcommand) {
    case cs2_schema_walker::Subcommand::kVersion:
      return RunVersion();
    case cs2_schema_walker::Subcommand::kPrintSignature:
      return RunPrintSignature();
    case cs2_schema_walker::Subcommand::kProbeLayout:
      return RunProbeLayout(parsed);
    case cs2_schema_walker::Subcommand::kDumpSchemaBytes:
      return RunDumpSchemaBytes(parsed);
    case cs2_schema_walker::Subcommand::kWalk:
      return RunWalk(parsed);
    case cs2_schema_walker::Subcommand::kNone:
    default:
      std::cerr << "cs2_schema_walker: internal error: subcommand=none\n";
      return 70;  // EX_SOFTWARE — should be unreachable.
  }
}
