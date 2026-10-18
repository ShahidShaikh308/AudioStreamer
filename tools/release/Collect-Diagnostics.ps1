[CmdletBinding()]
param(
    [string] $OutputPath = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'AudioStreamer-Diagnostics.zip'),
    [int] $MaximumLogFiles = 5
)

$ErrorActionPreference = 'Stop'
if ($MaximumLogFiles -lt 1 -or $MaximumLogFiles -gt 20) { throw 'MaximumLogFiles must be between 1 and 20.' }
$dataDirectory = Join-Path $env:ProgramData 'AudioStreamer'
$logDirectory = Join-Path $dataDirectory 'Logs'
$output = [IO.Path]::GetFullPath($OutputPath)
$staging = Join-Path ([IO.Path]::GetTempPath()) ("AudioStreamer-Diagnostics-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

try {
    $service = Get-Service -Name AudioStreamer -ErrorAction SilentlyContinue
    $details = if ($null -ne $service) { Get-CimInstance Win32_Service -Filter "Name='AudioStreamer'" } else { $null }
    $configPath = Join-Path $dataDirectory 'engine-settings.json'
    $config = if (Test-Path -LiteralPath $configPath) { Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } else { $null }
    $report = [ordered]@{
        collectedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        product = 'AudioStreamer'
        osVersion = [Environment]::OSVersion.Version.ToString()
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        service = if ($null -eq $service) { $null } else { [ordered]@{ status = $service.Status.ToString(); startMode = $details.StartMode; account = $details.StartName; processId = $details.ProcessId } }
        configuration = if ($null -eq $config) { $null } else { [ordered]@{ schemaVersion = $config.SchemaVersion; revision = $config.Revision; nodeIdPresent = -not [string]::IsNullOrWhiteSpace([string]$config.NodeId); nodeNamePresent = -not [string]::IsNullOrWhiteSpace([string]$config.NodeName) } }
        logs = @()
    }

    $logs = if (Test-Path -LiteralPath $logDirectory) {
        Get-ChildItem -LiteralPath $logDirectory -Filter 'audiostreamer-*.jsonl' -File |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First $MaximumLogFiles
    } else { @() }
    foreach ($file in $logs) {
        if ($file.Length -gt 8MB) { continue }
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            if ($report.logs.Count -ge 5000) { break }
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            try { $entry = $line | ConvertFrom-Json } catch { continue }
            $report.logs += [ordered]@{
                timestampUtc = $entry.TimestampUtc
                level = $entry.Level
                category = $entry.Category
                eventId = $entry.EventId
                exceptionType = $entry.ExceptionType
                propertyNames = @($entry.Properties.PSObject.Properties.Name)
            }
        }
    }

    $jsonPath = Join-Path $staging 'diagnostics.json'
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding utf8
    $parent = Split-Path -Parent $output
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent | Out-Null }
    if (Test-Path -LiteralPath $output) { throw "Output already exists; choose a new path: $output" }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $output -CompressionLevel Optimal
    Write-Output "Created privacy-bounded diagnostics bundle: $output"
}
finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
