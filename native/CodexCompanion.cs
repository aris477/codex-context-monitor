using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Local companion: never reads authentication, chat contents, or window screenshots.
public interface ICompanionHost {
    bool CodexRunning();
    bool Paused { get; }
    void ClearPause();
    string Health();
    void Start();
    void Stop(string identity);
}

public sealed class CompanionController {
    readonly ICompanionHost host;
    DateTime? absentSince;
    DateTime nextStart = DateTime.MinValue;
    string managed;
    public CompanionController(ICompanionHost host) { this.host = host; }
    public void Tick(DateTime now) {
        if (!host.CodexRunning()) {
            if (!absentSince.HasValue) absentSince = now;
            if (now - absentSince.Value < TimeSpan.FromSeconds(30)) return;
            if (managed != null) { host.Stop(managed); managed = null; }
            host.ClearPause(); nextStart = DateTime.MinValue;
            return;
        }
        absentSince = null;
        if (host.Paused) { managed = null; return; }
        string live = host.Health();
        if (live != null) { managed = live; return; }
        if (now < nextStart) return;
        nextStart = now.AddSeconds(60);
        host.Start();
    }
}

public sealed class WindowsCompanionHost : ICompanionHost {
    readonly string root, node, data, url;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    public WindowsCompanionHost(string root, string node, int port) {
        this.root = Path.GetFullPath(root); this.node = Path.GetFullPath(node);
        data = Path.Combine(this.root, ".codex-usage");
        url = "http://127.0.0.1:" + port + "/api/monitor-health";
        Directory.CreateDirectory(data);
    }
    public static bool IsOfficialPath(string path) {
        if (String.IsNullOrWhiteSpace(path)) return false;
        string name = Path.GetFileName(path);
        return path.IndexOf(@"\WindowsApps\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) >= 0
            && (name.Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Codex.exe", StringComparison.OrdinalIgnoreCase));
    }
    public bool CodexRunning() {
        foreach (string name in new[] { "ChatGPT", "Codex" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process) try { if (IsOfficialPath(process.MainModule.FileName)) return true; } catch { }
        return false;
    }
    public bool Paused { get { return File.Exists(Path.Combine(data, "companion-paused")); } }
    public void ClearPause() { string p = Path.Combine(data, "companion-paused"); if (File.Exists(p)) File.Delete(p); }
    public string Health() {
        try {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Proxy = null; request.Timeout = 2000; request.ReadWriteTimeout = 2000;
            request.KeepAlive = false;
            string text;
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream())) text = reader.ReadToEnd();
            var live = json.DeserializeObject(text) as System.Collections.Generic.Dictionary<string, object>;
            var disk = json.DeserializeObject(File.ReadAllText(Path.Combine(data, "server.json"))) as System.Collections.Generic.Dictionary<string, object>;
            if (live == null || disk == null || (string)live["app"] != "codex-context-monitor"
                || Convert.ToString(live["instance"]) != Convert.ToString(disk["instance"])
                || Convert.ToInt32(live["pid"]) != Convert.ToInt32(disk["pid"])) return null;
            return Convert.ToString(live["pid"]) + ":" + Convert.ToString(live["instance"]);
        } catch { return null; }
    }
    void Log(string message) {
        try {
            string path = Path.Combine(data, "companion.log");
            if (File.Exists(path) && new FileInfo(path).Length > 65536) File.WriteAllText(path, "");
            File.AppendAllText(path, DateTime.Now.ToString("o") + " " + message + Environment.NewLine);
        } catch { }
    }
    public void Start() {
        try {
            var start = new ProcessStartInfo(node, "--no-warnings \"" + Path.Combine(root, "local-server.js") + "\"");
            start.WorkingDirectory = root; start.UseShellExecute = false;
            start.CreateNoWindow = true; start.WindowStyle = ProcessWindowStyle.Hidden;
            start.RedirectStandardOutput = true; start.RedirectStandardError = true;
            var child = new Process { StartInfo = start, EnableRaisingEvents = true };
            child.OutputDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
            child.ErrorDataReceived += (s, e) => { if (e.Data != null) Log(e.Data); };
            child.Exited += (s, e) => { Log("Dashboard exited: " + child.ExitCode); child.Dispose(); };
            child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
            Log("Started dashboard PID " + child.Id);
        } catch (Exception e) { Log("Start failed: " + e.Message); }
    }
    public void Stop(string identity) {
        if (Health() != identity) return;
        try {
            using (var process = Process.GetProcessById(Int32.Parse(identity.Split(':')[0]))) {
                if (!process.MainModule.FileName.Equals(node, StringComparison.OrdinalIgnoreCase)) return;
                process.Kill(); Log("Stopped dashboard after Codex exited");
            }
        } catch (Exception e) { Log("Stop failed: " + e.Message); }
    }
    public void Record(string text) { Log(text); }
}

public static class CodexCompanion {
    public static string MutexName(string root) {
        using (var hash = SHA256.Create()) return @"Local\CodexContextCompanion-" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(root).ToLowerInvariant()))).Replace("-", "");
    }
    [STAThread] public static void Main(string[] args) {
        if (args.Length != 2 || !File.Exists(Path.Combine(args[0], "local-server.js")) || !File.Exists(args[1])) return;
        bool created;
        using (var mutex = new Mutex(true, MutexName(args[0]), out created)) {
            if (!created) return;
            int port;
            if (!Int32.TryParse(Environment.GetEnvironmentVariable("CODEX_CONTEXT_PORT"), out port)) port = 3765;
            var host = new WindowsCompanionHost(args[0], args[1], port);
            var controller = new CompanionController(host);
            host.Record("Companion started; checking Codex every 15 seconds");
            try {
                while (!File.Exists(Path.Combine(args[0], ".codex-usage", "companion-disabled"))) {
                    try { controller.Tick(DateTime.UtcNow); } catch (Exception e) { host.Record(e.Message); }
                    Thread.Sleep(15000);
                }
            } finally { mutex.ReleaseMutex(); }
        }
    }
}
