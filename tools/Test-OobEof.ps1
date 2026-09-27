$ErrorActionPreference = 'Stop'
# Synthetic loopback sockets only. Does not open the camera or modify other applications.
Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Threading;
public static class CameraOobEofProbe {
    [StructLayout(LayoutKind.Sequential)] public struct PollFd {
        public UIntPtr fd; public short events; public short revents;
    }
    [DllImport("ws2_32.dll", SetLastError=true)]
    public static extern int WSAPoll([In,Out] PollFd[] fds, uint n, int timeout);
    [DllImport("ws2_32.dll")]
    public static extern int WSAGetLastError();
    public static string Run(bool oob, int delayMs, int frameBytes, int readSize) {
        var steps = new List<string>();
        using(var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        using(var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)) {
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0)); listener.Listen(1);
            client.Connect(listener.LocalEndPoint);
            using(var server = listener.Accept()) {
                client.ReceiveTimeout = 500; server.NoDelay = true;
                var frame = new byte[frameBytes];
                for(int i = 0; i < frame.Length; i++) frame[i] = (byte)(i % 251);
                frame[frame.Length-2] = 255; frame[frame.Length-1] = 217;
                int sent = 0;
                while(sent < frame.Length)
                    sent += server.Send(frame, sent, frame.Length-sent, SocketFlags.None);
                if(oob) server.Send(new byte[]{0x5A}, SocketFlags.OutOfBand);
                server.Shutdown(SocketShutdown.Send); server.Close();
                if(delayMs > 0) Thread.Sleep(delayMs);
                int total = 0; bool eof = false; bool exact = true;
                var received = new byte[frameBytes+16];
                for(int attempt = 0; attempt < frameBytes/readSize+4; attempt++) {
                    var fds = new[]{new PollFd {
                        fd = new UIntPtr((ulong)client.Handle.ToInt64()), events = 0x300
                    }};
                    int ret = WSAPoll(fds, 1, 100); int flags = fds[0].revents;
                    if(ret < 0) throw new Exception("WSAPoll: " + WSAGetLastError());
                    if((flags & 0x300) == 0) {
                        steps.Add("flags="+flags.ToString("X")+",not-readable"); break;
                    }
                    int n = client.Receive(received, total,
                        Math.Min(readSize, received.Length-total), SocketFlags.None);
                    if(attempt == 0 || n == 0)
                        steps.Add("flags="+flags.ToString("X")+",recv="+n);
                    if(n == 0) { eof = true; break; }
                    total += n;
                }
                if(total != frame.Length) exact = false;
                else for(int i = 0; i < total; i++)
                    if(received[i] != frame[i]) { exact = false; break; }
                if(!exact || eof != oob) throw new Exception("Unexpected result: exact="+exact+", eof="+eof);
                return "oob="+oob+" delay="+delayMs+" bytes="+frameBytes+" readSize="+readSize+
                    " total="+total+" exact="+exact+" eof="+eof+" trace=["+string.Join("; ",steps)+"]";
            }
        }
    }
}
'@
foreach($useOob in @($false, $true)) {
    foreach($readDelay in @(0, 100)) {
        foreach($payloadSize in @(300, 6504, 60000)) {
            foreach($receiveSize in @(257, 65536)) {
                [CameraOobEofProbe]::Run($useOob, $readDelay, $payloadSize, $receiveSize)
            }
        }
    }
}
