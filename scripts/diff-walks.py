#!/usr/bin/env python3
"""diff-walks.py -- name the field that differs between two walker_output.pb files.

The commit-path determinism gate byte-compares two walks and aborts at exit 76 with the
sizes and the first differing byte offset. That offset is enough to prove a divergence and
useless for fixing one: an 8 MB WalkerOutput offset does not say which class, which field,
or which accessor leaked. This decodes both walks against schemas/walker_output.proto and
reports the differing field PATHS, e.g.

    entity_schema.classes[1847].metadata[3].value

which is what you need to decide whether the fix is another FilterKv3Nondeterminism entry
(walker/src/schema_walk.cpp) or a genuine walker read bug.

Pair it with the walks the gate now preserves into
cache/determinism/<build>-<platform>/walk-{1,2}.pb.

USAGE
    python scripts/diff-walks.py WALK1.pb WALK2.pb [--schemas DIR] [--max N] [--full]

    Exit codes:
      0  -- the two walks are byte-identical.
      1  -- they differ (the field paths are printed).
      2  -- usage/environment error (protoc missing, schemas unreadable, undecodable input).

REQUIREMENTS
    protoc on PATH and the `protobuf` Python runtime. Both are present on the release
    runners; locally, `brew install protobuf` plus `pip install protobuf`.
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import subprocess
import sys
import tempfile
from typing import Any, List, Tuple

VALUE_CLIP = 160


def build_bindings(schemas_dir: str, out_dir: str):
    """Compile schemas/*.proto to Python and import WalkerOutput."""
    protos = [f for f in sorted(os.listdir(schemas_dir)) if f.endswith(".proto")]
    if "walker_output.proto" not in protos:
        raise SystemExit(f"diff-walks: no walker_output.proto in {schemas_dir}")
    cmd = ["protoc", f"--proto_path={schemas_dir}", f"--python_out={out_dir}"] + protos
    try:
        subprocess.run(cmd, check=True, capture_output=True, text=True)
    except FileNotFoundError:
        raise SystemExit("diff-walks: protoc not found on PATH.")
    except subprocess.CalledProcessError as exc:
        raise SystemExit(f"diff-walks: protoc failed:\n{exc.stderr}")

    sys.path.insert(0, out_dir)
    spec = importlib.util.spec_from_file_location(
        "walker_output_pb2", os.path.join(out_dir, "walker_output_pb2.py"))
    mod = importlib.util.module_from_spec(spec)
    sys.modules["walker_output_pb2"] = mod
    spec.loader.exec_module(mod)
    return mod.WalkerOutput


def clip(v: Any) -> str:
    s = repr(v)
    return s if len(s) <= VALUE_CLIP else s[:VALUE_CLIP] + f"... ({len(s)} chars)"


def is_map(field) -> bool:
    return bool(field.message_type) and field.message_type.GetOptions().map_entry


def is_repeated(field) -> bool:
    # protobuf >= 5 exposes is_repeated; the upb FieldDescriptor dropped .label entirely.
    try:
        return field.is_repeated
    except AttributeError:
        return field.label == field.LABEL_REPEATED


def diff(a, b, path: str, out: List[Tuple[str, str, str]], limit: int) -> None:
    """Append (path, left, right) for every leaf that differs. Depth-first, field order."""
    for f in a.DESCRIPTOR.fields:
        if len(out) >= limit:
            return
        here = f"{path}.{f.name}" if path else f.name

        if is_map(f):
            ma, mb = getattr(a, f.name), getattr(b, f.name)
            for k in sorted(set(ma) - set(mb), key=repr):
                out.append((f"{here}[{k!r}]", clip(ma[k]), "<absent>"))
            for k in sorted(set(mb) - set(ma), key=repr):
                out.append((f"{here}[{k!r}]", "<absent>", clip(mb[k])))
            for k in sorted(set(ma) & set(mb), key=repr):
                if f.message_type.fields_by_name["value"].message_type:
                    diff(ma[k], mb[k], f"{here}[{k!r}]", out, limit)
                elif ma[k] != mb[k]:
                    out.append((f"{here}[{k!r}]", clip(ma[k]), clip(mb[k])))
            continue

        if is_repeated(f):
            ra, rb = getattr(a, f.name), getattr(b, f.name)
            if len(ra) != len(rb):
                out.append((here, f"<{len(ra)} entries>", f"<{len(rb)} entries>"))
            for i in range(min(len(ra), len(rb))):
                if len(out) >= limit:
                    return
                if f.message_type:
                    diff(ra[i], rb[i], f"{here}[{i}]", out, limit)
                elif ra[i] != rb[i]:
                    out.append((f"{here}[{i}]", clip(ra[i]), clip(rb[i])))
            continue

        if f.message_type:
            ha, hb = a.HasField(f.name), b.HasField(f.name)
            if ha != hb:
                out.append((here, "<set>" if ha else "<unset>", "<set>" if hb else "<unset>"))
            elif ha:
                diff(getattr(a, f.name), getattr(b, f.name), here, out, limit)
            continue

        va, vb = getattr(a, f.name), getattr(b, f.name)
        if va != vb:
            out.append((here, clip(va), clip(vb)))


def byte_report(p1: str, p2: str) -> Tuple[bytes, bytes]:
    b1, b2 = open(p1, "rb").read(), open(p2, "rb").read()
    print(f"walk 1: {p1}  ({len(b1):,} bytes)")
    print(f"walk 2: {p2}  ({len(b2):,} bytes)")
    if b1 == b2:
        print("\nbyte-identical.")
        return b1, b2
    first = next((i for i in range(min(len(b1), len(b2))) if b1[i] != b2[i]),
                 min(len(b1), len(b2)))
    delta = len(b2) - len(b1)
    print(f"size delta: {delta:+,}    first differing byte: {first:,}")
    # Fields 1-3 of WalkerOutput are three short strings; a diff inside their ~31 bytes
    # means the big entity_schema submessage's length varint moved.
    if first < 40:
        print("  (that offset is the top-of-message region: a submessage length prefix moved)")
    return b1, b2


def main(argv: List[str]) -> int:
    ap = argparse.ArgumentParser(
        prog="diff-walks.py",
        description="Report the differing field paths between two walker_output.pb files.")
    ap.add_argument("walk1")
    ap.add_argument("walk2")
    ap.add_argument("--schemas", default=None,
                    help="schemas/ directory (default: <repo>/schemas next to this script).")
    ap.add_argument("--max", type=int, default=50,
                    help="stop after N differing leaves (default: 50).")
    ap.add_argument("--full", action="store_true",
                    help="no cap; report every differing leaf.")
    args = ap.parse_args(argv)

    for p in (args.walk1, args.walk2):
        if not os.path.isfile(p):
            print(f"diff-walks: no such file: {p}", file=sys.stderr)
            return 2

    schemas = args.schemas or os.path.join(
        os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "schemas")
    if not os.path.isdir(schemas):
        print(f"diff-walks: schemas dir not found: {schemas}", file=sys.stderr)
        return 2

    b1, b2 = byte_report(args.walk1, args.walk2)
    if b1 == b2:
        return 0

    with tempfile.TemporaryDirectory(prefix="diff-walks-") as tmp:
        WalkerOutput = build_bindings(schemas, tmp)
        w1, w2 = WalkerOutput(), WalkerOutput()
        try:
            w1.ParseFromString(b1)
            w2.ParseFromString(b2)
        except Exception as exc:
            print(f"diff-walks: could not parse a walk as WalkerOutput: {exc}", file=sys.stderr)
            return 2

        limit = 10 ** 9 if args.full else args.max
        out: List[Tuple[str, str, str]] = []
        diff(w1, w2, "", out, limit)

    print(f"\n{len(out)} differing leaf field(s){' (capped)' if len(out) >= limit else ''}:\n")
    for path, left, right in out:
        print(f"  {path}")
        print(f"      walk 1: {left}")
        print(f"      walk 2: {right}")

    if not out:
        print("  none -- the bytes differ but every decoded field matches.")
        print("  Unknown fields or non-canonical encoding; compare with `protoc --decode_raw`.")

    roots = sorted({p.split("[")[0].split(".")[0] for p, _, _ in out})
    leaves = sorted({p.split("[")[-1].split(".")[-1] for p, _, _ in out})
    if roots:
        print(f"\ntop-level field(s) involved: {', '.join(roots)}")
        print(f"leaf name(s) involved:      {', '.join(leaves)}")
        print("\nIf a leaf is a KV3 defaults value, the fix is usually another regex in\n"
              "FilterKv3Nondeterminism (walker/src/schema_walk.cpp) naming the offending\n"
              "KV3 property. Check the value for an address-shaped or nan float first.")
    return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
