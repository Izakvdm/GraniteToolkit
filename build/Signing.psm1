# Code signing for the GraniteWMS Toolkit release build.
#
# Two ways to sign, chosen in Publish.ps1 with -Sign:
#
#   ArtifactSigning   Microsoft's Azure Artifact Signing (formerly Trusted
#                     Signing). Needs build\signing.json (copy
#                     signing.example.json), signtool 10.0.2261.755 or later,
#                     and the Microsoft.ArtifactSigning.Client dlib. Sign in
#                     first with "az login" (or any DefaultAzureCredential
#                     source). The private key never leaves Microsoft's HSM.
#
#   CertificateStore  A code signing certificate in the Windows certificate
#                     store, e.g. one on a hardware token, picked by its
#                     SHA-1 thumbprint (-CertificateThumbprint).
#
# Every signature is timestamped (RFC 3161, SHA-256), so files stay valid
# after the signing certificate expires. Nothing secret is kept in the repo:
# signing.json only names the account and profile, and is git-ignored.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Find-SignTool {
    param([string]$Explicit)
    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "signtool not found at $Explicit" }
        return (Resolve-Path $Explicit).Path
    }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (-not (Test-Path $kits)) { throw "signtool.exe not found. Install the Windows SDK (Signing Tools) or pass -SignToolPath." }
    $candidates = Get-ChildItem $kits -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path $_ }
    $first = $candidates | Select-Object -First 1
    if (-not $first) { throw "signtool.exe not found under $kits. Install the Windows SDK (Signing Tools) or pass -SignToolPath." }
    return $first
}

function Get-SignArguments {
    param(
        [ValidateSet('ArtifactSigning', 'CertificateStore')][string]$Mode,
        [string]$SigningConfig,
        [string]$CertificateThumbprint,
        [string]$TimestampUrl
    )
    $common = @('sign', '/fd', 'SHA256', '/td', 'SHA256')
    if ($Mode -eq 'ArtifactSigning') {
        if (-not (Test-Path $SigningConfig)) { throw "Signing config not found: $SigningConfig (copy build\signing.example.json to build\signing.json and fill it in)." }
        $cfg = Get-Content $SigningConfig -Raw | ConvertFrom-Json
        foreach ($key in 'DlibPath', 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
            if (-not $cfg.$key) { throw "$SigningConfig is missing '$key'." }
        }
        if (-not (Test-Path $cfg.DlibPath)) { throw "Artifact Signing dlib not found at $($cfg.DlibPath). It comes in the Microsoft.ArtifactSigning.Client NuGet package (x64 folder)." }

        # signtool reads the account details from a metadata file; write one
        # next to the config with only the keys it expects.
        $metadata = [ordered]@{
            Endpoint               = $cfg.Endpoint
            CodeSigningAccountName = $cfg.CodeSigningAccountName
            CertificateProfileName = $cfg.CertificateProfileName
        }
        if ($cfg.PSObject.Properties.Name -contains 'ExcludeCredentials') { $metadata.ExcludeCredentials = $cfg.ExcludeCredentials }
        $metadataPath = Join-Path (Split-Path $SigningConfig) 'signing.metadata.json'
        $metadata | ConvertTo-Json | Set-Content -Path $metadataPath -Encoding UTF8

        $ts = $TimestampUrl
        if (-not $ts) { $ts = 'http://timestamp.acs.microsoft.com' }
        return $common + @('/tr', $ts, '/dlib', $cfg.DlibPath, '/dmdf', $metadataPath)
    }

    if (-not $CertificateThumbprint) { throw "-CertificateThumbprint is required with -Sign CertificateStore." }
    $cert = Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My |
        Where-Object { $_.Thumbprint -eq $CertificateThumbprint.Replace(' ', '').ToUpperInvariant() } |
        Select-Object -First 1
    if (-not $cert) { throw "No certificate with thumbprint $CertificateThumbprint in CurrentUser\My or LocalMachine\My." }
    if (-not ($cert.EnhancedKeyUsageList | Where-Object { $_.ObjectId -eq '1.3.6.1.5.5.7.3.3' })) { throw "Certificate $CertificateThumbprint isn't a code signing certificate." }
    if (-not $TimestampUrl) { throw "-TimestampUrl is required with -Sign CertificateStore (your certificate authority's RFC 3161 timestamp server)." }
    return $common + @('/tr', $TimestampUrl, '/sha1', $cert.Thumbprint)
}

# Signs the files that aren't already validly signed (Microsoft's runtime
# files already carry Microsoft's signature and are left as they are), a
# batch at a time. Returns the files it signed.
function Invoke-CodeSigning {
    param(
        [string[]]$Files,
        [string]$SignTool,
        [string[]]$SignArgs
    )
    $toSign = @($Files | Where-Object { (Get-AuthenticodeSignature -FilePath $_).Status -ne 'Valid' })
    Write-Host "  $($toSign.Count) of $($Files.Count) files need signing."
    for ($i = 0; $i -lt $toSign.Count; $i += 50) {
        $batch = $toSign[$i..([Math]::Min($i + 49, $toSign.Count - 1))]
        & $SignTool @SignArgs @batch | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit code $LASTEXITCODE)." }
    }
    return ,$toSign
}

# Every PE file must be validly signed, and every file we signed must carry
# the same publisher. Returns that publisher's subject.
function Test-ReleaseSignatures {
    param([string[]]$Files, [string[]]$OurFiles)
    $publisher = $null
    $bad = @()
    foreach ($f in $Files) {
        $sig = Get-AuthenticodeSignature -FilePath $f
        if ($sig.Status -ne 'Valid') { $bad += "$f : $($sig.Status) $($sig.StatusMessage)"; continue }
        if ($OurFiles -contains $f) {
            if (-not $sig.TimeStamperCertificate) { $bad += "$f : signed without a timestamp" }
            $subject = $sig.SignerCertificate.Subject
            if ($null -eq $publisher) { $publisher = $subject }
            elseif ($publisher -ne $subject) { $bad += "$f : signed by '$subject', expected '$publisher'" }
        }
    }
    if ($bad.Count -gt 0) { throw ("Signature check failed:`n  " + ($bad -join "`n  ")) }
    return $publisher
}

Export-ModuleMember -Function Find-SignTool, Get-SignArguments, Invoke-CodeSigning, Test-ReleaseSignatures
