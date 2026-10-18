@echo off
cd /d "%~dp0"
set "DOTNET_CLI_HOME=%~dp0.tools\cli"
set "NUGET_PACKAGES=%~dp0.tools\packages"
if exist "%~dp0.tools\dotnet\dotnet.exe" (
  "%~dp0.tools\dotnet\dotnet.exe" run --project "%~dp0src\DbStudio\DbStudio.csproj"
) else (
  dotnet run --project "%~dp0src\DbStudio\DbStudio.csproj"
)
if errorlevel 1 pause
