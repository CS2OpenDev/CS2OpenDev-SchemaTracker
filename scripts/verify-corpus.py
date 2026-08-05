#!/usr/bin/env python3
"""verify-corpus.py -- READ-ONLY corpus verification for the CS2 schema-extraction pipeline.

A standalone gate you can point at `artifacts/` at any time -- to prove the corpus is dirty,
to watch it get cleaner, or to assert it is green -- WITHOUT depending on the .NET host, a
live CS2 binary cache, or any network access. Deliberately a separate, small, stdlib-only
tool: it runs anywhere `python` runs (including CI), and its correctness can be reasoned
about independently of the extractor it audits.

This script NEVER modifies anything. It only reads:
  - artifacts/<buildId>/{omissions.json, <platform>/{provenance.json, entity_schema.json}}
  - data/cs2-assets-inventory.json  (the source of truth for builds[] -> era, and eras[] ->
    kv3ClassDefaults / label / etc.)
It writes only to stdout/stderr, plus an optional machine-readable report to the path given by
`--json <path>` (never to a path inside the repo unless the caller explicitly names one there).

USAGE
    python scripts/verify-corpus.py [--json REPORT.json] [--root REPO_ROOT] [--max-print N]

    Exit codes:
      0  -- clean: no violations in any check family (informational findings may still exist).
      1  -- one or more violations found (see stdout summary / --json for the full list).
      2  -- usage/environment error (e.g. data/cs2-assets-inventory.json or artifacts/ missing).

THE FOUR CHECK FAMILIES
  1. provenance-uniformity   -- every artifacts/*/{platform}/provenance.json should agree on
                                 tool.gitCommit (and tool.walkerSrcFingerprint, for artifacts
                                 new enough to carry it) within a platform; outliers vs. the
                                 per-platform majority are reported.
  2. kv3-lints                -- over entity_schema.json in KV3-ON eras (era resolved per build
                                 via builds[].era -> eras[]; kv3ClassDefaults absent OR true means
                                 KV3-ON, explicit false means Gated -- see `era_kv3_state()`):
                                   (a) no bare `-nan`/`nan` token anywhere inside a
                                       MGetKV3ClassDefaults value (walker read uninitialized
                                       memory and the non-determinism filter did not catch it);
                                   (b) none of the 14 denylisted fields (see `_DENYLIST_FIELDS`)
                                       carry a raw same-line value -- they must read
                                       `<HIDDEN FOR DIFF>` (mirrors
                                       walker/src/schema_walk.cpp::FilterKv3Nondeterminism's own
                                       per-field regex *exactly*, including which fields require a
                                       trailing comma to match -- so this check also surfaces
                                       walker-regex gaps: a denylisted field that is the LAST
                                       property of its object has no trailing comma and silently
                                       escapes 4 of the 14 filters even in a fully "fixed" build);
                                   (c) at least one `<HIDDEN FOR DIFF>` marker exists SOMEWHERE in
                                       the file whenever the file has any non-empty
                                       MGetKV3ClassDefaults value at all (absence => pre-fix
                                       walker output that never ran the filter). Skipped for files
                                       with zero MGetKV3ClassDefaults metadata entries at all --
                                       some early "KV3-ON-by-absence" eras (2023 runtime-variant
                                       eras) predate the KV3-class-defaults engine feature
                                       entirely and legitimately carry none; that is not a walker
                                       defect and must not be reported as one.
                                 Gated eras: every MGetKV3ClassDefaults value must be the empty
                                 string; any non-empty value is a violation.
  3. structure                -- every inventory build with binaries listed for a platform must
                                 have that platform's artifact dir containing entity_schema.json
                                 and provenance.json; `artifacts/*/*.staging-*` and
                                 `artifacts/*/*.old-*` orphan dirs (interrupted extract runs) are
                                 violations.
  4. cross-platform-presence   -- INFORMATIONAL ONLY (never contributes to the exit code): builds
                                 where exactly one of {windows-x86_64, linux-x86_64} has a
                                 committed artifact dir.

--json REPORT SCHEMA (top-level object)
  {
    "schemaVersion": "1.0",
    "generatedUtc": "<ISO-8601 Z timestamp>",
    "repoRoot": "<str>",
    "exitCode": <int>,                      // the exit code this run produced
    "buildsInInventory": <int>,
    "summary": {
      "provenanceUniformity": { "<platform>|<field>": {filesChecked, distinctValues,
                                  majorityValue, majorityCount, outlierCount, distribution} },
      "kv3Lints":            { "<era>|<platform>": {state, filesChecked, nan, nanFiles,
                                  rawDenylist, rawDenylistFiles, missingHidden, gatedNonempty} },
      "structure":           { "<platform>": {expected, present, missingSchema, missingProv},
                                "orphanDirs": <int> },
      "crossPlatformPresence": { "buildsWithExactlyOnePlatform": <int> }
    },
    "violations": [                          // every record that makes exitCode nonzero
      { "check": "<check-id>", "message": "<str>", ...check-specific keys (buildId, platform,
        era, field, class, value, majorityValue, path) may be present, all optional except
        "check" and "message" }
    ],
    "informational": [                       // never affects exitCode
      { "check": "cross-platform-presence", "buildId": "<str>", "present": [...],
        "missing": [...], "message": "<str>" }
    ]
  }

Python 3.9-compatible stdlib only. No pip installs. Designed to run from the repo root, but
resolves its own root from `__file__` so it also works when invoked with a path from elsewhere.
"""
from __future__ import annotations

import argparse
import io
import json
import re
import sys
from collections import Counter, defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Dict, Iterable, List, Tuple

# ------------------------------------------------------------------------------------------------
# KV3 non-determinism denylist -- kept in exact lockstep with
# walker/src/schema_walk.cpp::FilterKv3Nondeterminism's kFilters list (14 fields, 2026-07-28).
# WHY duplicated here instead of imported: the walker is C++; this is the independent auditor that
# proves the C++ filter is doing what it claims on the ACTUAL emitted bytes, so it must not share
# code (or a bug) with the thing it audits.
# ------------------------------------------------------------------------------------------------
_DENYLIST_FIELDS: Tuple[str, ...] = (
    "m_id", "m_ID", "m_nControlPointCount", "m_nControlPointStart", "m_nRandomSeed",
    "m_seed", "m_outputPinID", "m_stateID", "m_pinID", "m_entryStateID",
    "m_nCollisionGroupNumber", "m_flMassInv", "valueB", "pitchfrac",
)

# Matches a denylisted field's SAME-LINE value exactly the way the walker's own regex does: the
# field name in quotes, a colon, ONE literal space, then everything to end-of-line (`.` does not
# cross `\n` by default, matching C++ std::regex's ECMAScript grammar without the multiline/dotall
# flags -- the walker never sets those). This is deliberately narrower than "the field name appears
# anywhere": when a field is itself an object wrapper (e.g. `"m_id":\n{\n\t"m_id": <HIDDEN...` --
# the id-typed wrapper struct and its inner scalar happen to share a name), the wrapper line has NO
# same-line value at all and is correctly ignored; only the inner leaf line is a real candidate.
# Sorted longest-first purely for readability/defensiveness; no field is a prefix of another here.
_DENYLIST_RE = re.compile(
    r'"(' + "|".join(re.escape(f) for f in sorted(_DENYLIST_FIELDS, key=len, reverse=True)) + r')": (.*)'
)

_HIDDEN_MARKER = "<HIDDEN FOR DIFF>"

# A standalone -nan/nan token, not a substring of some other identifier/number (e.g. must not match
# inside "banana" or "0.1nanoseconds"). printf's "%g"-family formatting is what actually emits this
# (lowercase, C locale) so the match is deliberately case-sensitive.
_NAN_RE = re.compile(r"(?<![A-Za-z0-9_.])-?nan(?![A-Za-z0-9_])")

_KV3_METADATA_NAME = "MGetKV3ClassDefaults"
_PLATFORMS: Tuple[str, ...] = ("windows-x86_64", "linux-x86_64")

_SNIPPET_LIMIT = 200


def _snippet(s: str, limit: int = _SNIPPET_LIMIT) -> str:
    s = s.replace("\n", "\\n").replace("\t", "\\t")
    return s if len(s) <= limit else s[: limit - 3] + "..."


# ------------------------------------------------------------------------------------------------
# Inventory loading + era resolution
# ------------------------------------------------------------------------------------------------

def load_inventory(repo_root: Path) -> Dict[str, Any]:
    path = repo_root / "data" / "cs2-assets-inventory.json"
    if not path.is_file():
        raise SystemExit(f"verify-corpus: FATAL - inventory file not found: {path}")
    with path.open(encoding="utf-8") as f:
        return json.load(f)


def era_kv3_state(era_record: Dict[str, Any]) -> str:
    """"kv3ClassDefaults absent means TRUE" (KV3-ON); explicit false means Gated. Never guess
    beyond that -- any other value is a data error we fail loudly on rather than silently coerce."""
    flag = era_record.get("kv3ClassDefaults", True)
    if flag is True:
        return "on"
    if flag is False:
        return "gated"
    raise SystemExit(
        f"verify-corpus: FATAL - era {era_record.get('era')!r} has non-boolean "
        f"kv3ClassDefaults={flag!r}; refusing to guess its KV3 state."
    )


# ------------------------------------------------------------------------------------------------
# Check 1: provenance uniformity
# ------------------------------------------------------------------------------------------------

def check_provenance_uniformity(
    repo_root: Path, build_ids: List[str]
) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    violations: List[Dict[str, Any]] = []
    summary: Dict[str, Any] = {}

    for plat in _PLATFORMS:
        by_field: Dict[str, Dict[str, str]] = {"tool.gitCommit": {}, "tool.walkerSrcFingerprint": {}}
        for bid in build_ids:
            p = repo_root / "artifacts" / bid / plat / "provenance.json"
            if not p.is_file():
                continue
            try:
                with p.open(encoding="utf-8") as f:
                    d = json.load(f)
            except (OSError, json.JSONDecodeError) as e:
                violations.append({
                    "check": "provenance-unreadable", "platform": plat, "buildId": bid,
                    "message": f"{plat} build {bid}: provenance.json unreadable: {e}",
                })
                continue
            tool = d.get("tool") or {}
            commit = tool.get("gitCommit")
            if commit:
                by_field["tool.gitCommit"][bid] = commit
            fprint = tool.get("walkerSrcFingerprint")
            if fprint:
                by_field["tool.walkerSrcFingerprint"][bid] = fprint

        for field_name, values in by_field.items():
            if not values:
                continue  # field never present for this platform (artifacts predate stamping it)
            counts = Counter(values.values())
            # deterministic tie-break: highest count wins, ties broken by the value itself
            majority_value, majority_count = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))[0]
            outliers = {bid: v for bid, v in values.items() if v != majority_value}
            for bid in sorted(outliers, key=lambda b: int(b)):
                v = outliers[bid]
                violations.append({
                    "check": "provenance-uniformity", "platform": plat, "field": field_name,
                    "buildId": bid, "value": v, "majorityValue": majority_value,
                    "message": f"{plat} build {bid}: {field_name}={v} (platform majority={majority_value})",
                })
            summary[f"{plat}|{field_name}"] = {
                "filesChecked": len(values),
                "distinctValues": len(counts),
                "majorityValue": majority_value,
                "majorityCount": majority_count,
                "outlierCount": len(outliers),
                "distribution": dict(counts),
            }
    return violations, summary


# ------------------------------------------------------------------------------------------------
# Check 2: KV3 lints
# ------------------------------------------------------------------------------------------------

def _iter_kv3_metadata_values(class_record: Dict[str, Any]) -> Iterable[Tuple[str, str]]:
    """Yield (className, value) for every MGetKV3ClassDefaults metadata entry on a class. Class-
    level metadata is the only place this has ever been observed to carry the attribute (verified
    across the full current corpus), but fields/staticFields are scanned too -- defensively, not
    expensively -- so a future era that starts attaching it per-field is still caught rather than
    silently skipped (fail-loud, never guess)."""
    name = class_record.get("name", "<unnamed>")
    metadata_sources = [class_record.get("metadata") or []]
    for fld in class_record.get("fields") or []:
        metadata_sources.append(fld.get("metadata") or [])
    for fld in class_record.get("staticFields") or []:
        metadata_sources.append(fld.get("metadata") or [])
    for metas in metadata_sources:
        for m in metas:
            if m.get("name") == _KV3_METADATA_NAME:
                yield name, (m.get("value") or "")


def scan_kv3_file(path: Path, state: str) -> Dict[str, Any]:
    """Load ONE entity_schema.json, extract every KV3-lint-relevant finding, then let `data` go out
    of scope on return -- never more than one parsed file resident at a time."""
    with path.open(encoding="utf-8") as f:
        data = json.load(f)

    nan_hits: List[Tuple[str, str]] = []
    raw_hits: List[Tuple[str, str, str]] = []
    gated_hits: List[Tuple[str, str]] = []
    has_nonempty = False
    has_hidden = False

    for cls in data.get("classes") or []:
        for cls_name, value in _iter_kv3_metadata_values(cls):
            if not value:
                continue
            has_nonempty = True
            if state == "gated":
                gated_hits.append((cls_name, _snippet(value)))
                continue
            # state == "on"
            if _HIDDEN_MARKER in value:
                has_hidden = True
            if _NAN_RE.search(value):
                nan_hits.append((cls_name, _snippet(value)))
            for mm in _DENYLIST_RE.finditer(value):
                field, rest = mm.group(1), mm.group(2)
                if not rest.startswith(_HIDDEN_MARKER):
                    raw_hits.append((cls_name, field, _snippet(rest, 100)))

    return {
        "nan_hits": nan_hits,
        "raw_hits": raw_hits,
        "gated_hits": gated_hits,
        "has_nonempty": has_nonempty,
        "has_hidden": has_hidden,
    }


def check_kv3_lints(
    repo_root: Path, inventory: Dict[str, Any]
) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    violations: List[Dict[str, Any]] = []
    summary: Dict[str, Any] = {}
    era_by_id = {e["era"]: e for e in inventory["eras"]}

    for b in inventory["builds"]:
        bid = str(b["build_id"])
        era_id = b["era"]
        era = era_by_id.get(era_id)
        if era is None:
            violations.append({
                "check": "kv3-unknown-era", "buildId": bid, "era": era_id,
                "message": f"build {bid}: era {era_id!r} not found in data/cs2-assets-inventory.json eras[]",
            })
            continue
        state = era_kv3_state(era)

        for plat in _PLATFORMS:
            p = repo_root / "artifacts" / bid / plat / "entity_schema.json"
            if not p.is_file():
                continue  # missing-file is check 3's job, not this check's

            key = f"{era_id}|{plat}"
            stats = summary.setdefault(key, {
                "state": state, "filesChecked": 0,
                "nan": 0, "nanFiles": 0,
                "rawDenylist": 0, "rawDenylistFiles": 0,
                "missingHidden": 0, "gatedNonempty": 0,
            })
            stats["filesChecked"] += 1

            try:
                result = scan_kv3_file(p, state)
            except (OSError, json.JSONDecodeError) as e:
                violations.append({
                    "check": "kv3-unreadable", "buildId": bid, "platform": plat, "era": era_id,
                    "message": f"{plat} build {bid}: entity_schema.json unreadable: {e}",
                })
                continue

            # "*Files" counters are FILE-granularity (does this one entity_schema.json contain the
            # problem at all?), matching how the issue is naturally described/tracked (e.g. "62
            # windows entity_schema.json files contain -nan"); "nan"/"rawDenylist" stay at
            # OCCURRENCE-granularity (how many individual bad values) for deeper triage.
            if result["nan_hits"]:
                stats["nanFiles"] += 1
            if result["raw_hits"]:
                stats["rawDenylistFiles"] += 1

            for cls_name, snippet in result["nan_hits"]:
                stats["nan"] += 1
                violations.append({
                    "check": "kv3-nan-token", "buildId": bid, "platform": plat, "era": era_id,
                    "class": cls_name, "value": snippet,
                    "message": f"{plat} build {bid} class {cls_name}: nan/-nan token in {_KV3_METADATA_NAME} value",
                })
            for cls_name, field, snippet in result["raw_hits"]:
                stats["rawDenylist"] += 1
                violations.append({
                    "check": "kv3-raw-denylist-value", "buildId": bid, "platform": plat,
                    "era": era_id, "class": cls_name, "field": field, "value": snippet,
                    "message": (
                        f"{plat} build {bid} class {cls_name}: denylisted field {field!r} has a "
                        f"raw same-line value ({snippet!r}) instead of {_HIDDEN_MARKER}"
                    ),
                })
            if state == "on" and result["has_nonempty"] and not result["has_hidden"]:
                stats["missingHidden"] += 1
                violations.append({
                    "check": "kv3-missing-hidden-marker", "buildId": bid, "platform": plat,
                    "era": era_id,
                    "message": (
                        f"{plat} build {bid}: KV3-ON era ({era_id}) has non-empty "
                        f"{_KV3_METADATA_NAME} values but zero {_HIDDEN_MARKER} markers anywhere "
                        f"in the file -- suspected pre-fix walker output"
                    ),
                })
            for cls_name, snippet in result["gated_hits"]:
                stats["gatedNonempty"] += 1
                violations.append({
                    "check": "kv3-gated-nonempty", "buildId": bid, "platform": plat, "era": era_id,
                    "class": cls_name, "value": snippet,
                    "message": (
                        f"{plat} build {bid} class {cls_name}: era {era_id} is Gated "
                        f"(kv3ClassDefaults=false) but {_KV3_METADATA_NAME} value is non-empty "
                        f"({snippet!r})"
                    ),
                })
    return violations, summary


# ------------------------------------------------------------------------------------------------
# Check 3: structure / orphans
# ------------------------------------------------------------------------------------------------

def check_structure(
    repo_root: Path, inventory: Dict[str, Any]
) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    violations: List[Dict[str, Any]] = []
    per_platform: Dict[str, Dict[str, int]] = defaultdict(
        lambda: {"expected": 0, "present": 0, "missingSchema": 0, "missingProv": 0}
    )

    for b in inventory["builds"]:
        bid = str(b["build_id"])
        binaries = b.get("binaries") or {}
        for plat in sorted(binaries.keys()):
            stats = per_platform[plat]
            stats["expected"] += 1
            pdir = repo_root / "artifacts" / bid / plat
            if not pdir.is_dir():
                violations.append({
                    "check": "structure-missing-artifact-dir", "buildId": bid, "platform": plat,
                    "path": str(pdir),
                    "message": f"build {bid}: inventory lists {plat} binaries but {pdir} does not exist",
                })
                continue
            stats["present"] += 1
            for fn, stat_key in (("entity_schema.json", "missingSchema"), ("provenance.json", "missingProv")):
                fpath = pdir / fn
                if not fpath.is_file():
                    stats[stat_key] += 1
                    violations.append({
                        "check": "structure-missing-file", "buildId": bid, "platform": plat,
                        "path": str(fpath),
                        "message": f"build {bid} {plat}: expected file missing: {fpath}",
                    })

    # Orphaned staging/old dirs -- siblings of the platform dir, pattern
    # artifacts/<build>/<platform>.staging-<guid> or artifacts/<build>/<platform>.old-<guid>.
    artifacts_dir = repo_root / "artifacts"
    orphans: List[Path] = []
    if artifacts_dir.is_dir():
        for pattern in ("*/*.staging-*", "*/*.old-*"):
            orphans.extend(sorted(artifacts_dir.glob(pattern)))
    for o in orphans:
        violations.append({
            "check": "structure-orphan-dir", "path": str(o),
            "message": f"orphaned staging/old directory (interrupted extract run): {o}",
        })

    summary = {plat: dict(stats) for plat, stats in per_platform.items()}
    summary["orphanDirs"] = len(orphans)
    return violations, summary


# ------------------------------------------------------------------------------------------------
# Check 4: cross-platform presence (informational only)
# ------------------------------------------------------------------------------------------------

def check_cross_platform_presence(
    repo_root: Path, build_ids: List[str]
) -> Tuple[List[Dict[str, Any]], Dict[str, Any]]:
    informational: List[Dict[str, Any]] = []
    for bid in build_ids:
        present = [p for p in _PLATFORMS if (repo_root / "artifacts" / bid / p).is_dir()]
        if len(present) == 1:
            missing = [p for p in _PLATFORMS if p not in present]
            informational.append({
                "check": "cross-platform-presence", "buildId": bid,
                "present": present, "missing": missing,
                "message": f"build {bid}: only {present[0]} is committed ({missing[0]} is not)",
            })
    summary = {"buildsWithExactlyOnePlatform": len(informational)}
    return informational, summary


# ------------------------------------------------------------------------------------------------
# Rendering
# ------------------------------------------------------------------------------------------------

def _print_table(headers: List[str], rows: List[List[str]]) -> None:
    widths = [len(h) for h in headers]
    for row in rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(str(cell)))
    line = "  ".join(h.ljust(widths[i]) for i, h in enumerate(headers))
    print(line)
    print("  ".join("-" * w for w in widths))
    for row in rows:
        print("  ".join(str(cell).ljust(widths[i]) for i, cell in enumerate(row)))


def _print_violations(violations: List[Dict[str, Any]], max_print: int) -> None:
    for v in violations[:max_print]:
        print(f"  - [{v['check']}] {v['message']}")
    remaining = len(violations) - max_print
    if remaining > 0:
        print(f"  ... and {remaining} more (use --json for the full machine-readable list)")


def render_report(
    prov_violations: List[Dict[str, Any]], prov_summary: Dict[str, Any],
    kv3_violations: List[Dict[str, Any]], kv3_summary: Dict[str, Any],
    struct_violations: List[Dict[str, Any]], struct_summary: Dict[str, Any],
    xplat_info: List[Dict[str, Any]], xplat_summary: Dict[str, Any],
    max_print: int,
) -> None:
    print("=" * 88)
    print("CHECK 1: Provenance uniformity")
    print("=" * 88)
    rows = []
    for key, s in sorted(prov_summary.items()):
        plat, field = key.split("|", 1)
        rows.append([plat, field, s["filesChecked"], s["distinctValues"],
                     s["majorityValue"][:16], s["majorityCount"], s["outlierCount"]])
    if rows:
        _print_table(
            ["platform", "field", "checked", "distinct", "majority", "majorityN", "outliers"], rows
        )
    else:
        print("(no provenance.json files found)")
    print(f"violations: {len(prov_violations)}")
    _print_violations(prov_violations, max_print)

    print()
    print("=" * 88)
    print("CHECK 2: KV3 lints")
    print("=" * 88)
    rows = []
    for key, s in sorted(kv3_summary.items(), key=lambda kv: kv[0]):
        era, plat = key.split("|", 1)
        rows.append([era, plat, s["state"], s["filesChecked"],
                     f'{s["nanFiles"]} ({s["nan"]})', f'{s["rawDenylistFiles"]} ({s["rawDenylist"]})',
                     s["missingHidden"], s["gatedNonempty"]])
    if rows:
        _print_table(
            ["era", "platform", "state", "checked", "nanFiles(occ)", "rawDenylistFiles(occ)",
             "missingHidden", "gatedNonempty"],
            rows,
        )
    else:
        print("(no entity_schema.json files found)")
    total_nan_files = sum(s["nanFiles"] for s in kv3_summary.values())
    total_raw_files = sum(s["rawDenylistFiles"] for s in kv3_summary.values())
    print(f"total files with >=1 -nan/nan token: {total_nan_files}   "
          f"total files with >=1 raw-denylist value: {total_raw_files}")
    print(f"violations: {len(kv3_violations)}")
    _print_violations(kv3_violations, max_print)

    print()
    print("=" * 88)
    print("CHECK 3: Structure / orphans")
    print("=" * 88)
    rows = []
    for plat in _PLATFORMS:
        s = struct_summary.get(plat)
        if s:
            rows.append([plat, s["expected"], s["present"], s["missingSchema"], s["missingProv"]])
    if rows:
        _print_table(["platform", "expected", "present", "missingSchema", "missingProv"], rows)
    print(f"orphan staging/old dirs: {struct_summary.get('orphanDirs', 0)}")
    print(f"violations: {len(struct_violations)}")
    _print_violations(struct_violations, max_print)

    print()
    print("=" * 88)
    print("CHECK 4: Cross-platform presence (informational only -- does not affect exit code)")
    print("=" * 88)
    print(f"builds with exactly one platform committed: {xplat_summary['buildsWithExactlyOnePlatform']}")
    for v in xplat_info[:max_print]:
        print(f"  - {v['message']}")
    remaining = len(xplat_info) - max_print
    if remaining > 0:
        print(f"  ... and {remaining} more (use --json for the full list)")


# ------------------------------------------------------------------------------------------------
# main
# ------------------------------------------------------------------------------------------------

def main(argv: List[str]) -> int:
    # WHY: Windows consoles default to cp1252 and choke on non-cp1252 bytes that can legitimately
    # appear inside artifact content (schema strings, KV3 blobs). Wrap stdout/stderr in a UTF-8
    # TextIOWrapper with errors='replace' BEFORE printing anything derived from artifact content.
    for stream_name in ("stdout", "stderr"):
        stream = getattr(sys, stream_name)
        buffer = getattr(stream, "buffer", None)
        if buffer is not None:
            try:
                setattr(sys, stream_name, io.TextIOWrapper(buffer, encoding="utf-8", errors="replace"))
            except (AttributeError, ValueError):
                pass  # stream already wrapped/unavailable (e.g. under a test harness) -- fine

    parser = argparse.ArgumentParser(
        prog="verify-corpus.py",
        description="READ-ONLY verification of the CS2 schema-extraction corpus (see module docstring for the 4 check families and the --json schema).",
    )
    parser.add_argument("--json", metavar="PATH", default=None,
                         help="Write the full machine-readable report to PATH (in addition to the stdout summary).")
    parser.add_argument("--root", metavar="REPO_ROOT", default=None,
                         help="Repo root override (default: the parent of this script's scripts/ dir).")
    parser.add_argument("--max-print", type=int, default=25,
                         help="Max violations/informational rows to print per check on stdout (default: 25). --json always has the full list.")
    args = parser.parse_args(argv[1:])

    repo_root = Path(args.root).resolve() if args.root else Path(__file__).resolve().parent.parent
    artifacts_dir = repo_root / "artifacts"
    if not artifacts_dir.is_dir():
        print(f"verify-corpus: FATAL - artifacts/ not found under repo root {repo_root}", file=sys.stderr)
        return 2

    try:
        inventory = load_inventory(repo_root)
    except SystemExit as e:
        print(str(e), file=sys.stderr)
        return 2

    build_ids = [str(b["build_id"]) for b in inventory["builds"]]

    print(f"verify-corpus: repo root = {repo_root}")
    print(f"verify-corpus: {len(build_ids)} builds in data/cs2-assets-inventory.json")
    print()

    prov_violations, prov_summary = check_provenance_uniformity(repo_root, build_ids)
    kv3_violations, kv3_summary = check_kv3_lints(repo_root, inventory)
    struct_violations, struct_summary = check_structure(repo_root, inventory)
    xplat_info, xplat_summary = check_cross_platform_presence(repo_root, build_ids)

    render_report(
        prov_violations, prov_summary,
        kv3_violations, kv3_summary,
        struct_violations, struct_summary,
        xplat_info, xplat_summary,
        args.max_print,
    )

    all_violations = prov_violations + kv3_violations + struct_violations
    exit_code = 1 if all_violations else 0

    print()
    print("=" * 88)
    if exit_code == 0:
        print("verify-corpus: CLEAN -- no violations found.")
    else:
        print(
            f"verify-corpus: FAILED -- {len(all_violations)} violation(s) "
            f"(provenance={len(prov_violations)}, kv3={len(kv3_violations)}, structure={len(struct_violations)})"
        )
    print("=" * 88)

    if args.json:
        report = {
            "schemaVersion": "1.0",
            "generatedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "repoRoot": str(repo_root),
            "exitCode": exit_code,
            "buildsInInventory": len(build_ids),
            "summary": {
                "provenanceUniformity": prov_summary,
                "kv3Lints": kv3_summary,
                "structure": struct_summary,
                "crossPlatformPresence": xplat_summary,
            },
            "violations": all_violations,
            "informational": xplat_info,
        }
        json_path = Path(args.json)
        with json_path.open("w", encoding="utf-8") as f:
            json.dump(report, f, indent=2, sort_keys=False)
        print(f"verify-corpus: wrote JSON report to {json_path}")

    return exit_code


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
