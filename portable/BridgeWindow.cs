using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CameraBridge
{
    internal sealed class BridgeContext : ApplicationContext
    {
        internal int ExitCode { get; private set; }
        private CameraSettings settings;
        private readonly bool background, testPattern;
        private readonly Form window;
        private readonly NotifyIcon tray;
        private readonly PictureBox preview;
        private readonly Label status, destination, statistics;
        private readonly Button startButton, stopButton, editButton;
        private readonly System.Windows.Forms.Timer refresh;
        private readonly ControlServer control;
        private volatile ICameraSource camera;
        private volatile CameraServer server;
        private volatile string state = "Starting", lastError = "";
        private Task starting;
        private Task stopping;
        private bool closing, allowClose;
        private volatile bool cancelStartup;
        private DateTime lastImage = DateTime.MinValue, lastRateAt = DateTime.UtcNow;
        private long lastCount;
        private double actualFps;

        internal BridgeContext(CameraSettings settings, bool background, bool testPattern, string pipeName)
        {
            this.settings = settings; this.background = background; this.testPattern = testPattern;
            window = new Form { Text = "Camera Bridge", ClientSize = new Size(630, 565), StartPosition = FormStartPosition.CenterScreen,
                MinimumSize = new Size(645, 600), Font = new Font("Segoe UI", 10), BackColor = Color.FromArgb(245, 247, 250) };
            var title = new Label { Text = "Camera Bridge", Font = new Font("Segoe UI", 21, FontStyle.Bold), AutoSize = true, Location = new Point(22, 15) };
            status = new Label { Text = "Starting camera...", Location = new Point(24, 62), AutoSize = false, Size = new Size(580, 44) };
            preview = new PictureBox { Location = new Point(24, 111), Size = new Size(320, 240), BackColor = Color.FromArgb(27, 31, 37), SizeMode = PictureBoxSizeMode.Zoom };
            var steps = new Label { Location = new Point(362, 118), Size = new Size(240, 230), Text =
                "1. Check your picture.\n\n2. Use the camera address below in the emulator's camera row.\n\n3. Leave this app running while you play.\n\nClosing the app stops the camera." };
            destination = new Label { Location = new Point(24, 367), Size = new Size(585, 30), Font = new Font("Segoe UI", 11, FontStyle.Bold), Text = "Camera address: " + settings.CameraDestination };
            statistics = new Label { Location = new Point(24, 402), Size = new Size(580, 24) };
            startButton = new Button { Text = "Start camera", Location = new Point(24, 445), Size = new Size(130, 38), Enabled = false };
            stopButton = new Button { Text = "Stop camera", Location = new Point(164, 445), Size = new Size(130, 38) };
            editButton = new Button { Text = "Edit settings", Location = new Point(304, 445), Size = new Size(130, 38) };
            var helpButton = new Button { Text = "Camera names", Location = new Point(444, 445), Size = new Size(155, 38) };
            var logButton = new LinkLabel { Text = "Open log", Location = new Point(24, 509), AutoSize = true };
            var cfgLabel = new Label { Text = "Settings: CameraBridge.cfg", Location = new Point(180, 509), AutoSize = true };
            window.Controls.AddRange(new Control[] { title, status, preview, steps, destination, statistics, startButton, stopButton, editButton, helpButton, logButton, cfgLabel });
            startButton.Click += async (s, e) => await StartAsync();
            stopButton.Click += async (s, e) => await StopAsync();
            editButton.Click += async (s, e) => { await StopAsync(); Process.Start("notepad.exe", "\"" + settings.ConfigPath + "\""); };
            helpButton.Click += (s, e) => {
                try { string[] names = DirectShowCameraCapture.ListDevices(); MessageBox.Show(window, names.Length == 0 ? "No webcam found. Plug it in, then try again." : "Use one of these names after camera= in the settings file:\n\n" + String.Join("\n", names), "Camera names"); }
                catch (Exception error) { MessageBox.Show(window, error.Message, "Camera names"); }
            };
            logButton.LinkClicked += (s, e) => { Program.Log("Log opened"); Process.Start("notepad.exe", "\"" + Program.LogPath + "\""); };
            window.FormClosing += async (s, e) => {
                if (allowClose) return;
                e.Cancel = true;
                if (closing) return;
                closing = true;
                await StopAsync();
                allowClose = true; window.Close();
            };
            window.FormClosed += (s, e) => ExitThread();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show Camera Bridge", null, (s, e) => ShowWindow());
            menu.Items.Add("Stop and exit", null, (s, e) => window.Close());
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Camera Bridge", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += (s, e) => ShowWindow();
            var handle = window.Handle;
            control = new ControlServer(pipeName, HandleControl);
            refresh = new System.Windows.Forms.Timer { Interval = 500 };
            refresh.Tick += (s, e) => UpdateView(); refresh.Start();
            if (!background) window.Show();
            window.BeginInvoke(new Action(async () => await StartAsync()));
        }
        private string HandleControl(string command)
        {
            if (command == "stop") { Post(() => window.Close()); return "stopping"; }
            if (command == "start") { Post(async () => await StartAsync()); return "starting"; }
            if (command == "show") { Post(ShowWindow); return "shown"; }
            if (command != "status") return "unknown command";
            var frame = GetFrame();
            return new JavaScriptSerializer().Serialize(new ControlStatus {
                ready = state == "Running" && frame != null && (DateTime.UtcNow - frame.CapturedAtUtc).TotalMilliseconds <= 2000,
                state = state, error = lastError, configStamp = settings.ConfigStamp, address = settings.CameraDestination, pid = Process.GetCurrentProcess().Id, testPattern = testPattern
            });
        }
        private void Post(Action action) { try { if (!window.IsDisposed) window.BeginInvoke(action); } catch (InvalidOperationException) { } }
        private void ShowWindow() { window.Show(); if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal; window.Activate(); }
        private CameraFrame GetFrame() { var source = camera; return source == null ? null : source.GetLatestFrame(); }
        private string CaptureStatus() { var source = camera; return source == null ? state : String.IsNullOrEmpty(source.LastError) ? source.CameraName : source.LastError; }
        private async Task StartAsync()
        {
            if (closing || starting != null || stopping != null || state == "Running") return;
            startButton.Enabled = false; stopButton.Enabled = false; editButton.Enabled = false;
            state = "Starting"; lastError = ""; status.Text = "Starting camera...";
            ExitCode = 0;
            cancelStartup = false;
            try
            {
                settings = CameraSettings.Load(settings.ConfigPath, testPattern);
                destination.Text = "Camera address: " + settings.CameraDestination;
                starting = Task.Run(() => {
                    camera = testPattern ? (ICameraSource)new PatternCamera(settings.Fps) : new DirectShowCameraCapture(settings.CameraName, settings.Fps, settings.JpegQuality, settings.Mirror);
                    server = new CameraServer(new CameraServerOptions { CameraAddress = settings.Address, CameraPort = settings.CameraPort,
                        PreviewPort = settings.PreviewPort, CaptureFps = settings.Fps, InstanceId = settings.ConfigStamp }, GetFrame, CaptureStatus, () => Post(() => window.Close()));
                    server.Start();
                    if (cancelStartup) camera.CancelStart();
                    camera.Start();
                    state = "Running";
                    Program.Log("Camera ready at " + settings.CameraDestination + "; device=" + camera.CameraName + "; testPattern=" + testPattern);
                });
                await starting;
                status.Text = testPattern ? "Ready - test picture (no webcam)" : "Ready - your camera is running";
            }
            catch (Exception error)
            {
                lastError = error.Message; state = "Error";
                ExitCode = 1;
                Program.Log(error.ToString()); status.Text = error.Message;
                ReleaseResources();
                if (background) Post(() => window.Close());
            }
            finally { starting = null; startButton.Enabled = state != "Running"; stopButton.Enabled = state == "Running"; editButton.Enabled = true; }
        }
        private async Task StopAsync()
        {
            if (stopping != null) { await stopping; return; }
            cancelStartup = true;
            startButton.Enabled = false; stopButton.Enabled = false; editButton.Enabled = false;
            if (starting != null)
            {
                var pendingCamera = camera;
                if (pendingCamera != null) pendingCamera.CancelStart();
                try { await starting; } catch { }
            }
            if (stopping != null) { await stopping; return; }
            state = "Stopping";
            stopping = Task.Run(new Action(ReleaseResources));
            await stopping;
            stopping = null;
            state = "Stopped"; status.Text = "Stopped - the webcam is free";
            Program.Log("Camera stopped");
            if (preview.Image != null) { var old = preview.Image; preview.Image = null; old.Dispose(); }
            lastImage = DateTime.MinValue; lastCount = 0; actualFps = 0; lastRateAt = DateTime.UtcNow;
            startButton.Enabled = !closing; editButton.Enabled = !closing;
        }
        private void ReleaseResources()
        {
            var oldServer = server; server = null;
            var oldCamera = camera; camera = null;
            if (oldServer != null) { try { oldServer.Dispose(); } catch (Exception e) { Program.Log("Server stop: " + e.Message); } }
            if (oldCamera != null) { try { oldCamera.Dispose(); } catch (Exception e) { Program.Log("Camera stop: " + e.Message); } }
        }
        private void UpdateView()
        {
            var source = camera; var host = server;
            if (source == null) { statistics.Text = ""; return; }
            var frame = source.GetLatestFrame();
            if (frame != null && frame.CapturedAtUtc != lastImage)
            {
                try
                {
                    using (var stream = new MemoryStream(frame.Jpeg))
                    using (var image = Image.FromStream(stream))
                    {
                        var replacement = new Bitmap(image); var previous = preview.Image;
                        preview.Image = replacement; if (previous != null) previous.Dispose();
                    }
                    lastImage = frame.CapturedAtUtc;
                }
                catch (Exception error) { Program.Log("Preview: " + error.Message); }
            }
            if (state == "Running" && (frame == null || (DateTime.UtcNow - frame.CapturedAtUtc).TotalMilliseconds > 2000))
                status.Text = String.IsNullOrEmpty(source.LastError) ? "No fresh picture. Check the webcam, then stop and start again." : source.LastError;
            else if (state == "Running") status.Text = testPattern ? "Ready - test picture (no webcam)" : "Ready - " + source.CameraName;
            double elapsed = (DateTime.UtcNow - lastRateAt).TotalSeconds;
            if (elapsed >= 1) { actualFps = (source.FrameCount - lastCount) / elapsed; lastCount = source.FrameCount; lastRateAt = DateTime.UtcNow; }
            statistics.Text = String.Format("Capture: {0:0.0} fps  |  Game pictures sent: {1}", actualFps, host == null ? 0 : host.CompletedCameraResponses);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refresh.Stop(); refresh.Dispose(); control.Dispose(); ReleaseResources();
                tray.Visible = false; tray.Dispose();
                if (preview.Image != null) preview.Image.Dispose();
                window.Dispose();
            }
            base.Dispose(disposing);
        }
    }
    internal sealed class PatternCamera : ICameraSource
    {
        private readonly int fps;
        private System.Threading.Timer timer;
        private CameraFrame latest;
        private long count;
        private volatile bool startCancelled;
        private readonly object gate = new object();
        public PatternCamera(int fps) { this.fps = fps; }
        public string CameraName { get { return "Test picture"; } }
        public string LastError { get { return ""; } }
        public long FrameCount { get { return Interlocked.Read(ref count); } }
        public CameraFrame GetLatestFrame() { lock (gate) return latest; }
        public void Start() { if (startCancelled) throw new OperationCanceledException("Camera start was cancelled."); Frame(null); timer = new System.Threading.Timer(Frame, null, 1000 / fps, 1000 / fps); }
        public void CancelStart() { startCancelled = true; }
        private void Frame(object unused)
        {
            lock (gate)
            {
                using (var bitmap = new Bitmap(320, 240))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font("Arial", 16))
                using (var stream = new MemoryStream())
                {
                    graphics.Clear(Color.MidnightBlue); graphics.DrawString("Camera Bridge\nTEST PICTURE\n" + count, font, Brushes.White, 25, 70);
                    bitmap.Save(stream, ImageFormat.Jpeg);
                    latest = new CameraFrame { Jpeg = stream.ToArray(), CapturedAtUtc = DateTime.UtcNow };
                    Interlocked.Increment(ref count);
                }
            }
        }
        public void Stop() { if (timer != null) { using (var ended = new ManualResetEvent(false)) { if (timer.Dispose(ended)) ended.WaitOne(3000); } timer = null; } lock (gate) latest = null; }
        public void Dispose() { Stop(); }
    }
}
