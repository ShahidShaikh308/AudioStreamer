[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ManifestPath,
    [Parameter(Mandatory = $true)] [string] $SignaturePath,
    [Parameter(Mandatory = $true)] [string] $PackageDirectory,
    [Parameter(Mandatory = $true)] [string] $TrustedSignerThumbprint
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.Pkcs

$manifestFile = (Resolve-Path -LiteralPath $ManifestPath).Path
$signatureFile = (Resolve-Path -LiteralPath $SignaturePath).Path
$packageRoot = (Resolve-Path -LiteralPath $PackageDirectory).Path
$jsonBytes = [IO.File]::ReadAllBytes($manifestFile)
$cms = [System.Security.Cryptography.Pkcs.SignedCms]::new(
    [System.Security.Cryptography.Pkcs.ContentInfo]::new($jsonBytes), $true)
$cms.Decode([IO.File]::ReadAllBytes($signatureFile))
$cms.CheckSignature($true)
if ($cms.SignerInfos.Count -ne 1) { throw 'Manifest must have exactly one signer.' }
$actualSigner = $cms.SignerInfos[0].Certificate.Thumbprint
if ($actualSigner -ne $TrustedSignerThumbprint.Replace(' ', '').ToUpperInvariant()) {
    throw 'Manifest signer does not match the configured trusted publisher thumbprint.'
}

$manifest = [Text.Encoding]::UTF8.GetString($jsonBytes) | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.architecture -ne 'x64') { throw 'Unsupported manifest schema or architecture.' }
if ($manifest.packageFile -notmatch '^[A-Za-z0-9._-]+\.msi$') { throw 'Package filename is not a safe basename.' }
if ($manifest.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Manifest package hash is malformed.' }
if ($manifest.signerThumbprint -ne $TrustedSignerThumbprint.Replace(' ', '').ToUpperInvariant()) {
    throw 'Manifest package signer does not match the configured trusted publisher thumbprint.'
}
$productVersion = [version]::MinValue
$minimumVersion = [version]::MinValue
if (-not [version]::TryParse($manifest.productVersion, [ref]$productVersion) -or
    -not [version]::TryParse($manifest.minimumSupportedVersion, [ref]$minimumVersion) -or
    $minimumVersion -gt $productVersion) { throw 'Manifest version range is invalid.' }
$created = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse($manifest.createdUtc, [ref]$created)) { throw 'Manifest creation time is invalid.' }
$packagePath = Join-Path $packageRoot $manifest.packageFile
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw 'Manifest package is missing.' }
if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ne $manifest.sha256.ToUpperInvariant()) {
    throw 'Package hash does not match the signed manifest.'
}
$authenticode = Get-AuthenticodeSignature -FilePath $packagePath
if ($authenticode.Status -ne 'Valid' -or $authenticode.SignerCertificate.Thumbprint -ne $manifest.signerThumbprint) {
    throw 'Package Authenticode identity does not match the signed manifest.'
}
Write-Output "Verified manifest and package $($manifest.productVersion) signed by $actualSigner."
