# Underlit - watch mode.
#
# Start once and leave the window open. Every time a file under src\ changes
# (you saving in an editor, or Claude copying new files over), this script
# waits for the writes to settle, stops the running Underlit, rebuilds Debug
# and relaunches the dev build. No double-clicking per iteration.
#
# Every build result is also appended to publish\dev-watch.log so the result
# can be checked remotely without looking at this window. (publish\ is
# gitignored.)
#
# Usage:
#   double-click scripts\dev-watch.cmd
#   or: powershell -ExecutionPolicy Bypass -File scripts\dev-watch.ps1
#
# IMPORTANT: ASCII-only on purpose. Windows PowerShell 5.1 reads .ps1 in the
# system ANSI codepage, and non-ASCII bytes break the parser.

param(
    [int]$SettleMs = 1500
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot
$srcDir   = Join-Path $repoRoot "src\Underlit"
$proj     = Join-Path $srcDir "Underlit.csproj"
$binDir   = Join-Path $srcDir "bin\Debug"
$logDir   = Join-Path $repoRoot "publish"
$logFile  = Join-Path $logDir "dev-watch.log"
$exts     = @(".cs", ".xaml", ".csproj", ".manifest", ".ico", ".svg", ".json")

if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

function Write-Log([string]$msg, [string]$color = "Gray") {
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg
    Write-Host $line -ForegroundColor $color
    try { Add-Content -Path $logFile -Value $line } catch { }
}

# Cheap fingerprint of the source tree: file count + newest write time +
# total size. Polling this once a second is far more reliable than
# FileSystemWatcher for files written by sync tools.
function Get-SourceSignature {
    $files = Get-ChildItem -Path $srcDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $exts -contains $_.Extension.ToLowerInvariant() -and
            $_.FullName -notmatch '\\(bin|obj)\\'
        }
    $count = 0; $newest = 0L; $size = 0L
    foreach ($f in $files) {
        $count++
        $size += $f.Length
        if ($f.LastWriteTimeUtc.Ticks -gt $newest) { $newest = $f.LastWriteTimeUtc.Ticks }
    }
    return "$count|$newest|$size"
}

function Find-DevExe {
    Get-ChildItem $binDir -Recurse -Filter "Underlit.exe" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Invoke-BuildAndRun {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    Get-Process -Name "Underlit" -ErrorAction SilentlyContinue | ForEach-Object {
        $_ | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 300

    Write-Log "Building..." "Cyan"
    $out = & dotnet build $proj -c Debug -nologo -v:q 2>&1
    $code = $LASTEXITCODE

    if ($code -ne 0) {
        Write-Log "BUILD FAILED ($([int]$sw.Elapsed.TotalSeconds) s):" "Red"
        $out | Where-Object { $_ -match "error" } | Select-Object -First 25 | ForEach-Object { Write-Log "  $_" "Red" }
        # Keep the user's screen + lights running on the last good build.
        $last = Find-DevExe
        if ($last) {
            Write-Log "Relaunching previous build: $($last.FullName)" "Yellow"
            Start-Process -FilePath $last.FullName
        }
        return
    }

    $exe = Find-DevExe
    if (-not $exe) { Write-Log "Build OK but Underlit.exe not found under $binDir" "Red"; return }
    Start-Process -FilePath $exe.FullName
    Write-Log "BUILD OK, relaunched in $([int]$sw.Elapsed.TotalSeconds) s" "Green"
}

$host.UI.RawUI.WindowTitle = "Underlit dev watch"
Write-Log "Watching $srcDir - leave this window open. Ctrl+C to stop." "Cyan"
Invoke-BuildAndRun
$lastSig = Get-SourceSignature

while ($true) {
    Start-Sleep -Milliseconds 1000
    $sig = Get-SourceSignature
    if ($sig -eq $lastSig) { continue }

    # Something changed. Wait until the tree stops changing so a batch of
    # files copied at once triggers one build, not five.
    do {
        $lastSig = $sig
        Start-Sleep -Milliseconds $SettleMs
        $sig = Get-SourceSignature
    } while ($sig -ne $lastSig)

    Write-Log "Change detected." "Yellow"
    Invoke-BuildAndRun
}
