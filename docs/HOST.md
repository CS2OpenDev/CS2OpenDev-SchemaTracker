# The host

The host is the .NET 10 program that produces the artifact sets — everything in the project except the C++ walker. It is one command-line tool, `cs2-schema-tracker`, that acquires a build's inputs from Steam, runs the matched walker as a subprocess, merges the walker's output with data parsed from the content pack, serializes every artifact in canonical form, validates each one, and promotes the complete set into `artifacts/`.

For the artifact catalog and the five-stage pipeline this doc assumes as background, see [README.md](../README.md) and [ARTIFACTS_GENERATION.md](ARTIFACTS_GENERATION.md). This doc goes deeper on the host itself: its command surface, what each capability actually does, how it talks to the walker, and how to operate it. For the walker, see [WALKER.md](WALKER.md).

The solution lives under `host/` (`Cs2SchemaTracker.sln`); the tool project is `host/src/Cs2SchemaTracker.Host`. It builds with `dotnet build` and tests with `dotnet test`. Third-party dependencies are deliberately narrow: `System.CommandLine` for the CLI, `Google.Protobuf` for descriptor round-trip and canonical JSON, `SteamKit2` for depot acquisition, and the Microsoft configuration libraries. The VPK and KV1/KV3 parsers are first-party code in the project.

## Command surface

`cs2-schema-tracker` is a `System.CommandLine` root command with one subcommand per operation. `--help` and `--version` (`-v`) work at the root and per subcommand; an unknown subcommand or option exits 64, and any escaped error exits non-zero with the message on stderr (the tool never fails silently).

### Everyday commands

| Command | Purpose | Key options |
|---|---|---|
| `extract` | The main command: run the full pipeline for one or more `(build, platform)` and write the complete artifact set. Off-repo by default; `--commit` promotes it into `artifacts/`. | `--build <id\|latest>` (repeatable), `--platform`, `--out`, `--commit`, `--verify`, `--all` / `--era` / `--pin` (batch selection), `--force`, `--no-acquire`, `--no-gate` |
| `acquire` | Fetch a build's inputs from Steam into the binary cache and verify them against their manifests. Single `(build, platform)`, or a batch over the inventory. | `--build <id\|latest>` (repeatable), `--platform`, `--out`, `--all`, `--auth`, `--from-manifest`, `--from-provenance`, `--content` (+ `--dir-only` / `--full-pak`), `--tools`, `--cache-only` / `--no-cache`, `--probe` |
| `verify-artifacts` | Read-only completeness check over committed sets: assert each `(build, platform)` under `artifacts/` is a legal all-or-nothing shape. The gate to run before publishing. | `--artifacts <root>`, `--build` (repeatable), `--changed-paths` (for CI diffs) |
| `diff` | Emit the build-to-build `changelog.json` between two committed builds, written under the newer build. `extract` also produces this inline; `diff` regenerates it standalone. Includes the 6th `localization` family (regenerated on demand from both builds' content) when both produced localization. | `--from`, `--to`, `--platform`, `--artifacts` |
| `emit-localization` | Regenerate the build-on-demand `localization.json` from a build's content (it is produced every dump but not committed). `--verify` checks the rebuild byte-for-byte against `provenance.localization`. | `--build`, `--platform`, `--out`, `--verify` |

`extract` is the command you run most; `acquire` is usually invoked implicitly by `extract` (it auto-acquires missing inputs unless `--no-acquire` is passed), but is run directly to pre-warm the cache or to do the authenticated historical backfill. `verify-artifacts` runs before every publish. `emit-localization` rebuilds the one artifact `extract` produces but does not commit.

### Diagnostic and internal commands

| Command | Purpose |
|---|---|
| `probe-layout` | Report the schema-system layout signature for a directory of binaries; non-zero exit on an unknown layout. Used when standing up a new era to confirm which layout a build presents. |
| `audit` | Regenerate `registry_audit.json` deterministically for one committed `(build, platform)` directory. |
| `dump-appinfo` | Diagnostic: fetch an app's current Steam PICS appinfo and write it to a file (`--app`, `--format json\|vdf`, `--out`). |
| `content-store migrate` | Internal: trim each build's co-located content pak into the content-addressed `_content/<gid>` store, validate byte-identical, and (with `--reclaim`) delete the co-located copy. |
| `reconcile-content-gids` | Internal: fix stale per-build content GIDs recorded in cache manifest records against the inventory's authoritative values (`--check` / `--apply`). |

`probe-layout`, `audit`, and `dump-appinfo` are diagnostics and maintenance tools; `content-store` and `reconcile-content-gids` are developer tooling for the binary cache and are not part of the documented artifact-facing surface. Keeping `data/cs2-assets-inventory.json` current is no longer a standalone command: a successful `extract --commit` of a never-before-seen build appends its row (era + content/binary GIDs, from the promoted provenance) as a build-level side effect (the former `sync-inventory` command has been retired).

## Capabilities in depth

### Steam acquisition

Acquisition is backed by `SteamKit2`. CS2 is free-to-play, so the default path logs on **anonymously** — which is enough to fetch the **current** public build of app 730 (both the per-OS binary depot and the shared content depot `2347770`). Anonymous PICS only exposes the current manifest, so a **historical** build cannot be resolved anonymously; those require an **authenticated** account that owns CS2. Two ways to name a prior build's exact inputs:

- `--auth` with `--from-manifest <spec.json>` — an explicit `{build, app, depots:[{depot_id, manifest_id}]}` spec fetches those exact per-depot manifest GIDs. Credentials come from `STEAM_USERNAME` / `STEAM_PASSWORD` in the environment (loadable from a repo-root `.env`); a one-time `--guard-code` seeds a Steam Guard session, which is then cached so later runs are non-interactive.
- `--from-provenance <provenance.json>` — re-acquire the exact inputs a committed set pinned, then SHA-256-verify each against the recorded hash. This is how any set is reproduced from its own provenance.

Batch acquisition (`--all`, or two-or-more `--build`) drives from `data/cs2-assets-inventory.json`: it resolves each inventory build that has a binary manifest for the platform and acquires it, skipping ones already present unless `--force`.

**The binary cache.** Acquired binaries land under the store root as `<root>/<build>/<platform>/`, alongside a `manifest-record.json` recording the Steam identity (appid, depots, manifest GIDs, creation times) that later becomes provenance. Resolution is cache-first: `extract` and `acquire` read the cache before contacting Steam. `--cache-only` forbids any Steam contact (fail if absent); `--no-cache` forces a fresh download and refreshes the cache.

**The content store.** The `pak01` content pack is large and changes far less often than the binaries, so it is stored once per content-depot manifest GID as a **trimmed** VPK under `<root>/_content/<gid>/game/csgo/`. The trim keeps only the files the seven content emitters read. Both platforms of a build — and every build whose content depot did not change — resolve the one shared copy by the GID recorded in their `manifest-record.json`. A content-only game update (one that changed only the content depot) carries its binaries forward from the previous binary-bearing build via the inventory, so it still produces a full set without re-downloading unchanged binaries. `--content` acquisition has narrowing options: `--dir-only` fetches just `pak01_dir.vpk`; `--full-pak` fetches the whole `pak01_*.vpk` set as a fallback.

- `--tools` — also fetch the Workshop Tools depot's (`2347779`) editor-DLL slice (every manifest file ending `.dll` under `game/` — hammer.dll, toolframework2.dll, modtools.dll, …; roughly 200 MB of the ~2 GB depot) and merge it non-destructively into the same per-build **windows** binaries directory, so the walker can register the editor modules' schema projects. Windows-x86_64 only — any other platform is an error. Rides the default unified acquire, a `--from-manifest` spec listing `2347779`, and the batch (each build's inventory `builds[].tools` GID drives the historical fetch; a build without a recorded tools GID is noted and omitted). A historical build needs `--auth`, exactly like binaries; the `2347779` identity is merged into `manifest-record.json` like every other depot. The cache-first hit is tools-aware: over an already-populated build directory whose `manifest-record.json` does not yet list `2347779`, `--tools` acquires only the missing tools slice into that directory (binaries and content untouched — no `--no-cache` re-download); when the record already lists it, the run is a full cache hit.

### VPK and KV parsing

The host parses the content pack itself — no external tooling. `VpkArchive` opens a `pak01_dir.vpk` (VPK version 1 and 2 headers), validates the signature, and reads the directory tree; a truncated or bad-signature VPK is a hard failure. Content files inside are parsed by two first-party parsers: **KV1** (Valve KeyValues text — items, game modes, localization, prop data, map overviews; sometimes UTF-16) and **KV3** (both the text form used for surface properties and collision data, and the small KV3 default-value payload the schema system emits per class). CRC32 is used to content-address and de-duplicate `.gameevents` blobs.

### Protobuf descriptor round-trip

`ProtoDescriptorExtractor` scans the input binaries for embedded `FileDescriptorProto`s, de-duplicates and canonicalizes them, and emits one `.proto` text file per descriptor plus a single serialized `FileDescriptorSet` (`protos.descriptorset`). A real CS2 binary set always embeds descriptors, so zero recovered descriptors is a structural failure that aborts the set. Many CS2 DLLs statically link their own protobuf runtime and embed byte-differing copies of the same well-known dependency (e.g. `google/protobuf/descriptor.proto`); those name collisions are resolved deterministically (the ordinal-first source path wins) and surfaced as a warning, not a failure, so the output is byte-identical across runs regardless of enumeration order.

### Deterministic serialization and validation

Every artifact is emitted in **canonical form**: sorted keys, fixed indentation, LF line endings, UTF-8 without BOM. Proto3 artifacts are serialized with `Google.Protobuf.JsonFormatter` (with default-value formatting on) and then re-sorted into canonical key order; the small number of non-proto3 JSONs go through the first-party `CanonicalJson` helper, which enforces the same shape. Determinism is a hard requirement: the same tool version over the same inputs produces byte-identical files. Timestamps come only from the input manifest, never the wall clock; there are no random GUIDs in output (a GUID appears only in transient temp filenames); and every collection is sorted before serialization.

Each emitter is a validation point: it parses its input and, because the output is written through the generated proto3 message class, an artifact that would not round-trip through its schema cannot be produced. The build-level `verify-artifacts` command re-checks completeness over committed sets — every required file present, every content-gated file present when the content depot is in provenance, `provenance.localization` populated (the build-on-demand `localization.json` is not committed, but its fingerprint must be recorded so an `emit-localization` rebuild is byte-verifiable), and anything legitimately absent recorded in `omissions.json` with a reason.

`localization.json` is the one content artifact produced every dump but **not committed** — at ~199 MB/set it is 96% of the working tree. `extract` still emits it into the staging dir (so extraction and determinism stay exercised, its fingerprint is computed, and its changelog family is diffed), then removes it before the promote. Its `sha256` / `size_bytes` / `token_count` are recorded in `provenance.localization`, and `emit-localization --verify` regenerates it and confirms it is byte-identical to what was dumped.

### Registry audit

`registry_audit.json` is a completeness ledger assembled from two owners. The walker supplies the observed-symbol universe for every family it traverses live in-process (schema classes and enums, console variables and commands, engine constants); the host supplies the `network_message` family from its own offline RTTI scan — the same scan `network_messages.json` is built from, so the audit's view of that family matches the artifact exactly. Every symbol is then marked `extracted` (naming the artifact it went to) or `omitted` (with a category-derived reason). The audit is synthesized after the other emitters have written into the staging directory and cross-checks that no produced artifact carries a symbol the universe never observed.

### Era resolution

A single walker binary cannot read every build, so the host resolves each build to the walker matched to its era before walking (see ARTIFACTS_GENERATION.md for the era model). `EraWalkerResolver` does this as a pure path-and-metadata computation over the single-source era catalog in `data/cs2-assets-inventory.json` — its top-level `eras[]` (each era's kind, hl2sdk pin or ridden pin, per-platform layout signature(s), and class-count band) plus every build's exact `era` id in `builds[]`:

- **Known build** (present in `builds[]`): use that build's exact `era` id — authoritative, no `provenance.json` read.
- **Fresh build** (not in `builds[]`): the newest compile-pin era (`eras[0]`), as optimistic forward-capture; the post-walk second gate then validates the layout.

A **compile-pin** era resolves to the walker binary named after the era id; a **runtime-variant** era reuses the walker of the compile-pin era whose `hl2sdkSha` equals its `ridesCompilePin`, but gates on its own `variantSignature` and class band. The walker binary path is `<NativesRoot>/<platform>/<walkerName>` (`.exe` on `windows-x86_64`).

The resolver refuses to fall back to another era's binary. As defense in depth, after the walk the host compares the layout signature the walker actually emitted against the resolved era's expected signature; a mismatch (wrong era binary, or a genuinely new layout) aborts before any artifact is staged. A build whose resolved era has no validated signature for the running platform also aborts — an unvalidated layout is never accepted.

### Changelog

`extract` writes `changelog.json` inline: it diffs the freshly-staged set against the immediately preceding committed build's set for the same platform and stages the result so it is promoted atomically with everything else. The predecessor is resolved by the same rule `verify-artifacts` uses, so extract's own output satisfies that gate. The floor build (no predecessor) correctly emits none. The standalone `diff` command produces the same file for an explicit `--from`/`--to` pair.

The changelog carries five always-present binary-derived families (classes, enums, convars, commands, engine_constants) plus an optional sixth `localization` family. Because `localization.json` is build-on-demand and not committed, the localization diff cannot read it from the committed predecessor set: the `to` side is this build's freshly-staged `localization.json`, and the `from` side is the predecessor's localization **regenerated on demand from its content** (both discarded after the diff). The localization family is emitted only when both builds produced localization; if the predecessor produced it but its content is no longer in the store, extract fails loud with acquire guidance (nothing is promoted). It diffs by token — `added` / `removed` tokens, and `changed` tokens surface an `englishValue` field change and a `valuesHash` field change (a hash of the per-language values map, so any per-language change is captured without dumping every language).

### Promotion, and where git fits

`extract` runs the full pipeline into a staging directory that is a sibling of the target, and only promotes the complete set into place after every emitter succeeds — so a failure leaves the existing set untouched and never lands a partial one. Off-repo runs promote under the `--out` root; `--commit` clobbers the set into `artifacts/<build>/<platform>/`. Before a promote that would replace a committed set, the host refuses to destroy a content artifact the committed set has that the fresh set omits (the guard against clobbering backfilled content with a binaries-only re-walk).

The host does **not** invoke git. `--commit` writes into the working tree only; staging the changes, committing one complete `(build, platform)` set per commit, tagging, and pushing are done as a separate step (scripts or by hand). The tool's own git identity — the commit SHA baked into `provenance.json` — comes from `Nerdbank.GitVersioning` at build time, never a runtime `git` shell-out.

## The host ↔ walker contract

For each `(build, platform)` the host runs the resolved era binary once as a subprocess (`WalkerProcessRunner`). The invocation is `walk --binaries <dir> --platform <name> --out <tempfile>`; the walker writes a single `walker_output.proto`-shaped file to the temp path (not stdout — stdout is drained and discarded, stderr is captured). The host does not interpret the walker's exit code beyond pass/fail: any non-zero exit — including the walker's own unknown-layout rejection (exit 75) — is surfaced verbatim with the walker's stderr and aborts the extract with no artifacts written. On success the host parses the one intermediate file, runs the second-gate signature check, and every emitter lifts its sub-message from that parsed output. The intermediate is transient and always deleted. Because the walker loads native binaries, it must run on the target platform's OS; the walker side of this contract is in [WALKER.md](WALKER.md).

## Usage patterns

The binaries store root should be set first (see Configuration); the examples assume it is.

**Extract one build (current live build, anonymous).** Auto-acquires the inputs if absent, walks, emits, and promotes into `artifacts/`:

```
cs2-schema-tracker extract --build latest --platform windows-x86_64 --commit
```

**Off-repo trial run.** Same pipeline, but write to `--out` instead of `artifacts/` (useful for inspecting a set before committing):

```
cs2-schema-tracker extract --build 23669931 --platform linux-x86_64 --out ./extract-out
```

**Batch / backfill.** Re-extract every committed build for a platform, or a whole era or pin. This produces the working-tree sets; commit them one build at a time afterward:

```
cs2-schema-tracker extract --all --platform windows-x86_64 --commit
cs2-schema-tracker extract --era cs2-2026-04-21 --platform windows-x86_64 --commit
```

**Historical, authenticated acquisition.** Pre-warm the cache for a prior build the anonymous path can't reach, then extract from cache:

```
cs2-schema-tracker acquire --auth --from-manifest ./spec.json --platform windows-x86_64
cs2-schema-tracker extract --build 12345678 --platform windows-x86_64 --commit
```

**Re-dump after a walker fix.** With a rebuilt era walker archived, re-extract the affected builds (`--force` re-walks off-repo sets that already exist) and verify each is byte-identical to what a fresh walk produces where expected via `--verify`.

**Verify before publishing.** Always gate a publish on the completeness check:

```
cs2-schema-tracker verify-artifacts --artifacts artifacts
```

**Pre-warm the cache.** Acquire binaries (and optionally content) without extracting, e.g. to stage a batch of downloads ahead of a bulk extract:

```
cs2-schema-tracker acquire --all --platform windows-x86_64
```

## Configuration

The host locates its inputs and outputs through a small set of knobs. Environment variables always win over `appsettings.json`, which wins over the built-in default; env vars are read live on each access. `appsettings.json` is discovered next to the executable and by walking up to the repo root. No configuration value ever reaches an emitted artifact byte.

| Setting | Env var | appsettings key | Meaning / default |
|---|---|---|---|
| Binaries store root | `CS2_BINARIES_ROOT` | `BinariesRoot` | Root of the acquired-binaries cache and the `_content` store (`<root>/<build>/<platform>/`, `<root>/_content/<gid>/`). Off-repo by convention (e.g. `/data/cs2-binaries`). When unset, the in-repo `cache/binaries/` convention is used. |
| Natives root | `CS2_WALKER_ERAS_ROOT` | `NativesRoot` | Root of the native per-era walker binaries: one `<platform>/` subdir per target, each holding the per-era walker binaries named by era id (`.exe` on `windows-x86_64`). When unset, the `natives/` directory next to the executable. |
| Walker binary override | `CS2_WALKER_BIN` | `WalkerBin` | Explicit single walker binary that **bypasses era selection** (a deliberate dev/CI escape hatch). When unset, the per-era binary is resolved for each build. |
| Inventory path | — | `InventoryPath` | Path to the single-source assets inventory + era catalog. When unset, `data/cs2-assets-inventory.json` under the repo root. |
| Extract output root | — | `ExtractOutRoot` | Default off-repo output root for non-committed `extract` runs. `--out` overrides it; both are ignored under `--commit`. Falls back to `./extract-out/`. |
| Extract platform | — | `ExtractPlatform` | Default `--platform` when not given on the command line. |
| Steam credentials | `STEAM_USERNAME` / `STEAM_PASSWORD` | — | Authenticated-acquire credentials, loadable from a repo-root `.env`; never logged. Only used with `--auth`. |

Precedence note for the output location: an explicit `--out` on `extract`/`acquire` wins over everything; otherwise `acquire` writes under `CS2_BINARIES_ROOT`/`BinariesRoot` when set (the same place `extract` reads), and `extract`'s off-repo default is `ExtractOutRoot` then `./extract-out/`. Under `--commit`, `extract` always writes to `artifacts/<build>/<platform>/` regardless of the output settings.
