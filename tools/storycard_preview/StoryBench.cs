// #604 perf pass 2 (2026-10-30): every story card at 4K on Skia's OpenGL path (headless Mesa, tools/stinger_bench/Egl.cs),
// frame by frame through its whole showing (--step MS of card time per frame, default 66.7) - the CPU time per frame (draw calls + flush, i.e. what
// Skia spends on the render thread feeding the graphics card), split into the entrance (0-1.5 s) and the hold, the
// managed bytes per frame, and PNG frames at fixed moments so a before and an after build can be compared pixel by
// pixel (tools/stinger_bench --compare).
//   storycard_preview OUT --storybench [--label L] [--raster] [--frames DIR] [--layouts classic,mixed] [--only PitStops]
// "mixed" (the default layout on air) shows each regular card in the variation it gets first, the special partner
// cards (Michelin pit tower, GO FAST) in the tall column.
using System.Diagnostics;
using System.Reflection;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class StoryBench
{
    public static void Run(string outDir, RaceSnapshot snap, FieldInfo? layoutField, string[] args,
                           List<(string Name, StoryKind Kind, string Weather, Action? Setup)> cards)
    {
        string label = Arg(args, "--label") ?? "run";
        bool raster = args.Contains("--raster");
        string? framesDir = Arg(args, "--frames");
        var layouts = (Arg(args, "--layouts") ?? "classic,mixed").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var gr = raster ? null : Egl.Create() ?? throw new InvalidOperationException("no GL");
        gr?.SetResourceCacheLimit(1024L * 1024 * 1024);
        if (gr != null) GpuContext.Current = gr;   // as the host does
        if (Environment.GetEnvironmentVariable("GS_NOPLATES") != null)   // debugging: every screen-grid picture drawn live
        {
            var asm = typeof(StoryCards).Assembly;
            var plate = asm.GetType("ApexHUD.Native.Overlays.ScreenPlate`1");
            foreach (var kt in new[] { asm.GetType("ApexHUD.Native.Overlays.BlurPlates+Key"), asm.GetType("ApexHUD.Native.Overlays.StoryCards+GrainKey") })
                if (plate != null && kt != null) plate.MakeGenericType(kt).GetField("Enabled")!.SetValue(null, false);
        }
        var info = new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = gr != null ? SKSurface.Create(gr, true, info) : SKSurface.Create(info);
        var c = surf.Canvas;
        const float S = 2f;
        double step = double.Parse(Arg(args, "--step") ?? "66.6667", System.Globalization.CultureInfo.InvariantCulture);   // card clock step (default 15 frames a second of card time: the bench at 4K on emulated graphics is slow)
        double[] dumpAt = (Arg(args, "--dumpat") ?? "400,900,1500,3000,5200,8600").Split(',').Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Console.WriteLine($"[{label}] story cards at 3840x2160 ({(gr != null ? "Skia GL on headless Mesa: ms = this thread's CPU incl. flush" : "CPU raster")})");
        Console.WriteLine($"[{label}] {"layout/card",-26} {"in avg",7} {"in max",7} {"hold avg",8} {"hold max",8} {"exit avg",8}  {"B/frame",9}  {"gpu wait",8}");
        var rows = new List<string> { "layout,card,in_avg_ms,in_max_ms,hold_avg_ms,hold_max_ms,exit_avg_ms,bytes_per_frame,emulated_gpu_wait_ms" };
        double grandSum = 0; int grandN = 0; long grandB = 0;
        foreach (var layout in layouts)
        {
            if (layoutField != null)
            {
                var names = Enum.GetNames(layoutField.FieldType);
                var nm = names.FirstOrDefault(n => n.Equals(layout, StringComparison.OrdinalIgnoreCase));
                if (nm == null) continue;
                layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, nm));
            }
            foreach (var card in cards)
            {
                card.Setup?.Invoke();
                var sc = new StoryCards();
                // warm-up showing (first-use costs: fonts, blobs, pictures - the host has these from earlier cards)
                StoryDirector.Force(card.Kind, 0, card.Weather);
                double end = StoryDirector.ExitAtMs + StoryDirector.ExitMs + 50;
                for (double q = 0; q < end; q += 500) { c.Clear(SKColors.Transparent); c.Save(); c.Scale(S); sc.Draw(c, 1920, 1080, snap, q); c.Restore(); c.Flush(); gr?.Flush(); }
                if (gr != null) Egl.Finish();
                StoryDirector.Force(card.Kind, 0, card.Weather);
                var inMs = new List<double>(); var holdMs = new List<double>(); var exitMs = new List<double>();
                long bytes = 0; int n = 0; double wait = 0;
                int di = 0;
                for (double q = 0; q < end; q += step)
                {
                    c.Clear(SKColors.Transparent);
                    if (gr != null) { c.Flush(); gr.Flush(); Egl.Finish(); }
                    long b0 = GC.GetAllocatedBytesForCurrentThread();
                    long t0 = Stopwatch.GetTimestamp();
                    c.Save(); c.Scale(S);
                    sc.Draw(c, 1920, 1080, snap, q);
                    c.Restore();
                    c.Flush(); gr?.Flush();
                    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    bytes += GC.GetAllocatedBytesForCurrentThread() - b0; n++;
                    long w0 = Stopwatch.GetTimestamp();
                    if (gr != null) Egl.Finish();
                    wait += Stopwatch.GetElapsedTime(w0).TotalMilliseconds;
                    (q < 1500 ? inMs : q < StoryDirector.ExitAtMs ? holdMs : exitMs).Add(ms);
                    if (framesDir != null && di < dumpAt.Length && q >= dumpAt[di])
                    {
                        Directory.CreateDirectory(framesDir);
                        using var img = surf.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100);
                        File.WriteAllBytes(Path.Combine(framesDir, $"{layout}_{card.Name}_{(int)dumpAt[di]:00000}.png"), d.ToArray());
                        di++;
                    }
                }
                static double Avg(List<double> l) => l.Count > 0 ? l.Average() : 0;
                static double Max(List<double> l) => l.Count > 0 ? l.Max() : 0;
                Console.WriteLine($"[{label}] {layout + "/" + card.Name,-26} {Avg(inMs),7:F2} {Max(inMs),7:F2} {Avg(holdMs),8:F2} {Max(holdMs),8:F2} {Avg(exitMs),8:F2}  {bytes / Math.Max(1, n),9}  {wait / Math.Max(1, n),8:F2}");
                rows.Add($"{layout},{card.Name},{Avg(inMs):F3},{Max(inMs):F3},{Avg(holdMs):F3},{Max(holdMs):F3},{Avg(exitMs):F3},{bytes / Math.Max(1, n)},{wait / Math.Max(1, n):F3}");
                grandSum += inMs.Sum() + holdMs.Sum() + exitMs.Sum(); grandN += n; grandB += bytes;
                RaceSim.UndoDq();
            }
        }
        Console.WriteLine($"[{label}] ALL cards: avg {grandSum / Math.Max(1, grandN):F2} ms/frame, {grandB / Math.Max(1, grandN)} B/frame");
        File.WriteAllLines(Path.Combine(outDir, $"storybench_{label}.csv"), rows);
    }

    static string? Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
