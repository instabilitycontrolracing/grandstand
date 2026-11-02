using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

/// <summary>
/// Round 2 (2026-10-27): a realistic overlay composition drawn by the REAL renderers - timing tower, session bar + watch
/// (normal, or battle mode: full width with the standings ticker), the team-radio card - laid out exactly as
/// OverlayCompositor does it, and published to <see cref="VpLayout"/> so the new cards place themselves against it.
/// Drivers are the canned placeholder field.
/// </summary>
sealed class Comp
{
    public readonly RaceSnapshot Snap = Canned.Build();
    readonly TimingTowerRenderer tower = new();
    readonly SessionBarRenderer bar = new();
    readonly WatchRenderer watch = new();
    readonly StandingsTickerRenderer ticker = new();
    readonly RadioPopupRenderer radio = new();
    readonly FeaturedCardRenderer featured = new();
    readonly BattleCardRenderer battleCard = new();
    readonly BattleMap battleMap = new();
    RaceSnapshot? battleSnap;
    /// <summary>Round 3: in battle mode also draw the real battle card (bottom centre) and the battle map (top right).</summary>
    public bool BattleCard;
    public bool Battle;              // full-width bar + ticker, tower away
    /// <summary>#413 previews: the standings ticker on in every scenario. In the app the ticker only shows with the battle-style
    /// bar (full width; battle or the replay bumper's look), and the tower is away then - drawn exactly that way.</summary>
    public static bool TickerAlways;
    public bool Tower = true;
    public bool Featured;
    public bool Radio;               // a team-radio card up (race control voice)
    public double RadioAtMs = -1;     // push the radio line at this time (>= 0 turns Radio on)
    public VpGraphic? LastLens;
    public string RadioWho = "ENGINEER", RadioText = "Box this lap, box this lap. Fuel only, tyres are fine.";
    public Func<double, bool>? BattleAt;   // battle mode as a function of time (overrides Battle)
    public DateTime Clock = new(2026, 10, 27, 20, 41, 0);
    double barFlipAt = -1; bool barWasFull;
    readonly SKNoDrawCanvas noDraw = new(1920, 1080);
    bool radioPushed;

    public float BarWidthAt(double t, bool full)
    {
        if (full != barWasFull) { barWasFull = full; barFlipAt = t; }
        float def = SessionBarRenderer.BarWidth, fullW = 1920f;
        if (barFlipAt < 0) return full ? fullW : def;
        float k = (float)Math.Clamp((t - barFlipAt) / 400.0, 0, 1); k = k * k * (3 - 2 * k);
        return full ? def + (fullW - def) * k : fullW - (fullW - def) * k;
    }

    /// <summary>Draws the under-graphics (radio card first = bottom, tower) - before the visual-pack graphic.</summary>
    public void Under(SKCanvas c, double t)
    {
        bool fight = BattleAt?.Invoke(t) ?? Battle;
        bool battle = fight || TickerAlways;   // the battle-style look (full-width bar, ticker, tower away)
        PresentationState.IsBattleActive = fight;
        PresentationState.BumperBattleStyle = TickerAlways;
        PresentationState.Tick(Snap);
        if (RadioAtMs >= 0 && t >= RadioAtMs) Radio = true;
        if (Radio)
        {
            if (!radioPushed) { radioPushed = true; RadioFeed.Push(RadioWho, RadioText); }
            float rx = 1920 - 24f, by = 1080 * 0.665f;
            c.Save(); c.Translate(rx, by); c.Scale(0.9f); c.Translate(-rx, -by);
            radio.Draw(c, rx, by, Snap, t);
            c.Restore();
            VpLayout.RadioRight = rx; VpLayout.RadioBottom = by;
            float rh = radio.LastHeight * 0.9f;
            VpLayout.RadioVisible = rh > 1;
            VpLayout.Radio = new SKRect(rx - RadioPopupRenderer.Width * 0.9f, by - rh, rx, by);
        }
        else { VpLayout.RadioVisible = false; VpLayout.RadioRight = 1896; VpLayout.RadioBottom = 1080 * 0.665f; }
        VpLayout.BattleMapVisible = false;
        VpLayout.BattleCardVisible = false;
        if (BattleCard)
        {
            // a battle for the player in P3 of GTP: the car ahead 0.5 s up the road, the car behind 0.6 s back
            if (battleSnap == null)
            {
                var g = Snap.ClassLeaderboard[0].Drivers;
                var list = new List<Driver>();
                for (int i = 0; i < g.Count; i++)
                    list.Add(g[i] with { LapDistPct = 0.42f - i * 0.0025f, LapsCompleted = 12, IntervalSeconds = i == 0 ? 0 : i == 2 ? 0.5 : i == 3 ? 0.6 : 1.4 });
                var cls = new List<DriverClass> { new(Snap.ClassLeaderboard[0].Name, Snap.ClassLeaderboard[0].Color, list) };
                cls.AddRange(Snap.ClassLeaderboard.Skip(1));
                battleSnap = Snap with { ClassLeaderboard = cls, Player = list[2], PlayerCarIdx = list[2].CarIdx, CameraTarget = list[2], CameraCarIdx = list[2].CarIdx };
            }
            float wl = (1920 - 1280f) / 2f, wt = 1080 - 348f;
            var canvas = fight ? c : (SKCanvas)noDraw;
            battleCard.Draw(canvas, wl + 140f, wt + 30f, battleSnap, t + 5000);
            bool on = fight && battleCard.IsActive;
            VpLayout.BattleCardVisible = on;
            VpLayout.BattleCard = new SKRect(Math.Min(wl + 140f, battleCard.DrawnRight > battleCard.DrawnLeft ? battleCard.DrawnLeft : 1e9f), wt + 30f - 40f,
                Math.Max(wl + 140f + BattleCardRenderer.CardWidth, battleCard.DrawnRight), wt + 30f + BattleCardRenderer.CardHeight);
            battleMap.Draw(canvas, 1920 - 24f - BattleMap.W, 172f, battleSnap, battleCard.BattleCars, on, t + 5000);
            VpLayout.BattleMapVisible = on;
            VpLayout.BattleMap = new SKRect(1920 - 24f - BattleMap.W, 172f, 1920 - 24f, 172f + BattleMap.H);
        }
        if (Tower && !battle) tower.Draw(c, 16f, 18f, 1080 - 34f, Snap, t + 5000);
        if (Featured && !battle) { c.Save(); c.Translate((1920 - 768f) / 2f, 1080 - 16f - 211f); featured.Draw(c, 24f, 32.5f, Snap, t + 5000); c.Restore(); }
        VpLayout.TowerVisible = Tower && !battle;
    }

    /// <summary>The ticker and the session bar + watch (drawn last in the real overlay, over most graphics).</summary>
    public void Top(SKCanvas c, double t)
    {
        bool battle = (BattleAt?.Invoke(t) ?? Battle) || TickerAlways;
        if (battle) ticker.Draw(c, 0f, 110f, 1920, Snap, t + 5000);
        VpLayout.TickerVisible = battle;
        VpLayout.Ticker = new SKRect(0, 110, 1920, 110 + StandingsTickerRenderer.BarHeight);
        float bw = BarWidthAt(t, battle), bx = (1920 - bw) / 2f;
        bar.Draw(c, bx, 18f, 1f, Snap, Clock, t + 5000, bw);
        watch.Draw(c, bx + bw / 2f, 18f + SessionBarRenderer.BarHeight / 2f, 64f, Clock.AddMilliseconds(t));
        VpLayout.SessionBar = new SKRect(bx, 18, bx + bw, 18 + SessionBarRenderer.BarHeight);
        VpLayout.SessionBarVisible = true;
        VpLayout.BattleStyle = battle;
    }

    /// <summary>Publish the layout for frame t without drawing (state machines that run ahead of the picture).</summary>
    public void Layout(double t)
    {
        bool battle = (BattleAt?.Invoke(t) ?? Battle) || TickerAlways;
        float bw = BarWidthAt(t, battle), bx = (1920 - bw) / 2f;
        VpLayout.SessionBar = new SKRect(bx, 18, bx + bw, 18 + SessionBarRenderer.BarHeight);
        VpLayout.TickerVisible = battle;
        VpLayout.Ticker = new SKRect(0, 110, 1920, 110 + StandingsTickerRenderer.BarHeight);
        VpLayout.SessionBarVisible = true;
    }
}
