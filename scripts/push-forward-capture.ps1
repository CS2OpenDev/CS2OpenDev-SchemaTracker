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

  With -Gate (what CI passes), the push to the protected branch goes through a GATE instead of
  straight in, because main requires a passing `ci gate` status and a GITHUB_TOKEN push can never
  carry one: GitHub suppresses push-event workflows for such pushes, so ci.yml never runs on the
  very commits this job lands. The sequence is

    1. push HEAD to an UNGATED staging branch (default capture/pending) -- always allowed,
    2. dispatch ci.yml on that ref (workflow_dispatch is the documented exception to the
       recursive-trigger guard; the same exception "Refresh latest-artifacts channel" relies on),
    3. wait for a `ci gate` check run to conclude on that SHA -- check runs attach to the COMMIT,
       so the status travels with the SHA when it is pushed on to main,
    4. push the SAME SHA to main, which now satisfies the rule.

  This satisfies the branch rule rather than bypassing it: a capture that breaks the repo is
  stopped at step 3 instead of landing and being noticed afterwards. It is also SAFER than the
  direct push it replaces, which is the non-obvious part -- step 1 gets the bytes onto the remote
  before anything can reject them, so a refusal or a CI failure leaves the capture preserved on
  the staging branch instead of dying with the runner. That matters because PICS serves only the
  current build and AcquireCommand now (correctly) refuses to re-acquire a historical one, so a
  capture lost at this stage is lost permanently.

  Without -Gate (an operator running this by hand) the behaviour is unchanged: a direct push,
  which an org-admin credential is allowed to make.

  Outputs appended to $GITHUB_OUTPUT when set: pushed, sets, tags_failed, gated.
  Exit: nonzero only when the push itself fails (a genuine rebase conflict fails loud; the
  payloads stay downloadable for 7 days and the next cron re-extracts while the build is
  current), or when -Gate is set and the gate does not pass.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$PlanPath,
  [string]$Remote = 'origin',
  [string]$Branch = 'main',
  # Gate the push through a CI run on a staging ref (see .DESCRIPTION). CI passes this; an
  # operator pushing by hand does not, and gets the previous direct-push behaviour.
  [switch]$Gate,
  [string]$StagingBranch = 'capture/pending',
  [string]$GateWorkflow = 'ci.yml',
  [string]$GateCheckName = 'ci gate',
  [int]$GateTimeoutSeconds = 2400,
  [int]$GatePollSeconds = 20
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

# The `ci gate` conclusion recorded against a commit, or $null when there is no completed run
# for it yet. Check runs hang off the SHA, not the ref, which is what lets a status earned on the
# staging branch satisfy the rule on main.
function Get-GateConclusion([string]$sha) {
  $repo = $env:GITHUB_REPOSITORY
  if (-not $repo) { Write-Error "GITHUB_REPOSITORY is not set; cannot query check runs"; exit 1 }
  $json = gh api "repos/$repo/commits/$sha/check-runs" --paginate 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $json) { return $null }
  $runs = @(($json | ConvertFrom-Json).check_runs |
            Where-Object { $_.name -eq $GateCheckName -and $_.status -eq 'completed' })
  if ($runs.Count -eq 0) { return $null }
  # A success anywhere on this SHA is decisive: the gate passed on exactly these bytes, whatever
  # else was cancelled or superseded around it. Otherwise report the newest completed verdict.
  if (@($runs | Where-Object { $_.conclusion -eq 'success' }).Count -gt 0) { return 'success' }
  return (@($runs | Sort-Object -Property completed_at -Descending)[0]).conclusion
}

# Park $sha on the ungated staging branch, run the gate against it, and return $true when the
# gate passed. Never deletes the staging branch on failure: that ref IS the preserved capture.
function Invoke-PushGate([string]$sha) {
  Write-Host "gate: parking $sha on $StagingBranch (ungated) before touching $Branch..."
  git push -f $Remote "${sha}:refs/heads/$StagingBranch"
  if ($LASTEXITCODE -ne 0) {
    Write-Error "gate: could not push $sha to $StagingBranch; refusing to continue"
    exit 1
  }

  $already = Get-GateConclusion $sha
  if ($already -eq 'success') {
    Write-Host "gate: $sha already carries a passing '$GateCheckName'. not re-running."
    return $true
  }

  Write-Host "gate: dispatching $GateWorkflow on $StagingBranch..."
  gh workflow run $GateWorkflow --ref $StagingBranch
  if ($LASTEXITCODE -ne 0) {
    Write-Error "gate: could not dispatch $GateWorkflow (actions: write missing on this job?)"
    exit 1
  }

  $deadline = (Get-Date).AddSeconds($GateTimeoutSeconds)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $GatePollSeconds
    $conclusion = Get-GateConclusion $sha
    if (-not $conclusion) { continue }
    if ($conclusion -eq 'success') {
      Write-Host "gate: '$GateCheckName' passed for $sha."
      return $true
    }
    if ($conclusion -in @('cancelled', 'skipped', 'stale', 'neutral')) {
      # Not a verdict on the bytes. ci.yml cancels in-progress runs for a ref, so a superseded
      # run lands here; keep waiting for the one that actually ran to completion.
      Write-Host "gate: ignoring a '$conclusion' '$GateCheckName' for $sha (superseded, not a verdict); still waiting."
      continue
    }
    Write-Host "::error title=forward-capture gate failed::'$GateCheckName' concluded '$conclusion' for $sha. The capture is NOT lost - it is on $StagingBranch. Fix the cause, then re-dispatch $GateWorkflow on that ref and push it to $Branch."
    return $false
  }

  Write-Host "::error title=forward-capture gate timed out::no completed '$GateCheckName' for $sha after $GateTimeoutSeconds s. The capture is on $StagingBranch and nothing is lost; check $GateWorkflow and push that ref to $Branch once it is green."
  return $false
}

# One push attempt at the CURRENT HEAD, gated when asked. Returns the git exit code of the push
# to $Branch so the caller can tell a rejection (rebase + retry) from success.
function Push-Head {
  if ($Gate) {
    $sha = (git rev-parse HEAD).Trim()
    if (-not (Invoke-PushGate $sha)) {
      Out-StepOutput "pushed=false"
      Out-StepOutput "sets=false"
      Out-StepOutput "tags_failed=false"
      Out-StepOutput "gated=blocked"
      exit 1
    }
  }
  git push $Remote "HEAD:$Branch"
  return $LASTEXITCODE
}

if ((Push-Head) -ne 0) {
  Write-Host "push rejected (non-fast-forward?). rebasing onto current $Remote/$Branch and retrying once..."
  git pull --rebase $Remote $Branch
  if ($LASTEXITCODE -ne 0) { Write-Error "git pull --rebase failed"; exit $LASTEXITCODE }
  # The rebase rewrote HEAD, so any status earned before it belongs to a SHA that no longer
  # exists -- the retry has to re-gate the NEW commit, not reuse the old verdict.
  if ((Push-Head) -ne 0) { Write-Error "git push ($Branch) failed after rebase retry"; exit $LASTEXITCODE }
}
Write-Host "pushed $($pending.Count) commit(s)."

if ($Gate) {
  # Landed on $Branch, so the parked copy has done its job.
  git push $Remote --delete $StagingBranch *> $null
  if ($LASTEXITCODE -eq 0) { Write-Host "gate: cleaned up $StagingBranch." }
  else { Write-Host "gate: could not delete $StagingBranch (harmless; it is a scratch ref)." }
}
Out-StepOutput "gated=$(if ($Gate) { 'passed' } else { 'skipped' })"

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
