param([switch]$NoBrowser)
$ErrorActionPreference = 'Stop'
$bridgeRoot = $PSScriptRoot
$bridgeConfig = Get-Content -LiteralPath (Join-Path $bridgeRoot 'bridge.config.json') -Raw | ConvertFrom-Json
$bridgeUrl = 'http://127.0.0.1:' + $bridgeConfig.port
$existingBridge = $null
try { $existingBridge = Invoke-RestMethod -Uri "$bridgeUrl/health" -TimeoutSec 2 } catch { }
for ($attempt = 0; $existingBridge -and $existingBridge.app -eq 'camera-bridge' -and $existingBridge.status -eq 'stopping' -and $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 200
    $existingBridge = $null
    try { $existingBridge = Invoke-RestMethod -Uri "$bridgeUrl/health" -TimeoutSec 1 } catch { }
}
if ($existingBridge -and $existingBridge.status -eq 'stopping') { throw 'Camera is still stopping. Wait a moment and try again.' }
if ($existingBridge -and $existingBridge.app -ne 'camera-bridge') {
    throw "Another service is using $bridgeUrl. Check bridge.config.json and the client camera route."
}
$bridgeProcess = $null
if (-not $existingBridge) {
    $nodeCandidate = if ($bridgeConfig.nodePath) { [string]$bridgeConfig.nodePath } elseif ($env:CAMERA_NODE_PATH) { $env:CAMERA_NODE_PATH } else { 'node.exe' }
    if ($nodeCandidate -match '[/\\]' -and -not [IO.Path]::IsPathRooted($nodeCandidate)) { $nodeCandidate = Join-Path $bridgeRoot $nodeCandidate }
    $nodeCommand = Get-Command $nodeCandidate -CommandType Application -ErrorAction Stop
    $bridgeProcess = Start-Process -FilePath $nodeCommand.Source -ArgumentList ('"' + (Join-Path $bridgeRoot 'server.js') + '"') -WorkingDirectory $bridgeRoot -WindowStyle Hidden -PassThru
}
$bridgeReady = $false
$bridgeStatus = $existingBridge
$bridgeDeadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    if ($bridgeProcess -and $bridgeProcess.HasExited) { throw 'Camera could not start. Check logs\bridge.log for details.' }
    try { $bridgeStatus = Invoke-RestMethod -Uri "$bridgeUrl/health" -TimeoutSec 1 } catch { $bridgeStatus = $null }
    if ($bridgeStatus -and $bridgeStatus.app -ne 'camera-bridge') { throw "Another service is using $bridgeUrl." }
    if ($bridgeStatus -and $bridgeStatus.ok) {
        if ($bridgeConfig.selfAddress -and $bridgeStatus.cameraAddress -ne ($bridgeConfig.selfAddress + ':' + $bridgeConfig.selfPort)) {
            throw 'Camera is running with different address settings. Stop it, then start it again.'
        }
        if ($bridgeConfig.eofCompat -and -not $bridgeStatus.eofCompat) {
            throw 'Camera is running without socket compatibility enabled. Stop it, then start it again.'
        }
        $bridgeReady = $true
        break
    }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $bridgeDeadline)
if (-not $bridgeReady) {
    $bridgeFailure = if ($bridgeStatus.lastError) { $bridgeStatus.lastError } else { 'No fresh camera frame arrived.' }
    throw "Camera is not ready: $bridgeFailure Check logs\bridge.log."
}
Write-Output "Camera ready: $bridgeUrl"
if (-not $NoBrowser) { Start-Process $bridgeUrl }
