using System.Diagnostics;
using System.Text;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-10-24 (Angelo: "the beginning part with the globe stayed choppy"). Two things:
/// <list type="bullet">
/// <item><b>Dress rehearsal.</b> In the quiet minute before the formation lap, the globe flight is drawn
/// once, silently, onto an invisible GPU surface the size of the screen - every part of it, the same size it
/// will really be. That uploads its pictures, makes the graphics card build its drawing programs, sizes
/// every buffer and gets .NET to compile the code, so the real flight's first frames do none of that.</item>
/// <item><b>Frame watcher.</b> Every frame of the opener is written to Documents\iRacing\intro_watch.txt
/// (kept between runs): music time, the gap since the last frame, CPU draw time, GPU flush time, the opener's
/// own share and any garbage collections - so if it's still choppy we can see exactly which frames and why.</item>
/// </list>
/// Render thread only.
/// </summary>
public static class IntroWatch
{
    public static GRContext? Gpu;

    static string Path => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "intro_watch.txt");

    // ---------------------------------------------------------------- rehearsal
    static string doneKey = "";
    static int step = -1;
    static SKSurface? surf;
    static readonly List<double> stepMs = new();
    const int WarmSteps = 30, DrawSteps = 48;

    static string KeyFor(RaceSnapshot s, int w, int h) => $"{s.TrackLatitude:0.0000}_{s.TrackLongitude:0.0000}_{w}x{h}";

    /// <summary>True while a rehearsal is waiting to run (asks the host for frames).</summary>
    public static bool Wants(RaceSnapshot s, int w, int h)
    {
        if (Gpu == null || w <= 0 || h <= 0 || !GridIntro.GlobeOpener) return false;
        if (s.Phase != RacePhase.PreRace || (s.SessionState ?? 0) >= 3) return false;   // the opener starts at 3
        if (!GlobeIntro.Ready(s)) return false;
        return doneKey != KeyFor(s, w, h);
    }

    /// <summary>One rehearsal step per frame. <paramref name="w"/>/<paramref name="h"/> = the layout size the
    /// grid entry gets (screen / uiScale); <paramref name="uiScale"/> = the scale the real frame draws at.</summary>
    public static void Rehearse(RaceSnapshot s, int w, int h, float uiScale, int devW, int devH)
    {
        if (!Wants(s, w, h)) return;
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (step < 0)
            {
                surf = SKSurface.Create(Gpu!, true, new SKImageInfo(devW, devH, SKColorType.Rgba8888, SKAlphaType.Premul));
                if (surf == null) { Finish(s, w, h, "no offscreen GPU surface"); return; }
                stepMs.Clear(); step = 0; GridIntro.RehearsalDone = false;
            }
            var cv = surf!.Canvas;
            cv.Clear(SKColors.Transparent);
            int save = cv.Save();
            if (uiScale != 1f) cv.Scale(uiScale);
            var scr = new SKRect(0, 0, w, h);
            if (step < WarmSteps)
                GlobeGT.Warm(cv, scr);                  // pictures onto the card, buffers sized, place names laid out
            else
            {
                int k = step - WarmSteps;
                double u = GridIntro.GlobeBarS * k / (DrawSteps - 1);
                GlobeIntro.DrawSplash(cv, scr, s, u, 1f);   // the real flight, frame by frame
            }
            cv.RestoreToCount(save);
            cv.Flush();
            Gpu!.Flush(true, true);                     // wait for the card, so the timing is honest
            step++;
            if (step >= WarmSteps + DrawSteps) Finish(s, w, h, null);
        }
        catch (Exception ex) { Finish(s, w, h, ex.Message); }
        finally { stepMs.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency); }
    }

    static void Finish(RaceSnapshot s, int w, int h, string? error)
    {
        doneKey = KeyFor(s, w, h);
        GridIntro.RehearsalDone = true;
        try { surf?.Dispose(); } catch { }
        surf = null;
        int n = step; step = -1;
        var draws = stepMs.Skip(WarmSteps).ToList();
        string msg = error != null
            ? $"globe rehearsal stopped after {n} steps: {error}"
            : $"globe rehearsal done: {draws.Count} frames drawn offscreen, first {(draws.Count > 0 ? draws[0] : 0):F0} ms, " +
              $"worst {(draws.Count > 0 ? draws.Max() : 0):F0} ms, last 10 avg {(draws.Count >= 10 ? draws.TakeLast(10).Average() : 0):F1} ms";
        Program.Log(msg);
        Append($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}\n  steps ms: {string.Join(" ", stepMs.Select(x => x.ToString("F0")))}\n");
    }

    // ---------------------------------------------------------------- frame watcher
    static readonly StringBuilder buf = new(64 * 1024);
    static bool watching;
    static double lastSeenMs;
    static long lastTs;
    static int g0, g1, g2;
    static long allocAt;

    /// <summary>Called by the host after every presented frame.</summary>
    public static void Frame(double nowMs, double drawMs, double flushMs, double introMs)
    {
        try { FrameCore(nowMs, drawMs, flushMs, introMs); }
        finally { Prof.Clear(); }
    }

    static void FrameCore(double nowMs, double drawMs, double flushMs, double introMs)
    {
        double raw = GridIntro.LiveRawT;
        long ts = Stopwatch.GetTimestamp();
        double gap = lastTs == 0 ? 0 : (ts - lastTs) * 1000.0 / Stopwatch.Frequency;
        lastTs = ts;
        if (raw >= 0 && raw < 40)
        {
            if (!watching)
            {
                watching = true; buf.Clear();
                buf.Append($"\n===== opener {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ({(Perf.Light ? "light" : "full")} mode)\n");
                buf.Append("  music_s  beat     gap_ms  draw_ms  flush_ms  opener_ms  gc  alloc_kb\n");
                g0 = GC.CollectionCount(0); g1 = GC.CollectionCount(1); g2 = GC.CollectionCount(2);
                allocAt = GC.GetTotalAllocatedBytes();
            }
            int n0 = GC.CollectionCount(0), n1 = GC.CollectionCount(1), n2 = GC.CollectionCount(2);
            string gc = n2 > g2 ? "G2" : n1 > g1 ? "G1" : n0 > g0 ? "G0" : "-";
            g0 = n0; g1 = n1; g2 = n2;
            long a = GC.GetTotalAllocatedBytes(); double kb = (a - allocAt) / 1024.0; allocAt = a;
            string beat = raw < GridIntro.GlobeInS ? "pages" : raw < GridIntro.GlobeOutS ? "GLOBE" : raw < GridIntro.GridS ? "track" : "grid";
            string flag = gap > 25 ? "  <-- slow" : "";
            buf.Append($"  {raw,7:F2}  {beat,-6} {gap,8:F1} {drawMs,8:F1} {flushMs,9:F1} {introMs,10:F1}  {gc,-3} {kb,8:F0}{flag}   | {Prof.Top(6)}\n");
            lastSeenMs = nowMs;
            GridIntro.LiveRawT = -1;
        }
        else if (watching && nowMs - lastSeenMs > 1500)
        {
            watching = false;
            Append(buf.ToString());
            Program.Log("intro watch: opener frames written to intro_watch.txt");
        }
    }

    static void Append(string text)
    {
        try { System.IO.File.AppendAllText(Path, text); } catch { }
    }
}
