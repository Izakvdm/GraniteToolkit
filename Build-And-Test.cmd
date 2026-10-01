@echo off
rem Builds the whole toolkit (shared core, UI, four modules, harnesses) and
rem runs the LogicHarness checks that need no extra data. The Install and BI
rem harnesses need real release/script folders; see README "Testing".
setlocal
cd /d "%~dp0"
dotnet build GraniteToolkit.sln -c Release -nologo
if errorlevel 1 goto :failed
echo.
echo === Shared core
dotnet run --project tests\Harness.Core -c Release --no-build || goto :failed
echo.
echo === DB Switcher
dotnet run --project tests\Harness.DbSwitcher -c Release --no-build || goto :failed
echo.
echo All checks passed.
pause
exit /b 0
:failed
echo.
echo Something failed. The errors are above.
pause
exit /b 1
