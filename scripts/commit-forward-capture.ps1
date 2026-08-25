<#
.SYNOPSIS
  Aggregate the scheduled-extract legs' uploads into git commits (one per new build).

.DESCRIPTION
  scheduled-extract.yml's platform legs never touch git: each uploads its promoted
  artifact set (plus its PICS capture sidecar and, when its extract appended a row, the
  inventory) as a workflow artifact named extract-<platform>. This script runs in the
  workflow's single commit job, against a checkout of main's CURRENT tip, and turns the
  downloaded uploads into history:

    * every platform set the run produced for a build lands as ONE commit;
    * one leg failing still yields a partial commit of the other leg's set;
    * when no leg produced a set but a PICS capture arrived, the capture is preserved
      as data/pics-captures/<build>.json (PICS is current-only: an uncommitted capture
      is lost forever once the public branch advances).

  Platform preference is windows-x86_64 then linux-x86_64, matching the old serialized
  legs' commit order: where the legs disagree on a shared file (the inventory row, the
  build-level pics-appinfo.json) the preferred leg's copy wins, and a copy already
  committed on main always wins over either.

  Never pushes. Tags are not created here either: the workflow tags after the push, so
  a rebase retry cannot leave a tag pointing at a pre-rebase commit. Pending tags are
  written to <IncomingDir>/tag-specs.tsv as "<buildId>TAB<tagMessage>" lines.

.NOTES
  Completeness / message / staging judgement stays in the host (commit-plan, evolution);
  this script only assembles files and runs git. Host dll: $CS2_HOST_DLL / $HOST_DLL if
  set, else built Release once. Operator-run local commits keep using commit-dump.ps1.
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$IncomingDir,   # download-artifact target: extract-<platform>/ per leg
  [string]$Repo = (Get-Location).Path
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Preference order everywhere a single winner is needed (inventory row, build-level
# pics-appinfo.json, the preserved capture). Windows first: the old serialized flow ran
# the windows leg first, so committed inventory rows historically carry the windows
# binaries gid + the tools gid; keeping that order keeps the data shape stable.
$PlatformPreference = @('windows-x86_64', 'linux-x86_64')

function Resolve-HostDll {
  foreach ($env in @($env:CS2_HOST_DLL, $env:HOST_DLL)) {
    if ($env -and (Test-Path $env)) { return $env }
  }
  Write-Host "==> building host (for commit-plan/evolution)..."
  & dotnet build (Join-Path $Repo 'host/src/Cs2SchemaTracker.Host') -c Release -p:SelfContained=false -p:PublishSingleFile=false -p:UseAppHost=false -v q --nologo | Out-Host
  if ($LASTEXITCODE -ne 0) { Write-Error "host build failed (needed for commit-plan)"; exit 1 }
  $dll = Join-Path $Repo 'host/artifacts/bin/Cs2SchemaTracker.Host/release/cs2-schema-tracker.dll'
  if (-not (Test-Path $dll)) { Write-Error "host dll not found after build: $dll"; exit 1 }
  return $dll
}

$IncomingDir = (Resolve-Path $IncomingDir).Path
$HostDll = Resolve-HostDll
$tagSpecPath = Join-Path $IncomingDir 'tag-specs.tsv'
Remove-Item $tagSpecPath -Force -ErrorAction SilentlyContinue

Push-Location $Repo
try {
  # Discover the leg uploads. A leg that resolved no new build uploads nothing; a leg
  # that failed after resolve still uploads meta.json (+ whatever it produced).
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
      Platform         = $p
      Build            = "$($meta.buildId)"
      ExtractOk        = [bool]$meta.extractOk
      InventoryBaseOid = "$($meta.inventoryBaseOid)"
      Dir              = $dir
    }
  }
  if ($legs.Count -eq 0) {
    Write-Host "no leg uploads under $IncomingDir. nothing to commit."
    exit 0
  }

  foreach ($group in ($legs | Group-Object Build | Sort-Object Name)) {
    $b = $group.Name

    # Legs whose promoted set actually arrived. Promote is all-or-nothing, so a present
    # platform dir means the extract succeeded; commit-plan re-verifies completeness below.
    # A set already committed on main is dropped: the legs' skip-if-committed check ran
    # against the run's trigger SHA, and this re-check against the true tip is what keeps
    # an operator push of the same set from being overwritten.
    $extracted = @($group.Group | Where-Object {
        $_.ExtractOk -and (Test-Path (Join-Path $_.Dir "artifacts/$b/$($_.Platform)"))
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
      # committed in either form; the legs seed their sidecar from this file on later
      # runs, so a successful extract promotes this ORIGINAL capture.
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

    # Assemble the platform sets into the checkout.
    foreach ($leg in $extracted) {
      $src = Join-Path $leg.Dir "artifacts/$b/$($leg.Platform)"
      $dst = "artifacts/$b/$($leg.Platform)"
      if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
      New-Item -ItemType Directory -Force -Path "artifacts/$b" | Out-Null
      Copy-Item $src $dst -Recurse
    }

    # Build-level files: a copy already committed on main wins (never churn a landed
    # artifact; for pics-appinfo.json that also keeps the earliest capture, whose body
    # is identical and whose capturedUtc predates any re-emit). Otherwise the preferred
    # leg's copy is taken.
    foreach ($name in @('pics-appinfo.json', 'omissions.json')) {
      $dst = "artifacts/$b/$name"
      foreach ($leg in $extracted) {
        $src = Join-Path $leg.Dir "artifacts/$b/$name"
        if ((Test-Path $src) -and -not (Test-Path $dst)) { Copy-Item $src $dst }
      }
    }

    # Inventory: the preferred leg's appended row wins, mirroring the old serialized flow
    # where the first leg to commit recorded the row and the later leg's append no-opped
    # (ForwardCaptureRecorder never mutates an existing row). The base-oid check refuses
    # to clobber an inventory that advanced on main after the legs checked out. That
    # guard also fails loud, on purpose, in the rare run where the legs resolved
    # DIFFERENT builds: the second build's leg copy is based on a pre-first-commit
    # inventory, and taking it would drop the first build's row. The next cron picks the
    # newer build up cleanly.
    foreach ($leg in $extracted) {
      $src = Join-Path $leg.Dir 'data/cs2-assets-inventory.json'
      if (-not (Test-Path $src)) { continue }
      $headOid = git rev-parse "HEAD:data/cs2-assets-inventory.json"
      if ($LASTEXITCODE -ne 0) { Write-Error "git rev-parse failed for the committed inventory"; exit 1 }
      if ($leg.InventoryBaseOid -and $headOid -ne $leg.InventoryBaseOid) {
        Write-Error ("the committed inventory advanced after the legs checked out " +
          "(leg base $($leg.InventoryBaseOid), main now $headOid). refusing to overwrite; re-run the extract.")
        exit 1
      }
      Copy-Item $src 'data/cs2-assets-inventory.json' -Force
      break
    }

    # Per platform: refresh the cumulative evolution artifact (non-fatal, same contract
    # as commit-dump.ps1), then stage exactly what the host's plan names.
    $plans = @()
    foreach ($leg in $extracted) {
      & dotnet $HostDll evolution --platform $leg.Platform --artifacts artifacts
      if ($LASTEXITCODE -ne 0) {
        Write-Warning "evolution refresh returned $LASTEXITCODE for build $b ($($leg.Platform)); committing the set without an evolution update. re-run 'evolution' later."
      }

      $planJson = & dotnet $HostDll commit-plan --build $b --platform $leg.Platform --artifacts artifacts
      if ($LASTEXITCODE -ne 0) { Write-Error "commit-plan refused build $b ($($leg.Platform)); see VIOLATION lines above"; exit 65 }
      $plan = $planJson | ConvertFrom-Json
      $plans += $plan

      foreach ($sp in $plan.stagePaths) {
        git add -- $sp
        if ($LASTEXITCODE -ne 0) { Write-Error "git add failed for '$sp' (build $b)"; exit 1 }
      }
      if (git status --porcelain -- $plan.inventoryPath) {
        git add -- $plan.inventoryPath
        if ($LASTEXITCODE -ne 0) { Write-Error "git add inventory failed for build $b"; exit 1 }
      }
    }

    git diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
      Write-Host "build ${b}: assembled set matches what main already has. nothing to commit."
      continue
    }

    # A preserved capture is redundant once the build-level pics-appinfo.json exists in
    # artifacts/ (the legs' seed step made the extract promote that same document).
    # Drop it in the same commit.
    $preservedRel = "data/pics-captures/$b.json"
    git ls-files --error-unmatch -- $preservedRel *> $null
    if ($LASTEXITCODE -eq 0 -and (Test-Path "artifacts/$b/pics-appinfo.json")) {
      git rm -q -- $preservedRel
      if ($LASTEXITCODE -ne 0) { Write-Error "git rm failed for $preservedRel"; exit 1 }
    }

    $platformList = @($plans | ForEach-Object { $_.platform }) -join ', '
    if ($plans.Count -eq 1) {
      $message = $plans[0].commitMessage
      $tagMessage = $plans[0].tagMessage
    }
    else {
      # Combined message for a full-build commit: the subject lists the platforms and the
      # body carries one line per platform. Nothing collapses across platforms on purpose:
      # schemaRevision embeds a per-platform layout hash, and the depot sets differ (per-OS
      # binary depots, the windows-only tools depot). The tag message stays single-line
      # because tag-specs.tsv is line-based.
      $parsed = @(foreach ($plan in $plans) {
          if ($plan.commitMessage -notmatch 'schemaRevision=(\S*) depots=(\S*)') {
            Write-Error "could not parse schemaRevision/depots from the $($plan.platform) commit plan"; exit 1
          }
          [pscustomobject]@{ Platform = $plan.platform; Rev = $Matches[1]; Depots = $Matches[2] }
        })
      $subject = "build $b ($platformList)"
      $body = @($parsed | ForEach-Object { "$($_.Platform): schemaRevision=$($_.Rev) depots=$($_.Depots)" }) -join "`n"
      $message = "$subject`n`n$body"
      $tagMessage = "$subject schemaRevision " +
      (@($parsed | ForEach-Object { "$($_.Platform)=$($_.Rev)" }) -join ' ')
    }

    git commit -q -m $message
    if ($LASTEXITCODE -ne 0) { Write-Error "git commit failed for build $b"; exit 1 }
    Write-Host "committed: build $b ($platformList)"

    git rev-parse -q --verify "refs/tags/build/$b" *> $null
    if ($LASTEXITCODE -ne 0) {
      "$b`t$tagMessage" | Add-Content -Path $tagSpecPath
      Write-Host "tag build/$b pending (the workflow tags after the push)."
    }
    else {
      Write-Host "tag build/$b already exists. not re-tagging."
    }
  }

  Write-Host "done. This script never pushes; review with 'git log'/'git show'."
}
finally {
  Pop-Location
}
