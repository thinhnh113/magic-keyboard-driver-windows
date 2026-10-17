@echo off
rem Elevates and runs install.ps1. Pass -RemoveVendorDriver to remove Magic Utilities' keyboard driver too.
net session >nul 2>&1 || (powershell -NoProfile -Command "Start-Process cmd -Verb RunAs -ArgumentList '/k \"\"%~f0\" %*\"'" & exit /b)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
