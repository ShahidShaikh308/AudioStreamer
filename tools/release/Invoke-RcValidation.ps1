[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $MsiPath,

    [switch] $InstallAndExercise,
    [switch] $ExerciseCrashRecovery,
    [switch] $LeaveInstalled
)

$ErrorActionPreference = 'Stop'
$serviceName = 'AudioStreamer'
$installerPath = (Resolve-Path -LiteralPath $MsiPath).Path

function Invoke-Msi([string[]] $Arguments) {
    $process = Start-Process -FilePath 'msiexec.exe' -ArgumentList $Arguments -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -notin @(0, 3010)) { throw "Windows Installer failed with exit code $($process.ExitCode)." }
}

if (-not $InstallAndExercise) {
    throw 'Pass -InstallAndExercise to install, validate, and remove the package on this disposable Windows machine.'
}

$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Installer validation requires an elevated PowerShell session.'
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw 'An AudioStreamer service is already installed; validation refuses to replace an existing installation.'
}

$installed = $false
try {
    Invoke-Msi @('/i', "`"$installerPath`"", '/qn', '/norestart')
    $installed = $true

    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    } while (($null -eq $service -or $service.Status -ne 'Running') -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $service) { throw 'Installer did not register the AudioStreamer service.' }
    if ($service.Status -ne 'Running') { throw "AudioStreamer service did not start. Current state: $($service.Status)." }

    $serviceDetails = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
    if ($serviceDetails.StartMode -ne 'Auto') { throw "Expected automatic startup, got '$($serviceDetails.StartMode)'." }
    if ($serviceDetails.StartName -notmatch 'LocalSystem') { throw "Unexpected service account '$($serviceDetails.StartName)'." }

    $recovery = (& sc.exe qfailure $serviceName | Out-String)
    if ($LASTEXITCODE -ne 0 -or $recovery -notmatch 'RESTART') { throw 'SCM restart recovery actions are not configured.' }

    $repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
    Push-Location $repoRoot
    try {
        & dotnet run --project tools/AudioStreamer.ConsoleClient/AudioStreamer.ConsoleClient.csproj --configuration Release --no-build -- --smoke
        if ($LASTEXITCODE -ne 0) { throw 'Named Pipe handshake, ping, snapshot, or subscription smoke check failed.' }
    }
    finally { Pop-Location }

    if ($ExerciseCrashRecovery) {
        $serviceDetails = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
        Stop-Process -Id $serviceDetails.ProcessId -Force
        $deadline = [DateTime]::UtcNow.AddSeconds(75)
        do {
            Start-Sleep -Seconds 1
            $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        } while (($null -eq $service -or $service.Status -ne 'Running') -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $service) { throw 'AudioStreamer service disappeared during recovery.' }
        if ($service.Status -ne 'Running') { throw 'The Service Control Manager did not recover AudioStreamer after forced process termination.' }
    }

    $dataDirectory = Join-Path $env:ProgramData 'AudioStreamer'
    $settingsPath = Join-Path $dataDirectory 'engine-settings.json'
    if (-not (Test-Path -LiteralPath $settingsPath)) { throw 'The Service did not create its machine configuration under ProgramData.' }
    $latestLog = Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Logs') -Filter 'audiostreamer-*.jsonl' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -eq $latestLog) { throw 'No structured JSONL service log was written under ProgramData.' }
    foreach ($line in Get-Content -LiteralPath $latestLog.FullName) {
        if (-not [string]::IsNullOrWhiteSpace($line)) { $null = $line | ConvertFrom-Json }
    }
    Write-Output 'RC package smoke validation passed: install, service startup, SCM recovery configuration, IPC snapshot, and structured logs.'
}
finally {
    if ($installed -and -not $LeaveInstalled) {
        Invoke-Msi @('/x', "`"$installerPath`"", '/qn', '/norestart')
    }
}
