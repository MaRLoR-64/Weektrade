@echo off
cd /d "%~dp0"
dotnet restore
dotnet run --project weekend-trade.csproj
pause
