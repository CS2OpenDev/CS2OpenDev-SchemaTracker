#!/usr/bin/env bash
# Check an existing vcpkg clone out at the commit walker/vcpkg.pin names, so classic-mode
# `vcpkg install` builds from a fixed ports tree instead of whatever the clone happened to be
# at. Usage: scripts/pin-vcpkg-ports.sh <vcpkg-root>   (CI passes $VCPKG_INSTALLATION_ROOT)
set -euo pipefail

root="${1:?usage: pin-vcpkg-ports.sh <vcpkg-root>}"
repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pin="$(grep -v '^[[:space:]]*#' "$repo/walker/vcpkg.pin" | tr -d '[:space:]' | head -c 40)"
[[ "$pin" =~ ^[0-9a-f]{40}$ ]] || { echo "walker/vcpkg.pin does not hold a 40-hex commit" >&2; exit 1; }
[[ -d "$root/.git" ]] || { echo "$root is not a git clone of vcpkg" >&2; exit 1; }

# Runner images may own the clone as a different user.
git config --global --add safe.directory "$root"

if ! git -C "$root" cat-file -e "$pin^{commit}" 2>/dev/null; then
  git -C "$root" fetch --depth=1 origin "$pin"
fi
git -C "$root" checkout --quiet --force --detach "$pin"
echo "vcpkg ports pinned at $(git -C "$root" rev-parse --short HEAD) ($root)"
