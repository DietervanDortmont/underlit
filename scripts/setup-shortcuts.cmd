@echo off
REM Underlit one-time setup - double-click to drop dev shortcuts on your desktop.
REM
REM Creates three .lnk files on your desktop:
REM   "Underlit dev (run)"     -> rebuild Debug + launch (~15s)
REM   "Underlit dev (install)" -> publish Release + Inno Setup + install (~1-2 min)
REM   "Underlit dev (uninstall)"
REM
REM Safe to re-run; existing shortcuts get overwritten.

setlocal
set SCRIPT_DIR=%~dp0
title Underlit setup

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%setup-shortcuts.ps1"
set EXITCODE=%ERRORLEVEL%

echo.
if "%EXITCODE%"=="0" (
    echo Done. Check your desktop.
) else (
    echo Setup failed - see errors above.
)
echo Press any key to close.
pause >nul
exit /b %EXITCODE%
