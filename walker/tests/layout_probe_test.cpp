// Layout-probe unit test.
//
// Asserts, WITHOUT needing a real CS2 binary:
//   1. ComputeLayoutSignature() is deterministic: two calls are equal.
//   2. The signature has the expected, stable shape.
//   3. Gate integrity: an ARBITRARY/unverified signature is never "known",
//      so the walk fails loud on any layout that has not been explicitly
//      registered by a human against a real CS2 binary. (The build-time
//      signature on a host whose pinned HL2SDK matches a registered build WILL
//      be known — that is the intended outcome once a layout is validated — so
//      this test no longer asserts the allow-list is empty.)
//
// No GoogleTest dependency (matches the walker's no-extra-deps test policy):
// returns non-zero on the first failed assertion.
#include "layout_probe.h"
#include "schema_record_layout_v1.h"  // prelayout::kVariant0 / kV1 (struct-hash goldens)

#include <cstdio>
#include <string>

namespace {

int g_failures = 0;

void Check(bool cond, const char* what) {
  if (!cond) {
    std::fprintf(stderr, "FAIL: %s\n", what);
    ++g_failures;
  }
}

}  // namespace

int main() {
  using namespace cs2_schema_walker;

  // PLATFORM-GATED runtime-layout goldens. The 2023 game DLL's CUtlTSHash/pool container is
  // laid out by Valve's per-OS toolchain, so the linux (g++) 2023 container offsets differ
  // from windows (MSVC) — see tshash_compat.h #if __linux__ + schema_record_layout_v1.h kV1.
  // Those constants feed ComputeRuntimeLayoutSignature[For](), so the V0 runtime sig, the kV1
  // struct sig, and the kVariant0 struct sig are ALL platform-specific. The RECORD layout is
  // identical cross-OS, so ComputeLayoutSignature (compile-time schema fingerprint) is NOT
  // gated here — only these three runtime-layout goldens. All six values are the
  // allow-listed / documented references for their platform (kKnownRuntimeLayoutVariants).
#if defined(__linux__)
  const char* const kExpectV0Runtime = "re-2023lt/v1/73c8566f1779a803";
  const char* const kExpectV1Struct = "re-cs2rel/v1/dea336a9965346ad";
  const char* const kExpectV0Struct = "re-cs2rel/v1/60482cee8dddf8ca";
#else
  const char* const kExpectV0Runtime = "re-2023lt/v1/69a8cb68432fca4f";
  const char* const kExpectV1Struct = "re-cs2rel/v1/55202ac2d6e7bfb9";
  const char* const kExpectV0Struct = "re-cs2rel/v1/d3ebecefee6511e6";
#endif

  const std::string a = ComputeLayoutSignature();
  const std::string b = ComputeLayoutSignature();

  // 1. Determinism.
  Check(a == b, "ComputeLayoutSignature is deterministic across calls");

  // 2. Shape: "hl2sdk-cs2/<sha>/v1/<16 hex>".
  Check(a.rfind("hl2sdk-cs2/", 0) == 0, "signature starts with hl2sdk-cs2/");
  Check(a.find("/v1/") != std::string::npos, "signature carries the /v1/ probe-version tag");
  Check(a.size() >= std::string("hl2sdk-cs2//v1/0000000000000000").size(),
        "signature is at least as long as the minimal well-formed form");
  // The fingerprint must be 16 lowercase hex digits at the very end.
  bool tail_hex = a.size() >= 16;
  if (tail_hex) {
    for (size_t i = a.size() - 16; i < a.size(); ++i) {
      char c = a[i];
      bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
      if (!hex) {
        tail_hex = false;
        break;
      }
    }
  }
  Check(tail_hex, "signature ends in 16 lowercase hex digits");

  // 3. Gate integrity: an arbitrary / never-registered signature must NOT
  //    be known, so the walk fails loud on any unverified layout. We do NOT
  //    assert anything about the build-time signature `a`: on a host whose
  //    pinned HL2SDK matches a registered CS2 build it is legitimately known.
  Check(!IsKnownLayout("hl2sdk-cs2/whatever/v1/deadbeefdeadbeef"),
        "an arbitrary signature is not known");
  Check(!IsKnownLayout("hl2sdk-cs2/0000000000000000000000000000000000000000/v1/0000000000000000"),
        "a zeroed signature is not known");

  // 4. Pre-2024 RUNTIME layout variant (second allow-list).
  const std::string r1 = ComputeRuntimeLayoutSignature();
  const std::string r2 = ComputeRuntimeLayoutSignature();
  // 4a. Determinism.
  Check(r1 == r2, "ComputeRuntimeLayoutSignature is deterministic across calls");
  // 4b. Shape: "re-2023lt/v1/<16 hex>".
  Check(r1.rfind("re-2023lt/v1/", 0) == 0, "runtime signature starts with re-2023lt/v1/");
  Check(r1.size() == std::string("re-2023lt/v1/0000000000000000").size(),
        "runtime signature is the expected fixed length");
  // 4c. The variant-0 (2023) runtime table's signature is the ALLOW-LISTED value. This
  //     is the direct byte-identity guard: if the derived offset table ever changes (or
  //     the transcribed hex is wrong), this fails loud at ctest time — BEFORE any
  //     extraction can fall through the N-way gate on a stale signature.
  // RE-TRANSCRIBED f200e8a9a8f1afbb -> 69a8cb68432fca4f: kClassSizeOff2023
  // 12 -> 24 (real m_nSize offset; the old +12 read inside the m_pszName pointer). The
  // size offset is folded into RuntimeLayoutFingerprint(), so the corrected V0 table
  // moves the fingerprint — the golden value is re-transcribed in lockstep with the
  // allow-list (layout_probe.cpp kKnownRuntimeLayoutVariants), keeping this byte-identity
  // guard green while the gate mechanism (fail-loud on unknown) is unchanged.
  Check(r1 == kExpectV0Runtime,
        "variant-0 runtime signature matches the transcribed/allow-listed value");
  Check(IsKnownRuntimeLayoutVariant(r1),
        "the variant-0 runtime signature is allow-listed (kKnownRuntimeLayoutVariants)");
  // 4d. Gate integrity for the runtime allow-list: an arbitrary runtime signature
  //     is never known (default-deny), so an underived pre-2024 variant fails loud.
  Check(!IsKnownRuntimeLayoutVariant("re-2023lt/v1/deadbeefdeadbeef"),
        "an arbitrary runtime signature is not a known variant");
  Check(!IsKnownRuntimeLayoutVariant("re-cs2rel/v1/0000000000000000"),
        "the zero-placeholder V1 runtime variant is not allow-listed");

  // 5. Pre-2024 RUNTIME layout variant V1 (struct-hash, second gate).
  // 5a. The struct-hash function is deterministic.
  const std::string v1a =
      ComputeRuntimeLayoutSignatureFor(prelayout::kV1, "cs2rel");
  const std::string v1b =
      ComputeRuntimeLayoutSignatureFor(prelayout::kV1, "cs2rel");
  Check(v1a == v1b, "ComputeRuntimeLayoutSignatureFor is deterministic across calls");
  // 5b. Shape: "re-<tag>/v1/<16 hex>".
  Check(v1a.rfind("re-cs2rel/v1/", 0) == 0,
        "V1 struct signature starts with re-cs2rel/v1/");
  // 5c. GOLDEN: the derived V1 table (schema_record_layout_v1.h::kV1) hashes to the
  //     documented + allow-listed value. If the struct's field set/order changes, or the
  //     V1 real_base_shift is edited, this fails loud at ctest time BEFORE any V1 build can
  //     pass the N-way gate on a stale signature (the byte-identity guard for V1).
  Check(v1a == kExpectV1Struct,
        "kV1 struct signature matches the derived/allow-listed golden");
  Check(IsKnownRuntimeLayoutVariant(v1a),
        "the V1 struct signature is allow-listed (kKnownRuntimeLayoutVariants)");
  // 5d. The SAME struct-hash over the V0 table (kVariant0) reproduces the documented
  //     reference d3ebecefee6511e6 — and is DISTINCT from V1's, proving real_base_shift
  //     (the ONLY differing field) is folded into the hash (no V1<->V0 collision).
  const std::string v0struct =
      ComputeRuntimeLayoutSignatureFor(prelayout::kVariant0, "cs2rel");
  Check(v0struct == kExpectV0Struct,
        "kVariant0 struct signature matches the documented reference d3ebecefee6511e6");
  Check(v0struct != v1a,
        "V0 and V1 struct signatures differ (real_base_shift is in the hash)");
  // 5e. V0's ORIGINAL variant-0 fingerprint (re-2023lt) is UNCHANGED by the V1 wiring —
  //     the golden re-2023lt/v1/69a8cb68432fca4f is preserved (already asserted at 4c);
  //     re-assert here that the two prefixes are disjoint (no accidental cross-wiring).
  Check(r1.rfind("re-2023lt/", 0) == 0 && v1a.rfind("re-cs2rel/", 0) == 0,
        "V0 (re-2023lt) and V1 (re-cs2rel) signature prefixes are disjoint");

  if (g_failures == 0) {
    std::printf("layout_probe_test: all checks passed\nsignature=%s\nruntime=%s\n",
                a.c_str(), r1.c_str());
    return 0;
  }
  std::fprintf(stderr, "layout_probe_test: %d check(s) failed\n", g_failures);
  return 1;
}
