@echo off
rem Builds dist\K580Rgb.exe: a single self-contained file (no .NET install needed).
cd /d "%~dp0"
dotnet publish src\K580Rgb.App -p:PublishProfile=Exe || (pause & exit /b 1)
echo.
echo Built %~dp0dist\K580Rgb.exe
