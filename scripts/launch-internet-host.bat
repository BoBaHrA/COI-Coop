@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET HOST ===
echo.
if not "%COI_COOP_RELAY_URL%"=="" (
  set "RELAYURL=%COI_COOP_RELAY_URL%"
) else (
  set "RELAYURL=https://coi-coop-relay.onrender.com"
)

echo Relay: %RELAYURL%
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-internet.ps1" -Mode Host -RelayUrl "%RELAYURL%"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo INTERNET HOST LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
