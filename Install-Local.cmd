@echo off
rem Installs the newest built GraniteWMS Toolkit MSI on this machine, with
rem DB Switcher, getting past the "Turn off Windows Installer" policy
rem (error 1625) for the length of the install only. DisableMSI is put
rem back exactly as it was afterwards. See build\Install-Local.ps1.
rem
rem   Install-Local.cmd                 newest MSI, everything (ADDLOCAL=ALL)
rem   Install-Local.cmd -CoreOnly       without DB Switcher
rem   Install-Local.cmd -Msi <path>     a specific MSI
rem   Install-Local.cmd -Uninstall      remove the toolkit
setlocal
rem PowerShell 7 passes its PSModulePath down to Windows PowerShell 5.1, which
rem then can't load its own modules (Get-FileHash went missing on Ultra).
rem Clearing it here makes 5.1 use its defaults.
set "PSModulePath="
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\Install-Local.ps1" %*
set RC=%ERRORLEVEL%
pause
exit /b %RC%
