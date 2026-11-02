// #505 bench (2026-10-30): the Michelin pit card's first seconds, frame by frame on a 60 fps clock, at 4K (scale 2) on
// Skia's OpenGL path (headless Mesa, tools/stinger_bench/Egl.cs) - the CPU time per frame (draws + flush), how many
// frames miss a 60 fps slot, and when the running man first moves.
//   storycard_preview OUT --pitbench [--label L] [--cold]   (--cold: the card's pictures were dropped from the graphics
//   card before it came in - what the GPU diet's 2-minute purge did to the dep49 warm-up)
//   storycard_preview OUT --pitclip [--label L]              (PNG frames of the card's first 2.5 s, 30 fps, 1080p)
// GS_MICHELIN_RUN = a running-man frame folder to copy into this tool's own home (never shipped, never committed).
using System.Diagnostics;
using System.Reflection;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class PitBench
{
    static void CopyFrames()
    {
        if (Environment.GetEnvironmentVariable("GS_MICHELIN_RUN") is not { Length: > 0 } src || !Directory.Exists(src)) return;
        var docs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "corner_logos");
        var dst = Path.Combine(docs, "michelin_run");
        if (string.Equals(Path.GetFullPath(src).TrimEnd('/'), Path.GetFullPath(dst).TrimEnd('/'), StringComparison.Ordinal)) return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        if (Environment.GetEnvironmentVariable("GS_MICHELIN_OUTRO") is { Length: > 0 } o && File.Exists(o))
            File.Copy(o, Path.Combine(docs, "michelin outro.png"), true);
    }

    static void UseColumn(FieldInfo? layoutField)
    {
        if (layoutField != null && Enum.GetNames(layoutField.FieldType).Contains("Mixed"))
            layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, "Mixed"));
    }

    public static void Run(string outDir, RaceSnapshot snap, FieldInfo? layoutField, string[] args)
    {
        string label = Arg(args, "--label") ?? "run";
        bool cold = args.Contains("--cold");
        CopyFrames();
        UseColumn(layoutField);
        bool raster = args.Contains("--raster");
        var gr = raster ? null : Egl.Create() ?? throw new InvalidOperationException("no GL");
        gr?.SetResourceCacheLimit(1024L * 1024 * 1024);
        const float S = 2f;
        using var surf = gr != null ? SKSurface.Create(gr, true, new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul))
                                    : SKSurface.Create(new SKImageInfo(3840, 2160, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;

        // 1. the warm-up the host runs while no card is up: the new one (PitCardWarm) if this build has it, else dep49's
        var warm = typeof(StoryCards).Assembly.GetType("ApexHUD.Native.Overlays.PitCardWarm");
        var sw = Stopwatch.StartNew();
        if (warm != null)
        {
            var tick = warm.GetMethod("Tick")!;
            var stepF = warm.GetField("step", BindingFlags.NonPublic | BindingFlags.Static)!;
            for (int i = 0; i < 2000 && (int)stepF.GetValue(null)! >= 0; i++)
            {
                tick.Invoke(null, new object?[] { c, snap, 1000.0 + i * 16.7, (Action<string>)(m => Console.WriteLine("  " + m)) });
                c.Flush(); gr?.Flush(); Thread.Sleep(5);
            }
        }
        else
        {
            using var p = new SKPaint { Color = new SKColor(255, 255, 255, 1) };     // dep49: plain sampling, 1x1
            foreach (var b in new[] { "michelin", "michelin man", "michelin man full" })
                if (CornerBrand.User(b) is { } bmp) c.DrawBitmap(bmp, new SKRect(0, 0, 1, 1), p);
            for (int i = 0; i < 400 && (MichelinRun.Count == 0 || !MichelinRun.Prewarm()); i++) Thread.Sleep(10);
            Thread.Sleep(300);
            foreach (var img in MichelinRun.Decoded()) c.DrawImage(img, new SKRect(0, 0, 1, 1), p);
        }
        c.Clear(SKColors.Transparent); c.Flush(); gr?.Flush(); if (gr != null) Egl.Finish();
        Console.WriteLine($"[{label}] warm-up done in {sw.ElapsedMilliseconds} ms ({(warm != null ? "PitCardWarm" : "dep49 style")}), {MichelinRun.Count} run frames");
        if (cold && gr != null) { gr.PurgeUnlockedResources(false); Console.WriteLine($"[{label}] COLD: pictures dropped from the graphics card before the card (GPU diet purge)"); }

        // 2. the card's first 3 s at 60 fps
        StoryDirector.Force(StoryKind.PitStops, 0, "");
        var sc = new StoryCards();
        var ms = new List<(double T, double Ms)>();
        for (double q = 0; q <= 3000; q += 1000 / 60.0)
        {
            c.Clear(SKColors.Transparent);
            long t0 = Stopwatch.GetTimestamp();
            c.Save(); c.Scale(S);
            sc.Draw(c, 1920, 1080, snap, q);
            c.Restore();
            c.Flush(); gr?.Flush();
            ms.Add((q, Stopwatch.GetElapsedTime(t0).TotalMilliseconds));
            if (gr != null) Egl.Finish();
        }
        void Line(string name, double a, double b)
        {
            var xs = ms.Where(x => x.T >= a && x.T < b).Select(x => x.Ms).OrderBy(x => x).ToList();
            Console.WriteLine($"[{label}] {name,-10} frames {xs.Count,3}  avg {xs.Average(),6:F2} ms  p50 {xs[xs.Count / 2],6:F2}  max {xs[^1],7:F2}  over 16.7 ms: {xs.Count(x => x > 16.7)}");
        }
        Line("0-0.5 s", 0, 500); Line("0.5-1 s", 500, 1000); Line("1-3 s", 1000, 3001); Line("ALL", 0, 3001);
        File.WriteAllLines(Path.Combine(outDir, $"pitbench_{label}.csv"), ms.Select(x => $"{x.T:F1},{x.Ms:F2}").Prepend("t_ms,frame_ms"));
    }

    /// <summary>The card's first 2.5 s at 30 fps (1080p PNGs, real sampling), decoding synchronously so the frames are
    /// deterministic: shows when the running man starts.</summary>
    public static void Clip(string outDir, RaceSnapshot snap, FieldInfo? layoutField, string[] args)
    {
        string label = Arg(args, "--label") ?? "run";
        CopyFrames();
        UseColumn(layoutField);
        typeof(StoryCards).Assembly.GetType("ApexHUD.Native.Overlays.MichelinRun")?.GetField("Synchronous")?.SetValue(null, true);
        for (int i = 0; i < 100 && MichelinRun.Count == 0; i++) Thread.Sleep(20);
        var dir = Path.Combine(outDir, "pitclip_" + label); Directory.CreateDirectory(dir);
        using var surf = SKSurface.Create(new SKImageInfo(1920, 1080));
        var c = surf.Canvas;
        StoryDirector.Force(StoryKind.PitStops, 0, "");
        var sc = new StoryCards();
        int k = 0;
        for (double q = 0; q <= 2500; q += 1000 / 30.0, k++)
        {
            Mock.Frame(c, 1920, 1080, snap);
            sc.Draw(c, 1920, 1080, snap, q);
            using var lp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 34, Typeface = Fonts.Inter.SemiBold };
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
            c.DrawRect(new SKRect(20, 1000, 760, 1060), bg);
            c.DrawText($"{label}   card time {q / 1000:0.00} s", 36, 1042, lp);
            using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 85);
            File.WriteAllBytes(Path.Combine(dir, $"{k:000}.png"), data.ToArray());
        }
        Console.WriteLine($"[{label}] {k} frames in {dir}");
    }

    /// <summary>#508 stills: the Fastest Laps (GO FAST) card's car lit (steady and mid-flash), classic and column. Without
    /// the owner's pictures a stand-in car and a stand-in light flare (a soft horizontal streak) are used - the point is
    /// where and how strongly each LED row glows.</summary>
    public static void GoFast(string outDir, RaceSnapshot snap, FieldInfo? layoutField, string[] args)
    {
        string label = Arg(args, "--label") ?? "run";
        var light = Assets.Resolve("logos", "sponsors", "gofast_light.png");
        if (!File.Exists(light))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(light)!);
            using var ls = SKSurface.Create(new SKImageInfo(512, 96));
            var lc = ls.Canvas; lc.Clear(SKColors.Transparent);
            using (var g = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(256, 48), 256,
                       new[] { new SKColor(255, 255, 255, 255), new SKColor(200, 225, 255, 120), new SKColor(160, 200, 255, 0) }, new[] { 0f, 0.12f, 1f }, SKShaderTileMode.Clamp) })
            { lc.Save(); lc.Scale(1, 0.16f, 256, 48); lc.DrawCircle(256, 48, 256, g); lc.Restore(); }
            using var img = ls.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(light, d.ToArray());
        }
        var dir = Path.Combine(outDir, "gofast_" + label); Directory.CreateDirectory(dir);
        using var surf = SKSurface.Create(new SKImageInfo(1920, 1080));
        var c = surf.Canvas;
        foreach (var lay in new[] { "Classic", "Mixed" })
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, lay));
            foreach (var (name, ms) in new[] { ("lit", 2600.0), ("flash", 1600.0) })
            {
                StoryDirector.Force(StoryKind.FastestLaps, 0, "");
                var sc = new StoryCards();
                using (var nd = new SKNoDrawCanvas(1920, 1080)) for (double q = 0; q < ms; q += 50) sc.Draw(nd, 1920, 1080, snap, q);
                c.Clear(new SKColor(18, 20, 26));
                sc.Draw(c, 1920, 1080, snap, ms);
                using var img = surf.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 95);
                File.WriteAllBytes(Path.Combine(dir, $"{lay.ToLowerInvariant()}_{name}.png"), d.ToArray());
            }
        }
        Console.WriteLine($"[{label}] GO FAST stills in {dir}");
    }

    /// <summary>#549 mock-up material: the mock TV frame alone, and each card (column layout, fully in) alone on a
    /// transparent 1080p picture - composed into the "more room" options by tools/storycard_preview/card_room.py.</summary>
    public static void RoomShots(string outDir, RaceSnapshot snap, FieldInfo? layoutField)
    {
        var dir = Path.Combine(outDir, "room_parts"); Directory.CreateDirectory(dir);
        using var surf = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas;
        c.Clear(SKColors.Transparent); Mock.Frame(c, 1920, 1080, snap);
        Save(surf, Path.Combine(dir, "bg.png"));
        UseColumn(layoutField);
        foreach (var (name, kind) in new[] { ("RaceSoFar", StoryKind.RaceSoFar), ("PitStops", StoryKind.PitStops), ("FastestLaps", StoryKind.FastestLaps) })
        {
            if (layoutField != null) layoutField.SetValue(null, Enum.Parse(layoutField.FieldType, kind == StoryKind.RaceSoFar ? "Column" : "Mixed"));
            StoryDirector.Force(kind, 0, "");
            var sc = new StoryCards();
            using (var nd = new SKNoDrawCanvas(1920, 1080)) for (double q = 0; q < 4000; q += 50) sc.Draw(nd, 1920, 1080, snap, q);
            c.Clear(SKColors.Transparent);
            sc.Draw(c, 1920, 1080, snap, 4000);
            Save(surf, Path.Combine(dir, $"card_{name}.png"));
        }
        Console.WriteLine("room parts in " + dir);
    }

    static void Save(SKSurface s, string path) { using var img = s.Snapshot(); using var d = img.Encode(SKEncodedImageFormat.Png, 100); File.WriteAllBytes(path, d.ToArray()); }

    static string? Arg(string[] args, string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
