// Visual pack preview (2026-10-27). For every item: 4K key frames (base + elevated) over a real Le Mans TV picture, one
// 1080p MP4 (base then elevated), and the per-frame managed allocations / CPU time of the graphic's Draw at 4K.
//   ./run.sh [--only V1,W12] [--novideo] [--nokeys] [--noalloc]
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ApexHUD.Native.Overlays;
using SkiaSharp;

var opt = new Dictionary<string, string>();
for (int i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--")) opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1";
string outRoot = Environment.GetEnvironmentVariable("OUT") ?? Path.GetFullPath("out");
string bgDir = Environment.GetEnvironmentVariable("GS_BG") ?? "";
var only = opt.TryGetValue("only", out var o) ? new HashSet<string>(o.Split(','), StringComparer.OrdinalIgnoreCase) : null;
StorySound.Enabled = false;
Fonts.WarmUp();

if (opt.TryGetValue("types", out var typesId))
{
    // what allocates in one graphic at one moment (--types V7 --at 1500 [--elev])
    var sc = Scenarios.All().First(x => x.Id.Equals(typesId, StringComparison.OrdinalIgnoreCase));
    double at = double.Parse(opt.GetValueOrDefault("at", "1000"), CultureInfo.InvariantCulture);
    var g = sc.Make(opt.ContainsKey("elev"));
    using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surf.Canvas;
    sc.Start(g, 0);
    for (double t = 0; t <= at; t += 1000.0 / 60) { sc.Frame?.Invoke(g, t); c.Save(); c.Scale(2); DrawGraphic(c, sc, g, t, false); c.Restore(); }
    using var types = new AllocTypes();
    Thread.Sleep(200);
    types.On = true;
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    var sw0 = Stopwatch.StartNew();
    for (int i = 0; i < 4000; i++) { c.Save(); c.Scale(2); DrawGraphic(c, sc, g, at, false); c.Restore(); }
    Console.WriteLine($"{(GC.GetAllocatedBytesForCurrentThread() - a0) / 4000.0:0.0} B/frame at {at} ms, {sw0.Elapsed.TotalMilliseconds / 4000:0.00} ms/frame (CPU 4K)");
    Thread.Sleep(1500); types.On = false; types.Dump(sc.Id);
    return;
}

if (opt.ContainsKey("dirbench"))
{
    // the data hookup (VisualPackDirector.Tick) with every item switched on, in a battle, measured per tick
    var baseSnap = Canned.Build();
    var gtp = baseSnap.ClassLeaderboard[0].Drivers;
    var snap = baseSnap with { Player = gtp[2], CameraTarget = gtp[2], PlayerCarIdx = gtp[2].CarIdx, SessionKey = "bench", TrackLatitude = 47.95,
        SessionLengthSeconds = 6 * 3600, CarSteer = new float[64], CarGear = new int[64], CarRpm = new float[64] };
    var dir = new VisualPackDirector();
    dir.Apply(new VpFlags { Weather = true, Night = true, Radar = true, Strategy = true, LiveTag = true, Bumper = true, Stats = true, YellowMap = true,
        Break = true, Pip = true, Lens = true, RaceControl = true, PurpleBeep = true, LowerThirdSounds = true });
    var ctx = new VpContext { BattleActive = true, Opponent = gtp[1], TvCamera = true, CameraKey = 3, ReadVar = n => 40.0 };
    long tot = 0; int cnt = 0;
    for (int i = 0; i < 4000; i++)
    {
        double now = 20000 + i * 16.67;
        PresentationState.Tick(snap);
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        dir.Tick(snap, now, ctx);
        long da = GC.GetAllocatedBytesForCurrentThread() - a0;
        if (i >= 1000) { tot += da; cnt++; }
    }
    Console.WriteLine($"director tick: {tot / (double)cnt:0.0} B/tick average over {cnt} ticks (battle on, all items on)");
    return;
}

var report = new StringBuilder();
// #413: --ticker = every scenario with the standings ticker on under the bar (folders get a _ticker suffix)
bool tickerRun = opt.ContainsKey("ticker");
Comp.TickerAlways = tickerRun;
foreach (var sc in Scenarios.All())
{
    if (only != null && !only.Contains(sc.Id)) continue;
    string dir = Path.Combine(outRoot, sc.Folder + (tickerRun ? "_ticker" : ""));
    Directory.CreateDirectory(dir);
    Console.WriteLine($"== {sc.Id} {sc.Title}");
    var bg4 = Bg.Load(bgDir, sc.Bg, 3840, 2160);
    var bg1 = Bg.Load(bgDir, sc.Bg, 1920, 1080);

    if (!opt.ContainsKey("nokeys"))
        foreach (bool elev in sc.Variants)
        {
            var g = sc.Make(elev);
            var keys = new Queue<(double, string)>(sc.Keys.Select((k, i) => (k, $"{(elev ? "elevated" : "base")}_{i + 1}_{k:0.0}s.png")));
            using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var nd = new SKNoDrawCanvas(1920, 1080);
            sc.Start(g, 0);
            // step through the timeline (state machines see every frame); render the 4K picture at the key times
            for (double t = 0; keys.Count > 0 && t <= sc.Seconds * 1000 + 1; t += 1000.0 / 60)
            {
                sc.Frame?.Invoke(g, t);
                if (t + 0.001 >= keys.Peek().Item1 * 1000)
                {
                    var (_, name) = keys.Dequeue();
                    var c = surf.Canvas;
                    Compose(c, bg4, 2f, sc, g, t, elev, label: false);
                    using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                    File.WriteAllBytes(Path.Combine(dir, name), data.ToArray());
                }
                else { nd.Save(); nd.Scale(2); sc.Under?.Invoke(nd, 1920, 1080, t); DrawGraphic(nd, sc, g, t, elev); sc.Top?.Invoke(nd, 1920, 1080, t); nd.Restore(); }
            }
            g.Dispose();
            if (sc.Sheet != null) MakeSheet(dir, sc, elev);
        }

    if (!opt.ContainsKey("novideo"))
    {
        string mp4 = Path.Combine(dir, sc.Folder.Split('_')[0] + "_preview.mp4");
        var psi = new ProcessStartInfo("ffmpeg", $"-y -loglevel error -f rawvideo -pix_fmt rgba -s 1920x1080 -r 60 -i - -c:v libx264 -preset veryfast -crf 19 -pix_fmt yuv420p -movflags +faststart \"{mp4}\"")
        { RedirectStandardInput = true, UseShellExecute = false };
        Process? ffp = null;
        try { ffp = Process.Start(psi); } catch (Exception ex) { Console.WriteLine("   no ffmpeg on the PATH - video skipped (" + ex.Message + ")"); }
        if (ffp != null)
        {
        using var ff = ffp;
        using var vbmp = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var vcan = new SKCanvas(vbmp);
        foreach (bool elev in sc.Variants)
        {
            var g = sc.Make(elev);
            sc.Start(g, 0);
            for (double t = 0; t <= sc.Seconds * 1000; t += 1000.0 / 60)
            {
                sc.Frame?.Invoke(g, t);
                Compose(vcan, bg1, 1f, sc, g, t, elev, label: true);
                vcan.Flush();
                ff.StandardInput.BaseStream.Write(vbmp.GetPixelSpan());
            }
            g.Dispose();
        }
        ff.StandardInput.Close();
        ff.WaitForExit();
        }
    }

    if (!opt.ContainsKey("noalloc"))
        foreach (bool elev in sc.Variants)
        {
            var g = sc.Make(elev);
            if (g is NightDemo nd0) nd0.GradeOnly = true;
            using var surf = SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var layoutCanvas = new SKNoDrawCanvas(3840, 2160);
            var c = surf.Canvas;
            // warm-up run (first-use caches: fonts, text blobs, string cache), then the measured run from a fresh start
            for (int pass = 0; pass < 2; pass++)
            {
                sc.Start(g, 0);
                long total = 0, max = 0; int n = 0, nz = 0; double ms = 0; var per = new List<long>();
                for (double t = 0; t <= sc.Seconds * 1000; t += 1000.0 / 60)
                {
                    sc.Frame?.Invoke(g, t);
                    // round 3: the live graphics' layout (VpLayout) for this moment, as in the pictures (not measured)
                    layoutCanvas.Save(); layoutCanvas.Scale(2); sc.Under?.Invoke(layoutCanvas, 1920, 1080, t); sc.Top?.Invoke(layoutCanvas, 1920, 1080, t); layoutCanvas.Restore();
                    c.Clear(SKColors.Transparent);
                    c.Save(); c.Scale(2f);
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    long t0 = Stopwatch.GetTimestamp();
                    DrawGraphic(c, sc, g, t, elev);
                    double dms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    long da = GC.GetAllocatedBytesForCurrentThread() - a0;
                    c.Restore();
                    if (pass == 1) { total += da; max = Math.Max(max, da); n++; if (da > 0) nz++; ms += dms; per.Add(da); }
                }
                if (pass == 1)
                {
                    per.Sort();
                    string line = $"{sc.Id} {(elev ? "elevated" : "base    ")}  frames {n}  alloc avg {total / (double)Math.Max(1, n):0.0} B/frame  median {per[per.Count / 2]} B  max {max} B  frames>0 {nz}  draw {ms / Math.Max(1, n):0.00} ms (CPU raster 4K)";
                    Console.WriteLine("   " + line);
                    report.AppendLine(line);
                    File.AppendAllText(Path.Combine(dir, "alloc.txt"), line + Environment.NewLine);
                }
            }
            g.Dispose();
        }
}
if (report.Length > 0) File.WriteAllText(Path.Combine(outRoot, "alloc_report.txt"), report.ToString());

static void MakeSheet(string dir, Scenario sc, bool elev)
{
    // the key frames side by side at 1280 wide each, a label under each one
    var files = sc.Keys.Select((k, i) => Path.Combine(dir, $"{(elev ? "elevated" : "base")}_{i + 1}_{k:0.0}s.png")).ToList();
    int n = files.Count, cw = 1280, ch = 720;
    using var surf = SKSurface.Create(new SKImageInfo(cw * n + 20 * (n + 1), ch + 110, SKColorType.Rgba8888, SKAlphaType.Premul));
    var c = surf.Canvas; c.Clear(new SKColor(10, 12, 16));
    using var hp = new SKPaint { FilterQuality = SKFilterQuality.High };
    using var tp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 26, Color = new SKColor(232, 194, 90) };
    for (int i = 0; i < n; i++)
    {
        if (!File.Exists(files[i])) continue;
        using var b = SKBitmap.Decode(files[i]);
        float x = 20 + i * (cw + 20);
        c.DrawBitmap(b, new SKRect(x, 20, x + cw, 20 + ch), hp);
        if (sc.Sheet != null && i < sc.Sheet.Length) c.DrawText(sc.Sheet[i], x + 4, ch + 68, tp);
    }
    using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 90);
    File.WriteAllBytes(Path.Combine(dir, $"sheet_{(elev ? "elevated" : "base")}.png"), data.ToArray());
}

static void DrawGraphic(SKCanvas c, Scenario sc, VpGraphic g, double t, bool elev)
{
    if (sc.Custom != null) sc.Custom(c, 1920, 1080, t, g);
    else g.Draw(c, 1920, 1080, t);
}

static void Compose(SKCanvas c, SKImage? bg, float scale, Scenario sc, VpGraphic g, double t, bool elev, bool label)
{
    c.Clear(new SKColor(30, 34, 40));
    if (bg != null) c.DrawImage(bg, 0, 0);
    c.Save();
    c.Scale(scale);
    sc.Under?.Invoke(c, 1920, 1080, t);
    // the graphics go on their own transparent layer, composited over the picture like the real overlay window
    c.SaveLayer();
    DrawGraphic(c, sc, g, t, elev);
    c.Restore();
    sc.Top?.Invoke(c, 1920, 1080, t);
    // preview watermark (placeholder data) + which design this is
    using var wp = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 11, Color = new SKColor(255, 255, 255, 120) };
    string wm = "PREVIEW  ·  PLACEHOLDER DATA" + (label ? (elev ? "  ·  ELEVATED" : "  ·  BASE") : "");
    using var sh = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 11, Color = new SKColor(0, 0, 0, 120) };
    c.DrawText(wm, 1920 - 24 - wp.MeasureText(wm) + 1, 1080 - 12 + 1, sh);
    c.DrawText(wm, 1920 - 24 - wp.MeasureText(wm), 1080 - 12, wp);
    c.Restore();
}

/// <summary>One item of the pack: how to make the graphic, start it, and feed it per frame.</summary>
sealed record Scenario(string Id, string Folder, string Title, string Bg, double Seconds, double[] Keys,
                       Func<bool, VpGraphic> Make, Action<VpGraphic, double> Start)
{
    public Action<VpGraphic, double>? Frame { get; init; }
    public Action<SKCanvas, int, int, double>? Under { get; init; }
    /// <summary>Drawn over the graphic (the session bar / ticker that sit on top in the real overlay).</summary>
    public Action<SKCanvas, int, int, double>? Top { get; init; }
    /// <summary>Designs to render (false = base, true = elevated).</summary>
    public bool[] Variants { get; init; } = { false, true };
    /// <summary>Labels for a side-by-side sheet of the key frames (sheet_base.png / sheet_elevated.png).</summary>
    public string[]? Sheet { get; init; }
    /// <summary>Notes for index.md (round 2).</summary>
    public string Note { get; init; } = "";
    public Action<SKCanvas, int, int, double, VpGraphic>? Custom { get; init; }
}

static class Bg
{
    static readonly Dictionary<string, SKImage?> cache = new();
    public static SKImage? Load(string dir, string name, int w, int h)
    {
        string key = name + w;
        if (cache.TryGetValue(key, out var img)) return img;
        img = null;
        string p = Path.Combine(dir, name + ".png");
        if (File.Exists(p))
        {
            using var src = SKBitmap.Decode(p);
            using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
            surf.Canvas.DrawBitmap(src, new SKRect(0, 0, w, h), paint);
            img = surf.Snapshot();
        }
        return cache[key] = img;
    }
}
