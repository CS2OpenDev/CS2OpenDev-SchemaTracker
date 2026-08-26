# Shared helpers for the git-commit scripts (commit-dump.ps1, commit-forward-capture.ps1).
# Dot-source this file; it defines functions only and runs nothing.

# Resolve the host dll used for commit-plan/evolution. Prefer a pre-published dll named by the
# environment (CI sets one), else build the host Release once.
function Resolve-HostDll {
  param([Parameter(Mandatory)][string]$Repo)
  foreach ($envPath in @($env:CS2_HOST_DLL, $env:HOST_DLL)) {
    if ($envPath -and (Test-Path $envPath)) { return $envPath }
  }
  Write-Host "==> building host (for commit-plan/evolution)..."
  & dotnet build (Join-Path $Repo 'host/src/Cs2SchemaTracker.Host') -c Release -p:SelfContained=false -p:PublishSingleFile=false -p:UseAppHost=false -v q --nologo | Out-Host
  if ($LASTEXITCODE -ne 0) { Write-Error "host build failed (needed for commit-plan)"; exit 1 }
  $dll = Join-Path $Repo 'host/artifacts/bin/Cs2SchemaTracker.Host/release/cs2-schema-tracker.dll'
  if (-not (Test-Path $dll)) { Write-Error "host dll not found after build: $dll"; exit 1 }
  return $dll
}

# Stage one (build, platform) set the way every commit path must: refresh the cumulative
# evolution artifact (non-fatal: the set is already validly promoted, and a rare retryable
# refresh failure must not forfeit it), ask the host for the authoritative plan (exit 65 on an
# incomplete set or a stale changelog), then git add the plan's stagePaths, git rm its
# removePaths, and stage the inventory iff git shows it changed. Returns the parsed plan.
function Invoke-ArtifactSetStage {
  param(
    [Parameter(Mandatory)][string]$HostDll,
    [Parameter(Mandatory)][string]$Repo,
    [Parameter(Mandatory)][string]$Build,
    [Parameter(Mandatory)][string]$Platform,
    [Parameter(Mandatory)][string]$Artifacts,
    [switch]$NoEvolution
  )

  if (-not $NoEvolution) {
    & dotnet $HostDll evolution --platform $Platform --artifacts $Artifacts | Out-Host
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "evolution refresh returned $LASTEXITCODE for build $Build ($Platform); committing the set without an evolution update. re-run 'evolution' later."
    }
  }

  $planJson = & dotnet $HostDll commit-plan --build $Build --platform $Platform --artifacts $Artifacts
  if ($LASTEXITCODE -ne 0) { Write-Error "commit-plan refused build $Build ($Platform); see VIOLATION lines above"; exit 65 }
  $plan = $planJson | ConvertFrom-Json

  foreach ($sp in $plan.stagePaths) {
    git -C $Repo add -- $sp
    if ($LASTEXITCODE -ne 0) { Write-Error "git add failed for '$sp' (build $Build)"; exit 1 }
  }
  if ($plan.PSObject.Properties['removePaths']) {
    foreach ($rp in $plan.removePaths) {
      git -C $Repo rm -q -- $rp
      if ($LASTEXITCODE -ne 0) { Write-Error "git rm failed for '$rp' (build $Build)"; exit 1 }
    }
  }
  if (git -C $Repo status --porcelain -- $plan.inventoryPath) {
    git -C $Repo add -- $plan.inventoryPath
    if ($LASTEXITCODE -ne 0) { Write-Error "git add inventory failed for build $Build"; exit 1 }
  }

  return $plan
}
