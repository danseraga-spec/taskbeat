@echo off
rem Сборка Taskbeat: самодостаточный exe для Windows x64 в папку dist
setlocal
set DOTNET=dotnet
where dotnet >nul 2>&1 || set DOTNET="%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe"
%DOTNET% publish "%~dp0app\Taskbeat.csproj" -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o "%~dp0dist"
