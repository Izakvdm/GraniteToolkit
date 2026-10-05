<#
.SYNOPSIS
  Builds a GraniteWMS Toolkit release: tests, publish, signing, MSI,
  portable zip and SHA-256 checksums.

.DESCRIPTION
  Steps, stopping at the first failure:
    1. Checks: .NET SDK, clean git tree (release only), vulnerable packages.
    2. Build and run the logic harnesses that need no extra data.
    3. Publish the launcher and the five modules, self-contained for win-x64,
       each on its own, then merge them into one folder. Two modules
       shipping the same file with different contents stops the build.
    4. Sign every exe and dll not already signed (Microsoft's runtime files
       keep Microsoft's signature) and verify every signature.
    5. Build the MSI from the signed files, sign it, verify it.
    6. Portable zip (without the developer tools), SHA256SUMS.txt and
       build-info.json.

  Output: artifacts\release\<version>\

.PARAMETER Sign
  None (development build, no signing), ArtifactSigning or CertificateStore.
  See build\Signing.psm1.

.PARAMETER Publisher
  The company name shown as the MSI's Manufacturer in Programs and
  Features. Required for a signed release; it should match the signing
  certificate's organisation.

.EXAMPLE
  .\build\Publish.ps1
  Development build: unsigned, tests, MSI and zip.

.EXAMPLE
  .\build\Publish.ps1 -Sign ArtifactSigning -Publisher "Your Company Ltd"
  Signed release with Azure Artifact Signing (build\signing.json).
#>
[CmdletBinding()]
param(
    [ValidateSet('None', 'ArtifactSigning', 'CertificateStore')]
    [string]$Sign = 'None',
    [string]$Publisher = '',
    [string]$CertificateThumbprint = '',
    [string]$TimestampUrl = '',
    [string]$SignToolPath = '',
    [string]$SigningConfig = '',
    [switch]$SkipMsi,
    [switch]$SkipTests,
    [switch]$AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'Signing.psm1') -Force
if (-not $SigningConfig) { $SigningConfig = Join-Path $PSScriptRoot 'signing.json' }

$apps = @('GraniteToolkit', 'GraniteInstallWizard', 'GraniteBiDeployWizard', 'GraniteNiFiDeploy', 'GraniteDbSwitcher')
$developerOnly = @('GraniteDbSwitcher')
$release = $Sign -ne 'None'

function Step($text) { Write-Host ''; Write-Host "=== $text" -ForegroundColor Cyan }
function Invoke-Checked {
    param([string]$What, [scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE)." }
}

# ---------------------------------------------------------------- 1. checks
Step 'Checks'
$csproj = [xml](Get-Content (Join-Path $root 'src\GraniteToolkit\GraniteToolkit.csproj'))
$version = @($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'No <Version> in src\GraniteToolkit\GraniteToolkit.csproj.' }
Write-Host "Toolkit version $version, signing: $Sign"

if ($release -and -not $Publisher) { throw '-Publisher is required for a signed release (the MSI Manufacturer; match the certificate''s organisation).' }
if (-not $Publisher) { $Publisher = 'Development build' }

$commit = 'unknown'
if (Get-Command git -ErrorAction SilentlyContinue) {
    Push-Location $root
    try {
        $commit = (git rev-parse --short HEAD).Trim()
        $dirty = git status --porcelain
        if ($dirty -and $release -and -not $AllowDirty) {
            throw "Uncommitted changes. A signed release must be built from a commit, so it can be rebuilt and audited later (or pass -AllowDirty):`n$($dirty -join "`n")"
        }
        if ($dirty) { $commit += '-dirty'; Write-Warning 'Building with uncommitted changes.' }
    } finally { Pop-Location }
}

Invoke-Checked 'dotnet restore' { dotnet restore (Join-Path $root 'GraniteToolkit.sln') -r win-x64 --nologo }
$vulnerable = dotnet list (Join-Path $root 'GraniteToolkit.sln') package --vulnerable --include-transitive
$vulnerable | Out-Host
if ($LASTEXITCODE -ne 0) {
    if ($release) { throw 'Could not check packages for known vulnerabilities (is nuget.org reachable?).' }
    Write-Warning 'Could not check packages for known vulnerabilities.'
} elseif ($vulnerable -match 'has the following vulnerable packages') {
    throw 'A package with a known vulnerability is referenced (see above). Update it in Directory.Packages.props.'
}

# ---------------------------------------------------------------- 2. tests
if (-not $SkipTests) {
    Step 'Tests'
    Invoke-Checked 'build' { dotnet build (Join-Path $root 'GraniteToolkit.sln') -c Release --nologo }
    foreach ($h in 'Harness.Core', 'Harness.Launcher', 'Harness.DbSwitcher', 'Harness.NiFiDeploy') {
        Invoke-Checked $h { dotnet run --project (Join-Path $root "tests\$h") -c Release --no-build }
    }
} elseif ($release) {
    throw '-SkipTests is not allowed for a signed release.'
}

# ---------------------------------------------------------------- 3. publish
Step 'Publish'
$out = Join-Path $root "artifacts\release\$version"
$work = Join-Path $root 'artifacts\work'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$appDir = Join-Path $work 'app'
New-Item -ItemType Directory -Force -Path $appDir, $out | Out-Null

foreach ($app in $apps) {
    Invoke-Checked "publish $app" {
        dotnet publish (Join-Path $root "src\$app\$app.csproj") -c Release -r win-x64 --self-contained true `
            -p:PublishSingleFile=false -p:ContinuousIntegrationBuild=true -p:DebugType=none `
            -o (Join-Path $work "publish\$app") --nologo
    }
}

# Merge, refusing any file two modules ship with different contents.
$owners = @{}
foreach ($app in $apps) {
    $from = Join-Path $work "publish\$app"
    foreach ($file in Get-ChildItem $from -Recurse -File) {
        $rel = $file.FullName.Substring($from.Length + 1)
        $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        if ($owners.ContainsKey($rel)) {
            if ($owners[$rel].Hash -ne $hash) { throw "$rel differs between $($owners[$rel].App) and $app. Pin the package version in Directory.Packages.props." }
            continue
        }
        $owners[$rel] = @{ App = $app; Hash = $hash }
        $target = Join-Path $appDir $rel
        New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
        Copy-Item $file.FullName $target
    }
}
Write-Host "  $($owners.Count) files in the merged app folder."

# Zips a folder. Compress-Archive gives up on the first file another process
# has open (Defender scanning a just-copied DLL, the search indexer), so
# each file is opened for shared reading and retried for a few seconds.
function New-ZipFromFolder([string]$Folder, [string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $zipStream = [System.IO.File]::Open($ZipPath, [System.IO.FileMode]::CreateNew)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem $Folder -Recurse -File | Sort-Object FullName) {
                $entryName = $file.FullName.Substring($Folder.Length + 1).Replace('\', '/')
                $source = $null
                for ($attempt = 1; $null -eq $source; $attempt++) {
                    try {
                        $source = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read,
                            [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
                    } catch {
                        if ($attempt -ge 20) { throw "Couldn't read $($file.FullName) for the zip: $($_.Exception.Message)" }
                        Start-Sleep -Milliseconds 500
                    }
                }
                try {
                    $entry = $archive.CreateEntry($entryName, [System.IO.Compression.CompressionLevel]::Optimal)
                    $entry.LastWriteTime = $file.LastWriteTime
                    $target = $entry.Open()
                    try { $source.CopyTo($target) } finally { $target.Dispose() }
                } finally { $source.Dispose() }
            }
        } finally { $archive.Dispose() }
    } finally { $zipStream.Dispose() }
}

# Files that belong to the developer-only feature: the module's own files.
function Test-DeveloperFile([string]$rel) {
    foreach ($d in $developerOnly) { if ($rel -like "$d.*") { return $true } }
    return $false
}

# ---------------------------------------------------------------- 4. sign
$pe = @(Get-ChildItem $appDir -Recurse -File -Include *.exe, *.dll | ForEach-Object { $_.FullName })
if ($release) {
    Step 'Sign'
    $signTool = Find-SignTool $SignToolPath
    $signArgs = Get-SignArguments -Mode $Sign -SigningConfig $SigningConfig -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl
    $signed = Invoke-CodeSigning -Files $pe -SignTool $signTool -SignArgs $signArgs
    $signer = Test-ReleaseSignatures -Files $pe -OurFiles $signed
    Write-Host "  All $($pe.Count) exe/dll files verified. Publisher: $signer"
} else {
    $signer = $null
    Write-Warning 'Development build: nothing is signed. Don''t give this build to clients.'
}

# Checksums of the installed files (shipped inside the MSI and the zip).
$appSums = Get-ChildItem $appDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($appDir.Length + 1)
}
Set-Content -Path (Join-Path $appDir 'SHA256SUMS.txt') -Value $appSums -Encoding UTF8

# ---------------------------------------------------------------- 5. MSI
if (-not $SkipMsi) {
    Step 'MSI'
    # Explicit file list for WiX: one component per file, the developer
    # tools' files in their own feature.
    $core = New-Object System.Text.StringBuilder
    $dev = New-Object System.Text.StringBuilder
    $dirs = @{}
    $n = 0
    foreach ($file in Get-ChildItem $appDir -Recurse -File | Sort-Object FullName) {
        $rel = $file.FullName.Substring($appDir.Length + 1)
        $sub = Split-Path $rel
        $dirRef = 'INSTALLFOLDER'
        if ($sub) {
            if ($sub.Contains('\')) { throw "Nested folder '$sub' in the publish output: the MSI file list only handles one folder level (the runtime's language folders). Extend Publish.ps1 before shipping this." }
            $dirRef = 'dir_' + (($sub -replace '[^A-Za-z0-9]', '_'))
            $dirs[$dirRef] = $sub
        }
        $n++
        $id = "f$n"
        $source = [System.Security.SecurityElement]::Escape($file.FullName)
        if ($rel -eq 'GraniteToolkit.exe') {
            # The Start menu shortcut hangs off the exe's own component, so the
            # component's key path is the file in Program Files (a separate
            # shortcut component fails ICE38/43/57 in a per-machine package).
            $line = "      <Component Directory=`"$dirRef`"><File Id=`"GraniteToolkitExe`" Source=`"$source`" KeyPath=`"yes`">" +
                    "<Shortcut Id=`"ToolkitShortcut`" Directory=`"ProgramMenuFolder`" Name=`"GraniteWMS Toolkit`" Description=`"Dashboard and installers for GraniteWMS`" WorkingDirectory=`"INSTALLFOLDER`" Icon=`"GraniteToolkit.ico`" Advertise=`"no`" />" +
                    "</File></Component>"
        } else {
            $line = "      <Component Directory=`"$dirRef`"><File Id=`"$id`" Source=`"$source`" KeyPath=`"yes`" /></Component>"
        }
        if (-not $sub -and (Test-DeveloperFile $rel)) { [void]$dev.AppendLine($line) } else { [void]$core.AppendLine($line) }
    }
    $dirXml = New-Object System.Text.StringBuilder
    foreach ($k in $dirs.Keys | Sort-Object) {
        [void]$dirXml.AppendLine("    <DirectoryRef Id=`"INSTALLFOLDER`"><Directory Id=`"$k`" Name=`"$([System.Security.SecurityElement]::Escape($dirs[$k]))`" /></DirectoryRef>")
    }
    $appFilesWxs = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- Generated by build\Publish.ps1 from the signed app folder. Don't edit. -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Fragment>
$dirXml
    <ComponentGroup Id="CoreFiles">
$core    </ComponentGroup>
    <ComponentGroup Id="DeveloperFiles">
$dev    </ComponentGroup>
  </Fragment>
</Wix>
"@
    $generated = Join-Path $work 'AppFiles.wxs'
    Set-Content -Path $generated -Value $appFilesWxs -Encoding UTF8

    $msiOut = Join-Path $work 'msi'
    Invoke-Checked 'MSI build' {
        dotnet build (Join-Path $root 'installer\GraniteToolkit.Installer.wixproj') -c Release --nologo `
            -p:ProductVersion=$version -p:Manufacturer="$Publisher" -p:AppFilesWxs="$generated" `
            -p:IconPath="$(Join-Path $root 'src\GraniteToolkit\Assets\GraniteToolkit.ico')" -o $msiOut
    }
    $msi = Get-ChildItem $msiOut -Filter *.msi | Select-Object -First 1
    if (-not $msi) { throw "No MSI produced in $msiOut." }
    $msiTarget = Join-Path $out "GraniteToolkit-$version.msi"
    Copy-Item $msi.FullName $msiTarget

    if ($release) {
        $null = Invoke-CodeSigning -Files @($msiTarget) -SignTool $signTool -SignArgs $signArgs
        $msiSigner = Test-ReleaseSignatures -Files @($msiTarget) -OurFiles @($msiTarget)
        if ($msiSigner -ne $signer) { throw "The MSI is signed by '$msiSigner', the files by '$signer'." }
    }
}

# ---------------------------------------------------------------- 6. zip, sums, info
Step 'Portable zip and checksums'
$zipSource = Join-Path $work 'portable'
New-Item -ItemType Directory -Force -Path $zipSource | Out-Null
Get-ChildItem $appDir -Force | Where-Object { -not (Test-DeveloperFile $_.Name) -and $_.Name -ne 'SHA256SUMS.txt' } |
    Copy-Item -Destination $zipSource -Recurse
$zipSums = Get-ChildItem $zipSource -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.FullName.Substring($zipSource.Length + 1)
}
Set-Content -Path (Join-Path $zipSource 'SHA256SUMS.txt') -Value $zipSums -Encoding UTF8
$zip = Join-Path $out "GraniteToolkit-$version-portable.zip"
New-ZipFromFolder -Folder $zipSource -ZipPath $zip

$releaseSums = Get-ChildItem $out -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content -Path (Join-Path $out 'SHA256SUMS.txt') -Value $releaseSums -Encoding UTF8

[ordered]@{
    product   = 'GraniteWMS Toolkit'
    version   = $version
    commit    = $commit
    built     = (Get-Date).ToString('o')
    builtBy   = "$env:USERDOMAIN\$env:USERNAME"
    signing   = $Sign
    publisher = $signer
    sdk       = (dotnet --version)
} | ConvertTo-Json | Set-Content -Path (Join-Path $out 'build-info.json') -Encoding UTF8

Step 'Done'
Get-ChildItem $out | Format-Table Name, Length -AutoSize | Out-Host
Write-Host "Release folder: $out"
