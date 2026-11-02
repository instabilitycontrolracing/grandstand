using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>Who is fighting (fixed while the radar is up).</summary>
/// <param name="Title">"BATTLE FOR P3"</param>
/// <param name="ClassName">"GTP" (printed after the title)</param>
/// <param name="ClassColor">class colour hex (line colour; black classes read as ivory)</param>
public sealed record BattleRadarModel(string Title, string ClassName, string ClassColor,
                                      string AheadNum, string AheadName, string BehindNum, string BehindName, float LapTimeSec);

/// <summary>
/// V3 (2026-10-27): the battle radar - the gap between two fighting cars and where it is going. Top-left under the
/// session bar (the tower's lane is free during a battle).
/// <para>Base: glass card, "BATTLE FOR P3 · GTP", the two cars (number chips, names), the gap now in the battle card's
/// Cormorant numerals, a trend line of the gap over the last four minutes with the 1-second attack zone shaded, and
/// "CLOSING 0.31 s / LAP" or "PULLING AWAY". Elevated: adds a proximity strip (the two cars as chevrons on a distance
/// scale in metres, a heat glow inside one second) and projects the trend forward - a dashed line to the catch point
/// with "CATCH IN ~3 LAPS".</para>
/// <para>Feed it with <see cref="Feed"/> (sim time, gap) every tick; it keeps one sample a second in a fixed ring
/// (no allocation) and fits the trend over the last 60 s.</para>
/// </summary>
public sealed class BattleRadar : VpGraphic
{
    public const float X = 40f, Y = 172f;
    const int Cap = 240;                  // one sample a second -> four minutes
    const double InMs = 480, OutMs = 380;

    readonly double[] ts = new double[Cap], gs = new double[Cap];
    int head, count;
    double lastSampleT = double.NegativeInfinity;
    double gapNow = double.NaN, slopePerSec;   // + = gap growing (pulling away)

    BattleRadarModel? model;
    double shownAt = -1, hideAt = double.MaxValue;
    VpLayer? layer;
    bool layerElevated;
    float cw, ch;
    SKRect graph, strip;

    readonly SKPaint line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.4f, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint glowLine = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8f, StrokeJoin = SKStrokeJoin.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f) };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint dash = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, PathEffect = SKPathEffect.CreateDash(new[] { 6f, 5f }, 0) };
    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint big = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 64f, TextAlign = SKTextAlign.Right };
    readonly SKPaint unit = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 12f };
    readonly SKPaint trend = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 19f };
    readonly SKPaint small = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 10.5f };
    readonly SKPaint heat = new() { IsAntialias = true };
    readonly SKPath path = new(), area = new(), proj = new(), chev = new();
    readonly ShaderSlot areaShader = new(), heatShader = new();

    public bool Showing => model != null;

    public void Show(BattleRadarModel m, double nowMs)
    {
        bool same = model != null && model.AheadNum == m.AheadNum && model.BehindNum == m.BehindNum;
        model = m;
        if (!same) { shownAt = nowMs; head = 0; count = 0; lastSampleT = double.NegativeInfinity; gapNow = double.NaN; }
        hideAt = double.MaxValue;
        Rebuild();
    }

    public override void Hide(double nowMs) { if (model != null && hideAt == double.MaxValue) hideAt = Math.Max(nowMs, shownAt + InMs); }

    public override bool Active(double nowMs) => model != null && nowMs < hideAt + OutMs;

    /// <summary>The gap (seconds, behind car to the car ahead) at sim time <paramref name="simT"/> (seconds).</summary>
    public void Feed(double simT, double gap)
    {
        if (double.IsNaN(gap) || gap < 0 || gap > 60) return;
        gapNow = double.IsNaN(gapNow) ? gap : gapNow + (gap - gapNow) * 0.25;
        if (simT < lastSampleT) { head = 0; count = 0; }   // replay rewound
        if (simT - lastSampleT < 1.0) return;
        lastSampleT = simT;
        ts[head] = simT; gs[head] = gap;
        head = (head + 1) % Cap; if (count < Cap) count++;
        // least squares over the last 60 s
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
        for (int i = 0; i < count; i++)
        {
            int k = (head - 1 - i + Cap) % Cap;
            double dt = ts[k] - simT;
            if (dt < -60) break;
            sx += dt; sy += gs[k]; sxx += dt * dt; sxy += dt * gs[k]; n++;
        }
        double den = n * sxx - sx * sx;
        slopePerSec = n >= 6 && Math.Abs(den) > 1e-9 ? (n * sxy - sx * sy) / den : 0;
    }

    const float HistFrac = 0.70f;
    const double ForecastSec = 900;   // the forecast zone shows the next 15 minutes   // elevated: history on the left, the forecast on the right

    double PerLap => slopePerSec * Math.Max(30f, model?.LapTimeSec ?? 100f);

    void Rebuild()
    {
        layer?.Dispose();
        var m = model!;
        layerElevated = Elevated;
        cw = 440f; ch = Elevated ? 318f : 214f;
        var cls = Vp.ClassLine(m.ClassColor);
        float gTop = Elevated ? 186f : 104f;
        graph = new SKRect(22, gTop, cw - 22, ch - 30);
        strip = new SKRect(22, 122, cw - 22, 150);
        layer = VpLayer.Record(new SKRect(-30, -20, cw + 30, ch + 40), c =>
        {
            using var shape = Vp.Slant(new SKRect(0, 0, cw, ch), 0f);
            Vp.GlassPanel(c, shape, cls);
            using (var sp = new SKPaint { Color = cls }) c.DrawRect(new SKRect(0, 0, 4, ch), sp);
            float tw = VpText.Draw(c, m.Title.ToUpperInvariant(), 22, 30, Fonts.Inter.Bold, 11.5f, Vp.Ivory, 2.6f);
            using (var d = new SKPaint { IsAntialias = true, Color = Vp.Txt3 }) c.DrawCircle(22 + tw + 10, 26, 1.6f, d);
            VpText.Draw(c, m.ClassName.ToUpperInvariant(), 22 + tw + 20, 30, Fonts.Inter.Bold, 11.5f, cls, 2.6f);
            VpText.Draw(c, "GAP TREND", cw - 22, 30, Fonts.Inter.SemiBold, 10f, Vp.Txt3, 2.2f, 1);
            // the two cars: ahead on top, behind under it
            float y1 = 56, y2 = 80;
            float nw = Math.Max(VpText.Width(m.AheadNum, Fonts.Inter.Bold, 12f), VpText.Width(m.BehindNum, Fonts.Inter.Bold, 12f)) + 12f;
            Chip(c, m.AheadNum, 22, y1, nw); Chip(c, m.BehindNum, 22, y2, nw);
            float nx = 22 + nw + 10;
            float maxName = cw - 22 - 150 - nx;
            VpText.Draw(c, Fit(m.AheadName, maxName), nx, y1 + 5, Fonts.BarlowCondensed.Bold, 19f, Vp.Ivory, 0.6f);
            VpText.Draw(c, Fit(m.BehindName, maxName), nx, y2 + 5, Fonts.BarlowCondensed.Bold, 19f, Vp.Txt2, 0.6f);
            VpText.Draw(c, "GAP", cw - 22, 52, Fonts.Inter.SemiBold, 9.5f, Vp.Txt3, 2.2f, 1);
            // graph frame: attack zone band + 0.5 s gridlines are drawn live (they rescale); a baseline here
            using (var bl = new SKPaint { Color = Vp.Ivory.WithAlpha(40) }) c.DrawRect(new SKRect(graph.Left, graph.Bottom, graph.Right, graph.Bottom + 1), bl);
            VpText.Draw(c, "-4 MIN", graph.Left + 26, ch - 12, Fonts.Inter.SemiBold, 9f, Vp.Txt3, 1.6f);
            float nowX = Elevated ? graph.Left + graph.Width * HistFrac : graph.Right;
            VpText.Draw(c, "NOW", nowX, ch - 12, Fonts.Inter.SemiBold, 9f, Vp.Txt3, 1.6f, Elevated ? 0 : 1);
            if (Elevated)
            {
                VpText.Draw(c, "+15 MIN", graph.Right, ch - 12, Fonts.Inter.SemiBold, 9f, Vp.Amber.WithAlpha(190), 1.6f, 1);
                VpText.Draw(c, "ON TRACK", strip.Left, strip.Top - 2, Fonts.Inter.SemiBold, 9f, Vp.Txt3, 2f);
                using var tick = new SKPaint { Color = Vp.Ivory.WithAlpha(30) };
                c.DrawRect(new SKRect(strip.Left, strip.MidY + 6, strip.Right, strip.MidY + 7), tick);
                for (int i = 0; i <= 4; i++)
                {
                    float x = strip.Right - 30 - i * (strip.Width - 60) / 4f;
                    c.DrawRect(new SKRect(x, strip.MidY + 3, x + 1, strip.MidY + 10), tick);
                }
            }
        });
    }

    static string Fit(string s, float maxW)
    {
        s = s.ToUpperInvariant();
        while (s.Length > 3 && VpText.Width(s, Fonts.BarlowCondensed.Bold, 19f, 0.6f) > maxW) s = s[..^2] + "…";
        return s;
    }

    static void Chip(SKCanvas c, string num, float x, float midY, float w)
    {
        var r = new SKRect(x, midY - 9, x + w, midY + 9);
        using (var p = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(r, 2, 2, p);
        VpText.Draw(c, num, r.MidX, midY + 4.5f, Fonts.Inter.Bold, 12f, new SKColor(8, 10, 16), 0, 0);
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        if (m == null || layer == null) return;
        if (layerElevated != Elevated) Rebuild();
        if (nowMs >= hideAt + OutMs) { model = null; return; }
        double t = nowMs - shownAt;
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, InMs)), outK = Vp.EaseIn(Vp.Seg(nowMs, hideAt, hideAt + OutMs));
        float a = inK * (1f - outK);
        if (a <= 0.002f) return;
        int save = c.Save();
        c.Translate(X - (1f - inK) * 40f - outK * 40f, Y);
        layer.Draw(c, 0, 0, a);

        var cls = Vp.ClassLine(m.ClassColor);
        double pl = PerLap;
        bool closing = pl < -0.05, away = pl > 0.05;
        var tcol = closing ? Vp.Amber : away ? Vp.StormPale : Vp.Txt2;

        // gap now
        if (!double.IsNaN(gapNow))
        {
            big.Color = Vp.Ivory.WithA(a);
            float gx = cw - 40;
            VpNum.Draw(c, gapNow, 2, gx, 88, big);
            unit.Color = Vp.Txt3.WithA(a);
            TextBlobs.Draw(c, "s", gx + 4, 88, unit);
        }
        // trend words
        trend.Color = tcol.WithA(a);
        string word = closing ? "CLOSING" : away ? "PULLING AWAY" : "HOLDING STEADY";
        float ty = Elevated ? 148f - 0f : 0f;
        if (!Elevated)
        {
            // base: the trend line sits right under the names (inside the graph area's top-left)
            TextBlobs.Draw(c, word, graph.Left, graph.Top + 18, trend);
            if (closing || away)
            {
                small.Color = Vp.Txt2.WithA(a);
                float ww = trend.MeasureText(word);
                TextBlobs.Draw(c, VpStr.F2(Math.Abs(pl)), graph.Left + ww + 10, graph.Top + 17, small);
                TextBlobs.Draw(c, "S / LAP", graph.Left + ww + 10 + small.MeasureText(VpStr.F2(Math.Abs(pl))) + 5, graph.Top + 17, small);
            }
        }

        DrawGraph(c, a, tcol, cls, t);
        if (Elevated) DrawProximity(c, m, a, cls, closing, nowMs);
        if (Elevated)
        {
            TextBlobs.Draw(c, word, graph.Left, graph.Top - 6, trend);
            float ww = trend.MeasureText(word);
            small.Color = Vp.Txt2.WithA(a);
            if (closing || away)
            {
                string v = VpStr.F2(Math.Abs(pl));
                TextBlobs.Draw(c, v, graph.Left + ww + 10, graph.Top - 7, small);
                TextBlobs.Draw(c, "S / LAP", graph.Left + ww + 10 + small.MeasureText(v) + 5, graph.Top - 7, small);
            }
            if (closing && !double.IsNaN(gapNow) && gapNow > 0.3)
            {
                double laps = (gapNow - 0.3) / -pl;
                if (laps < 30)
                {
                    string lt = VpStr.Int((long)Math.Ceiling(laps));
                    small.Color = Vp.Amber.WithA(a);
                    float rx = graph.Right;
                    float w3 = small.MeasureText("LAPS"), w2 = small.MeasureText(lt), w1 = small.MeasureText("CATCH IN ~");
                    TextBlobs.Draw(c, "LAPS", rx - w3, graph.Top - 7, small);
                    TextBlobs.Draw(c, lt, rx - w3 - 5 - w2, graph.Top - 7, small);
                    TextBlobs.Draw(c, "CATCH IN ~", rx - w3 - 5 - w2 - w1, graph.Top - 7, small);
                }
            }
        }
        c.RestoreToCount(save);
    }

    void DrawGraph(SKCanvas c, float a, SKColor tcol, SKColor cls, double t)
    {
        var g = graph;
        float top = g.Top + (Elevated ? 6 : 26);
        var full = new SKRect(g.Left + 26, top, g.Right, g.Bottom);   // the left 26 px carry the gap values
        var plot = Elevated ? new SKRect(full.Left, top, g.Left + g.Width * HistFrac, g.Bottom) : full;
        if (count < 2) return;
        double newest = ts[(head - 1 + Cap) % Cap];
        double lo = double.MaxValue, hi = 0;
        for (int i = 0; i < count; i++) { double v = gs[(head - 1 - i + Cap) % Cap]; lo = Math.Min(lo, v); hi = Math.Max(hi, v); }
        if (Elevated && slopePerSec < 0) lo = Math.Min(lo, 0.3);
        double minG = Math.Max(0, Math.Floor((lo - 0.15) * 2) / 2), maxG = Math.Ceiling((hi + 0.15) * 2) / 2;
        if (maxG - minG < 1.0) maxG = minG + 1.0;
        float Y(double gap) => plot.Bottom - (float)((gap - minG) / (maxG - minG)) * plot.Height;
        float Xs(double tt) => plot.Right - (float)((newest - tt) / Cap) * plot.Width;

        // attack zone (< 1 s) and half-second gridlines with their values
        fill.Shader = null;
        if (minG < 1.0)
        {
            fill.Color = Vp.Amber.WithA(0.08f * a);
            c.DrawRect(new SKRect(full.Left, Y(Math.Min(1.0, maxG)), full.Right, full.Bottom), fill);
        }
        fill.Color = Vp.Ivory.WithA(0.07f * a);
        small.Color = Vp.Txt3.WithA(0.9f * a);
        for (double gl = minG + 0.5; gl < maxG - 0.01; gl += 0.5)
        {
            c.DrawRect(new SKRect(full.Left, Y(gl), full.Right, Y(gl) + 1), fill);
            TextBlobs.Draw(c, VpStr.F1(gl), g.Left, Y(gl) + 4, small);
        }
        if (Elevated)
        {
            fill.Color = Vp.Ivory.WithA(0.18f * a);
            c.DrawRect(new SKRect(plot.Right, full.Top - 4, plot.Right + 1, full.Bottom), fill);
        }

        // the gap line (draws in left to right as the card arrives)
        float reveal = Vp.EaseIo(Vp.Seg(t, 150, 1100));
        path.Reset(); area.Reset();
        bool first = true; float lastX = 0, lastY = 0;
        for (int i = count - 1; i >= 0; i--)
        {
            int k = (head - 1 - i + Cap) % Cap;
            float x = Xs(ts[k]), y = Y(gs[k]);
            if (x < plot.Left) continue;
            if (x > plot.Left + plot.Width * reveal + 0.5f) break;
            if (first) { path.MoveTo(x, y); area.MoveTo(x, plot.Bottom); area.LineTo(x, y); first = false; }
            else { path.LineTo(x, y); area.LineTo(x, y); }
            lastX = x; lastY = y;
        }
        if (first) return;
        area.LineTo(lastX, plot.Bottom); area.Close();
        fill.Color = SKColors.White.WithA(a);
        fill.Shader = areaShader.Linear(new SKPoint(0, plot.Top), new SKPoint(0, plot.Bottom), [tcol.WithAlpha(70), tcol.WithAlpha(0)], default);
        c.DrawPath(area, fill);
        fill.Shader = null;
        glowLine.Color = tcol.WithA(0.45f * a);
        c.DrawPath(path, glowLine);
        line.Color = tcol.WithA(a);
        c.DrawPath(path, line);
        flat.Color = Vp.Ivory.WithA(a);
        c.DrawCircle(lastX, lastY, 4f, flat);

        // elevated: project the trend forward to the catch point
        if (Elevated && reveal >= 1f && slopePerSec < -0.0005 && !double.IsNaN(gapNow))
        {
            // the line runs to the catch point (or out of the forecast zone)
            double secsToCatch = (gapNow - 0.3) / -slopePerSec;
            // the forecast zone is compressed: its width covers ForecastSec (the history covers 4 minutes)
            double room = ForecastSec;
            double span = Math.Min(secsToCatch, room);
            float px = plot.Right + (float)(span / ForecastSec) * (full.Right - plot.Right);
            double gEnd = Math.Max(0.3, gapNow + slopePerSec * span);
            float k = Vp.EaseIo(Vp.Seg(t, 1100, 1700));
            proj.Reset(); proj.MoveTo(lastX, lastY); proj.LineTo(lastX + (px - lastX) * k, lastY + (Y(gEnd) - lastY) * k);
            dash.Color = Vp.Amber.WithA(0.9f * a);
            c.DrawPath(proj, dash);
            if (k >= 1f && secsToCatch <= room)
            {
                // catch marker: a crosshair where the lines meet
                float cy = Y(gEnd);
                line.Color = Vp.Amber.WithA(a); line.StrokeWidth = 1.6f;
                c.DrawCircle(px, cy, 6f, line);
                c.DrawLine(px - 10, cy, px - 4, cy, line); c.DrawLine(px + 4, cy, px + 10, cy, line);
                c.DrawLine(px, cy - 10, px, cy - 4, line); c.DrawLine(px, cy + 4, px, cy + 10, line);
                line.StrokeWidth = 2.4f;
            }
        }
    }

    void DrawProximity(SKCanvas c, BattleRadarModel m, float a, SKColor cls, bool closing, double nowMs)
    {
        if (double.IsNaN(gapNow)) return;
        var s = strip;
        // distance in metres at the lap's average speed
        double lapM = 13626;   // overwritten below when a lap length is known
        double metres = gapNow * (LapMetres > 0 ? LapMetres : lapM) / Math.Max(30f, m.LapTimeSec);
        const double Range = 200;   // metres shown
        float xa = s.Right - 30, xb = xa - (float)(Math.Min(metres, Range) / Range) * (s.Width - 60);
        float ly = s.MidY + 6;
        // heat inside one second
        float hk = (float)Math.Clamp(1.2 - gapNow, 0, 1);
        if (hk > 0)
        {
            heat.Shader = heatShader.Linear(new SKPoint(xb, 0), new SKPoint(xa, 0), [Vp.Amber.WithAlpha(0), Vp.Amber.WithAlpha(110), Vp.Amber.WithAlpha(0)], default);
            heat.Color = SKColors.White.WithA(a * hk * (0.75f + 0.25f * MathF.Sin((float)(nowMs / 160.0))));
            c.DrawRect(new SKRect(xb - 20, s.Top + 4, xa + 20, s.Bottom - 4), heat);
        }
        // the span between them
        flat.Color = Vp.Ivory.WithA(0.45f * a);
        c.DrawRect(new SKRect(xb, ly - 1, xa, ly + 1), flat);
        Chevron(c, xa, ly, Vp.Ivory.WithA(a));
        Chevron(c, xb, ly, (closing ? Vp.Amber : Vp.Txt2).WithA(a));
        small.Color = Vp.Ivory.WithA(a);
        string mt = VpStr.Int((long)Math.Round(metres));
        float mw = small.MeasureText(mt), uw = small.MeasureText("M");
        float lx = Math.Clamp((xa + xb) / 2 - (mw + 4 + uw) / 2, s.Left + 70, s.Right - mw - uw - 4);
        TextBlobs.Draw(c, mt, lx, ly - 9, small);
        TextBlobs.Draw(c, "M", lx + mw + 4, ly - 9, small);
    }

    /// <summary>Lap length in metres (for the proximity strip), set by the director.</summary>
    public double LapMetres { get; set; }

    void Chevron(SKCanvas c, float x, float y, SKColor col)
    {
        chev.Reset();
        chev.MoveTo(x + 9, y); chev.LineTo(x - 7, y - 8); chev.LineTo(x - 3, y); chev.LineTo(x - 7, y + 8); chev.Close();
        flat.Color = col;
        c.DrawPath(chev, flat);
    }

    public override void Dispose()
    {
        layer?.Dispose();
        foreach (var p in new[] { line, glowLine, fill, dash, flat, big, unit, trend, small, heat }) p.Dispose();
        path.Dispose(); area.Dispose(); proj.Dispose(); chev.Dispose(); areaShader.Dispose(); heatShader.Dispose();
    }
}
