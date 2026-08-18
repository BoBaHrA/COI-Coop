@echo off
setlocal
cd /d "%~dp0.."
echo === COI-Coop sandbox API collector ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-sandbox-api.ps1"
if errorlevel 1 (
  echo.
  echo SANDBOX API COLLECTION FAILED.
  pause
  exit /b 1
)
echo.
echo SANDBOX API COLLECTION COMPLETED.
pause
