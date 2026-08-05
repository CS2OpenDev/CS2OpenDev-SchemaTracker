<#
.SYNOPSIS
  Build every per-era walker binary for windows-x86_64 (MSVC + vcpkg) into the
  native bundle layout natives/windows-x86_64/{era}.exe.

.DESCRIPTION
  ONE command produces every era binary for this OS. For each compile-pin era in
  the consolidated inventory (data/cs2-assets-inventory.json, top-level eras[]) it:
  guards the submodule is initialized (fail-loud, never auto-inits), skips if already
  built+tested for this era+srcFingerprint (content-keyed resumability, see the
  comment block below; -Force overrides), checks out the era's hl2sdk pin in the
  single submodule working tree, regenerates the pin-static
  netmsg table, configures+builds a FRESH per-era build dir (the build docs vcpkg
  toolchain invocation) with -DWALKER_ERA_NAME=<era> so the emitted exe is era-named,
  runs ctest, asserts the built exe's emitted layout signature equals the era's
  windows-x86_64 layoutSignature (build-time second gate), then copies the era-named exe
  + its runtime DLLs into natives/windows-x86_64/ (the shared protobuf/abseil runtime is
  deduped — every era exe in the dir loads the same sibling DLLs).

  The 2 runtime-variant eras (kind:"runtime-variant") produce NO binary of their own —
  they ride their compile-pin's walker at runtime — so only the 11 compile-pin eras build.

  After the loop (ALWAYS, including on failure via a finally block) it restores the
  submodule to the canonical superproject gitlink pin and restores the working-tree
  netmsg_table.generated.inc, then asserts the submodule HEAD equals the gitlink SHA so
  a developer is never left on a stray pin.

  Exit 0 iff every requested compile-pin era built + tested + signature-matched green.

.NOTES
  HEAVY BUILD. Operator/CI only — it can run for hours.
  This script produces windows-x86_64 binaries (the walker only runs on a host matching
  the target tuple). Use scripts/build-era-walkers.sh on Linux.

  Signature capture: the walker prints its COMPILE-TIME ComputeLayoutSignature() via
  `--print-signature` (binaries-free — needs no CS2 modules on disk). The build-time
  signature gate therefore ALWAYS runs (it is unconditional); there is no
  skip-when-no-binaries path. -BinariesDir is retained only for the legacy probe-layout
  cross-check and is not required for the gate.
#>

<#
  Content-keyed resumability + walker-manifest.json
  ---------------------------------------------------------------------------------------
  RESUMABILITY KEY: the per-era skip used to compare the sidecar's recorded
  `walkerGitSha` against repo HEAD -- so an UNCOMMITTED walker source fix rebuilt ZERO
  binaries (HEAD hadn't moved), and HEAD churned on every unrelated artifacts commit even
  when nothing walker-relevant changed. The skip key is now `srcFingerprint`, the stable
  SHA256 content hash from walker/tools/src_fingerprint.py (hashes walker/src/**,
  walker/CMakeLists.txt, the netmsg-table generator, and schemas/*.proto -- see that
  script's docstring). `walkerGitSha` is still stamped into the sidecar/manifest for human
  provenance, it just no longer gates the skip.

  walker-manifest.json SCHEMA (natives/<platform>/walker-manifest.json, upserted after
  EVERY successful era install -- one property per era id):
    {
      "<eraId>": {
        "gitSha":          "<repo HEAD sha at build time -- provenance only, NOT the skip key>",
        "srcFingerprint":  "<walker/tools/src_fingerprint.py output -- the resumability skip key>",
        "hl2sdkPin":       "<hl2sdk submodule sha this era was built against>",
        "layoutSignature": "<--print-signature output, scoped to this platform>",
        "binarySha256":    "<sha256 of the installed era exe>",
        "builtUtc":        "<ISO-8601 UTC timestamp of this install>"
      },
      ...
    }
  This manifest is the on-disk record the host's preflight identity gate cross-checks against
  each binary's live `--version` output (manifest = what the build scripts produced; binary
  = what is actually on disk today). It is read-modify-write per era so a script that dies
  partway through a multi-era run leaves every already-finished era durably recorded.
#>
[CmdletBinding()]
param(
  # Repo root (default: parent of this script's dir).
  [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
  # Force rebuild even if an up-to-date archived binary exists for this era+walker-SHA.
  [switch]$Force,
  # Build only these era ids (default: all compile-pin eras). Matches inventory eras[].era.
  [string[]]$Era,
  # Output root for the native bundle (default: <repo>/natives). Binaries land in
  # <NativesRoot>/windows-x86_64/. Override to stage into an external bundle dir.
  [string]$NativesRoot = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'natives'),
  # Dir holding the platform's CS2 bin tree. Retained only for the legacy probe-layout
  # cross-check; the build-time signature gate no longer needs it (uses --print-signature).
  [string]$BinariesDir = $env:CS2_BINARIES_ROOT,
  # vcpkg toolchain file (build docs default). Derived from $VCPKG_ROOT.
  [string]$VcpkgToolchain = $(if ($env:VCPKG_ROOT) { Join-Path $env:VCPKG_ROOT 'scripts\buildsystems\vcpkg.cmake' } else { '' }),
  [string]$VcpkgTriplet = 'x64-windows',
  # CMake VS generator. Empty = auto-detect from the INSTALLED Visual Studio via vswhere, so the
  # build tracks whatever VS the machine/CI image ships (2022, 2026, ...) rather than a hardcode.
  [string]$Generator = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$OS = 'windows-x86_64'
$Sdk         = Join-Path $Repo 'walker\third_party\hl2sdk'
$Inventory   = Join-Path $Repo 'data\cs2-assets-inventory.json'
$NetmsgInc   = Join-Path $Repo 'walker\src\netmsg_table.generated.inc'
$GenNetmsg   = Join-Path $Repo 'walker\tools\gen_netmsg_table.py'
# Per-era build scratch (fresh dir per era) — gitignored, NOT shipped.
$ScratchRoot = Join-Path $Repo 'walker\build-eras'
# Where the shipped, era-named binaries + deduped runtime land.
$OutDir      = Join-Path $NativesRoot $OS

function Fail($msg) { Write-Error $msg; exit 1 }
function Run($exe, [string[]]$argv) {
  # Native tools (python gen_netmsg, cmake, ctest, git) legitimately write progress to
  # stderr. Under $ErrorActionPreference='Stop' + an outer stream redirect (CI logs,
  # *>&1, 2>&1) PS 5.1 wraps each native stderr line in a terminating NativeCommandError.
  # Localize to Continue around the native call so ONLY a nonzero exit code is a failure;
  # cmdlet errors elsewhere still honor Stop.
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { & $exe @argv } finally { $ErrorActionPreference = $prev }
  if ($LASTEXITCODE -ne 0) { Fail "command failed (exit $LASTEXITCODE): $exe $($argv -join ' ')" }
}

# Resolve the host dll used ONLY for target selection (`plan`). Prefer a pre-published dll named
# by $env:CS2_HOST_DLL (CI publishes it once); otherwise build the host Release once. The host is
# the single source of truth for the compile-pin era list — this replaces hand-parsing the
# inventory in PowerShell so the selection can never drift from the host's own model.
function Resolve-HostDll {
  if ($env:CS2_HOST_DLL -and (Test-Path $env:CS2_HOST_DLL)) { return $env:CS2_HOST_DLL }
  Write-Host "==> building host (for plan target selection)..."
  # Pipe to Out-Host so the build progress stays visible but does NOT leak into this
  # function's return value. A PS function returns EVERY uncaptured pipeline object, so
  # without this the dll path would be prefixed by all the 'dotnet build' output lines,
  # and the caller would splat that array as args to the `plan` invocation below.
  Run 'dotnet' @('build', (Join-Path $Repo 'host\src\Cs2SchemaTracker.Host'), '-c', 'Release', '-p:SelfContained=false', '-p:PublishSingleFile=false', '-p:UseAppHost=false', '-v', 'q', '--nologo') | Out-Host
  $dll = Join-Path $Repo 'host\artifacts\bin\Cs2SchemaTracker.Host\release\cs2-schema-tracker.dll'
  if (-not (Test-Path $dll)) { Fail "host dll not found after build: $dll" }
  return $dll
}

# --- preflight ------------------------------------------------------------------------
if (-not (Test-Path $Inventory)) { Fail "inventory not found: $Inventory" }
if (-not (Test-Path $GenNetmsg)) { Fail "netmsg generator not found: $GenNetmsg" }

# guard: the submodule MUST be initialized. Do NOT auto-init (per build docs) — it is
# large. Fail loud with the exact init command.
if (-not (Test-Path (Join-Path $Sdk 'public'))) {
  Fail @"
hl2sdk submodule not initialized at $Sdk.
Run this ONCE (large download), then re-run this script:
  git submodule update --init walker/third_party/hl2sdk
"@
}

# Canonical pin = whatever the superproject gitlink records. We snap back to it at the end.
$canonicalPin = (git -C $Repo ls-tree HEAD walker/third_party/hl2sdk).Split()[2]
if ([string]::IsNullOrWhiteSpace($canonicalPin)) { Fail "could not read canonical gitlink pin for the submodule" }

# Walker tool SHA stamped into every binary. Provenance ONLY -- see the schema comment
# above; content-keyed $srcFingerprint below is the resumability key.
$walkerGitSha = (git -C $Repo rev-parse HEAD).Trim()

# Content-keyed resumability key. Computed ONCE up front (same content -> same
# fingerprint regardless of which/how-many eras this invocation rebuilds).
$SrcFingerprintTool = Join-Path $Repo 'walker\tools\src_fingerprint.py'
if (-not (Test-Path $SrcFingerprintTool)) { Fail "src-fingerprint tool not found: $SrcFingerprintTool (required for the walker fingerprint)" }
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'  # see Run(): native stderr must not terminate under a redirect
try { $srcFingerprint = (& python $SrcFingerprintTool | Select-Object -First 1) }
finally { $ErrorActionPreference = $prevEap }
if ($LASTEXITCODE -ne 0) { Fail "src_fingerprint.py failed (exit $LASTEXITCODE)" }
$srcFingerprint = ("$srcFingerprint").Trim()
if ($srcFingerprint -notmatch '^[0-9a-f]{64}$') { Fail "src_fingerprint.py did not print a 64-hex digest (got '$srcFingerprint')" }

# buildDateUtc: prefer a reproducible SOURCE_DATE_EPOCH over wall-clock (spirit;
# this is binary provenance, never an artifact byte).
$buildDateUtc = $null
if ($env:SOURCE_DATE_EPOCH) {
  $buildDateUtc = [DateTimeOffset]::FromUnixTimeSeconds([int64]$env:SOURCE_DATE_EPOCH).UtcDateTime.ToString('o')
}

# Compile-pin eras come from the host `plan` command — the single source of truth for target
# selection. It projects inventory eras[] into per-platform tsv rows (era<TAB>hl2sdkSha<TAB>the
# layoutSignature scoped to $OS); runtime-variant eras are excluded (they ride a compile pin).
$HostDll = Resolve-HostDll
$planRows = & dotnet $HostDll plan --targets compile-pins --platform $OS --format tsv --inventory $Inventory
if ($LASTEXITCODE -ne 0) { Fail "host 'plan' failed (exit $LASTEXITCODE)" }
# @() forces array context so a single-era result still exposes .Count under Set-StrictMode.
$eras = @(foreach ($row in ($planRows -split "`n" | Where-Object { $_ -ne '' })) {
  $cols = $row -split "`t"
  [pscustomobject]@{ era = $cols[0]; hl2sdkSha = $cols[1]; sig = $cols[2] }
})
if ($Era) { $eras = @($eras | Where-Object { $Era -contains $_.era }) }
if (-not $eras) { Fail "no compile-pin eras to build (filter -Era did not match any entry)" }

New-Item -ItemType Directory -Force -Path $ScratchRoot | Out-Null
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# Resolve the CMake VS generator ONCE. Empty -Generator => detect the installed VS via vswhere so
# the build tracks whatever the machine/CI image ships (VS2022 = "17 2022", VS2026 = "18 2026").
if ([string]::IsNullOrWhiteSpace($Generator)) {
  $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (-not (Test-Path $vswhere)) { Fail "vswhere not found at $vswhere; pass -Generator explicitly." }
  $vsmajor = (& $vswhere -latest -products * -property installationVersion) -split '\.' | Select-Object -First 1
  switch ($vsmajor) {
    '17' { $Generator = 'Visual Studio 17 2022' }
    '18' { $Generator = 'Visual Studio 18 2026' }
    default { Fail "unrecognized Visual Studio major version '$vsmajor' from vswhere; pass -Generator." }
  }
}
Write-Host "==> CMake generator: $Generator"

$built = 0; $skipped = 0
$want  = $eras.Count
$green = 0
$hardFail = $false

function Restore-Submodule {
  Write-Host "==> restoring submodule to canonical pin $canonicalPin"
  # Snap the submodule working tree back to the recorded gitlink, and restore the
  # working-tree netmsg table to its committed (canonical-pin) content.
  & git -C $Repo submodule update --checkout walker/third_party/hl2sdk 2>$null
  & git -C $Repo checkout -- walker/src/netmsg_table.generated.inc 2>$null
  $head = (git -C $Sdk rev-parse HEAD).Trim()
  if ($head -ne $canonicalPin) {
    Fail "submodule NOT restored: HEAD=$head expected gitlink=$canonicalPin. Fix manually: git submodule update --checkout walker/third_party/hl2sdk"
  }
  Write-Host "    submodule HEAD == gitlink ($canonicalPin) OK"
}

# --- walker-manifest.json upsert -------------------------------------------------------
# Read-modify-write ONE era's entry into natives/<platform>/walker-manifest.json (schema
# documented in the comment block at the top of this script). Re-reads the file on every
# call (rather than caching in memory across the loop) so an entry already on disk from a
# PRIOR invocation of this script (e.g. a run that covered a different -Era subset) is
# preserved, not clobbered.
function Update-WalkerManifest([string]$manifestPath, [string]$eraId, [System.Collections.Specialized.OrderedDictionary]$entry) {
  $manifest = [ordered]@{}
  if (Test-Path $manifestPath) {
    $existing = Get-Content $manifestPath -Raw | ConvertFrom-Json
    foreach ($prop in $existing.PSObject.Properties) { $manifest[$prop.Name] = $prop.Value }
  }
  $manifest[$eraId] = $entry
  ($manifest | ConvertTo-Json -Depth 6) | Set-Content -Encoding utf8 $manifestPath
}

try {
  foreach ($e in $eras) {
    $eraId = $e.era
    $pin = $e.hl2sdkSha
    $shortPin = $pin.Substring(0, 8)
    Write-Host ""
    Write-Host "================ era $eraId  pin $shortPin ================"

    $eraDir   = Join-Path $ScratchRoot $eraId
    $buildDir = Join-Path $eraDir 'build'
    # The shipped, era-named binary + a resumability sidecar (sidecar lives in the
    # gitignored scratch dir, NOT in natives/, so the bundle stays clean).
    $exeName  = "$eraId.exe"
    $outExe   = Join-Path $OutDir $exeName
    $metaPath = Join-Path $eraDir "$eraId.meta.json"

    # --- (2) resumability skip ---------------------------------------------------------
    if (-not $Force -and (Test-Path $outExe) -and (Test-Path $metaPath)) {
      $meta = Get-Content $metaPath -Raw | ConvertFrom-Json
      # Property presence check (NOT bare $meta.srcFingerprint) because a sidecar written
      # before the fingerprint key existed has no such property, and Set-StrictMode -Version
      # Latest throws on a missing PSCustomObject property rather than returning $null.
      # A missing/mismatched fingerprint falls through to a rebuild, which backfills it.
      $metaHasFingerprint = $meta.PSObject.Properties.Name -contains 'srcFingerprint'
      if ($meta.hl2sdkSha -eq $pin -and $metaHasFingerprint -and $meta.srcFingerprint -eq $srcFingerprint -and $meta.ctest -eq 'passed') {
        Write-Host "    up-to-date (pin + srcFingerprint match, ctest passed) -> skip. Use -Force to rebuild."
        $skipped++; $green++
        continue
      }
    }

    # --- (3) checkout the pin (fail loud on dirty submodule tree) -----------------------
    $dirty = git -C $Sdk status --porcelain
    if ($dirty) { Fail "submodule working tree is dirty before checkout of $shortPin; refusing. Clean it first.`n$dirty" }
    Write-Host "==> git checkout $shortPin"
    Run 'git' @('-C', $Sdk, 'checkout', '--quiet', $pin)
    $head = (git -C $Sdk rev-parse HEAD).Trim()
    if ($head -ne $pin) { Fail "submodule checkout mismatch: HEAD=$head expected=$pin" }

    # --- (4) per-pin netmsg table regen (pin-static; MUST run before cmake) -------------
    Write-Host "==> regenerating netmsg table for pin $shortPin"
    Run 'python' @($GenNetmsg)

    # --- (5) FRESH per-era build dir configure + build (Release) ------------------------
    # A fresh dir cannot carry a stale era-probe cache result.
    if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

    Write-Host "==> cmake configure ($eraId)"
    Run 'cmake' @(
      '-G', $Generator, '-A', 'x64',
      "-DCMAKE_TOOLCHAIN_FILE=$VcpkgToolchain",
      "-DVCPKG_TARGET_TRIPLET=$VcpkgTriplet",
      "-DWALKER_ERA_NAME=$eraId",
      '-S', (Join-Path $Repo 'walker'),
      '-B', $buildDir
    )
    Write-Host "==> cmake build ($eraId)"
    Run 'cmake' @('--build', $buildDir, '--config', 'Release')

    # --- (6) ctest ---------------------------------------------------------------------
    Write-Host "==> ctest ($eraId)"
    Run 'ctest' @('--test-dir', $buildDir, '-C', 'Release', '--output-on-failure')

    # Locate the built exe (OUTPUT_NAME is era-named via -DWALKER_ERA_NAME).
    $exe = Get-ChildItem -Path $buildDir -Recurse -Filter $exeName -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if (-not $exe) { Fail "could not find the built era-named exe '$exeName' under $buildDir" }

    # --- (7) build-time signature second gate (UNCONDITIONAL) --------------------
    # The built exe prints its COMPILE-TIME ComputeLayoutSignature() via
    # `--print-signature` — binaries-free, no CS2 modules required. Output is exactly the
    # signature string + newline, so we capture the first (only) stdout line.
    Write-Host "==> --print-signature assert ($eraId)"
    # Per-platform layout signature, as selected by `plan --platform $OS` (empty when this era has
    # no signature registered for this platform). This is the Windows era-builder, which expects a
    # signature for every compile-pin era; fail loud on an empty one (never guess an unvalidated layout).
    $expectedSig = $e.sig
    if ([string]::IsNullOrWhiteSpace($expectedSig)) {
      Fail "era '$eraId' has no layoutSignatures.$OS in the inventory (never guess an unvalidated layout)"
    }
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'  # see Run(): native stderr must not terminate under a redirect
    try { $sig = (& $exe.FullName '--print-signature' | Select-Object -First 1) }
    finally { $ErrorActionPreference = $prevEap }
    if ($LASTEXITCODE -ne 0) { Fail "--print-signature failed (exit $LASTEXITCODE) for era $eraId" }
    $sig = ("$sig").Trim()
    if ([string]::IsNullOrWhiteSpace($sig)) { Fail "--print-signature produced no signature for era $eraId" }
    if ($sig -ne $expectedSig) {
      Fail "signature mismatch for era $eraId`n  expected: $expectedSig`n  got:      $sig"
    }
    Write-Host "    signature OK: $sig"

    # --- (8) install into natives/{platform}/ + resumability sidecar --------------------
    Copy-Item -Force $exe.FullName $outExe
    # The walker links vcpkg DLLs (libprotobuf, abseil_dll) dynamically (x64-windows triplet).
    # The host launches the era exe from natives/{platform}/, so those DLLs MUST sit beside it
    # or the process dies with STATUS_DLL_NOT_FOUND (0xC0000135). They are byte-identical across
    # eras, so copying each era's siblings into the shared $OutDir dedups to ONE copy per DLL.
    Get-ChildItem -Path $exe.Directory.FullName -Filter *.dll -File |
      ForEach-Object { Copy-Item -Force $_.FullName (Join-Path $OutDir $_.Name) }
    $meta = [ordered]@{
      era             = $eraId
      hl2sdkSha       = $pin
      walkerGitSha    = $walkerGitSha
      srcFingerprint  = $srcFingerprint
      layoutSignature = $expectedSig
      ctest           = 'passed'
      os              = $OS
    }
    if ($buildDateUtc) { $meta['buildDateUtc'] = $buildDateUtc }
    ($meta | ConvertTo-Json -Depth 5) | Set-Content -Encoding utf8 $metaPath
    Write-Host "    installed: $outExe"

    # --- (9) walker-manifest.json upsert -------------------------------------------------
    # Schema documented in the comment block at the top of this script. Written per era
    # (not batched after the loop) so a mid-run failure still leaves finished eras recorded.
    $manifestPath = Join-Path $OutDir 'walker-manifest.json'
    $binarySha256 = (Get-FileHash -Algorithm SHA256 $outExe).Hash.ToLowerInvariant()
    $builtUtc = if ($buildDateUtc) { $buildDateUtc } else { [DateTime]::UtcNow.ToString('o') }
    $manifestEntry = [ordered]@{
      gitSha          = $walkerGitSha
      srcFingerprint  = $srcFingerprint
      hl2sdkPin       = $pin
      layoutSignature = $expectedSig
      binarySha256    = $binarySha256
      builtUtc        = $builtUtc
    }
    Update-WalkerManifest $manifestPath $eraId $manifestEntry
    Write-Host "    walker-manifest.json updated: era=$eraId ($manifestPath)"

    $built++; $green++
  }
}
catch {
  Write-Host "BUILD LOOP FAILED: $_" -ForegroundColor Red
  $hardFail = $true
}
finally {
  # ALWAYS restore the submodule + netmsg table (even on injected failure).
  Restore-Submodule
}

Write-Host ""
Write-Host "built=$built skipped=$skipped (into $OutDir)"

# Exit 0 iff every requested compile-pin era is green (freshly built+tested or an
# up-to-date green skip). A hard failure (exception in the loop) is always exit 1.
if ($hardFail) { exit 1 }
if ($green -lt $want) {
  Write-Error "not every requested era is green ($green/$want)."
  exit 1
}
exit 0
