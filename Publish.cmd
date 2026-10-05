@echo off
rem Builds a GraniteWMS Toolkit release into artifacts\release\<version>:
rem the MSI, a portable zip and SHA-256 checksums. See build\Publish.ps1.
rem
rem   Publish.cmd                                          development build (unsigned)
rem   Publish.cmd -Sign ArtifactSigning -Publisher "Name"  signed release
rem   Publish.cmd -Sign CertificateStore -CertificateThumbprint <sha1> -TimestampUrl <url> -Publisher "Name"
setlocal
rem PowerShell 7 passes its PSModulePath down to Windows PowerShell 5.1, which
rem then can't load its own modules (Get-FileHash went missing on Ultra).
rem Clearing it here makes 5.1 use its defaults.
set "PSModulePath="
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Publish.ps1" %*
set RC=%ERRORLEVEL%
if not "%RC%"=="0" (
  echo.
  echo The build failed. The errors are above.
)
pause
exit /b %RC%
