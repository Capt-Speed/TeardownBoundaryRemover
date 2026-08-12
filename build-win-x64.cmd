@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK was not found.
  echo Install .NET 8 SDK from Microsoft, then run this file again.
  pause
  exit /b 1
)

dotnet publish src\TeardownBoundaryRemover\TeardownBoundaryRemover.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish\win-x64
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)

echo.
echo Build completed:
echo %~dp0publish\win-x64\TeardownBoundaryRemover.exe
endlocal
