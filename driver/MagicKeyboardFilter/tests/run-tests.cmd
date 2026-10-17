@echo off
rem Builds and runs the host unit tests for the remap engine (user-mode, no WDK needed beyond cl).
setlocal
set HERE=%~dp0
call "%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
if not exist "%HERE%obj" mkdir "%HERE%obj"
cl /nologo /W4 /WX /Od /Fo"%HERE%obj\\" /Fe"%HERE%obj\remap_test.exe" "%HERE%remap_test.c" "%HERE%..\Remap.c" || exit /b 1
"%HERE%obj\remap_test.exe"
