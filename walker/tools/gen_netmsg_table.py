#!/usr/bin/env python3
"""Generate the network-message id->type table from the PINNED hl2sdk .protos.

WHY (approach B): the live INetworkMessages registry is empty in the headless
walk (populating it needs ~the whole engine — see netmsg_walk.cpp),
but the id->proto-type mapping is encoded VERBATIM in the pinned net-message .proto
enums (NET_Messages / SVC_Messages / CLC_Messages / Bidirectional_Messages /
EBaseUserMessages / ECstrike15UserMessages / game-event / temp-entity id enums). Each
enum value (name=id) maps by a per-family naming convention to a `message C...` type
defined in the same .proto set. This script parses those .protos as TEXT, derives the
type per value, VERIFIES the type exists as a real `message`, and emits a generated C++
table (netmsg_table.generated.inc) the walker emits as NetworkMessagesWalk.

This is clean-room (the .protos are the pinned, allowed hl2sdk input) and DERIVED (never
hand-transcribed). It is PIN-STATIC: the table reflects the hl2sdk pin's net protocol,
applied to every build walked with that pin. Regenerate on a pin change:
    python walker/tools/gen_netmsg_table.py
A wrong/again-drifted convention degrades to a LOGGED miss (the value is dropped, not
emitted as a fabricated type) — so the emitted table only ever contains verified types.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SDK = os.path.normpath(os.path.join(HERE, "..", "third_party", "hl2sdk"))
OUT = os.path.normpath(os.path.join(HERE, "..", "src", "netmsg_table.generated.inc"))

# The net-message .proto files that carry id enums + their message definitions.
PROTO_FILES = [
    "common/networkbasetypes.proto",
    "common/netmessages.proto",
    "common/connectionless_netmessages.proto",
    "game/shared/usermessages.proto",
    "game/shared/cstrike15/cstrike15_usermessages.proto",
    "game/shared/gameevents.proto",
    "game/shared/cs/cs_gameevents.proto",
    "game/shared/te.proto",
    "game/shared/clientmessages.proto",
]

# Per id-enum family: (channel name, value-name prefix to strip, [type-prefixes]).
# For each enum value the CANDIDATE types are (type_prefix + suffix) for each listed
# type-prefix, in order; the FIRST one that names a real `message` wins. Some families
# (notably user messages) use two conventions, hence the lists. A value that matches
# none (a sentinel like *_MAX_BASE / svc_dummy / *_Legacy with no message) is dropped.
FAMILIES = {
    "NET_Messages":              ("NetMessages",    "net_",   ["CNETMsg_"]),
    "CLC_Messages":              ("ClcMessages",    "clc_",   ["CCLCMsg_"]),
    "SVC_Messages":              ("SvcMessages",    "svc_",   ["CSVCMsg_"]),
    "SVC_Messages_LowFrequency": ("SvcMessages",    "svc_",   ["CSVCMsg_"]),
    "Bidirectional_Messages":    ("Bidirectional",  "bi_",    ["CBidirMsg_", "CBidirectional_Messages_"]),
    "EBaseUserMessages":         ("UserMessages",   "UM_",    ["CUserMessage", "CUserMsg_"]),
    "ECstrike15UserMessages":    ("UserMessages",   "CS_UM_", ["CCSUsrMsg_", "CCSUsrMsg"]),
    "EBaseClientMessages":       ("ClientMessages", "CM_",    ["CClientMsg_", "CCLCMsg_"]),
}


def read_protos():
    texts = {}
    for rel in PROTO_FILES:
        p = os.path.join(SDK, rel)
        if os.path.exists(p):
            with open(p, "r", encoding="utf-8", errors="replace") as fh:
                texts[rel] = fh.read()
    return texts


def all_message_names(texts):
    names = set()
    for t in texts.values():
        for m in re.finditer(r"^\s*message\s+([A-Za-z_][A-Za-z0-9_]*)", t, re.M):
            names.add(m.group(1))
    return names


def enum_values(text, enum_name):
    """Return [(value_name, id), ...] for `enum <enum_name> { ... }`."""
    m = re.search(r"enum\s+" + re.escape(enum_name) + r"\s*\{(.*?)\}", text, re.S)
    if not m:
        return []
    out = []
    for vm in re.finditer(r"([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(-?\d+)\s*;", m.group(1)):
        out.append((vm.group(1), int(vm.group(2))))
    return out


def candidates(value_name, fam):
    _chan, vprefix, tprefixes = fam
    suffix = value_name[len(vprefix):] if value_name.startswith(vprefix) else value_name
    cands = [tp + suffix for tp in tprefixes]
    return [c for i, c in enumerate(cands) if c not in cands[:i]]


def main():
    texts = read_protos()
    if not texts:
        sys.stderr.write("gen_netmsg_table: no .protos found under %s\n" % SDK)
        return 1
    msgs = all_message_names(texts)

    rows = []          # (channel, id, type)
    misses = []        # (enum, value_name, id)
    blob = "\n".join(texts.values())
    for enum_name, fam in FAMILIES.items():
        chan = fam[0]
        seen_enum = False
        for rel, t in texts.items():
            vals = enum_values(t, enum_name)
            if vals:
                seen_enum = True
            for vname, vid in vals:
                t_name = None
                for c in candidates(vname, fam):
                    if c in msgs:
                        t_name = c
                        break
                if t_name is None:
                    misses.append((enum_name, vname, vid))
                    continue
                rows.append((chan, vid, t_name))
        if not seen_enum:
            sys.stderr.write("gen_netmsg_table: NOTE enum %s not found\n" % enum_name)

    # Dedup + deterministic sort (channel, id, type).
    rows = sorted(set(rows), key=lambda r: (r[0], r[1], r[2]))

    with open(OUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write("// GENERATED by walker/tools/gen_netmsg_table.py — DO NOT EDIT.\n")
        fh.write("// network-message id->type table, derived from the pinned hl2sdk\n")
        fh.write("// net-message .proto enums (PIN-STATIC). Regenerate on a pin change.\n")
        fh.write("// Each entry was verified to name a real `message` in the .proto set.\n")
        fh.write("struct NetMsgTableEntry { const char* channel; int id; const char* type; };\n")
        fh.write("static const NetMsgTableEntry kNetMsgTable[] = {\n")
        for chan, vid, t_name in rows:
            fh.write('  {"%s", %d, "%s"},\n' % (chan, vid, t_name))
        fh.write("};\n")

    sys.stderr.write("gen_netmsg_table: emitted %d entries to %s\n" % (len(rows), OUT))
    if misses:
        sys.stderr.write("gen_netmsg_table: %d UNMATCHED enum values (dropped):\n" % len(misses))
        for enum_name, vname, vid in misses[:40]:
            sys.stderr.write("  %s.%s=%d\n" % (enum_name, vname, vid))
    return 0


if __name__ == "__main__":
    sys.exit(main())
