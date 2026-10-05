@echo off
rem Builds the whole toolkit and runs the logic harnesses that need no extra
rem data. The Install and BI harnesses need real release/script folders;
rem see README "Testing".
setlocal
cd /d "%~dp0"
dotnet build GraniteToolkit.sln -c Release -nologo
if errorlevel 1 goto :failed
for %%h in (Harness.Core Harness.Launcher Harness.DbSwitcher Harness.NiFiDeploy) do (
  echo.
  echo === %%h
  dotnet run --project tests\%%h -c Release --no-build || goto :failed
)
echo.
echo All checks passed.
pause
exit /b 0
:failed
echo.
echo Something failed. The errors are above.
pause
exit /b 1
