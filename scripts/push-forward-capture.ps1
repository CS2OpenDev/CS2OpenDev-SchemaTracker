<#
.SYNOPSIS
  Push the aggregation's commits, then tag the pushed builds, from the aggregation's push-plan.

.DESCRIPTION
  Consumes the push-plan.json commit-forward-capture.ps1 writes ({ sets, tags: [ { build,
  message } ] }) and runs the one git judgement the aggregation defers: push with ONE rebase
  retry (the only writer that can interleave is an operator push landing between the commit
  job's checkout and its push), then create the build/<id> tags AFTER the push, so a rebase
  retry can never leave a tag pointing at a pre-rebase commit.

  The tag target is located by CONTENT (the newest commit touching artifacts/<id>/), never by
  matching commit-message text: after a rebase retry the pre-push range is stale, and a
  patch-equivalent operator commit may have replaced the run's own commit (tagging the
  surviving identical commit is correct). Tag failures are NON-FATAL here, recorded in the
  tags_failed output, so the caller's channel refresh and verify gate still run for sets that
  DID land; the workflow's last step turns them red.

  Outputs appended to $GITHUB_OUTPUT when set: pushed, sets, tags_failed.
  Exit: nonzero only when the push itself fails (a genuine rebase conflict fails loud; the
  payloads stay downloadable for 7 days and the next cron re-extracts while the build is
  current).
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PlanPath,
  [string]$Remote = 'origin',
  [string]$Branch = 'main'
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Out-StepOutput([string]$line) {
  if ($env:GITHUB_OUTPUT) { $line | Out-File -FilePath $env:GITHUB_OUTPUT -Append }
}

$pending = @(git rev-list "$Remote/$Branch..HEAD")
if ($LASTEXITCODE -ne 0) { Write-Error "git rev-list failed"; exit 1 }
if ($pending.Count -eq 0) {
  Write-Host "nothing to push."
  Out-StepOutput "pushed=false"
  Out-StepOutput "sets=false"
  Out-StepOutput "tags_failed=false"
  exit 0
}

# Commits exist, so the aggregation ran to completion and always wrote the plan; a missing
# file here is a real fault and fails loud.
$plan = Get-Content $PlanPath -Raw | ConvertFrom-Json
$sets = [bool]$plan.sets

git push $Remote "HEAD:$Branch"
if ($LASTEXITCODE -ne 0) {
  Write-Host "push rejected (non-fast-forward?). rebasing onto current $Remote/$Branch and retrying once..."
  git pull --rebase $Remote $Branch
  if ($LASTEXITCODE -ne 0) { Write-Error "git pull --rebase failed"; exit $LASTEXITCODE }
  git push $Remote "HEAD:$Branch"
  if ($LASTEXITCODE -ne 0) { Write-Error "git push ($Branch) failed after rebase retry"; exit $LASTEXITCODE }
}
Write-Host "pushed $($pending.Count) commit(s)."

# Outputs BEFORE tagging: a tag hiccup must never skip the channel refresh or the verify gate.
Out-StepOutput "pushed=true"
Out-StepOutput "sets=$($sets.ToString().ToLower())"

$anyFailed = $false
foreach ($t in @($plan.tags)) {
  $buildId = "$($t.build)"
  $sha = git log -n 1 --format=%H HEAD -- "artifacts/$buildId/"
  if (-not $sha) {
    Write-Warning "could not locate a commit touching artifacts/$buildId/ to tag"
    $anyFailed = $true; continue
  }
  git rev-parse -q --verify "refs/tags/build/$buildId" *> $null
  if ($LASTEXITCODE -eq 0) {
    Write-Host "tag build/$buildId appeared concurrently. not re-tagging."
    continue
  }
  git tag -a "build/$buildId" -m "$($t.message)" $sha
  if ($LASTEXITCODE -ne 0) {
    Write-Warning "git tag failed for build/$buildId"
    $anyFailed = $true; continue
  }
  git push $Remote "build/$buildId"
  if ($LASTEXITCODE -ne 0) {
    $remoteTag = git ls-remote --tags $Remote "refs/tags/build/$buildId"
    if ($remoteTag) {
      Write-Host "tag build/$buildId already exists on the remote. keeping the remote tag."
      continue
    }
    Write-Host "tag push failed for build/$buildId. retrying once..."
    git push $Remote "build/$buildId"
    if ($LASTEXITCODE -ne 0) {
      Write-Warning "git push (tag build/$buildId) failed after retry"
      $anyFailed = $true; continue
    }
  }
  Write-Host "tagged + pushed build/$buildId"
}
Out-StepOutput "tags_failed=$($anyFailed.ToString().ToLower())"
exit 0
