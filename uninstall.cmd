@echo off
chcp 65001 >nul
title Zapret Control uninstaller
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" -Uninstall
echo.
pause
