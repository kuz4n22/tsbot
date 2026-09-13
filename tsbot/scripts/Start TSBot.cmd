@echo off
rem TS Music Bot - start. Console window stays minimized; use "Stop TSBot.cmd" to stop.
cd /d "%~dp0"
set "PATH=%~dp0bin;%PATH%"
tasklist /FI "IMAGENAME eq TS3AudioBot.exe" 2>NUL | find /I "TS3AudioBot.exe" >NUL && (echo TSBot is already running. & timeout /t 3 >NUL & exit /b 0)
if exist "%~dp0stop.flag" del "%~dp0stop.flag"
rem 1) keep yt-dlp fresh (YouTube changes often; silently skipped if offline)
start "" /min /wait "%~dp0bin\yt-dlp.exe" -U
rem 2) check whether YouTube is reachable directly and tune the bypass proxy accordingly
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0bin\netcheck.ps1" -Apply
rem 3) run the bot inside a watchdog loop (auto-restart on crash)
start "TS Music Bot" /min cmd /c ""%~dp0bin\run-loop.cmd""
