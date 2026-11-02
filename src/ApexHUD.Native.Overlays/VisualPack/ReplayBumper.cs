using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>Who presents the replay. <paramref name="Logo"/> (the partner's own picture, when the owner has one) wins over
/// the typed wordmark (<paramref name="Title"/> big, <paramref name="Sub"/> small and tracked under it).</summary>
public sealed record BumperModel(string Title, string Sub, SKImage? Logo, string Moment, string Verb = "PRESENTED BY", double TimeOfDay = -1);

/// <summary>
/// V6 (2026-10-27): a sponsor-style bumper as a replay starts ("REPLAY presented by ..."), Angelo's promo idea.
/// <para>Base (2.6 s): a slanted glass band across the lower third - REPLAY in Barlow Black with a gold slash, the
/// moment ("LAP 12 · TERTRE ROUGE"), PRESENTED BY and the partner's mark - a light sweep runs along it; it slides in from
/// the left and leaves to the right. Elevated ("doors", 2.8 s): two navy doors with gold edges close from both sides
/// over the picture, the partner's mark sits in the middle with a sheen passing over it, then the doors open on the
/// replay with a warm flash on their edges.</para>
/// <para>Round 2 (2026-10-27, Angelo: replace the "weird circle" with my watch face): the centre of the doors is now the
/// broadcast WATCH - the session bar's own watch wearing his dial (<see cref="ClockDial"/>, drawn from his file, never
/// redrawn) - and its hands REWIND: they spin back two hours and settle on the sim's time of day at the replayed
/// moment. REPLAY above it, PRESENTED BY + the partner's mark and the moment below. While the bumper plays the session
/// bar takes its battle-mode look with the standings ticker on top of the doors (#337, see PresentationState).</para>
/// </summary>
public sealed class ReplayBumper : VpGraphic
{
    public double DurationMs => Elevated ? 2800 : 2600;

    BumperModel? model;
    double shownAt = -1;
    VpLayer? band, plateL, plateR, mark;
    bool bakedElevated;
    float bandW, bandH = 112f, markW, markH;

    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint sweep = new() { IsAntialias = false };
    readonly SKPaint sheen = new() { IsAntialias = false, BlendMode = SKBlendMode.SrcATop };
    readonly SKPaint tick = new() { IsAntialias = true, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint edgeGlow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 14f, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f) };
    VpLayer? medal, seamCover;
    readonly SKPath clip = new(), door = new();

    public ReplayBumper()
    {
        Watch.Clock = () => watchTime;
        sweep.Shader = SKShader.CreateLinearGradient(new SKPoint(-220, 0), new SKPoint(220, 0),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(26), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        sheen.Shader = SKShader.CreateLinearGradient(new SKPoint(-90, 0), new SKPoint(90, 0),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(150), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
    }

    public void Show(BumperModel m, double nowMs)
    {
        model = m with { Moment = m.Moment.ToUpperInvariant(), Verb = m.Verb.ToUpperInvariant() };
        shownAt = nowMs;
        Bake();
    }

    public override bool Active(double nowMs) => model != null && shownAt >= 0 && nowMs - shownAt < DurationMs;

    /// <summary>True while the bumper covers the picture completely (elevated doors closed) - the camera can cut then.</summary>
    public bool Covering(double nowMs) => Elevated && Active(nowMs) && nowMs - shownAt is > 520 and < 2050;

    void Bake()
    {
        band?.Dispose(); plateL?.Dispose(); plateR?.Dispose(); mark?.Dispose(); medal?.Dispose(); seamCover?.Dispose(); band = plateL = plateR = mark = medal = seamCover = null;
        var m = model!;
        bakedElevated = Elevated;
        // the partner's mark (logo picture or typed wordmark), centred on (0,0)
        float titleSize = Elevated ? 118f : 50f * BandScale;
        if (m.Logo != null)
        {
            markH = Elevated ? 150f : 64f;
            markW = m.Logo.Width * markH / m.Logo.Height;
        }
        else
        {
            markW = Math.Max(VpText.Width(m.Title, Fonts.BarlowCondensed.Black, titleSize, titleSize * 0.02f),
                             VpText.Width(m.Sub, Fonts.Inter.Bold, titleSize * 0.17f, titleSize * 0.06f));
            markH = titleSize * 1.32f;
        }
        mark = VpLayer.Record(new SKRect(-markW / 2 - 20, -markH / 2 - 20, markW / 2 + 20, markH / 2 + 20), c =>
        {
            if (m.Logo != null)
            {
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                c.DrawImage(m.Logo, new SKRect(-markW / 2, -markH / 2, markW / 2, markH / 2), lp);
                return;
            }
            float ty = -markH / 2 + titleSize * 0.86f;
            VpText.Draw(c, m.Title, 0, ty, Fonts.BarlowCondensed.Black, titleSize, Vp.Ivory2, titleSize * 0.02f, 0);
            using (var u = new SKPaint { Color = Vp.Gold }) c.DrawRect(new SKRect(-markW / 2, ty + titleSize * 0.1f, markW / 2, ty + titleSize * 0.1f + Math.Max(2f, titleSize * 0.04f)), u);
            VpText.Draw(c, m.Sub, 0, ty + titleSize * 0.38f, Fonts.Inter.Bold, titleSize * 0.17f, Vp.GoldPale, titleSize * 0.06f, 0);
        });

        if (!Elevated)
        {
            float replayW = VpText.Width("REPLAY", Fonts.BarlowCondensed.Black, 60f, 1f);
            float verbW = VpText.Width(m.Verb, Fonts.Inter.Bold, 11f, 2.6f);
            bandW = 60 + replayW + 40 + 2 + 40 + Math.Max(verbW, 10) + 22 + markW / BandScale + 70;
            // laid out at 1x, recorded at BandScale (bigger on the TV, still crisp)
            band = VpLayer.Record(new SKRect(-40, -30, bandW * BandScale + 40, bandH * BandScale + 40), c =>
            {
                c.Scale(BandScale);
                using var shape = Vp.Slant(new SKRect(0, 0, bandW, bandH), 34f);
                Vp.GlassPanel(c, shape, Vp.Gold, 0.95f, true, 2f);
                using (var bl = new SKPaint { Color = Vp.Gold }) c.DrawRect(new SKRect(0, bandH - 2, bandW - 34, bandH), bl);
                float x = 70;
                // gold slash, REPLAY, the moment under it
                using (var sl = new SKPaint { IsAntialias = true, Color = Vp.GoldHi })
                using (var sp = Vp.Slant(new SKRect(x - 26, 26, x - 14, 86), 9f)) c.DrawPath(sp, sl);
                VpText.Draw(c, "REPLAY", x, 74, Fonts.BarlowCondensed.Black, 60f, Vp.Ivory2, 1f);
                if (m.Moment.Length > 0) VpText.Draw(c, m.Moment.ToUpperInvariant(), x + 2, 96, Fonts.Inter.SemiBold, 10.5f, Vp.Txt3, 2.2f);
                x += replayW + 40;
                using (var dv = new SKPaint { Color = Vp.Ivory.WithAlpha(40) }) c.DrawRect(new SKRect(x, 24, x + 1.5f, bandH - 24), dv);
                x += 40;
                VpText.Draw(c, m.Verb, x, bandH / 2 + 4, Fonts.Inter.Bold, 11f, Vp.Txt3, 2.6f);
                x += verbW + 22;
                markCx = (x + markW / BandScale / 2) * BandScale;
            });
        }
        else
        {
            // two navy doors meeting in the middle with a slanted seam (screen-sized, design units)
            plateL = VpLayer.Record(new SKRect(-20, 0, 1060, 1080), c => Door(c, true));
            plateR = VpLayer.Record(new SKRect(860, 0, 1940, 1080), c => Door(c, false));
            // the seam's gold edge would run through the watch: the doors' own navy (same gradient, same dot field) is laid
            // over the middle, fading out above and below, so the seam opens around the watch
            seamCover = VpLayer.Record(new SKRect(960 - 420, 60, 960 + 420, 1000), c =>
            {
                var r = new SKRect(960 - 420, 60, 960 + 420, 1000);
                c.SaveLayer(r, null);
                Vp.NavyPlate(c, new SKRect(0, 0, 1920, 1080), 48f);
                using (var ln = new SKPaint { IsAntialias = true, StrokeWidth = 1f, Color = Vp.Ivory.WithAlpha(7) })
                    for (float x = -1100; x < 1920; x += 14) c.DrawLine(x, 1080, x + 1100 * 0.6f, 0, ln);
                using var m = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateRadialGradient(new SKPoint(960, WatchY + 120), 470,
                    new[] { SKColors.Black, SKColors.Black, SKColors.Black.WithAlpha(0) }, new[] { 0f, 0.62f, 1f }, SKShaderTileMode.Clamp) };
                c.Save(); c.Scale(0.62f, 1f, 960, WatchY + 120); c.DrawRect(new SKRect(0, 0, 1920, 1080), m); c.Restore();
                c.Restore();
            });

        }
    }

    float markCx;
    const float MedalX = 960, MedalY = 520;
    const float BandScale = 1.3f;
    const float Seam = 80f;   // the seam leans this far over the screen height

    static void DoorPath(SKPath p, bool left)
    {
        p.Reset();
        if (left) { p.MoveTo(-20, 0); p.LineTo(960 + Seam / 2, 0); p.LineTo(960 - Seam / 2, 1080); p.LineTo(-20, 1080); }
        else { p.MoveTo(960 + Seam / 2, 0); p.LineTo(1940, 0); p.LineTo(1940, 1080); p.LineTo(960 - Seam / 2, 1080); }
        p.Close();
    }

    static void Door(SKCanvas c, bool left)
    {
        using var p = new SKPath();
        DoorPath(p, left);
        c.Save();
        c.ClipPath(p, SKClipOperation.Intersect, true);
        Vp.NavyPlate(c, new SKRect(0, 0, 1920, 1080), 48f);
        // fine diagonal brushed lines
        using (var ln = new SKPaint { IsAntialias = true, StrokeWidth = 1f, Color = Vp.Ivory.WithAlpha(7) })
            for (float x = -1100; x < 1920; x += 14) c.DrawLine(x, 1080, x + 1100 * 0.6f, 0, ln);
        c.Restore();
        // gold edge along the seam
        using var gp = new SKPaint { IsAntialias = true, Color = Vp.GoldHi, StrokeWidth = 4f, Style = SKPaintStyle.Stroke };
        if (left) c.DrawLine(960 + Seam / 2 - 2, 0, 960 - Seam / 2 - 2, 1080, gp);
        else c.DrawLine(960 + Seam / 2 + 2, 0, 960 - Seam / 2 + 2, 1080, gp);
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        if (m == null || !Active(nowMs)) return;
        if (bakedElevated != Elevated) Bake();
        double t = nowMs - shownAt;
        if (!Elevated) DrawBand(c, w, h, t);
        else DrawDoors(c, w, h, t);
    }

    void DrawBand(SKCanvas c, int w, int h, double t)
    {
        if (band == null || mark == null) return;
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, 520));
        float outK = Vp.EaseIn(Vp.Seg(t, DurationMs - 450, DurationMs));
        float bw = bandW * BandScale, bh = bandH * BandScale;
        float x0 = (w - bw) / 2f, y0 = h * 0.68f;
        float dx = -(1f - inK) * (x0 + bw + 60) + outK * (w - x0 + 60);
        int save = c.Save();
        c.Translate(x0 + dx, y0);
        band.Draw(c, 0, 0, 1f);
        // the partner's mark rises in a beat later
        float mk = Vp.EaseOutExpo(Vp.Seg(t, 260, 760));
        mark.Draw(c, markCx, bh / 2 + (1f - mk) * 14f, mk);
        // light sweep along the band
        c.Save();
        clip.Reset(); clip.AddRect(new SKRect(0, 0, bw, bh));
        c.ClipPath(clip);
        float sx = -260 + Vp.EaseIo(Vp.Seg(t, 350, 1900)) * (bw + 520);
        c.Translate(sx, 0); c.Skew(-0.3f, 0);
        c.DrawRect(-220, 0, 440, bh, sweep);
        c.Restore();
        c.RestoreToCount(save);
    }

    void DrawDoors(SKCanvas c, int w, int h, double t)
    {
        if (plateL == null || plateR == null || mark == null) return;
        float close = Vp.EaseOutExpo(Vp.Seg(t, 0, 520));
        float open = Vp.EaseIn(Vp.Seg(t, 2050, 2650));
        float off = (1f - close) * (w / 2f + Seam + 40) + open * (w / 2f + Seam + 40);
        int save = c.Save();
        c.Scale(w / 1920f, h / 1080f);
        plateL.Draw(c, -off, 0, 1f);
        plateR.Draw(c, off, 0, 1f);
        // edge glow while the doors move
        float moving = Math.Max(1f - close, open);
        if (moving > 0.01f && moving < 0.99f)
        {
            edgeGlow.Color = Vp.Warm.WithA(0.55f * (1f - Math.Abs(moving - 0.5f) * 2f) + 0.2f);
            c.DrawLine(960 + Seam / 2 - off, 0, 960 - Seam / 2 - off, 1080, edgeGlow);
            c.DrawLine(960 + Seam / 2 + off, 0, 960 - Seam / 2 + off, 1080, edgeGlow);
        }
        // centre content while closed: REPLAY, the rewinding watch, PRESENTED BY + the mark, the moment
        float ca = Vp.Seg(t, 380, 700) * (1f - Vp.Seg(t, 1900, 2150));
        if (ca > 0.002f)
        {
            float cx = 960;
            seamCover?.Draw(c, 0, 0, Vp.Clamp01(ca * 1.6f) * Vp.Seg(close, 0.9f, 1f) * (1f - Vp.Seg(open, 0f, 0.04f)));
            word ??= new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 22f, TextAlign = SKTextAlign.Center };
            word.TextSize = 24f;
            word.Color = Vp.GoldHi.WithA(ca);
            TextTracked.Draw(c, word, "REPLAY", cx, WatchY - WatchR - 44, 14f);
            // the watch lands with a little overshoot and its hands wind BACK to the replayed moment
            float wk = Vp.EaseOutBack(Vp.Seg(t, 360, 820));
            double tod = model!.TimeOfDay >= 0 ? model.TimeOfDay : 14 * 3600 + 50 * 60;
            double rewind = 7200.0 * (1.0 - Vp.EaseOut(Vp.Seg(t, 420, 1500)));
            watchTime = DayStart.AddSeconds(((tod + rewind) % 86400 + 86400) % 86400);
            c.Save(); c.Translate(cx, WatchY); c.Scale(0.7f + 0.3f * wk); c.Translate(-cx, -WatchY);
            Watch.Draw(c, cx, WatchY, WatchR, ca * Vp.Clamp01(wk * 1.4f));
            c.Restore();
            // the partner: PRESENTED BY, then the mark with a sheen passing over it
            float my = WatchY + WatchR + 70 + markH / 2 * MarkScale;
            word.Color = Vp.Txt2.WithA(ca);
            word.TextSize = 13f;
            TextTracked.Draw(c, word, model.Verb, cx, WatchY + WatchR + 50, 2.6f);
            float mk = Vp.EaseOutBack(Vp.Seg(t, 520, 950));
            c.Save();
            c.Translate(cx, my);
            c.Scale(MarkScale * (0.92f + 0.08f * mk));
            c.SaveLayer(new SKRect(-markW / 2 - 20, -markH / 2 - 20, markW / 2 + 20, markH / 2 + 20), null);
            mark.Draw(c, 0, 0, ca * Vp.Clamp01(mk));
            float sx = -markW / 2 - 140 + Vp.EaseIo(Vp.Seg(t, 950, 1700)) * (markW + 280);
            c.Translate(sx, 0); c.Skew(-0.35f, 0);
            sheen.Color = SKColors.White.WithA(0.9f * ca);
            c.DrawRect(-90, -markH, 180, markH * 2, sheen);
            c.Restore();
            c.Restore();
            if (model.Moment.Length > 0)
            {
                word.Color = Vp.Txt2.WithA(ca);
                TextTracked.Draw(c, word, model.Moment, cx, my + markH / 2 * MarkScale + 40, 2.6f);
            }
        }
        c.RestoreToCount(save);
    }

    SKPaint? word;
    /// <summary>The rewinding watch (pre-rendered dial, hands per frame).</summary>
    public readonly LocalWatch Watch = new() { Caption = "" };
    DateTime watchTime;
    static readonly DateTime DayStart = new(2026, 1, 1);
    const float MarkScale = 0.62f;
    const float WatchY = 450f, WatchR = 178f;

    public override void Dispose()
    {
        band?.Dispose(); plateL?.Dispose(); plateR?.Dispose(); mark?.Dispose(); medal?.Dispose(); seamCover?.Dispose();
        flat.Dispose(); sweep.Dispose(); sheen.Dispose(); tick.Dispose(); edgeGlow.Dispose(); word?.Dispose(); Watch.Dispose();
        clip.Dispose(); door.Dispose();
    }
}
