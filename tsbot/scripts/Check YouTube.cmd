@echo off
set "PATH=%~dp0bin;%PATH%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bin\netcheck.ps1" -Verbose
pause
