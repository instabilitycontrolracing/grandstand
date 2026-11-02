using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>One class's top three for the break card.</summary>
public sealed record BreakClass(string ClassName, string ClassColor, IReadOnlyList<(string Num, string Name, string Gap)> Top);

/// <summary>What the break card shows (all from the race as it stands at the pause).</summary>
public sealed record BreakModel(string Track, string Status, string Paused, IReadOnlyList<BreakClass> Classes, VpTrack? Map);

/// <summary>
/// W8 (2026-10-27): "going to break" - when a replay has been paused for a while the TV goes to a branded holding card
/// (optional music bed under it, TV only, off by default), and when the replay plays again it clears with the stinger.
/// <para>Base ("holding plate"): the opener / loading-splash navy plate fades over the picture; the real track outline with
/// the gold comet lapping it; WE'LL BE RIGHT BACK with the breathing gold hairline; the race status; the class leaders
/// along the bottom strip; ON HOLD with pulsing dots and how long it has been paused. It dissolves with a warm flash on
/// resume. Elevated ("split screen"): the paused picture stays visible - cinema bars slide in top and bottom, a slanted
/// glass panel takes the left 44 % with BACK AFTER THIS, the hold clock, the map with each class leader's dot and the top
/// three of each class rolling one class at a time.</para>
/// </summary>
public sealed class BreakCard : VpGraphic
{
    public const double InMs = 700, OutMs = 650;

    BreakModel? model;
    double shownAt = -1, hideAt = double.MaxValue;
    VpLayer? plate, title, strip, panel, bars;
    VpLayer?[] classLayers = Array.Empty<VpLayer?>();
    VpTrackPath? path;
    bool bakedElevated;

    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint cometCore = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint cometGlow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 14, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7) };
    readonly SKPaint headGlow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16) };
    readonly SKPaint hairGlow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) };
    readonly SKPaint clock = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 20f };
    readonly SKPaint sweep = new() { IsAntialias = false };
    readonly SKPath seg = new(), clip = new();

    public BreakCard()
    {
        sweep.Shader = SKShader.CreateLinearGradient(new SKPoint(-350, 0), new SKPoint(350, 0),
            new[] { Vp.Ivory.WithAlpha(0), Vp.Ivory.WithAlpha(9), Vp.Ivory.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
    }

    public bool Showing => model != null && hideAt == double.MaxValue;

    /// <summary>#336: the real local time (PC clock) on the broadcast watch.</summary>
    public readonly LocalWatch Watch = new();

    public void Show(BreakModel m, double nowMs)
    {
        model = m; shownAt = nowMs; hideAt = double.MaxValue;
        Bake();
    }

    /// <summary>The replay plays again: dissolve.</summary>
    public override void Hide(double nowMs) { if (model != null && hideAt == double.MaxValue) hideAt = Math.Max(nowMs, shownAt + InMs); }

    public override bool Active(double nowMs) => model != null && nowMs < hideAt + OutMs;

    void Bake()
    {
        foreach (var l in new[] { plate, title, strip, panel, bars }) l?.Dispose();
        foreach (var l in classLayers) l?.Dispose();
        plate = title = strip = panel = bars = null; path?.Dispose(); path = null;
        var m = model!;
        bakedElevated = Elevated;
        if (!Elevated)
        {
            plate = VpLayer.Record(new SKRect(0, 0, 1920, 1080), c =>
            {
                Vp.NavyPlate(c, new SKRect(0, 0, 1920, 1080));
                using var vg = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(960, 540), 1190,
                    new[] { SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(0), SKColors.Black.WithAlpha(128) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) };
                c.DrawRect(0, 0, 1920, 1080, vg);
                if (m.Map != null)
                {
                    using var p = m.Map.PathIn(new SKRect(960 - 640, 450 - 330, 960 + 640, 450 + 330));
                    using var gl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 13, StrokeJoin = SKStrokeJoin.Round, Color = Vp.Ivory.WithAlpha(24), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9) };
                    c.DrawPath(p, gl);
                    using var ln = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeJoin = SKStrokeJoin.Round, Color = Vp.Ivory.WithAlpha(52) };
                    c.DrawPath(p, ln);
                }
            });
            if (m.Map != null) path = new VpTrackPath(m.Map.PathIn(new SKRect(960 - 640, 450 - 330, 960 + 640, 450 + 330)));
            title = VpLayer.Record(new SKRect(0, 330, 1920, 640), c =>
            {
                VpText.Draw(c, "THE REPLAY IS ON HOLD", 960, 410, Fonts.Inter.SemiBold, 24f, Vp.GoldHi, 10f, 0);
                float s = VpText.Fit("WE'LL BE RIGHT BACK", Fonts.BarlowCondensed.Black, 120f, 1400f, 3f, 40f);
                VpText.Draw(c, "WE'LL BE RIGHT BACK", 960, 530, Fonts.BarlowCondensed.Black, s, Vp.TitleGlow.WithAlpha(89), 3f, 0, blur: 20f);
                VpText.Draw(c, "WE'LL BE RIGHT BACK", 960, 530, Fonts.BarlowCondensed.Black, s, Vp.Ivory, 3f, 0);
                string sub = (m.Track + "  ·  " + m.Status).ToUpperInvariant();
                VpText.Draw(c, sub, 960, 620, Fonts.Inter.Medium, VpText.Fit(sub, Fonts.Inter.Medium, 22f, 1500f, 7f, 12f), Vp.Txt3, 7f, 0);
            });
            strip = VpLayer.Record(new SKRect(0, 780, 1920, 1060), c =>
            {
                int n = Math.Min(4, m.Classes.Count);
                if (n == 0) return;
                const float total = 1500, y0 = 820;
                float cw = total / 4, x0 = (1920 - cw * n) / 2;
                using (var rule = new SKPaint { Color = Vp.Ivory.WithAlpha(26) }) c.DrawRect((1920 - total) / 2, y0 - 5, total, 1, rule);
                using var div = new SKPaint { Color = Vp.Ivory.WithAlpha(31) };
                for (int i = 0; i < n; i++)
                {
                    var cl = m.Classes[i];
                    float cx = x0 + cw * i + cw / 2;
                    if (i > 0) c.DrawRect(x0 + cw * i - 0.5f, y0 + 15, 1, 85, div);
                    var col = Vp.ClassLine(cl.ClassColor);
                    float lw = VpText.Width(cl.ClassName + " LEADER", Fonts.Inter.SemiBold, 15f, 6f);
                    using (var sp = new SKPaint { Color = col }) c.DrawRect(cx - lw / 2 - 16, y0 + 29, 4, 14, sp);
                    VpText.Draw(c, cl.ClassName + " LEADER", cx, y0 + 42, Fonts.Inter.SemiBold, 15f, Vp.Txt3, 6f, 0);
                    if (cl.Top.Count > 0)
                    {
                        string v = ("#" + cl.Top[0].Num + "  " + cl.Top[0].Name).ToUpperInvariant();
                        VpText.Draw(c, v, cx, y0 + 92, Fonts.BarlowCondensed.Bold, VpText.Fit(v, Fonts.BarlowCondensed.Bold, 40f, cw - 30, 1f, 18f), Vp.Ivory, 1f, 0);
                    }
                }
                VpText.Draw(c, "GRANDSTAND", 1920 - 70, 1020, Fonts.Inter.Bold, 18f, Vp.Ivory.WithAlpha(115), 9f, 1);
            });
        }
        else
        {
            bars = VpLayer.Record(new SKRect(0, 0, 1920, 1080), c =>
            {
                using var b = new SKPaint { Color = new SKColor(2, 4, 8, 255) };   // opaque: the session bar under it must not ghost through
                c.DrawRect(0, 0, 1920, 96, b); c.DrawRect(0, 1080 - 96, 1920, 96, b);
                using var gl = new SKPaint { Color = Vp.Gold.WithAlpha(150) };
                c.DrawRect(0, 95, 1920, 1, gl); c.DrawRect(0, 1080 - 96, 1920, 1, gl);
            });
            const float PW = 860f;
            panel = VpLayer.Record(new SKRect(-30, 96, PW + 40, 1080 - 96), c =>
            {
                using var shape = Vp.Slant(new SKRect(-30, 96, PW, 1080 - 96), 90f, false, true);
                using (var g = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(PW, 0),
                           new[] { new SKColor(4, 9, 20, 246), new SKColor(6, 14, 30, 236), new SKColor(8, 18, 38, 200) }, new[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp) })
                    c.DrawPath(shape, g);
                using (var e = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Vp.GoldHi })
                    c.DrawLine(PW, 96, PW - 90, 1080 - 96, e);
                VpText.Draw(c, "THE REPLAY IS ON HOLD", 90, 196, Fonts.Inter.Bold, 15f, Vp.GoldHi, 5f);
                VpText.Draw(c, "BACK AFTER THIS", 86, 286, Fonts.BarlowCondensed.Black, 92f, Vp.Ivory, 1.5f);
                using (var hp = new SKPaint { Color = Vp.GoldHi }) c.DrawRect(90, 312, 220, 4, hp);
                string sub = m.Status.ToUpperInvariant();
                VpText.Draw(c, sub, 90, 356, Fonts.Inter.SemiBold, 15f, Vp.Txt2, 3f);
                VpText.Draw(c, m.Track.ToUpperInvariant(), 90, 382, Fonts.Inter.SemiBold, 12f, Vp.Txt3, 2.6f);
                if (m.Map != null)
                {
                    using var p = m.Map.PathIn(new SKRect(90, 420, 600, 640));
                    using var ln = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, StrokeJoin = SKStrokeJoin.Round, Color = Vp.Ivory.WithAlpha(120) };
                    c.DrawPath(p, ln);
                }
            });
            if (m.Map != null) path = new VpTrackPath(m.Map.PathIn(new SKRect(90, 420, 600, 640)));
            classLayers = new VpLayer?[m.Classes.Count];
            for (int i = 0; i < m.Classes.Count; i++)
            {
                var cl = m.Classes[i];
                classLayers[i] = VpLayer.Record(new SKRect(80, 660, 780, 900), c =>
                {
                    var col = Vp.ClassLine(cl.ClassColor);
                    using (var sp = new SKPaint { Color = col }) c.DrawRect(90, 676, 4, 18, sp);
                    VpText.Draw(c, cl.ClassName.ToUpperInvariant() + "  ·  TOP 3", 104, 692, Fonts.Inter.Bold, 13f, Vp.Ivory, 3f);
                    for (int r = 0; r < Math.Min(3, cl.Top.Count); r++)
                    {
                        float y = 740 + r * 48;
                        var (num, name, gap) = cl.Top[r];
                        VpText.Draw(c, (r + 1).ToString(), 104, y, Fonts.BarlowCondensed.ExtraBold, 32f, r == 0 ? Vp.GoldHi : Vp.Ivory, 0, 0);
                        float cw = Math.Max(36, VpText.Width(num, Fonts.Inter.Bold, 13f) + 14);
                        using (var cb = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(new SKRect(128, y - 20, 128 + cw, y + 1), 2, 2, cb);
                        VpText.Draw(c, num, 128 + cw / 2, y - 5, Fonts.Inter.Bold, 13f, new SKColor(8, 10, 16), 0, 0);
                        VpText.Draw(c, name.ToUpperInvariant(), 128 + cw + 14, y, Fonts.BarlowCondensed.Bold, 30f, Vp.Ivory, 0.6f);
                        VpText.Draw(c, gap, 700, y, Fonts.Inter.SemiBold, 16f, r == 0 ? Vp.GoldHi : Vp.Txt2, 0.5f, 1);
                    }
                });
            }
        }
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        if (m == null) return;
        if (bakedElevated != Elevated) Bake();
        if (nowMs >= hideAt + OutMs) { model = null; return; }
        double t = nowMs - shownAt;
        int save = c.Save();
        c.Scale(w / 1920f, h / 1080f);
        if (!Elevated) DrawPlate(c, m, t, nowMs);
        else DrawSplit(c, m, t, nowMs);
        // #336 (2026-10-27): the broadcast watch with the real local time, top middle; it drops in with the card
        float wk = Vp.EaseOutBack(Vp.Seg(t, 150, 750)) * (1f - Vp.EaseIo(Vp.Seg(nowMs, hideAt, hideAt + OutMs * 0.8)));
        if (wk > 0.003f) Watch.Draw(c, 960, (Elevated ? 104f : 150f) - (1f - Math.Min(1f, wk)) * 40f, Elevated ? 62f : 74f, Vp.Clamp01(wk));
        c.RestoreToCount(save);
    }

    void DrawPlate(SKCanvas c, BreakModel m, double t, double nowMs)
    {
        float inK = Vp.EaseIo(Vp.Seg(t, 0, InMs));
        float outK = Vp.EaseIo(Vp.Seg(nowMs, hideAt, hideAt + OutMs));
        float a = inK * (1f - outK);
        if (a <= 0.002f) return;
        double ts = t / 1000.0;
        plate!.Draw(c, 0, 0, a);
        float sx = -400f + (float)((ts * 0.1) % 1.0) * 2720f;
        sweep.Color = SKColors.White.WithA(a);
        c.Save(); c.Translate(sx, 0); c.DrawRect(-350, 0, 700, 1080, sweep); c.Restore();
        if (path != null) Comet(c, (float)((ts * 0.16) % 1.0), 0.2f, a, 3.6f);
        float tk = Vp.EaseOutExpo(Vp.Seg(t, 250, 1100));
        title!.Draw(c, 0, (1f - tk) * 20f, a * tk);
        float hw = 300f * (0.9f + 0.1f * MathF.Sin((float)(ts * 1.3)));
        flat.Color = Vp.GoldHi.WithA(a * tk);
        c.DrawRect(960 - hw, 568, 2 * hw, 4, flat);
        hairGlow.Color = Vp.GoldHi.WithA(0.4f * a * tk);
        c.DrawRect(960 - hw, 566, 2 * hw, 8, hairGlow);
        strip!.Draw(c, 0, 0, a * Vp.Seg(t, 500, 1200));
        HoldClock(c, 70, 1020, t, a, Vp.Txt3);
        // warm flash on the way out
        float fl = MathF.Sin(MathF.PI * Vp.Seg((float)(nowMs - hideAt), 0, (float)OutMs * 0.6f));
        if (hideAt != double.MaxValue && fl > 0f) { flat.Color = Vp.Warm.WithA(0.3f * fl); c.DrawRect(0, 0, 1920, 1080, flat); }
    }

    void HoldClock(SKCanvas c, float x, float y, double t, float a, SKColor col)
    {
        clock.Color = col.WithA(a);
        TextTracked.Draw(c, clock, "ON HOLD", x, y, 6f);
        float bx = x + TextBlobs.Measure("ON HOLD", clock) + 6f * 7 + 16;
        for (int i = 0; i < 3; i++)
        {
            double ph = ((t / 1000.0 * 1.4 - i * 0.18) % 1.0 + 1.0) % 1.0;
            float s = (float)Math.Max(0, Math.Sin(ph * Math.PI));
            flat.Color = col.WithA(a * (0.25f + 0.75f * s * s));
            c.DrawCircle(bx + i * 16, y - 7, 4, flat);
        }
        clock.Color = Vp.Ivory.WithA(a);
        TextBlobs.Draw(c, VpStr.Clock(t / 1000.0), bx + 60, y, clock);
    }

    void Comet(SKCanvas c, float head, float tail, float a, float width)
    {
        const int Core = 18;
        for (int g = 0; g < 4; g++)
        {
            float aa = MathF.Pow(1f - (g + 0.5f) / 4, 1.6f);
            if (!path!.Segment(head - tail * (g + 1) / 4, head - tail * g / 4, seg)) continue;
            cometGlow.Color = Vp.GoldHi.WithA(0.35f * aa * a);
            c.DrawPath(seg, cometGlow);
        }
        for (int i = 0; i < Core; i++)
        {
            float aa = MathF.Pow(1f - i / (float)Core, 1.6f);
            if (!path!.Segment(head - tail * (i + 1) / Core, head - tail * i / Core, seg)) continue;
            cometCore.StrokeWidth = width * (1f - 0.5f * i / Core);
            cometCore.Color = Vp.GoldHi.WithA(aa * a);
            c.DrawPath(seg, cometCore);
        }
        var p = path!.At(head);
        headGlow.Color = Vp.GoldHi.WithA(0.35f * a); c.DrawCircle(p, 24, headGlow);
        flat.Color = Vp.Warm.WithA(a); c.DrawCircle(p, 6, flat);
    }

    void DrawSplit(SKCanvas c, BreakModel m, double t, double nowMs)
    {
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, InMs));
        float outK = Vp.EaseIn(Vp.Seg(nowMs, hideAt, hideAt + OutMs));
        float k = inK * (1f - outK);
        if (k <= 0.002f) return;
        // cinema bars slide in
        float by = (1f - k) * 96f;
        c.Save(); c.Translate(0, -by); clip.Reset(); clip.AddRect(new SKRect(0, 0, 1920, 540)); c.ClipPath(clip); bars!.Draw(c, 0, 0, 1f); c.Restore();
        c.Save(); c.Translate(0, by); clip.Reset(); clip.AddRect(new SKRect(0, 540, 1920, 1080)); c.ClipPath(clip); bars!.Draw(c, 0, 0, 1f); c.Restore();
        // paused label on the picture side (top bar)
        clock.Color = Vp.Ivory.WithA(k * 0.85f);
        clock.TextSize = 14f;
        clock.TextAlign = SKTextAlign.Right;
        TextTracked.Draw(c, clock, m.Paused, 1920 - 60, 56 - by, 4f);
        clock.TextAlign = SKTextAlign.Left;
        clock.TextSize = 20f;
        // the panel slides in from the left
        float px = -(1f - k) * 900f;
        c.Save(); c.Translate(px, 0);
        panel!.Draw(c, 0, 0, 1f);
        double ts = t / 1000.0;
        if (path != null)
        {
            Comet(c, (float)((ts * 0.2) % 1.0), 0.18f, 1f, 2.6f);
        }
        // the classes roll, one every 4 s
        int nc = classLayers.Length;
        if (nc > 0)
        {
            double per = 4000;
            int i = (int)(t / per) % nc;
            double into = t % per;
            float ca = Vp.Seg(into, 0, 350) * (1f - Vp.Seg(into, per - 350, per));
            if (t < per) ca = Math.Max(ca, Vp.Seg(t, 400, 800) * (1f - Vp.Seg(into, per - 350, per)));
            classLayers[i]?.Draw(c, (1f - Vp.EaseOutExpo(Vp.Seg(into, 0, 500))) * 24f, 0, ca);
            for (int d = 0; d < nc; d++)
            {
                flat.Color = (d == i ? Vp.GoldHi : Vp.Ivory.WithAlpha(60));
                c.DrawCircle(104 + d * 18, 910, 4, flat);
            }
        }
        HoldClock(c, 90, 1080 - 96 - 40, t, 1f, Vp.Txt3);
        c.Restore();
    }

    public override void Dispose()
    {
        foreach (var l in new[] { plate, title, strip, panel, bars }) l?.Dispose();
        foreach (var l in classLayers) l?.Dispose();
        path?.Dispose();
        Watch.Dispose();
        foreach (var p in new[] { flat, cometCore, cometGlow, headGlow, hairGlow, clock, sweep }) p.Dispose();
        seg.Dispose(); clip.Dispose();
    }
}
