@echo off
rem Stops the bot that lives in this folder (graceful quit first, then force).
cd /d "%~dp0"
echo stop > "%~dp0stop.flag"
curl.exe -s -m 5 http://localhost:58913/api/system/quit >NUL 2>&1
timeout /t 4 /nobreak >NUL
powershell -NoProfile -Command "Get-Process TS3AudioBot, ciadpi -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%~dp0*' } | Stop-Process -Force"
if exist "%~dp0stop.flag" del "%~dp0stop.flag"
echo TSBot stopped.
timeout /t 2 >NUL
