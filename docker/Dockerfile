# syntax=docker/dockerfile:1
#
# Runtime image for the CS2 SchemaTracker host + Linux era walkers.
#
# BASE = ubuntu:24.04 to MATCH the toolchain the walkers are built with. The era walkers (and
# vcpkg's libprotobuf) are compiled on Ubuntu 24.04 (glibc 2.39, GLIBCXX 3.4.33) and require up to
# GLIBC_2.38 / GLIBCXX_3.4.32, so the base must be Ubuntu 24.04-era. (An older base such as the
# Steam Runtime "sniper" = Debian 11 / glibc 2.31 cannot run them — a binary built on a newer distro
# can't run on an older one. Running in Sniper would require building the walkers inside the Sniper
# SDK; we build in WSL, so we match the base to that.) This is also exactly the environment the
# re-dump runs in, so the walk is byte-consistent with the committed artifacts. The CS2 binaries the
# walker dlopen's load fine here — proven by the Ubuntu-24.04 re-dump — given the libs installed
# below. The container is portable at the Docker level: any Docker host can run it regardless of its
# own glibc, because the base image provides it.
#
# This image is a RUNTIME WRAPPER — it builds nothing:
#   * the per-era walkers need the hl2sdk + the per-era build harness (not a Docker concern),
#   * the host's git-SHA provenance stamp (nbgv) needs the repo's .git.
# So publish the self-contained host on the machine (where .git exists, giving a correct SHA) and
# have the prebuilt Linux natives present, THEN build this image. See BUILD.md "Docker image".
#
# Build context = repo root. The COPYs below expect, in the context:
#   dist/docker/host/                   the published self-contained linux-x64 host
#   natives/linux-x86_64/               the prebuilt Linux era walkers (+ bundled libprotobuf.so.*)
#   data/cs2-assets-inventory.json      the era catalog / inventory
# (.dockerignore keeps the heavy dirs and the bare .so dev symlinks out of the context.)

FROM ubuntu:24.04

# Three groups of runtime libraries:
#   1. the CS2-transitive graphics/video libs the oldest eras pull in (client -> libvideo.so ->
#      FFmpeg stack; panoramauiclient -> the VAAPI backends libva-drm/libva-x11). These are the full
#      non-shipped closure of the old-era modules; see docs/WALKER.md "Linux runtime system-lib
#      prerequisite". (--no-install-recommends means the libva-drm2/libva-x11-2 backends must be
#      listed explicitly — they'd otherwise arrive only as a `va-driver-all` recommends.)
#   2. the walker's own base deps NOT guaranteed in a minimal image (libstdc++6 for GLIBCXX, zlib1g
#      for libz) — the bundled libprotobuf.so.* also needs GLIBCXX_3.4.32 from libstdc++6,
#   3. libicu74 for the self-contained .NET host's globalization.
# (The build's heavier libs — libavcodec.so.58 etc. — ship inside each CS2 build and resolve at run
# time via LD_LIBRARY_PATH set by the host; no system FFmpeg is needed.)
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libx11-6 libbz2-1.0 libdrm2 libuuid1 libva2 libva-drm2 libva-x11-2 libvdpau1 \
      libstdc++6 zlib1g libicu74 \
 && rm -rf /var/lib/apt/lists/*

ARG APP=/opt/cs2-schema-tracker

# The self-contained host bundles its own .NET runtime; only the low-level libs above are needed.
COPY dist/docker/host/              ${APP}/
COPY natives/linux-x86_64/          ${APP}/natives/linux-x86_64/
# Baked as <default-repo-root>/data/... so a standalone (no-repo) run still resolves the era
# catalog. A `--commit` run against a bind-mounted repo overrides this via CS2_REPO_ROOT (below).
COPY data/cs2-assets-inventory.json ${APP}/data/cs2-assets-inventory.json

RUN chmod +x ${APP}/cs2-schema-tracker ${APP}/natives/linux-x86_64/cs2-*

# Fail the build LOUD if a walker can't load in this image. The era walkers link libprotobuf
# dynamically and load their OWN copy via $ORIGIN; that sibling .so is bundled only when the natives
# are built with VCPKG_ROOT (scripts/build-era-walkers.sh). The system libprotobuf here differs from
# the walker's bundled version, so natives WITHOUT the bundled lib — or built against a newer
# glibc/GLIBCXX than this base — would fail to load at run time. Assert a sample walker's deps all
# resolve now.
RUN set -e; \
    W="$(ls ${APP}/natives/linux-x86_64/cs2-* 2>/dev/null | head -1)"; \
    test -n "$W" || { echo "FATAL: no era walkers under natives/linux-x86_64"; exit 1; }; \
    if ldd "$W" 2>&1 | grep -q 'not found'; then \
      echo "FATAL: walker has unresolved libraries in this image:"; ldd "$W" | grep 'not found'; \
      echo "Natives must be the PORTABLE build (bundled libprotobuf.so via \$ORIGIN) AND built on a"; \
      echo "glibc/GLIBCXX no newer than this base (ubuntu:24.04). See BUILD.md 'Docker image'."; \
      exit 1; \
    fi

# Point the host at its BAKED natives. The inventory + repo root are resolved per-run: unset
# CS2_REPO_ROOT -> the baked ${APP}/data inventory (standalone); CS2_REPO_ROOT=/repo -> the
# bind-mounted repo's data/ + artifacts/.
ENV CS2_WALKER_ERAS_ROOT=${APP}/natives \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    PATH=${APP}:${PATH}

# `--commit` writes the produced set to a cwd-relative artifacts/, so default the workdir to the
# repo mount point (pair with -e CS2_REPO_ROOT=/repo -v <repo>:/repo). Standalone `--out` runs
# use an absolute output dir and ignore this.
WORKDIR /repo

ENTRYPOINT ["/opt/cs2-schema-tracker/cs2-schema-tracker"]
CMD ["--help"]
