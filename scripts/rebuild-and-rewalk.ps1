#requires -version 7
<#
.SYNOPSIS
  One command, run from a Windows host: rebuild the per-era walkers for BOTH platforms
  (windows natively, linux via WSL), repackage the portable Host bundle, then re-walk
  EVERY committed build on both platforms — leaving a fresh walker suite per platform, a
  bundled host, and an updated artifacts/ for all builds.

.DESCRIPTION
  Phases (each fail-loud; skip any with the matching -Skip switch):
    1. build host            dotnet build -> the portable host dll used by every later phase
    2. rebuild walkers       scripts/build-era-walkers.ps1 (windows) then, via WSL,
                             scripts/build-era-walkers.sh (linux). SEQUENTIAL — both share the
                             single hl2sdk submodule working tree and would corrupt each other's
                             per-era pin checkout if run at once. -> natives/{platform}/
    3. bundle                one SELF-CONTAINED bundle per platform via the MSBuild Bundle profile
                             (dotnet publish -p:PublishProfile=Bundle -p:RuntimeIdentifier=<rid>):
                             win natively -> dist/cs2-schema-tracker-<ver>-win-x64.zip; linux via
                             WSL -> ...-linux-x64.tar.gz. Each leg verify-natives-gates its own platform.
    4. re-walk               host `extract --all --commit` per platform: windows natively, linux
                             via WSL. Run in PARALLEL by default (they write disjoint
                             artifacts/<build>/<platform> subtrees). Promotes into artifacts/;
                             does NOT git-commit (commit is a separate, deliberate step).

  STATUS: a timestamped line is emitted at each phase boundary and every
  -StatusIntervalSeconds during long phases (walker builds: eras done; re-walk: builds done).

  LINUX SPECIFICS the script handles for you (learned the hard way):
    * The CS2 binaries drive must be reachable in WSL. WSL drops a hand-mounted drive when it
      idles, so the script installs a PERSISTENT /etc/fstab drvfs mount (S: -> /mnt/s) as root
      (WSL `-u root` needs no password) so it survives WSL restarts across the long re-walk.
    * The linux render convars need NO GPU/driver (they register at the render module's Init),
      but a Vulkan ICD, if present, is used; the script pins lavapipe (software Vulkan) via
      VK_ICD_FILENAMES for deterministic output. libva-style shims under ~/syslibs are added to
      LD_LIBRARY_PATH when present; the host appends each build's game .so dirs itself.

.NOTES
  Does NOT git-commit. When it finishes: review artifacts/, then commit (e.g. the artifacts +
  data/cs2-assets-inventory.json).
#>
[CmdletBinding()]
param(
  # Repo root (default: parent of this script's dir).
  [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

  # CS2 binaries store root. windows reads it natively; linux reads it through the WSL drvfs
  # mount of the same drive. Convention: <root>\<build>\<platform>\.
  [string]$BinariesRoot = 'S:\Counter-Strike 2\cs2-binaries',

  # WSL distro that carries the linux toolchain (dotnet under ~/.dotnet, g++/cmake, mesa vulkan).
  [string]$WslDistro = 'Ubuntu',

  # Which platforms to process. Default: both.
  [ValidateSet('windows-x86_64', 'linux-x86_64')]
  [string[]]$Platforms = @('windows-x86_64', 'linux-x86_64'),

  [switch]$SkipWalkerRebuild,   # reuse the natives already in natives/
  [switch]$SkipBundle,          # do not repackage the host bundle
  [switch]$SkipRewalk,          # stop after walkers + bundle

  # By default the re-walk passes --no-acquire (input binaries must already be in the store).
  # Pass -AllowAcquire to let extract fetch any missing build's binaries (may download GBs).
  [switch]$AllowAcquire,

  # Run the windows + linux re-walks one after another instead of together (lower peak load).
  [switch]$SequentialRewalk,

  [int]$StatusIntervalSeconds = 180,

  [string]$VcpkgRoot = $(if ($env:VCPKG_ROOT) { $env:VCPKG_ROOT } else { 'C:\tools\vcpkg' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ------------------------------------------------------------------------------------------------
# helpers
# ------------------------------------------------------------------------------------------------
$script:Start = Get-Date
function Status([string]$msg, [string]$phase = '') {
  $ts = (Get-Date).ToString('HH:mm:ss')
  $el = '{0:hh\:mm\:ss}' -f ((Get-Date) - $script:Start)
  $tag = if ($phase) { "[$phase] " } else { '' }
  Write-Host "[$ts +$el] $tag$msg"
}
function Fail([string]$msg) { Write-Host "ERROR: $msg" -ForegroundColor Red; exit 1 }

# C:\dev\x -> /mnt/c/dev/x  (spaces preserved; caller quotes for bash).
function To-Wsl([string]$winPath) {
  if ($winPath -match '^([A-Za-z]):[\\/](.*)$') {
    return "/mnt/$($matches[1].ToLower())/$($matches[2] -replace '\\','/')"
  }
  return $winPath
}

$LogDir = Join-Path $Repo 'dist\rebuild-logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Run a child process to completion, tailing its log for progress every interval. $Progress takes
# the log text and returns a short status string (or $null). Fails unless the process exits 0 OR
# $SuccessMarker (a regex) is found in the log.
function Invoke-Phase {
  param(
    [Parameter(Mandatory)][string]$Phase,
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string[]]$ArgumentList,
    [Parameter(Mandatory)][scriptblock]$Progress,
    [string]$SuccessMarker = ''
  )
  $out = Join-Path $LogDir "$Phase.out.log"
  $err = Join-Path $LogDir "$Phase.err.log"
  Status "starting" $Phase
  $p = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru -NoNewWindow `
    -RedirectStandardOutput $out -RedirectStandardError $err
  $last = Get-Date
  while (-not $p.HasExited) {
    Start-Sleep -Seconds 5
    if (((Get-Date) - $last).TotalSeconds -ge $StatusIntervalSeconds) {
      $txt = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
      $s = & $Progress $txt
      if ($s) { Status $s $Phase }
      $last = Get-Date
    }
  }
  $txt = (Get-Content $out -Raw -ErrorAction SilentlyContinue)
  $ok = ($p.ExitCode -eq 0) -or ($SuccessMarker -and $txt -match $SuccessMarker)
  if (-not $ok) {
    Get-Content $err -Tail 15 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  | $_" }
    Fail "$Phase failed (exit $($p.ExitCode)). Logs: $out ; $err"
  }
  Status "done" $Phase
  return $out
}

# progress extractors
$erasProgress = { param($t) if ($t) { "eras built: {0}/11" -f ([regex]::Matches($t, 'installed:').Count) } }
$rewalkProgress = {
  param($t)
  if ($t) {
    $m = [regex]::Matches($t, 'extract:\s*\[(\d+)/(\d+)\]')
    if ($m.Count) { $g = $m[$m.Count - 1].Groups; return "builds: $($g[1].Value)/$($g[2].Value)" }
  }
}

$doWin = $Platforms -contains 'windows-x86_64'
$doLin = $Platforms -contains 'linux-x86_64'
$wslRepo = To-Wsl $Repo
$binWsl = To-Wsl $BinariesRoot

# ------------------------------------------------------------------------------------------------
# 0. preflight
# ------------------------------------------------------------------------------------------------
Status "repo=$Repo  platforms=$($Platforms -join ',')  binaries=$BinariesRoot" 'preflight'
if (-not (Test-Path (Join-Path $Repo 'walker\CMakeLists.txt'))) { Fail "not a repo root (no walker/CMakeLists.txt): $Repo" }
if ($doLin) {
  $wslCheck = (& wsl -d $WslDistro -- bash -lc 'command -v g++ >/dev/null && test -x "$HOME/.dotnet/dotnet" && echo ok' 2>$null)
  if ($wslCheck -notmatch 'ok') { Fail "WSL '$WslDistro' missing g++ and/or ~/.dotnet/dotnet — required for the linux walker build/re-walk." }
}
Set-Location $Repo

# ------------------------------------------------------------------------------------------------
# 1. build host (used by every later phase: era-build plan, bundle, re-walk)
# ------------------------------------------------------------------------------------------------
$HostDll = Join-Path $Repo 'host\artifacts\bin\Cs2SchemaTracker.Host\release\cs2-schema-tracker.dll'
Invoke-Phase -Phase 'host-build' -FilePath 'dotnet' -Progress { param($t) 'building host' } `
  -ArgumentList @('build', (Join-Path $Repo 'host\src\Cs2SchemaTracker.Host'),
    '-c', 'Release', '-p:SelfContained=false', '-p:PublishSingleFile=false', '-p:UseAppHost=false',
    '-v', 'q', '--nologo') | Out-Null
if (-not (Test-Path $HostDll)) { Fail "host dll not found after build: $HostDll" }
$hostWsl = To-Wsl $HostDll

# ------------------------------------------------------------------------------------------------
# 2. rebuild era walkers (windows then linux — sequential; shared submodule)
# ------------------------------------------------------------------------------------------------
if (-not $SkipWalkerRebuild) {
  $env:VCPKG_ROOT = $VcpkgRoot
  $env:CS2_HOST_DLL = $HostDll
  if ($doWin) {
    Invoke-Phase -Phase 'walkers-windows' -FilePath 'pwsh' -Progress $erasProgress -SuccessMarker 'built=\d+' `
      -ArgumentList @('-NoProfile', '-File', (Join-Path $Repo 'scripts\build-era-walkers.ps1'), '-Repo', $Repo, '-Force') | Out-Null
  }
  if ($doLin) {
    # build-era-walkers.sh resolves CS2_HOST_DLL (the portable dll runs under linux dotnet too).
    $linBuild = "cd '$wslRepo' && export PATH=""`$HOME/.dotnet:`$PATH"" && export CS2_HOST_DLL='$hostWsl' && bash scripts/build-era-walkers.sh --force"
    Invoke-Phase -Phase 'walkers-linux' -FilePath 'wsl' -Progress $erasProgress -SuccessMarker 'built=\d+' `
      -ArgumentList @('-d', $WslDistro, '--', 'bash', '-lc', $linBuild) | Out-Null
  }
} else { Status "skipped (using existing natives/)" 'walkers' }

# ------------------------------------------------------------------------------------------------
# 3. repackage host bundle(s) — one SELF-CONTAINED bundle per platform via the MSBuild Bundle
#    profile, each built on its OWN OS (win natively -> .zip; linux via WSL -> .tar.gz with exec
#    bits). Each leg verify-natives-gates only its own platform (needs just that platform's natives).
# ------------------------------------------------------------------------------------------------
if (-not $SkipBundle) {
  $hostProj = Join-Path $Repo 'host\src\Cs2SchemaTracker.Host'
  if ($doWin) {
    Invoke-Phase -Phase 'bundle-windows' -FilePath 'dotnet' -Progress { param($t) 'assembling win-x64 bundle' } -SuccessMarker 'bundle assembled' `
      -ArgumentList @('publish', $hostProj, '-p:PublishProfile=Bundle', '-p:RuntimeIdentifier=win-x64', '-v', 'm', '--nologo') | Out-Null
  }
  if ($doLin) {
    # Publish the linux-x64 bundle UNDER WSL so bundle.targets' chmod+tar run on linux (exec bits).
    $linBundle = "cd '$wslRepo' && export PATH=""`$HOME/.dotnet:`$PATH"" && dotnet publish host/src/Cs2SchemaTracker.Host -p:PublishProfile=Bundle -p:RuntimeIdentifier=linux-x64 -v m --nologo"
    Invoke-Phase -Phase 'bundle-linux' -FilePath 'wsl' -Progress { param($t) 'assembling linux-x64 bundle' } -SuccessMarker 'bundle assembled' `
      -ArgumentList @('-d', $WslDistro, '--', 'bash', '-lc', $linBundle) | Out-Null
  }
  Get-ChildItem (Join-Path $Repo 'dist') -Filter 'cs2-schema-tracker-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.zip', '.gz' } | Sort-Object LastWriteTime |
    ForEach-Object { Status "bundle: $($_.Name)" 'bundle' }
} else { Status "skipped" 'bundle' }

# ------------------------------------------------------------------------------------------------
# 4. re-walk every build, per platform (promote into artifacts/, NO git)
# ------------------------------------------------------------------------------------------------
if ($SkipRewalk) { Status "re-walk skipped (walkers + bundle are up to date)"; Status "ALL DONE"; exit 0 }

$env:CS2_BINARIES_ROOT = $BinariesRoot
$env:CS2_WALKER_ERAS_ROOT = (Join-Path $Repo 'natives')
$acquireArg = if ($AllowAcquire) { '' } else { '--no-acquire' }

# Ensure the CS2 drive is reachable in WSL for the LIFETIME of the re-walk: install a persistent
# /etc/fstab drvfs mount (idempotent) as root, and mount it now. Survives WSL idle-restarts.
function Ensure-WslDriveMount {
  if (-not ($BinariesRoot -match '^([A-Za-z]):')) { return }
  $drive = "$($matches[1]):"
  $mnt = To-Wsl "$drive\"
  $mnt = $mnt.TrimEnd('/')
  $prep = "grep -q '^$drive $mnt' /etc/fstab || echo '$drive $mnt drvfs defaults 0 0' >> /etc/fstab; mkdir -p '$mnt'; mountpoint -q '$mnt' || mount -t drvfs $drive '$mnt'"
  & wsl -d $WslDistro -u root -- bash -c $prep 2>&1 | Out-Null
  $ok = (& wsl -d $WslDistro -- bash -lc "test -d '$binWsl' && echo ok" 2>$null)
  if ($ok -notmatch 'ok') { Fail "CS2 binaries not reachable in WSL at '$binWsl' (drvfs mount failed)." }
}

# Ensure the standard system libs the Linux walk needs are present. The walk dlopen's the
# build's CS2 modules; on the oldest era (cs2-2023-09-13) the REQUIRED client module ->
# libvideo.so -> the FFmpeg/graphics stack transitively needs a few standard libs the build
# does NOT ship. The build's OWN heavy libs (libavcodec.so.58 ...) are used directly via the
# host's LD_LIBRARY_PATH prepend, so no system FFmpeg is needed — only these. Idempotent:
# skipped when already present (fast path). See docs/WALKER.md "Linux runtime system-lib
# prerequisite".
function Ensure-WslWalkerRuntimeLibs {
  $have = (& wsl -d $WslDistro -- bash -lc "ldconfig -p 2>/dev/null | grep -q 'libva\.so\.2' && echo ok" 2>$null)
  if ($have -match 'ok') { return }
  Status "installing linux walker runtime libs (libX11/libbz2/libdrm/libuuid/libva{,-drm,-x11}/libvdpau)..." "rewalk:linux-x86_64"
  & wsl -d $WslDistro -u root -- bash -c "apt-get update -qq >/dev/null 2>&1; apt-get install -y libx11-6 libbz2-1.0 libdrm2 libuuid1 libva2 libva-drm2 libva-x11-2 libvdpau1 >/dev/null 2>&1" 2>&1 | Out-Null
}

# Launch a per-platform re-walk as a background process writing to $log; return @{ Proc; Log; Platform }.
function Start-Rewalk([string]$platform) {
  $log = Join-Path $LogDir "rewalk-$platform.log"
  if ($platform -eq 'windows-x86_64') {
    $cmd = "`$env:CS2_BINARIES_ROOT='$BinariesRoot'; `$env:CS2_WALKER_ERAS_ROOT='$($env:CS2_WALKER_ERAS_ROOT)'; " +
    "dotnet '$HostDll' extract --all --platform windows-x86_64 $acquireArg --commit"
    $p = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-Command', $cmd) -PassThru -NoNewWindow `
      -RedirectStandardOutput $log -RedirectStandardError (Join-Path $LogDir "rewalk-$platform.err.log")
  } else {
    Ensure-WslDriveMount
    Ensure-WslWalkerRuntimeLibs
    $lin = "export PATH=""`$HOME/.dotnet:`$PATH""; " +
    "[ -f /usr/share/vulkan/icd.d/lvp_icd.json ] && export VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json; " +
    "[ -d ""`$HOME/syslibs/root/usr/lib/x86_64-linux-gnu"" ] && export LD_LIBRARY_PATH=""`$HOME/syslibs/root/usr/lib/x86_64-linux-gnu""; " +
    "export CS2_BINARIES_ROOT='$binWsl'; export CS2_WALKER_ERAS_ROOT='$wslRepo/natives'; cd '$wslRepo'; " +
    "dotnet '$hostWsl' extract --all --platform linux-x86_64 $acquireArg --commit"
    $p = Start-Process -FilePath 'wsl' -ArgumentList @('-d', $WslDistro, '--', 'bash', '-lc', $lin) -PassThru -NoNewWindow `
      -RedirectStandardOutput $log -RedirectStandardError (Join-Path $LogDir "rewalk-$platform.err.log")
  }
  Status "started (log: $log)" "rewalk:$platform"
  return @{ Proc = $p; Log = $log; Platform = $platform }
}

# Poll a set of running re-walks, printing per-platform progress until all exit.
function Watch-Rewalks([object[]]$jobs) {
  $last = Get-Date
  while ($jobs | Where-Object { -not $_.Proc.HasExited }) {
    Start-Sleep -Seconds 5
    if (((Get-Date) - $last).TotalSeconds -ge $StatusIntervalSeconds) {
      foreach ($j in $jobs) {
        $txt = Get-Content $j.Log -Raw -ErrorAction SilentlyContinue
        $s = & $rewalkProgress $txt
        $state = if ($j.Proc.HasExited) { 'exited' } else { $s }
        if ($state) { Status $state "rewalk:$($j.Platform)" }
      }
      $last = Get-Date
    }
  }
}

$targets = @()
if ($doWin) { $targets += 'windows-x86_64' }
if ($doLin) { $targets += 'linux-x86_64' }

if ($SequentialRewalk) {
  foreach ($t in $targets) { $j = Start-Rewalk $t; Watch-Rewalks @($j) }
} else {
  $jobs = @($targets | ForEach-Object { Start-Rewalk $_ })
  Watch-Rewalks $jobs
}

# ------------------------------------------------------------------------------------------------
# 5. summary
# ------------------------------------------------------------------------------------------------
Status "===== RE-WALK SUMMARY =====" 'done'
foreach ($t in $targets) {
  $log = Join-Path $LogDir "rewalk-$t.log"
  $sum = (Get-Content $log -ErrorAction SilentlyContinue | Select-String -Pattern '^extract: ok=' | Select-Object -Last 1)
  Status ($(if ($sum) { $sum.Line } else { '(no summary line — check ' + $log + ')' })) "rewalk:$t"
}
Status "artifacts/ updated. NOT committed — review, then git-commit the artifact sets + inventory." 'done'
Status "ALL DONE"
