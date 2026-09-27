using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CameraBridge
{
    public sealed class ControlStatus
    {
        public bool ready;
        public bool testPattern;
        public string state, error, configStamp, address;
        public int pid;
    }
    internal static class Program
    {
        internal static string LogPath;
        private static readonly object logLock = new object();
        internal static void Log(string message)
        {
            try
            {
                lock (logLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 1024 * 1024) File.WriteAllText(LogPath, "");
                    File.AppendAllText(LogPath, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
        [STAThread]
        private static int Main(string[] args)
        {
            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CameraBridge.cfg");
            bool background = false, start = false, stop = false, testPattern = false;
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--config": if (++i >= args.Length) throw new ArgumentException("--config needs a file path."); configPath = Path.GetFullPath(args[i]); break;
                        case "--background": background = true; break;
                        case "--start": start = true; break;
                        case "--stop": stop = true; break;
                        case "--test-pattern": testPattern = true; break;
                        default: throw new ArgumentException("Unknown option: " + args[i]);
                    }
                }
                string identity = CameraSettings.Identity(configPath);
                LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CameraBridge", identity, "CameraBridge.log");
                string pipeName = "CameraBridge-" + identity;
                if (stop && (start || background)) throw new ArgumentException("Use --start or --stop, not both.");
                if (stop)
                {
                    string answer = ControlServer.Request(pipeName, "stop", 1500);
                    if (answer == null)
                    {
                        Mutex existing;
                        if (Mutex.TryOpenExisting("Local\\" + pipeName, out existing)) { existing.Dispose(); throw new IOException("Camera Bridge is still starting or cannot answer. Try stopping it again."); }
                        return 0;
                    }
                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    while (DateTime.UtcNow < deadline)
                    {
                        Mutex existing;
                        if (!Mutex.TryOpenExisting("Local\\" + pipeName, out existing)) return 0;
                        existing.Dispose(); Thread.Sleep(150);
                    }
                    throw new IOException("Camera Bridge did not finish stopping. Check its window or log.");
                }
                CameraSettings settings = CameraSettings.Load(configPath, testPattern);
                if (start)
                {
                    string existing = ControlServer.Request(pipeName, "status", 400);
                    Process child = null;
                    if (existing != null)
                    {
                        var previous = new JavaScriptSerializer().Deserialize<ControlStatus>(existing);
                        if (previous.testPattern != testPattern) throw new IOException("Camera Bridge is running in a different capture mode. Stop it, then start it again.");
                        if (previous.state == "Stopped" || previous.state == "Error")
                        {
                            ControlServer.Request(pipeName, "start", 500);
                            Thread.Sleep(200);
                        }
                    }
                    if (existing == null)
                    {
                        child = Process.Start(new ProcessStartInfo {
                            FileName = Application.ExecutablePath,
                            Arguments = "--background --config " + Quote(configPath) + (testPattern ? " --test-pattern" : ""),
                            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                            WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory
                        });
                    }
                    var deadline = DateTime.UtcNow.AddSeconds(25);
                    try
                    {
                        while (DateTime.UtcNow < deadline)
                        {
                            string json = ControlServer.Request(pipeName, "status", 400);
                            if (json != null)
                            {
                                var status = new JavaScriptSerializer().Deserialize<ControlStatus>(json);
                                if (status.testPattern != testPattern) throw new IOException("Camera Bridge is running in a different capture mode. Stop it, then start it again.");
                                if (status.configStamp != settings.ConfigStamp) throw new IOException("Camera Bridge is running with older settings. Stop it, then start it again.");
                                if (status.ready) return 0;
                                if (!String.IsNullOrEmpty(status.error)) throw new IOException(status.error);
                            }
                            if (child != null && child.HasExited && child.ExitCode != 0) throw new IOException("Camera Bridge could not start. Open CameraBridge.exe to see the problem.");
                            Thread.Sleep(150);
                        }
                        throw new IOException("No fresh camera picture arrived. Open CameraBridge.exe to check the camera.");
                    }
                    catch
                    {
                        // A failed launch must not leave our new background capture running.
                        if (child != null && !child.HasExited)
                        {
                            string current = ControlServer.Request(pipeName, "status", 500);
                            if (current != null && new JavaScriptSerializer().Deserialize<ControlStatus>(current).pid == child.Id)
                                ControlServer.Request(pipeName, "stop", 500);
                            if (!child.WaitForExit(8000)) child.Kill();
                        }
                        throw;
                    }
                    finally { if (child != null) child.Dispose(); }
                }
                bool created;
                using (var singleInstance = new Mutex(true, "Local\\" + pipeName, out created))
                {
                    if (!created)
                    {
                        if (!background) ControlServer.Request(pipeName, "show", 1500);
                        return 0;
                    }
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    int result;
                    using (var context = new BridgeContext(settings, background, testPattern, pipeName))
                    {
                        Application.Run(context);
                        result = context.ExitCode;
                    }
                    singleInstance.ReleaseMutex();
                    return result;
                }
            }
            catch (Exception error)
            {
                Log(error.ToString());
                if (!background && !start && !stop) MessageBox.Show(error.Message + "\n\nSettings: " + configPath, "Camera Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
        private static string Quote(string value)
        {
            // File paths cannot contain a quotation mark. Quote paths with spaces.
            if (value.IndexOf('"') >= 0) throw new ArgumentException("Invalid file path.");
            return "\"" + value + "\"";
        }
    }
    internal sealed class ControlServer : IDisposable
    {
        private readonly string name;
        private readonly Func<string, string> handler;
        private readonly Thread thread;
        private volatile bool stopped;
        private NamedPipeServerStream active;
        private readonly object gate = new object();
        internal ControlServer(string name, Func<string, string> handler)
        {
            this.name = name; this.handler = handler;
            thread = new Thread(Serve); thread.IsBackground = true; thread.Start();
        }
        private void Serve()
        {
            while (!stopped)
            {
                try
                {
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security))
                    {
                        lock (gate) { if (stopped) return; active = pipe; }
                        pipe.WaitForConnection();
                        var bytes = new byte[32];
                        var read = pipe.BeginRead(bytes, 0, bytes.Length, null, null);
                        if (!read.AsyncWaitHandle.WaitOne(2000)) continue;
                        int count = pipe.EndRead(read);
                        string command = Encoding.UTF8.GetString(bytes, 0, count).Trim();
                        string result = handler(command);
                        byte[] response = Encoding.UTF8.GetBytes(result + "\n");
                        pipe.Write(response, 0, response.Length); pipe.Flush();
                    }
                }
                catch (Exception error) { if (!stopped) Program.Log("Control: " + error.Message); }
                finally { lock (gate) active = null; }
            }
        }
        internal static string Request(string name, string command, int timeout)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    pipe.Connect(timeout);
                    byte[] data = Encoding.UTF8.GetBytes(command + "\n");
                    pipe.Write(data, 0, data.Length); pipe.Flush();
                    var buffer = new byte[8192];
                    var read = pipe.BeginRead(buffer, 0, buffer.Length, null, null);
                    if (!read.AsyncWaitHandle.WaitOne(timeout)) return null;
                    int length = pipe.EndRead(read);
                    return length == 0 ? null : Encoding.UTF8.GetString(buffer, 0, length).Trim();
                }
            }
            catch (TimeoutException) { return null; }
            catch (IOException) { return null; }
        }
        public void Dispose()
        {
            stopped = true;
            lock (gate) if (active != null) active.Dispose();
            if (Thread.CurrentThread != thread) thread.Join(2500);
        }
    }
}
