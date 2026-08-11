# CS2-Schema-Tracker

Offline, deterministic extraction of structured Counter-Strike 2 data straight from the shipped game binaries and content. For every CS2 build it produces one internally consistent, schema-validated set of JSON + protobuf artifacts under `artifacts/<build_id>/<platform>/`, with full provenance back to the bytes each value came from.

The point is to give downstream tools a **stable, diffable snapshot of every CS2 build** — entity schemas, protobuf descriptors, console variables, network-message tables, engine constants, and the game-data tables from the content pack — without anyone having to scrape the game or depend on a chain of third-party dumps.

## What you get

- **One artifact set per `(build, platform)`.** Two platforms are tracked: `windows-x86_64` and `linux-x86_64`. CS2 ships client and dedicated-server binaries together in one per-OS depot, so "client vs. server" is recorded per class in a `module` field, not as a separate download.
- **Deterministic output.** The same tool version over the same input binaries produces byte-identical files — so a `git diff` between two builds shows only what actually changed in the game.
- **Fail-loud, all-or-nothing.** A bad or missing input aborts before anything is written; a committed set is always complete, never half-populated. Anything legitimately absent (e.g. a content table a given era never shipped) is recorded explicitly rather than silently dropped.
- **Full provenance.** Every set records the exact Steam identity it came from and a SHA-256 of every input binary, so any set can be re-fetched and re-verified.

## Scope

**In scope**

- Every reachable CS2 build from the first one forward — Steam `build_id` **10832117** (CS2 Limited Test, 2023-03-22) onward.
- The two platforms above, public branch only.
- Data that is **directly extractable** from a build's binaries and content pack — the catalog below. Everything is traceable to a specific source; nothing is guessed or reconstructed heuristically.

**Out of scope**

- Source 1 CS:GO (pre-2023-03-22) — no comparable schema-reflection system exists to read.
- Attaching to a running game. The tool loads the binaries into its *own* process and walks them there; the game is never run.
- General asset extraction (maps, models, sounds) and curated/editorial overlays.
- macOS binaries, and Steam prerelease / non-public branches.

## Artifacts

Each `artifacts/<build_id>/<platform>/` set contains the following. Every file's exact shape is defined by a proto3 schema under [`schemas/`](schemas) — the single source of truth for consumers, which compile the same schemas to typed bindings in any language via `protoc`.

`entity_schema.json` carries the schema system's runtime flag words (`flags`, `flags2`) as raw bits rather than as interpreted booleans; **[docs/SCHEMA_FLAGS.md](docs/SCHEMA_FLAGS.md)** is the decode table for them.

### Extracted from the game binaries (always present)

| Artifact | What it is | Sourced from |
|---|---|---|
| `entity_schema.json` | The core artifact: every schema class/struct and enum, with fields (offset, size, type, metadata), parent chains, and KV3 default values. | The Source 2 **schema system**, walked live in-process. |
| `protos/*.proto` + `protos.descriptorset` | The build's protobuf definitions (network messages, game events, etc.), round-tripped to `.proto` text plus a binary `FileDescriptorSet`. Almost all are recovered from the binaries; the engine **wire-message** protos (`netmessages`, `usermessages`, `gameevents`, `te`, `clientmessages`, `cs_gameevents`, `cstrike15_usermessages`, `networkbasetypes`) that CS2 embeds in no binary are merged from the pinned hl2sdk SDK (see below) so the `network_messages.json` / `demo_messages.json` wire-ID→type joins resolve. Each SDK-sourced file is stamped with a provenance header. One file is deliberately **not** verbatim: `cstrike15_gcmessages.proto` is emitted as its referenced closure — only the types the rest of the set reaches, re-derived on every extract, with the Steam-side imports the closure never needs (`steammessages`, `engine_gcmessages`, `gcsdk_gcmessages`) dropped from its import list (the three files themselves still ship in the set). It carries a `DERIVED CLOSURE` header saying exactly what was kept. The emitted files carry **no `package` or `option csharp_namespace`** — that is faithful to Valve's descriptors, which declare neither, and the tool will not fabricate options the binaries don't embed; consumers that need a namespace should inject it in their own staging step. | `FileDescriptorProto`s embedded in the binaries, plus the pinned-hl2sdk wire descriptors for the 8 families the binaries don't carry. |
| `convars.json` | Every console variable: name, default, flags, description, and type/bounds where present. | The engine's console-variable (`ICvar` / `ConCommandBase`) registry. |
| `commands.json` | Every console command: name, flags, description. | The same console registry. |
| `network_messages.json` | The wire-protocol table: integer message-ID → protobuf message type, per network channel. | The engine's NetMessages registry, read by a static RTTI scan of the binary. |
| `demo_messages.json` | The `.dem` demo-stream table: command-ID → protobuf message type (a flat id space where one id can bind two message types). | `engine2`'s `CDemoMessagePB<id,type>` RTTI, scanned offline per build. |
| `engine_constants.json` | Named integer/string constants the binary exposes by name through the schema system or named-constant pools. | Schema metadata + named-constant pools. |
| `string_pools.json` | Reflection-reachable interned string pools, deduplicated. *(CS2 exposes no enumerable string pool, so this is emitted empty by design — proven by reverse-engineering, not an error.)* | Symbol pools in the binary. |
| `modules.json` | Every binary file read: path, SHA-256, size, export count, schema-registration count, and the interfaces it resolved at load. | Binary headers + the tool's own measurements. |
| `registry_audit.json` | A completeness ledger: every named registry symbol in the binary, each marked as `extracted` (naming the artifact it went to) or `omitted` (with a reason). | The tool's audit pass over the binary's symbols. |
| `provenance.json` | The full provenance record (see [Inputs & provenance](#inputs--provenance)). | Generated by the tool. |

### Extracted from the content pack (present when content was acquired)

These come from the shared content depot's `pak01` VPK and are present only when a build's `provenance.json` records that content depot (`2347770`) among its inputs. A build/era that never shipped a given table records it in `omissions.json` instead.

| Artifact | What it is | Sourced from |
|---|---|---|
| `gameevents.json` | The game-event registry (event names and their fields), structurally parsed. Merges the csgo pak's `game.gameevents` + `mod.gameevents` with the engine core pak's `core.gameevents` (the engine event registry) when that pak is present. | `.gameevents` files inside `game/csgo/pak01_dir.vpk` and `game/core/pak01_dir.vpk`. |
| `item_definitions.json` | Economy item definitions: items, prefabs, paint/sticker/music kits, rarities, qualities. | `scripts/items/items_game.txt` (KV1). |
| `game_modes.json` | Game types and their nested game modes: map groups, per-mode max players, convar overrides, type/mode/flag ids. | `gamemodes.txt` (KV1). |
| `localization.json` | Token-keyed display strings (token → per-language value) — the item/weapon-name → human-name join. **Build-on-demand — produced every dump but NOT committed** (see below). | `resource/csgo_<lang>.txt` (KV1, sometimes UTF-16). |
| `surface_properties.json` | Per-material physics and footstep/impact/acoustic mappings. | The `scripts/surfaceproperties_*.txt` family (KV3 text). |
| `prop_data.json` | Breakable-prop classes and health, gib groups, and the collision-group registry. | `scripts/propdata.txt` (KV1) + `scripts/collision_properties.txt` (KV3 text). |
| `map_overviews.json` | Per-map radar metadata (material, position, scale, rotation, zoom, bombsites, spawns) plus a maps inventory. | `resource/overviews/*.txt` (KV1, one per map). |

#### Build-on-demand: `localization.json`

`localization.json` is the one content artifact that is **produced every dump but not committed to the tree**. At ~199 MB per set it is 96% of the working tree, so it is stored as a build-on-demand artifact rather than in `artifacts/`:

- It is still **produced on every dump**, so extraction and determinism stay exercised, and its build-to-build **changelog is still emitted** (it appears as a `localization` family in `changelog.json`).
- It is regenerable on demand via the host `emit-localization` command against the same content input.
- Every set's `provenance.json` records `provenance.localization` — the `sha256` (hex, lowercase), `size_bytes`, and `token_count` of the canonical `localization.json`. The hash is over the deterministic canonical JSON, so an `emit-localization` rebuild is **byte-verifiable** against what was dumped. A build/era whose content depot was never acquired has no `provenance.localization`.

### Build-level (alongside the platform directories)

| Artifact | What it is |
|---|---|
| `omissions.json` | Records anything legitimately absent — a platform not dumped, or a content table an era never shipped — with a reason. Present only when there *is* an omission; a clean build carries none, and an absent file reads as "nothing omitted". |
| `changelog.json` | The diff against the immediately preceding committed build, per platform. |
| `schema_evolution/<platform>.json` | The cumulative, whole-history **schema-evolution graph** for a platform (every build-to-build structural delta + per-field history), at one fixed path under the artifacts root (not per build). Facts-only: field add/remove/retype/move, class reparent/resize, enum member churn, neutral rename *evidence* — no inferred renames or safety verdicts. Alongside the frozen 1:1 `paired_evidence`, three unselected N:M **candidate surfaces** carry wider provable relations for downstream curation: within-class remove/add pairs (type or offset equality), cross-module same-bare-name class pairs, and same-name/same-type field moves between surviving classes — each candidate listing exactly the signals that hold, never a selection among ties. |
| `pics-appinfo.json` | The Steam PICS appinfo snapshot for the build. Optional — captured only for builds acquired while they were the live build, and never reproducible afterward. |

## Directory layout

```text
artifacts/<build_id>/
  omissions.json              # build-level; present only when something is omitted
  pics-appinfo.json           # build-level; optional (forward-captured builds only)
  windows-x86_64/
    entity_schema.json
    convars.json
    ...
    protos/
      netmessages.proto
      ...
  linux-x86_64/
    ...
```

Per-platform directories are siblings under one `<build_id>` so a consumer can diff the two platforms of the same build trivially.

## Inputs & provenance

Everything is derived from a single CS2 build (identified by its Steam `build_id`) for one platform:

- **Game binaries** — the per-OS Steam binary depot (the Source 2 DLLs/SOs: `schemasystem`, `engine2`, `client`, `server`, and the rest). The source for every binary-derived artifact.
- **Content** — the build's `pak01` VPK from the shared content depot (`2347770`). The source for the content artifacts.
- **[`alliedmodders/hl2sdk`](https://github.com/alliedmodders/hl2sdk)** (cs2 branch, a pinned submodule) — the C++ struct layouts the walker uses to read the binaries, and the source for the engine wire-message protos CS2 embeds in no binary (compiled to `data/wire_descriptors.pb` via `scripts/gen-wire-descriptors.sh` and merged into `protos.descriptorset`). The only third-party CS2-domain build input; pinned per-build so the walker always matches the binary it reads.

Inputs are fetched from Steam, hashed (SHA-256 / size / mtime), and recorded in `provenance.json` along with the Steam identity (appid / depot / manifest / build id) and the tool version. Any set can be re-acquired from its provenance and verified against the recorded hashes.

## Using the artifacts

Consumers read the JSON directly, or run `protoc` against [`schemas/`](schemas) for typed bindings. The artifact surface — filenames, directory shape, and each artifact's schema — is treated as a stable contract: additive changes are safe; any rename or removal is a deliberate, documented break carried in the schema version every artifact stamps.

### Consuming only the latest build

A full clone checks out every committed build (12+ GB). If you only need the newest set, two machine-maintained channels carry just that — the newest build's `artifacts/<build_id>/` (all platforms committed so far), the cumulative schema-evolution data (`artifacts/schema_evolution/<platform>.json`, branch only), `schemas/`, the assets inventory, and a root `LATEST.json` manifest naming the current build id and platforms:

- **The `latest` branch** — the git answer, for a lightweight clone or submodule:

  ```sh
  git clone --depth 1 --branch latest https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker.git
  # or as a submodule that tracks the channel:
  git submodule add -b latest https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker.git schematracker
  git submodule update --remote --depth 1 schematracker
  ```

  Its history is append-only (never force-pushed), so a submodule's pinned commit stays fetchable forever while `--depth 1` keeps the download to roughly one set.

- **The `artifacts-latest` rolling release** — the no-git answer. Per-platform zips (set + schemas + manifest) are overwritten in place at stable URLs:

  ```
  https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/releases/download/artifacts-latest/cs2-artifacts-latest-windows-x86_64.zip
  https://github.com/CS2OpenDev/CS2OpenDev-SchemaTracker/releases/download/artifacts-latest/cs2-artifacts-latest-linux-x86_64.zip
  ```

Both channels are refreshed by the `publish-latest` workflow on every artifact push to `main`.

## Producing the artifacts / building the tool

This repo also contains the extraction tool that generates the artifacts. If you want to run it or understand how it works:

- **[docs/ARTIFACTS_GENERATION.md](docs/ARTIFACTS_GENERATION.md)** — the end-to-end pipeline: acquiring a build, running the walker, emitting and validating the set.
- **[docs/HOST.md](docs/HOST.md)** — the .NET host: its command-line surface, capabilities, and usage patterns.
- **[docs/WALKER.md](docs/WALKER.md)** — the C++ walker (and its RTTI scanners): how it reads the binaries and how it stays matched to each build.
- **[BUILD.md](BUILD.md)** — toolchain setup and build/test commands.

License: MIT — see [`LICENSE`](LICENSE).
