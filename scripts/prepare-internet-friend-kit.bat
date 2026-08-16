@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET friend kit packager ===
echo.

if "%~1"=="" (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\prepare-internet-friend-kit.ps1" -SourceSaveName "COOP_LAN_BASE" -RelayUrl "https://coi-coop-relay.onrender.com"
) else (
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\prepare-internet-friend-kit.ps1" %*
)
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo INTERNET FRIEND KIT PREPARATION FAILED with exit code %EXITCODE%.
) else (
  echo INTERNET FRIEND KIT PREPARATION COMPLETED.
)

pause
exit /b %EXITCODE%
