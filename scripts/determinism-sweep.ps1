<#
.SYNOPSIS
  CROSS-BUILD determinism sweep for the walker (strict reading).

.DESCRIPTION
  Proves that TWO independently-built walkers of the SAME current source, given
  DELIBERATELY DIFFERENT process/DLL memory layouts, emit BYTE-IDENTICAL
  walker_output for every era's representative build. Any per-rep byte difference
  is a determinism BUG: the walker leaked a load-address-derived value into the
  artifact (it read walker-adjacent / loaded-CS2-DLL memory whose bytes are fixed
  within one compiled binary but SHIFT when the binary is relinked at a different
  image base). A correct walker only reads INPUT-DLL CONSTANT descriptor data,
  whose VALUES are load-address-invariant, so it is identical across the two builds.

  WHAT THIS CATCHES, and why a same-binary re-run misses it. Two such bugs found this way:
    * 2023 class `size` read a schema record pointer's HIGH dword (m_pszName +12
      instead of m_nSize +24) -> emitted the high 32 bits of a CS2-DLL address.
    * modern `MPropertyAttributeRange` read an 8-byte char* AS two floats ->
      emitted the pointer bits reinterpreted as float min/max.
  Both were BYTE-IDENTICAL run-to-run with the SAME exe (Windows gives each DLL a
  per-BOOT-shared base, so the same binary re-run reads the same address bits), so
  the existing same-binary re-run check PASSED them. They only diverge when the
  walker is RECOMPILED at a different layout -- exactly what this harness forces.

  THE PERTURBATION, which is the crux. Pure LINK-TIME image rebase, injected via
  CMAKE_EXE_LINKER_FLAGS -- NO walker source or CMakeLists edits (tooling only):
    detA:  /DYNAMICBASE:NO /HIGHENTROPYVA:NO /BASE:0x140000000   (default region)
    detB:  /DYNAMICBASE:NO /HIGHENTROPYVA:NO /BASE:0x180000000   (collides with the
           Source2 DLLs' preferred base -> forces the loader to RELOCATE the CS2
           modules to different absolute addresses than detA)
  ASLR is DISABLED in both so each build is deterministic run-to-run (a mismatch is
  a true signal, never ASLR noise), and the two builds' walker image + subsequently
  mapped CS2 DLLs land at genuinely different addresses. The harness ASSERTS at
  build time (PE-header read) that the two exes actually got different ImageBases
  and that DYNAMICBASE is off, so a mis-applied perturbation fails loud rather than
  giving false confidence.

.EXAMPLE
  pwsh -File scripts/determinism-sweep.ps1

  Full sweep: all 12 reps, up to 10 distinct hl2sdk pins x2 builds.

.EXAMPLE
  pwsh -File scripts/determinism-sweep.ps1 -Reps 23669931,12147839,13240071

  Fast path -- only the current-pin reps (the two known bug classes live here).

.EXAMPLE
  pwsh -File scripts/determinism-sweep.ps1 -SkipBuild

  Re-diff without rebuilding (build dirs already populated).

.NOTES
  Operator/CI only. It issues HEAVY cmake builds and runs for a long time -- start it
  detached, not from a shell you are going to close.

  INTERPRETING A MISMATCH. A rep printing MISMATCH means an address-derived read. The
  report prints the protoc-decoded field PATH(s) that differ (e.g. classes[..].size, or
  classes[..].metadata[..].value). To fix:
    1. Find the walker read that produced that field (schema_walk.cpp for
       class/field/enum + metadata; the field name localizes it).
    2. The value is being derived from a POINTER / load address instead of from
       input-DLL constant data. Re-target the read to the correct struct offset
       that holds the real constant value (like size: +12 -> +24) or re-classify
       the blob (like MPropertyAttributeRange: float-pair -> string), so the
       emitted value no longer depends on where the DLL was mapped.
    3. Re-run this sweep; the rep must flip to IDENTICAL.

  Offline (no Steam/network): walks the local binary cache at -BinRoot.
  Produces + runs windows-x86_64 walkers only (host must match the tuple).
  Reuses the build-era-walkers submodule-pin dance (checkout pin, regen netmsg,
  fresh per-pin build dir, restore on exit). Restores the submodule + working-tree
  netmsg table in a finally block even on failure.
  Do NOT `git commit` while this runs; it does not commit anything.
#>
[CmdletBinding()]
param(
  # Repo root (default: parent of this script's dir).
  [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

  # Local CS2 binary cache root: <BinRoot>/<build_id>/windows-x86_64/ is passed to
  # `walk --binaries`. Defaults to $CS2_BINARIES_ROOT, else the in-repo cache.
  [string]$BinRoot = $(if ($env:CS2_BINARIES_ROOT) { $env:CS2_BINARIES_ROOT } else { Join-Path $PSScriptRoot '..\cache\binaries' }),

  # Subset of representative build ids to sweep (default: all 12). Each maps to a
  # pin below; only the needed pins are built.
  [string[]]$Reps,

  # Skip the build phase and just walk+diff using whatever exes already exist under
  # the two build roots (resumable / re-diff).
  [switch]$SkipBuild,

  # Build the two walkers but skip walk+diff (populate the build roots only).
  [switch]$BuildOnly,

  # Run ctest per build (extra gate; off by default to keep the sweep focused).
  [switch]$RunCTest,

  # The two perturbation image bases (hex). detB defaults to the Source2 DLL
  # preferred base to force CS2-module relocation.
  [string]$BaseA = '0x140000000',
  [string]$BaseB = '0x180000000',

  # Toolchain (build docs / build-era-walkers defaults). Derived from $VCPKG_ROOT.
  [string]$VcpkgToolchain = $(if ($env:VCPKG_ROOT) { Join-Path $env:VCPKG_ROOT 'scripts\buildsystems\vcpkg.cmake' } else { '' }),
  [string]$VcpkgTriplet   = 'x64-windows',

  # protoc for the decode-on-mismatch field-path localization. Auto-discovered from
  # vcpkg if left empty; only REQUIRED when a mismatch actually occurs.
  [string]$Protoc = '',

  # Scratch root for per-rep walker outputs.
  [string]$OutRoot = (Join-Path $env:TEMP 'cs2-det-sweep')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------------------
# Paths / constants
# ---------------------------------------------------------------------------------------
$OS          = 'windows-x86_64'
$Sdk         = Join-Path $Repo 'walker\third_party\hl2sdk'
$WalkerSrc   = Join-Path $Repo 'walker'
$Inventory   = Join-Path $Repo 'data\cs2-assets-inventory.json'
$NetmsgInc   = Join-Path $Repo 'walker\src\netmsg_table.generated.inc'
$GenNetmsg   = Join-Path $Repo 'walker\tools\gen_netmsg_table.py'
$SchemasDir  = Join-Path $Repo 'schemas'
$BuildRootA  = Join-Path $Repo 'walker\build-detA'
$BuildRootB  = Join-Path $Repo 'walker\build-detB'
$TopMsg      = 'cs2.schema_tracker.v0.WalkerOutput'

$Perturb = @{
  detA = @{ Root = $BuildRootA; Flags = "/DYNAMICBASE:NO /HIGHENTROPYVA:NO /BASE:$BaseA" }
  detB = @{ Root = $BuildRootB; Flags = "/DYNAMICBASE:NO /HIGHENTROPYVA:NO /BASE:$BaseB" }
}

function Fail($msg) { Write-Error $msg; exit 1 }

function Run($exe, [string[]]$argv) {
  # Native tools write progress to stderr; localize to Continue so only a nonzero
  # exit code is a failure (matches build-era-walkers.ps1 Run()).
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  # Pipe native stdout to the host (NOT the pipeline) so a caller like
  # Configure-And-Build returns ONLY its `return` value, not the tool's output.
  try { & $exe @argv | Out-Host } finally { $ErrorActionPreference = $prev }
  if ($LASTEXITCODE -ne 0) { Fail "command failed (exit $LASTEXITCODE): $exe $($argv -join ' ')" }
}

# Resolve the host dll used ONLY for target selection (`plan`). Prefer a pre-published dll named by
# $env:CS2_HOST_DLL; otherwise build the host Release once (matches build-era-walkers.ps1).
function Resolve-HostDll {
  if ($env:CS2_HOST_DLL -and (Test-Path $env:CS2_HOST_DLL)) { return $env:CS2_HOST_DLL }
  Write-Host "==> building host (for plan target selection)..."
  Run 'dotnet' @('build', (Join-Path $Repo 'host\src\Cs2SchemaTracker.Host'), '-c', 'Release', '-p:SelfContained=false', '-p:PublishSingleFile=false', '-p:UseAppHost=false', '-v', 'q', '--nologo')
  $dll = Join-Path $Repo 'host\artifacts\bin\Cs2SchemaTracker.Host\release\cs2-schema-tracker.dll'
  if (-not (Test-Path $dll)) { Fail "host dll not found after build: $dll" }
  return $dll
}

# ---------------------------------------------------------------------------------------
# Rep -> era -> hl2sdk pin table. Ordered as the task specifies. Each pin is
# cross-checked against the inventory eras[] (compile-pin hl2sdkSha or
# runtime-variant ridesCompilePin) so this stays self-consistent with the era
# catalog -- a typo here fails loud in the preflight below.
# ---------------------------------------------------------------------------------------
$RepTable = @(
  @{ Build='24304127'; Era='cs2-2026-07-09'; Pin='5f891c9026230cce0fc0a3fc4b5fef1c467a1385' }
  @{ Build='23669931'; Era='cs2-2026-04-21'; Pin='b8dcaf14c603076300cab3861c99b44878d65db4' }
  @{ Build='22627914'; Era='cs2-2026-01-22'; Pin='0da05cff57162fe8f950192cf73d89e77ab9ee00' }
  @{ Build='21529689'; Era='cs2-2025-10-16'; Pin='e54b31c60a4a2034406895206bbeee9bf8c9aef0' }
  @{ Build='20278147'; Era='cs2-2025-09-17'; Pin='a4fc170d18555b3478f25c447260b7a8839ecbda' }
  @{ Build='19605004'; Era='cs2-2025-07-31'; Pin='3525af9943da07536ba01ce86b54823b1b18ef00' }
  @{ Build='19251152'; Era='cs2-2025-03-20'; Pin='07f35e15477913484e7f5017390b75d99ce270fd' }
  @{ Build='17732524'; Era='cs2-2025-03-12'; Pin='f31e5fbbfe6d794b7c7b37977810e7457516a8b6' }
  @{ Build='17032840'; Era='cs2-2024-06-04'; Pin='f3b44f206d38d1b71164e558cd4087d84607d50c' }
  @{ Build='14446408'; Era='cs2-2024-04-03'; Pin='426ae7f3b47932734656896b79cafd21a5a5e63c' }
  @{ Build='13829089'; Era='cs2-2024-02-07'; Pin='00644551e4fa9682bce94a556ee1a952b6a463d2' }
  @{ Build='12147839'; Era='cs2-2023-03-22'; Pin='b8dcaf14c603076300cab3861c99b44878d65db4' }
  @{ Build='13240071'; Era='cs2-2023-09-13'; Pin='b8dcaf14c603076300cab3861c99b44878d65db4' }
)

# ---------------------------------------------------------------------------------------
# PE header reader -- confirms the perturbation actually took (different ImageBase,
# DYNAMICBASE off) at BUILD time so a mis-applied linker flag fails loud rather than
# silently reducing the sweep to a same-layout no-op (false-pass guard).
# ---------------------------------------------------------------------------------------
function Get-PEInfo([string]$path) {
  $fs = [System.IO.File]::OpenRead($path)
  try {
    $br = New-Object System.IO.BinaryReader($fs)
    $fs.Position = 0x3C
    $peOff = $br.ReadInt32()
    $fs.Position = $peOff
    if ($br.ReadUInt32() -ne 0x00004550) { throw "not a PE: $path" }  # 'PE\0\0'
    $optOff = $peOff + 4 + 20
    $fs.Position = $optOff
    $magic = $br.ReadUInt16()
    if ($magic -ne 0x20B) { throw "not PE32+ (magic=0x$($magic.ToString('X')))" }  # x64 only
    $fs.Position = $optOff + 24          # PE32+ ImageBase (ULONGLONG)
    $imageBase = $br.ReadUInt64()
    $fs.Position = $optOff + 70          # PE32+ DllCharacteristics (WORD)
    $dllChars = $br.ReadUInt16()
    [pscustomobject]@{
      ImageBase       = $imageBase
      DynamicBase     = [bool]($dllChars -band 0x0040)   # IMAGE_DLLCHARACTERISTICS_DYNAMIC_BASE
      HighEntropyVA   = [bool]($dllChars -band 0x0020)   # IMAGE_DLLCHARACTERISTICS_HIGH_ENTROPY_VA
    }
  } finally { $fs.Dispose() }
}

# ---------------------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------------------
if (-not (Test-Path $Inventory)) { Fail "inventory not found: $Inventory" }
if (-not (Test-Path $Sdk))         { Fail "hl2sdk submodule dir missing: $Sdk" }
if (-not (Test-Path (Join-Path $Sdk 'public'))) {
  Fail @"
hl2sdk submodule not initialized at $Sdk.
Run this ONCE (large download), then re-run:
  git submodule update --init walker/third_party/hl2sdk
"@
}
if (-not $SkipBuild -and -not (Test-Path $GenNetmsg)) { Fail "netmsg generator not found: $GenNetmsg" }

# Cross-check every rep's pin against the inventory's compile-pin eras (fail loud on drift). The
# known-pin set comes from the host `plan` command (the single source of truth for the era list),
# not a second hand-rolled inventory parse. Runtime-variant eras ride a compile pin, so the
# compile-pin hl2sdkSha set already covers every rep's build-time pin.
$HostDll = Resolve-HostDll
$planRows = & dotnet $HostDll plan --targets compile-pins --platform windows-x86_64 --format tsv --inventory $Inventory
if ($LASTEXITCODE -ne 0) { Fail "host 'plan' failed (exit $LASTEXITCODE)" }
$knownPins = New-Object System.Collections.Generic.HashSet[string]
foreach ($row in ($planRows -split "`n" | Where-Object { $_ -ne '' })) {
  [void]$knownPins.Add(($row -split "`t")[1])
}

# Filter reps.
$selected = $RepTable
if ($Reps) {
  $selected = @($RepTable | Where-Object { $Reps -contains $_.Build })
  if (-not $selected) { Fail "no reps matched -Reps ($($Reps -join ', ')). Valid: $(( $RepTable.Build ) -join ', ')" }
}
foreach ($r in $selected) {
  if (-not $knownPins.Contains($r.Pin)) {
    Fail "rep $($r.Build) (era $($r.Era)) pin $($r.Pin) is NOT in the inventory eras[] -- table drift; fix determinism-sweep.ps1"
  }
  $binDir = Join-Path (Join-Path $BinRoot $r.Build) $OS
  if (-not $SkipBuild -or -not $BuildOnly) {
    if (-not (Test-Path $binDir)) {
      Fail "binary cache missing for rep $($r.Build): $binDir (acquire it first; this harness is offline)"
    }
  }
}

$canonicalPin = (git -C $Repo ls-tree HEAD walker/third_party/hl2sdk).Split()[2]
if ([string]::IsNullOrWhiteSpace($canonicalPin)) { Fail "could not read canonical gitlink pin for the submodule" }
$walkerGitSha = (git -C $Repo rev-parse HEAD).Trim()

New-Item -ItemType Directory -Force -Path $OutRoot | Out-Null

# Distinct pins to build, in first-seen order.
$pins = @()
foreach ($r in $selected) { if ($pins -notcontains $r.Pin) { $pins += $r.Pin } }

Write-Host "determinism-sweep: walkerGitSha=$walkerGitSha canonicalPin=$canonicalPin"
Write-Host "  reps:  $(( $selected.Build ) -join ', ')"
Write-Host "  pins:  $(( $pins | ForEach-Object { $_.Substring(0,8) } ) -join ', ')"
Write-Host "  detA base=$BaseA  detB base=$BaseB  (ASLR off both)"

# ---------------------------------------------------------------------------------------
# Build helpers
# ---------------------------------------------------------------------------------------
function Configure-And-Build([string]$variant, [string]$pin, [string]$buildDir, [string]$linkFlags) {
  if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
  New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
  Write-Host "==> [$variant $($pin.Substring(0,8))] cmake configure  ($linkFlags)"
  Run 'cmake' @(
    '-G', 'Visual Studio 17 2022', '-A', 'x64',
    "-DCMAKE_TOOLCHAIN_FILE=$VcpkgToolchain",
    "-DVCPKG_TARGET_TRIPLET=$VcpkgTriplet",
    "-DCMAKE_EXE_LINKER_FLAGS=$linkFlags",
    '-S', $WalkerSrc,
    '-B', $buildDir
  )
  Write-Host "==> [$variant $($pin.Substring(0,8))] cmake build (Release)"
  Run 'cmake' @('--build', $buildDir, '--config', 'Release')
  if ($RunCTest) {
    Write-Host "==> [$variant $($pin.Substring(0,8))] ctest"
    Run 'ctest' @('--test-dir', $buildDir, '-C', 'Release', '--output-on-failure')
  }
  $exe = Get-ChildItem -Path $buildDir -Recurse -Filter 'cs2_schema_walker.exe' -ErrorAction SilentlyContinue |
         Select-Object -First 1
  if (-not $exe) { Fail "built walker exe not found under $buildDir" }
  return $exe.FullName
}

# ExeFor[pin][variant] = path to that build's exe
$ExeFor = @{}

function Restore-Submodule {
  Write-Host "==> restoring submodule to canonical pin $canonicalPin"
  & git -C $Repo submodule update --checkout walker/third_party/hl2sdk 2>$null
  & git -C $Repo checkout -- walker/src/netmsg_table.generated.inc 2>$null
  $head = (git -C $Sdk rev-parse HEAD).Trim()
  if ($head -ne $canonicalPin) {
    Fail "submodule NOT restored: HEAD=$head expected=$canonicalPin. Fix: git submodule update --checkout walker/third_party/hl2sdk"
  }
  Write-Host "    submodule HEAD == gitlink OK"
}

$hardFail = $false
try {
  # -------------------------------------------------------------------------------------
  # BUILD PHASE: for each distinct pin, build detA + detB with the perturbation.
  # -------------------------------------------------------------------------------------
  if (-not $SkipBuild) {
    foreach ($pin in $pins) {
      $short = $pin.Substring(0,8)
      Write-Host ""
      Write-Host "================ BUILD pin $short  (detA + detB) ================"

      $dirty = git -C $Sdk status --porcelain
      if ($dirty) { Fail "submodule tree dirty before checkout of $short; clean it first.`n$dirty" }
      Write-Host "==> git checkout $short"
      Run 'git' @('-C', $Sdk, 'checkout', '--quiet', $pin)
      $head = (git -C $Sdk rev-parse HEAD).Trim()
      if ($head -ne $pin) { Fail "submodule checkout mismatch: HEAD=$head expected=$pin" }

      Write-Host "==> regenerating netmsg table for $short"
      Run 'python' @($GenNetmsg)

      $dirA = Join-Path $BuildRootA $pin
      $dirB = Join-Path $BuildRootB $pin
      $exeA = Configure-And-Build 'detA' $pin $dirA $Perturb.detA.Flags
      $exeB = Configure-And-Build 'detB' $pin $dirB $Perturb.detB.Flags

      # PERTURBATION-APPLIED ASSERT (false-pass guard): the two exes MUST have
      # different ImageBases and DYNAMICBASE off, else the sweep is a same-layout
      # no-op and its IDENTICAL results would be meaningless.
      $peA = Get-PEInfo $exeA
      $peB = Get-PEInfo $exeB
      Write-Host ("    detA ImageBase=0x{0:X}  DynamicBase={1}" -f $peA.ImageBase, $peA.DynamicBase)
      Write-Host ("    detB ImageBase=0x{0:X}  DynamicBase={1}" -f $peB.ImageBase, $peB.DynamicBase)
      if ($peA.ImageBase -eq $peB.ImageBase) {
        Fail "perturbation NOT applied for ${short}: detA/detB share ImageBase 0x$($peA.ImageBase.ToString('X')). The /BASE flag did not take -- sweep would be a no-op."
      }
      if ($peA.DynamicBase -or $peB.DynamicBase) {
        Write-Warning "DYNAMICBASE still set (detA=$($peA.DynamicBase) detB=$($peB.DynamicBase)); ASLR may re-introduce run-to-run drift. Results remain valid for MISMATCH (still a bug) but re-run before trusting an IDENTICAL."
      }

      $ExeFor[$pin] = @{ detA = $exeA; detB = $exeB }
    }
  } else {
    # SkipBuild: locate existing exes.
    foreach ($pin in $pins) {
      $exeA = Get-ChildItem -Path (Join-Path $BuildRootA $pin) -Recurse -Filter 'cs2_schema_walker.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
      $exeB = Get-ChildItem -Path (Join-Path $BuildRootB $pin) -Recurse -Filter 'cs2_schema_walker.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
      if (-not $exeA -or -not $exeB) { Fail "-SkipBuild: missing exe for pin $($pin.Substring(0,8)) under $BuildRootA / $BuildRootB (build first)" }
      $ExeFor[$pin] = @{ detA = $exeA.FullName; detB = $exeB.FullName }
      # Same false-pass guard as the build path: the reused exes MUST differ in
      # ImageBase (else -SkipBuild would silently compare two same-layout builds).
      $peA = Get-PEInfo $exeA.FullName
      $peB = Get-PEInfo $exeB.FullName
      Write-Host ("    $($pin.Substring(0,8)): detA ImageBase=0x{0:X}  detB ImageBase=0x{1:X}" -f $peA.ImageBase, $peB.ImageBase)
      if ($peA.ImageBase -eq $peB.ImageBase) { Fail "perturbation NOT applied for $($pin.Substring(0,8)): reused detA/detB share ImageBase 0x$($peA.ImageBase.ToString('X')) -- rebuild without -SkipBuild." }
    }
  }
}
catch {
  Write-Host "BUILD PHASE FAILED: $_" -ForegroundColor Red
  $hardFail = $true
}
finally {
  if (-not $SkipBuild) { Restore-Submodule }
}
if ($hardFail) { exit 1 }
if ($BuildOnly) { Write-Host "`n-BuildOnly: built detA+detB for $($pins.Count) pin(s); skipping walk+diff."; exit 0 }

# ---------------------------------------------------------------------------------------
# protoc discovery (needed only if a mismatch must be decoded).
# ---------------------------------------------------------------------------------------
function Resolve-Protoc {
  if ($Protoc -and (Test-Path $Protoc)) { return $Protoc }
  $cmd = Get-Command protoc -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  # vcpkg installed tools (under $VCPKG_ROOT when set).
  if ($env:VCPKG_ROOT) {
    $cand = Get-ChildItem -Path (Join-Path $env:VCPKG_ROOT 'installed') -Recurse -Filter 'protoc.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cand) { return $cand.FullName }
  }
  return $null
}

function Decode-Pb([string]$protoc, [string]$pb, [string]$txt) {
  # cmd /c preserves BINARY stdin redirection (PowerShell pipelines corrupt bytes).
  $q = '"{0}" --decode={1} --proto_path="{2}" walker_output.proto < "{3}" > "{4}"' -f `
        $protoc, $TopMsg, $SchemasDir, $pb, $txt
  & cmd /c $q
  if ($LASTEXITCODE -ne 0) { throw "protoc --decode failed (exit $LASTEXITCODE) on $pb" }
}

# ---------------------------------------------------------------------------------------
# WALK + DIFF PHASE
# ---------------------------------------------------------------------------------------
$results = @()   # rows: Build, Era, Pin, Status, Detail
$anyMismatch = $false
$anyError = $false

foreach ($r in $selected) {
  $binDir = Join-Path (Join-Path $BinRoot $r.Build) $OS
  $repOut = Join-Path $OutRoot $r.Build
  New-Item -ItemType Directory -Force -Path $repOut | Out-Null
  $outA = Join-Path $repOut 'detA.pb'
  $outB = Join-Path $repOut 'detB.pb'
  $exeA = $ExeFor[$r.Pin].detA
  $exeB = $ExeFor[$r.Pin].detB

  Write-Host ""
  Write-Host "---- rep $($r.Build) (era $($r.Era), pin $($r.Pin.Substring(0,8))) ----"

  $walkFailed = $null
  foreach ($pair in @(@{v='detA';exe=$exeA;out=$outA}, @{v='detB';exe=$exeB;out=$outB})) {
    if (Test-Path $pair.out) { Remove-Item -Force $pair.out }
    Write-Host "    walk [$($pair.v)] -> $($pair.out)"
    $code = 1
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try {
      & $pair.exe 'walk' '--binaries' $binDir '--platform' $OS '--out' $pair.out
      $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $prev }
    if ($code -ne 0 -or -not (Test-Path $pair.out)) {
      $walkFailed = "$($pair.v) walk exit=$code (out present=$([bool](Test-Path $pair.out)))"
      break
    }
  }

  if ($walkFailed) {
    $results += [pscustomobject]@{ Build=$r.Build; Era=$r.Era; Pin=$r.Pin.Substring(0,8); Status='WALK-ERROR'; Detail=$walkFailed }
    $anyError = $true
    Write-Host "    WALK-ERROR: $walkFailed" -ForegroundColor Red
    continue
  }

  $hA = (Get-FileHash -Algorithm SHA256 $outA).Hash
  $hB = (Get-FileHash -Algorithm SHA256 $outB).Hash
  if ($hA -eq $hB) {
    $results += [pscustomobject]@{ Build=$r.Build; Era=$r.Era; Pin=$r.Pin.Substring(0,8); Status='IDENTICAL'; Detail=$hA.Substring(0,16) }
    Write-Host "    IDENTICAL ($($hA.Substring(0,16)))" -ForegroundColor Green
    continue
  }

  # MISMATCH -> localize the differing field path(s) via protoc --decode.
  $anyMismatch = $true
  $detail = "sha detA=$($hA.Substring(0,12)) detB=$($hB.Substring(0,12))"
  $protoc = Resolve-Protoc
  if (-not $protoc) {
    Write-Host "    MISMATCH (protoc not found -- cannot decode field path; pass -Protoc)" -ForegroundColor Red
    $results += [pscustomobject]@{ Build=$r.Build; Era=$r.Era; Pin=$r.Pin.Substring(0,8); Status='MISMATCH'; Detail="$detail; decode UNAVAILABLE" }
    continue
  }
  $txtA = Join-Path $repOut 'detA.txt'
  $txtB = Join-Path $repOut 'detB.txt'
  try {
    Decode-Pb $protoc $outA $txtA
    Decode-Pb $protoc $outB $txtB
  } catch {
    Write-Host "    MISMATCH (decode failed: $_)" -ForegroundColor Red
    $results += [pscustomobject]@{ Build=$r.Build; Era=$r.Era; Pin=$r.Pin.Substring(0,8); Status='MISMATCH'; Detail="$detail; decode FAILED" }
    continue
  }
  # Field-path diff: protoc --decode emits indented field-name lines, so a differing
  # line IS the field path. Show the first differing hunks.
  $diff = Compare-Object (Get-Content $txtA) (Get-Content $txtB) -SyncWindow 200
  $diffLines = @($diff | ForEach-Object { "{0} {1}" -f $_.SideIndicator, $_.InputObject })
  $shown = $diffLines | Select-Object -First 40
  Write-Host "    MISMATCH: $($diffLines.Count) differing decoded line(s). First $([Math]::Min(40,$diffLines.Count)):" -ForegroundColor Red
  $shown | ForEach-Object { Write-Host "      $_" }
  $diffFile = Join-Path $repOut 'field-diff.txt'
  $diffLines | Set-Content -Encoding utf8 $diffFile
  Write-Host "    full field diff: $diffFile"
  $results += [pscustomobject]@{ Build=$r.Build; Era=$r.Era; Pin=$r.Pin.Substring(0,8); Status='MISMATCH'; Detail="$($diffLines.Count) lines; $diffFile" }
}

# ---------------------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------------------
Write-Host ""
Write-Host "================ DETERMINISM SWEEP SUMMARY ================"
$results | Sort-Object Build | Format-Table -AutoSize Build, Era, Pin, Status, Detail | Out-String | Write-Host

$nIdent = @($results | Where-Object Status -eq 'IDENTICAL').Count
$nMis   = @($results | Where-Object Status -eq 'MISMATCH').Count
$nErr   = @($results | Where-Object { $_.Status -eq 'WALK-ERROR' }).Count
Write-Host "identical=$nIdent  mismatch=$nMis  walk-error=$nErr  (of $($results.Count) reps)"

if ($anyError)    { Write-Error "one or more reps failed to walk (see WALK-ERROR rows)."; exit 2 }
if ($anyMismatch) { Write-Error "CROSS-BUILD DETERMINISM VIOLATION: a load-address-derived read leaked into output. See MISMATCH rows + field-diff files."; exit 3 }
Write-Host "ALL REPS BYTE-IDENTICAL across the two perturbed builds -- cross-build determinism holds." -ForegroundColor Green
exit 0
