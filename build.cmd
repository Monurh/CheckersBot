@echo off
rem Builds the service and its engine worker into .\publish (copy that folder to the IIS site).
setlocal
cd /d "%~dp0"
dotnet publish src\CheckersApi -c Release -r win-x64 --self-contained false -o publish
if errorlevel 1 exit /b 1
echo.
echo Published to %~dp0publish
