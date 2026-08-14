@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop build launcher ===
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-mod.ps1"
set "EXITCODE=%ERRORLEVEL%"

echo.
if not "%EXITCODE%"=="0" (
  echo BUILD FAILED with exit code %EXITCODE%.
) else (
  echo BUILD COMPLETED.
)

echo.
pause
exit /b %EXITCODE%
