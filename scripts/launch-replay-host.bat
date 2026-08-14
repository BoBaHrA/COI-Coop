@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop experimental HOST replay ===
echo.
echo Use only with a disposable paused test save.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-coop.ps1" -Mode Host -Port 27015 -Replay
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo HOST LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
