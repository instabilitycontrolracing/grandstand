using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-27 (round 2): where the LIVE graphics are on screen this frame, in the compositor's 1920x1080 design space,
/// so the visual-pack cards place themselves around them instead of at fixed spots (Angelo: "it must never overlap the
/// session bar", "the race control card is too low"). The compositor writes these once per tick (plain fields, no
/// allocation); the harness writes them for the previews. Positions mirror OverlayCompositor: session bar plate top 18,
/// height 92, the watch (r 64) hanging below it; the standings ticker at 110 (44 high) in battle mode; the team-radio card
/// anchored bottom-right at (w-24, h*0.665) drawn at 90 %; the battle map at (w-24-300, 172).
/// </summary>
public static class VpLayout
{
    /// <summary>The session bar plate as drawn now (its width animates between 976 and full width).</summary>
    public static SKRect SessionBar = new(472, 18, 1448, 110);
    public static bool SessionBarVisible = true;
    /// <summary>Battle-mode look (full-width bar + ticker).</summary>
    public static bool BattleStyle;
    /// <summary>The standings ticker strip (when it shows).</summary>
    public static SKRect Ticker = new(0, 110, 1920, 154);
    public static bool TickerVisible;
    /// <summary>The team radio card (screen rect after its 90 % scale) and whether it is up.</summary>
    public static SKRect Radio;
    public static bool RadioVisible;
    /// <summary>Where the radio card's bottom-right corner is anchored even when it is not showing.</summary>
    public static float RadioRight = 1896, RadioBottom = 718;
    /// <summary>The battle map panel (top right) when it shows.</summary>
    public static SKRect BattleMap;
    public static bool BattleMapVisible;
    /// <summary>The timing tower lane (left).</summary>
    public static SKRect Tower = new(16, 18, 448, 1064);
    public static bool TowerVisible = true;

    // ---- round 3 (2026-10-27, Angelo: "it can't block the timing tower", "can't ever block any other element", the lens
    // flare "appears in the way during a lot of different things"): the rest of the live graphics, so the pack's cards
    // and the lens effects can find FREE space instead of a fixed spot.
    /// <summary>The battle card (bottom centre, 1000x280 plus its UNDER PRESSURE tab) when a battle is on.</summary>
    public static SKRect BattleCard = new(460, 722, 1460, 1042);
    public static bool BattleCardVisible;
    /// <summary>A story card (right-half takeover).</summary>
    public static SKRect Story = new(940, 0, 1920, 1080);
    public static bool StoryVisible;
    /// <summary>The featured driver lower third (bottom centre).</summary>
    public static SKRect Featured = new(576, 853, 1344, 1064);
    public static bool FeaturedVisible;
    /// <summary>A control-app custom card (right side, under the session bar).</summary>
    public static SKRect Custom = new(1336, 150, 1896, 520);
    public static bool CustomVisible;
    /// <summary>#619: the race-control bar (under the session bar / ticker / onboard place name) when it is up.</summary>
    public static SKRect RcBar;
    public static bool RcBarVisible;
    /// <summary>#608: the "latest message" plate (top right, beside the session bar) when it shows.</summary>
    public static SKRect LatestPlate;
    public static bool LatestPlateVisible;
    /// <summary>The onboard treatment (full screen: RPM arc, gear, pedals, driver plate, gaps) - no free space while it is up.</summary>
    public static bool OnboardVisible;

    /// <summary>#349: the centre third of the screen (where the cars are) stays clear of cards.</summary>
    public const float CentreLeft = 640f, CentreRight = 1280f;
    /// <summary>The right third, where cards that need room go (#349: x >= 1290).</summary>
    public const float RightThirdLeft = 1290f;

    /// <summary>The visual pack's own cards say where they are each frame (<see cref="Claim"/>), so they keep off each other.</summary>
    public const int SlotWeather = 0, SlotRaceControl = 1, SlotYellowMap = 2, SlotStrategy = 3, SlotLive = 4, SlotPip = 5, SlotAwards = 6, SlotCount = 7;
    static readonly SKRect[] claims = new SKRect[SlotCount];
    static readonly double[] claimAt = { -1e12, -1e12, -1e12, -1e12, -1e12, -1e12, -1e12 };
    const double ClaimLifeMs = 250;

    public static void Claim(int slot, SKRect r, double nowMs) { claims[slot] = r; claimAt[slot] = nowMs; }
    public static void Release(int slot) => claimAt[slot] = -1e12;

    /// <summary>Everything on screen right now, as rects (no allocation: fills <paramref name="dst"/>). <paramref name="self"/> = the
    /// asking card's own claim slot (left out), -1 = none.</summary>
    public static int Occupied(Span<SKRect> dst, double nowMs, int self = -1)
    {
        int n = 0;
        void Add(Span<SKRect> d, SKRect r) { if (n < d.Length && r.Width > 0 && r.Height > 0) d[n++] = r; }
        if (TowerVisible) Add(dst, Tower);
        if (SessionBarVisible)
        {
            Add(dst, SessionBar);
            float wx = SessionBar.MidX, wy = SessionBar.MidY;
            Add(dst, new SKRect(wx - 70, wy - 70, wx + 70, wy + 70));
        }
        if (TickerVisible) Add(dst, Ticker);
        if (RadioVisible) Add(dst, Radio);
        if (BattleMapVisible) Add(dst, BattleMap);
        if (BattleCardVisible) Add(dst, BattleCard);
        if (StoryVisible) Add(dst, Story);
        if (FeaturedVisible) Add(dst, Featured);
        if (CustomVisible) Add(dst, Custom);
        if (RcBarVisible) Add(dst, RcBar);
        if (LatestPlateVisible) Add(dst, LatestPlate);
        for (int i = 0; i < SlotCount; i++)
            if (i != self && nowMs - claimAt[i] < ClaimLifeMs && nowMs >= claimAt[i] - 1) Add(dst, claims[i]);
        return n;
    }

    /// <summary>#611: just the pack cards' claims (live ones), into <paramref name="dst"/>; returns how many.</summary>
    public static int Claims(Span<SKRect> dst, double nowMs)
    {
        int n = 0;
        for (int i = 0; i < SlotCount && n < dst.Length; i++)
            if (nowMs - claimAt[i] < ClaimLifeMs && nowMs >= claimAt[i] - 1 && claims[i].Width > 0 && claims[i].Height > 0) dst[n++] = claims[i];
        return n;
    }

    /// <summary>True when <paramref name="r"/> (grown by <paramref name="pad"/>) touches no live graphic, no other pack card and,
    /// when <paramref name="keepCentre"/>, not the centre third. Never free while the onboard treatment is up.</summary>
    public static bool IsFree(SKRect r, double nowMs, int self = -1, float pad = 12f, bool keepCentre = true)
    {
        if (OnboardVisible) return false;
        if (r.Left < 0 || r.Top < 0 || r.Right > 1920 || r.Bottom > 1080) return false;
        if (keepCentre && r.Right > CentreLeft && r.Left < CentreRight) return false;
        if (r.Top < CardTop) return false;   // #413: a full-width band under the session bar stack stays clear
        Span<SKRect> occ = stackalloc SKRect[24];
        int n = Occupied(occ, nowMs, self);
        var g = new SKRect(r.Left - pad, r.Top - pad, r.Right + pad, r.Bottom + pad);
        for (int i = 0; i < n; i++) if (g.IntersectsWith(occ[i])) return false;
        return true;
    }

    /// <summary>Distance from <paramref name="p"/> to the nearest live graphic or pack card (0 inside one).</summary>
    public static float Clearance(SKPoint p, double nowMs, int self = -1)
    {
        Span<SKRect> occ = stackalloc SKRect[24];
        int n = Occupied(occ, nowMs, self);
        float best = 1e9f;
        for (int i = 0; i < n; i++)
        {
            var r = occ[i];
            float dx = Math.Max(0, Math.Max(r.Left - p.X, p.X - r.Right)), dy = Math.Max(0, Math.Max(r.Top - p.Y, p.Y - r.Bottom));
            best = Math.Min(best, MathF.Sqrt(dx * dx + dy * dy));
        }
        return best;
    }

    /// <summary>Lowest point of everything hanging from the top edge right now: the bar, its watch, the ticker.</summary>
    public static float TopStackBottom
    {
        get
        {
            float b = SessionBarVisible ? Math.Max(SessionBar.Bottom, SessionBar.MidY + 64f + 6f) : 0f;
            if (TickerVisible) b = Math.Max(b, Ticker.Bottom);
            return b;
        }
    }

    /// <summary>#413 (Angelo: "almost all of those cards will touch the session bar and the session bar ticker if it is
    /// on"): every pack card keeps at least this far below <see cref="TopStackBottom"/> (bar plate + watch + the ticker
    /// while it shows), across the whole width.</summary>
    public const float TopGap = 16f;

    /// <summary>The highest a pack card's top may sit right now (<see cref="TopStackBottom"/> + <see cref="TopGap"/>).</summary>
    public static float CardTop => TopStackBottom + TopGap;

    /// <summary>True when <paramref name="r"/> touches the session bar (plate or watch) or the ticker.</summary>
    public static bool HitsTopStack(SKRect r)
    {
        if (SessionBarVisible)
        {
            if (r.IntersectsWith(SessionBar)) return true;
            float wx = SessionBar.MidX, wy = SessionBar.MidY;
            var watch = new SKRect(wx - 70, wy - 70, wx + 70, wy + 70);
            if (r.IntersectsWith(watch)) return true;
        }
        return TickerVisible && r.IntersectsWith(Ticker);
    }
}
