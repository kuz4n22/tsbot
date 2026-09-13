@echo off
rem Updates yt-dlp used by the bot (YouTube changes often; run this if links stop working).
"%~dp0bin\yt-dlp.exe" -U
pause
