$ErrorActionPreference = 'Stop'
$cameraConfig = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge.config.json') -Raw | ConvertFrom-Json
if (-not ('CameraPollProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CameraPollProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd { public IntPtr fd; public short events; public short revents; }
    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSAPoll([In, Out] PollFd[] fds, uint count, int timeout);
}
'@
}
$cameraSocket = New-Object Net.Sockets.Socket([Net.Sockets.AddressFamily]::InterNetwork, [Net.Sockets.SocketType]::Stream, [Net.Sockets.ProtocolType]::Tcp)
try {
    $cameraSocket.ReceiveTimeout = 3000
    $cameraSocket.SendTimeout = 3000
    $cameraSourceAddress = [Net.IPAddress]::Parse($cameraConfig.selfAddress)
    $cameraSocket.Bind((New-Object Net.IPEndPoint($cameraSourceAddress, 0)))
    $cameraSocket.Connect($cameraSourceAddress, [int]$cameraConfig.selfPort)
    $cameraRequest = [Text.Encoding]::ASCII.GetBytes("GET /preview.jpg HTTP/1.0`r`n`r`n")
    $null = $cameraSocket.Send($cameraRequest)
    $cameraBuffer = New-Object byte[] 65536
    $cameraBytes = 0
    do {
        $cameraRead = $cameraSocket.Receive($cameraBuffer)
        $cameraBytes += $cameraRead
    } while ($cameraRead -gt 0)
    $cameraPoll = New-Object 'CameraPollProbe+PollFd[]' 1
    $cameraPoll[0] = [CameraPollProbe+PollFd]@{ fd = $cameraSocket.Handle; events = 0x300; revents = 0 }
    $cameraPollResult = [CameraPollProbe]::WSAPoll($cameraPoll, 1, 0)
    $cameraEvents = [int]$cameraPoll[0].revents
    [pscustomobject]@{
        ResponseBytes = $cameraBytes
        FinalReceive = $cameraRead
        PollResult = $cameraPollResult
        ReturnedEvents = ('0x{0:X}' -f $cameraEvents)
        ReadableWithoutHangup = (($cameraEvents -band 0x300) -ne 0)
        ReadableIncludingHangup = (($cameraEvents -band 0x302) -ne 0)
    }
} finally {
    $cameraSocket.Dispose()
}
