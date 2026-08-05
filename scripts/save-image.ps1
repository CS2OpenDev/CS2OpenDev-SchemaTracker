<#
.SYNOPSIS
  Build the Docker runtime-wrapper image, save it to a fingerprint-named tarball, and print
  the CS2_EXPECT_FPRINT the operator must export on the remote host before running it.

.DESCRIPTION
  Image hygiene: closes the "stale remote image" hole. The host-side startup identity gate
  can assert the walker fingerprint baked into a running container matches what the operator
  EXPECTS, but only if the operator has a trustworthy fingerprint to assert in the first
  place. This script is that source of truth: it names the saved tar after the
  walker-source content fingerprint that was actually baked into the image, so the filename
  and the required env var can never drift apart by hand-typo.

  Steps (mirrors the existing manual flow documented in BUILD.md "Docker image"):
    1. Refuse if walker/ or schemas/ has uncommitted changes (the whole point of the
       fingerprint is that it is a reproducible function of committed content; a dirty tree
       makes it a lie).
    2. Compute srcFingerprint via walker/tools/src_fingerprint.py (same tool + same content
       scope the build scripts use for their resumability key).
    3. dotnet publish the self-contained linux-x64 host into dist/docker/host.
    4. docker build -f docker/Dockerfile -t <ImageTag> .
       (Context = repo root; the Dockerfile pulls natives/linux-x86_64/ and data/ from it --
       this script does NOT build those; run scripts/build-era-walkers.sh first.)
    5. docker save -o dist/cs2-schema-tracker-<srcFingerprint first 12 hex>.tar <ImageTag>
    6. Print `CS2_EXPECT_FPRINT=<same 12-hex prefix>` -- deliberately the SAME short prefix
       embedded in the tar filename (not the full 64-hex digest): the host-side startup
       identity gate treats CS2_EXPECT_FPRINT as a PREFIX match against the walker's
       reported fingerprint, so the tar filename and the env var the operator exports are
       always textually identical -- copy the filename's suffix, paste it, done.

.NOTES
  HEAVY BUILD (dotnet publish + docker build). Operator/CI only. Produces a LINUX image
  only -- see BUILD.md "Docker image" ("Windows builds are not covered").

  Does not itself verify natives/linux-x86_64/ is up to date or portable; the Dockerfile's
  own ldd guard (see BUILD.md "Docker image") fails loud at build time if it is not.
#>
[CmdletBinding()]
param(
  # Repo root (default: parent of this script's dir).
  [string]$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,

  # Docker image tag to build/save (matches the BUILD.md "Docker image" convention).
  [string]$ImageTag = 'cs2-schema-tracker:latest',

  # Base name used for the saved tar: dist/<ImageName>-<fprint12>.tar
  [string]$ImageName = 'cs2-schema-tracker',

  # Skip the `dotnet publish` step (reuse an already-published dist/docker/host).
  [switch]$SkipPublish,

  # Skip the `docker build` step (reuse an already-built local image tagged $ImageTag).
  [switch]$SkipDockerBuild,

  # Output dir for the saved tar (default: <repo>/dist).
  [string]$OutDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'dist')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail($msg) { Write-Error $msg; exit 1 }
function Run($exe, [string[]]$argv) {
  # Native tools (git, dotnet, docker) legitimately write progress to stderr; localize to
  # Continue around the call so ONLY a nonzero exit code is a failure (mirrors
  # build-era-walkers.ps1's Run()).
  $prev = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { & $exe @argv | Out-Host } finally { $ErrorActionPreference = $prev }
  if ($LASTEXITCODE -ne 0) { Fail "command failed (exit $LASTEXITCODE): $exe $($argv -join ' ')" }
}

$Dockerfile = Join-Path $Repo 'docker\Dockerfile'
$HostProj   = Join-Path $Repo 'host\src\Cs2SchemaTracker.Host\Cs2SchemaTracker.Host.csproj'
$PublishOut = Join-Path $Repo 'dist\docker\host'
$SrcFingerprintTool = Join-Path $Repo 'walker\tools\src_fingerprint.py'

# --- preflight ---------------------------------------------------------------------------
if (-not (Test-Path $Dockerfile)) { Fail "Dockerfile not found: $Dockerfile" }
if (-not (Test-Path $HostProj)) { Fail "host project not found: $HostProj" }
if (-not (Test-Path $SrcFingerprintTool)) { Fail "src-fingerprint tool not found: $SrcFingerprintTool (required to fingerprint the image)" }

# Refuse on a dirty walker/schemas tree: the fingerprint is only meaningful as a function of
# COMMITTED content. A dirty tree would bake uncommitted bytes into the image but stamp a
# filename/env-var that another checkout of the same commit could never reproduce.
$dirty = git -C $Repo status --porcelain -- walker/ schemas/
if ($dirty) {
  Fail "walker/ or schemas/ has uncommitted changes -- refusing (the image fingerprint would not be reproducible from a clean checkout of HEAD).`n$dirty"
}

# --- (2) compute the content fingerprint (same tool/content-scope as the build scripts) --
Write-Host "==> computing srcFingerprint"
$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'  # see Run(): native stderr must not terminate under a redirect
try { $srcFingerprint = (& python $SrcFingerprintTool | Select-Object -First 1) }
finally { $ErrorActionPreference = $prevEap }
if ($LASTEXITCODE -ne 0) { Fail "src_fingerprint.py failed (exit $LASTEXITCODE)" }
$srcFingerprint = ("$srcFingerprint").Trim()
if ($srcFingerprint -notmatch '^[0-9a-f]{64}$') { Fail "src_fingerprint.py did not print a 64-hex digest (got '$srcFingerprint')" }
$fprintShort = $srcFingerprint.Substring(0, 12)
Write-Host "    srcFingerprint=$srcFingerprint (short=$fprintShort)"

# --- (3) self-contained linux-x64 publish (BUILD.md "Self-contained publish") ------------
if (-not $SkipPublish) {
  Write-Host "==> dotnet publish (self-contained linux-x64 host)"
  Run 'dotnet' @(
    'publish', $HostProj,
    '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true',
    '-p:PublishTrimmed=false', '-p:UseAppHost=true', '-p:BundleRelease=false',
    '-o', $PublishOut
  )
} else {
  Write-Host "==> -SkipPublish: reusing existing $PublishOut"
  if (-not (Test-Path $PublishOut)) { Fail "-SkipPublish but publish output not found: $PublishOut" }
}

# --- (4) docker build (BUILD.md "Docker image") -------------------------------------------
if (-not $SkipDockerBuild) {
  Write-Host "==> docker build -t $ImageTag"
  Run 'docker' @('build', '-f', $Dockerfile, '-t', $ImageTag, $Repo)
} else {
  Write-Host "==> -SkipDockerBuild: reusing existing local image $ImageTag"
}

# --- (5) docker save, named after the fingerprint that was actually baked in -------------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$tarPath = Join-Path $OutDir "$ImageName-$fprintShort.tar"
Write-Host "==> docker save -> $tarPath"
Run 'docker' @('save', '-o', $tarPath, $ImageTag)
if (-not (Test-Path $tarPath)) { Fail "docker save did not produce $tarPath" }
$tarSizeMb = [Math]::Round((Get-Item $tarPath).Length / 1MB, 1)

# --- (6) the line the operator copies onto the remote host -------------------------------
Write-Host ""
Write-Host "================ SAVE-IMAGE SUMMARY ================"
Write-Host "image:           $ImageTag"
Write-Host "srcFingerprint:  $srcFingerprint"
Write-Host "tar:             $tarPath ($tarSizeMb MB)"
Write-Host ""
Write-Host "Transfer the tar to the remote host, 'docker load -i $($ImageName)-$fprintShort.tar',"
Write-Host "then export the SAME short fingerprint the startup identity gate checks"
Write-Host "(prefix-match against the walkers actually baked into the image) before running it:"
Write-Host ""
Write-Host "CS2_EXPECT_FPRINT=$fprintShort"
Write-Host ""
exit 0
