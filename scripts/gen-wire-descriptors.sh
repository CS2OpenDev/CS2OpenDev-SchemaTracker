#!/usr/bin/env bash
# Generate data/wire_descriptors.pb — the SDK-sourced wire-protocol FileDescriptorSet that the
# proto-descriptor extractor MERGES into every build's protos.descriptorset.
#
# WHY THIS EXISTS: CS2's engine wire-message protos (netmessages, usermessages, gameevents, te,
# clientmessages, cs_gameevents, cstrike15_usermessages, networkbasetypes) are NOT embedded as
# serialized FileDescriptorProtos in any shipped binary (proven: an all-182-binary, both-platform
# scan recovers 33 descriptors and none of these). They are the exact message families
# network_messages.json / demo_messages.json bind wire IDs to, so without them those join tables
# reference message types that live nowhere in the artifact set (3/191 resolve). The definitions DO
# exist, as source, in the pinned per-build hl2sdk submodule (the same first-party SDK the walker
# already links against). This script compiles them to descriptors so the extractor can carry them.
#
# The output carries ONLY those 8 wire files. Their imports (networkbasetypes aside) —
# source2_steam_stats, valveextensions, network_connection, cstrike15_gcmessages,
# google/protobuf/descriptor — are ALREADY recovered from the binaries per build and stay the
# canonical (binary-derived) copies; the extractor merges a wire file only when the binaries did not
# already provide it, so the binary-derived set always wins. Reference-quality (no source_code_info,
# same as every reconstructed descriptor); it makes the wire-ID→type join resolvable, it is not a
# per-byte-exact re-derivation of Valve's build.
#
# Regenerate after bumping the hl2sdk submodule pin. Deterministic for a fixed (protoc, hl2sdk pin).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HS="$REPO_ROOT/walker/third_party/hl2sdk"
OUT="$REPO_ROOT/data/wire_descriptors.pb"

if [ ! -d "$HS/common" ]; then
  echo "error: hl2sdk submodule not initialized at $HS (run: git submodule update --init)" >&2
  exit 1
fi
command -v protoc >/dev/null 2>&1 || { echo "error: protoc not on PATH" >&2; exit 1; }

# The 8 wire files to carry. networkbasetypes is included because netmessages/usermessages/te/
# gameevents/cs_gameevents/cstrike15_usermessages all reference its shared types and the binaries
# do not embed it either.
WIRE_FILES=(
  netmessages.proto
  usermessages.proto
  cstrike15_usermessages.proto
  clientmessages.proto
  gameevents.proto
  cs_gameevents.proto
  te.proto
  networkbasetypes.proto
)

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# Compile the wire files + their full transitive import closure (so protoc can resolve every type),
# emitting a self-contained FileDescriptorSet. Include paths cover every hl2sdk proto directory the
# closure spans.
protoc \
  -I "$HS/common" \
  -I "$HS/game/shared" \
  -I "$HS/game/shared/cs" \
  -I "$HS/game/shared/cstrike15" \
  -I "$HS/game/shared/econ" \
  -I "$HS/gcsdk" \
  -I "$HS/networksystem" \
  --include_imports \
  --descriptor_set_out="$TMP/full.pb" \
  "${WIRE_FILES[@]}"

# Strip the closure down to EXACTLY the 8 wire files (the deps ride in each build's binary-derived
# set), sorted by name for deterministic bytes.
HL2SDK_PIN="$(git -C "$HS" rev-parse HEAD 2>/dev/null || echo unknown)"
python "$REPO_ROOT/scripts/strip_wire_descriptors.py" "$TMP/full.pb" "$OUT" "${WIRE_FILES[@]}"

echo "wrote $OUT (hl2sdk pin $HL2SDK_PIN, protoc $(protoc --version))"
