@echo off
setlocal
cd /d "%~dp0.."

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\launch-internet-dual-test.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo DUAL INTERNET TEST FAILED with exit code %EXITCODE%.
  pause
)
exit /b %EXITCODE%
