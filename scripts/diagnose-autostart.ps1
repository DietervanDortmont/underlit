# Underlit autostart diagnostic.
#
# Dumps everything that affects whether Underlit launches at sign-in:
#   - The HKCU\...\Run registry value (and whether the file actually exists)
#   - The StartupApproved flag Task Manager toggles
#   - The most recent Underlit log entries (so we can see if Underlit even
#     tried to start after the last reboot)
#   - The user's last logon time vs the last Underlit launch time
#   - Any Defender / SmartScreen quarantine block on the EXE
#
# Run this AFTER a reboot where Underlit didn't autostart, copy/paste the
# whole output and send it back so we can see what Windows actually did.
#
# ASCII-only on purpose - see other .ps1 in this folder.

$ErrorActionPreference = "Continue"

# v0.6.52: capture the whole console output to a text file too, so the user
# can attach it without having to fight Windows console copy/paste. Goes
# next to the diagnose-autostart.cmd that launched us.
$reportPath = Join-Path $PSScriptRoot "diagnostic-output.txt"
try { Start-Transcript -Path $reportPath -Force | Out-Null } catch {}

function Section($title) {
    Write-Host ""
    Write-Host ("=" * 60) -ForegroundColor Cyan
    Write-Host $title                  -ForegroundColor Cyan
    Write-Host ("=" * 60) -ForegroundColor Cyan
}

# 1. Run-key entry.
Section "1. HKCU\...\Run\Underlit"
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$run = (Get-ItemProperty -Path $runKey -Name "Underlit" -ErrorAction SilentlyContinue).Underlit
if ($run) {
    Write-Host "Value: $run"
    # Parse out the path to test File.Exists
    $path = $run.Trim()
    if ($path.StartsWith('"')) {
        $end = $path.IndexOf('"', 1)
        if ($end -gt 0) { $path = $path.Substring(1, $end - 1) }
    }
    if (Test-Path $path) {
        Write-Host "  EXE on disk: EXISTS at $path" -ForegroundColor Green
    } else {
        Write-Host "  EXE on disk: MISSING at $path" -ForegroundColor Red
        Write-Host "  This is the 'Task Manager Enabled but doesn't launch' bug."
    }
} else {
    Write-Host "Run-key entry is ABSENT." -ForegroundColor Red
    Write-Host "Underlit won't autostart. Open Settings -> General -> Repair, or toggle Start with Windows off+on."
}

# 2. StartupApproved bit (Task Manager Disable toggle).
Section "2. HKCU\...\StartupApproved\Run\Underlit"
$saKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
$sa = (Get-ItemProperty -Path $saKey -Name "Underlit" -ErrorAction SilentlyContinue).Underlit
if ($sa) {
    $hex = ($sa | ForEach-Object { $_.ToString("X2") }) -join " "
    Write-Host "Bytes: $hex"
    if ($sa[0] -eq 0x02) {
        Write-Host "  State: ENABLED" -ForegroundColor Green
    } elseif ($sa[0] -eq 0x03) {
        Write-Host "  State: DISABLED by Task Manager / Settings -> Startup apps" -ForegroundColor Red
        Write-Host "  -> Open Task Manager -> Startup apps -> Underlit -> set to Enabled."
    } else {
        Write-Host "  State: unknown ($($sa[0]))" -ForegroundColor Yellow
    }
} else {
    Write-Host "No StartupApproved override (defaults to ENABLED)." -ForegroundColor Green
}

# 3. Underlit log.
Section "3. Latest Underlit log entries"
$log = "$env:LOCALAPPDATA\Underlit\logs\underlit.log"
if (Test-Path $log) {
    Write-Host "Log path: $log"
    Write-Host "(showing last 25 lines)"
    Get-Content $log -Tail 25 | ForEach-Object { Write-Host "  $_" }
} else {
    # Older builds wrote next to the EXE. Try a couple of fallbacks.
    $alts = @(
        "$env:LOCALAPPDATA\Underlit\underlit.log",
        "$env:APPDATA\Underlit\underlit.log"
    )
    $found = $false
    foreach ($a in $alts) {
        if (Test-Path $a) {
            Write-Host "Log path: $a"
            Get-Content $a -Tail 25 | ForEach-Object { Write-Host "  $_" }
            $found = $true
            break
        }
    }
    if (-not $found) {
        Write-Host "No Underlit log found at expected locations." -ForegroundColor Yellow
    }
}

# 4. Logon time vs last Underlit launch.
Section "4. Last Windows logon time"
try {
    $logon = Get-CimInstance -ClassName Win32_LogonSession -ErrorAction Stop |
        Where-Object { $_.LogonType -eq 2 -or $_.LogonType -eq 10 } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
    if ($logon) {
        Write-Host "Most recent interactive logon: $($logon.StartTime)"
    }
} catch {
    Write-Host "(couldn't query logon sessions)" -ForegroundColor Yellow
}

# 5. Defender quarantine / blocked status on the EXE.
Section "5. Windows Defender state on Underlit.exe"
$exe = $null
if ($run) {
    $p = $run.Trim()
    if ($p.StartsWith('"')) {
        $end = $p.IndexOf('"', 1)
        if ($end -gt 0) { $p = $p.Substring(1, $end - 1) }
    }
    if (Test-Path $p) { $exe = $p }
}
if ($exe) {
    # Mark-of-the-Web (Zone.Identifier ADS) - if present, Windows treats the
    # file as "downloaded from the internet" and SmartScreen may block it.
    $zone = Get-Item -Path "$exe`:Zone.Identifier" -ErrorAction SilentlyContinue
    if ($zone) {
        Write-Host "Mark-of-the-Web is PRESENT on the EXE." -ForegroundColor Yellow
        Write-Host "  This means Windows tagged the EXE as 'downloaded from internet'."
        Write-Host "  SmartScreen may block silent autostart launches."
        Write-Host "  Fix: right-click the EXE -> Properties -> tick 'Unblock' -> OK."
    } else {
        Write-Host "Mark-of-the-Web: not present (good)." -ForegroundColor Green
    }
} else {
    Write-Host "(skipped - no valid EXE path to check)"
}

# 6. Scheduled Task backstop (added v0.6.52).
Section "6. Scheduled Task \Underlit\Autostart"
$taskName = "\Underlit\Autostart"
$taskRaw = & schtasks.exe /Query /TN $taskName /FO LIST /V 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Host "Task exists." -ForegroundColor Green
    # Pull the interesting fields out of the verbose listing.
    foreach ($line in $taskRaw) {
        if ($line -match "^(Status|Last Run Time|Last Result|Next Run Time|Task To Run|Start In|Run As User|Logon Mode|Scheduled Task State):") {
            Write-Host "  $line"
        }
    }
} else {
    Write-Host "Task NOT registered." -ForegroundColor Red
    Write-Host "  Underlit 0.6.52+ creates this task automatically on first launch."
    Write-Host "  Open Settings -> General -> Repair to register it now."
}

# 7. Single-instance mutex check (catches "already running, skipped").
Section "7. Underlit currently running?"
$proc = Get-Process -Name "Underlit" -ErrorAction SilentlyContinue
if ($proc) {
    foreach ($p in $proc) {
        Write-Host "  PID $($p.Id) started at $($p.StartTime), path: $($p.Path)" -ForegroundColor Green
    }
    Write-Host "Underlit IS running. Check Task Manager -> Processes tab (NOT Services)."
} else {
    Write-Host "Underlit is NOT currently running." -ForegroundColor Red
}

Write-Host ""
Write-Host ("=" * 60) -ForegroundColor Cyan
Write-Host "End of diagnostic." -ForegroundColor Cyan
Write-Host ("=" * 60) -ForegroundColor Cyan
Write-Host ""
try { Stop-Transcript | Out-Null } catch {}

# v0.6.52: also save a clean (no PowerShell transcript header) copy and
# open it in Notepad so the user can just Ctrl+A / Ctrl+C without
# fighting the console buffer. The transcript file itself is still
# next to the script as diagnostic-output.txt for reference.
if (Test-Path $reportPath) {
    Write-Host "A copy of this output has been saved to:" -ForegroundColor Green
    Write-Host "  $reportPath" -ForegroundColor Green
    Write-Host "Opening it in Notepad now - select all (Ctrl+A), copy (Ctrl+C), paste it back." -ForegroundColor Green
    try { Start-Process notepad.exe -ArgumentList $reportPath } catch {}
}
Write-Host ""
Write-Host "Press Enter to close this window." -ForegroundColor DarkGray
$null = Read-Host
