@echo off
REM Underlit watch mode - double-click once and leave the window open.
REM Rebuilds and relaunches the dev build every time a source file changes.

setlocal
set SCRIPT_DIR=%~dp0
title Underlit dev watch

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%dev-watch.ps1" %*

echo.
echo Watcher stopped. Press any key to close this window.
pause >nul
