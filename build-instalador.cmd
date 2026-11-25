@echo off
rem Gera a Suite de Redes (.NET Framework 4.6.2, nada embutido) e o instalador.
setlocal
cd /d "%~dp0"
dotnet build SuiteRedes.csproj -c Release || exit /b 1
set ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
if not exist "%ISCC%" set ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe
"%ISCC%" /Q installer\SuiteRedes.iss || exit /b 1
echo Programa:   bin\Release\net462\SuiteRedes.exe
echo Instalador: publish\instalador\SuiteRedes-Setup.exe
