@echo off
setlocal
cd /d "%~dp0.."
echo === COI-Coop diagnostics collector ===
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\collect-diagnostics.ps1" -CopyToClipboard
set EXITCODE=%ERRORLEVEL%
echo.
if not "%EXITCODE%"=="0" (
  echo DIAGNOSTICS FAILED with exit code %EXITCODE%.
) else (
  echo DIAGNOSTICS COMPLETED. Output was copied to clipboard.
)
pause
exit /b %EXITCODE%
