# Underlit - fast local dev loop.
#
# Kills any running Underlit, rebuilds in Debug, launches the new EXE.
# Total round-trip: ~10-20 seconds on a warm cache.
#
# Usage (from repo root):
#   pwsh scripts/run.ps1            # Debug build, fastest
#   pwsh scripts/run.ps1 -Release   # Release build, ~5s slower but matches CI
#   pwsh scripts/run.ps1 -NoLaunch  # build only, don't start the EXE
#
# IMPORTANT: this file is intentionally ASCII-only. Windows PowerShell 5.1
# reads .ps1 files using the system ANSI codepage by default, so non-ASCII
# punctuation like em dashes turns into mojibake and breaks the parser.

param(
    [switch]$Release,
    [switch]$NoLaunch
)

$ErrorActionPreference = "Stop"
$repoRoot   = Split-Path -Parent $PSScriptRoot
$proj       = Join-Path $repoRoot "src\Underlit\Underlit.csproj"
$config     = if ($Release) { "Release" } else { "Debug" }

# 1. Stop any running Underlit so the EXE isn't locked.
Get-Process -Name "Underlit" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Stopping running Underlit (PID $($_.Id))..." -ForegroundColor Yellow
    $_ | Stop-Process -Force
    Start-Sleep -Milliseconds 300
}

# 2. Build. -nologo + -v:m keeps output short so errors stand out.
Write-Host "Building $config..." -ForegroundColor Cyan
$buildOutput = dotnet build $proj -c $config -nologo -v:m
if ($LASTEXITCODE -ne 0) {
    $buildOutput | Write-Host
    Write-Host "Build failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

if ($NoLaunch) {
    Write-Host "Build OK. (NoLaunch - not starting the EXE.)" -ForegroundColor Green
    exit 0
}

# 3. Launch. The output folder name embeds the TargetFramework which is
#    versioned in the csproj, so resolve it dynamically. That way this
#    script keeps working when we bump frameworks.
$binDir = Join-Path $repoRoot "src\Underlit\bin\$config"
$exe = Get-ChildItem $binDir -Recurse -Filter "Underlit.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $exe) {
    Write-Host "Couldn't find Underlit.exe under $binDir" -ForegroundColor Red
    exit 1
}

Write-Host "Launching $($exe.FullName)..." -ForegroundColor Green
Start-Process -FilePath $exe.FullName
