using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-18 (Angelo, design "B - Front Row Heroes"): the PRE-RACE body of the
/// starting-grid board. One page per class:
///   class strip (class logo, page dots)
///   FRONT ROW: two big hero cards (POLE in gold, P2)
///   THE REST:  two-column list - pos, number, driver, brand logo, quali time, gap
/// 2026-10-19 (Angelo: "bring the grid cards up to the quality of the rest of the intro"):
/// every page now BUILDS itself, like the info cards:
///   strip     - class-colour band wipes in, racing chevrons drift through it, the class
///               logo slides in and a light sheen passes over it
///   heroes    - cards rise in (pole first) with a little overshoot, the brand watermark
///               parallaxes in, the big number rolls up into its window, the name wipes in
///               behind an accent edge, the qualifying time decodes digit by digit, and a
///               sheen sweeps the cards every few seconds
///   rows      - cascade in from the left one after another, a class-colour flash on the
///               leading edge, and a gap-to-pole bar grows under each row
/// The page clock is supplied by the caller (seconds since that page started; 99 = built).
/// Logo backdrops are pre-scaled ONCE into cached images.
/// </summary>
public sealed class GridHeroBoard : IDisposable
{
    const float StripH = 84f, StripGap = 12f, HeroH = 236f, HeroGap = 16f, ListGap = 16f, RowH = 28f;
    const double CrossFadeMs = 450, MinPageMs = 8000;
    /// <summary>Page clock value meaning "fully built, no live effects".</summary>
    public const double Built = 99;

    static readonly SKColor Gold      = new(0xE8, 0xC2, 0x5A);
    static readonly SKColor Txt       = new(0xF4, 0xF6, 0xFA);
    // #340 (legibility, read from across the room): Txt2 was B9C1CF, Txt3 was 7E8AA0 (~55% - too dim on the TV)
    static readonly SKColor Txt2      = new(0xD3, 0xD9, 0xE3);
    static readonly SKColor Txt3      = new(0xB4, 0xBE, 0xCE);
    static readonly SKColor PlayerBlue= new(0x4D, 0x9F, 0xFF);

    readonly Dictionary<string, SKBitmap?> logos = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SKImage?> backdrops = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SKBitmap?> classLogos = new(StringComparer.OrdinalIgnoreCase);

    // 2026-10-24 (Angelo: "the intro is horribly choppy"): the class pages used to make ~40 SKPaints, a dozen
    // shaders, text blobs for every string and a fresh set of strings (names, lap times, gaps) on every frame -
    // ~200 KB of garbage per frame. Now paints/paths/shaders are kept (shaders rebuilt only when their colours
    // or geometry change), strings are made once per value, and text is drawn from cached blobs (TextBlobs).
    readonly SKPaint fill = new(), layer = new(), logoPaint = new(), numPaint = new(), namePaint = new(), carPaint = new(),
        qtPaint = new(), gapPaint = new(), rowPaint = new(), chipText = new(), chipBg = new(), measure = new();
    readonly SKRoundRect clipRR = new();
    readonly SKPath piece = new(), arrowHead = MakeArrowHead();
    readonly List<SKPoint> ribL = new(), ribR = new(); readonly List<float> ribT = new();
    readonly List<Driver> sorted = new();
    static readonly SKMaskFilter blur3 = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 3),
        solid25 = SKMaskFilter.CreateBlur(SKBlurStyle.Solid, 2.5f), solid3 = SKMaskFilter.CreateBlur(SKBlurStyle.Solid, 3);
    readonly SKPaint carShadowPaint = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 5f) };
    static SKPath MakeArrowHead()
    {
        var head = new SKPath();
        head.MoveTo(13, 0); head.LineTo(-3, -6.5f); head.LineTo(0, 0); head.LineTo(-3, 6.5f); head.Close();
        return head;
    }
    /// <summary>The shaders / shadows of one class page (two pages are on screen during a class change).</summary>
    sealed class PageFx
    {
        public readonly ShaderSlot StripBg = new(), StripHl = new(), LogoSheen = new(), SeriesLift = new(), Mask = new();
        public readonly ShadowSlot LogoShadow = new(), SeriesShadow = new();
        public readonly ShaderSlot[] HeroBg = { new(), new() }, HeroGlow = { new(), new() }, HeroWash = { new(), new() }, HeroHl = { new(), new() }, HeroSheen = { new(), new() };
        public readonly ShadowSlot[] HeroNum = { new(), new() };
        public readonly List<ShaderSlot> RowFlash = new(), RowGap = new();
        public ShaderSlot Row(List<ShaderSlot> l, int i) { while (l.Count <= i) l.Add(new ShaderSlot()); return l[i]; }
    }
    readonly List<PageFx> pageFx = new();
    PageFx Fx(int page) { page = Math.Clamp(page, 0, 31); while (pageFx.Count <= page) pageFx.Add(new PageFx()); return pageFx[page]; }
    PageFx fx = null!;   // the page being drawn

    // strings made once per value instead of every frame
    readonly Dictionary<(string?, string?, float, IntPtr, float), string> nameFits = new();
    readonly Dictionary<(string?, float, float), string> carFits = new();
    readonly Dictionary<float, string> laps = new(), gaps = new();
    readonly Dictionary<string, string?> brandSlugs = new();
    readonly Dictionary<string, string> classNames = new();
    readonly Dictionary<string, (string Key, SKBitmap? Logo)> classLogoByName = new();
    string NameFit(Driver d, SKPaint p, float maxW)
    {
        var k = (d.Name, d.AbbrevName, p.TextSize, p.Typeface?.Handle ?? IntPtr.Zero, maxW);
        if (nameFits.TryGetValue(k, out var v)) return v;
        if (nameFits.Count > 512) nameFits.Clear();
        return nameFits[k] = Fit(NameFormat.Broadcast(d), p, maxW);
    }
    string CarFit(string? car, SKPaint p, float maxW)
    {
        var k = (car, p.TextSize, maxW);
        if (carFits.TryGetValue(k, out var v)) return v;
        if (carFits.Count > 256) carFits.Clear();
        return carFits[k] = Fit((car ?? "").ToUpperInvariant(), p, maxW);
    }
    string Lap(float s) { if (!laps.TryGetValue(s, out var v)) { if (laps.Count > 512) laps.Clear(); laps[s] = v = FormatLap(s); } return v; }
    string Gap(float g) { if (!gaps.TryGetValue(g, out var v)) { if (gaps.Count > 512) gaps.Clear(); gaps[g] = v = FormatGap(g); } return v; }

    // ---- 2026-10-28 (#486, Angelo: "the post race stuff needs to bring the same exact design language as the pre race"):
    // the same class page shows the RESULT after the flag: hero cards = winner + P2, rows = the rest in finishing order;
    // time column = best race lap, gap column = finishing gap to the class winner (+N LAPS when lapped, DSQ). Only
    // iRacing's own numbers; a gap it doesn't give stays blank.
    /// <summary>Show the race result instead of the qualifying order.</summary>
    public bool Results { get; set; }
    Driver? winner;
    readonly Dictionary<float, string> bests = new();
    static readonly string[] lapsDown = Enumerable.Range(0, 100).Select(n => n == 1 ? "+1 LAP" : $"+{n} LAPS").ToArray();
    string Best(float s) { if (!bests.TryGetValue(s, out var v)) { if (bests.Count > 512) bests.Clear(); bests[s] = v = "BEST " + FormatLap(s); } return v; }
    float TimeOf(Driver d) => Results ? d.BestLapTime : d.QualifyTime;
    /// <summary>Seconds behind the reference (pole / class winner); -n = n laps down; NaN = not known.</summary>
    float GapVal(Driver d, float poleTime)
    {
        if (!Results) return d.QualifyTime > 0 && poleTime > 0 ? d.QualifyTime - poleTime : float.NaN;
        var w = winner;
        if (w == null || d.CarIdx == w.CarIdx) return 0;
        int down = w.LapsCompleted - d.LapsCompleted;
        if (down >= 1) return -Math.Min(down, 99);
        if (d.GapToLeaderSeconds is double g && w.GapToLeaderSeconds is double g0 && g >= g0) return (float)(g - g0);
        return float.NaN;
    }
    string GapText(Driver d, float poleTime)
    {
        if (Results && d.Disqualified) return "DSQ";
        float v = GapVal(d, poleTime);
        if (float.IsNaN(v)) return "";
        if (v < 0) return lapsDown[(int)-v];
        return Gap(v);
    }
    string? BrandSlug(string? brand)
    {
        var b = brand ?? "";
        if (!brandSlugs.TryGetValue(b, out var v)) brandSlugs[b] = v = ManufacturerBrand.Resolve(b);
        return v;
    }
    static SKPaint TextPaint(SKPaint p, SKTypeface tf, float size, SKColor col, float a)
    {
        p.Reset(); p.IsAntialias = true; p.Typeface = tf; p.TextSize = size; p.Color = col.WithAlpha((byte)(col.Alpha * a));
        return p;
    }

    // ---------------------------------------------------------------- page clock
    double pt = Built;                       // seconds since the page being drawn started
    double pageLen = 8;                      // how long the page stays up (s) - paces the overflow roll
    int tickPage = -1; bool tickDone;      // grid_tick sound, once per page
    bool Live => pt < Built - 1;             // live effects (sheens, drift) only while on air
    static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
    static float OutCubic(float x) { x = Clamp01(x); return 1 - (1 - x) * (1 - x) * (1 - x); }
    static float OutBack(float x) { x = Clamp01(x); const float c1 = 1.55f, c3 = c1 + 1; return 1 + c3 * (x - 1) * (x - 1) * (x - 1) + c1 * (x - 1) * (x - 1); }
    float Lin(double s, double d) => Clamp01((float)((pt - s) / d));
    float K(double s, double d) => OutCubic(Lin(s, d));
    float B(double s, double d) => OutBack(Lin(s, d));

    /// <summary>Draw the page for <paramref name="tOnMs"/> = time the board has been
    /// ON (accumulated across on-windows), so pages advance only while it shows.</summary>
    public void Draw(SKCanvas c, SKRect body, IReadOnlyList<DriverClass> classes, int playerCarIdx,
                     float alpha, double tOnMs, double boardOnMs)
    {
        int count = 0;   // (no list of pages per frame - 2026-10-24)
        for (int i = 0; i < classes.Count; i++) if (classes[i].Drivers.Count > 0) count++;
        if (count == 0) return;
        DriverClass PageAt(int index)
        {
            for (int i = 0, n = 0; i < classes.Count; i++)
                if (classes[i].Drivers.Count > 0) { if (n == index) return classes[i]; n++; }
            return classes[0];
        }

        double pageMs = Math.Max(MinPageMs, boardOnMs / count);
        int idx = (int)(tOnMs / pageMs) % count;
        double local = tOnMs % pageMs;
        pageLen = pageMs / 1000.0;
        if (count > 1 && local < CrossFadeMs && tOnMs >= pageMs)
        {
            float k = (float)(local / CrossFadeMs); k = k * k * (3 - 2 * k);
            int prev = (idx - 1 + count) % count;
            DrawPage(c, body, PageAt(prev), prev, count, playerCarIdx, alpha * (1 - k), Built);
            DrawPage(c, body, PageAt(idx), idx, count, playerCarIdx, alpha * k, local / 1000.0);
        }
        else DrawPage(c, body, PageAt(idx), idx, count, playerCarIdx, alpha, local / 1000.0);
    }

    /// <summary>Draw one specific class page (used by the scripted opener timeline).
    /// <paramref name="pageT"/> = seconds since the page started building (Built = static).</summary>
    public void DrawClassPage(SKCanvas c, SKRect body, IReadOnlyList<DriverClass> classes, int playerCarIdx, float alpha, int index, double pageT = Built, double pageLength = 8)
    {
        int count = 0; DriverClass? page = null;
        for (int i = 0; i < classes.Count; i++)
            if (classes[i].Drivers.Count > 0) { if (count == index) page = classes[i]; count++; }
        if (page != null && index >= 0) { pageLen = pageLength; DrawPage(c, body, page, index, count, playerCarIdx, alpha, pageT); }
    }

    void DrawPage(SKCanvas c, SKRect body, DriverClass cls, int pageIdx, int pageCount, int playerCarIdx, float a, double pageT)
    {
        if (a <= 0.01f) return;
        pt = pageT;
        fx = Fx(pageIdx);
        var drivers = sorted;   // reused list (the page is drawn before the next one starts)
        drivers.Clear();
        for (int i = 0; i < cls.Drivers.Count; i++) drivers.Add(cls.Drivers[i]);
        drivers.Sort(ByGridSlot);
        SKColor classColor = ParseHex(cls.Drivers[0].ClassColor);
        float poleTime = drivers[0].QualifyTime;
        winner = drivers[0];

        // --- class strip ---
        var strip = new SKRect(body.Left, body.Top, body.Right, body.Top + StripH);
        DrawClassStrip(c, strip, cls, classColor, drivers.Count, pageIdx, pageCount, a);

        // --- front row heroes: rise in, pole first ---
        float heroTop = strip.Bottom + StripGap;
        float heroW = (body.Width - HeroGap) / 2f;
        // Small classes: the hero cards and rows GROW to fill the board instead of leaving an
        // empty block at the bottom (Angelo 2026-10-19).
        int restN = Math.Max(0, drivers.Count - 2);
        int perColGuess = Math.Max(1, Math.Min((int)((body.Bottom - heroTop - HeroH - ListGap) / RowH), (restN + 1) / 2));
        float heroH = Math.Clamp(body.Bottom - heroTop - ListGap - perColGuess * 58f, HeroH, 400f);
        for (int i = 0; i < 2 && i < drivers.Count; i++)
        {
            double hs = 0.30 + i * 0.18;
            float am = Lin(hs, 0.22);
            if (am <= 0.01f) continue;
            var r = new SKRect(body.Left + i * (heroW + HeroGap), heroTop,
                               body.Left + i * (heroW + HeroGap) + heroW, heroTop + heroH);
            c.Save();
            c.Translate(0, (1 - B(hs, 0.6)) * 54f);
            DrawHero(c, r, drivers[i], i + 1, poleTime, classColor, drivers[i].CarIdx == playerCarIdx, a * am, hs);
            c.Restore();
        }

        // --- the rest of the class (Angelo 2026-10-19) ---
        // Laid out like the real grid, row by row: 3 left / 4 right, 5 left / 6 right ...
        // Rows cascade in; a class too big for the board ROLLS UP smoothly after the cascade
        // so every car is shown, ending on the back of the grid just before the page leaves.
        float listTop = heroTop + heroH + ListGap;
        float listH = body.Bottom - listTop;
        int rest = drivers.Count - 2;
        if (rest <= 0) return;
        int totalRows = (rest + 1) / 2;
        int fitRows = Math.Max(1, (int)(listH / RowH));
        bool scroll = totalRows > fitRows;
        int visRows = scroll ? fitRows : totalRows;
        float rowH = scroll ? listH / fitRows : Math.Clamp(listH / totalRows, RowH, 62f);
        float maxGap = 0;
        for (int i = 0; i < drivers.Count; i++) { float gv = GapVal(drivers[i], poleTime); if (!float.IsNaN(gv) && gv >= 0) maxGap = Math.Max(maxGap, gv); }

        float off = 0, dist = 0;
        if (scroll)
        {
            dist = (totalRows - visRows) * rowH;
            double s0 = 0.95 + visRows * 2 * 0.055 + 1.3;          // cascade done + a beat to read
            double s1 = Math.Max(s0 + 1.2, pageLen - 0.6);          // arrive on the back row before the page leaves
            float u = Clamp01((float)((pt - s0) / (s1 - s0)));
            u = u * u * (3 - 2 * u);
            off = dist * u;
        }

        var listRect = new SKRect(body.Left - 6, listTop, body.Right + 6, body.Bottom);
        c.Save();
        c.ClipRect(listRect);
        if (scroll) c.SaveLayer(listRect, null);
        for (int k = 0; k < rest; k++)
        {
            int row = k / 2, col = k % 2;
            float y = listTop + row * rowH - off;
            if (y > body.Bottom || y + rowH < listTop) continue;
            var rr = new SKRect(body.Left + col * (heroW + HeroGap), y,
                                body.Left + col * (heroW + HeroGap) + heroW, y + rowH);
            double rs = 0.95 + Math.Min(k, visRows * 2) * 0.055;
            float ra = Lin(rs, 0.25);
            if (ra <= 0.01f) continue;
            var d = drivers[k + 2];
            c.Save();
            c.Translate(-(1 - K(rs, 0.45)) * 34f, 0);
            DrawRow(c, rr, d, k + 3, poleTime, maxGap, classColor, d.CarIdx == playerCarIdx, row % 2 == 0, a * ra, rs);
            c.Restore();
        }
        if (scroll)
        {
            // soft fade where rows slide under the top / bottom edge
            byte topA = (byte)(off > 1 ? 0 : 255), botA = (byte)(off < dist - 1 ? 0 : 255);
            fill.Reset(); fill.BlendMode = SKBlendMode.DstIn;
            fill.Shader = fx.Mask.Linear(new SKPoint(0, listRect.Top), new SKPoint(0, listRect.Bottom),
                [new SKColor(255, 255, 255, topA), SKColors.White, SKColors.White, new SKColor(255, 255, 255, botA)],
                [0f, 16f / listRect.Height, 1f - 22f / listRect.Height, 1f]);
            c.DrawRect(listRect, fill);
            c.Restore();
        }
        c.Restore();
    }

    static int Key(Driver d) => d.PositionInClass > 0 ? d.PositionInClass : 10_000 + (d.Position > 0 ? d.Position : 999);
    static readonly Comparison<Driver> ByGridSlot = (p, q) => Key(p).CompareTo(Key(q));

    void DrawClassStrip(SKCanvas c, SKRect r, DriverClass cls, SKColor classColor, int count, int pageIdx, int pageCount, float a)
    {
        LastClassCars = count;   // the car count lives in the board's footer (StartingGridRenderer)
        float wipe = K(0.0, 0.55);
        if (wipe <= 0.005f) return;

        // ---- the band: wipes in left -> right ----
        c.Save();
        c.ClipRect(new SKRect(r.Left, r.Top, r.Left + r.Width * wipe, r.Bottom));
        clipRR.SetRect(r, 8, 8); c.ClipRoundRect(clipRR, antialias: true);
        fill.Reset();
        fill.Shader = fx.StripBg.Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0),
            [classColor.WithAlpha((byte)(120 * a)), classColor.WithAlpha((byte)(38 * a)), new SKColor(0x0B, 0x0E, 0x14, (byte)(120 * a))], Stops.At042);
        c.DrawRect(r, fill);
        // page timer (Angelo 2026-10-19): a red arrow travels left -> right across the band, arriving
        // at the right just as the next page comes in. It follows a zoomed-in piece of THIS track's
        // real outline - the twistiest stretch of sector 1 on the first class page, sector 2 on the
        // next, and so on (a generic chicane only when there's no map for the track).
        {
            var cl = ClassLogo(cls.Name);
            float lh0 = r.Height - 18f, lw0 = cl != null ? cl.Width * (lh0 / cl.Height) : 140f;
            float x0 = r.Left + 20 + lw0 + 40, x1 = seriesLogo != null ? SeriesLogoBox(r, Trimmed(seriesLogo)).Left - 22 : r.Right - 28;
            float u = Clamp01((float)((pt - 0.6) / Math.Max(0.5, pageLen - 0.6)));
            if (x1 > x0 + 60 && u > 0.002f)
            {
                var (course, cum) = Course(pageIdx, pageCount, x0, x1, r.MidY + 1, r.Height * 0.62f);
                int n = course.Count;
                float total = cum[n - 1], dh = total * u;
                // walk the course by arc length
                int segIdx = 0;
                (SKPoint P, SKPoint T) At(float d)
                {
                    while (segIdx < n - 2 && cum[segIdx + 1] < d) segIdx++;
                    while (segIdx > 0 && cum[segIdx] > d) segIdx--;
                    var a0 = course[segIdx]; var a1 = course[segIdx + 1];
                    float sl = Math.Max(1e-3f, cum[segIdx + 1] - cum[segIdx]);
                    float tt = Math.Clamp((d - cum[segIdx]) / sl, 0, 1);
                    var tan = new SKPoint((a1.X - a0.X) / sl, (a1.Y - a0.Y) / sl);
                    return (new SKPoint(a0.X + (a1.X - a0.X) * tt, a0.Y + (a1.Y - a0.Y) * tt), tan);
                }
                var red = new SKColor(0xD8, 0x1E, 0x2A);
                if (dh > 4)
                {
                    // tapered ribbon (hairline tail -> full head), drawn as short anti-aliased pieces
                    // so it can fade in along its own length however the track twists
                    var L = ribL; var R = ribR; var T = ribT;   // reused lists
                    L.Clear(); R.Clear(); T.Clear();
                    for (float d = 0; ; d += 2f)
                    {
                        if (d > dh) d = dh;
                        var (p, tan) = At(d);
                        float t = d / dh, hw = 0.4f + 2.6f * t * t;
                        L.Add(new SKPoint(p.X - tan.Y * hw, p.Y + tan.X * hw));
                        R.Add(new SKPoint(p.X + tan.Y * hw, p.Y - tan.X * hw));
                        T.Add(t);
                        if (d >= dh) break;
                    }
                    var rp = fill; rp.Reset(); rp.IsAntialias = true;
                    const int Step = 6;
                    for (int i0 = 0; i0 < L.Count - 1; i0 += Step)
                    {
                        int i1 = Math.Min(L.Count - 1, i0 + Step);
                        float tm = T[(i0 + i1) / 2];
                        float al = tm < 0.45f ? tm / 0.45f * 200f : 200f + (tm - 0.45f) / 0.55f * 55f;
                        var piece = this.piece; piece.Reset();
                        piece.MoveTo(L[i0]);
                        for (int i = i0 + 1; i <= i1; i++) piece.LineTo(L[i]);
                        for (int i = i1; i >= i0; i--) piece.LineTo(R[i]);
                        piece.Close();
                        rp.Color = red.WithAlpha((byte)(al * a));
                        c.DrawPath(piece, rp);
                    }
                }
                // slim arrowhead along the tangent, soft glow right at the head
                var (hp, htan) = At(dh);
                float ang = (float)Math.Atan2(htan.Y, htan.X);
                // no glow at the head: soft red over the gold GTD PRO band read as a yellow/orange tip
                c.Save();
                c.Translate(hp.X, hp.Y);
                c.RotateRadians(ang);
                fill.Reset(); fill.IsAntialias = true; fill.Color = red.WithAlpha((byte)(255 * a));
                c.DrawPath(arrowHead, fill);
                // (all red - no white highlight on the tip, Angelo 2026-10-19)
                c.Restore();
            }
        }
        // top + bottom hairlines in the class colour
        fill.Reset();
        fill.Shader = fx.StripHl.Linear(new SKPoint(r.Left, 0), new SKPoint(r.Right, 0), [classColor.WithAlpha((byte)(255 * a)), classColor.WithAlpha(0)], []);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 2), fill);
        c.DrawRect(new SKRect(r.Left, r.Bottom - 2, r.Right, r.Bottom), fill);
        c.Restore();
        // leading edge of the wipe: a bright class-colour blade
        if (wipe < 0.999f)
        {
            float ex = r.Left + r.Width * wipe;
            fill.Reset(); fill.IsAntialias = true; fill.Color = SKColors.White.WithAlpha((byte)(220 * a)); fill.MaskFilter = blur3;
            c.DrawRect(new SKRect(ex - 3, r.Top, ex + 1, r.Bottom), fill);
        }
        fill.Reset(); fill.Color = classColor.WithAlpha((byte)(255 * a));
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 5, r.Bottom), fill);

        // ---- class logo: slides in, then a light sheen passes over it ----
        float la = K(0.18, 0.45);
        float x = r.Left + 20 - (1 - la) * 46;
        var logo = ClassLogo(cls.Name);
        if (la > 0.01f)
        {
            if (logo != null)
            {
                float h = r.Height - 18f, w = logo.Width * (h / logo.Height);
                var dst = new SKRect(x, r.MidY - h / 2, x + w, r.MidY + h / 2);
                c.SaveLayer(dst, null);
                var lp = logoPaint; lp.Reset(); lp.IsAntialias = true; lp.FilterQuality = SKFilterQuality.High; lp.Color = SKColors.White.WithAlpha((byte)(255 * a * la));
                lp.ImageFilter = fx.LogoShadow.DropShadow(0, 2, 6, 6, new SKColor(0, 0, 0, (byte)(150 * a * la)));
                BitmapImages.Draw(c, logo, dst, lp);
                float sh = SheenPhase(0.75, 5.0, 0.8);
                if (sh >= 0) Sheen(c, dst, sh, SKColors.White, 0.85f * a, fx.LogoSheen, SKBlendMode.SrcATop);
                c.Restore();
            }
            else
            {
                var np = TextPaint(namePaint, Fonts.Inter.Bold, 48, Txt, a * la);
                if (!classNames.TryGetValue(cls.Name ?? "", out var cn)) classNames[cls.Name ?? ""] = cn = global::ApexHUD.Native.Common.ClassLogo.DisplayName(cls.Name);
                TextBlobs.Draw(c, cn, x, r.MidY + 17, np);
            }
        }

        // ---- series logo (right) - where the page-timer arrow finishes (Angelo 2026-10-19,
        // replaces the page dots): ties every class page to the championship being watched.
        // It slides in with the band, and gives a little lift as the arrow arrives.
        var sl = seriesLogo;
        if (sl != null)
        {
            var src = Trimmed(sl);
            var box = SeriesLogoBox(r, src);
            float sa = K(0.35, 0.45);
            float arrive = Clamp01((float)((pt - 0.6) / Math.Max(0.5, pageLen - 0.6)));
            float lift = Live && arrive > 0.94f ? (float)Math.Sin(Clamp01((arrive - 0.94f) / 0.06f) * Math.PI) : 0f;
            if (Live)   // a crisp tick as the page-timer arrow lands on the series logo (2026-10-20)
            {
                if (pageIdx != tickPage) { tickPage = pageIdx; tickDone = false; }
                if (arrive < 0.5f) tickDone = false;
                if (!tickDone && arrive >= 0.94f && arrive < 1f) { tickDone = true; StorySound.Play("grid_tick"); }
            }
            float sc = (0.85f + 0.15f * OutBack(Lin(0.35, 0.5))) * (1f + 0.06f * lift);
            var dst = new SKRect(box.MidX - box.Width * sc / 2 + (1 - sa) * 30, box.MidY - box.Height * sc / 2,
                                 box.MidX + box.Width * sc / 2 + (1 - sa) * 30, box.MidY + box.Height * sc / 2);
            if (lift > 0.01f)
            {
                fill.Reset(); fill.IsAntialias = true;
                fill.Shader = fx.SeriesLift.Radial(new SKPoint(dst.MidX, dst.MidY), dst.Width * 0.75f, [new SKColor(255, 60, 70, (byte)(90 * lift * a)), new SKColor(255, 60, 70, 0)], []);
                c.DrawCircle(dst.MidX, dst.MidY, dst.Width * 0.75f, fill);
            }
            var lp = logoPaint; lp.Reset(); lp.IsAntialias = true; lp.FilterQuality = SKFilterQuality.High; lp.Color = SKColors.White.WithAlpha((byte)(255 * a * sa));
            lp.ImageFilter = fx.SeriesShadow.DropShadow(0, 2, 4, 4, new SKColor(0, 0, 0, (byte)(140 * a * sa)));
            BitmapImages.Draw(c, sl, src, dst, lp);
        }
    }

    /// <summary>Car count of the class page drawn last (the incoming page during a change).</summary>
    public int LastClassCars { get; private set; }

    /// <summary>0..1 position of a sheen pass that first runs at <paramref name="first"/> s and
    /// repeats every <paramref name="every"/> s, lasting <paramref name="len"/> s; -1 when idle.</summary>
    float SheenPhase(double first, double every, double len)
    {
        if (pt < first || !Live) return -1;
        double t = (pt - first) % every;
        return t < len ? (float)(t / len) : -1;
    }

    /// <summary>A diagonal light band swept across <paramref name="r"/> at phase 0..1.</summary>
    void Sheen(SKCanvas c, SKRect r, float phase, SKColor col, float a, ShaderSlot slot, SKBlendMode mode = SKBlendMode.SrcOver)
    {
        float band = Math.Max(60f, r.Height * 0.9f);
        float sx = r.Left - band * 2 + (r.Width + band * 3) * phase;
        // the band's gradient is built relative to its own x and moved with the canvas (2026-10-24)
        fill.Reset(); fill.IsAntialias = true; fill.BlendMode = mode;
        fill.Shader = slot.Linear(new SKPoint(0, r.Bottom), new SKPoint(band, r.Top), [col.WithAlpha(0), col.WithAlpha((byte)(110 * a)), col.WithAlpha(0)], Stops.At05);
        c.Save(); c.Translate(sx, 0);
        c.DrawRect(new SKRect(r.Left - sx, r.Top, r.Right - sx, r.Bottom), fill);
        c.Restore();
    }

    void DrawHero(SKCanvas c, SKRect r, Driver d, int pos, float poleTime, SKColor classColor, bool isPlayer, float a, double hs)
    {
        bool pole = pos == 1;
        int hi = pole ? 0 : 1;   // this card's shader slots
        SKColor accent = pole ? Gold : Txt;
        c.Save();
        clipRR.SetRect(r, 10, 10); c.ClipRoundRect(clipRR, antialias: true);

        // base
        fill.Reset();
        fill.Shader = fx.HeroBg[hi].Linear(new SKPoint(r.Left, r.Top), new SKPoint(r.Right, r.Bottom),
            [new SKColor(0x10, 0x14, 0x1C, (byte)(235 * a)), new SKColor(0x18, 0x1E, 0x2A, (byte)(235 * a))], []);
        c.DrawRect(r, fill);
        // class-colour glow rising from the bottom-right corner
        fill.Shader = fx.HeroGlow[hi].Radial(new SKPoint(r.Right - r.Width * 0.18f, r.Bottom + 30), r.Width * 0.55f,
            [(pole ? Gold : classColor).WithAlpha((byte)(70 * a * K(hs + 0.2, 0.8))), SKColors.Transparent], []);
        c.DrawRect(r, fill);

        // huge faded brand-logo backdrop - parallaxes in from the right, then drifts slowly
        int bdMaxW = (int)(r.Width * 0.70f), bdMaxH = (int)(r.Height * 1.15f);
        var bd = Backdrop(d.CarBrand, bdMaxW, bdMaxH);
        if (bd != null)
        {
            float bh = bd.Height, bw = bd.Width;
            bool wide = bw >= bdMaxW - 2;
            float drift = (1 - K(hs, 1.4f)) * 90f + (Live ? (float)Math.Sin(pt * 0.35) * 5f : 0f);
            float bx = (wide ? r.Right - bw - 14 : r.Right - bw * 0.80f) + drift, by = r.MidY - bh / 2f;
            var dst = new SKRect(bx, by, bx + bw, by + bh);
            layer.Color = new SKColor(255, 255, 255, (byte)(40 * a * K(hs + 0.1, 0.9)));
            c.SaveLayer(dst, layer);
            fill.Reset(); fill.IsAntialias = true; fill.FilterQuality = SKFilterQuality.Medium;
            c.DrawImage(bd, dst, fill);
            c.Restore();
        }
        // legibility: dark wash from the left
        fill.Reset();
        fill.Shader = fx.HeroWash[hi].Linear(new SKPoint(r.Left, 0), new SKPoint(r.Left + r.Width * 0.75f, 0),
            [new SKColor(0x0B, 0x0E, 0x14, (byte)(225 * a)), new SKColor(0x0B, 0x0E, 0x14, 0)], []);
        c.DrawRect(r, fill);
        // 2026-10-28 (#483, Angelo: "on the top two in class I want their car image that iracing sends in there too ...
        // keep the car maker logos behind it ... everything exactly the same just with the cars in"): the car iRacing
        // renders with the driver's own paint (LiveryArt), standing in the right half of the card ON the maker-logo
        // backdrop. It glides in with the backdrop's parallax; nothing else on the card moves or resizes.
        if (LiveryArt.Get(d) is { } heroCar)
        {
            float ch = r.Height * 0.66f, cw = ch * heroCar.Width / heroCar.Height;
            float maxCw = r.Width * 0.50f;
            if (cw > maxCw) { cw = maxCw; ch = cw * heroCar.Height / heroCar.Width; }
            float cIn = K(hs + 0.2, 0.9);
            float cx = r.Right - 22 - cw / 2f + (1 - cIn) * 70f, by = r.Bottom - 16;
            carShadowPaint.Color = new SKColor(0, 0, 0, (byte)(140 * a * cIn));   // its own paint: a blur on the shared one crashed (bench)
            c.DrawOval(new SKRect(cx - cw * 0.44f, by - ch * 0.10f, cx + cw * 0.48f, by + ch * 0.04f), carShadowPaint);
            fill.Reset(); fill.IsAntialias = true; fill.FilterQuality = SKFilterQuality.High;
            fill.Color = SKColors.White.WithAlpha((byte)(255 * a * cIn));
            c.DrawImage(heroCar, new SKRect(cx - cw / 2f, by - ch, cx + cw / 2f, by), fill);
            fill.Reset();
        }
        // class accent stripe (drops down) + top hairline (draws across) in the accent colour
        fill.Reset(); fill.Color = classColor.WithAlpha((byte)(255 * a));
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 4, r.Top + r.Height * K(hs + 0.1, 0.5)), fill);
        float hlw = K(hs + 0.15, 0.7);
        fill.Reset();
        fill.Shader = fx.HeroHl[hi].Linear(new SKPoint(r.Left, 0), new SKPoint(r.Left + r.Width * hlw, 0), [accent.WithAlpha((byte)(220 * a)), accent.WithAlpha(0)], []);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + r.Width * hlw, r.Top + 2), fill);
        // sheen: once as the card lands, then every few seconds (pole's is golden)
        float sh = SheenPhase(hs + 0.55 + (pole ? 0 : 0.25), 4.5, 0.9);
        if (sh >= 0) Sheen(c, r, sh, pole ? new SKColor(0xFF, 0xE6, 0xA0) : SKColors.White, (pole ? 0.55f : 0.32f) * a, fx.HeroSheen[hi]);
        c.Restore();

        // pole: a slow gold breathing outline
        if (pole)
        {
            float br = Live ? 0.55f + 0.45f * (float)Math.Sin(pt * 2.2) : 0.6f;
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 1.5f;
            fill.Color = Gold.WithAlpha((byte)(120 * br * a * K(hs + 0.5, 0.6))); fill.MaskFilter = solid25;
            c.DrawRoundRect(r, 10, 10, fill);
        }
        // player glow
        if (isPlayer)
        {
            fill.Reset(); fill.IsAntialias = true; fill.Style = SKPaintStyle.Stroke; fill.StrokeWidth = 2;
            fill.Color = PlayerBlue.WithAlpha((byte)(230 * a)); fill.MaskFilter = solid3;
            c.DrawRoundRect(r, 10, 10, fill);
        }

        float L = r.Left + 26;
        float nx = L + 92, maxW = r.Right - nx - 22;
        float T0 = r.Top + (r.Height - 236f) * 0.42f;   // taller cards: the content block sits centred-ish

        // (no POLE POSITION / FRONT ROW tag - the giant number says it, Angelo 2026-10-19)

        // big position numeral rolls UP into its window
        // (Inter, not Cormorant: Cormorant's old-style "1" reads as a Roman "I".)
        float nr = B(hs + 0.25, 0.6);
        c.Save();
        c.ClipRect(new SKRect(r.Left, T0 + 38, nx - 6, T0 + 160));
        {
            var big = TextPaint(numPaint, Fonts.Inter.Bold, 104, accent, a);
            if (pole) big.ImageFilter = fx.HeroNum[hi].DropShadow(0, 0, 10, 10, Gold.WithAlpha((byte)(90 * a * nr)));
            TextBlobs.Draw(c, SmallStrings.Num(pos), L - 6, T0 + 146 + (1 - nr) * 125f, big);
        }
        c.Restore();

        // driver name wipes in behind an accent edge
        float nw = K(hs + 0.38, 0.5);
        if (nw > 0.001f)
        {
            var np = TextPaint(namePaint, Fonts.CormorantGaramond.Bold, 36, isPlayer ? SKColors.White : Txt, a);
            string name = NameFit(d, np, maxW);
            float full = TextBlobs.Measure(name, np) + 6;
            float ex = nx + full * nw;
            c.Save();
            c.ClipRect(new SKRect(nx - 2, T0 + 56, ex, T0 + 108));
            TextBlobs.Draw(c, name, nx, T0 + 98, np);
            c.Restore();
            if (nw < 0.999f)
            {
                fill.Reset(); fill.Color = accent.WithAlpha((byte)(255 * a));
                c.DrawRect(new SKRect(ex, T0 + 64, ex + 3, T0 + 104), fill);
            }
        }

        // number + licence chips, car name - slide in under the name
        float ca = K(hs + 0.6, 0.4);
        if (ca > 0.01f)
        {
            float cx = nx - (1 - ca) * 18;
            cx = Chip(c, cx, T0 + 112, d.CarNumber, SKColors.White, new SKColor(8, 10, 16), a * ca) + 6;
            cx = Chip(c, cx, T0 + 112, string.IsNullOrEmpty(d.LicenceString) ? "-" : SmallStrings.Char(d.LicenceString[0]),
                      ParseHex(d.LicenceColor), SKColors.White, a * ca) + 12;
            var car = TextPaint(carPaint, Fonts.Inter.SemiBold, 13.5f, Txt2, a * ca);   // #340: was 12
            TextBlobs.Draw(c, CarFit(d.CarName, car, r.Right - cx - 22), cx, T0 + 128, car);
        }

        // qualifying time decodes digit by digit, then the gap fades up
        float qa = K(hs + 0.7, 0.3);
        // (no QUALIFYING label - everyone knows what the time is, Angelo 2026-10-19)
        if (qa > 0.01f)
        {
            var qt = TextPaint(qtPaint, Fonts.Inter.Bold, 26, Txt, a * qa);
            // results (#486): WINNER / the finishing gap big, the best race lap beside it
            string t = Results ? (pole ? "WINNER" : GapText(d, poleTime)) : Lap(d.QualifyTime);
            if (t.Length == 0) t = Lap(d.BestLapTime);
            double ds = hs + 0.75;
            TextBlobs.Draw(c, Decode(t, ds, 0.055), nx, r.Bottom - 22, qt);
            string side = Results ? (d.BestLapTime > 0 ? Best(d.BestLapTime) : "") : (!pole && d.QualifyTime > 0 && poleTime > 0 ? Gap(d.QualifyTime - poleTime) : "");
            if (side.Length > 0)
            {
                float gx = nx + TextBlobs.Measure(t, qt) + 14;
                var gp = TextPaint(gapPaint, Fonts.Inter.SemiBold, 15, Txt2, a * K(ds + t.Length * 0.055, 0.35));
                TextBlobs.Draw(c, side, gx, r.Bottom - 24, gp);
            }
        }
    }

    /// <summary>2026-10-28 (#486): one class winner's hero card for the post-race opener page (same card as pole on the
    /// grid: gold numeral, name, chips, the car on the maker logo, WINNER + best lap). <paramref name="slot"/> picks the
    /// card's own shader set and staggers its entrance.</summary>
    public void DrawWinner(SKCanvas c, SKRect r, Driver d, int slot, bool isPlayer, float a, double t)
    {
        if (a <= 0.01f) return;
        bool was = Results; Results = true;
        fx = Fx(slot); pt = t; winner = d;
        double hs = 0.30 + slot * 0.18;
        float am = Lin(hs, 0.22);
        if (am > 0.01f)
        {
            c.Save();
            c.Translate(0, (1 - B(hs, 0.6)) * 54f);
            DrawHero(c, r, d, 1, 0, ParseHex(d.ClassColor), isPlayer, a * am, hs);
            c.Restore();
        }
        Results = was;
    }

    /// <summary>Digits scramble and settle left to right (one every <paramref name="step"/> s).</summary>
    string Decode(string s, double start, double step)
    {
        if (pt >= start + s.Length * step) return s;
        var ch = s.ToCharArray();
        int tick = (int)(pt * 30);
        for (int i = 0; i < ch.Length; i++)
            if (char.IsDigit(ch[i]) && pt < start + i * step)
                ch[i] = (char)('0' + ((tick * 7 + i * 13) % 10));
        return new string(ch);
    }

    void DrawRow(SKCanvas c, SKRect r, Driver d, int pos, float poleTime, float maxGap, SKColor classColor, bool isPlayer, bool shade, float a, double rs)
    {
        var inner = new SKRect(r.Left, r.Top + 1, r.Right, r.Bottom - 1);
        int ri = pos - 3;   // this row's shader slots
        if (shade || isPlayer)
        {
            fill.Reset(); fill.IsAntialias = true; fill.Color = isPlayer ? PlayerBlue.WithAlpha((byte)(46 * a)) : new SKColor(255, 255, 255, (byte)(9 * a));
            c.DrawRoundRect(inner, 4, 4, fill);
        }
        // arrival flash: a class-colour wash that fades off the row as it lands
        float fl = 1 - Lin(rs + 0.12, 0.5);
        if (fl > 0.01f)
        {
            fill.Reset();
            fill.Shader = fx.Row(fx.RowFlash, ri).Linear(new SKPoint(inner.Left, 0), new SKPoint(inner.Left + inner.Width * 0.6f, 0),
                [classColor.WithAlpha((byte)(110 * fl * a)), classColor.WithAlpha(0)], []);
            c.DrawRoundRect(inner, 4, 4, fill);
        }
        SKColor lead = isPlayer ? PlayerBlue : classColor;
        float leadA = isPlayer ? 1f : Math.Max(fl, 0.0f);
        if (leadA > 0.01f)
        {
            fill.Reset(); fill.Color = lead.WithAlpha((byte)(255 * leadA * a));
            c.DrawRect(new SKRect(inner.Left, inner.Top, inner.Left + 3, inner.Bottom), fill);
        }

        // gap-to-pole bar growing along the bottom of the row
        float gapV = GapVal(d, poleTime);
        if (maxGap > 0 && !float.IsNaN(gapV))
        {
            float frac = Math.Clamp(gapV < 0 ? 1f : gapV / maxGap, 0.02f, 1f) * K(rs + 0.25, 0.7);
            float bx0 = r.Left + 44, bw = (r.Right - 8 - bx0) * frac;
            fill.Reset();
            fill.Shader = fx.Row(fx.RowGap, ri).Linear(new SKPoint(bx0, 0), new SKPoint(bx0 + Math.Max(1, bw), 0),
                [classColor.WithAlpha((byte)(20 * a)), classColor.WithAlpha((byte)(150 * a))], []);
            c.DrawRect(new SKRect(bx0, inner.Bottom - 2, bx0 + bw, inner.Bottom), fill);
        }

        float y = r.MidY + 5;
        float z = Math.Clamp(r.Height / 30f, 1f, 1.3f);   // rows grow with the board
        y = r.MidY + 5 * z;
        DrawTabular(c, SmallStrings.Num(pos), r.Left + 34 * z, y, TextPaint(rowPaint, Fonts.Inter.Bold, 14 * z, Txt, a));
        // Fixed-width number column (Angelo 2026-10-19: everything lines up): the chip is
        // centred in a slot sized for a 3-digit number, so names always start at the same x.
        float slotL = r.Left + 44 * z, slotW;
        float chipW;
        {
            var mp = TextPaint(measure, Fonts.Inter.Bold, 11 * z, SKColors.White, 1);
            slotW = Math.Max(18 * z, mp.MeasureText("888") + 12);
            chipW = Math.Max(18 * z, mp.MeasureText(string.IsNullOrEmpty(d.CarNumber) ? "-" : d.CarNumber) + 12);
        }
        Chip(c, slotL + (slotW - Math.Min(chipW, slotW)) / 2f, r.MidY - 9 * z, d.CarNumber, SKColors.White, new SKColor(8, 10, 16), a, 18 * z, 11 * z);
        float x = slotL + slotW + 10 * z;

        float timeRight = r.Right - 78 * z, logoX = timeRight - 104 * z;   // logo column clear of the lap time
        {
            var np = TextPaint(namePaint, Fonts.Inter.SemiBold, 13.5f * z, isPlayer ? SKColors.White : Txt, a);
            TextBlobs.Draw(c, NameFit(d, np, logoX - x - 8), x, y, np);
        }

        var lg = Logo(d.CarBrand);
        if (lg != null)
        {
            // Every maker logo sits in the SAME box, centred, with its transparent padding trimmed
            // off first - so they line up neatly down the column (Angelo 2026-10-19).
            float la = K(rs + 0.15, 0.4);
            var src = Trimmed(lg);
            float boxW = 34f * z, boxH = 18f * z, bcx = logoX + boxW / 2f;
            float fit = Math.Min(boxW / src.Width, boxH / src.Height);
            float s = (0.6f + 0.4f * OutBack(Lin(rs + 0.15, 0.45))) * fit;
            float w = src.Width * s, h = src.Height * s;
            var lp = logoPaint; lp.Reset(); lp.IsAntialias = true; lp.FilterQuality = SKFilterQuality.High; lp.Color = SKColors.White.WithAlpha((byte)(235 * a * la));
            BitmapImages.Draw(c, lg, new SKRect(src.Left, src.Top, src.Right, src.Bottom), new SKRect(bcx - w / 2, r.MidY - h / 2, bcx + w / 2, r.MidY + h / 2), lp);
        }
        DrawTabular(c, Decode(Lap(TimeOf(d)), rs + 0.1, 0.03), timeRight, y, TextPaint(qtPaint, Fonts.Inter.SemiBold, 14 * z, Txt2, a));   // #340: was 13
        string gtx = GapText(d, poleTime);
        if (gtx.Length > 0)
            DrawTabular(c, gtx, r.Right - 8, y, TextPaint(gapPaint, Fonts.Inter.Medium, 13.5f * z, Txt3, a * K(rs + 0.35, 0.3)));   // #340: was 12
    }

    /// <summary>Right-aligned text with every digit in an equal-width cell (Inter's digits
    /// are proportional, so "1" is narrow): times, gaps and positions line up column-perfect
    /// down the board (Angelo 2026-10-19).</summary>
    static void DrawTabular(SKCanvas c, string s, float right, float y, SKPaint p)
    {
        if (string.IsNullOrEmpty(s)) return;
        var align = p.TextAlign; p.TextAlign = SKTextAlign.Left;
        float cell = p.MeasureText("0");
        float w = 0;
        foreach (char ch in s) w += char.IsDigit(ch) ? cell : p.MeasureText(SmallStrings.Char(ch));
        float x = right - w;
        foreach (char ch in s)
        {
            string t = SmallStrings.Char(ch);
            float cw = char.IsDigit(ch) ? cell : p.MeasureText(t);
            TextBlobs.Draw(c, t, x + (cw - p.MeasureText(t)) / 2f, y, p);
            x += cw;
        }
        p.TextAlign = align;
    }

    void DrawMore(SKCanvas c, SKRect r, int more, float a)
    {
        using var p = Paint(Fonts.Inter.Bold, 13, Txt3, a);   // #340: was 11
        TextTracked.Draw(c, p, $"+ {more} MORE", r.Left + 44, r.MidY + 5, 2.4f);
    }

    // ------------------------------------------------- page-timer course (real track outline)
    List<SKPoint>? trackPts;
    string? trackKey;
    readonly Dictionary<(string?, int, int, int, int, int, int), (List<SKPoint> Pts, float[] Cum)> courseCache = new();

    SKBitmap? seriesLogo; int seriesId = int.MinValue;
    void SetSeries(RaceSnapshot s)
    {
        if ((s.SeriesId ?? -1) == seriesId) return;
        seriesId = s.SeriesId ?? -1; seriesLogo?.Dispose(); seriesLogo = null;
        var slug = SeriesNames.ResolveLogoSlug(s.SeriesId);
        if (slug == null) return;
        try
        {
            var path = Assets.Resolve("logos", "series", slug + ".png");
            if (System.IO.File.Exists(path)) { using var st = System.IO.File.OpenRead(path); seriesLogo = Assets.Decode(st); }
        }
        catch { seriesLogo = null; }
    }
    /// <summary>Where the series logo sits at the right end of the class band.</summary>
    static SKRect SeriesLogoBox(SKRect band, SKRect src)
    {
        float h = band.Height - 14f, w = src.Width * (h / src.Height);
        if (w > 170f) { w = 170f; h = src.Height * (w / src.Width); }
        return new SKRect(band.Right - 14 - w, band.MidY - h / 2, band.Right - 14, band.MidY + h / 2);
    }
    /// <summary>Tell the board which track we're at (its outline shapes the page-timer arrow).</summary>
    public void SetTrack(RaceSnapshot s)
    {
        SetSeries(s);
        var b = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
        if (b == trackKey) return;
        trackKey = b; trackPts = null; courseCache.Clear();
        if (b == null) return;
        try
        {
            var json = Assets.Resolve("maps", b + ".json");
            if (!System.IO.File.Exists(json)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(json));
            var list = new List<SKPoint>();
            foreach (var p in doc.RootElement.GetProperty("points").EnumerateArray())
                list.Add(new SKPoint(p[0].GetSingle(), p[1].GetSingle()));
            if (list.Count >= 20) trackPts = list;
        }
        catch { trackPts = null; }
    }

    /// <summary>The course the arrow follows, already placed in the band (x0..x1, centred on midY).</summary>
    (List<SKPoint> Pts, float[] Cum) Course(int pageIdx, int pageCount, float x0, float x1, float midY, float bandH)
    {
        // keyed like the old "{x0:0}" string key (rounded to whole pixels), without making a string every frame;
        // the course's running length is kept with it (was a new array every frame)
        static int R0(float v) => (int)Math.Round(v, MidpointRounding.AwayFromZero);
        var key = (trackKey, pageIdx, pageCount, R0(x0), R0(x1), R0(midY), R0(bandH));
        if (courseCache.TryGetValue(key, out var hit)) return hit;
        var course = TrackCourse(pageIdx, pageCount, x0, x1, midY, bandH) ?? ChicaneCourse(x0, x1, midY, bandH);
        int n = course.Count;
        var cum = new float[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + SKPoint.Distance(course[i - 1], course[i]);
        return courseCache[key] = (course, cum);
    }

    List<SKPoint>? TrackCourse(int pageIdx, int pageCount, float x0, float x1, float midY, float bandH)
    {
        var P = trackPts;
        if (P == null) return null;
        int n = P.Count;
        // resample the lap evenly by distance
        var cum = new double[n + 1];
        for (int i = 0; i < n; i++) cum[i + 1] = cum[i] + SKPoint.Distance(P[i], P[(i + 1) % n]);
        double lap = cum[n];
        if (lap <= 1) return null;
        const int M = 720;
        var R = new SKPoint[M];
        for (int k = 0, seg = 0; k < M; k++)
        {
            double d = lap * k / M;
            while (seg < n - 1 && cum[seg + 1] < d) seg++;
            double sl = Math.Max(1e-6, cum[seg + 1] - cum[seg]);
            float t = (float)((d - cum[seg]) / sl);
            var a0 = P[seg]; var a1 = P[(seg + 1) % n];
            R[k] = new SKPoint(a0.X + (a1.X - a0.X) * t, a0.Y + (a1.Y - a0.Y) * t);
        }
        // how much the track turns at each sample
        var turn = new double[M];
        for (int k = 0; k < M; k++)
        {
            var pa = R[(k - 1 + M) % M]; var pb = R[k]; var pc = R[(k + 1) % M];
            double h0 = Math.Atan2(pb.Y - pa.Y, pb.X - pa.X), h1 = Math.Atan2(pc.Y - pb.Y, pc.X - pb.X);
            double dlt = h1 - h0;
            while (dlt > Math.PI) dlt -= 2 * Math.PI;
            while (dlt < -Math.PI) dlt += 2 * Math.PI;
            turn[k] = Math.Abs(dlt);
        }
        // in this page's sector, pick the twistiest stretch ~7.5 % of a lap long (zoomed in)
        int win = (int)(M * 0.075);
        int sA = M * pageIdx / Math.Max(1, pageCount), sB = M * (pageIdx + 1) / Math.Max(1, pageCount);
        int best = sA; double bestScore = -1;
        for (int s0 = sA; s0 <= Math.Max(sA, sB - win); s0 += 4)
        {
            double sc = 0;
            for (int k = 0; k < win; k++) sc += turn[(s0 + k) % M];
            if (sc > bestScore) { bestScore = sc; best = s0; }
        }
        var seg2 = new List<SKPoint>(win + 1);
        for (int k = 0; k <= win; k++) seg2.Add(R[(best + k) % M]);
        // lay it along its long axis, start on the left
        double mx = 0, my = 0;
        foreach (var p in seg2) { mx += p.X; my += p.Y; }
        mx /= seg2.Count; my /= seg2.Count;
        double sxx = 0, syy = 0, sxy = 0;
        foreach (var p in seg2) { double dx = p.X - mx, dy = p.Y - my; sxx += dx * dx; syy += dy * dy; sxy += dx * dy; }
        double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
        double ux = Math.Cos(ang), uy = Math.Sin(ang);
        var first = seg2[0]; var last = seg2[^1];
        if ((last.X - first.X) * ux + (last.Y - first.Y) * uy < 0) { ux = -ux; uy = -uy; }
        double vx = -uy, vy = ux;   // proper rotation: the track's shape is never mirrored
        var uv = new List<(double U, double V)>(seg2.Count);
        double umin = double.MaxValue, umax = double.MinValue, vmin = double.MaxValue, vmax = double.MinValue;
        foreach (var p in seg2)
        {
            double dx = p.X - mx, dy = p.Y - my;
            double U = dx * ux + dy * uy, V = dx * vx + dy * vy;
            uv.Add((U, V));
            umin = Math.Min(umin, U); umax = Math.Max(umax, U); vmin = Math.Min(vmin, V); vmax = Math.Max(vmax, V);
        }
        if (umax - umin < 1) return null;
        double sx = (x1 - x0) / (umax - umin);
        double sy = Math.Min(sx * 2.2, bandH / Math.Max(1e-3, vmax - vmin));   // fill the band's height (corners read bigger)
        double vmid = (vmin + vmax) / 2;
        var outPts = new List<SKPoint>(uv.Count);
        foreach (var (U, V) in uv) outPts.Add(new SKPoint((float)(x0 + (U - umin) * sx), (float)(midY + (V - vmid) * sy)));
        return outPts;
    }

    static List<SKPoint> ChicaneCourse(float x0, float x1, float midY, float bandH)
    {
        float amp = bandH * 0.27f;
        int bends = Math.Max(2, (int)Math.Round((x1 - x0) / 230f));
        float period = (x1 - x0) / bends;
        const float sharp = 3.2f; float norm = (float)Math.Tanh(sharp);
        var pts = new List<SKPoint>();
        for (float x = x0; x <= x1 + 0.01f; x += 3)
            pts.Add(new SKPoint(x, midY - amp * (float)Math.Tanh(sharp * Math.Sin((x - x0) / period * Math.PI * 2)) / norm));
        return pts;
    }

    readonly Dictionary<SKBitmap, SKRect> trimCache = new();
    /// <summary>The logo's visible pixels (alpha > 8), so padding in the PNG doesn't shift it.</summary>
    SKRect Trimmed(SKBitmap bmp)
    {
        if (trimCache.TryGetValue(bmp, out var rr)) return rr;
        int x0 = bmp.Width, y0 = bmp.Height, x1 = -1, y1 = -1;
        int st = Math.Max(1, Math.Max(bmp.Width, bmp.Height) / 256);   // sampled: cheap even for big logos
        for (int y = 0; y < bmp.Height; y += st)
            for (int x = 0; x < bmp.Width; x += st)
                if (bmp.GetPixel(x, y).Alpha > 8) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
        rr = x1 < 0 ? new SKRect(0, 0, bmp.Width, bmp.Height) : new SKRect(Math.Max(0, x0 - st), Math.Max(0, y0 - st), Math.Min(bmp.Width, x1 + st), Math.Min(bmp.Height, y1 + st));
        return trimCache[bmp] = rr;
    }
    // ---------------------------------------------------------------- helpers
    static SKPaint Paint(SKTypeface tf, float size, SKColor col, float a) =>
        new() { IsAntialias = true, Typeface = tf, TextSize = size, Color = col.WithAlpha((byte)(col.Alpha * a)) };

    /// <summary>Rounded chip with centred text; returns its right edge.</summary>
    float Chip(SKCanvas c, float x, float top, string? text, SKColor bg, SKColor fg, float a, float h = 22, float size = 12)
    {
        text = string.IsNullOrEmpty(text) ? "-" : text;
        var tp = TextPaint(chipText, Fonts.Inter.Bold, size, fg, a);
        tp.TextAlign = SKTextAlign.Center;
        float w = Math.Max(h, tp.MeasureText(text) + 12);
        var r = new SKRect(x, top, x + w, top + h);
        chipBg.Reset(); chipBg.IsAntialias = true; chipBg.Color = bg.WithAlpha((byte)(bg.Alpha * a));
        c.DrawRoundRect(r, 3, 3, chipBg);
        tp.GetFontMetrics(out var m);
        TextBlobs.Draw(c, text, r.MidX, r.MidY - (m.Ascent + m.Descent) / 2, tp);
        return r.Right;
    }

    static string Fit(string s, SKPaint p, float maxW)
    {
        if (string.IsNullOrEmpty(s) || p.MeasureText(s) <= maxW) return s ?? "";
        for (int n = s.Length - 1; n > 1; n--)
        {
            string t = s[..n].TrimEnd() + "\u2026";
            if (p.MeasureText(t) <= maxW) return t;
        }
        return s[..1];
    }

    static string FormatLap(float s)
    {
        if (s <= 0) return "NO TIME";
        int m = (int)(s / 60);
        return m > 0 ? $"{m}:{s - m * 60:00.000}" : $"{s:0.000}";
    }

    static string FormatGap(float g)
    {
        if (g < 0) g = 0;
        int m = (int)(g / 60);
        return m > 0 ? $"+{m}:{g - m * 60:00.000}" : $"+{g:0.000}";
    }

    internal static SKColor ParseClassColor(string? hex) => ParseHex(hex);

    static SKColor ParseHex(string? hex)
    {
        if (string.IsNullOrEmpty(hex)) return new SKColor(0x9A, 0xA3, 0xB2);
        // spans, not substrings: asked every frame for every class colour / licence chip (2026-10-24)
        var s = hex.AsSpan().TrimStart('#');
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length < 6) return new SKColor(0x9A, 0xA3, 0xB2);
        if (byte.TryParse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var r)
            && byte.TryParse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var g)
            && byte.TryParse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            return new SKColor(r, g, b);
        return new SKColor(0x9A, 0xA3, 0xB2);
    }

    SKBitmap? Logo(string? brand)
    {
        var slug = BrandSlug(brand);
        if (slug == null) return null;
        if (logos.TryGetValue(slug, out var b)) return b;
        try
        {
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
            if (System.IO.File.Exists(path)) { using var st = System.IO.File.OpenRead(path); b = Assets.Decode(st); }
        }
        catch { b = null; }
        return logos[slug] = b;
    }

    /// <summary>Brand logo pre-scaled ONCE to <paramref name="height"/> px into an
    /// immutable image (cheap to draw every frame; the GPU caches the texture).</summary>
    /// <remarks>2026-10-18: sized to FIT a maxW x maxH box (was height-only, which
    /// blew wide logos like Cadillac's crest + wordmark up so only a slice showed).</remarks>
    readonly Dictionary<(string, int, int), SKImage?> backdropBySize = new();
    internal SKImage? Backdrop(string? brand, int maxW, int maxH)   // also used by the battle card
    {
        var slug = BrandSlug(brand);
        if (slug == null) return null;
        if (backdropBySize.TryGetValue((slug, maxW, maxH), out var hit)) return hit;   // no key string per frame (2026-10-24)
        string key = $"{slug}@{maxW}x{maxH}";
        if (backdrops.TryGetValue(key, out var img)) return backdropBySize[(slug, maxW, maxH)] = img;
        img = null;
        var src = Logo(brand);
        if (src != null)
        {
            float s = Math.Min(maxW / (float)src.Width, maxH / (float)src.Height);
            int w = Math.Max(1, (int)(src.Width * s));
            int h = Math.Max(1, (int)(src.Height * s));
            using var surf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surf != null)
            {
                surf.Canvas.Clear(SKColors.Transparent);
                using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                surf.Canvas.DrawBitmap(src, new SKRect(0, 0, w, h), p);
                img = surf.Snapshot();
            }
        }
        backdropBySize[(slug, maxW, maxH)] = img;
        return backdrops[key] = img;
    }

    internal SKBitmap? ClassLogoFor(string? cls) => ClassLogo(cls);

    SKBitmap? ClassLogo(string? cls)
    {
        // looked up by the class's own name first: Resolve() upper-cases it on every call (2026-10-24)
        if (classLogoByName.TryGetValue(cls ?? "", out var known)) return known.Logo;
        var found = ClassLogoLookup(cls);
        classLogoByName[cls ?? ""] = ("", found);
        return found;
    }

    SKBitmap? ClassLogoLookup(string? cls)
    {
        // same logo rule as the rest of ApexHUD (iRacing's GT3 class -> GTD PRO)
        string key = global::ApexHUD.Native.Common.ClassLogo.Resolve(cls) ?? (cls ?? "").Replace(" ", "").ToUpperInvariant();
        if (classLogos.TryGetValue(key, out var b)) return b;
        try
        {
            var path = Assets.Resolve("logos", "classes", key + ".png");
            if (System.IO.File.Exists(path)) { using var st = System.IO.File.OpenRead(path); b = Assets.Decode(st); }
        }
        catch { b = null; }
        return classLogos[key] = b;
    }

    public void Dispose()
    {
        foreach (var b in logos.Values) b?.Dispose();
        foreach (var i in backdrops.Values) i?.Dispose();
        foreach (var b in classLogos.Values) b?.Dispose();
        carShadowPaint.Dispose();
        logos.Clear(); backdrops.Clear(); classLogos.Clear(); backdropBySize.Clear(); classLogoByName.Clear();
    }
}
