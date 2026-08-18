@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop soak regression checker ===
echo Checking the two most recent Captain of Industry logs...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\check-coop-soak.ps1" -LatestCount 2 -CopyToClipboard
set "EXITCODE=%ERRORLEVEL%"

echo.
if "%EXITCODE%"=="0" (
  echo SOAK CHECK PASSED.
) else if "%EXITCODE%"=="1" (
  echo SOAK CHECK FAILED - regression markers were found.
) else (
  echo SOAK CHECK INCOMPLETE with exit code %EXITCODE%.
)

echo.
pause
exit /b %EXITCODE%
