using SkiaSharp;

namespace ApexHUD.Native.Overlays;

public enum AwardKind { Stint, LapsLed, Consistent, PhotoFinish }

/// <summary>One award, as iRacing's data had it at the flag (nothing invented).</summary>
/// <param name="Value">the big number ("18", "23", "0.21", "0.084")</param>
/// <param name="Series">the award's own data: stint = the stint's laps (from, to); laps led = (led, race laps);
/// consistent = lap-time differences to the driver's median (s, clean laps only); photo finish = (gap)</param>
/// <param name="Rival">photo finish: the driver beaten to the line ("#23 K. VOSSKAMP")</param>
public sealed record AwardCard(AwardKind Kind, string Driver, string CarNumber, string ClassName, string ClassColor, string Car,
                               string Value, string Detail, float[] Series, string Rival = "", string RivalNumber = "");

/// <summary>
/// V7 round 2 (2026-10-27, Angelo: "not a set of cards all at once - one award at a time, each with a clear own identity
/// like the existing sponsored award cards"). The race awards are members of the story-card family - the cards that
/// already present Move of the Race ("built by" A&amp;B), Fastest Laps (by GO FAST), Cleanest Driver (by Trading Paints):
/// the same slanted card with its print shadow, carbon weave, colour slab and white keyline, the livery stripe along the
/// foot, the eyebrow with "BY [partner]" right after it, the big odometer numerals, the landing flash and kick, and the
/// shared exit (a bar in the card's colour wipes it away). Each award carries its OWN sponsor slot (sponsors.json:
/// AwardStint / AwardLapsLed / AwardConsistent / AwardPhotoFinish) and its own object, the way the Michelin card is a pit
/// tower and the GO FAST card a stopwatch:
/// <list type="bullet">
/// <item>LONGEST STINT - the fuel gauge: the needle falls from F towards E as the laps of the stint count up, and a
/// ribbon of the stint's laps lights one by one between its two pit stops.</item>
/// <item>MOST LAPS LED - the leader light: an LED board (the session bar's yellow-flag light box language) where the
/// number lights column by column.</item>
/// <item>MOST CONSISTENT - the metronome: every clean lap as a tick on a timing strip around the driver's median, the
/// tight band of their spread, a metronome swinging in time.</item>
/// <item>PHOTO FINISH - the film strip: two frames of negative with sprocket holes, the winner and the car beaten to the
/// line either side of the chequered line, the margin stamped across; the camera flash as it lands.</item>
/// </list>
/// Round 4 (2026-10-27, #349 spacing): every award is a tall portrait card in the story cards' right-third column
/// (x 1290..1884, 594 wide, from under the session bar stack to the foot) - the centre third stays clear, never over the
/// tower; it waits for (and gives way to) the battle card / battle map / radio card and anything else on that column
/// (<see cref="Spot"/> + VpLayout, decided by the director). The objects are re-laid for the portrait: the gauge above the
/// odometer, the LED board full width, the timing strip over the value with the metronome beside it, the film strip
/// running top to bottom (P1 frame above the chequered line, P2 below). No label under 13 design px; small greys bright.
/// Static parts are baked when a card is shown; a frame draws a couple of images and the moving parts - no allocation.
/// </summary>
public sealed class RaceAwards : VpGraphic
{
    public const double CardMs = 7600, ExitMs = 520;
    /// <summary>The portrait card (design px): the story cards' right-third column.</summary>
    public const float CW = 594f, CH = 872f;
    const float Sl = 46f;              // the slanted left edge's run across the card's height
    const float L = 84f, R = CW - 28f;  // content edges (clear of the slab / keyline at every height)
    const float ColRight = 1884f, ColTop = 172f, ColBottom = 1044f;
    /// <summary>#340: small labels are never dim (the VisualPack greys predate that pass).</summary>
    static readonly SKColor Lbl = new(0xC6, 0xCB, 0xD3), Lbl2 = new(0xD3, 0xD9, 0xE3);

    AwardCard? card;
    int index, total;
    double shownAt = -1;
    VpLayer? chrome, obj, lit;
    SKColor accent;
    Partner partner;
    SponsorTheme? logoTheme;

    readonly SKPaint flat = new() { IsAntialias = true };
    readonly SKPaint stroke = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint add = new() { IsAntialias = true, BlendMode = SKBlendMode.Plus };
    readonly SKPaint big = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = 170f };
    readonly SKPaint small = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPath path = new(), clip = new();
    static readonly SKPath SlantShape = Slant();
    readonly SKPaint trail = new(), scan = new() { BlendMode = SKBlendMode.Plus };
    readonly ShaderSlot trailShader = new(), scanShader = new();

    /// <summary>The sponsors.json slot for a kind.</summary>
    public static string SlotOf(AwardKind k) => k switch
    {
        AwardKind.Stint => "AwardStint", AwardKind.LapsLed => "AwardLapsLed", AwardKind.Consistent => "AwardConsistent", _ => "AwardPhotoFinish",
    };

    public static string TitleOf(AwardKind k) => k switch
    {
        AwardKind.Stint => "LONGEST STINT", AwardKind.LapsLed => "MOST LAPS LED", AwardKind.Consistent => "MOST CONSISTENT", _ => "PHOTO FINISH",
    };

    /// <summary>The award's own colour when its partner doesn't bring one.</summary>
    static SKColor KindColor(AwardKind k) => k switch
    {
        AwardKind.Stint => new SKColor(0xFF, 0x9F, 0x1C), AwardKind.LapsLed => new SKColor(0xFF, 0xB0, 0x20),
        AwardKind.Consistent => new SKColor(0x5E, 0xE7, 0xFF), _ => new SKColor(0xE6, 0xE9, 0xEE),
    };

    /// <summary>A partner's theme colour (the story cards' partner colours; a Custom partner's from its logo).</summary>
    SKColor PartnerColor(Partner p, SKColor fallback) => p switch
    {
        Partner.AB => new SKColor(0xE8, 0xC2, 0x6A), Partner.Storm => new SKColor(0x4F, 0xB3, 0xFF), Partner.GoFast => new SKColor(0x24, 0xE0, 0x8A),
        Partner.TradingPaints => new SKColor(0x27, 0x9B, 0xF0), Partner.Custom when logoTheme != null => logoTheme.Accent, _ => fallback,
    };

    /// <summary>Where the card goes now (1920x1080 design units): the right-third column, its top under whatever hangs
    /// from the top edge (session bar + watch, the ticker), its foot at the story cards' foot.</summary>
    public static SKRect Spot()
    {
        float top = Math.Max(ColTop, VpLayout.CardTop);
        float h = Math.Min(CH, ColBottom - top);
        return new SKRect(ColRight - CW * (h / CH), top, ColRight, top + h);
    }

    public bool Showing => card != null;
    /// <summary>The award on screen has started its exit.</summary>
    public bool Leaving(double nowMs) => card != null && nowMs - shownAt >= CardMs - ExitMs;
    /// <summary>How far through its time the award on screen is (0..1).</summary>
    public double Progress(double nowMs) => card == null ? 1 : Math.Clamp((nowMs - shownAt) / CardMs, 0, 1);
    public override bool Active(double nowMs) => card != null && nowMs - shownAt < CardMs;
    public override void Hide(double nowMs) { if (Active(nowMs) && nowMs - shownAt < CardMs - ExitMs) shownAt = nowMs - (CardMs - ExitMs); }

    /// <summary>Plays one award now (<paramref name="i"/> of <paramref name="n"/> in tonight's set).</summary>
    public void Show(AwardCard c, int i, int n, double nowMs)
    {
        card = c; index = i; total = n; shownAt = nowMs;
        partner = SponsorPack.For(SlotOf(c.Kind));
        logoTheme?.Dispose(); logoTheme = null;
        if (partner == Partner.Custom) logoTheme = SponsorTheme.FromFile(SponsorPack.LogoPath(partner));
        accent = PartnerColor(partner, KindColor(c.Kind));
        Bake();
    }

    // ------------------------------------------------------------------ bake (event time)
    void Bake()
    {
        chrome?.Dispose(); obj?.Dispose(); lit?.Dispose(); chrome = obj = lit = null;
        var m = card!;
        var col = accent;
        chrome = VpLayer.Record(new SKRect(-40, -40, CW + 40, CH + 40), c =>
        {
            using var shape = Slant();
            // the story cards' body: hard print shadow, flat panel, carbon weave + grain, colour slab + keyline, livery foot
            c.Save(); c.Translate(12, 12);
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 150) }) c.DrawPath(shape, sh);
            c.Restore();
            var c0 = Vp.Mix(new SKColor(6, 8, 12), col, 0.05f); var c1 = Vp.Mix(new SKColor(16, 19, 26), col, 0.1f);
            using (var g = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, CH), new[] { c1.WithAlpha(246), c0.WithAlpha(250) }, null, SKShaderTileMode.Clamp) })
                c.DrawPath(shape, g);
            c.Save(); c.ClipPath(shape, SKClipOperation.Intersect, true);
            Weave(c);
            using (var slab = new SKPath())
            {
                slab.MoveTo(Sl, 0); slab.LineTo(Sl + 14, 0); slab.LineTo(14, CH); slab.LineTo(0, CH); slab.Close();
                using var sp = new SKPaint { IsAntialias = true, Color = col }; c.DrawPath(slab, sp);
            }
            using (var kl = new SKPaint { IsAntialias = true, StrokeWidth = 3, Color = SKColors.White.WithAlpha(210) }) c.DrawLine(Sl + 21, 0, 21, CH, kl);
            using (var top = new SKPaint { Color = col }) c.DrawRect(new SKRect(0, 0, CW, 4), top);
            Livery(c, col);
            c.Restore();
            // the set counter on its own line, then the eyebrow with BY [partner] right after it (or, for a mark too wide
            // to follow the title inside the column, at the top right on the counter's line)
            VpText.Draw(c, "RACE AWARDS  ·  " + (index + 1) + " / " + total, L, 38, Fonts.Inter.Bold, 13f, Lbl, 2.4f);
            float y = 82;
            float ex = VpText.Draw(c, TitleOf(m.Kind), L, y, Fonts.BarlowCondensed.Bold, 26f, col, 3.2f);
            if (partner != Partner.None)
            {
                const float Mh = 40f;
                float verbW = VpText.Width("BY", Fonts.Inter.Bold, 13.6f, 3.3f) + 10;
                float mw = partner == Partner.Custom && logoTheme != null ? logoTheme.Logo.Width * 32f / logoTheme.Logo.Height : Presenters.MarkWidth(partner, Mh);
                bool after = L + ex + 16 + verbW + mw <= R;
                float bx = after ? L + ex + 16 : R - verbW - mw, my = after ? y - 9 : 33;
                if (partner == Partner.Custom && logoTheme != null)
                {
                    bx += VpText.Draw(c, "BY", bx, my + 5, Fonts.Inter.Bold, 13.6f, Lbl, 3.3f) + 10;
                    using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    c.DrawImage(logoTheme.Logo, new SKRect(bx, my - 16, bx + mw, my + 16), lp);
                }
                else Presenters.DrawPresentedAfter(c, partner, "BY", bx, my, Mh, 1f);
            }
            // the driver
            string name = m.Driver.ToUpperInvariant();
            float ns = VpText.Fit(name, Fonts.BarlowCondensed.Black, 64f, R - L + 4, 0.4f, 34f);
            VpText.Draw(c, name, L - 3, 156, Fonts.BarlowCondensed.Black, ns, Vp.Ivory, 0.4f);
            float cx = L;
            cx += Vp.NumberChip(c, m.CarNumber, cx, 188, 24f) + 10;
            var cl = Vp.ClassLine(m.ClassColor);
            using (var cp = new SKPaint { Color = cl }) c.DrawRect(new SKRect(cx, 177, cx + 4, 199), cp);
            string car = (m.ClassName + "  ·  " + m.Car).ToUpperInvariant();
            float cs = VpText.Fit(car, Fonts.Inter.SemiBold, 15f, R - cx - 14, 1.8f, 13f);
            if (VpText.Width(car, Fonts.Inter.SemiBold, cs, 1.8f) > R - cx - 14) car = m.Car.ToUpperInvariant();
            VpText.Draw(c, car, cx + 14, 194, Fonts.Inter.SemiBold, cs, Lbl2, 1.8f);
            // the detail along the foot (two lines at its " · " when it won't fit one at 13)
            float dy = m.Kind == AwardKind.PhotoFinish ? CH - 30 : CH - 38;
            FootLine(c, m.Detail.ToUpperInvariant(), dy);
        });
        switch (m.Kind)
        {
            case AwardKind.Stint: BakeStint(m); break;
            case AwardKind.LapsLed: BakeLed(m); break;
            case AwardKind.Consistent: BakeConsistent(m); break;
            case AwardKind.PhotoFinish: BakePhoto(m); break;
        }
    }

    static void FootLine(SKCanvas c, string s, float y)
    {
        float fs = VpText.Fit(s, Fonts.Inter.Bold, 14f, R - L, 2f, 13f);
        if (VpText.Width(s, Fonts.Inter.Bold, fs, 2f) <= R - L) { VpText.Draw(c, s, L - 2, y, Fonts.Inter.Bold, fs, Lbl, 2f); return; }
        int cut = s.LastIndexOf("  ·  ", StringComparison.Ordinal);
        if (cut < 0) { VpText.Draw(c, s, L - 2, y, Fonts.Inter.Bold, 13f, Lbl, 1.2f); return; }
        VpText.Draw(c, s[..cut], L - 2, y - 22, Fonts.Inter.Bold, 13f, Lbl, 2f);
        VpText.Draw(c, s[(cut + 5)..], L - 2, y, Fonts.Inter.Bold, 13f, Lbl, 2f);
    }

    static SKPath Slant()
    {
        var s = new SKPath();
        s.MoveTo(Sl, 0); s.LineTo(CW, 0); s.LineTo(CW, CH); s.LineTo(0, CH); s.Close();
        return s;
    }

    static void Weave(SKCanvas c)
    {
        using var b = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var cv = new SKCanvas(b))
        {
            cv.Clear(SKColors.Transparent);
            for (int by = 0; by < 2; by++)
                for (int bx = 0; bx < 2; bx++)
                {
                    bool down = (bx + by) % 2 == 0;
                    var rc = new SKRect(bx * 8, by * 8, bx * 8 + 8, by * 8 + 8);
                    using var g = SKShader.CreateLinearGradient(new SKPoint(rc.Left, rc.Top), down ? new SKPoint(rc.Left, rc.Bottom) : new SKPoint(rc.Right, rc.Top),
                        new[] { new SKColor(255, 255, 255, 30), new SKColor(255, 255, 255, 4), new SKColor(0, 0, 0, 60) }, null, SKShaderTileMode.Clamp);
                    using var gp = new SKPaint { Shader = g };
                    cv.DrawRect(rc, gp);
                }
        }
        using (var ws = SKShader.CreateBitmap(b, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat))
        using (var wp = new SKPaint { Shader = ws, Color = SKColors.White.WithAlpha(150) })
            c.DrawRect(new SKRect(0, 0, CW, CH), wp);
        using (var fade = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, CH), new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 120) }, null, SKShaderTileMode.Clamp))
        using (var fp = new SKPaint { Shader = fade }) c.DrawRect(new SKRect(0, 0, CW, CH), fp);
        using (var grain = SKShader.CreatePerlinNoiseFractalNoise(0.85f, 0.85f, 2, 7))
        using (var gp = new SKPaint { Shader = grain, Color = SKColors.White.WithAlpha(26), BlendMode = SKBlendMode.Overlay })
            c.DrawRect(new SKRect(0, 0, CW, CH), gp);
    }

    static void Livery(SKCanvas c, SKColor col)
    {
        float h = 9, y1 = CH, y0 = y1 - h;
        void Cut(float x0, float x1, SKColor cc)
        {
            using var p = new SKPath();
            p.MoveTo(x0 + h, y0); p.LineTo(x1 + h, y0); p.LineTo(x1, y1); p.LineTo(x0, y1); p.Close();
            using var pp = new SKPaint { IsAntialias = true, Color = cc };
            c.DrawPath(p, pp);
        }
        Cut(-20, CW + 20, col);   // #422: the card's colour only (no white + red cuts)
    }

    // ------------------------------------------------------------------ 1. LONGEST STINT - the fuel gauge
    // portrait: the gauge across the top of the object area, the laps rolling up under it, the stint ribbon above the foot
    static readonly SKPoint GaugeC = new((L + R) / 2f, 418);
    const float GaugeR = 170f, G0 = 150f, GSweep = 240f;     // E at 150 deg (lower left) .. F at 30 deg (lower right), clockwise
    const float OdoBase = 694f, OdoSize = 170f, RibY = 750f, RibX0 = L + 26, RibX1 = R - 26;

    void BakeStint(AwardCard m)
    {
        var col = accent;
        obj = VpLayer.Record(new SKRect(20, 210, CW - 4, CH - 60), c =>
        {
            using (var tr = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 24, StrokeCap = SKStrokeCap.Butt, Color = new SKColor(255, 255, 255, 18) })
                c.DrawArc(new SKRect(GaugeC.X - GaugeR, GaugeC.Y - GaugeR, GaugeC.X + GaugeR, GaugeC.Y + GaugeR), G0, GSweep, false, tr);
            using var tk = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Butt, Color = Vp.Ivory.WithAlpha(150) };
            for (int i = 0; i <= 8; i++)
            {
                float a = (G0 + GSweep * i / 8f) * MathF.PI / 180, r0 = GaugeR - (i % 4 == 0 ? 46 : 35), r1 = GaugeR - 20;
                tk.StrokeWidth = i % 4 == 0 ? 4 : 2;
                c.DrawLine(GaugeC.X + MathF.Cos(a) * r0, GaugeC.Y + MathF.Sin(a) * r0, GaugeC.X + MathF.Cos(a) * r1, GaugeC.Y + MathF.Sin(a) * r1, tk);
            }
            void Lbl3(string s, float frac, SKColor cc)
            {
                float a = (G0 + GSweep * frac) * MathF.PI / 180;
                VpText.Draw(c, s, GaugeC.X + MathF.Cos(a) * (GaugeR - 72), GaugeC.Y + MathF.Sin(a) * (GaugeR - 72) + 12, Fonts.BarlowCondensed.Bold, 32f, cc, 0, 0);
            }
            Lbl3("E", 0, Vp.Loss); Lbl3("½", 0.5f, Lbl2); Lbl3("F", 1, Vp.Ivory);
            VpText.Draw(c, "FUEL", GaugeC.X, GaugeC.Y + 66, Fonts.Inter.Bold, 14f, Lbl, 4f, 0);
            // the readout beside the rolling laps (two lines when the number is wide)
            using (var bp = new SKPaint { Typeface = Fonts.BarlowCondensed.Black, TextSize = OdoSize })
            {
                float digitsW = bp.MeasureText("0") * Math.Max(1, m.Value.Length);
                float rx = L + digitsW + 24, room = R - rx;
                const string Lab = "LAPS ON ONE STINT";
                float ly = OdoBase - 58;
                if (VpText.Width(Lab, Fonts.BarlowCondensed.Bold, 26f, 2.6f) <= room) VpText.Draw(c, Lab, rx, ly, Fonts.BarlowCondensed.Bold, 26f, col, 2.6f);
                else
                {
                    VpText.Draw(c, "LAPS ON", rx, ly - 30, Fonts.BarlowCondensed.Bold, 26f, col, 2.6f);
                    VpText.Draw(c, "ONE STINT", rx, ly, Fonts.BarlowCondensed.Bold, 26f, col, 2.6f);
                }
                if (m.Series.Length >= 2)
                {
                    string fr = "FROM LAP " + (int)m.Series[0], to = "TO LAP " + (int)m.Series[1];
                    if (VpText.Width(fr + " " + to, Fonts.Inter.SemiBold, 15f, 2f) <= room) VpText.Draw(c, fr + " " + to, rx, ly + 34, Fonts.Inter.SemiBold, 15f, Lbl2, 2f);
                    else { VpText.Draw(c, fr, rx, ly + 32, Fonts.Inter.SemiBold, 15f, Lbl2, 2f); VpText.Draw(c, to, rx, ly + 56, Fonts.Inter.SemiBold, 15f, Lbl2, 2f); }
                }
            }
            // the ribbon frame, a pit post at each end
            using (var rb = new SKPaint { Color = new SKColor(255, 255, 255, 14) }) c.DrawRect(new SKRect(RibX0, RibY, RibX1, RibY + 22), rb);
            using (var pp = new SKPaint { IsAntialias = true, Color = Vp.Ivory })
            {
                c.DrawRect(new SKRect(RibX0 - 18, RibY - 10, RibX0 - 14, RibY + 32), pp); c.DrawRect(new SKRect(RibX1 + 14, RibY - 10, RibX1 + 18, RibY + 32), pp);
            }
            VpText.Draw(c, "PIT", RibX0 - 16, RibY - 18, Fonts.Inter.Bold, 13f, Lbl, 2f, 0);
            VpText.Draw(c, "PIT", RibX1 + 16, RibY - 18, Fonts.Inter.Bold, 13f, Lbl, 2f, 0);
        });
    }

    void DrawStint(SKCanvas c, AwardCard m, double t, float a)
    {
        int laps = int.TryParse(m.Value, out var v) ? v : 0;
        float k = Vp.EaseOut(Vp.Seg(t, 700, 2900));
        // the needle falls from F towards E (a full tank burnt down to a splash)
        float frac = 1f - 0.86f * k;
        float ang = (G0 + GSweep * frac) * MathF.PI / 180;
        stroke.StrokeWidth = 24; stroke.StrokeCap = SKStrokeCap.Butt;
        stroke.Color = Vp.Mix(Vp.Loss, accent, Vp.Clamp01(frac * 2.2f)).WithA(a);
        c.DrawArc(new SKRect(GaugeC.X - GaugeR, GaugeC.Y - GaugeR, GaugeC.X + GaugeR, GaugeC.Y + GaugeR), G0, GSweep * frac, false, stroke);   // what's left in the tank
        stroke.StrokeWidth = 7; stroke.StrokeCap = SKStrokeCap.Round; stroke.Color = Vp.Ivory.WithA(a);
        c.DrawLine(GaugeC.X - MathF.Cos(ang) * 24, GaugeC.Y - MathF.Sin(ang) * 24, GaugeC.X + MathF.Cos(ang) * (GaugeR - 32), GaugeC.Y + MathF.Sin(ang) * (GaugeR - 32), stroke);
        flat.Color = accent.WithA(a); c.DrawCircle(GaugeC, 17, flat);
        flat.Color = new SKColor(10, 12, 16).WithA(a); c.DrawCircle(GaugeC, 7.5f, flat);
        // the laps rolling up
        big.TextSize = OdoSize; big.Color = Vp.Ivory.WithA(a);
        Odometer(c, laps, L - 4, OdoBase, big, k);
        // the ribbon: one segment per lap, lighting as the needle falls
        int n = Math.Clamp(laps, 1, 60);
        float sw = (RibX1 - RibX0) / n;
        int litN = (int)MathF.Round(n * k);
        for (int i = 0; i < litN; i++)
        {
            flat.Color = (i == litN - 1 && k < 1 ? Vp.Ivory : accent).WithA(a * (0.55f + 0.45f * (i % 2)));
            c.DrawRect(new SKRect(RibX0 + i * sw + 1.5f, RibY + 3, RibX0 + (i + 1) * sw - 1.5f, RibY + 19), flat);
        }
    }

    // ------------------------------------------------------------------ 2. MOST LAPS LED - the leader light (LED board)
    static readonly SKRect Board = new(L, 236, R, 596);
    const float Pitch = 15f;

    // 5x7 dot font (rows top to bottom, bit 4 = left column)
    static readonly Dictionary<char, byte[]> Dots = new()
    {
        ['0'] = new byte[] { 14, 17, 19, 21, 25, 17, 14 }, ['1'] = new byte[] { 4, 12, 4, 4, 4, 4, 14 }, ['2'] = new byte[] { 14, 17, 1, 2, 4, 8, 31 },
        ['3'] = new byte[] { 31, 2, 4, 2, 1, 17, 14 }, ['4'] = new byte[] { 2, 6, 10, 18, 31, 2, 2 }, ['5'] = new byte[] { 31, 16, 30, 1, 1, 17, 14 },
        ['6'] = new byte[] { 6, 8, 16, 30, 17, 17, 14 }, ['7'] = new byte[] { 31, 1, 2, 4, 8, 8, 8 }, ['8'] = new byte[] { 14, 17, 17, 14, 17, 17, 14 },
        ['9'] = new byte[] { 14, 17, 17, 15, 1, 2, 12 }, ['L'] = new byte[] { 16, 16, 16, 16, 16, 16, 31 }, ['A'] = new byte[] { 14, 17, 17, 31, 17, 17, 17 },
        ['P'] = new byte[] { 30, 17, 17, 30, 16, 16, 16 }, ['S'] = new byte[] { 15, 16, 16, 14, 1, 1, 30 }, ['E'] = new byte[] { 31, 16, 16, 30, 16, 16, 31 },
        ['D'] = new byte[] { 28, 18, 17, 17, 17, 18, 28 }, [' '] = new byte[] { 0, 0, 0, 0, 0, 0, 0 },
    };

    void BakeLed(AwardCard m)
    {
        var col = accent;
        // dim board + every LED off
        obj = VpLayer.Record(new SKRect(Board.Left - 30, Board.Top - 30, Board.Right + 30, Board.Bottom + 150), c =>
        {
            using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 160), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14) })
                c.DrawRoundRect(new SKRect(Board.Left, Board.Top + 8, Board.Right, Board.Bottom + 8), 14, 14, sh);
            using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(8, 7, 5) }) c.DrawRoundRect(Board, 14, 14, bg);
            using (var rim = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(255, 255, 255, 30) }) c.DrawRoundRect(Board, 14, 14, rim);
            using var off = new SKPaint { IsAntialias = true, Color = Vp.Mix(new SKColor(26, 22, 14), col, 0.12f) };
            for (float y = Board.Top + 18; y < Board.Bottom - 10; y += Pitch)
                for (float x = Board.Left + 18; x < Board.Right - 10; x += Pitch) c.DrawCircle(x, y, 5.2f, off);
            VpText.Draw(c, "LAPS LED", Board.MidX, Board.Bottom + 58, Fonts.BarlowCondensed.Bold, 36f, col, 6f, 0);
            if (m.Series.Length >= 2 && m.Series[1] > 0)
            {
                string a = "OF " + (int)m.Series[1] + " LAPS", b = (int)MathF.Round(m.Series[0] * 100 / m.Series[1]) + "% OF THE RACE IN FRONT";
                string one = a + "  ·  " + b;
                if (VpText.Width(one, Fonts.Inter.SemiBold, 15f, 2f) <= Board.Width) VpText.Draw(c, one, Board.MidX, Board.Bottom + 94, Fonts.Inter.SemiBold, 15f, Lbl2, 2f, 0);
                else { VpText.Draw(c, a, Board.MidX, Board.Bottom + 94, Fonts.Inter.SemiBold, 15f, Lbl2, 2f, 0); VpText.Draw(c, b, Board.MidX, Board.Bottom + 118, Fonts.Inter.SemiBold, 15f, Lbl2, 2f, 0); }
            }
        });
        // the lit number: big digits, centred on the grid
        string txt = m.Value;
        int cols = 0; foreach (char ch in txt) cols += 6;
        int gridCols = (int)((Board.Width - 28) / Pitch), gridRows = (int)((Board.Height - 28) / Pitch);
        int scale = Math.Max(1, Math.Min((gridRows - 2) / 7, (gridCols - 2) / Math.Max(1, cols - 1)));
        int startCol = (gridCols - (cols - 1) * scale) / 2, startRow = (gridRows - 7 * scale) / 2;
        lit = VpLayer.Record(new SKRect(Board.Left - 30, Board.Top - 30, Board.Right + 30, Board.Bottom + 30), c =>
        {
            using var glow = new SKPaint { IsAntialias = true, Color = col.WithAlpha(110), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7) };
            using var on = new SKPaint { IsAntialias = true, Color = Vp.Mix(col, SKColors.White, 0.25f) };
            int cc = startCol;
            foreach (char ch in txt)
            {
                if (Dots.TryGetValue(ch, out var rows))
                    for (int r = 0; r < 7; r++)
                        for (int b = 0; b < 5; b++)
                            if ((rows[r] >> (4 - b) & 1) != 0)
                                for (int sy = 0; sy < scale; sy++)
                                    for (int sx = 0; sx < scale; sx++)
                                    {
                                        float x = Board.Left + 18 + (cc + b * scale + sx) * Pitch, y = Board.Top + 18 + (startRow + r * scale + sy) * Pitch;
                                        c.DrawCircle(x, y, 9f, glow); c.DrawCircle(x, y, 5.6f, on);
                                    }
                cc += 6 * scale;
            }
        });
    }

    void DrawLed(SKCanvas c, AwardCard m, double t, float a)
    {
        // lights on column by column, then a slow scan of brightness across the board
        float k = Vp.Seg(t, 650, 1700);
        float rx = Board.Left + (Board.Width + 40) * k;
        c.Save();
        clip.Reset(); clip.AddRect(new SKRect(Board.Left - 30, Board.Top - 30, rx, Board.Bottom + 30));
        c.ClipPath(clip);
        float flick = t < 1900 ? (((int)(t / 45)) % 5 == 0 ? 0.7f : 1f) : 1f;
        lit!.Draw(c, 0, 0, a * flick);
        c.Restore();
        if (k >= 1)
        {
            float sx = Board.Left - 200 + (float)((t - 1700) % 2600 / 2600.0) * (Board.Width + 400);
            c.Save();
            clip.Reset(); clip.AddRoundRect(Board, 14, 14);
            c.ClipPath(clip, SKClipOperation.Intersect, true);
            // one shader at the origin, the canvas moved (a shader made at sx would be a new object every frame)
            scan.Shader = scanShader.Linear(new SKPoint(-120, 0), new SKPoint(120, 0), [SKColors.White.WithAlpha(0), SKColors.White.WithAlpha(26), SKColors.White.WithAlpha(0)], null);
            scan.Color = SKColors.White.WithA(a);
            c.Translate(sx, 0);
            c.DrawRect(new SKRect(-120, Board.Top, 120, Board.Bottom), scan);
            c.Restore();
        }
    }

    // ------------------------------------------------------------------ 3. MOST CONSISTENT - the metronome
    // portrait: the timing strip across the top, the value under it with the metronome swinging beside it
    static readonly SKRect Strip = new(L + 66, 236, R - 6, 456);
    static readonly SKPoint Metro = new(R - 82, 768);
    const float ValBase = 642f;

    void BakeConsistent(AwardCard m)
    {
        var col = accent;
        obj = VpLayer.Record(new SKRect(20, 210, CW - 4, CH - 60), c =>
        {
            // timing strip: graph paper, the median line, +-1 s
            using (var bg = new SKPaint { Color = new SKColor(255, 255, 255, 8) }) c.DrawRect(Strip, bg);
            using var gp = new SKPaint { Color = new SKColor(255, 255, 255, 14), StrokeWidth = 1 };
            for (float x = Strip.Left; x <= Strip.Right + 0.5f; x += Strip.Width / 16f) c.DrawLine(x, Strip.Top, x, Strip.Bottom, gp);
            for (float y = Strip.Top; y <= Strip.Bottom + 0.5f; y += Strip.Height / 8f) c.DrawLine(Strip.Left, y, Strip.Right, y, gp);
            using (var md = new SKPaint { Color = Vp.Ivory.WithAlpha(120), StrokeWidth = 1.5f }) c.DrawLine(Strip.Left, Strip.MidY, Strip.Right, Strip.MidY, md);
            VpText.Draw(c, "+1.0 S", Strip.Left - 8, Strip.Top + 13, Fonts.Inter.SemiBold, 13f, Lbl, 0.6f, 1);
            VpText.Draw(c, "MEDIAN", Strip.Left - 8, Strip.MidY + 5, Fonts.Inter.SemiBold, 13f, Vp.Ivory, 0.6f, 1);
            VpText.Draw(c, "-1.0 S", Strip.Left - 8, Strip.Bottom - 1, Fonts.Inter.SemiBold, 13f, Lbl, 0.6f, 1);
            const string Cap = "EVERY CLEAN LAP AGAINST THEIR OWN MEDIAN";
            VpText.Draw(c, Cap, L, Strip.Bottom + 32, Fonts.Inter.Bold, VpText.Fit(Cap, Fonts.Inter.Bold, 13.5f, R - L, 1.6f, 13f), Lbl, 1.6f);
            // under the value: what it is
            VpText.Draw(c, "SECONDS", L, ValBase + 46, Fonts.BarlowCondensed.Bold, 26f, col, 2.6f);
            VpText.Draw(c, "TYPICAL VARIATION", L, ValBase + 78, Fonts.BarlowCondensed.Bold, 22f, Lbl2, 2.4f);
            VpText.Draw(c, "LAP TO LAP", L, ValBase + 104, Fonts.BarlowCondensed.Bold, 22f, Lbl2, 2.4f);
            // the metronome's body
            var mc = Metro;
            using (var body = new SKPath())
            {
                body.MoveTo(mc.X - 64, mc.Y + 36); body.LineTo(mc.X + 64, mc.Y + 36); body.LineTo(mc.X + 30, mc.Y - 240); body.LineTo(mc.X - 30, mc.Y - 240); body.Close();
                using var bp = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(mc.X - 64, 0), new SKPoint(mc.X + 64, 0),
                    new[] { new SKColor(24, 28, 36), new SKColor(44, 50, 62), new SKColor(18, 20, 26) }, null, SKShaderTileMode.Clamp) };
                c.DrawPath(body, bp);
                using var be = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = col.WithAlpha(120) };
                c.DrawPath(body, be);
            }
            using var sc = new SKPaint { IsAntialias = true, StrokeWidth = 1.5f, Color = Vp.Ivory.WithAlpha(70) };
            for (int i = -4; i <= 4; i++) { float yy = mc.Y - 212 + (i + 4) * 20; c.DrawLine(mc.X - 8, yy, mc.X + 8, yy, sc); }
        });
    }

    void DrawConsistent(SKCanvas c, AwardCard m, double t, float a)
    {
        var d = m.Series;
        int n = d.Length;
        // the spread band (one standard deviation), then the laps ticking in
        float sd = 0; for (int i = 0; i < n; i++) sd += d[i] * d[i]; sd = n > 1 ? MathF.Sqrt(sd / n) : 0;
        float band = Math.Min(1f, sd) * Strip.Height / 2;
        float k = Vp.Seg(t, 600, 2600);
        flat.Color = accent.WithA(0.16f * a * Vp.Seg(t, 500, 900));
        c.DrawRect(new SKRect(Strip.Left, Strip.MidY - band, Strip.Left + Strip.Width * Math.Max(k, 0.001f), Strip.MidY + band), flat);
        int shown = (int)MathF.Ceiling(n * k);
        for (int i = 0; i < shown; i++)
        {
            float x = Strip.Left + Strip.Width * (i + 0.5f) / Math.Max(1, n);
            float y = Strip.MidY - Math.Clamp(d[i], -1f, 1f) * Strip.Height / 2;
            stroke.StrokeWidth = 2; stroke.StrokeCap = SKStrokeCap.Round; stroke.Color = accent.WithA(0.5f * a);
            c.DrawLine(x, Strip.MidY, x, y, stroke);
            flat.Color = (i == shown - 1 && k < 1 ? Vp.Ivory : accent).WithA(a);
            c.DrawCircle(x, y, 5f, flat);
        }
        // the metronome swings (a beat a second)
        var mc = Metro;
        float swing = MathF.Sin((float)(t / 1000.0 * Math.PI)) * 17f * Vp.Seg(t, 400, 900);
        c.Save(); c.RotateDegrees(swing, mc.X, mc.Y + 10);
        stroke.StrokeWidth = 5; stroke.Color = Vp.Ivory.WithA(a);
        c.DrawLine(mc.X, mc.Y + 10, mc.X, mc.Y - 242, stroke);
        flat.Color = accent.WithA(a); c.DrawRoundRect(new SKRect(mc.X - 19, mc.Y - 186, mc.X + 19, mc.Y - 158), 4, 4, flat);
        c.Restore();
        flat.Color = new SKColor(60, 66, 80).WithA(a); c.DrawCircle(mc.X, mc.Y + 10, 9, flat);
        // the value
        big.TextSize = 116f; big.Color = Vp.Ivory.WithA(a * Vp.Seg(t, 900, 1300));
        TextBlobs.Draw(c, m.Value, L - 4, ValBase, big);
    }

    // ------------------------------------------------------------------ 4. PHOTO FINISH - the film strip
    // portrait: the negative runs top to bottom - P1's frame above the chequered line, P2's below - and winds down into place
    static readonly SKRect Film = new(66, 220, CW - 12, 640);

    void BakePhoto(AwardCard m)
    {
        var col = accent;
        obj = VpLayer.Record(new SKRect(20, 180, CW + 20, CH - 60), c =>
        {
            // the negative: warm dark film base, sprocket holes down both edges
            using (var fb = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(Film.Left, 0), new SKPoint(Film.Right, 0),
                       new[] { new SKColor(40, 28, 16), new SKColor(22, 15, 9), new SKColor(40, 28, 16) }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(Film, fb);
            using var hole = new SKPaint { IsAntialias = true, Color = new SKColor(4, 4, 6) };
            for (float y = Film.Top + 10; y + 18 < Film.Bottom; y += 32)
            {
                c.DrawRoundRect(new SKRect(Film.Left + 10, y, Film.Left + 28, y + 18), 3, 3, hole);
                c.DrawRoundRect(new SKRect(Film.Right - 28, y, Film.Right - 10, y + 18), 3, 3, hole);
            }
            // the two frames
            float mid = Film.MidY;
            var f1 = new SKRect(Film.Left + 40, Film.Top + 20, Film.Right - 40, mid - 22);
            var f2 = new SKRect(Film.Left + 40, mid + 22, Film.Right - 40, Film.Bottom - 20);
            foreach (var (f, pos, num, who, win) in new[] { (f1, "P1", m.CarNumber, m.Driver, true), (f2, "P2", m.RivalNumber, m.Rival, false) })
            {
                using (var fr = new SKPaint { Shader = SKShader.CreateRadialGradient(new SKPoint(f.MidX, f.MidY), f.Width * 0.7f,
                           new[] { new SKColor(70, 74, 82), new SKColor(28, 30, 36) }, null, SKShaderTileMode.Clamp) })
                    c.DrawRect(f, fr);
                // slit-scan streaks: a photo-finish camera smears everything that moves into horizontal lines
                var rr = new Random(win ? 7 : 11);
                using (var st = new SKPaint { IsAntialias = true })
                    for (int i = 0; i < 50; i++)
                    {
                        float yy = f.Top + 6 + (float)rr.NextDouble() * (f.Height - 12), ww = 40 + (float)rr.NextDouble() * f.Width * 0.8f, xx = f.Left + (float)rr.NextDouble() * (f.Width - ww);
                        st.Color = SKColors.White.WithAlpha((byte)(6 + rr.Next(16)));
                        c.DrawRect(new SKRect(xx, yy, xx + ww, yy + 1 + (float)rr.NextDouble() * 2.5f), st);
                    }
                VpText.Draw(c, pos, f.Left + 18, f.MidY + 34, Fonts.BarlowCondensed.Black, 96f, win ? col : Lbl2, 0.5f);
                float nx = f.Left + 142;
                Vp.NumberChip(c, num, nx, f.Top + 58, 28f);
                string nm = who.ToUpperInvariant();
                VpText.Draw(c, nm, nx, f.Top + 120, Fonts.BarlowCondensed.Bold, VpText.Fit(nm, Fonts.BarlowCondensed.Bold, 36f, f.Right - 16 - nx, 0.4f, 22f), Vp.Ivory, 0.4f);
                VpText.Draw(c, win ? "WINNER" : "BEATEN TO THE LINE", nx, f.Top + 150, Fonts.Inter.Bold, 13f, win ? col : Lbl, 2f);
                // frame number on the film edge, like a real negative
                VpText.Draw(c, win ? "1A" : "2A", f.Right - 8, f.Top + 20, Fonts.Inter.Bold, 13f, new SKColor(240, 176, 92), 1.4f, 1);
            }
            // the chequered line between them
            using var w1 = new SKPaint { Color = SKColors.White }; using var b1 = new SKPaint { Color = new SKColor(10, 10, 12) };
            float x0 = Film.Left + 36, sq = 12.5f;
            int cols = (int)((Film.Right - 36 - x0) / sq);
            for (int i = 0; i < cols; i++)
                for (int j = 0; j < 2; j++)
                    c.DrawRect(new SKRect(x0 + i * sq, mid - 12 + j * 12, x0 + (i + 1) * sq, mid + j * 12), (i + j) % 2 == 0 ? w1 : b1);
            VpText.Draw(c, "MARGIN AT THE LINE", R, Film.Bottom + 36, Fonts.BarlowCondensed.Bold, 24f, col, 2.6f, 1);
        });
    }

    void DrawPhoto(SKCanvas c, AwardCard m, double t, float a)
    {
        // the gap stamped across, after the film stops
        float k = Vp.EaseOutBack(Vp.Seg(t, 1200, 1500));
        if (k > 0.01f)
        {
            big.TextSize = 112f;
            big.Color = Vp.Ivory.WithA(a * Vp.Clamp01(k));
            float tw = TextBlobs.Measure(m.Value, big);
            c.Save(); c.Translate((L + R) / 2f - 34, 784); c.Scale(1.4f - 0.4f * k); c.RotateDegrees(-3);
            flat.Color = new SKColor(10, 12, 16).WithA(0.85f * a * Vp.Clamp01(k));
            c.DrawRoundRect(new SKRect(-tw / 2 - 26, -96, tw / 2 + 62, 18), 6, 6, flat);
            stroke.StrokeWidth = 3; stroke.Color = accent.WithA(a * Vp.Clamp01(k));
            c.DrawRoundRect(new SKRect(-tw / 2 - 26, -96, tw / 2 + 62, 18), 6, 6, stroke);
            TextBlobs.Draw(c, m.Value, -tw / 2 - 8, 0, big);
            small.TextSize = 30f; small.Typeface = Fonts.BarlowCondensed.Bold; small.Color = accent.WithA(a * Vp.Clamp01(k));
            TextBlobs.Draw(c, "S", tw / 2 + 4, -4, small);
            small.Typeface = Fonts.Inter.Bold;
            c.Restore();
        }
        // the camera flash as the frame lands
        float fl = MathF.Max(0, 1f - (float)Math.Abs(t - 1050) / 140f);
        if (fl > 0) { add.Color = SKColors.White.WithA(0.5f * fl * a); c.DrawRect(new SKRect(0, 0, CW, CH), add); }
    }

    // ------------------------------------------------------------------ the rolling odometer
    static readonly string[] Digits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

    void Odometer(SKCanvas c, int value, float x, float baseline, SKPaint p, float k)
    {
        Span<char> buf = stackalloc char[8];
        value.TryFormat(buf, out int n);
        float cell = TextBlobs.Measure("0", p);
        var keep = p.Color;
        for (int i = 0; i < n; i++)
        {
            int dgt = buf[i] - '0';
            float ki = Vp.EaseOut(Vp.Clamp01((k - i * 0.06f) / Math.Max(0.2f, 1 - n * 0.06f)));
            float v = (dgt + 10) * ki;
            int lo = (int)MathF.Floor(v); float fr = v - lo;
            c.Save();
            clip.Reset(); clip.AddRect(new SKRect(x + i * cell - 4, baseline - p.TextSize * 0.78f, x + (i + 1) * cell + 4, baseline + 6));
            c.ClipPath(clip);
            p.Color = keep.WithAlpha((byte)(keep.Alpha * (1 - fr)));
            TextBlobs.Draw(c, Digits[lo % 10], x + i * cell, baseline - fr * p.TextSize * 0.45f, p);
            if (fr > 0.001f) { p.Color = keep.WithAlpha((byte)(keep.Alpha * fr)); TextBlobs.Draw(c, Digits[(lo + 1) % 10], x + i * cell, baseline + (1 - fr) * p.TextSize * 0.45f, p); }
            c.Restore();
        }
        p.Color = keep;
    }

    // ------------------------------------------------------------------ draw
    public override void Draw(SKCanvas c, int w, int h, double nowMs)
    {
        Vp.LastScale = Vp.DeviceScale(c);
        var m = card;
        if (m == null || chrome == null) return;
        double t = nowMs - shownAt;
        if (t >= CardMs) { card = null; VpLayout.Release(VpLayout.SlotAwards); return; }
        var r = Spot();
        float sc = r.Height / CH;
        VpLayout.Claim(VpLayout.SlotAwards, r, nowMs);
        float outK = Vp.EaseIo(Vp.Seg(t, CardMs - ExitMs, CardMs));
        int save = c.Save();
        c.Translate(r.Left, r.Top);
        c.Scale(sc);
        // the shared exit: a bar in the card's colour sweeps across
        float bandX = -60 + (CW + 140) * outK;
        if (outK > 0) { clip.Reset(); clip.AddRect(new SKRect(bandX, -60, CW + 80, CH + 80)); c.ClipPath(clip); }
        // entrances: each award its own (stint rises like the tank filling, laps led powers on, consistent wipes in on the
        // beat, photo finish slides in like film)
        int inner = c.Save();
        switch (m.Kind)
        {
            case AwardKind.Stint:
            {
                float rise = Vp.EaseOut(Vp.Seg(t, 0, 650));
                clip.Reset(); clip.AddRect(new SKRect(-40, CH + 40 - (CH + 80) * rise, CW + 40, CH + 40)); c.ClipPath(clip);
                break;
            }
            case AwardKind.LapsLed:
            {
                float open = Vp.EaseOut(Vp.Seg(t, 0, 420));
                clip.Reset(); clip.AddRect(new SKRect(-40, CH / 2 - (CH / 2 + 40) * open, CW + 40, CH / 2 + (CH / 2 + 40) * open)); c.ClipPath(clip);
                break;
            }
            case AwardKind.Consistent:
            {
                float wipe = Vp.EaseIo(Vp.Seg(t, 0, 600));
                clip.Reset(); clip.AddRect(new SKRect(-40, -40, -40 + (CW + 80) * wipe, CH + 40)); c.ClipPath(clip);
                break;
            }
            default:
            {
                float slide = (1f - Vp.EaseOut(Vp.Seg(t, 0, 700))) * (CW + 100);
                c.Translate(slide, 0);
                break;
            }
        }
        // landing kick (as the story cards do)
        double ti = t - 620;
        if (ti > 0 && ti < 400)
        {
            float amp = 6f * (float)Math.Exp(-ti / 1000.0 * 11);
            c.Translate((float)Math.Sin(ti / 1000.0 * 70) * amp, (float)Math.Cos(ti / 1000.0 * 57) * amp * 0.6f);
        }
        chrome.Draw(c, 0, 0, 1f);
        float a = 1f;
        // the object: photo finish film winds down into place on its own (a beat behind the card)
        if (m.Kind == AwardKind.PhotoFinish)
        {
            float film = -(1f - Vp.EaseOut(Vp.Seg(t, 250, 1050))) * (CH + 200);
            float judder = t > 1050 && t < 1250 ? MathF.Sin((float)(t - 1050) / 18f) * 4f * (1 - (float)(t - 1050) / 200f) : 0f;
            c.Save();
            c.ClipPath(SlantShape, SKClipOperation.Intersect, true);
            obj?.Draw(c, 0, film + judder, a);
            c.Restore();
            DrawPhoto(c, m, t, a);
        }
        else
        {
            obj?.Draw(c, 0, 0, a * Vp.Seg(t, 300, 700));
            if (m.Kind == AwardKind.Stint) DrawStint(c, m, t, a);
            else if (m.Kind == AwardKind.LapsLed) DrawLed(c, m, t, a);
            else DrawConsistent(c, m, t, a);
        }
        // landing flash + light streak across the card
        if (ti > 0 && ti < 500)
        {
            if (ti < 200) { add.Color = SKColors.White.WithA(0.27f * (1 - (float)ti / 200f)); c.DrawRect(new SKRect(0, 0, CW, CH), add); }
            float sx = -160 + (CW + 320) * Vp.EaseOut((float)(ti / 500));
            path.Reset(); path.MoveTo(sx + 70, 0); path.LineTo(sx + 120, 0); path.LineTo(sx + 50, CH); path.LineTo(sx, CH); path.Close();
            add.Color = SKColors.White.WithA(0.18f); c.DrawPath(path, add);
        }
        c.RestoreToCount(inner);
        if (outK > 0 && outK < 1)
        {
            c.RestoreToCount(save); save = c.Save();
            c.Translate(r.Left, r.Top); c.Scale(sc);
            trail.Shader = trailShader.Linear(new SKPoint(-220, 0), new SKPoint(0, 0), [accent.WithAlpha(0), accent.WithAlpha(120)], null);
            trail.Color = SKColors.White;
            c.Save(); c.Translate(bandX, 0);
            c.DrawRect(new SKRect(Math.Max(-60 - bandX, -220), -10, 0, CH + 10), trail);
            c.Restore();
            flat.Color = accent; c.DrawRect(new SKRect(bandX, -10, bandX + 26, CH + 10), flat);
            flat.Color = SKColors.White.WithAlpha(160); c.DrawRect(new SKRect(bandX + 23, -10, bandX + 26, CH + 10), flat);
        }
        c.RestoreToCount(save);
    }

    public override void Dispose()
    {
        chrome?.Dispose(); obj?.Dispose(); lit?.Dispose(); logoTheme?.Dispose();
        flat.Dispose(); stroke.Dispose(); add.Dispose(); big.Dispose(); small.Dispose(); path.Dispose(); clip.Dispose(); trail.Dispose(); trailShader.Dispose(); scan.Dispose(); scanShader.Dispose();
    }
}
