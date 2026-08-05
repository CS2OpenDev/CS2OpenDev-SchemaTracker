#!/usr/bin/env bash
# Bootstrap a NEW linux-x86_64 walker era: build the per-pin walker and CAPTURE its
# compile-time layout signature BEFORE it is registered in the inventory eras[].
#
# Why this exists separately from scripts/build-era-walkers.sh: that script SKIPS any era
# with no registered layoutSignatures.linux-x86_64 (it has nothing to gate the built exe
# against — "never guess"). But you cannot register a signature you have not yet
# observed. This script is the pre-step: it builds the pin's walker (default OUTPUT_NAME —
# the era name is not known until it is registered), runs ctest, and prints
# `--print-signature` with NO signature gate, so the operator can (1) read the sig, (2) walk
# a representative build and validate byte-vs-windows, (3) register the sig, after which
# build-era-walkers.sh maintains the era normally.
#
# It does NOT install into natives/ — it is a throwaway bring-up build. Output exe path is
# printed at the end.
#
# HEAVY BUILD — long-running, and it dies with its parent process, so start it detached
# (nohup/tmux/screen, or a CI job) rather than from a shell you are going to close.
# Builds into WSL-native fs (~/wbuild-era/<pin>) for speed; source stays on /mnt/c.
# Serialize: only one pin can be checked out in the single submodule tree at a time.
#
# Usage: bootstrap-linux-era.sh <hl2sdk-pin-sha>
set -euo pipefail

PIN="${1:?usage: bootstrap-linux-era.sh <hl2sdk-pin-sha>}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
SDK="$REPO/walker/third_party/hl2sdk"
GEN_NETMSG="$REPO/walker/tools/gen_netmsg_table.py"
BUILD_ROOT="${WBUILD_ERA_ROOT:-$HOME/wbuild-era}"
BUILD_DIR="$BUILD_ROOT/$PIN"

fail() { echo "ERROR: $*" >&2; exit 1; }

[[ -d "$SDK/public" ]] || fail "hl2sdk submodule not initialized at $SDK"
[[ -f "$GEN_NETMSG" ]] || fail "netmsg generator not found: $GEN_NETMSG"

CANONICAL_PIN="$(git -C "$REPO" ls-tree HEAD walker/third_party/hl2sdk | awk '{print $3}')"
[[ -n "$CANONICAL_PIN" ]] || fail "could not read canonical gitlink pin"

# Cross-OS line endings: the submodule working tree was checked out by Windows git (autocrlf)
# but we build under WSL git, which then reports thousands of spurious CRLF "modifications".
# Pin this repo's config to LF so WSL git and the working tree agree; the third-party SDK tree
# never carries real uncommitted edits (the netmsg generator writes to the MAIN repo, not here),
# so a FORCE checkout below is safe and the spurious dirt is not user work.
git -C "$SDK" config core.autocrlf false
git -C "$SDK" config core.eol lf

restore_submodule() {
  echo "==> restoring submodule to canonical pin $CANONICAL_PIN"
  git -C "$SDK" checkout -f "$CANONICAL_PIN" >/dev/null 2>&1 || true
  git -C "$REPO" checkout -- walker/src/netmsg_table.generated.inc >/dev/null 2>&1 || true
  local head; head="$(git -C "$SDK" rev-parse HEAD)"
  [[ "$head" == "$CANONICAL_PIN" ]] || { echo "ERROR: submodule NOT restored (HEAD=$head)"; exit 1; }
  echo "    submodule HEAD == gitlink OK"
}
trap restore_submodule EXIT

echo "==> git checkout -f ${PIN:0:8} in submodule"
git -C "$SDK" checkout -f "$PIN"
git -C "$SDK" checkout -f "$PIN" -- .   # force working tree to the pin's content (discard CRLF noise)
head="$(git -C "$SDK" rev-parse HEAD)"
[[ "$head" == "$PIN" ]] || fail "checkout mismatch: HEAD=$head expected=$PIN"

echo "==> applying hl2sdk GCC-compat syntax shims (older headers only; no-op on GCC-clean pins)"
bash "$SCRIPT_DIR/patch-hl2sdk-gcc-compat.sh" "$SDK"

echo "==> regenerating netmsg table for ${PIN:0:8}"
python3 "$GEN_NETMSG"

echo "==> fresh cmake configure ($BUILD_DIR)"
rm -rf "$BUILD_DIR"; mkdir -p "$BUILD_DIR"
cmake -S "$REPO/walker" -B "$BUILD_DIR" -DCMAKE_BUILD_TYPE=Release

echo "==> cmake build (-j$(nproc))"
cmake --build "$BUILD_DIR" -j"$(nproc)"

echo "==> ctest"
ctest --test-dir "$BUILD_DIR" --output-on-failure || echo "WARN: ctest reported failures (continuing — bring-up)"

built="$(find "$BUILD_DIR" -type f -name 'cs2_schema_walker' 2>/dev/null | head -n1)"
[[ -n "$built" ]] || fail "could not find built walker exe under $BUILD_DIR"

echo ""
echo "================ BOOTSTRAP RESULT ================"
echo "pin:        $PIN"
echo "exe:        $built"
printf 'signature:  '
"$built" --print-signature | head -n1
echo "================================================="
echo "NEXT: walk a representative build with this exe, validate byte-vs-windows, then register"
echo "      the signature in data/cs2-assets-inventory.json eras[].layoutSignatures[linux-x86_64] +"
echo "      walker/src/layout_probe.cpp kKnownLayoutSignatures."
