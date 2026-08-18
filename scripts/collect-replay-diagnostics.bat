@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop replay diagnostics collector ===
echo Collecting the two most recent Captain of Industry logs...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\collect-diagnostics.ps1" -LatestCount 2 -CopyToClipboard
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo REPLAY DIAGNOSTICS FAILED with exit code %EXITCODE%.
) else (
  echo REPLAY DIAGNOSTICS COMPLETED. Both recent logs were copied to clipboard.
)

pause
exit /b %EXITCODE%
