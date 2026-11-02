using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OverlaySharp.Graphics.Adapters;
using OverlaySharp.Graphics.OpenGL.Enums;
using OverlaySharp.Windows;
using SkiaSharp;

// ============================================================
//  ApexHUD GPU overlay PROOF OF CONCEPT  (v3 — real-window targeting)
//  Proves a GPU-composited overlay holds a steady cadence over a
//  LIVE iRacing race/replay. v2 attached to a hidden launcher stub
//  window at (-32000,-32000) 160x25 — rendered fine at ~58fps but
//  off-screen. v3 enumerates VISIBLE, on-screen, large windows and
//  targets the real iRacing render window; logs all candidates.
// ============================================================

const string LogPath   = @"C:\Users\Administrator\Documents\iRacing\gpu_poc.txt";
const string FramesDir = @"C:\Users\Administrator\Documents\iRacing\ApexHUD-Native\assets\transitions\frames\gtp";
const int    RunSeconds = 15;

void Log(string msg) => File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}\r\n");

try
{
    File.WriteAllText(LogPath, "# ApexHUD GPU overlay POC v3 — run log\r\n");
    Log("start");

    nint target = PickIRacingWindow(Log);
    if (target == 0) { Log("No suitable iRacing window found (see candidates above)."); return; }
    Log($"CHOSEN target handle: 0x{target:X}");

    if (!Directory.Exists(FramesDir)) { Log($"frames folder missing: {FramesDir}"); return; }
    SKImage[] frames = Directory.GetFiles(FramesDir, "*.jpg").OrderBy(f => f)
        .Select(f => SKImage.FromEncodedData(f)).Where(i => i != null).ToArray()!;
    Log($"frames loaded: {frames.Length}");
    if (frames.Length == 0) { Log("no frames"); return; }

    using var overlay = new PocOverlay(target, frames)
    {
        MeasureFps = true, AntiAliasing = true, VSyncState = VSyncState.On,
    };

    _ = overlay.StartOverlayAsync();
    Log("overlay started — running 15s");
    await Task.Delay(RunSeconds * 1000);

    Log($"overlay geometry: X={overlay.X} Y={overlay.Y} W={overlay.Width} H={overlay.Height}");
    Log($"render frames captured: {overlay.Gaps.Count}  (~{overlay.Gaps.Count / (double)RunSeconds:F0} fps)");

    var stop = overlay.StopOverlayAsync();
    if (await Task.WhenAny(stop, Task.Delay(3000)) != stop) Log("stop timed out (non-fatal)");

    WriteSummary(LogPath, overlay.Gaps);
    Log("done");
}
catch (Exception ex)
{
    try { File.AppendAllText(LogPath, $"\r\nFATAL: {ex}\r\n"); } catch { }
}


static nint PickIRacingWindow(Action<string> log)
{
    var cands = new List<(nint h, string title, int w, int ht, int left, int top)>();
    EnumWindows((h, _) =>
    {
        if (!IsWindowVisible(h)) return true;
        int len = GetWindowTextLength(h);
        if (len == 0) return true;
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        string title = sb.ToString();
        if (title.IndexOf("iRacing", StringComparison.OrdinalIgnoreCase) < 0) return true;
        if (!GetWindowRect(h, out RECT r)) return true;
        cands.Add((h, title, r.Right - r.Left, r.Bottom - r.Top, r.Left, r.Top));
        return true;
    }, IntPtr.Zero);

    foreach (var c in cands)
        log($"candidate: 0x{c.h:X}  {c.w}x{c.ht} @ ({c.left},{c.top})  \"{c.title}\"");

    // On-screen (not the -32000 parking spot) and reasonably large; pick biggest.
    var best = cands
        .Where(c => c.left > -10000 && c.top > -10000 && c.w >= 640 && c.ht >= 360)
        .OrderByDescending(c => (long)c.w * c.ht)
        .FirstOrDefault();
    return best.h;
}

static void WriteSummary(string path, List<double> gaps)
{
    using var sw = new StreamWriter(path, true);
    sw.WriteLine();
    sw.WriteLine("# ===== CADENCE SUMMARY (ms between presented frames) =====");
    if (gaps.Count < 2) { sw.WriteLine($"# frames: {gaps.Count} — overlay rendered nothing."); return; }
    var sorted = gaps.OrderBy(x => x).ToList();
    double avg = gaps.Average();
    int over33 = gaps.Count(g => g > 33.0), over20 = gaps.Count(g => g > 20.0);
    sw.WriteLine($"# frames : {gaps.Count}");
    sw.WriteLine($"# avg    : {avg:F2} ms   (=> {1000.0 / avg:F1} fps)");
    sw.WriteLine($"# median : {sorted[sorted.Count / 2]:F2} ms");
    sw.WriteLine($"# p95    : {sorted[(int)(sorted.Count * 0.95)]:F2} ms");
    sw.WriteLine($"# min/max: {sorted.First():F2} / {sorted.Last():F2} ms");
    sw.WriteLine($"# >33ms  : {over33} ({100.0 * over33 / gaps.Count:F1}%)   >20ms: {over20} ({100.0 * over20 / gaps.Count:F1}%)");
    sw.WriteLine();
    sw.WriteLine("# avg ~16-17ms => holds 60fps over a live race => proceed with rewrite.");
    sw.WriteLine("# avg ~33ms    => 30fps, still a big win => proceed.");
    sw.WriteLine("# avg ~50-67ms => throttled even on GPU => environment issue.");
}

[DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr p);
[DllImport("user32.dll")] static extern bool IsWindowVisible(nint h);
[DllImport("user32.dll")] static extern int GetWindowTextLength(nint h);
[DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint h, StringBuilder s, int max);
[DllImport("user32.dll")] static extern bool GetWindowRect(nint h, out RECT r);

delegate bool EnumWindowsProc(nint h, IntPtr p);
[StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }


sealed class PocOverlay : OverlayWindow
{
    readonly SKImage[] frames;
    readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    long lastTs;
    public List<double> Gaps { get; } = new(4096);
    public PocOverlay(nint target, SKImage[] frames) : base(target) => this.frames = frames;

    public override void OnRender(IGraphics g)
    {
        long now = Stopwatch.GetTimestamp();
        if (lastTs != 0) Gaps.Add((now - lastTs) * MsPerTick);
        lastTs = now;

        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        int w = Width > 0 ? Width : 1920, h = Height > 0 ? Height : 1080;

        g.DrawRectangleFill(new SKRect(0, 0, w, h), new SKColor(0, 0, 0, 40));
        g.DrawRectangleStroke(new SKRect(3, 3, w - 3, h - 3), new SKColor(0xC0, 0x84, 0xFC, 255), 6f);

        int idx = (int)(elapsedMs / (1000.0 / 30.0)) % frames.Length;
        g.DrawImage(frames[idx], 20, 20);

        float y = (float)((elapsedMs % 1200) / 1200.0) * (h - 80);
        g.DrawRectangleFill(new SKRect(0, y, w, y + 40), new SKColor(0xFF, 0xDA, 0x59, 220));
        g.DrawText(40, 60, $"APEXHUD GPU POC  frame {idx}/{frames.Length}  ({w}x{h})",
            new SKColor(255, 255, 255, 255), g.DefaultFont);
    }
}
