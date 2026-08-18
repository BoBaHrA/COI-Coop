@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop blueprint placement API collector ===

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\collect-blueprint-api.ps1"
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo BLUEPRINT API COLLECTION FAILED with exit code %EXITCODE%.
) else (
  echo BLUEPRINT API COLLECTION COMPLETED.
)

pause
exit /b %EXITCODE%
