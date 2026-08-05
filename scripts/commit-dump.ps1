<#
.SYNOPSIS
  Git-commit + tag an ALREADY-PROMOTED artifact set.

.DESCRIPTION
  The extract + era-aware gate + promote-into-artifacts/ work is done by the host:
    cs2-schema-tracker extract --build <id> --platform <P> --commit
  This script is the small, explicit GIT step that follows (the host never git-commits,
  by design). For one or more already-on-disk `artifacts/<build>/<platform>/` sets it asks the
  host for the authoritative commit PLAN (`cs2-schema-tracker commit-plan`) — which validates the
  set is complete (the SAME ArtifactSet / content-depot gating verify-artifacts uses, so the
  required-file list can never drift from a hand-maintained copy here), derives the commit + tag
  message from the promoted provenance.json, and names the paths to stage — then runs git against it.

  Review the `extract --commit` diff BEFORE running this. Never pushes.

.NOTES
  All completeness / message / staging judgement lives in the host (Cli/CommitPlanCommand); this
  script only executes git. Host dll: $CS2_HOST_DLL / $HOST_DLL if set, else built Release once.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string[]]$Build,          # one or more build ids
  [string]$Platform = "windows-x86_64",
  [string]$Repo     = (Get-Location).Path,
  [switch]$Tag,                                     # also write/refresh build/<id> tag
  [switch]$Force,                                   # move an existing build/<id> tag
  [switch]$NoEvolution                              # skip the cumulative schema-evolution refresh
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Resolve the host dll used for `commit-plan`. Prefer a pre-published dll named by the environment
# (CI sets one), else build the host Release once.
function Resolve-HostDll {
  foreach ($env in @($env:CS2_HOST_DLL, $env:HOST_DLL)) {
    if ($env -and (Test-Path $env)) { return $env }
  }
  Write-Host "==> building host (for commit-plan)..."
  & dotnet build (Join-Path $Repo 'host\src\Cs2SchemaTracker.Host') -c Release -p:SelfContained=false -p:PublishSingleFile=false -p:UseAppHost=false -v q --nologo | Out-Host
  if ($LASTEXITCODE -ne 0) { Write-Error "host build failed (needed for commit-plan)"; exit 1 }
  $dll = Join-Path $Repo 'host\artifacts\bin\Cs2SchemaTracker.Host\release\cs2-schema-tracker.dll'
  if (-not (Test-Path $dll)) { Write-Error "host dll not found after build: $dll"; exit 1 }
  return $dll
}
$HostDll = Resolve-HostDll
$Artifacts = Join-Path $Repo 'artifacts'

foreach ($b in $Build) {
  # Refresh the fixed-path cumulative schema-evolution artifact BEFORE commit-plan, so its updated
  # bytes are on disk when commit-plan names it as a stage path (it rides in this build's commit).
  # Incremental when a contiguous prior exists; otherwise a from-scratch full backfill (the first run
  # / after a mid-chain backfill). Non-fatal: the build set is already validly promoted; a failure
  # just leaves the artifact for a manual `evolution` re-run.
  if (-not $NoEvolution) {
    & dotnet $HostDll evolution --platform $Platform --artifacts $Artifacts
    if ($LASTEXITCODE -ne 0) { Write-Warning "evolution refresh returned $LASTEXITCODE for build $b ($Platform) — committing the set without an evolution update; re-run 'evolution' later." }
  }

  # The host validates completeness (fail-loud, exit 65 on an incomplete set) and emits the plan.
  $planJson = & dotnet $HostDll commit-plan --build $b --platform $Platform --artifacts $Artifacts
  if ($LASTEXITCODE -ne 0) { Write-Error "commit-plan refused build $b ($Platform) — see VIOLATION lines above"; exit 65 }
  $plan = $planJson | ConvertFrom-Json

  foreach ($sp in $plan.stagePaths) {
    git -C $Repo add -- $sp
    if ($LASTEXITCODE -ne 0) { Write-Error "git add failed for '$sp' (build $b)"; exit 1 }
  }
  # A forward-capture extract --commit of a never-before-seen build appends its row to the
  # single-source inventory (plan.inventoryPath). Stage that change too so the committed artifact
  # set and the committed inventory stay in lockstep. No-op when unchanged.
  if (git -C $Repo status --porcelain -- $plan.inventoryPath) {
    git -C $Repo add -- $plan.inventoryPath
    if ($LASTEXITCODE -ne 0) { Write-Error "git add inventory failed for build $b"; exit 1 }
  }

  git -C $Repo commit -q -m $plan.commitMessage
  if ($LASTEXITCODE -ne 0) { Write-Error "git commit failed for build $b (nothing staged? already committed?)"; exit 1 }
  Write-Host "committed: build $b ($Platform)"

  if ($Tag) {
    $tagArgs = @("-C", $Repo, "tag", "-a", $plan.tagName, "-m", $plan.tagMessage)
    if ($Force) { $tagArgs = @("-C", $Repo, "tag", "-f", "-a", $plan.tagName, "-m", $plan.tagMessage) }
    git @tagArgs
    if ($LASTEXITCODE -ne 0) { Write-Error "git tag failed for build $b (exists? use -Force)"; exit 1 }
    Write-Host "tagged: $($plan.tagName)"
  }
}
Write-Host "done. Review with 'git log'/'git show' before pushing (this script never pushes)."
