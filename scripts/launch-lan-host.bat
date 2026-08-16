@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop TWO-PC LAN HOST ===
echo.
echo This keeps the proven co-op protocol on loopback and exposes
echo development LAN tunnels on TCP 27115-27118.
echo.
echo IMPORTANT:
echo   1. Host and client must use byte-identical starting saves.
echo   2. Allow Captain of Industry through Windows Firewall on Private networks.
echo   3. Give the client one of this PC's LAN IPv4 addresses below.
echo.
echo LAN IPv4 candidates:
powershell.exe -NoProfile -Command "Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue ^| Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.PrefixOrigin -ne 'WellKnown' } ^| Sort-Object InterfaceMetric ^| ForEach-Object { '  ' + $_.IPAddress + '  (' + $_.InterfaceAlias + ')' }"
echo.

set "COI_COOP_LAN=1"
set "COI_COOP_BIND=0.0.0.0"
set "COI_COOP_LAN_BASE_PORT=27115"
set "COI_COOP_HOST=127.0.0.1"

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch-coop.ps1" -Mode Host -Port 27015 -Replay
set "EXITCODE=%ERRORLEVEL%"

if not "%EXITCODE%"=="0" (
  echo.
  echo LAN HOST LAUNCH FAILED with exit code %EXITCODE%.
  pause
)

exit /b %EXITCODE%
