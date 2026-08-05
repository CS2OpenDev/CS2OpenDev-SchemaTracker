# Building CS2-Schema-Tracker

Hybrid project: a C++ schema-walker kernel (`walker/`) and a .NET host (`host/`). This document
covers building every artifact the project ships — the per-era walker binaries, the host (a plain
build **and** the self-contained publish), and the Docker runtime image (including how to run it) —
and when to reach for each build path.

There are three build outputs, produced in **dependency order**:

1. **Era walkers** → `natives/<platform>/<era>[.exe]` — the per-era C++ binaries the host launches.
2. **Host** → a `dotnet build` for local use, or a self-contained `dotnet publish` for Docker / distribution.
3. **Docker image** — a runtime wrapper that packages the *published* Linux host + the *prebuilt* Linux natives.

The Docker image consumes the self-contained publish (2) and the Linux natives (1), so build those first.

## Prerequisites

- **.NET 10 SDK** — builds, tests, and publishes `host/` (pinned in `global.json`).
- **CMake** + a C++ toolchain — builds `walker/`: MSVC (Visual Studio 2022) on Windows, `g++` on Linux/WSL.
- **libprotobuf (C++)** — from vcpkg on Windows (`$VCPKG_ROOT`), from apt on Linux (`libprotobuf-dev protobuf-compiler`).
- **HL2SDK submodule** — `walker/third_party/hl2sdk` (cs2 branch), pinned. It is large; initialize it only when you actually build the walker:
  ```bash
  git submodule update --init walker/third_party/hl2sdk
  ```
- **Docker** — only for the runtime image.

### First-time Windows bootstrap

```powershell
scripts/bootstrap-windows.ps1 -Stage All        # all toolchains (+ optional WSL2 for Linux extraction)
scripts/bootstrap-windows.ps1 -Stage Protobuf   # just vcpkg at C:\tools\vcpkg + protobuf:x64-windows
```

> winget's `protoc` is compiler-only (no C++ runtime / headers / CMake config), so `find_package(Protobuf)` cannot resolve against it — the walker needs vcpkg's libprotobuf, which ships the matched `protoc` + library pair.

## Walker (C++)

Two ways to build, depending on what you need: a single dev build for iteration, or the per-era set the host actually ships.

### Single dev build (fast iteration + unit tests)

Builds one walker against the currently checked-out hl2sdk pin. Use it while developing the C++ kernel or to run the walker unit tests.

**Windows (MSVC).** Specify the Visual Studio generator — the default NMake generator fails outside the Developer Command Prompt — and pass the vcpkg toolchain so `find_package(Protobuf)` resolves to vcpkg's matched protoc + libprotobuf:

```powershell
cmake -G "Visual Studio 17 2022" -A x64 `
      -DCMAKE_TOOLCHAIN_FILE=$env:VCPKG_ROOT\scripts\buildsystems\vcpkg.cmake `
      -DVCPKG_TARGET_TRIPLET=x64-windows `
      -S walker -B walker/build
cmake --build walker/build --config Release
ctest --test-dir walker/build -C Release --output-on-failure
```

**Linux / WSL (g++).** libprotobuf from apt — no toolchain file needed:

```bash
cmake -S walker -B walker/build
cmake --build walker/build
ctest --test-dir walker/build
```

### Per-era walkers (the shippable set)

The host launches a **per-era** walker for each build. `build-era-walkers` builds one binary per
compile-pin era — checking out that era's hl2sdk pin, building, running ctest, asserting the build-time
layout signature, and installing the result into `natives/<platform>/`:

```powershell
# Windows -> natives/windows-x86_64/<era>.exe   (uses $env:VCPKG_ROOT; auto-detects the installed VS)
pwsh scripts/build-era-walkers.ps1
```

```bash
# Linux / WSL -> natives/linux-x86_64/<era>     (portable build; VCPKG_ROOT is REQUIRED)
scripts/build-era-walkers.sh
```

> `-Force` (ps1) / `--force` (sh) rebuilds eras already built; `-Era <id>` / `--era <id>` limits the run to one era.
> Build the Linux natives on **Ubuntu 24.04 (WSL)** to match the Docker base's glibc/GLIBCXX. The walkers link
> libprotobuf dynamically and load a sibling copy via `$ORIGIN`; that copy is bundled only when `VCPKG_ROOT`
> is set, and the Docker image's `ldd` guard rejects natives built without it.

## Host (.NET)

### Dev build + tests

```text
dotnet build host/Cs2SchemaTracker.sln
dotnet test  host/Cs2SchemaTracker.sln
```

### Release build — for running extraction locally

A plain Release build produces a stable executable to drive extraction on the current machine. Build it
from the working tree so its provenance SHA (stamped from the repo's `.git`) is correct:

```powershell
dotnet build host/src/Cs2SchemaTracker.Host -c Release
# -> host/artifacts/bin/Cs2SchemaTracker.Host/release/cs2-schema-tracker[.exe]
```

### Self-contained publish — for Docker / distribution

Reach for `dotnet publish` when you need a host that carries its own .NET runtime — a standalone binary
that runs on a machine or container with no SDK installed. This is exactly what the Docker image packages.
Run it from a working tree where `.git` exists so the nbgv provenance SHA is stamped correctly:

```bash
dotnet publish host/src/Cs2SchemaTracker.Host/Cs2SchemaTracker.Host.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishTrimmed=false -p:UseAppHost=true -p:BundleRelease=false \
  -o dist/docker/host
```

> Swap `-r linux-x64` for `-r win-x64` to publish a standalone Windows host. Use `dotnet build` for local
> dev and local extraction; `dotnet publish --self-contained` when the target machine has no SDK installed.

## Docker image (Linux runtime wrapper)

The image **builds nothing** — it packages the already-published Linux host and the already-built Linux
natives onto an `ubuntu:24.04` base. Build it only after the self-contained publish and the Linux natives
exist:

```bash
# Context = repo root. Expects in the context:
#   dist/docker/host/               the self-contained linux-x64 host (publish, above)
#   natives/linux-x86_64/           the prebuilt Linux era walkers (+ bundled libprotobuf.so.*)
#   data/cs2-assets-inventory.json  the era catalog / inventory
docker build -f docker/Dockerfile -t cs2-schema-tracker:latest .
```

**Why Ubuntu 24.04.** The base matches the toolchain the walkers are built with. The era walkers (and
vcpkg's libprotobuf) compile on Ubuntu 24.04 (glibc 2.39) and require up to `GLIBC_2.38` /
`GLIBCXX_3.4.32`, so the base must be Ubuntu-24.04-era — an older base (e.g. the Steam Runtime "sniper"
= Debian 11 / glibc 2.31) can't run a binary built on a newer distro. It's also the exact environment the
re-dump runs in, so the walk stays byte-consistent with the committed artifacts, and the CS2 binaries the
walker `dlopen`s load fine here given the libs the image installs. The container itself is portable at the
Docker level — any Docker host runs it regardless of its own glibc, because the base image provides it.
(If you instead need walker *binaries* that run standalone on old distros, build the natives inside the
`sniper/sdk` image against glibc 2.31 and use a Sniper base — a separate, heavier setup.)

**The natives must be the portable build.** As noted above, the walkers load a sibling `libprotobuf.so`
via `$ORIGIN`, and that copy is bundled only when `VCPKG_ROOT` was set at build time (release artifacts
are). The base image's system protobuf soname differs from the walker's bundled version, so a
non-portable natives build would fail to load in the container. The Dockerfile `ldd`s a walker at build
time and fails loud on any unresolved library, so use the release natives or rebuild locally with
`VCPKG_ROOT` (`rebuild-and-rewalk.ps1 -VcpkgRoot …`) first.

### Running the image

The container needs the **binaries store** mounted read-only, and — to commit — the **repo** mounted
read-write. Layout of the store is `<root>/<build>/<platform>/game/bin/linuxsteamrt64/…`.

Re-walk every committed Linux build and commit the results into the repo:

```sh
docker run --rm \
  -e CS2_REPO_ROOT=/repo \
  -e CS2_BINARIES_ROOT=/binaries \
  -v "$PWD:/repo" \
  -v "/srv/games/Counter-Strike 2/cs2-binaries:/binaries:ro" \
  cs2-schema-tracker:latest \
  extract --all --platform linux-x86_64 --no-acquire --commit
```

- `CS2_REPO_ROOT=/repo` makes the host enumerate the mounted repo's `artifacts/` and load the inventory
  from its `data/`; the image's `WORKDIR /repo` makes `--commit`'s cwd-relative `artifacts/` land in that
  same mount. (Both are needed — input via the env, output via cwd.)
- Add `--user "$(id -u):$(id -g)"` if the committed files should be owned by your user.

Fix just one era, or run standalone against a plain output dir (no repo mount — the host then uses the
era catalog baked into the image):

```sh
docker run --rm -e CS2_REPO_ROOT=/repo -e CS2_BINARIES_ROOT=/binaries \
  -v "$PWD:/repo" -v "/srv/games/Counter-Strike 2/cs2-binaries:/binaries:ro" \
  cs2-schema-tracker:latest \
  extract --era cs2-2023-09-13 --platform linux-x86_64 --no-acquire --commit

docker run --rm -e CS2_BINARIES_ROOT=/binaries \
  -v "/srv/games/Counter-Strike 2/cs2-binaries:/binaries:ro" -v /tmp/out:/out \
  cs2-schema-tracker:latest \
  extract --build 12312218 --platform linux-x86_64 --out /out --no-acquire
```

- **Windows builds are not covered** — a Linux container cannot run the Windows `.exe` walkers (they are
  PE binaries). Windows extraction needs a Windows host/runner; the image intentionally carries only
  `natives/linux-x86_64/`.
- **`--no-acquire`** requires the input binaries to already be in the mounted store; drop it to let the
  host download missing builds (needs Steam auth + network).
- **Reproducibility:** pin the base image by digest (`FROM ubuntu:24.04@sha256:…`) instead of `:latest`.
- The six system libs the image installs (`libx11-6 libbz2-1.0 libdrm2 libuuid1 libva2 libvdpau1`) are
  documented in [docs/WALKER.md](docs/WALKER.md) → "Linux runtime system-lib prerequisite".

## Extractor identity gate

Every `extract` run resolves each selected build's per-era walker binary and asks it `--version`
(`WalkerIdentity`, host-side), printing a one-line startup banner —
`extract: tool=<host git sha> walkers=<fingerprint|mixed|unknown>` — before any build is walked. A
mixed or unverified walker set (differing content fingerprints across eras, or any era whose binary
predates the fingerprint line and reports `unknown`) hard-fails `--commit` at exit 78 with a per-era
table (escape hatch: `--allow-mixed-walkers`, never for a corpus-committing run); an off-repo run only
warns and proceeds. This is what makes a stale or partially-rebuilt `natives/` set loud instead of
silently poisoning the corpus. For a remote/CI run (e.g. inside the Docker image), export
`CS2_EXPECT_FPRINT=<fingerprint prefix>` to the value you built there — a mismatch hard-fails
unconditionally (exit 78, not bypassed by `--allow-mixed-walkers`), catching a stale deployed image
before it can walk anything with the wrong binaries.

## Schemas (protoc)

Check that the proto family compiles. Use a real temp-file path — passing `NUL` creates a literal `NUL` file on Windows:

```bash
# Linux / WSL
for f in schemas/*.proto; do protoc --proto_path=schemas --descriptor_set_out=/dev/null "$f"; done
```

```powershell
# Windows PowerShell
Get-ChildItem schemas\*.proto | ForEach-Object {
  protoc --proto_path=schemas --descriptor_set_out="$env:TEMP\protoc.bin" "schemas/$($_.Name)"
}
```
