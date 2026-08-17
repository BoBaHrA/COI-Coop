@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop SINGLE-PC INTERNET DUAL TEST ===
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\launch-internet-dual-test.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo DUAL INTERNET TEST FAILED with exit code %EXITCODE%.
  pause
)
exit /b %EXITCODE%
