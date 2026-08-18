@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop PUBLIC RELAY SNAPSHOT TEST ===
echo.
if not "%COI_COOP_RELAY_URL%"=="" (
  set "RELAYURL=%COI_COOP_RELAY_URL%"
) else (
  set "RELAYURL=https://coi-coop-relay.onrender.com"
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0test-public-relay-snapshot.ps1" -RelayUrl "%RELAYURL%"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo PUBLIC RELAY SNAPSHOT TEST FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
