// Race-opener bench (2026-10-24, "the intro is horribly choppy"). Runs every beat of the pre-race opener
// through the REAL StartingGridRenderer / GridIntro / GlobeGT code frame by frame (60 fps clock, no audio) and
// reports, per beat: managed bytes allocated per frame (this thread), GC collections, CPU ms per frame on a
// no-draw canvas (our own geometry/CPU cost) and on a raster surface. Also benches GlobeGT.DrawSplash alone
// (like the owner's PC console bench), renders a contact sheet and dumps frames for a pixel comparison.
//
//   ./run.sh [--label before] [--sheet /abs/intro_before.png] [--frames /abs/DIR] [--scale 2] [--csv F] [--types]
//   (give paths absolute: the bench runs from its own home/ folder)
//   ./run.sh --compare DIR_A DIR_B
using System.Diagnostics;
using System.Text;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

var opt = new Dictionary<string, string>();
for (int i = 0; i < args.Length; i++)
    if (args[i].StartsWith("--")) opt[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "1";
if (opt.ContainsKey("help")) { Console.WriteLine("--label L --sheet PNG --frames DIR --scale S (2 = 4K) --csv FILE | --compare A B"); return; }
if (opt.TryGetValue("compare", out var cmpA)) { Compare(cmpA, args[^1]); return; }

string label = opt.GetValueOrDefault("label", "run");
float scale = float.Parse(opt.GetValueOrDefault("scale", "1"), System.Globalization.CultureInfo.InvariantCulture);
// --noglobe: no GT7 globe pictures, so the opener falls back to the dot globe (GlobeIntro), like a public install
FakeAssets.Ensure(Assets.Resolve(), globe: !opt.ContainsKey("noglobe"));
StartingGridRenderer.OpenerAudioEnabled = false;

var snap = Bench.MakeSnapshot();
// GS_CARS = a folder of iRacing car pictures (pk_car.png): injected for each class's top two (2026-10-28, #483)
if (Environment.GetEnvironmentVariable("GS_CARS") is { Length: > 0 } carsDir)
{
    var pics = Directory.GetFiles(carsDir, "*.png").OrderBy(x => x).ToArray();
    int pi = 0;
    // the bench drivers have no car folder; give every driver one (the picture lookup needs it)
    snap = snap with { ClassLeaderboard = snap.ClassLeaderboard.Select(cl => cl with { Drivers = cl.Drivers.Select(d => d with { CarPath = "bench" + d.CarIdx }).ToList() }).ToList() };
    foreach (var cl in snap.ClassLeaderboard)
        foreach (var d in cl.Drivers.OrderBy(d => d.PositionInClass).Take(2))
            if (pics.Length > 0) LiveryArt.Inject(d, File.ReadAllBytes(pics[pi++ % pics.Length]));
}
// the host calls this every frame long before the opener: let the globe pictures load in the background
var sw0 = Stopwatch.StartNew();
while (!(GlobeIntro.Ready(snap) && (GlobeGT.Ready(snap) || !GlobeGT.Installed)) && sw0.ElapsedMilliseconds < 60000) { GlobeIntro.Prepare(snap); Thread.Sleep(20); }
Console.WriteLine($"[{label}] globe ready: {GlobeIntro.Ready(snap)} after {sw0.ElapsedMilliseconds} ms (GlobeGT {GlobeGT.Ready(snap)})");

if (opt.TryGetValue("outro", out var outroPng))
{
    // the post-race pages (#486): outro forced on a finished race; one PNG per second of each full-screen page
    var pr = snap with { Phase = RacePhase.PostRace, SessionTypeName = "Race",
        ClassLeaderboard = snap.ClassLeaderboard.Select((cl, ci) => cl with { Drivers = cl.Drivers.Select(d => { int i = Math.Max(0, d.PositionInClass - 1);
            return d with { BestLapTime = 200.3f + ci * 6 + i * 0.137f, GapToLeaderSeconds = ci * 40 + i * 3.412, LapsCompleted = i >= 10 ? 25 : 26 }; }).ToList() }).ToList() };
    using var orr = new OutroRenderer();
    using var os = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
    OutroDirector.Tick(pr, 0); OutroDirector.Force(pr, 0);
    foreach (double tt in new[] { 1.0, 5.0, 8.0, 12.0, 16.0, 19.0, 24.0, 28.0 })
    {
        OutroDirector.Tick(pr, tt * 1000);
        if (!OutroDirector.FullScreen) continue;
        os.Canvas.Clear(new SKColor(60, 80, 60));
        orr.Draw(os.Canvas, 1920, 1080, pr, tt * 1000);
        using var im = os.Snapshot(); using var dat = im.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outroPng.Replace(".png", $"_{tt:00}.png"), dat.ToArray());
    }
    Console.WriteLine("outro pages written"); return;
}
if (opt.TryGetValue("hero", out var heroPng))
{
    // one class page of the grid board, fully built (2026-10-28, #483 preview)
    using var hb = new GridHeroBoard();
    using var hs = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
    for (int ci = 0; ci < snap.ClassLeaderboard.Count; ci++)
    {
        hs.Canvas.Clear(new SKColor(20, 24, 30));
        hb.DrawClassPage(hs.Canvas, new SKRect(60, 60, 1860, 1020), snap.ClassLeaderboard, snap.PlayerCarIdx, 1f, ci, double.Parse(opt.GetValueOrDefault("t", "99"), System.Globalization.CultureInfo.InvariantCulture));
        using var im = hs.Snapshot(); using var dat = im.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(heroPng.Replace(".png", $"_{ci}.png"), dat.ToArray());
    }
    Console.WriteLine("hero pages written"); return;
}
int W = (int)(1920 * scale), H = (int)(1080 * scale);
Console.WriteLine($"[{label}] surface {W}x{H} (ui scale {scale})");

if (opt.ContainsKey("types"))
{
    // what allocates, per beat (warm, no-draw canvas)
    using var types = new AllocTypes();
    Bench.Timeline(snap, W, H, scale, raster: false, null);
    for (int bi = 0; bi < Bench.Beats.Length; bi++)
    {
        Bench.OnlyBeat = bi; Bench.Types = types;
        Bench.Timeline(snap, W, H, scale, raster: false, null);
        Thread.Sleep(1500); types.On = false; types.Dump(Bench.Beats[bi].Name);
    }
    return;
}

// 1) GlobeGT.DrawSplash alone, like the PC console bench
Bench.GlobeOnly(snap, W, H, raster: true, "globe-only raster");
Bench.GlobeOnly(snap, W, H, raster: false, "globe-only nodraw");

if (opt.TryGetValue("probe", out var probeT))
{
    // one frozen moment drawn thousands of times with allocation sampling on: what does a steady frame allocate?
    using var types = new AllocTypes();
    double t = double.Parse(probeT, System.Globalization.CultureInfo.InvariantCulture);
    Bench.Probe(snap, W, H, scale, t, types);
    return;
}
if (opt.ContainsKey("quick"))
{
    // no-draw passes only (fast): cold then warm, per-beat bytes / ms
    var q1 = Bench.Timeline(snap, W, H, scale, raster: false, null);
    var q2 = Bench.Timeline(snap, W, H, scale, raster: false, null);
    Bench.Report(label + " quick (no raster: 'cold'/'raster' columns are no-draw too)", q1, q2, q2);
    return;
}

// 2) the whole opener through StartingGridRenderer: cold raster pass (first time, as on air), then warm passes
var cold = Bench.Timeline(snap, W, H, scale, raster: true, null);
var warmNo = Bench.Timeline(snap, W, H, scale, raster: false, null);
var cap = new Captures(opt.GetValueOrDefault("frames"), W, H);
var warmRa = Bench.Timeline(snap, W, H, scale, raster: true, cap);
Bench.Report(label, cold, warmNo, warmRa);
if (opt.TryGetValue("csv", out var csv)) Bench.Csv(csv, cold, warmNo, warmRa);
if (opt.TryGetValue("sheet", out var sheet)) cap.SaveSheet(sheet, label);

static void Compare(string a, string b)
{
    int files = 0, worstPx = 0; long total = 0;
    foreach (var fa in Directory.GetFiles(a, "*.png").OrderBy(x => x))
    {
        var fb = Path.Combine(b, Path.GetFileName(fa));
        if (!File.Exists(fb)) { Console.WriteLine($"{Path.GetFileName(fa)}: missing in B"); continue; }
        using var A = SKBitmap.Decode(fa); using var B = SKBitmap.Decode(fb);
        int diff = 0, big = 0, max = 0;
        for (int y = 0; y < A.Height; y++)
            for (int x = 0; x < A.Width; x++)
            {
                var p = A.GetPixel(x, y); var q = B.GetPixel(x, y);
                int d = Math.Max(Math.Max(Math.Abs(p.Red - q.Red), Math.Abs(p.Green - q.Green)), Math.Max(Math.Abs(p.Blue - q.Blue), Math.Abs(p.Alpha - q.Alpha)));
                if (d > 0) diff++; if (d > 2) big++; if (d > max) max = d;
            }
        files++; total += diff; worstPx = Math.Max(worstPx, max);
        Console.WriteLine($"{Path.GetFileName(fa),-22} differing px {diff,8} ({100.0 * diff / (A.Width * A.Height):0.000}%)  >2 levels {big,7}  max diff {max}");
    }
    Console.WriteLine($"{files} frames compared, {total} differing pixels in all, worst channel difference {worstPx}");
}

sealed record Beat(string Name, double From, double To);
sealed class FrameRec { public double T; public long Bytes; public double Ms; public int Beat; }
sealed class Run
{
    public string Kind = "";
    public List<FrameRec> Frames = new();
    public int[,] Gc = new int[Bench.Beats.Length, 3];
}

sealed class Captures
{
    public static readonly double[] Times = { 1.0, 3.2, 4.45, 5.6, 6.8, 7.9, 8.3, 9.7, 11.2, 15.0, 20.3, 34.3 };
    readonly string? dir; readonly int w, h;
    public readonly List<(double T, SKBitmap Tile)> Tiles = new();
    public Captures(string? dir, int w, int h) { this.dir = dir; this.w = w; this.h = h; if (dir != null) Directory.CreateDirectory(dir); }
    public void Take(SKSurface s, double t)
    {
        using var img = s.Snapshot();
        using var full = SKBitmap.FromImage(img);
        if (dir != null)
        {
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(dir, $"t{t:00.00}.png"), data.ToArray());
        }
        var tile = full.Resize(new SKImageInfo(480, 270), SKFilterQuality.Medium);
        Tiles.Add((t, tile));
    }
    public void SaveSheet(string path, string label)
    {
        int cols = 4, rows = (Tiles.Count + cols - 1) / cols;
        using var surf = SKSurface.Create(new SKImageInfo(cols * 480 + (cols + 1) * 8, rows * 270 + (rows + 1) * 8 + 40));
        var c = surf.Canvas; c.Clear(new SKColor(24, 24, 28));
        using var tp = new SKPaint { Color = SKColors.White, TextSize = 22, IsAntialias = true };
        c.DrawText($"Grandstand opener - {label} (t = seconds into the theme)", 10, 28, tp);
        tp.TextSize = 16;
        for (int i = 0; i < Tiles.Count; i++)
        {
            float x = 8 + (i % cols) * 488, y = 48 + (i / cols) * 278;
            c.DrawBitmap(Tiles[i].Tile, x, y);
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 160) };
            c.DrawRect(x, y, 90, 24, bg);
            c.DrawText($"t={Tiles[i].T:0.00}", x + 6, y + 18, tp);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var img = surf.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, d.ToArray());
        Console.WriteLine($"contact sheet -> {path}");
    }
}

static class Bench
{
    public static readonly Beat[] Beats =
    {
        new("iracing page", 0, GridIntro.SeriesEarlyS),
        new("series page", GridIntro.SeriesEarlyS, GridIntro.GlobeInS),
        new("globe", GridIntro.GlobeInS, GridIntro.GlobeOutS),
        new("track page", GridIntro.TrackS, GridIntro.GridS),
        new("class 1 (GT3)", GridIntro.GridS, GridIntro.Class2S),
        new("class 2 (LMP2)", GridIntro.Class2S, GridIntro.Class3S),
        new("class 3 (GTP)", GridIntro.Class3S, GridIntro.BoardOutS),
        new("hold + dive", GridIntro.BoardOutS, GridIntro.BoardOutS + GridIntro.HoldAfterBoardS + GridIntro.PlateHoldS + GridIntro.DissolveS + 0.2),
    };
    public static AllocTypes? Types;
    public static int OnlyBeat = -1;   // --types: draw every frame (state must advance) but only one beat matters
    static int BeatOf(double t) { for (int i = 0; i < Beats.Length; i++) if (t >= Beats[i].From && t < Beats[i].To) return i; return -1; }

    // Strings made once (iRacing's session strings don't change frame to frame); the records themselves are
    // rebuilt for every frame, as the data layer does, so caches keyed on object identity would miss as on air.
    static readonly string[] Brands = { "BMW", "Porsche", "Ferrari", "Mercedes-AMG", "Audi", "Lamborghini", "McLaren", "Aston Martin", "Ford", "Chevrolet" };
    static readonly (string Name, string Col, string Car)[] Cls = { ("GTP", "0xFFDA59", "BMW M Hybrid V8"), ("LMP2", "0x33CEFF", "Dallara P217"), ("GT3", "0xFF5888", "BMW M4 GT3") };
    static readonly string[,] Names = MakeNames();
    static string[,] MakeNames()
    {
        var n = new string[36, 3];
        for (int i = 0; i < 36; i++) { var c = Cls[i / 12].Name; n[i, 0] = $"Driver {c} {i % 12 + 1}"; n[i, 1] = $"D. {c}{i % 12 + 1}"; n[i, 2] = (10 + i).ToString(); }
        return n;
    }
    static readonly SessionExtras Extras = SessionExtras.Empty with { TrackCity = "Le Mans", TrackCountry = "France", TrackLength = "13.63 km", TrackTurns = 38 };

    public static RaceSnapshot MakeSnapshot()
    {
        var classes = new List<DriverClass>();
        int idx = 0;
        foreach (var (name, col, car) in Cls)
        {
            var ds = new List<Driver>();
            for (int i = 0; i < 12; i++, idx++)
                ds.Add(new Driver(idx, Names[idx, 0], Names[idx, 1], "DR", Names[idx, 2], car, Brands[(idx * 3) % Brands.Length], name, col,
                    "A 4.99", "0x0153DB", idx + 1, i + 1, 0, 0.9f, 0, 0, 0, 0, null, null, false, true, false, false, 2500 + 37 * idx, QualifyTime: 100.5f + i * 0.21f));
            classes.Add(new DriverClass(name, col, ds));
        }
        return RaceSnapshot.Disconnected with
        {
            IsConnected = true, Phase = RacePhase.PreRace, SessionState = 3, SessionTypeName = "Race",
            TrackRawName = "lemans full", TrackDisplayName = "Circuit des 24 Heures du Mans", TrackBroadcastName = "Circuit de la Sarthe",
            TrackCountryCode = "FR", SeriesId = 447, PlayerCarIdx = 30, ClassLeaderboard = classes, SessionTimeOfDay = 13 * 3600 + 30 * 60,
            Extras = Extras, TrackLatitude = 47.9497, TrackLongitude = 0.2242, TrackConfigName = "",
        };
    }

    static (int, int, int) GcNow() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

    public static void GlobeOnly(RaceSnapshot snap, int W, int H, bool raster, string name)
    {
        using var surf = raster ? SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul)) : null;
        using var nd = raster ? null : new SKNoDrawCanvas(W, H);
        var cv = raster ? surf!.Canvas : nd!;
        var r = new SKRect(0, 0, W, H);
        // as on air: the opener warms the globe during the pages before it (pictures, place names, buffer sizes)
        for (int i = 0; i < 30; i++) { GlobeGT.Warm(cv, r); Thread.Sleep(10); }
        Thread.Sleep(500);
        GlobeGT.DrawSplash(cv, r, snap, 0.5, 1);   // JIT
        int n = (int)Math.Round(GridIntro.GlobeBarS * 60);
        var g0 = GcNow(); long tot = 0, max = 0; double ms = 0, msMax = 0;
        for (int k = 0; k < n; k++)
        {
            double u = k / 60.0;
            if (raster) cv.Clear(SKColors.Black);
            long b0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
            GlobeGT.DrawSplash(cv, r, snap, u, 1);
            double dt = Stopwatch.GetElapsedTime(t0).TotalMilliseconds; long b = GC.GetAllocatedBytesForCurrentThread() - b0;
            tot += b; max = Math.Max(max, b); ms += dt; msMax = Math.Max(msMax, dt);
        }
        var g1 = GcNow();
        Console.WriteLine($"{name,-20} {W}x{H}: {n} frames  alloc/frame avg {tot / n / 1024.0,8:0.0} KB  max {max / 1024.0,8:0.0} KB   gen0/1/2 {g1.Item1 - g0.Item1}/{g1.Item2 - g0.Item2}/{g1.Item3 - g0.Item3}   cpu ms/frame avg {ms / n,6:0.00} max {msMax,6:0.00}");
    }

    public static Run Timeline(RaceSnapshot snap, int W, int H, float scale, bool raster, Captures? cap)
    {
        var run = new Run { Kind = raster ? "raster" : "nodraw" };
        using var grid = new StartingGridRenderer();
        using var surf = raster ? SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul)) : null;
        using var nd = raster ? null : new SKNoDrawCanvas(W, H);
        var cv = raster ? surf!.Canvas : nd!;
        int lw = (int)Math.Round(W / scale), lh = (int)Math.Round(H / scale);
        float ox = (lw - 1100f) / 2f + 16f, oy = Math.Max(0f, (lh - 830f) / 2f) + 16f;
        const double FrameMs = 1000.0 / 60.0;
        double now = 50_000;
        void Frame(RaceSnapshot s, double tNow)
        {
            if (raster) cv.Clear(SKColors.Transparent);
            cv.Save(); if (scale != 1f) cv.Scale(scale);
            grid.ScreenRect = new SKRect(0, 0, lw, lh);
            grid.Draw(cv, ox, oy, s, tNow);
            cv.Restore();
        }
        var gridding = snap with { SessionState = 1 };
        for (int i = 0; i < 60; i++, now += FrameMs) Frame(gridding, now);   // a second of gridding: nothing shows yet
        double launch = now;
        double end = Beats[^1].To;
        var capAt = Captures.Times.Select(t => (int)Math.Round(t * 60)).ToHashSet();
        var g = new (int, int, int)[Beats.Length + 1];
        int lastBeat = -2;
        for (int k = 0; k / 60.0 < end; k++)
        {
            double t = k / 60.0, tNow = launch + k * FrameMs;
            int beat = BeatOf(t);
            if (beat != lastBeat)
            {
                var gn = GcNow();
                if (lastBeat >= 0) { run.Gc[lastBeat, 0] += gn.Item1 - g[lastBeat].Item1; run.Gc[lastBeat, 1] += gn.Item2 - g[lastBeat].Item2; run.Gc[lastBeat, 2] += gn.Item3 - g[lastBeat].Item3; }
                if (beat >= 0) g[beat] = gn;
                lastBeat = beat;
            }
            if (OnlyBeat >= 0 && beat > OnlyBeat) break;
            if (Types != null && beat == OnlyBeat && !Types.On) { Thread.Sleep(1500); Types.On = true; }
            var fresh = MakeSnapshot();   // a new snapshot every frame, like the data layer (not measured)
            long b0 = GC.GetAllocatedBytesForCurrentThread(); long t0 = Stopwatch.GetTimestamp();
            Frame(fresh, tNow);
            if (raster) cv.Flush();
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds; long b = GC.GetAllocatedBytesForCurrentThread() - b0;
            run.Frames.Add(new FrameRec { T = t, Bytes = b, Ms = ms, Beat = beat });
            if (cap != null && raster && capAt.Contains(k)) cap.Take(surf!, Math.Round(t, 2));
        }
        if (lastBeat >= 0) { var gn = GcNow(); run.Gc[lastBeat, 0] += gn.Item1 - g[lastBeat].Item1; run.Gc[lastBeat, 1] += gn.Item2 - g[lastBeat].Item2; run.Gc[lastBeat, 2] += gn.Item3 - g[lastBeat].Item3; }
        return run;
    }

    public static void Probe(RaceSnapshot snap, int W, int H, float scale, double t, AllocTypes types)
    {
        using var grid = new StartingGridRenderer();
        using var nd = new SKNoDrawCanvas(W, H);
        int lw = (int)Math.Round(W / scale), lh = (int)Math.Round(H / scale);
        float ox = (lw - 1100f) / 2f + 16f, oy = Math.Max(0f, (lh - 830f) / 2f) + 16f;
        double now = 50_000;
        void Frame(RaceSnapshot s, double tNow) { nd.Save(); if (scale != 1f) nd.Scale(scale); grid.ScreenRect = new SKRect(0, 0, lw, lh); grid.Draw(nd, ox, oy, s, tNow); nd.Restore(); }
        var gridding = snap with { SessionState = 1 };
        for (int i = 0; i < 60; i++, now += 1000 / 60.0) Frame(gridding, now);
        double launch = now;
        for (int k = 0; k / 60.0 <= t; k++) Frame(snap, launch + k * 1000 / 60.0);
        double at = launch + t * 1000;
        long b0 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++) Frame(snap, at);
        Console.WriteLine($"probe t={t}: {(GC.GetAllocatedBytesForCurrentThread() - b0) / 200.0:0} bytes per frame (same snapshot)");
        Thread.Sleep(1500); types.On = true;
        for (int i = 0; i < 20000; i++) Frame(snap, at);
        Thread.Sleep(1500); types.On = false; types.Dump($"t={t}");
    }

    public static void Report(string label, Run cold, Run warmNo, Run warmRa)
    {
        Console.WriteLine();
        Console.WriteLine($"[{label}] per beat. 'first' = the beat's first frame on the cold (first-ever) raster pass; steady = mean of the warm passes' frames after the first 3.");
        Console.WriteLine($"{"beat",-16}{"frames",7} | {"cold first KB",13}{"cold max KB",12}{"gc0/1/2",10} | {"nodraw KB/f",12}{"median",8}{"max KB",9}{"ms/f",7}{"max ms",8} | {"raster KB/f",12}{"ms/f",7}{"max ms",8}{"gc0/1/2",9}");
        for (int bi = 0; bi < Beats.Length; bi++)
        {
            var c = cold.Frames.Where(f => f.Beat == bi).ToList();
            var n = warmNo.Frames.Where(f => f.Beat == bi).Skip(3).ToList();
            var r = warmRa.Frames.Where(f => f.Beat == bi).Skip(3).ToList();
            if (c.Count == 0) continue;
            Console.WriteLine($"{Beats[bi].Name,-16}{c.Count,7} | {c[0].Bytes / 1024.0,13:0.0}{c.Max(f => f.Bytes) / 1024.0,12:0.0}{$"{cold.Gc[bi, 0]}/{cold.Gc[bi, 1]}/{cold.Gc[bi, 2]}",10} | " +
                              $"{n.Average(f => f.Bytes) / 1024.0,12:0.00}{Median(n) / 1024.0,8:0.00}{n.Max(f => f.Bytes) / 1024.0,9:0.0}{n.Average(f => f.Ms),7:0.00}{n.Max(f => f.Ms),8:0.00} | " +
                              $"{r.Average(f => f.Bytes) / 1024.0,12:0.00}{r.Average(f => f.Ms),7:0.00}{r.Max(f => f.Ms),8:0.00}{$"{warmRa.Gc[bi, 0]}/{warmRa.Gc[bi, 1]}/{warmRa.Gc[bi, 2]}",9}");
        }
        Console.WriteLine($"{"whole opener",-16}{cold.Frames.Count,7} | total cold alloc {cold.Frames.Sum(f => f.Bytes) / 1048576.0:0.0} MB, warm nodraw {warmNo.Frames.Sum(f => f.Bytes) / 1048576.0:0.0} MB, warm raster {warmRa.Frames.Sum(f => f.Bytes) / 1048576.0:0.0} MB");
    }

    static double Median(List<FrameRec> f) { var b = f.Select(x => x.Bytes).OrderBy(x => x).ToList(); return b.Count == 0 ? 0 : b[b.Count / 2]; }

    public static void Csv(string path, params Run[] runs)
    {
        var sb = new StringBuilder("pass,t,beat,bytes,ms\n");
        string[] names = { "cold-raster", "warm-nodraw", "warm-raster" };
        for (int i = 0; i < runs.Length; i++)
            foreach (var f in runs[i].Frames) sb.Append(System.FormattableString.Invariant($"{names[i]},{f.T:0.000},{f.Beat},{f.Bytes},{f.Ms:0.000}\n"));
        File.WriteAllText(path, sb.ToString());
    }
}
