using System.IO;
using System.Text.Json;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>One field of the splash's bottom strip (SESSION / CLASSES / LOCAL TIME / TRACK).</summary>
internal readonly record struct SplashCell(string Label, string Value, bool Gold);

/// <summary>
/// Everything the splash shows, made by <see cref="SplashDirector"/> from real data only. Empty strings and a
/// missing cell mean "not known" - the splash hides them. <see cref="Key"/> decides whether the layers are rebuilt.
/// </summary>
internal sealed class SplashContent
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Label { get; init; }
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public required string Status { get; init; }
    public IReadOnlyList<SplashCell> Cells { get; init; } = Array.Empty<SplashCell>();
    /// <summary>Full path of the track map JSON (assets\maps), or null: no outline, no comet.</summary>
    public string? MapFile { get; init; }

    public string Key => string.Join("\u001f", Width, Height, Label, Title, Subtitle, Status, MapFile ?? "",
        string.Join("\u001e", Cells.Select(c => c.Label + "=" + c.Value + (c.Gold ? "*" : ""))));
}

/// <summary>
/// 2026-10-27 — the TV loading splash, concept A "TRACE" (approved design: navy plate from the grid opener, the
/// real track outline in faint white, a gold comet lapping it as the indeterminate loader, COMING UP / track name /
/// gold hairline / subtitle, the SESSION · CLASSES · LOCAL TIME · TRACK strip, "SIM LOADING •••" and a small
/// GRANDSTAND). Hand-off: the comet hurries round, the line lights up, the camera dives through the comet's head
/// with a warm flash and the plate dissolves to the game. Laid out in 3840x2160 design units (fitted to the TV).
/// <para><b>Cost.</b> Everything static is rasterised once per content / size change on a background thread
/// (plate, track outline + glow, title block, info strip) and uploaded to the card once. A frame is then a handful of
/// image draws with alpha, the moving light sweep, the hairline, the loading dots and the comet - its tail cut from a
/// cached <see cref="SKPathMeasure"/> into one reused <see cref="SKPath"/>. No allocation per frame; SaveLayer only
/// while the whole splash is fading. 30 fps while idle, 60 during fades and the hand-off. No sound.</para>
/// <para>Threading: <see cref="Send"/> and <see cref="SetContent"/> are called by the host loop; everything else runs
/// on the splash host's render thread.</para>
/// </summary>
internal sealed class LoadingSplash : IDisposable
{
    public const float W = 3840f, H = 2160f;
    /// <summary>#438: the big watch (centre, radius) and the title baseline under it (design units).</summary>
    const float WatchCy = 690f, WatchR = 420f, TitleY = 1450f;
    const float TrackCx = W / 2, TrackCy = 900f, TrackMaxW = 2500f, TrackMaxH = 1320f;
    const double FadeMs = 600, HandOffMs = 1400, CrossMs = 450;

    // Grandstand palette (GridIntro / GlobeIntro)
    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A), Txt = new(0xF4, 0xF6, 0xFA), Txt3 = new(0x7E, 0x8A, 0xA0),
                            Navy0 = new(0x0B, 0x1C, 0x36), Navy1 = new(0x03, 0x08, 0x14), NavyHi = new(0x12, 0x2C, 0x52),
                            Warm = new(0xFF, 0xF4, 0xE0), TitleGlow = new(0x9F, 0xC4, 0xFF);

    public enum Command { Hide, Show, HandOff }
    enum Phase { Hidden, In, Idle, HandOff, Out, Clearing }

    volatile int command;
    volatile bool holding;
    volatile float interval = 33.3f;

    /// <summary>Host loop: what the splash should be doing now (it animates its own way there).</summary>
    public void Send(Command c) => command = (int)c;

    /// <summary>Shows / hides the splash's window (wired to the host's SetVisible).</summary>
    public Action<bool>? SetHostVisible { get; set; }

    /// <summary>The splash host's GPU context (set on its render thread when ready); the layers are uploaded to it once.</summary>
    public GRContext? Gpu { get; set; }

    /// <summary>True while the splash covers the screen (so the main overlay stays hidden behind it until the dissolve).</summary>
    public bool Holding => holding;

    /// <summary>True while the splash fully covers the screen (2026-10-27: the session review underneath can stop).</summary>
    public bool Covering => phase == Phase.Idle;

    /// <summary>True while the splash is on screen at all (showing, fading or handing off).</summary>
    public bool Visible => phase != Phase.Hidden;

    /// <summary>Present cap for the splash host: 30 fps idle, 60 while fading / handing off.</summary>
    public double PresentIntervalMs => interval;

    // ---- content: baked off the render thread --------------------------------------------------------------
    volatile string? wantedKey;
    int bakeSeq;
    Layers? baked;                       // finished bake waiting for the render thread

    /// <summary>Host loop: new content. Rebuilds the layers in the background when anything visible changed.</summary>
    public void SetContent(SplashContent c)
    {
        string key = c.Key;
        if (key == wantedKey) return;
        wantedKey = key;
        int seq = Interlocked.Increment(ref bakeSeq);
        Task.Run(() =>
        {
            try
            {
                var l = Layers.Bake(c);
                l.Key = key;
                if (seq != Volatile.Read(ref bakeSeq)) { l.Dispose(); return; }
                Interlocked.Exchange(ref baked, l)?.Dispose();
                Program.Log("splash: picture ready (" + key + ")");
            }
            catch (Exception ex) { Program.Log("splash: could not build the picture: " + ex.Message); }
        });
    }

    // ---- render-thread state ---------------------------------------------------------------------------------
    Layers? cur, prev;
    double crossStart;
    Phase phase = Phase.Hidden;
    double phaseStart;
    float fadeFrom, handAlpha = 1f;
    bool broken;

    readonly SKPaint img = new() { FilterQuality = SKFilterQuality.Medium, IsAntialias = true };
    readonly SKPaint layerPaint = new();
    /// <summary>#336 (2026-10-27): the broadcast watch with the REAL local time (PC clock), top middle. Captioned "REAL TIME":
    /// the info strip's LOCAL TIME cell is the track's time of day.</summary>
    public readonly LocalWatch Watch = new() { Caption = "", PinToGpu = false, CaptionScale = 2.2f };   // #477: no "REAL TIME" caption
    readonly SKPaint sweep = new() { IsAntialias = false };
    readonly SKPaint vignette = new() { IsAntialias = false };
    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint hairGlow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) };
    readonly SKPaint lineGlow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 26, StrokeCap = SKStrokeCap.Round,
                                        StrokeJoin = SKStrokeJoin.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) };
    readonly SKPaint line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint cometGlow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 28, StrokeCap = SKStrokeCap.Round,
                                         StrokeJoin = SKStrokeJoin.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) };
    readonly SKPaint cometCore = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 7, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint headGlow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 35) };
    readonly SKPaint text = new() { IsAntialias = true };
    readonly SKPath seg = new();

    public LoadingSplash()
    {
        // the slow light sweep, built around x = 0 and moved with the canvas
        sweep.Shader = SKShader.CreateLinearGradient(new SKPoint(-700, 0), new SKPoint(700, 0),
            new[] { Txt.WithAlpha(0), Txt.WithAlpha(9), Txt.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        vignette.Shader = SKShader.CreateRadialGradient(new SKPoint(W / 2, H / 2), W * 0.62f,
            new[] { SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(128) },
            new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
    }

    /// <summary>Render thread, every loop: picks up new layers and advances the show / hand-off / hide animation.</summary>
    public bool FrameNeeded(double nowMs)
    {
        var nb = Interlocked.Exchange(ref baked, null);
        if (nb != null)
        {
            try { nb.Upload(Gpu); } catch (Exception ex) { Program.Log("splash: upload failed: " + ex.Message); }
            Program.Log($"splash: new picture on the render thread (gpu={(Gpu != null)}, phase={phase}, cmd={(Command)command})");
            if (cur == null || phase == Phase.Hidden) { prev?.Dispose(); prev = null; cur?.Dispose(); cur = nb; }
            else { prev?.Dispose(); prev = cur; cur = nb; crossStart = nowMs; }
        }
        if (prev != null && nowMs - crossStart >= CrossMs) { prev.Dispose(); prev = null; }

        var cmd = broken ? Command.Hide : (Command)command;
        switch (phase)
        {
            case Phase.Hidden:
                // #492 (2026-10-30, Angelo: "it just shows the old screen"): only come up with the picture made for what is
                // loading NOW - never the one left from the last sim run (that is a different track) while the new one bakes
                if (cmd == Command.Show && cur != null && cur.Key == wantedKey)
                {
                    phase = Phase.In; phaseStart = nowMs; fadeFrom = 0f;
                    SetHostVisible?.Invoke(true);
                    Program.Log("splash: showing");
                }
                break;
            case Phase.In:
            {
                float a = FadeIn(nowMs);
                if (cmd == Command.Hide) { phase = Phase.Out; phaseStart = nowMs; fadeFrom = a; }
                else if (cmd == Command.HandOff) { phase = Phase.HandOff; phaseStart = nowMs; handAlpha = a; }
                else if (a >= 1f) phase = Phase.Idle;
                break;
            }
            case Phase.Idle:
                if (cmd == Command.Hide) { phase = Phase.Out; phaseStart = nowMs; fadeFrom = 1f; }
                else if (cmd == Command.HandOff) { phase = Phase.HandOff; phaseStart = nowMs; handAlpha = 1f; }
                break;
            case Phase.HandOff:
                if (nowMs - phaseStart >= HandOffMs) phase = Phase.Clearing;
                break;
            case Phase.Out:
            {
                float a = FadeOut(nowMs);
                if (cmd == Command.Show) { phase = Phase.In; phaseStart = nowMs; fadeFrom = a; }
                else if (a <= 0f) phase = Phase.Clearing;
                break;
            }
        }

        holding = phase is Phase.In or Phase.Idle
                  || (phase == Phase.HandOff && nowMs - phaseStart < HandOffMs * 0.5);
        interval = phase == Phase.Idle ? 33.3f : 16.6f;
        return phase != Phase.Hidden;
    }

    float FadeIn(double now) => fadeFrom + (1f - fadeFrom) * EaseIo((float)((now - phaseStart) / FadeMs));
    float FadeOut(double now) => fadeFrom * (1f - EaseIo((float)((now - phaseStart) / FadeMs)));

    /// <summary>Render thread: draws the splash (the host has already cleared the canvas to transparent).</summary>
    public void Render(SKCanvas c, int width, int height, double nowMs)
    {
        if (phase == Phase.Clearing)
        {
            // one see-through frame is presented first, so the window never comes back showing an old picture
            phase = Phase.Hidden; holding = false;
            SetHostVisible?.Invoke(false);
            return;
        }
        var L = cur;
        if (phase == Phase.Hidden || L == null || broken) return;
        try { Draw(c, L, width, height, nowMs); }
        catch (Exception ex)
        {
            broken = true;
            Program.Log("splash: switched off after a drawing error: " + ex.Message);
        }
    }

    void Draw(SKCanvas c, Layers L, int width, int height, double nowMs)
    {
        double t = nowMs / 1000.0;
        float gA = phase switch
        {
            Phase.In => FadeIn(nowMs),
            Phase.Out => FadeOut(nowMs),
            Phase.HandOff => handAlpha,
            _ => 1f,
        };
        float h = phase == Phase.HandOff ? Clamp01((float)((nowMs - phaseStart) / HandOffMs)) : 0f;
        float plateA = (1f - EaseIo(Seg(h, 0.45f, 0.95f))) * gA;
        if (plateA <= 0.002f) return;
        float x = prev == null ? 1f : EaseIo((float)((nowMs - crossStart) / CrossMs));   // crossfade to new content

        int outer = c.Save();
        if (width != L.W || height != L.H) c.Scale(width / (float)L.W, height / (float)L.H);   // window size moved: stretch until re-baked
        bool layer = plateA < 0.999f;
        if (layer) { layerPaint.Color = new SKColor(255, 255, 255, (byte)(255 * plateA)); c.SaveLayer(layerPaint); }

        img.Color = SKColors.White;
        if (L.Plate != null) c.DrawImage(L.Plate, 0, 0, img);
        c.Translate(L.OffX, L.OffY);
        c.Scale(L.Scale);

        // slow light sweep
        float sx = -1200f + (float)((t * 0.12) % 1.0) * (W + 2400f);
        c.Save(); c.Translate(sx, 0); c.DrawRect(-700, 0, 1400, H, sweep); c.Restore();

        // the track: outline (a picture while idle, vectors while the camera dives) and the comet lapping it
        if (prev?.Track != null && x < 1f) DrawLayer(c, prev.Track, prev.TrackRect, 1f - x);
        if (L.Path != null && L.Measure != null && L.Length > 0)
        {
            float head = (float)((t * 0.22) % 1.0) + 0.9f * EaseIn(Seg(h, 0f, 0.3f));   // a lap every ~4.5 s; hurries on hand-off
            c.Save();
            if (h > 0f)
            {
                float zoom = 1f + 7f * EaseIn(Seg(h, 0.18f, 0.85f));
                L.Measure.GetPosition(Frac(head) * L.Length, out var hp);
                float k = EaseIo(Seg(h, 0.1f, 0.6f));
                float zx = TrackCx + (hp.X - TrackCx) * k, zy = TrackCy + (hp.Y - TrackCy) * k;
                c.Translate(zx, zy); c.Scale(zoom); c.Translate(-zx, -zy);
                lineGlow.Color = Txt.WithAlpha(26);
                c.DrawPath(L.Path, lineGlow);
                line.Color = Txt.WithAlpha(A(0.22f + 0.6f * EaseOut(Seg(h, 0f, 0.25f))));
                c.DrawPath(L.Path, line);
            }
            else if (L.Track != null) DrawLayer(c, L.Track, L.TrackRect, x);
            DrawComet(c, L, head, 0.22f + 0.5f * Seg(h, 0f, 0.3f));
            c.Restore();
        }

        // COMING UP / track name / hairline / subtitle
        float ta = 1f - EaseIo(Seg(h, 0.05f, 0.3f));
        float lift = -60f * EaseIn(Seg(h, 0.05f, 0.3f));
        c.Save(); c.Translate(0, lift);
        if (prev?.Title != null && x < 1f) DrawLayer(c, prev.Title, prev.TitleRect, ta * (1f - x));
        if (L.Title != null) DrawLayer(c, L.Title, L.TitleRect, ta * x);
        float hw = 420f * (0.92f + 0.08f * (float)Math.Sin((t * 1.3) % (2 * Math.PI)));
        flat.Color = Gold.WithAlpha(A(ta));
        c.DrawRect(W / 2 - hw, TitleY + 30, 2 * hw, 8, flat);
        hairGlow.Color = Gold.WithAlpha(A(0.4f * ta));
        c.DrawRect(W / 2 - hw, TitleY + 26, 2 * hw, 16, hairGlow);
        c.Restore();

        // SESSION · CLASSES · LOCAL TIME · TRACK strip and the GRANDSTAND corner
        float ia = 1f - EaseIo(Seg(h, 0f, 0.25f));
        if (prev?.Info != null && x < 1f) DrawLayer(c, prev.Info, prev.InfoRect, ia * (1f - x));
        if (L.Info != null) DrawLayer(c, L.Info, L.InfoRect, ia * x);

        // status: SIM LOADING with pulsing dots, SESSION LIVE as it hands off
        if (phase != Phase.HandOff || h < 0.08f)
        {
            if (L.Status != null)
            {
                text.Color = Txt3.WithAlpha(A(0.85f));
                c.DrawText(L.Status, 140, 2040, text);
                float bx = 140 + L.StatusWidth + 34;
                for (int i = 0; i < 3; i++)
                {
                    double ph = ((t * 1.4 - i * 0.18) % 1.0 + 1.0) % 1.0;
                    float s = (float)Math.Max(0.0, Math.Sin(ph * Math.PI));
                    flat.Color = Txt3.WithAlpha(A(0.25f + 0.75f * s * s));
                    c.DrawCircle(bx + i * 34, 2040 - 13, 8, flat);
                }
            }
        }
        else if (L.Live != null)
        {
            text.Color = Gold.WithAlpha(A(1f - Seg(h, 0.3f, 0.5f)));
            c.DrawText(L.Live, 140, 2040, text);
        }

        // #336: the real local time on the broadcast watch, top middle; it lifts away with the title on hand-off
        // #438 (2026-10-27, Angelo: "I want this watch face to be the feature - huge, in the middle of the screen, the
        // highlight of this splash"): his own dial (2720 px, sharp at this size), centre stage; the title sits under it
        Watch.Draw(c, W / 2, WatchCy + lift, WatchR, ta);

        c.DrawRect(0, 0, W, H, vignette);

        // warm flash as the camera dives through the line
        float fl = (float)Math.Sin(Math.PI * Seg(h, 0.4f, 0.62f));
        if (fl > 0f) { flat.Color = Warm.WithAlpha(A(0.35f * fl)); c.DrawRect(0, 0, W, H, flat); }

        c.RestoreToCount(outer);
    }

    void DrawLayer(SKCanvas c, SKImage image, SKRect dst, float a)
    {
        if (a <= 0.002f) return;
        img.Color = new SKColor(255, 255, 255, A(a));
        c.DrawImage(image, dst, img);
    }

    /// <summary>The gold comet: a tapering tail cut from the cached measure into the one reused path, a soft glow
    /// in six chunks, and a warm head.</summary>
    void DrawComet(SKCanvas c, Layers L, float head, float tail)
    {
        const int Glow = 6, Core = 24;
        for (int g = 0; g < Glow; g++)
        {
            float aa = MathF.Pow(1f - (g + 0.5f) / Glow, 1.6f);
            if (!Segment(L, head - tail * (g + 1) / Glow, head - tail * g / Glow)) continue;
            cometGlow.Color = Gold.WithAlpha(A(0.35f * aa));
            c.DrawPath(seg, cometGlow);
        }
        for (int i = 0; i < Core; i++)
        {
            float aa = MathF.Pow(1f - i / (float)Core, 1.6f);
            if (!Segment(L, head - tail * (i + 1) / Core, head - tail * i / Core)) continue;
            cometCore.StrokeWidth = 7f * (1f - 0.5f * i / Core);
            cometCore.Color = Gold.WithAlpha(A(aa));
            c.DrawPath(seg, cometCore);
        }
        if (!L.Measure!.GetPosition(Frac(head) * L.Length, out var p)) return;
        headGlow.Color = Gold.WithAlpha(A(0.35f));
        c.DrawCircle(p.X, p.Y, 49, headGlow);
        flat.Color = Warm;
        c.DrawCircle(p.X, p.Y, 12.6f, flat);
    }

    /// <summary>Lap fractions f0..f1 (may wrap past the line) into <see cref="seg"/>.</summary>
    bool Segment(Layers L, float f0, float f1)
    {
        seg.Reset();
        float len = L.Length;
        f0 = Frac(f0); f1 = Frac(f1);
        if (f1 <= 0f) f1 = 1f;
        var m = L.Measure!;
        if (f0 < f1) return m.GetSegment(f0 * len, f1 * len, seg, true);
        bool a = m.GetSegment(f0 * len, len, seg, true);
        bool b = m.GetSegment(0, f1 * len, seg, true);
        return a || b;
    }

    static float Frac(float v) => v - MathF.Floor(v);
    static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    static float Seg(float t, float a, float b) => Clamp01((t - a) / (b - a));
    static float EaseIo(float v) { v = Clamp01(v); return v * v * (3f - 2f * v); }
    static float EaseOut(float v) { v = 1f - Clamp01(v); return 1f - v * v * v; }
    static float EaseIn(float v) { v = Clamp01(v); return v * v * v; }
    static byte A(float a) => (byte)(255f * Clamp01(a) + 0.5f);

    public void Dispose()
    {
        Interlocked.Exchange(ref baked, null)?.Dispose();
        cur?.Dispose(); prev?.Dispose(); cur = prev = null;
        Watch.Dispose();
        foreach (var p in new[] { img, layerPaint, sweep, vignette, flat, hairGlow, lineGlow, line, cometGlow, cometCore, headGlow, text }) p.Dispose();
        seg.Dispose();
    }

    // =============================================================================================================
    /// <summary>The static pictures for one content + size, made on a background thread.</summary>
    sealed class Layers : IDisposable
    {
        public int W, H;
        public string Key = "";
        public float Scale, OffX, OffY;
        public SKImage? Plate, Track, Title, Info;
        public SKRect TrackRect, TitleRect, InfoRect;     // where each picture lands, in design units
        public SKPath? Path;                              // outline in design units
        public SKPathMeasure? Measure;
        public float Length;
        public SKTextBlob? Status, Live;
        public float StatusWidth;

        public static Layers Bake(SplashContent c)
        {
            var L = new Layers { W = Math.Max(16, c.Width), H = Math.Max(16, c.Height) };
            L.Scale = Math.Min(L.W / LoadingSplash.W, L.H / LoadingSplash.H);
            L.OffX = (L.W - LoadingSplash.W * L.Scale) / 2f;
            L.OffY = (L.H - LoadingSplash.H * L.Scale) / 2f;
            var black = Fonts.BarlowCondensed.Black; var bold = Fonts.BarlowCondensed.Bold;
            var iSemi = Fonts.Inter.SemiBold; var iMed = Fonts.Inter.Medium; var iBold = Fonts.Inter.Bold;

            // the plate: navy radial (the opener's), faint dot field. Whole window, in pixels.
            using (var surf = SKSurface.Create(new SKImageInfo(L.W, L.H, SKColorType.Rgba8888, SKAlphaType.Premul)))
            {
                var cv = surf.Canvas;
                cv.Clear(Navy1);
                cv.Translate(L.OffX, L.OffY); cv.Scale(L.Scale);
                using (var bg = new SKPaint { IsDither = true, Shader = SKShader.CreateRadialGradient(new SKPoint(LoadingSplash.W / 2, LoadingSplash.H * 0.46f),
                           LoadingSplash.W * 0.75f, new[] { NavyHi, Navy0, Navy1 }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp) })
                    cv.DrawRect(0, 0, LoadingSplash.W, LoadingSplash.H, bg);
                using (var dot = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(13) })
                    for (int gy = 120; gy < LoadingSplash.H; gy += 120)
                        for (int gx = 120; gx < LoadingSplash.W; gx += 120)
                            cv.DrawCircle(gx, gy, 2.2f, dot);
                L.Plate = surf.Snapshot();
            }

            // the real track outline (no map -> no outline and no comet: name + hairline only)
            if (c.MapFile != null && LoadOutline(c.MapFile) is { } path)
            {
                var meas = new SKPathMeasure(path, true);
                if (meas.Length > 100f)
                {
                    L.Path = path; L.Measure = meas; L.Length = meas.Length;
                    var r = path.Bounds; r.Inflate(80, 80);
                    L.Track = Picture(L, r, out L.TrackRect, cv =>
                    {
                        using var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 26, StrokeCap = SKStrokeCap.Round,
                                                       StrokeJoin = SKStrokeJoin.Round, Color = Txt.WithAlpha(26),
                                                       MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) };
                        cv.DrawPath(path, glow);
                        using var ln = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round,
                                                     StrokeJoin = SKStrokeJoin.Round, Color = Txt.WithAlpha(56) };
                        cv.DrawPath(path, ln);
                    });
                }
                else { meas.Dispose(); path.Dispose(); }
            }

            // title block (the hairline is drawn live: it breathes)
            L.Title = Picture(L, new SKRect(0, 1240, LoadingSplash.W, 1600), out L.TitleRect, cv =>
            {
                if (c.Label.Length > 0) DrawTracked(cv, c.Label, LoadingSplash.W / 2, TitleY - 140, iSemi, 44, 20, Gold, 0);
                if (c.Title.Length > 0)
                {
                    string name = c.Title.ToUpperInvariant();
                    float size = Fit(name, black, 150, 2500, 6);
                    DrawTracked(cv, name, LoadingSplash.W / 2, TitleY, black, size, 6, TitleGlow.WithAlpha(89), 0, blur: 40);
                    DrawTracked(cv, name, LoadingSplash.W / 2, TitleY, black, size, 6, Txt, 0);
                }
                if (c.Subtitle.Length > 0)
                {
                    string sub = c.Subtitle.ToUpperInvariant();
                    DrawTracked(cv, sub, LoadingSplash.W / 2, TitleY + 100, iMed, Fit(sub, iMed, 38, 2800, 14), 14, Txt3, 0);
                }
            });

            // info strip + brand corner
            L.Info = Picture(L, new SKRect(0, 1590, LoadingSplash.W, 2090), out L.InfoRect, cv =>
            {
                int n = c.Cells.Count;
                if (n > 0)
                {
                    const float y0 = 1640, total = 3000, cw = total / 4;
                    float left = (LoadingSplash.W - total) / 2, x0 = (LoadingSplash.W - cw * n) / 2;
                    using var rule = new SKPaint { IsAntialias = true, Color = Txt.WithAlpha(26) };
                    cv.DrawRect(left, y0 - 10, total, 2, rule);
                    rule.Color = Txt.WithAlpha(31);
                    for (int i = 0; i < n; i++)
                    {
                        var cell = c.Cells[i];
                        float cx = x0 + cw * i + cw / 2;
                        if (i > 0) cv.DrawRect(x0 + cw * i - 1, y0 + 30, 2, 170, rule);
                        DrawTracked(cv, cell.Label, cx, y0 + 80, iSemi, 34, 12, Txt3, 0);
                        string v = cell.Value.ToUpperInvariant();
                        DrawTracked(cv, v, cx, y0 + 180, bold, Fit(v, bold, 84, cw - 40, 2), 2, cell.Gold ? Gold : Txt, 0);
                    }
                }
                DrawTracked(cv, "GRANDSTAND", LoadingSplash.W - 140, 2040, iBold, 36, 18, Txt.WithAlpha(115), 1);
            });

            if (c.Status.Length > 0) (L.Status, L.StatusWidth) = Blob(c.Status, iSemi, 40, 10);
            (L.Live, _) = Blob("SESSION LIVE", iSemi, 40, 10);
            return L;
        }

        /// <summary>Raster picture of a design-space rect at the output scale; <paramref name="dst"/> is where it
        /// lands (design units, rounded out to whole pixels so it draws 1:1).</summary>
        static SKImage Picture(Layers L, SKRect design, out SKRect dst, Action<SKCanvas> draw)
        {
            int pw = Math.Max(1, (int)Math.Ceiling(design.Width * L.Scale)), ph = Math.Max(1, (int)Math.Ceiling(design.Height * L.Scale));
            dst = new SKRect(design.Left, design.Top, design.Left + pw / L.Scale, design.Top + ph / L.Scale);
            using var surf = SKSurface.Create(new SKImageInfo(pw, ph, SKColorType.Rgba8888, SKAlphaType.Premul));
            var cv = surf.Canvas;
            cv.Clear(SKColors.Transparent);
            cv.Scale(L.Scale);
            cv.Translate(-design.Left, -design.Top);
            draw(cv);
            return surf.Snapshot();
        }

        /// <summary>The recorded line (assets\maps JSON: {"w","h","points":[[x,y],...]}) fitted into the outline box.</summary>
        static SKPath? LoadOutline(string file)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var pts = new List<SKPoint>();
                foreach (var p in doc.RootElement.GetProperty("points").EnumerateArray())
                    pts.Add(new SKPoint(p[0].GetSingle(), p[1].GetSingle()));
                if (pts.Count < 3) return null;
                float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in pts) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
                float bw = Math.Max(1e-3f, maxX - minX), bh = Math.Max(1e-3f, maxY - minY);
                float s = Math.Min(TrackMaxW / bw, TrackMaxH / bh);
                float cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;
                var path = new SKPath();
                path.MoveTo(TrackCx + (pts[0].X - cx) * s, TrackCy + (pts[0].Y - cy) * s);
                for (int i = 1; i < pts.Count; i++) path.LineTo(TrackCx + (pts[i].X - cx) * s, TrackCy + (pts[i].Y - cy) * s);
                path.Close();
                return path;
            }
            catch { return null; }
        }

        /// <summary>Letter-spaced text as one blob (each character advanced by its own width + spacing, like the preview renderer).</summary>
        static (SKTextBlob? Blob, float Width) Blob(string s, SKTypeface tf, float size, float track)
        {
            if (s.Length == 0) return (null, 0);
            using var font = new SKFont(tf, size) { Subpixel = true };
            using var measure = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
            var glyphs = new ushort[s.Length];
            var at = new SKPoint[s.Length];
            float x = 0;
            for (int i = 0; i < s.Length; i++)
            {
                glyphs[i] = font.GetGlyph(s[i]);
                at[i] = new SKPoint(x, 0);
                x += measure.MeasureText(s[i].ToString()) + track;
            }
            using var b = new SKTextBlobBuilder();
            b.AddPositionedRun(glyphs, font, at);
            return (b.Build(), x - track);
        }

        static float Width(string s, SKTypeface tf, float size, float track)
        {
            using var measure = new SKPaint { Typeface = tf, TextSize = size, SubpixelText = true };
            float x = 0;
            for (int i = 0; i < s.Length; i++) x += measure.MeasureText(s[i].ToString());
            return x + track * Math.Max(0, s.Length - 1);
        }

        static float Fit(string s, SKTypeface tf, float size, float maxW, float track)
        {
            while (size > 10 && Width(s, tf, size, track) > maxW) size -= 2;
            return size;
        }

        /// <summary>align: 0 centre, 1 right, -1 left.</summary>
        static void DrawTracked(SKCanvas cv, string s, float x, float y, SKTypeface tf, float size, float track, SKColor col, int align, float blur = 0)
        {
            var (blob, w) = Blob(s, tf, size, track);
            if (blob == null) return;
            using (blob)
            using (var p = new SKPaint { IsAntialias = true, Color = col })
            {
                if (blur > 0) p.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
                float x0 = align == 0 ? x - w / 2 : align > 0 ? x - w : x;
                cv.DrawText(blob, x0, y, p);
            }
        }

        /// <summary>Render thread: move the pictures onto the graphics card once (a raster image would otherwise be
        /// re-sent whenever Skia's cache drops it).</summary>
        public void Upload(GRContext? gr)
        {
            if (gr == null) return;
            Plate = ToGpu(Plate, gr); Track = ToGpu(Track, gr); Title = ToGpu(Title, gr); Info = ToGpu(Info, gr);
        }

        static SKImage? ToGpu(SKImage? raster, GRContext gr)
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

        public void Dispose()
        {
            Plate?.Dispose(); Track?.Dispose(); Title?.Dispose(); Info?.Dispose();
            Measure?.Dispose(); Path?.Dispose(); Status?.Dispose(); Live?.Dispose();
            Plate = Track = Title = Info = null; Measure = null; Path = null; Status = Live = null;
        }
    }
}
