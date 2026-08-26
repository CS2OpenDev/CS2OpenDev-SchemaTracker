<#
.SYNOPSIS
  Aggregate the scheduled-extract legs' uploads into git commits (one per new build).

.DESCRIPTION
  scheduled-extract.yml's platform legs never touch git: each uploads its promoted artifact set
  (minus the gitignored localization.json) plus its build manifest and PICS capture sidecar as a
  workflow artifact named extract-<platform>. This script runs in the workflow's single commit
  job, against a checkout of main's CURRENT tip, and turns the downloaded uploads into history:

    * every platform set the run produced for a build lands as ONE commit;
    * one leg failing still yields a partial commit of the other leg's set;
    * when no leg produced a set but a PICS capture arrived, the capture is preserved
      as data/pics-captures/<build>.json (PICS is current-only: an uncommitted capture
      is lost forever once the public branch advances).

  Per landed set the host does the judgement against the TIP, not the legs' stale trigger tree:
  merge-omissions folds each leg's content carrier into the build manifest, emit-pics derives the
  build-level pics-appinfo.json from the winning capture (a committed copy wins, then the
  preserved capture, then the preferred leg's sidecar), record-build appends or fact-merges the
  inventory row, reconcile-changelog repairs a from_build the tip outran, and commit-plan
  validates and names what to stage and what to remove.

  Never pushes. Tags are not created here either: push-forward-capture.ps1 tags after the push,
  so a rebase retry cannot leave a tag pointing at a pre-rebase commit. Its inputs are written
  to <IncomingDir>/push-plan.json as { sets: <bool>, tags: [ { build, message } ] }; the plan is
  always written when the incoming dir exists, so the consumer reads it unconditionally.

.NOTES
  Host dll: $CS2_HOST_DLL / $HOST_DLL if set, else built Release once. Operator-run local
  commits keep using commit-dump.ps1.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$IncomingDir,   # download-artifact target: extract-<platform>/ per leg
  [string]$Repo = (Get-Location).Path
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'lib/commit-common.ps1')

# Preference order where a single winner is needed (the leg whose sidecar feeds emit-pics, and
# the provenance that frames captured_utc). Windows first: committed rows and pics framing have
# carried the windows facts first since the pipeline began, so keeping the order keeps the data
# shape stable.
$PlatformPreference = @('windows-x86_64', 'linux-x86_64')

if (-not (Test-Path $IncomingDir)) {
  Write-Host "no leg payload directory at $IncomingDir. nothing to commit."
  exit 0
}
$IncomingDir = (Resolve-Path $IncomingDir).Path
$HostDll = Resolve-HostDll -Repo $Repo

# The aggregation depends on the forward-capture host commands (record-build, emit-pics,
# merge-omissions, reconcile-changelog). A release bundle published before they existed must
# fail HERE with guidance, not partway through a landing.
& dotnet $HostDll record-build --help *> $null
if ($LASTEXITCODE -ne 0) {
  Write-Error ("the resolved host dll lacks the forward-capture commands (record-build and " +
    "friends): the release bundle predates this pipeline. wait for release.yml to publish an " +
    "updated bundle, or point CS2_HOST_DLL at a current build.")
  exit 1
}

$pushPlanPath = Join-Path $IncomingDir 'push-plan.json'
Remove-Item $pushPlanPath -Force -ErrorAction SilentlyContinue

$setsLanded = $false
$pendingTags = @()

Push-Location $Repo
try {
  # Discover the leg uploads. A leg that resolved no new build uploads nothing, and
  # stage-leg-payload.ps1 writes meta.json LAST, so a payload without one is truncated and
  # ignored whole.
  $legs = @()
  foreach ($p in $PlatformPreference) {
    $dir = Join-Path $IncomingDir "extract-$p"
    $metaPath = Join-Path $dir 'meta.json'
    if (-not (Test-Path $metaPath)) { continue }
    $meta = Get-Content $metaPath -Raw | ConvertFrom-Json
    if ("$($meta.platform)" -ne $p) {
      Write-Error "meta.json under extract-$p names platform '$($meta.platform)'"; exit 1
    }
    $legs += [pscustomobject]@{
      Platform = $p
      Build    = "$($meta.buildId)"
      Dir      = $dir
    }
  }
  if ($legs.Count -eq 0) {
    Write-Host "no leg uploads under $IncomingDir. nothing to commit."
    [pscustomobject]@{ sets = $false; tags = @() } |
      ConvertTo-Json -Depth 3 | Set-Content -Path $pushPlanPath
    exit 0
  }

  # Oldest build first (numeric, not lexical), so a two-build run lands each build on top of
  # the previous one's commit and the changelog/predecessor chain stays contiguous.
  foreach ($group in ($legs | Group-Object Build | Sort-Object { [long]$_.Name })) {
    $b = $group.Name

    # Legs whose promoted set actually arrived. Promote is all-or-nothing, so a present
    # platform dir means the extract succeeded; commit-plan re-verifies completeness below.
    # A set already committed on main is dropped: the legs' skip-if-committed check ran
    # against the run's trigger SHA, and this re-check against the true tip is what keeps
    # an operator push of the same set from being overwritten.
    $extracted = @($group.Group | Where-Object {
        Test-Path (Join-Path $_.Dir "artifacts/$b/$($_.Platform)")
      })
    $alreadyLanded = @($extracted | Where-Object {
        git cat-file -e "HEAD:artifacts/$b/$($_.Platform)/entity_schema.json" *> $null
        $LASTEXITCODE -eq 0
      })
    foreach ($leg in $alreadyLanded) {
      Write-Host "build ${b}: $($leg.Platform) set is already committed on main. dropping that leg's upload."
    }
    $extracted = @($extracted | Where-Object { $alreadyLanded -notcontains $_ })

    if ($extracted.Count -eq 0) {
      # No set this run. Preserve the PICS capture if one arrived and is not already
      # committed in either form; capture-pics seeds later runs' sidecars from this file,
      # so a successful extract promotes this ORIGINAL capture.
      $sidecarLeg = $group.Group |
        Where-Object { Test-Path (Join-Path $_.Dir 'pics-appinfo-capture.json') } |
        Select-Object -First 1
      if ($null -eq $sidecarLeg) {
        Write-Host "build ${b}: no set and no PICS capture arrived. nothing to commit."
        continue
      }
      git cat-file -e "HEAD:artifacts/$b/pics-appinfo.json" *> $null
      if ($LASTEXITCODE -eq 0) {
        Write-Host "build ${b}: PICS appinfo already committed under artifacts/. nothing to preserve."
        continue
      }
      $preservedRel = "data/pics-captures/$b.json"
      git cat-file -e "HEAD:$preservedRel" *> $null
      if ($LASTEXITCODE -eq 0) {
        Write-Host "build ${b}: $preservedRel already committed (earliest capture wins). nothing to do."
        continue
      }
      New-Item -ItemType Directory -Force -Path (Split-Path $preservedRel) | Out-Null
      Copy-Item (Join-Path $sidecarLeg.Dir 'pics-appinfo-capture.json') $preservedRel
      git add -- $preservedRel
      if ($LASTEXITCODE -ne 0) { Write-Error "git add failed for $preservedRel"; exit 1 }
      $cap = Get-Content $preservedRel -Raw | ConvertFrom-Json
      $msg = "pics capture $b`n`n" +
        "no platform set could be extracted this run; preserving the current-only PICS appinfo " +
        "(change $($cap.changeNumber), sha1 $($cap.appInfoSha1)) until an artifact set lands."
      git commit -q -m $msg
      if ($LASTEXITCODE -ne 0) { Write-Error "git commit failed for the build $b PICS capture"; exit 1 }
      Write-Host "committed: PICS capture for build $b (no artifact set this run)"
      continue
    }

    # Assemble the platform sets into the checkout. Only the per-platform dirs are copied;
    # build-level files are derived below by the host against the tip.
    foreach ($leg in $extracted) {
      $src = Join-Path $leg.Dir "artifacts/$b/$($leg.Platform)"
      $dst = "artifacts/$b/$($leg.Platform)"
      if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
      New-Item -ItemType Directory -Force -Path "artifacts/$b" | Out-Null
      Copy-Item $src $dst -Recurse
    }

    # Build manifest: each landed leg's content carrier merges in; every other platform's
    # committed carrier survives (taking a leg file whole would drop them).
    foreach ($leg in $extracted) {
      & dotnet $HostDll merge-omissions --build $b --platform $leg.Platform `
        --from (Join-Path $leg.Dir "artifacts/$b/omissions.json") --artifacts artifacts | Out-Host
      if ($LASTEXITCODE -ne 0) { Write-Error "merge-omissions failed for build $b ($($leg.Platform))"; exit 1 }
    }

    # Build-level pics-appinfo.json: a committed copy wins (never churn a landed artifact);
    # else the preserved capture (earliest wins, even against a fresher leg capture from a
    # queued run); else the preferred leg's sidecar. emit-pics refuses a capture that does not
    # describe this build. NON-FATAL like the extract's own pics emit: a pics failure must not
    # forfeit the time-sensitive set (the artifact is optional; sidecars stay downloadable).
    git cat-file -e "HEAD:artifacts/$b/pics-appinfo.json" *> $null
    if ($LASTEXITCODE -ne 0) {
      $captureSrc = $null
      git cat-file -e "HEAD:data/pics-captures/$b.json" *> $null
      if ($LASTEXITCODE -eq 0) {
        $captureSrc = "data/pics-captures/$b.json"
      }
      else {
        # Any leg's sidecar will do (a failed-extract leg still uploads its capture).
        foreach ($leg in $group.Group) {
          $sidecar = Join-Path $leg.Dir 'pics-appinfo-capture.json'
          if (Test-Path $sidecar) { $captureSrc = $sidecar; break }
        }
      }
      if ($captureSrc) {
        & dotnet $HostDll emit-pics --build $b --capture $captureSrc --artifacts artifacts | Out-Host
        if ($LASTEXITCODE -ne 0) {
          Write-Warning "emit-pics returned $LASTEXITCODE for build $b; committing the set without pics-appinfo.json."
        }
      }
    }

    # Inventory: record each landed platform against the TIP's inventory (append the row for a
    # new build; fact-merge a later platform's GID into an existing row). Runs before staging
    # so the plan's inventory check picks the change up.
    foreach ($leg in $extracted) {
      & dotnet $HostDll record-build --build $b --platform $leg.Platform | Out-Host
      if ($LASTEXITCODE -ne 0) { Write-Error "record-build failed for build $b ($($leg.Platform))"; exit 1 }
    }

    # Per platform: repair a changelog the tip outran, then evolution + commit-plan + staging
    # via the shared contract (commit-plan also names the preserved capture for removal).
    $plans = @()
    foreach ($leg in $extracted) {
      & dotnet $HostDll reconcile-changelog --build $b --platform $leg.Platform --artifacts artifacts | Out-Host
      if ($LASTEXITCODE -ne 0) { Write-Error "reconcile-changelog failed for build $b ($($leg.Platform))"; exit 65 }
      $plans += Invoke-ArtifactSetStage -HostDll $HostDll -Repo $Repo -Build $b -Platform $leg.Platform -Artifacts artifacts
    }

    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
      Write-Host "build ${b}: assembled set matches what main already has. nothing to commit."
      continue
    }

    $platformList = @($plans | ForEach-Object { $_.platform }) -join ', '
    if ($plans.Count -eq 1) {
      $message = $plans[0].commitMessage
      $tagMessage = $plans[0].tagMessage
    }
    else {
      # Combined message for a full-build commit: the subject lists the platforms and the
      # body carries one line per platform, from the plans' structured schemaRevision/depots
      # fields. Nothing collapses across platforms on purpose: schemaRevision embeds a
      # per-platform layout hash, and the depot sets differ (per-OS binary depots, the
      # windows-only tools depot).
      $subject = "build $b ($platformList)"
      $body = @($plans | ForEach-Object {
          "$($_.platform): schemaRevision=$($_.schemaRevision) depots=$($_.depots)"
        }) -join "`n"
      $message = "$subject`n`n$body"
      $tagMessage = "$subject schemaRevision " +
      (@($plans | ForEach-Object { "$($_.platform)=$($_.schemaRevision)" }) -join ' ')
    }

    git commit -q -m $message
    if ($LASTEXITCODE -ne 0) { Write-Error "git commit failed for build $b"; exit 1 }
    Write-Host "committed: build $b ($platformList)"
    $setsLanded = $true

    git rev-parse -q --verify "refs/tags/build/$b" *> $null
    if ($LASTEXITCODE -ne 0) {
      $pendingTags += [pscustomobject]@{ build = $b; message = $tagMessage }
      Write-Host "tag build/$b pending (the workflow tags after the push)."
    }
    else {
      Write-Host "tag build/$b already exists. not re-tagging."
    }
  }

  [pscustomobject]@{ sets = $setsLanded; tags = @($pendingTags) } |
    ConvertTo-Json -Depth 3 | Set-Content -Path $pushPlanPath
  Write-Host "done. This script never pushes; review with 'git log'/'git show'."
}
finally {
  Pop-Location
}
exit 0
