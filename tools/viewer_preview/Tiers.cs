// #576 (2026-10-29), #603 (2026-10-30): the tiered viewer looks + the thank-you roll, drawn by the REAL ViewerTierRenderer over
// the canned field (tower + session bar + watch) on a race picture, at 3840x2160 (the TV; layout scaled 2x like the app).
// Stills (PNG) at each look's key moments, <= 10 s clips (mp4 through ffmpeg, with the look's stingers mixed in from
// assets/audio_public), edge cases, the backlog / interruption timelines and a 4K frame-cost bench.
//   dotnet viewer_preview.dll OUT_DIR [background.png] --tiers [--bench] [--noclips]
// Every name, amount and message here is a SAMPLE (the preview never reads the real viewer list).
using System.Diagnostics;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class Tiers
{
    const int W = 3840, H = 2160, LW = 1920, LH = 1080;
    const float Sc = 2f;

    static SKBitmap? Bg;
    static SKImage? BgImg;
    public static void SetBg(SKBitmap? bg) { Bg = bg; BgImg = bg != null ? SKImage.FromBitmap(bg) : null; }
    public static string Out = "out";
    static double simMs;

    public static void Run(string outDir, SKBitmap? bg, string[] args)
    {
        ViewerTiers.LooksOn = true;
        ViewerTiers.SoundsOn = true;
        ViewerFeed.Enabled = true;
        ViewerFund.Enabled = false;
        StorySound.Enabled = false;
        StorySound.Clock = () => (long)simMs;
        bool clips = !args.Contains("--noclips") && Environment.GetEnvironmentVariable("TIERS_NOCLIPS") == null;

        if (args.Contains("--bench")) { Bench(); return; }
        if (args.Contains("--gpubench")) { GpuBench(args); return; }   // #604

        // ---- stills at each look's key moments
        Shots("01_chat", "default", Chat, (350, "a_unrolling"), (950, "b_text_arriving"), (3000, "c_hold"), (6800, "d_exit"));
        Shots("02_tier3", "default", T3, (420, "a_unrolling_edge_tracing"), (1150, "b_amount_rolling"), (3200, "c_hold"), (8150, "d_exit"));
        Shots("03_tier2", "default", T2, (260, "a_seams_shoot_out"), (700, "b_panels_follow"), (1250, "c_amount_rolling"), (3000, "d_hold"), (6500, "e_scrolled"), (-380, "f_folding_back"));
        Shots("04_tier1", "default", T1, (330, "a_bloom"), (1000, "b_name_lands"), (1650, "c_shine_amount_rolling"), (3600, "d_hold"), (6950, "e_collapse"), (11000, "f_tower_band"));
        Shots("05_roll", "sample_roll", Roll, (2600, "a_everyone"), (17400, "b_supporters"), (27500, "c_tier1"), (52000, "d_tier3"));
        // more looks
        Shots("06_tier3_member", "default", () => Feed("member", "Sofia Marques", "", "", "youtube"), (3000, "hold"));
        Shots("06_tier3_milestone", "default", () => ViewerFeed.Accept(new ViewerInput("milestone", "Grace O'Neill", "", "A whole year of race nights. Still the best stream on a Friday!", "youtube", Months: 12), true), (3000, "hold"));
        Shots("06_tier2_giftbomb", "default", () => ViewerFeed.Accept(new ViewerInput("giftbomb", "pitwall_pete", "", "", "twitch", Gifts: 10), true), (3000, "hold"));
        Shots("06_tier3_bits", "default", () => Feed("bits", "kerb_crawler", "500", "Cheer for the double stint!", "twitch"), (3000, "hold"));
        // edge cases
        Shots("07_edge_long_name_tier1", "default", () => Feed("superchat", "TheVeryLongestNameAllowed_123456", "$250.00", "Long name, big amount.", "youtube"), (3600, "hold"));
        Shots("07_edge_long_message_chat", "default", () => Feed("comment", "Chen Wei", "", new string('x', 0) + "This is a very long chat message that goes on and on about tyre pressures, fuel windows, the weather radar, the next pit stop and the night stint ahead of us all", "youtube"), (3000, "hold"));
        Shots("07_edge_long_message_tier3", "default", () => Feed("superchat", "Hannah Schmidt", "$7.50", "Supercalifragilisticexpialidocious_and_a_very_long_word_without_spaces_to_break and then some more words to wrap over two lines and beyond", "youtube"), (3200, "hold"));
        Shots("07_edge_emoji_tier3", "default", () => Feed("superchat", "🏁 Emoji Fan 🏁", "$5.00", "🔥🔥🔥 GO GO GO 🏎️💨 bring it home 🏆", "youtube"), (3200, "hold"));
        Shots("07_edge_yen_is_tier3", "default", () => ViewerFeed.Accept(new ViewerInput("superchat", "Aiko Tanaka", "¥1,000", "Ganbatte! From Tokyo", "youtube", Currency: "JPY"), true), (3200, "hold"));
        Shots("07_edge_rupee_tier2", "default", () => ViewerFeed.Accept(new ViewerInput("superchat", "Priya Patel", "₹2,000.00", "Cheering from Mumbai!", "youtube", Currency: "INR"), true), (3000, "hold"));
        Shots("07_edge_real_tier2", "default", () => ViewerFeed.Accept(new ViewerInput("superchat", "Mateus Costa", "R$ 100,00", "Vai BMW!", "youtube", Currency: "BRL"), true), (3000, "hold"));
        Shots("07_edge_japanese_name_tier1", "default", () => ViewerFeed.Accept(new ViewerInput("superchat", "田中 愛子", "¥10,000", "最高のレース!", "youtube", Currency: "JPY"), true), (3600, "hold"));
        Shots("07_edge_cyrillic_name_chat", "default", () => Feed("comment", "Дмитрий", "", "Отличная гонка!", "youtube"), (3000, "hold"));
        // themes
        Shots("08_halloween_chat", "halloween", Chat, (3000, "hold"));
        Shots("08_halloween_tier3", "halloween", T3, (3200, "hold"));
        Shots("08_halloween_tier2", "halloween", T2, (3000, "hold"));
        Shots("08_halloween_tier1", "halloween", T1, (3600, "hold"), (11000, "band"));
        Shots("08_halloween_roll", "halloween_roll", Roll, (2600, "everyone"));

        Timelines();

        if (clips)
        {
            Clip("chat", "default", Chat, new[] { (0.0, 7.7) });
            Clip("tier3", "default", T3, new[] { (0.0, 9.2) });
            Clip("tier2", "default", T2, new[] { (0.0, 9.6) });
            Clip("tier1", "default", T1, new[] { (0.0, 8.2), (9.6, 11.2) });
            Clip("roll", "default", Roll, new[] { (0.0, 3.8), (14.9, 18.1), (25.3, 28.3) });
            Clip("halloween_tier1", "halloween", T1, new[] { (0.0, 8.2), (9.6, 11.2) });
        }
        PrintChecks();
    }

    // ---------------------------------------------------------------- the sample events
    static void Feed(string type, string name, string amount, string msg, string platform) => ViewerFeed.Accept(type, name, amount, msg, platform, true);
    static void Chat() => Feed("comment", "Lena Hoffmann", "", "That save into Arnage was unreal 🔥🔥 how is the tyre wear looking for the next stint?", "youtube");
    static void T3() => Feed("superchat", "Marco Bellini", "$5.00", "Great pace tonight, keep it clean through the night stint! 🏁", "youtube");
    static void T2() => Feed("tip", "Sofia Marques", "€25", "For the tyre fund! Send it into the Porsche curves and bring the BMW home in one piece.", "tip");
    static void T1() => Feed("superchat", "RacingDad_74", "$100.00", "Been watching since the first stint. Bring it home, the whole family is cheering for you! 🏆", "youtube");
    static void Roll() => ViewerTierRenderer.RequestRoll(true);

    // ---------------------------------------------------------------- the harness
    sealed class Run1
    {
        public Comp Comp = new();
        public ViewerTierRenderer V = new() { ForceClear = true };
        public List<(double T, string Cue)> Cues = new();
    }

    static Run1 Fresh(string theme, Action start)
    {
        Theme.Set(theme.StartsWith("halloween") ? "halloween" : "default");
        while (ViewerFeed.TryTake(out _)) { }
        ViewerFeed.ForgetSeen();
        ViewerTierRenderer.StopRoll();
        simMs = 0;
        StorySound.ResetBusy();
        var r = new Run1();
        StorySound.OnPlay = cue => r.Cues.Add((simMs, cue));
        // the stop request clears any roll left over from the previous run
        r.V.Tick(r.Comp.Snap, 0);
        start();
        return r;
    }

    static void Frame(SKCanvas c, bool draw, Run1 r, double t)
    {
        simMs = t;
        if (draw)
        {
            if (BgImg != null) { using var p = new SKPaint { FilterQuality = SKFilterQuality.High }; c.DrawImage(BgImg, new SKRect(0, 0, c.DeviceClipBounds.Width, c.DeviceClipBounds.Height), p); }
            else c.Clear(new SKColor(70, 82, 74));
        }
        c.Save();
        if (draw) c.Scale(Sc);
        r.Comp.Layout(t);
        r.Comp.Under(c, t);
        r.V.Tick(r.Comp.Snap, t);
        var tower = new SKPoint(16f, 18f);
        r.V.DrawUnder(c, LW, LH, VpLayout.SessionBar, tower, t);
        r.Comp.Top(c, t);
        r.V.DrawOver(c, LW, LH, VpLayout.SessionBar, tower, r.Comp.Snap, t);
        c.Restore();
    }

    static readonly SKNoDrawCanvas Nd = new(LW, LH);

    /// <summary>Stills of one run at the given times (negative = that many ms before the look's end).</summary>
    static void Shots(string name, string theme, Action start, params (double At, string Label)[] at)
    {
        if (Skip(name)) return;
        try
        {
            var r = Fresh(theme, start);
            using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
            double end = 0;
            var times = new List<(double, string)>();
            // the first frame starts the look; its length is known after that
            Frame(Nd, false, r, 0);
            foreach (var (a, l) in at)
            {
                double tt = a >= 0 ? a : LookEnd(r) + a;
                times.Add((tt, l)); end = Math.Max(end, tt);
            }
            times.Sort();
            int k = 0;
            for (double t = 16.7; k < times.Count; t += 16.7)
            {
                bool shoot = t + 16.7 > times[k].Item1;
                if (shoot)
                {
                    surf.Canvas.Clear();
                    Frame(surf.Canvas, true, r, t);
                    Save(surf, $"{name}_{times[k].Item2}");
                    k++;
                }
                else Frame(Nd, false, r, t);
            }
            Console.WriteLine($"ok   {name}  ({string.Join(", ", times.Select(x => $"{x.Item2} @{x.Item1:0} ms"))}; cues: {string.Join(", ", r.Cues.Select(x => $"{x.Cue} @{x.T:0}"))})");
        }
        catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); }
    }

    static double LookEnd(Run1 r) => r.V.CurrentEndsAt > 0 ? r.V.CurrentEndsAt : 8000;

    static bool Skip(string name) => Environment.GetEnvironmentVariable("TIERS_ONLY") is { Length: > 0 } o && !name.Contains(o);

    static void Save(SKSurface surf, string name)
    {
        using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(Path.Combine(Out, name + ".png"), data.ToArray());
    }

    /// <summary>A clip at 30 fps of the given windows of the timeline (seconds), joined with hard cuts, at 4K, with the cues
    /// the renderer asked for mixed in at their moments (the sound files from assets/audio_public).</summary>
    static void Clip(string name, string theme, Action start, (double From, double To)[] windows)
    {
        if (Skip(name)) return;
        try
        {
            var r = Fresh(theme, start);
            string silent = Path.Combine(Out, name + ".video.mp4"), file = Path.Combine(Out, name + ".mp4");
            var psi = new ProcessStartInfo("ffmpeg", $"-y -loglevel error -f rawvideo -pix_fmt rgba -s {W}x{H} -r 30 -i - -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p -movflags +faststart \"{silent}\"")
            { RedirectStandardInput = true, UseShellExecute = false };
            using var ff = Process.Start(psi)!;
            var stdin = ff.StandardInput.BaseStream;
            var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var surf = SKSurface.Create(info);
            var buf = new byte[W * H * 4];
            double end = windows.Max(x => x.To);
            int frames = 0;
            double nextOut = -1;
            for (double t = 0; t <= end * 1000; t += 1000.0 / 60)
            {
                bool inWin = windows.Any(x => t >= x.From * 1000 && t < x.To * 1000);
                bool write = inWin && t >= nextOut;
                if (write) { surf.Canvas.Clear(); Frame(surf.Canvas, true, r, t); }
                else Frame(Nd, false, r, t);
                if (write)
                {
                    nextOut = t + 1000.0 / 30 - 1;
                    unsafe { fixed (byte* p = buf) surf.ReadPixels(info, (IntPtr)p, W * 4, 0, 0); }
                    stdin.Write(buf, 0, buf.Length);
                    frames++;
                }
            }
            stdin.Close();
            ff.WaitForExit();
            // the cues, placed on the clip's own timeline
            var placed = new List<(double At, string File)>();
            double acc = 0;
            foreach (var (from, to) in windows)
            {
                foreach (var (ct, cue) in r.Cues)
                    if (!cue.Contains("skipped") && ct >= from * 1000 && ct < to * 1000)
                    {
                        string f = Path.Combine(AppContext.BaseDirectory, "assets", "audio_public", cue + ".wav");
                        if (File.Exists(f)) placed.Add((acc + ct / 1000.0 - from, f));
                    }
                acc += to - from;
            }
            if (placed.Count == 0) File.Move(silent, file, true);
            else
            {
                var inputs = string.Join(" ", placed.Select(p => $"-i \"{p.File}\""));
                var chains = string.Join(";", placed.Select((p, i) => $"[{i + 1}:a]adelay={(int)(p.At * 1000)}|{(int)(p.At * 1000)}[a{i}]"));
                var mix = string.Join("", placed.Select((_, i) => $"[a{i}]")) + $"amix=inputs={placed.Count}:normalize=0,apad[a]";
                var mx = new ProcessStartInfo("ffmpeg", $"-y -loglevel error -i \"{silent}\" {inputs} -filter_complex \"{chains};{mix}\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest \"{file}\"") { UseShellExecute = false };
                using var m = Process.Start(mx)!; m.WaitForExit();
                File.Delete(silent);
            }
            Console.WriteLine($"ok   {name}.mp4  ({frames} frames, {frames / 30.0:0.0} s; sound: {(placed.Count == 0 ? "none" : string.Join(", ", placed.Select(p => $"{Path.GetFileNameWithoutExtension(p.File)} @{p.At:0.00}s")))})");
        }
        catch (Exception e) { Console.WriteLine("FAIL " + name + ": " + e); }
    }

    // ---------------------------------------------------------------- timelines: a backlog, an interruption
    static void Timelines()
    {
        // a burst: 6 events inside a second - what plays when
        {
            var r = Fresh("default", () =>
            {
                Feed("comment", "apex_hunter", "", "first!", "youtube");
                Feed("superchat", "Jordan Lee", "$2.00", "Go go go", "youtube");
                Feed("bits", "NightStint", "300", "", "twitch");
                Feed("member", "Pierre Duval", "", "", "youtube");
                Feed("tip", "Grace O'Neill", "$15", "Fuel money", "tip");
                Feed("superchat", "Diego Ramírez", "$60.00", "Vamos!", "youtube");
                Feed("superchat", "Jordan Lee", "$2.00", "Go go go", "youtube");   // the same again: a sample skips the look-alike check
            });
            var log = new List<string>();
            ViewerEvent? last = null;
            for (double t = 0; t < 90000; t += 16.7)
            {
                Frame(Nd, false, r, t);
                if (!ReferenceEquals(r.V.Current, last)) { last = r.V.Current; if (last != null) log.Add($"{t / 1000,6:0.0}s  {last.Tier,-6} {last.Kind,-9} {last.Name}"); }
            }
            Console.WriteLine("timeline - a burst of 7 samples in one second (paid / members first, then chat; holds shorter while 3+ wait):");
            foreach (var l in log) Console.WriteLine("  " + l);
            Console.WriteLine("  cues: " + string.Join(", ", r.Cues.Select(x => $"{x.Cue} @{x.T / 1000:0.0}s")));
        }
        // an interruption: a battle starts 2 s into a tier 3 card and ends 10 s later
        {
            var r = Fresh("default", T3);
            r.V.ForceClear = false;   // the real gates (ViewerCardsRenderer.ScreenClear) on the canned race
            r.Comp.BattleAt = t => t >= 2000 && t < 12000;
            var log = new List<string>();
            string lastState = "";
            for (double t = 0; t < 26000; t += 16.7)
            {
                Frame(Nd, false, r, t);
                string st = r.V.Current != null ? $"playing {r.V.CurrentTier}" : r.V.Queued > 0 ? $"waiting ({(r.V.WhyWaiting.Length > 0 ? r.V.WhyWaiting : "gap")})" : "idle";
                if (st != lastState) { lastState = st; log.Add($"{t / 1000,6:0.0}s  {st}"); }
            }
            Console.WriteLine("timeline - a battle from 2 s to 12 s cuts a tier 3 card short (it plays again from the start afterwards):");
            foreach (var l in log) Console.WriteLine("  " + l);
        }
    }

    // ---------------------------------------------------------------- 4K frame cost
    static void Bench()
    {
        Console.WriteLine("frame cost at 3840x2160 (layout 1920x1080 scaled 2x, like the TV): the tier renderer's Tick + DrawUnder + DrawOver only");
        Console.WriteLine("  raster = CPU rasterising (an upper bound; on the PC the pixels are the graphics card's job), nodraw = the render thread's CPU side");
        Console.WriteLine($"  {"look @ window",-38} {"raster avg",10} {"raster max",10} {"nodraw avg",10} {"bytes/frame",12}");
        BenchOne("chat (unroll + text)", Chat, 300, 1500);
        BenchOne("chat (hold)", Chat, 3000, 4000);
        BenchOne("tier 3 (edge trace, amount, sweep)", T3, 400, 2400);
        BenchOne("tier 3 (exit)", T3, 7900, 8500);
        BenchOne("tier 2 (seams, panels, amount, sweep)", T2, 0, 2600);
        BenchOne("tier 2 (scrolling message)", T2, 4000, 6000);
        BenchOne("tier 1 (bloom, rays, name, shine)", T1, 200, 2300);
        BenchOne("tier 1 (hold)", T1, 3000, 5000);
        BenchOne("tier 1 (collapse into the band)", T1, 6600, 7600);
        BenchOne("tier 1 band on the tower", T1, 9000, 11000);
        BenchOne("roll (stage in, first page)", Roll, 0, 2500);
        BenchOne("roll (page change)", Roll, 14800, 16800);
        BenchOne("nothing playing (idle Tick)", () => { }, 0, 2000);
    }

    static void BenchOne(string label, Action start, double from, double to)
    {
        double rasterSum = 0, rasterMax = 0, ndSum = 0; long bytes = 0; int n = 0, nn = 0;
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = SKSurface.Create(info);
        var nd4 = new SKNoDrawCanvas(W, H);
        for (int pass = 0; pass < 2; pass++)
        {
            var r = Fresh("default", start);
            var tower = new SKPoint(16f, 18f);
            for (double t = 0; t < to; t += 1000.0 / 60)
            {
                bool rec = t >= from;
                simMs = t;
                r.Comp.Layout(t);
                if (!rec) { Frame(Nd, false, r, t); continue; }
                // the field under it is drawn but not timed
                var c = pass == 0 ? surf.Canvas : (SKCanvas)nd4;
                if (pass == 0) { c.Clear(); c.Save(); c.Scale(Sc); r.Comp.Under(c, t); c.Restore(); }
                else r.Comp.Under(Nd, t);
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                long t0 = Stopwatch.GetTimestamp();
                c.Save(); c.Scale(Sc);
                r.V.Tick(r.Comp.Snap, t);
                r.V.DrawUnder(c, LW, LH, VpLayout.SessionBar, tower, t);
                r.V.DrawOver(c, LW, LH, VpLayout.SessionBar, tower, r.Comp.Snap, t);
                c.Restore();
                if (pass == 0) surf.Canvas.Flush();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                long b1 = GC.GetAllocatedBytesForCurrentThread();
                if (pass == 0) { rasterSum += ms; rasterMax = Math.Max(rasterMax, ms); n++; }
                else
                {
                    ndSum += ms; bytes += b1 - b0; nn++;
                    if (b1 - b0 > 0 && Environment.GetEnvironmentVariable("BENCH_DETAIL") != null) Console.WriteLine($"    {label} t={t:0} allocated {b1 - b0} B");
                }
            }
        }
        Console.WriteLine($"  {label + $" @ {from / 1000:0.0}-{to / 1000:0.0}s",-38} {rasterSum / Math.Max(1, n),8:0.00}ms {rasterMax,8:0.00}ms {ndSum / Math.Max(1, nn),8:0.000}ms {bytes / Math.Max(1, nn),10:0} B");
    }

    // ---------------------------------------------------------------- #604: the same windows on Skia's GPU path
    // (headless Mesa through tools/stinger_bench/Egl.cs): ms = this thread's CPU time per frame incl. the flush (what Skia
    // spends feeding the graphics card), bytes per frame, and - with --frames DIR - PNGs of the tier renderer alone at
    // fixed moments, for a pixel compare of two builds (tools/stinger_bench --compare). --raster: CPU raster instead.
    static void GpuBench(string[] args)
    {
        int ai = Array.IndexOf(args, "--frames");
        string? frames = ai >= 0 && ai + 1 < args.Length ? args[ai + 1] : null;
        if (frames != null) Directory.CreateDirectory(frames);
        bool raster = args.Contains("--raster");
        var gr = raster ? null : Egl.Create() ?? throw new InvalidOperationException("no GL");
        gr?.SetResourceCacheLimit(1024L * 1024 * 1024);
        if (gr != null) GpuContext.Current = gr;
        var info = new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surf = gr != null ? SKSurface.Create(gr, true, info) : SKSurface.Create(info);
        Console.WriteLine($"4K frame cost of the tier renderer ({(gr != null ? "Skia GL on headless Mesa: CPU incl. flush" : "CPU raster")})");
        Console.WriteLine($"  {"look @ window",-38} {"avg",8} {"max",8} {"bytes/frame",12}");
        void One(string label, string tag, Action start, double from, double to, double[] shots)
        {
            var r = Fresh("default", start);
            var tower = new SKPoint(16f, 18f);
            double sum = 0, mx = 0, wait = 0; long bytes = 0; int n = 0, si = 0;
            var c = surf.Canvas;
            for (double t = 0; t < to; t += 1000.0 / 60)
            {
                simMs = t;
                r.Comp.Layout(t);
                bool rec = t >= from;
                c.Clear(SKColors.Transparent);
                if (gr != null) { c.Flush(); gr.Flush(); Egl.Finish(); }
                long b0 = GC.GetAllocatedBytesForCurrentThread(), t0 = Stopwatch.GetTimestamp();
                c.Save(); c.Scale(Sc);
                r.V.Tick(r.Comp.Snap, t);
                r.V.DrawUnder(c, LW, LH, VpLayout.SessionBar, tower, t);
                r.V.DrawOver(c, LW, LH, VpLayout.SessionBar, tower, r.Comp.Snap, t);
                c.Restore();
                c.Flush(); gr?.Flush();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                long b1 = GC.GetAllocatedBytesForCurrentThread();
                long w0 = Stopwatch.GetTimestamp();
                if (gr != null) Egl.Finish();
                double wms = (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
                if (rec) { sum += ms; mx = Math.Max(mx, ms); bytes += b1 - b0; n++; wait += wms; }
                if (frames != null && si < shots.Length && t >= shots[si])
                {
                    using var im = surf.Snapshot(); using var d = im.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(frames, $"{tag}_{(int)shots[si]:00000}.png"), d.ToArray());
                    si++;
                }
            }
            Console.WriteLine($"  {label + $" @ {from / 1000:0.0}-{to / 1000:0.0}s",-38} {sum / Math.Max(1, n),6:0.00}ms {mx,6:0.00}ms {bytes / Math.Max(1, n),10:0} B   pixel work (emulated GPU) {wait / Math.Max(1, n),6:0.00}ms");
        }
        One("tier 1 (bloom, rays, name, shine)", "t1in", T1, 200, 2300, new double[] { 330, 1000, 1650 });
        One("tier 1 (hold)", "t1hold", T1, 3000, 5000, new double[] { 3600, 4800 });
        One("tier 1 (collapse into the band)", "t1out", T1, 6600, 7600, new double[] { 6950, 7400 });
        One("roll (stage in, first page)", "rollin", Roll, 0, 2500, new double[] { 400, 2400 });
        One("roll (hold, names)", "rollhold", Roll, 5000, 7000, new double[] { 6000 });
        One("roll (page change)", "rollpage", Roll, 14800, 16800, new double[] { 15200, 16500 });
        One("tier 2 (seams, panels, amount, sweep)", "t2", T2, 0, 2600, new double[] { 700, 2500 });
        One("tier 3 (edge trace, amount, sweep)", "t3", T3, 400, 2400, new double[] { 1150, 2300 });
    }

    // ---------------------------------------------------------------- checks (printed)
    static void PrintChecks()
    {
        Console.WriteLine("tiers (defaults: tier 2 from $" + ViewerTiers.Tier2From + ", tier 1 from $" + ViewerTiers.Tier1From + ", " + ViewerTiers.BitsPerDollar + " bits = $1, " + ViewerTiers.GiftTier2 + "+ gifts = tier 2):");
        foreach (var (k, a, cur) in new[] { ("superchat", "$5.00", ""), ("superchat", "$9.99", ""), ("tip", "$10", ""), ("superchat", "€20", ""), ("superchat", "£49.99", ""), ("tip", "$50.00", ""),
                     ("superchat", "CA$75.00", "CAD"), ("bits", "500", ""), ("bits", "5000", ""), ("superchat", "1.234,50 €", "EUR"), ("superchat", "¥1,000", "JPY"), ("superchat", "¥10,000", "JPY"),
                     ("superchat", "₹2,000.00", "INR"), ("superchat", "R$ 100,00", "BRL"), ("tip", "25 SEK", "SEK"), ("superchat", "", "") })
        {
            ViewerFeed.TryKind(k, out var kind);
            double? usd = ViewerTiers.Usd(kind, a, cur);
            var e = new ViewerEvent(kind, "x", a, "", "youtube", false, true, 0, usd);
            Console.WriteLine($"  {k,-9} {a,-12} -> ~${usd?.ToString("0.##") ?? "-",-7} {e.Tier}");
        }
        Console.WriteLine($"  member -> {new ViewerEvent(ViewerKind.Member, "x", "", "", "youtube", false, true).Tier}, 5 gifted subs -> {new ViewerEvent(ViewerKind.Sub, "x", "", "", "twitch", false, true, 5).Tier}, chat -> {new ViewerEvent(ViewerKind.Comment, "x", "", "", "youtube", false, true).Tier}");
        Console.WriteLine("drawable: \"" + ViewerTiers.Drawable("Great race 🔥🏁 GG ✨ Ünïcödé", Fonts.Inter.Medium) + "\"   shortcodes: \"" + ViewerFeed.StripShortcodes("gg :face-blue-smiling: lap 12:30 :chillwdog:") + "\"");
        foreach (var p in ViewerTierRenderer.Outline(ViewerRoll.Sample())) Console.WriteLine($"  roll page: {p.Eyebrow} / {p.Title} - {p.Count} names");

        // the wiring, through the real Accept (not samples: test=false), with the roll file in this run's own home folder
        Console.WriteLine("wiring (real events, test=false):");
        ViewerFeed.ForgetSeen(); ViewerRoll.Clear();
        ViewerFeed.CommentsOn = true;
        while (ViewerFeed.TryTake(out _)) { }
        void A(string what, ViewerInput i) { var r = ViewerFeed.Accept(i); Console.WriteLine($"  {what,-44} -> {(r.OnScreen ? "ON SCREEN" : "not shown")}: {r.Why}"); }
        A("YouTube Super Chat ¥1,000 (id LCC.1)", new("superchat", "Aiko", "¥1,000", "Ganbatte", "youtube", Currency: "JPY", Id: "LCC.1"));
        A("  the same id again", new("superchat", "Aiko", "¥1,000", "Ganbatte", "youtube", Currency: "JPY", Id: "LCC.1"));
        A("YouTube chat with an emote code", new("comment", "Lena", "", "hi :face-blue-smiling:", "youtube", Id: "LCC.2"));
        A("  another chat 1 s later (rate limit)", new("comment", "Marco", "", "hello", "youtube", Id: "LCC.3"));
        A("Nightbot", new("comment", "Nightbot", "", "Follow the rules", "twitch"));
        A("!command", new("comment", "kerb", "", "!discord", "twitch"));
        A("Twitch gift bomb of 5 from Dad", new("giftbomb", "Dad", "", "", "twitch", Gifts: 5));
        A("  one of its subs (partOfGift)", new("giftsub", "Dad", "", "", "twitch", Gifts: 1, Recipient: "Kid1", PartOfGift: true));
        A("  another, without the flag", new("giftsub", "Dad", "", "", "twitch", Gifts: 1, Recipient: "Kid2"));
        A("YouTube membership gift of 3", new("giftmember", "Mum", "", "", "youtube", Gifts: 3));
        A("  a received membership", new("giftreceived", "Kid3", "", "", "youtube"));
        A("YouTube milestone 6 months", new("milestone", "Sofia", "", "Half a year!", "youtube", Months: 6));
        A("Twitch resub 7 months", new("resub", "kerb", "", "7 months!", "twitch", Months: 7));
        A("StreamElements tip €25", new("tip", "Sofia", "€25", "tyres", "tip", Currency: "EUR", Id: "se1"));
        A("  the same tip moments later (no id)", new("tip", "Sofia", "€25", "tyres", "tip", Currency: "EUR"));
        A("bits 1500", new("bits", "kerb", "1500", "go", "twitch"));
        A("follow", new("follow", "f", "", "", "twitch"));
        A("YouTube new subscriber (a follow)", new("newsubscriber", "s", "", "", "youtube"));
        A("Present viewer", new("join", "Lurker", "", "", "youtube"));
        var taken = new List<string>();
        while (ViewerFeed.TryTake(out var e) && e != null) taken.Add($"{e.Tier}:{e.Kind}:{e.Name}{(e.Gifts > 0 ? $"x{e.Gifts}" : "")}{(e.Months > 0 ? $"/{e.Months}mo" : "")}");
        Console.WriteLine("  queued for the screen: " + string.Join(", ", taken));
        Console.WriteLine("  on the roll: " + string.Join(", ", ViewerRoll.Snapshot().Select(p => p.Name + (p.Supporter ? $" (~${p.Usd:0.##}{(p.Gifts > 0 ? $", {p.Gifts} gifted" : "")}{(p.Member ? ", member" : "")})" : ""))));
        ViewerRoll.Clear();
    }
}
