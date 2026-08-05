<#
.SYNOPSIS
    Bootstrap toolchains for CS2-Schema-Tracker on Windows.

.DESCRIPTION
    Two stages, both idempotent -- safe to re-run.

    Stage A (Windows-side, winget):
      - .NET 8 SDK
      - CMake
      - MSVC Build Tools 2022 (C++ workload, Windows 11 SDK)
      - GitHub CLI (gh)
      - protoc (Google Protocol Buffers compiler)
      - Python 3.12

    Stage B (WSL2 + Ubuntu, for Linux tuple extraction):
      - WSL2 + Ubuntu-22.04
      - dotnet-sdk-8.0, build-essential, cmake, protobuf-compiler inside WSL

    Stage Protobuf (Windows-side, vcpkg -- NARROW, additive, independent):
      - Bootstraps vcpkg into C:\tools\vcpkg if absent (clone + bootstrap).
      - vcpkg install protobuf:x64-windows  (libprotobuf C++ runtime + headers +
        CMake config AND a matched protoc -- a matched pair, no version skew).
      This exists because the winget Stage-A protoc package on this box ships the
      protoc compiler ONLY: no libprotobuf C++ runtime/headers/CMake config, so
      walker/CMakeLists.txt's find_package(Protobuf) cannot resolve. The walker
      then configures with:
        -DCMAKE_TOOLCHAIN_FILE=C:\tools\vcpkg\scripts\buildsystems\vcpkg.cmake
        -DVCPKG_TARGET_TRIPLET=x64-windows
      Stage Protobuf does ONLY protobuf -- it does NOT run Stage A (MSVC ~6 GB)
      or Stage B (WSL).: vcpkg's protobuf is a generic serialization
      runtime, not a CS2-domain input; HL2SDK stays the only CS2-domain dep.

    After Stage A + Stage B + Stage Protobuf, you can natively build and run all
    four (linux|windows)-x86_64.(server|client) tuples from this single Windows
    box (per) AND build the C++ walker.

    Run as the user (not Administrator). winget and wsl --install prompt for
    elevation themselves when needed. vcpkg bootstrap + install run unelevated.

.PARAMETER Stage
    A        -- Windows-side installs only
    B        -- WSL2 + Ubuntu only (requires Stage A's reboot already done)
    Protobuf -- vcpkg libprotobuf C++ (x64-windows) only; for the walker build.
                Independent of A/B; safe to run alone. Idempotent.
    All      -- Stage A then Stage B (default; does NOT run Stage Protobuf)

.PARAMETER SkipMsvc
    Skip the MSVC Build Tools install in Stage A. Useful if you've already
    installed Visual Studio 2022 separately, or just want to dry-run Stage A
    without the 6 GB download.

.EXAMPLE
    .\scripts\bootstrap-windows.ps1 -Stage A
    .\scripts\bootstrap-windows.ps1 -Stage B
    .\scripts\bootstrap-windows.ps1 -Stage Protobuf  # walker libprotobuf C++ only
    .\scripts\bootstrap-windows.ps1                  # default: All

.NOTES
    ASCII-only by design: Windows PowerShell 5.1 reads .ps1 files as
    Windows-1252 unless they have a UTF-8 BOM, so non-ASCII characters
    (em-dashes, smart quotes) corrupt the parser. Keep this file ASCII.
#>

[CmdletBinding()]
param(
    [ValidateSet('A', 'B', 'Protobuf', 'All')]
    [string]$Stage = 'All',

    [switch]$SkipMsvc
)

# Deliberately NOT using `$ErrorActionPreference = 'Stop'` -- native commands
# (winget, git, wsl) emit stderr in normal operation; Stop would turn benign
# stderr into terminating errors. We check $LASTEXITCODE explicitly.
$ErrorActionPreference = 'Continue'

# ---- helpers ---------------------------------------------------------------------------

function Write-Section($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-OK($msg)      { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Skip($msg)    { Write-Host "  [skip] $msg" -ForegroundColor Yellow }
function Write-Doing($msg)   { Write-Host "  [...]  $msg" -ForegroundColor Gray }
function Fail($msg)          { Write-Host "  [FAIL] $msg" -ForegroundColor Red; throw $msg }

function Test-Command($name) {
    $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

# Avoid shadowing the automatic $args variable -- name the parameter $cmdArgs.
function Get-Version($cmd, $cmdArgs) {
    try { (& $cmd @cmdArgs | Select-Object -First 1) } catch { $null }
}

function Install-Winget($id, $extraArgs = @()) {
    Write-Doing "winget install $id"
    $argList = @(
        'install', '--id', $id,
        '--accept-package-agreements', '--accept-source-agreements',
        '--silent'
    ) + $extraArgs
    & winget @argList
    $code = $LASTEXITCODE
    # 0                = success
    # -1978335189      = APPINSTALLER_CLI_ERROR_PACKAGE_ALREADY_INSTALLED
    # -1978335212      = APPINSTALLER_CLI_ERROR_NO_APPLICABLE_INSTALLER (also "already installed" in some cases)
    if ($code -ne 0 -and $code -ne -1978335189 -and $code -ne -1978335212) {
        Fail "winget install $id exited with $code"
    }
    Write-OK "$id present"
}

# Read git config without the surrounding stderr drama. Returns $null if unset.
function Get-GitConfig($key) {
    $value = & git config --global --get $key 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($value)) { return $null }
    return $value.Trim()
}

# Convert a Windows path (C:\Users\<you>\...) to a WSL path (/mnt/c/Users/<you>/...).
# Avoids the PS 7+ scriptblock-in-replace syntax that breaks on PS 5.1.
function ConvertTo-WslPath($winPath) {
    if ($winPath -notmatch '^([A-Za-z]):[\\/](.*)$') { return $winPath }
    $drive = $matches[1].ToLower()
    $rest  = $matches[2] -replace '\\', '/'
    return "/mnt/$drive/$rest"
}

# Write a file as UTF-8 *without* BOM. Set-Content -Encoding utf8 in PS 5.1 adds
# a BOM, which bash rejects when it appears before #!/usr/bin/env.
function Write-FileUtf8NoBom($path, $content) {
    $enc = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($path, $content, $enc)
}

# Parse a KEY=VALUE .env file into a hashtable. Strips surrounding quotes and
# blank/comment lines. Values that interpolate variables ($foo, ${foo}) are NOT
# expanded -- we treat the file as static. Returns empty hashtable if file absent.
function Get-EnvFile($path) {
    if (-not (Test-Path $path)) { return @{} }
    $h = @{}
    foreach ($line in Get-Content -Path $path -Encoding UTF8) {
        if ($line -match '^\s*#') { continue }
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$') {
            $val = $Matches[2]
            if (($val.StartsWith('"') -and $val.EndsWith('"')) -or
                ($val.StartsWith("'") -and $val.EndsWith("'"))) {
                $val = $val.Substring(1, $val.Length - 2)
            }
            if ($val -ne '') { $h[$Matches[1]] = $val }
        }
    }
    return $h
}

# ---- Stage A: Windows-side -------------------------------------------------------------

function Invoke-StageA {
    Write-Section "Stage A -- Windows-side toolchain (winget)"

    if (-not (Test-Command 'winget')) {
        Fail "winget not found. Install 'App Installer' from the Microsoft Store, then re-run."
    }

    # .NET 8 SDK
    if (Test-Command 'dotnet') {
        $sdks = & dotnet --list-sdks
        if (($sdks | Out-String) -match '(?m)^8\.') {
            Write-Skip ".NET 8 SDK already installed"
        } else {
            Install-Winget 'Microsoft.DotNet.SDK.8'
        }
    } else {
        Install-Winget 'Microsoft.DotNet.SDK.8'
    }

    # CMake
    if (Test-Command 'cmake') {
        Write-Skip "CMake already installed: $(Get-Version cmake @('--version'))"
    } else {
        Install-Winget 'Kitware.CMake'
    }

    # MSVC Build Tools 2022 (large ~6 GB)
    if ($SkipMsvc) {
        Write-Skip "MSVC Build Tools install skipped (-SkipMsvc)"
    } else {
        $msvcInstalled = (Test-Path 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools') -or
                         (Test-Path 'C:\Program Files\Microsoft Visual Studio\2022\BuildTools') -or
                         (Test-Path 'C:\Program Files\Microsoft Visual Studio\2022\Community') -or
                         (Test-Path 'C:\Program Files\Microsoft Visual Studio\2022\Professional')
        if ($msvcInstalled) {
            Write-Skip "Visual Studio 2022 (or Build Tools) already installed"
        } else {
            Write-Doing "winget install Microsoft.VisualStudio.2022.BuildTools (this is large, 15-30 min)"
            $override = '--add Microsoft.VisualStudio.Workload.VCTools ' +
                        '--add Microsoft.VisualStudio.Component.Windows11SDK.22621 ' +
                        '--includeRecommended --quiet --wait --norestart'
            & winget install --id Microsoft.VisualStudio.2022.BuildTools `
                --accept-package-agreements --accept-source-agreements `
                --silent --override $override
            $code = $LASTEXITCODE
            if ($code -ne 0 -and $code -ne -1978335189 -and $code -ne -1978335212) {
                Fail "MSVC Build Tools install exited with $code"
            }
            Write-OK "MSVC Build Tools present"
        }
    }

    # GitHub CLI
    if (Test-Command 'gh') {
        Write-Skip "gh already installed: $(Get-Version gh @('--version'))"
    } else {
        Install-Winget 'GitHub.cli'
    }

    # protoc -- winget package name varies; try a couple, fall back to direct download
    if (Test-Command 'protoc') {
        Write-Skip "protoc already installed: $(Get-Version protoc @('--version'))"
    } else {
        $protocInstalled = $false
        foreach ($id in @('Google.Protobuf', 'protocolbuffers.Protobuf')) {
            Write-Doing "Trying winget id '$id' for protoc"
            & winget install --id $id --accept-package-agreements --accept-source-agreements --silent
            $c = $LASTEXITCODE
            if ($c -eq 0 -or $c -eq -1978335189 -or $c -eq -1978335212) {
                $protocInstalled = $true
                break
            }
        }
        if (-not $protocInstalled) {
            Write-Doing "winget didn't have protoc -- downloading from GitHub releases"
            $protocVersion = '28.3'
            $url = "https://github.com/protocolbuffers/protobuf/releases/download/v$protocVersion/protoc-$protocVersion-win64.zip"
            $tmp = Join-Path $env:TEMP "protoc-$protocVersion-win64.zip"
            $dest = 'C:\tools\protoc'
            Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
            New-Item -ItemType Directory -Force -Path $dest | Out-Null
            Expand-Archive -Path $tmp -DestinationPath $dest -Force
            Remove-Item $tmp -Force
            # Add C:\tools\protoc\bin to machine PATH if not already there
            $machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
            if ($machinePath -notlike '*C:\tools\protoc\bin*') {
                [Environment]::SetEnvironmentVariable('Path', "$machinePath;C:\tools\protoc\bin", 'Machine')
                Write-OK "Added C:\tools\protoc\bin to system PATH (open a new shell to pick it up)"
            }
            Write-OK "protoc installed to $dest"
        }
    }

    # Python 3.12
    # NB: Test-Command 'python' returns true for the Windows Store stub python.exe
    # that ships with Windows 11. The stub writes "Python was not found..." to stderr
    # on any invocation. Actually run --version and validate the output to tell the
    # difference between a real Python and the stub.
    $pyVer = ''
    if (Test-Command 'python') {
        $pyVer = (& python --version 2>$null | Out-String).Trim()
    }
    if ($pyVer -match '^Python \d') {
        Write-Skip "Python already installed: $pyVer"
    } else {
        Install-Winget 'Python.Python.3.12'
    }

    # Git identity
    Write-Section 'Git identity'
    $gitName  = Get-GitConfig 'user.name'
    $gitEmail = Get-GitConfig 'user.email'
    if (-not $gitName -or -not $gitEmail) {
        Write-Host '  Git identity is not fully set.'
        if (-not $gitName)  { $n = Read-Host '  Enter your name for git commits';  & git config --global user.name $n }
        if (-not $gitEmail) { $e = Read-Host '  Enter your email for git commits'; & git config --global user.email $e }
        Write-OK 'Git identity configured'
    } else {
        Write-Skip "Git identity already set: $gitName <$gitEmail>"
    }

    Write-Section 'Stage A complete'
    Write-Host '  Open a NEW terminal so PATH changes take effect, then verify:'
    Write-Host '    dotnet --list-sdks'
    Write-Host '    cmake --version'
    Write-Host '    gh --version'
    Write-Host '    protoc --version'
    Write-Host '  Stage B (WSL2 + Linux) is optional but recommended for native Linux-tuple dev.'
}

# ---- Stage B: WSL2 + Ubuntu ------------------------------------------------------------

# Install / configure Ubuntu non-interactively, given a UNIX user + password sourced
# from .env. Skips the OOBE wizard via --no-launch, creates the user via root-mode bash,
# sets passwordless sudo, makes them the default user. Password crosses only the WSL
# stdin pipe -- never on disk, never in any command line.
function Install-UbuntuUnattended($user, $password) {
    Write-Doing "Installing Ubuntu-22.04 with --no-launch (UNIX user '$user' from .env)"

    # Distro list check (UTF-16 LE handling)
    $prevOutEnc = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::Unicode
        $listOut = & wsl --list --quiet 2>$null
        $hasUbuntu = (($listOut | Out-String) -match 'Ubuntu-22\.04')
    } finally {
        [Console]::OutputEncoding = $prevOutEnc
    }

    if (-not $hasUbuntu) {
        & wsl --install -d Ubuntu-22.04 --no-launch
        if ($LASTEXITCODE -ne 0) {
            Fail "wsl --install -d Ubuntu-22.04 --no-launch exited with $LASTEXITCODE. WSL feature may need to be enabled first (admin): wsl --install"
        }
        Write-OK 'Ubuntu-22.04 registered (no OOBE)'
    } else {
        Write-Skip 'Ubuntu-22.04 already registered'
    }

    # Initialize the distro by invoking it once as root. The first call extracts the rootfs.
    Write-Doing 'Initializing distro rootfs'
    & wsl -d Ubuntu-22.04 --user root -- /bin/true
    if ($LASTEXITCODE -ne 0) {
        Fail "Distro init failed: $LASTEXITCODE. Run 'wsl --status' to check WSL health."
    }

    # Build the user-setup script in PowerShell memory. Single-quote-escape the password
    # using bash's '\''  pattern so apostrophes don't break out of the single-quoted literal.
    $escapedPw = $password -replace "'", "'\''"
    $userSetup = @"
set -euo pipefail
USERNAME='$user'
PASSWORD='$escapedPw'
if ! id -u "`$USERNAME" >/dev/null 2>&1; then
  useradd -m -s /bin/bash "`$USERNAME"
  echo "`$USERNAME:`$PASSWORD" | chpasswd
  usermod -aG sudo "`$USERNAME"
fi
echo "`$USERNAME ALL=(ALL) NOPASSWD: ALL" > /etc/sudoers.d/`$USERNAME
chmod 0440 /etc/sudoers.d/`$USERNAME
echo "[stage-b] user '`$USERNAME' ready, passwordless sudo enabled"
"@
    $userSetup = $userSetup -replace "`r`n", "`n"

    Write-Doing "Creating UNIX user '$user' and enabling passwordless sudo (via root)"
    # Force stdin encoding to UTF-8 so multi-byte characters in the password survive the pipe.
    $prevInputEnc = [Console]::InputEncoding
    $prevOutputEnc = $OutputEncoding
    try {
        [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
        $OutputEncoding = New-Object System.Text.UTF8Encoding $false
        $userSetup | & wsl -d Ubuntu-22.04 --user root -- bash
        $code = $LASTEXITCODE
    } finally {
        [Console]::InputEncoding = $prevInputEnc
        $OutputEncoding = $prevOutputEnc
    }
    # Best-effort wipe of the in-memory copy. PowerShell strings are immutable so the
    # original literal still lives in the runtime's string pool, but this at least
    # clears the named variables.
    $userSetup = $null
    $escapedPw = $null
    if ($code -ne 0) { Fail "User-setup script failed with $code" }
    Write-OK "UNIX user '$user' configured"

    # Set default user. Newer wsl uses 'wsl --manage'; older uses the per-distro launcher.
    Write-Doing "Setting default WSL user to '$user'"
    & wsl --manage Ubuntu-22.04 --set-default-user $user 2>$null
    if ($LASTEXITCODE -ne 0) {
        # Fallback for older WSL: the per-distro .exe launcher (may not exist on all installs)
        $distroExe = Get-Command -Name 'ubuntu2204.exe' -ErrorAction SilentlyContinue
        if ($distroExe) { & $distroExe.Source config --default-user $user }
        if ($LASTEXITCODE -ne 0) {
            Write-Skip "Could not set default user via either method; you can set it manually with: wsl --manage Ubuntu-22.04 --set-default-user $user"
        }
    }
}

function Invoke-StageB {
    Write-Section 'Stage B -- WSL2 + Ubuntu (Linux tuple dev path)'

    # Read .env from repo root (script lives in scripts/, repo root is one up)
    $envPath = Join-Path $PSScriptRoot '..\.env'
    $envFile = Get-EnvFile $envPath
    $hasWslCreds = $envFile.ContainsKey('WSL_USERNAME') -and $envFile.ContainsKey('WSL_PASSWORD')

    # WSL outputs distro names as UTF-16 LE. PS 5.1 default OutputEncoding mangles them.
    # Set OutputEncoding to Unicode just for WSL queries, restore after.
    $prevOutEnc = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::Unicode

        if (-not (Test-Command 'wsl')) {
            Write-Host '  WSL not present. Running ''wsl --install -d Ubuntu-22.04'' -- this REQUIRES A REBOOT.'
            Write-Host '  Press Enter to continue, or Ctrl+C to cancel.'
            $null = Read-Host
            & wsl --install -d Ubuntu-22.04
            Write-Host "`n  WSL install initiated. REBOOT now, then re-run '.\scripts\bootstrap-windows.ps1 -Stage B' to finish." -ForegroundColor Yellow
            return
        }
    } finally {
        [Console]::OutputEncoding = $prevOutEnc
    }

    if ($hasWslCreds) {
        Write-Skip "Loaded WSL_USERNAME + WSL_PASSWORD from $envPath (values stay in script memory only)"
        Install-UbuntuUnattended -user $envFile['WSL_USERNAME'] -password $envFile['WSL_PASSWORD']
    } else {
        # Interactive fallback: existing behavior
        $prevOutEnc = [Console]::OutputEncoding
        try {
            [Console]::OutputEncoding = [System.Text.Encoding]::Unicode
            $listOut = & wsl --list --quiet 2>$null
            $hasUbuntu = (($listOut | Out-String) -match 'Ubuntu-22\.04')
        } finally {
            [Console]::OutputEncoding = $prevOutEnc
        }
        if (-not $hasUbuntu) {
            Write-Doing 'Installing Ubuntu-22.04 inside WSL (interactive first-time setup)'
            Write-Host "  Hint: set WSL_USERNAME and WSL_PASSWORD in .env to skip the OOBE wizard next time."
            & wsl --install -d Ubuntu-22.04
            Write-Host '  Finish first-time Ubuntu setup in the window that opened (set username + password), then re-run this script.' -ForegroundColor Yellow
            return
        }
        Write-OK 'Ubuntu-22.04 present in WSL'
    }

    Write-Section 'Provisioning Ubuntu toolchain (apt + dotnet)'

    $bootstrapInside = @'
#!/usr/bin/env bash
set -euo pipefail
echo "  [Ubuntu] apt update"
sudo apt-get update -qq
echo "  [Ubuntu] apt install build-essential cmake git protobuf-compiler curl"
sudo apt-get install -y -qq build-essential cmake git protobuf-compiler curl ca-certificates wget

if ! command -v dotnet >/dev/null 2>&1; then
  echo "  [Ubuntu] Installing dotnet-sdk-8.0 via Microsoft repo"
  wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/ms.deb
  sudo dpkg -i /tmp/ms.deb
  rm /tmp/ms.deb
  sudo apt-get update -qq
  sudo apt-get install -y -qq dotnet-sdk-8.0
else
  echo "  [Ubuntu] dotnet already installed: $(dotnet --version)"
fi

echo "  [Ubuntu] Versions:"
echo "    gcc:    $(gcc --version | head -1)"
echo "    cmake:  $(cmake --version | head -1)"
echo "    protoc: $(protoc --version)"
echo "    dotnet: $(dotnet --version)"
echo "  [Ubuntu] Repo reachable under your Windows user profile, e.g. /mnt/c/Users/<you>/dev/CS2-Schema-Tracker"
'@

    # WSL Linux tools need LF line endings -- here-string preserves them.
    # Write WITHOUT a BOM so bash doesn't choke on the shebang.
    $bootstrapInside = $bootstrapInside -replace "`r`n", "`n"
    $tmpScript = Join-Path $env:TEMP 'cs2-bootstrap-wsl.sh'
    Write-FileUtf8NoBom $tmpScript $bootstrapInside
    $wslPath = ConvertTo-WslPath $tmpScript
    & wsl -d Ubuntu-22.04 -- bash $wslPath
    $code = $LASTEXITCODE
    Remove-Item $tmpScript -Force -ErrorAction SilentlyContinue
    if ($code -ne 0) { Fail "WSL provisioning script exited with $code" }

    Write-Section 'Stage B complete'
    $wslRepo = ConvertTo-WslPath ((Resolve-Path (Join-Path $PSScriptRoot '..')).Path)
    Write-Host "  From WSL, your repo is at $wslRepo"
    Write-Host '  Suggested next:'
    Write-Host '    wsl -d Ubuntu-22.04'
    Write-Host "    cd $wslRepo"
    Write-Host '    dotnet build host/Cs2SchemaTracker.sln'
}

# ---- Stage Protobuf: vcpkg libprotobuf C++ (x64-windows) -------------------------------

# Install libprotobuf C++ (runtime + headers + CMake config + a matched protoc) via
# vcpkg into a stable, predictable location so walker/CMakeLists.txt's
# find_package(Protobuf) resolves. NARROW and INDEPENDENT: touches nothing that
# Stage A or Stage B touch, never triggers them. Idempotent (skips a present install).
#
# Why vcpkg and not winget: this box's winget protobuf package ships protoc ONLY --
# no libprotobuf C++ runtime/headers/CMake config -- which is exactly what blocks
# find_package(Protobuf). vcpkg builds a matched protoc + libprotobuf pair from
# source under one triplet, so generated *.pb.h and the linked runtime can never
# version-skew (the failure mode of mixing winget protoc with a separately-sourced
# libprotobuf).: protobuf is a generic serialization runtime, not a CS2-domain
# input; HL2SDK remains the only CS2-domain build input.
function Invoke-StageProtobuf {
    Write-Section "Stage Protobuf -- vcpkg libprotobuf C++ (x64-windows)"

    if (-not (Test-Command 'git')) {
        Fail "git not found. Install git (Stage A installs the rest of the toolchain), then re-run."
    }

    $vcpkgRoot = 'C:\tools\vcpkg'
    $vcpkgExe  = Join-Path $vcpkgRoot 'vcpkg.exe'

    # --- bootstrap vcpkg if absent --------------------------------------------------
    if (Test-Path $vcpkgExe) {
        Write-Skip "vcpkg already bootstrapped at $vcpkgRoot"
    } else {
        # A $vcpkgRoot that exists WITHOUT vcpkg.exe is a partial/interrupted clone --
        # e.g. a killed `git clone` that left a missing scripts/ dir and a stale
        # .git\index.lock. Bootstrapping that broken tree fails every time. Remove the
        # partial clone and re-clone fresh. Scoped strictly to the script-managed root.
        if (Test-Path $vcpkgRoot) {
            Write-Doing "Found partial/broken vcpkg at $vcpkgRoot (no vcpkg.exe) -- removing and re-cloning fresh"
            Remove-Item -Recurse -Force $vcpkgRoot
        }
        Write-Doing "Cloning vcpkg into $vcpkgRoot"
        $toolsParent = Split-Path $vcpkgRoot -Parent
        New-Item -ItemType Directory -Force -Path $toolsParent | Out-Null
        & git clone --depth 1 https://github.com/microsoft/vcpkg.git $vcpkgRoot
        if ($LASTEXITCODE -ne 0) { Fail "git clone of vcpkg exited with $LASTEXITCODE" }

        Write-Doing "Bootstrapping vcpkg (builds the vcpkg.exe tool, ~1-2 min)"
        $bootstrap = Join-Path $vcpkgRoot 'bootstrap-vcpkg.bat'
        & cmd.exe /c "`"$bootstrap`" -disableMetrics"
        if ($LASTEXITCODE -ne 0) { Fail "bootstrap-vcpkg.bat exited with $LASTEXITCODE" }
        if (-not (Test-Path $vcpkgExe)) { Fail "vcpkg.exe still absent after bootstrap" }
        Write-OK "vcpkg bootstrapped: $(Get-Version $vcpkgExe @('version') )"
    }

    # --- install protobuf:x64-windows if absent -------------------------------------
    # `vcpkg list` is the cheap idempotency check; a built port shows up there.
    $alreadyInstalled = $false
    $listOut = & $vcpkgExe list 2>$null
    if (($listOut | Out-String) -match '(?m)^protobuf:x64-windows') {
        $alreadyInstalled = $true
    }

    if ($alreadyInstalled) {
        Write-Skip "protobuf:x64-windows already installed in vcpkg"
    } else {
        Write-Doing "vcpkg install protobuf:x64-windows (builds from source, ~15-30 min)"
        & $vcpkgExe install protobuf:x64-windows --triplet x64-windows
        if ($LASTEXITCODE -ne 0) { Fail "vcpkg install protobuf:x64-windows exited with $LASTEXITCODE" }
        Write-OK "protobuf:x64-windows installed"
    }

    # --- report the matched pair + how to point CMake at it -------------------------
    $vcpkgProtoc = Join-Path $vcpkgRoot 'installed\x64-windows\tools\protobuf\protoc.exe'
    if (Test-Path $vcpkgProtoc) {
        Write-OK "vcpkg protoc: $(Get-Version $vcpkgProtoc @('--version'))  ($vcpkgProtoc)"
    } else {
        Write-Skip "vcpkg protoc.exe not at expected path ($vcpkgProtoc); CONFIG mode still provides protobuf::protoc"
    }
    $toolchain = Join-Path $vcpkgRoot 'scripts\buildsystems\vcpkg.cmake'

    Write-Section 'Stage Protobuf complete'
    Write-Host '  Configure the walker against this libprotobuf with:'
    Write-Host "    cmake -G `"Visual Studio 17 2022`" -A x64 ``"
    Write-Host "          -DCMAKE_TOOLCHAIN_FILE=`"$toolchain`" ``"
    Write-Host '          -DVCPKG_TARGET_TRIPLET=x64-windows ``'
    Write-Host '          -S walker -B walker/build'
    Write-Host '    cmake --build walker/build --config Release'
    Write-Host '    ctest --test-dir walker/build -C Release --output-on-failure'
}

# ---- main ------------------------------------------------------------------------------

Write-Host "CS2-Schema-Tracker bootstrap -- stage: $Stage" -ForegroundColor Cyan

switch ($Stage) {
    'A'        { Invoke-StageA }
    'B'        { Invoke-StageB }
    'Protobuf' { Invoke-StageProtobuf }
    'All'      { Invoke-StageA; Invoke-StageB }
}

Write-Host "`nBootstrap finished." -ForegroundColor Cyan
