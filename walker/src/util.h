// Small header-only walker utilities shared across walk/loader/boot TUs.
//
// HL2SDK-FREE by design: this header pulls in ONLY <string>/<string_view> so it
// can be included from the deliberately HL2SDK-free TUs (loader.cpp) as well as
// the HL2SDK-touching walks. Keep it that way — do NOT add any HL2SDK include.
//
// NOTE: schema_walk.cpp has its own Str() with an `era` overload and must NOT
// include this header (it would clash with the overload set).
#pragma once

#include <cstddef>
#include <string>
#include <string_view>

namespace cs2_schema_walker {

// Null-safe C-string to std::string (null -> empty). Consolidated here so the
// per-era copies cannot drift and produce differing output bytes.
inline std::string Str(const char* p) { return p ? std::string(p) : std::string(); }

// ASCII case-insensitive equality between a string view and a NUL-terminated
// C-string. Manual 'A'..'Z' lowering (NOT std::tolower) so the result is
// locale-independent and byte-identical to the two hand-rolled copies it
// replaces (loader.cpp module match, engine_boot.cpp FindLoaded).
inline bool EqCi(std::string_view a, const char* b) {
  const std::size_t bn = std::char_traits<char>::length(b);
  if (a.size() != bn) return false;
  for (std::size_t i = 0; i < a.size(); ++i) {
    char ca = a[i], cb = b[i];
    if (ca >= 'A' && ca <= 'Z') ca = static_cast<char>(ca - 'A' + 'a');
    if (cb >= 'A' && cb <= 'Z') cb = static_cast<char>(cb - 'A' + 'a');
    if (ca != cb) return false;
  }
  return true;
}

}  // namespace cs2_schema_walker
