using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>Where the story cards sit (2026-10-27, #349). Classic = the original right-half card. The other four keep
/// the centre third of the picture (x 640..1280 in 1920 design units) clear: every card is reflowed ONCE into a
/// right-third column made of two sections - "top" (title / partner + the headline numbers) and "bottom" (the chart,
/// picture, list or detail) - and the layouts are just arrangements of those two sections.</summary>
public enum CardLayout
{
    /// <summary>The original right-half card (default, unchanged).</summary>
    Classic,
    /// <summary>A: one tall column, top + bottom stacked, top to bottom of the screen.</summary>
    Column,
    /// <summary>B: two panels - the top one drops in, the bottom one follows - with a gap between.</summary>
    Halves,
    /// <summary>C: one smaller top-right panel that shows the top, then flips to the bottom (page dots).</summary>
    Flip,
    /// <summary>D: a short top piece in the top-right corner and a short bottom piece in the bottom-right corner.</summary>
    Corners,
    /// <summary>2026-10-27 (#393, default): regular cards take column / halves / flip / corners in turn, one per showing;
    /// the special partner cards (the Michelin pit tower, GO FAST) always take the tall column.</summary>
    Mixed,
}

public sealed partial class StoryCards
{
    /// <summary>control.json "cardLayout" (#349): classic | column | halves | flip | corners. Set from the control
    /// settings (OverlayCompositor); classic draws exactly the original cards.</summary>
    public static CardLayout Layout = CardLayout.Mixed;

    /// <summary>2026-10-27 (#412, Angelo: "maybe we make them 90 degree angle all the way around"): the story cards'
    /// slanted / chamfered edges become plain rectangles (classic and the column layouts). Approved by Angelo (on by
    /// default); the preview tool's --slanted shows the old edges.</summary>
    public static bool SquareCorners = true;   // #412 approved by Angelo 2026-10-27

    public static CardLayout ParseLayout(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "column" or "a" => CardLayout.Column,
        "halves" or "b" => CardLayout.Halves,
        "flip" or "c" => CardLayout.Flip,
        "corners" or "d" => CardLayout.Corners,
        "mixed" => CardLayout.Mixed,
        _ => CardLayout.Classic,
    };

    // the right-third column, in 1920x1080 design units: x 1290..1884 (the classic card's right margin), clear of the
    // centre third (640..1280) and of the session bar (top 172, as the classic card)
    const float ColW = 594f, ColY0 = 172f;
    const double FlipLen = 0.5;
    float colY0 = float.NaN; double colY0CardMs = double.NaN;
    double colCardT;    // the card's own clock (the Fastest Laps schedule runs on it whatever panel is drawing)

    /// <summary>How tall each card's top section is (its title, partner and headline numbers, nothing spare).</summary>
    static float TopH(StoryKind k) => k switch
    {
        StoryKind.RaceSoFar => 300, StoryKind.YourRace => 320, StoryKind.MoveOfRace => 310, StoryKind.FastestLaps => 330,
        StoryKind.PitStops => 290, StoryKind.Halfway => 270, StoryKind.WeatherWatch => 340, StoryKind.DriverOfDay => 300,
        StoryKind.CarContact => 360, StoryKind.Limits => 300, StoryKind.Cleanest => 340, _ => 320,
    };

    /// <summary>When the flip panel turns to page 2: halfway through the card, never before 4.2 s.</summary>
    static double FlipAt => Math.Max(4.2, (StoryDirector.ExitAtMs - StoryDirector.StartedMs) / 2000.0);

    // mixed (#393): the variation picked for the card on screen, chosen once when it starts and kept to its end; the
    // cycle never gives two regular cards in a row the same variation
    // #517 (Angelo: "the split variation of the cards only need to play if there is a radio playing"): corners left the
    // cycle - it is now the layout a regular card takes while a team radio call is on screen (SplitTick)
    static readonly CardLayout[] MixCycle = { CardLayout.Column, CardLayout.Halves, CardLayout.Flip };
    double mixStartMs = double.NaN;
    CardLayout mixPick = CardLayout.Column;
    int mixNext;
    /// <summary>The arrangement the card on screen is using (after mixed / special-card resolution) - previews, logs.</summary>
    public CardLayout ShownLayout { get; private set; } = CardLayout.Classic;

    /// <summary>Special partner cards (#393) always take the tall column with their own treatment: whatever card GO FAST
    /// presents (SponsorPack mapping, today Fastest Laps and On the Limit) and the Michelin pit-stop tower slot.</summary>
    public static bool IsSpecial(StoryKind k) => k == StoryKind.PitStops || Presenters.For(k) == Partner.GoFast;

    // the paged GO FAST Fastest Laps (#393): one page per class, the card's time split evenly, at least 4 s a page
    bool fastPaged;
    double pageStartMs = double.NaN, pagePer = 4;
    int pageCount = 1;
    const double MinPageS = 4, PageMove = 0.8;

    void DrawColumn(SKCanvas c, float w, float h, RaceSnapshot snap, StoryKind kind, double t, float outK)
    {
        var cls = ClassOf(snap, snap.Player!);
        colCardT = t;
        var eff = Layout;
        if (IsSpecial(kind)) eff = CardLayout.Column;
        else if (Layout == CardLayout.Mixed)
        {
            if (StoryDirector.StartedMs != mixStartMs) { mixStartMs = StoryDirector.StartedMs; mixPick = MixCycle[mixNext]; mixNext = (mixNext + 1) % MixCycle.Length; }
            eff = mixPick;
        }
        // #517: the split (corners) arrangement is the radio one - never the full layout of a card
        if (eff == CardLayout.Corners) eff = CardLayout.Column;
        double nowMs = StoryDirector.StartedMs + t * 1000;
        float sk = SplitTick(nowMs, !IsSpecial(kind));
        ShownLayout = sk >= 0.5f ? CardLayout.Corners : eff;
        fastPaged = kind == StoryKind.FastestLaps && IsSpecial(kind);
        if (fastPaged)
        {
            if (StoryDirector.StartedMs != pageStartMs)
            {
                pageStartMs = StoryDirector.StartedMs;
                pageCount = Math.Max(1, FastestList(snap, cls).Count);
                double total = (StoryDirector.ExitAtMs - StoryDirector.StartedMs) / 1000.0;
                pagePer = Math.Max(MinPageS, total / pageCount);
                StoryDirector.ExtendExit(StoryDirector.StartedMs + pagePer * pageCount * 1000, StoryDirector.StartedMs + t * 1000);
            }
        }
        // 2026-10-29 (Angelo: right-side cards "have a little winglet that goes above them" and it overlaps the session
        // bar): the gold FINAL tab (Driver of the Day, and every card during the outro, when the bar keeps its battle look
        // with the standings ticker) hangs 40 px above the card. The column now starts low enough that the tab - or the
        // card itself - keeps VpLayout.TopGap clear of the bar, its watch and the ticker; eased so it never jumps.
        bool tabUp = kind == StoryKind.DriverOfDay || OutroDirector.Running;
        float colL = w - 36 - ColW, stack = 0f;   // what hangs from the top edge above the column itself
        if (VpLayout.SessionBarVisible)
        {
            if (VpLayout.SessionBar.Right > colL) stack = VpLayout.SessionBar.Bottom;
            if (VpLayout.SessionBar.MidX + 70f > colL) stack = Math.Max(stack, VpLayout.SessionBar.MidY + 70f);
        }
        if (VpLayout.TickerVisible) stack = Math.Max(stack, VpLayout.Ticker.Bottom);
        float wantY0 = Math.Max(ColY0, stack + VpLayout.TopGap + (tabUp ? 40f : 0f));
        if (float.IsNaN(colY0) || StoryDirector.StartedMs != colY0CardMs) { colY0 = wantY0; colY0CardMs = StoryDirector.StartedMs; }
        else colY0 += (wantY0 - colY0) * 0.12f;
        float x0 = w - 36 - ColW, x1 = w - 36, y0 = colY0, y1 = h - 36;
        SKRect top, bot; double delay;
        switch (eff)
        {
            case CardLayout.Column: top = new SKRect(x0, y0, x1, y0 + TopH(kind)); bot = new SKRect(x0, top.Bottom, x1, y1); delay = 0; break;
            case CardLayout.Halves: top = new SKRect(x0, y0, x1, y0 + TopH(kind)); bot = new SKRect(x0, y1 - 400, x1, y1); delay = 0.55; break;
            case CardLayout.Flip: top = bot = new SKRect(x0, y0, x1, y0 + 420); delay = 0; break;
            default: top = new SKRect(x0, y0, x1, y0 + Math.Min(320, TopH(kind))); bot = new SKRect(x0, y1 - 300, x1, y1); delay = 0.35; break;
        }
        // #517: the split (corners) pieces - top piece kept clear of the radio card's top (never shorter than 290), the
        // bottom piece under it - and where they glide out of / back into the full layout from
        float sTopH = Math.Min(Math.Min(320, TopH(kind)), Math.Max(290, float.IsNaN(splitCap) ? 999 : splitCap - y0));
        var sTop = new SKRect(x0, y0, x1, y0 + sTopH);
        var sBot = new SKRect(x0, y1 - 300, x1, y1);
        SKRect fTop = eff == CardLayout.Flip ? new SKRect(x0, y0, x1, y0 + sTopH) : top;
        SKRect fBot = eff switch
        {
            CardLayout.Column => new SKRect(x0, top.Bottom, x1, top.Bottom + 300),
            CardLayout.Flip => new SKRect(x0, top.Bottom - 300, x1, top.Bottom),
            _ => bot,
        };
        var full = new SKRect(x0, top.Top, x1, Math.Max(top.Bottom, bot.Bottom));
        var bounds = sk <= 0 ? full : sk >= 1 ? new SKRect(x0, y0, x1, sBot.Bottom) : SKRect.Union(full, new SKRect(x0, y0, x1, sBot.Bottom));
        var wipeCol = kind switch
        {
            StoryKind.WeatherWatch => Storm, StoryKind.DriverOfDay => Gold, StoryKind.FastestLaps => GoGreen,
            StoryKind.YourRace => Hud, StoryKind.PitStops => PitOrange, StoryKind.CarContact or StoryKind.Cleanest => TpBlue, StoryKind.Limits => GoGreen,
            _ => ParseHex(cls?.Color ?? snap.Player!.ClassColor, Gold),
        };
        float bandX = bounds.Left - 30 + (bounds.Width + 60) * outK;
        c.Save();
        // 2026-11-06 (#913, Angelo): the pit stop tower itself slides off screen - no coloured wipe
        if (outK > 0 && kind == StoryKind.PitStops) c.Translate((w + 80 - bounds.Left) * outK * outK, 0);
        else if (outK > 0) c.ClipRect(new SKRect(bandX, 0, w + 10, h + 10));
        double impact = ImpactAt(kind), ti = impact >= 0 ? t - impact : -1;
        if (ti > 0 && ti < 0.4)   // camera kick as the card lands, as the classic card
        {
            float amp = 6f * (float)Math.Exp(-ti * 11);
            c.Translate((float)Math.Sin(ti * 70) * amp, (float)Math.Cos(ti * 57) * amp * 0.6f);
        }
        if (sk <= 0) Arrange(c, eff, kind, full, top, bot, delay, snap, cls, t, wipeCol);
        else if (sk >= 1) Arrange(c, CardLayout.Corners, kind, bounds, sTop, sBot, 0.35, snap, cls, t, wipeCol);
        else
        {
            // #517 morph: the two split pieces glide out of the full layout (and back in, reversed). They firm up in
            // the first part of the glide; the full layout shows only around them and fades out, so the text never
            // doubles up for long. Clocks untouched: no entrance replays.
            var mt = Lerp(fTop, sTop, sk); var mb = Lerp(fBot, sBot, sk);
            // #604 perf pass 2: the two fade sheets cover the card's column (with room for its shadows, tab and entrance),
            // not the whole 4K screen - nothing of the card is drawn left of it
            var sheet = new SKRect(x0 - 300, -50, w + 50, h + 50);
            float fullA = 1 - Smooth((sk - 0.1f) / 0.6f), splitA = Smooth(sk / 0.3f);
            if (fullA > 0.004f)
            {
                c.Save();
                if (splitA > 0.98f) { c.ClipRect(mt, SKClipOperation.Difference); c.ClipRect(mb, SKClipOperation.Difference); }
                splitLayer.Color = SKColors.White.WithAlpha((byte)(255 * fullA));
                c.SaveLayer(sheet, splitLayer);
                Arrange(c, eff, kind, full, top, bot, delay, snap, cls, t, wipeCol);
                c.Restore();
                c.Restore();
            }
            splitLayer.Color = SKColors.White.WithAlpha((byte)(255 * splitA));
            c.SaveLayer(sheet, splitLayer);
            Arrange(c, CardLayout.Corners, kind, SKRect.Union(mt, mb), mt, mb, 0.35, snap, cls, t, wipeCol);
            c.Restore();
        }
        if (fastPaged && pageCount > 1)
        {
            int page = Math.Min(pageCount - 1, (int)(t / pagePer));
            float dk = K(t, 0.8, 0.4);
            using var dp = new SKPaint { IsAntialias = true };
            for (int i = 0; i < pageCount; i++)
            {
                bool on = i == page;
                dp.Color = (on ? GoGreen : SKColors.White).WithAlpha((byte)((on ? 255 : 90) * dk));
                c.DrawCircle(top.Right - 26 - (pageCount - 1 - i) * 18, bounds.Bottom + 16, on ? 5.5f : 4.5f, dp);
            }
        }
        c.Restore();
        if (outK > 0 && outK < 1 && kind != StoryKind.PitStops)
        {
            float bw = 26;
            using (var trail = SKShader.CreateLinearGradient(new SKPoint(bandX - 220, 0), new SKPoint(bandX, 0), new[] { wipeCol.WithAlpha(0), wipeCol.WithAlpha(120) }, null, SKShaderTileMode.Clamp))
            using (var tp = new SKPaint { Shader = trail }) c.DrawRect(new SKRect(Math.Max(bounds.Left - 30, bandX - 220), bounds.Top - 10, bandX, bounds.Bottom + 10), tp);
            using (var bp = new SKPaint { Color = wipeCol }) c.DrawRect(new SKRect(bandX, bounds.Top - 10, bandX + bw, bounds.Bottom + 10), bp);
            using (var hl = new SKPaint { Color = SKColors.White.WithAlpha(160) }) c.DrawRect(new SKRect(bandX + bw - 3, bounds.Top - 10, bandX + bw, bounds.Bottom + 10), hl);
        }
    }

    /// <summary>Draws one arrangement of the card's two sections (the layouts' panels, as #349 / #393).</summary>
    void Arrange(SKCanvas c, CardLayout eff, StoryKind kind, SKRect bounds, SKRect top, SKRect bot, double delay, RaceSnapshot snap, DriverClass? cls, double t, SKColor wipeCol)
    {
        switch (eff)
        {
            case CardLayout.Column:
                ColPanel(c, kind, bounds, 0, top, bot, snap, cls, t, t, true);
                break;
            case CardLayout.Flip:
            {
                double fk = (t - FlipAt) / FlipLen;
                if (fk < 0.5)
                {
                    c.Save();
                    if (fk > 0) c.Scale(1, Math.Max(0.001f, 1 - (float)fk * 2), top.MidX, top.MidY);
                    ColPanel(c, kind, top, 1, top, top, snap, cls, t, t, true);
                    c.Restore();
                }
                else
                {
                    c.Save();
                    if (fk < 1) c.Scale(1, Math.Max(0.001f, (float)fk * 2 - 1), top.MidX, top.MidY);
                    ColPanel(c, kind, top, 2, top, top, snap, cls, 99, t - FlipAt - FlipLen * 0.5, true);
                    c.Restore();
                }
                // page dots
                float dk = K(t, 0.8, 0.4);
                using var dp = new SKPaint { IsAntialias = true };
                for (int i = 0; i < 2; i++)
                {
                    bool on = (i == 0) == (fk < 0.5);
                    dp.Color = (on ? wipeCol : SKColors.White).WithAlpha((byte)((on ? 255 : 90) * dk));
                    c.DrawCircle(top.Right - 44 + i * 18, top.Bottom + 16, on ? 5.5f : 4.5f, dp);
                }
                break;
            }
            default:
                ColPanel(c, kind, top, 1, top, bot, snap, cls, t, t, true);
                ColPanel(c, kind, bot, 2, top, bot, snap, cls, t - delay, t - delay, false);
                break;
        }
    }

    // ------------------------------------------------------------------ #517 split while the team radio is up
    /// <summary>Whether a team radio / race control card is on screen this frame. Set by the compositor every tick
    /// (RadioPopupRenderer.IsActive); the preview tools set it by hand.</summary>
    public static bool RadioOn;
    /// <summary>How long the radio has to stay on (or off) before the card follows it - a blip never moves the card.</summary>
    const double SplitDebounceMs = 300;
    /// <summary>The full &lt;-&gt; split glide.</summary>
    const double SplitMoveMs = 420;
    bool splitRaw, splitWant;
    double splitRawSinceMs, splitLin, splitLastMs = double.NaN, splitCardMs = double.NaN;
    float splitCap = float.NaN;
    readonly SKPaint splitLayer = new();

    /// <summary>0 = the card's full layout, 1 = the split (corners) layout around the radio card; eased, debounced.
    /// A card that starts while the radio is already up starts split (no glide).</summary>
    float SplitTick(double nowMs, bool canSplit)
    {
        bool raw = RadioOn;
        if (raw != splitRaw) { splitRaw = raw; splitRawSinceMs = nowMs; }
        if (StoryDirector.StartedMs != splitCardMs)
        {
            splitCardMs = StoryDirector.StartedMs;
            splitWant = raw; splitLin = raw ? 1 : 0; splitLastMs = nowMs;
            splitCap = float.NaN;
        }
        else if (splitRaw != splitWant && nowMs - splitRawSinceMs >= SplitDebounceMs) splitWant = splitRaw;
        double dt = double.IsNaN(splitLastMs) ? 0 : Math.Clamp(nowMs - splitLastMs, 0, 100);
        splitLastMs = nowMs;
        splitLin = Math.Clamp(splitLin + (splitWant ? dt : -dt) / SplitMoveMs, 0, 1);
        // where the radio card's top is (the split top piece stops above it); followed smoothly, kept after it goes
        if (VpLayout.RadioVisible && VpLayout.Radio.Height > 1)
        {
            float target = VpLayout.Radio.Top - 14;
            splitCap = float.IsNaN(splitCap) || splitLin <= 0 ? target : splitCap + (target - splitCap) * (float)Math.Min(1, dt / 120);
        }
        if (!canSplit) return 0;
        return Smooth((float)splitLin);
    }

    static float Smooth(float x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
    static SKRect Lerp(SKRect a, SKRect b, float k) =>
        new(a.Left + (b.Left - a.Left) * k, a.Top + (b.Top - a.Top) * k, a.Right + (b.Right - a.Right) * k, a.Bottom + (b.Bottom - a.Bottom) * k);

    /// <summary>One panel: the card's own entrance, its own background, then the top section (part 1), the bottom
    /// section (part 2) or both (part 0, the tall column: top in <paramref name="topR"/>, bottom in <paramref name="botR"/>).
    /// <paramref name="te"/> runs the entrance, <paramref name="tc"/> the content's reveals.</summary>
    void ColPanel(SKCanvas c, StoryKind kind, SKRect r, int part, SKRect topR, SKRect botR, RaceSnapshot s, DriverClass? cls, double te, double tc, bool tab)
    {
        if (te < 0) return;
        int sc = c.Save();
        float a = 1;
        ColEnter(c, kind, r, te, ref a);
        var inner = ColBg(c, kind, r, part, botR, s, cls, te, a);
        if (part == 0 && kind == StoryKind.PitStops)
        {
            // #401: the tower is one piece - one screen with the header and numbers over the quickest stops
            float split = inner.Top + 214;
            PitTop(c, new SKRect(inner.Left, inner.Top, inner.Right, split), s, tc, a);
            PitBottom(c, new SKRect(inner.Left, split, inner.Right, inner.Bottom), s, tc, a);
            PitOutro(c, inner, tc, a);
        }
        else if (part == 0)
        {
            ColTop(c, kind, new SKRect(inner.Left, topR.Top, inner.Right, topR.Bottom), s, cls, tc, a);
            ColDivider(c, kind, inner, topR.Bottom, s, cls, tc, a);
            ColBottom(c, kind, new SKRect(inner.Left, botR.Top, inner.Right, inner.Bottom), s, cls, tc, a);
        }
        else if (part == 1) ColTop(c, kind, inner, s, cls, tc, a);
        else ColBottom(c, kind, inner, s, cls, tc, a);
        c.RestoreToCount(sc);
        sc = c.Save();
        ColFinish(c, kind, r, te, a);
        c.RestoreToCount(sc);
        double impact = ImpactAt(kind), ti = impact >= 0 ? te - impact : -1;
        if (tab) BroadcastLayer(c, r, kind, s, te, ti, a);
        else ImpactFlash(c, r, ti, a);
    }

    /// <summary>The landing flash and light streak on a panel that has no tab (BroadcastLayer's first half).</summary>
    static void ImpactFlash(SKCanvas c, SKRect r, double ti, float a)
    {
        if (ti < 0 || ti >= 0.5) return;
        c.Save(); c.ClipRect(r);
        if (ti < 0.2)
            using (var fl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(70 * (1 - ti / 0.2) * a)), BlendMode = SKBlendMode.Plus }) c.DrawRect(r, fl);
        float sx = r.Left - 160 + (r.Width + 320) * Ease((float)(ti / 0.5));
        using (var band = new SKPath())
        {
            band.MoveTo(sx + 90, r.Top); band.LineTo(sx + 150, r.Top); band.LineTo(sx + 60, r.Bottom); band.LineTo(sx, r.Bottom); band.Close();
            using var bp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(46 * a)), BlendMode = SKBlendMode.Plus };
            c.DrawPath(band, bp);
        }
        c.Restore();
    }

    // ------------------------------------------------------------------ entrances (each card's own, per panel)
    static void ColEnter(SKCanvas c, StoryKind kind, SKRect r, double t, ref float a)
    {
        switch (kind)
        {
            case StoryKind.RaceSoFar:
            { float wipe = K(t, 0, 0.7); c.ClipRect(new SKRect(r.Left - 2, r.Top - 4, r.Left + (r.Width + 4) * wipe, r.Bottom + 4)); break; }
            case StoryKind.YourRace:
            { float boot = K(t, 0, 0.5), sc = 1.04f - 0.04f * boot; c.Scale(sc, sc, r.MidX, r.MidY); break; }
            case StoryKind.MoveOfRace:
            { float rise = K(t, 0, 0.7); c.ClipRect(new SKRect(r.Left - 4, r.Bottom - (r.Height + 8) * rise, r.Right + 4, r.Bottom + 4)); break; }
            case StoryKind.FastestLaps:
            { float inK = K(t, 0, 0.6); c.Translate((1 - inK) * (r.Width + 60), 0); break; }
            case StoryKind.PitStops:
            { float rise = OutBack(Math.Clamp((float)(t / 0.75), 0, 1)); c.Scale(1, Math.Max(0.001f, rise), r.MidX, r.Bottom); break; }
            case StoryKind.Halfway:
            { float open = K(t, 0, 0.7); c.ClipRect(new SKRect(r.MidX - (r.Width / 2 + 60) * open, r.Top - 4, r.MidX + (r.Width / 2 + 60) * open, r.Bottom + 4)); break; }
            case StoryKind.WeatherWatch:
            { float open = K(t, 0.15, 0.45), halfH = Math.Max(2, r.Height / 2 * open); c.ClipRect(new SKRect(r.Left - 2, r.MidY - halfH, r.Right + 2, r.MidY + halfH)); break; }
            case StoryKind.DriverOfDay:
            { float up = K(t, 0, 0.9); a *= up; float sc = 1.03f - 0.03f * up; c.Scale(sc, sc, r.MidX, r.MidY); break; }
            case StoryKind.CarContact or StoryKind.Cleanest:
            { float inK = K(t, 0, 0.35); c.ClipRect(new SKRect(r.Left - 4, r.Top - 4, r.Left + (r.Width + 30) * inK, r.Bottom + 4)); break; }
            case StoryKind.Limits:
            { float inK = K(t, 0, 0.55); c.ClipRect(new SKRect(r.Left - 4, r.Top - 4, r.Right + 4, r.Top + (r.Height + 8) * inK)); break; }
        }
    }

    // ------------------------------------------------------------------ backgrounds (the card's identity, per panel)
    const float ColSlant = 24f;

    static SKPath SlantCol(SKRect r)
    {
        var shape = new SKPath();
        float sl = SquareCorners ? 0 : ColSlant;
        shape.MoveTo(r.Left + sl, r.Top); shape.LineTo(r.Right, r.Top); shape.LineTo(r.Right, r.Bottom); shape.LineTo(r.Left, r.Bottom); shape.Close();
        return shape;
    }

    /// <summary><see cref="Glass"/> for a column panel: the same print shadow, weave, colour slab, keyline and livery
    /// stripe, with the slant scaled to the narrower panel.</summary>
    static void GlassCol(SKCanvas c, SKPath shape, SKRect r, SKColor c0, SKColor c1, SKColor edge, float a)
    {
        c.Save(); c.Translate(10, 10);
        using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)) }) c.DrawPath(shape, sh);
        c.Restore();
        using (var g = SKShader.CreateLinearGradient(new SKPoint(r.Left, r.Top), new SKPoint(r.Left, r.Bottom),
                   new[] { c1.WithAlpha((byte)(246 * a)), c0.WithAlpha((byte)(250 * a)) }, null, SKShaderTileMode.Clamp))
        using (var gp = new SKPaint { IsAntialias = true, Shader = g }) c.DrawPath(shape, gp);
        c.Save(); c.ClipPath(shape, antialias: true);
        Texture(c, r, a);
        Theme.SkinNow(c, r, ThemeSurface.Story, a);   // #601: the broadcast theme (nothing on Default)
        using (var slab = new SKPath())
        {
            float sl = SquareCorners ? 0 : ColSlant;
            slab.MoveTo(r.Left + sl, r.Top); slab.LineTo(r.Left + sl + 14, r.Top); slab.LineTo(r.Left + 14, r.Bottom); slab.LineTo(r.Left, r.Bottom); slab.Close();
            using var sp = new SKPaint { IsAntialias = true, Color = edge.WithAlpha((byte)(255 * a)) };
            c.DrawPath(slab, sp);
        }
        using (var kl = new SKPaint { IsAntialias = true, StrokeWidth = 3, Color = SKColors.White.WithAlpha((byte)(210 * a)) })
            c.DrawLine(r.Left + (SquareCorners ? 0 : ColSlant) + 20, r.Top, r.Left + 20, r.Bottom, kl);
        using (var topBar = new SKPaint { Color = edge.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(r.Left, r.Top, r.Right, r.Top + 4), topBar);
        Livery(c, r, edge, a);
        c.Restore();
    }

    /// <summary>Draws the panel's background and leaves the canvas clipped to it; returns the content rect.</summary>
    SKRect ColBg(SKCanvas c, StoryKind kind, SKRect r, int part, SKRect botR, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        switch (kind)
        {
            case StoryKind.YourRace:
            {
                float ch = SquareCorners ? 0 : 26;
                using var shape = Chamfer(r, ch);
                BlurPlates.Path(c, SquareCorners ? 21 : 20, r, shape, r, new SKColor(0, 0, 0, (byte)(120 * a * K(t, 0, 0.5))), 18);   // #604: a picture while it holds
                c.ClipPath(shape, antialias: true);
                using (var bg = new SKPaint { Color = new SKColor(4, 7, 11, (byte)(250 * a)) }) c.DrawRect(r, bg);
                var p = s.Player!;
                if (part != 1 && LiveryArt.GetFront(p) == null && LiveryArt.Get(p) == null)
                {
                    var photo = CarPhoto(p.CarName) ?? CarArt.Fallback(p.CarName, p.CarBrand, p.ClassName, p.ClassColor);
                    if (photo != null)
                    {
                        var pr = part == 0 ? new SKRect(r.Left, botR.Top, r.Right, r.Bottom) : r;
                        float s0 = Math.Max(pr.Width / photo.Width, pr.Height / photo.Height), pw = photo.Width * s0, ph = photo.Height * s0;
                        using var ip = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * K(t, 0.1, 0.9))) };
                        c.DrawBitmap(photo, new SKRect(pr.MidX - pw / 2, pr.MidY - ph / 2, pr.MidX + pw / 2, pr.MidY + ph / 2), ip);
                    }
                }
                using (var dk = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom),
                           new[] { new SKColor(2, 5, 9, (byte)(150 * a)), new SKColor(2, 5, 9, (byte)(205 * a)), new SKColor(2, 5, 9, (byte)(240 * a)) },
                           new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
                using (var dp = new SKPaint { Shader = dk }) c.DrawRect(r, dp);
                Halftone(c, r, a);
                using (var sl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(40 * a)) })
                    for (float yy = r.Top; yy < r.Bottom; yy += 3) c.DrawRect(new SKRect(r.Left, yy, r.Right, yy + 1), sl);
                if (t < 0.45)
                    using (var fl = new SKPaint { Color = Hud.WithAlpha((byte)(40 * (1 - t / 0.45) * ((int)(t * 30) % 2))) }) c.DrawRect(r, fl);
                return new SKRect(r.Left + 40, r.Top, r.Right - 34, r.Bottom);
            }
            case StoryKind.WeatherWatch:
            {
                var rr = new SKRoundRect(r, 22, 22);
                BlurPlates.Rect(c, 22, r, 22, new SKColor(0, 0, 0, (byte)(130 * a)), 18);   // #604: a picture while it holds
                using (var bg = SKShader.CreateRadialGradient(new SKPoint(r.MidX, r.MidY), Math.Max(r.Width, r.Height) * 0.8f, new[] { new SKColor(6, 26, 44, (byte)(250 * a)), new SKColor(2, 10, 18, (byte)(252 * a)) }, null, SKShaderTileMode.Clamp))
                using (var bp = new SKPaint { Shader = bg }) c.DrawRoundRect(rr, bp);
                c.ClipRoundRect(rr, antialias: true);
                var ctr = part == 1 ? new SKPoint(r.Right - 110, r.Bottom + 40) : ScopeCentre(part == 0 ? new SKRect(r.Left, botR.Top, r.Right, r.Bottom) : r);
                using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.3f, Color = Storm.WithAlpha((byte)(70 * a)) })
                {
                    for (float rad = 90; rad < 1000; rad += 90) c.DrawCircle(ctr, rad, ring);
                    c.DrawLine(r.Left, ctr.Y, r.Right, ctr.Y, ring); c.DrawLine(ctr.X, r.Top, ctr.X, r.Bottom, ring);
                }
                using (var scl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(55 * a)) })
                    for (float yy = r.Top; yy < r.Bottom; yy += 3) c.DrawRect(new SKRect(r.Left, yy, r.Right, yy + 1), scl);
                return new SKRect(r.Left + 36, r.Top, r.Right - 30, r.Bottom);
            }
            case StoryKind.DriverOfDay:
            {
                BlurPlates.Rect(c, 23, r, 0, new SKColor(0, 0, 0, (byte)(160 * a)), 22);   // #604: a picture while it holds
                using (var bg = new SKPaint { Color = new SKColor(4, 4, 5, (byte)(250 * a)) }) c.DrawRect(r, bg);
                c.ClipRect(r);
                float sw = (float)Math.Sin(t * 0.9) * 6;
                for (int i = 0; i < 2; i++)
                {
                    float ox = i == 0 ? r.Left + 30 : r.Right - 30, dir = i == 0 ? 1f : -1f;
                    c.Save(); c.Translate(ox, r.Top - 20); c.RotateDegrees(dir * (24 + sw));
                    using var beam = new SKPath(); beam.MoveTo(-8, 0); beam.LineTo(8, 0); beam.LineTo(130, 1000); beam.LineTo(-130, 1000); beam.Close();
                    using var bs = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 1000), new[] { new SKColor(255, 240, 200, (byte)(70 * a * K(t, 0.4, 0.8))), new SKColor(255, 240, 200, 0) }, null, SKShaderTileMode.Clamp);
                    using var bp = new SKPaint { Shader = bs, IsAntialias = true, BlendMode = SKBlendMode.Plus };
                    c.DrawPath(beam, bp);
                    c.Restore();
                }
                using (var gf = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = Gold.WithAlpha((byte)(230 * a)) })
                {
                    c.DrawRect(new SKRect(r.Left + 14, r.Top + 14, r.Right - 14, r.Bottom - 14), gf);
                    gf.StrokeWidth = 1; gf.Color = Gold.WithAlpha((byte)(120 * a));
                    c.DrawRect(new SKRect(r.Left + 22, r.Top + 22, r.Right - 22, r.Bottom - 22), gf);
                }
                return new SKRect(r.Left + 30, r.Top, r.Right - 30, r.Bottom);
            }
            case StoryKind.PitStops:
                return part == 0 ? PitTower(c, r, t, a) : PitFrame(c, r, part, t, a);
            default:
            {
                SKColor c0, c1, edge;
                switch (kind)
                {
                    case StoryKind.MoveOfRace: c0 = new SKColor(14, 12, 10); c1 = new SKColor(22, 19, 16); edge = Gold; break;
                    case StoryKind.FastestLaps or StoryKind.Limits: c0 = new SKColor(6, 16, 12); c1 = new SKColor(10, 26, 20); edge = GoGreen; break;
                    case StoryKind.Halfway: c0 = new SKColor(10, 13, 20); c1 = new SKColor(14, 18, 27); edge = ParseHex(cls?.Color, Gold); break;
                    case StoryKind.CarContact or StoryKind.Cleanest: c0 = new SKColor(16, 17, 28); c1 = new SKColor(28, 30, 46); edge = TpBlue; break;
                    default: c0 = new SKColor(9, 12, 18); c1 = new SKColor(15, 19, 28); edge = ParseHex(cls?.Color, Gold); break;
                }
                using var shape = SlantCol(r);
                GlassCol(c, shape, r, c0, c1, edge, a);
                c.ClipPath(shape, antialias: true);
                if (kind == StoryKind.RaceSoFar)
                    using (var gp = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(7 * a)), StrokeWidth = 1 })
                    {
                        for (float gx = r.Left; gx < r.Right; gx += 32) c.DrawLine(gx, r.Top, gx, r.Bottom, gp);
                        for (float gy = r.Top; gy < r.Bottom; gy += 32) c.DrawLine(r.Left, gy, r.Right, gy, gp);
                    }
                if (kind == StoryKind.CarContact)
                {
                    if (part != 2) TpBand(c, new SKRect(r.Left, r.Top + 4, r.Right, r.Top + 38), a);
                    Scratches(c, r, a * 0.4f, 7, 14);
                }
                if (kind is StoryKind.CarContact or StoryKind.Cleanest) TpPattern(c, r, t, a);
                if (kind == StoryKind.Limits) LimitsTheme(c, r, s, t, a);   // #424
                if (kind == StoryKind.Limits && part != 2) KerbLeft(c, r, KerbColours(s), a, 30, SquareCorners ? 0 : ColSlant);   // #411
                return new SKRect(r.Left + 62, r.Top, r.Right - 30, r.Bottom);
            }
        }
    }

    static SKPath Chamfer(SKRect r, float ch)
    {
        var shape = new SKPath();
        shape.MoveTo(r.Left + ch, r.Top); shape.LineTo(r.Right, r.Top); shape.LineTo(r.Right, r.Bottom - ch);
        shape.LineTo(r.Right - ch, r.Bottom); shape.LineTo(r.Left, r.Bottom); shape.LineTo(r.Left, r.Top + ch); shape.Close();
        return shape;
    }

    /// <summary>Things drawn over a finished panel: the cockpit's HUD brackets, the radar's frame, the CRT line.</summary>
    static void ColFinish(SKCanvas c, StoryKind kind, SKRect r, double t, float a)
    {
        if (kind == StoryKind.YourRace)
        {
            float ch = SquareCorners ? 0 : 26;
            float bk = K(t, 0.05, 0.5), bl = 56 * bk;
            using var bp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Hud.WithAlpha((byte)(230 * a)) };
            c.DrawLine(r.Left + ch, r.Top, r.Left + ch + bl, r.Top, bp); c.DrawLine(r.Left, r.Top + ch, r.Left, r.Top + ch + bl, bp);
            c.DrawLine(r.Left, r.Top + ch, r.Left + ch, r.Top, bp);
            c.DrawLine(r.Right, r.Bottom - ch, r.Right, r.Bottom - ch - bl, bp); c.DrawLine(r.Right - ch, r.Bottom, r.Right - ch - bl, r.Bottom, bp);
            c.DrawLine(r.Right - ch, r.Bottom, r.Right, r.Bottom - ch, bp);
            c.DrawLine(r.Right - bl, r.Top, r.Right, r.Top, bp); c.DrawLine(r.Right, r.Top, r.Right, r.Top + bl, bp);
            c.DrawLine(r.Left, r.Bottom, r.Left + bl, r.Bottom, bp); c.DrawLine(r.Left, r.Bottom - bl, r.Left, r.Bottom, bp);
        }
        else if (kind == StoryKind.WeatherWatch)
        {
            float open = K(t, 0.15, 0.45), halfH = Math.Max(2, r.Height / 2 * open), line = K(t, 0, 0.18);
            c.Save(); c.ClipRect(new SKRect(r.Left - 4, r.MidY - halfH - 2, r.Right + 4, r.MidY + halfH + 2));
            using (var frame = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = Storm.WithAlpha((byte)(200 * a)) }) c.DrawRoundRect(new SKRoundRect(r, 22, 22), frame);
            c.Restore();
            if (line < 1 || open < 0.2f)
                using (var crt = new SKPaint { Color = SKColors.White.WithAlpha((byte)(230 * (1 - open) * line * a)) })
                    c.DrawRect(new SKRect(r.Left + r.Width * (1 - line) / 2, r.MidY - 2, r.Right - r.Width * (1 - line) / 2, r.MidY + 2), crt);
        }
    }

    /// <summary>The thin line between the two sections of the tall column, in the card's colour.</summary>
    static void ColDivider(SKCanvas c, StoryKind kind, SKRect inner, float y, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        var col = kind switch
        {
            StoryKind.WeatherWatch => Storm, StoryKind.DriverOfDay => Gold, StoryKind.FastestLaps or StoryKind.Limits => GoGreen,
            StoryKind.YourRace => Hud, StoryKind.CarContact or StoryKind.Cleanest => TpBlue, StoryKind.MoveOfRace => Gold,
            _ => ParseHex(cls?.Color, Gold),
        };
        float k = K(t, 0.5, 0.5);
        using var dp = new SKPaint { IsAntialias = true, StrokeWidth = 1.5f, Color = col.WithAlpha((byte)(110 * a * k)) };
        c.DrawLine(inner.Left, y, inner.Left + (inner.Right - inner.Left) * k, y, dp);
    }

    // ------------------------------------------------------------------ shared column pieces

    /// <summary>The card's eyebrow, then its partner: right after the words when the row has room ("BY [mark]",
    /// Angelo 2026-10-20), else on its own row underneath. Returns how far down the next line starts (0 or 36).</summary>
    float ColHead(SKCanvas c, float L, float R, float y, string text, DriverClass? cls, SKColor col, StoryKind kind, string verb, float markH, double t, float a)
    {
        float ex = Eyebrow(c, L, y, text, cls, col, t, a);
        var partner = Presenters.For(kind);
        float pw = PresentedWidth(partner, verb, markH);
        if (pw <= 0) return 0;
        float pa = a * K(t, 0.4, 0.4);
        if (ex + 16 + pw <= R) { Presenters.DrawPresentedAfter(c, partner, verb, ex + 16, y - 6, markH, pa); return 0; }
        Presenters.DrawPresentedAfter(c, partner, verb, L, y + 30, markH, pa);
        return 36;
    }

    static float PresentedWidth(Partner p, string verb, float h)
    {
        if (p == Partner.None) return 0;
        float mw = Presenters.MarkWidth(p, h);
        if (mw <= 0) return 0;
        using var lp = new SKPaint { Typeface = Fonts.Inter.Bold, TextSize = Math.Max(9, h * 0.34f) };
        return TrackedWidth(lp, verb, lp.TextSize * 0.24f) + 10 + mw;
    }

    /// <summary>A labelled number box (the column's headline numbers): label in the card's small caps, the value big
    /// and rolling in, an optional line under it.</summary>
    void Stat(SKCanvas c, SKRect b, string label, string value, SKColor col, SKColor accent, double t, double at, float a, float size, string? sub = null)
    {
        float k = K(t, at, 0.35);
        if (k <= 0) return;
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(110 * a * k)) }) c.DrawRect(b, bg);
        using (var bd = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = accent.WithAlpha((byte)(110 * a * k)) }) c.DrawRect(b, bd);
        using (var tk = new SKPaint { Color = accent.WithAlpha((byte)(255 * a * k)) }) c.DrawRect(new SKRect(b.Left, b.Top, b.Left + 16, b.Top + 3), tk);
        using (var lp = P(Fonts.Inter.Bold, 13, Txt3, a * k)) TextTracked.Draw(c, lp, Fit(label, lp, b.Width - 20), b.Left + 12, b.Top + 24, 2.2f);
        using var vp = P(Fonts.BarlowCondensed.ExtraBold, size, col, a * k);
        while (vp.MeasureText(value) > b.Width - 22 && vp.TextSize > 28) vp.TextSize -= 1;
        float vy = sub != null ? b.Bottom - 34 : b.Bottom - 16;
        DrawRolling(c, value, b.Left + 12, vy, vp, K(t, at, 0.9), SKTextAlign.Left);
        if (sub != null)
            using (var sp = P(Fonts.Inter.SemiBold, 13, Txt2, a * k)) c.DrawText(Fit(sub, sp, b.Width - 22), b.Left + 12, b.Bottom - 12, sp);
    }

    /// <summary>Breaks text into at most <paramref name="max"/> lines that fit <paramref name="maxW"/> (the last one
    /// shortened with "…" if it still runs over). Returns the line count.</summary>
    static int Wrap(string text, SKPaint p, float maxW, string[] lines, int max)
    {
        int n = 0;
        string cur = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string tryLine = cur.Length == 0 ? word : cur + " " + word;
            if (p.MeasureText(tryLine) <= maxW || cur.Length == 0) { cur = tryLine; continue; }
            if (n == max - 1) { cur = tryLine; continue; }
            lines[n++] = cur; cur = word;
        }
        if (cur.Length > 0 && n < max) lines[n++] = Fit(cur, p, maxW);
        return n;
    }

    // ------------------------------------------------------------------ the sections, card by card
    void ColTop(SKCanvas c, StoryKind kind, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        switch (kind)
        {
            case StoryKind.RaceSoFar: RaceSoFarTop(c, r, s, cls, t, a); break;
            case StoryKind.YourRace: YourRaceTop(c, r, s, cls, t, a); break;
            case StoryKind.MoveOfRace: MoveTop(c, r, s, t, a); break;
            case StoryKind.FastestLaps: FastestTop(c, r, s, cls, t, a); break;
            case StoryKind.PitStops: PitTop(c, r, s, t, a); break;
            case StoryKind.Halfway: HalfwayTop(c, r, s, cls, t, a); break;
            case StoryKind.WeatherWatch: WeatherTop(c, r, s, cls, t, a); break;
            case StoryKind.DriverOfDay: TrophyTop(c, r, s, t, a); break;
            case StoryKind.CarContact: ContactTop(c, r, s, t, a); break;
            case StoryKind.Limits: LimitsTop(c, r, s, t, a); break;
            case StoryKind.Cleanest: CleanTop(c, r, s, t, a); break;
        }
    }

    void ColBottom(SKCanvas c, StoryKind kind, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        switch (kind)
        {
            case StoryKind.RaceSoFar: RaceSoFarBottom(c, r, s, cls, t, a); break;
            case StoryKind.YourRace: YourRaceBottom(c, r, s, cls, t, a); break;
            case StoryKind.MoveOfRace: MoveBottom(c, r, s, t, a); break;
            case StoryKind.FastestLaps: FastestBottom(c, r, s, cls, t, a); break;
            case StoryKind.PitStops: PitBottom(c, r, s, t, a); break;
            case StoryKind.Halfway: HalfwayBottom(c, r, s, cls, t, a); break;
            case StoryKind.WeatherWatch: WeatherBottom(c, r, s, cls, t, a); break;
            case StoryKind.DriverOfDay: TrophyBottom(c, r, s, t, a); break;
            case StoryKind.CarContact: ContactBottom(c, r, s, t, a); break;
            case StoryKind.Limits: LimitsBottom(c, r, s, cls, t, a); break;
            case StoryKind.Cleanest: CleanBottom(c, r, s, t, a); break;
        }
    }

    // ================================================================== 1. RACE SO FAR
    void RaceSoFarTop(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 52;
        y += ColHead(c, L, R, y, "RACE SO FAR", cls, Red, StoryKind.RaceSoFar, "PRESENTED BY", 30, t, a);
        using (var tp = P(Fonts.BarlowCondensed.Black, 76, Txt, a * K(t, 0.3, 0.4)))
            c.DrawText(Fit(ToGo(s).ToUpperInvariant(), tp, R - L), L, y + 80, tp);
        var fl = cls != null ? RaceDiary.Fastest(cls.Name) : null;
        float sy = y + 104, sh = Math.Min(118, r.Bottom - 16 - sy), sw = (R - L - 24) / 3f;
        if (sh < 80) return;
        var acc = ParseHex(cls?.Color, Gold);
        Stat(c, new SKRect(L, sy, L + sw, sy + sh), "LEAD CHANGES", (cls != null ? RaceDiary.LeadChanges(cls.Name) : 0).ToString(CultureInfo.InvariantCulture), Txt, acc, t, 1.0, a, 50);
        Stat(c, new SKRect(L + sw + 12, sy, L + sw * 2 + 12, sy + sh), "PIT STOPS", (cls != null ? RaceDiary.TotalPitStops(cls.Drivers) : 0).ToString(CultureInfo.InvariantCulture), Txt, acc, t, 1.1, a, 50);
        Stat(c, new SKRect(L + sw * 2 + 24, sy, R, sy + sh), "FASTEST", fl is { } f ? LapTimeFormat.Lap(f.Time) : "-", Purple, acc, t, 1.2, a, 40, fl?.Name);
    }

    void RaceSoFarBottom(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        float L = r.Left, R = r.Right;
        using (var hp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 0.5, 0.4))) TextTracked.Draw(c, hp, "POSITION BY LAP", L, r.Top + 34, 2.4f);
        if (cls == null || cls.Drivers.Count == 0) return;
        int n = cls.Drivers.Count;
        // the running order under the chart, two columns, as many rows as the panel has room for (none in the corners)
        int rows = Math.Clamp((int)((r.Height - 300) / 34), 0, 5);
        float listH = rows > 0 ? rows * 34 + 18 : 0;
        var area = new SKRect(L + 40, r.Top + 62, R - 12, r.Bottom - 48 - listH);
        if (!(RaceDiary.LiveChart(cls.Name) is { } lc && LiveLapChart(c, area, s, cls, lc.Chart, lc.Now, t, a)))
            LapChartLines(c, area, s, cls, t, a);
        if (rows == 0) return;
        float lk = K(t, 1.9, 0.5), colW = (R - L) / 2f, ly0 = r.Bottom - listH + 10;
        int slots = rows * 2, shownN = Math.Min(n, slots);
        int playerAt = -1; for (int i = 0; i < n; i++) if (cls.Drivers[i].CarIdx == s.PlayerCarIdx) playerAt = i;
        for (int k = 0; k < shownN; k++)
        {
            int i = k;
            if (k == shownN - 1 && playerAt >= shownN) i = playerAt;      // always show where you are
            var d = cls.Drivers[i];
            bool me = d.CarIdx == s.PlayerCarIdx;
            float lx = L + (k / rows) * colW, ly = ly0 + (k % rows) * 34 + 22, la = a * lk;
            var col = me ? Blue : i == 0 ? Gold : LinePalette[d.CarIdx % LinePalette.Length];
            using (var dp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * la)) }) c.DrawRect(new SKRect(lx, ly - 9, lx + 4, ly + 4), dp);
            using (var pp = P(Fonts.BarlowCondensed.ExtraBold, 25, i == 0 ? Gold : Txt, la)) { pp.TextAlign = SKTextAlign.Right; c.DrawText((i + 1).ToString(CultureInfo.InvariantCulture), lx + 38, ly, pp); }
            using (var np = P(Fonts.Inter.SemiBold, 21, me ? SKColors.White : Txt2, la)) c.DrawText(Fit(NameFormat.Broadcast(d), np, colW - 110), lx + 48, ly, np);
            int g = RaceDiary.Gained(d);
            if (g != 0) DrawDelta(c, lx + colW - 30, ly - 7, g, la, 18);
        }
    }

    /// <summary>The lap-by-lap chart (the classic card's fallback when there's no live chart yet), lines only.</summary>
    void LapChartLines(SKCanvas c, SKRect area, RaceSnapshot s, DriverClass cls, double t, float a)
    {
        int n = cls.Drivers.Count;
        var chart = RaceDiary.LapChart(cls.Drivers);
        int maxLap = 1, minLap = int.MaxValue;
        foreach (var kv in chart) foreach (var hp in kv.Value) { maxLap = Math.Max(maxLap, hp.Lap); minLap = Math.Min(minLap, hp.Lap); }
        if (minLap == int.MaxValue || minLap >= maxLap) minLap = Math.Max(0, maxLap - 1);
        float X(double lap) => area.Left + area.Width * (float)((lap - minLap) / Math.Max(1, maxLap - minLap));
        float Y(int pos) => area.Top + area.Height * (pos - 1) / Math.Max(1, n - 1);
        using (var lab = P(Fonts.Inter.SemiBold, 12, Txt3, a))
        using (var tick = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * a)), StrokeWidth = 1 })
        {
            int step = Math.Max(1, (int)Math.Ceiling((maxLap - minLap) / 6.0));
            lab.TextAlign = SKTextAlign.Center;
            for (int lp = minLap; lp <= maxLap; lp += step)
            {
                c.DrawLine(X(lp), area.Top - 10, X(lp), area.Bottom + 6, tick);
                c.DrawText(lp == 0 ? "GRID" : "L" + lp, X(lp), area.Bottom + 28, lab);
            }
            lab.TextAlign = SKTextAlign.Right;
            for (int q = 1; q <= n; q += n > 10 ? 2 : 1) c.DrawText("P" + q, area.Left - 14, Y(q) + 4, lab);
        }
        double reveal = minLap + (maxLap - minLap) * Ease((float)((t - 0.45) / 1.7));
        var order = cls.Drivers.Select((d, i) => (d, i)).OrderBy(x => x.d.CarIdx == s.PlayerCarIdx ? 2 : x.i == 0 ? 1 : 0).ToList();
        foreach (var (d, i) in order)
        {
            if (!chart.TryGetValue(d.CarIdx, out var hist) || hist.Count < 2 || reveal <= minLap) continue;
            bool me = d.CarIdx == s.PlayerCarIdx;
            var col = me ? Blue : i == 0 ? Gold : LinePalette[d.CarIdx % LinePalette.Length];
            using var path = new SKPath();
            path.MoveTo(X(hist[0].Lap), Y(hist[0].Pos));
            SKPoint end = new(X(hist[0].Lap), Y(hist[0].Pos));
            for (int k = 1; k < hist.Count; k++)
            {
                if (hist[k].Lap <= reveal) { end = new SKPoint(X(hist[k].Lap), Y(hist[k].Pos)); path.LineTo(end); continue; }
                double f = (reveal - hist[k - 1].Lap) / Math.Max(1e-6, hist[k].Lap - hist[k - 1].Lap);
                if (f > 0)
                {
                    end = new SKPoint(X(hist[k - 1].Lap + (hist[k].Lap - hist[k - 1].Lap) * f), Y(hist[k - 1].Pos) + (Y(hist[k].Pos) - Y(hist[k - 1].Pos)) * (float)f);
                    path.LineTo(end);
                }
                break;
            }
            if (me)
                using (var glow = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 12, Color = Blue.WithAlpha((byte)(70 * a)), ImageFilter = SKImageFilter.CreateBlur(6, 6), StrokeJoin = SKStrokeJoin.Round })
                    c.DrawPath(path, glow);
            using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = me ? 5 : i == 0 ? 3.5f : 2.4f, Color = col.WithAlpha((byte)((me || i == 0 ? 255 : 170) * a)), StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round })
                c.DrawPath(path, lp);
            using (var dp = new SKPaint { IsAntialias = true, Color = col.WithAlpha((byte)(255 * a)) }) c.DrawCircle(end, me ? 6 : 4, dp);
        }
    }

    // ================================================================== 2. YOUR RACE
    void YourRaceTop(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        var p = s.Player!;
        float L = r.Left, R = r.Right, y = r.Top + 54;
        float hk = K(t, 0.25, 0.4);
        using (var ep = P(Fonts.BarlowCondensed.Bold, 22, Hud, a * hk)) TextTracked.Draw(c, ep, OutroDirector.Running ? "YOUR RACE  ·  FINAL" : "YOUR RACE", L, y, 3.4f);
        var clogo = cls != null ? ClassLogoBmp(cls.Name) : null;
        if (clogo != null) { float lh = 24, lw = clogo.Width * lh / clogo.Height; DrawLogo(c, clogo, R - lw / 2, y - 6, lw, lh, a * hk); }
        using (var np = P(Fonts.BarlowCondensed.Black, 64, SKColors.White, a * K(t, 0.35, 0.4))) c.DrawText(Fit(p.Name.ToUpperInvariant(), np, R - L), L, y + 58, np);
        using (var sp = P(Fonts.Inter.SemiBold, 16, Txt2, a * K(t, 0.4, 0.4))) TextTracked.Draw(c, sp, Fit($"#{p.CarNumber}   ·   {p.CarName.ToUpperInvariant()}", sp, R - L - 20), L, y + 86, 1.8f);
        int start = RaceDiary.GridPos(p.CarIdx), now = p.PositionInClass, gained = start > 0 ? start - now : 0;
        float bw = (R - L - 24) / 3f, by = y + 106, bh = Math.Min(140, r.Bottom - 18 - by);
        if (bh < 90) return;
        Readout(c, new SKRect(L, by, L + bw, by + bh), "STARTED", start > 0 ? $"P{start}" : "-", SKColors.White, t, 0.55, a, 78);
        Readout(c, new SKRect(L + bw + 12, by, L + bw * 2 + 12, by + bh), "NOW", $"P{now}", now == 1 ? Gold : SKColors.White, t, 0.65, a, 78);
        Readout(c, new SKRect(L + bw * 2 + 24, by, R, by + bh), "PLACES", gained > 0 ? $"+{gained}" : gained.ToString(CultureInfo.InvariantCulture), gained > 0 ? Gain : gained < 0 ? Loss : Txt2, t, 0.75, a, 78);
    }

    void YourRaceBottom(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        var p = s.Player!;
        float L = r.Left, R = r.Right;
        float fy = r.Bottom - 26 - 96, fw = (R - L - 24) / 3f;
        // our own picture of the car (iRacing's renderer with its livery), lit, sitting above the readouts
        var front = LiveryArt.GetFront(p);
        var own = front ?? LiveryArt.Get(p);
        var area = new SKRect(L + 40, r.Top + 70, R - 8, fy - 64);
        bool trace = area.Height >= 110;
        if (own != null)
        {
            float maxH = trace ? Math.Min(area.Height * 0.8f, 200) : Math.Max(60, fy - r.Top - 30);
            float ch2 = Math.Min(maxH, own.Height * 1.6f), cw2 = ch2 * own.Width / own.Height;
            if (cw2 > (R - L) * 0.80f) { cw2 = (R - L) * 0.80f; ch2 = cw2 * own.Height / own.Width; }
            float drift = (1 - K(t, 0.1, 1.2)) * 60f;
            float bottom = fy - 10;
            var dst = new SKRect(R - cw2 + drift, bottom - ch2, R + drift, bottom);
            using (var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(dst.MidX, dst.MidY), cw2 * 0.6f,
                       new[] { new SKColor(70, 110, 170, (byte)(70 * a)), SKColors.Transparent }, null, SKShaderTileMode.Clamp) })
                c.DrawRect(r, glow);
            using (var sh2 = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12f) })
                c.DrawOval(new SKRect(dst.Left + cw2 * 0.06f, dst.Bottom - ch2 * 0.10f, dst.Right - cw2 * 0.02f, dst.Bottom + ch2 * 0.05f), sh2);
            using var ip2 = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * K(t, 0.1, 0.9))) };
            c.DrawImage(own, dst, ip2);
        }
        if (trace)
        {
            using (var box = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f, Color = Hud.WithAlpha((byte)(90 * a * K(t, 0.8, 0.4))) })
                c.DrawRect(new SKRect(area.Left - 40, area.Top - 26, area.Right + 8, area.Bottom + 40), box);
            using (var tl = P(Fonts.Inter.Bold, 11, Hud, a * K(t, 0.8, 0.4))) TextTracked.Draw(c, tl, "POSITION TRACE", area.Left - 30, area.Top - 8, 3f);
            PositionTrace(c, area, RaceDiary.PlayerHistory, p.PositionInClass, p.LapsCompleted, cls?.Drivers.Count ?? 0, t, a, Hud);
        }
        int now = p.PositionInClass;
        float cb = ClassBest(cls);
        string best = p.BestLapTime > 0 ? LapTimeFormat.Lap(p.BestLapTime) : "-";
        string toBest = p.BestLapTime > 0 && cb > 0 ? (Math.Abs(p.BestLapTime - cb) < 0.0005f ? "CLASS FASTEST" : "+" + (p.BestLapTime - cb).ToString("0.000", CultureInfo.InvariantCulture)) : "-";
        string ahead = now <= 1 ? "LEADING" : p.IntervalSeconds is double ia && ia > 0 ? "+" + ia.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-";
        Readout(c, new SKRect(L, fy, L + fw, fy + 96), "BEST LAP", best, SKColors.White, t, 1.2, a, 34);
        Readout(c, new SKRect(L + fw + 12, fy, L + fw * 2 + 12, fy + 96), "TO CLASS BEST", toBest, toBest == "CLASS FASTEST" ? Purple : SKColors.White, t, 1.3, a, toBest == "CLASS FASTEST" ? 26 : 34);
        Readout(c, new SKRect(L + fw * 2 + 24, fy, R, fy + 96), "TO CAR AHEAD", ahead, SKColors.White, t, 1.4, a, 34);
    }

    // ================================================================== 3. MOVE OF THE RACE
    void MoveTop(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var d = StoryDirector.MoveOfRace(s);
        float L = r.Left, R = r.Right, y = r.Top + 54;
        string mt = OutroDirector.Running ? "BIGGEST CLIMBER" : "MOVES OF THE RACE";
        y += ColHead(c, L, R, y, mt, null, Gold, StoryKind.MoveOfRace, Presenters.Verb(StoryKind.MoveOfRace), 34, t, a);
        if (d == null) return;
        int g = Math.Max(1, RaceDiary.GridPos(d.CarIdx)), now = Math.Max(1, d.PositionInClass), gained = g - now;
        float bigK = K(t, 1.2, 0.5);
        float bigW;
        using (var gp = P(Fonts.BarlowCondensed.Black, 200, Gain, a * bigK))
        {
            gp.TextAlign = SKTextAlign.Right;
            bigW = gp.MeasureText($"+{gained}");
            c.DrawText($"+{gained}", R, y + 186, gp);
        }
        using (var lp = P(Fonts.Inter.Bold, 18, Gain, a * bigK))
        {
            string pl = gained == 1 ? "PLACE GAINED" : "PLACES GAINED";
            TextTracked.Draw(c, lp, pl, R - TrackedWidth(lp, pl, 3f), y + 216, 3f);
        }
        float leftW = R - L - Math.Max(bigW, 200) - 16;
        using (var np = P(Fonts.BarlowCondensed.Black, 66, Txt, a * K(t, 0.4, 0.4)))
        {
            var parts = d.Name.ToUpperInvariant().Split(' ', 2);
            c.DrawText(Fit(parts[0], np, leftW), L, y + 72, np);
            if (parts.Length > 1) c.DrawText(Fit(parts[1], np, leftW), L, y + 128, np);
        }
        using (var sp = P(Fonts.Inter.SemiBold, 15, Txt2, a * K(t, 0.45, 0.4))) c.DrawText(Fit(d.CarName.ToUpperInvariant(), sp, leftW), L, y + 158, sp);
        if (y + 220 <= r.Bottom - 6)
            using (var rp = P(Fonts.BarlowCondensed.ExtraBold, 38, Txt, a * K(t, 1.6, 0.4)))
            {
                string ga = $"P{g}", na = $"P{now}";
                c.DrawText(ga, L, y + 216, rp);
                float ax0 = L + rp.MeasureText(ga) + 12, ax1 = ax0 + 70;
                Arrow(c, ax0, ax1, y + 206, K(t, 1.7, 0.5), a, 3f);
                c.DrawText(na, ax1 + 20, y + 216, rp);
            }
    }

    void MoveBottom(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var d = StoryDirector.MoveOfRace(s);
        if (d == null) return;
        int g = Math.Max(1, RaceDiary.GridPos(d.CarIdx)), now = Math.Max(1, d.PositionInClass);
        float L = r.Left, R = r.Right;
        // the tower of places, built from A&B's shingles; slots never get so small that the P numbers shrink - a long
        // climb shows the places it went through, evenly picked, always with the grid slot and where they are now
        var tower = new SKRect(L, r.Top + 30, L + 210, r.Bottom - 30);
        int cap = Math.Max(2, (int)(tower.Height / 54));
        Span<int> pos = stackalloc int[16];
        int span = g - now + 1, slots;
        if (span + 1 <= cap)
        {
            int first = Math.Max(1, now - 1), last = Math.Min(first + cap - 1, Math.Max(g + 1, first + span));
            slots = Math.Min(16, last - first + 1);
            for (int i = 0; i < slots; i++) pos[i] = first + i;
        }
        else
        {
            slots = Math.Min(16, cap);
            for (int i = 0; i < slots; i++) pos[i] = now + (int)Math.Round((g - now) * (double)i / (slots - 1));
        }
        float slotH = tower.Height / slots;
        int gi = 0, ni = 0;
        for (int i = 0; i < slots; i++) { if (pos[i] == g) gi = i; if (pos[i] == now) ni = i; }
        var tile = Bmp("logos", "sponsors", "ab_roof_tile.png");
        if (tile != null)
        {
            float ts = 230f / tile.Width;
            using var shd = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(ts, ts).PostConcat(SKMatrix.CreateTranslation(tower.Left, tower.Top)));
            using var tp = new SKPaint { Shader = shd, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
            c.DrawRect(tower, tp);
        }
        using (var dk = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(120 * a)) }) c.DrawRect(tower, dk);
        float climbK = K(t, 0.9, 1.7);
        float markY = tower.Top + slotH * gi + (slotH * ni - slotH * gi) * climbK;
        for (int i = 0; i < slots; i++)
        {
            int q = pos[i];
            var sr = new SKRect(tower.Left, tower.Top + slotH * i, tower.Right, tower.Top + slotH * (i + 1));
            float lay = K(t, 0.25 + (slots - i) * 0.04, 0.3);
            bool passed = q <= g && q >= now && markY <= sr.Top + 2;
            if (passed) using (var lit = new SKPaint { Color = Gold.WithAlpha((byte)(70 * a)) }) c.DrawRect(sr, lit);
            using (var cl = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(200 * a * lay)), StrokeWidth = 3 }) c.DrawLine(sr.Left, sr.Bottom, sr.Left + sr.Width * lay, sr.Bottom, cl);
            using (var pl = P(Fonts.BarlowCondensed.ExtraBold, Math.Min(28, slotH * 0.52f), q == now ? Gold : passed ? Txt : Txt2, a * lay))
                c.DrawText("P" + q, sr.Left + 14, sr.MidY + pl.TextSize * 0.36f, pl);
            if (q == g) using (var gt = P(Fonts.Inter.Bold, 11, Txt3, a * lay)) { gt.TextAlign = SKTextAlign.Right; c.DrawText("GRID", sr.Right - 12, sr.MidY + 4, gt); }
        }
        using (var edge = new SKPaint { Color = Gold.WithAlpha((byte)(200 * a)), StrokeWidth = 2, Style = SKPaintStyle.Stroke }) c.DrawRect(tower, edge);
        var plate = new SKRect(tower.Left + 64, markY + 6, tower.Right - 10, markY + slotH - 6);
        using (var pb = new SKPaint { IsAntialias = true, Color = new SKColor(14, 15, 20, (byte)(245 * a)) }) c.DrawRoundRect(plate, 8, 8, pb);
        using (var pe = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = Gold.WithAlpha((byte)(255 * a)) }) c.DrawRoundRect(plate, 8, 8, pe);
        using (var nb = P(Fonts.BarlowCondensed.Black, Math.Min(38, plate.Height * 0.72f), SKColors.White, a)) c.DrawText("#" + d.CarNumber, plate.Left + 12, plate.MidY + nb.TextSize * 0.36f, nb);
        if (Maker(d.CarBrand) is { } mkb)
        {
            float bh = plate.Height * 0.66f, bw2 = mkb.Width * bh / mkb.Height; if (bw2 > 50) { bw2 = 50; bh = mkb.Height * bw2 / mkb.Width; }
            using var dark = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
            c.DrawBitmap(mkb, new SKRect(plate.Right - 8 - bw2, plate.MidY - bh / 2, plate.Right - 8, plate.MidY + bh / 2), dark);
        }
        // also climbing, beside the tower
        float lx = tower.Right + 28;
        var others = new List<Driver>();
        foreach (var cl in s.ClassLeaderboard) foreach (var o in cl.Drivers) if (o.CarIdx != d.CarIdx && RaceDiary.Gained(o) >= 1) others.Add(o);
        others.Sort((p1, p2) => RaceDiary.Gained(p2).CompareTo(RaceDiary.Gained(p1)));
        float oy = r.Top + 46;
        if (others.Count > 0)
            using (var hp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 2.0, 0.4))) TextTracked.Draw(c, hp, "ALSO CLIMBING", lx, oy, 3f);
        for (int i = 0; i < Math.Min(6, others.Count) && oy + 40 + i * 42 < r.Bottom - 24; i++)
        {
            var o = others[i];
            float ok = K(t, 2.05 + i * 0.08, 0.3);
            using (var on = P(Fonts.Inter.SemiBold, 19, o.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt2, a * ok)) c.DrawText(Fit(NameFormat.Broadcast(o), on, R - lx - 70), lx, oy + 38 + i * 42, on);
            DrawDelta(c, R - 26, oy + 31 + i * 42, RaceDiary.Gained(o), a * ok, 18);
        }
    }

    // ================================================================== 4. FASTEST LAPS
    List<(DriverClass Cls, (float Time, int CarIdx, string Name, int Lap) F)> FastestList(RaceSnapshot s, DriverClass? pcls)
    {
        var list = new List<(DriverClass Cls, (float Time, int CarIdx, string Name, int Lap) F)>();
        if (pcls != null && RaceDiary.Fastest(pcls.Name) is { } pf) list.Add((pcls, pf));
        foreach (var cl in s.ClassLeaderboard)
            if (cl != pcls && RaceDiary.Fastest(cl.Name) is { } of) list.Add((cl, of));
        list.Sort((x, y) => y.F.Time.CompareTo(x.F.Time));      // slowest class first, building to the quickest
        return list;
    }

    /// <summary>The classic card's schedule, on the card's own clock: wind up to the first, then hold / sweep on.</summary>
    (int Idx, float Tr) FastestSchedule(int count, double t)
    {
        if (fastPaged)
        {
            int pi = Math.Min(count - 1, (int)(t / pagePer));
            double within = t - pi * pagePer;
            return (pi, pi > 0 && within < PageMove ? Ease((float)(within / PageMove)) : 1f);
        }
        const double First = 2.5, Hold = 1.7, Move = 0.8;
        int idx = 0; float tr = 1;
        double tt = t - First;
        if (tt >= 0 && count > 1)
        {
            int k = (int)(tt / (Hold + Move));
            double within = tt - k * (Hold + Move);
            if (within < Hold) { idx = k; tr = 1; }
            else { idx = k + 1; tr = Ease((float)((within - Hold) / Move)); }
            if (idx >= count) { idx = count - 1; tr = 1; }
        }
        return (idx, tr);
    }

    void FastestTop(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? pcls, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 54;
        y += ColHead(c, L, R, y, "FASTEST LAPS", null, GoGreen, StoryKind.FastestLaps, Presenters.Verb(StoryKind.FastestLaps), 38, t, a);
        using (var sg = P(Fonts.BarlowCondensed.Bold, 17, GoGreen, a * K(t, 0.6, 0.4))) TextTracked.Draw(c, sg, "DRIVE SMARTER, GO FASTER", L, y + 32, 2.4f);
        var list = FastestList(s, pcls);
        if (list.Count == 0) return;
        var (idx, tr) = FastestSchedule(list.Count, colCardT);
        var cur = list[idx]; var prev = idx > 0 ? list[idx - 1] : cur;
        var shown = tr < 0.5f ? prev : cur;
        float xa = tr >= 1 ? 1 : Math.Abs(tr * 2 - 1);
        float rowY = y + 62;
        if (ClassLogoBmp(shown.Cls.Name) is { } clb) { float lh = 28, lw = clb.Width * lh / clb.Height; DrawLogo(c, clb, L + lw / 2, rowY, lw, lh, a * K(t, 0.6, 0.4) * xa); }
        else using (var cn = P(Fonts.BarlowCondensed.Bold, 22, Txt2, a * K(t, 0.6, 0.4) * xa)) TextTracked.Draw(c, cn, ClassLogo.DisplayName(shown.Cls.Name), L, rowY + 8, 2.4f);
        string lapTxt = LapTimeFormat.Lap(shown.F.Time);
        using (var tp = P(Fonts.BarlowCondensed.ExtraBold, 108, GoGreen, a * K(t, 0.6, 0.4) * xa))
        {
            if (idx == 0 && tr >= 1) DrawRolling(c, lapTxt, L - 4, rowY + 112, tp, K(t, 0.8, 1.7), SKTextAlign.Left);
            else c.DrawText(lapTxt, L - 4, rowY + 112, tp);
        }
        Driver? fd = null; foreach (var d in shown.Cls.Drivers) if (d.CarIdx == shown.F.CarIdx) fd = d;
        using (var np = P(Fonts.BarlowCondensed.Bold, 34, shown.F.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt, a * K(t, 2.4, 0.4) * xa))
            c.DrawText(Fit(shown.F.Name.ToUpperInvariant(), np, R - L), L, rowY + 152, np);
        using (var sp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 2.5, 0.4) * xa))
            c.DrawText(Fit($"LAP {shown.F.Lap}" + (fd != null ? "   ·   " + fd.CarName.ToUpperInvariant() : ""), sp, R - L), L, rowY + 176, sp);
        var p = s.Player!;
        float cb0 = ClassBest(pcls);
        if (p.BestLapTime > 0 && rowY + 204 <= r.Bottom - 4)
            using (var yb = P(Fonts.Inter.Bold, 13, Txt2, a * K(t, 0.4, 0.4)))
                TextTracked.Draw(c, yb, $"YOUR BEST  {LapTimeFormat.Lap(p.BestLapTime)}" + (cb0 > 0 && p.BestLapTime - cb0 > 0.0005f ? $"   +{p.BestLapTime - cb0:0.000}" : ""), L, rowY + 202, 1.6f);
    }

    void FastestBottom(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? pcls, double t, float a)
    {
        var list = FastestList(s, pcls);
        if (list.Count == 0) return;
        var (idx, tr) = FastestSchedule(list.Count, colCardT);
        var cur = list[idx]; var prev = idx > 0 ? list[idx - 1] : cur;
        float L = r.Left, R = r.Right;
        // every class, one row each (its own small watch, logo, time, driver); the big watch above when there's room
        const float RowH = 66;
        // paged (#393): this page's class only - its three quickest under the big watch instead of every class
        // #410: with the (twice-size) 911 behind the watch, a class page lists its two quickest instead of three
        var car = Bmp("logos", "sponsors", "gofast_911_front.png");
        int qRows = car != null ? 2 : 3;
        float listTop = fastPaged ? r.Bottom - 20 - qRows * 42 - 44 : r.Bottom - 20 - list.Count * RowH;
        float wr = Math.Min(190, Math.Min((listTop - r.Top - 36) / 2f, (R - L) / 2f - 10));
        // 2026-10-27 (#398, Angelo: "the car needs to be bigger ... to the boundaries of the horizontal width"): GO FAST's
        // 911 head-on spans the panel's full inner width on every class page; the watch keeps the classic proportion to it
        // (about a fifth of the car's width) so the headlights show either side of it
        // 2026-10-27 (#410, Angelo): the car twice the inner width - it runs past the padding to the panel's own edges
        // and is clipped by the panel (and to this section, so it never sits over the title); the watch stays on top
        float carW = (R - L) * 2, carH = car != null ? carW * car.Height / car.Width : 0;
        if (car != null) wr = Math.Min(wr, carW * 0.235f);
        if (wr >= 70)
        {
            float cx = (L + R) / 2f, cy = r.Top + 18 + wr;
            if (car != null)
            {
                c.Save();
                c.ClipRect(new SKRect(r.Left - 120, r.Top, r.Right + 120, r.Bottom));
                float ck = K(t, 0.15, 0.8);
                float cw = carW, chh = carH;
                var dst = new SKRect(cx - cw / 2, cy - chh * 0.52f, cx + cw / 2, cy + chh * 0.48f);
                c.SaveLayer(dst, null);
                using (var cp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(105 * a * ck)) })
                    c.DrawBitmap(car, dst, cp);
                using (var mk2 = SKShader.CreateRadialGradient(new SKPoint(cx, cy), cw * 0.52f, new[] { SKColors.Black, SKColors.Black, SKColors.Black.WithAlpha(0) }, new[] { 0f, 0.86f, 1f }, SKShaderTileMode.Clamp))
                using (var mp2 = new SKPaint { Shader = mk2, BlendMode = SKBlendMode.DstIn }) c.DrawRect(dst, mp2);
                c.Restore();
                if (Bmp("logos", "sponsors", "gofast_light.png") is { } fl)
                {
                    // the headlights flare on as each class page lands
                    double lt = t - 0.55 - (fastPaged ? idx * pagePer : 0);
                    float lk = lt < 0 ? (fastPaged && idx > 0 ? 1 : 0) : lt < 0.12 ? Math.Max(fastPaged && idx > 0 ? 1 : 0, (float)(lt / 0.12) * 1.4f) : 1f + 0.4f * (float)Math.Exp(-(lt - 0.12) * 4);
                    // #508 (2026-10-29, Angelo: "the porsche needs the second row of headlight sprites ... and it should flash like
                    // the pattern of headlight flashing the GT3 cars do"): the 911's lamps have two rows of LEDs (53% and 57% down
                    // the picture) - both glow; once lit, a GT3-style double high-beam flash (on, off, on) every 3.2 s
                    if (lt > 1.0)
                    {
                        double ph = (lt - 1.0) % 3.2;
                        if (ph < 0.09 || (ph >= 0.18 && ph < 0.27)) lk = Math.Max(lk, 1.55f);
                    }
                    // 2026-10-30 (#508 again): the upper row now matches the lower one (same glow size, full strength)
                    for (int i = 0; i < 2; i++)
                        foreach (float hy in HeadlightRows)
                        {
                            var hp = new SKPoint(dst.Left + dst.Width * (i == 0 ? 0.255f : 0.745f), dst.Top + dst.Height * hy);
                            float fw = cw * 0.455f * lk, fh = fw * fl.Height / fl.Width;
                            using var lp = new SKPaint { BlendMode = SKBlendMode.Plus, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * Math.Min(1, lk))) };
                            c.DrawBitmap(fl, new SKRect(hp.X - fw / 2, hp.Y - fh / 2, hp.X + fw / 2, hp.Y + fh / 2), lp);
                        }
                }
                c.Restore();
            }
            float wk = K(t, 0.2, 0.5);
            c.Save(); c.Scale(0.85f + 0.15f * wk, 0.85f + 0.15f * wk, cx, cy);
            using (var sh = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(150 * a * wk)), ImageFilter = SKImageFilter.CreateBlur(16, 16) }) c.DrawCircle(cx, cy + 12, wr, sh);
            using (var lay = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a * wk)) })
            {
                c.SaveLayer(lay);
                if (colCardT < 2.5 || idx == 0 && tr >= 1)
                    watch.DrawChrono(c, cx, cy, wr, cur.F.Time, K(t, 0.5, 2.0), GoGreen);
                else
                {
                    var a0 = WatchRenderer.ChronoAngles(prev.F.Time);
                    var a1 = WatchRenderer.ChronoAngles(cur.F.Time);
                    double dSec = ((a1.Sec - a0.Sec) % 360 + 360) % 360;
                    double dTen = ((a1.Tenth - a0.Tenth) % 360 + 360) % 360 + 360;
                    watch.DrawChronoAngles(c, cx, cy, wr, a0.Sec + dSec * tr, a0.Min + (a1.Min - a0.Min) * tr, a0.Tenth + dTen * tr, GoGreen);
                }
                c.Restore();
            }
            c.Restore();
        }
        else if (!fastPaged)
            using (var hp = P(Fonts.BarlowCondensed.Bold, 22, GoGreen, a * K(t, 1.0, 0.4))) TextTracked.Draw(c, hp, "EVERY CLASS", L, r.Top + 40, 3f);
        if (fastPaged) { ClassQuickest(c, L, R, listTop, tr < 0.5f ? prev.Cls : cur.Cls, s, t, a * (tr >= 1 ? 1 : Math.Abs(tr * 2 - 1)), qRows); return; }
        int active = tr < 0.5f ? Math.Max(0, idx - 1) : idx;
        for (int i = 0; i < list.Count; i++)
        {
            float ok = K(t, 1.4 + i * 0.15, 0.4);
            bool on = i == active;
            float oy = listTop + i * RowH + RowH / 2, ox = L + 32;
            if (on)
            {
                using (var hl = new SKPaint { Color = GoGreen.WithAlpha((byte)(28 * a * ok)) }) c.DrawRect(new SKRect(L - 10, oy - RowH / 2 + 3, R, oy + RowH / 2 - 3), hl);
                using (var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, Color = GoGreen.WithAlpha((byte)(255 * a * ok)) }) c.DrawCircle(ox, oy, 31, ring);
            }
            using (var lay = new SKPaint { Color = SKColors.White.WithAlpha((byte)((on ? 255 : 150) * a * ok)) })
            {
                c.SaveLayer(lay);
                watch.DrawChrono(c, ox, oy, 27, list[i].F.Time, K(t, 1.5 + i * 0.15, 1.4), GoGreen);
                c.Restore();
            }
            float ta = a * ok * (on ? 1f : 0.6f), tx = ox + 48;
            float logoW = 0;
            if (ClassLogoBmp(list[i].Cls.Name) is { } cl) { float lh = 20, lw = Math.Min(70, cl.Width * lh / cl.Height); DrawLogo(c, cl, tx + lw / 2, oy, lw, lh, ta); logoW = lw + 14; }
            else using (var cn = P(Fonts.BarlowCondensed.Bold, 20, Txt2, ta)) { string nm = ClassLogo.DisplayName(list[i].Cls.Name); c.DrawText(nm, tx, oy + 7, cn); logoW = cn.MeasureText(nm) + 14; }
            using (var tp = P(Fonts.BarlowCondensed.ExtraBold, 34, GoGreen, ta)) c.DrawText(LapTimeFormat.Lap(list[i].F.Time), tx + logoW, oy + 12, tp);
            using (var np = P(Fonts.Inter.SemiBold, 15, Txt2, ta)) { np.TextAlign = SKTextAlign.Right; c.DrawText(Fit(list[i].F.Name, np, R - tx - logoW - 150), R, oy + 7, np); }
        }
    }

    /// <summary>A class's three quickest laps (the paged Fastest Laps' per-class page).</summary>
    void ClassQuickest(SKCanvas c, float L, float R, float y, DriverClass cls, RaceSnapshot s, double t, float a, int rows = 3)
    {
        using (var hp = P(Fonts.BarlowCondensed.Bold, 22, GoGreen, a * K(t, 1.0, 0.4)))
            TextTracked.Draw(c, hp, "QUICKEST IN " + ClassLogo.DisplayName(cls.Name).ToUpperInvariant(), L, y + 24, 3f);
        var best = new List<Driver>();
        foreach (var d in cls.Drivers) if (d.BestLapTime > 1) best.Add(d);
        best.Sort((x, z) => x.BestLapTime.CompareTo(z.BestLapTime));
        for (int i = 0; i < Math.Min(rows, best.Count); i++)
        {
            var d = best[i];
            float ok = K(t, 1.4 + i * 0.12, 0.4), ry = y + 44 + i * 42 + 30;
            bool me = d.CarIdx == s.PlayerCarIdx;
            if (me) using (var hl = new SKPaint { Color = Blue.WithAlpha((byte)(46 * a * ok)) }) c.DrawRect(new SKRect(L - 10, ry - 30, R, ry + 8), hl);
            using (var pp = P(Fonts.BarlowCondensed.ExtraBold, 28, i == 0 ? GoGreen : Txt2, a * ok)) c.DrawText((i + 1).ToString(CultureInfo.InvariantCulture), L, ry, pp);
            using (var np = P(Fonts.Inter.SemiBold, 19, me ? SKColors.White : Txt, a * ok)) c.DrawText(Fit(NameFormat.Broadcast(d), np, R - L - 190), L + 34, ry - 2, np);
            using (var tp = P(Fonts.BarlowCondensed.ExtraBold, 34, i == 0 ? GoGreen : SKColors.White, a * ok)) { tp.TextAlign = SKTextAlign.Right; c.DrawText(LapTimeFormat.Lap(d.BestLapTime), R, ry + 2, tp); }
        }
    }

    // ================================================================== 5. PIT STOPS - the Michelin tower, split
    // Top: the tower's screen with the header and the headline numbers. Bottom: a second screen with the quickest stops
    // and the leaning pylon on its right with MICHELIN running up it (PIT LANE without Angelo's michelin.png). The
    // Bibendum panel at the screen's right end doesn't fit a third-width tower and is left out.
    /// <summary>PitFrame's outline (same sums): part 1 the screen alone, part 2 the screen + the leaning pylon.</summary>
    static SKPath PitFramePath1(SKRect r) => PitFramePath(r, 1);
    static SKPath PitFramePath2(SKRect r) => PitFramePath(r, 2);
    static SKPath PitFramePath(SKRect r, int part)
    {
        const float PylonTop = 112f, PylonBot = 88f, Lean = 22f;
        float leg = Math.Clamp(r.Height * 0.2f, 60, 110);
        var box = part == 2 ? new SKRect(r.Left, r.Top + 6, r.Right - PylonTop + 18, r.Bottom - leg) : new SKRect(r.Left, r.Top + 6, r.Right, r.Bottom - 6);
        var tower = new SKPath();
        tower.AddRoundRect(box, 24, 24);
        float pTopL = r.Right - PylonTop, pTopR = r.Right, pBotR = r.Right - Lean, pBotL = pBotR - PylonBot, pY0 = box.Top + 26;
        if (part == 2)
        {
            using var stem = new SKPath();
            stem.MoveTo(pTopL - 30, pY0); stem.LineTo(pTopR, pY0); stem.LineTo(pBotR, r.Bottom); stem.LineTo(pBotL, r.Bottom); stem.LineTo(pTopL - 30, pY0 + 60); stem.Close();
            using var u = tower.Op(stem, SKPathOp.Union);
            if (u != null) { tower.Reset(); tower.AddPath(u); }
        }
        return tower;
    }

    SKRect PitFrame(SKCanvas c, SKRect r, int part, double t, float a)
    {
        const float F = 22f, PylonTop = 112f, PylonBot = 88f, Lean = 22f;
        float leg = Math.Clamp(r.Height * 0.2f, 60, 110);   // the pylon runs on below the screen to the ground, like the real tower
        var box = part == 2 ? new SKRect(r.Left, r.Top + 6, r.Right - PylonTop + 18, r.Bottom - leg) : new SKRect(r.Left, r.Top + 6, r.Right, r.Bottom - 6);
        float pTopL = r.Right - PylonTop, pTopR = r.Right, pBotR = r.Right - Lean, pBotL = pBotR - PylonBot, pY0 = box.Top + 26;
        // #604 perf pass 2: the outline (a path union) once per card size; shadow + bevel blurs as pictures while it holds
        using var tower = PathMemo.Get(part == 2 ? 12 : 11, r, part == 2 ? PitFramePath2 : PitFramePath1);
        TowerBody(c, part == 2 ? 12 : 11, r, tower, box.Top, a, 18, 12, 8, 3);   // shadow, body, bevel, outline: one picture while it holds
        if (part == 2)
        {
            // the yellow edge down the pylon's inner side, and the word running up it, slanted like the real lettering
            float yTop = box.Bottom - 10;
            float ex = (pBotL - pTopL) / (r.Bottom - pY0);
            using (var yp = new SKPaint { IsAntialias = true, Color = MYellow.WithAlpha((byte)(255 * a)) })
            using (var edge = new SKPath())
            {
                float xa = pTopL + ex * (yTop - pY0);
                edge.MoveTo(xa + 4, yTop); edge.LineTo(xa + 12, yTop); edge.LineTo(pBotL + 12, r.Bottom - 2); edge.LineTo(pBotL + 4, r.Bottom - 2); edge.Close();
                if (yTop < r.Bottom - 30) c.DrawPath(edge, yp);
            }
            string word = CornerBrand.User("michelin") != null ? "MICHELIN" : "PIT LANE";
            float midTop = (pTopL + pTopR) / 2f, midBot = (pBotL + pBotR) / 2f;
            float sy = (pY0 + r.Bottom) / 2f + 10, sx = midTop + (midBot - midTop) * ((sy - pY0) / (r.Bottom - pY0)) + 6;
            c.Save();
            c.Translate(sx, sy); c.RotateDegrees(-90 + MathF.Atan2(midTop - midBot, r.Bottom - pY0) * 180 / MathF.PI);
            using var wp = P(Fonts.BarlowCondensed.Black, 80, SKColors.White, a);
            wp.TextSkewX = -0.22f; wp.TextAlign = SKTextAlign.Center;
            float maxLen = (r.Bottom - pY0) - 60;
            while (wp.MeasureText(word) > maxLen && wp.TextSize > 30) wp.TextSize -= 2;
            while (wp.MeasureText(word) < maxLen * 0.85f && wp.TextSize < PylonBot * 0.66f) wp.TextSize += 2;
            c.DrawText(word, 0, wp.TextSize * 0.36f, wp);
            c.Restore();
        }
        var screen = new SKRect(box.Left + F + 2, box.Top + F, box.Right - F, box.Bottom - F);
        using (var sp = new SKPaint { IsAntialias = true, Color = new SKColor(9, 11, 16, (byte)(250 * a)) }) c.DrawRoundRect(screen, 10, 10, sp);
        c.ClipRoundRect(new SKRoundRect(screen, 10, 10), antialias: true);
        using (var yp2 = new SKPaint { Color = MYellow.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(screen.Left, screen.Top, screen.Left + 14, screen.Bottom), yp2);
        return new SKRect(screen.Left + 14, screen.Top, screen.Right, screen.Bottom);
    }

    /// <summary>#401 (Angelo: "it should be one piece and change it back to the Michelin Man on the stem"): the whole
    /// tower as ONE shape, as the classic card - the screen up top in its thick blue frame, the leaning pylon leaving its
    /// right end with an elbow and running down to the foot of the column; on the pylon the Michelin Man (his
    /// "michelin man full.png", else "michelin man.png", clipped to the pylon) or, without his pictures, the typed word.</summary>
    /// <summary>#446 (2026-10-27, Angelo: "the outro of the card should show the logo at the end on the part where the
    /// information is"): over the card's last ~2.6 s the screen eases to white with the commercial's closing logo
    /// (corner_logos\michelin outro.png - his own frame, never shipped) and holds it as the card leaves. Only when the card
    /// ran its course (not when it was cut short by a battle).</summary>
    static void PitOutro(SKCanvas c, SKRect inner, double t, float a)
    {
        var logo = CornerBrand.User("michelin outro");
        if (logo == null) return;
        double life = (StoryDirector.ExitAtMs - StoryDirector.StartedMs) / 1000.0;
        if (life < 7) return;
        double toExit = life - t;
        float k = (float)Math.Clamp((2.6 - toExit) / 0.6, 0, 1);
        if (k <= 0) return;
        k = k * k * (3 - 2 * k);
        var full = new SKRect(inner.Left - 14, inner.Top, inner.Right, inner.Bottom);
        using (var wp = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * k * a)) }) c.DrawRect(full, wp);
        // #689 (2026-10-31, Angelo: "take the top michelin man out and just leave the text michelin motion for life, that
        // way there are not two michelin man at the same time"): only the part of his outro picture from the yellow line
        // down (yellow line, MICHELIN, MOTION FOR LIFE) - the running man on the stem is the only Michelin Man at the close
        var src = new SKRect(0, logo.Height * (286f / 500f), logo.Width, logo.Height);
        float pad = 34, sc = Math.Min((full.Width - 2 * pad) / src.Width, (full.Height - 2 * pad) / src.Height);
        float lw = src.Width * sc, lh = src.Height * sc, grow = 0.94f + 0.06f * k;
        lw *= grow; lh *= grow;
        using var lp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * k * a)) };
        c.DrawBitmap(logo, src, new SKRect(full.MidX - lw / 2, full.MidY - lh / 2, full.MidX + lw / 2, full.MidY + lh / 2), lp);
    }

    /// <summary>PitTower's outline (same sums): the screen box and the leaning stem with its elbow, as one shape.</summary>
    static SKPath PitTowerPath(SKRect r)
    {
        const float TopW = 150f, BotW = 112f, Lean = 26f, Elbow = 40f;
        var box = new SKRect(r.Left, r.Top + 6, r.Right, Math.Max(r.Top + 420, r.Bottom - 226));
        float topR = r.Right - 6, topL = topR - TopW, botR = topR - Lean, botL = botR - BotW;
        var tower = new SKPath();
        tower.AddRoundRect(box, 24, 24);
        using (var stem = new SKPath())
        {
            // #683 (2026-10-31, Angelo: "where the stem attaches to the body on the right side it needs to be a smoother
            // corner ... more like the real tower"): the screen's right side now flows down into the stem's leaning outer
            // edge in one smooth curve (was the box's rounded corner meeting the stem edge in a little notch)
            float sTop = box.Bottom - 40, sx = (botR - topR) / (r.Bottom - sTop);
            float y3 = box.Bottom + 90, x3 = topR + sx * (y3 - sTop);
            float un = MathF.Sqrt(sx * sx + 1), ux = sx / un, uy = 1 / un;
            stem.MoveTo(topL - Elbow, box.Bottom - 56); stem.LineTo(r.Right, box.Bottom - 56);
            stem.CubicTo(r.Right, box.Bottom + 6, x3 - ux * 50, y3 - uy * 50, x3, y3);
            stem.LineTo(botR, r.Bottom); stem.LineTo(botL, r.Bottom);
            float ex = (botL - topL) / (r.Bottom - box.Bottom);
            stem.LineTo(topL + ex * Elbow, box.Bottom + Elbow);
            stem.QuadTo(topL, box.Bottom, topL - Elbow, box.Bottom);   // the elbow
            stem.Close();
            using var u = tower.Op(stem, SKPathOp.Union);
            if (u != null) { tower.Reset(); tower.AddPath(u); }
        }
        return tower;
    }

    SKRect PitTower(SKCanvas c, SKRect r, double t, float a)
    {
        const float F = 22f, TopW = 150f, BotW = 112f, Lean = 26f, Elbow = 40f;
        var box = new SKRect(r.Left, r.Top + 6, r.Right, Math.Max(r.Top + 420, r.Bottom - 226));
        float topR = r.Right - 6, topL = topR - TopW, botR = topR - Lean, botL = botR - BotW;
        // #604 perf pass 2: the Michelin tower's outline (a path union) once per card size; its soft shadow and the
        // blurred bevel as screen-grid pictures while it holds still (they were an offscreen blur + a CPU mask a frame)
        using var tower = PathMemo.Get(15, r, PitTowerPath);
        TowerBody(c, 15, r, tower, box.Top, a, 18, 12, 8, 3);   // shadow, body, bevel, outline: one picture while it holds
        // the yellow edge down the pylon's inner side
        float ex2 = (botL - topL) / (r.Bottom - box.Bottom);
        using (var yp = new SKPaint { IsAntialias = true, Color = MYellow.WithAlpha((byte)(255 * a)) })
        using (var edge = new SKPath())
        {
            float y0 = box.Bottom + Elbow, xa = topL + ex2 * Elbow;
            edge.MoveTo(xa + 4, y0); edge.LineTo(xa + 12, y0); edge.LineTo(botL + 12, r.Bottom - 2); edge.LineTo(botL + 4, r.Bottom - 2); edge.Close();
            c.DrawPath(edge, yp);
        }
        var mlogo = CornerBrand.User("michelin");
        var bib = CornerBrand.User("michelin man full") ?? CornerBrand.User("michelin man");
        float pylTop = box.Bottom + 12, pylBot = r.Bottom - 12;
        float midTop = (topL + topR) / 2f, midBot = (botL + botR) / 2f;
        bool ran = false;
        if (MichelinRun.Count > 0)   // #409: the running Michelin Man, upright on a white panel at the top of the stem, once, then the logo
        {
            // #436: the man runs INSIDE the stem - a white window the stem's own shape (inset from its edges, right of the
            // yellow edge), from just under the tower to the foot
            float yT = box.Bottom + 8, yB = r.Bottom - 8, sTop = box.Bottom - 40;
            float XL(float y) => topL + ex2 * (y - box.Bottom) + 16;
            float XR(float y) => topR + (botR - topR) * ((y - sTop) / (r.Bottom - sTop)) - 8;
            using var win = new SKPath();
            win.MoveTo(XL(yT), yT); win.LineTo(XR(yT), yT); win.LineTo(XR(yB), yB); win.LineTo(XL(yB), yB); win.Close();
            // #441: arms tucked in now - fitted to the stem's middle width
            // #505 (2026-10-30, Angelo: "the running animation doesn't even run"): he runs from the card's first frame (was
            // held on frame 1 for 0.7 s while the card came in; the frames are decoded and on the GPU ahead - PitCardWarm)
            ran = MichelinRun.DrawIn(c, win, new SKRect(Math.Min(XL(yT), XL(yB)), yT, Math.Max(XR(yT), XR(yB)), yB), t, a, RunPaint,
                fitW: (XR((yT + yB) / 2) - XL((yT + yB) / 2)) + 8, centreX: (XL((yT + yB) / 2) + XR((yT + yB) / 2)) / 2);
            if (ran)
                using (var we = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, Color = MBlueDeep.WithAlpha((byte)(255 * a)) })
                    c.DrawPath(win, we);
        }
        if (ran) { }
        else if (bib != null && mlogo != null)
        {
            float cy = (pylTop + pylBot) / 2f, cx = midTop + (midBot - midTop) * ((cy - box.Bottom) / (r.Bottom - box.Bottom)) + 6;
            float bsc = Math.Min((BotW - 18) / bib.Width, (pylBot - pylTop) / bib.Height);
            float mw = bib.Width * bsc, mh = bib.Height * bsc;
            c.Save(); c.ClipPath(tower, antialias: true);
            using (var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) })
                c.DrawBitmap(bib, new SKRect(cx - mw / 2, cy - mh / 2, cx + mw / 2, cy + mh / 2), mp);
            c.Restore();
        }
        else
        {
            string word = mlogo != null ? "MICHELIN" : "PIT LANE";
            float sy = (pylTop + pylBot) / 2f, sx = midTop + (midBot - midTop) * ((sy - box.Bottom) / (r.Bottom - box.Bottom)) + 6;
            c.Save();
            c.Translate(sx, sy); c.RotateDegrees(-90 + MathF.Atan2(midTop - midBot, r.Bottom - box.Bottom) * 180 / MathF.PI);
            using var wp = P(Fonts.BarlowCondensed.Black, 80, SKColors.White, a);
            wp.TextSkewX = -0.22f; wp.TextAlign = SKTextAlign.Center;
            float maxLen = (pylBot - pylTop) - 30;
            while (wp.MeasureText(word) > maxLen && wp.TextSize > 30) wp.TextSize -= 2;
            while (wp.MeasureText(word) < maxLen * 0.85f && wp.TextSize < BotW * 0.62f) wp.TextSize += 2;
            c.DrawText(word, 0, wp.TextSize * 0.36f, wp);
            c.Restore();
        }
        var screen = new SKRect(box.Left + F + 2, box.Top + F, box.Right - F, box.Bottom - F);
        using (var sp = new SKPaint { IsAntialias = true, Color = new SKColor(9, 11, 16, (byte)(250 * a)) }) c.DrawRoundRect(screen, 10, 10, sp);
        c.ClipRoundRect(new SKRoundRect(screen, 10, 10), antialias: true);
        using (var yp2 = new SKPaint { Color = MYellow.WithAlpha((byte)(255 * a)) }) c.DrawRect(new SKRect(screen.Left, screen.Top, screen.Left + 14, screen.Bottom), yp2);
        return new SKRect(screen.Left + 14, screen.Top, screen.Right, screen.Bottom);
    }

    void PitTop(SKCanvas c, SKRect sc, RaceSnapshot s, double t, float a)
    {
        var mlogo = CornerBrand.User("michelin");
        var band = new SKRect(sc.Left, sc.Top, sc.Right, sc.Top + 82);
        using (var hb = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(10 * a)) }) c.DrawRect(band, hb);
        float hx = band.Left + 22;
        if (mlogo != null) hx += CornerBrand.Draw(c, mlogo, hx, band.MidY, 190, 50, a) + 20;
        using (var ht = P(Fonts.BarlowCondensed.Black, mlogo != null ? 44 : 60, SKColors.White, a)) TextTracked.Draw(c, ht, mlogo != null ? "PIT STOPS" : "PIT LANE", hx, band.MidY + (mlogo != null ? 16 : 21), 3f);
        using (var hr = new SKPaint { Color = MYellow.WithAlpha((byte)(200 * a)) }) c.DrawRect(new SKRect(band.Left + 22, band.Bottom - 2, band.Right - 22, band.Bottom), hr);
        var p = s.Player!;
        double mine = 0; foreach (var x in RaceDiary.Stops) if (x.CarIdx == p.CarIdx && (mine == 0 || x.Seconds < mine)) mine = x.Seconds;
        float L = sc.Left + 22, R = sc.Right - 20, cw = (R - L) / 3f, ly = band.Bottom + 40, vy = Math.Min(band.Bottom + 116, sc.Bottom - 22);
        for (int i = 0; i < 3; i++)
        {
            float k = K(t, 0.8 + i * 0.12, 0.35), x = L + cw * i;
            string label = i == 0 ? "STOPS" : i == 1 ? "YOURS" : "YOUR QUICKEST";
            string val = i == 0 ? RaceDiary.Stops.Count.ToString(CultureInfo.InvariantCulture) : i == 1 ? RaceDiary.PitStops(p.CarIdx).ToString(CultureInfo.InvariantCulture)
                       : mine > 0 ? mine.ToString("0.0", CultureInfo.InvariantCulture) + "s" : "-";
            using (var lp = P(Fonts.BarlowCondensed.Bold, 22, MYellow, a * k)) TextTracked.Draw(c, lp, label, x, ly, 2.4f);
            using var vp = P(Fonts.BarlowCondensed.ExtraBold, 66, i == 2 ? MYellow : SKColors.White, a * k);
            while (vp.MeasureText(val) > cw - 10 && vp.TextSize > 40) vp.TextSize -= 2;
            DrawRolling(c, val, x, vy, vp, K(t, 0.8 + i * 0.12, 0.9), SKTextAlign.Left);
        }
    }

    void PitBottom(SKCanvas c, SKRect sc, RaceSnapshot s, double t, float a)
    {
        using (var hs = P(Fonts.BarlowCondensed.Bold, 24, MYellow, a)) TextTracked.Draw(c, hs, "QUICKEST STOPS", sc.Left + 20, sc.Top + 38, 2.6f);
        using (var hr = new SKPaint { Color = MYellow.WithAlpha((byte)(200 * a)) }) c.DrawRect(new SKRect(sc.Left + 20, sc.Top + 52, sc.Right - 20, sc.Top + 54), hr);
        var list = RaceDiary.Stops.OrderBy(x => x.Seconds).ToList();
        double worst = list.Count > 0 ? list.Max(x => x.Seconds) : 1;
        const float RowH = 86;
        float rowY = sc.Top + 70;
        int n = Math.Min(list.Count, (int)((sc.Bottom - 8 - rowY) / RowH));
        for (int i = 0; i < n; i++)
        {
            var st = list[i];
            float rk = K(t, 0.7 + i * 0.12, 0.35), ry = rowY + i * RowH, ra = a * rk;
            c.Save(); c.Translate(0, (1 - rk) * -20);
            bool me = st.CarIdx == s.PlayerCarIdx;
            var nbox = new SKRect(sc.Left + 18, ry, sc.Left + 84, ry + 70);
            using (var nb = new SKPaint { IsAntialias = true, Color = (me ? Blue : MYellow).WithAlpha((byte)(255 * ra)) }) c.DrawRoundRect(nbox, 6, 6, nb);
            using (var nt = P(Fonts.BarlowCondensed.Black, 60, me ? SKColors.White : MBlueDeep, ra)) { nt.TextAlign = SKTextAlign.Center; c.DrawText((i + 1).ToString(CultureInfo.InvariantCulture), nbox.MidX, nbox.MidY + 21, nt); }
            float x = nbox.Right + 16, tR = sc.Right - 40;
            float timeW;
            using (var tm = P(Fonts.BarlowCondensed.ExtraBold, 66, i == 0 ? MYellow : SKColors.White, ra))
            {
                string ts = st.Seconds.ToString("0.0", CultureInfo.InvariantCulture);
                tm.TextAlign = SKTextAlign.Right; c.DrawText(ts, tR, ry + 54, tm); timeW = tm.MeasureText(ts);
            }
            using (var sx = P(Fonts.Inter.Bold, 20, new SKColor(170, 178, 192), ra)) c.DrawText("s", tR + 6, ry + 54, sx);
            float nameW = tR - timeW - 14 - x;
            using (var nm = P(Fonts.BarlowCondensed.Bold, 36, SKColors.White, ra)) c.DrawText(Fit(st.Name.ToUpperInvariant(), nm, nameW), x, ry + 30, nm);
            using (var lp = P(Fonts.Inter.Bold, 13, new SKColor(170, 178, 192), ra))
                TextTracked.Draw(c, lp, Fit($"{ClassLogo.DisplayName(st.Class)}  ·  LAP {st.Lap}", lp, nameW), x, ry + 52, 1.6f);
            float lx0 = x, lx1 = Math.Max(x + 40, tR - timeW - 14), ly = ry + 66;
            using (var lane = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(40 * ra)), StrokeWidth = 2 }) c.DrawLine(lx0, ly, lx1, ly, lane);
            float frac = (float)(st.Seconds / worst) * K(t, 0.9 + i * 0.12, 0.6);
            using (var fill = new SKPaint { Color = (i == 0 ? MYellow : new SKColor(120, 160, 230)).WithAlpha((byte)(255 * ra)), StrokeWidth = 6, StrokeCap = SKStrokeCap.Round })
                c.DrawLine(lx0, ly, lx0 + (lx1 - lx0) * frac, ly, fill);
            c.Restore();
        }
    }

    // ================================================================== 6. HALFWAY
    void HalfwayTop(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        float mid = (r.Left + r.Right) / 2f, y = r.Top + 70;
        using (var ep = P(Fonts.BarlowCondensed.Black, 44, Txt, a * K(t, 0.3, 0.4))) { ep.TextAlign = SKTextAlign.Center; c.DrawText("HALFWAY", mid, y + 4, ep); }
        using (var sp = P(Fonts.Inter.SemiBold, 16, Txt2, a * K(t, 0.4, 0.4))) { sp.TextAlign = SKTextAlign.Center; c.DrawText(ToGo(s).ToUpperInvariant(), mid, y + 36, sp); }
        var p = s.Player!;
        var acc = ParseHex(cls?.Color, Gold);
        float L = r.Left, R = r.Right, sy = y + 64, sh = Math.Min(118, r.Bottom - 16 - sy), sw = (R - L - 24) / 3f;
        if (sh < 80) return;
        string you = $"P{p.PositionInClass}";
        string sub = p.PositionInClass <= 1 ? "LEADING THE CLASS" : p.GapToLeaderSeconds is double gl && gl > 0 ? $"+{gl:0.0}s TO THE LEAD" : "";
        Stat(c, new SKRect(L, sy, L + sw, sy + sh), "LEAD CHANGES", RaceDiary.AllLeadChanges.ToString(CultureInfo.InvariantCulture), Txt, acc, t, 1.0, a, 50);
        Stat(c, new SKRect(L + sw + 12, sy, L + sw * 2 + 12, sy + sh), "PIT STOPS", RaceDiary.Stops.Count.ToString(CultureInfo.InvariantCulture), Txt, acc, t, 1.1, a, 50);
        Stat(c, new SKRect(L + sw * 2 + 24, sy, R, sy + sh), "YOU", you, p.PositionInClass == 1 ? Gold : Txt, acc, t, 1.2, a, 50, sub.Length > 0 ? sub : null);
    }

    void HalfwayBottom(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        float L = r.Left, R = r.Right, mid = (L + R) / 2f;
        float top = r.Top + 84, bottom = r.Bottom - 24;
        using (var hl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(230 * a)), StrokeWidth = 3 }) c.DrawLine(mid, top - 30, mid, bottom + 10, hl);
        var pill = new SKRect(mid - 46, top - 62, mid + 46, top - 30);
        using (var pb = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) }) c.DrawRoundRect(pill, 16, 16, pb);
        using (var pt = P(Fonts.Inter.Bold, 16, new SKColor(10, 12, 18), a)) { pt.TextAlign = SKTextAlign.Center; c.DrawText("50%", mid, pill.MidY + 6, pt); }
        using (var chp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 0.5, 0.4)))
        {
            chp.TextAlign = SKTextAlign.Right; c.DrawText("AT THE START", mid - 60, top - 40, chp);
            chp.TextAlign = SKTextAlign.Left; c.DrawText("NOW", mid + 60, top - 40, chp);
        }
        if (cls == null || cls.Drivers.Count == 0) return;
        // rows never shrink below 44 units (names stay at 18+): a short panel shows the front of the field
        int n = Math.Min(14, Math.Min(cls.Drivers.Count, (int)((bottom - top) / 44)));
        if (n < 2) return;
        float rowH = Math.Min(66, (bottom - top) / n);
        float fs = Math.Clamp(rowH * 0.42f, 13, 21);
        float Yp(int pos) => top + rowH * (pos - 0.5f);
        float ck = K(t, 0.8, 1.0);
        for (int i = 0; i < Math.Min(12, cls.Drivers.Count); i++)
        {
            var d = cls.Drivers[i];
            int g = RaceDiary.GridPos(d.CarIdx), nw = d.PositionInClass;
            if (g <= 0 || g > n || nw > n || nw <= 0) continue;
            bool me = d.CarIdx == s.PlayerCarIdx;
            int gained = g - nw;
            var col = me ? Blue : gained > 0 ? Gain : gained < 0 ? Loss : new SKColor(120, 128, 140);
            float y0 = Yp(g), y1 = Yp(nw), x0 = mid - 40, x1 = mid + 40;
            using var path = new SKPath();
            path.MoveTo(x0, y0);
            path.CubicTo(mid - 4, y0, mid + 4, y1, x1, y1);
            using var meas = new SKPathMeasure(path);
            using var part = new SKPath();
            meas.GetSegment(0, meas.Length * ck, part, true);
            using (var lp = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = me ? 4.5f : 2.5f, Color = col.WithAlpha((byte)((me ? 255 : 200) * a)), StrokeCap = SKStrokeCap.Round })
                c.DrawPath(part, lp);
            float la = a * K(t, 0.6 + g * 0.03, 0.3), ra = a * K(t, 1.4 + nw * 0.03, 0.3);
            using (var pl = P(Fonts.Inter.Bold, fs, Txt3, la)) c.DrawText("P" + g, L, y0 + fs * 0.36f, pl);
            using (var nl = P(Fonts.Inter.SemiBold, fs, me ? SKColors.White : Txt2, la)) { nl.TextAlign = SKTextAlign.Right; c.DrawText(Fit(NameFormat.Broadcast(d), nl, x0 - 10 - (L + fs * 2.2f)), x0 - 10, y0 + fs * 0.36f, nl); }
            using (var nr = P(Fonts.Inter.SemiBold, fs, me ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(d), nr, R - fs * 2.2f - (x1 + 10)), x1 + 10, y1 + fs * 0.36f, nr);
            using (var pr = P(Fonts.Inter.Bold, fs, nw == 1 ? Gold : Txt, ra)) { pr.TextAlign = SKTextAlign.Right; c.DrawText("P" + nw, R, y1 + fs * 0.36f, pr); }
        }
    }

    // ================================================================== 7. WEATHER WATCH
    static SKPoint ScopeCentre(SKRect bottom) => new(bottom.MidX + 40, bottom.Top + Math.Max(90, (bottom.Height - 280) * 0.5f + 20));

    (string Title, string Big, string Label, SKColor Col) WeatherStory(RaceSnapshot s) => WeatherStory(s, StoryDirector.WeatherHeadline);

    (string Title, string Big, string Label, SKColor Col) WeatherStory(RaceSnapshot s, string head)
    {
        bool raining = s.IsRaining || s.Extras.PrecipNow > 0.01f;
        int wetLvl = s.Extras.TrackWetness;
        string title = head switch
        {
            "RAIN" => "Rain is falling",
            "WET" => "Water on the track",
            "DRYING" => wetLvl >= 3 ? "The track is drying" : "The rain has stopped",
            "HEATING" => "The track is heating up",
            "COOLING" => "The track is cooling down",
            _ => raining ? "Rain is falling" : "Conditions update",
        };
        bool f = UnitPrefs.Fahrenheit(s);
        float dT = float.IsNaN(RaceDiary.StartTrackC) ? 0 : s.TrackTempC - RaceDiary.StartTrackC;
        float dTu = f ? dT * 9 / 5 : dT;
        string unit = f ? "°F" : "°C";
        switch (head)
        {
            case "HEATING" or "COOLING":
                return (title, (dTu >= 0 ? "+" : "-") + Math.Abs(dTu).ToString("0", CultureInfo.InvariantCulture) + unit,
                        $"TRACK TEMP SINCE THE START  ·  NOW {Temp(s.TrackTempC, f)}", dTu >= 0 ? new SKColor(0xFF, 0x8A, 0x3D) : Storm);
            case "WET" or "DRYING" when wetLvl > 0:
                return (title, SessionExtras.WetnessName(wetLvl), head == "WET" ? "TRACK SURFACE  ·  GRIP IS DOWN" : "TRACK SURFACE  ·  A DRY LINE IS COMING", head == "WET" ? Storm : SKColors.White);
            case "RAIN":
                return (title, "RAIN", wetLvl > 0 ? $"FALLING NOW  ·  TRACK {SessionExtras.WetnessName(wetLvl)}" : "FALLING NOW", Storm);
            default:
                return (title, raining ? "WET" : $"{s.Extras.RainChancePct:0}%", raining ? "RAIN FALLING NOW" : "CHANCE OF RAIN", SKColors.White);
        }
    }

    readonly string[] wrapLines = new string[2];

    void WeatherTop(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? pcls, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 52;
        using (var ep = P(Fonts.BarlowCondensed.Bold, 22, Storm, a * K(t, 0.5, 0.4))) TextTracked.Draw(c, ep, "WEATHER WATCH", L, y, 3.4f);
        var partner = Presenters.For(StoryKind.WeatherWatch);
        float pw = PresentedWidth(partner, "PRESENTED BY", 34);
        float ew; using (var ep2 = P(Fonts.BarlowCondensed.Bold, 22, Storm, 1)) ew = TrackedWidth(ep2, "WEATHER WATCH", 3.4f);
        if (pw > 0)
        {
            if (L + ew + 20 + pw <= R) Presenters.DrawPresentedBy(c, partner, "PRESENTED BY", R, y - 6, 34, a * K(t, 0.55, 0.4));
            else { Presenters.DrawPresentedAfter(c, partner, "PRESENTED BY", L, y + 30, 34, a * K(t, 0.55, 0.4)); y += 36; }
        }
        var (title, big, label, col) = WeatherStory(s);
        float titleBase;
        using (var tp = P(Fonts.BarlowCondensed.Black, 72, SKColors.White, a * K(t, 0.6, 0.4)))
        {
            int n = Wrap(title.ToUpperInvariant(), tp, R - L, wrapLines, 2);
            for (int i = 0; i < n; i++) c.DrawText(wrapLines[i], L, y + 78 + i * 66, tp);
            titleBase = y + 78 + (n - 1) * 66;
        }
        // the big number takes what's left (never above the classic 156)
        float labelBase = r.Bottom - 22, bigBase = labelBase - 34;
        float size = Math.Min(156, (bigBase - titleBase - 26) / 0.8f);
        float nk = K(t, 0.8, 0.4);
        if (size >= 56)
        {
            using var bp2 = P(Fonts.BarlowCondensed.Black, size, col, a * nk);
            while (bp2.MeasureText(big) > R - L - 10 && bp2.TextSize > 56) bp2.TextSize -= 4;
            DrawRolling(c, big, L, bigBase, bp2, K(t, 0.8, 1.4), SKTextAlign.Left);
            using var bl = P(Fonts.Inter.Bold, 15, Storm, a * nk);
            TextTracked.Draw(c, bl, Fit(label, bl, R - L - 60), L + 4, labelBase, 2.4f);
        }
        else
            using (var bl = P(Fonts.BarlowCondensed.ExtraBold, 42, col, a * nk)) c.DrawText(Fit(big + "  " + label, bl, R - L), L, labelBase, bl);
    }

    void WeatherBottom(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? pcls, double t, float a)
    {
        float L = r.Left, R = r.Right;
        var ctr = ScopeCentre(new SKRect(L - 36, r.Top, R + 30, r.Bottom));
        bool raining = s.IsRaining || s.Extras.PrecipNow > 0.01f;
        float wet = raining ? 1f : Math.Clamp(s.Extras.RainChancePct / 100f, 0.1f, 1f);
        float sweepDeg = (float)((t * 110) % 360);
        float open = K(t, 0.15, 0.45);
        float fit = Math.Max(60, Math.Min((R - L) * 0.36f, (r.Height - 280) * 0.42f + 40));
        var trackPts = TrackOnScope(s, new SKPoint(ctr.X - 20, ctr.Y), fit);
        if (trackPts != null)
        {
            float tk = K(t, 0.5, 1.2);
            using var tpath = new SKPath();
            tpath.MoveTo(trackPts[0]); foreach (var tp0 in trackPts) tpath.LineTo(tp0); tpath.Close();
            using (var tg = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 10, Color = Storm.WithAlpha((byte)(60 * a * tk)), ImageFilter = SKImageFilter.CreateBlur(6, 6), StrokeJoin = SKStrokeJoin.Round })
                c.DrawPath(tpath, tg);
            using (var tl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0xBF, 0xE8, 0xFF, (byte)(220 * a * tk)), StrokeJoin = SKStrokeJoin.Round })
                c.DrawPath(tpath, tl);
        }
        var rnd = new Random(447);
        const int Cells = 22;
        for (int i = 0; i < Cells; i++)
        {
            float ang = (float)(rnd.NextDouble() * 360), dist = 80 + (float)rnd.NextDouble() * 520, size = 18 + (float)rnd.NextDouble() * 56, heavy = (float)rnd.NextDouble();
            int onTrack = rnd.Next(1 << 20);
            if (i >= Cells * wet) continue;
            float rad = ang * (float)Math.PI / 180f;
            var pc = new SKPoint(ctr.X + dist * (float)Math.Cos(rad), ctr.Y + dist * (float)Math.Sin(rad));
            if (trackPts != null && i % 3 != 2) { var q = trackPts[onTrack % trackPts.Count]; pc = new SKPoint(q.X + (float)Math.Cos(rad) * 30, q.Y + (float)Math.Sin(rad) * 30); }
            pc = new SKPoint(pc.X + (float)(t * 14), pc.Y - (float)(t * 6));
            float behind = ((sweepDeg - ang) % 360 + 360) % 360;
            float glow = (0.25f + 0.75f * (float)Math.Exp(-behind / 70.0)) * open;
            var core = heavy > 0.75f ? new SKColor(255, 255, 255) : new SKColor(0x7F, 0xD4, 0xFF);
            using var cg = SKShader.CreateRadialGradient(pc, size, new[] { core.WithAlpha((byte)(170 * glow * a)), Storm.WithAlpha((byte)(90 * glow * a)), Storm.WithAlpha(0) }, new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp);
            using var cp = new SKPaint { Shader = cg };
            c.DrawCircle(pc, size, cp);
        }
        c.Save(); c.Translate(ctr.X, ctr.Y); c.RotateDegrees(sweepDeg);
        using (var sw = new SKPaint { IsAntialias = true, Shader = SKShader.CreateSweepGradient(SKPoint.Empty, new[] { Storm.WithAlpha(0), Storm.WithAlpha(0), Storm.WithAlpha((byte)(110 * a)) }, new[] { 0f, 0.82f, 1f }) })
            c.DrawCircle(0, 0, 900, sw);
        using (var sl = new SKPaint { IsAntialias = true, Color = new SKColor(0xBF, 0xE8, 0xFF, (byte)(200 * a)), StrokeWidth = 2 }) c.DrawLine(0, 0, 900, 0, sl);
        c.Restore();
        // instrument readouts, two by two
        bool f = UnitPrefs.Fahrenheit(s);
        int wetLvl = s.Extras.TrackWetness;
        float dT = float.IsNaN(RaceDiary.StartTrackC) ? 0 : s.TrackTempC - RaceDiary.StartTrackC;
        double early = RaceDiary.ClassPaceEarly, nowPace = pcls != null ? RaceDiary.ClassPace(pcls) : 0;
        string pace = early > 0 && nowPace > 0 ? (Math.Abs(nowPace - early) < 0.05 ? "SAME" : (nowPace > early ? "+" : "-") + Math.Abs(nowPace - early).ToString("0.0", CultureInfo.InvariantCulture) + "s") : "-";
        var cellsTxt = new (string L, string V, string? S)[]
        {
            ("TRACK", Temp(s.TrackTempC, f), DeltaTemp(dT, f)),
            ("AIR", Temp(s.AirTempC, f), s.Extras.Humidity >= 0 ? $"HUMIDITY {s.Extras.Humidity * 100:0}%" : null),
            wetLvl > 1 ? ("SURFACE", SessionExtras.WetnessName(wetLvl), s.Extras.Skies >= 0 ? Sky(s.Extras.Skies).ToUpperInvariant() : null) :
            ("SKIES", Sky(s.Extras.Skies), s.Extras.WindMs > 0.05f ? (f ? $"WIND {s.Extras.WindMs * 2.237f:0} MPH" : $"WIND {s.Extras.WindMs * 3.6f:0} KM/H") : "CALM"),
            ("LAP TIMES", pace, early > 0 && nowPace > 0 ? "VS EARLY RACE" : null),
        };
        const float CellH = 120;
        float iw = (R - L - 12) / 2f, iy0 = r.Bottom - 22 - CellH * 2 - 12;
        for (int i = 0; i < 4; i++)
        {
            float ik = K(t, 1.0 + i * 0.1, 0.35);
            float bx = L + (i % 2) * (iw + 12), by = iy0 + (i / 2) * (CellH + 12);
            var b = new SKRect(bx, by, bx + iw, by + CellH);
            using (var ib = new SKPaint { IsAntialias = true, Color = new SKColor(2, 10, 18, (byte)(210 * a * ik)) }) c.DrawRoundRect(b, 10, 10, ib);
            using (var ie = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f, Color = Storm.WithAlpha((byte)(150 * a * ik)) }) c.DrawRoundRect(b, 10, 10, ie);
            using (var il = P(Fonts.Inter.Bold, 12, Storm, a * ik)) TextTracked.Draw(c, il, cellsTxt[i].L, b.Left + 16, b.Top + 26, 2.6f);
            using (var iv = P(Fonts.BarlowCondensed.ExtraBold, 42, SKColors.White, a * ik))
            {
                while (iv.MeasureText(cellsTxt[i].V) > iw - 28 && iv.TextSize > 18) iv.TextSize -= 1;
                c.DrawText(Fit(cellsTxt[i].V, iv, iw - 28), b.Left + 16, b.Top + 72, iv);
            }
            if (cellsTxt[i].S != null)
                using (var isub = P(Fonts.Inter.Bold, 11, i == 0 && dT > 0.3f ? Loss : i == 0 && dT < -0.3f ? Storm : Txt2, a * ik)) c.DrawText(Fit(cellsTxt[i].S!, isub, iw - 28), b.Left + 16, b.Top + 100, isub);
        }
    }

    // ================================================================== 8. DRIVER OF THE DAY
    void TrophyTop(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var d = StoryDirector.DriverOfDay(s);
        float cx = (r.Left + r.Right) / 2f, y = r.Top + 96;
        using (var ep = P(Fonts.CormorantGaramond.Bold, 42, Gold, a * K(t, 0.5, 0.5)))
        {
            ep.TextAlign = SKTextAlign.Center;
            c.DrawText("Driver of the Day", cx, y, ep);
            float tw = ep.MeasureText("Driver of the Day") / 2 + 20, rl = Math.Min(90, (r.Right - r.Left) / 2 - tw - 8);
            using var rule = new SKPaint { Color = Gold.WithAlpha((byte)(180 * a * K(t, 0.6, 0.5))), StrokeWidth = 1.5f };
            if (rl > 10) { c.DrawLine(cx - tw - rl, y - 14, cx - tw, y - 14, rule); c.DrawLine(cx + tw, y - 14, cx + tw + rl, y - 14, rule); }
        }
        if (d == null) return;
        float ny = Math.Min(y + 112, r.Bottom - 70);
        using (var np = P(Fonts.CormorantGaramond.Bold, 76, SKColors.White, a * K(t, 1.0, 0.6))) { np.TextAlign = SKTextAlign.Center; c.DrawText(Fit(d.Name, np, r.Right - r.Left), cx, ny, np); }
        using (var cp = P(Fonts.Inter.SemiBold, 16, Txt2, a * K(t, 1.1, 0.6))) { cp.TextAlign = SKTextAlign.Center; c.DrawText(Fit($"#{d.CarNumber}   ·   {d.CarName.ToUpperInvariant()}", cp, r.Right - r.Left), cx, ny + 40, cp); }
        var dcls = ClassOf(s, d);
        if (ny + 100 < r.Bottom - 24 && dcls != null && ClassLogoBmp(dcls.Name) is { } cl) { float lh = 26, lw = cl.Width * lh / cl.Height; DrawLogo(c, cl, cx, ny + 86, lw, lh, a * K(t, 1.7, 0.5)); }
    }

    void TrophyBottom(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        var d = StoryDirector.DriverOfDay(s);
        if (d == null) return;
        float cx = (r.Left + r.Right) / 2f;
        // the stats sit at the foot; the engraved maker badge takes the room above when there is some
        float statsTop = r.Bottom - 222;
        float bh0 = Math.Min(230, statsTop - r.Top - 40);
        if (bh0 >= 80 && Maker(d.CarBrand) is { } mk)
        {
            float bk = K(t, 0.7, 0.8);
            float bh = bh0, bw = mk.Width * bh / mk.Height; if (bw > 400) { bw = 400; bh = mk.Height * bw / mk.Width; }
            float by = r.Top + 30 + (bh0 - bh) / 2;
            var dst = new SKRect(cx - bw / 2, by, cx + bw / 2, by + bh);
            using (var halo = SKShader.CreateRadialGradient(new SKPoint(cx, dst.MidY), 260, new[] { Gold.WithAlpha((byte)(60 * a * bk)), Gold.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
            using (var hp = new SKPaint { Shader = halo }) c.DrawCircle(cx, dst.MidY, 260, hp);
            using (var orig = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a * bk)) })
                c.DrawBitmap(mk, dst, orig);
            float gx = dst.Left - 120 + (dst.Width + 240) * K(t, 1.6, 1.2);
            c.Save(); c.ClipRect(dst);
            using (var gl = SKShader.CreateLinearGradient(new SKPoint(gx - 60, 0), new SKPoint(gx + 60, 0), new[] { SKColors.White.WithAlpha(0), SKColors.White.WithAlpha((byte)(150 * a)), SKColors.White.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
            using (var layp = new SKPaint { BlendMode = SKBlendMode.Plus })
            using (var glp = new SKPaint { Shader = gl, BlendMode = SKBlendMode.SrcIn })
            { c.SaveLayer(dst, layp); c.DrawBitmap(mk, dst); c.DrawRect(dst, glp); c.Restore(); }
            c.Restore();
        }
        int g = RaceDiary.GridPos(d.CarIdx), fin = d.PositionInClass, gained = RaceDiary.Gained(d);
        float fk = K(t, 1.4, 0.6), sx = Math.Min(190, (r.Right - r.Left) / 2 - 70);
        using (var lp = P(Fonts.Inter.Bold, 15, Txt3, a * fk))
        {
            lp.TextAlign = SKTextAlign.Center;
            c.DrawText("STARTED", cx - sx, statsTop + 30, lp); c.DrawText("FINISHED", cx + sx, statsTop + 30, lp); c.DrawText("GAINED", cx, statsTop + 30, lp);
        }
        using (var vp = P(Fonts.CormorantGaramond.Bold, 96, SKColors.White, a * fk))
        {
            vp.TextAlign = SKTextAlign.Center;
            c.DrawText($"P{g}", cx - sx, statsTop + 120, vp);
            vp.Color = (fin == 1 ? Gold : SKColors.White).WithAlpha((byte)(255 * a * fk)); c.DrawText($"P{fin}", cx + sx, statsTop + 120, vp);
            vp.Color = Gold.WithAlpha((byte)(255 * a * fk)); c.DrawText($"+{gained}", cx, statsTop + 120, vp);
        }
        if (d.BestLapTime > 0)
            using (var bp = P(Fonts.Inter.Bold, 15, Txt3, a * K(t, 1.8, 0.5))) { bp.TextAlign = SKTextAlign.Center; c.DrawText($"BEST LAP  {LapTimeFormat.Lap(d.BestLapTime)}", cx, statsTop + 170, bp); }
    }

    // ================================================================== 9. CAR CONTACT (and the DQ card)
    void ContactTop(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 76;
        bool dq = StoryDirector.ContactShown?.Dq == true;
        y += ColHead(c, L, R, y, dq ? "DISQUALIFIED" : "CAR CONTACT", null, dq ? TpRed : TpBlue, StoryKind.CarContact, Presenters.Verb(StoryKind.CarContact), 34, t, a);
        var ct = StoryDirector.ContactShown;
        float py = y + 26, ph = Math.Min(226, r.Bottom - 18 - py);
        if (ct == null)
        {
            using var np = P(Fonts.BarlowCondensed.Black, 64, Txt, a * K(t, 0.3, 0.4));
            c.DrawText(Fit("A CLEAN RACE SO FAR", np, R - L), L, py + 90, np);
            return;
        }
        if (ph < 150) return;
        var d1 = Find(s, ct.CarIdx); var d2 = ct.OtherCarIdx >= 0 ? Find(s, ct.OtherCarIdx) : null;
        float hit = OutBack(Math.Clamp((float)((t - 0.15) / 0.45), 0, 1));
        double ti = t - 0.6;
        float recoil = ti > 0 ? 12f * (float)Math.Exp(-ti * 6) * (float)Math.Sin(ti * 18) : 0;
        if (d2 != null || ct.OtherName != null)
        {
            float pw = (R - L - 24) / 2;
            var lr = new SKRect(L, py, L + pw, py + ph); var rr = new SKRect(R - pw, py, R, py + ph);
            lr.Offset(-(1 - hit) * (pw + 200) - recoil, 0); rr.Offset((1 - hit) * (pw + 200) + recoil, 0);
            Plate(c, lr, d1, ct.Name, ct.Brand, ct.Points, RaceDiary.IncOf(ct.CarIdx, ct.Total), ct.CarIdx == s.PlayerCarIdx, a, true, TpBlue, TpRed, t, compact: true);
            Plate(c, rr, d2, ct.OtherName ?? "", ct.OtherBrand, ct.OtherPoints, RaceDiary.IncOf(ct.OtherCarIdx, ct.OtherTotal), ct.OtherCarIdx == s.PlayerCarIdx, a, false, TpRed, TpBlue, t, compact: true);
            PaintBurst(c, (L + R) / 2, py + ph / 2, t, a, TpBlue, TpRed);
        }
        else
        {
            var solo = LiveryArt.Get(d1);
            float pw = (R - L) * (solo != null ? 0.50f : 0.66f);
            var lr = new SKRect(L, py, L + pw, py + ph);
            lr.Offset(-(1 - hit) * (pw + 200) - recoil, 0);
            Plate(c, lr, d1, ct.Name, ct.Brand, ct.Dq ? -2 : ct.Points, RaceDiary.IncOf(ct.CarIdx, ct.Total), ct.CarIdx == s.PlayerCarIdx, a, true, ct.Dq ? TpRed : TpBlue, SKColors.White, t, drawCar: solo == null, compact: true);
            PaintBurst(c, lr.Right + 10, py + ph / 2, t, a, TpBlue, SKColors.White);
            if (solo != null)
            {
                var room = new SKRect(lr.Right + 8, py - 40, R + 20, py + ph + 6);
                float cw = room.Width, chh = cw * solo.Height / solo.Width;
                if (chh > room.Height) { chh = room.Height; cw = chh * solo.Width / solo.Height; }
                float cx = room.MidX + (1 - hit) * 300 + (ct.Dq ? DqShake(t) : 0), by = room.Bottom + (ct.Dq ? Math.Abs(DqShake(t)) * 0.6f : 0);
                using (var sh = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)(150 * a)), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f) })
                    c.DrawOval(new SKRect(cx - cw * 0.44f, by - chh * 0.10f, cx + cw * 0.48f, by + chh * 0.05f), sh);
                using var ip = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
                c.DrawImage(solo, new SKRect(cx - cw / 2, by - chh, cx + cw / 2, by), ip);
                if (ct.Dq) DqStamp(c, new SKPoint(cx, by - chh * 0.52f), Math.Min(cw, chh * 2.2f), t, a, ct);
            }
            else if (ct.Dq) DqStamp(c, new SKPoint(lr.MidX, lr.MidY), lr.Width * 0.8f, t, a, ct);
        }
    }

    void ContactBottom(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 34;
        var ct = StoryDirector.ContactShown;
        if (ct != null)
        {
            using var lp = P(Fonts.Inter.Bold, 14, Txt2, a * K(t, 0.9, 0.4));
            string what = ct.Dq ? $"DISQUALIFIED  ·  {Math.Max(0, ct.Total)}x INCIDENT POINTS" : ct.Points >= 4 ? "HEAVY CONTACT" : "CONTACT";
            string where = "";
            if (ct.Pct >= 0)
            {
                var cn = TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, ct.Pct, s.TrackBroadcastName);
                var st = cn == null ? TrackCorners.Straight(s.TrackRawName, s.Extras.TrackLength, ct.Pct) : null;
                where = cn != null ? "AT " + cn.ToUpperInvariant() : st != null ? "ON " + st.ToUpperInvariant() : "";
            }
            string line1 = $"LAP {ct.Lap}  ·  {ClassLogo.DisplayName(ct.Class)}  ·  {what}";
            string line2 = (where.Length > 0 && !ct.Dq ? where : "") + (ct.Points < 0 ? "" : (where.Length > 0 && !ct.Dq ? "  ·  " : "") + $"+{ct.Points}x" + (ct.OtherName != null ? $" / +{ct.OtherPoints}x" : ""));
            TextTracked.Draw(c, lp, Fit(line1, lp, R - L - 60), L, y, 2f);
            if (line2.Length > 0) { TextTracked.Draw(c, lp, Fit(line2, lp, R - L - 60), L, y + 24, 2f); y += 24; }
            y += 22;
        }
        float ty = y + 26;
        using (var hp = P(Fonts.BarlowCondensed.Bold, 22, TpBlue, a * K(t, 1.0, 0.4))) TextTracked.Draw(c, hp, "DAMAGE REPORT  ·  INCIDENT POINTS", L, ty, 2.6f);
        var all = new List<Driver>();
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) if (Inc(d) > 0) all.Add(d);
        all.Sort((p1, p2) => Inc(p2).CompareTo(Inc(p1)));
        float footTop = r.Bottom - 58;
        int rows = Math.Min(all.Count, Math.Max(0, (int)((footTop - 8 - ty - 14) / 46)));
        var shown = all.Take(rows).ToList();
        var me = s.Player;
        if (me != null && Inc(me) > 0 && !shown.Any(x => x.CarIdx == me.CarIdx) && shown.Count > 0) shown[^1] = me;
        int max = Math.Max(1, all.Count > 0 ? Inc(all[0]) : 1);
        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i]; bool mine = d.CarIdx == s.PlayerCarIdx;
            float rk = K(t, 1.1 + i * 0.08, 0.35), ra = a * rk, ry = ty + 14 + i * 46;
            c.Save(); c.Translate((1 - rk) * 40, 0);
            if (mine) using (var hl = new SKPaint { Color = Blue.WithAlpha((byte)(46 * ra)) }) c.DrawRect(new SKRect(L - 10, ry, R, ry + 40), hl);
            if (Maker(d.CarBrand) is { } mk) { float bh = 26, bw = Math.Min(44, mk.Width * bh / mk.Height); DrawLogo(c, mk, L + 22, ry + 20, bw, bh, ra); }
            using (var np = P(Fonts.BarlowCondensed.Bold, 27, mine ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(d).ToUpperInvariant(), np, 170), L + 54, ry + 29, np);
            float bx0 = L + 236, bx1 = R - 64;
            using (var tr = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(22 * ra)) }) c.DrawRect(new SKRect(bx0, ry + 12, bx1, ry + 28), tr);
            float frac = Inc(d) / (float)max * K(t, 1.2 + i * 0.08, 0.6);
            ScuffStrip(c, new SKRect(bx0, ry + 12, bx0 + (bx1 - bx0) * frac, ry + 28), i % 2 == 0 ? TpBlue : TpRed, Inc(d), d.CarIdx, ra);
            using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 32, mine ? SKColors.White : TpBlue, ra)) { vp.TextAlign = SKTextAlign.Right; c.DrawText($"{Inc(d)}x", R, ry + 31, vp); }
            c.Restore();
        }
        using (var fp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 1.6, 0.4)))
            TextTracked.Draw(c, fp, Fit($"CONTACTS THIS RACE  {RaceDiary.Contacts.Count}      YOUR INCIDENTS  {(me != null ? Math.Max(0, Inc(me)) : 0)}x", fp, R - L - 40), L, r.Bottom - 44, 2f);
        using (var sg = P(Fonts.Inter.Bold, 11, new SKColor(0x8A, 0x93, 0xA8), a * K(t, 1.8, 0.4)))
            TextTracked.Draw(c, sg, "THE CUSTOM CAR PAINTING PLATFORM FOR IRACING", L, r.Bottom - 22, 1.6f);
    }

    // ================================================================== 10. DRIVING ON THE LIMIT
    void LimitsTop(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 74;
        y += ColHead(c, L, R, y, "DRIVING ON THE LIMIT", null, GoGreen, StoryKind.Limits, Presenters.Verb(StoryKind.Limits), 38, t, a);
        var spots = RaceDiary.OffSpots;
        float nk = K(t, 0.7, 0.4);
        string total = RaceDiary.TotalOffs.ToString(CultureInfo.InvariantCulture);
        using (var bp = P(Fonts.BarlowCondensed.Black, 150, SKColors.White, a * nk)) DrawRolling(c, total, L - 4, y + 156, bp, K(t, 0.7, 1.2), SKTextAlign.Left);
        using (var bl = P(Fonts.BarlowCondensed.Bold, 24, GoGreen, a * nk)) TextTracked.Draw(c, bl, "TIMES OFF THE TRACK", L, y + 190, 2.6f);
        if (spots.Count == 0) return;
        const int Bins = 40;
        Span<int> bins = stackalloc int[Bins];
        foreach (var sp in spots) bins[Math.Clamp((int)(sp.Pct * Bins), 0, Bins - 1)]++;
        int hot = 0; for (int i = 1; i < Bins; i++) if (bins[i] > bins[hot]) hot = i;
        float bxL = L + 250;
        using var hp = P(Fonts.Inter.Bold, 13, Txt2, a * K(t, 2.3, 0.4));
        TextTracked.Draw(c, hp, "HOTSPOT", bxL, y + 50, 2.4f);
        using var hv = P(Fonts.BarlowCondensed.ExtraBold, 34, SKColors.White, a * K(t, 2.3, 0.4));
        // r4: the same place the zoomed map's label names (Hotspot)
        var (hn, cnt) = Hotspot(s);
        string cn = hn ?? $"{HotWindowCentre(spots) * 100f:0}% INTO THE LAP";
        float maxW = R - bxL;
        while (hv.TextSize > 24 && hv.MeasureText(cn) > maxW) hv.TextSize -= 2;
        c.DrawText(Fit(cn, hv, maxW), bxL, y + 92, hv);
        c.DrawText($"{cnt} OFF{(cnt == 1 ? "" : "S")} THERE", bxL, y + 122, hp);
    }

    void LimitsBottom(SKCanvas c, SKRect r, RaceSnapshot s, DriverClass? cls, double t, float a)
    {
        float L = r.Left, R = r.Right;
        var spots = RaceDiary.OffSpots;
        var kerb = KerbColours(s);
        // 2026-10-27 (#398, Angelo: "keep the car in it ... bigger, to the boundaries of the width"): GO FAST's 911 sweeps in
        // and parks full width above the footer; the map sits beside the list and the list loses rows to make the room
        var side911 = Bmp("logos", "sponsors", "gofast_911_side.png");
        float footH = 62, carH = side911 != null ? (R - L) * side911.Height / side911.Width : 0;
        if (carH > r.Height * 0.4f) carH = r.Height * 0.4f;
        float carBottom = r.Bottom - footH - 6;
        bool stacked = r.Height >= 480 && side911 == null;
        float bodyBottom = side911 != null ? carBottom - carH - 14 : r.Bottom - footH;
        SKRect mapBox; float listL, listTop;
        if (side911 != null)
        {
            float side = Math.Min(250, bodyBottom - r.Top - 24);
            mapBox = new SKRect(L - 6, r.Top + 18, L - 6 + side, r.Top + 18 + side); listL = mapBox.Right + 22; listTop = r.Top + 40;
        }
        else if (stacked) { float mh = Math.Min(250, r.Height - footH - 60 - 4 * 44); mapBox = new SKRect(L, r.Top + 20, R, r.Top + 20 + mh); listL = L; listTop = mapBox.Bottom + 40; }
        else { float side = Math.Min(250, r.Height - footH - 30); mapBox = new SKRect(L - 6, r.Top + 18, L - 6 + side, r.Top + 18 + side); listL = mapBox.Right + 22; listTop = r.Top + 40; }
        var pts = TrackOnScope(s, new SKPoint(mapBox.MidX, mapBox.MidY), Math.Min(mapBox.Width, mapBox.Height) * 0.46f);
        const int Bins = 40;
        Span<int> bins = stackalloc int[Bins];
        foreach (var sp in spots) bins[Math.Clamp((int)(sp.Pct * Bins), 0, Bins - 1)]++;
        int hot = 0; for (int i = 1; i < Bins; i++) if (bins[i] > bins[hot]) hot = i;
        float[]? cumL = null;
        if (pts != null)
        {
            cumL = new float[pts.Count + 1];
            for (int i = 1; i <= pts.Count; i++) cumL[i] = cumL[i - 1] + SKPoint.Distance(pts[i - 1], pts[i % pts.Count]);
            float tot = Math.Max(1e-3f, cumL[^1]); for (int i = 0; i < cumL.Length; i++) cumL[i] /= tot;
        }
        SKPoint OnTrack(float pct)
        {
            if (pts == null || cumL == null) return new SKPoint(mapBox.Left + mapBox.Width * pct, mapBox.MidY);
            pct -= MathF.Floor(pct);
            int lo = 0, hi = cumL.Length - 1;
            while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (cumL[mid] <= pct) lo = mid; else hi = mid; }
            float spn = cumL[hi] - cumL[lo], fr = spn > 0 ? (pct - cumL[lo]) / spn : 0;
            var p0 = pts[lo % pts.Count]; var p1 = pts[hi % pts.Count];
            return new SKPoint(p0.X + (p1.X - p0.X) * fr, p0.Y + (p1.Y - p0.Y) * fr);
        }
        // 2026-10-27 (#402, Angelo: "start with the full track outline but then zoom into the part of the track with the
        // most offs"): the full circuit while the offs drop in, then an ease-in-out zoom onto the densest stretch, named
        float hotC = HotWindowCentre(spots);
        var zm = MapZoom(mapBox, pts, cumL, hotC, spots.Count, t, out float zs);
        c.Save();
        float clipPad = zs > 1.001f ? 4 : 40;   // zoomed: nothing spills past the map onto the numbers / list
        c.ClipRect(new SKRect(mapBox.Left - clipPad, mapBox.Top - clipPad, mapBox.Right + clipPad, mapBox.Bottom + clipPad));
        c.Concat(ref zm);
        float tk = K(t, 0.4, 1.0);
        using (var tl = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 8 / zs, StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round, Color = new SKColor(70, 76, 88, (byte)(255 * a * tk)) })
        {
            using var path = new SKPath();
            if (pts != null) { path.MoveTo(pts[0]); foreach (var q in pts) path.LineTo(q); path.Close(); }
            else { path.MoveTo(mapBox.Left, mapBox.MidY); path.LineTo(mapBox.Right, mapBox.MidY); }
            using var meas = new SKPathMeasure(path, false);
            using var part = new SKPath(); meas.GetSegment(0, meas.Length * tk, part, true);
            c.DrawPath(part, tl);
            tl.StrokeWidth = 2.5f / zs; tl.Color = new SKColor(200, 206, 216, (byte)(255 * a * tk)); c.DrawPath(part, tl);
        }
        var sf = OnTrack(0);
        using (var sfp = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * a * tk)) }) c.DrawRect(new SKRect(sf.X - 3 / zs, sf.Y - 10 / zs, sf.X + 3 / zs, sf.Y + 10 / zs), sfp);
        var jit = new Random(91);
        for (int i = 0; i < spots.Count; i++)
        {
            float dk = K(t, 0.9 + i * (1.2 / Math.Max(1, spots.Count)), 0.25);
            if (dk <= 0) break;
            var q = OnTrack(spots[i].Pct);
            q = new SKPoint(q.X + (float)(jit.NextDouble() - 0.5) * 12, q.Y + (float)(jit.NextDouble() - 0.5) * 12);
            bool mine = spots[i].CarIdx == s.PlayerCarIdx;
            using var dp = new SKPaint { IsAntialias = true, Color = (mine ? Blue : GoGreen).WithAlpha((byte)(230 * a * dk)) };
            c.DrawCircle(q, 6f * (0.6f + 0.4f * OutBack(dk)) / zs, dp);
        }
        if (spots.Count > 0)
        {
            var hq = OnTrack((hot + 0.5f) / Bins);
            float hk = K(t, 2.2, 0.4), pulse = 1 + 0.08f * (float)Math.Sin(t * 5);
            using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f / zs, Color = SKColors.White.WithAlpha((byte)(255 * a * hk)) };
            c.DrawCircle(hq, 26 * pulse * (0.6f + 0.4f * hk) / zs, ring);
        }
        c.Restore();
        HotLabel(c, s, mapBox, pts, cumL, hotC, spots.Count, zm, t, a);
        // who runs wide the most
        using (var hp = P(Fonts.BarlowCondensed.Bold, 22, GoGreen, a * K(t, 1.2, 0.4))) TextTracked.Draw(c, hp, "MOST TIMES OFF", listL, listTop, 3f);
        var field = new List<Driver>();
        foreach (var cl in s.ClassLeaderboard) foreach (var d in cl.Drivers) field.Add(d);
        var worst = field.Where(d => RaceDiary.OffsOf(d.CarIdx) > 0).OrderByDescending(d => RaceDiary.OffsOf(d.CarIdx)).ToList();
        int rows = Math.Min(worst.Count, Math.Max(0, (int)((bodyBottom - 6 - listTop - 14) / 44)));
        if (side911 != null) Limits911(c, side911, new SKRect(L, carBottom - carH, R, carBottom), r.Left - 60, t, a);
        var shown = worst.Take(rows).ToList();
        var me = s.Player;
        if (me != null && RaceDiary.OffsOf(me.CarIdx) > 0 && !shown.Any(x => x.CarIdx == me.CarIdx) && shown.Count > 0) shown[^1] = me;
        int max = Math.Max(1, worst.Count > 0 ? RaceDiary.OffsOf(worst[0].CarIdx) : 1);
        bool blocks = R - listL >= 420;
        for (int i = 0; i < shown.Count; i++)
        {
            var d = shown[i]; bool mine = d.CarIdx == s.PlayerCarIdx; int n = RaceDiary.OffsOf(d.CarIdx);
            float rk = K(t, 1.3 + i * 0.08, 0.35), ra = a * rk, ry = listTop + 14 + i * 44;
            c.Save(); c.Translate((1 - rk) * 40, 0);
            if (mine) using (var hl = new SKPaint { Color = Blue.WithAlpha((byte)(46 * ra)) }) c.DrawRect(new SKRect(listL - 8, ry, R, ry + 38), hl);
            using (var np = P(Fonts.BarlowCondensed.Bold, 27, mine ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(d).ToUpperInvariant(), np, blocks ? 190 : R - listL - 50), listL, ry + 28, np);
            if (blocks)
            {
                float bx0 = listL + 200, bw = Math.Min(24, (R - 60 - bx0) / max);
                for (int k = 0; k < n; k++)
                    KerbBlock(c, new SKRect(bx0 + k * bw, ry + 10, bx0 + (k + 1) * bw - 1, ry + 28),
                              mine && k % 2 == 0 ? Blue : mine ? SKColors.White : kerb[k % kerb.Length], ra * K(t, 1.4 + i * 0.08 + k * 0.03, 0.2), d.CarIdx * 17 + k);
            }
            using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 32, mine ? SKColors.White : GoGreen, ra)) { vp.TextAlign = SKTextAlign.Right; c.DrawText(n.ToString(CultureInfo.InvariantCulture), R, ry + 30, vp); }
            c.Restore();
        }
        if (cls != null)
        {
            var clean = cls.Drivers.Where(d => d.LapsCompleted >= 3).OrderBy(d => RaceDiary.OffsOf(d.CarIdx)).ThenBy(d => d.PositionInClass).FirstOrDefault();
            if (clean != null)
                using (var fp = P(Fonts.Inter.Bold, 13, Txt3, a * K(t, 1.8, 0.4)))
                    TextTracked.Draw(c, fp, Fit($"CLEANEST IN {ClassLogo.DisplayName(cls.Name)}  {NameFormat.Broadcast(clean).ToUpperInvariant()} ({RaceDiary.OffsOf(clean.CarIdx)})      YOURS  {(me != null ? RaceDiary.OffsOf(me.CarIdx) : 0)}", fp, R - L - 40), L, r.Bottom - 46, 2f);
        }
        using (var sg = P(Fonts.BarlowCondensed.Bold, 17, GoGreen, a * K(t, 1.9, 0.4))) TextTracked.Draw(c, sg, "YOUR PERSONAL RACE ENGINEER", L, r.Bottom - 20, 2.4f);
    }

    /// <summary>#411: On the Limit's rumble strip down the card's LEFT edge, full height, following the edge's slant (bottom-left
    /// to the top of the slanted side; straight up with square corners): kerb on the outside, white line and asphalt inside.</summary>
    static void KerbLeft(SKCanvas c, SKRect r, SKColor[] cols, float a, float width, float slant)
    {
        float dy = -r.Height, len = MathF.Sqrt(slant * slant + dy * dy);
        c.Save();
        c.Translate(r.Left, r.Bottom);
        c.RotateDegrees(MathF.Atan2(dy, slant) * 180 / MathF.PI);
        DrawKerb(c, new SKRect(0, 0, len, width), cols, a);
        c.Restore();
    }

    // ---- #402: On the Limit's map zooms from the full circuit onto the stretch with the most offs
    const double ZoomAt = 2.0, ZoomLen = 1.2;
    const float HotHalf = 0.045f;          // the window either side of the hotspot centre, in laps

    /// <summary>Centre (lap fraction) of the densest 3-of-40-bins window of trips off the track (wraps round the line).</summary>
    static float HotWindowCentre(IReadOnlyList<(int CarIdx, float Pct)> spots)
    {
        const int Bins = 40;
        Span<int> bins = stackalloc int[Bins];
        foreach (var sp in spots) bins[Math.Clamp((int)(sp.Pct * Bins), 0, Bins - 1)]++;
        int best = 0, bestN = -1;
        for (int i = 0; i < Bins; i++)
        {
            int n = bins[(i + Bins - 1) % Bins] + bins[i] * 2 + bins[(i + 1) % Bins];
            if (n > bestN) { bestN = n; best = i; }
        }
        return (best + 0.5f) / Bins;
    }

    static bool InWindow(float pct, float centre)
    {
        float d = Math.Abs(pct - centre); d = Math.Min(d, 1 - d);
        return d <= HotHalf;
    }

    /// <summary>A point on the recorded line at a lap fraction, by distance along it (the cards' OnTrack, without a closure).</summary>
    static SKPoint OnTrackAt(List<SKPoint>? pts, float[]? cumL, SKRect box, float pct)
    {
        if (pts == null || cumL == null) return new SKPoint(box.Left + box.Width * (pct - MathF.Floor(pct)), box.MidY);
        pct -= MathF.Floor(pct);
        int lo = 0, hi = cumL.Length - 1;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (cumL[mid] <= pct) lo = mid; else hi = mid; }
        float spn = cumL[hi] - cumL[lo], fr = spn > 0 ? (pct - cumL[lo]) / spn : 0;
        var p0 = pts[lo % pts.Count]; var p1 = pts[hi % pts.Count];
        return new SKPoint(p0.X + (p1.X - p0.X) * fr, p0.Y + (p1.Y - p0.Y) * fr);
    }

    /// <summary>The map's zoom at time t: identity while the full outline shows (to 2 s), then an ease-in-out over 1.2 s
    /// onto the hotspot stretch (with a margin, kept inside the map box), held. <paramref name="scale"/> = the zoom factor
    /// (stroke widths and dot sizes are divided by it so they stay the same on screen).</summary>
    static SKMatrix MapZoom(SKRect box, List<SKPoint>? pts, float[]? cumL, float centre, int spots, double t, out float scale)
    {
        scale = 1;
        if (pts == null || spots < 3 || t <= ZoomAt) return SKMatrix.Identity;
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int k = 0; k <= 36; k++)
        {
            var p = OnTrackAt(pts, cumL, box, centre - HotHalf + k * (HotHalf * 2 / 36));
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
        }
        float minSide = Math.Max(box.Width, box.Height) * 0.26f;
        float bw = Math.Max(minSide, (maxX - minX) * 1.5f), bh = Math.Max(minSide, (maxY - minY) * 1.5f);
        float target = Math.Clamp(Math.Min(box.Width / bw, box.Height / bh), 1f, 4.5f);
        float fx = (minX + maxX) / 2, fy = (minY + maxY) / 2;
        // keep the framed window inside the map: the focus never closer to an edge than half the zoomed view
        float hx = box.Width / 2 / target, hy = box.Height / 2 / target;
        fx = Math.Clamp(fx, box.Left + hx, box.Right - hx); fy = Math.Clamp(fy, box.Top + hy, box.Bottom - hy);
        float u = (float)Math.Clamp((t - ZoomAt) / ZoomLen, 0, 1);
        float z = u * u * (3 - 2 * u);                                       // ease in-out
        scale = 1 + (target - 1) * z;
        float cx = box.MidX + (fx - box.MidX) * z, cy = box.MidY + (fy - box.MidY) * z;
        // screen = box centre + scale * (world - focus), the focus sliding from the box centre to the hotspot
        // (z = 0: scale 1 and the focus at the box centre, exactly the identity)
        float ox = box.MidX - scale * cx, oy = box.MidY - scale * cy;
        return new SKMatrix(scale, 0, ox, 0, scale, oy, 0, 0, 1);
    }

    /// <summary>The hotspot's corner or straight name (TrackCorners) next to it once the map has zoomed in.</summary>
    static void HotLabel(SKCanvas c, RaceSnapshot s, SKRect box, List<SKPoint>? pts, float[]? cumL, float centre, int spots, SKMatrix zm, double t, float a)
    {
        if (pts == null || spots < 3) return;
        float k = K(t, ZoomAt + ZoomLen, 0.4);
        if (k <= 0) return;
        // the corner most of the offs in the window were at; else what the window's centre is called (corner or straight);
        // else the busiest corner - the same name as the HOTSPOT line (Hotspot)
        string? name = Hotspot(s).Name;
        if (string.IsNullOrEmpty(name)) return;
        var p = zm.MapPoint(OnTrackAt(pts, cumL, box, centre));
        // #424: a themed place gets its own lettering (CornerTheme's label, rendered once and cached)
        if (HotTheme(s) != null && CornerTheme.LabelImage("MOST OFFS AT", name) is { } lab)
        {
            float ls = Math.Min(Math.Min(box.Width * 0.95f, 380) / lab.Width, 70f / lab.Height);
            float lw = lab.Width * ls, lh = lab.Height * ls;
            float lx = Math.Clamp(p.X - lw / 2, box.Left + 2, box.Right - lw - 2), ly = p.Y < box.MidY ? p.Y + 40 : p.Y - 40 - lh;
            ly = Math.Clamp(ly, box.Top + 2, box.Bottom - lh - 2);
            hotLabelPaint ??= new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 24 };
            hotLabelPaint.Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(a * k, 0, 1)));
            hotLabelPaint.FilterQuality = SKFilterQuality.High;
            c.DrawImage(lab, new SKRect(lx, ly, lx + lw, ly + lh), hotLabelPaint);
            return;
        }
        // paints made once and re-coloured (no per-frame allocation)
        hotLabelPaint ??= new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Bold, TextSize = 24 };
        hotFillPaint ??= new SKPaint { IsAntialias = true };
        hotLabelPaint.Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(a * k, 0, 1)));
        float w = hotLabelPaint.MeasureText(name);
        float x = Math.Clamp(p.X - w / 2, box.Left + 4, box.Right - w - 4), y = p.Y < box.MidY ? p.Y + 64 : p.Y - 50;
        y = Math.Clamp(y, box.Top + 26, box.Bottom - 8);
        hotFillPaint.Color = new SKColor(6, 16, 12, (byte)(210 * Math.Clamp(a * k, 0, 1)));
        c.DrawRoundRect(new SKRect(x - 10, y - 24, x + w + 10, y + 8), 6, 6, hotFillPaint);
        hotFillPaint.Color = GoGreen.WithAlpha((byte)(255 * Math.Clamp(a * k, 0, 1)));
        c.DrawRect(new SKRect(x - 10, y + 6, x + w + 10, y + 8), hotFillPaint);
        c.DrawText(name, x, y, hotLabelPaint);
    }
    static SKPaint? hotLabelPaint, hotFillPaint;

    // ---- #424 (Angelo: "when the GO FAST one zooms in to say a specific corner, I want that corner theme to change the
    // theme of the card as it zooms in, and the entire card's background"): a themed hotspot (PlayStation space, Michelin
    // blue, Dunlop graphite + yellow, the bricks, speed, the red earth, a maker's colours - the battle map's backdrops)
    // cross-fades over the whole card with the zoom; a dark scrim keeps the text readable; plain places stay GO FAST green
    static CornerTheme.Theme? hotTheme; static string? hotThemeFor; static bool hotThemeSet;
    static SKPaint? scrimPaint;

    static CornerTheme.Theme? HotTheme(RaceSnapshot s)
    {
        var n = Hotspot(s).Name;
        if (!hotThemeSet || !string.Equals(n, hotThemeFor, StringComparison.Ordinal)) { hotThemeSet = true; hotThemeFor = n; hotTheme = CornerTheme.For(n); }
        return hotTheme;
    }

    /// <summary>How far the card has turned into the hotspot's theme (follows the map zoom: 0 before 2 s, 1 at 3.2 s).</summary>
    static float ThemeK(double t)
    {
        float u = (float)Math.Clamp((t - ZoomAt) / ZoomLen, 0, 1);
        return u * u * (3 - 2 * u);
    }

    static void LimitsTheme(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        if (RaceDiary.OffSpots.Count < 3) return;
        float z = ThemeK(t);
        if (z <= 0.001f || HotTheme(s) is not { } th) return;
        BattleMap.Backdrop(c, r, th, hotThemeFor!, a * z, t * 1000);
        scrimPaint ??= new SKPaint();
        scrimPaint.Color = new SKColor(4, 6, 10, (byte)(255 * Math.Clamp(0.42f * z * a, 0, 1)));
        c.DrawRect(r, scrimPaint);
    }

    static float hotNameCentre = float.NaN; static int hotNameSpots = -1; static string? hotName, hotNameTrack; static int hotCount;

    /// <summary>2026-10-27 (r4): the ONE place both the zoomed map's label and the HOTSPOT line name, and how many offs
    /// were there: the densest stretch of offs (<see cref="HotWindowCentre"/>, the window the map zooms onto) named by
    /// <see cref="HotName"/> with the offs in that window; only when the stretch has no corner / straight name, the
    /// busiest single named corner and its offs; null = no names at all (the caller says how far into the lap).
    /// Worked out once per hotspot / count of offs (render thread only), not every frame.</summary>
    static (string? Name, int Count) Hotspot(RaceSnapshot s)
    {
        var spots = RaceDiary.OffSpots;
        float centre = HotWindowCentre(spots);
        if (centre != hotNameCentre || spots.Count != hotNameSpots || !string.Equals(s.TrackRawName, hotNameTrack, StringComparison.Ordinal))
        {
            hotNameCentre = centre; hotNameSpots = spots.Count; hotNameTrack = s.TrackRawName;
            string? name = HotName(s, centre);
            int n = 0;
            if (name != null) { for (int i = 0; i < spots.Count; i++) if (InWindow(spots[i].Pct, centre)) n++; }
            else
            {
                var busiest = spots.Select(sp => TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, sp.Pct, s.TrackBroadcastName))
                                   .Where(x => x != null).GroupBy(x => x!).OrderByDescending(g => g.Count()).FirstOrDefault();
                if (busiest != null) { name = busiest.Key; n = busiest.Count(); }
                else for (int i = 0; i < spots.Count; i++) if (InWindow(spots[i].Pct, centre)) n++;
            }
            hotName = name?.ToUpperInvariant(); hotCount = n;
        }
        return (hotName, hotCount);
    }
    static string? HotName(RaceSnapshot s, float centre)
    {
        string? name = null; int bestN = 0;
        var spotList = RaceDiary.OffSpots;
        for (int i = 0; i < spotList.Count; i++)
        {
            if (!InWindow(spotList[i].Pct, centre)) continue;
            var ni = TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, spotList[i].Pct, s.TrackBroadcastName);
            if (ni == null || ni == name) continue;
            int n = 0;
            for (int j = 0; j < spotList.Count; j++)
                if (InWindow(spotList[j].Pct, centre) && TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, spotList[j].Pct, s.TrackBroadcastName) == ni) n++;
            if (n > bestN) { bestN = n; name = ni; }
        }
        name ??= TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, centre, s.TrackBroadcastName);
        if (name == null && TrackCorners.Straight(s.TrackRawName, s.Extras.TrackLength, centre) is { } st) name = st;
        return name;
    }

    /// <summary>GO FAST's 911 on its side sweeping in from the left and parking in <paramref name="dst"/>, green underglow
    /// on, speed streaks while it moves (the classic card's car, sized to the column's full inner width).</summary>
    static void Limits911(SKCanvas c, SKBitmap side, SKRect dst, float fromX, double t, float a)
    {
        float travel = Ease(Math.Clamp((float)((t - 0.25) / 1.1), 0, 1));
        float dx = (fromX - dst.Width - 40 - dst.Left) * (1 - travel);
        var d = new SKRect(dst.Left + dx, dst.Top, dst.Right + dx, dst.Bottom);
        float sw = d.Width, sh = d.Height;
        using (var glow = SKShader.CreateRadialGradient(new SKPoint(d.MidX, d.Bottom - 4), sw * 0.55f, new[] { GoGreen.WithAlpha((byte)(110 * a)), GoGreen.WithAlpha(0) }, null, SKShaderTileMode.Clamp))
        using (var gp = new SKPaint { Shader = glow })
        { c.Save(); c.Scale(1, 0.18f, d.MidX, d.Bottom - 4); c.DrawCircle(d.MidX, d.Bottom - 4, sw * 0.55f, gp); c.Restore(); }
        if (travel < 0.98f)
            using (var st = new SKPaint { IsAntialias = true, StrokeWidth = 2, Color = GoGreen.WithAlpha((byte)(120 * a * (1 - travel))) })
                for (int k = 0; k < 5; k++) c.DrawLine(d.Left - 30 - k * 40, d.Top + sh * (0.35f + k * 0.1f), d.Left - 140 - k * 60, d.Top + sh * (0.35f + k * 0.1f), st);
        using var cp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * a)) };
        c.DrawBitmap(side, d, cp);
    }

    // ================================================================== 11. CLEANEST
    void CleanTop(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float L = r.Left, R = r.Right, y = r.Top + 74;
        y += ColHead(c, L, R, y, OutroDirector.Running ? "CLEANEST DRIVER" : "CLEANEST SO FAR", null, TpBlue, StoryKind.Cleanest, Presenters.Verb(StoryKind.Cleanest), 34, t, a);
        var best = StoryDirector.Cleanest(s.ClassLeaderboard.SelectMany(x => x.Drivers));
        if (best == null) return;
        int inc = Inc(best);
        float hk = OutBack(Math.Clamp((float)((t - 0.15) / 0.5), 0, 1));
        float pw = (R - L) * 0.6f;
        var plate = new SKRect(L, y + 24, L + pw, y + 24 + 156);
        plate.Offset(-(1 - hk) * (plate.Width + 200), 0);
        Plate(c, plate, best, NameFormat.Broadcast(best), best.CarBrand, inc, inc, best.CarIdx == s.PlayerCarIdx, a, true, TpBlue, SKColors.White, -1, compact: true);
        float nk = K(t, 0.7, 0.4);
        using (var np = P(Fonts.BarlowCondensed.Black, 170, TpBlue, a * nk))
        {
            string v = inc + "x";
            while (np.MeasureText(v) > R - L - pw - 24 && np.TextSize > 90) np.TextSize -= 4;
            DrawRolling(c, v, R, y + 160, np, K(t, 0.7, 1.0), SKTextAlign.Right);
        }
        float ly = y + 214;
        if (ly > r.Bottom - 8) return;
        using var lp = P(Fonts.BarlowCondensed.Bold, 24, Txt2, a * nk);
        TextTracked.Draw(c, lp, OutroDirector.Running ? "INCIDENT POINTS ALL RACE" : "INCIDENT POINTS SO FAR", L + 2, ly, 3f);
        var bcls = ClassOf(s, best);
        if (ly + 30 <= r.Bottom - 8)
            TextTracked.Draw(c, lp, Fit($"P{best.PositionInClass}  ·  {(bcls != null ? ClassLogo.DisplayName(bcls.Name) : "")}  ·  {best.LapsCompleted} LAPS", lp, R - L - 40), L + 2, ly + 30, 3f);
    }

    void CleanBottom(SKCanvas c, SKRect r, RaceSnapshot s, double t, float a)
    {
        float L = r.Left, R = r.Right, ty = r.Top + 44;
        using (var hp = P(Fonts.BarlowCondensed.Bold, 22, TpBlue, a * K(t, 1.1, 0.4))) TextTracked.Draw(c, hp, "CLEANEST IN EACH CLASS", L, ty, 3f);
        int i = 0;
        foreach (var cl in s.ClassLeaderboard)
        {
            var cb = StoryDirector.Cleanest(cl.Drivers);
            if (cb == null) continue;
            float rk = K(t, 1.2 + i * 0.1, 0.35), ra = a * rk, ry = ty + 18 + i * 52;
            if (ry + 44 > r.Bottom - 40) break;
            c.Save(); c.Translate((1 - rk) * 40, 0);
            float nx = L;
            if (ClassLogoBmp(cl.Name) is { } lg) { DrawLogo(c, lg, L + 36, ry + 20, 72, 24, ra); nx = L + 84; }
            else using (var cn = P(Fonts.BarlowCondensed.Bold, 20, Txt2, ra)) { string nm = ClassLogo.DisplayName(cl.Name); c.DrawText(nm, L, ry + 28, cn); nx = L + 84; }
            using (var nm = P(Fonts.BarlowCondensed.Bold, 28, cb.CarIdx == s.PlayerCarIdx ? SKColors.White : Txt, ra)) c.DrawText(Fit(NameFormat.Broadcast(cb).ToUpperInvariant(), nm, 180), nx, ry + 30, nm);
            int ci = Inc(cb);
            ScuffStrip(c, new SKRect(nx + 190, ry + 12, R - 70, ry + 28), i % 2 == 0 ? TpBlue : TpRed, ci, cb.CarIdx, ra);
            using (var vp = P(Fonts.BarlowCondensed.ExtraBold, 32, TpBlue, ra)) { vp.TextAlign = SKTextAlign.Right; c.DrawText(ci + "x", R, ry + 31, vp); }
            c.Restore();
            i++;
        }
        using (var sg = P(Fonts.Inter.Bold, 11, new SKColor(0x8A, 0x93, 0xA8), a * K(t, 1.8, 0.4)))
            TextTracked.Draw(c, sg, "THE CUSTOM CAR PAINTING PLATFORM FOR IRACING", L, r.Bottom - 22, 1.6f);
    }
}
