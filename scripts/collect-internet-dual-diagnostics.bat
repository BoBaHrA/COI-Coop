@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET DUAL diagnostics ===
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\collect-internet-dual-diagnostics.ps1"
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo DIAGNOSTICS COLLECTION FAILED with exit code %EXITCODE%.
)
pause
exit /b %EXITCODE%
