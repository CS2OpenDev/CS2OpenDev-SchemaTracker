# The walker

The walker (`walker/`) is the small C++ kernel that reads a CS2 build's binaries. It loads the shipped Source 2 DLLs into its **own** process, walks Valve's live C++ object graphs — the schema system, the console-variable registry, the message registries — and writes a single intermediate file the .NET host then merges into the artifact set.

This document goes into the walker's mechanics. For where the walker sits in the pipeline and the operational era model, see [ARTIFACTS_GENERATION.md](ARTIFACTS_GENERATION.md); for the host that drives it, see [HOST.md](HOST.md).

## Why it's a separate C++ kernel

Source 2's schema metadata is not a table you can statically parse out of a DLL. It is *built at runtime*: each module's C++ static initializers construct the class/enum/field descriptors on the heap when the module loads and registers, and the console variables are only created when the owning subsystem's `Init()` runs `ConVar_Register()`. To read any of it you have to **execute** the game's own code and then walk the resulting object graph in memory.

That forces two things:

- **It must run native, in-process.** The metadata lives at real addresses inside loaded modules; the only way to reach it is to load those modules and dereference their pointers. The walker `dlopen`/`LoadLibrary`s the DLLs into itself — it does **not** attach to or inject into a running game, and it never launches CS2.
- **It must use the real C++ struct layouts.** Reading a `CSchemaClassInfo` or a `SchemaClassFieldData_t` correctly means knowing each field's exact offset for the compiler and version that built the binary. Those layouts come from [`alliedmodders/hl2sdk`](https://github.com/alliedmodders/hl2sdk) (cs2 branch), pinned as a submodule at `walker/third_party/hl2sdk`. This is the one third-party CS2-domain build input, and the reason the kernel is C++ rather than part of the C# host: it links the SDK headers directly and reads Valve's structs with zero marshaling or duplicated layout declarations.

The walker is a clean-room implementation. It uses the same *technique* as `ValveResourceFormat/DumpSource2` (in-process `dlopen` → `CreateInterface` → `InstallSchemaBindings` → schema walk), but no upstream source is copied; every struct layout it reads comes straight from the pinned hl2sdk headers.

## How a walk runs

A walk is one invocation over one platform's binaries. It loads *all* modules together — client, server, engine, and the schema-bearing subsystems — so "client vs. server" is captured per class in a `module` tag rather than by separate runs. The sequence:

1. **Probe the layout first.** Before loading anything, the walker computes its own compile-time layout signature and checks it against a validated allow-list. An unknown layout aborts here, before any modules load (see [The layout probe](#the-layout-probe)).
2. **Load the modules.** The loader walks a curated, dependency-ordered allow-list of schema-bearing modules (not the whole directory — the bin dirs also carry rendering/audio/codec/Qt modules with external dependencies a headless process can't satisfy and no schema to offer). It resolves the standard `CreateInterface` factory each module exports, obtains the live `CSchemaSystem*` via `CreateInterface("SchemaSystem_001", …)`, and grabs the `ICvar` and `INetworkMessages` handles. The loader treats every interface as an opaque `void*`, so it stays free of hl2sdk headers; only the individual walk translation units cast back to the real types. A required module missing on disk, a missing factory export, a null schema system, or a load failure of any allow-listed module all abort the run.
3. **Force schema registration.** Each Source 2 module exports `InstallSchemaBindings`; calling it drives that module's static schema bindings to register against the live schema system. This is done per module at load time.
4. **Best-effort partial engine boot** (for console variables). ConVars/ConCommands don't exist until a subsystem's `Init()` registers them, so the walker brings up a slice of the engine: it Connects every loaded module through an *incremental real-interface factory* (each interface a later module asks for is answered with the real pointer of an earlier module already Connected, plus a minimal real `ICvar`, `CSchemaSystem`, and `IApplication`), then `Init()`s the game-config set (`host`, `matchmaking`, `server`, `client`) so each one flushes its convars into the live `ICvar`. A naive stub factory access-violates — later modules dereference interfaces a stub would null — so the factory minimizes nulls by handing back real pointers. Two reversible, boot-window-only crash patches keep the Init from faulting (a no-op'd `ICvar` change-callback slot and a forced-`false` pixel-visibility convar), both installed via `VirtualProtect`/`mprotect` and restored on scope exit. If the registry is still empty after Init, the boot fails loud rather than emit an empty `convars.json`.
5. **Post-boot registration retry** (older builds only). On the earliest builds the partial boot doesn't populate the schema, so if the schema system is still empty the walker retries `InstallSchemaBindings("SchemaSystem_001", …)` — passing the interface-version string as the registration handshake the older era expects. This is gated on the schema system being empty (a vtable-only probe, so it doesn't depend on a drifting field offset), which means it is a strict no-op on modern builds and leaves their output unchanged. Running it *after* the boot matters: doing it earlier perturbs the boot's convar registration.
6. **Walk the object graphs** into an in-memory proto (see [What it extracts](#what-it-extracts)).
7. **Serialize and write atomically.** The whole proto is built in memory, serialized, written to a sibling temp file, then renamed into place. Nothing is written until the walk fully succeeds.

On success the walker **hard-exits** immediately after the file is renamed, skipping normal teardown. This is deliberate: the booted engine modules fault during DLL detach while unregistering ConVar change callbacks (a known Source 2 headless-teardown issue), which would corrupt an otherwise-clean exit code and drop a spurious minidump. On Windows it calls `TerminateProcess` (which `ExitProcess`/`_Exit` do not fully avoid, because they still run `DllMain(DLL_PROCESS_DETACH)`); on Linux it calls `_Exit`. Failure paths exit the same way, carrying the deterministic non-zero code, so an expected fail-loud also skips the crashy teardown. The output file is already on disk before either exit, so nothing is lost.

## What it extracts

Everything below is emitted into one `walker_output.proto` message (`schemas/walker_output.proto`). Every collection is sorted by a stable key before it's added, so output is byte-identical across re-runs — the live schema-system and convar containers have undefined iteration order, which must never leak into the artifact.

| Walk | Source | Notes |
|---|---|---|
| **Entity schema** | `CSchemaSystem` object graph | Every registered type scope → classes/structs and enums; per class the fields (name, offset, recursive `SchemaType`, size), the parent chain, and reflection metadata carried **verbatim** — including the raw KV3 `MGetKV3ClassDefaults` string. The walker does *not* parse the KV3; the host does the structural parse. |
| **Console variables + commands** | `ICvar` / `ConCommandBase` registry | Per convar: name, default (rendered locally from the raw value union), flag names, help text, and type/bounds where present. Per command: name, flags, description. Populated by the partial engine boot above. |
| **Engine constants** | Schema enumerators | Every registered enum's members are binary-named integer constants: name is `"<Enum>::<Member>"`, value is read verbatim. Read through the same object graph and headers as the entity walk — nothing inferred. |
| **String pools** | (reflection-reachable interned pools) | Emitted **empty by design**. CS2's schema system interns no strings through an enumerable pool; this was confirmed by reverse-engineering, so an empty pool list is the complete, correct answer, not a gap or an error. |
| **Module manifest** | Boot observations | Per module: the `CreateInterface` versions it actually resolved at boot (the only place that's observable). The host joins this onto `modules.json`, which it fills out with each binary's hash/size/export count. |
| **Registry universe** | Independent live re-traversal | A superset ledger of every named symbol the walk *observed* — classes, enums, convars, commands, constants — enumerated independently of the extraction above (reusing the same low-level name/module readers so keys can't drift). The host diffs this against what actually landed in artifacts to synthesize `registry_audit.json`; a symbol the live registry has but extraction dropped still shows up here, so the audit is a genuine completeness check rather than a circular one. |

**`MGetKV3ClassDefaults` is recovered by a live call, not a verbatim read.** Its `m_pData` is a generated per-class accessor thunk; the walker calls it and serializes the resulting `KeyValues3` via tier0 `SaveKV3AsJSON` (guarded, watchdog'd, determinism-filtered). That accessor ABI is only valid for eras **`cs2-2025-07-31` and newer** (windows recovers 2400–3006 values/build; linux is byte-identical). For the older KV3-bearing eras (`cs2-2024-02-07` … `cs2-2025-03-20`) the ABI does not hold — the call recovers nothing on windows and crashes on linux — so those eras are marked `"kv3ClassDefaults": false` in the inventory and the host passes `CS2_WALKER_NO_KV3_DEFAULTS` to skip the call (the value is emitted empty, deferred-with-reason). `cs2-2023-*` carry no such metadata at all. A standalone walker run (no host) does **not** apply this gate — set `CS2_WALKER_NO_KV3_DEFAULTS=1` yourself when walking a pre-`2025-07-31` build.

The network- and demo-message tables are *not* filled by a live walk — see the next section. The walker still carries a legacy `network_messages` field in its output, but it's a retiring vestige the host no longer reads.

## The RTTI scanners

`network_messages.json` and `demo_messages.json` are produced by **offline static RTTI scans of the binaries**, not by a live walk.

The reason is that the live registries these tables would come from aren't reachable headless. Populating `INetworkMessages` needs essentially the whole engine `Init()`, which access-violates on the partial boot; and even reading it would mean calling vtable slots whose layout is pin-specific, so an ungated call on an other-era build faults. So instead of a live read, the scanners exploit a static fact: CS2 registers a message *by instantiating a C++ template* for it, and each instantiation leaves an RTTI type descriptor in the binary's read-only data. A message appears if and only if the build instantiated its template — dead or unregistered proto entries have no instantiation and are excluded by construction.

- **`network_messages`** parses every `CNetMessagePB<id, type, …>` RTTI descriptor, decoding the id and message type out of the mangled template argument list. It cross-validates against the independent `CUserMessagePB<id, type, …>` instantiations for the same messages and fails loud on any id/type disagreement.
- **`demo_messages`** parses `CDemoMessagePB<id, type>` (the `.dem` demo-stream command table); those instantiations live in `engine2`. Its id space is flat, and **id 15 is a dual** — two message types bind it (a spawn-groups command and an HLTV-broadcast variant); both rows are kept, exactly as the network scan keeps the `DisconnectToLobby` 335/374 dual.

Both scanners are pure `read-file` + parse (no DLL load, no engine), decode both ABIs — MSVC name-mangling for `windows-x86_64` and Itanium `type_info` mangling for `linux-x86_64`, through a shared decoder so the two platforms can't drift — and per the cross-platform invariant the decoded id set is identical on both. Zero decoded messages from a real binary set, or an unimplemented platform mangling, aborts before any output. In the current codebase these scanners live in the host (they need no walker process, only the binary bytes), but they are the binary-reading counterpart to the live walk and share its fail-loud discipline.

These scans give the wire-ID→message-*type-name* binding, but the message *schemas* (the `.proto` definitions those names refer to) come from `protos.descriptorset`. CS2 embeds a serialized `FileDescriptorProto` for most of its protos, but **not** for the engine wire-message families the RTTI table binds — `netmessages`, `usermessages`, `cstrike15_usermessages`, `clientmessages`, `gameevents`, `cs_gameevents`, `networkbasetypes`, `te`. This is proven, not assumed: a byte-scan across all 182 shipped binaries on both platforms recovers 33 embedded descriptors and none of these eight. Without them every `protoMessageType` in `network_messages.json` would reference a message defined nowhere in the set (only 3 of 191 resolve). They are therefore compiled from the pinned hl2sdk submodule (the same SDK the walker's struct layouts come from) into the committed `data/wire_descriptors.pb`, and the host's proto extractor merges them into `protos.descriptorset` — but *only* for names the binaries didn't already provide, so a binary-derived descriptor is always canonical. With them merged, the join resolves 191/191 (and `demo_messages.json` 19/19). Each SDK-sourced `.proto` is stamped with a provenance header; regenerate the set with `scripts/gen-wire-descriptors.sh` after bumping the hl2sdk pin.

## The per-era model, mechanically

A single walker can't read every CS2 build: the game's C++ struct layouts drift over time, and a walker compiled against the wrong layout reads garbage or crashes. So the tool compiles **one walker per era**, an era being a span of builds that share a layout. The era catalog lives in `data/cs2-assets-inventory.json`: a top-level `eras[]` maps each era → its hl2sdk pin → the layout signatures its walker expects per platform, and each build record carries its exact `era`.

There are two mechanisms:

**Compile-pin eras** (the common case). The era pins a specific hl2sdk commit whose declared struct layouts match that era's binaries; the walker is compiled against those headers and archived per platform. When a game update drifts the layout past the current era, a new pin is found, validated against the newest build, registered in the inventory `eras[]` and the allow-list, and its walker built and archived. Note that an update can move a layout the walker touches *without* changing the schema record headers — e.g. an `ICvar`/`CCvar` vtable change crashes the old walker on the convar walk while the schema fingerprint is unchanged; that still gets its own pin (and thus its own allow-list entry and per-era binary), because the fix is recompiling the affected path against the updated headers.

Because the era's hl2sdk pin is the functional key, the walker absorbs SDK-version drift through a set of **CMake configure-time probes** that test-compile a small snippet against the checked-out submodule and select the right code path — for example whether the ConVar API exposes the newer `ConVarData`/`ConVarRef` surface or the older `ConVarHandle` one, whether tier0's spin-lock is a `dllimport`, and whether `schematypes.h` uses the newer or older enum/member spellings. Each probe's "new" branch is a byte-identical passthrough to the existing code; the "old" branch resolves to a per-era layout mirror transcribed field-for-field from that pin's header. (These are cached across reconfigures, so switching the pinned submodule between eras requires clearing the stale cache values or the build dir.)

**Runtime-layout variants** (the early-2023 builds). The 2023-era builds predate any hl2sdk schema headers — no pin *declares* their structs — so they can't be given a compile pin of their own. Instead they ride a modern compile pin and recover the older layout from **reverse-engineered offset tables selected at runtime**. Because they share a modern pin, their compile-time signature is identical to that pin's `current` era, so they can't be discriminated by the compile-time allow-list; they're discriminated *at runtime* by which RE offset table validates against the live DLL's records, and gated by a separate runtime-signature allow-list. When a build's schema is populated only through the older `SchemaSystem_001` registration handshake, the walker runs an N-way probe (`DetectSchemaVariant`) that tries the modern interpretation first (short-circuiting so modern builds stay byte-identical), then each known RE table; a match routes the whole walk through that era's offset accessors, and no match fails loud.

**Platform note.** For the 2023 era the schema *records* themselves are laid out identically by MSVC and g++ — the same field offsets on both OSes, so the record readers are shared and produce cross-OS-identical class/enum/constant output. What differs per platform is the `CUtlTSHash` / container *geometry* (how the bindings tables are shaped in memory); the offset tables account for that difference while the record layer stays common.

## The layout probe

The probe is what enforces "never guess." It has two forms, matching the two era mechanisms.

**Compile-time signature.** `ComputeLayoutSignature()` is a pure function of the pinned hl2sdk struct layout plus the submodule commit SHA. It hashes an `offsetof()` of every field the walk actually dereferences — across `CSchemaClassInfo`/`SchemaClassInfoData_t`, `SchemaClassFieldData_t`, `SchemaBaseClassInfoData_t`, `SchemaMetadataEntryData_t`, `SchemaEnumInfoData_t`, `SchemaEnumeratorInfoData_t`, and `CSchemaType` — into a signature of the form `hl2sdk-cs2/<sdk_sha>/v1/<16-hex-fingerprint>`. If any field the walker touches moves, the fingerprint changes. The signature is deliberately encoded as "the layout I will dereference against": two walker builds that read identical offsets produce identical signatures, and any SDK bump that shifts a touched field produces a different one. It is **ABI-specific** — a Windows (MSVC) and a Linux (g++) walker compute *different* fingerprints for the same pin — so the allow-list holds both platforms' signatures and each walker checks membership for the one it computed.

**Runtime signature.** For the 2023 runtime variants the fingerprint is instead over the *derived RE offset table* (the record/pool/bucket constants), of the form `re-<tag>/v1/<fnv16>`. This lives in a second, disjoint allow-list so the two spaces never collide.

**The rule.** Both allow-lists are default-deny: a layout the walker hasn't been validated against is rejected. On the compile-time path, a `walk` or `probe-layout` invocation computes the signature and, if it's not in the allow-list, prints it to stderr and exits non-zero (75) — it never loads modules against an unknown layout. On the runtime-variant path, a schema that populates only through the older handshake but matches *neither* modern nor any known RE table also fails loud with the observed signature. Either way, an unrecognized layout aborts loudly rather than dumping garbage under guessed offsets. The host runs its own second gate on the probe output, and a walker-side ctest keeps the allow-list and the inventory `eras[]` in lockstep.

## Platform specifics

The walker builds with MSVC on Windows and g++ on Linux, and — per the requirement that a walk runs on a host whose OS+arch matches the target — Linux artifacts are produced on Linux and Windows artifacts on Windows.

The main behavioral difference is fault handling around the risky native calls (the data-subsystem `Connect`/`Init` during boot, the `InstallSchemaBindings` exports, and the leaf memory reads in the convar mirror scan). These calls can fault on a module that bails or an offset that's slightly off, and the walker's design is to *skip the faulting call and continue* rather than die:

- **Windows** uses SEH `__try`/`__except` leaves at each such call site.
- **Linux** (Itanium ABI, no SEH) uses a `sigaction`-based guard (`posix_crash_guard.h`): a `sigsetjmp`/`siglongjmp` pair that turns a fault inside a POD-only guarded callback into a "faulted" return, restoring the previous signal handlers and mask afterward. A lighter persistent variant (`SafeProbeCopy`) guards the thousands of leaf reads the adaptive convar/command memory-mirror does while it solves the container geometry against known convar canaries — on Windows those reads are SEH `__try` leaves; on Linux the bare `memcpy` would SIGSEGV on a garbage candidate address, so the guard turns it into a `false` and lets the self-validating scan run to completion. A fault *outside* any active guard restores the default disposition and re-raises, so a genuine walker bug still crashes normally and is never silently swallowed. The whole POSIX guard header compiles to nothing on Windows, so the Windows binary is unaffected.

The hard-exit-on-teardown behavior described earlier is also platform-split: `TerminateProcess` on Windows (to skip `DllMain` detach entirely), `_Exit` on Linux (to skip `.so` finalizers).

### Linux runtime system-lib prerequisite

A walk `dlopen`s the build's CS2 modules, which transitively pull in the libraries those modules were linked against. Most of those — including the heavy multimedia stack (`libavcodec.so.58`, `libavformat`, `libavutil`, `libvpx`, `libswscale`, `libvideo.so`) — are **shipped inside the build's own bin dirs**, and the host makes them resolvable by prepending `game/bin/linuxsteamrt64` and `game/csgo/bin/linuxsteamrt64` to `LD_LIBRARY_PATH` before launching the walker (`WalkerProcessRunner`), the POSIX counterpart to the Windows `AddDllDirectory` pass. So **no system FFmpeg install is needed** — the walk uses the exact sonames the build was built against (e.g. `*.so.58`, which modern distros no longer package).

What the build does **not** ship is a small set of standard system libraries the shipped modules depend on transitively. On the oldest layouts (eras `cs2-2023-03-22` / `cs2-2023-09-13`), the **required** `client` module → `libvideo.so` → the FFmpeg/graphics stack, and `panoramauiclient` → the VAAPI backends, need these present on the host, or the module fails to `dlopen` and the walk fails loud:

`libX11.so.6`, `libbz2.so.1.0`, `libdrm.so.2`, `libuuid.so.1`, `libva.so.2`, `libva-drm.so.2`, `libva-x11.so.2`, `libvdpau.so.1`

Install them once in any Linux extraction environment (local, self-hosted, or a CI linux-extract job — they are already preinstalled on GitHub `ubuntu-latest`):

```sh
# Debian/Ubuntu
sudo apt-get install -y libx11-6 libbz2-1.0 libdrm2 libuuid1 libva2 libva-drm2 libva-x11-2 libvdpau1
# RHEL/Fedora (libva bundles the drm/x11 backends)
sudo dnf install -y libX11 bzip2-libs libdrm libuuid libva libvdpau
```

On Debian/Ubuntu the `libva-drm2` / `libva-x11-2` backends are separate packages that normally arrive as a `va-driver-all` *recommends* — a lean install (`--no-install-recommends`, e.g. the Docker image) must list them explicitly. Newer builds do not pull this chain, so only extractions that touch the oldest eras require the libraries; installing them unconditionally is the simplest rule.

## Output and exit behavior

The walker emits exactly one file per invocation, in the `walker_output.proto` shape (`schemas/walker_output.proto`) — a serialized protobuf carrying the raw walk observations plus `schema_version`, `walker_version`, `platform`, and the probed layout signature. It carries *only* what the walker can observe; identity the host owns — `build_id`, Steam changelist, provenance — stays out of this file and is stamped by the host when it writes the public artifacts. That intermediate output schema is versioned together with the public artifact schemas, so any change to its shape is coordinated with the schema and host owners.

The exit contract is fail-loud and all-or-nothing: any failure before the file is written (unknown layout, missing module, null interface, empty convar registry, an underived older layout) exits non-zero with the error on stderr and **zero** output bytes. On success the file is built entirely in memory, serialized, written to a temp sibling, and atomically renamed — so a reader never sees a partial file — and the process then hard-exits 0. The CLI surface is:

| Command | Purpose |
|---|---|
| `walk --binaries <dir> --platform <P> --out <file>` | Load, boot, walk, and write the output file. |
| `probe-layout --binaries <dir>` | Run the layout probe against a binary set; print the signature, exit non-zero (75) if unknown. |
| `--print-signature` | Print this walker's compile-time layout signature (a pure function of the pin; touches no binaries). |
| `--version` | Print version, git SHA, and schema-family version. |
