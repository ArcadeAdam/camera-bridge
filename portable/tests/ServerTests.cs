using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CameraBridge;

internal static class ServerTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd { public IntPtr Socket; public short Events; public short Returned; }
    [DllImport("Ws2_32.dll", SetLastError = true)]
    private static extern int WSAPoll([In, Out] PollFd[] descriptors, uint count, int timeout);

    private static int assertions;
    private static void Assert(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }

    private static byte[] MakeJpeg()
    {
        using (Bitmap bitmap = new Bitmap(320, 240))
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (MemoryStream output = new MemoryStream())
        {
            graphics.Clear(Color.CornflowerBlue);
            graphics.FillRectangle(Brushes.Orange, 45, 50, 180, 90);
            bitmap.Save(output, ImageFormat.Jpeg);
            return output.ToArray();
        }
    }

    private static Socket Connect(int port, IPAddress source)
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(source, 0));
        socket.ReceiveTimeout = 3000; socket.SendTimeout = 3000; socket.NoDelay = true;
        socket.Connect(IPAddress.Loopback, port);
        return socket;
    }

    private static void Write(Socket socket, byte[] bytes)
    {
        for (int offset = 0; offset < bytes.Length; )
            offset += socket.Send(bytes, offset, bytes.Length - offset, SocketFlags.None);
    }

    // Deliberately reproduces the installed emulator's original POLLIN mask.
    // Do not add POLLHUP (0x002), and do not consume the urgent byte separately.
    private static byte[] EmulatorRead(Socket socket, out bool sawUrgent)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        bool eof = false;
        sawUrgent = false;
        byte[] buffer = new byte[65536];
        using (MemoryStream response = new MemoryStream())
        {
            while (elapsed.ElapsedMilliseconds < 3000)
            {
                PollFd[] fds = new PollFd[] { new PollFd { Socket = socket.Handle, Events = 0x300 } };
                int ready = WSAPoll(fds, 1, 100);
                if (ready < 0) throw new Exception("WSAPoll failed: " + Marshal.GetLastWin32Error());
                if ((fds[0].Returned & 0x200) != 0) sawUrgent = true;
                if (ready == 0 || (fds[0].Returned & 0x300) == 0) continue;
                int count = socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                if (count == 0) { eof = true; break; }
                response.Write(buffer, 0, count);
            }
            Assert(eof, "Original WSAPoll 0x300 read mask never observed normal recv()==0.");
            return response.ToArray();
        }
    }

    private static byte[] ReadNormal(Socket socket)
    {
        byte[] buffer = new byte[8192];
        using (MemoryStream response = new MemoryStream())
        {
            int count;
            while ((count = socket.Receive(buffer)) != 0) response.Write(buffer, 0, count);
            return response.ToArray();
        }
    }

    private static byte[] Body(byte[] response, out string headers)
    {
        int offset = -1;
        for (int i = 0; i + 3 < response.Length; i++)
            if (response[i] == 13 && response[i + 1] == 10 && response[i + 2] == 13 && response[i + 3] == 10) { offset = i + 4; break; }
        Assert(offset > 0, "Response did not contain an HTTP header boundary.");
        headers = Encoding.ASCII.GetString(response, 0, offset);
        byte[] body = new byte[response.Length - offset];
        Buffer.BlockCopy(response, offset, body, 0, body.Length);
        return body;
    }

    private static string Preview(int port, string path, string extraHeaders, string method)
    {
        using (Socket socket = Connect(port, IPAddress.Loopback))
        {
            Write(socket, Encoding.ASCII.GetBytes(method + " " + path + " HTTP/1.1\r\nHost: 127.0.0.1:" + port + "\r\n" + extraHeaders + "\r\n"));
            return Encoding.UTF8.GetString(ReadNormal(socket));
        }
    }

    private static string ForeignHost(int port, string path)
    {
        using (Socket socket = Connect(port, IPAddress.Loopback))
        {
            Write(socket, Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: attacker.invalid\r\n\r\n"));
            return Encoding.UTF8.GetString(ReadNormal(socket));
        }
    }

    private static void TestGameRequest(CameraServer server, byte[] jpeg, bool fragmented, bool withHost)
    {
        using (Socket socket = Connect(server.CameraPort, IPAddress.Loopback))
        {
            byte[] request = Encoding.ASCII.GetBytes("GET /img.jpg HTTP/1.0\r\n" + (withHost ? "Host: " + server.CameraAddress + "\r\n" : "") + "\r\n");
            if (fragmented)
            {
                Write(socket, request); Thread.Sleep(5); Write(socket, new byte[] { 0 });
            }
            else
            {
                byte[] terminated = new byte[request.Length + 1];
                Buffer.BlockCopy(request, 0, terminated, 0, request.Length); Write(socket, terminated);
            }
            bool urgent;
            byte[] response = EmulatorRead(socket, out urgent);
            string headers;
            byte[] body = Body(response, out headers);
            Assert(headers.StartsWith("HTTP/1.0 200 OK\r\n"), "Game response was not HTTP 200.");
            Assert(headers.Contains("Content-Type: image/jpeg\r\n"), "JPEG MIME type missing.");
            Assert(headers.Contains("Content-Length: " + jpeg.Length + "\r\n"), "Content-Length is not exact.");
            Assert(headers.Contains("Connection: close\r\n"), "Response did not request connection close.");
            Assert(Convert.ToBase64String(body) == Convert.ToBase64String(jpeg), "JPEG bytes changed or urgent zero leaked into normal response.");
            Assert(urgent, "No POLLRDBAND urgent indication was observed.");
        }
    }

    private static int FreePort()
    { TcpListener socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); int port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop(); return port; }

    private static void TestBusyPorts(CameraFrame frame)
    {
        TcpListener occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Server.ExclusiveAddressUse = true; occupied.Start();
        int busy = ((IPEndPoint)occupied.LocalEndpoint).Port;
        int previewPort = FreePort();
        try
        {
            using (CameraServer server = new CameraServer(new CameraServerOptions { CameraAddress = IPAddress.Loopback, PreviewPort = previewPort, CameraPort = busy }, delegate { return frame; }, null, null))
            {
                bool refused = false;
                try { server.Start(); } catch (InvalidOperationException) { refused = true; }
                Assert(refused && !server.IsRunning, "Busy camera port did not fail startup.");
                TcpListener reuse = new TcpListener(IPAddress.Loopback, previewPort);
                reuse.Server.ExclusiveAddressUse = true;
                reuse.Start(); reuse.Stop();
                Assert(true, "First listener was released when second listener failed.");
            }
            using (CameraServer server = new CameraServer(new CameraServerOptions { CameraAddress = IPAddress.Loopback, PreviewPort = busy, CameraPort = 0 }, delegate { return frame; }, null, null))
            {
                bool refused = false;
                try { server.Start(); } catch (InvalidOperationException) { refused = true; }
                Assert(refused && !server.IsRunning, "Busy preview port did not fail startup.");
            }
        }
        finally { occupied.Stop(); }
    }

    public static int Main()
    {
        try
        {
            byte[] jpeg = MakeJpeg();
            CameraFrame current = new CameraFrame { Jpeg = jpeg, CapturedAtUtc = DateTime.UtcNow };
            int stops = 0;
            using (AutoResetEvent stopped = new AutoResetEvent(false))
            using (CameraServer server = new CameraServer(new CameraServerOptions {
                CameraAddress = IPAddress.Loopback, PreviewPort = 0, CameraPort = 0, InstanceId = "server-test-instance", FrameMaxAgeMs = 2000
            }, delegate { return current; }, delegate { return "Camera \"test\"\nstatus"; }, delegate { Interlocked.Increment(ref stops); stopped.Set(); }))
            {
                server.Start();
                for (int i = 0; i < 8; i++)
                { current = new CameraFrame { Jpeg = jpeg, CapturedAtUtc = DateTime.UtcNow }; TestGameRequest(server, jpeg, (i & 1) != 0, false); }
                Assert(server.CameraRequests == 8 && server.CompletedCameraResponses == 8, "Camera request counters are wrong.");
                TestGameRequest(server, jpeg, false, true);
                Assert(ForeignHost(server.CameraPort, "/img.jpg").StartsWith("HTTP/1.0 403"), "Foreign Host received a game camera image.");
                string health = Preview(server.PreviewPort, "/health", "", "GET");
                Assert(health.Contains("\"app\":\"camera-bridge-portable\"") && health.Contains("\"ok\":true") && health.Contains("server-test-instance"), "Health identity/readiness missing.");
                Assert(health.Contains("\\\"test\\\"\\u000a"), "Status JSON escaping failed.");
                string page = Preview(server.PreviewPort, "/", "", "GET");
                Assert(page.Contains("Game camera address") && page.Contains("Stop camera"), "Preview controls missing.");
                Assert(ForeignHost(server.PreviewPort, "/preview.jpg").StartsWith("HTTP/1.0 403"), "Foreign Host received the webcam image.");
                Assert(ForeignHost(server.PreviewPort, "/health").StartsWith("HTTP/1.0 403"), "Foreign Host received camera status.");

                using (Socket foreign = Connect(server.CameraPort, IPAddress.Parse("127.0.0.2")))
                {
                    int received = 0;
                    try { Write(foreign, Encoding.ASCII.GetBytes("GET /img.jpg HTTP/1.0\r\n\r\n\0")); received = foreign.Receive(new byte[1]); }
                    catch (SocketException error) { Assert(error.SocketErrorCode == SocketError.ConnectionReset || error.SocketErrorCode == SocketError.ConnectionAborted || error.SocketErrorCode == SocketError.Shutdown, "Unexpected mismatch-peer result: " + error.SocketErrorCode); }
                    Assert(received == 0, "Foreign source received camera data.");
                }
                Assert(server.RejectedPeers == 1, "Foreign source was not counted as rejected.");

                current = new CameraFrame { Jpeg = jpeg, CapturedAtUtc = DateTime.UtcNow.AddSeconds(-10) };
                Assert(Preview(server.PreviewPort, "/preview.jpg", "", "GET").StartsWith("HTTP/1.0 503"), "Expired frame was served as a live image.");
                health = Preview(server.PreviewPort, "/health", "", "GET");
                Assert(health.Contains("\"ok\":false") && health.Contains("\"status\":\"stale\"") && health.Contains("Camera frame is stale"), "Stale health did not explain failure.");
                Assert(Preview(server.PreviewPort, "/api/stop", "Origin: http://example.com\r\n", "POST").StartsWith("HTTP/1.0 403"), "Cross-origin stop was accepted.");
                Assert(Preview(server.PreviewPort, "/api/stop", "X-Camera-Instance: wrong\r\n", "POST").StartsWith("HTTP/1.0 403"), "Wrong instance token was accepted.");
                Assert(stops == 0, "Rejected stop invoked callback.");
                Assert(Preview(server.PreviewPort, "/api/stop", "X-Camera-Instance: server-test-instance\r\n", "POST").StartsWith("HTTP/1.0 200"), "Verified stop was rejected.");
                Assert(stopped.WaitOne(2000) && stops == 1, "Stop callback was not delivered exactly once.");
                server.Stop(); server.Stop();
                Assert(!server.IsRunning, "Stop is not idempotent.");
            }
            TestBusyPorts(current);
            Console.WriteLine("PASS: " + assertions + " assertions; original WSAPoll 0x300 EOF, exact JPEG/OOB separation, fragmented NUL, peer restriction, freshness, stop security, busy-port rollback.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
    }
}
