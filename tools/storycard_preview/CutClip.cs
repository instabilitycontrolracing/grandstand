// #510 clip (2026-10-30): what a story card does around camera cuts, before and after the settle delay.
//   storycard_preview OUT --cutclip   -> OUT/cutclip_before/*.png and OUT/cutclip_after/*.png (1080p, 30 fps, 9 s)
// The camera is scripted (TV camera on one car, a cut to an ONBOARD at 2.0 s, a cut back to a TV camera at 5.0 s) and a
// card is due right at the first cut. The card itself is drawn by the real StoryCards code; its fade for the onboard
// follows the host's own rule (hidden in ~60 ms on the cut, back over ~130 ms), and the AFTER run asks the real
// PresentationState.TickCamera / ShotSettling when the card may start again. The BEFORE run starts it again the moment
// the onboard is over - on the cut back, as dep49 did.
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

static class CutClip
{
    public static void Run(string outDir, RaceSnapshot snap)
    {
        foreach (var after in new[] { false, true }) Render(outDir, snap, after);
    }

    static void Render(string outDir, RaceSnapshot snap, bool after)
    {
        const int W = 1920, H = 1080;
        const double Fps = 30, EndMs = 9000, CutOnMs = 2000, CutBackMs = 5000, DueMs = 1950;
        string dir = Path.Combine(outDir, after ? "cutclip_after" : "cutclip_before");
        Directory.CreateDirectory(dir);
        using var surf = SKSurface.Create(new SKImageInfo(W, H));
        var c = surf.Canvas;
        var sc = new StoryCards();
        int carA = snap.ClassLeaderboard[0].Drivers[0].CarIdx, carB = snap.ClassLeaderboard[0].Drivers[1].CarIdx;
        using var layer = new SKPaint();
        double cardStart = DueMs;           // the card was due on the cut: it starts a frame before the camera data says so
        bool bowed = false, restarted = false;
        double onRawSince = -1, offSince = -1; bool active = false; float hideK = 0;
        double restartMs = -1, bowAt = -1;
        var tick = typeof(PresentationState).GetMethod("TickCamera");
        var settling = typeof(PresentationState).GetProperty("ShotSettling");
        int k = 0;
        for (double now = 0; now <= EndMs; now += 1000 / Fps, k++)
        {
            bool onboard = now >= CutOnMs && now < CutBackMs;
            int car = now < CutOnMs ? carA : carB;
            int group = onboard ? 7 : 1;
            var cam = OnboardData.None with { CamCarIdx = car, CamGroup = group, CamGroupName = onboard ? "Cockpit" : "TV1", IsOnboard = onboard };
            var s = snap with { Onboard = cam };
            tick?.Invoke(null, new object[] { s, now });   // PresentationState.TickCamera (by name: older builds lack it)
            // the host's onboard rule (OverlayCompositor.TickOnboard): 0.6 s on-hold, 0.35 s off-hold, the cards hide at once
            if (onboard) { offSince = -1; if (onRawSince < 0) onRawSince = now; } else { onRawSince = -1; if (offSince < 0) offSince = now; }
            if (!active && onboard && now - onRawSince >= 600) active = true;
            if (active && !onboard && now - offSince >= 350) active = false;
            float ht = onboard || active ? 1f : 0f, dt = (float)(1000 / Fps);
            hideK += (ht - hideK) * Math.Min(1f, dt / (ht > hideK ? 60f : 130f));
            bool held = onboard || active;
            // the card: pulled by the onboard cut (cut short -> back to the front of the line), then started again
            if (!bowed && held && now >= cardStart) { bowed = true; bowAt = now; }
            if (bowed && !restarted)
            {
                bool may = after ? !held && !(bool)(settling?.GetValue(null) ?? false) : !held;
                if (may) { restarted = true; restartMs = now; cardStart = now; }
            }
            Mock.Frame(c, W, H, s);
            if (onboard)
            {
                using var tint = new SKPaint { Color = new SKColor(10, 12, 16, 235) };
                c.DrawRect(0, 0, W, H, tint);
                using var cp = new SKPaint { IsAntialias = true, Color = new SKColor(60, 64, 72), Style = SKPaintStyle.Stroke, StrokeWidth = 60 };
                c.DrawArc(new SKRect(560, 560, 1360, 1360), 200, 140, false, cp);   // a wheel rim: the onboard shot
            }
            bool showCard = now >= cardStart && (!bowed || restarted) || (bowed && !restarted && now < bowAt + StoryDirector.ExitMs && hideK < 0.995f);   // pulled: it fades with the cut, its exit plays hidden
            if (showCard)
            {
                double t0 = restarted ? restartMs : DueMs;
                StoryDirector.Force(StoryKind.RaceSoFar, t0, "");
                float vis = 1f - hideK;
                if (vis > 0.005f)
                {
                    layer.Color = SKColors.White.WithAlpha((byte)(255 * vis));
                    c.SaveLayer(layer);
                    sc.Draw(c, W, H, s, now);
                    c.Restore();
                }
            }
            Caption(c, after, now, onboard, CutOnMs, CutBackMs, restartMs);
            using var img = surf.Snapshot(); using var data = img.Encode(SKEncodedImageFormat.Png, 80);
            File.WriteAllBytes(Path.Combine(dir, $"{k:000}.png"), data.ToArray());
        }
        Console.WriteLine($"cutclip {(after ? "after" : "before")}: {k} frames, card back at {restartMs / 1000:0.00} s");
    }

    static void Caption(SKCanvas c, bool after, double now, bool onboard, double on, double back, double restart)
    {
        using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 190) };
        c.DrawRect(new SKRect(0, 960, 1920, 1080), bg);
        using var tp = new SKPaint { IsAntialias = true, Color = SKColors.White, TextSize = 34, Typeface = Fonts.Inter.SemiBold };
        c.DrawText($"{(after ? "AFTER" : "BEFORE")}   {now / 1000:0.00} s   camera: {(onboard ? "ONBOARD (cut at 2.0 s)" : now < on ? "TV camera" : "TV camera (cut back at 5.0 s)")}", 30, 1005, tp);
        // timeline: cuts in white, the card's restart in gold
        float x0 = 30, x1 = 1890, y = 1045;
        using var lp = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 4 };
        c.DrawLine(x0, y, x1, y, lp);
        float X(double ms) => x0 + (x1 - x0) * (float)(ms / 9000);
        using var cut = new SKPaint { Color = SKColors.White, StrokeWidth = 4 };
        c.DrawLine(X(on), y - 14, X(on), y + 14, cut); c.DrawLine(X(back), y - 14, X(back), y + 14, cut);
        if (restart >= 0) { using var gp = new SKPaint { Color = new SKColor(0xE8, 0xC2, 0x5A), StrokeWidth = 6 }; c.DrawLine(X(restart), y - 16, X(restart), y + 16, gp); }
        using var np = new SKPaint { Color = new SKColor(0xFF, 0x5A, 0x5A) };
        c.DrawCircle(X(now), y, 8, np);
    }
}
