@echo off
rem Publishes one toolkit module as a portable, self-contained single exe
rem (no .NET install needed on the server), the same way each wizard's own
rem Publish.cmd did before the toolkit.
rem
rem   Publish.cmd             asks which module
rem   Publish.cmd install     GraniteInstallWizard
rem   Publish.cmd bi          GraniteBiDeployWizard
rem   Publish.cmd switcher    GraniteDbSwitcher
rem   Publish.cmd attach      GraniteAttachInstaller
rem   Publish.cmd all         all four
rem
rem Output: dist\<Module>-v<version>.exe
setlocal
set CHOICE=%~1
if "%CHOICE%"=="" (
  echo Which module? install, bi, switcher, attach or all
  set /p CHOICE=^> 
)
if /i "%CHOICE%"=="all" (
  call :publish GraniteInstallWizard || goto :failed
  call :publish GraniteBiDeployWizard || goto :failed
  call :publish GraniteDbSwitcher || goto :failed
  call :publish GraniteAttachInstaller || goto :failed
  goto :done
)
if /i "%CHOICE%"=="install"  call :publish GraniteInstallWizard   || goto :failed
if /i "%CHOICE%"=="bi"       call :publish GraniteBiDeployWizard  || goto :failed
if /i "%CHOICE%"=="switcher" call :publish GraniteDbSwitcher      || goto :failed
if /i "%CHOICE%"=="attach"   call :publish GraniteAttachInstaller || goto :failed
goto :done

:publish
set MOD=%~1
set PROJ=%~dp0src\%MOD%\%MOD%.csproj
set VER=
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content '%PROJ%')).Project.PropertyGroup.Version | Where-Object { $_ }"`) do set VER=%%v
if "%VER%"=="" (
  echo Couldn't read the version from %PROJ%.
  exit /b 1
)
echo.
echo Publishing %MOD% v%VER%...
if exist "%~dp0dist\publish" rmdir /s /q "%~dp0dist\publish"
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -nologo -o "%~dp0dist\publish"
if errorlevel 1 exit /b 1
copy /y "%~dp0dist\publish\%MOD%.exe" "%~dp0dist\%MOD%-v%VER%.exe" >nul
rmdir /s /q "%~dp0dist\publish"
echo Done: dist\%MOD%-v%VER%.exe
exit /b 0

:failed
echo.
echo The publish failed. The errors are above.
pause
exit /b 1

:done
if exist "%~dp0dist" start "" explorer "%~dp0dist"
endlocal
