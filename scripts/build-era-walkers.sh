#!/usr/bin/env bash
# Build every per-era walker binary for linux-x86_64 (g++) into the native bundle
# layout natives/linux-x86_64/{era}.
#
# ONE command produces every era binary for this OS. For each compile-pin era in the
# consolidated inventory (data/cs2-assets-inventory.json, top-level eras[]) it: guards the
# submodule is initialized (fail-loud, never auto-inits), skips if already built+tested for
# this era+srcFingerprint (content-keyed resumability; --force overrides), checks out the
# era's hl2sdk pin in the single submodule working tree, regens the pin-static netmsg table,
# configures+builds a FRESH per-era build dir with -DWALKER_ERA_NAME=<era> (era-named exe, built with $ORIGIN
# RPATH so it loads the sibling libprotobuf.so), runs ctest, asserts the built exe's emitted
# layout signature equals the era's linux-x86_64 layoutSignature (build-time second gate),
# then copies the era-named exe + its runtime .so closure into natives/linux-x86_64/ (the
# shared protobuf/abseil runtime is deduped — every era exe in the dir loads the same
# sibling .so via $ORIGIN).
#
# The 2 runtime-variant eras produce NO binary of their own (they ride their compile-pin's
# walker at runtime), so only the 11 compile-pin eras build.
#
# After the loop (ALWAYS, including on failure via a trap) it restores the submodule to the
# canonical superproject gitlink pin and restores the working-tree netmsg_table.generated.inc,
# then asserts the submodule HEAD equals the gitlink SHA.
#
# Exit 0 iff every requested compile-pin era built + tested + signature-matched green.
#
# HEAVY BUILD. Operator/CI only — it can run for hours.
# produces linux-x86_64 binaries only. Use scripts/build-era-walkers.ps1 on Windows.
#
# Signature capture: the walker prints its COMPILE-TIME ComputeLayoutSignature() via
# `--print-signature` (binaries-free — needs no CS2 modules on disk). The build-time
# signature gate therefore ALWAYS runs (unconditional); there is no
# skip-when-no-binaries path. CS2_BINARIES_ROOT/--binaries-dir is retained only for the
# legacy probe-layout cross-check and is not required for the gate.
#
# Content-keyed resumability + walker-manifest.json
# ---------------------------------------------------------------------------------------
# RESUMABILITY KEY: the per-era skip used to compare the sidecar's recorded
# walkerGitSha against repo HEAD -- so an UNCOMMITTED walker source fix rebuilt ZERO
# binaries (HEAD hadn't moved), and HEAD churned on every unrelated artifacts commit even
# when nothing walker-relevant changed. The skip key is now srcFingerprint, the stable
# SHA256 content hash from walker/tools/src_fingerprint.py (hashes walker/src/**,
# walker/CMakeLists.txt, the netmsg-table generator, and schemas/*.proto -- see that
# script's docstring). walkerGitSha is still stamped into the sidecar/manifest for human
# provenance, it just no longer gates the skip.
#
# walker-manifest.json SCHEMA (natives/<platform>/walker-manifest.json, upserted after
# EVERY successful era install -- one property per era id):
#   {
#     "<eraId>": {
#       "gitSha":          "<repo HEAD sha at build time -- provenance only, NOT the skip key>",
#       "srcFingerprint":  "<walker/tools/src_fingerprint.py output -- the resumability skip key>",
#       "hl2sdkPin":       "<hl2sdk submodule sha this era was built against>",
#       "layoutSignature": "<--print-signature output, scoped to this platform>",
#       "binarySha256":    "<sha256 of the installed era exe>",
#       "builtUtc":        "<ISO-8601 UTC timestamp of this install>"
#     },
#     ...
#   }
# This manifest is the on-disk record the host's preflight identity gate cross-checks against
# each binary's live --version output (manifest = what the build scripts produced; binary =
# what is actually on disk today). It is read-modify-write per era so a script that dies
# partway through a multi-era run leaves every already-finished era durably recorded.
set -euo pipefail

# --- args -----------------------------------------------------------------------------
FORCE=0
ERA_FILTER=()
BINARIES_DIR="${CS2_BINARIES_ROOT:-}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
NATIVES_ROOT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --force) FORCE=1; shift ;;
    --era) ERA_FILTER+=("$2"); shift 2 ;;
    --binaries-dir) BINARIES_DIR="$2"; shift 2 ;;
    --natives-root) NATIVES_ROOT="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 1 ;;
  esac
done

OS='linux-x86_64'
SDK="$REPO/walker/third_party/hl2sdk"
INVENTORY="$REPO/data/cs2-assets-inventory.json"
GEN_NETMSG="$REPO/walker/tools/gen_netmsg_table.py"
# Per-era build scratch (fresh dir per era) — gitignored, NOT shipped. Under WSL, point
# WBUILD_ERA_ROOT at a WSL-native path (e.g. ~/wbuild-era) so CMake's many try-compile probes
# don't crawl on the /mnt/c 9p mount; only the final ELF is copied back to natives/ on /mnt/c.
SCRATCH_ROOT="${WBUILD_ERA_ROOT:-$REPO/walker/build-eras}"
# Where the shipped, era-named binaries + deduped runtime land.
[[ -n "$NATIVES_ROOT" ]] || NATIVES_ROOT="$REPO/natives"
OUT_DIR="$NATIVES_ROOT/$OS"

fail() { echo "ERROR: $*" >&2; exit 1; }

# Optional vcpkg toolchain for a PORTABLE bundle. When VCPKG_ROOT is set we build against
# vcpkg's DYNAMIC protobuf (triplet x64-linux-dynamic, mirroring the Windows x64-windows
# dynamic build) and bundle its .so closure into natives/ (loaded via the walker's $ORIGIN
# RPATH). Without VCPKG_ROOT the build falls back to system protobuf (apt) via
# find_package(Protobuf) — fine for a LOCAL run, but NOT portable (the apt .so lives in
# /usr/lib, so nothing gets bundled and the walker only runs where apt protobuf is installed).
VCPKG_TRIPLET="${VCPKG_TRIPLET:-x64-linux-dynamic}"
CMAKE_VCPKG_ARGS=()
VCPKG_LIB_DIR=""
if [[ -n "${VCPKG_ROOT:-}" ]]; then
  _tc="$VCPKG_ROOT/scripts/buildsystems/vcpkg.cmake"
  [[ -f "$_tc" ]] || fail "VCPKG_ROOT set but toolchain missing: $_tc"
  CMAKE_VCPKG_ARGS=( "-DCMAKE_TOOLCHAIN_FILE=$_tc" "-DVCPKG_TARGET_TRIPLET=$VCPKG_TRIPLET" )
  VCPKG_LIB_DIR="$VCPKG_ROOT/installed/$VCPKG_TRIPLET/lib"
  # The build-tree ELF is linked with RUNPATH=$ORIGIN (for the shipped layout), so it does NOT
  # find vcpkg's libprotobuf.so when run in place (ctest + the --print-signature gate below run
  # the BUILD-tree binary). Point LD_LIBRARY_PATH at the vcpkg lib dir so those in-place runs
  # work. The SHIPPED binary in natives/ ignores this — it loads the co-located .so via $ORIGIN
  # (validated by the post-install self-check in section 8).
  export LD_LIBRARY_PATH="$VCPKG_LIB_DIR${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  echo "==> vcpkg toolchain: $_tc (triplet $VCPKG_TRIPLET) — portable dynamic-protobuf bundle"
else
  echo "==> no VCPKG_ROOT — building against system (apt) protobuf; bundle will NOT be portable"
fi

# --- preflight ------------------------------------------------------------------------
[[ -f "$INVENTORY" ]] || fail "inventory not found: $INVENTORY"
[[ -f "$GEN_NETMSG" ]] || fail "netmsg generator not found: $GEN_NETMSG"

# guard: the submodule MUST be initialized. Do NOT auto-init (per build docs).
if [[ ! -d "$SDK/public" ]]; then
  fail $'hl2sdk submodule not initialized at '"$SDK"$'.\nRun this ONCE (large download), then re-run:\n  git submodule update --init walker/third_party/hl2sdk'
fi

CANONICAL_PIN="$(git -C "$REPO" ls-tree HEAD walker/third_party/hl2sdk | awk '{print $3}')"
[[ -n "$CANONICAL_PIN" ]] || fail "could not read canonical gitlink pin for the submodule"

WALKER_GIT_SHA="$(git -C "$REPO" rev-parse HEAD)"

# Content-keyed resumability key -- see the schema comment block above. Computed
# ONCE up front: same content -> same fingerprint regardless of which/how-many eras this
# invocation rebuilds. walker/tools/src_fingerprint.py is pure stdlib python3.
SRC_FINGERPRINT_TOOL="$REPO/walker/tools/src_fingerprint.py"
[[ -f "$SRC_FINGERPRINT_TOOL" ]] || fail "src-fingerprint tool not found: $SRC_FINGERPRINT_TOOL (required for the walker fingerprint)"
SRC_FINGERPRINT="$(python3 "$SRC_FINGERPRINT_TOOL")"
[[ "$SRC_FINGERPRINT" =~ ^[0-9a-f]{64}$ ]] || fail "src_fingerprint.py did not print a 64-hex digest (got '$SRC_FINGERPRINT')"

BUILD_DATE_UTC=""
if [[ -n "${SOURCE_DATE_EPOCH:-}" ]]; then
  BUILD_DATE_UTC="$(date -u -d "@${SOURCE_DATE_EPOCH}" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || true)"
fi

mkdir -p "$SCRATCH_ROOT" "$OUT_DIR"

# --- always-run restore (trap) --------------------------------------------------------
restore_submodule() {
  echo "==> restoring submodule to canonical pin $CANONICAL_PIN"
  # Force-checkout the canonical pin DIRECTLY in the submodule (not `submodule update
  # --checkout`, which does not reliably switch the pin under WSL git on the /mnt/c mount and
  # left the submodule stuck at the last-built era). -f discards the working tree; the SDK
  # never carries real edits (netmsg regen writes to the MAIN repo). core.autocrlf=false keeps
  # WSL git from seeing spurious CRLF.
  git -C "$SDK" config core.autocrlf false >/dev/null 2>&1 || true
  git -C "$SDK" checkout -f "$CANONICAL_PIN" >/dev/null 2>&1 || true
  git -C "$SDK" checkout -f "$CANONICAL_PIN" -- . >/dev/null 2>&1 || true
  git -C "$REPO" checkout -- walker/src/netmsg_table.generated.inc >/dev/null 2>&1 || true
  local head; head="$(git -C "$SDK" rev-parse HEAD)"
  if [[ "$head" != "$CANONICAL_PIN" ]]; then
    echo "ERROR: submodule NOT restored: HEAD=$head expected gitlink=$CANONICAL_PIN" >&2
    echo "       fix manually: git -C walker/third_party/hl2sdk checkout -f $CANONICAL_PIN" >&2
    exit 1
  fi
  echo "    submodule HEAD == gitlink ($CANONICAL_PIN) OK"
}
trap restore_submodule EXIT

# --- read compile-pin eras (era<TAB>sha<TAB>linux-signature) via the host `plan` command -------
# The host is the single source of truth for target selection (Cli/PlanCommand): it projects the
# inventory eras[] into per-platform tsv rows. This is the linux-x86_64 builder, so scope the
# layoutSignature to linux-x86_64 (empty when absent — those eras are SKIPPED below: nothing to
# gate the built exe on for linux). runtime-variant eras are excluded by `plan` (they ride a
# compile pin; no binary of their own).
resolve_host() {
  # Prefer a pre-published dll named by $CS2_HOST_DLL (CI publishes it once); else build Release once.
  if [[ -n "${CS2_HOST_DLL:-}" && -f "${CS2_HOST_DLL:-}" ]]; then
    HOST=(dotnet "$CS2_HOST_DLL"); return
  fi
  echo "==> building host (for plan target selection)..." >&2
  dotnet build "$REPO/host/src/Cs2SchemaTracker.Host" -c Release -p:SelfContained=false -p:PublishSingleFile=false -p:UseAppHost=false -v q --nologo >&2 \
    || fail "host build failed (needed for plan target selection)"
  local dll="$REPO/host/artifacts/bin/Cs2SchemaTracker.Host/release/cs2-schema-tracker.dll"
  [[ -f "$dll" ]] || fail "host dll not found after build: $dll"
  HOST=(dotnet "$dll")
}
resolve_host
mapfile -t ERA_ROWS < <("${HOST[@]}" plan --targets compile-pins --platform linux-x86_64 --format tsv --inventory "$INVENTORY")
[[ ${#ERA_ROWS[@]} -gt 0 ]] || fail "no compile-pin eras read from inventory"

BUILT=0; SKIPPED=0; GREEN=0

in_filter() {
  [[ ${#ERA_FILTER[@]} -eq 0 ]] && return 0
  local x; for x in "${ERA_FILTER[@]}"; do [[ "$x" == "$1" ]] && return 0; done
  return 1
}

# count requested eras that are buildable+gateable on linux (have a linux signature)
WANT=0
for row in "${ERA_ROWS[@]}"; do
  IFS=$'\t' read -r era sha sig <<<"$row"
  in_filter "$era" || continue
  [[ -z "$sig" ]] && continue
  WANT=$((WANT+1))
done

for row in "${ERA_ROWS[@]}"; do
  IFS=$'\t' read -r era pin sig <<<"$row"
  in_filter "$era" || continue
  short="${pin:0:8}"
  echo ""
  echo "================ era $era  pin $short ================"

  # without a registered linux-x86_64 signature there is nothing to gate the built exe
  # against, so this era's linux layout is not yet validated — skip it (never guess a layout).
  if [[ -z "$sig" ]]; then
    echo "    no layoutSignatures.linux-x86_64 registered for era $era -> skip (not yet validated on linux)."
    SKIPPED=$((SKIPPED+1))
    continue
  fi

  era_dir="$SCRATCH_ROOT/$era"
  build_dir="$era_dir/build"
  exe_name="$era"
  out_exe="$OUT_DIR/$exe_name"
  meta_path="$era_dir/$era.meta.json"

  # --- (2) resumability skip ----------------------------------------------------------
  # Content-keyed: gates on srcFingerprint, NOT walkerGitSha -- see the schema comment
  # block near the top of this script. A sidecar written before the fingerprint key
  # existed has no "srcFingerprint"; m.get(...) returns None for it, which never equals a
  # real 64-hex digest, so an old sidecar falls through to a rebuild that backfills it.
  if [[ $FORCE -eq 0 && -f "$out_exe" && -f "$meta_path" ]]; then
    if python3 - "$meta_path" "$pin" "$SRC_FINGERPRINT" <<'PY'
import json, sys
m = json.load(open(sys.argv[1]))
sys.exit(0 if (m.get("hl2sdkSha")==sys.argv[2] and m.get("srcFingerprint")==sys.argv[3] and m.get("ctest")=="passed") else 1)
PY
    then
      echo "    up-to-date (pin + srcFingerprint match, ctest passed) -> skip. Use --force to rebuild."
      SKIPPED=$((SKIPPED+1)); GREEN=$((GREEN+1))
      continue
    fi
  fi

  # --- (3) checkout the pin (fail loud on dirty submodule tree) ------------------------
  if [[ -n "$(git -C "$SDK" status --porcelain)" ]]; then
    fail "submodule working tree is dirty before checkout of $short; refusing. Clean it first."
  fi
  echo "==> git checkout $short"
  git -C "$SDK" checkout --quiet "$pin"
  head="$(git -C "$SDK" rev-parse HEAD)"
  [[ "$head" == "$pin" ]] || fail "submodule checkout mismatch: HEAD=$head expected=$pin"

  # --- (4a) hl2sdk GCC-compat syntax shims (older headers only; no-op on GCC-clean pins) --
  echo "==> applying hl2sdk GCC-compat syntax shims for pin $short"
  bash "$SCRIPT_DIR/patch-hl2sdk-gcc-compat.sh" "$SDK"

  # --- (4) per-pin netmsg table regen (pin-static; MUST run before cmake) --------------
  echo "==> regenerating netmsg table for pin $short"
  python3 "$GEN_NETMSG"

  # --- (5) FRESH per-era build dir configure + build (Release) -------------------------
  rm -rf "$build_dir"; mkdir -p "$build_dir"
  echo "==> cmake configure ($era)"
  cmake -S "$REPO/walker" -B "$build_dir" \
        -DCMAKE_BUILD_TYPE=Release \
        -DWALKER_ERA_NAME="$era" \
        "${CMAKE_VCPKG_ARGS[@]}"
  echo "==> cmake build ($era)"
  cmake --build "$build_dir"

  # locate the built exe (OUTPUT_NAME is era-named via -DWALKER_ERA_NAME)
  built="$(find "$build_dir" -type f -name "$exe_name" 2>/dev/null | head -n1)"
  [[ -n "$built" ]] || fail "could not find the built era-named exe '$exe_name' under $build_dir"

  # --- (6) ctest ----------------------------------------------------------------------
  echo "==> ctest ($era)"
  ctest --test-dir "$build_dir" --output-on-failure

  # --- (7) build-time signature second gate (UNCONDITIONAL) ---------------------
  # The built exe prints its COMPILE-TIME ComputeLayoutSignature() via `--print-signature`
  # — binaries-free, no CS2 modules required. Output is exactly the signature string + newline.
  echo "==> --print-signature assert ($era)"
  if ! got="$("$built" --print-signature | head -n1)"; then
    fail "--print-signature failed for era $era"
  fi
  [[ -n "$got" ]] || fail "--print-signature produced no signature for era $era"
  if [[ "$got" != "$sig" ]]; then
    fail $'signature mismatch for era '"$era"$'\n  expected: '"$sig"$'\n  got:      '"$got"
  fi
  echo "    signature OK: $got"

  # --- (8) install into natives/{platform}/ + runtime .so + resumability sidecar -------
  cp -f "$built" "$out_exe"
  # Bundle the runtime .so closure into the shared $OUT_DIR so every era exe finds it via
  # $ORIGIN. The closure is byte-identical across eras, so this dedups to ONE copy per .so.
  # -P preserves the SONAME symlink chain (libprotobuf.so -> .so.N -> .so.N.M), all landing
  # in the same dir so the relative links resolve. With vcpkg (portable) we copy the dynamic
  # protobuf+abseil closure from vcpkg's lib dir; without it we copy whatever sits beside the
  # built ELF (apt links /usr/lib, so this finds nothing -> non-portable bundle, as warned).
  shopt -s nullglob
  if [[ -n "$VCPKG_LIB_DIR" ]]; then
    [[ -d "$VCPKG_LIB_DIR" ]] || fail "vcpkg lib dir not found: $VCPKG_LIB_DIR (is protobuf:$VCPKG_TRIPLET installed?)"
    for lib in "$VCPKG_LIB_DIR"/*.so*; do cp -fP "$lib" "$OUT_DIR/"; done
  else
    for lib in "$(dirname "$built")"/*.so*; do cp -fP "$lib" "$OUT_DIR/"; done
  fi
  shopt -u nullglob

  # $ORIGIN portability self-check: run the INSTALLED binary with LD_LIBRARY_PATH CLEARED so it
  # must resolve libprotobuf.so from its own dir via $ORIGIN alone — proving the shipped natives/
  # layout is self-contained. Only meaningful in vcpkg/portable mode (apt builds link /usr/lib).
  if [[ -n "$VCPKG_LIB_DIR" ]]; then
    origin_sig="$(env -u LD_LIBRARY_PATH "$out_exe" --print-signature 2>/dev/null | head -n1 || true)"
    if [[ "$origin_sig" != "$sig" ]]; then
      fail "portability check FAILED for $era: installed $out_exe did not emit its signature with \$ORIGIN alone (got '${origin_sig:-<load error>}', expected '$sig'). Bundled libprotobuf.so / RPATH is not self-contained."
    fi
    echo "    \$ORIGIN portability OK (self-contained)"
  fi

  python3 - "$meta_path" "$era" "$pin" "$WALKER_GIT_SHA" "$SRC_FINGERPRINT" "$sig" "$OS" "$BUILD_DATE_UTC" <<'PY'
import json, sys
(_, out, era, sha, wsha, fprint, sig, os_, date) = sys.argv
m = {
  "era": era, "hl2sdkSha": sha, "walkerGitSha": wsha, "srcFingerprint": fprint,
  "layoutSignature": sig, "ctest": "passed", "os": os_,
}
if date:
    m["buildDateUtc"] = date
json.dump(m, open(out, "w"), indent=2, sort_keys=True)
open(out, "a").write("\n")
PY
  echo "    installed: $out_exe"

  # --- (9) walker-manifest.json upsert --------------------------------------------------
  # Schema documented in the comment block near the top of this script. Written per era
  # (not batched after the loop) so a mid-run failure still leaves finished eras recorded.
  # Read-modify-write so an entry already on disk from a prior invocation (e.g. a run that
  # covered a different --era subset) is preserved, not clobbered.
  manifest_path="$OUT_DIR/walker-manifest.json"
  python3 - "$manifest_path" "$era" "$pin" "$WALKER_GIT_SHA" "$SRC_FINGERPRINT" "$sig" "$out_exe" "$BUILD_DATE_UTC" <<'PY'
import hashlib, json, os, sys
from datetime import datetime, timezone
(_, manifest_path, era, pin, wsha, fprint, sig, exe, date) = sys.argv
manifest = {}
if os.path.exists(manifest_path):
    with open(manifest_path) as f:
        manifest = json.load(f)
h = hashlib.sha256()
with open(exe, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""):
        h.update(chunk)
built_utc = date if date else datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
manifest[era] = {
    "gitSha": wsha,
    "srcFingerprint": fprint,
    "hl2sdkPin": pin,
    "layoutSignature": sig,
    "binarySha256": h.hexdigest(),
    "builtUtc": built_utc,
}
with open(manifest_path, "w") as f:
    json.dump(manifest, f, indent=2, sort_keys=True)
    f.write("\n")
PY
  echo "    walker-manifest.json updated: era=$era ($manifest_path)"

  BUILT=$((BUILT+1)); GREEN=$((GREEN+1))
done

echo ""
echo "built=$BUILT skipped=$SKIPPED (into $OUT_DIR)"

# Exit 0 iff every requested (linux-gateable) compile-pin era is green. The EXIT trap
# restores the submodule.
if [[ $GREEN -lt $WANT ]]; then
  echo "ERROR: not every requested era is green ($GREEN/$WANT)." >&2
  exit 1
fi
exit 0
