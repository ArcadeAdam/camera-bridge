@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Camera.ps1" %*
pause
