@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0play-coop.ps1" -Mode Join
if errorlevel 1 pause
