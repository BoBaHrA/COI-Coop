@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop experimental CLIENT replay ===
echo.
echo Use only with the same disposable paused test save as the host.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-coop.ps1" -Mode Client -Port 27015 -Replay
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo CLIENT LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
