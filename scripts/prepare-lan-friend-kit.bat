@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop LAN friend kit packager ===
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\prepare-lan-friend-kit.ps1" %*
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo FRIEND KIT PREPARATION FAILED with exit code %EXITCODE%.
) else (
  echo FRIEND KIT PREPARATION COMPLETED.
)

echo.
pause
exit /b %EXITCODE%
