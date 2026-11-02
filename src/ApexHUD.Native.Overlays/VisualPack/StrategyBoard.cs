using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>One car on the strategy board. Stop laps are what we saw; planned laps are our estimate (shown as such).</summary>
public sealed record StrategyRow(int Pos, string CarNumber, string Driver, int Stops, int StintLaps, int LastStopLap,
                                 int NextStopLap, bool InPit, IReadOnlyList<int> StopLaps, IReadOnlyList<int> PlannedLaps, bool IsYou = false);

/// <param name="RaceLaps">laps the race will run (known or estimated from the clock), 0 = unknown</param>
/// <param name="TypicalStint">laps a stint lasts in this class so far (from the stops seen), 0 = not known yet</param>
/// <param name="FuelLine">our own car's fuel ("YOUR FUEL 42.3 L · 11 LAPS LEFT"), "" when not in a car</param>
public sealed record StrategyModel(string ClassName, string ClassColor, int Lap, int RaceLaps, int TypicalStint,
                                   IReadOnlyList<StrategyRow> Rows, string FuelLine);

/// <summary>
/// V4 (2026-10-27): the tyre and fuel strategy board for long races - who has pitted, how old their tyres are, and when the
/// next stops should come. Centre screen in a quiet moment (or from the control app).
/// <para>Data: iRacing tells us when each car is on pit road and how many laps it has done, not its tyres or fuel, so tyre
/// age = laps since the car's last stop, and the next stop = last stop + the class's typical stint (the stints we have seen
/// so far), labelled EST. Our own car's fuel comes from telemetry.</para>
/// <para>Base: a table - position, car chip, driver, stops, a tyre-age bar against the typical stint (green, amber, red as
/// the stint runs long), last stop, next stop (EST.) or a pulsing IN PIT. Elevated ("race timeline"): each car's whole
/// race on one line - stints as bars, every stop a gold diamond, the stops still to come as hollow dashed diamonds out to
/// the flag, and a NOW line with a glow; rows wipe in one after another.</para>
/// <para>Round 3 (2026-10-27, Angelo: "it can't block the timing tower"; #349 the centre third stays clear of cards; #340
/// bigger text, never smaller): the board lives in the RIGHT THIRD (x 1296..1896), in the tallest free stretch of that
/// column per <see cref="VpLayout"/> - under the session bar / ticker stack and the battle map, above (or below) the team
/// radio card, clear of the battle card, custom and story cards and the pack's other cards. The type keeps its size; to
/// fit, the board shows fewer COLUMNS (STOPS and LAST STOP are gone - the tyre-age bar and the next stop stay) and fewer
/// ROWS (the top N cars of the class that fit, our own car always kept). No room for 3 rows = it fades away.</para>
/// </summary>
public sealed class StrategyBoard : VpGraphic
{
    public const double HoldMs = 16000, OutMs = 500;
    const float RowH = 46f, HeadH = 146f, FootFuel = 84f, FootNote = 34f, ElevExtra = 24f;
    /// <summary>The right-third column the board may use (design units).</summary>
    const float ColL = 1296f, ColR = 1896f, MaxW = ColR - ColL;
    const int MinRows = 3, MaxRows = 16;

    StrategyModel? model;
    double shownAt = -1, lastMs = -1;
    VpLayer? layer;
    bool bakedElevated;
    float boardH, x0, y0, W = MaxW;
    int nRows;                                   // rows baked into the layer
    readonly int[] shown = new int[MaxRows];     // which model rows they are
    float vis = 1f, curTop = float.NaN;
    // column positions (board space), set per bake from the board width
    float cAge, cAgeW, cNext, cNameEnd, tl0, tl1;
    string fuelLine = "";
    // sponsored: per row count, the frame fit and the board width that keeps the frame inside the column at full type size
    readonly float[] fitFor = new float[MaxRows + 1], wFor = new float[MaxRows + 1];

    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f };
    readonly SKPaint dash = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, PathEffect = SKPathEffect.CreateDash(new[] { 4f, 4f }, 0) };
    readonly SKPaint glow = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f) };
    readonly SKPaint fade = new();
    readonly SKPath clip = new(), dia = new();

    public void Show(StrategyModel m, double nowMs)
    {
        model = m; shownAt = nowMs; lastMs = -1; vis = 1f; curTop = float.NaN;
        // round 2: a sponsored board takes its whole look from the partner's own logo file (sponsors.json slot "StrategyBoard")
        theme?.Dispose(); theme = null;
        var p = SponsorPack.For(Slot);
        if (p != Partner.None && !HouseLook) theme = SponsorTheme.FromFile(SponsorPack.LogoPath(p));
        framed = false;
        if (theme != null)
        {
            PrecomputeFits(m);
            // the partner's mark only becomes the frame when it can hold a full-width board inside the right third
            // (a round mark stretched round a tall board would squeeze the columns); otherwise the house glass in the
            // partner's colour, with "PRESENTED BY" and the logo in the header
            framed = wFor[Math.Min(MaxRows, m.Rows.Count)] >= MinFramedW;
        }
        // place it against the layout as it is now, so the first frame has nothing to bake
        var gap = FreeGap(nowMs);
        int n = RowsFor(gap, m);
        Bake(n > 0 ? n : Math.Min(m.Rows.Count, MinRows));
    }

    /// <summary>Preview / test: the house look even when the slot has a partner.</summary>
    public bool HouseLook { get; set; }

    /// <summary>The sponsors.json slot for this board.</summary>
    public const string Slot = "StrategyBoard";
    /// <summary>The sponsor's measured look (null = the house gold glass).</summary>
    public SponsorTheme? Theme => theme;
    SponsorTheme? theme;
    float fit = 1f;
    /// <summary>Round 3: true = the partner's measured mark is the board's frame (needs room for a board this wide).</summary>
    bool framed;
    const float MinFramedW = 540f;
    /// <summary>Whether the partner's mark frames the board this time (false = house glass in the partner's colour).</summary>
    public bool Framed => framed;
    const float boardScale = 1f;   // round 3: never scaled down (#340) - rows and columns go instead
    readonly SKPaint bandPaint = new() { IsAntialias = true }, glassPaint = new() { IsAntialias = true };
    readonly SKPaint hair = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0 };
    readonly SKPaint logoPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    readonly SKPaint flash = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    const double BoomMs = 380, GrowMs = 1050, ContentMs = 900, SponsoredOutMs = 700;

    public override bool Active(double nowMs) => model != null && nowMs - shownAt < HoldMs + (theme != null && framed ? SponsoredOutMs : OutMs);

    public override void Hide(double nowMs) { if (Active(nowMs)) shownAt = Math.Min(shownAt, nowMs - HoldMs); }

    float FootH(StrategyModel m) => m.FuelLine.Length > 0 ? FootFuel : FootNote;
    float BoardH(StrategyModel m, int n) => HeadH + n * RowH + FootH(m) + (Elevated ? ElevExtra : 0f);

    void PrecomputeFits(StrategyModel m)
    {
        var th = theme!;
        th.BuildPaths(1f);
        for (int n = 1; n <= Math.Min(MaxRows, m.Rows.Count); n++)
        {
            float w = MaxW, f = 1f;
            for (int it = 0; it < 3; it++) { f = th.FitFactor(w / BoardH(m, n), 0.02f); w = MathF.Floor(Math.Min(MaxW, MaxW / f)); }
            fitFor[n] = f; wFor[n] = w;
        }
    }

    /// <summary>The tallest free stretch of the right-third column right now (design units); empty = none.</summary>
    static SKRect FreeGap(double nowMs)
    {
        if (VpLayout.OnboardVisible) return SKRect.Empty;
        Span<SKRect> occ = stackalloc SKRect[24];
        int n = VpLayout.Occupied(occ, nowMs, VpLayout.SlotStrategy);
        Span<float> top = stackalloc float[24], bot = stackalloc float[24];
        int k = 0;
        const float Pad = 14f;
        for (int i = 0; i < n; i++)
        {
            var r = occ[i];
            if (r.Right <= ColL - Pad || r.Left >= ColR + Pad) continue;
            // insertion sort by top
            float t = r.Top - Pad, b = r.Bottom + Pad;
            int j = k++;
            while (j > 0 && top[j - 1] > t) { top[j] = top[j - 1]; bot[j] = bot[j - 1]; j--; }
            top[j] = t; bot[j] = b;
        }
        float y = VpLayout.CardTop, bestT = 0, bestH = 0;   // #413: 16 px under the bar / watch / ticker
        for (int i = 0; i <= k; i++)
        {
            float next = i < k ? top[i] : 1064f;
            if (next - y > bestH) { bestH = next - y; bestT = y; }
            if (i < k) y = Math.Max(y, bot[i]);
        }
        return bestH > 0 ? new SKRect(ColL, bestT, ColR, bestT + bestH) : SKRect.Empty;
    }

    /// <summary>How many rows fit in <paramref name="gap"/> (0 = fewer than the minimum).</summary>
    int RowsFor(SKRect gap, StrategyModel m)
    {
        if (gap.Height <= 0) return 0;
        int max = Math.Min(MaxRows, m.Rows.Count);
        for (int n = max; n >= MinRows; n--)
        {
            float h = BoardH(m, n) * (framed ? fitFor[n] : 1f);
            if (h <= gap.Height) return n;
        }
        return 0;
    }

    void Bake(int n)
    {
        layer?.Dispose();
        var m = model!;
        bakedElevated = Elevated;
        nRows = n = Math.Max(1, Math.Min(n, Math.Min(MaxRows, m.Rows.Count)));
        // the top n of the class, our own car always on (in the last line when it is further back)
        int you = -1;
        for (int i = 0; i < m.Rows.Count; i++) if (m.Rows[i].IsYou) { you = i; break; }
        for (int i = 0; i < n; i++) shown[i] = i;
        if (you >= n) shown[n - 1] = you;
        boardH = BoardH(m, n);
        W = framed ? wFor[n] : MaxW;
        // columns from the board width: [pos][car][driver ......][tyre age bar][laps][next stop]
        cNext = W - 74f;
        float numStart = cNext - 44f - 58f;
        cNameEnd = Math.Max(134f + 150f, numStart - 130f);
        cAge = cNameEnd + 10f;
        cAgeW = numStart - 12f - cAge;
        if (cAgeW < 40f) { cAgeW = 0f; cNameEnd = numStart - 12f; }   // narrow board: no bar, the coloured lap count stays
        tl0 = 134f + 170f; tl1 = W - 40f;
        // the fuel line: drop its middle part (L / LAP) if it is too wide for the board
        fuelLine = m.FuelLine.ToUpperInvariant();
        if (fuelLine.Length > 0 && VpText.Width(fuelLine, Fonts.Inter.Bold, 13f, 2.2f) > W - 110f)
        {
            var parts = fuelLine.Split("  ·  ");
            if (parts.Length == 3) fuelLine = parts[0] + "  ·  " + parts[2];
        }
        var cls = Vp.ClassLine(m.ClassColor);
        if (theme != null && framed)
        {
            // the frame: the mark stretched so its INNER outline holds the board
            // a thick band stretched to card size would swamp it: keep the outline, thin the band to ~5 % of the frame
            theme.BuildPaths(1f);
            for (int pass = 0; pass < 3; pass++)
            {
                fit = theme.FitFactor(W / boardH, 0.02f);
                float frameH = boardH * fit, thick = theme.BandThickness * theme.BandKeep * frameH;
                if (thick <= 58f) break;
                theme.BuildPaths(Math.Clamp(theme.BandKeep * 52f / thick, 0.08f, 1f));
            }
            var acc = theme.Accent;
            acc.ToHsv(out float hh, out float ss, out float vv);
            bandPaint.Shader?.Dispose();
            bandPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1),
                new[] { SKColor.FromHsv(hh, ss * 0.8f, Math.Min(100, vv * 1.12f)), theme.Color, SKColor.FromHsv(hh, ss, vv * 0.62f) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            glassPaint.Shader?.Dispose();
            glassPaint.Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1),
                new[] { Vp.Mix(Vp.GlassTop, theme.Color, 0.16f).WithAlpha(246), Vp.Mix(Vp.GlassBot, theme.Color, 0.08f).WithAlpha(250) }, null, SKShaderTileMode.Clamp);
        }
        layer = VpLayer.Record(new SKRect(-30, -20, W + 30, boardH + 40), c =>
        {
            var th = theme;
            if (th == null || !framed)
            {
                using var shape = Vp.Slant(new SKRect(0, 0, W, boardH), 26f);
                Vp.GlassPanel(c, shape, th?.Accent ?? cls, 0.95f);
            }
            // header: eyebrow, title + lap on one line, the typical stint under it
            float ebW = VpText.Draw(c, "STRATEGY", 44, 46, Fonts.Inter.Bold, 13f, th?.Accent ?? Vp.GoldHi, 3.6f);
            using (var d = new SKPaint { IsAntialias = true, Color = Vp.Txt3 }) c.DrawCircle(44 + ebW + 12, 41, 2f, d);
            float clsW = VpText.Draw(c, m.ClassName.ToUpperInvariant(), 44 + ebW + 24, 46, Fonts.Inter.Bold, 13f, cls, 3.6f);
            if (th != null)
            {
                // "PRESENTED BY" and the partner's own logo, small, after the eyebrow (when it fits)
                float bx = 44 + ebW + 24 + clsW + 26;
                float pbW = VpText.Width("PRESENTED BY", Fonts.Inter.Bold, 10f, 2.4f);
                float lh = 30f, lw = th.Logo.Width * lh / th.Logo.Height, room = W - 44 - (bx + pbW + 12);
                if (lw > room) { lw = room; lh = th.Logo.Height * lw / th.Logo.Width; }
                if (lw >= 40f && lh >= 12f)
                {
                    bx += VpText.Draw(c, "PRESENTED BY", bx, 46, Fonts.Inter.Bold, 10f, Vp.Txt3, 2.4f) + 12;
                    using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    c.DrawImage(th.Logo, new SKRect(bx, 41 - lh / 2, bx + lw, 41 + lh / 2), lp);
                }
            }
            VpText.Draw(c, Elevated ? "THE RACE SO FAR" : "TYRES & STOPS", 42, 92, Fonts.BarlowCondensed.Black, 46f, Vp.Ivory, 0.6f);
            string right = "LAP " + m.Lap + (m.RaceLaps > 0 ? " OF " + m.RaceLaps : "");
            VpText.Draw(c, right, W - 48, 90, Fonts.BarlowCondensed.Bold, 26f, Vp.Ivory, 0.8f, 1);
            if (m.TypicalStint > 0) VpText.Draw(c, "TYPICAL STINT  " + m.TypicalStint + " LAPS", 44, 116, Fonts.Inter.SemiBold, 11f, Vp.Txt3, 2.2f);
            // column heads
            float hy = HeadH - 10;
            void Head(string s, float x, int al = -1) => VpText.Draw(c, s, x, hy, Fonts.Inter.SemiBold, 10f, Vp.Txt3, 2.2f, al);
            Head("POS", 48, 0); Head("CAR", 74); Head("DRIVER", 134);
            if (!Elevated)
            {
                Head("TYRE AGE", cAgeW > 0 ? cAge : cNameEnd + 12); Head("NEXT STOP", cNext, 0);
            }
            else
            {
                // lap ruler for the timeline
                int laps = Math.Max(m.RaceLaps, m.Lap + 1);
                int step = laps <= 30 ? 10 : laps <= 80 ? 20 : 40;
                using var tk = new SKPaint { Color = Vp.Ivory.WithAlpha(26) };
                for (int l = 0; l <= laps; l += step)
                {
                    if (laps - l < step * 0.6f && l > 0) continue;   // leave room for FLAG
                    float x = tl0 + (tl1 - tl0) * l / laps;
                    VpText.Draw(c, l == 0 ? "START" : "L" + l, x, hy, Fonts.Inter.SemiBold, 10f, Vp.Txt3, 1.4f, 0);
                    c.DrawRect(new SKRect(x, HeadH - 2, x + 1, HeadH + n * RowH), tk);
                }
                VpText.Draw(c, "FLAG", tl1, hy, Fonts.Inter.SemiBold, 10f, Vp.GoldHi, 1.4f, 0);
            }
            using (var hr = new SKPaint { Color = Vp.Ivory.WithAlpha(30) }) c.DrawRect(new SKRect(24, HeadH - 1, W - 30, HeadH), hr);
            // rows
            for (int i = 0; i < n; i++)
            {
                var r = m.Rows[shown[i]];
                float ry = HeadH + i * RowH;
                if (r.IsYou) using (var yb = new SKPaint { Color = new SKColor(0x3B, 0x82, 0xF6, 46) }) c.DrawRect(new SKRect(16, ry + 2, W - 22, ry + RowH - 2), yb);
                else if (i % 2 == 1) using (var zb = new SKPaint { Color = Vp.Ivory.WithAlpha(8) }) c.DrawRect(new SKRect(16, ry + 2, W - 22, ry + RowH - 2), zb);
                float mid = ry + RowH / 2;
                VpText.Draw(c, r.Pos.ToString(), 48, mid + 9, Fonts.BarlowCondensed.ExtraBold, 26f, r.Pos == 1 ? Vp.GoldHi : Vp.Ivory, 0, 0);
                float cw = Math.Max(38f, VpText.Width(r.CarNumber, Fonts.Inter.Bold, 13f) + 14f);
                using (var cb = new SKPaint { IsAntialias = true, Color = SKColors.White }) c.DrawRoundRect(new SKRect(74, mid - 11, 74 + cw, mid + 11), 2, 2, cb);
                VpText.Draw(c, r.CarNumber, 74 + cw / 2, mid + 5, Fonts.Inter.Bold, 13f, new SKColor(8, 10, 16), 0, 0);
                string name = r.Driver.ToUpperInvariant();
                float nameEnd = (Elevated ? tl0 - 16 : cNameEnd) - 4;
                // #340: the name keeps its size (24, at the least 21); a very long one is cut at the column edge
                float ns = VpText.Fit(name, Fonts.BarlowCondensed.Bold, 24f, nameEnd - 134, 0.6f, 21f);
                c.Save(); c.ClipRect(new SKRect(130, ry, nameEnd, ry + RowH));
                VpText.Draw(c, name, 134, mid + 8, Fonts.BarlowCondensed.Bold, ns, r.IsYou ? SKColors.White : Vp.Ivory, 0.6f);
                c.Restore();
                if (!Elevated)
                {
                    if (cAgeW > 0) using (var tr = new SKPaint { Color = Vp.Ivory.WithAlpha(22) }) c.DrawRoundRect(new SKRect(cAge, mid - 4, cAge + cAgeW, mid + 4), 4, 4, tr);
                    if (!r.InPit)
                    {
                        string nx = r.NextStopLap > 0 ? "L" + r.NextStopLap : "-";
                        float nw = VpText.Draw(c, nx, cNext, mid + 8, Fonts.BarlowCondensed.Bold, 24f, Vp.Ivory, 0.4f, 0);
                        if (r.NextStopLap > 0) VpText.Draw(c, "EST.", cNext + nw / 2 + 6, mid + 7, Fonts.Inter.SemiBold, 9f, Vp.Txt3, 1.4f);
                    }
                }
            }
            // footer: our own fuel, then the estimate note on its own line
            float fy = HeadH + n * RowH + 34 + (Elevated ? ElevExtra : 0);
            if (fuelLine.Length > 0)
            {
                using (var hr = new SKPaint { Color = Vp.Ivory.WithAlpha(30) }) c.DrawRect(new SKRect(24, fy - 24, W - 30, fy - 23), hr);
                DrawFuelGlyph(c, 46, fy - 6);
                VpText.Draw(c, fuelLine, 70, fy, Fonts.Inter.Bold, 13f, Vp.GoldHi, 2.2f);
                VpText.Draw(c, "NEXT STOPS ARE ESTIMATES FROM THE STINTS SEEN SO FAR", 44, fy + 28, Fonts.Inter.SemiBold, 10f, Vp.Txt3, 1.4f);
            }
            else VpText.Draw(c, "NEXT STOPS ARE ESTIMATES FROM THE STINTS SEEN SO FAR", 44, boardH - 12, Fonts.Inter.SemiBold, 10f, Vp.Txt3, 1.4f);
        });
    }

    static void DrawFuelGlyph(SKCanvas c, float x, float y)
    {
        using var p = new SKPaint { IsAntialias = true, Color = Vp.GoldHi, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        c.DrawRoundRect(new SKRect(x - 8, y - 10, x + 6, y + 10), 2, 2, p);
        c.DrawLine(x - 5, y - 4, x + 3, y - 4, p);
        c.DrawLine(x + 6, y - 6, x + 11, y - 2, p); c.DrawLine(x + 11, y - 2, x + 11, y + 7, p);
    }

    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = model;
        if (m == null || !Active(nowMs) || layer == null) { VpLayout.Release(VpLayout.SlotStrategy); return; }
        double dt = lastMs < 0 ? 16 : Math.Clamp(nowMs - lastMs, 0, 100);
        lastMs = nowMs;
        // round 3: where it can go this frame (right third, free stretch); fewer rows when the stretch shrinks
        var gap = FreeGap(nowMs);
        int want = RowsFor(gap, m);
        if (bakedElevated != Elevated || (want > 0 && want != nRows)) Bake(want);
        float target = want > 0 ? 1f : 0f;
        vis += (target - vis) * (float)Math.Min(1, dt / 180.0);
        if (target == 0 && vis < 0.01f) vis = 0;
        if (vis <= 0f) { VpLayout.Release(VpLayout.SlotStrategy); return; }
        float frameH = boardH * (framed ? fit : 1f);
        float wantTop = want > 0 ? gap.Top : (float.IsNaN(curTop) ? VpLayout.CardTop : curTop);
        // it jumps to its new spot (never glides across whatever took its old one)
        curTop = wantTop;
        float frameW = W * (framed ? fit : 1f);
        var place = new SKRect(ColR - frameW, curTop, ColR, curTop + frameH);
        if (want > 0) VpLayout.Claim(VpLayout.SlotStrategy, place, nowMs); else VpLayout.Release(VpLayout.SlotStrategy);
        double t = nowMs - shownAt;
        int fsave = -1;
        if (vis < 0.999f) { fade.Color = SKColors.White.WithA(vis); fsave = c.SaveLayer(fade); }
        if (theme != null && framed) DrawSponsored(c, w, h, m, t, place);
        else DrawHouse(c, m, t, place);
        if (fsave >= 0) c.RestoreToCount(fsave);
    }

    void DrawHouse(SKCanvas c, StrategyModel m, double t, SKRect place)
    {
        float inK = Vp.EaseOutExpo(Vp.Seg(t, 0, 600)), outK = Vp.EaseIn(Vp.Seg(t, HoldMs, HoldMs + OutMs));
        float a = Vp.Seg(t, 0, 250) * (1f - outK);
        x0 = place.Left; y0 = place.Top;
        int save = c.Save();
        // it slides in from the right edge (its own column), not up through the picture
        c.Translate(x0 + (1f - inK) * 40f, y0 + outK * 16f);
        layer!.Draw(c, 0, 0, a);
        for (int i = 0; i < nRows; i++)
        {
            var r = m.Rows[shown[i]];
            float ry = HeadH + i * RowH, mid = ry + RowH / 2;
            float rk = Vp.EaseOutExpo(Vp.Seg(t, 250 + i * 70, 900 + i * 70));
            if (!Elevated) DrawAgeRow(c, m, r, mid, rk, a, t);
            else DrawTimelineRow(c, m, r, mid, rk, a, t);
        }
        if (Elevated) DrawNow(c, m, a, t, nRows);
        c.RestoreToCount(save);
    }

    /// <summary>
    /// The sponsored board (round 2): it BOOMS out of the partner's logo. The logo pops up in its own spot; its
    /// mark then grows - the picture itself scaled up until the mark is card-sized, handing over to the measured outline
    /// of the mark (<see cref="SponsorTheme"/>) as the card's border, with a shockwave and a flash in the partner's colour;
    /// the glass inside fills in and the board's rows wipe in. On the way out it all shrinks back into the logo.
    /// Round 3: all of it happens inside the board's own free spot in the right third (the flash too).
    /// </summary>
    void DrawSponsored(SKCanvas c, int w, int h, StrategyModel m, double t, SKRect final)
    {
        var th = theme!;
        double tOut = t - HoldMs;
        float shrink = tOut > 0 ? Vp.EaseIn(Vp.Seg(tOut, 250, SponsoredOutMs)) : 0f;
        // final frame on screen (design units)
        float bw = W * boardScale, bh = boardH * boardScale;
        float cx = final.MidX, cy = final.MidY;
        // the logo on screen at its natural size (inside the spot), and its mark there (level box, centred on the mark)
        float lh = 170f, lw = th.Logo.Width * lh / th.Logo.Height;
        float maxLw = Math.Min(760f, final.Width - 20f);
        if (lw > maxLw) { lw = maxLw; lh = th.Logo.Height * lw / th.Logo.Width; }
        var logoR = new SKRect(cx - lw / 2, cy - lh / 2, cx + lw / 2, cy + lh / 2);
        float mcx = logoR.Left + th.MarkCentre.X * lw, mcy = logoR.Top + th.MarkCentre.Y * lh;
        float mw = th.MarkSize.Width * lw, mh = th.MarkSize.Height * lh;
        var markR = new SKRect(mcx - mw / 2, mcy - mh / 2, mcx + mw / 2, mcy + mh / 2);
        float pop = Vp.EaseOutBack(Vp.Seg(t, 0, BoomMs));
        float grow = (float)Math.Clamp((t - BoomMs) / (GrowMs - BoomMs), 0, 1);
        float g = grow <= 0 ? 0 : 1f - MathF.Pow(1f - grow, 3f);                 // fast out, settles
        g = g + 0.035f * MathF.Sin(MathF.PI * Vp.Clamp01((grow - 0.55f) / 0.45f)); // a touch of overshoot
        g = Math.Max(0, g * (1f - shrink));
        var cur = Lerp(markR, final, g);
        float tilt = th.Tilt * (1f - Vp.Clamp01(g));
        // 1. the logo picture, growing about its mark (whose centre rides to the frame's centre); it hands over to the
        //    measured outline as it goes
        float logoA = (grow <= 0 ? pop : 1f - Vp.Seg(grow, 0.18f, 0.5f)) * (shrink > 0 ? Vp.Seg(shrink, 0.75f, 1f) : 1f);
        if (shrink >= 0.999f) logoA = 1f - Vp.Seg(tOut, SponsoredOutMs - 120, SponsoredOutMs);
        if (logoA > 0.003f)
        {
            float k = MathF.Sqrt((cur.Width / Math.Max(1, markR.Width)) * (cur.Height / Math.Max(1, markR.Height)));
            float ps = grow <= 0 ? 0.6f + 0.4f * pop : 1f;
            c.Save();
            c.Translate(cur.MidX, cur.MidY); c.Scale(k * ps); c.Translate(-mcx, -mcy);
            logoPaint.Color = SKColors.White.WithA(logoA);
            c.DrawImage(th.Logo, logoR, logoPaint);
            c.Restore();
        }
        // 2. the border (the mark's band) and the glass inside it
        float bandA = Vp.Seg(grow, 0.15f, 0.45f) * (shrink > 0 ? 1f - Vp.Seg(shrink, 0.8f, 1f) : 1f);
        var M = SponsorTheme.BoxTo(cur);
        if (tilt != 0) M = M.PostConcat(SKMatrix.CreateRotationDegrees(tilt, cur.MidX, cur.MidY));
        if (bandA > 0.003f)
        {
            float glassA = Vp.Seg(t, BoomMs + 300, BoomMs + 700) * (1f - Vp.Seg(shrink, 0f, 0.6f));
            c.Save(); c.Concat(ref M);
            if (glassA > 0.003f) { glassPaint.Color = SKColors.White.WithA(glassA); c.DrawPath(th.Inner, glassPaint); }
            bandPaint.Color = SKColors.White.WithA(bandA);
            c.DrawPath(th.Band, bandPaint);
            hair.Color = SKColors.White.WithA(0.35f * bandA); c.DrawPath(th.Outer, hair);
            hair.Color = SKColors.Black.WithA(0.45f * bandA); c.DrawPath(th.Inner, hair);
            c.Restore();
        }
        // 3. the boom: a shockwave of the outline and a flash, in the partner's colour - kept to the board's own spot
        float sw = Vp.Seg(t, BoomMs, BoomMs + 650);
        if (sw > 0 && sw < 1)
        {
            float k = 1f + 0.12f * Vp.EaseOut(sw);
            var r2 = new SKRect(cur.MidX - cur.Width * k / 2, cur.MidY - cur.Height * k / 2, cur.MidX + cur.Width * k / 2, cur.MidY + cur.Height * k / 2);
            var M2 = SponsorTheme.BoxTo(r2);
            if (tilt != 0) M2 = M2.PostConcat(SKMatrix.CreateRotationDegrees(tilt, cur.MidX, cur.MidY));
            c.Save();
            c.ClipRect(new SKRect(final.Left - 6, final.Top - 6, final.Right + 6, final.Bottom + 6));
            c.Concat(ref M2);
            bandPaint.Color = SKColors.White.WithA(0.45f * (1f - sw));
            c.DrawPath(th.Band, bandPaint);
            c.Restore();
            flash.Color = th.Accent.WithA(0.22f * MathF.Sin(MathF.PI * Vp.Seg(t, BoomMs, BoomMs + 360)));
            c.Save(); c.Concat(ref M); c.DrawPath(th.Outer, flash); c.Restore();
        }
        // 4. the board itself, inside the border
        float a = Vp.Seg(t, BoomMs + ContentMs - 400, BoomMs + ContentMs) * (1f - Vp.Seg(tOut, 0, 260));
        if (a > 0.003f)
        {
            int save = c.Save();
            clip.Reset(); clip.AddPath(th.Inner);
            clip.Transform(M);
            c.ClipPath(clip, SKClipOperation.Intersect, true);
            c.Translate(cx - bw / 2, cy - bh / 2 + (1f - a) * 16f);
            c.Scale(boardScale);
            layer!.Draw(c, 0, 0, a);
            double tc = t - (BoomMs + ContentMs - 400);
            for (int i = 0; i < nRows; i++)
            {
                var r = m.Rows[shown[i]];
                float ry = HeadH + i * RowH, mid = ry + RowH / 2;
                float rk = Vp.EaseOutExpo(Vp.Seg(tc, 250 + i * 70, 900 + i * 70));
                if (!Elevated) DrawAgeRow(c, m, r, mid, rk, a, tc);
                else DrawTimelineRow(c, m, r, mid, rk, a, tc);
            }
            if (Elevated) DrawNow(c, m, a, tc, nRows);
            c.RestoreToCount(save);
        }
    }

    static SKRect Lerp(SKRect a, SKRect b, float k) =>
        new(a.Left + (b.Left - a.Left) * k, a.Top + (b.Top - a.Top) * k, a.Right + (b.Right - a.Right) * k, a.Bottom + (b.Bottom - a.Bottom) * k);

    static SKColor AgeColor(float f) => f < 0.7f ? Vp.Gain : f < 1.0f ? Vp.Amber : Vp.Loss;

    void DrawAgeRow(SKCanvas c, StrategyModel m, StrategyRow r, float mid, float rk, float a, double t)
    {
        int typical = m.TypicalStint > 0 ? m.TypicalStint : Math.Max(10, r.StintLaps);
        float f = r.StintLaps / (float)typical;
        float numX = cAgeW > 0 ? cAge + cAgeW + 12 : cNameEnd + 12;
        if (cAgeW > 0)
        {
            float wv = cAgeW * Math.Min(1f, f) * rk;
            flat.Color = AgeColor(f).WithA(a);
            c.DrawRoundRect(new SKRect(cAge, mid - 4, cAge + wv, mid + 4), 4, 4, flat);
            if (f > 1f)   // running long: the bar's end flashes
            {
                flat.Color = Vp.Loss.WithA(a * (0.4f + 0.4f * MathF.Sin((float)(t / 220.0))));
                c.DrawCircle(cAge + cAgeW, mid, 6, flat);
            }
        }
        small ??= new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 24f };
        small.Color = (cAgeW > 0 ? Vp.Ivory : AgeColor(f)).WithA(a * rk);
        VpNum.Draw(c, r.StintLaps, 0, numX, mid + 8f, small);
        smallLbl ??= new SKPaint { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 9.5f };
        smallLbl.Color = Vp.Txt3.WithA(a * rk);
        TextBlobs.Draw(c, "LAPS", numX + VpNum.Width(r.StintLaps, 0, small) + 5, mid + 7f, smallLbl);
        if (r.InPit) InPitPill(c, cNext, mid, a, t);
    }

    SKPaint? small, smallLbl, pill;

    void InPitPill(SKCanvas c, float cx, float mid, float a, double t)
    {
        float pulse = 0.65f + 0.35f * MathF.Sin((float)(t / 160.0));
        flat.Color = new SKColor(0xFB, 0x92, 0x3C).WithA(a * pulse);
        c.DrawRoundRect(new SKRect(cx - 34, mid - 12, cx + 34, mid + 12), 3, 3, flat);
        pill ??= new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 19f, TextAlign = SKTextAlign.Center };
        pill.Color = new SKColor(10, 12, 16).WithA(a);
        TextBlobs.Draw(c, "IN PIT", cx, mid + 6.5f, pill);
    }

    float LapX(StrategyModel m, float lap)
    {
        int laps = Math.Max(m.RaceLaps, m.Lap + 1);
        return tl0 + (tl1 - tl0) * Math.Clamp(lap / laps, 0, 1.02f);
    }

    void DrawTimelineRow(SKCanvas c, StrategyModel m, StrategyRow r, float mid, float rk, float a, double t)
    {
        var cls = Vp.ClassLine(m.ClassColor);
        float xNow = LapX(m, m.Lap);
        float reveal = tl0 + (xNow - tl0) * rk;
        c.Save();
        clip.Reset(); clip.AddRect(new SKRect(tl0 - 12, mid - 20, reveal + 12, mid + 20));
        c.ClipPath(clip);
        // stints done: bars between stops, alternating brightness so stints read apart
        int prev = 0;
        for (int i = 0; i <= r.StopLaps.Count; i++)
        {
            int end = i < r.StopLaps.Count ? r.StopLaps[i] : m.Lap;
            float xa = LapX(m, prev) + (i > 0 ? 5 : 0), xb = LapX(m, end) - (i < r.StopLaps.Count ? 5 : 0);
            if (xb > xa)
            {
                flat.Color = (r.IsYou ? new SKColor(0x60, 0xA5, 0xFA) : cls).WithA(a * (i % 2 == 0 ? 0.55f : 0.35f));
                c.DrawRoundRect(new SKRect(xa, mid - 5, xb, mid + 5), 3, 3, flat);
            }
            prev = end;
        }
        for (int i = 0; i < r.StopLaps.Count; i++) Diamond(c, LapX(m, r.StopLaps[i]), mid, 7f, Vp.GoldHi.WithA(a), true);
        c.Restore();
        // the future: planned stops (estimates) after NOW, dashed
        float fk = Vp.EaseIo(Vp.Seg(t, 1100, 1800));
        if (fk > 0f)
        {
            float xEnd = xNow + (tl1 - xNow) * fk;
            dia.Reset(); dia.MoveTo(xNow + 6, mid); dia.LineTo(xEnd, mid);
            dash.Color = Vp.Ivory.WithA(0.35f * a);
            c.DrawPath(dia, dash);
            for (int i = 0; i < r.PlannedLaps.Count; i++)
            {
                float px = LapX(m, r.PlannedLaps[i]);
                if (px <= xEnd) Diamond(c, px, mid, 7f, Vp.GoldPale.WithA(0.9f * a), false);
            }
        }
        if (r.InPit)
        {
            float pulse = 0.5f + 0.5f * MathF.Sin((float)(t / 160.0));
            glow.Color = new SKColor(0xFB, 0x92, 0x3C).WithA(a * pulse);
            c.DrawCircle(xNow, mid, 12f, glow);
        }
    }

    void Diamond(SKCanvas c, float x, float y, float r, SKColor col, bool filled)
    {
        dia.Reset();
        dia.MoveTo(x, y - r); dia.LineTo(x + r, y); dia.LineTo(x, y + r); dia.LineTo(x - r, y); dia.Close();
        if (filled) { flat.Color = col; c.DrawPath(dia, flat); }
        else { stroke.Color = col; c.DrawPath(dia, stroke); }
    }

    void DrawNow(SKCanvas c, StrategyModel m, float a, double t, int n)
    {
        float k = Vp.EaseOutExpo(Vp.Seg(t, 250, 1100));
        float x = tl0 + (LapX(m, m.Lap) - tl0) * k;
        float top = HeadH - 4, bot = HeadH + n * RowH;
        glow.Color = Vp.GoldHi.WithA(0.45f * a);
        c.DrawRect(new SKRect(x - 3, top, x + 3, bot), glow);
        flat.Color = Vp.GoldHi.WithA(a);
        c.DrawRect(new SKRect(x - 0.75f, top, x + 0.75f, bot), flat);
        pill ??= new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 19f, TextAlign = SKTextAlign.Center };
        flat.Color = Vp.GoldHi.WithA(a);
        c.DrawRoundRect(new SKRect(x - 26, bot + 4, x + 26, bot + 24), 3, 3, flat);
        pill.Color = new SKColor(10, 12, 16).WithA(a);
        pill.TextSize = 15f;
        TextBlobs.Draw(c, "NOW", x, bot + 19, pill);
        pill.TextSize = 19f;
    }

    public override void Dispose()
    {
        layer?.Dispose(); theme?.Dispose();
        bandPaint.Shader?.Dispose(); glassPaint.Shader?.Dispose();
        bandPaint.Dispose(); glassPaint.Dispose(); hair.Dispose(); logoPaint.Dispose(); flash.Dispose();
        flat.Dispose(); stroke.Dispose(); dash.Dispose(); glow.Dispose(); fade.Dispose(); clip.Dispose(); dia.Dispose();
        small?.Dispose(); smallLbl?.Dispose(); pill?.Dispose();
    }
}
