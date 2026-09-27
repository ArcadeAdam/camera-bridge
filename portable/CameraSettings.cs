using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CameraBridge
{
    public sealed class CameraSettings
    {
        public string ConfigPath, CameraName, ConfigStamp;
        public IPAddress Address;
        public int CameraPort, PreviewPort, Fps, JpegQuality;
        public bool Mirror;
        public string PreviewUrl { get { return "http://127.0.0.1:" + PreviewPort; } }
        public string CameraDestination { get { return Address + ":" + CameraPort; } }
        public const string DefaultText =
            "# Camera Bridge settings\r\n" +
            "# Stop the camera before editing. Save, then start it again.\r\n" +
            "# auto selects the first webcam. Or enter its exact name.\r\n" +
            "camera=auto\r\n\r\n" +
            "# This computer's LAN IP. Example: 192.168.1.2\r\n" +
            "# auto finds a local network address. Check it in the app.\r\n" +
            "address=auto\r\n" +
            "camera_port=18080\r\n" +
            "preview_port=80\r\n\r\n" +
            "# Camera speed: 1 to 30. Picture quality: 1 to 100.\r\n" +
            "fps=30\r\n" +
            "jpeg_quality=85\r\n" +
            "mirror=false\r\n";

        public static string Identity(string path)
        {
            return Hash(WindowsIdentity.GetCurrent().User.Value + "|" + Path.GetFullPath(path).ToUpperInvariant()).Substring(0, 24);
        }
        public static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant();
        }
        public static string[] LocalAddresses()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .ThenBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .Select(a => a.Address.ToString()).Distinct().ToArray();
        }
        public static CameraSettings Load(string path, bool allowLoopback)
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) File.WriteAllText(path, DefaultText, new UTF8Encoding(false));
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keys = new HashSet<string>(new[] { "camera", "address", "camera_port", "preview_port", "fps", "jpeg_quality", "mirror" }, StringComparer.OrdinalIgnoreCase);
            string content = File.ReadAllText(path, Encoding.UTF8);
            int lineNumber = 0;
            foreach (string raw in content.Split('\n'))
            {
                lineNumber++;
                string line = raw.Trim().TrimStart('\uFEFF');
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                int eq = line.IndexOf('=');
                if (eq < 1) throw new InvalidDataException("Settings line " + lineNumber + " needs name=value.");
                string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                if (!keys.Contains(key)) throw new InvalidDataException("Unknown setting: " + key);
                if (values.ContainsKey(key)) throw new InvalidDataException("The setting " + key + " is listed twice.");
                values.Add(key, value);
            }
            var settings = new CameraSettings();
            settings.ConfigPath = path;
            settings.ConfigStamp = Hash(path.ToUpperInvariant() + "|" + content);
            settings.CameraName = Get(values, "camera", "auto");
            if (settings.CameraName.Length == 0 || settings.CameraName.Any(Char.IsControl)) throw new InvalidDataException("camera must be auto or a webcam name.");
            string address = Get(values, "address", "auto");
            string[] local = LocalAddresses();
            if (address.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                if (local.Length == 0) throw new InvalidDataException("No LAN address was found. Connect this computer to your network, then try again.");
                address = local[0];
            }
            if (!IPAddress.TryParse(address, out settings.Address) || settings.Address.AddressFamily != AddressFamily.InterNetwork ||
                !(local.Contains(settings.Address.ToString()) || (allowLoopback && IPAddress.IsLoopback(settings.Address))))
                throw new InvalidDataException("address must be this computer's LAN IPv4 address. Available: " + String.Join(", ", local));
            settings.CameraPort = Number(values, "camera_port", 18080, 1, 65535);
            settings.PreviewPort = Number(values, "preview_port", 80, 1, 65535);
            if (settings.CameraPort == settings.PreviewPort) throw new InvalidDataException("camera_port and preview_port must be different.");
            settings.Fps = Number(values, "fps", 30, 1, 30);
            settings.JpegQuality = Number(values, "jpeg_quality", 85, 1, 100);
            if (!Boolean.TryParse(Get(values, "mirror", "false"), out settings.Mirror)) throw new InvalidDataException("mirror must be true or false.");
            return settings;
        }
        private static string Get(Dictionary<string, string> values, string key, string fallback)
        { string value; return values.TryGetValue(key, out value) ? value : fallback; }
        private static int Number(Dictionary<string, string> values, string key, int fallback, int min, int max)
        {
            int number;
            if (!Int32.TryParse(Get(values, key, fallback.ToString()), out number) || number < min || number > max)
                throw new InvalidDataException(key + " must be a whole number from " + min + " to " + max + ".");
            return number;
        }
    }
}
