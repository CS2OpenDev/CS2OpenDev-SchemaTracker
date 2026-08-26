<#
.SYNOPSIS
  Assemble one extraction leg's upload payload for the commit job.

.DESCRIPTION
  Owns the producer half of the leg payload contract whose consumer is
  commit-forward-capture.ps1 (the two live side by side in scripts/): the platform set minus the
  gitignored localization.json, the build manifest so the commit job can merge THIS platform's
  content carrier, any PICS capture sidecar, and meta.json { buildId, platform } written LAST so
  a payload interrupted mid-copy has no meta.json and the commit job ignores it whole.

  Runs even after a failed extract: the payload then degrades to meta.json plus the sidecar,
  which is exactly what the commit job's preservation path needs.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$BuildId,
  [Parameter(Mandatory)][string]$Platform,
  [switch]$ExtractOk,
  [string]$OutDir = 'outgoing'
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($ExtractOk) {
  $dst = "$OutDir/artifacts/$BuildId/$Platform"
  New-Item -ItemType Directory -Force -Path $dst | Out-Null
  # localization.json is gitignored (~199 MB, regenerable) and must not ride the upload;
  # excluded from the copy rather than copied and deleted.
  Get-ChildItem "artifacts/$BuildId/$Platform" -Exclude localization.json |
    Copy-Item -Destination $dst -Recurse
  # The build manifest rides so the commit job can merge THIS platform's content carrier
  # into the committed one (a whole-file copy would drop the other platform's).
  if (Test-Path "artifacts/$BuildId/omissions.json") {
    Copy-Item "artifacts/$BuildId/omissions.json" "$OutDir/artifacts/$BuildId/omissions.json"
  }
}

$sidecar = "cache/pics/$BuildId/$Platform/pics-appinfo-capture.json"
if (Test-Path $sidecar) { Copy-Item $sidecar (Join-Path $OutDir 'pics-appinfo-capture.json') }

# meta.json LAST: its presence marks the payload complete.
@{ buildId = $BuildId; platform = $Platform } |
  ConvertTo-Json | Set-Content -Path (Join-Path $OutDir 'meta.json')

Write-Host "staged payload:"
Get-ChildItem $OutDir -Recurse -File | ForEach-Object { Write-Host "  $($_.FullName)" }
