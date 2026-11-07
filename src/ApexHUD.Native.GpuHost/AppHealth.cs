using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "ready for the public"):
///   Updates      - once a day Grandstand asks GitHub for the newest release and shows a banner when it's newer
///                  (download + install stay the user's click)
///   Crash files  - an unexpected error is written to Documents\iRacing\Grandstand\crashes; next start the app offers
///                  "Report it", which opens a pre-filled GitHub issue in the browser (the user reads it and presses Submit)
///   First run    - a setup checklist (iRacing, OBS + Spout2, Trading Paints, WebView2) until the user says done
///   Single PC    - one click: settings for driving and streaming on the same computer
/// </summary>
internal static class AppHealth
{
    const string Repo = "instabilitycontrolracing/grandstand";
    static readonly string CrashDir = Path.Combine(ControlState.Folder, "crashes");
    static readonly string WizardFile = Path.Combine(ControlState.Folder, "setup_done.txt");
    static readonly string SeenCrashFile = Path.Combine(ControlState.Folder, "crashes", "seen.txt");
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public static string Version { get; } = ReadVersion();
    static string latest = "", latestUrl = "";
    static DateTime lastCheck = DateTime.MinValue;

    static string ReadVersion()
    {
        foreach (var p in new[] { Path.Combine(AppContext.BaseDirectory, "VERSION"), Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "VERSION") })
            try { if (File.Exists(p)) return File.ReadAllText(p).Trim(); } catch { }
        return "0.1.0-beta.3";
    }

    public static void Init(Action<string> log)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrash(e.ExceptionObject as Exception, "unhandled");
        // a background task that failed quietly doesn't close Grandstand: log it, never report it as a crash
        TaskScheduler.UnobservedTaskException += (_, e) => { log("background task error: " + e.Exception.GetBaseException().Message); e.SetObserved(); };
        _ = Task.Run(async () => { await Task.Delay(20_000); await CheckUpdate(log); });
    }

    static void WriteCrash(Exception? ex, string where)
    {
        try
        {
            Directory.CreateDirectory(CrashDir);
            var sb = new StringBuilder();
            sb.AppendLine($"Grandstand {Version} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} - {where}");
            sb.AppendLine($"Windows {Environment.OSVersion.Version}, .NET {Environment.Version}, {Environment.ProcessorCount} CPUs");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "(no details)");
            File.WriteAllText(Path.Combine(CrashDir, $"crash_{DateTime.Now:yyyyMMdd_HHmmss}.txt"), sb.ToString());
        }
        catch { }
    }

    static async Task CheckUpdate(Action<string> log)
    {
        if ((DateTime.UtcNow - lastCheck).TotalHours < 20) return;
        lastCheck = DateTime.UtcNow;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases?per_page=5");
            req.Headers.UserAgent.ParseAdd("Grandstand-update-check");
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return;
            var arr = JsonNode.Parse(await res.Content.ReadAsStringAsync())?.AsArray();
            var rel = arr?.FirstOrDefault(r => r?["draft"]?.GetValue<bool>() != true);
            if (rel == null) return;
            latest = (rel["tag_name"]?.ToString() ?? "").TrimStart('v');
            latestUrl = rel["html_url"]?.ToString() ?? "";
            if (Newer(latest, Version)) log($"update: {latest} is out (this is {Version})");
        }
        catch { }
    }

    /// <summary>Compares "0.1.0-beta.4" with "0.1.0-beta.3" (numbers, then the beta number).</summary>
    static bool Newer(string a, string b)
    {
        static (int, int, int, int) P(string v)
        {
            var main = v.Split('-')[0].Split('.').Select(x => int.TryParse(x, out int n) ? n : 0).Concat(new[] { 0, 0, 0 }).ToArray();
            int beta = v.Contains("-") ? (int.TryParse(new string(v.Split('-')[1].Where(char.IsDigit).ToArray()), out int bn) ? bn : 0) : int.MaxValue;
            return (main[0], main[1], main[2], beta);
        }
        return a.Length > 0 && P(a).CompareTo(P(b)) > 0;
    }

    public static void WizardDone()
    {
        try { Directory.CreateDirectory(ControlState.Folder); File.WriteAllText(WizardFile, DateTime.Now.ToString("s")); } catch { }
        try
        {
            Directory.CreateDirectory(CrashDir);
            var newest = Directory.GetFiles(CrashDir, "crash_*.txt").OrderByDescending(f => f).FirstOrDefault();
            if (newest != null) File.WriteAllText(SeenCrashFile, Path.GetFileName(newest));
        }
        catch { }
    }

    static bool Installed(params string[] names)
    {
        try
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                foreach (var key in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    using (var k = root.OpenSubKey(key))
                        if (k != null)
                            foreach (var sub in k.GetSubKeyNames())
                                using (var s = k.OpenSubKey(sub))
                                    if (s?.GetValue("DisplayName") is string dn && names.Any(n => dn.Contains(n, StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch { }
        return false;
    }

    static bool SpoutPlugin()
    {
        foreach (var dir in new[] { @"C:\Program Files\obs-studio\obs-plugins\64bit", @"C:\ProgramData\obs-studio\plugins" })
            try { if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir, "*spout*", SearchOption.AllDirectories).Any()) return true; } catch { }
        return false;
    }

    public static object Describe()
    {
        string? crash = null, crashText = null;
        try
        {
            if (Directory.Exists(CrashDir))
            {
                var newest = Directory.GetFiles(CrashDir, "crash_*.txt").OrderByDescending(f => f).FirstOrDefault();
                string seen = File.Exists(SeenCrashFile) ? File.ReadAllText(SeenCrashFile).Trim() : "";
                if (newest != null && Path.GetFileName(newest) != seen && (DateTime.Now - File.GetLastWriteTime(newest)).TotalDays < 7)
                {
                    crash = Path.GetFileName(newest);
                    crashText = File.ReadAllText(newest);
                    if (crashText.Length > 5000) crashText = crashText[..5000];
                }
            }
        }
        catch { }
        string issue = crashText == null ? "" : $"https://github.com/{Repo}/issues/new?title={Uri.EscapeDataString("Crash: " + crashText.Split('\n').Skip(3).FirstOrDefault()?.Trim())}&body={Uri.EscapeDataString("What I was doing when it happened:\n\n\n```\n" + crashText[..Math.Min(crashText.Length, 3500)] + "\n```")}";
        bool obs = Installed("OBS Studio");
        return new
        {
            version = Version,
            update = Newer(latest, Version) ? new { version = latest, url = latestUrl } : null,
            crash, crashIssue = issue,
            wizard = !File.Exists(WizardFile),
            checks = new object[]
            {
                new { id = "iracing", ok = Installed("iRacing"), name = "iRacing", tip = "Install iRacing", link = "https://www.iracing.com" },
                new { id = "obs", ok = obs, name = "OBS Studio", tip = "For streaming and the highlight reel", link = "https://obsproject.com" },
                new { id = "spout", ok = SpoutPlugin(), name = "Spout2 plugin for OBS", tip = "Sends Grandstand's graphics into OBS (then Settings > Stream output)", link = "https://github.com/Off-World-Live/obs-spout2-plugin/releases" },
                new { id = "tp", ok = Installed("Trading Paints"), name = "Trading Paints", tip = "Real liveries on the grid and driver cards", link = "https://www.tradingpaints.com/install" },
                new { id = "spectator", ok = true, name = "Broadcasting other people's races", tip = "Your own account works: join their session as a spectator. A second account is only needed to race AND broadcast the same session at once", link = "https://www.iracing.com" },
            },
        };
    }
}
