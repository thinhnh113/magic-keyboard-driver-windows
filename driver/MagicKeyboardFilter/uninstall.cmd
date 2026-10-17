@echo off
rem Elevates and runs uninstall.ps1.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process cmd -Verb RunAs -ArgumentList '/k \"\"%~f0\"\"'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
