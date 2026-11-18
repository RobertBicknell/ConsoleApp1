@echo off
cd /d "%~dp0"
start "" http://localhost:5190
dotnet run --project src\Csv2Xml.TestUi --urls http://localhost:5190
