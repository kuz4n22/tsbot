@echo off
title TS Music Bot
rem bin\ first on PATH: yt-dlp finds deno (JS runtime for YouTube) and ffmpeg there
set "PATH=%~dp0;%PATH%"
cd /d "%~dp0..\bot"
:loop
if exist "%~dp0..\stop.flag" del "%~dp0..\stop.flag" & exit /b 0
"%~dp0..\bot\TS3AudioBot.exe" --non-interactive --hide-banner
set code=%errorlevel%
if exist "%~dp0..\stop.flag" del "%~dp0..\stop.flag" & exit /b 0
echo [%date% %time%] bot exited with code %code%, restarting in 5 s...
timeout /t 5 /nobreak >nul
goto loop
