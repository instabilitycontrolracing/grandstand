using SkiaSharp;

namespace ApexHUD.Native.Overlays;

public enum WeatherKind { RainStart, RainHeavier, Drying, TrackDry, TempUp, TempDown }

/// <summary>What the weather card says. Strings are ready to print (units already applied).</summary>
/// <param name="Lead">First part of the headline ("RAIN AT").</param>
/// <param name="Place">Where (corner / straight, "" when not known) - printed in the accent colour.</param>
/// <param name="Lap">Right of the eyebrow ("LAP 14"), may be "".</param>
/// <param name="Wetness">iRacing TrackWetness now (1 dry .. 7 extremely wet, 0 unknown).</param>
/// <param name="PrevWetness">What it was before (the elevated gauge fills from here).</param>
/// <param name="PlacePct">Lap fraction of the place (elevated radar marker), negative = unknown.</param>
public sealed record WeatherModel(WeatherKind Kind, string Lead, string Place, string Lap, int Wetness, int PrevWetness,
                                  string Air, string TrackTemp, string RainChance, VpTrack? Track = null, float PlacePct = -1f);

/// <summary>
/// V1 (2026-10-27): the weather card - "RAIN AT TERTRE ROUGE". A slanted glass callout top-right under the session bar
/// when rain starts, gets heavier, the track dries or the track temperature swings.
/// <para>Base: storm-blue accent, an icon tile with rain falling in it (sun for drying, a thermometer for temperature),
/// eyebrow / headline / one line of facts. Elevated ("storm cell"): wider, a live radar scope with the real track
/// outline, a sweep that lights up a rain cell over the place, streaks of rain across the glass and a seven-step
/// wetness gauge filling from the old level to the new one.</para>
/// </summary>
public sealed class WeatherCard : VpGraphic
{
    public const double InMs = 520, OutMs = 420, HoldMs = 9000;
    const float MinTop = 128f, RightMargin = 40f;
    float curTop = float.NaN; double lastDrawMs;

    // 2026-10-27 round 2 (Angelo): a StormExhibit card - its own sponsor slot ("WeatherCard" in sponsors.json, StormExhibit
    // by default). The partner's name is TYPESET in the card ("Storm" ivory, "Exhibit" storm blue - just type, no drawn
    // mark); if he drops his own logo file in Documents\iRacing\Grandstand and names it in the partner's "logo", that
    // picture is used instead. Nothing of a company's mark is drawn here.
    Partner sponsor; SKImage? sponsorLogo; string sponsorWord = "";
    /// <summary>The sponsor slot name in sponsors.json.</summary>
    public const string Slot = "WeatherCard";

    WeatherModel? model;
    double shownAt = -1, hideAt = -1;
    VpLayer? layer;
    float cardW, cardH;
    bool layerElevated;

    // live paints / paths
    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint sweep = new() { IsAntialias = false };
    readonly SKPaint radarSweep = new() { IsAntialias = true };
    readonly SKPaint blobPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    readonly SKPaint ping = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPath clip = new(), shape = new(), radarClip = new();
    SKImage? blob, streaks;
    SKPoint radarC, placeOnRadar; float radarR;

    public WeatherCard()
    {
        sweep.Shader = SKShader.CreateLinearGradient(new SKPoint(-160, 0), new SKPoint(160, 0),
            new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(16), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
        // the radar's sweep wedge: a sweep gradient that is bright at angle 0 and fades behind it (rotated with the canvas)
        radarSweep.Shader = SKShader.CreateSweepGradient(new SKPoint(0, 0),
            new[] { Vp.Storm.WithAlpha(0), Vp.Storm.WithAlpha(0), Vp.Storm.WithAlpha(40), Vp.StormPale.WithAlpha(150) },
            new[] { 0f, 0.62f, 0.94f, 1f });
    }

    public bool Showing => model != null;
    public WeatherModel? Model => model;

    public void Show(WeatherModel m, double nowMs)
    {
        model = m; shownAt = nowMs; hideAt = nowMs + HoldMs;
        LoadSponsor();
        Rebuild();
    }

    /// <summary>Event time: who presents the card and their logo file (if any).</summary>
    void LoadSponsor()
    {
        sponsorLogo?.Dispose(); sponsorLogo = null;
        sponsor = SponsorPack.For(Slot);
        sponsorWord = "";
        if (sponsor == Partner.None) return;
        try
        {
            var path = SponsorPack.LogoPath(sponsor);
            if (path != null) { using var b = SKBitmap.Decode(path); if (b != null) sponsorLogo = SKImage.FromBitmap(b); }
        }
        catch { sponsorLogo = null; }
        var w = SponsorPack.Info(sponsor).Word;
        sponsorWord = !string.IsNullOrWhiteSpace(w) ? w! : sponsor switch
        {
            Partner.Storm => "StormExhibit", Partner.AB => "A&B", Partner.GoFast => "GO FAST", Partner.TradingPaints => "Trading Paints", _ => "",
        };
    }

    /// <summary>Width of "PRESENTED BY [mark]" at the eyebrow size (0 = no sponsor).</summary>
    float PresentedWidth()
    {
        if (sponsor == Partner.None || (sponsorLogo == null && sponsorWord.Length == 0)) return 0;
        float vw = VpText.Width("PRESENTED BY", Fonts.Inter.Bold, 9.5f, 2.2f) + 10;
        if (sponsorLogo != null) return vw + sponsorLogo.Width * 22f / Math.Max(1, sponsorLogo.Height);
        return vw + VpText.Width(sponsorWord, Fonts.Inter.Bold, 16f, 0.2f);
    }

    /// <summary>"PRESENTED BY StormExhibit", right-aligned at <paramref name="right"/> on the baseline (bake time).</summary>
    void DrawPresented(SKCanvas c, float right, float baseline)
    {
        float w = PresentedWidth();
        if (w <= 0) return;
        float x = right - w;
        x += VpText.Draw(c, "PRESENTED BY", x, baseline, Fonts.Inter.Bold, 9.5f, Vp.Txt3, 2.2f) + 10;
        if (sponsorLogo != null)
        {
            float lh = 22f, lw = sponsorLogo.Width * lh / Math.Max(1, sponsorLogo.Height);
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            c.DrawImage(sponsorLogo, new SKRect(x, baseline - lh * 0.78f, x + lw, baseline + lh * 0.22f), lp);
            return;
        }
        if (sponsorWord.StartsWith("Storm", StringComparison.Ordinal) && sponsorWord.Length > 5)
        {
            // typeset: "Storm" ivory + the rest in StormExhibit's blue
            x += VpText.Draw(c, "Storm", x, baseline, Fonts.Inter.Bold, 16f, Vp.Ivory, 0.2f) + 0.2f;
            VpText.Draw(c, sponsorWord[5..], x, baseline, Fonts.Inter.Bold, 16f, Vp.Storm, 0.2f);
        }
        else VpText.Draw(c, sponsorWord, x, baseline, Fonts.Inter.Bold, 16f, Vp.Ivory, 0.2f);
    }

    public override void Hide(double nowMs) { if (model != null && hideAt > nowMs) hideAt = Math.Max(nowMs, shownAt + InMs); }

    public override bool Active(double nowMs) => model != null && nowMs < hideAt + OutMs;

    SKColor Accent => model?.Kind switch
    {
        WeatherKind.Drying or WeatherKind.TrackDry => Vp.GoldHi,
        WeatherKind.TempUp => Vp.Amber,
        WeatherKind.TempDown => Vp.StormPale,
        _ => Vp.Storm,
    };

    bool Rainy => model?.Kind is WeatherKind.RainStart or WeatherKind.RainHeavier;

    void Rebuild()
    {
        layer?.Dispose(); layer = null;
        var m = model!;
        layerElevated = Elevated;
        cardW = Elevated ? 760f : 580f;
        cardH = Elevated ? 196f : 128f;
        if (!Elevated)
        {
            // the facts line decides the width (never runs into the edge)
            float need = 140f + Math.Max(FactsWidth(m, false), EyebrowWidth(m) + 30f + PresentedWidth()) + 30f;
            cardW = Math.Clamp(need, 580f, 760f);
        }
        var r = new SKRect(0, 0, cardW, cardH);
        shape.Reset();
        using (var s = Vp.Slant(r, 18f, true, false)) shape.AddPath(s);
        var acc = Accent;
        layer = VpLayer.Record(new SKRect(-30, -20, cardW + 30, cardH + 40), c =>
        {
            Vp.GlassPanel(c, shape, acc);
            float x0;
            if (!Elevated)
            {
                // icon tile
                var tile = new SKRect(32, 22, 32 + 84, 22 + 84);
                using (var tp = new SKPaint { IsAntialias = true, Color = acc.WithAlpha(34) }) c.DrawRoundRect(tile, 4, 4, tp);
                using (var tr = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = acc.WithAlpha(110) }) c.DrawRoundRect(tile, 4, 4, tr);
                DrawGlyph(c, tile, m.Kind, acc);
                x0 = 140f;
            }
            else x0 = 40f;
            float right = Elevated ? cardW - 236f : cardW - 26f;
            // eyebrow + lap
            string eb = m.Kind switch { WeatherKind.TempUp or WeatherKind.TempDown => "TRACK CONDITIONS", _ => "WEATHER UPDATE" };
            float ebW = VpText.Draw(c, eb, x0, Elevated ? 40 : 38, Fonts.Inter.Bold, 11.5f, acc, 2.6f);
            DrawPresented(c, right, Elevated ? 40 : 38);
            if (m.Lap.Length > 0)
            {
                using (var d = new SKPaint { IsAntialias = true, Color = Vp.Txt3 }) c.DrawCircle(x0 + ebW + 11, (Elevated ? 40 : 38) - 4, 1.6f, d);
                VpText.Draw(c, m.Lap, x0 + ebW + 21, Elevated ? 40 : 38, Fonts.Inter.SemiBold, 11.5f, Vp.Txt3, 2.2f);
            }
            // headline: LEAD in ivory, PLACE in the accent (one fitted size)
            string lead = m.Lead.ToUpperInvariant(), place = m.Place.ToUpperInvariant();
            string full = place.Length > 0 ? lead + " " + place : lead;
            float hs = VpText.Fit(full, Fonts.BarlowCondensed.Black, Elevated ? 52f : 40f, right - x0, 0.5f, 22f);
            float hy = Elevated ? 98 : 80;
            float lw = VpText.Draw(c, lead, x0, hy, Fonts.BarlowCondensed.Black, hs, Vp.Ivory, 0.5f);
            if (place.Length > 0)
            {
                float px = x0 + lw + VpText.Width(" ", Fonts.BarlowCondensed.Black, hs) + 0.5f;
                var pc = Rainy ? Vp.StormPale : acc;
                VpText.Draw(c, place, px, hy, Fonts.BarlowCondensed.Black, hs, pc.WithAlpha(90), 0.5f, -1, blur: 10f);
                VpText.Draw(c, place, px, hy, Fonts.BarlowCondensed.Black, hs, pc, 0.5f);
            }
            // the facts line
            float fy = Elevated ? 130 : 108;
            float fx = x0;
            void Fact(string label, string value)
            {
                if (value.Length == 0) return;
                if (fx > x0) { using (var d = new SKPaint { IsAntialias = true, Color = Vp.Txt3.WithAlpha(150) }) c.DrawCircle(fx + 10, fy - 4, 1.6f, d); fx += 22; }
                fx += VpText.Draw(c, label, fx, fy, Fonts.Inter.SemiBold, 10.5f, Vp.Txt3, 1.8f) + 7;
                fx += VpText.Draw(c, value, fx, fy, Fonts.Inter.Bold, 13f, Vp.Ivory, 0.6f);
            }
            if (!Elevated) Fact("SURFACE", SurfaceName(m.Wetness));
            Fact("AIR", m.Air);
            Fact("TRACK", m.TrackTemp);
            if (Rainy || Elevated) Fact("RAIN", m.RainChance);
            if (Elevated)
            {
                // wetness gauge frame (the fill is drawn live)
                VpText.Draw(c, "SURFACE", x0, 166, Fonts.Inter.SemiBold, 10.5f, Vp.Txt3, 1.8f);
                VpText.Draw(c, SurfaceName(m.Wetness), right, 166, Fonts.Inter.Bold, 12f, Vp.Ivory, 1.2f, 1);
                float gx = x0, gw = right - x0, sw = (gw - 6 * 4) / 7f;
                using var gp = new SKPaint { IsAntialias = true, Color = Vp.Ivory.WithAlpha(22) };
                for (int i = 0; i < 7; i++) c.DrawRect(new SKRect(gx + i * (sw + 4), 174, gx + i * (sw + 4) + sw, 180), gp);
                // radar scope
                radarR = 70f;
                radarC = new SKPoint(cardW - 112f, 88f);
                radarClip.Reset(); radarClip.AddCircle(radarC.X, radarC.Y, radarR - 1f);
                using (var disc = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(radarC, radarR,
                           new[] { new SKColor(0x07, 0x16, 0x26), new SKColor(0x03, 0x0A, 0x12) }, null, SKShaderTileMode.Clamp) })
                    c.DrawCircle(radarC, radarR, disc);
                using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = Vp.Storm.WithAlpha(40) })
                {
                    for (int i = 1; i <= 3; i++) c.DrawCircle(radarC, radarR * i / 3f, ring);
                    c.DrawLine(radarC.X - radarR, radarC.Y, radarC.X + radarR, radarC.Y, ring);
                    c.DrawLine(radarC.X, radarC.Y - radarR, radarC.X, radarC.Y + radarR, ring);
                    ring.Color = Vp.Storm.WithAlpha(120); ring.StrokeWidth = 1.5f;
                    c.DrawCircle(radarC, radarR, ring);
                }
                using (var tk = new SKPaint { IsAntialias = true, StrokeWidth = 1.2f, Color = Vp.Storm.WithAlpha(90) })
                    for (int i = 0; i < 36; i++)
                    {
                        float a = i * MathF.PI / 18f, r0 = radarR - (i % 3 == 0 ? 7 : 4);
                        c.DrawLine(radarC.X + MathF.Cos(a) * r0, radarC.Y + MathF.Sin(a) * r0, radarC.X + MathF.Cos(a) * radarR, radarC.Y + MathF.Sin(a) * radarR, tk);
                    }
                placeOnRadar = radarC;
                if (m.Track != null)
                {
                    float bx = radarR * 0.78f;
                    var box = new SKRect(radarC.X - bx, radarC.Y - bx, radarC.X + bx, radarC.Y + bx);
                    using var tpath = m.Track.PathIn(box);
                    using (var tl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeJoin = SKStrokeJoin.Round, Color = Vp.Ivory.WithAlpha(120) })
                        c.DrawPath(tpath, tl);
                    if (m.PlacePct >= 0)
                    {
                        using var tp = new VpTrackPath(new SKPath(tpath));
                        placeOnRadar = tp.At(m.PlacePct);
                    }
                }
                VpText.Draw(c, "LIVE RADAR", radarC.X, radarC.Y + radarR + 22, Fonts.Inter.Bold, 9.5f, Vp.Storm.WithAlpha(190), 2.4f, 0);
            }
        });
        if (Elevated)
        {
            blob ??= BakeBlob();
            streaks ??= BakeStreaks();
        }
    }

    float EyebrowWidth(WeatherModel m)
    {
        string eb = m.Kind switch { WeatherKind.TempUp or WeatherKind.TempDown => "TRACK CONDITIONS", _ => "WEATHER UPDATE" };
        return VpText.Width(eb, Fonts.Inter.Bold, 11.5f, 2.6f) + (m.Lap.Length > 0 ? 21 + VpText.Width(m.Lap, Fonts.Inter.SemiBold, 11.5f, 2.2f) : 0);
    }

    float FactsWidth(WeatherModel m, bool elev)
    {
        float w = 0; int n = 0;
        void F(string l, string v)
        {
            if (v.Length == 0) return;
            if (n++ > 0) w += 22;
            w += VpText.Width(l, Fonts.Inter.SemiBold, 10.5f, 1.8f) + 7 + VpText.Width(v, Fonts.Inter.Bold, 13f, 0.6f);
        }
        if (!elev) F("SURFACE", SurfaceName(m.Wetness));
        F("AIR", m.Air); F("TRACK", m.TrackTemp);
        if (Rainy || elev) F("RAIN", m.RainChance);
        return w;
    }

    static string SurfaceName(int w) => w switch
    {
        1 => "DRY", 2 => "MOSTLY DRY", 3 => "DAMP", 4 => "LIGHTLY WET", 5 => "WET", 6 => "VERY WET", 7 => "FLOODED", _ => "",
    };

    static void DrawGlyph(SKCanvas c, SKRect tile, WeatherKind k, SKColor acc)
    {
        float cx = tile.MidX, cy = tile.MidY;
        using var p = new SKPaint { IsAntialias = true, Color = Vp.Ivory };
        if (k is WeatherKind.Drying or WeatherKind.TrackDry)
        {
            // sun half behind a small cloud
            using var sp = new SKPaint { IsAntialias = true, Color = acc };
            c.DrawCircle(cx - 6, cy - 6, 14, sp);
            sp.StrokeWidth = 3; sp.StrokeCap = SKStrokeCap.Round;
            for (int i = 0; i < 8; i++)
            {
                float a = i * MathF.PI / 4f;
                c.DrawLine(cx - 6 + MathF.Cos(a) * 20, cy - 6 + MathF.Sin(a) * 20, cx - 6 + MathF.Cos(a) * 26, cy - 6 + MathF.Sin(a) * 26, sp);
            }
            Cloud(c, cx + 8, cy + 10, 0.72f, p);
        }
        else if (k is WeatherKind.TempUp or WeatherKind.TempDown)
        {
            using var sp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Vp.Ivory };
            c.DrawRoundRect(new SKRect(cx - 7, cy - 28, cx + 7, cy + 10), 7, 7, sp);
            using var fp = new SKPaint { IsAntialias = true, Color = acc };
            c.DrawCircle(cx, cy + 16, 11, fp);
            c.DrawRect(new SKRect(cx - 3, cy - (k == WeatherKind.TempUp ? 20 : 4), cx + 3, cy + 12), fp);
            using var ap = new SKPaint { IsAntialias = true, Color = acc };
            using var tri = new SKPath();
            float ax = cx + 22, ay = cy - 8;
            if (k == WeatherKind.TempUp) { tri.MoveTo(ax, ay - 9); tri.LineTo(ax + 8, ay + 3); tri.LineTo(ax - 8, ay + 3); }
            else { tri.MoveTo(ax, ay + 9); tri.LineTo(ax + 8, ay - 3); tri.LineTo(ax - 8, ay - 3); }
            tri.Close(); c.DrawPath(tri, ap);
        }
        else Cloud(c, cx, cy - 8, 1f, p);
    }

    static void Cloud(SKCanvas c, float cx, float cy, float s, SKPaint p)
    {
        c.DrawCircle(cx - 14 * s, cy + 4 * s, 11 * s, p);
        c.DrawCircle(cx + 1 * s, cy - 4 * s, 15 * s, p);
        c.DrawCircle(cx + 15 * s, cy + 5 * s, 10 * s, p);
        c.DrawRoundRect(new SKRect(cx - 25 * s, cy + 2 * s, cx + 25 * s, cy + 15 * s), 6 * s, 6 * s, p);
    }

    /// <summary>A radar return: soft green -> yellow -> red rings of rain, drawn once.</summary>
    static SKImage BakeBlob()
    {
        const int S = 160;
        using var surf = SKSurface.Create(new SKImageInfo(S, S, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas; c.Clear(SKColors.Transparent);
        var rng = new Random(77);
        void Lobe(float x, float y, float r, SKColor col, float blur)
        {
            using var p = new SKPaint { IsAntialias = true, Color = col, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur) };
            c.DrawCircle(x, y, r, p);
        }
        for (int i = 0; i < 9; i++) Lobe(80 + (float)(rng.NextDouble() - 0.5) * 70, 80 + (float)(rng.NextDouble() - 0.5) * 46, 26 + (float)rng.NextDouble() * 12, new SKColor(0x2E, 0xC8, 0x6A, 120), 12);
        for (int i = 0; i < 6; i++) Lobe(78 + (float)(rng.NextDouble() - 0.5) * 44, 80 + (float)(rng.NextDouble() - 0.5) * 30, 14 + (float)rng.NextDouble() * 8, new SKColor(0xF5, 0xD0, 0x2A, 150), 8);
        for (int i = 0; i < 3; i++) Lobe(76 + (float)(rng.NextDouble() - 0.5) * 22, 80 + (float)(rng.NextDouble() - 0.5) * 16, 7 + (float)rng.NextDouble() * 4, new SKColor(0xF0, 0x55, 0x3A, 170), 5);
        return GpuContext.Pin(surf.Snapshot());
    }

    /// <summary>A tile of thin slanted rain streaks (tiled across the elevated card and scrolled).</summary>
    static SKImage BakeStreaks()
    {
        const int S = 256;
        using var surf = SKSurface.Create(new SKImageInfo(S, S, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surf.Canvas; c.Clear(SKColors.Transparent);
        var rng = new Random(11);
        using var p = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        for (int i = 0; i < 46; i++)
        {
            float x = (float)rng.NextDouble() * S, y = (float)rng.NextDouble() * S, len = 10 + (float)rng.NextDouble() * 22;
            p.StrokeWidth = 0.8f + (float)rng.NextDouble() * 0.7f;
            p.Color = SKColors.White.WithAlpha((byte)(18 + rng.Next(30)));
            for (int dx = -S; dx <= S; dx += S)          // wrap so the tile repeats seamlessly
                for (int dy = -S; dy <= S; dy += S)
                    c.DrawLine(x + dx, y + dy, x + dx - len * 0.28f, y + dy + len, p);
        }
        return GpuContext.Pin(surf.Snapshot());
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        if (m == null || layer == null) return;
        if (layerElevated != Elevated) Rebuild();
        double t = nowMs - shownAt;
        if (nowMs >= hideAt + OutMs) { model = null; return; }
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, InMs));
        float outK = Vp.EaseIn(Vp.Seg(nowMs, hideAt, hideAt + OutMs));
        float left = w - RightMargin - cardW;
        var acc = Accent;

        int save = c.Save();
        // round 2 (Angelo: "it must NEVER overlap or interrupt the session bar, normal or battle mode"): it hangs below
        // whatever hangs from the top edge right now - the bar's plate, its watch, the battle-mode ticker - and moves
        // down smoothly if that stack grows while it is up
        float wantTop = Math.Max(MinTop, VpLayout.CardTop);   // #413: 16 px under the stack
        var rect = new SKRect(left, wantTop, left + cardW, wantTop + cardH);
        while (VpLayout.HitsTopStack(rect) && wantTop < 600) { wantTop += 8; rect.Offset(0, 8); }
        if (float.IsNaN(curTop) || t < 1) curTop = wantTop;
        else curTop += (wantTop - curTop) * Math.Min(1f, (float)Math.Max(0, nowMs - lastDrawMs) / 140f);
        if (curTop < wantTop) curTop = wantTop;   // #413: down at once when the ticker comes in (never over it), back up smoothly
        lastDrawMs = nowMs;
        VpLayout.Claim(VpLayout.SlotWeather, new SKRect(left, curTop, left + cardW, curTop + cardH), nowMs);   // round 3: the others keep off it
        c.Translate(left + outK * 60f, curTop);
        // the reveal: a clip that opens right to left with a bright leading bar (mirrors the story-card exit band)
        float open = cardW + 40f;
        float revealX = cardW + 20f - open * inK;
        clip.Reset();
        clip.AddRect(new SKRect(revealX, -30, cardW + 40, cardH + 50));
        c.ClipPath(clip);
        float a = 1f - outK;
        layer.Draw(c, 0, 0, a);

        // moving parts, clipped to the card
        c.Save();
        c.ClipPath(shape, SKClipOperation.Intersect, true);
        double ts = t / 1000.0;
        if (!Perf.Light)
        {
            float sx = -200f + (float)((ts * 0.16) % 1.0) * (cardW + 400f);
            c.Save(); c.Translate(sx, 0); sweep.Color = SKColors.White.WithA(a); c.DrawRect(-160, 0, 320, cardH, sweep); c.Restore();
        }
        if (Elevated && Rainy && streaks != null)
        {
            float off = (float)((ts * 220.0) % 256.0), offX = -off * 0.28f;
            blobPaint.Color = SKColors.White.WithA(0.8f * a);
            for (float ty = -256 + off; ty < cardH; ty += 256)
                for (float tx = -256 + offX % 256; tx < cardW + 256; tx += 256)
                    c.DrawImage(streaks, tx, ty, blobPaint);
        }
        if (!Elevated && Rainy) DrawFallingDrops(c, ts, a, acc);
        if (Elevated) DrawRadar(c, m, ts, a);
        if (Elevated) DrawGauge(c, m, t, a, acc);
        c.Restore();

        // leading bar while it opens
        if (inK < 0.995f)
        {
            flat.Color = acc.WithA(a);
            c.DrawRect(new SKRect(revealX, -2, revealX + 5, cardH + 2), flat);
            flat.Color = SKColors.White.WithA(0.8f * a);
            c.DrawRect(new SKRect(revealX, -2, revealX + 1.5f, cardH + 2), flat);
        }
        c.RestoreToCount(save);
    }

    void DrawFallingDrops(SKCanvas c, double ts, float a, SKColor acc)
    {
        var tile = new SKRect(32, 22, 116, 106);
        c.Save();
        c.ClipRect(new SKRect(tile.Left + 2, tile.MidY + 4, tile.Right - 2, tile.Bottom - 2));
        stroke.StrokeWidth = 2.6f;
        int n = Model?.Kind == WeatherKind.RainHeavier ? 6 : 4;
        for (int i = 0; i < n; i++)
        {
            float ph = (float)((ts * 1.6 + i * 0.37) % 1.0);
            float x = tile.Left + 20 + i * (44f / (n - 1)) - ph * 6f, y = tile.MidY + 2 + ph * 40f;
            stroke.Color = acc.WithA(a * (1f - ph * 0.7f));
            c.DrawLine(x, y, x - 3.2f, y + 10f, stroke);
        }
        c.Restore();
    }

    void DrawRadar(SKCanvas c, WeatherModel m, double ts, float a)
    {
        float ang = (float)((ts * 0.55) % 1.0) * 360f;
        // sweep wedge
        c.Save();
        c.Translate(radarC.X, radarC.Y);
        c.RotateDegrees(ang);
        radarSweep.Color = SKColors.White.WithA(a);
        c.DrawCircle(0, 0, radarR - 1.5f, radarSweep);
        c.Restore();
        // the rain cell: brightest just after the sweep passes it, fading over the turn
        if (blob != null && (m.Kind is WeatherKind.RainStart or WeatherKind.RainHeavier))
        {
            float dx = placeOnRadar.X - radarC.X, dy = placeOnRadar.Y - radarC.Y;
            float cellAng = MathF.Atan2(dy, dx) * 180f / MathF.PI; if (cellAng < 0) cellAng += 360f;
            float since = ang - cellAng; if (since < 0) since += 360f;
            float glow = 0.45f + 0.55f * MathF.Exp(-since / 140f);
            float drift = (float)Math.Sin(ts * 0.7) * 3f;
            float bs = m.Kind == WeatherKind.RainHeavier ? 74f : 58f;
            blobPaint.Color = SKColors.White.WithA(glow * a);
            c.Save();
            c.ClipPath(radarClip, SKClipOperation.Intersect, true);
            c.DrawImage(blob, new SKRect(placeOnRadar.X - bs + drift, placeOnRadar.Y - bs * 0.75f, placeOnRadar.X + bs + drift, placeOnRadar.Y + bs * 0.75f), blobPaint);
            c.Restore();
        }
        // ping at the place
        float pp = (float)((ts * 0.55) % 1.0);
        ping.StrokeWidth = 1.6f;
        ping.Color = Vp.Ivory.WithA(a * (1f - pp));
        c.DrawCircle(placeOnRadar, 4f + pp * 18f, ping);
        flat.Color = Vp.Ivory.WithA(a);
        c.DrawCircle(placeOnRadar, 3.4f, flat);
    }

    void DrawGauge(SKCanvas c, WeatherModel m, double t, float a, SKColor acc)
    {
        float x0 = 40f, right = cardW - 236f, sw = (right - x0 - 6 * 4) / 7f;
        float from = Math.Clamp(m.PrevWetness, 0, 7), to = Math.Clamp(m.Wetness, 0, 7);
        float k = Vp.EaseIo(Vp.Seg(t, InMs + 300, InMs + 1500));
        float level = from + (to - from) * k;
        for (int i = 0; i < 7; i++)
        {
            float f = Vp.Clamp01(level - i);
            if (f <= 0f) continue;
            var col = Vp.Mix(Vp.GoldHi, Vp.Storm, i / 6f);
            flat.Color = col.WithA(a);
            float sx = x0 + i * (sw + 4);
            c.DrawRect(new SKRect(sx, 174, sx + sw * f, 180), flat);
        }
    }

    public override void Dispose()
    {
        layer?.Dispose(); blob?.Dispose(); streaks?.Dispose(); sponsorLogo?.Dispose();
        flat.Dispose(); stroke.Dispose(); sweep.Dispose(); radarSweep.Dispose(); blobPaint.Dispose(); ping.Dispose();
        clip.Dispose(); shape.Dispose(); radarClip.Dispose();
    }
}
