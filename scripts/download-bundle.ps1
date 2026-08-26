<#
.SYNOPSIS
  Download + unpack the newest GitHub Release bundle and resolve the managed host dll.

.DESCRIPTION
  Shared by scheduled-extract.yml's extraction legs and its commit job (one copy of the
  release-asset contract: asset pattern, archive layout, dll path). Downloads the newest
  Release's bundle asset for the requested flavor, unpacks it under <Dir>, verifies
  cs2-schema-tracker.dll is present, appends HOST_DLL to $GITHUB_ENV when set, and writes the
  dll path as the only stdout line (progress goes to the host stream, so `$dll = ./...` works).
  Requires GH_TOKEN in the environment for `gh release download`.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][ValidateSet('windows', 'linux')][string]$Flavor,
  [string]$Dir = 'dist/bundle'
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pattern = if ($Flavor -eq 'windows') { '*.zip' } else { '*-linux-x64.tar.gz' }
$downloadDir = Split-Path $Dir -Parent
if (-not $downloadDir) { $downloadDir = '.' }
New-Item -ItemType Directory -Force -Path $Dir | Out-Null

Write-Host "downloading latest release bundle ($pattern)..."
gh release download --pattern $pattern --dir $downloadDir --clobber
if ($LASTEXITCODE -ne 0) {
  Write-Error "no GitHub Release with a $pattern bundle asset found. release.yml must publish a bundle first (it publishes on the first push to main)."
  exit 1
}
$asset = Get-ChildItem $downloadDir -Filter $pattern | Select-Object -First 1
if (-not $asset) { Write-Error "release download produced no $pattern in $downloadDir/."; exit 1 }

if ($Flavor -eq 'windows') {
  Write-Host "unzipping $($asset.Name) -> $Dir"
  Expand-Archive -Path $asset.FullName -DestinationPath $Dir -Force
}
else {
  Write-Host "untarring $($asset.Name) -> $Dir"
  tar -xzf $asset.FullName -C $Dir
  if ($LASTEXITCODE -ne 0) { Write-Error "tar extraction failed for $($asset.Name)"; exit 1 }
}

# The managed dll ships in both the portable bundle and the self-contained platform bundles,
# and the muxer resolves a self-contained layout's local runtime from the dll's runtimeconfig,
# so `dotnet <dll>` is uniform across flavors.
$dll = Join-Path (Resolve-Path $Dir).Path 'cs2-schema-tracker.dll'
if (-not (Test-Path $dll)) { Write-Error "bundle missing cs2-schema-tracker.dll at $dll"; exit 1 }
if ($env:GITHUB_ENV) { "HOST_DLL=$dll" | Out-File -FilePath $env:GITHUB_ENV -Append }
$dll
