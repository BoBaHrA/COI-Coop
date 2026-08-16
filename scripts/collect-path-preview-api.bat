@echo off
setlocal
cd /d "%~dp0.."
echo === COI-Coop path preview API collector ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-path-preview-api.ps1"
if errorlevel 1 (
  echo.
  echo PATH PREVIEW API COLLECTION FAILED.
  pause
  exit /b 1
)
echo.
echo PATH PREVIEW API COLLECTION COMPLETED.
pause
