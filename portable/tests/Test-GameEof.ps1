# Read-only camera protocol check against an already running bridge.
# Opens no webcam, records no images, and uses the emulator's original 0x300 poll mask.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Address,
    [ValidateRange(1, 65535)][int]$Port = 18080,
    [ValidateRange(1, 20)][int]$Count = 3
)
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
public static class PortableGameEofProbe {
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public IntPtr Socket; public short Events, Returned; }
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int WSAPoll([In, Out] PollFd[] descriptors, uint count, int timeout);
    public static string Run(string address, int port, bool fragmented) {
        IPAddress local = IPAddress.Parse(address);
        using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        using (MemoryStream response = new MemoryStream()) {
            socket.Bind(new IPEndPoint(local, 0));
            socket.SendTimeout = 3000; socket.ReceiveTimeout = 3000; socket.NoDelay = true;
            socket.Connect(local, port);
            byte[] request = Encoding.ASCII.GetBytes("GET /img.jpg HTTP/1.0\r\n\r\n" + (fragmented ? "" : "\0"));
            for (int offset = 0; offset < request.Length; ) offset += socket.Send(request, offset, request.Length - offset, SocketFlags.None);
            if (fragmented) { Thread.Sleep(5); socket.Send(new byte[] { 0 }); }
            bool eof = false, urgent = false;
            Stopwatch clock = Stopwatch.StartNew();
            byte[] bytes = new byte[65536];
            while (clock.ElapsedMilliseconds < 4000) {
                PollFd[] fds = new PollFd[] { new PollFd { Socket = socket.Handle, Events = 0x300 } };
                if (WSAPoll(fds, 1, 100) < 0) throw new IOException("WSAPoll failed.");
                if ((fds[0].Returned & 0x200) != 0) urgent = true;
                if ((fds[0].Returned & 0x300) == 0) continue;
                int received = socket.Receive(bytes, 0, bytes.Length, SocketFlags.None);
                if (received == 0) { eof = true; break; }
                response.Write(bytes, 0, received);
                if (response.Length > 520 * 1024) throw new IOException("Response exceeds the camera protocol limit.");
            }
            if (!eof || !urgent) throw new IOException("Original WSAPoll mask did not observe urgent readiness and normal recv()==0.");
            byte[] all = response.ToArray();
            int boundary = -1;
            for (int i = 0; i + 3 < all.Length; i++)
                if (all[i] == 13 && all[i + 1] == 10 && all[i + 2] == 13 && all[i + 3] == 10) { boundary = i + 4; break; }
            if (boundary < 0) throw new IOException("No HTTP header boundary.");
            string headers = Encoding.ASCII.GetString(all, 0, boundary);
            int length = all.Length - boundary;
            if (!headers.StartsWith("HTTP/1.0 200 OK\r\n") || !headers.Contains("Content-Length: " + length + "\r\n") ||
                !headers.Contains("Content-Type: image/jpeg\r\n") || length < 4 || all[boundary] != 255 || all[boundary + 1] != 216 ||
                all[all.Length - 2] != 255 || all[all.Length - 1] != 217)
                throw new IOException("JPEG response, content length, or final EOI marker is invalid.");
            using (MemoryStream jpeg = new MemoryStream(all, boundary, length))
            using (Image picture = Image.FromStream(jpeg)) {
                if (picture.Width != 320 || picture.Height != 240) throw new IOException("Camera image dimensions are not 320x240.");
                return "PASS: " + address + ":" + port + "; JPEG=" + length + " bytes, 320x240, exact Content-Length, final EOI, POLLRDBAND, normal EOF; fragmentedNul=" + fragmented;
            }
        }
    }
}
'@
for ($index = 0; $index -lt $Count; $index++) {
    [PortableGameEofProbe]::Run($Address, $Port, (($index % 2) -eq 1))
}
