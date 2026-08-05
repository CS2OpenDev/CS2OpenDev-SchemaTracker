# Generating the artifacts

This is the operational guide to producing the `artifacts/<build_id>/<platform>/` sets: how a build becomes an artifact set, the pipeline stages, and the recurring operations (adding a new build, standing up a new walker era). For the tool internals see [HOST.md](HOST.md) and [WALKER.md](WALKER.md).

## The pipeline, end to end

One build, one platform, in five stages:

1. **Acquire** — fetch the build's inputs from Steam into the local binary cache: the per-OS binary depot and the `pak01` content slice. The host records the Steam identity and input hashes as it goes.
2. **Walk** — the host picks the walker binary matched to this build's era and runs it once against the cached binaries. The walker loads the Source 2 DLLs into its own process, walks the schema system, console registry, and message registries, and writes a single intermediate file.
3. **Merge + emit** — the host reads the walker's output, parses the content pack (VPK → KV1/KV3), round-trips the embedded protobuf descriptors, and writes every per-artifact JSON/proto file in canonical form.
4. **Validate** — every emitted artifact is round-tripped through its proto3 schema; the set is checked for completeness (all-or-nothing); a build-to-build `changelog.json` is written against the previous committed build.
5. **Promote + record** — `extract --commit` writes the complete set into `artifacts/<build_id>/<platform>/`, replacing any prior set for that build and platform. Recording it in git (the artifact commit) is a deliberately separate step, so a human or CI can review the diff first.

The host runs stages 1–4 in a single `extract` invocation, and `--commit` additionally performs stage 5's promotion into the working tree. Because the walker must load native binaries, **the walker for a platform runs on that platform's OS** — Linux artifacts are produced on a Linux host, Windows artifacts on Windows.

## The core guarantees, and how they're enforced

- **Deterministic.** Same tool version + same inputs ⇒ byte-identical output. JSON is emitted in canonical form (sorted keys, no insignificant whitespace); timestamps come only from the input manifest, never the wall clock; every collection is iterated in a stable order.
- **Fail-loud, never partial.** Any input failure — a corrupt binary, a missing module, a VPK or KV parse error, an unrecognized schema-system layout — aborts with a non-zero exit *before* any artifact bytes are written.
- **All-or-nothing.** A commit is either one complete `(build, platform)` set or one complete build across both platforms. There is no partial set. Anything legitimately missing goes in the build-level `omissions.json` with a reason, never a silent skip.
- **Content-gated artifacts.** The seven content artifacts are required only when the build's `provenance.json` lists the content depot (`2347770`) among its inputs. A build acquired binaries-only, or an era that never shipped a table, records the absence in `omissions.json`.

`verify-artifacts` re-checks these completeness rules over any committed set and is the gate to run before publishing.

## The walker-era model

No one walker binary covers the whole build range: Valve moves C++ struct offsets between updates, and a walker built against stale layouts either misreads fields or faults outright. So the tool ships **one walker per era**, an era being a span of builds that share a layout. The era catalog lives in `data/cs2-assets-inventory.json`: a top-level `eras[]` plus the exact `era` id on every build record. Two kinds exist: **compile-pin eras** (the common case), which pin their own `alliedmodders/hl2sdk` commit and get a walker compiled against it, and **runtime-layout variants** (the early-2023 builds), which ride a modern compile pin and recover the older layout from reverse-engineered offset tables selected at runtime.

[WALKER.md](WALKER.md) covers the mechanics of both kinds and the layout probe that enforces them; [HOST.md](HOST.md) covers how the host resolves a build to its walker binary. Operationally: the host refuses to fall back to a different era's binary, and a build whose live layout doesn't match its era's known signature aborts rather than guessing. A layout mismatch therefore cannot silently corrupt output.

## Recurring operations

### Add a newly released build

New builds are appended forward as CS2 updates ship:

1. **Record the build.** A successful `extract --commit` of a never-before-seen build appends its row to the inventory (`data/cs2-assets-inventory.json`) as a side effect — the host is the sole writer (era + content/binary GIDs, from the promoted provenance). Steam manifest identities for a build are hand-extracted from SteamDB and folded in the same way.
2. **Acquire its inputs** into the binary cache. The current live build is fetchable anonymously; older builds need an authenticated, CS2-owning Steam account and are resolved from the recorded manifest identities. A content-only update (one that changed only the content depot) carries its binaries forward from the previous binary-bearing build.
3. **Extract + commit** both platforms. If the build falls in an existing era, its walker already exists; if the game has drifted past the current era's layout, stand up a new era first (below).

### Stand up a new era

When a game update changes the struct layout enough that the current walker crashes or misreads the newest builds:

1. **Find the pin.** Identify the `alliedmodders/hl2sdk` cs2-branch commit that tracks that CS2 update (its layout matches the new binaries). Confirm empirically: build a candidate walker and check it walks the newest build cleanly with a sane class count.
2. **Register the era** in `data/cs2-assets-inventory.json` `eras[]` (kind, hl2sdk pin, the per-platform layout signatures, and the class-count band) and in the walker's known-signature allow-list, then set the new build's `era`.
3. **Build + archive** the era's walker for both platforms, and validate: the walker walks the era's builds without crashing, output is deterministic across re-runs, and the two platforms agree on classes/enums/constants (only platform-specific console variables differ).
4. **Re-extract** the affected builds through the new walker and commit.

A newer SDK can require additional headers or sources in the walker's build — expect to adjust `walker/CMakeLists.txt` if the compile fails on missing includes.

## Batching

Bulk work (backfilling many builds, re-dumping after a walker fix) runs in two phases, because extraction and git-commit have very different cost profiles: extract every set into the working tree first, then commit them one build at a time. Each commit is a single complete `(build, platform)` set so history stays coherent and bisectable.
