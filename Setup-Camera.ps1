<#
.SYNOPSIS
Create this PC's Camera bridge configuration without starting the camera or game.
.DESCRIPTION
Discovers runtimes from CAMERA_NODE_PATH, CAMERA_FFMPEG_PATH, CAMERA_PYTHON_PATH
and PATH, then prompts for missing values. Existing configuration is preserved
unless -ReplaceExisting is supplied; replacement creates a backup first.
Use -WhatIf to preview without writing and -NoPrompt for unattended validation.
Setup creates bridge configuration only; it does not download runtimes or edit other applications.
.PARAMETER BridgeRoot
Existing bridge folder where bridge.config.json will be written. Defaults to this script's folder.
.PARAMETER NodePath
Server runtime executable, version 18 or newer. Discovered from the environment or PATH if omitted.
.PARAMETER FFmpegPath
Capture executable. Discovered from the environment or PATH if omitted.
.PARAMETER PythonPath
Helper runtime executable, major version 3, used by the native socket compatibility helper.
.PARAMETER CameraName
Exact capture-device name, for example USB Video Device. Setup does not open the camera.
.PARAMETER SelfAddress
An IPv4 address assigned to an active network adapter on this PC, not loopback.
.PARAMETER Fps
Requested webcam capture rate from 1 through 30. Defaults to 30; the camera must support the mode.
.PARAMETER Port
Loopback HTTP port. Defaults to 80.
.PARAMETER SelfPort
Same-PC camera compatibility port. Defaults to 18080.
.PARAMETER ReplaceExisting
Explicitly allow replacing bridge.config.json, after saving a dated backup.
.PARAMETER NoPrompt
Fail with a useful message instead of asking for missing values.
.EXAMPLE
.\Setup-Camera.ps1
.EXAMPLE
.\Setup-Camera.ps1 -NodePath C:\Tools\node.exe -FFmpegPath C:\Tools\ffmpeg.exe -PythonPath C:\Tools\helper\python.exe -CameraName 'USB Video Device' -SelfAddress 192.168.1.3 -NoPrompt -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    [string]$BridgeRoot = $PSScriptRoot,
    [string]$NodePath,
    [string]$FFmpegPath,
    [string]$PythonPath,
    [string]$CameraName,
    [string]$SelfAddress,
    [ValidateRange(1, 30)][int]$Fps = 30,
    [ValidateRange(1, 65535)][int]$Port = 80,
    [ValidateRange(1, 65535)][int]$SelfPort = 18080,
    [switch]$ReplaceExisting,
    [switch]$NoPrompt
)

$ErrorActionPreference = 'Stop'
$BridgeRoot = (Resolve-Path -LiteralPath $BridgeRoot).ProviderPath
if (-not (Test-Path -LiteralPath $BridgeRoot -PathType Container)) { throw 'BridgeRoot must be an existing folder.' }
$configPath = Join-Path $BridgeRoot 'bridge.config.json'
$existingBytes = $null
if (Test-Path -LiteralPath $configPath) {
    if (-not $ReplaceExisting) { throw "Configuration already exists and was left unchanged: $configPath. Use -ReplaceExisting explicitly to replace it, or add -WhatIf -ReplaceExisting to preview." }
    $existingBytes = [IO.File]::ReadAllBytes($configPath)
}

function Read-Missing([string]$Value, [string]$Parameter, [string]$Prompt) {
    if ([string]::IsNullOrWhiteSpace($Value)) {
        if ($NoPrompt) { throw "Missing -$Parameter. Supply it explicitly when using -NoPrompt." }
        $Value = Read-Host $Prompt
    }
    if ([string]::IsNullOrWhiteSpace($Value)) { throw "-$Parameter cannot be blank." }
    return $Value.Trim()
}

function Resolve-Runtime([string]$Value, [string]$Parameter, [string]$EnvironmentName, [string[]]$Names) {
    if ([string]::IsNullOrWhiteSpace($Value)) { $Value = [Environment]::GetEnvironmentVariable($EnvironmentName) }
    if ([string]::IsNullOrWhiteSpace($Value)) {
        foreach ($name in $Names) {
            $found = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Where-Object { $_.Source -notlike '*\Microsoft\WindowsApps\*' } | Select-Object -First 1
            if ($found) { $Value = $found.Source; break }
        }
    }
    $Value = (Read-Missing $Value $Parameter "Full path to $($Names[0])").Trim('"')
    if ($Value -match '[\x00-\x1f]' -or $Value -like '*\Microsoft\WindowsApps\*') { throw "-$Parameter must name an installed executable, not an application execution alias." }
    if ($Value -match '[/\\]' -and -not [IO.Path]::IsPathRooted($Value)) { $Value = Join-Path $BridgeRoot $Value }
    $command = Get-Command $Value -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $command -or -not (Test-Path -LiteralPath $command.Source -PathType Leaf) -or [IO.Path]::GetExtension($command.Source) -ine '.exe') {
        throw "-$Parameter executable was not found: $Value"
    }
    return (Resolve-Path -LiteralPath $command.Source).ProviderPath
}

function Runtime-Version([string]$Path, [string]$Arguments) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Path; $start.Arguments = $Arguments
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit(); throw "Version check timed out: $Path" }
        $output = ($stdout.Result + "`n" + $stderr.Result).Trim()
        if ($process.ExitCode -ne 0) { throw "Version check failed for $Path (exit $($process.ExitCode)): $($output.Split([char]10)[0])" }
        return $output
    } finally { $process.Dispose() }
}

$localAddresses = @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {
    $_.OperationalStatus -eq [Net.NetworkInformation.OperationalStatus]::Up
} | ForEach-Object { $_.GetIPProperties().UnicastAddresses } | Where-Object {
    $_.Address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork -and -not [Net.IPAddress]::IsLoopback($_.Address)
} | ForEach-Object { $_.Address.IPAddressToString } | Sort-Object -Unique)
if ([string]::IsNullOrWhiteSpace($SelfAddress) -and -not $NoPrompt) {
    Write-Output ('Active IPv4 addresses on this PC: ' + ($localAddresses -join ', '))
    if ($localAddresses.Count -eq 1) {
        $SelfAddress = Read-Host "This PC's camera IPv4 address [$($localAddresses[0])]"
        if ([string]::IsNullOrWhiteSpace($SelfAddress)) { $SelfAddress = $localAddresses[0] }
    }
}
$SelfAddress = Read-Missing $SelfAddress 'SelfAddress' "This PC's IPv4 address"
$parsedAddress = $null
if ($SelfAddress -notmatch '^\d{1,3}(\.\d{1,3}){3}$' -or -not [Net.IPAddress]::TryParse($SelfAddress, [ref]$parsedAddress) -or $localAddresses -notcontains $SelfAddress) {
    throw "-SelfAddress must be an IPv4 address assigned to an active adapter on this PC. Available: $($localAddresses -join ', ')"
}
$NodePath = Resolve-Runtime $NodePath 'NodePath' 'CAMERA_NODE_PATH' @('node.exe')
$FFmpegPath = Resolve-Runtime $FFmpegPath 'FFmpegPath' 'CAMERA_FFMPEG_PATH' @('ffmpeg.exe')
$PythonPath = Resolve-Runtime $PythonPath 'PythonPath' 'CAMERA_PYTHON_PATH' @('python.exe', 'python3.exe')
$nodeVersion = Runtime-Version $NodePath '--version'
if ($nodeVersion -notmatch '^v(\d+)\.' -or [int]$Matches[1] -lt 18) { throw "Server runtime version 18 or newer is required; received: $nodeVersion" }
$pythonVersion = Runtime-Version $PythonPath '--version'
if ($pythonVersion -notmatch '^Python 3\.') { throw "Helper runtime major version 3 is required; received: $pythonVersion" }
$ffmpegVersion = Runtime-Version $FFmpegPath '-version'
if ($ffmpegVersion -notmatch '^ffmpeg version ') { throw 'The capture executable did not report the expected version format.' }
Write-Output "Runtimes verified: server $nodeVersion; $pythonVersion; $($ffmpegVersion.Split([char]10)[0].Trim())"
if ([string]::IsNullOrWhiteSpace($CameraName) -and -not $NoPrompt) {
    Write-Output 'Use the exact capture-device name. To list device names separately, run:'
    Write-Output ('& "' + $FFmpegPath + '" -list_devices true -f dshow -i dummy')
}
$CameraName = Read-Missing $CameraName 'CameraName' 'Webcam capture-device name (for example USB Video Device)'
if ($CameraName -match '[\x00-\x1f\x7f]') { throw '-CameraName must be one line with no control characters.' }
$config = [ordered]@{
    nodePath = $NodePath; ffmpegPath = $FFmpegPath; pythonPath = $PythonPath
    cameraName = $CameraName; port = $Port; selfAddress = $SelfAddress; selfPort = $SelfPort
    eofCompat = $true; fps = $Fps; jpegQuality = 4; mirror = $false
}
$json = ($config | ConvertTo-Json) + "`r`n"
$null = $json | ConvertFrom-Json
Write-Output "Configuration: $configPath"
Write-Output $json
Write-Output "Camera endpoint for manual client routing: ${SelfAddress}:$SelfPort"
Write-Output 'Keep existing network routes. Setup creates bridge configuration only.'
if (-not $PSCmdlet.ShouldProcess($configPath, 'Write Camera bridge configuration')) { return }

function Same-Bytes([byte[]]$Left, [byte[]]$Right) {
    return [Convert]::ToBase64String($Left) -ceq [Convert]::ToBase64String($Right)
}

$backup = $null
if ($null -ne $existingBytes) {
    if (-not (Same-Bytes ([IO.File]::ReadAllBytes($configPath)) $existingBytes)) { throw 'Configuration changed while setup was running; nothing was replaced.' }
    $backupDirectory = Join-Path $BridgeRoot 'diagnostics'
    $null = [IO.Directory]::CreateDirectory($backupDirectory)
    $backup = Join-Path $backupDirectory ('bridge.config-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.json.backup')
    if (Test-Path -LiteralPath $backup) { throw "Backup already exists: $backup" }
    [IO.File]::WriteAllBytes($backup, $existingBytes)
    if (-not (Same-Bytes ([IO.File]::ReadAllBytes($backup)) $existingBytes)) { throw 'Configuration backup verification failed.' }
}
$temporary = $configPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    if ($null -ne $existingBytes) {
        if (-not (Same-Bytes ([IO.File]::ReadAllBytes($configPath)) $existingBytes)) { throw 'Configuration changed before replacement; nothing was replaced.' }
        [IO.File]::Replace($temporary, $configPath, [NullString]::Value)
    } else { [IO.File]::Move($temporary, $configPath) }
} finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
Write-Output "Saved $configPath"
if ($backup) { Write-Output "Previous configuration backup: $backup" }
Write-Output 'Setup complete. Start Camera.cmd can start the camera when you are ready.'
