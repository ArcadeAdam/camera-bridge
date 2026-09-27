$ErrorActionPreference = 'Stop'
$bridgeConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge.config.json') -Raw | ConvertFrom-Json
$bridgeUrl = 'http://127.0.0.1:' + $bridgeConfig.port
try { $bridgeStatus = Invoke-RestMethod -Uri "$bridgeUrl/health" -TimeoutSec 2 } catch {
    Write-Output 'Camera bridge is not responding; it may already be stopped.'
    exit 0
}
if ($bridgeStatus.app -ne 'camera-bridge') { throw 'The port is occupied by another service; nothing was stopped.' }
Invoke-RestMethod -Method Post -Uri "$bridgeUrl/api/stop" -TimeoutSec 3 | Out-Null
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 200
    try { $null = Invoke-RestMethod -Uri "$bridgeUrl/health" -TimeoutSec 1 } catch {
        Write-Output 'Camera stopped.'
        exit 0
    }
}
throw 'The stop request was accepted, but Camera is still responding. Check logs\bridge.log.'
