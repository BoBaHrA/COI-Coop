@echo off
setlocal
cd /d "%~dp0.."

echo === COI-Coop ramp/slope API collector ===
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0collect-ramp-api.ps1"
set EXITCODE=%ERRORLEVEL%

if not "%EXITCODE%"=="0" (
    echo RAMP API COLLECTION FAILED with exit code %EXITCODE%.
    exit /b %EXITCODE%
)

echo RAMP API COLLECTION COMPLETED.
pause
