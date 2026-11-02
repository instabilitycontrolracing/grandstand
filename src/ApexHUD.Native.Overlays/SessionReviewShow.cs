using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-27 — SESSION REVIEW: the TV show that plays between live sessions (Angelo: "like the track guide things that
/// play immediately after the race intro, but showing a breakdown of whatever my most recent session was ... so even while
/// I may be working on something else the TV can sit all day and play stuff about how to get better at each part of the
/// track"). The track guide's card, map and walk, with his own numbers on every stop:
///   opener  - the session in headlines (best / theoretical / typical lap, result, laps, incidents) and every lap's time
///   stops   - one per named corner and straight, in lap order: the camera flies in on the 3D map, the stretch lights up,
///             the brake point, slowest point and full-throttle point are pinned on it, the left column has best vs typical
///             times, speeds, brake and throttle points, gear, trouble there and one line on what the best pass did better;
///             the speed trace (best vs typical, with brake and throttle) runs along the bottom
///   closer  - "work on these three": the places with the most time to find, lit and numbered on the map
/// and round again. Laid out in the track card's 1920x1080 design units, drawn at the TV's own resolution (3840x2160).
/// <para><b>Cost</b> (it runs all day): everything static is rasterised on a background thread - the screen plate with the
/// card and footer, the track's glow and shadow, the terrain, the overview inset, and for each page its left column, theme
/// backdrop, chart and map labels (pages are made one ahead and dropped one behind). A frame is a handful of image draws
/// (the map ones through the camera's perspective matrix), the circuit line and the lit stretch transformed into reused
/// paths, a halo and a few labels. No allocation per frame; 30 frames a second; nothing at all while hidden.</para>
/// <para>Threading: <see cref="SetData"/>/<see cref="Send"/> from the host loop; everything else on the render thread.</para>
/// </summary>
public sealed class SessionReviewShow : IDisposable
{
    public const float DW = 1920f, DH = 1080f;
    const double OpenS = 18, StopS = 7.6, CloseS = 18, FlyStopS = 1.9, FlyWideS = 2.4, FadeMs = 700;

    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A), Txt = new(0xF4, 0xF6, 0xFA), Txt2 = new(0xB9, 0xC1, 0xCF), Txt3 = new(0x7E, 0x8A, 0xA0),
                            Navy0 = new(0x0B, 0x1C, 0x36), Navy1 = new(0x03, 0x08, 0x14), NavyHi = new(0x12, 0x2C, 0x52), CardNavy = new(0x0A, 0x1A, 0x38),
                            Glass = new(0x07, 0x0B, 0x14), BrakeRed = new(0xFF, 0x5E, 0x52), GoGreen = new(0x5B, 0xE3, 0x8E), Amber = new(0xFF, 0xB0, 0x3B),
                            Warm = new(0xFF, 0xF4, 0xE0), Coral = new(0xFF, 0x7A, 0x5C);

    public enum Command { Hide, Show, ShowNow }
    enum Phase { Hidden, In, Idle, Out, Clearing }

    // ---- host side ------------------------------------------------------------------------------------------------
    volatile int command;
    volatile float interval = 33.3f;
    SessionReviewData? wantedData; bool wantedImperial; int wantedW, wantedH;
    int buildSeq;
    Model? builtModel;                      // finished build waiting for the render thread
    readonly object gate = new();

    /// <summary>Log sink (the host's log).</summary>
    public static Action<string>? Log;
    static void L(string s) { try { Log?.Invoke("review: " + s); } catch { } }

    /// <summary>Shows / hides the window (wired to the host).</summary>
    public Action<bool>? SetHostVisible { get; set; }
    /// <summary>The window's GPU context (set on its render thread when ready).</summary>
    public GRContext? Gpu { get; set; }
    /// <summary>Present cap: 30 fps.</summary>
    public double PresentIntervalMs => interval;
    /// <summary>True once a review is ready to play.</summary>
    public bool HasContent => Volatile.Read(ref hasContent);
    volatile bool hasContent;
    /// <summary>True while it covers the screen fully (so the loading splash can take over on top of it).</summary>
    public bool Covering => phase == Phase.Idle;
    /// <summary>Length of one full loop, seconds (0 until built).</summary>
    public double LoopSeconds => cur?.Loop ?? 0;

    public void Send(Command c) => command = (int)c;

    /// <summary>Host loop: the review to show and the screen size. Rebuilt in the background only when it changed.</summary>
    public void SetData(SessionReviewData? d, bool imperial, int width, int height)
    {
        lock (gate)
        {
            if (ReferenceEquals(d, wantedData) && imperial == wantedImperial && width == wantedW && height == wantedH) return;
            wantedData = d; wantedImperial = imperial; wantedW = width; wantedH = height;
        }
        int seq = Interlocked.Increment(ref buildSeq);
        if (d == null || width < 64 || height < 64) { hasContent = false; return; }
        Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var m = Model.Build(d, imperial, width, height);
                if (seq != Volatile.Read(ref buildSeq)) { m.Dispose(); return; }
                Interlocked.Exchange(ref builtModel, m)?.Dispose();
                hasContent = true;
                L($"built {Path.GetFileName(d.SourceFile)}: {d.Segments.Count} stops, loop {m.Loop:0} s, map={(m.PlanePath != null)}, terrain={(m.TerrainBmp != null)} ({sw.ElapsedMilliseconds} ms)");
            }
            catch (Exception ex) { L("could not build: " + ex.Message); }
        });
    }

    // ---- render thread ----------------------------------------------------------------------------------------------
    Model? cur;
    Phase phase = Phase.Hidden;
    double phaseStart, loopStart = double.NaN, hiddenSince;
    float fadeFrom;
    bool broken;
    readonly Dictionary<int, Page> pages = new();
    readonly HashSet<int> baking = new();
    readonly ConcurrentQueue<Page> bakedPages = new();
    int lastPage = -1;

    readonly SKPaint img = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    readonly SKPaint imgHi = new() { FilterQuality = SKFilterQuality.High, IsAntialias = true };
    readonly SKPaint layerPaint = new();
    /// <summary>#336: the real local time (PC clock) on the broadcast watch.</summary>
    public readonly LocalWatch Watch = new() { PinToGpu = false };   // its own window / GL context
    readonly SKPaint line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint halo = new() { IsAntialias = true };
    readonly SKPaint fadeL = new(), fadeR = new(), fadeT = new(), fadeB = new();
    readonly SKPath tmp = new(), tmp2 = new(), tmpSeg = new();

    public SessionReviewShow()
    {
        halo.Shader = SKShader.CreateRadialGradient(new SKPoint(0, 0), 27, new[] { new SKColor(255, 248, 228), Gold.WithAlpha(150), Gold.WithAlpha(0) },
                                                    new[] { 0f, 0.4f, 1f }, SKShaderTileMode.Clamp);
    }

    /// <summary>Render thread, every loop: picks up a new build / baked pages and runs the show / hide animation.</summary>
    public bool FrameNeeded(double nowMs)
    {
        var nb = Interlocked.Exchange(ref builtModel, null);
        if (nb != null)
        {
            try { nb.Upload(Gpu); } catch (Exception ex) { L("upload failed: " + ex.Message); }
            DropPages();
            baking.Clear();
            cur?.Dispose();
            cur = nb;
            SetFades(nb);
            loopStart = nowMs;                  // a new session's review starts from its opener
            broken = false;
        }
        while (bakedPages.TryDequeue(out var pg))
        {
            baking.Remove(pg.Index);
            if (cur == null || pg.Owner != cur || pages.ContainsKey(pg.Index)) { pg.Dispose(); continue; }
            try { pg.Upload(Gpu); } catch { }
            pages[pg.Index] = pg;
        }

        var cmd = broken || cur == null ? Command.Hide : (Command)command;
        switch (phase)
        {
            case Phase.Hidden:
                if (cmd != Command.Hide && cur != null)
                {
                    if (double.IsNaN(loopStart) || nowMs - hiddenSince > 5 * 60_000) loopStart = nowMs;   // back after a while: from the top
                    phase = cmd == Command.ShowNow ? Phase.Idle : Phase.In; phaseStart = nowMs; fadeFrom = 0f;
                    SetHostVisible?.Invoke(true);
                    L(cmd == Command.ShowNow ? "showing (under the splash)" : "showing");
                }
                else if (pages.Count > 0 && nowMs - hiddenSince > 60_000) DropPages();   // hidden a minute: give the memory back
                break;
            case Phase.In:
            {
                float a = FadeIn(nowMs);
                if (cmd == Command.Hide) { phase = Phase.Out; phaseStart = nowMs; fadeFrom = a; }
                else if (cmd == Command.ShowNow || a >= 1f) phase = Phase.Idle;
                break;
            }
            case Phase.Idle:
                if (cmd == Command.Hide) { phase = Phase.Out; phaseStart = nowMs; fadeFrom = 1f; }
                break;
            case Phase.Out:
            {
                float a = FadeOut(nowMs);
                if (cmd != Command.Hide) { phase = cmd == Command.ShowNow ? Phase.Idle : Phase.In; phaseStart = nowMs; fadeFrom = a; }
                else if (a <= 0f) phase = Phase.Clearing;
                break;
            }
        }
        interval = 33.3f;
        return phase != Phase.Hidden;
    }

    float FadeIn(double now) => fadeFrom + (1f - fadeFrom) * EaseIo((float)((now - phaseStart) / FadeMs));
    float FadeOut(double now) => fadeFrom * (1f - EaseIo((float)((now - phaseStart) / FadeMs)));

    void DropPages()
    {
        foreach (var p in pages.Values) p.Dispose();
        pages.Clear();
        lastPage = -1;
    }

    /// <summary>Render thread: draws the show (the host has cleared the canvas to transparent).</summary>
    public void Render(SKCanvas c, int width, int height, double nowMs)
    {
        if (phase == Phase.Clearing)
        {
            phase = Phase.Hidden; hiddenSince = nowMs;
            SetHostVisible?.Invoke(false);
            return;
        }
        var M = cur;
        if (phase == Phase.Hidden || M == null || broken) return;
        float gA = phase switch { Phase.In => FadeIn(nowMs), Phase.Out => FadeOut(nowMs), _ => 1f };
        try { Draw(c, M, width, height, (nowMs - loopStart) / 1000.0, gA); }
        catch (Exception ex) { broken = true; L("switched off after a drawing error: " + ex); }
    }

    /// <summary>Draws the show at loop time <paramref name="t"/> seconds (offline previews call this directly).</summary>
    public void Draw(SKCanvas c, Model M, int width, int height, double t, float gA)
    {
        if (gA <= 0.002f) return;
        t %= M.Loop; if (t < 0) t += M.Loop;
        int p = 0;
        while (p < M.PageStart.Length - 1 && t >= M.PageStart[p + 1]) p++;
        double lt = t - M.PageStart[p], dur = M.PageLen[p];
        EnsurePages(M, p);
        pages.TryGetValue(p, out var pg);

        int outer = c.Save();
        if (width != M.W || height != M.H) c.Scale(width / (float)M.W, height / (float)M.H);
        bool layer = gA < 0.999f;
        if (layer) { layerPaint.Color = new SKColor(255, 255, 255, A(gA)); c.SaveLayer(layerPaint); }
        img.Color = SKColors.White;
        if (M.Plate != null) c.DrawImage(M.Plate, 0, 0, img);
        c.Translate(M.OffX, M.OffY);
        c.Scale(M.S);

        // ---- page alpha: in after a breath, out at the end (the track guide's rhythm) ----
        float ain = EaseOut((float)((lt - 0.15) / 0.55)), aout = Clamp01((float)((dur - lt) / 0.35));
        float pa = Math.Min(ain, aout);

        // ---- the camera ----
        int prevP = p == 0 ? M.PageStart.Length - 1 : p - 1;
        double fly = M.Cam[p].Z > 1.01f || M.Cam[prevP].Z > 1.01f ? (M.Cam[p].Z > 1.01f && M.Cam[prevP].Z > 1.01f ? FlyStopS : FlyWideS) : 0.01;
        float e = EaseIo((float)(lt / fly));
        float z = M.Cam[prevP].Z + (M.Cam[p].Z - M.Cam[prevP].Z) * e;
        float fx = M.Cam[prevP].F.X + (M.Cam[p].F.X - M.Cam[prevP].F.X) * e, fy = M.Cam[prevP].F.Y + (M.Cam[p].F.Y - M.Cam[prevP].F.Y) * e;
        float zoomAmt = Clamp01((z - 1f) / 0.35f);
        var zc = new SKPoint(M.PlaneRect.MidX, M.PlaneRect.MidY);
        var zm = SKMatrix.CreateTranslation(-fx, -fy);
        zm = zm.PostConcat(SKMatrix.CreateScale(z, z));
        float kk = 1 - Math.Min(1, (z - 1) / 0.35f);
        zm = zm.PostConcat(SKMatrix.CreateTranslation(zc.X + (fx - zc.X) * kk, zc.Y + (fy - zc.Y) * kk));
        var H = M.Persp.PreConcat(zm);
        var HL = SKMatrix.CreateTranslation(0, -Model.Lift).PreConcat(H);

        // ---- the map zone ----
        int clip = c.Save();
        c.ClipRect(M.MapClip);
        // theme backdrop (fades with the page)
        if (pg?.Backdrop != null)
        {
            float env = Math.Min(EaseOut((float)((lt - 0.1) / 0.6)), Clamp01((float)((dur - lt) / 0.5)));
            DrawImg(c, pg.Backdrop, M.MapClip, 0.92f * env);
        }
        float themeEnv = pg?.Backdrop != null ? Math.Min(Clamp01((float)(lt / 0.6)), Clamp01((float)((dur - lt) / 0.5))) : 0f;
        if (M.PlanePath != null)
        {
            // opener: the circuit traces itself, as on the track card
            float pk = p == 0 ? EaseOut((float)((lt - 0.3) / 3.8)) : 1f;
            if (M.Terrain != null)
            {
                c.Save();
                var tm = H.PreConcat(M.TerrainToPlane);
                c.Concat(ref tm);
                imgHi.Color = SKColors.White.WithAlpha(A((p == 0 ? EaseOut((float)(lt / 1.2)) : 1f) * (1 - 0.65f * themeEnv)));
                c.DrawImage(M.Terrain, 0, 0, imgHi);
                c.Restore();
            }
            float glowA = p == 0 ? Seg((float)lt, 3.2f, 4.4f) : 1f;
            if (M.Shadow != null && glowA > 0)
            {
                c.Save(); c.Concat(ref H);
                img.Color = SKColors.White.WithAlpha(A(glowA));
                c.DrawImage(M.Shadow, M.GlowRect, img);
                c.Restore();
            }
            if (M.Glow != null && glowA > 0)
            {
                c.Save(); c.Concat(ref HL);
                img.Color = SKColors.White.WithAlpha(A(glowA));
                c.DrawImage(M.Glow, M.GlowRect, img);
                c.Restore();
            }
            // the line: lifted above its shadow with a soft gold wall, white (vectors, so it stays sharp at any zoom)
            var src = M.PlanePath;
            if (pk < 1f)
            {
                tmpSeg.Reset();
                M.Measure!.GetSegment(0, M.PlaneLen * Math.Max(0.001f, pk), tmpSeg, true);
                src = tmpSeg;
            }
            src.Transform(H, tmp);
            line.StrokeWidth = 2.6f; line.Color = Gold.WithAlpha(30);
            for (int wi = 1; wi < 4; wi++) { c.Save(); c.Translate(0, -Model.Lift * wi / 4f); c.DrawPath(tmp, line); c.Restore(); }
            src.Transform(HL, tmp);
            line.StrokeWidth = 5.4f; line.Color = SKColors.White.WithAlpha(245);
            c.DrawPath(tmp, line);
            if (pk < 1f && M.Measure!.GetPosition(M.PlaneLen * pk, out var hd)) Halo(c, HL.MapPoint(hd), 1.1f, 1f);
            // start / finish
            if (pk >= 0.999f || p != 0)
            {
                var s0 = HL.MapPoint(M.SfA); var s1 = HL.MapPoint(M.SfB);
                float dx = s1.X - s0.X, dy = s1.Y - s0.Y, ln = MathF.Max(0.001f, MathF.Sqrt(dx * dx + dy * dy));
                float nx = -dy / ln * 16, ny = dx / ln * 16;
                line.StrokeWidth = 4f; line.Color = SKColors.White.WithAlpha(230);
                c.DrawLine(s0.X - nx, s0.Y - ny, s0.X + nx, s0.Y + ny, line);
            }

            // the stop: lit in gold, the light running through it, then its pins
            if (pg != null)
            {
                if (pg.Lit != null) DrawLit(c, M, pg, pg.Lit, HL, lt, dur, pa, pg.Accent, true);
                if (pg.Focus != null)
                    for (int i = 0; i < pg.Focus.Length; i++)
                        DrawLit(c, M, pg, pg.Focus[i], HL, lt - 0.9 - i * 0.5, dur, pa, Gold, false);
            }
            // the track logo in the infield until the camera flies in
            if (M.Logo != null)
            {
                float la = (p == 0 ? Seg((float)lt, 1.4f, 2.6f) : 1f) * (1 - zoomAmt);
                if (la > 0.01f) DrawImg(c, M.Logo, M.LogoRect, la, hi: true);
            }
        }
        // edges melt into the card
        c.DrawRect(M.MapClip, fadeL); c.DrawRect(M.MapClip, fadeR); c.DrawRect(M.MapClip, fadeT); c.DrawRect(M.MapClip, fadeB);
        c.RestoreToCount(clip);
        // the pins sit over the faded edges (always readable)
        if (pg != null && M.PlanePath != null) DrawPins(c, M, pg, HL, lt, dur, pa, fly);

        // ---- the little overview while the camera is in close ----
        if (zoomAmt > 0.02f && M.Inset != null)
        {
            DrawImg(c, M.Inset, M.InsetRect, zoomAmt);
            if (pg?.InsetLit != null)
            {
                line.StrokeWidth = 4.2f; line.Color = pg.Accent.WithAlpha(A(zoomAmt));
                c.DrawPath(pg.InsetLit, line);
            }
        }

        // ---- the chart along the bottom ----
        if (pg?.Chart != null)
        {
            float rv = EaseIo((float)((lt - 0.5) / 1.1));
            if (rv > 0.001f)
            {
                c.Save();
                var cr = pg.ChartRect;
                c.ClipRect(new SKRect(cr.Left, cr.Top, cr.Left + cr.Width * rv, cr.Bottom));
                DrawImg(c, pg.Chart, cr, pa);
                c.Restore();
            }
        }

        // ---- the left column ----
        if (pg?.Left != null) DrawImg(c, pg.Left, pg.LeftRect, pa);

        // ---- #336 (2026-10-27): the broadcast watch with the real local time, top middle, hanging over the panel's top
        // edge the way the session bar's watch hangs below the bar (pre-rendered dial, only the hands move)
        Watch.Draw(c, DW / 2f, 66f, 52f, 1f);

        c.RestoreToCount(outer);
    }

    void DrawLit(SKCanvas c, Model M, Page pg, SKPath lit, SKMatrix HL, double lt, double dur, float pa, SKColor col, bool run)
    {
        float la = Math.Min(EaseOut((float)((lt - 0.2) / 0.5)), Clamp01((float)((dur - (lt + 0.0)) / 0.35))) ;
        if (!run) la = Math.Min(EaseOut((float)(lt / 0.5)), pa);
        if (la <= 0.01f) return;
        lit.Transform(HL, tmp2);
        line.StrokeWidth = 22f; line.Color = col.WithAlpha(A(0.16f * la)); c.DrawPath(tmp2, line);
        line.StrokeWidth = 13f; line.Color = col.WithAlpha(A(0.30f * la)); c.DrawPath(tmp2, line);
        line.StrokeWidth = 8f; line.Color = col.WithAlpha(A(la)); c.DrawPath(tmp2, line);
        if (!run || pg.LitMeasure == null) return;
        float rk = EaseIo((float)((lt - 0.6) / Math.Max(1, dur - 1.6)));
        if (rk <= 0 || rk >= 1) return;
        if (pg.LitMeasure.GetPosition(pg.LitLen * rk, out var hp)) Halo(c, HL.MapPoint(hp), 1f, la);
    }

    void Halo(SKCanvas c, SKPoint at, float scale, float a)
    {
        c.Save();
        c.Translate(at.X, at.Y);
        if (scale != 1f) c.Scale(scale);
        halo.Color = SKColors.White.WithAlpha(A(a));
        c.DrawCircle(0, 0, 27, halo);
        c.Restore();
    }

    /// <summary>The pins on a stop: brake point, slowest point, full throttle (and the focus numbers on the closer), each
    /// a stick up from the line to its label. Laid out once the camera has settled.</summary>
    void DrawPins(SKCanvas c, Model M, Page pg, SKMatrix HL, double lt, double dur, float pa, double fly)
    {
        if (pg.Pins.Length == 0 || lt < fly) return;
        if (!pg.PinsPlaced)
        {
            pg.PinsPlaced = true;
            var placed = new List<SKRect>(pg.Pins.Length);
            for (int i = 0; i < pg.Pins.Length; i++)
            {
                ref var pin = ref pg.Pins[i];
                var at = HL.MapPoint(pin.Plane);
                pin.Screen = at;
                float w = pin.Image.Width / M.S, h = pin.Image.Height / M.S;
                float stick = 58;
                SKRect r;
                int guard = 0;
                while (true)
                {
                    float left = Math.Clamp(at.X - w * 0.18f, M.MapClip.Left + 12, M.MapClip.Right - 12 - w);
                    r = new SKRect(left, at.Y - stick - h, left + w, at.Y - stick);
                    if (r.Top < M.MapClip.Top + 8) { r = new SKRect(left, at.Y + 30, left + w, at.Y + 30 + h); }
                    bool hit = false;
                    foreach (var q in placed) if (q.IntersectsWith(new SKRect(r.Left - 6, r.Top - 6, r.Right + 6, r.Bottom + 6))) { hit = true; break; }
                    if (!hit || ++guard > 6) break;
                    stick += h + 12;
                }
                placed.Add(r);
                pin.Box = r;
            }
        }
        // sticks and dots first, then every label over them (a tall stick never crosses a label)
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < pg.Pins.Length; i++)
            {
                ref var pin = ref pg.Pins[i];
                float a = Math.Min(EaseOut((float)((lt - fly - 0.15 - i * 0.4) / 0.35)), pa);
                if (a <= 0.01f) continue;
                var at = pin.Screen; var r = pin.Box;
                if (pass == 1) { DrawImg(c, pin.Image, r, a); continue; }
                bool below = r.Top > at.Y;
                float sx = Math.Clamp(at.X, r.Left + 2, r.Right - 2);
                line.StrokeWidth = 1.6f; line.Color = pin.Colour.WithAlpha(A(0.85f * a));
                c.DrawLine(at.X, at.Y, sx, below ? r.Top : r.Bottom, line);
                fill.Color = Glass.WithAlpha(A(0.75f * a)); c.DrawCircle(at.X, at.Y, 8.5f, fill);
                fill.Color = pin.Colour.WithAlpha(A(a)); c.DrawCircle(at.X, at.Y, 5.5f, fill);
            }
    }

    void DrawImg(SKCanvas c, SKImage image, SKRect dst, float a, bool hi = false)
    {
        if (a <= 0.002f) return;
        var p = hi ? imgHi : img;
        p.Color = new SKColor(255, 255, 255, A(a));
        c.DrawImage(image, dst, p);
    }

    /// <summary>Asks for this page and the next to be made (in the background) and lets go of the rest.</summary>
    void EnsurePages(Model M, int p)
    {
        if (p == lastPage) return;                 // asked once per page; finished bakes arrive in FrameNeeded
        lastPage = p;
        int n = Next(M, p), pr = p == 0 ? M.PageStart.Length - 1 : p - 1;
        dropList.Clear();
        foreach (var k in pages.Keys) if (k != p && k != n && k != pr) dropList.Add(k);
        foreach (var k in dropList) { pages[k].Dispose(); pages.Remove(k); }
        Request(M, p); Request(M, n);
    }
    readonly List<int> dropList = new();

    void Request(Model M, int want)
    {
        if (pages.ContainsKey(want) || baking.Contains(want)) return;
        baking.Add(want);
        Task.Run(() =>
        {
            try { bakedPages.Enqueue(Page.Bake(M, want)); }
            catch (Exception ex) { L($"page {want}: {ex.Message}"); bakedPages.Enqueue(new Page { Owner = M, Index = want }); }
        });
    }
    static int Next(Model M, int p) => (p + 1) % M.PageStart.Length;

    /// <summary>Offline previews: makes every page needed for loop time <paramref name="t"/> right now (blocking).</summary>
    public void PrepareAt(Model M, double t)
    {
        t %= M.Loop;
        int p = 0;
        while (p < M.PageStart.Length - 1 && t >= M.PageStart[p + 1]) p++;
        foreach (var k in pages.Keys.ToArray()) if (k != p) { pages[k].Dispose(); pages.Remove(k); }
        if (!pages.ContainsKey(p)) pages[p] = Page.Bake(M, p);
        lastPage = p;
        foreach (var w in new[] { Next(M, p) }) baking.Add(w);   // previews don't bake ahead
    }

    /// <summary>Offline previews: build synchronously.</summary>
    public Model BuildNow(SessionReviewData d, bool imperial, int w, int h)
    {
        var m = Model.Build(d, imperial, w, h);
        cur?.Dispose(); cur = m; DropPages(); SetFades(m);
        return m;
    }

    void SetFades(Model m)
    {
        var z = m.MapClip; var col = m.Matte;
        bool tr = m.Terrain != null;
        fadeL.Shader?.Dispose(); fadeR.Shader?.Dispose(); fadeT.Shader?.Dispose(); fadeB.Shader?.Dispose();
        fadeL.Shader = SKShader.CreateLinearGradient(new SKPoint(z.Left, 0), new SKPoint(z.Left + z.Width * (tr ? 0.16f : 0.08f), 0), new[] { col, col.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        fadeR.Shader = SKShader.CreateLinearGradient(new SKPoint(z.Right, 0), new SKPoint(z.Right - z.Width * (tr ? 0.13f : 0.06f), 0), new[] { col, col.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        fadeT.Shader = SKShader.CreateLinearGradient(new SKPoint(0, z.Top), new SKPoint(0, z.Top + z.Height * (tr ? 0.2f : 0.08f)), new[] { col, col.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        fadeB.Shader = SKShader.CreateLinearGradient(new SKPoint(0, z.Bottom), new SKPoint(0, z.Bottom - z.Height * (tr ? 0.16f : 0.08f)), new[] { col, col.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
    }

    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    static float Seg(float t, float a, float b) => Clamp01((t - a) / (b - a));
    static float EaseIo(float v) { v = Clamp01(v); return v * v * (3f - 2f * v); }
    static float EaseOut(float v) { v = 1f - Clamp01(v); return 1f - v * v * v; }
    static byte A(float a) => (byte)(255f * Clamp01(a) + 0.5f);

    public void Dispose()
    {
        Interlocked.Exchange(ref builtModel, null)?.Dispose();
        DropPages();
        while (bakedPages.TryDequeue(out var p)) p.Dispose();
        cur?.Dispose(); cur = null;
        Watch.Dispose();
        foreach (var p in new[] { img, imgHi, layerPaint, line, fill, halo, fadeL, fadeR, fadeT, fadeB }) p.Dispose();
        tmp.Dispose(); tmp2.Dispose(); tmpSeg.Dispose();
    }

    // =================================================================================================================
    public readonly record struct Camera(float Z, SKPoint F);

    /// <summary>Everything that stays the same for one review on one screen size (made on a background thread).</summary>
    public sealed class Model : IDisposable
    {
        public const float Lift = 14f;
        public required SessionReviewData D;
        public bool Imperial;
        public int W, H;
        public float S, OffX, OffY;
        public SKRect Card, MapClip, PlaneRect, InsetRect, LogoRect, GlowRect, ChartRect, LeftRect;
        public float ColL, ColW, ColR;
        public SKColor Matte = CardNavy;
        public SKMatrix Persp = SKMatrix.Identity, TerrainToPlane = SKMatrix.Identity;
        public SKPath? PlanePath; public SKPathMeasure? Measure; public float PlaneLen;
        public SKPoint SfA, SfB;
        public SKBitmap? TerrainBmp, LogoBmp;
        public SKImage? Plate, Terrain, Glow, Shadow, Logo, Inset;
        public SKMatrix InsetM = SKMatrix.Identity;   // plane -> inset (design units)
        public Camera[] Cam = Array.Empty<Camera>();
        public double[] PageStart = Array.Empty<double>(), PageLen = Array.Empty<double>();
        public double Loop;
        /// <summary>Stops shown (indices into D.Segments), in lap order.</summary>
        public int[] Stops = Array.Empty<int>();
        public string Snapshot = "";

        public static Model Build(SessionReviewData d, bool imperial, int w, int h)
        {
            var M = new Model { D = d, Imperial = imperial, W = w, H = h };
            M.S = Math.Min(w / DW, h / DH);
            M.OffX = (w - DW * M.S) / 2f; M.OffY = (h - DH * M.S) / 2f;
            // the card (the track guide's wide card, full screen on the TV): 48 px margins, a header line above it
            M.Card = new SKRect(48, 76, DW - 48, DH - 44);
            var r = M.Card;
            M.ColL = r.Left + 70; M.ColW = r.Width * 0.34f; M.ColR = M.ColL + M.ColW;
            var zone = new SKRect(M.ColR + 50, r.Top + 24, r.Right - 30, r.Bottom - 24);
            M.ChartRect = new SKRect(zone.Left + 28, zone.Bottom - 196, zone.Right - 20, zone.Bottom - 6);
            M.MapClip = new SKRect(zone.Left, zone.Top, zone.Right, zone.Bottom - 150);
            M.LeftRect = new SKRect(r.Left + 2, r.Top + 2, M.ColR + 22, r.Bottom - 108);
            var mz = M.MapClip;
            M.PlaneRect = new SKRect(mz.Left, mz.Top - mz.Height * 0.22f, mz.Right, mz.Bottom + mz.Height * 0.12f);
            float pin = mz.Width * 0.17f;
            M.Persp = TrackTerrain.Perspective(M.PlaneRect, new SKPoint(mz.Left + pin, mz.Top + 10), new SKPoint(mz.Right - pin, mz.Top + 10),
                                              new SKPoint(mz.Right + mz.Width * 0.06f, mz.Bottom), new SKPoint(mz.Left - mz.Width * 0.06f, mz.Bottom));

            var snap = RaceSnapshot.Disconnected with { TrackDisplayName = d.TrackDisplay, TrackRawName = d.TrackRaw, TrackConfigName = d.TrackConfig, TrackBroadcastName = d.TrackDisplay };
            try { M.LogoBmp = PreRaceCards.TrackLogoOf(snap); } catch { M.LogoBmp = null; }
            var mt = GridIntro.Matte(M.LogoBmp);
            if (mt.Matte is { } mc) M.Matte = mc;

            // ---- the circuit: Grandstand's recorded map (with its terrain), else the lap's own GPS ----
            List<SKPoint>? pts = null; string? mapBase = null; string? mapJson = null;
            try
            {
                mapBase = TrackMapCache.Resolve(d.TrackConfig, d.TrackDisplay, d.TrackDisplay, d.TrackRaw);
                if (mapBase != null)
                {
                    mapJson = Assets.Resolve("maps", mapBase + ".json");
                    if (File.Exists(mapJson))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(mapJson));
                        pts = new List<SKPoint>();
                        foreach (var q in doc.RootElement.GetProperty("points").EnumerateArray()) pts.Add(new SKPoint(q[0].GetSingle(), q[1].GetSingle()));
                        if (pts.Count < 20) pts = null;
                    }
                }
            }
            catch { pts = null; }
            if (pts == null) { mapBase = null; mapJson = null; }
            if (pts == null && d.Outline is { Length: >= 40 } ol)
            {
                pts = new List<SKPoint>(ol.Length / 2);
                for (int i = 0; i + 1 < ol.Length; i += 2) pts.Add(new SKPoint(ol[i], ol[i + 1]));
            }
            SKMatrix toPlane = SKMatrix.Identity;
            if (pts != null)
            {
                float minX = pts.Min(q => q.X), maxX = pts.Max(q => q.X), minY = pts.Min(q => q.Y), maxY = pts.Max(q => q.Y);
                // the recorded maps live in their PNG's pixels (0..w, 0..h); fit the outline's own bounds, as the card does with the PNG
                float mw0 = maxX - minX, mh0 = maxY - minY;
                var pr = M.PlaneRect;
                float sc = Math.Min(pr.Width * 0.9f / mw0, pr.Height * 0.9f / mh0);
                // the track card fits the map PNG (w x h); outlines are drawn into it with a margin, so fitting the bounds is
                // within a few % of the same framing
                toPlane = SKMatrix.CreateScaleTranslation(sc, sc, pr.MidX - (minX + mw0 / 2) * sc, pr.MidY - (minY + mh0 / 2) * sc);
                var path = new SKPath();
                SKPoint last = default;
                for (int i = 0; i < pts.Count; i++)
                {
                    var q = toPlane.MapPoint(pts[i]);
                    if (i == 0) { path.MoveTo(q); last = q; }
                    else if (SKPoint.Distance(q, last) > 1.2f || i == pts.Count - 1) { path.LineTo(q); last = q; }
                }
                path.Close();
                M.PlanePath = path;
                M.Measure = new SKPathMeasure(path, true);
                M.PlaneLen = M.Measure.Length;
                M.Measure.GetPosition(0, out M.SfA);
                M.Measure.GetPosition(Math.Min(M.PlaneLen, 6f), out M.SfB);

                // terrain (built and cached by the track card; built here if this track never had one)
                if (mapBase != null && mapJson != null && !double.IsNaN(d.TrackLat))
                {
                    double lenM = TrackCorners.LengthM(d.TrackLength);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    TrackTerrain.Data? tr;
                    while ((tr = TrackTerrain.For(mapBase, mapJson, d.TrackLat, d.TrackLon, lenM)) == null && sw.Elapsed.TotalSeconds < 30) Thread.Sleep(250);
                    if (tr != null)
                    {
                        // the terrain is stored in map units; the card fits the map PNG, we fit the outline: same transform
                        M.TerrainBmp = tr.Ground;
                        M.TerrainToPlane = toPlane.PreConcat(tr.ToMap);
                    }
                }
            }

            // ---- which stops, how long each page, where the camera goes ----
            var stops = new List<int>();
            for (int i = 0; i < d.Segments.Count; i++) stops.Add(i);
            M.Stops = stops.ToArray();
            int pagesN = stops.Count + 2;
            M.PageLen = new double[pagesN]; M.PageStart = new double[pagesN]; M.Cam = new Camera[pagesN];
            double acc = 0;
            var overview = new Camera(1f, new SKPoint(M.PlaneRect.MidX, M.PlaneRect.MidY));
            for (int p = 0; p < pagesN; p++)
            {
                M.PageLen[p] = p == 0 ? OpenS : p == pagesN - 1 ? CloseS : StopS;
                M.PageStart[p] = acc; acc += M.PageLen[p];
                M.Cam[p] = overview;
                if (p > 0 && p < pagesN - 1 && M.PlanePath != null)
                {
                    var sg = d.Segments[stops[p - 1]];
                    using var seg = M.SegmentPath(sg.StartPct, sg.EndPct);
                    var b = seg.Bounds;
                    float ext = Math.Max(Math.Max(b.Width, b.Height), Math.Min(M.PlaneRect.Width, M.PlaneRect.Height) * 0.12f);
                    float zz = Math.Clamp(Math.Min(M.PlaneRect.Width, M.PlaneRect.Height) * 0.5f / ext, 1.3f, 2.3f);
                    M.Cam[p] = new Camera(zz, new SKPoint(b.MidX, b.MidY));
                }
            }
            M.Loop = acc;

            // ---- the pictures ----
            M.BakePlate();
            if (M.PlanePath != null) M.BakeLine();
            M.BakeLogo(mt.Feathered ?? M.LogoBmp);
            if (M.PlanePath != null) M.BakeInset();
            if (M.TerrainBmp != null) M.Terrain = SKImage.FromBitmap(M.TerrainBmp);
            return M;
        }

        /// <summary>A stretch of the outline (lap fractions, may wrap past the line) in plane coordinates.</summary>
        public SKPath SegmentPath(float a0, float a1)
        {
            var seg = new SKPath();
            if (Measure == null || PlaneLen <= 0) return seg;
            if (a1 < a0) a1 += 1;
            using var m = new SKPathMeasure(PlanePath!, true);
            if (a1 <= 1) m.GetSegment(PlaneLen * a0, PlaneLen * a1, seg, true);
            else { m.GetSegment(PlaneLen * a0, PlaneLen, seg, true); m.GetSegment(0, PlaneLen * (a1 - 1), seg, true); }
            return seg;
        }

        public SKPoint PointAt(float pct)
        {
            using var m = new SKPathMeasure(PlanePath!, true);
            pct -= MathF.Floor(pct);
            m.GetPosition(PlaneLen * pct, out var p);
            return p;
        }

        // ---------------------------------------------------------------------------------------------------- plate
        void BakePlate()
        {
            using var surf = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surf.Canvas;
            c.Clear(Navy1);
            c.Translate(OffX, OffY); c.Scale(S);
            // the TV plate: the loading splash's navy, so review and splash hand over seamlessly
            using (var bg = new SKPaint { IsDither = true, Shader = SKShader.CreateRadialGradient(new SKPoint(DW / 2, DH * 0.46f), DW * 0.75f,
                       new[] { NavyHi, Navy0, Navy1 }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(-OffX / S, -OffY / S, W / S, H / S, bg);
            using (var dot = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(13) })
                for (int gy = 60; gy < DH; gy += 60)
                    for (int gx = 60; gx < DW; gx += 60)
                        c.DrawCircle(gx, gy, 1.1f, dot);

            // header line over the card
            var d = D;
            Text.Tracked(c, "SESSION REVIEW", Card.Left + 4, 52, Fonts.Inter.Bold, 15, 4.2f, Gold);
            float hx = Card.Left + 4 + Text.TrackedWidth("SESSION REVIEW", Fonts.Inter.Bold, 15, 4.2f) + 18;
            using (var dp = new SKPaint { IsAntialias = true, Color = Gold.WithAlpha(150) }) c.DrawCircle(hx, 46.5f, 2.6f, dp);
            string when = SessionLabel(d);
            Text.Tracked(c, when, hx + 18, 52, Fonts.Inter.SemiBold, 15, 3.4f, Txt2);
            Text.Tracked(c, "GRANDSTAND", Card.Right - 4, 52, Fonts.Inter.Bold, 15, 7f, Txt.WithAlpha(110), align: 1);

            // the card: soft shadow, the track logo's plate colour, a faint rim
            using (var rr = new SKRoundRect(Card, 18, 18))
            {
                using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
                { c.Save(); c.Translate(0, 8); c.DrawRoundRect(rr, sh); c.Restore(); }
                using (var bp = new SKPaint { IsAntialias = true, Color = Matte }) c.DrawRoundRect(rr, bp);
                using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = Txt.WithAlpha(22) }) c.DrawRoundRect(rr, rim);
            }
            // the divider between the story and the map
            using (var dv = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, Card.Top + 40), new SKPoint(0, Card.Bottom - 40),
                       new[] { Gold.WithAlpha(0), Gold.WithAlpha(110), Gold.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(ColR + 26, Card.Top + 40, 1.2f, Card.Height - 80, dv);

            // the session's facts along the bottom, always there
            float fy = Card.Bottom - 50;
            using (var fl = new SKPaint { Color = new SKColor(255, 255, 255, 28) }) c.DrawRect(ColL, fy - 64, ColW, 1, fl);
            var facts = new List<(string, string)>();
            if (d.BestLap is { } bl) facts.Add(("BEST LAP", LapTime(bl)));
            if (d.Theoretical is { } th) facts.Add(("THEORETICAL", LapTime(th)));
            facts.Add((d.IsRace ? "LAPS" : "TIMED LAPS", d.LapsCompleted.ToString(CultureInfo.InvariantCulture)));
            if (d.IsRace && d.FinishClassPos is { } fp) facts.Add(("FINISHED", "P" + fp.ToString(CultureInfo.InvariantCulture)));
            else facts.Add(("CLEAN LAPS", d.CleanLaps.ToString(CultureInfo.InvariantCulture)));
            float fw = ColW / Math.Max(1, facts.Count);
            for (int i = 0; i < facts.Count; i++)
            {
                float x0 = ColL + i * fw;
                Text.Tracked(c, facts[i].Item1, x0, fy - 32, Fonts.Inter.Bold, 11, 2.6f, Txt3);
                Text.Plain(c, facts[i].Item2, x0, fy, Fonts.Inter.Bold, 26, Txt);
            }
            Plate = surf.Snapshot();
        }

        // ------------------------------------------------------------------------------------------- glow / shadow
        void BakeLine()
        {
            var b = PlanePath!.Bounds; b.Inflate(40, 40);
            GlowRect = b;
            const float Res = 1.0f;   // a pixel per design unit: soft light, it needn't be sharper
            int pw = (int)Math.Ceiling(b.Width * Res), ph = (int)Math.Ceiling(b.Height * Res);
            SKImage Make(Action<SKCanvas> draw)
            {
                using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
                var c = surf.Canvas; c.Clear(SKColors.Transparent);
                c.Scale(Res); c.Translate(-b.Left, -b.Top);
                draw(c);
                return surf.Snapshot();
            }
            Shadow = Make(c =>
            {
                using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0, 0, 0, 120), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) };
                c.DrawPath(PlanePath, p);
            });
            Glow = Make(c =>
            {
                using var p = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 16, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, Color = Gold.WithAlpha(90), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) };
                c.DrawPath(PlanePath, p);
            });
        }

        void BakeLogo(SKBitmap? logo)
        {
            if (logo == null || PlanePath == null) return;
            var b = PlanePath.Bounds;
            // where the infield's centre lands on screen, sized by the ground's scale there (as the track card does)
            var pc = Persp.MapPoint(b.MidX, b.MidY); var prr = Persp.MapPoint(b.MidX + 100, b.MidY);
            float depth = SKPoint.Distance(pc, prr) / 100f;
            float ls = Math.Min(b.Width * 0.42f / logo.Width, b.Height * 0.4f / logo.Height) * depth;
            float lw = logo.Width * ls, lh = logo.Height * ls;
            LogoRect = new SKRect(pc.X - lw / 2, pc.Y - 14 - lh / 2, pc.X + lw / 2, pc.Y - 14 + lh / 2);
            int pw = Math.Max(1, (int)Math.Ceiling(lw * S)), ph = Math.Max(1, (int)Math.Ceiling(lh * S));
            using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            surf.Canvas.Clear(SKColors.Transparent);
            using (var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High })
                surf.Canvas.DrawBitmap(logo, new SKRect(0, 0, pw, ph), p);
            Logo = surf.Snapshot();
        }

        /// <summary>The whole lap small in the corner of the map (with its own scrap of ground and the logo), for while
        /// the camera is in close.</summary>
        void BakeInset()
        {
            var fb = PlanePath!.Bounds;
            float isc = Math.Min(270f / fb.Width, 172f / fb.Height);
            var ib = new SKRect(MapClip.Right - 26 - fb.Width * isc, MapClip.Top + 22, MapClip.Right - 26, MapClip.Top + 22 + fb.Height * isc);
            var ipM = SKMatrix.CreateTranslation(-fb.Left, -fb.Top).PostConcat(SKMatrix.CreateScale(isc, isc)).PostConcat(SKMatrix.CreateTranslation(ib.Left, ib.Top));
            var iplane = new SKRect(ib.Left, ib.Top - ib.Height * 0.25f, ib.Right, ib.Bottom + ib.Height * 0.05f);
            var ipersp = TrackTerrain.Perspective(iplane, new SKPoint(ib.Left + ib.Width * 0.14f, ib.Top), new SKPoint(ib.Right - ib.Width * 0.14f, ib.Top),
                                                  new SKPoint(ib.Right + ib.Width * 0.04f, ib.Bottom), new SKPoint(ib.Left - ib.Width * 0.04f, ib.Bottom));
            InsetM = ipersp.PreConcat(ipM);
            var box = new SKRect(ib.Left - 26, ib.Top - 24, ib.Right + 26, ib.Bottom + 22);
            InsetRect = box;
            int pw = (int)Math.Ceiling(box.Width * S), ph = (int)Math.Ceiling(box.Height * S);
            using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surf.Canvas; c.Clear(SKColors.Transparent);
            c.Scale(S); c.Translate(-box.Left, -box.Top);
            using (var bk = new SKPaint { IsAntialias = true, Color = Matte.WithAlpha(225) })
                c.DrawRoundRect(new SKRect(ib.Left - 16, ib.Top - 14, ib.Right + 16, ib.Bottom + 14), 12, 12, bk);
            using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, 26) })
                c.DrawRoundRect(new SKRect(ib.Left - 16, ib.Top - 14, ib.Right + 16, ib.Bottom + 14), 12, 12, bg);
            if (TerrainBmp != null)
            {
                var ir = new SKRect(ib.Left - 20, ib.Top - 20, ib.Right + 20, ib.Bottom + 16);
                int l0 = c.SaveLayer(ir, null);
                c.ClipRect(ir, SKClipOperation.Intersect, true);
                c.Save();
                var igm = InsetM.PreConcat(TerrainToPlane);
                c.Concat(ref igm);
                using (var gp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High }) c.DrawBitmap(TerrainBmp, 0, 0, gp);
                c.Restore();
                using (var im2 = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateRadialGradient(new SKPoint(ib.MidX, ib.MidY), Math.Min(ib.Width, ib.Height) * 0.75f,
                           new[] { SKColors.White, SKColors.White.WithAlpha(150), SKColors.Transparent }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
                    c.DrawRect(ir, im2);
                c.RestoreToCount(l0);
            }
            using (var ip = new SKPath(PlanePath))
            {
                ip.Transform(InsetM);
                using var il = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.White.WithAlpha(200) };
                c.DrawPath(ip, il);
                if (Logo != null && LogoBmp != null)
                {
                    var lb = GridIntro.Matte(LogoBmp).Feathered ?? LogoBmp;
                    var b2 = ip.Bounds;
                    float ls2 = Math.Min(b2.Width * 0.42f / lb.Width, b2.Height * 0.4f / lb.Height);
                    float lw2 = lb.Width * ls2, lh2 = lb.Height * ls2;
                    using var lp2 = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    c.DrawBitmap(lb, new SKRect(b2.MidX - lw2 / 2, b2.MidY - lh2 / 2, b2.MidX + lw2 / 2, b2.MidY + lh2 / 2), lp2);
                }
            }
            Inset = surf.Snapshot();
        }

        /// <summary>Render thread: the pictures move onto the graphics card once.</summary>
        public void Upload(GRContext? gr)
        {
            if (gr == null) return;
            Plate = ToGpu(Plate, gr); Glow = ToGpu(Glow, gr); Shadow = ToGpu(Shadow, gr); Logo = ToGpu(Logo, gr); Inset = ToGpu(Inset, gr); Terrain = ToGpu(Terrain, gr);
        }

        public void Dispose()
        {
            Plate?.Dispose(); Glow?.Dispose(); Shadow?.Dispose(); Logo?.Dispose(); Inset?.Dispose(); Terrain?.Dispose();
            Plate = Glow = Shadow = Logo = Inset = Terrain = null;
            Measure?.Dispose(); PlanePath?.Dispose(); Measure = null; PlanePath = null;
        }
    }

    internal static SKImage? ToGpu(SKImage? raster, GRContext gr)
    {
        if (raster == null) return null;
        try
        {
            var tex = raster.ToTextureImage(gr);
            if (tex == null) return raster;
            raster.Dispose();
            return tex;
        }
        catch { return raster; }
    }

    // =================================================================================================================
    public struct Pin
    {
        public SKPoint Plane, Screen;
        public SKRect Box;
        public SKColor Colour;
        public SKImage Image;
    }

    /// <summary>One page of the show (opener, a stop, the closer): its pictures, made on a background thread.</summary>
    public sealed class Page : IDisposable
    {
        public Model? Owner;
        public int Index;
        public SKImage? Left, Backdrop, Chart;
        public SKRect LeftRect, ChartRect;
        public SKPath? Lit, InsetLit;
        public SKPath[]? Focus;
        public SKPathMeasure? LitMeasure; public float LitLen;
        public SKColor Accent = Gold;
        public Pin[] Pins = Array.Empty<Pin>();
        public bool PinsPlaced;

        public static Page Bake(Model M, int index)
        {
            var pg = new Page { Owner = M, Index = index, LeftRect = M.LeftRect, ChartRect = M.ChartRect };
            int n = M.PageStart.Length;
            var d = M.D;
            if (index == 0) { BakeOpener(M, pg); return pg; }
            if (index == n - 1) { BakeCloser(M, pg); return pg; }
            int si = M.Stops[index - 1];
            var s = d.Segments[si];
            pg.Accent = CornerTheme.Accent(s.Name);
            if (M.PlanePath != null)
            {
                pg.Lit = M.SegmentPath(s.StartPct, s.EndPct);
                pg.LitMeasure = new SKPathMeasure(pg.Lit, false);
                pg.LitLen = pg.LitMeasure.Length;
                pg.InsetLit = new SKPath(pg.Lit); pg.InsetLit.Transform(M.InsetM);
                // pins: brake point, slowest point, full throttle (best pass)
                var pins = new List<Pin>();
                string U = M.Imperial ? "MPH" : "KM/H";
                if (s.BrakePct is { } bp && s.BrakeToApexBest is { } bm)
                    pins.Add(MakePin(M, bp, BrakeRed, "BRAKE", $"{bm:0} M TO THE APEX"));
                if (s.ApexPct is { } ap && s.MinKmhBest is { } mk)
                    pins.Add(MakePin(M, ap, Gold, "SLOWEST", $"{Speed(mk, M.Imperial)} {U}" + (s.Gear is { } g && g > 0 ? $"  ·  GEAR {g}" : "")));
                if (s.ThrottlePct is { } tp && s.ThrottleAfterApexBest is { } tm)
                    pins.Add(MakePin(M, tp, GoGreen, "FULL THROTTLE", $"{tm:0} M AFTER"));
                pg.Pins = pins.ToArray();
            }
            var th = CornerTheme.For(s.Name);
            if (th != null) pg.Backdrop = Picture(M, M.MapClip, c => BattleMap.Backdrop(c, M.MapClip, th, s.Name, 1f, 0));
            pg.Left = Picture(M, M.LeftRect, c => StopColumn(M, c, index, si, s, pg.Accent));
            if (s.TraceBest != null) pg.Chart = Picture(M, M.ChartRect, c => SpeedChart(M, c, s));
            return pg;
        }

        static Pin MakePin(Model M, float pct, SKColor col, string label, string value)
        {
            var at = M.PointAt(pct);
            const float h = 48;
            float w1 = Text.TrackedWidth(label, Fonts.Inter.Bold, 11, 2.4f), w2 = Text.Width(value, Fonts.BarlowCondensed.Bold, 24);
            float w = Math.Max(w1, w2) + 32;
            var img = PictureSize(M, w, h, c =>
            {
                using (var rr = new SKRoundRect(new SKRect(0, 0, w, h), 6, 6))
                using (var bg = new SKPaint { IsAntialias = true, Color = Glass.WithAlpha(214) }) c.DrawRoundRect(rr, bg);
                using (var bar = new SKPaint { IsAntialias = true, Color = col }) c.DrawRect(new SKRect(0, 0, 3.5f, h), bar);
                Text.Tracked(c, label, 15, 17, Fonts.Inter.Bold, 11, 2.4f, col);
                Text.Plain(c, value, 15, 40, Fonts.BarlowCondensed.Bold, 24, Txt);
            });
            return new Pin { Plane = at, Colour = col, Image = img };
        }

        // ------------------------------------------------------------------------------------------- the left column
        static string Where(SessionReviewData d)
        {
            string w = string.Join(", ", new[] { d.TrackCity, d.TrackCountry }.Where(z => !string.IsNullOrWhiteSpace(z))).ToUpperInvariant();
            return w.Length > 0 ? w : "SESSION REVIEW";
        }

        static float Header(Model M, SKCanvas c, string eyebrow, string title, string? sub)
        {
            float colL = M.ColL, colW = M.ColW;
            float y = M.Card.Top + 64;
            Text.Tracked(c, eyebrow, colL, y, Fonts.Inter.Bold, 13, 3.6f, Gold);
            Text.Fit(c, title.ToUpperInvariant(), colL, y + 54, Fonts.CormorantGaramond.Bold, 50, 24, colW, Txt);
            using (var rl = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(colL, 0), new SKPoint(colL + colW, 0), new[] { Gold.WithAlpha(200), Gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(colL, y + 72, colW, 1.4f, rl);
            float dy = y + 104;
            if (!string.IsNullOrEmpty(sub))
                foreach (var ln in Text.Wrap(sub, Fonts.Inter.Medium, 16, colW))
                {
                    Text.Plain(c, ln, colL, dy, Fonts.Inter.Medium, 16, Txt2);
                    dy += 24;
                }
            return dy + 26;
        }

        static void StopColumn(Model M, SKCanvas c, int page, int si, ReviewSegment s, SKColor acc)
        {
            var d = M.D;
            float colL = M.ColL, colW = M.ColW, colX = colL + colW / 2;
            float storyTop = Header(M, c, Where(d), d.TrackDisplay, Summary(d, M.Imperial));
            // progress: one tick per stop; its height is the time to find there; the current one lit in its colour
            int n = M.Stops.Length;
            double maxGain = d.Segments.Max(x => x.Gain ?? 0);
            float tick = colW / Math.Max(1, n);
            for (int i = 0; i < n; i++)
            {
                var st = d.Segments[M.Stops[i]];
                bool now = i == page - 1, done = i < page - 1;
                var col = now ? acc : done ? new SKColor(255, 255, 255, 150) : new SKColor(255, 255, 255, 48);
                float hgt = st.Straight ? 2f : 5f;
                if (maxGain > 0 && st.Gain is { } gn) hgt += (float)(gn / maxGain) * 12f;
                using var tp = new SKPaint { IsAntialias = true, Color = col };
                c.DrawRoundRect(new SKRect(colL + i * tick + 1.5f, storyTop + 6 - hgt, colL + (i + 1) * tick - 1.5f, storyTop + 6), 1.5f, 1.5f, tp);
            }
            Text.Tracked(c, (s.Straight ? "STRAIGHT  " : "CORNER  ") + (page) + "  OF  " + n, colL, storyTop + 36, Fonts.Inter.Bold, 13, 3.2f, acc);
            // trouble there, on the right of the same line
            string trouble = Trouble(s);
            if (trouble.Length > 0)
            {
                float tw = Text.TrackedWidth(trouble, Fonts.Inter.Bold, 12, 2.4f);
                float tx = colL + colW - tw;
                Warn(c, tx - 22, storyTop + 31.5f, 8.5f);
                Text.Tracked(c, trouble, tx, storyTop + 36, Fonts.Inter.Bold, 12, 2.4f, Amber);
            }

            // the corner's own themed label, scaled to fit the column (as the track card does)
            string nm = s.Name.ToUpperInvariant();
            var thk = CornerTheme.For(nm)?.Kind;
            float nwid = Text.Width(nm, Fonts.CormorantGaramond.Bold, 40) + 0.6f * nm.Length;
            float need = thk switch
            {
                CornerTheme.Kind.Dunlop => Math.Max(760f, (nwid + 50f) / 0.605f),
                CornerTheme.Kind.Maker when nm.Contains("FORD") => 330f,
                CornerTheme.Kind.Maker when nm.Contains("CORVETTE") => (BrandGlyphs.CanSpell("corvette", nm) ? Math.Min(640f, BrandGlyphs.Measure("corvette", nm, 24f)) : nwid) + 90f,
                CornerTheme.Kind.Maker => nwid + 2 * 120f,
                CornerTheme.Kind.PlayStation => nwid + 200f,
                CornerTheme.Kind.Michelin => nwid + 200f,
                _ => nwid + 40f,
            };
            string? her = CornerTheme.HeritageOf(d.TrackRaw, s.Name);
            if (her != null) need = Math.Max(need, Text.TrackedWidth(her, Fonts.Inter.Bold, 17, 3.2f) + 70f);   // the heritage pill must fit too
            float zs = Math.Min(thk == CornerTheme.Kind.Dunlop ? 1.05f : 1.3f, (colW + 30f) / need);
            float labelY = thk == CornerTheme.Kind.Dunlop ? storyTop + 92 : storyTop + 104;
            int sv = c.Save();
            c.Translate(colX, labelY); c.Scale(zs, zs);
            CornerTheme.DrawLabel(c, 0, 0, s.Straight ? "DOWN" : "INTO", nm, 1f, her);
            c.RestoreToCount(sv);

            // the numbers - 2026-10-27 (Angelo: "some of the corner and straight logos overlap the information ... like Dunlop
            // bridge, move the information down just a little"): start them below the label's real drawn bottom
            float labelBottom = labelY + LabelBottom(s.Straight ? "DOWN" : "INTO", nm, her) * zs;
            float y1 = Math.Max(storyTop + 250, labelBottom + 70);
            var row1 = new List<Cell>();
            if (s.BestTime is { } bt)
            {
                row1.Add(new Cell("YOUR BEST", Secs(bt), s.BestLap > 0 ? "LAP " + s.BestLap : "", Txt));
                if (s.TypicalTime is { } tt) row1.Add(new Cell("TYPICAL", Secs(tt), s.Passes > 1 ? "MIDDLE OF " + s.Passes + " CLEAN" : "ONE CLEAN PASS", Txt));
                if (s.Gain is { } g && s.Passes > 1) row1.Add(new Cell("TO FIND", "+" + g.ToString("0.000", CultureInfo.InvariantCulture), "SECONDS A LAP", acc));
            }
            else row1.Add(new Cell("NO CLEAN PASS", "-", "EVERY TIME THROUGH HAD TROUBLE", Amber));
            Cells(c, row1, colL, colW, y1);
            var row2 = new List<Cell>();
            string U = M.Imperial ? "MPH" : "KM/H";
            if (!s.Straight)
            {
                if (s.MinKmhBest is { } mb) row2.Add(new Cell("SLOWEST", Speed(mb, M.Imperial), s.MinKmhTypical is { } mt ? $"{U} · TYPICAL {Speed(mt, M.Imperial)}" : U, Txt));
                else if (s.ExitKmhBest is { } xb) row2.Add(new Cell("LEAVING IT", Speed(xb, M.Imperial), s.ExitKmhTypical is { } xt ? $"{U} · TYPICAL {Speed(xt, M.Imperial)}" : U, Txt));
                if (s.BrakeToApexBest is { } bb) row2.Add(new Cell("BRAKE", $"{bb:0} M", s.BrakeToApexTypical is { } btp ? $"TO APEX · TYP {btp:0}" : "TO THE APEX", Txt));
                else if (s.Passes > 0 && s.MinKmhBest != null) row2.Add(new Cell("BRAKE", "NONE", "TAKEN WITHOUT BRAKING", Txt));
                if (s.ThrottleAfterApexBest is { } tb) row2.Add(new Cell("FULL THROTTLE", $"{tb:0} M", s.ThrottleAfterApexTypical is { } ttp ? $"AFTER APEX · TYP {ttp:0}" : "AFTER THE APEX", Txt));
                if (row2.Count < 4 && s.MinKmhBest != null && s.Gear is { } gr && gr > 0) row2.Add(new Cell("GEAR", gr.ToString(CultureInfo.InvariantCulture), "AT THE APEX", Txt));
            }
            else
            {
                if (s.EntryKmhBest is { } eb) row2.Add(new Cell("ONTO IT", Speed(eb, M.Imperial), s.EntryKmhTypical is { } et ? $"{U} · TYPICAL {Speed(et, M.Imperial)}" : U, Txt));
                if (s.TopKmhBest is { } tb) row2.Add(new Cell("TOP SPEED", Speed(tb, M.Imperial), s.TopKmhTypical is { } tt ? $"{U} · TYPICAL {Speed(tt, M.Imperial)}" : U, Txt));
                row2.Add(new Cell("LENGTH", M.Imperial ? (s.LengthM / 1609.344).ToString("0.00", CultureInfo.InvariantCulture) + " MI" : (s.LengthM / 1000).ToString("0.00", CultureInfo.InvariantCulture) + " KM", "", Txt));
            }
            if (s.Passes > 0) Cells(c, row2, colL, colW, y1 + 104);

            // the coaching line's room: when a tall corner label pushed the numbers down, the coaching line wins over the
            // pass strip so they never overlap (2026-10-27, the Dunlop bridge pages)
            string tip = SessionReviewAnalyzer.Tip(s, M.Imperial);
            var tipLines = tip.Length > 0 ? Text.Wrap(tip, Fonts.Inter.SemiBold, 18, colW - 24).ToList() : new List<string>();
            float tipTop = y1 + 214 - 18, tipBottom = tipTop + 26 * tipLines.Count + 4;
            bool stripFits = tipLines.Count == 0 || tipBottom < M.Card.Bottom - 150 - 22;

            // every clean pass, by time: how far each was from the best one
            if (stripFits && s.PassTimes is { Length: >= 2 } pt && s.BestTime is { } best0)
            {
                float sy = M.Card.Bottom - 150;
                float spread = (float)(pt.Max() - best0);
                string right = "SLOWEST +" + spread.ToString("0.00", CultureInfo.InvariantCulture) + " S";
                Text.Tracked(c, "EVERY CLEAN PASS", colL, sy, Fonts.Inter.Bold, 11, 2.6f, Txt3);
                Text.Tracked(c, right, colL + colW, sy, Fonts.Inter.Bold, 11, 2.2f, Txt3, align: 1);
                float x0 = colL + 8, x1 = colL + colW - 8, yy = sy + 24;
                using (var axis = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(40) }) c.DrawRect(x0, yy - 0.6f, x1 - x0, 1.2f, axis);
                float range = Math.Max(0.05f, spread);
                using var dp = new SKPaint { IsAntialias = true };
                foreach (var v in pt.OrderByDescending(v => v))
                {
                    float x = x0 + (x1 - x0) * (float)((v - best0) / range);
                    bool isBest = Math.Abs(v - best0) < 1e-4;
                    dp.Color = isBest ? Gold : Txt.WithAlpha(150);
                    c.DrawCircle(x, yy, isBest ? 7f : 5f, dp);
                }
                if (s.TypicalTime is { } typ)
                {
                    float x = x0 + (x1 - x0) * (float)((typ - best0) / range);
                    using var tk = new SKPaint { IsAntialias = true, Color = acc, StrokeWidth = 2 };
                    c.DrawLine(x, yy - 13, x, yy + 13, tk);
                    Text.Tracked(c, "TYPICAL", x, yy + 30, Fonts.Inter.Bold, 9.5f, 1.6f, acc, align: 0);
                }
                Text.Tracked(c, "BEST", x0, yy + 30, Fonts.Inter.Bold, 9.5f, 1.6f, Gold, align: 0);
            }

            // the coaching line
            if (tipLines.Count > 0)
            {
                float ty = y1 + 214;
                var lines = tipLines;
                using (var bar = new SKPaint { IsAntialias = true, Color = acc }) c.DrawRect(new SKRect(colL, ty - 18, colL + 3, ty - 18 + 26 * lines.Count + 4), bar);
                foreach (var ln in lines) { Text.Plain(c, ln, colL + 18, ty, Fonts.Inter.SemiBold, 18, Txt); ty += 26; }
            }
        }

        readonly record struct Cell(string Label, string Value, string Sub, SKColor Colour);

        static readonly ConcurrentDictionary<string, float> LabelBottoms = new();
        /// <summary>How far below its origin a corner label really draws (logos, the Dunlop bridge, heritage pill), measured
        /// once per label by drawing it offscreen and finding the lowest visible pixel. Page bake time only, never per frame.</summary>
        static float LabelBottom(string eye, string name, string? heritage)
        {
            return LabelBottoms.GetOrAdd(eye + "|" + name + "|" + heritage, _ =>
            {
                const int W = 1600, H = 900, OX = 800, OY = 300;
                try
                {
                    using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Premul));
                    using (var cv = new SKCanvas(bmp))
                    {
                        cv.Clear(SKColors.Transparent);
                        cv.Translate(OX, OY);
                        CornerTheme.DrawLabel(cv, 0, 0, eye, name, 1f, heritage);
                    }
                    var px = bmp.GetPixelSpan();
                    for (int y = H - 1; y >= 0; y--)
                    {
                        int row = y * W * 4;
                        for (int x = 0; x < W; x++)
                            if (px[row + x * 4 + 3] > 40) return y - OY;
                    }
                }
                catch { }
                return 120f;
            });
        }

        static void Cells(SKCanvas c, List<Cell> cells, float colL, float colW, float y)
        {
            if (cells.Count == 0) return;
            float cw = colW / cells.Count;
            float vs = cells.Count >= 4 ? 34 : 40;
            for (int i = 0; i < cells.Count; i++)
            {
                float x0 = colL + i * cw;
                var ce = cells[i];
                Text.Tracked(c, ce.Label, x0, y, Fonts.Inter.Bold, 11, 2.6f, Txt3);
                Text.Fit(c, ce.Value, x0, y + 42, Fonts.BarlowCondensed.ExtraBold, vs, 20, cw - 16, ce.Colour);
                if (ce.Sub.Length > 0) Text.FitTracked(c, ce.Sub, x0, y + 64, Fonts.Inter.SemiBold, 10.5f, 1.4f, cw - 14, Txt3);
                if (i > 0) using (var sep = new SKPaint { Color = new SKColor(255, 255, 255, 30) }) c.DrawRect(x0 - 14, y - 12, 1, 84, sep);
            }
        }

        static string Trouble(ReviewSegment s)
        {
            var parts = new List<string>(3);
            if (s.Incidents > 0) parts.Add(s.Incidents + "X");
            if (s.Spins > 0) parts.Add(s.Spins == 1 ? "1 SPIN" : s.Spins + " SPINS");
            if (s.Offs > 0) parts.Add(s.Offs == 1 ? "1 OFF" : s.Offs + " OFFS");
            return string.Join("  ·  ", parts);
        }

        static void Warn(SKCanvas c, float cx, float cy, float r)
        {
            using var p = new SKPaint { IsAntialias = true, Color = Amber };
            using var tri = new SKPath();
            tri.MoveTo(cx, cy - r); tri.LineTo(cx + r * 1.05f, cy + r * 0.8f); tri.LineTo(cx - r * 1.05f, cy + r * 0.8f); tri.Close();
            c.DrawPath(tri, p);
            p.Color = new SKColor(0x1A, 0x10, 0x02);
            c.DrawRect(new SKRect(cx - 0.9f, cy - r * 0.45f, cx + 0.9f, cy + r * 0.25f), p);
            c.DrawCircle(cx, cy + r * 0.5f, 1.1f, p);
        }

        // ------------------------------------------------------------------------------------------- the speed trace
        static void SpeedChart(Model M, SKCanvas c, ReviewSegment s)
        {
            var r = M.ChartRect;
            using (var rr = new SKRoundRect(r, 12, 12))
            {
                using (var bg = new SKPaint { IsAntialias = true, Color = Glass.WithAlpha(196) }) c.DrawRoundRect(rr, bg);
                using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Txt.WithAlpha(24) }) c.DrawRoundRect(rr, rim);
            }
            string U = M.Imperial ? "MPH" : "KM/H";
            Text.Tracked(c, "SPEED THROUGH " + s.Name.ToUpperInvariant() + "  ·  " + U, r.Left + 22, r.Top + 30, Fonts.Inter.Bold, 12, 2.8f, Txt2);
            // legend, right
            float lx = r.Right - 22;
            string l2 = "TYPICAL", l1 = "YOUR BEST" + (s.BestLap > 0 ? "  ·  LAP " + s.BestLap : "");
            float w2 = Text.TrackedWidth(l2, Fonts.Inter.Bold, 11, 2.2f), w1 = Text.TrackedWidth(l1, Fonts.Inter.Bold, 11, 2.2f);
            Text.Tracked(c, l2, lx - w2, r.Top + 30, Fonts.Inter.Bold, 11, 2.2f, Txt3);
            using (var lp = new SKPaint { IsAntialias = true, StrokeWidth = 2.4f, Color = Txt.WithAlpha(140) }) c.DrawLine(lx - w2 - 34, r.Top + 26, lx - w2 - 10, r.Top + 26, lp);
            float x1 = lx - w2 - 56 - w1;
            Text.Tracked(c, l1, x1, r.Top + 30, Fonts.Inter.Bold, 11, 2.2f, Gold);
            using (var lp = new SKPaint { IsAntialias = true, StrokeWidth = 3.2f, Color = Gold }) c.DrawLine(x1 - 34, r.Top + 26, x1 - 10, r.Top + 26, lp);

            var plot = new SKRect(r.Left + 92, r.Top + 52, r.Right - 24, r.Bottom - 46);
            var tb = s.TraceBest!; var tt = s.TraceTypical;
            int N = tb.Length;
            float conv = M.Imperial ? 1f / 1.609344f : 1f;
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var v in tb) if (!float.IsNaN(v)) { mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
            if (tt != null) foreach (var v in tt) if (!float.IsNaN(v)) { mn = Math.Min(mn, v); mx = Math.Max(mx, v); }
            if (mn >= mx) { mn -= 10; mx += 10; }
            mn *= conv; mx *= conv;
            float step = mx - mn > 120 ? 50 : mx - mn > 50 ? 25 : 10;
            float lo = MathF.Floor(mn / step) * step, hi = MathF.Ceiling(mx / step) * step;
            float X(int i) => plot.Left + plot.Width * i / (N - 1);
            float Y(float v) => plot.Bottom - (v * conv - lo) / (hi - lo) * plot.Height;
            // grid + axis numbers
            using (var gp = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(20), StrokeWidth = 1 })
                for (float v = lo; v <= hi + 0.1f; v += step)
                {
                    float yy = plot.Bottom - (v - lo) / (hi - lo) * plot.Height;
                    c.DrawLine(plot.Left, yy, plot.Right, yy, gp);
                    Text.Plain(c, v.ToString("0", CultureInfo.InvariantCulture), plot.Left - 12, yy + 5, Fonts.Inter.SemiBold, 12, Txt3, align: 1);
                }
            // the stretch itself, shaded
            float f0 = (float)(((s.StartPct - s.TraceFromPct) % 1 + 1) % 1 / s.TraceSpanPct), f1 = f0 + (float)(SegLen(s) / s.TraceSpanPct);
            using (var sp = new SKPaint { Color = Txt.WithAlpha(10) })
                c.DrawRect(new SKRect(plot.Left + plot.Width * f0, plot.Top, plot.Left + plot.Width * Math.Min(1, f1), plot.Bottom), sp);
            using (var dl = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(46), StrokeWidth = 1, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) })
            {
                c.DrawLine(plot.Left + plot.Width * f0, plot.Top, plot.Left + plot.Width * f0, plot.Bottom, dl);
                if (f1 <= 1) c.DrawLine(plot.Left + plot.Width * f1, plot.Top, plot.Left + plot.Width * f1, plot.Bottom, dl);
            }
            // brake / throttle strips under the plot (best pass)
            float by = plot.Bottom + 10, bh = 7;
            if (s.BrakeBest != null && s.ThrottleBest != null)
                for (int i = 0; i < N - 1; i++)
                {
                    float x0 = X(i), xw = X(i + 1) - x0 + 0.6f;
                    float bv = Math.Clamp(s.BrakeBest[i], 0, 1), tv = Math.Clamp(s.ThrottleBest[i], 0, 1);
                    using var bp = new SKPaint { Color = BrakeRed.WithAlpha((byte)(30 + 225 * bv)) };
                    if (bv > 0.02f) c.DrawRect(x0, by, xw, bh, bp);
                    bp.Color = GoGreen.WithAlpha((byte)(30 + 225 * tv));
                    if (tv > 0.02f) c.DrawRect(x0, by + bh + 4, xw, bh, bp);
                }
            Text.Tracked(c, "BRAKE", plot.Left - 12, by + 7, Fonts.Inter.Bold, 9.5f, 1.4f, BrakeRed, align: 1);
            Text.Tracked(c, "THROTTLE", plot.Left - 12, by + bh + 4 + 7, Fonts.Inter.Bold, 9.5f, 1.4f, GoGreen, align: 1);
            // the traces
            SKPath Trace(float[] a)
            {
                var p = new SKPath(); bool first = true;
                for (int i = 0; i < N; i++) { if (float.IsNaN(a[i])) { first = true; continue; } if (first) { p.MoveTo(X(i), Y(a[i])); first = false; } else p.LineTo(X(i), Y(a[i])); }
                return p;
            }
            if (tt != null)
                using (var tp = Trace(tt)) using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeJoin = SKStrokeJoin.Round, Color = Txt.WithAlpha(140) })
                    c.DrawPath(tp, lp);
            using (var bpth = Trace(tb))
            {
                using (var gl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8, StrokeJoin = SKStrokeJoin.Round, Color = Gold.WithAlpha(60), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) })
                    c.DrawPath(bpth, gl);
                using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.2f, StrokeJoin = SKStrokeJoin.Round, Color = Gold })
                    c.DrawPath(bpth, lp);
            }
            // the slowest point of the best pass, labelled
            if (!s.Straight && s.MinKmhBest is { } mk)
            {
                int mi = -1;
                for (int i = 0; i < N; i++)
                    if (!float.IsNaN(tb[i]) && X(i) >= plot.Left + plot.Width * f0 - 2 && X(i) <= plot.Left + plot.Width * Math.Min(1, f1) + 2 && (mi < 0 || tb[i] < tb[mi])) mi = i;
                if (mi < 0) return;
                float px = X(mi), py = Y(tb[mi]);
                using (var dp = new SKPaint { IsAntialias = true, Color = Warm }) c.DrawCircle(px, py, 4.5f, dp);
                Text.Plain(c, Speed(mk, M.Imperial), Math.Clamp(px, plot.Left + 18, plot.Right - 18) + (py + 24 > plot.Bottom - 4 ? 22 : 0), py + 24 > plot.Bottom - 4 ? py + 6 : py + 24, Fonts.BarlowCondensed.Bold, 20, Gold, align: 0);
            }
            if (s.Straight && s.TopKmhBest is { } tk)
            {
                int mi = 0; for (int i = 0; i < N; i++) if (!float.IsNaN(tb[i]) && tb[i] > tb[mi]) mi = i;
                float px = X(mi), py = Y(tb[mi]);
                using (var dp = new SKPaint { IsAntialias = true, Color = Warm }) c.DrawCircle(px, py, 4.5f, dp);
                Text.Plain(c, Speed(tk, M.Imperial), Math.Min(px, plot.Right - 18), py - 12, Fonts.BarlowCondensed.Bold, 20, Gold, align: 0);
            }
        }

        static double SegLen(ReviewSegment s) { double a = s.EndPct - s.StartPct; return a <= 0 ? a + 1 : a; }

        // ------------------------------------------------------------------------------------------- opener
        static void BakeOpener(Model M, Page pg)
        {
            var d = M.D;
            pg.Left = Picture(M, M.LeftRect, c =>
            {
                float colL = M.ColL, colW = M.ColW;
                float top = Header(M, c, Where(d), d.TrackDisplay, Summary(d, M.Imperial));
                // the headline: the best lap, big
                if (d.BestLap is { } bl)
                {
                    Text.Tracked(c, "BEST LAP" + (d.BestLapNo > 0 ? "  ·  LAP " + d.BestLapNo : ""), colL, top + 18, Fonts.Inter.Bold, 13, 3.2f, Gold);
                    Text.Plain(c, LapTime(bl), colL - 3, top + 118, Fonts.BarlowCondensed.ExtraBold, 112, Txt);
                }
                float y = top + 176;
                var r1 = new List<Cell>();
                if (d.Theoretical is { } th) r1.Add(new Cell("THEORETICAL", LapTime(th), "YOUR BEST OF EVERY PIECE", Gold));
                if (d.TypicalLap is { } tl) r1.Add(new Cell("TYPICAL LAP", LapTime(tl), "MIDDLE OF " + d.CleanLaps + " CLEAN LAPS", Txt));
                if (d.Theoretical is { } th2 && d.BestLap is { } bl2 && bl2 - th2 > 0.0005) r1.Add(new Cell("TO THEORETICAL", "+" + (bl2 - th2).ToString("0.000", CultureInfo.InvariantCulture), "FROM YOUR BEST LAP", Txt));
                Cells(c, r1, colL, colW, y);
                var r2 = new List<Cell>();
                if (d.IsRace && d.FinishClassPos is { } fp)
                    r2.Add(new Cell(d.CarClass.Length > 0 ? "FINISHED IN " + d.CarClass.ToUpperInvariant() : "FINISHED", "P" + fp,
                                    (d.StartClassPos is { } sp ? "STARTED P" + sp : "") + (d.ClassSize is { } cs2 && cs2 > 1 ? (d.StartClassPos != null ? " · " : "") + cs2 + " CARS" : ""), Txt));
                r2.Add(new Cell(d.IsRace ? "LAPS" : "TIMED LAPS", d.LapsCompleted.ToString(CultureInfo.InvariantCulture), d.CleanLaps + " CLEAN" + (d.PitStops > 0 ? " · " + d.PitStops + (d.PitStops == 1 ? " STOP" : " STOPS") : ""), Txt));
                r2.Add(new Cell("INCIDENTS", d.Incidents + "X", (d.Offs > 0 || d.Spins > 0) ? string.Join(" · ", new[] { d.Spins > 0 ? d.Spins + (d.Spins == 1 ? " SPIN" : " SPINS") : "", d.Offs > 0 ? d.Offs + (d.Offs == 1 ? " OFF" : " OFFS") : "" }.Where(x => x.Length > 0)) : "CLEAN SESSION", d.Incidents > 0 ? Amber : Txt));
                if (d.TopKmh is { } tk) r2.Add(new Cell("TOP SPEED", Speed(tk, M.Imperial), M.Imperial ? "MPH" : "KM/H", Txt));
                Cells(c, r2, colL, colW, y + 104);
                var r3 = new List<Cell>();
                bool f = M.Imperial;
                string T(float cc) => f ? $"{cc * 9 / 5 + 32:0}°F" : $"{cc:0}°C";
                if (d.AirC is { } air) r3.Add(new Cell("AIR", T(air), "AVERAGE", Txt));
                if (d.TrackC is { } trk) r3.Add(new Cell("TRACK", T(trk), "AVERAGE", Txt));
                if (d.DrivenMinutes > 0.5) r3.Add(new Cell("SESSION", d.DrivenMinutes >= 60 ? $"{(int)(d.DrivenMinutes / 60)}H {(int)(d.DrivenMinutes % 60):00}M" : $"{Math.Round(d.DrivenMinutes):0} MIN", "RECORDED", Txt));
                if (d.Driver.Length > 0) r3.Add(new Cell("DRIVER", d.Driver.ToUpperInvariant(), d.CarClass.Length > 0 ? d.CarClass.ToUpperInvariant() : "", Txt));
                Cells(c, r3, colL, colW, y + 208);
                // what comes next
                string next = d.Focus.Count > 0 ? "UP NEXT: EVERY CORNER AND STRAIGHT, YOUR BEST AGAINST YOUR TYPICAL - THEN THE " + (d.Focus.Count == 1 ? "PLACE" : Words(d.Focus.Count).ToUpperInvariant() + " PLACES") + " WITH THE MOST TIME TO FIND."
                                                : "UP NEXT: EVERY CORNER AND STRAIGHT, YOUR BEST AGAINST YOUR TYPICAL.";
                var nl = Text.WrapTracked(next, Fonts.Inter.SemiBold, 12, 1.6f, colW);
                float ty = M.Card.Bottom - 150 - (nl.Count - 1) * 20;
                foreach (var ln in nl) { Text.Tracked(c, ln, colL, ty, Fonts.Inter.SemiBold, 12, 1.6f, Txt3); ty += 20; }
            });
            if (d.Laps.Count > 0) pg.Chart = Picture(M, M.ChartRect, c => LapChart(M, c));
        }

        static void LapChart(Model M, SKCanvas c)
        {
            var d = M.D; var r = M.ChartRect;
            using (var rr = new SKRoundRect(r, 12, 12))
            {
                using (var bg = new SKPaint { IsAntialias = true, Color = Glass.WithAlpha(196) }) c.DrawRoundRect(rr, bg);
                using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Txt.WithAlpha(24) }) c.DrawRoundRect(rr, rim);
            }
            Text.Tracked(c, "EVERY LAP", r.Left + 22, r.Top + 30, Fonts.Inter.Bold, 12, 2.8f, Txt2);
            float lx = r.Right - 22;
            void Key(string label, SKColor col)
            {
                float w = Text.TrackedWidth(label, Fonts.Inter.Bold, 11, 2.2f);
                Text.Tracked(c, label, lx - w, r.Top + 30, Fonts.Inter.Bold, 11, 2.2f, Txt3);
                bool outline = col.Alpha == 0;
                using var p = new SKPaint { IsAntialias = true, Color = outline ? Txt.WithAlpha(150) : col, Style = outline ? SKPaintStyle.Stroke : SKPaintStyle.Fill, StrokeWidth = 1.4f };
                c.DrawRoundRect(new SKRect(lx - w - 22, r.Top + 21, lx - w - 10, r.Top + 31), 2, 2, p);
                lx -= w + 44;
            }
            Key("PIT LANE", Txt.WithAlpha(50)); Key("NOT COUNTED", Txt.WithAlpha(0)); Key("TROUBLE", Coral); Key("CLEAN", Txt.WithAlpha(170)); Key("BEST", Gold);
            var plot = new SKRect(r.Left + 70, r.Top + 56, r.Right - 24, r.Bottom - 34);
            var laps = d.Laps;
            var clean = laps.Where(l => !l.Pit).Select(l => l.Time).OrderBy(v => v).ToList();
            double best = d.BestLap ?? (clean.Count > 0 ? clean[0] : laps.Min(l => l.Time));
            double lo = Math.Floor(Math.Min(best, clean.Count > 0 ? clean[0] : best) - 1.0), hi = Math.Max(lo + 3, Math.Ceiling((clean.Count > 0 ? clean[^1] : best) + 0.6));
            if (hi - lo > 14) hi = lo + 14;
            double step = hi - lo > 8 ? 4 : hi - lo > 4 ? 2 : 1;
            using (var gp = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(20) })
                for (double v = lo; v <= hi + 1e-6; v += step)
                {
                    float yy = plot.Bottom - (float)((v - lo) / (hi - lo)) * plot.Height;
                    c.DrawLine(plot.Left, yy, plot.Right, yy, gp);
                    Text.Plain(c, LapTimeShort(v), plot.Left - 12, yy + 5, Fonts.Inter.SemiBold, 12, Txt3, align: 1);
                }
            int n = laps.Count;
            float slot = plot.Width / n, bw = Math.Min(46, slot * 0.62f);
            for (int i = 0; i < n; i++)
            {
                var l = laps[i];
                float cx = plot.Left + slot * (i + 0.5f);
                bool isBest = d.BestLapNo == l.Number && Math.Abs(l.Time - best) < 0.0005;
                bool counted = l.Counted || l.Pit;
                var col = l.Pit ? Txt.WithAlpha(50) : isBest ? Gold : !counted ? Txt.WithAlpha(150) : l.Incidents > 0 || !l.Clean ? Coral : Txt.WithAlpha(170);
                float v = (float)Math.Clamp((l.Time - lo) / (hi - lo), 0.02, 1);
                float top = plot.Bottom - v * plot.Height;
                using (var bp = new SKPaint { IsAntialias = true, Color = col, Style = counted ? SKPaintStyle.Fill : SKPaintStyle.Stroke, StrokeWidth = 1.6f })
                    c.DrawRoundRect(counted ? new SKRect(cx - bw / 2, top, cx + bw / 2, plot.Bottom) : new SKRect(cx - bw / 2 + 0.8f, top + 0.8f, cx + bw / 2 - 0.8f, plot.Bottom), 3, 3, bp);
                if (l.Pit) Text.Tracked(c, "PIT", cx, top + 16, Fonts.Inter.Bold, 9.5f, 1.2f, Txt3, align: 0);
                if (isBest) Text.Plain(c, LapTime(l.Time), cx, top - 8, Fonts.BarlowCondensed.Bold, 18, Gold, align: 0);
                Text.Plain(c, l.Number.ToString(CultureInfo.InvariantCulture), cx, plot.Bottom + 20, Fonts.Inter.SemiBold, 12, isBest ? Gold : Txt3, align: 0);
            }
        }

        // ------------------------------------------------------------------------------------------- closer
        static void BakeCloser(Model M, Page pg)
        {
            var d = M.D;
            var focus = d.Focus.Where(i => i >= 0 && i < d.Segments.Count).ToList();
            if (M.PlanePath != null)
            {
                pg.Focus = focus.Select(i => M.SegmentPath(d.Segments[i].StartPct, d.Segments[i].EndPct)).ToArray();
                var pins = new List<Pin>();
                for (int k = 0; k < focus.Count; k++)
                {
                    var s = d.Segments[focus[k]];
                    float mid = s.StartPct + (float)SegLen(s) / 2;
                    pins.Add(NumberPin(M, mid, k + 1, s.Name.ToUpperInvariant(), "+" + (s.Gain ?? 0).ToString("0.000", CultureInfo.InvariantCulture) + " S"));
                }
                pg.Pins = pins.ToArray();
            }
            double sum = focus.Sum(i => d.Segments[i].Gain ?? 0);
            pg.Left = Picture(M, M.LeftRect, c =>
            {
                float colL = M.ColL, colW = M.ColW;
                string sub = focus.Count == 0 ? "No place stands out: your typical pass is within a few hundredths of your best everywhere."
                    : $"Match your own best pass in {(focus.Count == 1 ? "this place" : "these " + Words(focus.Count) + " places")} and your typical lap is {sum.ToString("0.000", CultureInfo.InvariantCulture)} s quicker.";
                float top = Header(M, c, "THE TAKEAWAY", focus.Count == 0 ? "A CONSISTENT SESSION" : "WORK ON THESE " + Words(focus.Count).ToUpperInvariant(), sub);
                float y = top + 14;
                for (int k = 0; k < focus.Count; k++)
                {
                    var s = d.Segments[focus[k]];
                    var acc = CornerTheme.Accent(s.Name);
                    // the number in a ring
                    using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, Color = acc })
                        c.DrawCircle(colL + 24, y + 28, 23, ring);
                    Text.Plain(c, (k + 1).ToString(CultureInfo.InvariantCulture), colL + 24, y + 40, Fonts.BarlowCondensed.ExtraBold, 32, acc, align: 0);
                    float tx = colL + 70;
                    Text.Tracked(c, (s.Straight ? "STRAIGHT" : "CORNER") + "  ·  " + (s.Passes > 1 ? s.Passes + " CLEAN PASSES" : ""), tx, y + 10, Fonts.Inter.Bold, 11, 2.4f, acc);
                    Text.Fit(c, s.Name.ToUpperInvariant(), tx, y + 46, Fonts.CormorantGaramond.Bold, 34, 20, colW - 70 - 150, Txt);
                    Text.Plain(c, "+" + (s.Gain ?? 0).ToString("0.000", CultureInfo.InvariantCulture), colL + colW, y + 44, Fonts.BarlowCondensed.ExtraBold, 40, acc, align: 1);
                    Text.Tracked(c, "SECONDS A LAP", colL + colW, y + 64, Fonts.Inter.Bold, 10, 2f, Txt3, align: 1);
                    string tip = SessionReviewAnalyzer.Tip(s, M.Imperial);
                    float ty = y + 76;
                    if (tip.Length > 0)
                        foreach (var ln in Text.Wrap(tip, Fonts.Inter.Medium, 15, colW - 70 - (ty < y + 80 ? 130 : 0)).Take(2))
                        { Text.Plain(c, ln, tx, ty, Fonts.Inter.Medium, 15, Txt2); ty += 21; }
                    y += 148;
                    if (k < focus.Count - 1) using (var sep = new SKPaint { Color = Txt.WithAlpha(24) }) c.DrawRect(tx, y - 22, colW - 70, 1, sep);
                }
                // the sum of it: today's typical lap, and with all of them
                if (focus.Count > 0 && d.TypicalLap is { } typ)
                {
                    var cl = new List<Cell> { new("TYPICAL LAP", LapTime(typ), "MIDDLE OF " + d.CleanLaps + " CLEAN LAPS", Txt),
                                              new("WITH " + (focus.Count == 1 ? "IT" : Words(focus.Count).ToUpperInvariant()), LapTime(typ - sum), "SAME LAP, THOSE PLACES FIXED", Gold) };
                    if (d.Theoretical is { } th) cl.Add(new Cell("THEORETICAL", LapTime(th), "YOUR BEST OF EVERY PIECE", Txt));
                    Cells(c, cl, colL, colW, M.Card.Bottom - 230);
                }
            });
            if (d.Segments.Any(x => x.Gain.HasValue)) pg.Chart = Picture(M, M.ChartRect, c => GainChart(M, c, focus));
        }

        static Pin NumberPin(Model M, float pct, int num, string name, string value)
        {
            var at = M.PointAt(pct);
            const float h = 48;
            float w1 = Text.TrackedWidth(name, Fonts.Inter.Bold, 11, 2.2f), w2 = Text.Width(value, Fonts.BarlowCondensed.Bold, 24);
            float w = Math.Max(w1, w2) + 32 + 40;
            var img = PictureSize(M, w, h, c =>
            {
                using (var rr = new SKRoundRect(new SKRect(0, 0, w, h), 6, 6))
                using (var bg = new SKPaint { IsAntialias = true, Color = Glass.WithAlpha(220) }) c.DrawRoundRect(rr, bg);
                using (var nb = new SKPaint { IsAntialias = true, Color = Gold }) c.DrawRoundRect(new SKRoundRect(new SKRect(0, 0, 38, h), 6, 6), nb);
                Text.Plain(c, num.ToString(CultureInfo.InvariantCulture), 19, 34, Fonts.BarlowCondensed.ExtraBold, 30, new SKColor(0x14, 0x10, 0x04), align: 0);
                Text.Tracked(c, name, 52, 17, Fonts.Inter.Bold, 11, 2.2f, Gold);
                Text.Plain(c, value, 52, 40, Fonts.BarlowCondensed.Bold, 24, Txt);
            });
            return new Pin { Plane = at, Colour = Gold, Image = img };
        }

        static void GainChart(Model M, SKCanvas c, List<int> focus)
        {
            var d = M.D; var r = M.ChartRect;
            using (var rr = new SKRoundRect(r, 12, 12))
            {
                using (var bg = new SKPaint { IsAntialias = true, Color = Glass.WithAlpha(196) }) c.DrawRoundRect(rr, bg);
                using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Txt.WithAlpha(24) }) c.DrawRoundRect(rr, rim);
            }
            Text.Tracked(c, "TIME TO FIND, AROUND THE LAP", r.Left + 22, r.Top + 30, Fonts.Inter.Bold, 12, 2.8f, Txt2);
            string note = "TYPICAL PASS MINUS YOUR BEST, SECONDS";
            Text.Tracked(c, note, r.Right - 22 - Text.TrackedWidth(note, Fonts.Inter.Bold, 11, 2.2f), r.Top + 30, Fonts.Inter.Bold, 11, 2.2f, Txt3);
            var plot = new SKRect(r.Left + 70, r.Top + 60, r.Right - 24, r.Bottom - 22);
            var segs = M.Stops.Select(i => d.Segments[i]).ToList();
            double mx = Math.Max(0.1, segs.Max(s => s.Gain ?? 0));
            double step = mx > 0.6 ? 0.2 : mx > 0.3 ? 0.1 : 0.05;
            double hi = Math.Ceiling(mx / step) * step;
            using (var gp = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(20) })
                for (double v = 0; v <= hi + 1e-6; v += step)
                {
                    float yy = plot.Bottom - (float)(v / hi) * plot.Height;
                    c.DrawLine(plot.Left, yy, plot.Right, yy, gp);
                    Text.Plain(c, v.ToString("0.00", CultureInfo.InvariantCulture), plot.Left - 12, yy + 5, Fonts.Inter.SemiBold, 12, Txt3, align: 1);
                }
            int n = segs.Count;
            float slot = plot.Width / n, bw = slot * 0.64f;
            for (int i = 0; i < n; i++)
            {
                var s = segs[i];
                int fi = focus.IndexOf(M.Stops[i]);
                float cx = plot.Left + slot * (i + 0.5f);
                float v = (float)((s.Gain ?? 0) / hi);
                float top = plot.Bottom - Math.Max(0.01f, v) * plot.Height;
                using (var bp = new SKPaint { IsAntialias = true, Color = fi >= 0 ? Gold : s.Straight ? Txt.WithAlpha(70) : Txt.WithAlpha(130) })
                    c.DrawRoundRect(new SKRect(cx - bw / 2, top, cx + bw / 2, plot.Bottom), 3, 3, bp);
                if (fi >= 0) Text.Plain(c, (fi + 1).ToString(CultureInfo.InvariantCulture), cx, top - 8, Fonts.BarlowCondensed.ExtraBold, 22, Gold, align: 0);
            }
        }

        // ------------------------------------------------------------------------------------------- helpers
        static SKImage Picture(Model M, SKRect design, Action<SKCanvas> draw)
        {
            int pw = Math.Max(1, (int)Math.Ceiling(design.Width * M.S)), ph = Math.Max(1, (int)Math.Ceiling(design.Height * M.S));
            using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            var c = surf.Canvas;
            c.Clear(SKColors.Transparent);
            c.Scale(pw / design.Width, ph / design.Height);
            c.Translate(-design.Left, -design.Top);
            draw(c);
            return surf.Snapshot();
        }

        static SKImage PictureSize(Model M, float w, float h, Action<SKCanvas> draw) => Picture(M, new SKRect(0, 0, w, h), draw);

        public void Upload(GRContext? gr)
        {
            if (gr == null) return;
            Left = ToGpu(Left, gr); Backdrop = ToGpu(Backdrop, gr); Chart = ToGpu(Chart, gr);
            for (int i = 0; i < Pins.Length; i++) Pins[i].Image = ToGpu(Pins[i].Image, gr)!;
        }

        public void Dispose()
        {
            Left?.Dispose(); Backdrop?.Dispose(); Chart?.Dispose(); Left = Backdrop = Chart = null;
            Lit?.Dispose(); InsetLit?.Dispose(); LitMeasure?.Dispose(); Lit = InsetLit = null; LitMeasure = null;
            if (Focus != null) foreach (var f in Focus) f.Dispose();
            Focus = null;
            foreach (var p in Pins) p.Image?.Dispose();
            Pins = Array.Empty<Pin>();
        }
    }

    // ---------------------------------------------------------------------------------------------- formatting
    static string Speed(float kmh, bool imperial) => Math.Round(imperial ? kmh / 1.609344f : kmh).ToString("0", CultureInfo.InvariantCulture);
    static string LapTime(double s) { int m = (int)(s / 60); return m > 0 ? $"{m}:{(s - m * 60).ToString("00.000", CultureInfo.InvariantCulture)}" : s.ToString("0.000", CultureInfo.InvariantCulture); }
    static string LapTimeShort(double s) { int m = (int)(s / 60); return m > 0 ? $"{m}:{(s - m * 60).ToString("00", CultureInfo.InvariantCulture)}" : s.ToString("0", CultureInfo.InvariantCulture); }
    static string Secs(double s) => s >= 60 ? LapTime(s) : s.ToString("0.000", CultureInfo.InvariantCulture);
    static string Words(int n) => n switch { 1 => "one", 2 => "two", 3 => "three", _ => n.ToString(CultureInfo.InvariantCulture) };

    static string SessionLabel(SessionReviewData d)
    {
        string type = d.SessionType.Length > 0 ? d.SessionType.ToUpperInvariant() : "SESSION";
        string when = d.SessionStart > DateTime.MinValue.AddYears(1) ? d.SessionStart.ToString("ddd d MMM  ·  HH:mm", CultureInfo.InvariantCulture).ToUpperInvariant() : "";
        return when.Length > 0 ? type + "  ·  " + when : type;
    }

    /// <summary>One sentence: car, laps, best, result.</summary>
    static string Summary(SessionReviewData d, bool imperial)
    {
        var parts = new List<string>(4);
        if (d.Car.Length > 0) parts.Add(d.Car);
        parts.Add(d.LapsCompleted + (d.IsRace ? (d.LapsCompleted == 1 ? " lap" : " laps") : (d.LapsCompleted == 1 ? " timed lap" : " timed laps")));
        if (d.BestLap is { } b) parts.Add("best " + LapTime(b));
        if (d.IsRace && d.FinishClassPos is { } fp) parts.Add("finished P" + fp + (d.ClassSize is { } cs && cs > 1 ? " of " + cs : "") + (d.CarClass.Length > 0 ? " in " + d.CarClass : ""));
        return string.Join("  ·  ", parts);
    }

    /// <summary>Text drawn in the bakes (thread-safe: nothing cached between threads).</summary>
    static class Text
    {
        public static float Width(string s, SKTypeface tf, float size)
        {
            using var p = new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true, SubpixelText = true };
            return p.MeasureText(s);
        }

        public static float TrackedWidth(string s, SKTypeface tf, float size, float track)
        {
            using var p = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
            float x = 0;
            foreach (var ch in s) x += p.MeasureText(ch.ToString());
            return x + track * Math.Max(0, s.Length - 1);
        }

        /// <summary>align: -1 left, 0 centre, 1 right.</summary>
        public static void Plain(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, SKColor col, int align = -1)
        {
            if (string.IsNullOrEmpty(s)) return;
            using var p = new SKPaint { Typeface = tf, TextSize = size, IsAntialias = true, SubpixelText = true, Color = col,
                                        TextAlign = align < 0 ? SKTextAlign.Left : align == 0 ? SKTextAlign.Center : SKTextAlign.Right };
            c.DrawText(s, x, y, p);
        }

        public static void Tracked(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, float track, SKColor col, int align = -1)
        {
            if (string.IsNullOrEmpty(s)) return;
            using var font = new SKFont(tf, size) { Subpixel = true };
            using var mp = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
            var glyphs = new ushort[s.Length];
            var at = new SKPoint[s.Length];
            float px = 0;
            for (int i = 0; i < s.Length; i++) { glyphs[i] = font.GetGlyph(s[i]); at[i] = new SKPoint(px, 0); px += mp.MeasureText(s[i].ToString()) + track; }
            float w = px - track;
            using var b = new SKTextBlobBuilder();
            b.AddPositionedRun(glyphs, font, at);
            using var blob = b.Build();
            using var p = new SKPaint { IsAntialias = true, Color = col };
            float x0 = align < 0 ? x : align == 0 ? x - w / 2 : x - w;
            if (blob != null) c.DrawText(blob, x0, y, p);
        }

        public static void Fit(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, float min, float maxW, SKColor col, int align = -1)
        {
            float w = Width(s, tf, size);
            if (w > maxW && w > 0) size = Math.Max(min, size * maxW / w * 0.985f);
            Plain(c, s, x, y, tf, size, col, align);
        }

        public static void FitTracked(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, float track, float maxW, SKColor col)
        {
            float w = TrackedWidth(s, tf, size, track);
            if (w > maxW && w > 0) { float k = maxW / w * 0.98f; size *= k; track *= k; }
            Tracked(c, s, x, y, tf, size, track, col);
        }

        public static List<string> WrapTracked(string text, SKTypeface tf, float size, float track, float maxW)
        {
            var outp = new List<string>(); string line = "";
            foreach (var w in text.Split(' '))
            {
                string t = line.Length == 0 ? w : line + " " + w;
                if (TrackedWidth(t, tf, size, track) > maxW && line.Length > 0) { outp.Add(line); line = w; } else line = t;
            }
            if (line.Length > 0) outp.Add(line);
            return outp;
        }

        public static IEnumerable<string> Wrap(string text, SKTypeface tf, float size, float maxW)
        {
            using var p = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
            var words = text.Split(' ');
            string line = "";
            var outp = new List<string>();
            foreach (var w in words)
            {
                string t = line.Length == 0 ? w : line + " " + w;
                if (p.MeasureText(t) > maxW && line.Length > 0) { outp.Add(line); line = w; }
                else line = t;
            }
            if (line.Length > 0) outp.Add(line);
            return outp;
        }
    }
}
