[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $MsiPath,
    [Parameter(Mandatory = $true)] [string] $ProductVersion,
    [Parameter(Mandatory = $true)] [string] $MinimumSupportedVersion,
    [Parameter(Mandatory = $true)] [string] $CertificatePath,
    [Parameter(Mandatory = $true)] [securestring] $CertificatePassword,
    [Parameter(Mandatory = $true)] [string] $OutputDirectory,
    [string] $RollbackMsiPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.Pkcs

function Get-ValidatedVersion([string] $Value, [string] $Name) {
    $parsed = [version]::MinValue
    if (-not [version]::TryParse($Value, [ref] $parsed) -or $parsed.Revision -ne -1) {
        throw "$Name must use numeric major.minor.patch format."
    }
    return $parsed.ToString(3)
}

$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$signature = Get-AuthenticodeSignature -FilePath $msi
if ($signature.Status -ne 'Valid') { throw "MSI Authenticode signature is not valid: $($signature.Status)." }
$certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path -LiteralPath $CertificatePath).Path, $CertificatePassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
if ($certificate.Thumbprint -ne $signature.SignerCertificate.Thumbprint) {
    throw 'The signing certificate does not match the MSI Authenticode signer.'
}
if (-not $certificate.HasPrivateKey) { throw 'The manifest signing certificate has no private key.' }

$version = Get-ValidatedVersion $ProductVersion 'ProductVersion'
$minimum = Get-ValidatedVersion $MinimumSupportedVersion 'MinimumSupportedVersion'
$rollbackName = $null
if ($RollbackMsiPath) {
    $rollback = (Resolve-Path -LiteralPath $RollbackMsiPath).Path
    if ((Get-AuthenticodeSignature -FilePath $rollback).Status -ne 'Valid') { throw 'Rollback MSI must have a valid Authenticode signature.' }
    $rollbackName = [IO.Path]::GetFileName($rollback)
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$manifest = [ordered]@{
    schemaVersion = 1
    productVersion = $version
    minimumSupportedVersion = $minimum
    architecture = 'x64'
    packageFile = [IO.Path]::GetFileName($msi)
    sha256 = (Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant()
    signerThumbprint = $certificate.Thumbprint.ToUpperInvariant()
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    rollbackPackageFile = $rollbackName
}
$json = $manifest | ConvertTo-Json -Depth 4
$manifestPath = Join-Path $OutputDirectory 'audiostreamer-update.json'
$signaturePath = "$manifestPath.p7s"
$contentBytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
$contentInfo = [System.Security.Cryptography.Pkcs.ContentInfo]::new($contentBytes)
$signedCms = [System.Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $true)
$cmsSigner = [System.Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
$cmsSigner.IncludeOption = [System.Security.Cryptography.X509Certificates.X509IncludeOption]::EndCertOnly
$signedCms.ComputeSignature($cmsSigner)
[IO.File]::WriteAllBytes($manifestPath, $contentBytes)
[IO.File]::WriteAllBytes($signaturePath, $signedCms.Encode())
Write-Output "Created detached-signed update metadata: $manifestPath"
