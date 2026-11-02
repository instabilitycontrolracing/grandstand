// Preview of the built-in class transition (ProceduralStinger) as it plays in the timing-tower lane on a
// 1920x1080 screen, plus the drawn car panel (CarArt). Writes into <repo>/shots:
//   transition_preview.mp4        every class back to back, 60 fps (ffmpeg on PATH or FFMPEG=...)
//   transition_contact.png        8 moments of the GT3 roller (lane crops)
//   transition_classes.png        the held frame of each class side by side
//   transition_4k_lane.png        one frame at 3840x2160 (lane crop) - checks 4K sharpness
//   car_panel_preview.png         drawn car panels for two cars
// and prints the per-frame cost and managed allocations of ProceduralStinger.Draw.
using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
string shots = Path.Combine(repo, "shots");
Directory.CreateDirectory(shots);
const int SW = 1920, SH = 1080, FPS = 60;
var lane = new SKRect(16, 18, 16 + 400, 18 + (SH - 34));

var scenes = new[]
{
    (new Scene(SceneFocus.Overall, null, null), BroadcastPalette.Leader),
    (new Scene(SceneFocus.Class, "GTP", ClassColors.For("GTP", "#C084FC")), ClassColors.For("GTP", "#C084FC")),
    (new Scene(SceneFocus.Class, "LMP2", ClassColors.For("LMP2", "#60A5FA")), ClassColors.For("LMP2", "#60A5FA")),
    (new Scene(SceneFocus.Class, "GT3", ClassColors.For("GT3", "#FFDA59")), ClassColors.For("GT3", "#FFDA59")),
};

using var stinger = new ProceduralStinger();
using var screen = new SKBitmap(new SKImageInfo(SW, SH, SKColorType.Rgba8888, SKAlphaType.Premul));
using var canvas = new SKCanvas(screen);

// ---- the mock "game + tower" underneath (what the wipe reveals) ----
void DrawWorld(SKCanvas c, int boardIndex)
{
    using (var bg = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(SW, SH),
               new[] { new SKColor(58, 72, 86), new SKColor(110, 120, 118), new SKColor(40, 44, 40) }, null, SKShaderTileMode.Clamp) })
        c.DrawRect(0, 0, SW, SH, bg);
    using (var road = new SKPaint { Color = new SKColor(34, 36, 38) }) { using var p = new SKPath(); p.MoveTo(700, SH); p.LineTo(1150, 520); p.LineTo(1300, 520); p.LineTo(1900, SH); p.Close(); c.DrawPath(p, road); }
    using var glass = new SKPaint { Color = new SKColor(10, 14, 20, 225) };
    c.DrawRoundRect(new SKRect(lane.Left, lane.Top, lane.Right, lane.Top + 46 + 22 * 34), 6, 6, glass);
    using var row = new SKPaint { Color = new SKColor(255, 255, 255, 30) };
    using var tp = new SKPaint { Color = SKColors.White, TextSize = 18, IsAntialias = true, Typeface = Fonts.Inter.SemiBold };
    string head = boardIndex == 0 ? "OVERALL" : ClassLogo.DisplayName(scenes[boardIndex].Item1.ClassName);
    c.DrawText(head, lane.Left + 16, lane.Top + 30, tp);
    for (int i = 0; i < 22; i++)
    {
        float y = lane.Top + 46 + i * 34;
        if (i % 2 == 0) c.DrawRect(lane.Left + 6, y, lane.Width - 12, 32, row);
        c.DrawText($"{i + 1,2}   DRIVER {(char)('A' + (i + boardIndex * 5) % 26)}.", lane.Left + 14, y + 22, tp);
    }
}

// the splash renderer's wipe (SceneDirector.SplashReveal + TimingTowerSplashRenderer edge), copied for the preview
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
using var edgeLine = new SKPaint { IsAntialias = true };
using var edgeGlow = new SKPaint { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f) };

double drawMs = 0; long drawAlloc = 0; int drawN = 0, allocFrames = 0;
void Frame(SKCanvas c, int sceneIdx, int boardBefore, double t)
{
    var (scene, hex) = scenes[sceneIdx];
    double total = ProceduralStinger.DurationMs(scene);
    DrawWorld(c, t < total / 2 ? boardBefore : sceneIdx);
    var (top, bot) = Reveal(t, total);
    if (bot - top <= 0.001f) return;
    SKColor col = SKColor.Parse(hex);
    float cl = lane.Left + top * lane.Width, cr = lane.Left + bot * lane.Width;
    bool full = top <= 0.0001f && bot >= 0.9999f;
    c.Save();
    if (!full) c.ClipRect(new SKRect(cl, lane.Top, cr, lane.Bottom));
    long a0 = GC.GetAllocatedBytesForCurrentThread();
    long t0 = Stopwatch.GetTimestamp();
    stinger.Draw(c, lane, scene, col, t, total, null, null, null);
    drawMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    long da = GC.GetAllocatedBytesForCurrentThread() - a0; drawAlloc += da; drawN++; if (da > 0) allocFrames++;
    c.Restore();
    if (!full)
    {
        float ex = bot < 0.9999f ? cr : cl;
        var lc = CarArt.LightColor(col);
        edgeGlow.Color = lc.WithAlpha(90); edgeLine.Color = lc;
        c.DrawRect(new SKRect(ex - 5, lane.Top, ex + 5, lane.Bottom), edgeGlow);
        c.DrawRect(new SKRect(ex - 1.5f, lane.Top, ex + 1.5f, lane.Bottom), edgeLine);
    }
}

// warm-up (first draw builds shaders/blobs - not counted)
foreach (var wt in new[] { 100.0, 450, 1000, 1300, 5400 }) for (int ws = 0; ws < 4; ws++) Frame(canvas, ws, 0, wt);
drawMs = 0; drawAlloc = 0; drawN = 0; allocFrames = 0;

// ---- video ----
string ffmpeg = Environment.GetEnvironmentVariable("FFMPEG") ?? "ffmpeg";
string mp4 = Path.Combine(shots, "transition_preview.mp4");
var psi = new ProcessStartInfo(ffmpeg, $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {SW}x{SH} -r {FPS} -i - -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -movflags +faststart \"{mp4}\"")
{ RedirectStandardInput = true, UseShellExecute = false };
Process? ff = null;
try { ff = Process.Start(psi); } catch (Exception ex) { Console.WriteLine("ffmpeg not started: " + ex.Message); }
var raw = new byte[SW * SH * 4];
int frames = 0;
int prevBoard = 0;
for (int s = 0; s < scenes.Length; s++)
{
    double total = ProceduralStinger.DurationMs(scenes[s].Item1);
    int n = (int)Math.Ceiling((total + 600) / 1000.0 * FPS);   // the roller + 0.6 s of the new board
    for (int i = 0; i < n; i++)
    {
        Frame(canvas, s, prevBoard, i * 1000.0 / FPS);
        if (ff != null)
        {
            System.Runtime.InteropServices.Marshal.Copy(screen.GetPixels(), raw, 0, raw.Length);
            ff.StandardInput.BaseStream.Write(raw, 0, raw.Length);
        }
        frames++;
    }
    prevBoard = s;
}
if (ff != null) { ff.StandardInput.Close(); ff.WaitForExit(); Console.WriteLine($"wrote {mp4} ({frames} frames, exit {ff.ExitCode})"); }
Console.WriteLine($"ProceduralStinger.Draw (CPU raster, 1080p): {drawMs / Math.Max(1, drawN):F2} ms/frame avg, {drawAlloc / Math.Max(1, drawN)} managed bytes/frame over {drawN} frames ({allocFrames} frames allocated anything, {drawAlloc} bytes total)");

// ---- contact sheet: 8 moments of the GT3 roller (lane crops) ----
SKImage Crop(SKBitmap b, SKRectI r) { using var img = SKImage.FromBitmap(b); return img.Subset(r); }
var cropR = new SKRectI(0, 0, 432, SH);
double[] moments = { 120, 300, 450, 600, 900, 2400, 4900, 5400 };
using (var sheet = new SKBitmap(new SKImageInfo(cropR.Width * 8 + 9 * 12, SH + 70, SKColorType.Rgba8888, SKAlphaType.Premul)))
using (var sc = new SKCanvas(sheet))
using (var lp = new SKPaint { Color = SKColors.White, TextSize = 26, IsAntialias = true, Typeface = Fonts.Inter.SemiBold })
{
    sc.Clear(new SKColor(16, 18, 22));
    for (int k = 0; k < moments.Length; k++)
    {
        Frame(canvas, 3, 2, moments[k]);
        using var sub = Crop(screen, cropR);
        float x = 12 + k * (cropR.Width + 12);
        sc.DrawImage(sub, x, 12);
        sc.DrawText($"GT3  t = {moments[k] / 1000:0.00} s", x + 8, SH + 52, lp);
    }
    Save(sheet, Path.Combine(shots, "transition_contact.png"));
}

// ---- each class at its held frame ----
using (var sheet = new SKBitmap(new SKImageInfo(cropR.Width * 4 + 5 * 12, SH + 24, SKColorType.Rgba8888, SKAlphaType.Premul)))
using (var sc = new SKCanvas(sheet))
{
    sc.Clear(new SKColor(16, 18, 22));
    for (int s = 0; s < scenes.Length; s++)
    {
        Frame(canvas, s, 0, 1500);
        using var sub = Crop(screen, cropR);
        sc.DrawImage(sub, 12 + s * (cropR.Width + 12), 12);
    }
    Save(sheet, Path.Combine(shots, "transition_classes.png"));
}

// ---- 4K: same frame drawn at height/1080 = 2 (as OverlayCompositor does) ----
using (var big = new SKBitmap(new SKImageInfo(SW * 2, SH * 2, SKColorType.Rgba8888, SKAlphaType.Premul)))
using (var bc = new SKCanvas(big))
{
    bc.Scale(2f);
    Frame(bc, 1, 0, 1700);
    using var img = SKImage.FromBitmap(big);
    using var sub = img.Subset(new SKRectI(0, 0, 864, SH * 2));
    using var data = sub.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(Path.Combine(shots, "transition_4k_lane.png"), data.ToArray());
}

// ---- drawn car panels ----
var cars = new[] { ("BMW M4 GT3 EVO", "BMW", "GT3", "#00A94F"), ("Porsche 963 GTP", "Porsche", "GTP", "#000000"), ("Dallara P217", "Dallara", "LMP2", "#0072CE") };
using (var sheet = new SKBitmap(new SKImageInfo(960 * cars.Length + 12 * (cars.Length + 1), 540 + 24, SKColorType.Rgba8888, SKAlphaType.Premul)))
using (var sc = new SKCanvas(sheet))
using (var ip = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
{
    sc.Clear(new SKColor(16, 18, 22));
    for (int i = 0; i < cars.Length; i++)
    {
        var (name, brand, cls, hex) = cars[i];
        var art = CarArt.Drawn(name, brand, cls, hex);
        if (art != null) sc.DrawBitmap(art, new SKRect(12 + i * 972, 12, 12 + i * 972 + 960, 12 + 540), ip);
    }
    Save(sheet, Path.Combine(shots, "car_panel_preview.png"));
}
Console.WriteLine("wrote shots/transition_contact.png, transition_classes.png, transition_4k_lane.png, car_panel_preview.png");

static void Save(SKBitmap b, string path)
{
    using var img = SKImage.FromBitmap(b);
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(path, data.ToArray());
}

namespace ApexHUD.Native.Overlays
{
    // Copies of the two small types the stinger / car art need, so the preview doesn't pull in the WPF app.
    public enum SceneFocus { Overall, Class }
    public sealed record Scene(SceneFocus Focus, string? ClassName, string? ClassColor)
    {
        public string Label => Focus == SceneFocus.Overall ? "OVERALL" : ClassName ?? "";
    }
    internal static class TextTracked
    {
        public static void Draw(SKCanvas c, SKPaint paint, string text, float x, float y, float spacing, bool alignRight = false)
        {
            if (string.IsNullOrEmpty(text)) return;
            var w = paint.GetGlyphWidths(text);
            float total = 0; for (int i = 0; i < w.Length; i++) total += w[i] + (i == w.Length - 1 ? 0 : spacing);
            float sx = paint.TextAlign == SKTextAlign.Center ? x - total / 2 : paint.TextAlign == SKTextAlign.Right ? x - total : x;
            var prev = paint.TextAlign; paint.TextAlign = SKTextAlign.Left;
            for (int i = 0; i < text.Length; i++) { c.DrawText(text[i].ToString(), sx, y, paint); sx += w[i] + spacing; }
            paint.TextAlign = prev;
        }
    }
}
