@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop replay save preparation ===
echo Close BOTH Captain of Industry instances before continuing.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\prepare-replay-saves.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo SAVE PREPARATION FAILED with exit code %EXITCODE%.
) else (
  echo SAVE PREPARATION COMPLETED.
)

echo.
pause
exit /b %EXITCODE%
