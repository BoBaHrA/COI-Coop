@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop TWO-PC LAN CLIENT ===
echo.
echo Enter the LAN IPv4 address shown by the host PC.
echo Example: 192.168.1.50
echo.
set /p "HOSTIP=Host LAN IPv4: "

if "%HOSTIP%"=="" (
  echo Host IP is required.
  pause
  exit /b 2
)

set "COI_COOP_LAN=1"
set "COI_COOP_BIND=127.0.0.1"
set "COI_COOP_LAN_BASE_PORT=27115"
set "COI_COOP_HOST=%HOSTIP%"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-coop.ps1" -Mode Client -Port 27015 -Replay
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo LAN CLIENT LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
