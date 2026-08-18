@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop HOST + BOT diagnostics ===
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\collect-host-bot-diagnostics.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo HOST + BOT DIAGNOSTICS FAILED with exit code %EXITCODE%.
  pause
)
exit /b %EXITCODE%
