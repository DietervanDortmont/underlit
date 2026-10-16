@echo off
REM Underlit local install - double-click to publish, build installer, install, launch (~1-2 min).
REM
REM Use this when you want to test the installed-app behaviour (autostart, shortcuts,
REM uninstall flow). For pure code-change iteration, dev-run.cmd is ~10x faster.

setlocal
set SCRIPT_DIR=%~dp0
title Underlit local install

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%install-local.ps1" %*
set EXITCODE=%ERRORLEVEL%

if not "%EXITCODE%"=="0" (
    echo.
    echo ============================================
    echo  Install failed - see errors above.
    echo  Press any key to close this window.
    echo ============================================
    pause >nul
    exit /b %EXITCODE%
)

echo.
echo Done. Press any key to close this window.
pause >nul
exit /b 0
