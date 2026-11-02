// TV loading splash preview (2026-10-27, round 2: shows the #336 local-time watch). Renders LoadingSplash (GpuHost, linked
// as source) offline: a 4K key frame, hand-off frames, and per-frame allocation (idle / hand-off).
//   dotnet splash_preview.dll <outDir> <map json | ->     (see run.sh / run.ps1)
using SkiaSharp;
using ApexHUD.Native.GpuHost;

namespace ApexHUD.Native.GpuHost { internal static class Program { public static void Log(string m) => Console.WriteLine("LOG " + m); } }

static class Run
{
    static void Main(string[] args)
    {
        string outDir = args[0], map = args[1];
        int w = 3840, h = 2160;
        var s = new LoadingSplash();
        bool visible = false; s.SetHostVisible = v => visible = v;
        var content = new SplashContent { Width = w, Height = h, Label = "COMING UP", Title = "Circuit des 24 Heures du Mans",
            Subtitle = "24 Heures du Mans  ·  Full Circuit", Status = "LOADING RACE  ·  54 CARS", MapFile = map == "-" ? null : map,
            Cells = new[] { new SplashCell("SESSION", "PRACTICE", true), new SplashCell("CLASSES", "GTP  ·  LMP2  ·  GT3", false),
                            new SplashCell("LOCAL TIME", "14:30", false), new SplashCell("TRACK", "DRY  ·  22°C", false) } };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        s.SetContent(content);
        while (!s.FrameNeeded(0) ) { s.Send(LoadingSplash.Command.Show); Thread.Sleep(5); if (sw.ElapsedMilliseconds > 20000) throw new Exception("no bake"); }
        Console.WriteLine($"bake+show {sw.ElapsedMilliseconds} ms visible={visible}");
        using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;
        void Frame(double now) { c.Clear(SKColors.Black); s.FrameNeeded(now); s.Render(c, w, h, now); }
        void Save(string name, int sz) {
            using var img = surf.Snapshot();
            using var bmp = new SKBitmap(sz == w ? w : 1920, sz == w ? h : 1080);
            using (var cv = new SKCanvas(bmp)) { cv.DrawImage(img, new SKRect(0, 0, bmp.Width, bmp.Height), new SKPaint { FilterQuality = SKFilterQuality.High }); }
            using var f = File.Create(Path.Combine(outDir, name)); bmp.Encode(f, SKEncodedImageFormat.Png, 90);
        }
        // idle frames 0..5 s at 30 fps; measure allocation from 1 s
        long alloc0 = 0; int nIdle = 0; double ms = 0;
        for (int i = 0; i <= 150; i++)
        {
            double now = i * 1000.0 / 30;
            if (i == 30) { GC.Collect(); alloc0 = GC.GetAllocatedBytesForCurrentThread(); sw.Restart(); }
            Frame(now);
            if (i >= 30) nIdle++;
            if (i == 78) Save("keyframe_2.6s.png", w);
        }
        long idleBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0; ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"idle: {nIdle} frames, {idleBytes / (double)nIdle:F1} B/frame, {ms / nIdle:F1} ms/frame (CPU raster 4K)");
        // long idle run, measured after JIT tiering has settled
        for (int i = 0; i < 400; i++) Frame(5000 + i * 33.3);
        long a2 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 400; i < 700; i++) Frame(5000 + i * 33.3);
        Console.WriteLine($"idle (warm): {(GC.GetAllocatedBytesForCurrentThread() - a2) / 300.0:F1} B/frame");
        s.Send(LoadingSplash.Command.HandOff);
        long a1 = GC.GetAllocatedBytesForCurrentThread(); int nh = 0;
        for (int i = 150; i <= 200; i++)
        {
            double now = 5000 + 700*33.3 + (i - 150) * 1000.0 / 60;
            Frame(now); nh++;
            if (i == 150 + 42) Save("handoff_mid.png", 1920);   // 700 ms in = h 0.5
            if (i == 150 + 21) Save("handoff_q1.png", 1920);
            if (i == 150 + 63) Save("handoff_q3.png", 1920);
        }
        Console.WriteLine($"hand-off: {nh} frames, {(GC.GetAllocatedBytesForCurrentThread() - a1) / (double)nh:F1} B/frame, visible={visible}");
        for (int i = 0; i < 40; i++) { Frame(5000 + 700*33.3 + 1500 + i * 16.6); }
        double T0 = 40000;
        s.Send(LoadingSplash.Command.Show);
        for (int i = 0; i < 90; i++) Frame(T0 + i * 33.3);
        s.Send(LoadingSplash.Command.HandOff);
        long a3 = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 90; i++) Frame(T0 + 3000 + i * 16.6);
        Console.WriteLine($"second show+hand-off: {(GC.GetAllocatedBytesForCurrentThread() - a3) / 90.0:F1} B/frame, visible={visible}");
        Console.WriteLine($"after hand-off visible={visible}, frameNeeded={s.FrameNeeded(5000 + 700*33.3 + 3000)}");
    }
}
