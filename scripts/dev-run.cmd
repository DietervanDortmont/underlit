@echo off
REM Underlit dev loop - double-click to rebuild and launch the dev build (~15s).
REM
REM Wraps scripts/run.ps1 with -ExecutionPolicy Bypass so it works regardless of
REM the system's PowerShell execution policy. -NoProfile skips your conda /
REM personal profile and saves ~3 seconds per invocation.
REM
REM Pauses on failure so you can read the error before the window closes.

setlocal
set SCRIPT_DIR=%~dp0
title Underlit dev run

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%run.ps1" %*
set EXITCODE=%ERRORLEVEL%

if not "%EXITCODE%"=="0" (
    echo.
    echo ============================================
    echo  Build failed - see errors above.
    echo  Press any key to close this window.
    echo ============================================
    pause >nul
    exit /b %EXITCODE%
)

REM On success the script has already launched the app; just close this window.
exit /b 0
