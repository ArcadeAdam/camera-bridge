using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CameraBridge
{
    // The capture owner publishes a new snapshot; it must not change Jpeg after publishing.
    public sealed class CameraFrame
    {
        public byte[] Jpeg;
        public DateTime CapturedAtUtc;
    }

    public sealed class CameraServerOptions
    {
        public int PreviewPort = 80;
        public int CameraPort = 18080;
        public IPAddress CameraAddress;
        public int FrameMaxAgeMs = 2000;
        public int CaptureFps = 30;
        public string InstanceId;
    }

    public sealed class CameraServer : IDisposable
    {
        private const int MaxHeaders = 8192;
        private const int MaxImage = 500 * 1024;
        private const int RequestTimeoutMs = 5000;
        private readonly CameraServerOptions options;
        private readonly Func<CameraFrame> frameProvider;
        private readonly Func<string> statusProvider;
        private readonly Action stopRequested;
        private readonly object lifecycle = new object();
        private readonly object clientsLock = new object();
        private readonly HashSet<Socket> clients = new HashSet<Socket>();
        private readonly SemaphoreSlim slots = new SemaphoreSlim(16, 16);
        private TcpListener previewListener;
        private TcpListener cameraListener;
        private Thread previewThread;
        private Thread cameraThread;
        private volatile bool stopping = true;
        private string lastError;
        private long cameraRequests;
        private long completedCameraResponses;
        private long rejectedPeers;
        private int stopNotified;

        public CameraServer(CameraServerOptions options, Func<CameraFrame> frameProvider,
                            Func<string> statusProvider, Action stopRequested)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (frameProvider == null) throw new ArgumentNullException("frameProvider");
            if (options.CameraAddress == null || options.CameraAddress.AddressFamily != AddressFamily.InterNetwork ||
                options.CameraAddress.Equals(IPAddress.Any) || options.CameraAddress.Equals(IPAddress.Broadcast))
                throw new ArgumentException("CameraAddress must be this computer's IPv4 address.");
            if (options.PreviewPort < 0 || options.PreviewPort > 65535 || options.CameraPort < 0 || options.CameraPort > 65535)
                throw new ArgumentOutOfRangeException("options", "Ports must be between 0 and 65535.");
            if (options.FrameMaxAgeMs < 1) throw new ArgumentOutOfRangeException("options", "FrameMaxAgeMs must be positive.");
            this.options = new CameraServerOptions {
                PreviewPort = options.PreviewPort, CameraPort = options.CameraPort,
                CameraAddress = options.CameraAddress, FrameMaxAgeMs = options.FrameMaxAgeMs,
                CaptureFps = options.CaptureFps,
                InstanceId = String.IsNullOrWhiteSpace(options.InstanceId) ? Guid.NewGuid().ToString("N") : options.InstanceId
            };
            this.frameProvider = frameProvider;
            this.statusProvider = statusProvider ?? delegate { return "Waiting for a camera frame."; };
            this.stopRequested = stopRequested;
        }

        public int PreviewPort { get; private set; }
        public int CameraPort { get; private set; }
        public string InstanceId { get { return options.InstanceId; } }
        public string CameraAddress { get { return options.CameraAddress + ":" + CameraPort.ToString(CultureInfo.InvariantCulture); } }
        public long CameraRequests { get { return Interlocked.Read(ref cameraRequests); } }
        public long CompletedCameraResponses { get { return Interlocked.Read(ref completedCameraResponses); } }
        public long RejectedPeers { get { return Interlocked.Read(ref rejectedPeers); } }
        public string LastError { get { return Interlocked.CompareExchange(ref lastError, null, null); } }
        public bool IsRunning { get { return !stopping; } }

        // Both listeners must bind before any connection is served. Port 0 is useful for isolated tests.
        public void Start()
        {
            lock (lifecycle)
            {
                if (!stopping) return;
                TcpListener preview = null;
                TcpListener camera = null;
                try
                {
                    preview = NewListener(IPAddress.Loopback, options.PreviewPort);
                    preview.Start(16);
                    camera = NewListener(options.CameraAddress, options.CameraPort);
                    camera.Start(16);
                    PreviewPort = ((IPEndPoint)preview.LocalEndpoint).Port;
                    CameraPort = ((IPEndPoint)camera.LocalEndpoint).Port;
                    previewListener = preview;
                    cameraListener = camera;
                    stopNotified = 0;
                    lastError = null;
                    stopping = false;
                    previewThread = StartAcceptThread(preview, false);
                    cameraThread = StartAcceptThread(camera, true);
                }
                catch (Exception error)
                {
                    if (preview != null) preview.Stop();
                    if (camera != null) camera.Stop();
                    stopping = true;
                    lastError = "Could not open the camera server ports: " + error.Message;
                    throw new InvalidOperationException(lastError, error);
                }
            }
        }

        private static TcpListener NewListener(IPAddress address, int port)
        {
            TcpListener listener = new TcpListener(address, port);
            listener.Server.ExclusiveAddressUse = true;
            return listener;
        }

        private Thread StartAcceptThread(TcpListener listener, bool game)
        {
            Thread thread = new Thread(delegate() { AcceptLoop(listener, game); });
            thread.IsBackground = true;
            thread.Name = game ? "Camera image listener" : "Camera preview listener";
            thread.Start();
            return thread;
        }

        private void AcceptLoop(TcpListener listener, bool game)
        {
            while (!stopping)
            {
                Socket socket;
                try { socket = listener.AcceptSocket(); }
                catch (SocketException error) { if (!stopping) lastError = error.Message; return; }
                catch (ObjectDisposedException) { return; }
                IPAddress peer = ((IPEndPoint)socket.RemoteEndPoint).Address;
                bool allowed = game ? peer.Equals(options.CameraAddress) : IPAddress.IsLoopback(peer);
                if (!allowed || !slots.Wait(0))
                {
                    if (!allowed) Interlocked.Increment(ref rejectedPeers);
                    socket.Close();
                    continue;
                }
                lock (clientsLock)
                {
                    if (stopping) { slots.Release(); socket.Close(); return; }
                    clients.Add(socket);
                }
                Socket accepted = socket;
                ThreadPool.QueueUserWorkItem(delegate { Serve(accepted, game); });
            }
        }

        private sealed class Request
        {
            public string Method;
            public string Path;
            public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private static Request ReadRequest(Socket socket, bool game)
        {
            byte[] bytes = new byte[MaxHeaders + 17];
            int count = 0, boundary = -1;
            Stopwatch clock = Stopwatch.StartNew();
            while (boundary < 0)
            {
                int remaining = RequestTimeoutMs - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0) throw new InvalidDataException("HTTP header timeout.");
                socket.ReceiveTimeout = remaining;
                int received = socket.Receive(bytes, count, bytes.Length - count, SocketFlags.None);
                if (received == 0) throw new InvalidDataException("Connection ended before HTTP headers.");
                int previous = count;
                count += received;
                for (int i = Math.Max(0, previous - 3); i + 3 < count; i++)
                {
                    if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    { boundary = i + 4; break; }
                }
                if ((boundary < 0 && count >= MaxHeaders) || boundary > MaxHeaders)
                    throw new InvalidDataException("HTTP headers exceed limit.");
            }
            for (int i = 0; i < boundary; i++)
                if (bytes[i] >= 127 || (bytes[i] < 32 && bytes[i] != 9 && bytes[i] != 10 && bytes[i] != 13))
                    throw new InvalidDataException("Invalid HTTP header character.");
            string[] lines = Encoding.ASCII.GetString(bytes, 0, boundary - 4).Split(new string[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || (first[2] != "HTTP/1.0" && first[2] != "HTTP/1.1") || !first[1].StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid HTTP request line.");
            if (lines.Length > 51) throw new InvalidDataException("Too many HTTP headers.");
            Request request = new Request { Method = first[0], Path = first[1].Split('?')[0] };
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) throw new InvalidDataException("Invalid HTTP header.");
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                if (request.Headers.ContainsKey(name)) throw new InvalidDataException("Duplicate HTTP header.");
                request.Headers.Add(name, value);
            }
            string length;
            if (request.Headers.ContainsKey("Transfer-Encoding") ||
                (request.Headers.TryGetValue("Content-Length", out length) && length != "0"))
                throw new InvalidDataException("Request bodies are unsupported.");
            if (game) ReadPadding(socket, bytes, boundary, count - boundary);
            else if (count != boundary) throw new InvalidDataException("Unexpected request body.");
            return request;
        }

        private static void ReadPadding(Socket socket, byte[] initial, int start, int count)
        {
            int padding = count;
            for (int i = start; i < start + count; i++)
                if (initial[i] != 0) throw new InvalidDataException("Only trailing NUL request padding is supported.");
            if (padding > 16) throw new InvalidDataException("Request padding exceeds limit.");
            Stopwatch clock = Stopwatch.StartNew();
            byte[] more = new byte[17];
            while (true)
            {
                int remaining = 20 - (int)clock.ElapsedMilliseconds;
                if (remaining <= 0 || !socket.Poll(padding == 0 ? remaining * 1000 : 0, SelectMode.SelectRead)) return;
                int received = socket.Receive(more, 0, 17 - padding, SocketFlags.None);
                if (received == 0) return;
                for (int i = 0; i < received; i++)
                    if (more[i] != 0) throw new InvalidDataException("Only trailing NUL request padding is supported.");
                padding += received;
                if (padding > 16) throw new InvalidDataException("Request padding exceeds limit.");
            }
        }

        private CameraFrame GetFreshFrame(out long? age, out string reason)
        {
            age = null;
            reason = null;
            CameraFrame frame;
            try { frame = frameProvider(); }
            catch (Exception error) { reason = "Camera frame unavailable: " + error.Message; return null; }
            if (frame == null || frame.Jpeg == null) { reason = SafeStatus(); return null; }
            age = (long)(DateTime.UtcNow - frame.CapturedAtUtc.ToUniversalTime()).TotalMilliseconds;
            if (age < -1000 || age > options.FrameMaxAgeMs)
            { reason = "Camera frame is stale. " + SafeStatus(); return null; }
            byte[] jpeg = frame.Jpeg;
            if (jpeg.Length < 4 || jpeg.Length > MaxImage || jpeg[0] != 255 || jpeg[1] != 216 ||
                jpeg[jpeg.Length - 2] != 255 || jpeg[jpeg.Length - 1] != 217)
            { reason = "Camera produced an incomplete or oversized JPEG."; return null; }
            return frame;
        }

        private string SafeStatus()
        {
            try { return statusProvider() ?? "Waiting for a camera frame."; }
            catch { return "Camera status unavailable."; }
        }

        private void Serve(Socket socket, bool game)
        {
            bool notifyStop = false;
            bool responding = false;
            try
            {
                socket.SendTimeout = RequestTimeoutMs;
                socket.ReceiveTimeout = RequestTimeoutMs;
                socket.NoDelay = true;
                Request request = ReadRequest(socket, game);
                string host;
                bool hasHost = request.Headers.TryGetValue("Host", out host);
                bool allowedHost = game ? !hasHost || AllowedCameraHost(host) : hasHost && AllowedHost(host);
                if (!allowedHost)
                {
                    responding = true;
                    SendText(socket, game, 403, "Forbidden", "This camera endpoint requires its local host address.\n");
                    return;
                }
                long? age;
                string reason;
                if (request.Method == "GET" && (request.Path == "/img.jpg" || request.Path == "/preview.jpg"))
                {
                    bool counted = game && request.Path == "/img.jpg";
                    if (counted) Interlocked.Increment(ref cameraRequests);
                    CameraFrame frame = GetFreshFrame(out age, out reason);
                    responding = true;
                    if (frame == null) SendText(socket, game, 503, "Service Unavailable", reason + "\n");
                    else
                    {
                        Send(socket, game, 200, "OK", "image/jpeg", frame.Jpeg);
                        if (counted) Interlocked.Increment(ref completedCameraResponses);
                    }
                }
                else if (!game && request.Method == "GET" && request.Path == "/health")
                {
                    CameraFrame frame = GetFreshFrame(out age, out reason);
                    string json = "{\"app\":\"camera-bridge-portable\",\"ok\":" + (frame != null && !stopping ? "true" : "false") +
                        ",\"instanceId\":" + Json(InstanceId) + ",\"status\":" + Json(stopping ? "stopping" : frame != null ? "live" : age.HasValue ? "stale" : "starting") +
                        ",\"statusMessage\":" + Json(SafeStatus()) + ",\"cameraAddress\":" + Json(CameraAddress) +
                        ",\"port\":" + PreviewPort.ToString(CultureInfo.InvariantCulture) +
                        ",\"captureFps\":" + options.CaptureFps.ToString(CultureInfo.InvariantCulture) +
                        ",\"width\":320,\"height\":240,\"eofCompat\":true,\"frameAgeMs\":" + (age.HasValue ? Math.Max(0, age.Value).ToString(CultureInfo.InvariantCulture) : "null") +
                        ",\"gameImageRequests\":" + CameraRequests.ToString(CultureInfo.InvariantCulture) +
                        ",\"gameResponsesCompleted\":" + CompletedCameraResponses.ToString(CultureInfo.InvariantCulture) +
                        ",\"lastError\":" + (frame != null ? "null" : Json(reason)) + "}";
                    responding = true;
                    Send(socket, false, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
                }
                else if (!game && request.Method == "GET" && request.Path == "/")
                {
                    responding = true;
                    Send(socket, false, 200, "OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PreviewHtml));
                }
                else if (!game && request.Method == "POST" && request.Path == "/api/stop")
                {
                    responding = true;
                    if (!AllowedStop(request)) SendText(socket, false, 403, "Forbidden", "Stop requires a local same-origin request.\n");
                    else
                    {
                        Send(socket, false, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes("{\"ok\":true,\"status\":\"stopping\"}"));
                        notifyStop = Interlocked.CompareExchange(ref stopNotified, 1, 0) == 0;
                    }
                }
                else { responding = true; SendText(socket, game, 404, "Not Found", "Not found.\n"); }
            }
            catch (InvalidDataException error)
            {
                if (!responding) { try { SendText(socket, game, 400, "Bad Request", error.Message + "\n"); } catch { } }
            }
            catch (SocketException error) { if (!stopping) lastError = error.Message; }
            catch (ObjectDisposedException) { }
            catch (Exception error) { if (!stopping) lastError = error.Message; }
            finally
            {
                lock (clientsLock) clients.Remove(socket);
                socket.Close();
                slots.Release();
                if (notifyStop) ThreadPool.QueueUserWorkItem(delegate { try { if (stopRequested != null) stopRequested(); else Stop(); } catch (Exception error) { lastError = error.Message; } });
            }
        }

        private bool AllowedStop(Request request)
        {
            string host;
            if (!request.Headers.TryGetValue("Host", out host) || !AllowedHost(host)) return false;
            string origin;
            if (request.Headers.TryGetValue("Origin", out origin))
            {
                Uri uri;
                if (!Uri.TryCreate(origin, UriKind.Absolute, out uri) || uri.Scheme != "http" || uri.UserInfo.Length != 0 ||
                    uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !AllowedHost(uri.Authority)) return false;
            }
            string site;
            if (request.Headers.TryGetValue("Sec-Fetch-Site", out site) && site != "same-origin" && site != "none") return false;
            string token;
            if (request.Headers.TryGetValue("X-Camera-Instance", out token) && !String.Equals(token, InstanceId, StringComparison.Ordinal)) return false;
            return true;
        }

        private bool AllowedHost(string host)
        {
            return String.Equals(host, "127.0.0.1:" + PreviewPort, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(host, "localhost:" + PreviewPort, StringComparison.OrdinalIgnoreCase) ||
                (PreviewPort == 80 && (String.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) || String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)));
        }

        private bool AllowedCameraHost(string host)
        {
            return String.Equals(host, CameraAddress, StringComparison.OrdinalIgnoreCase) ||
                (CameraPort == 80 && String.Equals(host, options.CameraAddress.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        private static void SendText(Socket socket, bool urgent, int code, string status, string text)
        { Send(socket, urgent, code, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text)); }

        private static void Send(Socket socket, bool urgent, int code, string status, string type, byte[] body)
        {
            string header = "HTTP/1.0 " + code.ToString(CultureInfo.InvariantCulture) + " " + status + "\r\nContent-Type: " + type +
                "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) +
                "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nCross-Origin-Resource-Policy: same-origin\r\n\r\n";
            SendAll(socket, Encoding.ASCII.GetBytes(header));
            SendAll(socket, body);
            // MSG_OOB stays outside the JPEG stream. It preserves the tested Windows
            // compatibility behavior: POLLRDBAND makes the emulator observe EOF.
            if (urgent && socket.Send(new byte[] { 0 }, 0, 1, SocketFlags.OutOfBand) != 1)
                throw new IOException("The urgent EOF indication could not be sent.");
            socket.Shutdown(SocketShutdown.Send);
        }

        private static void SendAll(Socket socket, byte[] bytes)
        {
            for (int sent = 0; sent < bytes.Length; )
            {
                int count = socket.Send(bytes, sent, bytes.Length - sent, SocketFlags.None);
                if (count == 0) throw new IOException("The response connection closed.");
                sent += count;
            }
        }

        private static string Json(string text)
        {
            if (text == null) return "null";
            StringBuilder value = new StringBuilder("\"");
            foreach (char c in text)
            {
                if (c == '"' || c == '\\') value.Append('\\').Append(c);
                else if (c < 32) value.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else value.Append(c);
            }
            return value.Append('"').ToString();
        }

        public void Stop()
        {
            Thread preview, camera;
            lock (lifecycle)
            {
                stopping = true;
                if (previewListener != null) previewListener.Stop();
                if (cameraListener != null) cameraListener.Stop();
                lock (clientsLock) { foreach (Socket socket in clients) { try { socket.Close(); } catch { } } }
                preview = previewThread; camera = cameraThread;
                previewListener = null; cameraListener = null;
            }
            if (preview != null && preview != Thread.CurrentThread) preview.Join(1000);
            if (camera != null && camera != Thread.CurrentThread) camera.Join(1000);
        }

        public void Dispose() { Stop(); }

        private const string PreviewHtml = @"<!doctype html><html lang='en'><meta charset='utf-8'><meta name='viewport' content='width=device-width'>
<title>Camera Bridge</title><style>body{font:16px system-ui,sans-serif;background:#13202b;color:#edf5ff;max-width:720px;margin:40px auto;padding:20px}img{width:640px;max-width:100%;image-rendering:auto;border-radius:12px;background:#07121b}code{color:#8edcff}button{padding:10px 18px;border-radius:6px;border:0;cursor:pointer}#error{color:#ffd199;white-space:pre-wrap}</style>
<h1>Camera Bridge</h1><p id='state'>Connecting…</p><img id='preview' alt='Live webcam preview'><p>Game camera address: <code id='address'>Waiting…</code></p><p id='error'></p><button id='stop'>Stop camera</button>
<script>var stopped=false,busy=false,live=false,instance='';var image=document.getElementById('preview');function next(){busy=false;if(!stopped&&live&&!document.hidden)setTimeout(frame,33);}image.onload=next;image.onerror=function(){busy=false;};function frame(){if(!stopped&&live&&!busy&&!document.hidden){busy=true;image.src='/preview.jpg?t='+Date.now();}}async function health(){if(stopped)return;try{var r=await fetch('/health',{cache:'no-store'});var s=await r.json();instance=s.instanceId;live=s.ok;document.getElementById('state').textContent=s.statusMessage;document.getElementById('address').textContent=s.cameraAddress;document.getElementById('error').textContent=s.lastError||'';if(live)frame();else{image.removeAttribute('src');busy=false;}}catch(e){live=false;document.getElementById('state').textContent='Camera server offline';document.getElementById('error').textContent=String(e);}if(!stopped)setTimeout(health,1000);}document.getElementById('stop').onclick=async function(){this.disabled=true;try{var r=await fetch('/api/stop',{method:'POST',headers:{'X-Camera-Instance':instance}});if(!r.ok)throw Error('Stop failed: '+r.status);stopped=true;live=false;image.removeAttribute('src');document.getElementById('state').textContent='Camera stopped';document.getElementById('error').textContent='';}catch(e){document.getElementById('error').textContent=String(e);this.disabled=false;}};health();</script></html>";
    }
}
