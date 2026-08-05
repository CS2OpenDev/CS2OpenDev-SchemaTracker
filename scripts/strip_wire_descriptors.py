#!/usr/bin/env python3
# Strip a protoc --include_imports FileDescriptorSet down to EXACTLY the named wire files,
# sorted by name, and write the deterministic result. Used by scripts/gen-wire-descriptors.sh.
#
# The full set protoc emits carries the wire files PLUS their whole import closure (the GC/steam
# deps). We keep only the wire files: their deps are recovered from each build's binaries and stay
# the canonical binary-derived copies, and the extractor merges a wire file only when the binaries
# did not already supply it.
import sys
from google.protobuf import descriptor_pb2


def main(argv):
    if len(argv) < 4:
        print("usage: strip_wire_descriptors.py <in.pb> <out.pb> <keep.proto> [<keep.proto> ...]",
              file=sys.stderr)
        return 2
    in_path, out_path, keep = argv[1], argv[2], set(argv[3:])

    full = descriptor_pb2.FileDescriptorSet()
    with open(in_path, "rb") as f:
        full.ParseFromString(f.read())

    by_name = {fd.name: fd for fd in full.file}
    missing = sorted(keep - by_name.keys())
    if missing:
        print(f"error: compiled set is missing expected wire files: {missing}", file=sys.stderr)
        return 1

    out = descriptor_pb2.FileDescriptorSet()
    for name in sorted(keep):
        out.file.append(by_name[name])

    with open(out_path, "wb") as f:
        f.write(out.SerializeToString())
    print(f"kept {len(out.file)} wire file(s): {sorted(keep)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
