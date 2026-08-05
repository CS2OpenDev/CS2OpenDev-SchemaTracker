#!/usr/bin/env bash
# Apply GCC-compatibility SYNTAX shims to the CHECKED-OUT hl2sdk submodule working tree.
#
# Some older cs2-branch headers (e.g. the 2024-02 pin 00644551) declare a private copy
# ctor with the injected-class-name PLUS template args, e.g. in public/tier0/threadtools.h:
#     CAutoLockT<MUTEX_TYPE>( const CAutoLockT<MUTEX_TYPE> & );
# MSVC accepts this; strict GCC rejects it ("expected ')' before 'const'"). Valve fixed it
# upstream in later headers by dropping the redundant <T> from the ctor NAME:
#     CAutoLockT( const CAutoLockT<MUTEX_TYPE> & );
# That is a SEMANTICALLY IDENTICAL change — the constructor is unchanged, so the compiled
# struct layout / behavior and thus the layout signature are unaffected; it only lets
# strict GCC parse the declaration.
#
# We cannot edit the pinned submodule permanently (clean-room), so we apply the same
# syntax-only fix to the WORKING TREE at build time; the era-build scripts' `git checkout -f`
# restore reverts it afterwards. This is the same spirit as the -Dstricmp=strcasecmp / bare
# LINUX hl2sdk GCC shims already applied in walker/CMakeLists.txt — a build-input GCC-compat
# fix, not a functional change. No-op on GCC-clean headers (modern pins) and on Windows/MSVC.
# Idempotent (re-running changes nothing once patched).
#
# Usage: patch-hl2sdk-gcc-compat.sh <hl2sdk-root>
set -euo pipefail
SDK="${1:?usage: patch-hl2sdk-gcc-compat.sh <hl2sdk-root>}"
TT="$SDK/public/tier0/threadtools.h"
[[ -f "$TT" ]] || exit 0

# Drop the redundant template-args from an injected-class-name copy-ctor declarator:
#   `<indent>Foo<T>( const Foo<T> & );`  ->  `<indent>Foo( const Foo<T> & );`
# Anchored to a line that STARTS (after indent) with `Ident<Ident>( const` so it only ever
# touches a ctor declarator, never a return-type usage (`Foo<T> &operator=(...)`).
before="$(grep -cE '^[[:space:]]*[A-Za-z_]+<[A-Za-z_]+>[[:space:]]*\([[:space:]]*const' "$TT" || true)"
if [[ "$before" -gt 0 ]]; then
  sed -i -E 's/^([[:space:]]*)([A-Za-z_]+)<([A-Za-z_]+)>([[:space:]]*\([[:space:]]*const)/\1\2\4/' "$TT"
  echo "patch-hl2sdk-gcc-compat: fixed $before ill-formed injected-class-name ctor decl(s) in threadtools.h"
fi
