// Steady race-frame bench (2026-10-29, efficiency pass). Draws what is on screen for most of a race the way
// OverlayCompositor does - tower, session bar + watch (bounded drop-shadow layer), and in "battle" mode the ticker and
// the battle card - for N frames on a 3840x2160 raster surface scaled 2x (the TV). Prints CPU ms and managed bytes per
// frame for each graphic. The canned field (Canned.cs, made-up drivers) moves a little every frame like live data.
//   race_frame [frames] [battle] [--nodraw] [--types=tower] [--png] [--ticks]
//     --nodraw  no-draw canvas: the render thread's CPU side only (on the PC the pixels are the graphics card's job)
//     --types=X sample which object types graphic X allocates
//     --vary    a deterministic moving field (swaps, pits, lapped car, battle glow, pulses, long name, practice half)
//     --ticks   the per-call state work of OverlayCompositor.FrameNeeded instead (see Ticks.cs)
//     --gpu     Skia's OpenGL path on headless Mesa (tools/stinger_bench/Egl.cs), as on the PC: the ms are this thread's
//               CPU side (draw calls + flush); "gpu+wait" adds waiting for the emulated GPU (not meaningful for the PC)
//     --livery  every car gets a stand-in "livery" picture (LiveryArt.Inject), so the battle card draws its car layers
//     --dump=D  PNGs of each graphic alone at the hash frames into folder D (for a PSNR compare of two builds)
// "pixels" lines: per-graphic hashes of every 97th frame - must not change when only efficiency work was done (the
// session bar's wall-clock animations - sun icon, flag cloth - follow the bench clock through SessionBarRenderer.TestClock)
using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

int frames = args.Length > 0 && int.TryParse(args[0], out int fa) ? fa : 600;
bool battleMode = args.Contains("battle");
bool vary = args.Contains("--vary");
string? only = args.FirstOrDefault(a => a.StartsWith("--types="))?["--types=".Length..];
using var types = new AllocTypes();
var baseSnap = Canned.Build();
if (args.Contains("--livery")) baseSnap = StandInLiveries(baseSnap);
PresentationState.Tick(baseSnap);
if (args.Contains("--ticks")) { Ticks.Run(baseSnap, frames); return; }

var info = new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul);
bool gpu = args.Contains("--gpu");
GRContext? gr = gpu ? Egl.Create() : null;
if (gpu && gr == null) { Console.WriteLine("no GPU context"); return; }
gr?.SetResourceCacheLimit(1024L * 1024 * 1024);
if (gr != null) GpuContext.Current = gr;   // as the host does (pictures kept on the graphics card)
using var surface = gr != null ? SKSurface.Create(gr, true, info) : SKSurface.Create(info);
string? dumpDir = args.FirstOrDefault(a => a.StartsWith("--dump="))?["--dump=".Length..];
if (dumpDir != null) Directory.CreateDirectory(dumpDir);
var readBuf = new byte[3840 * 2160 * 4];
double waitMs = 0, flushMs = 0;
// --nodraw: an SKNoDrawCanvas - every call goes through SkiaSharp but nothing is rasterised, i.e. roughly the CPU the
// render thread spends per frame on the GPU path (where the pixels are the graphics card's job)
bool noDraw = args.Contains("--nodraw");
SKCanvas canvas = noDraw ? new SKNoDrawCanvas(3840, 2160) : surface.Canvas;
var tower = new TimingTowerRenderer();
var bar = new SessionBarRenderer();
var watch = new WatchRenderer();
var ticker = new StandingsTickerRenderer();
var battle = new BattleCardRenderer();
var layerPaint = new SKPaint();
var shadow = SKImageFilter.CreateDropShadow(0, 3f, 4f, 4f, new SKColor(0, 0, 0, 115), null);
layerPaint.ImageFilter = shadow;
layerPaint.Color = new SKColor(0, 0, 0, 255);

string[] names = { "tower", "sessionbar", "watch", "ticker", "battle" };
var ms = new double[names.Length]; var bytes = new long[names.Length];
double now = 20000; var clock0 = new DateTime(2026, 10, 29, 20, 15, 7, 250); var clock = clock0;
var hashes = new List<string>(); int w = 1920, h = 1080;
SessionBarRenderer.TestClock = () => clock;   // the bar's wall-clock animations follow the bench clock: repeatable hashes
long Ts() => Stopwatch.GetTimestamp();
double Ms(long t0) => (Ts() - t0) * 1000.0 / Stopwatch.Frequency;

// the snapshot changes a few times a second like live data (gaps / lap distance), not every frame
RaceSnapshot snap = baseSnap;
int warm = 120;
for (int f = 0; f < frames + warm; f++)
{
    clock = clock0.AddMilliseconds(f * 16.7);
    if (vary) { if (f % 6 == 0) snap = Vary.At(baseSnap, f, frames + warm); }
    else if (f % 6 == 0) snap = baseSnap with { CapturedAt = DateTime.UtcNow };
    PresentationState.IsBattleActive = battleMode;   // the battle look: full-width bar + ticker
    PresentationState.Tick(snap);
    bool rec = f >= warm;
    canvas.Clear(SKColors.Transparent);
    canvas.Save(); canvas.Scale(2f);
    void Part(int i, Action a)
    {
        long a0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Ts();
        types.On = rec && only == names[i];
        a();
        types.On = false;
        if (gr != null) { canvas.Flush(); gr.Flush(); }   // GPU path: each graphic's flush (where Skia does its CPU work) is charged to it
        if (rec) { ms[i] += Ms(t0); bytes[i] += GC.GetAllocatedBytesForCurrentThread() - a0; }
        if (gr != null) { long tw = Ts(); Egl.Finish(); if (rec) waitMs += Ms(tw); }
    }
    float plateW = battleMode ? w : SessionBarRenderer.BarWidth, plateX = (w - plateW) / 2f;
    void DrawPart(int i)
    {
        switch (i)
        {
            case 0: tower.Draw(canvas, 16f, 18f, h - 34f, snap, now); break;
            case 1: bar.Draw(canvas, plateX, 18f, 1f, snap, clock, now, plateW); break;
            case 2:
                float cx = plateX + plateW / 2f, cy = 18f + SessionBarRenderer.BarHeight / 2f, half = 64f + 40f;
                canvas.SaveLayer(new SKRect(cx - half, cy - half, cx + half, cy + half), layerPaint);
                watch.Draw(canvas, cx, cy, 64f, clock);
                canvas.Restore();
                break;
            case 3: ticker.Draw(canvas, 0f, 110f, w, snap, now); break;
            case 4: battle.Draw(canvas, (w - 1280f) / 2f + 140f, h - 348f + 30f, snap, now); break;
        }
    }
    for (int i = 0; i < names.Length; i++) if (battleMode || i < 3) Part(i, () => DrawPart(i));
    canvas.Restore();
    if (gr != null)
    {
        long tf = Ts(); canvas.Flush(); gr.Flush();
        double fl = Ms(tf); if (rec) flushMs += fl;
    }
    else canvas.Flush();
    if (rec && !noDraw && (f - warm) % (vary ? 23 : 97) == 0)
    {
        // per-graphic pixel hashes: each graphic alone on a cleared canvas, same time stamp
        var parts = new List<string>();
        for (int i = 0; i < names.Length; i++)
        {
            if (!battleMode && i >= 3) continue;
            canvas.Clear(SKColors.Transparent);
            canvas.Save(); canvas.Scale(2f);
            DrawPart(i);
            canvas.Restore(); canvas.Flush(); gr?.Flush();
            unsafe { fixed (byte* pb = readBuf) surface.ReadPixels(info, (IntPtr)pb, info.RowBytes, 0, 0); }
            parts.Add(names[i][..2] + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(readBuf))[..8]);
            if (dumpDir != null && (i == 4 || !battleMode))
            { using var img = surface.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(Path.Combine(dumpDir, $"{names[i]}_{f - warm:0000}.png"), d.ToArray()); }
            if (f - warm == (vary ? 23 * 9 : 97 * 3) && args.Contains("--png"))
            { using var img = surface.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes($"race_frame_{names[i]}.png", d.ToArray()); }
        }
        hashes.Add(string.Join(",", parts));
    }
    now += 16.7;
}
Console.WriteLine($"race_frame: {frames} frames at 3840x2160 ({(noDraw ? "no-draw canvas = CPU side only" : gr != null ? "Skia GL on headless Mesa: ms = CPU side incl. each graphic's flush" : "raster")}, 2x){(battleMode ? ", battle look" : "")}");
double tot = 0; long totB = 0;
for (int i = 0; i < names.Length; i++)
{
    if (!battleMode && i >= 3) continue;
    Console.WriteLine($"  {names[i],-11} {ms[i] / frames,7:F3} ms/frame  {bytes[i] / (double)frames,9:F1} B/frame");
    tot += ms[i]; totB += bytes[i];
}
if (only != null) types.Dump(only);
foreach (var hh in hashes) Console.WriteLine("  pixels " + hh);
Console.WriteLine($"  {"TOTAL",-11} {tot / frames,7:F3} ms/frame  {totB / (double)frames,9:F1} B/frame");
if (gr != null) Console.WriteLine($"  GPU path: flush {flushMs / frames:F3} ms/frame on top (Skia sending the frame's work to the graphics card), waiting for the emulated GPU {waitMs / frames:F3} ms/frame (not meaningful for the PC)");


// --livery: a made-up car picture (a body, a cabin, two wheels, a stripe) for every car, as if iRacing had rendered it
static RaceSnapshot StandInLiveries(RaceSnapshot snap)
{
    var classes = snap.ClassLeaderboard.Select(cl => cl with { Drivers = cl.Drivers.Select(d => d with { CarPath = "standin" + d.CarIdx }).ToList() }).ToList();
    snap = snap with { ClassLeaderboard = classes };
    foreach (var cl in classes)
        foreach (var d in cl.Drivers)
        {
            using var surf = SKSurface.Create(new SKImageInfo(640, 240, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surf.Canvas; c.Clear(SKColors.Transparent);
            var hue = (d.CarIdx * 47) % 360;
            using var body = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 60), new SKPoint(0, 200),
                new[] { SKColor.FromHsl(hue, 70, 60), SKColor.FromHsl(hue, 80, 30) }, null, SKShaderTileMode.Clamp) };
            c.DrawRoundRect(new SKRect(20, 90, 620, 190), 40, 40, body);
            c.DrawRoundRect(new SKRect(180, 50, 440, 120), 30, 30, body);
            using var dark = new SKPaint { IsAntialias = true, Color = new SKColor(20, 20, 24) };
            c.DrawCircle(140, 190, 42, dark); c.DrawCircle(500, 190, 42, dark);
            using var stripe = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha(200) };
            c.DrawRect(new SKRect(20, 130, 620, 142), stripe);
            using var img = surf.Snapshot(); using var png = img.Encode(SKEncodedImageFormat.Png, 100);
            LiveryArt.Inject(d, png.ToArray());
        }
    return snap;
}
