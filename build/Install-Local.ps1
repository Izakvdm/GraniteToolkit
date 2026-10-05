<#
.SYNOPSIS
  Installs (or removes) a GraniteWMS Toolkit MSI on this machine, getting
  past the "Turn off Windows Installer" policy (error 1625) for the length
  of the install only.

.DESCRIPTION
  For your own development machine, where the DisableMSI policy blocks
  installs that weren't deployed through Group Policy or Intune. On a client
  server, ask their IT to deploy the MSI instead (see README "Installing").

  Steps, stopping at the first problem:
    1. Asks for administrator rights if it doesn't have them.
    2. Finds the MSI (the newest in artifacts\release unless -Msi is given)
       and checks it against the SHA256SUMS.txt next to it.
    3. Checks its Authenticode signature: a valid signature is shown, an
       unsigned development build gets a warning, anything else stops.
    4. If DisableMSI is set, remembers its value, sets it to 0 and restarts
       the Windows Installer service (the service keeps the old value until
       it restarts).
    5. Runs msiexec with a verbose log.
    6. Always puts DisableMSI back exactly as it was and restarts the
       service again, even if the install fails or the window is closed.
       The original value is also written to
       HKLM\SOFTWARE\Granite Toolkit\PendingPolicyRestore first, so if the
       script is killed or the machine restarts mid-install, the next run
       restores it before doing anything else.

  Only DisableMSI is touched. If the install is still refused, the script
  reports which other policy is likely responsible (AppLocker, Software
  Restriction Policies) and changes nothing else.

.PARAMETER Msi
  Path to the MSI. Default: GraniteToolkit-<highest version>.msi under
  artifacts\release.

.PARAMETER CoreOnly
  Install without DB Switcher (the client-server feature set). By default
  everything is installed (ADDLOCAL=ALL), since this is for a developer
  machine.

.PARAMETER Uninstall
  Remove the toolkit instead of installing it.

.PARAMETER Quiet
  Basic progress bar only (msiexec /passive) instead of the full installer UI.

.EXAMPLE
  .\Install-Local.cmd
  Installs the newest built MSI with DB Switcher.

.EXAMPLE
  .\Install-Local.cmd -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Msi = '',
    [switch]$CoreOnly,
    [switch]$Uninstall,
    [switch]$Quiet,
    # Set when the script relaunches itself elevated, so its window stays open.
    [switch]$PauseAtEnd
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$policyKey   = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Installer'
$policyValue = 'DisableMSI'
$pendingKey  = 'HKLM:\SOFTWARE\Granite Toolkit\PendingPolicyRestore'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Finish([int]$code) {
    if ($PauseAtEnd) { Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}
# Any unexpected error: say what it was and keep an elevated window open.
# (The finally block below has already put DisableMSI back by then.)
trap {
    Say "Error: $($_.Exception.Message)" 'Red'
    Finish 1
}

# --- 1. Administrator --------------------------------------------------------

$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin   = (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Say 'Asking for administrator rights...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Msi)       { $argList += @('-Msi', "`"$Msi`"") }
    if ($CoreOnly)  { $argList += '-CoreOnly' }
    if ($Uninstall) { $argList += '-Uninstall' }
    if ($Quiet)     { $argList += '-Quiet' }
    $argList += '-PauseAtEnd'
    try {
        $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $p.ExitCode
    }
    catch {
        Say 'Administrator rights were refused, so nothing was installed.' 'Yellow'
        exit 1
    }
}

# --- Policy helpers ----------------------------------------------------------

function Get-PolicyState {
    if (-not (Test-Path $policyKey)) { return [pscustomobject]@{ KeyExists = $false; Exists = $false; Value = $null } }
    $item = Get-ItemProperty -Path $policyKey -Name $policyValue -ErrorAction SilentlyContinue
    if ($null -eq $item) { return [pscustomobject]@{ KeyExists = $true; Exists = $false; Value = $null } }
    return [pscustomobject]@{ KeyExists = $true; Exists = $true; Value = [int]$item.$policyValue }
}

function Restart-Msiserver {
    # The service reads the policy when it starts, so a value change needs a restart.
    $svc = Get-Service -Name msiserver
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name msiserver -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    # msiserver is a demand-start service: msiexec starts it with the current policy.
}

function Save-Pending($state) {
    New-Item -Path $pendingKey -Force | Out-Null
    Set-ItemProperty -Path $pendingKey -Name 'KeyExisted'   -Value ([int]$state.KeyExists)
    Set-ItemProperty -Path $pendingKey -Name 'ValueExisted' -Value ([int]$state.Exists)
    if ($state.Exists) { Set-ItemProperty -Path $pendingKey -Name 'Value' -Value $state.Value -Type DWord }
    Set-ItemProperty -Path $pendingKey -Name 'Saved' -Value (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
}

function Restore-Policy([bool]$keyExisted, [bool]$valueExisted, $value) {
    if ($valueExisted) {
        if (-not (Test-Path $policyKey)) { New-Item -Path $policyKey -Force | Out-Null }
        Set-ItemProperty -Path $policyKey -Name $policyValue -Value ([int]$value) -Type DWord
    }
    else {
        Remove-ItemProperty -Path $policyKey -Name $policyValue -ErrorAction SilentlyContinue
        if (-not $keyExisted -and (Test-Path $policyKey)) {
            $left = Get-Item $policyKey
            if ($left.ValueCount -eq 0 -and $left.SubKeyCount -eq 0) { Remove-Item $policyKey }
        }
    }
    Restart-Msiserver
    Remove-Item -Path $pendingKey -Recurse -Force -ErrorAction SilentlyContinue
}

# A previous run that was killed mid-install: put the policy back first.
if (Test-Path $pendingKey) {
    $p = Get-ItemProperty -Path $pendingKey
    $savedValue = $null
    if ($p.ValueExisted -eq 1) { $savedValue = $p.Value }
    Say "A previous install didn't finish (started $($p.Saved)). Restoring DisableMSI first." 'Yellow'
    Restore-Policy ([bool]$p.KeyExisted) ([bool]$p.ValueExisted) $savedValue
}

# --- 2. Find and check the MSI -----------------------------------------------

$root = Split-Path $PSScriptRoot -Parent
if (-not $Msi) {
    $releaseDir = Join-Path $root 'artifacts\release'
    $candidates = @(Get-ChildItem -Path $releaseDir -Filter 'GraniteToolkit-*.msi' -Recurse -File -ErrorAction SilentlyContinue |
        ForEach-Object {
            $v = $null
            if ($_.BaseName -match '^GraniteToolkit-(\d+\.\d+\.\d+)$' -and [Version]::TryParse($Matches[1], [ref]$v)) {
                [pscustomobject]@{ File = $_; Version = $v }
            }
        } | Sort-Object Version -Descending)
    if ($candidates.Count -eq 0) {
        Say "No GraniteToolkit-x.y.z.msi found under $releaseDir. Run Publish.cmd first, or pass -Msi <path>." 'Red'
        Finish 1
    }
    $Msi = $candidates[0].File.FullName
}
$Msi = (Resolve-Path $Msi).Path
Say "MSI: $Msi" 'Cyan'

$sums = Join-Path (Split-Path $Msi -Parent) 'SHA256SUMS.txt'
if (Test-Path $sums) {
    $name = Split-Path $Msi -Leaf
    $line = Get-Content $sums | ForEach-Object { $_.TrimStart([char]0xFEFF) } | Where-Object { $_ -match "^\s*([0-9a-fA-F]{64})\s+\*?$([regex]::Escape($name))\s*$" } | Select-Object -First 1
    if (-not $line) {
        Say "$name isn't listed in SHA256SUMS.txt. Stopping." 'Red'
        Finish 1
    }
    $expected = ($line -split '\s+')[0].ToLowerInvariant()
    $actual = (Get-FileHash -Path $Msi -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($expected -ne $actual) {
        Say "Checksum mismatch: the MSI isn't the one the build produced. Stopping." 'Red'
        Finish 1
    }
    Say 'Checksum matches SHA256SUMS.txt.' 'Green'
}
else {
    Say 'No SHA256SUMS.txt next to the MSI, so the checksum was not checked.' 'Yellow'
}

# --- 3. Signature ------------------------------------------------------------

$sig = Get-AuthenticodeSignature -FilePath $Msi
switch ($sig.Status) {
    'Valid'     { Say "Signed by: $($sig.SignerCertificate.Subject)" 'Green' }
    'NotSigned' { Say 'Unsigned development build. Fine for this machine; never give it to a client.' 'Yellow' }
    default     { Say "Signature problem ($($sig.Status)): $($sig.StatusMessage). Stopping." 'Red'; Finish 1 }
}

# --- 4-6. Policy, install, restore -------------------------------------------

$logDir = Join-Path $env:ProgramData 'Granite Toolkit\Logs'
if (-not (Test-Path $logDir)) { $logDir = $env:TEMP }
$action = 'install'
if ($Uninstall) { $action = 'uninstall' }
$log = Join-Path $logDir ("msi-$action-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))

$msiArgs = @()
if ($Uninstall) { $msiArgs += @('/x', "`"$Msi`"") }
else {
    $msiArgs += @('/i', "`"$Msi`"")
    if (-not $CoreOnly) { $msiArgs += 'ADDLOCAL=ALL' }
}
if ($Quiet) { $msiArgs += '/passive' }
$msiArgs += @('/l*v', "`"$log`"")

$original = Get-PolicyState
$changed = $false
if ($original.Exists -and $original.Value -ne 0) {
    if ((Get-CimInstance Win32_ComputerSystem).PartOfDomain) {
        Say 'This machine is on a domain, so DisableMSI may come from Group Policy. It is lifted for this install only and put back afterwards.' 'Yellow'
    }
    Say "DisableMSI is $($original.Value). Lifting it for this $action only." 'Yellow'
}

$exitCode = -1
try {
    if ($original.Exists -and $original.Value -ne 0) {
        Save-Pending $original
        Set-ItemProperty -Path $policyKey -Name $policyValue -Value 0 -Type DWord
        $changed = $true
        Restart-Msiserver
    }

    Say "Running: msiexec $($msiArgs -join ' ')"
    $proc = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru
    $exitCode = $proc.ExitCode
}
finally {
    if ($changed) {
        Restore-Policy $original.KeyExists $original.Exists $original.Value
        Say "DisableMSI put back to $($original.Value)." 'Green'
    }
}

# --- Result ------------------------------------------------------------------

switch ($exitCode) {
    0     { Say "Done ($action)." 'Green' }
    3010  { Say "Done ($action). Windows wants a restart to finish." 'Yellow'; $exitCode = 0 }
    1602  { Say 'Cancelled.' 'Yellow' }
    1625  {
        Say 'Windows Installer still refused it (1625), so another policy is blocking it:' 'Red'
        if (Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\SrpV2\Msi') { Say '  - AppLocker has Windows Installer rules (SrpV2\Msi). Check Local Security Policy > Application Control Policies.' }
        if (Test-Path 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Safer\CodeIdentifiers') { Say '  - Software Restriction Policies are configured (Safer\CodeIdentifiers).' }
        Say "  The log says which: search it for 'policy' in $log"
    }
    default { Say "msiexec ended with code $exitCode. Log: $log" 'Red' }
}
Say "Log: $log"
Finish $exitCode
