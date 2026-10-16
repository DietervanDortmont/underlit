# Underlit - local installer dev loop.
#
# Mirrors what the GitHub Actions release.yml workflow does, but on YOUR
# machine, no tag push, no CI wait. Total round-trip: ~1-2 minutes.
#
# Steps:
#   1. Stop any running Underlit.
#   2. dotnet publish (self-contained, single-file) into ./publish.
#   3. Run Inno Setup Compiler on installer/Underlit.iss to produce
#      installer/Output/UnderlitSetup-x.y.z.exe.
#   4. Run the installer silently.
#   5. Optionally launch the freshly-installed Underlit.
#
# Use this when you specifically need to test installer behaviour:
#   - AutoStart registry path written by Inno Setup (must match the
#     install location, not the bin/ folder).
#   - Inno Setup tasks (start-with-windows checkbox, desktop shortcut).
#   - Uninstall / re-install behaviour.
#
# For quick code changes, prefer scripts/run.ps1, which is ~10x faster.
#
# Usage (from repo root):
#   pwsh scripts/install-local.ps1
#   pwsh scripts/install-local.ps1 -NoLaunch       # install but don't start
#   pwsh scripts/install-local.ps1 -Uninstall      # just uninstall, skip the rest
#
# IMPORTANT: this file is intentionally ASCII-only. Windows PowerShell 5.1
# reads .ps1 files using the system ANSI codepage by default, so non-ASCII
# punctuation like em dashes turns into mojibake and breaks the parser.

param(
    [switch]$NoLaunch,
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$repoRoot   = Split-Path -Parent $PSScriptRoot
$proj       = Join-Path $repoRoot "src\Underlit\Underlit.csproj"
$iss        = Join-Path $repoRoot "installer\Underlit.iss"
$publishDir = Join-Path $repoRoot "publish"
$outputDir  = Join-Path $repoRoot "installer\Output"

# Locate Inno Setup. Searches a few well-known places PLUS the Windows
# uninstall registry (which records every installed app's InstallLocation),
# PLUS the winget per-user package cache. Inno Setup ends up in different
# folders depending on whether the user installed it as admin, per-user,
# or via winget, so a fixed-path lookup misses too often.
function Find-Iscc {
    # 1) Hard-coded standard install locations.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 5\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    # 2) Whatever's on PATH.
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    # 3) Walk the Windows uninstall registry for any "Inno Setup" entry.
    #    Both HKLM and HKCU (per-user installs land in HKCU). The
    #    "InstallLocation" value, if set, points at the install folder
    #    that contains ISCC.exe. We also try "Inno Setup: App Path"
    #    which Inno Setup itself writes.
    $regRoots = @(
        "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
    )
    foreach ($root in $regRoots) {
        if (-not (Test-Path $root)) { continue }
        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { return }
            $name = $props.DisplayName
            if (-not $name) { return }
            if ($name -notmatch 'Inno Setup') { return }
            foreach ($prop in @($props.InstallLocation, $props.'Inno Setup: App Path')) {
                if (-not $prop) { continue }
                $iscc = Join-Path $prop "ISCC.exe"
                if (Test-Path $iscc) { return $iscc }
            }
        } | Where-Object { $_ } | Select-Object -First 1 | ForEach-Object { return $_ }
    }
    # The pipe-and-return trick above doesn't actually escape the function,
    # so re-loop and stash the hit explicitly. PowerShell function flow is
    # quirky with nested ForEach-Object returns.
    foreach ($root in $regRoots) {
        if (-not (Test-Path $root)) { continue }
        $items = Get-ChildItem $root -ErrorAction SilentlyContinue
        foreach ($item in $items) {
            $props = Get-ItemProperty $item.PSPath -ErrorAction SilentlyContinue
            if (-not $props) { continue }
            if (-not $props.DisplayName) { continue }
            if ($props.DisplayName -notmatch 'Inno Setup') { continue }
            foreach ($prop in @($props.InstallLocation, $props.'Inno Setup: App Path')) {
                if (-not $prop) { continue }
                $iscc = Join-Path $prop "ISCC.exe"
                if (Test-Path $iscc) { return $iscc }
            }
        }
    }

    # 4) winget caches packages under %LOCALAPPDATA%\Microsoft\WinGet\Packages.
    #    JRSoftware.InnoSetup lands here when winget installs it. Search broadly.
    $wingetRoot = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages"
    if (Test-Path $wingetRoot) {
        $hit = Get-ChildItem $wingetRoot -Recurse -Filter "ISCC.exe" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Host "Inno Setup Compiler (ISCC.exe) not found." -ForegroundColor Yellow
    Write-Host "It's needed to build the local installer EXE." -ForegroundColor Yellow

    # Offer to install via winget. Modern Windows 10 (1809+) and Windows 11
    # ship with winget by default, so this works for almost everyone without
    # asking them to download anything manually.
    $winget = Get-Command "winget.exe" -ErrorAction SilentlyContinue
    if ($winget) {
        Write-Host ""
        Write-Host "I can install Inno Setup for you via winget right now." -ForegroundColor Cyan
        $reply = Read-Host "Install Inno Setup via winget? [Y/n]"
        if ($reply -eq "" -or $reply -match '^[Yy]') {
            Write-Host "Running: winget install --id JRSoftware.InnoSetup --silent --accept-source-agreements --accept-package-agreements" -ForegroundColor Cyan
            winget install --id JRSoftware.InnoSetup --silent --accept-source-agreements --accept-package-agreements
            $wingetExit = $LASTEXITCODE
            # winget returns -1978335189 (= 0x8A15002B) when the package is
            # already installed and there's no upgrade available. That's a
            # success for our purposes - we just need ISCC.exe on disk.
            if ($wingetExit -ne 0 -and $wingetExit -ne -1978335189) {
                Write-Host "winget install failed (exit $wingetExit)." -ForegroundColor Red
                Write-Host "Install manually from https://jrsoftware.org/isdl.php and re-run." -ForegroundColor Yellow
                exit 1
            }
            $iscc = Find-Iscc
            if (-not $iscc) {
                Write-Host "Inno Setup is reportedly installed but ISCC.exe still isn't findable." -ForegroundColor Red
                Write-Host "Try opening a new PowerShell window (so PATH refreshes) and re-running this script." -ForegroundColor Yellow
                Write-Host "Or install manually from https://jrsoftware.org/isdl.php." -ForegroundColor Yellow
                exit 1
            }
            Write-Host "Inno Setup found at $iscc" -ForegroundColor Green
        }
        else {
            Write-Host "Aborted. Install manually from https://jrsoftware.org/isdl.php and re-run." -ForegroundColor Yellow
            exit 1
        }
    }
    else {
        Write-Host "winget isn't available on this machine." -ForegroundColor Red
        Write-Host "Install Inno Setup manually from https://jrsoftware.org/isdl.php and re-run." -ForegroundColor Yellow
        exit 1
    }
}

# Read the version straight from the csproj so the installer's filename
# matches without us having to keep two values in sync.
[xml]$xml = Get-Content $proj
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
if (-not $version) { Write-Host "Couldn't parse <Version> from csproj." -ForegroundColor Red; exit 1 }

# 1. Stop running Underlit (would lock the EXE during install).
Get-Process -Name "Underlit" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running Underlit (PID $($_.Id))..." -ForegroundColor Yellow
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

if ($Uninstall) {
    # Uninstall the most-recent installed Underlit and exit. Inno Setup
    # writes the unins000.exe into the install folder.
    $installRoot    = "$env:LOCALAPPDATA\Programs\Underlit"        # per-user default
    $installRootSys = "$env:ProgramFiles\Underlit"                 # system default
    foreach ($p in @("$installRoot\unins000.exe", "$installRootSys\unins000.exe")) {
        if (Test-Path $p) {
            Write-Host "Uninstalling via $p..." -ForegroundColor Cyan
            Start-Process -FilePath $p -ArgumentList "/SILENT","/SUPPRESSMSGBOXES","/NORESTART" -Wait
            Write-Host "Uninstalled." -ForegroundColor Green
            exit 0
        }
    }
    Write-Host "No existing Underlit install found." -ForegroundColor Yellow
    exit 0
}

# 2. dotnet publish - same flags release.yml uses.
# v0.6.64: -p:PublishReadyToRun=true pre-JITs the assembly to native code,
# cutting cold-start time on first-of-the-day launches noticeably. The
# csproj also sets this, but passing it on the CLI keeps the intent
# obvious here.
Write-Host "Publishing self-contained single-file build with R2R (v$version)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj `
    -c Release -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:Version=$version `
    -o $publishDir `
    -nologo -v:m
if ($LASTEXITCODE -ne 0) { Write-Host "Publish failed." -ForegroundColor Red; exit $LASTEXITCODE }

# 3. Inno Setup compile.
Write-Host "Compiling installer..." -ForegroundColor Cyan
& $iscc "/DMyAppVersion=$version" $iss
if ($LASTEXITCODE -ne 0) { Write-Host "ISCC failed." -ForegroundColor Red; exit $LASTEXITCODE }

# Locate the produced .exe. Inno emits to installer/Output by default.
$setupExe = Get-ChildItem $outputDir -Filter "UnderlitSetup-$version.exe" |
    Select-Object -First 1
if (-not $setupExe) {
    Write-Host "Installer EXE not produced." -ForegroundColor Red
    exit 1
}

# 4. Run silently. /SP- suppresses the "This will install..." prompt;
#    /VERYSILENT hides the wizard; /SUPPRESSMSGBOXES handles "do you
#    want to keep settings" etc; /NORESTART avoids any reboot prompts.
#    /TASKS picks the optional tasks defined in the .iss; without it,
#    only the unchecked-by-default ones are skipped.
Write-Host "Installing $($setupExe.Name)..." -ForegroundColor Cyan
Start-Process -FilePath $setupExe.FullName `
    -ArgumentList "/SP-","/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/TASKS=startwithwindows" `
    -Wait

# 5. Launch.
if (-not $NoLaunch) {
    $exe = "$env:LOCALAPPDATA\Programs\Underlit\Underlit.exe"
    if (-not (Test-Path $exe)) { $exe = "$env:ProgramFiles\Underlit\Underlit.exe" }
    if (Test-Path $exe) {
        Write-Host "Launching $exe..." -ForegroundColor Green
        Start-Process -FilePath $exe
    }
    else {
        Write-Host "Installed but couldn't locate Underlit.exe to launch." -ForegroundColor Yellow
    }
}
else {
    Write-Host "Installed. (NoLaunch - not starting.)" -ForegroundColor Green
}
