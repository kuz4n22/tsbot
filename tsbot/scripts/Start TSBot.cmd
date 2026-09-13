@echo off
rem TS Music Bot - start. Console window stays minimized; use "Stop TSBot.cmd" to stop.
cd /d "%~dp0"
set "PATH=%~dp0bin;%PATH%"
rem already running from this folder? (count the bot and its watchdog window separately)
for /f "usebackq" %%c in (`powershell -NoProfile -Command "(Get-Process TS3AudioBot -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%~dp0*' } | Measure-Object).Count"`) do set "RUNNING=%%c"
for /f "usebackq" %%w in (`powershell -NoProfile -Command "(Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | Where-Object { $_.CommandLine -like '*%~dp0bin\run-loop.cmd*' } | Measure-Object).Count"`) do set "WATCHDOG=%%w"
if not "%RUNNING%%WATCHDOG%"=="00" (echo TSBot is already running. & timeout /t 3 >NUL & exit /b 0)
if exist "%~dp0stop.flag" del "%~dp0stop.flag"
rem 1) keep yt-dlp fresh (YouTube changes often; silently skipped if offline)
start "" /min /wait "%~dp0bin\yt-dlp.exe" -U
rem 2) run the bot inside a watchdog loop (auto-restart on crash); the bot itself checks
rem    whether YouTube needs the bypass and switches on its own
start "TS Music Bot" /min cmd /c ""%~dp0bin\run-loop.cmd""
