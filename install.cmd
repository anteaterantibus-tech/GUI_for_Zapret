@echo off
chcp 65001 >nul
title Zapret Control installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
echo.
pause
