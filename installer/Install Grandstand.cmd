@echo off
rem Grandstand - beta installer. Double-click this file (no admin needed).
title Grandstand - installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\grandstand_install.ps1" %*
set RC=%ERRORLEVEL%
echo.
if not "%RC%"=="0" echo The installer stopped with code %RC%. Read the message above.
pause
exit /b %RC%
