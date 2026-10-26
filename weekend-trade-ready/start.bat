@echo off
cd /d "%~dp0"
echo Restoring NuGet packages...
dotnet restore
if errorlevel 1 pause & exit /b 1

echo.
echo Starting weekend-trade bot...
dotnet run --project weekend-trade.csproj
pause
