using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>A local yellow on the track map.</summary>
/// <param name="Start">Lap fraction where the yellow sector starts.</param>
/// <param name="End">Lap fraction where it ends (may be less than Start: wraps past the line).</param>
/// <param name="IncidentPct">Lap fraction of the car in trouble (the beacon), negative = unknown.</param>
/// <param name="Sector">Marshal-sector number shown ("SECTOR 4"), 0 = none.</param>
/// <param name="Ground">The track card's terrain for this circuit (TrackTerrain), null = none built yet.</param>
public sealed record YellowMapModel(VpTrack Track, float Start, float End, float IncidentPct, string Place, string Cause, int Sector,
                                    TrackTerrain.Data? Ground = null);

/// <summary>
/// V8 round 2 (2026-10-27, Angelo: "liked, but it must look like the existing 3D map system and zoom from the full map
/// down to the yellow sector"). Built on the track card's 3D map (PreRaceCards.PanelTrackWide) and the battle map's plate:
/// <list type="bullet">
/// <item>The plate: the battle map's rounded glass with the hairline across the top (yellow here) and the place named in
/// its own corner theme (<see cref="CornerTheme"/> label: the Dunlop bridge, Tertre Rouge's red earth...), the corner's
/// backdrop faintly behind the map.</item>
/// <item>The map: the circuit laid on a ground plane tilted back in perspective (<see cref="TrackTerrain.Perspective"/>,
/// the same corner pins as the track card), the real terrain under it when it has been built, the line lifted off its
/// shadow by a soft gold wall, white with a glow, the start/finish tick; the edges melt away.</item>
/// <item>The camera: it traces the lap, holds on the whole circuit, then flies down to the yellow sector (the track card's
/// lap-walk zoom), where the sector glows yellow on the line - flashing in time with the marshal panels in the header -
/// the cars move as dots and the car in trouble pulses.</item>
/// </list>
/// Round 4 (2026-10-27, #349 spacing): the plate lives in the right-third column (x 1290..1884, 594 wide), in the tallest
/// free stretch of it (VpLayout: under the session bar stack, never over the tower, the battle card / battle map, the radio
/// card or another pack card; the centre third stays clear). Two sizes, both baked at show time: the full plate (430 tall)
/// and a compact one (280 tall) - the compact one whenever a battle card is up (it then sits under the battle map) or the
/// free stretch is shorter than the full plate; no room for even the compact plate = it is not drawn until there is (its
/// camera only starts once it is first on screen). It jumps (160 ms fade-in), never glides across graphics. Header: the
/// marshal panels, "YELLOW FLAG" drawn at 14 px and the place's themed name under it (the theme's own eyebrow was ~7 px
/// once scaled into the header); the cause line along the foot at 14 px (13 min, bright).
/// #425 (2026-10-27, Angelo: "as it zooms in, change to the theme of the incident location, then smoothly flash alternate
/// between the yellow and the theme"): the plate is baked as a base (shadow, glass, marshal housings, cause line) plus two
/// skins - the YELLOW-FLAG look (amber wash, yellow keyline, plain place name) and the place's THEME (CornerTheme of the
/// incident's corner / straight: the battle map's backdrop for it - PlayStation space, Michelin blue + yellow edge, Dunlop
/// graphite, bricks, speed, earth, maker colours - and the name in the theme's own lettering, CornerTheme.DrawLabel via
/// LabelImage; a plain place gets a neutral asphalt theme). During the fly-down the theme cross-fades in (the plate, the
/// map's ground tint and the line's wall / glow colour); once zoomed it alternates smoothly (cosine, 1.6 s period) between
/// the two for the rest of the card. The yellow sector on the line and the marshal panels stay yellow throughout.
/// Static parts are baked when it is shown; a frame transforms the baked paths into fixed scratch paths and draws them -
/// no allocation. Cars come in through <see cref="SetCar"/> into fixed arrays.
/// </summary>
public sealed class YellowSectorMap : VpGraphic
{
    /// <summary>Plate width (the right-third column) and its two heights.</summary>
    public const float W = 594f, FullH = 430f, CompactH = 280f;
    const float ColL = 1290f, ColR = 1884f, ColTop = 172f, Pad = 14f;
    const int MaxCars = 64;
    const double InMs = 460, OutMs = 420, JumpMs = 160;
    const float Lift = 11f;
    static readonly SKColor Lbl = new(0xC6, 0xCB, 0xD3);

    /// <summary>One baked size of the plate (map zone, perspective, paths in plane space).</summary>
    sealed class Size : IDisposable
    {
        public float H;
        public VpLayer? Plate, YellowSkin, ThemeSkin, Foot;
        public SKRect Zone;
        public SKMatrix Persp, GroundToPlane;
        public SKPath? PlanePath, SectorPath;
        public SKPathMeasure? Measure;
        public float PlaneLen;
        public SKPoint ZoomFocus; public float ZoomTo = 1f;
        public readonly SKPaint FadeX = new() { BlendMode = SKBlendMode.DstIn }, FadeY = new() { BlendMode = SKBlendMode.DstIn };
        public void Reset()
        {
            Plate?.Dispose(); YellowSkin?.Dispose(); ThemeSkin?.Dispose(); Foot?.Dispose(); PlanePath?.Dispose(); SectorPath?.Dispose(); Measure?.Dispose();
            Plate = YellowSkin = ThemeSkin = Foot = null; PlanePath = SectorPath = null; Measure = null;
            FadeX.Shader?.Dispose(); FadeY.Shader?.Dispose(); FadeX.Shader = FadeY.Shader = null;
        }
        public void Dispose() { Reset(); FadeX.Dispose(); FadeY.Dispose(); }
    }

    YellowMapModel? model;
    double shownAt = -1, hideAt = double.MaxValue, startAt = -1;
    readonly Size full = new() { H = FullH }, compact = new() { H = CompactH };
    SKImage? ground;
    CornerTheme.Theme theme = Neutral;
    /// <summary>#425: the theme for a place without one of its own - asphalt grey, lit from the corner (the maker plate style).</summary>
    static readonly CornerTheme.Theme Neutral = new(CornerTheme.Kind.Maker, new SKColor(0xD6, 0xDC, 0xE6), new SKColor(0x6A, 0x74, 0x84), null);
    /// <summary>#425 timing: theme in over the fly-down, then the alternation's period.</summary>
    const double ThemeIn0 = 1700, ThemeIn1 = 2800, AltPeriod = 1600;
    static readonly SKColor Amber = new(0xFF, 0xB0, 0x00);
    /// <summary>How much of the place's theme shows at card time <paramref name="t"/> (0 = yellow-flag look, 1 = theme).</summary>
    public static float ThemeMix(double t) => t < ThemeIn1 ? Vp.EaseIo(Vp.Seg(t, ThemeIn0, ThemeIn1))
        : 0.5f + 0.5f * MathF.Cos((float)((t - ThemeIn1) / AltPeriod * 2 * Math.PI));
    Size? curSize; float curTop = float.NaN; double jumpAt = -1e12;
    readonly float[] carPct = new float[MaxCars];
    readonly SKColor[] carCol = new SKColor[MaxCars];
    readonly bool[] carOn = new bool[MaxCars];
    int troubleCar = -1;

    readonly SKPath tmp = new(), tmp2 = new(), clip = new();
    readonly SKPaint line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    readonly SKPaint glow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f) };
    readonly SKPaint shadow = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8, StrokeJoin = SKStrokeJoin.Round, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f) };
    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint img = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    readonly SKPaint ring = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

    public bool Showing => model != null && hideAt == double.MaxValue;
    /// <summary>The plate's spot on the last frame (design units; empty = not on screen) - previews, logs.</summary>
    public SKRect LastRect { get; private set; }
    public bool LastCompact { get; private set; }

    public void Show(YellowMapModel m, double nowMs)
    {
        bool fresh = model == null || hideAt != double.MaxValue;
        model = m;
        if (fresh) { shownAt = nowMs; startAt = -1; curSize = null; }
        hideAt = double.MaxValue;
        ground?.Dispose(); ground = m.Ground != null ? SKImage.FromBitmap(m.Ground.Ground) : null;
        theme = (m.Place.Length > 0 ? CornerTheme.For(m.Place) : null) ?? Neutral;
        Bake(full); Bake(compact);
    }

    public override void Hide(double nowMs)
    {
        if (model == null || hideAt != double.MaxValue) return;
        // the minimum time on screen counts from when it was first actually drawn
        double from = startAt >= 0 ? startAt : nowMs;
        hideAt = Math.Max(nowMs, from + 4200);
        if (startAt < 0) hideAt = nowMs;   // never got on screen: just go
    }

    public override bool Active(double nowMs) => model != null && nowMs < hideAt + OutMs;

    /// <summary>Clears the car list (call once per tick before <see cref="SetCar"/>).</summary>
    public void ClearCars() { Array.Clear(carOn); troubleCar = -1; }

    public void SetCar(int carIdx, float pct, SKColor classColor, bool inTrouble)
    {
        if ((uint)carIdx >= MaxCars || pct < 0) return;
        carPct[carIdx] = pct; carCol[carIdx] = classColor; carOn[carIdx] = true;
        if (inTrouble) troubleCar = carIdx;
    }

    // ------------------------------------------------------------------ bake (event time)
    void Bake(Size z)
    {
        z.Reset();
        var m = model!;
        float H = z.H;
        string eye = "YELLOW FLAG", place = m.Place.ToUpperInvariant();
        // the 3D map zone inside the plate (plate space: 0..W, 0..H)
        var zone = z.Zone = new SKRect(14, 74, W - 14, H - 40);
        var plane = new SKRect(zone.Left, zone.Top - zone.Height * 0.22f, zone.Right, zone.Bottom + zone.Height * 0.12f);
        float pin = zone.Width * 0.17f;
        z.Persp = TrackTerrain.Perspective(plane, new SKPoint(zone.Left + pin, zone.Top + 6), new SKPoint(zone.Right - pin, zone.Top + 6),
                                           new SKPoint(zone.Right + zone.Width * 0.06f, zone.Bottom), new SKPoint(zone.Left - zone.Width * 0.06f, zone.Bottom));
        // map units -> plane (fit, as the track card does)
        using var raw = m.Track.PathRaw();
        var b = raw.Bounds;
        float sc = Math.Min(plane.Width * 0.9f / Math.Max(1, b.Width), plane.Height * 0.9f / Math.Max(1, b.Height));
        var toPlane = SKMatrix.CreateScaleTranslation(sc, sc, plane.MidX - b.MidX * sc, plane.MidY - b.MidY * sc);
        z.PlanePath = new SKPath(raw); z.PlanePath.Transform(toPlane);
        var measure = z.Measure = new SKPathMeasure(z.PlanePath, true);
        z.PlaneLen = measure.Length;
        var sector = z.SectorPath = new SKPath();
        float s0 = m.Start - MathF.Floor(m.Start), s1 = m.End - MathF.Floor(m.End);
        float len = z.PlaneLen;
        void Add(float a0, float a1) { using var part = new SKPath(); measure.GetSegment(len * a0, len * a1, part, true); sector.AddPath(part); }
        if (s1 >= s0) Add(s0, s1); else { Add(s0, 1); Add(0, s1); }
        // the camera's target: the sector framed like a lap-walk stop
        var sb = sector.Bounds;
        float ext = Math.Max(Math.Max(sb.Width, sb.Height), Math.Min(zone.Width, zone.Height) * 0.12f);
        z.ZoomTo = Math.Clamp(Math.Min(zone.Width, zone.Height) * 0.5f / ext, 1.6f, 3.2f);
        z.ZoomFocus = new SKPoint(sb.MidX, sb.MidY);
        if (m.Ground != null) z.GroundToPlane = m.Ground.ToMap.PostConcat(toPlane);
        // fades at the zone's edges (the map melts away instead of being cut off)
        z.FadeX.Shader = SKShader.CreateLinearGradient(new SKPoint(zone.Left, 0), new SKPoint(zone.Right, 0),
            new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.12f, 0.88f, 1f }, SKShaderTileMode.Clamp);
        z.FadeY.Shader = SKShader.CreateLinearGradient(new SKPoint(0, zone.Top), new SKPoint(0, zone.Bottom),
            new[] { SKColors.Transparent, SKColors.White, SKColors.White, SKColors.Transparent }, new[] { 0f, 0.14f, 0.9f, 1f }, SKShaderTileMode.Clamp);
        var th = theme;
        var panel = new SKRect(0, 0, W, H);
        var bounds = new SKRect(-30, -30, W + 30, H + 40);
        // the base: shadow, dark glass, edge
        z.Plate = VpLayer.Record(bounds, c =>
        {
            using var rr = new SKRoundRect(panel, 14, 14);
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 115), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) })
            { c.Save(); c.Translate(0, 6); c.DrawRoundRect(rr, sh); c.Restore(); }
            using (var f = new SKPaint { IsAntialias = true, Color = new SKColor(0x0B, 0x0E, 0x14, 225) }) c.DrawRoundRect(rr, f);
            using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = new SKColor(255, 255, 255, 18) }) c.DrawRoundRect(rr, edge);
        });
        // over both skins: the marshal panel housings (LEDs drawn live) and the cause line
        z.Foot = VpLayer.Record(bounds, c =>
        {
            using var housing = new SKPaint { IsAntialias = true, Color = new SKColor(16, 14, 8) };
            using var hrim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = new SKColor(255, 255, 255, 40) };
            foreach (float x in new[] { 16f, W - 60f })
            {
                c.DrawRoundRect(new SKRect(x, 14, x + 44, 58), 5, 5, housing);
                c.DrawRoundRect(new SKRect(x, 14, x + 44, 58), 5, 5, hrim);
            }
            // the foot: what probably happened (14 px, 13 at the least; past that the part after the last " · " goes)
            string cause = m.Cause.ToUpperInvariant();
            float maxW = W - 44;
            float cs = VpText.Fit(cause, Fonts.Inter.SemiBold, 14f, maxW, 1.8f, 13f);
            while (VpText.Width(cause, Fonts.Inter.SemiBold, cs, 1.8f) > maxW && cause.LastIndexOf("  ·  ", StringComparison.Ordinal) is int cut && cut > 0) cause = cause[..cut];
            if (cause.Length > 0) VpText.Draw(c, cause, 22, H - 15, Fonts.Inter.SemiBold, cs, Lbl, 1.8f);
        });
        // the yellow-flag look: an amber wash from the top, the yellow keyline, YELLOW FLAG + the place in plain letters
        z.YellowSkin = VpLayer.Record(bounds, c =>
        {
            using var rr = new SKRoundRect(panel, 14, 14);
            c.Save(); c.ClipRoundRect(rr, antialias: true);
            using (var wash = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H),
                       new[] { Amber.WithAlpha(70), Amber.WithAlpha(18), Amber.WithAlpha(34) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(panel, wash);
            c.Restore();
            using (var hl = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, 0),
                       new[] { Vp.Yellow.WithAlpha(0), Vp.Yellow.WithAlpha(235), Vp.Yellow.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(14, 0, W - 28, 1.6f, hl);
            using (var kl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = Vp.Yellow.WithAlpha(120) }) c.DrawRoundRect(rr, kl);
            VpText.Draw(c, eye, W / 2, 27, Fonts.Inter.Bold, 14f, Vp.Yellow, 2.8f, 0);
            if (place.Length > 0)
                VpText.Draw(c, place, W / 2, 62, Fonts.BarlowCondensed.Bold, VpText.Fit(place, Fonts.BarlowCondensed.Bold, 32f, W - 148, 1.6f, 18f), Vp.Ivory, 1.6f, 0);
        });
        // the place's theme: its backdrop over the whole plate, its colour on the top edge, the name in its own lettering
        z.ThemeSkin = VpLayer.Record(bounds, c =>
        {
            using var rr = new SKRoundRect(panel, 14, 14);
            c.Save(); c.ClipRoundRect(rr, antialias: true);
            BattleMap.Backdrop(c, panel, th, place, 0.92f, 0);
            c.Restore();
            using (var hl = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(W, 0),
                       new[] { th.Rule.WithAlpha(0), th.Rule.WithAlpha(235), th.Rule.WithAlpha(0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp) })
                c.DrawRect(14, 0, W - 28, 1.6f, hl);
            using (var kl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = th.Eye.WithAlpha(110) }) c.DrawRoundRect(rr, kl);
            // the name in the theme's own lettering (CornerTheme.DrawLabel), sized so the name reads at ~28 px, its baseline
            // where the plain name sits; YELLOW FLAG stays readable at 14 px above it (under it for the Dunlop arch)
            bool dunlop = th.Kind == CornerTheme.Kind.Dunlop;
            if (place.Length > 0)
            {
                var (ltf, lsz, ltr, lex) = LabelStyle(th, place);
                float nameW = VpText.Width(place, ltf, lsz, ltr) + lex;
                float k = Math.Clamp(Math.Min(28f / lsz, (W - 140f) / Math.Max(1f, nameW)), 0.5f, 1f);
                c.Save(); c.ClipRoundRect(rr, antialias: true);
                c.Translate(W / 2, (dunlop ? 66f : 64f) - (1000f + 42f) * k);
                c.Scale(k);
                CornerTheme.DrawLabel(c, 0, 1000f, "", place, 1f, null, compact: true);
                c.Restore();
            }
            using (var ep = new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f, Color = th == Neutral ? Vp.Yellow : th.Eye })
                TextTracked.DrawHalo(c, ep, eye, W / 2 - VpText.Width(eye, Fonts.Inter.Bold, 14f, 2.8f) / 2, dunlop ? 88f : 25f, 2.8f);
        });
    }

    /// <summary>The theme's name lettering in CornerTheme.DrawLabel (its NameStyle) and the room its decoration takes
    /// either side (the PlayStation symbols), to scale the themed label to a readable name that fits between the panels.</summary>
    static (SKTypeface Tf, float Size, float Track, float Extra) LabelStyle(CornerTheme.Theme th, string name) => th.Kind switch
    {
        CornerTheme.Kind.Dunlop => (Fonts.CormorantGaramond.Bold, 44f, 1.2f, 0f),
        CornerTheme.Kind.Michelin => (Fonts.BarlowCondensed.Black, 46f, 1.4f, 60f),
        CornerTheme.Kind.PlayStation => (Fonts.Inter.SemiBold, 32f, 3.6f, 192f),
        CornerTheme.Kind.Maker when name.Contains("Ferrari", StringComparison.OrdinalIgnoreCase) => (Fonts.BarlowCondensed.ExtraBold, 42f, 2f, 0f),
        CornerTheme.Kind.Maker => (Fonts.Inter.Bold, 30f, 4f, 0f),
        _ => (Fonts.CormorantGaramond.Bold, 40f, 0.6f, 0f),
    };

    static SKMatrix Zoom(Size s, float z)
    {
        var zone = s.Zone;
        var zc = new SKPoint(zone.MidX, zone.MidY + zone.Height * 0.05f);
        var f = s.ZoomFocus;
        float kk = 1 - Math.Min(1, (z - 1) / 0.35f);
        var mtx = SKMatrix.CreateTranslation(-f.X, -f.Y);
        mtx = mtx.PostConcat(SKMatrix.CreateScale(z, z));
        return mtx.PostConcat(SKMatrix.CreateTranslation(zc.X + (f.X - zc.X) * kk, zc.Y + (f.Y - zc.Y) * kk));
    }

    /// <summary>The tallest free stretch of the right-third column now (design units, top..bottom; empty = none).</summary>
    static (float Top, float Bottom) FreeStretch(double nowMs)
    {
        if (VpLayout.OnboardVisible) return (0, 0);
        Span<SKRect> occ = stackalloc SKRect[24];
        int n = VpLayout.Occupied(occ, nowMs, VpLayout.SlotYellowMap);
        Span<float> top = stackalloc float[24], bot = stackalloc float[24];
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            var r = occ[i];
            if (r.Right <= ColL - Pad || r.Left >= ColR + Pad) continue;
            float t = r.Top - Pad, b = r.Bottom + Pad;
            int j = k++;
            while (j > 0 && top[j - 1] > t) { top[j] = top[j - 1]; bot[j] = bot[j - 1]; j--; }
            top[j] = t; bot[j] = b;
        }
        float y = Math.Max(ColTop, VpLayout.CardTop), bestT = 0, bestB = 0;   // #413
        for (int i = 0; i <= k; i++)
        {
            float next = i < k ? top[i] : 1064f;
            if (next - y > bestB - bestT) { bestT = y; bestB = next; }
            if (i < k) y = Math.Max(y, bot[i]);
        }
        return (bestT, bestB);
    }

    // ------------------------------------------------------------------ draw
    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        LastRect = SKRect.Empty;
        if (m == null || full.Plate == null || compact.Plate == null) return;
        if (nowMs >= hideAt + OutMs) { model = null; VpLayout.Release(VpLayout.SlotYellowMap); return; }
        // placement (round 4): the tallest free stretch of the right third; compact with a battle card up or when short
        var (gt, gb) = FreeStretch(nowMs);
        float room = gb - gt;
        Size? size = room >= FullH && !VpLayout.BattleCardVisible ? full : room >= CompactH ? compact : null;
        if (size == null) { curSize = null; VpLayout.Release(VpLayout.SlotYellowMap); return; }   // no room: off screen until there is
        if (startAt < 0) startAt = nowMs;
        if (size != curSize || MathF.Abs(gt - curTop) > 24f) { if (curSize != null) jumpAt = nowMs; curSize = size; curTop = gt; }
        else curTop = gt;
        var z = size;
        float H = z.H;
        double t = nowMs - startAt;
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, InMs)), outK = Vp.EaseIn(Vp.Seg(nowMs, hideAt, hideAt + OutMs));
        float a = inK * (1f - outK) * Vp.Seg(nowMs, jumpAt, jumpAt + JumpMs);
        var spot = new SKRect(ColR - W, curTop, ColR, curTop + H);
        VpLayout.Claim(VpLayout.SlotYellowMap, spot, nowMs);
        LastRect = spot; LastCompact = z == compact;
        if (a <= 0.003f) return;
        var zone = z.Zone; var persp = z.Persp; var measure = z.Measure!; var planePath = z.PlanePath!; float planeLen = z.PlaneLen;
        int save = c.Save();
        c.Translate(spot.Left, spot.Top + (1f - inK) * 18f);
        float mix = ThemeMix(t);   // #425: 0 = yellow-flag look, 1 = the place's theme
        z.Plate!.Draw(c, 0, 0, a);
        z.YellowSkin!.Draw(c, 0, 0, a * (1f - mix));
        z.ThemeSkin!.Draw(c, 0, 0, a * mix);
        z.Foot!.Draw(c, 0, 0, a);
        // marshal panels: alternate flashing (LED dots)
        double ts = t / 1000.0;
        for (int p = 0; p < 2; p++)
        {
            bool on = ((int)(ts * 2.4) + p) % 2 == 0;
            float x = p == 0 ? 16f : W - 60f;
            flat.Color = Vp.Yellow.WithA(a * (on ? 1f : 0.12f));
            for (int gy = 0; gy < 5; gy++) for (int gx = 0; gx < 5; gx++) c.DrawCircle(x + 7 + gx * 7.5f, 21 + gy * 7.5f, 2.6f, flat);
        }
        // the camera: trace in, hold the whole lap, fly down to the sector
        float trace = Vp.EaseOut(Vp.Seg(t, 250, 1350));
        float fly = Vp.EaseIo(Vp.Seg(t, 1500, 2800));
        float zf = 1f + (z.ZoomTo - 1f) * fly;
        var Z = Zoom(z, zf);
        int layer = c.SaveLayer(new SKRect(zone.Left, zone.Top - 30, zone.Right, zone.Bottom), null);
        clip.Reset(); clip.AddRect(new SKRect(zone.Left, zone.Top - 30, zone.Right, zone.Bottom)); c.ClipPath(clip);
        // ground
        if (ground != null)
        {
            var gm = z.GroundToPlane.PostConcat(Z);
            c.Save(); c.Concat(ref persp); c.Concat(ref gm);
            img.Color = SKColors.White.WithA(a * 0.85f);
            c.DrawImage(ground, 0, 0, img);
            c.Restore();
            // #425: the ground takes the look's colour (amber for the flag, the theme's colour), only where there is ground
            flat.BlendMode = SKBlendMode.SrcATop;
            flat.Color = Vp.Mix(Amber, theme.Rule, mix).WithA(a * (0.16f + 0.2f * mix));
            c.DrawRect(zone.Left, zone.Top - 30, zone.Width, zone.Height + 30, flat);
            flat.BlendMode = SKBlendMode.SrcOver;
        }
        var wallCol = Vp.Mix(Vp.Gold, theme.Eye, mix);
        // the line: shadow, gold wall, the lifted white line (traced in), start/finish
        if (trace < 1f) { tmp2.Reset(); measure.GetSegment(0, planeLen * Math.Max(0.002f, trace), tmp2, true); tmp2.Transform(Z, tmp); }
        else planePath.Transform(Z, tmp);
        c.Save(); c.Concat(ref persp);
        shadow.Color = new SKColor(0, 0, 0, Vp.A(0.5f * a)); c.DrawPath(tmp, shadow);
        c.Restore();
        line.StrokeWidth = 2.6f; line.Color = wallCol.WithA(0.13f * a);
        for (int wi = 1; wi < 5; wi++) { c.Save(); c.Translate(0, -Lift * wi / 5f); c.Concat(ref persp); c.DrawPath(tmp, line); c.Restore(); }
        c.Save(); c.Translate(0, -Lift); c.Concat(ref persp);
        glow.StrokeWidth = 12; glow.Color = wallCol.WithA(0.35f * a); c.DrawPath(tmp, glow);
        line.StrokeWidth = 4.2f; line.Color = SKColors.White.WithA(0.95f * a); c.DrawPath(tmp, line);
        // the yellow sector, flashing with the marshal panels once the camera is on it
        float yk = Vp.Seg(t, 1300, 1800);
        if (yk > 0 && z.SectorPath != null)
        {
            z.SectorPath.Transform(Z, tmp2);
            bool on = ((int)(ts * 2.4)) % 2 == 0;
            float pulse = on ? 1f : 0.55f;
            glow.StrokeWidth = 26; glow.Color = Vp.Yellow.WithA(0.55f * a * yk * pulse); c.DrawPath(tmp2, glow);
            line.StrokeWidth = 7.5f; line.Color = Vp.Mix(Vp.Yellow, SKColors.White, on ? 0.15f : 0f).WithA(a * yk); c.DrawPath(tmp2, line);
        }
        // start / finish tick
        if (trace >= 0.999f && measure.GetPositionAndTangent(0, out var sf, out var tan))
        {
            var p0 = Z.MapPoint(sf); var tv = Z.MapVector(tan.X, tan.Y);
            float tl = MathF.Max(0.001f, MathF.Sqrt(tv.X * tv.X + tv.Y * tv.Y));
            line.StrokeWidth = 3; line.Color = SKColors.White.WithA(0.9f * a);
            c.DrawLine(p0.X - tv.Y / tl * 12, p0.Y + tv.X / tl * 12, p0.X + tv.Y / tl * 12, p0.Y - tv.X / tl * 12, line);
        }
        // the cars on the line; the car in trouble pulsing
        float ck = Vp.Seg(t, 1100, 1500);
        if (ck > 0)
            for (int i = 0; i < MaxCars; i++)
            {
                if (!carOn[i] || !measure.GetPosition(planeLen * (carPct[i] - MathF.Floor(carPct[i])), out var cp)) continue;
                var sp = Z.MapPoint(cp);
                bool bad = i == troubleCar;
                flat.Color = new SKColor(0, 0, 0).WithA(0.6f * a * ck); c.DrawCircle(sp, bad ? 9 : 6.5f, flat);
                flat.Color = (bad ? Vp.Yellow : carCol[i]).WithA(a * ck); c.DrawCircle(sp, bad ? 7 : 4.6f, flat);
                if (bad)
                {
                    float ph = (float)(ts * 1.4 % 1.0);
                    ring.StrokeWidth = 2.5f; ring.Color = Vp.Yellow.WithA(a * ck * (1 - ph));
                    c.DrawCircle(sp, 8 + 26 * ph, ring);
                }
            }
        c.Restore();
        c.DrawRect(zone.Left, zone.Top - 30, zone.Width, zone.Height + 30, z.FadeX);
        c.DrawRect(zone.Left, zone.Top - 30, zone.Width, zone.Height + 30, z.FadeY);
        c.RestoreToCount(layer);
        c.RestoreToCount(save);
    }

    public override void Dispose()
    {
        full.Dispose(); compact.Dispose(); ground?.Dispose();
        tmp.Dispose(); tmp2.Dispose(); clip.Dispose();
        foreach (var p in new[] { line, glow, shadow, flat, img, ring }) p.Dispose();
    }
}
