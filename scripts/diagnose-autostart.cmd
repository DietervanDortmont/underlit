@echo off
REM Underlit autostart diagnostic - double-click to dump everything we need
REM to figure out why autostart isn't firing on this machine.
REM
REM Run this AFTER a reboot where Underlit didn't autostart, then copy the
REM full output and paste it back so we can see what's going on.

setlocal
set SCRIPT_DIR=%~dp0
title Underlit autostart diagnostic

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%diagnose-autostart.ps1"
exit /b %ERRORLEVEL%
