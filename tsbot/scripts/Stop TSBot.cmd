@echo off
rem Stops the bot that lives in this folder: graceful quit, then the watchdog window, then force.
cd /d "%~dp0"
echo stop > "%~dp0stop.flag"
curl.exe -s -m 5 http://localhost:58913/api/system/quit >NUL 2>&1
timeout /t 4 /nobreak >NUL
rem the watchdog window of THIS folder (a window left in selection mode never sees stop.flag)
powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='cmd.exe'\" | Where-Object { $_.CommandLine -like '*%~dp0bin\run-loop.cmd*' -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
powershell -NoProfile -Command "Get-Process TS3AudioBot, ciadpi -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%~dp0*' } | Stop-Process -Force"
if exist "%~dp0stop.flag" del "%~dp0stop.flag"
echo TSBot stopped.
timeout /t 2 >NUL
