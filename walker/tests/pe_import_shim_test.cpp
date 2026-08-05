// pe_import_shim_test — unit coverage for the Windows ERROR_PROC_NOT_FOUND
// recovery path (walker/src/pe_import_shim.*).
//
// The path exists to rescue the 2023-03-22 limited-test pulse_system.dll, whose
// telemetry imports the shipped tier0.dll never exported. The two properties
// that keep it SAFE rather than merely convenient are checked here:
//
//   1. The redirect allow-list is exact. Anything outside kShimmableImports must
//      be rejected, because stubbing an unknown import would silently change
//      behaviour instead of failing loud.
//   2. The import-table rewrite is structurally sound and actually re-points the
//      named symbols — verified by a round trip on a real PE (this test's own
//      module), so no deliberately-broken fixture binary needs to be checked in.
//
// Parser failure modes are covered too: a non-PE input must be refused, never
// silently treated as having no imports.
//
// Non-Windows builds compile this to a trivially passing main (the whole feature
// is #if defined(_WIN32) — the ELF loader binds lazily and never hits this).

#include <cstdio>

#if defined(_WIN32)

#include <windows.h>

#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

#include "pe_import_shim.h"

namespace shim = cs2_schema_walker::pe_import_shim;

namespace {

int g_failures = 0;

void Check(bool cond, const char* what) {
  if (!cond) {
    std::fprintf(stderr, "FAIL: %s\n", what);
    ++g_failures;
  }
}

std::filesystem::path SelfPath() {
  wchar_t buf[MAX_PATH * 2];
  const DWORD n = ::GetModuleFileNameW(nullptr, buf, static_cast<DWORD>(std::size(buf)));
  if (n == 0 || n >= std::size(buf)) return {};
  return std::filesystem::path(buf);
}

// (1) The allow-list is exactly the three inert telemetry symbols.
void TestAllowList() {
  Check(shim::IsShimmable("g_tm_api"), "g_tm_api is shimmable");
  Check(shim::IsShimmable("?Register@VTm_Zone_Base@@QEAAXXZ"),
        "VTm_Zone_Base::Register is shimmable");
  Check(shim::IsShimmable("?Unregister@VTm_Zone_Base@@QEAAXXZ"),
        "VTm_Zone_Base::Unregister is shimmable");

  // Anything else must be refused — this is the property that stops the recovery
  // path from becoming a blanket "stub whatever is missing" tolerance.
  Check(!shim::IsShimmable("CreateFileW"), "an ordinary import is NOT shimmable");
  Check(!shim::IsShimmable("g_pMemAlloc"), "a tier0 data export is NOT shimmable");
  Check(!shim::IsShimmable(""), "the empty symbol is NOT shimmable");
  Check(!shim::IsShimmable("g_tm_api2"), "a near-miss name is NOT shimmable");
}

// (2) The parser walks a real import table, and the OS oracle agrees that a
// module which loaded successfully has no unresolvable imports.
void TestParsesRealModule() {
  const std::filesystem::path self = SelfPath();
  Check(!self.empty(), "resolved own module path");
  if (self.empty()) return;

  std::string err;
  std::vector<shim::MissingImport> all;
  Check(shim::ListImports(self, &all, &err), ("ListImports on self: " + err).c_str());
  Check(!all.empty(), "own module imports at least one named symbol");

  std::vector<shim::MissingImport> missing;
  err.clear();
  Check(shim::FindUnresolvableImports(self, &missing, &err),
        ("FindUnresolvableImports on self: " + err).c_str());
  // This process is running, so by construction every import resolved.
  Check(missing.empty(), "a running module reports no unresolvable imports");
}

// (3) A non-PE input is refused rather than reported as import-free.
void TestRejectsNonPe() {
  const std::filesystem::path tmp =
      std::filesystem::temp_directory_path() / "cs2_pe_import_shim_test_notpe.bin";
  {
    std::ofstream o(tmp, std::ios::binary | std::ios::trunc);
    o << "this is definitely not a portable executable";
  }
  std::string err;
  std::vector<shim::MissingImport> out;
  Check(!shim::ListImports(tmp, &out, &err), "non-PE input is rejected");
  Check(!err.empty(), "non-PE rejection sets an error string");

  err.clear();
  out.clear();
  Check(!shim::ListImports(std::filesystem::temp_directory_path() / "no-such-file-98765.dll",
                           &out, &err),
        "missing file is rejected");
  Check(!err.empty(), "missing-file rejection sets an error string");

  std::error_code ec;
  std::filesystem::remove(tmp, ec);
}

// (4) Round trip: rewrite a real PE so a symbol it genuinely imports is redirected
// to a different DLL, then re-parse and confirm the redirect landed and the rest of
// the table survived intact. Uses this test's own module as the subject, so the
// rewrite path is exercised without shipping a deliberately-broken fixture.
void TestRewriteRoundTrip() {
  const std::filesystem::path self = SelfPath();
  if (self.empty()) return;

  std::string err;
  std::vector<shim::MissingImport> all;
  if (!shim::ListImports(self, &all, &err) || all.empty()) {
    Check(false, "round trip needs at least one named import");
    return;
  }

  // Redirect a single real import. The choice is arbitrary; what matters is that
  // it sits inside an existing descriptor's thunk range, forcing a genuine split.
  const shim::MissingImport picked = all[all.size() / 2];
  std::vector<shim::MissingImport> redirect{picked};

  const std::filesystem::path out =
      std::filesystem::temp_directory_path() / "cs2_pe_import_shim_test_rewrite.bin";
  const std::string fake_dll = "cs2_roundtrip_probe.dll";
  Check(shim::WriteShimmedCopy(self, redirect, fake_dll, out, &err),
        ("WriteShimmedCopy: " + err).c_str());

  std::vector<shim::MissingImport> after;
  err.clear();
  Check(shim::ListImports(out, &after, &err), ("re-parse rewritten PE: " + err).c_str());

  // The rewritten table must still describe exactly the same set of symbols —
  // the IAT slots are addressed by baked-in code, so nothing may be dropped.
  Check(after.size() == all.size(), "rewrite preserves the total import count");

  bool redirected = false;
  bool others_intact = true;
  for (const auto& imp : after) {
    if (imp.symbol == picked.symbol && imp.dll == fake_dll) {
      redirected = true;
    } else if (imp.symbol == picked.symbol && imp.dll == picked.dll) {
      others_intact = false;  // still bound to the original dependency
    }
  }
  Check(redirected, "the selected symbol now resolves from the shim DLL");
  Check(others_intact, "the selected symbol no longer resolves from its original DLL");

  // Every other symbol must keep its original owning DLL.
  size_t unchanged = 0;
  for (const auto& a : all) {
    if (a.symbol == picked.symbol) continue;
    for (const auto& b : after) {
      if (a.symbol == b.symbol && a.dll == b.dll) {
        ++unchanged;
        break;
      }
    }
  }
  Check(unchanged == all.size() - 1, "all other imports keep their original DLL");

  std::error_code ec;
  std::filesystem::remove(out, ec);
}

}  // namespace

int main() {
  TestAllowList();
  TestParsesRealModule();
  TestRejectsNonPe();
  TestRewriteRoundTrip();

  if (g_failures != 0) {
    std::fprintf(stderr, "pe_import_shim_test: %d check(s) failed\n", g_failures);
    return 1;
  }
  std::printf("pe_import_shim_test: all checks passed\n");
  return 0;
}

#else  // !_WIN32

int main() {
  std::printf("pe_import_shim_test: skipped (Windows-only feature)\n");
  return 0;
}

#endif  // defined(_WIN32)
