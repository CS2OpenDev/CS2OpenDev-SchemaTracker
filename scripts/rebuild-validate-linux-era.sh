#!/usr/bin/env bash
# Rebuild an already-bootstrapped linux era walker (after its signature was tentatively
# registered in layout_probe.cpp + the inventory eras[]) and validate its walk vs the committed
# windows artifact. Keeps the submodule at the pin for the (fast, incremental) rebuild, then
# restores it. If validation fails, REVERT the tentative registration edits (this script does
# not touch git — it only builds + reports).
#
# Usage: rebuild-validate-linux-era.sh <hl2sdk-pin-sha> <build-id>
set -uo pipefail

PIN="${1:?usage: rebuild-validate-linux-era.sh <pin> <build-id>}"
BUILD="${2:?usage: rebuild-validate-linux-era.sh <pin> <build-id>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
SDK="$REPO/walker/third_party/hl2sdk"
BUILD_ROOT="${WBUILD_ERA_ROOT:-$HOME/wbuild-era}"
BUILD_DIR="$BUILD_ROOT/$PIN"

fail() { echo "ERROR: $*" >&2; exit 1; }

[[ -d "$BUILD_DIR" ]] || fail "no bootstrap build dir at $BUILD_DIR (run bootstrap-linux-era.sh first)"
CANONICAL_PIN="$(git -C "$REPO" ls-tree HEAD walker/third_party/hl2sdk | awk '{print $3}')"

git -C "$SDK" config core.autocrlf false
git -C "$SDK" config core.eol lf
restore() {
  echo "==> restoring submodule to $CANONICAL_PIN"
  git -C "$SDK" checkout -f "$CANONICAL_PIN" >/dev/null 2>&1 || true
  git -C "$REPO" checkout -- walker/src/netmsg_table.generated.inc >/dev/null 2>&1 || true
  git -C "$SDK" rev-parse HEAD
}
trap restore EXIT

echo "==> git checkout -f ${PIN:0:8}"
git -C "$SDK" checkout -f "$PIN" >/dev/null 2>&1
git -C "$SDK" checkout -f "$PIN" -- . >/dev/null 2>&1
[[ "$(git -C "$SDK" rev-parse HEAD)" == "$PIN" ]] || fail "checkout mismatch"

echo "==> hl2sdk GCC-compat syntax shims"
bash "$SCRIPT_DIR/patch-hl2sdk-gcc-compat.sh" "$SDK"

echo "==> regen netmsg table"
python3 "$REPO/walker/tools/gen_netmsg_table.py" >/dev/null

echo "==> incremental rebuild (layout_probe.cpp changed)"
cmake --build "$BUILD_DIR" -j"$(nproc)" 2>&1 | tail -6

echo "==> ctest era-pins-consistency (allow-list <-> inventory eras[] lockstep)"
ctest --test-dir "$BUILD_DIR" -R era-pins-consistency --output-on-failure || fail "era-pins-consistency FAILED — allow-list/inventory eras[] drift"

echo "==> validate walk vs committed windows (record-count parity)"
# Locate the freshly-built era walker and the rep build's linux binaries.
BIN="${CS2_BINARIES_ROOT:-$HOME/cs2-binaries}/$BUILD/linux-x86_64"
[[ -d "$BIN" ]] || fail "no linux binaries at $BIN"
EXE="$(find "$BUILD_DIR" -type f -name "cs2_schema_walker_$PIN" 2>/dev/null | head -n1)"
[[ -z "$EXE" ]] && EXE="$(find "$BUILD_DIR" -type f -name 'cs2_schema_walker' 2>/dev/null | head -n1)"
[[ -n "$EXE" ]] || fail "no bootstrapped exe under $BUILD_DIR"
echo "exe=$EXE  sig=$("$EXE" --print-signature | head -n1)"

# Run the era walker over the rep build -> raw WalkerOutput protobuf. The walker dlopens the CS2
# .so's, so LD_LIBRARY_PATH points at the build's two CS2 bin dirs.
export LD_LIBRARY_PATH="$BIN/game/bin/linuxsteamrt64:$BIN/game/csgo/bin/linuxsteamrt64"
OUT="/tmp/val_${PIN}_${BUILD}.pb"; ERR="/tmp/val_${PIN}_${BUILD}.err"
echo "==> walking (timeout 240s) ..."
if ! timeout 240 "$EXE" walk --binaries "$BIN" --platform linux-x86_64 --out "$OUT" 2>"$ERR"; then
  tail -8 "$ERR"; fail "walk failed"
fi
[[ -f "$OUT" ]] || fail "walk produced no output"

# The record-count parity comparison (classes/enums/convars/commands/engine_constants vs the
# committed windows-x86_64 artifact, with the windows-only dev-command tolerance) lives in the host
# now — verify-era-parity decodes the WalkerOutput and compares with typed protobuf/JSON parsing.
resolve_host() {
  for cand in "${CS2_HOST_DLL:-}" "${HOST_DLL:-}"; do
    if [[ -n "$cand" && -f "$cand" ]]; then HOST=(dotnet "$cand"); return; fi
  done
  dotnet build "$REPO/host/src/Cs2SchemaTracker.Host" -c Release -p:SelfContained=false -p:PublishSingleFile=false -p:UseAppHost=false -v q --nologo >&2 || fail "host build failed (needed for verify-era-parity)"
  local dll="$REPO/host/artifacts/bin/Cs2SchemaTracker.Host/release/cs2-schema-tracker.dll"
  [[ -f "$dll" ]] || fail "host dll not found: $dll"
  HOST=(dotnet "$dll")
}
resolve_host
"${HOST[@]}" verify-era-parity --walk "$OUT" --build "$BUILD" --artifacts "$REPO/artifacts"
rc=$?
rm -f "$OUT" "$ERR"
exit $rc
