using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// HALF-SCREEN PANELS (Angelo 2026-10-19: "little half splash screens"). Before the green flag
/// each info card takes over one half of the screen, top to bottom, starting just under the
/// session bar + starting-order strip (those stay visible). Panels alternate left / right so the
/// live picture keeps changing sides. Everything inside is centred on the panel. Each keeps its
/// own silhouette, with the shape's feature on its inner edge (towards the live half):
///   Weather - radar scope bulging into the live half      Race Info - ticket notches + tear line
///   Poles   - slanted inner edge                           A&amp;B - a house gable
///   Track   - the circuit breaks out into the live half    Driver - inner bottom corner cut
///   Storm   - a lightning bolt notched into the inner edge
/// Pictures use full-size panel versions (the strip versions made for the old lower third were
/// far too small and turned grainy when blown up); the radar is never enlarged past ~its real size.
/// </summary>
public sealed partial class PreRaceCards
{
    /// <summary>Whole screen in the card's coordinates. When set, pre-race cards draw as panels.</summary>
    public SKRect Screen;

    // #773 (2026-11-05): bottom pad 40 -> 90 so the panels clear the iRacing logo in the bottom-right corner (24 px margin + 45 px logo + gap)
    const float PanelTop = 172f, PanelBottomPad = 90f, PanelSidePad = 48f, PanelGap = 40f;

    SKRect PanelRect(int slot, out bool right)
    {
        right = slot % 2 == 1;
        float w = Screen.Width / 2f - PanelSidePad - PanelGap / 2f;
        float left = right ? Screen.MidX + PanelGap / 2f : Screen.Left + PanelSidePad;
        return new SKRect(left, Screen.Top + PanelTop, left + w, Screen.Bottom - PanelBottomPad);
    }

    static SKPoint PanelScope(SKRect r, bool right) => new(right ? r.Left + 10 : r.Right - 10, r.Top + 250);
    const float PanelScopeR = 130f;

    // silhouettes, built as a LEFT panel (inner edge = right) and mirrored for the right half
    static SKPath PanelShape(Kind k, SKRect r, bool right)
    {
        float L = r.Left, T = r.Top, R = r.Right, B = r.Bottom, W = r.Width;
        SKPath p;
        switch (k)
        {
            case Kind.Weather:
            {
                using var box = new SKPath(); box.AddRoundRect(r, 16, 16);
                box.AddCircle(R - 10, T + 250, PanelScopeR);
                p = box.Simplify() ?? new SKPath(box);
                break;
            }
            case Kind.RaceInfo:
            {
                using var box = new SKPath(); box.AddRoundRect(r, 16, 16);
                using var n = new SKPath(); n.AddCircle(L, T + 500, 24); n.AddCircle(R, T + 500, 24);
                p = box.Op(n, SKPathOp.Difference) ?? new SKPath(box);
                break;
            }
            case Kind.Poles:
                p = RoundedPoly(16, new SKPoint(L, T), new SKPoint(R, T), new SKPoint(R - 80, B), new SKPoint(L, B)); break;
            case Kind.PartnerAB:
                p = RoundedPoly(16, new SKPoint(L, B), new SKPoint(L, T + 110), new SKPoint(L + W * 0.5f, T - 6), new SKPoint(R, T + 110), new SKPoint(R, B)); break;
            case Kind.Driver:
                p = RoundedPoly(16, new SKPoint(L, T), new SKPoint(R, T), new SKPoint(R, B - 250), new SKPoint(R - 120, B), new SKPoint(L, B)); break;
            case Kind.PartnerStorm:
            {
                using var box = new SKPath(); box.AddRoundRect(r, 16, 16);
                float y = T + r.Height * 0.45f;
                using var bolt = new SKPath();
                bolt.AddPoly(new[] { new SKPoint(R + 2, y - 70), new SKPoint(R - 46, y - 14), new SKPoint(R - 18, y - 10), new SKPoint(R - 70, y + 70),
                                     new SKPoint(R - 34, y + 6), new SKPoint(R - 60, y + 2), new SKPoint(R + 2, y - 70) }, true);
                p = box.Op(bolt, SKPathOp.Difference) ?? new SKPath(box);
                break;
            }
            default:
                p = new SKPath(); p.AddRoundRect(r, 16, 16); break;
        }
        if (right) p.Transform(SKMatrix.CreateScale(-1, 1, r.MidX, 0));
        return p;
    }

    void DrawPanel(SKCanvas c, RaceSnapshot snap, Kind kind, int slot, double local, float a)
    {
        cardLocal = local;
        var r0 = PanelRect(slot, out bool right);
        float slide = (1 - a) * 170 * (right ? 1 : -1);
        var r = new SKRect(r0.Left + slide, r0.Top, r0.Right + slide, r0.Bottom);
        if (kind == Kind.TrackFacts)
        {
            // 2026-10-25 (Angelo: "the track logo and track outline one in the intro takes the width of the screen, expand
            // that card out and give it time to do the animation of that whole process"): the full width, rising up
            var wr = new SKRect(Screen.Left + PanelSidePad, Screen.Top + PanelTop + (1 - a) * 60, Screen.Right - PanelSidePad, Screen.Bottom - PanelBottomPad + (1 - a) * 60);
            using var wshape = new SKPath(); wshape.AddRoundRect(wr, 18, 18);
            DrawFrame(c, wr, Gold, a, null, false, GridIntro.Matte(TrackLogoFor(snap)).Matte, false, wshape);
            PanelTrackWide(c, wr, snap, a);
            return;
        }
        if (kind == Kind.Globe)   // the globe flight (GlobeIntro): its own space backdrop, a thin cyan rim
        {
            using var rr = new SKRoundRect(r, 16, 16);
            using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(120 * a)), ImageFilter = SKImageFilter.CreateBlur(16, 16) }) c.DrawRoundRect(rr, sh);
            int sv = c.Save();
            c.ClipRoundRect(rr, antialias: true);
            GlobeIntro.Draw(c, r, snap, local, a);
            c.RestoreToCount(sv);
            using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(0x8F, 0xE3, 0xFF, (byte)(160 * a)) }) c.DrawRoundRect(rr, rim);
            return;
        }
        var shape = PanelShape(kind, r, right);
        // even margins both sides, everything centred on the panel (Angelo 2026-10-19)
        float cl = r.Left + 90, cr = r.Right - 90, cw = cr - cl, cx = r.MidX;

        SKColor accent = kind switch { Kind.Weather or Kind.PartnerStorm => StormLight, Kind.Driver => PlayerBlue, _ => Gold };
        var backdrop = kind switch
        {
            Kind.Weather or Kind.PartnerStorm => Bmp("logos", "sponsors", "storm_radar_panel.png") ?? Bmp("logos", "sponsors", "storm_radar_bg.png"),
            Kind.PartnerAB => Bmp("logos", "sponsors", "ab_roof_panel.png") ?? Bmp("logos", "sponsors", "ab_roof_bg.png"),
            _ => null,
        };
        SKColor? fill = kind == Kind.TrackFacts ? GridIntro.Matte(TrackLogoFor(snap)).Matte : null;

        // Radar cards (Angelo 2026-10-19): the panel is cut to the radar picture's own shape, so the
        // radar fills it edge to edge at its real size - crisp, never blown up.
        if (backdrop != null && kind is Kind.Weather or Kind.PartnerStorm)
        {
            float h = r.Width * backdrop.Height / backdrop.Width;
            r = new SKRect(r.Left, r.Top, r.Right, r.Top + h);
            shape.Dispose();
            shape = PanelShape(kind, r, right);
        }
        frameRevealR = -1; frameShingles = kind == Kind.PartnerAB; frameCarSlide = 0;
        frameBackdropA = kind == Kind.PartnerStorm ? K(0.08, 0.7) : 1;
        frameMaxScale = kind is Kind.Weather or Kind.PartnerStorm ? 1.05f : float.MaxValue;   // radar: never blown up
        float wm = 66;
        if (kind == Kind.Weather)
        {
            while (wm > 30 && WordmarkWidth(wm) > cw) wm -= 1;
            float wx = cx - WordmarkWidth(wm) / 2;
            frameRevealCenter = new SKPoint(wx + wm * 0.52f, r.Top + 122 - wm * 0.36f);
            frameRevealR = K(0.0, 1.3) * 1500;
        }
        DrawFrame(c, r, accent, a, backdrop, kind == Kind.PartnerAB, fill, false, shape);
        frameMaxScale = float.MaxValue;
        switch (kind)
        {
            case Kind.Weather:      PanelWeather(c, r, shape, snap, a, cl, cw, wm, right); break;
            case Kind.RaceInfo:     PanelRaceInfo(c, r, snap, a, cl, cw); break;
            case Kind.Poles:        PanelPoles(c, r, snap, a, cl, cw, right); break;
            case Kind.Watch:        PanelWatch(c, r, snap, a, cl, cw, right); break;
            case Kind.PartnerAB:    PanelAB(c, r, a, cw); break;
            case Kind.TrackFacts:   PanelTrack(c, r, snap, a, cl, cw, right); break;
            case Kind.Driver:       PanelDriver(c, r, shape, snap, a, cl, cw, right); break;
            case Kind.PartnerStorm: PanelStorm(c, r, shape, a, cw); break;
        }
        shape.Dispose();
    }

    // ---- centred text helpers
    static float TrackedWidth(SKPaint p, string s, float track) => p.MeasureText(s) + Math.Max(0, s.Length - 1) * track;

    /// <summary>Tracked caps label centred on <paramref name="x"/>.</summary>
    static void LabelC(SKCanvas c, string s, float x, float y, float size, SKColor col, float a)
    {
        using var lp = P(Fonts.Inter.Bold, size, col, a);
        float tr = 0.3f * size;
        TextTracked.Draw(c, lp, s, x - TrackedWidth(lp, s, tr) / 2, y, tr);
    }

    /// <summary>Big stat centred on <paramref name="x"/>: label over a large value (+ optional line under).</summary>
    static void BigStatC(SKCanvas c, float x, float y, string label, string value, float a, float valueSize, float maxW, string? sub = null)
    {
        LabelC(c, label, x, y, 18, Txt3, a);
        float vs;
        using (var vp = P(Fonts.Inter.Bold, valueSize, Txt, a))
        { FitSize(vp, value, maxW, 22); vs = vp.TextSize; vp.TextAlign = SKTextAlign.Center; c.DrawText(value, x, y + 14 + vs * 0.78f, vp); }
        if (sub != null) using (var sp = P(Fonts.Inter.SemiBold, 22, Txt2, a)) { sp.TextAlign = SKTextAlign.Center; c.DrawText(sub, x, y + 14 + vs * 0.78f + 36, sp); }
    }

    /// <summary>A line that grows outward from the centre.</summary>
    static void HLineC(SKCanvas c, float cx, float halfW, float y, float a, SKColor? col = null, float h = 1)
    {
        using var p = new SKPaint { Color = (col ?? new SKColor(255, 255, 255, 34)).WithAlpha((byte)((col?.Alpha ?? 34) * a)) };
        c.DrawRect(new SKRect(cx - halfW, y, cx + halfW, y + h), p);
    }

    static void TextC(SKCanvas c, string s, float x, float y, SKTypeface tf, float size, SKColor col, float a, float maxW, float min = 18)
    {
        using var p = P(tf, size, col, a);
        FitSize(p, s, maxW, min);
        p.TextAlign = SKTextAlign.Center;
        c.DrawText(s, x, y, p);
    }

    // ---------------------------------------------------------------- WEATHER
    void PanelWeather(SKCanvas c, SKRect r, SKPath shape, RaceSnapshot s, float a, float cl, float cw, float wm, bool right)
    {
        float cx = cl + cw / 2, la = a * K(0.0, 0.35);
        if (SponsorPack.IsOn(Partner.Storm)) TextC(c, "presented by", cx, r.Top + 58, Fonts.Inter.SemiBold, 24, Txt2, la, cw);
        StormWordmark(c, cx - WordmarkWidth(wm) / 2, r.Top + 122, wm, la);
        HLineC(c, cx, cw / 2 * K(0.2, 0.6), r.Top + 152, a);

        // the radar ping sweeping out of the StormExhibit icon
        float beamA = 1 - K(1.2, 0.6);
        if (beamA > 0)
        {
            var ctr = frameRevealCenter;
            c.Save();
            c.ClipPath(shape, antialias: true);
            c.Translate(ctr.X, ctr.Y); c.RotateDegrees((float)(cardLocal * 330 - 60));
            using (var beam = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(SKPoint.Empty,
                       new[] { SKColors.Transparent, StormLight.WithAlpha(0), StormLight.WithAlpha((byte)(150 * beamA * a)) }, new[] { 0f, 0.86f, 1f }) })
                c.DrawCircle(0, 0, 1600, beam);
            using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = StormLight.WithAlpha((byte)(170 * beamA * a)) })
                c.DrawCircle(0, 0, frameRevealR, ring);
            c.Restore();
        }
        var e = s.Extras;
        bool f = IsImperial(s);
        // the scope shows the live rain chance from the game (Angelo 2026-10-19)
        string rainNow = e.PrecipNow > 0.01f || s.IsRaining ? "RAIN" : $"{e.RainChancePct:0}%";
        DrawScopeAt(c, PanelScope(r, right), PanelScopeR, a, Tick(rainNow, K(0.3, 1.0)), e.PrecipNow > 0.01f || s.IsRaining ? "FALLING NOW" : "RAIN CHANCE");
        float Hit(float x, float y) { float d = SKPoint.Distance(new SKPoint(x, y), frameRevealCenter); return Math.Clamp((frameRevealR - d) / 160f, 0, 1); }
        float TickK(float x, float y) { float d = SKPoint.Distance(new SKPoint(x, y), frameRevealCenter); return Ease(Math.Clamp((frameRevealR - d) / 700f, 0, 1)); }
        float colW = cw / 3f;
        var cells = new List<(string Label, string Value, string? Sub, bool Tick)>
        {
            ("AIR", Temp(s.AirTempC, f), null, true),
            ("TRACK", Temp(s.TrackTempC, f), null, true),
            ("SKY", Sky(e.Skies), null, false),
            ("WIND", e.WindMs > 0.05f ? (f ? $"{e.WindMs * 2.237f:0} MPH" : $"{e.WindMs * 3.6f:0} KM/H") : "CALM", e.WindMs > 0.05f ? "from " + Compass(e.WindDirRad) : null, true),
            ("RAIN", e.PrecipNow > 0.01f || s.IsRaining ? "RAIN" : $"{e.RainChancePct:0}%", null, true),
            ("HUMIDITY", e.Humidity >= 0 ? $"{e.Humidity * 100:0}%" : "-", null, true),
        };
        for (int i = 0; i < cells.Count; i++)
        {
            float x = cl + colW * (i % 3 + 0.5f), y = r.Top + 188 + (i / 3) * 172;
            var cell = cells[i];
            BigStatC(c, x, y, cell.Label, cell.Tick ? Tick(cell.Value, TickK(x, y)) : cell.Value, a * Hit(x, y), 68, colW - 40, cell.Sub);   // room between columns
        }
    }

    void DrawScopeAt(SKCanvas c, SKPoint sc, float rad, float a, string? value = null, string? label = null)
    {
        float k = K(0.0, 0.6);
        c.Save();
        using (var clip = new SKPath()) { clip.AddCircle(sc.X, sc.Y, rad - 1); c.ClipPath(clip, antialias: true); }
        using (var dim = new SKPaint { Shader = SKShader.CreateRadialGradient(sc, rad,
                   new[] { new SKColor(4, 12, 20, (byte)(90 * a)), new SKColor(4, 12, 20, (byte)(180 * a)) }, null, SKShaderTileMode.Clamp) })
            c.DrawCircle(sc, rad, dim);
        using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = StormLight.WithAlpha((byte)(120 * a * k)) })
        {
            for (int i = 1; i <= 3; i++) c.DrawCircle(sc, rad * i / 3.4f, ring);
            c.DrawLine(sc.X - rad, sc.Y, sc.X + rad, sc.Y, ring);
            c.DrawLine(sc.X, sc.Y - rad, sc.X, sc.Y + rad, ring);
        }
        using (var beam = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(sc,
                   new[] { StormLight.WithAlpha(0), StormLight.WithAlpha(0), StormLight.WithAlpha((byte)(160 * a * k)) }, new[] { 0f, 0.8f, 1f }) })
        {
            c.Save(); c.RotateRadians((float)(cardLocal * 2.2), sc.X, sc.Y);
            c.DrawCircle(sc, rad, beam);
            c.Restore();
        }
        c.Restore();
        if (value != null)
        {
            // a dark disc behind the reading so it stays legible over the sweep
            using (var disc = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(sc, rad * 0.62f,
                       new[] { new SKColor(4, 12, 20, (byte)(210 * a)), new SKColor(4, 12, 20, 0) }, null, SKShaderTileMode.Clamp) })
                c.DrawCircle(sc, rad * 0.62f, disc);
            float va = a * k;
            using (var vp = P(Fonts.Inter.Bold, rad * 0.52f, Txt, va)) { vp.TextAlign = SKTextAlign.Center; FitSize(vp, value, rad * 1.25f, 18); c.DrawText(value, sc.X, sc.Y + vp.TextSize * 0.30f, vp); }
            if (label != null) LabelC(c, label, sc.X, sc.Y + rad * 0.52f, 13, StormLight, va);
        }
        using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = StormLight.WithAlpha((byte)(230 * a)) })
            c.DrawCircle(sc, rad - 1, rim);
    }

    // ---------------------------------------------------------------- RACE INFO
    void PanelRaceInfo(SKCanvas c, SKRect r, RaceSnapshot s, float a, float cl, float cw)
    {
        float cx = cl + cw / 2;
        string series = (SeriesNames.ResolveName(s.SeriesId) ?? "TODAY'S RACE").ToUpperInvariant();
        var slug = SeriesNames.ResolveLogoSlug(s.SeriesId);
        var slogo = slug != null ? Bmp("logos", "series", slug + ".png") : null;
        float nameY = r.Top + 200;
        if (slogo != null)
        {
            float sc = Math.Min(cw * 0.8f / slogo.Width, 290f / slogo.Height);
            float lw = slogo.Width * sc, lh = slogo.Height * sc;
            var dst = new SKRect(cx - lw / 2, r.Top + 44, cx + lw / 2, r.Top + 44 + lh);
            float fk = K(0.15, 0.75);
            float sx = (float)Math.Cos((1 - fk) * Math.PI);
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * Math.Min(1f, fk * 3))),
                ImageFilter = SKImageFilter.CreateDropShadow(0, 6, 14, 14, new SKColor(0, 0, 0, (byte)(160 * a))) };
            c.Save();
            c.Translate(dst.MidX, dst.MidY); c.Scale(Math.Max(0.02f, Math.Abs(sx)), 1); c.Translate(-dst.MidX, -dst.MidY);
            c.DrawBitmap(slogo, dst, lp);
            c.Restore();
            nameY = dst.Bottom + 84;
        }
        // the series name flips on letter by letter, centred
        using (var p = P(Fonts.CormorantGaramond.Bold, 62, Txt, a))
        {
            FitSize(p, series, cw, 22);
            float total = p.MeasureText(series), x = cx - total / 2, mid = nameY - p.TextSize * 0.34f;
            for (int i = 0; i < series.Length; i++)
            {
                string ch = series[i].ToString();
                float w = p.MeasureText(ch);
                float k = K(0.05 + i * 0.025, 0.22);
                if (k > 0 && ch != " ")
                {
                    byte al = p.Color.Alpha;
                    p.Color = p.Color.WithAlpha((byte)(al * k));
                    c.Save(); c.Translate(0, mid); c.Scale(1, Math.Max(0.02f, k)); c.Translate(0, -mid);
                    c.DrawText(ch, x, nameY, p);
                    c.Restore();
                    p.Color = p.Color.WithAlpha(al);
                }
                x += w;
            }
        }
        // the ticket's tear line between the notches, perforating outward from the middle
        float ty = r.Top + 500, half = (r.Width / 2 - 34) * K(0.3, 0.7);
        using (var dot = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(60 * a)) })
            for (float d = 0; d <= half; d += 14) { c.DrawCircle(r.MidX + d, ty, 2.2f, dot); if (d > 0) c.DrawCircle(r.MidX - d, ty, 2.2f, dot); }

        int cars = 0; var irs = new List<int>(); var classNames = new List<string>();
        foreach (var cl0 in s.ClassLeaderboard)
        {
            if (cl0.Drivers.Count == 0) continue;
            classNames.Add(ClassLogo.DisplayName(cl0.Name));
            foreach (var d in cl0.Drivers) { cars++; if (d.IRating > 0) irs.Add(d.IRating); }
        }
        string length = s.TotalLaps > 0 ? $"{s.TotalLaps} LAPS" : Duration(s.SessionTimeRemaining);
        var stats = new (string L, string V)[]
        {
            ("LENGTH", length), ("FIELD", $"{cars} CARS"),
            (classNames.Count == 1 ? "1 CLASS" : $"{classNames.Count} CLASSES", classNames.Count == 0 ? "-" : string.Join(" · ", classNames)),
            ("STRENGTH OF FIELD", irs.Count > 0 ? Sof(irs).ToString("N0", CultureInfo.InvariantCulture) : "-"),
        };
        float colW = cw / 2f;
        for (int i = 0; i < stats.Length; i++)
        {
            float k = K(0.7 + i * 0.15, 0.35);
            float x = cl + colW * (i % 2 + 0.5f), y = r.Top + 560 + (i / 2) * 160 + (1 - k) * 22;
            BigStatC(c, x, y, stats[i].L, Tick(stats[i].V, K(0.7 + i * 0.15, 0.9)), a * k, 66, colW - 24);
        }
    }

    // ---------------------------------------------------------------- POLE SITTERS
    void PanelPoles(SKCanvas c, SKRect r, RaceSnapshot s, float a, float cl, float cw, bool right)
    {
        float cx = cl + cw / 2;
        // the headline opens out from the centre
        float hk = K(0.0, 0.45);
        c.Save();
        c.ClipRect(new SKRect(cx - (cw / 2 + 20) * hk, r.Top, cx + (cw / 2 + 20) * hk, r.Bottom));
        Headline(c, "POLE SITTERS", cx, r.Top + 132, cw, 104, a, center: true);
        c.Restore();
        HLineC(c, cx, 90 * K(0.35, 0.35), r.Top + 158, a, Gold.WithAlpha(220), 3);

        const double LightsOn = 0.35, LightStep = 0.2, LightsOut = LightsOn + 5 * LightStep + 0.35;
        float ly = r.Top + 285;
        if (cardLocal < LightsOut + 0.25)
        {
            float outFade = 1 - Lin(LightsOut, 0.12);
            float sp = cw / 5f;
            float hA = Math.Min(1f, K(0.1, 0.25)) * (1 - Lin(LightsOut + 0.05, 0.2));
            float hw = sp * 0.42f, hh = 74f, bulbR = Math.Min(hw, hh) * 0.68f;
            using (var beam = new SKPaint { IsAntialias = true, Color = new SKColor(14, 16, 21, (byte)(235 * a * hA)) })
                c.DrawRoundRect(new SKRect(cl, ly - 9, cl + cw, ly + 9), 5, 5, beam);
            for (int li = 0; li < 5; li++)
            {
                float lx = cl + sp * (li + 0.5f);
                bool lit = cardLocal >= LightsOn + li * LightStep && outFade > 0;
                var pod = new SKRect(lx - hw, ly - hh, lx + hw, ly + hh);
                using (var housing = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, pod.Top), new SKPoint(0, pod.Bottom),
                           new[] { new SKColor(34, 37, 46, (byte)(240 * a * hA)), new SKColor(12, 13, 18, (byte)(240 * a * hA)) }, null, SKShaderTileMode.Clamp) })
                    c.DrawRoundRect(pod, 16, 16, housing);
                if (lit)
                {
                    using (var glow = new SKPaint { IsAntialias = true, Color = new SKColor(255, 40, 40, (byte)(140 * a * outFade)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 24) })
                        c.DrawCircle(lx, ly, bulbR * 1.4f, glow);
                    using (var bulb = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(lx - bulbR * 0.25f, ly - bulbR * 0.3f), bulbR * 1.2f,
                               new[] { new SKColor(255, 170, 160, (byte)(255 * a * outFade)), new SKColor(255, 45, 45, (byte)(255 * a * outFade)), new SKColor(190, 10, 20, (byte)(255 * a * outFade)) },
                               new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp) })
                        c.DrawCircle(lx, ly, bulbR, bulb);
                }
                else using (var off = new SKPaint { IsAntialias = true, Color = new SKColor(62, 18, 22, (byte)(220 * a * hA)) })
                    c.DrawCircle(lx, ly, bulbR, off);
            }
        }
        var poles = new List<(DriverClass Cls, Driver D)>();
        foreach (var cls in s.ClassLeaderboard)
        {
            Driver? best = null;
            foreach (var d in cls.Drivers)
                if (d.PositionInClass > 0 && (best == null || d.PositionInClass < best.PositionInClass)) best = d;
            if (best != null) poles.Add((cls, best));
        }
        poles = poles.Take(3).Reverse().ToList();
        // after lights out the pole sitters take the lights' place, one centred row per class,
        // sliding in from the live side
        for (int i = 0; i < poles.Count; i++)
        {
            var (cls, d) = poles[i];
            float k = Ease((float)Math.Clamp((cardLocal - (LightsOut + 0.05) - i * 0.18) / 0.5, 0, 1));
            if (k <= 0) continue;
            float y = r.Top + 230 + i * 205, x = cx + (1 - k) * 160 * (right ? -1 : 1), ra = a * k;
            var logo = Bmp("logos", "classes", (ClassLogo.Resolve(cls.Name) ?? cls.Name.Replace(" ", "").ToUpperInvariant()) + ".png");
            if (logo != null)
            {
                float h = 46, w = logo.Width * (h / logo.Height);
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * ra)) };
                c.DrawBitmap(logo, new SKRect(x - w / 2, y, x + w / 2, y + h), lp);
            }
            TextC(c, NameFormat.Broadcast(d), x, y + 118, Fonts.CormorantGaramond.Bold, 72, Txt, ra, cw, 26);
            TextC(c, Lap(d.QualifyTime) + "    #" + d.CarNumber, x, y + 160, Fonts.Inter.SemiBold, 28, Txt2, ra, cw);
            if (i < poles.Count - 1) HLineC(c, x, cw / 2 * 0.7f, y + 186, ra);
        }
    }

    // ---------------------------------------------------------------- DRIVERS TO WATCH (2026-11-06)
    void PanelWatch(SKCanvas c, SKRect r, RaceSnapshot s, float a, float cl, float cw, bool right)
    {
        float cx = cl + cw / 2;
        float hk = K(0.0, 0.45);
        c.Save();
        c.ClipRect(new SKRect(cx - (cw / 2 + 20) * hk, r.Top, cx + (cw / 2 + 20) * hk, r.Bottom));
        Headline(c, "DRIVERS TO WATCH", cx, r.Top + 126, cw, 88, a, center: true);
        c.Restore();
        HLineC(c, cx, 90 * K(0.35, 0.35), r.Top + 152, a, Gold.WithAlpha(220), 3);
        var picks = DriversToWatch.For(s);
        for (int i = 0; i < Math.Min(3, picks.Count); i++)
        {
            var p = picks[i];
            float k = Ease((float)Math.Clamp((cardLocal - 0.5 - i * 0.25) / 0.5, 0, 1));
            if (k <= 0) continue;
            float y = r.Top + 200 + i * 196, x = cx + (1 - k) * 160 * (right ? -1 : 1), ra = a * k;
            var cc = SKColor.TryParse(p.ClassColor.StartsWith('#') ? p.ClassColor : "#" + p.ClassColor, out var pc) ? pc : Gold;
            using (var bar = new SKPaint { IsAntialias = true, Color = cc.WithAlpha((byte)(255 * ra)) })
                c.DrawRoundRect(new SKRect(x - 46, y + 4, x + 46, y + 9), 2.5f, 2.5f, bar);
            LabelC(c, (p.Tag + " · " + ClassLogo.DisplayName(p.ClassName)).ToUpperInvariant(), x, y + 40, 19, Gold, ra);
            TextC(c, p.Name + "   #" + p.Number, x, y + 100, Fonts.CormorantGaramond.Bold, 58, Txt, ra, cw, 24);
            TextC(c, p.Value, x, y + 140, Fonts.Inter.Bold, 30, Gold, ra, cw);
            TextC(c, p.Reason, x, y + 174, Fonts.Inter.Medium, 21, Txt2, ra, cw, 14);
            if (i < Math.Min(3, picks.Count) - 1) HLineC(c, x, cw / 2 * 0.7f, y + 190, ra);
        }
    }

    // ---------------------------------------------------------------- A&B PARTNER
    void PanelAB(SKCanvas c, SKRect r, float frameA, float cw)
    {
        float cx = r.MidX;
        float a = frameA * K(0.45, 0.4);
        Headline(c, "OFFICIAL PARTNER", cx, r.Top + 236, cw, 78, a, center: true);
        using (var p = P(Fonts.Inter.SemiBold, 34, Txt2, a))
        {
            p.TextAlign = SKTextAlign.Center;
            FitSize(p, "Insurance Backed Restoration", cw, 16);
            c.DrawText("Insurance Backed Restoration", cx, r.Top + 300, p);
            using (var amp = P(Fonts.CormorantGaramond.Bold, p.TextSize * 1.4f, Gold, a)) { amp.TextAlign = SKTextAlign.Center; c.DrawText("&", cx, r.Top + 352, amp); }
            c.DrawText("General Contracting", cx, r.Top + 402, p);
        }
        var logo = Bmp("logos", "sponsors", "AB_GC_Logo.png");
        if (logo == null) return;
        float sc = Math.Min(cw / logo.Width, 250f / logo.Height);
        float w = logo.Width * sc, h = logo.Height * sc;
        var dst = new SKRect(cx - w / 2, r.Top + 640 - h / 2, cx + w / 2, r.Top + 640 + h / 2);
        const double StampAt = 2.05, StampLen = 0.28;
        float sk = (float)Math.Clamp((cardLocal - StampAt) / StampLen, 0, 1);
        if (sk <= 0) return;
        float fall = sk * sk, scale = 1.8f - 0.8f * fall, logoA = frameA * Math.Min(1f, sk * 2.5f);
        using (var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * logoA)),
                   ImageFilter = SKImageFilter.CreateDropShadow(0, 3 + 14 * (1 - fall), 8 + 14 * (1 - fall), 8 + 14 * (1 - fall), new SKColor(0, 0, 0, (byte)(200 * Math.Min(1f, sk * 2.5f)))) })
        {
            c.Save();
            c.Translate(dst.MidX, dst.MidY); c.Scale(scale); c.Translate(-dst.MidX, -dst.MidY);
            c.DrawBitmap(logo, dst, lp);
            c.Restore();
        }
        float dk = (float)Math.Clamp((cardLocal - StampAt - StampLen) / 0.8, 0, 1);
        if (dk > 0 && dk < 1)
        {
            var rnd = new Random(11);
            for (int i = 0; i < 34; i++)
            {
                float ang = (float)(rnd.NextDouble() * Math.PI), dir = rnd.NextDouble() < 0.5 ? -1 : 1;
                float dist = (60 + (float)rnd.NextDouble() * 200) * Ease(dk);
                float px = dst.MidX + dir * (w * 0.35f + dist * (float)Math.Cos(ang * 0.3));
                float py = dst.Bottom - 10 - dist * 0.35f * (float)Math.Sin(ang);
                float pr = (8 + (float)rnd.NextDouble() * 18) * (0.6f + dk);
                using var dp = new SKPaint { IsAntialias = true, Color = new SKColor(210, 196, 176, (byte)(110 * (1 - dk) * frameA)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8) };
                c.DrawCircle(px, py, pr, dp);
            }
        }
    }

    // ---------------------------------------------------------------- TRACK FACTS
    void PanelTrack(SKCanvas c, SKRect r, RaceSnapshot s, float a, float cl, float cw, bool right)
    {
        float cx = cl + cw / 2;
        var e = s.Extras;
        bool f = IsImperial(s);
        // country flag above the name, both centred
        var flag = string.IsNullOrEmpty(s.TrackCountryCode) ? null : Bmp("flags", s.TrackCountryCode.ToLowerInvariant() + ".png");
        float na = a * K(0.0, 0.4);
        if (flag != null)
        {
            float h = 44, w = flag.Width * (h / flag.Height);
            using var fp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * na)) };
            c.DrawBitmap(flag, new SKRect(cx - w / 2, r.Top + 34, cx + w / 2, r.Top + 34 + h), fp);
        }
        string name = (s.TrackDisplayName ?? "").ToUpperInvariant();
        TextC(c, name, cx, r.Top + 132, Fonts.CormorantGaramond.Bold, 66, Txt, na, cw, 22);
        HLineC(c, cx, cw / 2 * K(0.2, 0.6), r.Top + 156, a);

        // the circuit, drawn big - and breaking out of the panel into the live half
        var map = MapFor(s);
        if (map != null)
        {
            var area = new SKRect(r.Left - 46, r.Top + 190, r.Right + 46, r.Top + 660);   // centred, spilling over both edges
            float sc = Math.Min(area.Width / map.Width, area.Height / map.Height);
            float mw = map.Width * sc, mh = map.Height * sc;
            var dst = new SKRect(area.MidX - mw / 2, area.MidY - mh / 2, area.MidX + mw / 2, area.MidY + mh / 2);
            var path = MapPathFor(s);
            if (path != null)
            {
                using var pth = new SKPath(path);
                pth.Transform(SKMatrix.CreateScaleTranslation(sc, sc, dst.Left, dst.Top));
                pth.Transform(WalkZoom(pth, area));
                using var meas = new SKPathMeasure(pth, true);
                float L = meas.Length, pk = K(0.1, 1.6);
                c.Save(); c.ClipRoundRect(new SKRoundRect(area, 24, 24), antialias: true);
                if (L > 0 && pk > 0)
                {
                    using var seg = new SKPath();
                    meas.GetSegment(0, L * pk, seg, true);
                    using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 14, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                               Color = Gold.WithAlpha((byte)(110 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9) })
                        c.DrawPath(seg, glow);
                    using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                               Color = SKColors.White.WithAlpha((byte)(245 * a)) })
                        c.DrawPath(seg, line);
                    if (pk < 1 && meas.GetPositionAndTangent(L * pk, out var head, out _))
                        using (var halo = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(head, 26,
                                   new[] { new SKColor(255, 244, 214, (byte)(255 * a)), Gold.WithAlpha((byte)(150 * a)), Gold.WithAlpha(0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp) })
                            c.DrawCircle(head, 26, halo);
                }
                c.Restore();
                DrawLapWalk(c, meas, L, a, 1.4f, 44);
                var tlogo = TrackLogoFor(s);
                if (tlogo != null && L > 0)
                {
                    var mt = GridIntro.Matte(tlogo);
                    var img = mt.Feathered ?? tlogo;
                    double sx = 0, sy = 0; int n = 0;
                    for (int i = 0; i < 200; i++) if (meas.GetPosition(L * i / 200f, out var pp)) { sx += pp.X; sy += pp.Y; n++; }
                    var ctr = n > 0 ? new SKPoint((float)(sx / n), (float)(sy / n)) : new SKPoint(dst.MidX, dst.MidY);
                    float lk = K(0.6, 0.6);
                    float ls = Math.Min(mw * 0.5f / img.Width, mh * 0.45f / img.Height) * (1.25f - 0.25f * lk);
                    float lw = img.Width * ls, lh = img.Height * ls;
                    using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * lk * (1 - walkZoomAmt))) };
                    c.DrawBitmap(img, new SKRect(ctr.X - lw / 2, ctr.Y - lh / 2, ctr.X + lw / 2, ctr.Y + lh / 2), lp);
                }
            }
        }
        // three facts along the bottom, centred in their thirds, counting up
        float sa = a * K(0.35, 0.35), tk = K(0.45, 1.1), third = cw / 3f, y = r.Top + 712;
        string place = string.IsNullOrWhiteSpace(e.TrackCity) ? (e.TrackCountry ?? "-") : e.TrackCity;
        BigStatC(c, cl + third * 0.5f, y, "LOCATION", place, sa, 50, third - 16);
        BigStatC(c, cl + third * 1.5f, y, "LENGTH", Tick(TrackLen(e.TrackLength, f), tk), sa, 50, third - 16);
        BigStatC(c, cl + third * 2.5f, y, "TURNS", e.TrackTurns > 0 ? Tick(e.TrackTurns.ToString(CultureInfo.InvariantCulture), tk) : "-", sa, 50, third - 16);
    }

    // ---------------------------------------------------------------- TRACK, FULL WIDTH (2026-10-25)
    // Left: the flag, the circuit's name and its facts - and, once the lap walk starts, the stop it's at: the corner's
    // own themed label (CornerTheme: the Dunlop bridge, PlayStation space, Michelin plate...) with its heritage, and how
    // fast the field goes there. Right: the circuit traces itself, then the camera flies to every corner and straight in
    // lap order, lighting it up, and pulls back out to the whole lap at the end.
    SKPath? lastWalkPath;

    void PanelTrackWide(SKCanvas c, SKRect r, RaceSnapshot s, float a)
    {
        var e = s.Extras;
        bool f = IsImperial(s);
        float colL = r.Left + 70, colW = r.Width * 0.34f, colR = colL + colW, colX = colL + colW / 2;
        var zone = new SKRect(colR + 50, r.Top + 30, r.Right - 40, r.Bottom - 30);

        // ---- the circuit
        var map = MapFor(s);
        var path = MapPathFor(s);
        if (map != null && path != null)
        {
            // 2026-10-25 (Angelo: the map in 3D, "with terrain features but not like a satellite map"): the map is laid out
            // on a ground plane taller than the zone and tilted back in perspective into it; the real ground (TrackTerrain)
            // lies under the line, and the line rides a little above it with a soft gold wall down to its shadow
            var plane = new SKRect(zone.Left, zone.Top - zone.Height * 0.22f, zone.Right, zone.Bottom + zone.Height * 0.12f);
            float pin = zone.Width * 0.17f;
            var persp = TrackTerrain.Perspective(plane, new SKPoint(zone.Left + pin, zone.Top + 10), new SKPoint(zone.Right - pin, zone.Top + 10),
                                                 new SKPoint(zone.Right + zone.Width * 0.06f, zone.Bottom), new SKPoint(zone.Left - zone.Width * 0.06f, zone.Bottom));
            float sc = Math.Min(plane.Width * 0.9f / map.Width, plane.Height * 0.9f / map.Height);
            float mw = map.Width * sc, mh = map.Height * sc;
            var dst = new SKRect(plane.MidX - mw / 2, plane.MidY - mh / 2, plane.MidX + mw / 2, plane.MidY + mh / 2);
            var toPlane = SKMatrix.CreateScaleTranslation(sc, sc, dst.Left, dst.Top);
            using var pth = new SKPath(path);
            pth.Transform(toPlane);
            using var flat = new SKPath(pth);                                         // the unzoomed outline (logo centre)
            lastWalkPath?.Dispose(); lastWalkPath = new SKPath(flat);
            var zoomM = WalkZoom(pth, plane);
            pth.Transform(zoomM);
            using var meas = new SKPathMeasure(pth, true);
            float L = meas.Length, pk = K(0.3, 3.8);
            int mapLayer = c.SaveLayer(zone, null);                                   // faded at its edges below
            var mb = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
            var terrain = mb == null ? null : TrackTerrain.For(mb, Assets.Resolve("maps", mb + ".json"), s.TrackLatitude, s.TrackLongitude, TrackCorners.LengthM(e.TrackLength));
            // 2026-10-25 (Angelo: the intro "should also change theme with each corner or straight"): behind the map, the
            // stop's own backdrop (PlayStation space, Michelin blue, Dunlop graphite, bricks, Mulsanne speed, maker colours...)
            // fading in and out with each stop, like the battle map
            float themeEnv = 0;
            if (lapStops is { Count: > 0 })
            {
                double twT = cardLocal - InS - WalkStartS;
                int siT = twT >= 0 ? (int)(twT / WalkStepS) : -1;
                if (siT >= 0 && siT < lapStops.Count && CornerTheme.For(lapStops[siT].Name) is { } thT)
                {
                    float uT = (float)((twT - siT * WalkStepS) / WalkStepS);
                    themeEnv = Math.Min(Math.Clamp(uT / 0.12f, 0, 1), Math.Clamp((1 - uT) / 0.1f, 0, 1));
                    BattleMap.Backdrop(c, zone, thT, lapStops[siT].Name, a * themeEnv * 0.92f, cardLocal * 1000);
                }
            }
            c.Save(); c.Concat(ref persp);
            if (terrain != null)
            {
                var gm = terrain.ToMap.PostConcat(toPlane).PostConcat(zoomM);
                c.Save(); c.Concat(ref gm);
                using (var gp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * K(0.0, 1.2) * terrain.FadeIn * (1 - 0.65f * themeEnv))) })
                    c.DrawBitmap(terrain.Ground, 0, 0, gp);
                c.Restore();
            }
            // the line's shadow on the ground
            if (L > 0 && pk > 0)
            {
                using var sseg = new SKPath(); meas.GetSegment(0, L * pk, sseg, true);
                using (var shp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 9, StrokeJoin = SKStrokeJoin.Round, Color = new SKColor(0, 0, 0, (byte)(120 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5) })
                    c.DrawPath(sseg, shp);
            }
            c.Restore();
            const float Lift = 14f;
            if (L > 0 && pk > 0)
            {
                // the wall from the shadow up to the line: a few soft gold copies stacked up
                using var wseg = new SKPath(); meas.GetSegment(0, L * pk, wseg, true);
                using var wp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, StrokeJoin = SKStrokeJoin.Round, Color = Gold.WithAlpha((byte)(30 * a)) };
                for (int wi = 1; wi < 7; wi++) { c.Save(); c.Translate(0, -Lift * wi / 7f); c.Concat(ref persp); c.DrawPath(wseg, wp); c.Restore(); }
            }
            c.Save(); c.Translate(0, -Lift); c.Concat(ref persp);
            if (L > 0 && pk > 0)
            {
                using var seg = new SKPath();
                meas.GetSegment(0, L * pk, seg, true);
                using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 16, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                           Color = Gold.WithAlpha((byte)(90 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10) })
                    c.DrawPath(seg, glow);
                using (var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round,
                           Color = SKColors.White.WithAlpha((byte)(245 * a)) })
                    c.DrawPath(seg, line);
                if (pk < 1 && meas.GetPositionAndTangent(L * pk, out var head, out _))
                    using (var halo = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(head, 30,
                               new[] { new SKColor(255, 244, 214, (byte)(255 * a)), Gold.WithAlpha((byte)(150 * a)), Gold.WithAlpha(0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp) })
                        c.DrawCircle(head, 30, halo);
                // start / finish line
                if (meas.GetPositionAndTangent(0, out var sf, out var tan))
                    using (var sfp = new SKPaint { IsAntialias = true, StrokeWidth = 4, Color = new SKColor(255, 255, 255, (byte)(230 * a * K(3.8, 0.6))) })
                        c.DrawLine(sf.X - tan.Y * 16, sf.Y + tan.X * 16, sf.X + tan.Y * 16, sf.Y - tan.X * 16, sfp);
            }
            DrawLapWalk(c, meas, L, a, 1.8f, 44, chip: false);
            c.Restore();
            // the zoomed track melts away at the map's edges instead of being cut off
            using (var mx = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(zone.Left, 0), new SKPoint(zone.Right, 0),
                       new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, terrain != null ? new[] { 0f, 0.2f, 0.85f, 1f } : new[] { 0f, 0.1f, 0.94f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(zone, mx);
            using (var my = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(0, zone.Top), new SKPoint(0, zone.Bottom),
                       new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, terrain != null ? new[] { 0f, 0.22f, 0.86f, 1f } : new[] { 0f, 0.08f, 0.92f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(zone, my);
            c.RestoreToCount(mapLayer);
            // while zoomed in: the whole lap small in the corner of the map, the stop lit on it - you always know where you are
            if (walkZoomAmt > 0.02f && lapStops is { Count: > 0 })
            {
                var fb = flat.Bounds;
                float isc = Math.Min(260f / fb.Width, 170f / fb.Height);
                var ib = new SKRect(zone.Right - 24 - fb.Width * isc, zone.Top + 20, zone.Right - 24, zone.Top + 20 + fb.Height * isc);
                using var ip = new SKPath(flat);
                ip.Transform(SKMatrix.CreateTranslation(-fb.Left, -fb.Top).PostConcat(SKMatrix.CreateScale(isc, isc)).PostConcat(SKMatrix.CreateTranslation(ib.Left, ib.Top)));
                float ia = a * walkZoomAmt;
                // no box of its own: it sits straight on the card, in the card's (the logo's) colour - just a faint hairline
                using (var bg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, (byte)(26 * ia)) })
                    c.DrawRoundRect(new SKRect(ib.Left - 16, ib.Top - 14, ib.Right + 16, ib.Bottom + 14), 12, 12, bg);
                // 2026-10-25 (Angelo: all the maps 3D): the little overview tilts back too, on its own scrap of ground
                var ipM = SKMatrix.CreateTranslation(-fb.Left, -fb.Top).PostConcat(SKMatrix.CreateScale(isc, isc)).PostConcat(SKMatrix.CreateTranslation(ib.Left, ib.Top));
                var iplane = new SKRect(ib.Left, ib.Top - ib.Height * 0.25f, ib.Right, ib.Bottom + ib.Height * 0.05f);
                var ipersp = TrackTerrain.Perspective(iplane, new SKPoint(ib.Left + ib.Width * 0.14f, ib.Top), new SKPoint(ib.Right - ib.Width * 0.14f, ib.Top),
                                                      new SKPoint(ib.Right + ib.Width * 0.04f, ib.Bottom), new SKPoint(ib.Left - ib.Width * 0.04f, ib.Bottom));
                c.Save(); c.Concat(ref ipersp);
                if (terrain != null)
                {
                    var igm = terrain.ToMap.PostConcat(toPlane).PostConcat(ipM);
                    int il0 = c.SaveLayer(new SKRect(ib.Left - 20, ib.Top - 40, ib.Right + 20, ib.Bottom + 16), null);
                    c.ClipRect(new SKRect(ib.Left - 20, ib.Top - 40, ib.Right + 20, ib.Bottom + 16), SKClipOperation.Intersect, true);
                    c.Save(); c.Concat(ref igm);
                    using (var igp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha((byte)(255 * ia * terrain.FadeIn)) })
                        c.DrawBitmap(terrain.Ground, 0, 0, igp);
                    c.Restore();
                    using (var im2 = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateRadialGradient(new SKPoint(ib.MidX, ib.MidY), Math.Min(ib.Width, ib.Height) * 0.75f,
                               new[] { SKColors.White, SKColors.White.WithAlpha(150), SKColors.Transparent }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
                        c.DrawRect(new SKRect(ib.Left - 20, ib.Top - 40, ib.Right + 20, ib.Bottom + 16), im2);
                    c.RestoreToCount(il0);
                }
                using (var il = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f, StrokeJoin = SKStrokeJoin.Round, Color = SKColors.White.WithAlpha((byte)(200 * ia)) })
                    c.DrawPath(ip, il);
                // the track logo stays in the circuit's outline - in the little overview while the camera is in close
                if (TrackLogoFor(s) is { } tl2)
                {
                    var img2 = GridIntro.Matte(tl2).Feathered ?? tl2;
                    float ls2 = Math.Min(ib.Width * 0.42f / img2.Width, ib.Height * 0.4f / img2.Height);
                    float lw2 = img2.Width * ls2, lh2 = img2.Height * ls2;
                    using var lp2 = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * ia)) };
                    c.DrawBitmap(img2, new SKRect(ib.MidX - lw2 / 2, ib.MidY - lh2 / 2, ib.MidX + lw2 / 2, ib.MidY + lh2 / 2), lp2);
                }
                double tw0 = cardLocal - InS - WalkStartS;
                int si = (int)(tw0 / WalkStepS);
                if (si >= 0 && si < lapStops.Count)
                {
                    using var im = new SKPathMeasure(ip, true);
                    var st0 = lapStops[si];
                    float a0 = st0.StartPct, a1 = st0.EndPct; if (a1 < a0) a1 += 1;
                    using var hs = new SKPath();
                    void Add2(float p0, float p1) { using var part = new SKPath(); im.GetSegment(im.Length * p0, im.Length * p1, part, true); hs.AddPath(part); }
                    if (a1 <= 1) Add2(a0, a1); else { Add2(a0, 1); Add2(0, a1 - 1); }
                    using (var hl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 5, StrokeCap = SKStrokeCap.Round, Color = CornerTheme.Accent(st0.Name).WithAlpha((byte)(255 * ia)) })
                        c.DrawPath(hs, hl);
                }
                c.Restore();
            }
            // the track logo in the infield until the camera flies in
            var tlogo = TrackLogoFor(s);
            if (tlogo != null)
            {
                var mt = GridIntro.Matte(tlogo);
                var img = mt.Feathered ?? tlogo;
                var b = flat.Bounds;
                float lk = K(1.4, 1.2);
                // 2026-11-06 (Angelo: "the Watkins Glen logo in the wrong place" on the track intro): the middle of the map's
                // box can be ON the line (Watkins Glen's Boot) - use the opener's roomiest open spot in the infield instead,
                // sized to fit it; the box middle only when the layout has no room inside
                var hole = GridIntro.Hole((mb ?? s.TrackRawName ?? "") + "|card|" + (int)(100f * img.Width / img.Height), path, map.Width, map.Height, (float)img.Width / img.Height);
                var centre = new SKPoint(b.MidX, b.MidY);
                float fitW = b.Width * 0.42f, fitH = b.Height * 0.4f;
                if (hole is SKRect hr && hr.Width > 0)
                {
                    centre = toPlane.MapPoint(hr.MidX, hr.MidY);
                    fitW = Math.Min(fitW, hr.Width * sc * 0.92f); fitH = Math.Min(fitH, hr.Height * sc * 0.92f);
                }
                // the plane is tilted: put the logo where that spot lands on screen, sized by the ground's scale there
                var pc = persp.MapPoint(centre.X, centre.Y); var pr = persp.MapPoint(centre.X + 100, centre.Y);
                float depth = SKPoint.Distance(pc, pr) / 100f;
                float ls = Math.Min(fitW / img.Width, fitH / img.Height) * (1.2f - 0.2f * lk) * depth;
                float lw = img.Width * ls, lh = img.Height * ls;
                using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * lk * (1 - walkZoomAmt))) };
                c.DrawBitmap(img, new SKRect(pc.X - lw / 2, pc.Y - 14 - lh / 2, pc.X + lw / 2, pc.Y - 14 + lh / 2), lp);
            }
        }

        // ---- the divider between the story and the map: a gold hairline fading out at both ends
        using (var dv = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top + 40), new SKPoint(0, r.Bottom - 40),
                   new[] { Gold.WithAlpha(0), Gold.WithAlpha((byte)(110 * a * K(0.3, 0.6))), Gold.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(colR + 26, r.Top + 40, 1.2f, r.Height - 80, dv);

        // ---- the left column: who we are (2026-10-25, Angelo: "more information on it like a short track description")
        float na = a * K(0.0, 0.45);
        float y = r.Top + 64;
        var flag = string.IsNullOrEmpty(s.TrackCountryCode) ? null : Bmp("flags", s.TrackCountryCode.ToLowerInvariant() + ".png");
        string where = string.Join(", ", new[] { e.TrackCity, e.TrackCountry }.Where(z => !string.IsNullOrWhiteSpace(z))).ToUpperInvariant();
        using (var ep = P(Fonts.Inter.Bold, 13, Gold, na))
        {
            float tw = TrackedWidth(ep, where, 3.6f), fw = 0;
            if (flag != null) fw = flag.Width * (18f / flag.Height) + 12;
            float x0 = colL;
            if (flag != null)
                using (var fp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * na)) })
                    c.DrawBitmap(flag, new SKRect(x0, y - 15, x0 + fw - 12, y + 3), fp);
            TextTracked.Draw(c, ep, where, x0 + fw, y, 3.6f);
        }
        string name = (s.TrackDisplayName ?? "").ToUpperInvariant();
        using (var np = P(Fonts.CormorantGaramond.Bold, 50, Txt, na))
        {
            FitSize(np, name, colW, 24);
            c.DrawText(name, colL, y + 54, np);
        }
        using (var rl = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(colL, 0), new SKPoint(colR, 0), new[] { Gold.WithAlpha((byte)(200 * a)), Gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp) })
            c.DrawRect(colL, y + 72, colW * K(0.2, 0.7), 1.4f, rl);

        // a short description of the circuit
        var desc = TrackStory.Description(s.TrackRawName);
        float dy = y + 104;
        if (desc != null)
        {
            using var dp = P(Fonts.Inter.Medium, 16, Txt2, a * K(0.35, 0.5));
            foreach (var line in Wrap(desc, dp, colW))
            {
                c.DrawText(line, colL, dy, dp);
                dy += 24;
            }
        }

        // the lap walk: which stop, a progress strip of every stop, the corner's own design, and what it is
        double tw2 = cardLocal - InS - WalkStartS;
        int n = lapStops?.Count ?? 0;
        int idx = n > 0 && tw2 >= 0 ? (int)(tw2 / WalkStepS) : -1;
        float storyTop = dy + 26;
        if (n > 0)
        {
            // progress: one tick per stop, the current one lit in its own colour (straights drawn longer and thinner)
            float px = colL, pw = colW, tick = pw / n;
            for (int i = 0; i < n; i++)
            {
                var st0 = lapStops![i];
                bool cur = i == idx, done = idx >= 0 && i < idx;
                var col = cur ? CornerTheme.Accent(st0.Name) : done ? new SKColor(255, 255, 255, 150) : new SKColor(255, 255, 255, 40);
                float h = st0.Straight ? 2f : 5f;
                using var tp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(col.Alpha * a * K(0.5, 0.6))) };
                c.DrawRoundRect(new SKRect(px + i * tick + 1.5f, storyTop - h / 2, px + (i + 1) * tick - 1.5f, storyTop + h / 2), 1.5f, 1.5f, tp);
            }
        }
        if (idx >= 0 && idx < n)
        {
            float u = (float)((tw2 - idx * WalkStepS) / WalkStepS);
            float sa = a * Math.Min(Math.Clamp(u / 0.16f, 0, 1), Math.Clamp((1 - u) / 0.12f, 0, 1));
            var st = lapStops![idx];
            var acc = CornerTheme.Accent(st.Name);
            using (var cp = P(Fonts.Inter.Bold, 13, acc, sa))
                TextTracked.Draw(c, cp, (st.Straight ? "STRAIGHT  " : "CORNER  ") + (idx + 1) + "  OF  " + n, colL, storyTop + 32, 3.2f);
            // the corner's own themed label, scaled to fit the column
            string nm = st.Name.ToUpperInvariant();
            var thk = CornerTheme.For(nm)?.Kind;
            float nwid;
            using (var mp = new SKPaint { Typeface = Fonts.CormorantGaramond.Bold, TextSize = 40f }) nwid = mp.MeasureText(nm) + 0.6f * nm.Length;
            float need = thk switch
            {
                CornerTheme.Kind.Dunlop => Math.Max(760f, (nwid + 50f) / 0.605f),
                CornerTheme.Kind.Maker when nm.Contains("FORD") => 330f,          // the Ford script title is compact
                CornerTheme.Kind.Maker when nm.Contains("CORVETTE") => (BrandGlyphs.CanSpell("corvette", nm) ? Math.Min(640f, BrandGlyphs.Measure("corvette", nm, 24f)) : nwid) + 90f,   // chrome letters, badge under
                CornerTheme.Kind.Maker => nwid + 2 * 120f,
                CornerTheme.Kind.PlayStation => nwid + 200f,
                CornerTheme.Kind.Michelin => nwid + 200f,
                _ => nwid + 40f,
            };
            // 2026-10-28: track-pack heritage lines run up to 48 characters - the label also shrinks so its heritage pill
            // (17 px Inter Bold, 2.4 px tracking, 18 px pad each side) stays inside the column. Pack tracks only, so the
            // six hand-made tracks lay out exactly as before.
            if (CornerTheme.HeritageOf(lapTrack, st.Name) is { } her)     // every track (Mexico's Peraltada line ran over too)
                using (var hp = new SKPaint { Typeface = Fonts.Inter.Bold, TextSize = 17f })
                    need = Math.Max(need, hp.MeasureText(her) + 2.4f * Math.Max(0, her.Length - 1) + 36f + 10f);
            float zs = Math.Min(thk == CornerTheme.Kind.Dunlop ? 1.2f : 1.5f, (colW + 30f) / need);
            float labelY = thk == CornerTheme.Kind.Dunlop ? storyTop + 70 : storyTop + 64 + (r.Bottom - 200 - storyTop - 64) * 0.28f;
            int sv = c.Save();
            c.Translate(colX, labelY); c.Scale(zs, zs);
            CornerTheme.DrawLabel(c, 0, 0, st.Straight ? "DOWN" : "INTO", nm, sa, CornerTheme.HeritageOf(lapTrack, st.Name));
            c.RestoreToCount(sv);

            // what it is: where on the lap, what kind of corner (or how long the straight is), and how fast they go there
            double lenM = TrackCorners.LengthM(e.TrackLength);
            float s0 = st.StartPct, s1 = st.EndPct; if (s1 < s0) s1 += 1;
            var cells = new List<(string L, string V)>();
            if (lenM > 0)
            {
                double atKm = (s0 * lenM) / 1000.0;
                cells.Add(("ON THE LAP", f ? (atKm / 1.609344).ToString("0.0", CultureInfo.InvariantCulture) + " MI" : atKm.ToString("0.0", CultureInfo.InvariantCulture) + " KM"));
                if (st.Straight)
                {
                    double km = (s1 - s0) * lenM / 1000.0;
                    cells.Add(("LENGTH", f ? (km / 1.609344).ToString("0.0", CultureInfo.InvariantCulture) + " MI" : km.ToString("0.0", CultureInfo.InvariantCulture) + " KM"));
                }
                else cells.Add(("TYPE", CornerKind(st.Name, st.StartPct, st.EndPct)));
            }
            float? kmh = TrackSpeeds.Of(lapCls, st.Name, st.Straight);
            if (kmh is { } v)
                cells.Add((st.Straight ? "TOP SPEED" : "MIN SPEED", (f ? Math.Round(v / 1.609344f) : Math.Round(v)).ToString(CultureInfo.InvariantCulture) + (f ? " MPH" : " KM/H")));
            float cy = r.Bottom - 186, cw2 = colW / Math.Max(1, cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                float x0 = colL + i * cw2;
                using (var lp = P(Fonts.Inter.Bold, 13, Txt3, sa)) TextTracked.Draw(c, lp, cells[i].L, x0, cy, 2.2f);   // #340: was 11
                // 2026-11-06 (#926): "RIGHT-HANDER" ran into "42 MPH" - shrink a value to fit its own column
                using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 40, Txt, sa))
                {
                    float room = cw2 - 30f, wv = vp.MeasureText(cells[i].V);
                    if (wv > room && wv > 0) vp.TextSize = Math.Max(20f, 40f * room / wv);
                    c.DrawText(cells[i].V, x0, cy + 42, vp);
                }
                if (i > 0) using (var sep = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(30 * sa)) }) c.DrawRect(x0 - 14, cy - 12, 1, 58, sep);
            }
        }

        // the facts along the bottom, always there
        float fy = r.Bottom - 56, fa = a * K(0.35, 0.35), tk = K(0.45, 1.1);
        using (var fl = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(28 * fa)) }) c.DrawRect(colL, fy - 66, colW, 1, fl);
        var facts = new List<(string, string)> { ("LENGTH", Tick(TrackLen(e.TrackLength, f), tk)), ("TURNS", e.TrackTurns > 0 ? Tick(e.TrackTurns.ToString(CultureInfo.InvariantCulture), tk) : "-") };
        if (TrackStory.FirstRace(s.TrackRawName) is { } fr) facts.Add(("FIRST RACE", fr));
        if (n > 0) facts.Add(("NAMED STOPS", n.ToString(CultureInfo.InvariantCulture)));
        float fw2 = colW / facts.Count;
        for (int i = 0; i < facts.Count; i++)
        {
            float x0 = colL + i * fw2;
            using (var lp = P(Fonts.Inter.Bold, 13, Txt3, fa)) TextTracked.Draw(c, lp, facts[i].Item1, x0, fy - 34, 2.2f);   // #340: was 11
            using (var vp = P(Fonts.Inter.Bold, 26, Txt, fa)) c.DrawText(facts[i].Item2, x0, fy, vp);
        }
    }

    /// <summary>What kind of corner a stretch is, from the shape of the outline: hairpin, right / left-hander, chicane,
    /// esses, kink - "right" and "left" as the driver sees it.</summary>
    string CornerKind(string name, float a0, float a1)
    {
        if (name.Contains("Chicane", StringComparison.OrdinalIgnoreCase)) return "CHICANE";
        if (name.Contains("Esses", StringComparison.OrdinalIgnoreCase) || name.Contains("Curves", StringComparison.OrdinalIgnoreCase)) return "ESSES";
        var path = lastWalkPath;
        if (path == null) return "CORNER";
        using var m = new SKPathMeasure(path, true);
        float L = m.Length; if (L <= 0) return "CORNER";
        if (a1 < a0) a1 += 1;
        double sum = 0, total = 0; SKPoint? prev = null;
        for (int i = 0; i <= 24; i++)
        {
            float p = a0 + (a1 - a0) * i / 24f; p -= MathF.Floor(p);
            if (!m.GetPositionAndTangent(L * p, out _, out var t)) continue;
            if (prev is { } q)
            {
                double ang = Math.Atan2(q.X * t.Y - q.Y * t.X, q.X * t.X + q.Y * t.Y);
                sum += ang; total += Math.Abs(ang);
            }
            prev = t;
        }
        double deg = Math.Abs(sum) * 180 / Math.PI, tot = total * 180 / Math.PI;
        if (tot > 140 && deg < 50) return "ESSES";
        if (deg < 22) return "KINK";
        string side = sum > 0 ? "RIGHT" : "LEFT";             // screen y points down: a clockwise turn is a right-hander
        return deg > 140 ? side + " HAIRPIN" : side + "-HANDER";
    }

    static IEnumerable<string> Wrap(string text, SKPaint p, float maxW)
    {
        var words = text.Split(' ');
        string line = "";
        foreach (var w in words)
        {
            string t = line.Length == 0 ? w : line + " " + w;
            if (p.MeasureText(t) > maxW && line.Length > 0) { yield return line; line = w; }
            else line = t;
        }
        if (line.Length > 0) yield return line;
    }

    // ---------------------------------------------------------------- DRIVER TO WATCH
    void PanelDriver(SKCanvas c, SKRect r, SKPath shape, RaceSnapshot s, float a, float cl, float cw, bool right)
    {
        float cx = cl + cw / 2;
        var d = s.Player;
        // the car fills the top of the panel, rushing in from the live side
        // 2026-10-25 (Angelo: "the driver to watch is still that BMW stock photo"): our own car first (sim front photo /
        // iRacing render with the livery), sitting whole in the panel; a press photo only when there's nothing of ours
        var ownCar = CarBackdrop(null, d);
        bool cut = ownCar != null;
        var car = ownCar ?? CarPanelBackdrop(d?.CarName) ?? CarArt.Fallback(d?.CarName, d?.CarBrand, d?.ClassName, d?.ClassColor);   // user picture / drawn panel (2026-10-24)
        var area = new SKRect(r.Left, r.Top, r.Right, r.Top + r.Height * 0.52f);
        if (car != null)
        {
            float sc = cut ? Math.Min(area.Width * 0.86f / car.Width, area.Height * 0.86f / car.Height) : Math.Max(area.Width / car.Width, area.Height / car.Height);
            float w = car.Width * sc, h = car.Height * sc;
            float slideK = 1 - K(0.1, 0.6);
            float slide = slideK * 360 * (right ? -1 : 1);
            var dst = cut ? new SKRect(area.MidX - w / 2 + slide, area.Bottom - h - 30, area.MidX + w / 2 + slide, area.Bottom - 30)
                          : new SKRect(area.MidX - w / 2 + slide, area.MidY - h / 2, area.MidX + w / 2 + slide, area.MidY + h / 2);
            c.Save();
            c.ClipPath(shape, antialias: true);
            c.ClipRect(area);
            for (int gi = 3; gi >= 1 && slideK > 0.02f; gi--)
                using (var gp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium, Color = SKColors.White.WithAlpha((byte)(60 * a * slideK / gi)) })
                    c.DrawBitmap(car, new SKRect(dst.Left + gi * 50 * slideK * (right ? -1 : 1), dst.Top, dst.Right + gi * 50 * slideK * (right ? -1 : 1), dst.Bottom), gp);
            using (var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * (1 - 0.4f * slideK))) })
                c.DrawBitmap(car, dst, ip);
            using (var fade = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, area.Bottom - 160), new SKPoint(0, area.Bottom),
                       new[] { Glass.WithAlpha(0), Glass.WithAlpha((byte)(255 * a)) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(area, fade);
            c.Restore();
            float flash = 1 - K(0.45, 0.3);
            if (flash > 0 && cardLocal > 0.4) using (var fp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(150 * flash * a)) }) c.DrawPath(shape, fp);
        }
        if (d == null) return;
        float y0 = area.Bottom;
        // the maker badge sits on the seam between the photo and the info, centred
        var slug = ManufacturerBrand.Resolve(d.CarBrand ?? "");
        var logo = slug != null ? Bmp("logos", "manufacturers", slug + ".png") : null;
        if (logo != null)
        {
            float lk = K(0.5, 0.4);
            float sc = Math.Min(120f / logo.Width, 96f / logo.Height) * (0.8f + 0.2f * lk), w = logo.Width * sc, h = logo.Height * sc;
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * lk)),
                ImageFilter = SKImageFilter.CreateDropShadow(0, 3, 8, 8, new SKColor(0, 0, 0, (byte)(190 * a))) };
            c.DrawBitmap(logo, new SKRect(cx - w / 2, y0 - h * 0.62f, cx + w / 2, y0 + h * 0.38f), lp);
        }
        LabelC(c, "DRIVER TO WATCH", cx, y0 + 76, 22, PlayerBlue, a * K(0.3, 0.4));
        TextC(c, NameFormat.Broadcast(d), cx, y0 + 156, Fonts.CormorantGaramond.Bold, 86, Txt, a * K(0.35, 0.4), cw, 28);
        TextC(c, (d.CarName ?? "").ToUpperInvariant(), cx, y0 + 194, Fonts.Inter.SemiBold, 26, Txt2, a * K(0.4, 0.4), cw);
        HLineC(c, cx, cw / 2 * K(0.45, 0.5), y0 + 216, a);
        float colW = cw / 4f;
        float S(int i) => a * K(0.6 + i * 0.12, 0.3);
        string ir = d.IRating > 0 ? d.IRating.ToString("N0", CultureInfo.InvariantCulture) : "-";
        float sy = y0 + 240;
        BigStatC(c, cl + colW * 0.5f, sy, "CAR", "#" + d.CarNumber, S(0), 54, colW - 12);
        BigStatC(c, cl + colW * 1.5f, sy, "STARTS", d.PositionInClass > 0 ? $"P{d.PositionInClass}" : "-", S(1), 54, colW - 12, d.ClassName?.ToUpperInvariant());
        BigStatC(c, cl + colW * 2.5f, sy, "IRATING", Tick(ir, K(0.8, 1.4)), S(2), 54, colW - 12);
        BigStatC(c, cl + colW * 3.5f, sy, "LICENCE", string.IsNullOrEmpty(d.LicenceString) ? "-" : d.LicenceString, S(3), 54, colW - 12);
    }

    /// <summary>The full-size car picture for the panel (car_&lt;name&gt;_panel.png), else the strip version.</summary>
    SKBitmap? CarPanelBackdrop(string? carName)
    {
        if (string.IsNullOrWhiteSpace(carName)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (char ch in carName.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        string slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "_+", "_").Trim('_');
        var found = CarPhotos.Find(carName);   // press photo matched by name (2026-10-20)
        if (found != null) return Bmp("logos", "sponsors", System.IO.Path.GetFileName(found));
        return Bmp("logos", "sponsors", "car_" + slug + "_panel.png") ?? CarBackdrop(carName);
    }

    // ---------------------------------------------------------------- STORMEXHIBIT PARTNER
    void PanelStorm(SKCanvas c, SKRect r, SKPath shape, float a, float cw)
    {
        float cx = r.MidX;
        float wmSize = 92;
        while (wmSize > 30 && WordmarkWidth(wmSize) > cw) wmSize -= 1;
        float w = WordmarkWidth(wmSize), lk = K(0.5, 0.55);
        if (lk > 0) StormWordmark(c, cx - w / 2 + (1 - lk) * 200, r.Top + r.Height * 0.36f, wmSize, a * lk);
        float ta = a * K(0.3, 0.4);
        using (var p = P(Fonts.CormorantGaramond.Bold, 66, Txt, ta))
        {
            p.TextAlign = SKTextAlign.Center; FitSize(p, "Recorded weather history", cw, 22);
            c.DrawText("Recorded weather history", cx, r.Top + r.Height * 0.62f, p);
            p.Color = StormLight.WithAlpha((byte)(255 * ta));
            c.DrawText("for any US address", cx, r.Top + r.Height * 0.62f + p.TextSize * 1.15f, p);
        }
        // the strike
        float boltA = 1 - Lin(0.05, 0.5);
        if (boltA <= 0) return;
        c.Save();
        c.ClipPath(shape, antialias: true);
        float flash = 1 - Lin(0.0, 0.2);
        if (flash > 0) using (var fp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(230 * flash * a)) }) c.DrawRect(r, fp);
        var rnd = new Random(5 + (int)(cardLocal * 24) % 3);
        using var bolt = new SKPath();
        float bx = cx + 40, by = r.Top - 4;
        bolt.MoveTo(bx, by);
        while (by < r.Bottom + 4)
        {
            by += 18 + (float)rnd.NextDouble() * 30;
            bx += ((float)rnd.NextDouble() - 0.5f) * 80;
            bolt.LineTo(bx, by);
            if (rnd.NextDouble() < 0.16)
            {
                using var fork = new SKPath(); fork.MoveTo(bx, by);
                fork.LineTo(bx + ((float)rnd.NextDouble() - 0.5f) * 160, by + 30 + (float)rnd.NextDouble() * 40);
                using var fpnt = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(220, 236, 255, (byte)(200 * boltA * a)) };
                c.DrawPath(fork, fpnt);
            }
        }
        using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 24, StrokeJoin = SKStrokeJoin.Round,
                   Color = StormLight.WithAlpha((byte)(160 * boltA * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
            c.DrawPath(bolt, glow);
        using (var core = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, StrokeJoin = SKStrokeJoin.Round,
                   Color = new SKColor(240, 248, 255, (byte)(255 * boltA * a)) })
            c.DrawPath(bolt, core);
        c.Restore();
    }
}
