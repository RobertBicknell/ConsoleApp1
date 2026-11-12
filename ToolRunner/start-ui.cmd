@echo off
rem Starts the toolrunner UI without Visual Studio, against the real docker/podman on this machine.
rem   start-ui.cmd            uses src\ToolRunner\config
rem   start-ui.cmd D:\cfg     uses that config folder
setlocal
set "HERE=%~dp0"
if "%~1"=="" (set "TOOLRUNNER_CONFIG=%HERE%src\ToolRunner\config") else (set "TOOLRUNNER_CONFIG=%~1")
set "ASPNETCORE_URLS=http://localhost:5180"
start "" http://localhost:5180
dotnet run --project "%HERE%src\ToolRunner.Ui" --no-launch-profile -c Release
