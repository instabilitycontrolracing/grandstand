using System.IO;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-25 (Angelo: "the battle mode setup should show a track map on it ... it should only show the battling
/// drivers"; then: "it should be zoomed in to the section of the track that we are at, with the corner name coming up -
/// FIGHTING INTO ARNAGE - and the same design as the onboard one, except zoomed in"). A glass panel next to the battle
/// card. The map follows the fight: it frames the stretch from just behind the last battling car to the end of the next
/// named corner (CrewChief's track landmarks), easing smoothly as the cars move. Onboard-map style: dark-edged gold line,
/// start/finish tick, the field faint, the rivals ivory with their numbers, the user's car a pulsing gold dot; the
/// corner coming up is picked out on the line and named in the header.
/// </summary>
public sealed class BattleMap
{
    public const float W = 300f, H = 230f;
    static readonly SKColor Gold = new(0xE8, 0xC2, 0x5A), Ivory = new(0xF4, 0xF6, 0xFA), Glass = new(0x0B, 0x0E, 0x14, 225);

    string? key; bool tried;
    SKPoint[]? raw; float[]? cum;
    readonly SKPath path = new();          // the circuit in map coordinates (drawn through the zoom matrix)
    // #742 (2026-11-01, "leaner, same quality"): the map only ever shows a few hundred metres of the circuit, but the whole
    // circuit (thousands of points at Le Mans) was stroked three times a frame, two of them through a blur. Now only the
    // stretch near the view is stroked: same picture (everything else was clipped away anyway), a fraction of the work.
    readonly SKPath vpath = new();
    SKRect vpathFor = SKRect.Empty;
    float alpha;
    public float Alpha => alpha;
    double lastMs = -1;
    // the zoom: centre (map coordinates) and scale, eased
    SKPoint vc; float vs = -1;

    string themeKey = "", themeName = "", prevTheme = ""; float themeK = 1f;

    static SKColor Mix(SKColor a, SKColor b, float k) => new((byte)(a.Red + (b.Red - a.Red) * k), (byte)(a.Green + (b.Green - a.Green) * k), (byte)(a.Blue + (b.Blue - a.Blue) * k), (byte)(a.Alpha + (b.Alpha - a.Alpha) * k));

    /// <summary>The plate's backdrop in a place's theme.</summary>
    internal static void Backdrop(SKCanvas c, SKRect r, CornerTheme.Theme th, string name, float a, double nowMs)
    {
        if (a <= 0.01f) return;
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);
        var baseC = new SKColor(0x0B, 0x0E, 0x14);
        using var p = new SKPaint { IsAntialias = true };
        switch (th.Kind)
        {
            case CornerTheme.Kind.PlayStation:
                if (CornerBrand.User("playstation bg") is { } bg)
                {
                    float sc = Math.Max(r.Width / bg.Width, r.Height / bg.Height);
                    float w = bg.Width * sc, h = bg.Height * sc;
                    using (var ip = new SKPaint { FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)) })
                        c.DrawBitmap(bg, new SKRect(r.MidX - w / 2, r.MidY - h / 2, r.MidX + w / 2, r.MidY + h / 2), ip);
                    p.Color = new SKColor(0, 0, 0, A(0.3f)); c.DrawRect(r, p);
                }
                else
                {
                    p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { new SKColor(0x02, 0x10, 0x3A, A(1)), new SKColor(0x00, 0x05, 0x16, A(1)) }, null, SKShaderTileMode.Clamp);
                    c.DrawRect(r, p); p.Shader = null;
                }
                break;
            case CornerTheme.Kind.Michelin:
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { new SKColor(0x2E, 0x5C, 0xB0, A(1)), new SKColor(0x18, 0x34, 0x6E, A(1)) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p); p.Shader = null;
                p.Color = new SKColor(0xFC, 0xE5, 0x00, A(1)); c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 7, r.Bottom), p);
                break;
            case CornerTheme.Kind.Dunlop:
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { new SKColor(0x3A, 0x3C, 0x41, A(1)), new SKColor(0x1C, 0x1D, 0x20, A(1)) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p); p.Shader = null;
                p.Color = new SKColor(0xFF, 0xCE, 0x00, A(1)); c.DrawRect(new SKRect(r.Left, r.Bottom - 6, r.Right, r.Bottom), p);
                break;
            case CornerTheme.Kind.Bricks:
                p.Color = Mix(baseC, th.Rule, 0.35f).WithAlpha(A(1)); c.DrawRect(r, p);
                p.Color = th.Eye.WithAlpha(A(0.1f));
                for (int row = 0; row * 9 < r.Height; row++)
                    for (float bx = r.Left - (row % 2) * 11; bx < r.Right; bx += 22)
                        c.DrawRect(new SKRect(bx + 1, r.Top + row * 9 + 1, bx + 20, r.Top + row * 9 + 8), p);
                break;
            case CornerTheme.Kind.Speed:
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { new SKColor(0x0A, 0x1C, 0x36, A(1)), new SKColor(0x04, 0x0A, 0x16, A(1)) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p); p.Shader = null;
                {
                    var rnd = new Random(7); float run = (float)(nowMs / 1000.0 * 260 % (r.Width * 2));
                    for (int i = 0; i < 16; i++)
                    {
                        float len = 40 + (float)rnd.NextDouble() * 120, yy = r.Top + (float)rnd.NextDouble() * r.Height;
                        float xx = r.Right - ((float)rnd.NextDouble() * r.Width * 2 + run) % (r.Width * 2) + len;
                        using var sk = SKShader.CreateLinearGradient(new SKPoint(xx - len, 0), new SKPoint(xx, 0), new[] { th.Eye.WithAlpha(0), th.Eye.WithAlpha(A(0.35f)) }, null, SKShaderTileMode.Clamp);
                        p.Shader = sk; c.DrawRect(xx - len, yy, len, 1.2f, p); p.Shader = null;
                    }
                }
                break;
            case CornerTheme.Kind.Portrait:   // 2026-10-28: a driver's corner - warm graphite, fine gold hairlines top and bottom
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { Mix(baseC, th.Rule, 0.22f).WithAlpha(A(1)), Mix(baseC, th.Rule, 0.08f).WithAlpha(A(1)) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p); p.Shader = null;
                p.Color = th.Rule.WithAlpha(A(0.85f));
                c.DrawRect(new SKRect(r.Left, r.Top + 3, r.Right, r.Top + 4), p);
                c.DrawRect(new SKRect(r.Left, r.Bottom - 4, r.Right, r.Bottom - 3), p);
                break;
            case CornerTheme.Kind.Earth:
                p.Color = Mix(baseC, th.Rule, 0.3f).WithAlpha(A(1)); c.DrawRect(r, p);
                p.Shader = SKShader.CreateRadialGradient(new SKPoint(r.MidX, r.Bottom), r.Width * 0.7f, new[] { th.Eye.WithAlpha(A(0.28f)), th.Eye.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p); p.Shader = null;
                break;
            default:   // a maker: the plate in a deep shade of its colour, lit from the top corner
                p.Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), new[] { Mix(baseC, th.Rule, 0.42f).WithAlpha(A(1)), Mix(baseC, th.Rule, 0.16f).WithAlpha(A(1)) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p);
                p.Shader = SKShader.CreateRadialGradient(new SKPoint(r.Right - 20, r.Top + 10), r.Width * 0.8f, new[] { th.Eye.WithAlpha(A(0.22f)), th.Eye.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
                c.DrawRect(r, p); p.Shader = null;
                break;
        }
    }

    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint fill = new() { IsAntialias = true };
    readonly SKPaint text = new() { IsAntialias = true, TextAlign = SKTextAlign.Center };

    /// <summary>Draw at the panel's top-left. <paramref name="cars"/> = the battling cars (player first if involved).</summary>
    /// 2026-10-31 (Angelo: "the mini map ... on the main screen too in the top right when I am in regular mode ... yellow flag
    /// logic and everything"): <paramref name="cruise"/> = the regular-mode map - our car (+ class cars close by), the place we're
    /// heading into. Both modes frame a yellow flag ahead (up to 2.5 km), mark it flashing and say YELLOW FLAG in the header.
    public void Draw(SKCanvas c, float x, float y, RaceSnapshot snap, IReadOnlyList<int> cars, bool show, double nowMs, bool cruise = false)
    {
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100); lastMs = nowMs;
        alpha = Math.Clamp(alpha + (float)(dt / 280.0) * (show && cars.Count >= (cruise ? 1 : 2) ? 1 : -1), 0f, 1f);
        if (alpha <= 0.01f) { vs = -1; return; }
        var k = TrackMapCache.Resolve(snap.TrackConfigName, snap.TrackDisplayName, snap.TrackBroadcastName, snap.TrackRawName);
        if (k != key || !tried) { key = k; tried = true; Load(k); vs = -1; }
        if (raw == null || cum == null) return;
        float a = alpha * alpha * (3 - 2 * alpha);
        byte A(float v) => (byte)Math.Clamp(v * 255f * a, 0, 255);

        // ---- the battling cars, front to back along the lap (handles the start/finish line between them)
        var bc = new List<Driver>();
        foreach (var idx in cars) if (Find(snap, idx) is { LapDistPct: >= 0 } d) bc.Add(d);
        if (bc.Count == 0) return;
        float refPct = bc[0].LapDistPct;
        float Rel(float p) { float r = p - refPct; if (r > 0.5f) r -= 1; if (r < -0.5f) r += 1; return r; }
        float back = bc.Min(d => Rel(d.LapDistPct)), front = bc.Max(d => Rel(d.LapDistPct));
        float frontPct = refPct + front;
        double lenM = TrackCorners.LengthM(snap.Extras.TrackLength); if (lenM <= 0) lenM = 5000;
        float M(double metres) => (float)(metres / lenM);
        var corner = TrackCorners.Upcoming(snap.TrackRawName, snap.Extras.TrackLength, frontPct, 700);
        // yellow flag ahead (YellowFlagCallout works out where): frame it, flash it
        bool ylw = YellowFlagCallout.YellowNow && YellowFlagCallout.CurrentPct >= 0;
        float yRel = ylw ? DistAhead(YellowFlagCallout.CurrentPct, frontPct) : 1f;
        if (ylw && yRel > 0.5f && 1f - yRel < M(150)) yRel = 0f;            // just behind the front car: it's here
        bool ylwAhead = ylw && yRel < M(2500);
        float yFlash = 0.5f + 0.5f * (float)Math.Sin(nowMs / 1000.0 * 2 * Math.PI * 1.6);

        // ---- the window: 120 m behind the last car to the end of the corner coming up (at least 350 m ahead)
        float w0 = refPct + back - M(120);
        float w1 = refPct + front + M(350);
        if (ylwAhead) w1 = Math.Max(w1, frontPct + yRel + M(150));
        if (corner is { } cn) { float ce = cn.EndPct; float rel = ce - frontPct; if (rel < -0.5f) rel += 1; if (rel > 0 && rel < M(1200)) w1 = Math.Max(w1, frontPct + rel + M(60)); }
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int s2 = 0; s2 <= 40; s2++)
        {
            var q = RawAt(w0 + (w1 - w0) * s2 / 40f);
            minX = Math.Min(minX, q.X); maxX = Math.Max(maxX, q.X); minY = Math.Min(minY, q.Y); maxY = Math.Max(maxY, q.Y);
        }

        // 2026-10-25 (Angelo: "that mini map thing needs redesigning to be more in line with what we have refined"):
        // the track card's language - a rounded glass plate with a gold hairline, the corner named in its own theme
        // (CornerTheme), the circuit as a white line with a soft glow, the stretch ahead lit in the corner's colour,
        // the edges melting away, numbered chips for the cars and the gap at the foot.
        // header: FIGHTING INTO / THROUGH / DOWN + the place, themed
        string? straight = corner is { Inside: false } && DistAhead(corner.Value.StartPct, frontPct) > M(350) || corner == null
            ? TrackCorners.Straight(snap.TrackRawName, snap.Extras.TrackLength, frontPct) : null;
        string eye, place;
        if (straight != null) { eye = "FIGHTING DOWN"; place = straight.ToUpperInvariant(); }
        else if (corner is { } h) { eye = h.Inside ? "FIGHTING THROUGH" : "FIGHTING INTO"; place = h.Name.ToUpperInvariant(); }
        else { eye = "ON TRACK"; place = ""; }
        if (cruise) eye = eye.Replace("FIGHTING DOWN", "DOWN").Replace("FIGHTING THROUGH", "THROUGH").Replace("FIGHTING INTO", "HEADING INTO");
        if (ylwAhead)
        {
            eye = "YELLOW FLAG";
            var yp = YellowFlagCallout.CurrentPlace;
            if (!string.IsNullOrWhiteSpace(yp)) place = yp.ToUpperInvariant();
        }
        var panel = new SKRect(x, y + (1 - a) * 18f, x + W, y + H + (1 - a) * 18f);
        using var rr = new SKRoundRect(panel, 14, 14);
        fill.Shader = null;
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, A(0.45f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) })
        { c.Save(); c.Translate(0, 6); c.DrawRoundRect(rr, sh); c.Restore(); }
        fill.Color = new SKColor(0x0B, 0x0E, 0x14, A(0.86f)); c.DrawRoundRect(rr, fill);
        // 2026-10-25 (Angelo: "the entire colours of the minimap should change to what their corners or straights are"):
        // while the fight is at a themed place the whole plate takes its look - the PlayStation space backdrop, Michelin
        // blue, Dunlop graphite and yellow, the maker's colours, the Brickyard's bricks, Mulsanne speed, the red earth -
        // cross-fading as the fight moves on
        string tk = CornerTheme.For(place) is { } thNow ? thNow.Kind + "|" + place : "";
        if (tk != themeKey) { prevTheme = themeName; themeName = place; themeKey = tk; themeK = 0; }
        themeK = Math.Min(1f, themeK + (float)(dt / 600.0));
        var thCur = tk.Length > 0 ? CornerTheme.For(themeName) : null;
        var thPrev = CornerTheme.For(prevTheme);
        c.Save(); c.ClipRoundRect(rr, antialias: true);
        if (thPrev != null && themeK < 1) Backdrop(c, panel, thPrev, prevTheme, a * (1 - themeK), nowMs);
        if (thCur != null) Backdrop(c, panel, thCur, themeName, a * themeK, nowMs);
        c.Restore();
        float themed = thCur != null ? themeK : (thPrev != null ? 1 - themeK : 0);
        var hlCol = ylwAhead ? Mix(new SKColor(0xFF, 0xD4, 0x00), Gold, 1 - yFlash) : thCur?.Eye ?? Gold;
        using (var hl = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(panel.Left, 0), new SKPoint(panel.Right, 0),
                   new[] { hlCol.WithAlpha(0), hlCol.WithAlpha(A(0.9f)), hlCol.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(panel.Left + 14, panel.Top, panel.Width - 28, 1.4f, hl);
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, A(0.07f)) }) c.DrawRoundRect(rr, edge);

        // 2026-10-25 (Angelo: the corner / straight themes in the battle mini map too): the place's full themed label,
        // scaled to fit the header (falls back to the compact one if it can't be drawn)
        SKColor acc;
        var lab = place.Length > 0 ? CornerTheme.LabelImage(eye, place) : null;
        if (lab != null)
        {
            acc = CornerTheme.Accent(place);
            var hb = new SKRect(panel.Left + 10, panel.Top + 6, panel.Right - 10, panel.Top + 58);
            float ls = Math.Min(hb.Width / lab.Width, hb.Height / lab.Height);
            float lw = lab.Width * ls, lh = lab.Height * ls;
            using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha(A(1)) };
            c.DrawImage(lab, new SKRect(hb.MidX - lw / 2, hb.MidY - lh / 2, hb.MidX + lw / 2, hb.MidY + lh / 2), lp);
        }
        else acc = CornerTheme.DrawCompact(c, panel.Left + 16, panel.Top + 12, W - 32, eye, place, a);

        var box = new SKRect(panel.Left + 14, panel.Top + 62, panel.Right - 14, panel.Bottom - 30);
        float wantS = Math.Min(box.Width / Math.Max(1, maxX - minX), box.Height / Math.Max(1, maxY - minY)) * 0.82f;
        var wantC = new SKPoint((minX + maxX) / 2, (minY + maxY) / 2);
        if (vs < 0) { vs = wantS; vc = wantC; }
        else
        {
            float e = 1f - MathF.Exp(-(float)dt / 650f);       // ease toward the new framing (no jitter)
            vs += (wantS - vs) * e; vc = new SKPoint(vc.X + (wantC.X - vc.X) * e, vc.Y + (wantC.Y - vc.Y) * e);
        }
        SKPoint Tx(SKPoint p) => new(box.MidX + (p.X - vc.X) * vs, box.MidY + (p.Y - vc.Y) * vs);
        // 2026-10-25 (Angelo: "all the maps 3D, including the little ones"): the map lies on a ground plane tilted back in
        // perspective, on the real terrain (TrackTerrain); the line rides a little above its shadow; the car dots and
        // number chips stay upright where the cars are, so they read
        var plane = new SKRect(box.Left, box.Top - box.Height * 0.3f, box.Right, box.Bottom + box.Height * 0.08f);
        float pin = box.Width * 0.15f;
        var persp = TrackTerrain.Perspective(plane, new SKPoint(box.Left + pin, box.Top - 4), new SKPoint(box.Right - pin, box.Top - 4),
                                             new SKPoint(box.Right + box.Width * 0.05f, box.Bottom + 6), new SKPoint(box.Left - box.Width * 0.05f, box.Bottom + 6));
        const float Lift = 5f;
        SKPoint PT(SKPoint mapPt) { var q = persp.MapPoint(Tx(mapPt)); return new SKPoint(q.X, q.Y - Lift); }
        var terrain = k == null ? null : TrackTerrain.For(k, Assets.Resolve("maps", k + ".json"), snap.TrackLatitude, snap.TrackLongitude, lenM);

        var mapR = new SKRect(panel.Left + 2, panel.Top + 56, panel.Right - 2, panel.Bottom - 26);
        // #742: the circuit near the view only - map-space window around the view centre, generous on every side (the
        // tilted ground shows more at the top than the box does), rebuilt only when the view has moved noticeably
        {
            float hx = box.Width / Math.Max(1e-4f, vs) * 1.25f, hy = box.Height / Math.Max(1e-4f, vs) * 1.6f;
            var want = new SKRect(vc.X - hx, vc.Y - hy, vc.X + hx, vc.Y + hy);
            float tol = Math.Min(hx, hy) * 0.15f;
            if (vpathFor.IsEmpty || Math.Abs(want.Left - vpathFor.Left) > tol || Math.Abs(want.Top - vpathFor.Top) > tol
                || Math.Abs(want.Right - vpathFor.Right) > tol || Math.Abs(want.Bottom - vpathFor.Bottom) > tol)
            {
                var keep = new SKRect(want.Left - tol * 2, want.Top - tol * 2, want.Right + tol * 2, want.Bottom + tol * 2);
                vpathFor = want;
                vpath.Reset();
                int n = raw.Length; bool open = false;
                for (int i = 0; i <= n; i++)
                {
                    var pt = raw[i % n];
                    bool inside = keep.Contains(pt);
                    bool nextIn = keep.Contains(raw[(i + 1) % n]), prevIn = keep.Contains(raw[(i - 1 + n) % n]);
                    if (inside || nextIn || prevIn)       // a point inside or touching the window (so lines run off the edge)
                    {
                        if (!open) { vpath.MoveTo(pt); open = true; } else vpath.LineTo(pt);
                    }
                    else open = false;
                }
            }
        }
        var drawPath = vpath.IsEmpty ? path : vpath;
        int layer = c.SaveLayer(mapR, null);
        var zoomM = SKMatrix.CreateTranslation(-vc.X, -vc.Y).PostConcat(SKMatrix.CreateScale(vs, vs)).PostConcat(SKMatrix.CreateTranslation(box.MidX, box.MidY));
        // the ground, then the line's shadow on it
        c.Save(); c.Concat(ref persp);
        if (terrain != null)
        {
            var gm = terrain.ToMap.PostConcat(zoomM);
            c.Save(); c.Concat(ref gm);
            using (var gp = new SKPaint { IsAntialias = false, FilterQuality = SKFilterQuality.Low, Color = SKColors.White.WithAlpha(A(terrain.FadeIn * (1 - 0.7f * themed))) })
                c.DrawBitmap(terrain.Ground, 0, 0, gp);
            c.Restore();
        }
        c.Concat(ref zoomM);
        using (var shp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = 6f / vs, Color = new SKColor(0, 0, 0, A(0.5f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3f / vs) })
            c.DrawPath(drawPath, shp);
        c.Restore();
        // the circuit: soft glow, then a crisp white line (widths kept in screen pixels), raised above the ground
        c.Save();
        c.Translate(0, -Lift); c.Concat(ref persp);
        c.Translate(box.MidX, box.MidY); c.Scale(vs); c.Translate(-vc.X, -vc.Y);
        using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, StrokeWidth = 9f / vs,
                   Color = (thCur?.Eye ?? Gold).WithAlpha(A(0.3f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f / vs) })
            c.DrawPath(drawPath, glow);
        stroke.StrokeWidth = 2.6f / vs; stroke.Color = SKColors.White.WithAlpha(A(0.9f)); c.DrawPath(drawPath, stroke);
        c.Restore();
        // the corner / straight coming up, lit in its own colour (on the raised plane)
        c.Save(); c.Translate(0, -Lift); c.Concat(ref persp);
        if (corner is { } hc)
        {
            float cs = hc.StartPct, ce = hc.EndPct; if (ce < cs) ce += 1;
            using var seg = new SKPath();
            seg.MoveTo(Tx(RawAt(cs)));
            for (int s3 = 1; s3 <= 20; s3++) seg.LineTo(Tx(RawAt(cs + (ce - cs) * s3 / 20f)));
            using (var sg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, StrokeWidth = 9,
                       Color = acc.WithAlpha(A(0.4f)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4) })
                c.DrawPath(seg, sg);
            stroke.StrokeWidth = 3.6f; stroke.Color = acc.WithAlpha(A(1)); c.DrawPath(seg, stroke);
        }
        // start / finish tick
        {
            var s0 = Tx(RawAt(0)); var s1 = Tx(RawAt(0.002f));
            float dx = s1.X - s0.X, dy = s1.Y - s0.Y, l = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dy * dy));
            float nx = -dy / l * 9f, ny = dx / l * 9f;
            stroke.StrokeWidth = 2.4f; stroke.Color = Ivory.WithAlpha(A(0.9f)); c.DrawLine(s0.X - nx, s0.Y - ny, s0.X + nx, s0.Y + ny, stroke);
        }
        c.Restore();
        // the yellow flag spot: a flashing amber halo with a little marshal flag
        if (ylwAhead)
        {
            var yq = PT(RawAt(YellowFlagCallout.CurrentPct));
            var yc = new SKColor(0xFF, 0xD4, 0x00);
            using (var yh = new SKPaint { IsAntialias = true, Color = yc.WithAlpha(A(0.18f + 0.3f * yFlash)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6) })
                c.DrawCircle(yq, 15f + 5f * yFlash, yh);
            fill.Color = new SKColor(0, 0, 0, A(0.7f)); c.DrawCircle(yq, 6.5f, fill);
            fill.Color = yc.WithAlpha(A(0.55f + 0.45f * yFlash)); c.DrawCircle(yq, 4.8f, fill);
            stroke.StrokeWidth = 1.6f; stroke.Color = Ivory.WithAlpha(A(0.9f)); c.DrawLine(yq.X, yq.Y - 6, yq.X, yq.Y - 24, stroke);
            using (var fp = new SKPath())
            {
                fp.MoveTo(yq.X, yq.Y - 24); fp.LineTo(yq.X + 13, yq.Y - 21); fp.LineTo(yq.X, yq.Y - 16); fp.Close();
                fill.Color = yc.WithAlpha(A(0.6f + 0.4f * yFlash)); c.DrawPath(fp, fill);
            }
        }
        // everyone else, faint
        var inBattle = new HashSet<int>(bc.Select(d => d.CarIdx));
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers)
            {
                if (inBattle.Contains(d.CarIdx) || d.LapDistPct < 0 || d.IsInPit) continue;
                fill.Color = Ivory.WithAlpha(A(0.25f)); c.DrawCircle(PT(RawAt(d.LapDistPct)), 2.8f, fill);
            }
        // the battling cars, back to front so the player's dot is on top, each with a numbered glass chip
        for (int i = bc.Count - 1; i >= 0; i--)
        {
            var d = bc[i];
            bool me = d.CarIdx == snap.PlayerCarIdx;
            var p = PT(RawAt(d.LapDistPct));
            var col = me ? Gold : Ivory;
            if (me)
            {
                float pulse = 0.5f + 0.5f * (float)Math.Sin(nowMs / 1000.0 * 4.0);
                fill.Color = Gold.WithAlpha(A(0.16f + 0.12f * pulse)); c.DrawCircle(p, 13f + 2.5f * pulse, fill);
            }
            fill.Color = new SKColor(0, 0, 0, A(0.7f)); c.DrawCircle(p, 7.2f, fill);
            fill.Color = col.WithAlpha(A(1)); c.DrawCircle(p, 5.4f, fill);
            var q2 = PT(RawAt(d.LapDistPct + 0.0015f));
            float tx = q2.X - p.X, tyv = q2.Y - p.Y, tl = MathF.Max(1e-3f, MathF.Sqrt(tx * tx + tyv * tyv));
            float side = i % 2 == 0 ? 1 : -1, dist = 26f + (i / 2) * 20f;
            var lp = new SKPoint(p.X - tyv / tl * dist * side, p.Y + tx / tl * dist * side);
            stroke.StrokeWidth = 1f; stroke.Color = col.WithAlpha(A(0.45f)); c.DrawLine(p, lp, stroke);
            text.Typeface = Fonts.Inter.Bold; text.TextSize = 13.5f; text.TextAlign = SKTextAlign.Center;   // #340: was 12
            float cw = text.MeasureText(d.CarNumber) + 14;
            var chip = new SKRect(lp.X - cw / 2, lp.Y - 9, lp.X + cw / 2, lp.Y + 9);
            fill.Color = new SKColor(0x0B, 0x0E, 0x14, A(0.92f)); c.DrawRoundRect(chip, 9, 9, fill);
            using (var cb = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = col.WithAlpha(A(0.9f)) }) c.DrawRoundRect(chip, 9, 9, cb);
            text.Color = col.WithAlpha(A(1)); c.DrawText(d.CarNumber, lp.X, lp.Y + 4.3f, text);
        }
        // melt the map into the plate at its edges
        using (var mx = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(mapR.Left, 0), new SKPoint(mapR.Right, 0),
                   new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.1f, 0.9f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(mapR, mx);
        using (var my = new SKPaint { BlendMode = SKBlendMode.DstIn, Shader = SKShader.CreateLinearGradient(new SKPoint(0, mapR.Top), new SKPoint(0, mapR.Bottom),
                   new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.1f, 0.9f, 1f }, SKShaderTileMode.Clamp) })
            c.DrawRect(mapR, my);
        c.RestoreToCount(layer);

        if (ylwAhead || (cruise && bc.Count < 2))
        {
            // the foot (regular mode / a yellow ahead): how far to the yellow, or where we are
            text.TextAlign = SKTextAlign.Left; text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f;
            text.Color = ylwAhead ? new SKColor(0xFF, 0xD4, 0x00, A(1)) : new SKColor(0xC4, 0xCA, 0xD4, A(1));
            TextTracked.Draw(c, text, ylwAhead ? "YELLOW AHEAD" : "ON TRACK", panel.Left + 16, panel.Bottom - 11, 1.8f);
            if (ylwAhead)
            {
                double m = yRel * lenM;
                text.TextAlign = SKTextAlign.Right; text.Typeface = Fonts.BarlowCondensed.ExtraBold; text.TextSize = 18; text.Color = Ivory.WithAlpha(A(1));
                c.DrawText(m < 60 ? "HERE" : m >= 1000 ? (m / 1000).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " km" : ((int)(m / 10) * 10) + " m",
                           panel.Right - 16, panel.Bottom - 9, text);
            }
            text.TextAlign = SKTextAlign.Center;
            return;
        }
        // the foot: the gap between the front and the back of the fight
        var rear = bc.OrderBy(d => Rel(d.LapDistPct)).First();
        var lead = bc.OrderBy(d => Rel(d.LapDistPct)).Last();
        double lapS = rear.BestLapTime > 0 ? rear.BestLapTime : rear.LastLapTime > 0 ? rear.LastLapTime : 240;
        double gapS = (Rel(lead.LapDistPct) - Rel(rear.LapDistPct)) * lapS;   // fraction of a lap x a lap time
        if (rear.IntervalSeconds is double iv && bc.Count == 2 && iv > 0 && iv < 5) gapS = iv;
        text.TextAlign = SKTextAlign.Left; text.Typeface = Fonts.Inter.Bold; text.TextSize = 13f; text.Color = new SKColor(0xC4, 0xCA, 0xD4, A(1));   // #340: was 10.5, 8A93A6
        TextTracked.Draw(c, text, bc.Count > 2 ? $"{bc.Count} CARS" : "GAP", panel.Left + 16, panel.Bottom - 11, 1.8f);
        text.TextAlign = SKTextAlign.Right; text.Typeface = Fonts.BarlowCondensed.ExtraBold; text.TextSize = 18; text.Color = Ivory.WithAlpha(A(1));
        c.DrawText(gapS.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s", panel.Right - 16, panel.Bottom - 9, text);
        text.TextAlign = SKTextAlign.Center;
    }

    static Driver? Find(RaceSnapshot s, int idx)
    {
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) if (d.CarIdx == idx) return d;
        return null;
    }

    static SKColor ParseOr(string? hex, SKColor fb) => !string.IsNullOrWhiteSpace(hex) && SKColor.TryParse(hex, out var c) ? c : fb;

    void Load(string? k)
    {
        raw = null; cum = null;
        try
        {
            var json = k == null ? null : Assets.Resolve("maps", k + ".json");
            if (json == null || !File.Exists(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(json));
            var list = new List<SKPoint>();
            foreach (var pt in doc.RootElement.GetProperty("points").EnumerateArray()) list.Add(new SKPoint(pt[0].GetSingle(), pt[1].GetSingle()));
            if (list.Count < 20) return;
            var cu = new float[list.Count + 1];
            for (int i = 1; i <= list.Count; i++) cu[i] = cu[i - 1] + SKPoint.Distance(list[i - 1], list[i % list.Count]);
            float tot = cu[^1]; if (tot <= 0) return;
            for (int i = 0; i < cu.Length; i++) cu[i] /= tot;
            raw = list.ToArray(); cum = cu;
            vpath.Reset(); vpathFor = SKRect.Empty;
            path.Reset();
            for (int i = 0; i < raw.Length; i++) { if (i == 0) path.MoveTo(raw[i]); else path.LineTo(raw[i]); }
            path.Close();
        }
        catch { raw = null; cum = null; }
    }

    static float DistAhead(float targetPct, float fromPct) { float d = targetPct - fromPct; d -= MathF.Floor(d); return d; }

    /// <summary>Where on the map (its own coordinates) a lap fraction is.</summary>
    SKPoint RawAt(float pct)
    {
        pct -= MathF.Floor(pct);
        int lo = 0, hi = cum!.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (cum[mid] <= pct) lo = mid; else hi = mid; }
        float span = cum[hi] - cum[lo], k = span > 0 ? (pct - cum[lo]) / span : 0;
        var a = raw![lo % raw.Length]; var b = raw[hi % raw.Length];
        return new SKPoint(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);
    }
}
