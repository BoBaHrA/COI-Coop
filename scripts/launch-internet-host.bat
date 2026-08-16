@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET HOST ===
echo.
if not "%COI_COOP_RELAY_URL%"=="" (
  set "RELAYURL=%COI_COOP_RELAY_URL%"
) else (
  set /p "RELAYURL=Relay URL (https://...onrender.com): "
)

if "%RELAYURL%"=="" (
  echo Relay URL is required.
  pause
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-internet.ps1" -Mode Host -RelayUrl "%RELAYURL%"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo INTERNET HOST LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
