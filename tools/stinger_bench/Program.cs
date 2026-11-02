// Class-transition bench (2026-10-30, Angelo: "class transitions choppy", ~42 ms a frame on the PC).
// Plays the built-in roller (ProceduralStinger) exactly as the timing-tower splash does - in the tower lane, at
// the 4K host scale (height / 1080 = 2), inside the left-to-right wipe clip - for every class, frame by frame on a
// 60 fps clock, and reports CPU raster ms per frame (avg / p50 / p95 / max) and managed bytes per frame.
//
//   ./run.sh [--only gtp,gt3] [--label L] [--frames DIR] [--every N] [--video DIR] [--sheet PNG] [--scale 2]
//   ./run.sh --compare DIR_A DIR_B          (pixel diff of two --frames dumps: max / mean / PSNR per frame)
//   StingerSrc=/abs/old/ProceduralStinger.cs OUT=before ./run.sh ...   (bench an older copy of the roller)
// Give paths absolute. Assets (fonts, logos): GS_ASSETS = a folder laid out like assets/ (fonts/, logos/).
using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

SKSurface? surfaceRef = null;
var opt = new Dictionary<string, string>();
for (int i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--")) opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1";
if (opt.TryGetValue("compare", out var cmpA)) { Bench.Compare(cmpA, args[^1]); return; }

string label = opt.GetValueOrDefault("label", "run");
OverlayLog.Sink = m => Console.WriteLine("  log: " + m);
float scale = float.Parse(opt.GetValueOrDefault("scale", "2"), System.Globalization.CultureInfo.InvariantCulture);
int every = int.Parse(opt.GetValueOrDefault("every", "6"));
string? framesDir = opt.GetValueOrDefault("frames");
string? videoDir = opt.GetValueOrDefault("video");
int reps = int.Parse(opt.GetValueOrDefault("reps", "1"));
const int FPS = 60;
int SW = (int)(1920 * scale), SH = (int)(1080 * scale);
var lane = new SKRect(16, 18, 16 + 400, 18 + (1080 - 34));   // the splash entry's lane (design units)
var laneDev = new SKRectI((int)Math.Floor(lane.Left * scale) - 8, (int)Math.Floor(lane.Top * scale),
                          (int)Math.Ceiling(lane.Right * scale) + 8, (int)Math.Ceiling(lane.Bottom * scale));
if (laneDev.Width % 2 == 1) laneDev.Right++;
if (laneDev.Height % 2 == 1) laneDev.Bottom++;

SKBitmap? Png(params string[] p) { var f = Assets.Resolve(p); return File.Exists(f) ? Assets.Decode(f) : null; }
var wordmark = Png("logos", "iracing-brand", "iRacing-Inline-Color-White.png");
var series = Png("logos", "series", "IMSA iRacing Series.png");
var scenes = new (string Name, Scene Scene, string Hex, SKBitmap? Logo)[]
{
    ("overall", new Scene(SceneFocus.Overall, null, null), BroadcastPalette.Leader, null),
    ("gtp", new Scene(SceneFocus.Class, "GTP", ClassColors.For("GTP", "#C084FC")), ClassColors.For("GTP", "#C084FC"), Png("logos", "classes", "GTP.png")),
    ("lmp2", new Scene(SceneFocus.Class, "LMP2", ClassColors.For("LMP2", "#60A5FA")), ClassColors.For("LMP2", "#60A5FA"), Png("logos", "classes", "LMP2.png")),
    ("gt3", new Scene(SceneFocus.Class, "GT3", ClassColors.For("GT3", "#FFDA59")), ClassColors.For("GT3", "#FFDA59"), null),   // no logo: the big name path
    ("pits", new Scene(SceneFocus.Pits, "GTD", ClassColors.For("GTD", "#FFDA59")), ClassColors.For("GTD", "#FFDA59"), Png("logos", "classes", "GTD.png")),
};
Console.WriteLine($"[{label}] {SW}x{SH} (scale {scale}), wordmark {(wordmark != null)}, series {(series != null)}, lane {laneDev}");

using var stinger = new ProceduralStinger();
var info = new SKImageInfo(SW, SH, SKColorType.Rgba8888, SKAlphaType.Premul);
// --gpu: Skia's OpenGL backend (as on the PC) on a headless Mesa context; the "cpu" time is then what Skia spends on
// this thread building the frame for the graphics card (draw calls + flush), "total" adds waiting for the (emulated) GPU
bool gpu = opt.ContainsKey("gpu");
GRContext? gr = gpu ? Egl.Create() : null;
if (gpu && gr == null) { Console.WriteLine("no GPU context"); return; }
if (gr != null) gr.SetResourceCacheLimit(1024L * 1024 * 1024);
#if PROF
if (gr != null && opt.ContainsKey("sync")) ProceduralStinger.Sync = () => { surfaceRef!.Canvas.Flush(); gr.Flush(); Egl.Finish(); };
#endif
using var surface = gr != null ? SKSurface.Create(gr, true, info) : SKSurface.Create(info);
double lastTotal = 0;
surfaceRef = surface;
var canvas = surface.Canvas;
using var edgeLine = new SKPaint { IsAntialias = true };
using var edgeGlow = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f) };

// SceneDirector.SplashReveal's wipe (380 ms in, 380 ms out), as TimingTowerSplashRenderer clips it
const double WipeMs = 380;
static double Smoothstep(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);
(float, float) Reveal(double t, double total)
{
    double wipe = Math.Min(WipeMs, total / 3.0);
    if (t < 0 || t >= total) return (0, 0);
    if (t < wipe) return (0, (float)Smoothstep(t / wipe));
    if (t < total - wipe) return (0, 1);
    return ((float)Smoothstep((t - (total - wipe)) / wipe), 1);
}

double Frame(int si, double t, out long alloc)
{
    var s = scenes[si];
    double total = ProceduralStinger.DurationMs(s.Scene);
    canvas.Clear(SKColors.Transparent);
    alloc = 0;
    var (top, bot) = Reveal(t, total);
    if (bot - top <= 0.001f) return 0;
    var col = SKColor.Parse(s.Hex);
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    long t0 = Stopwatch.GetTimestamp();
    canvas.Save();
    canvas.Scale(scale);
    float cl = lane.Left + top * lane.Width, cr = lane.Left + bot * lane.Width;
    bool full = top <= 0.0001f && bot >= 0.9999f;
    canvas.Save();
    if (!full) canvas.ClipRect(new SKRect(cl, lane.Top, cr, lane.Bottom));
    stinger.Draw(canvas, lane, s.Scene, col, Math.Max(0, t), total, wordmark, series, s.Logo);
    canvas.Restore();
    if (!full)
    {
        float ex = bot < 0.9999f ? cr : cl;
        var lc = CarArt.LightColor(col);
        edgeGlow.Color = lc.WithAlpha(90); edgeLine.Color = lc;
        canvas.DrawRect(new SKRect(ex - 5, lane.Top, ex + 5, lane.Bottom), edgeGlow);
        canvas.DrawRect(new SKRect(ex - 1.5f, lane.Top, ex + 1.5f, lane.Bottom), edgeLine);
    }
    canvas.Restore();
    canvas.Flush();
    gr?.Flush();
    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    if (gr != null) { Egl.Finish(); lastTotal = Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
    alloc = GC.GetAllocatedBytesForCurrentThread() - a0;
    return ms;
}

// warm-up: first draws build shaders / blobs / caches and JIT (the host does this ahead of the splash too)
for (int si = 0; si < scenes.Length; si++) foreach (var wt in new[] { 100.0, 450, 700, 1300, 3000, 5400 }) Frame(si, wt, out _);

if (framesDir != null) Directory.CreateDirectory(framesDir);
if (videoDir != null) Directory.CreateDirectory(videoDir);
var all = new List<double>(); var totals = new List<double>();
long allocTotal = 0; int allocFrames = 0;
Console.WriteLine($"{"scene",-8} {"frames",6} {"avg ms",8} {"p50",7} {"p95",7} {"max",7} {"hold avg",9}  bytes/frame");
var only = opt.GetValueOrDefault("only")?.Split(',');
for (int si = 0; si < scenes.Length; si++)
{
    var s = scenes[si];
    if (only != null && !only.Contains(s.Name)) continue;
    double total = ProceduralStinger.DurationMs(s.Scene);
    int n = (int)Math.Ceiling(total / 1000.0 * FPS);
    // --prepare: like the host, the roller's still parts are drawn in the background while the board before it holds
    var prep = typeof(ProceduralStinger).GetMethod("Prepare");
    if (opt.ContainsKey("prepare") && prep != null)
    {
        var idle = typeof(ProceduralStinger).GetProperty("Idle")!;
        var sw = Stopwatch.StartNew();
        for (int k = 0; k < 400; k++)
        {
            canvas.Save(); canvas.Scale(scale);
            prep.Invoke(stinger, new object?[] { canvas, lane, s.Scene, SKColor.Parse(s.Hex), wordmark, series, s.Logo });
            canvas.Restore();
            if (k > 2 && (bool)idle.GetValue(stinger)!) break;
            Thread.Sleep(10);
        }
        canvas.Save(); canvas.Scale(scale);
        prep.Invoke(stinger, new object?[] { canvas, lane, s.Scene, SKColor.Parse(s.Hex), wordmark, series, s.Logo });
        canvas.Restore();
        Console.WriteLine($"  prepared {s.Name} in {sw.ElapsedMilliseconds} ms");
    }
    var ms = new List<double>(); var hold = new List<double>();
    long sceneAlloc = 0;
    Process? ff = null;
    if (videoDir != null)
    {
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg",
            $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {laneDev.Width}x{laneDev.Height} -r {FPS} -i - -c:v libx264 -preset medium -crf 16 -pix_fmt yuv420p -movflags +faststart \"{Path.Combine(videoDir, $"{label}_{s.Name}.mp4")}\"")
        { RedirectStandardInput = true, UseShellExecute = false };
        try { ff = Process.Start(psi); } catch (Exception ex) { Console.WriteLine("ffmpeg: " + ex.Message); }
    }
    var raw = new byte[laneDev.Width * laneDev.Height * 4];
    using var crop = new SKBitmap(new SKImageInfo(laneDev.Width, laneDev.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
    using var cropCanvas = new SKCanvas(crop);
    for (int rep = 0; rep < reps; rep++)
    for (int i = 0; i < n; i++)
    {
        double t = i * 1000.0 / FPS;
        double m = Frame(si, t, out long a);
        ms.Add(m); all.Add(m); totals.Add(lastTotal);
        if (opt.TryGetValue("slow", out var slowS) && m > double.Parse(slowS)) Console.WriteLine($"  slow: {s.Name} t={t:F0} ms {m:F1} ms");
        if (t > 1300 && t < total - 800) hold.Add(m);
        sceneAlloc += a; allocTotal += a; if (a > 0) allocFrames++;
        if (rep > 0) continue;
        bool dump = framesDir != null && i % every == 0;
        if (dump || ff != null)
        {
            using var img = surface.Snapshot(laneDev);
            cropCanvas.Clear(new SKColor(28, 32, 38));      // a dark "game" behind, like the TV
            cropCanvas.DrawImage(img, 0, 0);
            if (dump)
            {
                using var d = img.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(framesDir!, $"{s.Name}_{i:000}.png"), d.ToArray());
            }
            if (ff != null)
            {
                System.Runtime.InteropServices.Marshal.Copy(crop.GetPixels(), raw, 0, raw.Length);
                ff.StandardInput.BaseStream.Write(raw, 0, raw.Length);
            }
        }
    }
    if (ff != null) { ff.StandardInput.Close(); ff.WaitForExit(); }
    ms.Sort(); hold.Sort();
    Console.WriteLine($"{s.Name,-8} {ms.Count,6} {ms.Average(),8:F2} {ms[ms.Count / 2],7:F2} {ms[(int)(ms.Count * 0.95)],7:F2} {ms[^1],7:F2} {(hold.Count > 0 ? hold.Average() : 0),9:F2}  {sceneAlloc / Math.Max(1, ms.Count)}");
}
all.Sort();
if (gr != null) Console.WriteLine($"[{label}] GPU mode: the ms above are this thread's (Skia's CPU side); incl. waiting for the emulated GPU: avg {totals.Average():F2} ms");
Console.WriteLine($"[{label}] ALL: {all.Count} frames, avg {all.Average():F2} ms, p50 {all[all.Count / 2]:F2}, p95 {all[(int)(all.Count * 0.95)]:F2}, max {all[^1]:F2}; " +
                  $"managed {allocTotal / Math.Max(1, all.Count)} B/frame ({allocFrames} frames allocated)");

#if PROF
{
    string[] nm = { "base", "bloom", "ghost", "bands", "streaks", "chevrons", "rails", "flash", "panel", "scan", "grain", "vignette" };
    for (int k = 0; k < nm.Length; k++) Console.WriteLine($"  {nm[k],-10} {ProceduralStinger.Acc[k] / ProceduralStinger.Frames:F2} ms/frame");
}
#endif
if (opt.TryGetValue("sheet", out var sheetPng))
{
    // held-frame and punch-in moments of each class, lane crops at 4K
    double[] moments = { 200, 480, 900, 2600 };
    int cw = laneDev.Width, ch = laneDev.Height;
    using var sheet = new SKBitmap(new SKImageInfo(cw * moments.Length * 2 + 40, ch * 2 / 2 + 40, SKColorType.Rgba8888, SKAlphaType.Premul));
    using var sc = new SKCanvas(sheet);
    sc.Clear(new SKColor(16, 18, 22));
    int k = 0;
    foreach (int si in new[] { 1, 3 })
        foreach (var mt in moments)
        {
            Frame(si, mt, out _);
            using var img = surface.Snapshot(laneDev);
            sc.DrawImage(img, 20 + k * cw, 20);
            k++;
        }
    using var im = SKImage.FromBitmap(sheet); using var dat = im.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(sheetPng, dat.ToArray());
    Console.WriteLine("sheet " + sheetPng);
}

static class Bench
{
    public static void Compare(string a, string b)
    {
        double worstPsnr = double.MaxValue; int worstMax = 0; string worstFile = ""; int n = 0; double meanSum = 0;
        foreach (var fa in Directory.GetFiles(a, "*.png").OrderBy(x => x))
        {
            var fb = Path.Combine(b, Path.GetFileName(fa));
            if (!File.Exists(fb)) { Console.WriteLine("missing " + fb); continue; }
            using var ba = SKBitmap.Decode(fa); using var bb = SKBitmap.Decode(fb);
            if (ba.Width != bb.Width || ba.Height != bb.Height) { Console.WriteLine("size differs " + fa); continue; }
            var pa = ba.Bytes; var pb = bb.Bytes;
            long se = 0, sad = 0; int mx = 0, over8 = 0;
            for (int i = 0; i < pa.Length; i++)
            {
                int d = Math.Abs(pa[i] - pb[i]);
                se += d * d; sad += d; if (d > mx) mx = d; if (d > 8) over8++;
            }
            double mse = se / (double)pa.Length;
            double psnr = mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
            double mean = sad / (double)pa.Length;
            meanSum += mean; n++;
            if (psnr < worstPsnr) { worstPsnr = psnr; worstFile = Path.GetFileName(fa); }
            if (mx > worstMax) worstMax = mx;
            Console.WriteLine($"{Path.GetFileName(fa),-16} PSNR {psnr,6:F1} dB  mean |d| {mean:F3}  max {mx,3}  >8: {over8 * 100.0 / pa.Length:F3}%");
        }
        Console.WriteLine($"COMPARE {n} frames: mean |d| {meanSum / Math.Max(1, n):F3}/255, worst PSNR {worstPsnr:F1} dB ({worstFile}), worst single-channel diff {worstMax}");
    }
}

namespace ApexHUD.Native.Overlays
{
    // the two SceneDirector types the roller reads (SceneDirector.cs pulls in the whole broadcast)
    public enum SceneFocus { Overall, Class, Pits }
    public sealed record Scene(SceneFocus Focus, string? ClassName, string? ClassColor)
    {
        public string Label => Focus == SceneFocus.Overall ? "OVERALL" : ClassName ?? "";
    }
    // CarArt.LightColor, copied (CarArt.cs pulls in the tower's text helpers)
    public static class CarArt
    {
        public static SKColor LightColor(SKColor c)
        {
            float lum = (0.2126f * c.Red + 0.7152f * c.Green + 0.0722f * c.Blue) / 255f;
            return lum < 0.12f ? new SKColor(214, 222, 232) : c;
        }
    }
}
