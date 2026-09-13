@echo off
cd /d "%~dp0"
echo stop > "%~dp0stop.flag"
curl.exe -s -m 5 http://localhost:58913/api/system/quit >NUL 2>&1
timeout /t 4 /nobreak >NUL
taskkill /IM TS3AudioBot.exe /F >NUL 2>&1
taskkill /IM ciadpi.exe /F >NUL 2>&1
if exist "%~dp0stop.flag" del "%~dp0stop.flag"
echo TSBot stopped.
timeout /t 2 >NUL
