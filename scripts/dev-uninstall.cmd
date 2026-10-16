@echo off
REM Underlit uninstall - double-click to silently uninstall the local copy.

setlocal
set SCRIPT_DIR=%~dp0
title Underlit uninstall

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%install-local.ps1" -Uninstall
set EXITCODE=%ERRORLEVEL%

if not "%EXITCODE%"=="0" (
    echo.
    echo Uninstall failed. Press any key to close.
    pause >nul
    exit /b %EXITCODE%
)

echo.
echo Done. Press any key to close.
pause >nul
exit /b 0
