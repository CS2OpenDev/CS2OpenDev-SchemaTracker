// inventory eras[] <-> kKnownLayoutSignatures consistency test.
//
// This is the ctest home of what used to be scripts/check-era-pins-consistency.py
// (a brittle regex-scrape of layout_probe.cpp). The data is 100% walker-owned, so
// a ctest is the robust home: it links the REAL allow-list directly via the
// KnownLayoutSignatures() accessor (no source regex) and runs automatically in
// `ctest` (and thus in the per-era build harness's ctest gate + CI's walker build).
//
// The former walker/era-pins.json has been consolidated INTO the inventory
// (data/cs2-assets-inventory.json, path injected via INVENTORY_JSON_PATH). The
// inventory's top-level `eras[]` array holds every era: 11 kind:"compile-pin"
// entries (each with a per-platform `layoutSignatures` object) followed by 2
// kind:"runtime-variant" entries (which have `variantSignature`/`ridesCompilePin`,
// NO layoutSignatures — those ride a compile pin and are gated by a separate runtime
// allow-list, not kKnownLayoutSignatures).
//
// It asserts, against the inventory `eras[]` slice:
//   1. SET EQUALITY: the set of compile-pin PER-PLATFORM `layoutSignatures` values
//      (across both windows-x86_64 + linux-x86_64 keys, all compile-pin eras)
//      exactly equals the set of KnownLayoutSignatures() — no drift in either
//      direction.
//   2. Each signature embeds its era's `hl2sdkSha` (i.e. ".../<hl2sdkSha>/v1/...").
//   3. No duplicate signature across eras/platforms.
//   4. The `eras` array was found and is non-empty.
//   (The old era-pins.json `schemaVersion == 1` check is DROPPED — the inventory has
//    no schemaVersion; it carries `_meta` instead.)
//
// FALSE-MATCH HAZARD (why we scope to the eras[] slice): the platform keys
// "windows-x86_64" / "linux-x86_64" ALSO appear under every builds[].binaries entry
// (376 builds!), where their values are content/binary GID strings like
// "5502194087696430282". A naive whole-file scan would ingest ~752 garbage
// "signatures" and blow up the set-equality. So we first bracket-scope the scan to
// the top-level `eras[]` array (string-aware `[`/`]` depth counting from the `[`
// after the `"eras"` key to its matching `]`), then run the hl2sdkSha<->signature
// interleave scan ONLY over that slice. Within eras[], those platform keys appear
// ONLY inside compile-pin `layoutSignatures`, so the slice yields exactly the 22
// compile-pin signatures (11 eras x 2 platforms) and excludes both the builds[] GIDs
// and the 2 runtime-variant signatures.
//
// PER-PLATFORM: each compile-pin era carries a `layoutSignatures` object keyed by
// platform (windows-x86_64 + linux-x86_64). The flat kKnownLayoutSignatures allow-list
// holds BOTH platforms' signatures (a windows walker and a linux walker each compute
// their own ABI-specific fingerprint), so set-equality still holds.
//
// Mirrors the old script's exit-1-on-drift semantics: returns non-zero on the
// first class of failure, with a diff on stderr.
//
// No GoogleTest dependency (matches the walker's no-extra-deps test policy).
#include "layout_probe.h"

#include <cstdio>
#include <fstream>
#include <set>
#include <sstream>
#include <string>
#include <vector>

#ifndef INVENTORY_JSON_PATH
#error "INVENTORY_JSON_PATH must be defined by CMake (path to data/cs2-assets-inventory.json)"
#endif

namespace {

int g_failures = 0;

void Fail(const std::string& what) {
  std::fprintf(stderr, "FAIL: %s\n", what.c_str());
  ++g_failures;
}

// Read the whole file into a string. Returns false (and sets *err) on open
// failure: a missing/unreadable inventory is a hard failure, not a silent pass.
bool ReadFile(const char* path, std::string* out, std::string* err) {
  std::ifstream f(path, std::ios::binary);
  if (!f) {
    *err = std::string("cannot open inventory at ") + path;
    return false;
  }
  std::ostringstream ss;
  ss << f.rdbuf();
  *out = ss.str();
  return true;
}

// Extract the substring of `text` that is the VALUE of the top-level `"eras"` array:
// everything from the `[` that follows the `"eras"` key up to and including its
// matching `]`. Matching is found by string-aware `[`/`]` depth counting (quoted
// strings, with backslash escapes, are skipped so a `[`/`]` inside a value can't
// throw off the balance). Returns false (and sets *err) if the key or a balanced
// close bracket is not found. Scoping the later scan to this slice is what keeps the
// builds[] platform-keyed GIDs out of the signature set (see the file header).
bool ExtractErasSlice(const std::string& text, std::string* slice, std::string* err) {
  const std::string needle = "\"eras\"";
  size_t k = text.find(needle);
  if (k == std::string::npos) {
    *err = "inventory: top-level \"eras\" key not found";
    return false;
  }
  size_t colon = text.find(':', k + needle.size());
  if (colon == std::string::npos) {
    *err = "inventory: no ':' after \"eras\" key";
    return false;
  }
  size_t open = text.find('[', colon + 1);
  if (open == std::string::npos) {
    *err = "inventory: no '[' opening the eras array";
    return false;
  }

  bool in_string = false;
  int depth = 0;
  for (size_t i = open; i < text.size(); ++i) {
    char c = text[i];
    if (in_string) {
      if (c == '\\') {
        ++i;  // skip the escaped char
        continue;
      }
      if (c == '"') in_string = false;
      continue;
    }
    if (c == '"') {
      in_string = true;
      continue;
    }
    if (c == '[') {
      ++depth;
    } else if (c == ']') {
      --depth;
      if (depth == 0) {
        // Inclusive slice [open .. i].
        *slice = text.substr(open, i - open + 1);
        return true;
      }
    }
  }
  *err = "inventory: unbalanced eras[] array (no matching ']')";
  return false;
}

// Extract the double-quoted string value that follows the FIRST occurrence of
// `"<key>"` at-or-after `from`. Returns true and advances *from past the value;
// false if the key is not found. Dependency-free; sufficient for this flat,
// machine-generated manifest (no escaped quotes inside the values we read).
bool NextStringValue(const std::string& text, const std::string& key,
                     size_t* from, std::string* value, size_t* key_pos) {
  const std::string needle = "\"" + key + "\"";
  size_t search = *from;
  for (;;) {
    size_t k = text.find(needle, search);
    if (k == std::string::npos) return false;
    // Find the ':' after the key.
    size_t colon = text.find(':', k + needle.size());
    if (colon == std::string::npos) return false;
    // The value must be a STRING: the first non-whitespace char after the colon
    // is a double-quote. This skips occurrences whose value is an OBJECT — notably
    // classBands.windows-x86_64 / .linux-x86_64 ({ "min":…, "max":… }), which
    // share the platform key names with layoutSignatures but hold band objects,
    // not signature strings. Without this guard the scan would misread the band
    // object's first sub-key ("min") as a bogus signature value.
    size_t v = colon + 1;
    while (v < text.size() &&
           (text[v] == ' ' || text[v] == '\t' || text[v] == '\n' || text[v] == '\r')) {
      ++v;
    }
    if (v >= text.size()) return false;
    if (text[v] != '"') {
      // Not a string value — skip this occurrence, look for the next.
      search = k + needle.size();
      continue;
    }
    size_t open = v;
    size_t close = text.find('"', open + 1);
    if (close == std::string::npos) return false;
    *value = text.substr(open + 1, close - (open + 1));
    *key_pos = k;
    *from = close + 1;
    return true;
  }
}

}  // namespace

int main() {
  using namespace cs2_schema_walker;

  std::string file_text;
  std::string err;
  if (!ReadFile(INVENTORY_JSON_PATH, &file_text, &err)) {
    std::fprintf(stderr, "FAIL: %s\n", err.c_str());
    return 1;
  }

  // Scope EVERYTHING below to the top-level eras[] slice. Without this, the
  // windows-x86_64 / linux-x86_64 keys under builds[].binaries (GID strings) would be
  // scooped up as bogus signatures (see the FALSE-MATCH HAZARD note in the header).
  std::string text;  // the eras[] slice; all scans/offsets below are relative to it.
  if (!ExtractErasSlice(file_text, &text, &err)) {
    std::fprintf(stderr, "FAIL: %s\n", err.c_str());
    return 1;
  }

  // Walk every per-platform layout signature in document order WITHIN the eras[] slice.
  // Each compile-pin era object lists hl2sdkSha BEFORE its layoutSignatures object, and
  // the platform keys (windows-x86_64 / linux-x86_64) hold the signature strings — so
  // the hl2sdkSha paired with a given signature is the most-recent hl2sdkSha seen before
  // it. runtime-variant entries carry neither key, so they contribute nothing here.
  std::set<std::string> manifest_sigs;
  std::vector<std::string> manifest_sigs_ordered;
  {
    size_t cursor = 0;
    std::string last_sha;
    size_t last_sha_pos = std::string::npos;

    // The signature-bearing keys inside a layoutSignatures object. These platform strings
    // appear as JSON KEYS only within compile-pin layoutSignatures inside this slice, so
    // scanning for the double-quoted key is unambiguous.
    auto next_sig = [&](size_t from, std::string* val, size_t* pos, std::string* key) -> bool {
      // Find whichever platform key comes first at-or-after `from`.
      bool found = false;
      for (const char* k : {"windows-x86_64", "linux-x86_64"}) {
        size_t probe = from;
        std::string v;
        size_t p = std::string::npos;
        if (NextStringValue(text, k, &probe, &v, &p) &&
            (!found || p < *pos)) {
          *val = v;
          *pos = p;
          *key = k;
          found = true;
        }
      }
      return found;
    };

    // Interleave hl2sdkSha and the platform signature keys by document position.
    for (;;) {
      size_t probe = cursor;
      std::string sha_val;
      size_t sha_pos = std::string::npos;
      bool have_sha = NextStringValue(text, "hl2sdkSha", &probe, &sha_val, &sha_pos);

      std::string sig_val;
      size_t sig_pos = std::string::npos;
      std::string sig_key;
      bool have_sig = next_sig(cursor, &sig_val, &sig_pos, &sig_key);

      if (!have_sig && !have_sha) break;

      // Whichever key comes first in the document, consume that one.
      if (have_sha && (!have_sig || sha_pos < sig_pos)) {
        last_sha = sha_val;
        last_sha_pos = sha_pos;
        cursor = sha_pos + 1;  // advance past this sha key; next scan finds the value/next key.
        continue;
      }

      // A platform signature is next; advance the cursor past it.
      cursor = sig_pos + 1;

      // 2. embedded-pin check: the paired hl2sdkSha must appear in the signature
      //    as ".../<sha>/v1/".
      if (last_sha.empty() || last_sha_pos == std::string::npos ||
          last_sha_pos > sig_pos) {
        Fail("inventory eras[]: layoutSignatures." + sig_key +
             " without a preceding hl2sdkSha: " + sig_val);
      } else {
        const std::string embedded = "hl2sdk-cs2/" + last_sha + "/v1/";
        if (sig_val.find(embedded) == std::string::npos) {
          Fail("inventory eras[]: hl2sdkSha " + last_sha +
               " not embedded in its layoutSignatures." + sig_key + " " + sig_val);
        }
      }

      // 3. duplicate check.
      if (!manifest_sigs.insert(sig_val).second) {
        Fail("inventory eras[]: duplicate layout signature " + sig_val);
      }
      manifest_sigs_ordered.push_back(sig_val);
    }
  }

  // 4. The eras[] slice must have yielded at least one compile-pin signature.
  if (manifest_sigs.empty()) {
    Fail("inventory eras[]: no compile-pin layoutSignatures entries found");
  }

  // Build the allow-list set from the REAL compiled-in array.
  std::set<std::string> probe_sigs;
  for (const char* s : KnownLayoutSignatures()) {
    if (s != nullptr) probe_sigs.insert(s);
  }

  // 1. SET EQUALITY (both directions).
  for (const auto& s : manifest_sigs) {
    if (probe_sigs.find(s) == probe_sigs.end()) {
      Fail("DRIFT: in inventory eras[] but NOT in kKnownLayoutSignatures: " + s);
    }
  }
  for (const auto& s : probe_sigs) {
    if (manifest_sigs.find(s) == manifest_sigs.end()) {
      Fail("DRIFT: in kKnownLayoutSignatures but NOT in inventory eras[]: " + s);
    }
  }

  if (g_failures == 0) {
    std::printf(
        "era_pins_consistency_test: OK — inventory eras[] and "
        "kKnownLayoutSignatures agree (%zu era signatures).\n",
        manifest_sigs.size());
    return 0;
  }
  std::fprintf(stderr, "era_pins_consistency_test: %d check(s) failed\n",
               g_failures);
  return 1;
}
