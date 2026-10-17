@echo off
rem Builds everything and produces artifacts\installer\MagicKeyboard-Setup-<version>.exe
rem Needs: .NET 8 SDK, VS 2022 Build Tools + WDK 10.0.26100 (driver), Inno Setup 6.
setlocal
cd /d "%~dp0"

echo === Driver
call driver\MagicKeyboardFilter\build.cmd || exit /b 1

echo === Tests
call driver\MagicKeyboardFilter\tests\run-tests.cmd || exit /b 1

echo === App (self-contained: no .NET install needed on the target PC)
if exist artifacts\app rmdir /s /q artifacts\app
dotnet publish gui\MagicKeyboard -c Release -r win-x64 --self-contained true -o artifacts\app --nologo -v q || exit /b 1
rem KeyProbe shares the same runtime files, so it costs only a few KB.
dotnet publish tools\KeyProbe -c Release -r win-x64 --self-contained true -o artifacts\app --nologo -v q || exit /b 1

echo === Installer
set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
"%ISCC%" /Q installer\MagicKeyboard.iss || exit /b 1
dir /b artifacts\installer\*.exe
