@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET friend kit packager ===
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\prepare-internet-friend-kit.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo INTERNET FRIEND KIT PREPARATION FAILED with exit code %EXITCODE%.
) else (
  echo INTERNET FRIEND KIT PREPARATION COMPLETED.
)

pause
exit /b %EXITCODE%
