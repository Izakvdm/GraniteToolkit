@echo off
rem Builds a GraniteWMS Toolkit release into artifacts\release\<version>:
rem the MSI, a portable zip and SHA-256 checksums. See build\Publish.ps1.
rem
rem   Publish.cmd                                          development build (unsigned)
rem   Publish.cmd -Sign ArtifactSigning -Publisher "Name"  signed release
rem   Publish.cmd -Sign CertificateStore -CertificateThumbprint <sha1> -TimestampUrl <url> -Publisher "Name"
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Publish.ps1" %*
set RC=%ERRORLEVEL%
if not "%RC%"=="0" (
  echo.
  echo The build failed. The errors are above.
)
pause
exit /b %RC%
