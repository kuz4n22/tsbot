@echo off
rem TSBot - offline setup from this unpacked package: same as the one-line installer, but the bot itself is taken from here.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Payload "%~dp0."
pause
