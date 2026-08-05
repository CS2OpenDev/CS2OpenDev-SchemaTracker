// Schema-system memory-layout probe.
//
// The probe computes a deterministic FINGERPRINT of the exact CSchemaSystem /
// CSchemaType / CSchemaClassInfo / SchemaClassFieldData_t / SchemaEnumInfoData_t
// / SchemaMetadataEntryData_t memory layout the walker was BUILT against (the
// pinned HL2SDK headers), combined with the pinned HL2SDK commit SHA. That
// fingerprint is the "layout signature" that gates extraction.
//
// Semantics (why a build-time fingerprint is the right thing):
//   The walker reads Valve's live structs by the offsets HL2SDK declares. If the
//   shipped DLL's real layout diverges from those offsets, the walk silently
//   corrupts — the worst failure mode for a verifiable dumper. The signature
//   therefore encodes "the layout I will dereference against". Two walker builds
//   that read identical offsets produce an identical signature; any HL2SDK bump
//   that moves a field the walker touches changes the signature.
//
// KNOWN-LAYOUTS set: the walker carries a static allow-list of signatures it has
// VALIDATED against a real CS2 binary (see kKnownLayoutSignatures in
// layout_probe.cpp). Until a build's signature has been validated and recorded
// there, it is rejected (`known=false`) and the walk fails loud. We never guess.
#pragma once

#include <cstdint>
#include <filesystem>
#include <optional>
#include <span>
#include <string>

namespace cs2_schema_walker {

struct LayoutProbeResult {
  // Stable string of the form
  //   `hl2sdk-cs2/<sdk_sha>/v1/<hex-fingerprint>`
  // The host treats this as opaque — it records it in provenance and checks for
  // an exact match in its own known-layouts notion. The walker's OWN allow-list
  // (kKnownLayoutSignatures) is what gates extraction here.
  std::string signature;

  // True iff `signature` is in the walker's validated allow-list.
  bool known = false;
};

// Compute the layout fingerprint the walker was built against. Pure function of
// the compiled-in HL2SDK struct layout + pinned SHA; does NOT touch the binaries
// dir or any live object. Deterministic: identical inputs -> identical output,
// byte for byte. Exposed for the layout-determinism unit test.
std::string ComputeLayoutSignature();

// Return true iff `signature` is in the walker's validated KNOWN-LAYOUTS set.
bool IsKnownLayout(const std::string& signature);

// The walker's validated KNOWN-LAYOUTS allow-list — the exact set of layout
// signatures IsKnownLayout() accepts, as nul-terminated string literals. This is
// the SINGLE source of truth for that allow-list: IsKnownLayout() iterates this
// span, and the era-pins-consistency test asserts set-equality against
// the inventory eras[] per-platform `layoutSignatures` values (both
// windows-x86_64 + linux-x86_64 keys) WITHOUT regex-scraping the .cpp source.
// The list is FLAT across platforms: a windows walker and a linux walker each
// compute their own ABI-specific fingerprint and check membership in this one
// set. Stable order (declaration order); deterministic.
std::span<const char* const> KnownLayoutSignatures();

// --- Pre-2024 RUNTIME LAYOUT VARIANTS (second allow-list) ---------------------
//
// The 112 CS2 builds in 2023-03-23 .. 2024-03-16 are BELOW the hl2sdk cs2-branch
// schema-header floor
// (2024-03-18, ab21c708) so NO hl2sdk pin declares their structs: every pre-2024
// era must ride a MODERN compile pin (b8dcaf14) and recover the schema by
// binary-derived RE — exactly like the 2023 baseline 10832117. Because they all
// ride b8dcaf14 their ComputeLayoutSignature() is IDENTICAL to the `current` era,
// so they CANNOT be discriminated by the compile-time allow-list above (adding them
// there would duplicate the `current` signature and break the era-pins-consistency
// ctest). They are instead discriminated AT RUNTIME by which record/pool offset
// table validates the live DLL (the N-way generalization of
// DetectSchemaIs2023Layout), and gated by THIS separate allow-list.
//
// A "runtime layout variant signature" is a fingerprint of the DERIVED RUNTIME
// OFFSET TABLE (not the compiled struct layout), of the form
//   `re-<tag>/v1/<fnv16>`
// It is INTENTIONALLY not the `hl2sdk-cs2/...` compile-time form, so the two
// allow-lists never collide and the consistency ctest (which keys on the
// compile-time form) ignores these.
//
// DEFAULT-DENY: the array starts EMPTY — until the build phase derives and
// validates a variant and adds its exact runtime signature here,
// IsKnownRuntimeLayoutVariant() returns false for everything and the N-way probe
// must fail loud + print the observed signature to stderr. Never allow-list an
// unvalidated layout.
std::span<const char* const> KnownRuntimeLayoutVariants();

// Compute the RUNTIME-layout signature of the variant-0 (2023) derived offset table —
// a deterministic fingerprint (form `re-2023lt/v1/<fnv16>`) of the k...Off2023 record /
// pool / bucket constants in schema_record_layout_2023.h + tshash_compat.h. This is the
// runtime analogue of ComputeLayoutSignature(): it hashes the offsets the k2023 reader
// DEREFERENCES, not the compiled struct layout. Pure function of the compiled-in offset
// table; does NOT touch any live object. Deterministic. The N-way runtime probe
// (DetectSchemaVariant) computes this for a build whose records validate under the
// variant-0 table and checks it against IsKnownRuntimeLayoutVariant().
std::string ComputeRuntimeLayoutSignature();

// Compute the RUNTIME-layout signature of an ARBITRARY pre-2024 offset TABLE (form
// `re-<tag>/v1/<fnv16>`). Unlike ComputeRuntimeLayoutSignature() — which hashes the
// hard-pinned variant-0 named constants under the `re-2023lt` prefix and is kept
// UNTOUCHED so V0's golden re-2023lt/v1/69a8cb68432fca4f is preserved — this hashes the
// FULL Pre2024LayoutOffsets struct (all 35 fields, INCLUDING real_base_shift, which is the
// ONLY field distinguishing V1 from V0; a fingerprint omitting it would collide V1 onto
// V0). The N-way DetectSchemaVariant probe computes this for each derived non-V0 variant
// table (tag "cs2rel") and gates the result on IsKnownRuntimeLayoutVariant(). Pure function
// of the passed table; deterministic. The struct is forward-declared (only a
// reference is taken), so this header stays free of the heavy schema_record_layout_v1.h.
namespace prelayout {
struct Pre2024LayoutOffsets;
}
std::string ComputeRuntimeLayoutSignatureFor(const prelayout::Pre2024LayoutOffsets& table,
                                             const char* tag);

// Return true iff `signature` is in the validated pre-2024 runtime-variant set
// above. The N-way runtime layout probe (build-phase; not yet wired) uses this as
// its gate. Until a variant is registered this is always false (default-deny).
bool IsKnownRuntimeLayoutVariant(const std::string& signature);

// Probe the schema-system layout against the modules under `binaries_dir`.
// Validates the dir (fail-loud on a missing/empty dir) and returns the computed
// signature + its known/unknown status. An "unknown signature" is NOT a hard
// error here — it's a soft outcome (known=false) the caller surfaces via exit 75.
// Returns std::nullopt + populates *err only on a hard failure (dir missing, no
// modules present).
std::optional<LayoutProbeResult>
ProbeLayout(const std::filesystem::path& binaries_dir, std::string* err);

}  // namespace cs2_schema_walker
