# Underlit - one-time desktop-shortcut setup.
#
# Drops three shortcuts on the user's desktop, each pointing at one of the
# .cmd wrappers in this folder. Each shortcut also gets the Underlit icon
# (resolved from the installed copy if present, otherwise the dev-build EXE
# if present, otherwise none) so they look right alongside other apps.
#
# Safe to re-run; existing shortcuts are overwritten.
#
# IMPORTANT: ASCII-only on purpose. Windows PowerShell 5.1 reads .ps1 in the
# system ANSI codepage, and non-ASCII bytes in comments break the parser.

$ErrorActionPreference = "Stop"

$repoRoot   = Split-Path -Parent $PSScriptRoot
$scriptsDir = $PSScriptRoot
$desktop    = [Environment]::GetFolderPath("Desktop")

# Try to point Windows at a real .ico/.exe so the shortcuts have a logo.
# Order of preference: installed copy, then any existing dev-build EXE,
# then no icon (Windows uses the default cmd.exe icon).
$installedExe = "$env:LOCALAPPDATA\Programs\Underlit\Underlit.exe"
$devExeCandidates = Get-ChildItem -Path "$repoRoot\src\Underlit\bin" -Recurse -Filter "Underlit.exe" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending
$iconSource = if (Test-Path $installedExe) {
    $installedExe
} elseif ($devExeCandidates) {
    $devExeCandidates[0].FullName
} else {
    $null
}

function New-Lnk {
    param(
        [string]$LnkPath,
        [string]$Target,
        [string]$Description
    )
    if (Test-Path $LnkPath) { Remove-Item $LnkPath -Force }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $lnk = $shell.CreateShortcut($LnkPath)
        $lnk.TargetPath       = $Target
        $lnk.WorkingDirectory = Split-Path $Target -Parent
        $lnk.WindowStyle      = 1            # normal window
        $lnk.Description      = $Description
        if ($script:iconSource) { $lnk.IconLocation = "$($script:iconSource),0" }
        $lnk.Save()
    } finally {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
    Write-Host "  [+] $LnkPath" -ForegroundColor Green
}

Write-Host "Creating Underlit dev shortcuts on the desktop..." -ForegroundColor Cyan

New-Lnk `
    -LnkPath "$desktop\Underlit dev (run).lnk" `
    -Target  "$scriptsDir\dev-run.cmd" `
    -Description "Rebuild Debug and launch the dev build (~15 seconds)."

New-Lnk `
    -LnkPath "$desktop\Underlit dev (install).lnk" `
    -Target  "$scriptsDir\dev-install.cmd" `
    -Description "Publish Release, build the installer with Inno Setup, install, and launch (~1-2 minutes)."

New-Lnk `
    -LnkPath "$desktop\Underlit dev (uninstall).lnk" `
    -Target  "$scriptsDir\dev-uninstall.cmd" `
    -Description "Silently uninstall the locally-installed Underlit."

New-Lnk `
    -LnkPath "$desktop\Underlit dev (watch).lnk" `
    -Target  "$scriptsDir\dev-watch.cmd" `
    -Description "Leave open: rebuilds and relaunches the dev build on every source change."

Write-Host "All four shortcuts created." -ForegroundColor Green
