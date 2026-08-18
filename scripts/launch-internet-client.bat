@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop INTERNET CLIENT ===
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

set /p "SESSIONCODE=Session code: "
if "%SESSIONCODE%"=="" (
  echo Session code is required.
  pause
  exit /b 2
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-internet.ps1" -Mode Client -RelayUrl "%RELAYURL%" -SessionCode "%SESSIONCODE%"
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo INTERNET CLIENT LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
