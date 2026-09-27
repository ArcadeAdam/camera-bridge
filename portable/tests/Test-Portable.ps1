# Isolated process tests. Uses a generated test picture and temporary ports/settings.
# The missing-camera case enumerates names but never selects or opens a real webcam.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [string]$Address
)
$ErrorActionPreference = 'Stop'
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
if (-not $Address) {
    $Address = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() |
        Where-Object { $_.OperationalStatus -eq 'Up' -and $_.NetworkInterfaceType -ne 'Loopback' -and $_.NetworkInterfaceType -ne 'Tunnel' } |
        ForEach-Object { $_.GetIPProperties().UnicastAddresses } |
        Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' -and -not [System.Net.IPAddress]::IsLoopback($_.Address) -and -not $_.Address.ToString().StartsWith('169.254.') } |
        Select-Object -First 1 -ExpandProperty Address | ForEach-Object { $_.ToString() }
}
if (-not $Address) { throw 'An assigned LAN IPv4 address is needed for these tests.' }
$localAddress = [System.Net.IPAddress]::Parse($Address)
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('CameraBridge.Tests.' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($testRoot)
$config = [System.IO.Path]::GetFullPath((Join-Path $testRoot 'CameraBridge.cfg'))
$utf8 = New-Object System.Text.UTF8Encoding($false)
$assertions = 0

function Assert-True([bool]$Condition, [string]$Message) {
    $script:assertions++
    if (-not $Condition) { throw $Message }
}
function New-Listener([System.Net.IPAddress]$BindAddress, [int]$Port) {
    $listener = New-Object System.Net.Sockets.TcpListener($BindAddress, $Port)
    $listener.Server.ExclusiveAddressUse = $true
    $listener.Start()
    return $listener
}
function Start-Command([string[]]$Arguments) {
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $ExePath
    $info.Arguments = ($Arguments -join ' ') + ' --config "' + $config + '"'
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    Write-Verbose ("Launching: " + $info.Arguments)
    return [System.Diagnostics.Process]::Start($info)
}
function Finish-Command([System.Diagnostics.Process]$Process) {
    try {
        if (-not $Process.WaitForExit(40000)) {
            # This is only the exact CLI process started by this test.
            $Process.Kill()
            throw 'The test command did not finish within 40 seconds.'
        }
        return $Process.ExitCode
    }
    finally { $Process.Dispose() }
}
function Invoke-CommandLine([string[]]$Arguments) { return (Finish-Command (Start-Command $Arguments)) }
function Read-Status {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $script:pipeName, [System.IO.Pipes.PipeDirection]::InOut, [System.IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $pipe.Connect(600)
        $request = [System.Text.Encoding]::UTF8.GetBytes("status`n")
        $pipe.Write($request, 0, $request.Length)
        $buffer = New-Object byte[] 8192
        $read = $pipe.BeginRead($buffer, 0, $buffer.Length, $null, $null)
        if (-not $read.AsyncWaitHandle.WaitOne(1000)) { return $null }
        $length = $pipe.EndRead($read)
        if ($length -eq 0) { return $null }
        return ([System.Text.Encoding]::UTF8.GetString($buffer, 0, $length) | ConvertFrom-Json)
    }
    catch [System.TimeoutException] { return $null }
    catch [System.IO.IOException] { return $null }
    finally { $pipe.Dispose() }
}
function Test-MutexPresent {
    $existing = $null
    $found = [System.Threading.Mutex]::TryOpenExisting('Local\' + $script:pipeName, [ref]$existing)
    if ($existing) { $existing.Dispose() }
    return $found
}
function Assert-PortsFree {
    $preview = $null
    $camera = $null
    try {
        $preview = New-Listener ([System.Net.IPAddress]::Loopback) $script:previewPort
        $camera = New-Listener $localAddress $script:cameraPort
        Assert-True $true 'Ports were not released.'
    }
    finally {
        if ($preview) { $preview.Stop() }
        if ($camera) { $camera.Stop() }
    }
}

$previewReservation = $null
$cameraReservation = $null
try {
    $previewReservation = New-Listener ([System.Net.IPAddress]::Loopback) 0
    $cameraReservation = New-Listener $localAddress 0
    $previewPort = ([System.Net.IPEndPoint]$previewReservation.LocalEndpoint).Port
    $cameraPort = ([System.Net.IPEndPoint]$cameraReservation.LocalEndpoint).Port
    if ($cameraPort -eq $previewPort) { throw 'Temporary ports collided. Run the test again.' }
    $baseConfig = "camera=auto`r`naddress=$Address`r`ncamera_port=$cameraPort`r`npreview_port=$previewPort`r`nfps=12`r`njpeg_quality=85`r`nmirror=false`r`n"
    [System.IO.File]::WriteAllText($config, $baseConfig, $utf8)
    $sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $identity = [System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sid + '|' + $config.ToUpperInvariant()))).Replace('-', '').ToLowerInvariant().Substring(0, 24)
    }
    finally { $sha.Dispose() }
    $pipeName = 'CameraBridge-' + $identity
    $previewReservation.Stop(); $previewReservation = $null
    $cameraReservation.Stop(); $cameraReservation = $null

    Assert-True ((Invoke-CommandLine @('--stop')) -eq 0) 'Stopping an absent instance failed.'
    Assert-True ((Invoke-CommandLine @('--start', '--test-pattern')) -eq 0) 'Test-pattern startup failed.'
    $initial = Read-Status
    if (-not $initial) {
        Write-Verbose ('Expected pipe: ' + $pipeName)
        Write-Verbose ('Available pipes: ' + ([System.IO.Directory]::GetFiles('\\.\pipe\') -match 'CameraBridge' -join ', '))
        Write-Verbose ('Processes: ' + ((Get-CimInstance Win32_Process -Filter "Name = 'CameraBridge.exe'" | Select-Object ProcessId, CommandLine) | ConvertTo-Json -Compress))
    }
    Assert-True ($initial.ready -and $initial.testPattern) ('The first instance was not ready in test mode: ' + ($initial | ConvertTo-Json -Compress))
    Assert-True ((Invoke-CommandLine @('--start', '--test-pattern')) -eq 0) 'Repeated start failed.'
    Assert-True ((Read-Status).pid -eq $initial.pid) 'Repeated start created a different instance.'

    Assert-True ((Invoke-CommandLine @('--start')) -ne 0) 'A real-camera start accepted the running test-picture instance.'
    $status = Read-Status
    Assert-True ($status.ready -and $status.pid -eq $initial.pid) 'Mode mismatch stopped the healthy instance.'

    [System.IO.File]::AppendAllText($config, "# Changed settings fingerprint`r`n", $utf8)
    Assert-True ((Invoke-CommandLine @('--start', '--test-pattern')) -ne 0) 'Changed config was accepted by an older running instance.'
    $status = Read-Status
    Assert-True ($status.ready -and $status.pid -eq $initial.pid) 'Config mismatch stopped the healthy instance.'
    Assert-True ((Invoke-CommandLine @('--stop')) -eq 0) 'Stop could not find the instance after its config changed.'
    Assert-True (-not (Test-MutexPresent)) 'Stop left the instance mutex alive.'
    Assert-True ((Invoke-CommandLine @('--stop')) -eq 0) 'Repeated stop failed.'
    Assert-PortsFree

    [System.IO.File]::WriteAllText($config, $baseConfig, $utf8)
    $first = Start-Command @('--start', '--test-pattern')
    $second = Start-Command @('--start', '--test-pattern')
    Assert-True ((Finish-Command $first) -eq 0) 'First concurrent start failed.'
    Assert-True ((Finish-Command $second) -eq 0) 'Second concurrent start failed.'
    $status = Read-Status
    Assert-True ($status.ready -and $status.testPattern) 'Concurrent startup did not produce a ready instance.'
    $matching = @(Get-CimInstance Win32_Process -Filter "Name = 'CameraBridge.exe'" | Where-Object {
        $_.ExecutablePath -eq $ExePath -and $_.CommandLine -and $_.CommandLine.Contains($config)
    })
    Assert-True ($matching.Count -eq 1 -and $matching[0].ProcessId -eq $status.pid) 'Concurrent start left more than one background instance.'
    Assert-True ((Invoke-CommandLine @('--stop')) -eq 0) 'Stopping the concurrent-start instance failed.'
    Assert-PortsFree

    # The name is intentionally impossible; capture does not bind any real camera.
    $missing = $baseConfig.Replace('camera=auto', 'camera=No such camera ' + [Guid]::NewGuid().ToString('N'))
    [System.IO.File]::WriteAllText($config, $missing, $utf8)
    Assert-True ((Invoke-CommandLine @('--start')) -ne 0) 'A missing camera unexpectedly started.'
    Assert-True (-not (Test-MutexPresent)) 'Missing-camera failure left a background instance.'
    Assert-PortsFree

    $pending = Start-Command @('--start')
    $observed = $null
    $waitUntil = [DateTime]::UtcNow.AddSeconds(10)
    while (-not $observed -and -not $pending.HasExited -and [DateTime]::UtcNow -lt $waitUntil) {
        $observed = Read-Status
        if (-not $observed) { Start-Sleep -Milliseconds 50 }
    }
    Assert-True ($observed -and -not $observed.ready) 'Could not observe the pending missing-camera startup.'
    $stopElapsed = [System.Diagnostics.Stopwatch]::StartNew()
    Assert-True ((Invoke-CommandLine @('--stop')) -eq 0) 'Stopping during camera startup failed.'
    Assert-True ($stopElapsed.ElapsedMilliseconds -lt 15000) 'Stop during startup exceeded its deadline.'
    Assert-True ((Finish-Command $pending) -ne 0) 'A cancelled startup reported success.'
    Assert-True (-not (Test-MutexPresent)) 'Cancelled startup left a background instance.'
    Assert-PortsFree

    $invalid = @(
        ($baseConfig + "unknown_option=true`r`n"),
        ($baseConfig + "FPS=12`r`n"),
        ($baseConfig.Replace("address=$Address", 'address=203.0.113.99')),
        ($baseConfig.Replace("camera_port=$cameraPort", "camera_port=$previewPort")),
        ($baseConfig.Replace('fps=12', 'fps=31'))
    )
    foreach ($text in $invalid) {
        [System.IO.File]::WriteAllText($config, $text, $utf8)
        Assert-True ((Invoke-CommandLine @('--start', '--test-pattern')) -ne 0) 'An invalid config was accepted.'
        Assert-True (-not (Test-MutexPresent)) 'An invalid config started a background process.'
        Assert-True ([System.IO.File]::ReadAllText($config) -ceq $text) 'Validation altered the invalid config.'
    }
    Assert-PortsFree
    Write-Output "PASS: $assertions assertions; repeated commands, start race, mode/config mismatch, missing camera, invalid settings, and released ports."
}
finally {
    if ($previewReservation) { $previewReservation.Stop() }
    if ($cameraReservation) { $cameraReservation.Stop() }
    if ($pipeName) {
        try { [void](Invoke-CommandLine @('--stop')) } catch { Write-Warning $_.Exception.Message }
    }
    # Delete only this invocation's verified temporary fixture, never application files.
    if (-not $pipeName -or -not (Test-MutexPresent)) {
        $resolvedRoot = [System.IO.Path]::GetFullPath($testRoot)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
        if ($resolvedRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
            [System.IO.Path]::GetFileName($resolvedRoot).StartsWith('CameraBridge.Tests.', [System.StringComparison]::Ordinal)) {
            Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
        }
    }
    else { Write-Warning "A test instance still exists; retained its config at $config" }
}
