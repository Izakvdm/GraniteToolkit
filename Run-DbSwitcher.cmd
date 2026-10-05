@echo off
rem Builds DB Switcher (Release) and starts it, for testing client databases
rem on this machine without installing the MSI. It asks for administrator
rem rights when it starts (app.manifest). Build-And-Test.cmd is for the
rem whole toolkit; Publish.cmd makes the MSI.
setlocal
cd /d "%~dp0"
echo Building DB Switcher...
dotnet build src\GraniteDbSwitcher\GraniteDbSwitcher.csproj -c Release -nologo -v q
if errorlevel 1 (
  echo.
  echo The build failed. The errors are above.
  pause
  exit /b 1
)
echo Starting DB Switcher...
start "" "%~dp0src\GraniteDbSwitcher\bin\Release\net8.0-windows\GraniteDbSwitcher.exe"
endlocal
