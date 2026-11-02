// Session review preview (2026-10-27): renders SessionReviewShow offline from a real telemetry file.
//   dotnet session_review_preview.dll <file.ibt|review.json> <outDir> [--landmarks trackLandmarksData.json] [--metric]
//          [--keys t1,t2,...] [--video FROM-TO] [--fps 30] [--vscale 0.5] [--bench] [--size 3840x2160]
// Key frames are written as 4K PNGs (key_<t>.png); --video writes f00000.png... at --vscale for ffmpeg.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

string src = args[0], outDir = args[1];
Directory.CreateDirectory(outDir);
string? Opt(string k) { int i = Array.IndexOf(args, k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
bool Has(string k) => args.Contains(k);
if (Opt("--landmarks") is { } lm) TrackCorners.LandmarkFileOverride = lm;
SessionReviewShow.Log = s => Console.WriteLine("LOG " + s);
OverlayLog.Sink = s => Console.WriteLine("OVL " + s);
int W = 3840, H = 2160;
if (Opt("--size") is { } sz) { var p = sz.Split('x'); W = int.Parse(p[0]); H = int.Parse(p[1]); }

var sw = Stopwatch.StartNew();
SessionReviewData d = src.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
    ? JsonSerializer.Deserialize<SessionReviewData>(File.ReadAllText(src), SessionReviewData.Json)!
    : SessionReviewAnalyzer.Analyze(src) ?? throw new Exception("no completed lap in " + src);
Console.WriteLine($"analysed in {sw.ElapsedMilliseconds} ms: {d.SessionType} {d.TrackDisplay}, {d.Segments.Count} stops");
File.WriteAllText(Path.Combine(outDir, "review.json"), JsonSerializer.Serialize(d, new JsonSerializerOptions(SessionReviewData.Json) { WriteIndented = true }));

var show = new SessionReviewShow();
sw.Restart();
var M = show.BuildNow(d, !Has("--metric"), W, H);
Console.WriteLine($"model in {sw.ElapsedMilliseconds} ms, loop {M.Loop:0.0} s, pages {M.PageStart.Length}: " +
                  string.Join(" ", M.PageStart.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture))));

using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
var c = surf.Canvas;
void Frame(double t) { c.Clear(SKColors.Transparent); show.PrepareAt(M, t); show.Draw(c, M, W, H, t, 1f); }
void Save(string file, float scale)
{
    using var img = surf.Snapshot();
    if (scale >= 0.999f) { using var data = img.Encode(SKEncodedImageFormat.Png, 90); File.WriteAllBytes(file, data.ToArray()); return; }
    int w = (int)(W * scale), h = (int)(H * scale);
    using var bmp = new SKBitmap(w, h);
    using (var cv = new SKCanvas(bmp)) using (var p = new SKPaint { FilterQuality = SKFilterQuality.High }) cv.DrawImage(img, new SKRect(0, 0, w, h), p);
    using var d2 = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 85);
    File.WriteAllBytes(file, d2.ToArray());
}

if (Opt("--keys") is { } keys)
    foreach (var k in keys.Split(','))
    {
        double t = double.Parse(k, CultureInfo.InvariantCulture);
        // settle: pins are laid out once the camera has arrived, so run the page from its start up to t
        int p = 0; while (p < M.PageStart.Length - 1 && t >= M.PageStart[p + 1]) p++;
        for (double tt = M.PageStart[p]; tt < t; tt += 0.5) Frame(tt);
        Frame(t);
        Save(Path.Combine(outDir, $"key_{k}.png"), 1f);
        Console.WriteLine("key " + k);
    }

if (Opt("--video") is { } vr)
{
    var parts = vr.Split(',');
    int fps = int.Parse(Opt("--fps") ?? "30"); float vs = float.Parse(Opt("--vscale") ?? "0.5", CultureInfo.InvariantCulture);
    int fno = 0;
    foreach (var range in parts)
    {
        var ab = range.Split('-');
        double a = double.Parse(ab[0], CultureInfo.InvariantCulture), b = double.Parse(ab[1], CultureInfo.InvariantCulture);
        for (double t = a; t < b; t += 1.0 / fps) { Frame(t); Save(Path.Combine(outDir, $"f{fno++:00000}.png"), vs); }
    }
    Console.WriteLine($"video frames {fno}");
}

if (Has("--bench"))
{
    // steady frames of a stop page and the opener, pages already made (as on air): bytes + CPU per frame
    foreach (var (name, t0) in new[] { ("opener", 6.0), ("stop", M.PageStart[Math.Min(5, M.PageStart.Length - 2)] + 3.0), ("closer", M.PageStart[^1] + 6.0) })
    {
        show.PrepareAt(M, t0);
        for (int i = 0; i < 30; i++) show.Draw(c, M, W, H, t0 + i / 30.0, 1f);
        GC.Collect(); GC.WaitForPendingFinalizers();
        long b0 = GC.GetAllocatedBytesForCurrentThread(); var s2 = Stopwatch.StartNew();
        int n = 90;
        for (int i = 0; i < n; i++) { c.Clear(SKColors.Transparent); show.Draw(c, M, W, H, t0 + 1 + i / 30.0, 1f); }
        Console.WriteLine($"bench {name}: {(GC.GetAllocatedBytesForCurrentThread() - b0) / (double)n:0.0} B/frame, {s2.Elapsed.TotalMilliseconds / n:0.0} ms/frame (CPU raster {W}x{H})");
    }
}
