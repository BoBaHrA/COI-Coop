@echo off
setlocal
echo === COI-Coop placement API metadata collector ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-placement-api.ps1"
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" (
  echo COLLECT FAILED with exit code %EXITCODE%.
) else (
  echo COLLECT COMPLETED.
)
pause
exit /b %EXITCODE%
