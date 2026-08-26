<#
.SYNOPSIS
  Git-commit + tag an ALREADY-PROMOTED artifact set.

.DESCRIPTION
  The extract + era-aware gate + promote-into-artifacts/ work is done by the host:
    cs2-schema-tracker extract --build <id> --platform <P> --commit
  This script is the small, explicit GIT step that follows (the host never git-commits,
  by design). For one or more already-on-disk `artifacts/<build>/<platform>/` sets it asks the
  host for the authoritative commit PLAN (`cs2-schema-tracker commit-plan`), which validates the
  set is complete (the SAME ArtifactSet / content-depot gating verify-artifacts uses, so the
  required-file list can never drift from a hand-maintained copy here), derives the commit + tag
  message from the promoted provenance.json, and names the paths to stage plus any preserved
  PICS capture to remove; the script then runs git against it. The staging contract itself lives
  in scripts/lib/commit-common.ps1, shared with the scheduled pipeline's commit job.

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

. (Join-Path $PSScriptRoot 'lib/commit-common.ps1')

$HostDll = Resolve-HostDll -Repo $Repo
$Artifacts = Join-Path $Repo 'artifacts'

foreach ($b in $Build) {
  $plan = Invoke-ArtifactSetStage -HostDll $HostDll -Repo $Repo -Build $b -Platform $Platform `
    -Artifacts $Artifacts -NoEvolution:$NoEvolution

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
