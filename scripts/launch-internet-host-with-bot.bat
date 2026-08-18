@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET HOST + HEADLESS CLIENT ===
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\launch-internet-host-with-bot.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo HOST + BOT TEST FAILED with exit code %EXITCODE%.
  pause
)
exit /b %EXITCODE%
