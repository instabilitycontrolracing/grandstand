using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;
using SkiaSharp;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// Phase C — the multi-renderer compositor. Owns a registry of overlays, each a
/// triple of (existing renderer, screen position, visibility predicate), and
/// draws every visible one onto the single shared GPU <see cref="SKCanvas"/> the
/// <see cref="NativeOverlayHost"/> hands it. This replaces the nine separate WPF
/// transparent windows with one canvas — the renderers are reused UNCHANGED;
/// only the window/present layer changed.
///
/// <para><b>On-demand (dirty-driven) rendering — the headline behaviour.</b> The
/// host calls <see cref="FrameNeeded"/> every loop tick (including while idle)
/// and only presents when it returns true. A frame is needed when: a transition
/// splash is active (the video path), an overlay's visibility just flipped (its
/// entrance/exit fade is still playing), an overlay animates continuously while
/// visible, or the rendered race data changed within the last
/// <see cref="ContentSettleMs"/> (so renderer-internal pulses/arrows triggered by
/// a data change play out fully). When none of those hold, the host sleeps and
/// the GPU costs ~0 — static overlays are free at rest. See DECISIONS.md.</para>
///
/// <para><b>No per-frame allocations.</b> The decision path uses an alloc-free
/// numeric hash of the rendered fields; the paint adapters allocate nothing; the
/// renderers themselves were already made alloc-free on their hot paths.</para>
/// </summary>
internal sealed class OverlayCompositor : IDisposable
{
    /// <summary>
    /// One overlay on the shared canvas. <see cref="Visible"/> is the same gate
    /// the WPF window read (<see cref="PresentationState"/> / <see cref="SceneDirector"/>);
    /// <see cref="OnsetMs"/> is how long after a visibility flip the renderer's
    /// own entrance/exit fade keeps it changing (so we keep presenting through
    /// it); <see cref="Continuous"/> marks overlays that animate every frame
    /// while visible (e.g. a sweeping watch); <see cref="Paint"/> positions the
    /// canvas and calls the existing renderer at its in-window offset.
    /// </summary>
    /// <summary>A rect grown by m on every side (fade sheets: room for shadows / glows).</summary>
    static SKRect Grow(SKRect r, float m) => r.IsEmpty ? r : new SKRect(r.Left - m, r.Top - m, r.Right + m, r.Bottom + m);

    sealed class Entry
    {
        /// <summary>Fades away while an onboard shot is on air (the full-screen onboard owns the bottom of the screen).</summary>
        public bool HideOnOnboard;
        public int ProfSlot = -2;
        public required string Name;
        public required Func<bool> Visible;
        public required Action<SKCanvas, RaceSnapshot, double, int, int> Paint;
        public double CostMs; public int CostN;   // paint time, for the "frame cost" log line
        public double OnsetMs;
        public bool Continuous;
        /// <summary>Paint/visibility errors so far; after too many the graphic is switched off (the rest keep running).</summary>
        public int Fails;
        public bool Broken;
        public double LastFailLogMs = -1e9;
        // Optional self-gated "I am active / mid-animation" signal. For overlays
        // whose visibility is decided by the renderer's own state machine
        // (battle / fastest-lap / pit callout) rather than a PresentationState
        // gate: when it returns true we keep painting + presenting so the
        // entrance/exit animation plays out even past the data settle window.
        public Func<bool>? Animating;
        /// <summary>2026-10-30 (Angelo: "does it have to render all of the gaps in 4K?"): where this graphic draws (layout
        /// units). A fade then uses an offscreen sheet just that big instead of the whole 4K screen. Null = whole screen.</summary>
        public Func<SKRect>? Bounds;

        public bool LastVisible;
        public double FlipAtMs = -1;
        public bool PaintThisFrame;

        // Requirement B: while a tower transition plays, freeze this overlay to an
        // SKPicture (recorded once, replayed cheaply) so its per-present allocation
        // and GC do not spike the splash frame budget. Cleared when live again.
        public bool Cacheable;
        public SKPicture? Cache;
        /// <summary>#428: only freeze when the graphic is fully shown (never a picture taken mid-fade).</summary>
        public Func<bool>? CacheOk;

        // Grandstand control app (2026-10-23): applied from ControlState only when its
        // Version changes - never parsed per frame. Allowed=false hides the graphic;
        // Ox/Oy/Sc move and resize it around its Anchor (1080p design pixels).
        public bool Allowed = true;
        public float Ox, Oy, Sc = 1f;
        public Func<int, int, SKPoint>? Anchor;
    }

    /// <summary>
    /// After any change to the rendered data, keep presenting for this long so
    /// every renderer-internal animation a data change can trigger plays to
    /// completion. The tower's position-change arrow (~3.5 s) is the longest;
    /// the snapshot field itself only differs for a single frame, so without a
    /// settle window the animation would freeze after one present.
    /// </summary>
    const double ContentSettleMs = 3600;

    readonly RaceStateService raceState;
    readonly bool demo;
    readonly bool demoGrid;

    readonly TimingTowerRenderer tower = new();
    readonly TimingTowerSplashRenderer splash = new();
    readonly FeaturedCardRenderer featured = new();
    readonly StandingsTickerRenderer ticker = new();
    readonly BattleCardRenderer battle = new();
    readonly RadioPopupRenderer radioPopup = new();
    readonly YellowFlagCallout yellowFlag = new();   // 2026-10-25: animated yellow-flag callout
    readonly OnboardFullRenderer onboard = new();
    readonly PackCardRenderer pack = new();
    readonly PitBattleCard pitCard = new();
    SKNoDrawCanvas? pitNoDraw;
    readonly SKPaint packFade = new();
    SKNoDrawCanvas? packNoDraw;   // full-screen onboard (2026-10-24); OnboardRenderer = the small card
    double onboardRawSinceMs = -1, onboardOffSinceMs = -1, onboardLastMs = -1;
    double currentNowMs, lastStoryWhyMs = -1e9, lastDataPresentMs = -1e9;
    bool dataOnly;
    float battleSlide; double lastBattleSlideMs = -1;
    readonly StartingGridRenderer grid = new();
    readonly PreRaceCards preCards = new();      // 2026-10-18: lower-third info cards until the green
    readonly GridSpotlightCycle spotlight = new();
    readonly FastestLapRenderer fastest = new();
    readonly PitCalloutRenderer pit = new();
    readonly SessionBarRenderer bar = new();
    readonly ViewerTierRenderer viewerTiers = new();     // 2026-10-30 (#576, Angelo approved): the tiered viewer looks + thank-you roll
    readonly ViewerCardsRenderer viewerCards = new();   // 2026-10-29 (#494): Super Chat / tips / members, track fund
    readonly RaceControlBar rcBar = new();               // 2026-10-30 (#619): race control as a bar under the session bar
    readonly ViewerLatestPlate latestPlate = new();      // 2026-10-30 (#608): the latest viewer message, kept up
    Entry? latestEntry, rcEntry;
    bool latestWanted; double latestForceUntilMs = -1, lastCamCollectMs = -1e9;
    readonly SKRect[] camRects = new SKRect[40];         // #611: the graphics on screen, for the cam placer (no allocation)
    string themePick = "default"; bool themeByDate; double lastThemeCheckMs = -1e9;
    ThemeDateSettings themeDates = new(); int seenThemePreview; double lastThemeFrameMs = -1e9;   // #601
    readonly WatchRenderer watch = new();
    readonly StoryCards story = new();
    readonly BattleMap battleMap = new();     // 2026-10-25: battle track map (only the battling cars)
    readonly OutroRenderer outro = new();   // 2026-10-20: post-race outro (flag, podiums, results)   // 2026-10-19: half-screen race-story takeovers
    readonly LensRain lensRain = new();       // 2026-10-26 (#234): rain drops on the TV camera's lens
    readonly ReplayWipe replayWipe = new();   // 2026-10-26 (#235): REPLAY / LIVE wipe + corner REPLAY tag
    Entry? lensEntry, wipeEntry;
    readonly VisualPackDirector vp = new();       // 2026-10-27: the visual review pack (V1-V8, W8-W13), all off by default
    readonly Dictionary<string, Entry> vpEntries = new();
    readonly Func<string, double> readVar;
    readonly List<Entry> entries = new();
    readonly Entry towerEntry;       // captured for the splash-occlusion skip
    Entry? battleEntry;

    // Control app state (2026-10-23)
    int appliedControlVersion;
    double controlBoostUntilMs = -1;

    RaceSnapshot currentSnap = RaceSnapshot.Disconnected;
    ulong lastSig;
    bool sigInit;
    double lastChangeAtMs = -1e9;

    // Present clock — bumped once per actual present (each Render call). Drives
    // the even-pacing splash video clock and a running estimate of the display's
    // present interval.
    long presentIndex;
    double presentIntervalMs = 1000.0 / 60.0;
    double lastPresentNowMs = -1;

    // Even-pacing anchor for the transition video.
    bool splashClockArmed;
    long splashStartPresent;
    double splashStartReal;

    // Session-bar battle-mode width animation state (mirrors MainWindow's
    // ComputeBarWidth): the bar grows from 976 px to full-width when a battle is
    // active and shrinks back, over a 400 ms smoothstep.
    bool barWasFullWidth;
    double barWidthFlipAtMs = -1;

    // Transition playback health counters (see Render).
    bool clipStatsArmed;
    int clipSlowPresents;
    double lastClipPresentMs = -1;

    /// <summary>Total presents issued so far (used by the host's cadence log).</summary>
    public long PresentCount => presentIndex;

    /// <summary>#488: which OBS scene matches the broadcast right now (see the streaming plan's phase table).</summary>
    static string ObsSceneFor(RaceSnapshot s)
    {
        if (!s.IsConnected) return "Starting soon";
        if (PresentationState.ReplayHideK > 0.5f) return "Replay";
        if (OutroDirector.FullScreen) return "Post-race";
        if (PresentationState.OnboardActive) return "Onboard";
        if (s.Phase == RacePhase.PreRace && StartingGridRenderer.MusicEndMs < 0) return "Onboard";   // the opener: full screen, no cams
        if (StoryDirector.Visible) return "Story card";
        if (PresentationState.IsBattleActive || PresentationState.BumperBattleStyle || PresentationState.IsPostRace) return "Battle";   // tower off: cams on the left
        return "Racing";
    }

    public OverlayCompositor(RaceStateService raceState, bool demo, bool demoGrid = false)
    {
        this.raceState = raceState;
        this.demo = demo;
        ObsMarkers.Start();   // 2026-10-28 (#487): idle unless "Highlights to OBS" is on
        this.demoGrid = demoGrid;

        // Registered in WPF z-order (App.OnStartup): tower, then the splash above
        // it (covers the rebuild), then the featured lower-third. Painted in this
        // order each present onto the freshly-cleared shared canvas. Geometry is
        // computed from the live canvas size (the iRacing window), which on the
        // primary 1080p display equals the dimensions the renderers were authored
        // against — see the per-window numbers in PORT_PLAN.md.

        // Tower lane — 432 px wide lane at the left edge, 16 px inset, full height.
        towerEntry = new Entry
        {
            Name = "tower",
            Bounds = static () => new SKRect(0, 0, 720, 1080),   // tower lane + its pit / stop tags to the right
            Visible = static () => PresentationState.TowerVisible,
            OnsetMs = 220 + 80,            // TimingTowerRenderer gate fade + overhang
            Continuous = false,           // animates only on data changes (covered by the settle window)
            Animating = () => tower.WingsAnimating,   // keep presenting while a pit/fastest-lap wing is out
            Cacheable = true,
            Paint = (c, snap, nowMs, w, h) =>
            {
                // Top inset 18 (matches the session-bar top edge); bottom held at
                // h-16 (aligned with the featured card), so height = h-34.
                float availableHeight = h - 34f;
                // #644 (Angelo 10-31): the tower always keeps its full length - the face cam sits over its bottom
                tower.Draw(c, 16f, 18f, availableHeight, snap, nowMs);
                // 2026-10-30 (class transitions choppy): the next built-in roller's still parts, drawn ahead in the background
                if (!SceneDirector.SplashActive) splash.Prepare(c, 16f, 18f, 400f, h - 34f, snap);   // the splash still covers the whole lane (cam included)
            },
        };
        entries.Add(towerEntry);

        // #644 (2026-10-31, Angelo): the face cam lives at the bottom of the left column - drawn AFTER the tower (on top of
        // the names) and BEFORE the splash and every other graphic (the transitions play over it). It never moves: its bottom
        // edge is the tower's bottom edge, and the tower keeps its full length. Picture: OBS's "GSFaceCam" Spout sender.
        FaceCamFeed.Start();
        entries.Add(new Entry
        {
            Name = "facecam",
            HideOnOnboard = true,
            Bounds = static () => new SKRect(0, 0, 440, 1080),
            Visible = () => ControlState.Current.FaceCam.On && FaceCamFeed.AgeMs < 1600 && (FaceCamRuleOn || faceCamVisK > 0.005f),
            OnsetMs = 400,
            Continuous = true,
            Paint = (c, snap, nowMs, w, h) => PaintFaceCam(c, nowMs, w, h),
        });

        // Splash — same lane as the tower; drawn on top during a scene transition.
        entries.Add(new Entry
        {
            Name = "splash",
            HideOnOnboard = true,
            Bounds = static () => new SKRect(0, 0, 432, 1080),   // 2026-10-30: the onboard fade's layer just around the lane, not 4K
            Visible = static () => SceneDirector.SplashActive,
            OnsetMs = SceneDirector.SplashFadeMs + 80,
            Continuous = false,           // SplashActive forces a frame directly (the video path)
            Paint = (c, snap, nowMs, w, h) =>
            {
                float width = 400f;        // 432 lane − 2×16 inset
                float height = h - 34f;    // matches the tower lane (top 18, bottom h-16)
                double videoNowMs = SplashNowMs(nowMs);
                splash.Draw(c, 16f, 18f, width, height, snap, videoNowMs);
            },
        });

        // Featured card — 768×254 lower-third, horizontally centred, 24 px off the
        // bottom; the renderer adds its own (24,22) card inset and entrance slide.
        entries.Add(new Entry
        {
            Name = "featured",
            HideOnOnboard = true,
            CacheOk = () => featured.LastAlpha >= 0.99f,
            Visible = static () => PresentationState.FeaturedVisible,
            OnsetMs = 420 + 280 + 80,      // entrance + outer gate fade + overhang
            Continuous = false,
            Cacheable = true,
            Paint = (c, snap, nowMs, w, h) =>
            {
                float left = (w - 768f) / 2f;
                // Move the card straight down so its visible glass bottom lines
                // up with the timing tower's bottom edge. Tower bottom = 16 px
                // top inset + (h-32) height = h-16. The card's glass bottom sits
                // 211 px below this slot top (32.5 inset + 178.5 visible glass),
                // so slot top = (h-16) - 211. Resolution-independent.
                float top = h - 16f - 211f;
                c.Save();
                c.Translate(left, top);
                // Card is 189 tall with the glass trimmed 10.5 px off the bottom;
                // drawn at inset (24, 32.5).
                featured.Draw(c, 24f, 32.5f, snap, nowMs);
                c.Restore();
            },
        });

        // ===== Batch 2-5: the rest of the suite (positions mirror the WPF windows) =====

        // Standings ticker - full-width strip flush under the session-bar line
        // (bar top=18, height 92 → bottom 110; ticker follows). Shows only during
        // a battle (TickerVisible), i.e. when the tower/featured yield.
        // 320 ms renderer fade + overhang.
        entries.Add(new Entry
        {
            Name = "ticker",
            Bounds = static () => Grow(VpLayout.Ticker, 40f),
            HideOnOnboard = true,
            // battle, or pre-race after the intro: rolls the starting order until green
            Visible = static () => StandingsTickerRenderer.ShouldShow && !OutroDirector.FullScreen,   // over the outro: "ticker_post"
            OnsetMs = 320 + 80,
            // The ticker SCROLLS on the render clock, so it must drive the
            // on-demand gate itself. When false it only animated because a
            // concurrent battle (battle.Animating) forced a global present every
            // frame; the instant the battle ended that driver vanished and the
            // ticker froze mid-scroll, stranding its last frame on screen (the
            // "ghost"). Continuous keeps the present loop alive while it is
            // visible, so it scrolls smoothly AND its 320 ms exit fade always
            // plays to completion. Mirrors the spotlight entry below.
            Continuous = true,
            Paint = (c, snap, nowMs, w, h) => ticker.Draw(c, 0f, 110f, w, snap, nowMs),
        });

        // Battle card - auto-context lower-third, centred, 8 px off the bottom.
        // It SELF-GATES: the renderer's IsActive state machine decides when a
        // battle is on, so it is always evaluated (Visible = true, draws nothing
        // when inactive) and we publish IsBattleActive each present (see Render).
        // Animating keeps it presenting through its entrance/exit.
        entries.Add(new Entry
        {
            Name = "battle",
            Bounds = static () => Grow(VpLayout.BattleCard, 260f),
            HideOnOnboard = true,
            Visible = static () => true,
            Animating = () => battle.IsActive,
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                float winLeft = (w - 1280f) / 2f;
                float winTop = h - 340f - 8f; if (winTop < 0f) winTop = 0f;
                // 2026-10-24 (Angelo): when a story card takes over, the battle card slides down out of
                // the way (and back when it ends) instead of sitting under it
                double dt = lastBattleSlideMs < 0 ? 16 : Math.Clamp(nowMs - lastBattleSlideMs, 0, 100);
                lastBattleSlideMs = nowMs;
                float target = StoryDirector.Visible ? 1f : 0f;
                battleSlide += (target - battleSlide) * (float)Math.Min(1, dt / 140.0);
                if (Math.Abs(target - battleSlide) < 0.002f) battleSlide = target;
                // round 3: where the card is (its UNDER PRESSURE tab rides 40 px above it), for the visual pack's free-space search
                VpLayout.BattleCard = new SKRect(winLeft + 140f, winTop + 30f - 40f + battleSlide * 220f, winLeft + 140f + BattleCardRenderer.CardWidth, winTop + 30f + BattleCardRenderer.CardHeight + battleSlide * 220f);
                if (battleSlide >= 0.995f) return;
                int sv = -1;
                if (battleSlide > 0.001f)
                {
                    using var lp = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(255 * (1 - battleSlide))) };
                    // 2026-10-30: a sheet the size of the battle slot (+ its slide and the tab / wide cards), not the 4K screen
                    sv = c.SaveLayer(new SKRect(0, winTop - 120f, w, h), lp);
                    c.Translate(0, battleSlide * 220f);
                }
                // Card centred in its 1280x340 slot: +140 x, +30 y.
                // (2026-10-25: the pack card with steering / pedals was retired - Angelo liked the battle card better;
                // the three-way card now grows to five cars instead)
                // 2026-10-25 (Angelo picked "pit lane race"): both of us on pit road -> the pit lane battle card; the
                // battle card keeps running underneath (its state drives the tower and the ticker)
                LiveryArt.Prewarm(snap);     // 2026-10-25: car pictures for the whole field, ahead of time
                var rivalPit = battle.Opponent;
                pitCard.Tick(snap, rivalPit, nowMs);
                if (snap.Player is { IsInPit: true } meP && rivalPit is { IsInPit: true } && battle.IsActive)
                {
                    battle.Draw(pitNoDraw ??= new SKNoDrawCanvas(w, h), winLeft + 140f, winTop + 30f, snap, nowMs);
                    var rv = snap.ClassLeaderboard.SelectMany(cl => cl.Drivers).FirstOrDefault(d => d.CarIdx == rivalPit.CarIdx) ?? rivalPit;
                    pitCard.Draw(c, w / 2f, winTop + 30f + BattleCardRenderer.CardHeight, snap, meP, rv, battle.BattlePosition, nowMs, 1f);
                }
                else { pitCard.Hidden(); battle.Draw(c, winLeft + 140f, winTop + 30f, snap, nowMs); }
                if (sv >= 0) c.RestoreToCount(sv);
                // round 3: the card widens for three / five cars - publish its drawn width
                if (battle.DrawnRight > battle.DrawnLeft)
                    VpLayout.BattleCard = new SKRect(Math.Min(battle.DrawnLeft, VpLayout.BattleCard.Left), VpLayout.BattleCard.Top, Math.Max(battle.DrawnRight, VpLayout.BattleCard.Right), VpLayout.BattleCard.Bottom);
                // 2026-10-25 (Angelo: "it keeps getting kicked around and the radio sometimes gets covered by it ... top
                // left or right corner below the session bar"): the battle map has its own fixed spot, top-right under
                // the session bar and ticker. Away while a story card is up or during an onboard shot (the onboard's
                // gap readout lives up there).
                bool mapShow = battle.IsActive && !(snap.Player is { IsInPit: true }) && battleSlide < 0.5f && PresentationState.OnboardK < 0.5f;
                battleMap.Draw(c, w - 24f - BattleMap.W, 172f, snap, battle.BattleCars, mapShow, nowMs);
                VpLayout.BattleMapVisible = mapShow;
                VpLayout.BattleMap = new SKRect(w - 24f - BattleMap.W, 172f, w - 24f, 172f + BattleMap.H);
            },
        });


        // Starting grid - centred 1100x830 slot; phase-gated (pre-race grid /
        // post-race classification). 520 ms cross-fade + overhang.
        entries.Add(new Entry
        {
            Name = "grid",
            // Hidden while the pre-race hero cycle is covering the same slot.
            Visible = () => PresentationState.GridVisible && !StoryDirector.Visible,   // Driver of the Day plays first
            // Pre-race: keep presenting so the 30 s on/off fades and the class-page
            // cross-fades of the hero board animate smoothly (2026-10-18).
            Animating = () => StartingGridRenderer.PreRaceAnimating && PresentationState.GridVisible,
            // Exit is the longer of the two fades (820 ms float-up dissolve at
            // the green); keep presenting through it. The 520 ms entrance fits
            // inside this window, and on entrance the entry is visible anyway.
            OnsetMs = 820 + 80,
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                float winLeft = (w - 1100f) / 2f;
                float winTop = Math.Max(0f, (h - 830f) / 2f);
                grid.ScreenRect = new SKRect(0, 0, w, h);                 // intro splash fills the screen
                grid.Draw(c, winLeft + 16f, winTop + 16f, snap, nowMs);   // 16 px inset
            },
        });

        // Pre-race info cards (2026-10-18): lower third, bottom centre, from the end of
        // the opener until the green flag. Continuous while active (slide animations).
        entries.Add(new Entry
        {
            Name = "precards",
            Visible = () => PresentationState.Phase == RacePhase.PreRace && StartingGridRenderer.CardsStartMs >= 0,
            OnsetMs = 500,
            Continuous = true,
            Paint = (c, snap, nowMs, w, h) =>
                { preCards.Screen = new SKRect(0, 0, w, h); preCards.Draw(c, (w - PreRaceCards.CardW) / 2f, h - PreRaceCards.CardH - 70f, snap, nowMs); },
        });
        // Grid spotlight - pre-race hero cycle in the same centred slot. Plays the
        // front-runner cards (top 3 per class, ~3 s each) on entering PreRace, then
        // goes inactive and the full grid board (gated above) fades in. Continuous
        // while active so the card holds + cross-fades advance every present.
        entries.Add(new Entry
        {
            Name = "spotlight",
            // 2026-10-25 (Angelo): the old top-3-in-class qualifying cards are retired - they held the intro back
            // (music playing, picture blocked). Kept in code, never shown.
            Visible = static () => false,
            OnsetMs = 360,
            Continuous = true,
            Paint = (c, snap, nowMs, w, h) => spotlight.Draw(c, w, h, nowMs),
        });

        // Fastest lap - transient callout (~49% down, centred horizontally).
        // Self-gated by the renderer's IsActive; evaluated every present so it
        // catches a new session best, presents through its slide-in/out.
        entries.Add(new Entry
        {
            Name = "fastest",
            HideOnOnboard = true,
            // 2026-10-25 (Angelo): the old fastest-lap chip is retired - a new fastest lap plays the GoFast Fastest Laps
            // card instead (StoryDirector). Kept in code.
            Visible = static () => false,
            Animating = () => fastest.IsActive,
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                float winLeft = (w - 784f) / 2f;
                float winTop = h * 0.493f; if (winTop + 174f > h) winTop = h - 174f - 8f;
                fastest.Draw(c, winLeft + 32f, winTop + 48f, snap, nowMs);
            },
        });

        // Pit callout - transient callout pinned near the top (15.2% down).
        // Self-gated by the renderer's IsActive (showing != null).
        entries.Add(new Entry
        {
            Name = "pit",
            // 2026-10-24 (Angelo): off - the tower already shows who is in the pits, and on lap 1 the
            // pop-ups were constant. Kept in code in case it comes back as an option.
            Visible = static () => false,
            Animating = () => false,
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                float winLeft = (w - 784f) / 2f;
                float winTop = h * 0.152f;
                pit.Draw(c, winLeft + 12f, winTop + 12f, snap, nowMs);
            },
        });


        // Team radio captions (2026-10-24): what the engineer / strategist / race director says.
        entries.Add(new Entry
        {
            Name = "radio",
            Bounds = static () => VpLayout.RadioVisible ? Grow(VpLayout.Radio, 80f) : SKRect.Empty,
            Visible = static () => true,
            Animating = () => radioPopup.IsActive(currentNowMs),
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                // 90 % size, near-square card (Angelo: "a little too big", "too long horizontally"), anchored at its bottom-right corner
                float rx = w - 24f, by = h * 0.665f;   // 2026-10-25: up a little so the battle card's UNDER PRESSURE tab clears it (was 0.70)
                c.Save(); c.Translate(rx, by); c.Scale(0.9f); c.Translate(-rx, -by);
                radioPopup.Draw(c, rx, by, snap, nowMs);
                c.Restore();
                // 2026-10-27: where it is, for the race control card (W12) that stacks above it
                VpLayout.RadioRight = rx; VpLayout.RadioBottom = by;
                float rh = radioPopup.LastHeight * 0.9f;
                VpLayout.RadioVisible = rh > 1f && radioPopup.IsActive(nowMs);
                VpLayout.Radio = new SKRect(rx - RadioPopupRenderer.Width * 0.9f, by - rh, rx, by);
            },
        });

        // Yellow flag callout (2026-10-25, Angelo): flashing marshal light panels + "YELLOW FLAG: <corner / straight>"
        // and what probably happened, centred under the session bar and ticker.
        entries.Add(new Entry
        {
            Name = "yellowflag",
            Visible = static () => true,
            Animating = () => yellowFlag.IsActive,
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                yellowFlag.Tick(snap, nowMs);
                // 2026-10-25 (Angelo: "get rid of the card that comes on just below the session bar"): the session bar's
                // light box carries the yellow now; the callout still works out where / why (and logs it), but isn't drawn
            },
        });

        // Post-race outro (2026-10-20) - full-screen flag / podiums / results (OutroDirector).
        entries.Add(new Entry
        {
            Name = "outro",
            Visible = static () => OutroDirector.FullScreen,
            Continuous = true,
            OnsetMs = 400,
            Paint = (c, snap, nowMs, w, h) => outro.Draw(c, w, h, snap, nowMs),
        });
        // 2026-10-28 (#486): the standings ticker ON TOP of the full-screen post-race pages (the bar is drawn later anyway)
        entries.Add(new Entry
        {
            Name = "ticker_post",
            Visible = static () => StandingsTickerRenderer.ShouldShow && OutroDirector.FullScreen,
            OnsetMs = 320 + 80,
            Continuous = true,
            Paint = (c, snap, nowMs, w, h) => ticker.Draw(c, 0f, 110f, w, snap, nowMs),
        });

        // Story cards (2026-10-19) - right-half takeover in quiet moments (StoryDirector).
        entries.Add(new Entry
        {
            Name = "story",
            HideOnOnboard = true,
            Visible = static () => StoryDirector.Visible,
            Continuous = true,
            OnsetMs = 700,
            Paint = (c, snap, nowMs, w, h) => story.Draw(c, w, h, snap, nowMs),
        });

        // 2026-10-27 (#339, preview - miniCardsInBattle, OFF by default): mini story cards during a battle while the full
        // ones are held (#338): a 560x110 plate in the right third under the battle map (StoryCards.MiniRect).
        entries.Add(new Entry
        {
            Name = "storymini",
            HideOnOnboard = true,
            Visible = static () => StoryDirector.MiniVisible,
            Continuous = true,
            OnsetMs = 450,
            Paint = (c, snap, nowMs, w, h) => story.DrawMini(c, w, h, snap, nowMs),
        });

        // Custom cards (2026-10-24) - made in the control app's card builder (CustomCards). Right side,
        // under the session bar. Presents only while fading or after its 2 Hz value refresh.
        entries.Add(new Entry
        {
            Name = "custom",
            Visible = static () => CustomCards.Visible,
            Animating = () => CustomCards.Animating(currentNowMs),
            Continuous = false,
            OnsetMs = 450,
            Paint = (c, snap, nowMs, w, h) => CustomCards.Draw(c, w - 24f, 150f, nowMs),
        });

        // 2026-10-29 (#494): viewer cards - a paid message rides a strip that slides down from under the session bar (drawn
        // before the bar, so the bar covers its slide), a big one also gets a banner over the tower's cap; members / subs a
        // pre-race style card under the bar; the track fund a slim bar. Self-gated: plays only when the screen is clear.
        entries.Add(new Entry
        {
            Name = "viewer",
            HideOnOnboard = true,
            Visible = () => viewerCards.Visible,
            Continuous = true,
            OnsetMs = 500,
            Paint = (c, snap, nowMs, w, h) =>
            {
                SKPoint? tt = PresentationState.TowerVisible && towerEntry.Allowed && towerEntry.Sc == 1f
                    ? new SKPoint(16f + towerEntry.Ox, 18f + towerEntry.Oy) : null;
                viewerCards.Draw(c, w, h, VpLayout.SessionBar, tt, nowMs);
            },
        });

        // #576 (2026-10-30, Angelo approved): the tiered looks - chat card, tier 3 card and tier 1's tower band go UNDER the
        // session bar; tier 2's sleeve, tier 1's full-screen moment and the thank-you roll go OVER it (entry after the bar).
        entries.Add(new Entry
        {
            Name = "viewertiers",
            HideOnOnboard = true,
            Visible = () => ViewerTiers.LooksOn && viewerTiers.Visible,
            // #603: fades use a sheet just round the card / band, not the whole 4K screen
            Bounds = () => viewerTiers.UnderBounds(VpLayout.SessionBar, new SKPoint(16f + towerEntry.Ox, 18f + towerEntry.Oy)),
            Continuous = true,
            OnsetMs = 500,
            Paint = (c, snap, nowMs, w, h) =>
            {
                SKPoint? tt = PresentationState.TowerVisible && towerEntry.Allowed && towerEntry.Sc == 1f
                    ? new SKPoint(16f + towerEntry.Ox, 18f + towerEntry.Oy) : null;
                viewerTiers.DrawUnder(c, w, h, VpLayout.SessionBar, tt, nowMs);
            },
        });

        // 2026-10-30 (#619): race control as a slim bar that slides down from under the session bar (under the ticker in a
        // battle, under the corner / straight name on an onboard). Drawn before the bar, so the bar covers its slide.
        rcEntry = new Entry
        {
            Name = "racecontrol",
            Bounds = () => rcBar.LastRect.IsEmpty ? SKRect.Empty : Grow(rcBar.LastRect, 30f),
            Visible = static () => true,
            Animating = () => rcBar.IsActive(currentNowMs),
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) =>
            {
                RcAnchor(w, out float cx, out float top, out float regK, out float maxRight);
                rcBar.Draw(c, cx, top, regK, maxRight, nowMs);
            },
        };
        entries.Add(rcEntry);

        // 2026-10-30 (#608): the latest viewer message, kept up top right beside the session bar until the next one plays.
        latestEntry = new Entry
        {
            Name = "viewerlatest",
            HideOnOnboard = true,
            Bounds = () => latestPlate.LastRect.IsEmpty ? SKRect.Empty : Grow(latestPlate.LastRect, 24f),
            Visible = () => latestPlate.Active,
            Animating = () => latestPlate.Fading,
            Continuous = false,
            OnsetMs = 500,
            Paint = (c, snap, nowMs, w, h) => latestPlate.Draw(c, VpLayout.SessionBar, nowMs),
        };
        entries.Add(latestEntry);

        // Session bar + Rolex watch - full-width top strip. Drawn LAST so the
        // watch overhang composites on top of the ticker beneath it. The watch
        // sweeps continuously => Continuous, so the canvas does not idle while
        // the bar is visible (BarVisible = not post-race). Composition glue
        // (bar-width animation, watch entrance + drop-shadow) is in PaintSessionBar.
        entries.Add(new Entry
        {
            Name = "sessionbar",
            Bounds = static () => Grow(new SKRect(VpLayout.SessionBar.Left, 0, VpLayout.SessionBar.Right, VpLayout.SessionBar.Bottom), 140f),
            Visible = static () => PresentationState.BarVisible,
            Continuous = true,
            OnsetMs = 0,
            Paint = (c, snap, nowMs, w, h) =>
            {
                // onboard shot (2026-10-24): the bar fades up and away, and comes back after
                float ok = PresentationState.OnboardK;
                if (ok >= 0.995f) return;
                if (ok <= 0.005f) { PaintSessionBar(c, snap, nowMs, w, h); return; }
                obLayer.Color = new SKColor(0, 0, 0, (byte)(255 * (1 - ok)));
                int sv = c.SaveLayer(Grow(new SKRect(VpLayout.SessionBar.Left, 0, VpLayout.SessionBar.Right, VpLayout.SessionBar.Bottom), 140f), obLayer);   // 2026-10-30: bar-sized sheet
                c.Translate(0, -ok * 40f);
                PaintSessionBar(c, snap, nowMs, w, h);
                c.RestoreToCount(sv);
            },
        });

        entries.Add(new Entry
        {
            Name = "viewertiers_top",
            // #603: the roll can be asked for with the tier looks off too (control app Play / Sample, or after the race)
            Visible = () => viewerTiers.Visible && (ViewerTiers.LooksOn || ViewerTierRenderer.RollActive),
            Bounds = () => viewerTiers.OverBounds(VpLayout.SessionBar),
            Continuous = true,
            OnsetMs = 500,
            Paint = (c, snap, nowMs, w, h) =>
            {
                SKPoint? tt = PresentationState.TowerVisible && towerEntry.Allowed && towerEntry.Sc == 1f
                    ? new SKPoint(16f + towerEntry.Ox, 18f + towerEntry.Oy) : null;
                viewerTiers.DrawOver(c, w, h, VpLayout.SessionBar, tt, snap, nowMs);
            },
        });

        // Onboard (2026-10-24): full-screen treatment - RPM arc, gear, speed, steering, pedals, driver plate, gaps.
        entries.Add(new Entry
        {
            Name = "onboard",
            Visible = static () => PresentationState.OnboardActive && PresentationState.IsRacing,
            Animating = () => PresentationState.OnboardK > 0.002f && PresentationState.OnboardK < 0.998f,
            Continuous = true,
            OnsetMs = 420,
            Paint = (c, snap, nowMs, w, h) => onboard.Draw(c, w, h, snap, nowMs, PresentationState.OnboardK),
        });

        // the radio card is drawn FIRST, i.e. underneath everything else: any big card that comes up
        // covers it instead of the other way round (Angelo 2026-10-24: "keep it out of the way")
        var radioE = entries.Find(e => e.Name == "radio");
        if (radioE != null) { entries.Remove(radioE); entries.Insert(0, radioE); }

        // 2026-10-26 (#234): rain on the lens - it is on the CAMERA, so it sits under every graphic (even the radio card).
        // Ticked in FrameNeeded; keeps presenting only while drops are on the lens or fading.
        lensEntry = new Entry
        {
            Name = "lensrain",
            Visible = static () => true,
            Animating = () => lensRain.Active,
            Continuous = false,
            Paint = (c, snap, nowMs, w, h) => lensRain.Draw(c),
        };
        entries.Insert(0, lensEntry);

        // 2026-10-26 (#235): the replay / live wipe covers everything, so it is drawn last; the small corner REPLAY tag
        // is static (it repaints with the rest, no presents of its own once faded in).
        wipeEntry = new Entry
        {
            Name = "replaywipe",
            Visible = () => replayWipe.Active(currentNowMs) || replayWipe.TagShowing || ReplayHideAnimating,
            Animating = () => replayWipe.Active(currentNowMs) || replayWipe.TagFading || ReplayHideAnimating,
            Continuous = false,
            OnsetMs = 120,
            Paint = (c, snap, nowMs, w, h) => { replayWipe.TagTop = ReplayTagTop(w); replayWipe.Draw(c, w, h, nowMs); },
        };
        entries.Add(wipeEntry);

        // 2026-10-27: visual pack graphics. Lens effects sit with the lens rain (under every graphic); the cards go
        // under the replay wipe, the break card and the bumper on top of the cards.
        int lensAt = entries.IndexOf(lensEntry) + 1;
        foreach (var (name, g) in vp.Graphics())
        {
            var gr = g;
            var e = new Entry
            {
                Name = name,
                Visible = () => gr.Active(currentNowMs),
                Animating = () => gr.Active(currentNowMs),
                Continuous = false,
                OnsetMs = 120,
                Paint = (c, snap, nowMs, w, h) => gr.Draw(c, w, h, nowMs),
            };
            vpEntries[name] = e;
            if (name == "vp_lens") entries.Insert(lensAt, e);
            // #337 (2026-10-27): the bumper goes UNDER the ticker and the session bar, which take their battle-mode look on top of it
            else if (name == "vp_bumper" && entries.FindIndex(x => x.Name == "ticker") is int ti && ti >= 0) entries.Insert(ti, e);
            else entries.Insert(entries.IndexOf(wipeEntry), e);
        }
        readVar = n => raceState.ReadDouble(n);
        vp.Log = Program.Log;
        ViewerFeed.Log = Program.Log;   // #494
        RaceControlBar.Log = Program.Log;   // #619
        ViewerLatest.Log = Program.Log;     // #608
        ApexHUD.Native.Overlays.OnboardFullRenderer.PlaceLog = Program.Log;   // #383
        ApexHUD.Native.Overlays.FeaturedCardRenderer.FadeLog = Program.Log;   // #428
        vp.CutToCockpit = CutToCockpit;

        SetAnchors();
        ApplyControl(0);
    }

    /// <summary>Where each graphic grows from when the control app resizes it.</summary>
    void SetAnchors()
    {
        foreach (var e in entries)
        {
            e.Anchor = e.Name switch
            {
                "tower" or "splash" => static (w, h) => new SKPoint(16f, 18f),
                "sessionbar" => static (w, h) => new SKPoint(w / 2f, 18f),
                "ticker" => static (w, h) => new SKPoint(0f, 110f),
                "featured" => static (w, h) => new SKPoint(w / 2f, h - 16f),
                "battle" => static (w, h) => new SKPoint(w / 2f, h - 8f),
                "precards" => static (w, h) => new SKPoint(w / 2f, h - 70f),
                "fastest" => static (w, h) => new SKPoint(w / 2f, h * 0.49f),
                "pit" => static (w, h) => new SKPoint(w / 2f, h * 0.152f),
                "story" => static (w, h) => new SKPoint(w, h / 2f),
                "custom" => static (w, h) => new SKPoint(w - 24f, 150f),
                "viewer" => static (w, h) => new SKPoint(w / 2f, 110f),
                _ => static (w, h) => new SKPoint(w / 2f, h / 2f),
            };
            if (e.Name == "battle") battleEntry = e;
        }
    }

    /// <summary>Re-apply the control app's settings - only when they changed.</summary>
    void ApplyControl(double nowMs)
    {
        int v = ControlState.Version;
        if (v == appliedControlVersion) return;
        appliedControlVersion = v;
        var s = ControlState.Current;
        foreach (var e in entries)
        {
            bool allowed = s.OnAir && (s.Mode != "lean" || ControlState.LeanSet.Contains(e.Name));
            if (e.Name == "custom") allowed &= s.CustomCards;
            if (e.Name == "lensrain") allowed &= s.LensRain;        // 2026-10-26 (#234)
            if (e.Name == "replaywipe") allowed &= s.ReplayWipes;   // 2026-10-26 (#235)
            if (e.Name.StartsWith("vp_", StringComparison.Ordinal)) allowed &= VpAllowed(e.Name, s.Visuals);
            if (e.Name == "racecontrol") allowed &= s.RaceControlBar;            // #619
            if (e.Name == "viewerlatest") allowed &= s.Viewer.KeepLatest;        // #608
            if (s.Elements.TryGetValue(e.Name, out var es))
            {
                allowed &= es.Enabled;
                e.Ox = es.X; e.Oy = es.Y; e.Sc = es.Scale;
            }
            else { e.Ox = 0; e.Oy = 0; e.Sc = 1f; }
            e.Allowed = allowed;
            e.Cache?.Dispose(); e.Cache = null;
        }
        var vs = s.Visuals;
        vp.Apply(new VpFlags
        {
            Weather = vs.WeatherCard, Night = vs.NightPalette && s.OnAir, Radar = false /* V3 not approved (round 2) */, Strategy = vs.StrategyBoard, LiveTag = vs.LiveTag,
            Bumper = vs.ReplayBumper, Stats = vs.StatsCards, YellowMap = vs.YellowMap, Break = vs.BreakCard, BreakMusic = vs.BreakMusic,
            LowerThirdSounds = vs.LowerThirdSounds, Pip = vs.PipOnboard, PipCamera = vs.PipCamera, Lens = vs.LensEffects,
            RaceControl = vs.PenaltyCard, PurpleBeep = vs.PurpleBeep, LensFov = (float)vs.LensFov, LensPitch = vs.LensPitch is double lp ? (float)lp : float.NaN,
            Elevated = new HashSet<string>(vs.Elevated, StringComparer.OrdinalIgnoreCase),
        });
        CornerTheme.Refined = s.CornerRefined;   // 2026-10-27 (#115 / #118)
        // 2026-10-29 (#494): viewer cards, the track fund and the broadcast theme
        ViewerFeed.Enabled = s.Viewer.Cards; ViewerFeed.CommentsOn = s.Viewer.Comments; ViewerFeed.BigAmount = s.Viewer.BigAmount; ViewerFeed.BigBits = s.Viewer.BigBits;
        ViewerTiers.LooksOn = s.Viewer.TierLooks; ViewerTiers.Tier2From = s.Viewer.Tier2From; ViewerTiers.Tier1From = s.Viewer.Tier1From; ViewerTiers.AutoThanks = s.Viewer.AutoThanks; ViewerTiers.SoundsOn = s.Viewer.TierSounds;
        RaceControlBar.On = s.RaceControlBar;                                         // #619
        ViewerLatest.KeepOn = s.Viewer.KeepLatest; ViewerLatest.PaidOnly = s.Viewer.LatestPaidOnly;   // #608
        ViewerFund.Enabled = s.Viewer.Fund; ViewerFund.Goal = s.Viewer.FundGoal; ViewerFund.Target = s.Viewer.FundTarget;
        themePick = s.Theme; themeByDate = s.ThemeByDate; themeDates = s.ThemeDates ?? new(); lastThemeCheckMs = -1e9;
        Theme.SetCustoms(s.CustomThemes);   // #601: the editor's themes (an edited theme on air updates straight away)
        StoryDirector.NoCardsInBattleWhenDriving = s.NoCardsInBattleWhenDriving;   // 2026-10-27 (#338)
        StoryDirector.MiniCardsInBattle = s.MiniCardsInBattle;                     // 2026-10-27 (#339, preview)
        StoryCards.Layout = StoryCards.ParseLayout(s.CardLayout);                  // 2026-10-27 (#349)
        StoryDirector.CardMs = s.StoryCardSeconds * 1000.0;
        StoryDirector.CooldownMs = s.StoryGapMinutes * 60000.0;
        UnitPrefs.Temperature = s.TempUnit;
        if (SceneTransitions.Mode != s.Transitions) { SceneTransitions.SetMode(s.Transitions); Program.Log($"class transitions: {s.Transitions}"); }
        if (Perf.Mode != s.Performance) { Perf.Mode = s.Performance; Program.Log($"performance: {s.Performance} (light now {Perf.Light})"); }
        controlBoostUntilMs = nowMs + 900;   // present the change (and any fade) right away
    }

    static bool VpAllowed(string name, VisualPackSettings v) => name switch
    {
        "vp_lens" => v.LensEffects, "vp_weather" => v.WeatherCard, "vp_radar" => false, "vp_yellowmap" => v.YellowMap,
        "vp_pip" => v.PipOnboard, "vp_racecontrol" => v.PenaltyCard, "vp_strategy" => v.StrategyBoard, "vp_stats" => v.StatsCards,
        "vp_live" => v.LiveTag, "vp_bumper" => v.ReplayBumper, "vp_break" => v.BreakCard,
        _ => false,
    };

    /// <summary>W10 option: cut the iRacing camera to a car's cockpit (replays / spectating, director off). Event-time only.</summary>
    void CutToCockpit(int carIdx)
    {
        try
        {
            var groups = raceState.CameraGroups();
            var g = groups.FirstOrDefault(x => x.Name.Contains("cockpit", StringComparison.OrdinalIgnoreCase))
                    ?? groups.FirstOrDefault(x => x.Name.Contains("chase", StringComparison.OrdinalIgnoreCase));
            if (g == null || !raceState.CarNumbersRaw().TryGetValue(carIdx, out var raw)) return;
            raceState.CamToCar(raw, g.Num);
            Program.Log($"visual pack: PiP camera cut to {g.Name} of car index {carIdx}");
        }
        catch (Exception ex) { Program.Log("visual pack: camera cut failed: " + ex.Message); }
    }

    /// <summary>One-shot requests from the control app (test cards), run on the render thread.</summary>
    void DrainRequests(RaceSnapshot snap, double nowMs)
    {
        while (ControlState.Requests.TryDequeue(out var r))
        {
            if (r == "outro") OutroDirector.Force(snap, nowMs);
            else if (r.StartsWith("vp:", StringComparison.Ordinal)) Program.Log("visual pack test " + r[3..] + ": " + vp.Test(r[3..], snap, nowMs));
            else if (r.StartsWith("custom:", StringComparison.Ordinal)) CustomCards.Requests.Enqueue(r["custom:".Length..]);
            else if (r == "latestplate") { ViewerLatest.Sample(); latestForceUntilMs = nowMs + 9000; Program.Log("latest plate test (9 s)"); }   // #608
            else if (r == "rcbar")   // #619: a spoken line, then a decision with a car
            {
                RaceControlBar.PushSpoken("[[0]]Track limits are being monitored at the Ford Chicanes.[[3.2]]Repeated offences will be penalised.[[end=6]]");
                var d = snap.Player ?? snap.CameraTarget ?? snap.Leader;
                RaceControlBar.Post(new RcMessage(RcCategory.Penalty, "L12", "", d?.CarNumber ?? "29", d != null ? ApexHUD.Native.Common.NameFormat.Broadcast(d) : "A. MORELL",
                    d?.ClassName ?? "GTP", d?.ClassColor ?? "", "TEST: DRIVE THROUGH PENALTY - TRACK LIMITS", d?.Incidents ?? 4, 17));
            }
            else if (Enum.TryParse<StoryKind>(r, true, out var k) && k != StoryKind.None)
                StoryDirector.Force(k, nowMs, k == StoryKind.WeatherWatch ? "HEATING" : "RAIN");
            controlBoostUntilMs = nowMs + 900;
        }
    }

    /// <summary>#619: where the race-control bar hangs: under the session bar plate (regular), under the ticker (battle), or
    /// under the onboard's corner / straight name; never into the radio column.</summary>
    void RcAnchor(int w, out float cx, out float top, out float regularK, out float maxRight)
    {
        bool onboard = PresentationState.OnboardActive && PresentationState.IsRacing && PresentationState.OnboardK > 0.5f;
        regularK = 0f;
        if (onboard) { cx = w / 2f; top = OnboardFullRenderer.PlaceBottom > 0f ? OnboardFullRenderer.PlaceBottom + 12f : 24f; }
        else if (VpLayout.TickerVisible) { cx = w / 2f; top = VpLayout.Ticker.Bottom; }
        else if (VpLayout.SessionBarVisible) { cx = VpLayout.SessionBar.MidX; top = VpLayout.SessionBar.Bottom; regularK = 1f; }
        else { cx = w / 2f; top = 18f; }
        maxRight = w;
        if (VpLayout.RadioVisible && VpLayout.Radio.Top < top + RaceControlBar.H + 8f && VpLayout.Radio.Bottom > top) maxRight = VpLayout.Radio.Left - 12f;
    }

    /// <summary>#611: every graphic that is on screen or about to come on, in 1080 units, into <see cref="camRects"/>
    /// (no allocation). Full-screen moments (the opener, the onboard view, tier 1, the roll, the outro) and a replay with
    /// the graphics stepped aside give nothing - the cams stay put under those.</summary>
    int CollectCamRects(RaceSnapshot snap, double nowMs)
    {
        var s = ControlState.Current;
        if (!s.OnAir || !snap.IsConnected) return 0;
        if (OutroDirector.FullScreen || ViewerTierRenderer.FullScreen || PresentationState.ReplayHideK > 0.5f) return 0;
        if (PresentationState.OnboardActive && PresentationState.IsRacing && PresentationState.OnboardK > 0.5f) return 0;
        if (snap.Phase == RacePhase.PreRace && StartingGridRenderer.MusicEndMs < 0) return 0;
        var d = camRects;
        int n = 0;
        void Add(SKRect r) { if (n < d.Length && r.Width > 1 && r.Height > 1) d[n++] = r; }
        if (towerEntry.Allowed && (PresentationState.TowerVisible || SceneDirector.SplashActive)) Add(VpLayout.Tower);
        if (VpLayout.SessionBarVisible)
        {
            var b = VpLayout.SessionBar; Add(b);
            Add(new SKRect(b.MidX - 70f, b.MidY - 70f, b.MidX + 70f, b.MidY + 70f));   // the watch
        }
        if (VpLayout.TickerVisible) Add(VpLayout.Ticker);
        if (StoryDirector.Visible) Add(StoryCards.Layout == CardLayout.Classic ? new SKRect(940f, 0f, 1920f, 1080f) : new SKRect(1280f, 150f, 1904f, 1076f));
        if (StoryDirector.MiniVisible) Add(StoryCards.MiniRect());
        if (VpLayout.FeaturedVisible) Add(VpLayout.Featured);
        if (VpLayout.BattleCardVisible) Add(VpLayout.BattleCard);
        if (VpLayout.BattleMapVisible) Add(VpLayout.BattleMap);
        if (VpLayout.RadioVisible) Add(VpLayout.Radio);
        if (VpLayout.CustomVisible) Add(VpLayout.Custom);
        if (snap.Phase == RacePhase.PreRace && StartingGridRenderer.CardsStartMs >= 0)
            Add(new SKRect((1920f - PreRaceCards.CardW) / 2f, 1080f - PreRaceCards.CardH * 1.2f - 70f, (1920f + PreRaceCards.CardW) / 2f, 1080f - 70f));
        var bar = VpLayout.SessionBar;
        if (viewerCards.Current != null) Add(new SKRect(bar.Left - 40f, bar.Bottom - 4f, bar.Right + 40f, bar.Bottom + 170f));
        if (viewerTiers.Visible)
        {
            Add(viewerTiers.UnderBounds(bar, new SKPoint(16f + towerEntry.Ox, 18f + towerEntry.Oy)));
            Add(viewerTiers.OverBounds(bar));
        }
        // the visual pack's cards (race control card, yellow map, weather, strategy ...) say where they are
        n += VpLayout.Claims(d.AsSpan(n), nowMs);
        // #619 the race-control bar (or where it is about to come down), #608 the latest plate (up or about to fade in)
        if (rcEntry is { Allowed: true })
        {
            if (!rcBar.LastRect.IsEmpty) Add(rcBar.LastRect);
            else if (RaceControlBar.Waiting && !rcBar.NextRect.IsEmpty) Add(rcBar.NextRect);
        }
        if (latestPlate.Active || latestWanted) Add(ViewerLatestPlate.RectFor(bar));
        return n;
    }

    /// <summary>#397 (2026-10-27): the REPLAY tag sits top-right, under the corner / straight label band (y 150), and
    /// drops below the battle card, the ticker or the battle map when one of them is in that corner.</summary>
    float ReplayTagTop(int w)
    {
        // the tag's right edge: the screen's right margin, or just left of the battle map when the map is up (the map
        // owns the top-right corner from y 172 in a battle; the battle card itself sits at the bottom)
        float r = VpLayout.BattleMapVisible ? VpLayout.BattleMap.Left - 12f : w - 24f;
        replayWipe.TagRight = r;
        float top = 150f, h = ReplayWipe.TagH, l = r - 160f;
        var rc = new SKRect(l, top, r, top + h);
        if (VpLayout.TickerVisible && rc.IntersectsWith(VpLayout.Ticker)) top = VpLayout.Ticker.Bottom + 10f;
        rc = new SKRect(l, top, r, top + h);
        if (VpLayout.RadioVisible && rc.IntersectsWith(VpLayout.Radio)) top = VpLayout.Radio.Bottom + 10f;
        return top;
    }

    void PaintEntry(Entry e, SKCanvas canvas, RaceSnapshot snap, double nowMs, int width, int height)
    {
        int save = canvas.Save();
        try
        {
            if (e.Ox != 0f || e.Oy != 0f || e.Sc != 1f)
            {
                var a = e.Anchor?.Invoke(width, height) ?? new SKPoint(width / 2f, height / 2f);
                canvas.Translate(e.Ox, e.Oy);
                if (e.Sc != 1f) { canvas.Translate(a.X, a.Y); canvas.Scale(e.Sc); canvas.Translate(-a.X, -a.Y); }
            }
            long pt0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float obk = e.HideOnOnboard ? PresentationState.CardsHiddenK : 0f;
            // #423: everything but the replay wipe / REPLAY tag fades away while a replay plays
            float rk = e.Name == "replaywipe" ? 0f : ReplayHideEased;
            float vis = (1 - obk) * (1 - rk);
            if (vis <= 0.005f) return;
            if (vis < 0.995f)
            {
                obLayer.Color = new SKColor(0, 0, 0, (byte)(255 * vis));
                var b = e.Bounds?.Invoke() ?? SKRect.Empty;
                if (b.IsEmpty) canvas.SaveLayer(obLayer); else canvas.SaveLayer(b, obLayer);   // just the graphic, not 4K
            }
            e.Paint(canvas, snap, nowMs, width, height);
            double paintMs = (System.Diagnostics.Stopwatch.GetTimestamp() - pt0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            e.CostMs += paintMs;
            if (e.Name == "grid") LastGridMs = paintMs;
            if (e.ProfSlot == -2) e.ProfSlot = Prof.Slot("g." + e.Name);   // per-graphic time for the intro watcher
            Prof.Add(e.ProfSlot, paintMs);
            e.CostN++;
        }
        catch (Exception ex) { Fail(e, "paint", ex, nowMs); }
        finally { canvas.RestoreToCount(save); }
    }

    /// <summary>Per-graphic paint cost since the last call, most expensive first ("tower 12.3"), then reset (2026-10-24).</summary>
    public string TakeCosts()
    {
        var parts = new List<(string, double)>();
        foreach (var e in entries)
        {
            if (e.CostN > 0) parts.Add((e.Name, e.CostMs / e.CostN));
            e.CostMs = 0; e.CostN = 0;
        }
        parts.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return string.Join(", ", parts.Take(5).Select(p => $"{p.Item1} {p.Item2:F1}"));
    }

    /// <summary>A graphic threw: log it (at most every 10 s), and after 30 errors switch only that graphic off.</summary>
    void Fail(Entry e, string where, Exception ex, double nowMs)
    {
        e.Fails++;
        if (nowMs - e.LastFailLogMs > 10000)
        {
            e.LastFailLogMs = nowMs;
            Program.Log($"{e.Name} {where} error #{e.Fails}: {ex.GetType().Name}: {ex.Message}");
        }
        if (e.Fails >= 30 && !e.Broken)
        {
            e.Broken = true;
            Program.Log($"{e.Name} switched off after {e.Fails} errors (the rest of the broadcast keeps running)");
        }
    }

    static bool SafeBool(Func<bool>? f, out Exception? err)
    {
        err = null;
        if (f == null) return false;
        try { return f(); } catch (Exception ex) { err = ex; return false; }
    }

    /// <summary>Live status for the control app (built only when it asks, off the render thread).</summary>
    public object Describe(RaceStateService rs)
    {
        var snap = currentSnap;
        var els = new List<object>(entries.Count);
        foreach (var e in entries) els.Add(new { name = e.Name, allowed = e.Allowed, showing = e.LastVisible });
        int cars = 0;
        var classes = snap.ClassLeaderboard;
        for (int i = 0; i < classes.Count; i++) cars += classes[i].Drivers.Count;
        return new
        {
            connected = snap.IsConnected,
            live = rs.IsSessionLive,
            drivingLive = DrivingState.DrivingLive,   // 2026-10-29: the go-live Hue watcher picks 'Stream - driving' vs 'Stream - talking'
            intro = OpenerAudio.IsPlaying,     // 2026-10-25: the race engineer waits with race control while this is true
            demo,
            track = snap.TrackDisplayName,
            session = snap.SessionTypeName,
            phase = snap.Phase.ToString(),
            cars,
            classes = classes.Count,
            story = StoryDirector.Current.ToString(),
            presents = presentIndex,
            canvas = new { w = lastCanvasW, h = lastCanvasH },
            elements = els,
            viewer = new { queued = viewerCards.Queued, showing = viewerCards.Current?.Kind.ToString(), waiting = viewerCards.WhyWaiting, theme = Theme.Current.Id, fund = ViewerFund.Current },
        };
    }
    int lastCanvasW, lastCanvasH;

    RaceSnapshot? cannedCache;

    RaceSnapshot Resolve()
    {
        if (!demo && !raceState.Current.IsConnected)
            return RaceSnapshot.Disconnected;   // never put the canned demo field on air in a real session
        if (demo)
            // The canned snapshot is static — build it ONCE and reuse it. Building
            // it every tick (≈125 Hz incl. idle) would allocate a full snapshot per
            // call and churn the GC mid-animation, breaking requirement C. The
            // --demo-grid switch forces the PreRace phase so the grid + hero cycle
            // are visible without iRacing being parked on the grid.
            return cannedCache ??= demoGrid
                ? CannedSnapshot.Build() with { Phase = RacePhase.PreRace }
                : CannedSnapshot.Build();
        return raceState.Current;       // the service caches Current; no per-call alloc
    }

    /// <summary>
    /// Decide whether THIS frame must be presented. Called every host loop tick
    /// (≈125 Hz including idle). Advances the shared visibility + rotation statics
    /// the same way <see cref="App"/> does on the WPF render thread, records each
    /// entry's paint flag for <see cref="Render"/>, and returns the on-demand
    /// verdict. Allocation-free.
    /// </summary>
    public bool FrameNeeded(double nowMs)
    {
        currentNowMs = nowMs;
        var snap = Resolve();
        currentSnap = snap;
        NativeOverlayHost.DeepIdle = !snap.IsConnected && !demo;
        App.SnapshotOverride = snap;
        ApplyControl(nowMs);
        DrainRequests(snap, nowMs);
        PresentationState.Tick(snap);
        TickOnboard(snap, nowMs);
        SceneDirector.Tick(snap);
        TickImmersion(snap, nowMs);
        // spotlight.Tick(snap, nowMs);   // retired 2026-10-25 (see the "spotlight" entry)

        // replay rewound / skipped: the diary no longer lines up - start it fresh,
        // and keep the event callouts quiet for a few seconds (everything "changed" at once)
        int jumps = raceState.ReplayJumps;
        if (jumps != seenReplayJumps)
        {
            seenReplayJumps = jumps;
            RaceDiary.Reset();
            PresentationState.QuietUntilTicks = Environment.TickCount64 + 8000;
            Program.Log("replay jump: race diary reset, callouts quiet for 8 s");
        }
        // 2026-10-24: which iRacing class IDs / short names became which broadcast class - so the
        // ClassDetection ID table can be checked against a real session.
        var classMap = raceState.ClassMapReport;
        if (!ReferenceEquals(classMap, seenClassMap) && classMap != seenClassMap)
        {
            seenClassMap = classMap;
            if (classMap.Length > 0) Program.Log("classes: " + classMap);
        }
        PresentationState.CalloutActive = fastest.IsActive;
        RaceDiary.Tick(snap);
        TrackTerrain.Prepare(snap);                   // 2026-10-25: the 3D track card's ground, built in the background
        TrackSpeeds.Tick(snap);                       // 2026-10-25: corner / straight speeds for the track card's lap walk
        StoryDirector.Tick(snap, nowMs, PresentationState.IsBattleActive);
        onboard.Feed(snap, nowMs);   // the onboard view keeps recording our inputs while it's off screen (2026-10-25)
        if (nowMs - lastStoryWhyMs > 30000 && snap.Phase == RacePhase.Broadcast)
        {
            lastStoryWhyMs = nowMs;
            Program.Log($"story: {(StoryDirector.Visible ? "showing " + StoryDirector.Current : "waiting - " + StoryDirector.WhyNot)}");
        }
        OutroDirector.Tick(snap, nowMs);
        viewerCards.Tick(snap, nowMs);
        viewerTiers.Tick(snap, nowMs);   // #576 (takes the events only while the tier looks are on)   // #494: next viewer card when the screen is clear
        if (nowMs - lastThemeCheckMs > 60000)   // #494: the theme (auto by date is checked once a minute)
        {
            lastThemeCheckMs = nowMs;
            string th = Theme.Resolve(themePick, themeByDate, themeDates.Halloween, themeDates.Christmas, themeDates.July4, DateTime.Now);
            if (th != Theme.Active.Id) { Theme.Set(th); Program.Log("theme: " + Theme.Active.Name); }
        }
        // #601: the theme editor's live preview (on until the editor stops it, or 10 minutes after its last change)
        int pv = ControlState.PreviewVersion;
        if (pv != seenThemePreview || (Theme.Previewing && DateTime.UtcNow > ControlState.PreviewUntilUtc))
        {
            seenThemePreview = pv;
            var spec = DateTime.UtcNow <= ControlState.PreviewUntilUtc ? ControlState.PreviewTheme : null;
            Theme.Preview(spec == null ? null : ThemeDef.FromSpec(spec));
            controlBoostUntilMs = nowMs + 900;
        }
        try { CustomCards.Tick(snap, nowMs, StoryDirector.Visible || StoryDirector.MiniVisible || OutroDirector.FullScreen); }
        catch (Exception ex) { if (nowMs - lastCustomFailMs > 10000) { lastCustomFailMs = nowMs; Program.Log("custom cards: " + ex.Message); } }
        GlobeIntro.Prepare(snap);   // build this track's globe in the background long before the opener

        ulong sig = Signature(snap);
        if (!sigInit || sig != lastSig)
        {
            sigInit = true;
            lastSig = sig;
            lastChangeAtMs = nowMs;
        }
        bool contentFresh = (nowMs - lastChangeAtMs) < ContentSettleMs;

        // #601: a theme with particles / a light sweep keeps ~30 presents a second so the motion stays smooth (Default: never)
        if (Theme.Animated && nowMs - lastThemeFrameMs >= 33) { lastThemeFrameMs = nowMs; controlBoostUntilMs = Math.Max(controlBoostUntilMs, nowMs + 1); }
        bool need = SceneDirector.SplashActive || nowMs < controlBoostUntilMs || vp.Night.Moving
                    || IntroWatch.Wants(snap, lastCanvasW, lastCanvasH);   // frames for the globe's dress rehearsal

        foreach (var e in entries)
        {
            bool vis = false;
            if (e.Allowed && !e.Broken)
            {
                vis = SafeBool(e.Visible, out var verr);
                if (verr != null) Fail(e, "visible", verr, nowMs);
            }
            if (e.FlipAtMs < 0 || vis != e.LastVisible)
            {
                e.FlipAtMs = nowMs;
                e.LastVisible = vis;
            }
            bool onset = (nowMs - e.FlipAtMs) < e.OnsetMs;
            bool animating = false;
            if (e.Allowed && !e.Broken && e.Animating != null)
            {
                animating = SafeBool(e.Animating, out var aerr);
                if (aerr != null) Fail(e, "animating", aerr, nowMs);
            }

            // Paint the overlay while it is visible OR still mid fade-in/out OR
            // self-reporting an active animation (self-gated callouts), so the
            // shared canvas is composited correctly (and a fade-out's final
            // empty frame clears its pixels).
            e.PaintThisFrame = vis || onset || animating;
            if (!e.PaintThisFrame) continue;

            if (vis && e.Continuous) need = true;        // always-animating while visible
            else if (onset) need = true;                 // entrance / exit fade
            else if (animating) need = true;             // self-gated callout mid-animation
            else if (vis && contentFresh) dataOnly = true;   // visible + data recently moved
        }

        // 2026-10-24 (perf): live data moves every tick (the clock, gaps), which kept the 4K overlay
        // redrawing ~55x a second even when nothing was animating. Plain data updates now redraw at
        // most 20x a second; animations and transitions still get the full 60.
        if (!need && dataOnly && nowMs - lastDataPresentMs >= Perf.DataRedrawMs) { need = true; lastDataPresentMs = nowMs; }
        dataOnly = false;
        return need;
    }

    /// <summary>
    /// Composite every overlay flagged this frame onto the (already-cleared)
    /// shared canvas. Called exactly once per actual present.
    /// </summary>
    public void Render(SKCanvas canvas, int width, int height, double nowMs)
    {
        // 4K stream (Angelo 2026-10-19): every overlay is authored for a 1080p window. On a
        // bigger window (the 4K streaming screen) scale the whole layout up uniformly, so it
        // looks identical - just sharper - instead of shrinking to a quarter of the screen.
        float uiScale = height > 1300 ? height / 1080f : 1f;
        LastGridMs = 0;
        Theme.Clock = nowMs;   // #601: the clock for themed glass helpers that have no time of their own
        if (uiScale == 1f) RenderCore(canvas, width, height, nowMs);
        else
        {
            int save = canvas.Save();
            canvas.Scale(uiScale);
            RenderCore(canvas, (int)Math.Round(width / uiScale), (int)Math.Round(height / uiScale), nowMs);
            canvas.RestoreToCount(save);
        }
        // the globe's dress rehearsal, one offscreen frame per present, in the quiet minute before the opener
        try { IntroWatch.Rehearse(currentSnap, (int)Math.Round(width / uiScale), (int)Math.Round(height / uiScale), uiScale, width, height); }
        catch (Exception ex) { Program.Log("globe rehearsal: " + ex.Message); }
    }

    readonly SKPaint obLayer = new();
    string? wipeWhy;
    double lastVpFailMs = -1e9;

    /// <summary>2026-10-26 (#234 / #235): advance the lens rain and watch the replay for a wipe (render thread).</summary>
    void TickImmersion(RaceSnapshot snap, double nowMs)
    {
        try
        {
            if (lensEntry is { Allowed: true, Broken: false })
                lensRain.Tick(snap, nowMs, lastCanvasW > 0 ? lastCanvasW : 1920, lastCanvasH > 0 ? lastCanvasH : 1080);
        }
        catch (Exception ex) { if (lensEntry != null) Fail(lensEntry, "tick", ex, nowMs); }
        try
        {
            string? why = replayWipe.Tick(snap, nowMs, wipeEntry is { Allowed: true, Broken: false });
            if (why != null)
            {
                Program.Log("replay wipe: " + why);
                StorySound.PlayBroadcast("replay_wipe");   // the Grandstand stinger; only ever heard in replays
            }
            wipeWhy = why;
        }
        catch (Exception ex) { if (wipeEntry != null) Fail(wipeEntry, "tick", ex, nowMs); }
        // 2026-10-30 (#611): the graphics on screen for the cam placer, every 150 ms (OBS is driven from its own thread)
        if (nowMs - lastCamCollectMs >= 150)
        {
            lastCamCollectMs = nowMs;
            try
            {
                if (ControlState.Current.Cams.KeepClear)
                {
                    long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    int n = CollectCamRects(snap, nowMs);
                    double ms = System.Diagnostics.Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
                    ObsCams.Publish(camRects.AsSpan(0, n), ms);
                }
            }
            catch (Exception ex) { if (nowMs - lastVpFailMs > 10000) { lastVpFailMs = nowMs; Program.Log("cams: " + ex.Message); } }
        }
        // 2026-10-28 (#487): highlight markers (start / finish / your passes / pit stops; renderers add their own)
        try { HighlightLog.Enabled = ControlState.Current.Highlights; HighlightLog.Note(snap, PresentationState.IsBattleActive); } catch { }
        // 2026-10-28 (#488): the OBS scene that fits what's on screen (the cams move out of the graphics' way)
        try { ObsMarkers.WantScene(ObsSceneFor(snap), (long)nowMs); } catch { }
        // 2026-10-27: visual pack (decides what plays and feeds it; allocation-free between events)
        try
        {
            var ob = snap.Onboard;
            var ctx = new VpContext
            {
                BattleActive = PresentationState.IsBattleActive,
                Opponent = PresentationState.IsBattleActive ? battle.Opponent : null,
                RadioActive = radioPopup.IsActive(nowMs),
                WipeFired = wipeWhy,
                TvCamera = LensRain.IsTvCamera(ob.GroupOnlyName, ob.CamGroupName),
                CameraKey = ob.CamGroup * 4099 + ob.CamCarIdx,
                ReadVar = readVar,
                CameraFree = ReplayState.IsReplay && !ControlState.Current.Director,
            };
            StoryCards.RadioOn = ctx.RadioActive;   // #517: story cards take their split layout around the radio card
            vp.Tick(snap, nowMs, ctx);
            // 2026-10-27 round 2: where the live graphics are (the cards place themselves around them), and #337: the
            // session bar takes its battle-mode look (full width + ticker) while the replay bumper plays
            VpLayout.SessionBarVisible = PresentationState.BarVisible && PresentationState.OnboardK < 0.5f;
            VpLayout.TickerVisible = StandingsTickerRenderer.ShouldShow && PresentationState.OnboardK < 0.5f;
            VpLayout.BattleStyle = PresentationState.BarFullWidth;
            VpLayout.TowerVisible = PresentationState.TowerVisible;
            if (!PresentationState.IsBattleActive) VpLayout.BattleMapVisible = false;
            // round 3: the rest of the live graphics, so the strategy board, the BACK TO LIVE card and the lens effects
            // find free space (Angelo: "can't ever block any other element")
            VpLayout.BattleCardVisible = PresentationState.IsBattleActive && battleSlide < 0.995f;
            VpLayout.StoryVisible = StoryDirector.Visible;
            VpLayout.FeaturedVisible = PresentationState.FeaturedVisible && PresentationState.OnboardK < 0.5f;
            VpLayout.CustomVisible = CustomCards.Visible;
            if (CustomCards.Visible) VpLayout.Custom = new SKRect(1920f - 24f - CustomCards.CardW - 40f, 150f, 1920f - 24f, 150f + CustomCards.Height);
            VpLayout.OnboardVisible = PresentationState.OnboardActive && PresentationState.IsRacing && PresentationState.OnboardK > 0.02f;
            PresentationState.BumperBattleStyle = vp.Bumper.Active(nowMs) && PresentationState.IsRacing;
            // #619 / #608: the race-control bar and the latest plate (the cards and the cam placer keep clear of them)
            bool rcAllowed = rcEntry is { Allowed: true, Broken: false };
            if (!rcAllowed && (RaceControlBar.Waiting || rcBar.IsActive(nowMs))) rcBar.Reset();   // off air / Lean: dropped, not saved up
            bool rcUp = rcAllowed && (rcBar.IsActive(nowMs) || RaceControlBar.Waiting);
            RaceControlBar.Up = rcUp;
            VpLayout.RcBar = rcBar.LastRect; VpLayout.RcBarVisible = rcUp && !rcBar.LastRect.IsEmpty;
            bool viewerBusy = viewerCards.Current != null || viewerTiers.Current != null || ViewerTierRenderer.FullScreen;
            bool clear = ViewerCardsRenderer.ScreenClear(snap, out _, false) && !SceneDirector.SplashActive && !OutroDirector.FullScreen;
            latestWanted = latestEntry is { Allowed: true, Broken: false } && ViewerFeed.Enabled && !viewerBusy && (clear || nowMs < latestForceUntilMs);
            latestPlate.Tick(latestWanted, nowMs);
            VpLayout.LatestPlate = ViewerLatestPlate.RectFor(VpLayout.SessionBar); VpLayout.LatestPlateVisible = latestPlate.Active;
        }
        catch (Exception ex) { if (nowMs - lastVpFailMs > 10000) { lastVpFailMs = nowMs; Program.Log("visual pack: " + ex.Message); } }
    }

    /// <summary>Onboard shot detection, held 0.6 s on / 0.35 s off so camera flicks don't flash the graphics;
    /// eases <see cref="PresentationState.OnboardK"/> toward it (~0.4 s).</summary>
    // ---- #423: the overlay steps aside for replays (behind live in a live session, or the director's slow-mo) ----
    double replayHideLastMs = -1;
    /// <summary>Smoothstepped <see cref="PresentationState.ReplayHideK"/> (out in ~0.35 s, back in ~1.1 s).</summary>
    static float ReplayHideEased => SmoothK(PresentationState.ReplayHideK);
    static float SmoothK(float x) => x * x * (3 - 2 * x);
    bool ReplayHideAnimating => PresentationState.ReplayHideK > 0.002f && PresentationState.ReplayHideK < 0.998f;

    void TickReplayHide(double nowMs)
    {
        bool want = ReplayWipe.BehindLiveNow || ReplayDirector.SlowMoActive;
        double dt = replayHideLastMs < 0 ? 16 : Math.Clamp(nowMs - replayHideLastMs, 0, 100);
        replayHideLastMs = nowMs;
        float k = PresentationState.ReplayHideK;
        k = want ? Math.Min(1f, k + (float)(dt / 350.0)) : Math.Max(0f, k - (float)(dt / 1100.0));
        PresentationState.ReplayHideK = k;
    }

    void TickOnboard(RaceSnapshot snap, double nowMs)
    {
        TickReplayHide(nowMs);
        PresentationState.TickCamera(snap, nowMs);   // #510: new cards wait ~1.6 s after any camera cut
        bool raw = snap.IsConnected && snap.Onboard.IsOnboard && PresentationState.IsRacing;
        if (raw) { onboardOffSinceMs = -1; if (onboardRawSinceMs < 0) onboardRawSinceMs = nowMs; }
        else { onboardRawSinceMs = -1; if (onboardOffSinceMs < 0) onboardOffSinceMs = nowMs; }
        bool active = PresentationState.OnboardActive;
        if (!active && raw && nowMs - onboardRawSinceMs >= 600) active = true;
        if (active && !raw && nowMs - onboardOffSinceMs >= 350) active = false;
        PresentationState.OnboardActive = active;
        double dt = onboardLastMs < 0 ? 16 : Math.Clamp(nowMs - onboardLastMs, 0, 100);
        onboardLastMs = nowMs;
        float k = PresentationState.OnboardK, target = active ? 1f : 0f;
        k += (target - k) * (float)Math.Min(1, dt / 130.0);
        if (Math.Abs(target - k) < 0.002f) k = target;
        PresentationState.OnboardK = k;
        // #510: the cards hide straight away on a cut to an onboard (fast, ~60 ms), and come back with the onboard look
        PresentationState.OnboardPending = raw && !active;
        float hk = PresentationState.CardHideK, ht = raw || active ? 1f : 0f;
        hk += (ht - hk) * (float)Math.Min(1, dt / (ht > hk ? 60.0 : 130.0));
        if (Math.Abs(ht - hk) < 0.002f) hk = ht;
        PresentationState.CardHideK = hk;
    }

    // ---- 2026-10-27 (#44 "first-show timing tower hitch"): the tower's one-off work done before it first appears ----
    double towerWarmCheckMs = double.NegativeInfinity;
    Task? towerPrewarm; string towerPrewarmKey = "";

    /// <summary>Every 5 s with a field: decode the field's pictures + JIT the tower in the background
    /// (<see cref="TimingTowerRenderer.Prewarm"/> / <see cref="TimingTowerRenderer.WarmCode"/>); once that is done and
    /// while the tower is still hidden (and no opener / transition is playing), a no-draw layout pass
    /// (<see cref="TimingTowerRenderer.WarmLayout"/>) and on the next frame one invisible draw
    /// (<see cref="TimingTowerRenderer.WarmGpu"/>), both on this thread. Logged once per field.</summary>
    void WarmTower(SKCanvas canvas, RaceSnapshot snap, double nowMs, int height)
    {
        if (towerGpuDue)
        {
            // the frame after WarmLayout: the invisible GPU draw (textures, glyph atlas, fade layer, blur)
            towerGpuDue = false;
            if (PresentationState.TowerVisible || OpenerAudio.IsPlaying || SceneDirector.SplashActive || !snap.IsConnected) return;
            long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
            tower.WarmGpu(canvas, 16f, 18f, height - 34f, snap, nowMs);
            Program.Log($"tower warm-up: GPU draw {System.Diagnostics.Stopwatch.GetElapsedTime(g0).TotalMilliseconds:F1} ms (tower hidden)");
            return;
        }
        if (nowMs - towerWarmCheckMs < 5000 || !snap.IsConnected || snap.ClassLeaderboard.Count == 0) return;
        towerWarmCheckMs = nowMs;
        if (towerPrewarm is { IsCompleted: false }) return;
        if (tower.GpuWarmFor(snap)) return;
        string key = TimingTowerRenderer.FieldKey(snap);
        if (towerPrewarm == null || towerPrewarmKey != key)
        {
            towerPrewarmKey = key;
            var s = snap;
            towerPrewarm = Task.Run(() =>
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                TimingTowerRenderer.WarmCode(s);
                tower.Prewarm(s);
                Program.Log($"tower warm-up: pictures + code ready in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms (background)");
            });
            return;
        }
        if (PresentationState.TowerVisible || OpenerAudio.IsPlaying || SceneDirector.SplashActive) return;
        long l0 = System.Diagnostics.Stopwatch.GetTimestamp();
        tower.WarmLayout(snap, nowMs, height - 34f);      // this frame: blobs / layout on a no-draw canvas (CPU only)
        Program.Log($"tower warm-up: layout {System.Diagnostics.Stopwatch.GetElapsedTime(l0).TotalMilliseconds:F1} ms (tower hidden)");
        towerGpuDue = true;                                // next frame: the GPU part
    }
    bool towerGpuDue;

    // ---- #519 (2026-10-29, Angelo: "free up space" on the RTX 3080 Ti, which runs out in VR): Grandstand may keep up to
    // 1.5 GB of pictures on the card (Skia never hands it back by itself) and the intro's globe pins ~235 MB more. Once the
    // race is under way: every 20 s, the globe pictures go back to plain memory (re-pinned by the next intro's warm-up)
    // and Skia drops pictures nothing has drawn for 2 minutes. Never during the opener, a transition or the outro.
    double gpuDietMs = double.NegativeInfinity;
    bool gpuDietLogged;
    void GpuDiet(RaceSnapshot snap, double nowMs)
    {
        if (nowMs - gpuDietMs < 20000) return;
        gpuDietMs = nowMs;
        var ctx = ApexHUD.Native.Overlays.GpuContext.Current;
        if (ctx == null) return;
        // 2026-10-29 (Angelo: "make it stop growing now"): Grandstand's picture memory grew ~0.4 GB in 3 minutes during a
        // race and pushed the RTX 3080 Ti over its limit (VR stutters). While a session is on (practice too, not only
        // races) and no big show is playing, the cache is capped at 384 MB; the 1.5 GB allowance comes back for the
        // intro / splash / outro, which need it.
        bool show = OpenerAudio.IsPlaying || SceneDirector.SplashActive || OutroDirector.FullScreen;
        // 2026-10-29 (night): from the race start too (was: only after lap 1) - the start is when the 3080 Ti is busiest in VR,
        // and the lap-1 rule made the limit flap between 384 MB and 1.5 GB (each drop = a big purge, each rise = regrowth)
        // 2026-10-30 (Angelo: slides and class transitions choppy): 384 MB was smaller than what a 4K race screen at Le Mans
        // draws, so Skia threw pictures out and re-uploaded them every frame (tower 40-70 ms, session bar ~25 ms a frame,
        // the overlay at 4-8 fps), and flapping the limit around every splash purged everything again each time. Now one
        // steady limit for the whole session - 1 GB, room for the race screen + transitions - and only pictures unused for
        // 2 minutes are dropped (30 s also dropped the story-card pictures between cards).
        _ = show;
        bool lean = snap.IsConnected;
        // 2026-10-30 (#625, VR lag with the 3080 Ti's memory pinned at 11.9 / 12.3 GB): a race at Le Mans peaked at ~550 MB
        // of pictures (gpu diet log), so 640 MB keeps the whole race screen + transitions cached (no re-uploads) and hands
        // back the headroom 1 GB allowed; unused pictures go after 60 s instead of 2 min.
        long want = lean ? 640L * 1024 * 1024 : 1536L * 1024 * 1024;
        if (ctx.GetResourceCacheLimit() != want)
        {
            ctx.SetResourceCacheLimit(want);
            Program.Log($"gpu diet: picture memory limit {want / (1024 * 1024)} MB");
        }
        if (!lean) return;
        try
        {
            ctx.GetResourceCacheUsage(out _, out long before);
            bool globe = GlobeGT.ReleaseGpu();
            ctx.PurgeUnusedResources(60_000);
            ctx.GetResourceCacheUsage(out _, out long after);
            if (globe || !gpuDietLogged || before - after > 64L * 1024 * 1024)
            {
                gpuDietLogged = true;
                Program.Log($"gpu diet: picture memory {before / (1024 * 1024)} -> {after / (1024 * 1024)} MB{(globe ? ", globe pictures released" : "")}");
            }
        }
        catch (Exception ex) { Program.Log("gpu diet: " + ex.Message); }
    }

    // ---- #505 (2026-10-29): the Michelin pit card's pictures decoded ahead and given an invisible GPU draw. 2026-10-30: moved
    // to PitCardWarm (Overlays) - drawn the way the card draws them and touched every minute, so the GPU diet's 2-minute
    // purge no longer throws them away before the card comes in. ----
    void WarmPitCard(SKCanvas canvas, RaceSnapshot snap, double nowMs) => PitCardWarm.Tick(canvas, snap, nowMs, Program.Log);

    // ---- #644 (2026-10-31): the face cam drawn by Grandstand ----
    float FaceCamH => ControlState.Current.FaceCam.Width * 9f / 16f;
    /// <summary>The cam is on screen (switched on + a live picture) - the tower then leaves its rows to it.</summary>
    bool FaceCamShown => ControlState.Current.FaceCam.On && FaceCamFeed.AgeMs < 1600 && !PresentationState.OnboardActive;
    /// <summary>Angelo 10-31: the camera is the bottom of the timing tower, so it comes and goes WITH the tower - only while
    /// racing (green flag on; never in the intro / pre-race cards / post-race results), never in a battle, an onboard, a
    /// replay with the graphics stepped aside, or a full-screen viewer moment.</summary>
    static bool FaceCamRuleOn => PresentationState.TowerVisible && PresentationState.ReplayHideK < 0.5f
                                 && !ViewerTierRenderer.FullScreen && !OutroDirector.FullScreen;
    float faceCamVisK; double faceCamLastMs = -1;

    static readonly SKPaint faceCamImgPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
    static readonly SKPaint faceCamShadow = new()
    {
        IsAntialias = true, Color = new SKColor(0, 0, 0, 110),
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f),
    };

    void PaintFaceCam(SKCanvas c, double nowMs, int w, int h)
    {
        var img = FaceCamFeed.Image();
        if (img == null) return;
        float cw = ControlState.Current.FaceCam.Width, ch = FaceCamH;
        // Angelo 10-31: never moves - the camera's bottom edge IS the tower's bottom edge (h-16), always
        float x = 16f, y = h - 16f - ch;
        // fade out over the last 0.6 s of a stale feed (the guard switched the cam off in OBS)
        double age = FaceCamFeed.AgeMs;
        float a = age < 1000 ? 1f : (float)Math.Clamp(1 - (age - 1000) / 600.0, 0, 1);
        // fades in / out with the tower (~0.25 s), in place - it never slides
        double dt = faceCamLastMs < 0 ? 16 : Math.Clamp(nowMs - faceCamLastMs, 0, 100);
        faceCamLastMs = nowMs;
        float target = FaceCamRuleOn ? 1f : 0f;
        faceCamVisK += (target - faceCamVisK) * (float)Math.Min(1, dt / 90.0);
        if (Math.Abs(target - faceCamVisK) < 0.004f) faceCamVisK = target;
        a *= faceCamVisK;
        if (a <= 0.003f) return;
        var dst = new SKRect(x, y, x + cw, y + ch);
        // fill the 16:9 box (crop, never stretch)
        float sr = img.Width / (float)img.Height, dr = cw / ch;
        var src = sr > dr
            ? new SKRect((img.Width - img.Height * dr) / 2f, 0, (img.Width + img.Height * dr) / 2f, img.Height)
            : new SKRect(0, (img.Height - img.Width / dr) / 2f, img.Width, (img.Height + img.Width / dr) / 2f);
        int save = a < 1f ? c.SaveLayer(new SKPaint { Color = new SKColor(255, 255, 255, (byte)(255 * a)) }) : c.Save();
        // square edges, no shadow: flush with the tower panel's sides and bottom line
        c.ClipRect(dst, SKClipOperation.Intersect, true);
        c.DrawImage(img, src, dst, faceCamImgPaint);
        c.RestoreToCount(save);
    }

    /// <summary>The grid/opener entry's paint time this frame (ms) - for the intro watcher.</summary>
    public static double LastGridMs;

    void RenderCore(SKCanvas canvas, int width, int height, double nowMs)
    {
        presentIndex++;
        lastCanvasW = width; lastCanvasH = height;
        if (lastPresentNowMs >= 0)
        {
            double gap = nowMs - lastPresentNowMs;
            if (gap > 1 && gap < 100)
                presentIntervalMs += 0.1 * (gap - presentIntervalMs); // EMA toward the real refresh interval
        }
        lastPresentNowMs = nowMs;

        // Bounded per-frame work (requirement B): while the transition splash is
        // fully opaque it completely covers the tower lane, so painting the tower
        // underneath is wasted GPU. Skip it then — faithful, since the tower is
        // invisible behind an opaque splash. During the fade in/out the splash is
        // translucent, so the tower is drawn and shows through, as it should.
        double videoNowMs = SplashNowMs(nowMs);
        // Wipe transition (2026-10-18): the tower is hidden only while the splash
        // covers the WHOLE lane; during the wipe in/out it must paint underneath.
        bool splashOpaque = SceneDirector.SplashActive && SceneDirector.SplashFullyCovers(videoNowMs);

        var snap = currentSnap;
        WarmTower(canvas, snap, nowMs, height);   // 2026-10-27 (#44): first-show hitch
        WarmPitCard(canvas, snap, nowMs);           // #505: the Michelin pit card's pictures, ahead of time
        GpuDiet(snap, nowMs);                       // #519: give back graphics memory the race doesn't need
        int nightSave = vp.Night.Begin(canvas);   // 2026-10-27 (V2): night grade over every graphic (nothing by day / when off)
        // While the splash wipes OUT it uncovers the tower, which must show the
        // NEW board live - so stop replaying the frozen (old-board) picture then.
        bool wipingOut = SceneDirector.SplashActive && SceneDirector.SplashReveal(videoNowMs).Top > 0f;
        bool transition = SceneDirector.SplashActive && !wipingOut;
        if (!transition)
            foreach (var e in entries)
                if (e.Cache != null) { e.Cache.Dispose(); e.Cache = null; }   // #428: no picture survives to a later wipe
        foreach (var e in entries)
        {
            if (!e.PaintThisFrame) continue;
            if (splashOpaque && ReferenceEquals(e, towerEntry)) continue;

            // Requirement B: during a transition the splash is the only overlay that
            // must keep animating. Freeze each heavy cacheable overlay to an SKPicture
            // recorded once at transition start, then replay it (cheap, no per-present
            // allocation) so the splash gets a clean frame budget. Live rendering
            // resumes the moment the transition ends.
            // 2026-10-27 (#428, Angelo: "that clear driver card thing is still happening"): the frozen picture was taken
            // ONCE at the start of a transition - often while the card (or the onboard / replay fade around it) was mid-fade,
            // so a see-through card was shown for the whole wipe; a picture left from an earlier wipe could even come back.
            // Now: never freeze anything that is fading (whole-overlay fade or the graphic's own), else paint it live.
            bool fading = (e.HideOnOnboard && PresentationState.CardsHiddenK > 0.005f)
                          || (e.Name != "replaywipe" && ReplayHideEased > 0.005f)
                          || (e.CacheOk != null && !e.CacheOk());
            if (transition && e.Cacheable && !fading)
            {
                if (e.Cache == null && !e.Broken)
                {
                    using var rec = new SKPictureRecorder();
                    var rc = rec.BeginRecording(new SKRect(0, 0, width, height));
                    PaintEntry(e, rc, snap, nowMs, width, height);
                    e.Cache = rec.EndRecording();
                }
                if (e.Cache != null) canvas.DrawPicture(e.Cache);
                continue;
            }
            if (e.Cache != null) { e.Cache.Dispose(); e.Cache = null; }
            PaintEntry(e, canvas, snap, nowMs, width, height);
        }
        NightPalette.End(canvas, nightSave);

        // Transition playback health (2026-10-18): one log line per clip - frames
        // shown on time vs held (decode-ahead behind), and slow presents (>25 ms
        // gaps) while the clip played. Lets us tell a loader stall from a render hitch.
        if (SceneDirector.SplashActive)
        {
            if (!clipStatsArmed) { clipStatsArmed = true; clipSlowPresents = 0; }
            if (lastClipPresentMs >= 0 && nowMs - lastClipPresentMs > 25) clipSlowPresents++;
            lastClipPresentMs = nowMs;
        }
        else if (clipStatsArmed)
        {
            clipStatsArmed = false;
            lastClipPresentMs = -1;
            Program.Log($"transition clip done: frames on-time={ApexHUD.Native.Overlays.FrameStreamer.StatOnTime} " +
                        $"held={ApexHUD.Native.Overlays.FrameStreamer.StatHeld} " +
                        $"BACKWARD-steps={ApexHUD.Native.Overlays.FrameStreamer.StatBackward} slow-presents(>25ms)={clipSlowPresents}");
            ApexHUD.Native.Overlays.FrameStreamer.StatOnTime = 0;
            ApexHUD.Native.Overlays.FrameStreamer.StatHeld = 0;
            ApexHUD.Native.Overlays.FrameStreamer.StatBackward = 0;
        }

        // The battle card is the one cross-overlay signal: its renderer's state
        // machine decides whether a battle is on, and the tower / featured /
        // ticker visibility gates all read PresentationState.IsBattleActive.
        // Publish it once per present, right after battle.Draw (above) evaluated
        // it - the gates pick it up on the next FrameNeeded tick.
        PresentationState.IsBattleActive = battle.IsActive && (battleEntry?.Allowed ?? true);

        // Battle-mode diagnostic (2026-10-18): one log line every 5 s while connected,
        // with exactly what the battle gate sees, so a race where it didn't fire can
        // be explained afterwards. Cheap (runs 0.2x/s).
        if (snap.IsConnected && nowMs - lastBattleDiagMs >= 5000)
        {
            lastBattleDiagMs = nowMs;
            try
            {
                var bc = ApexHUD.Native.Common.BattleDetection.Compute(snap);
                string G(double? v) => v is double x ? x.ToString("0.00") : "-";
                string line = $"{DateTime.Now:MM-dd HH:mm:ss}  session='{snap.SessionTypeName}' state={snap.SessionState} phase={snap.Phase} " +
                            $"player={(snap.Player is { } p ? $"#{p.CarNumber} P{p.PositionInClass} {p.ClassName} int={G(p.IntervalSeconds)}" : "NOT FOUND")} " +
                            $"ahead={(bc.Ahead?.CarNumber ?? "-")} gapA={G(bc.GapAheadSeconds)} behind={(bc.Behind?.CarNumber ?? "-")} gapB={G(bc.GapBehindSeconds)} " +
                            $"ACTIVE={battle.IsActive}{Environment.NewLine}";
                // disk write off the render thread; the file is started fresh each run so it never grows forever
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        if (!battleDiagFresh) { battleDiagFresh = true; System.IO.File.WriteAllText(BattleDiagPath, line); }
                        else System.IO.File.AppendAllText(BattleDiagPath, line);
                    }
                    catch { }
                });
            }
            catch (Exception ex) { Program.Log($"battle-diag error: {ex.Message}"); }
        }
    }
    double lastBattleDiagMs = -1e9;
    double lastCustomFailMs = -1e9;
    SKImageFilter? steadyWatchShadow;
    int seenReplayJumps;
    string seenClassMap = "";
    SKPaint? layerPaintCache;
    static volatile bool battleDiagFresh;
    static readonly string BattleDiagPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "battle_diag.log");

    // ===== Session bar + watch composition (ported from MainWindow.OnPaintSurface) =====

    void PaintSessionBar(SKCanvas canvas, RaceSnapshot snap, double nowMs, int width, int height)
    {
        const float BarPlateTop = 18f; // nudged 2px below the tower top so the watch face clears the screen edge
        float plateWidth = ComputeBarWidth(width, nowMs);
        float plateHeight = SessionBarRenderer.BarHeight;
        float plateX = (width - plateWidth) / 2f;
        float plateY = BarPlateTop;

        // Pre-race entrance (Angelo 2026-10-18): when the opener music ends the full-width
        // bar drops in and fades up over ~0.6 s instead of popping on.
        int revealSave = -1;
        if (PresentationState.Phase == RacePhase.PreRace && StartingGridRenderer.MusicEndMs >= 0)
        {
            float k = (float)Math.Clamp((nowMs - StartingGridRenderer.MusicEndMs) / 600.0, 0, 1);
            k = k * k * (3 - 2 * k);
            if (k < 1f)
            {
                using var rp = new SKPaint { Color = new SKColor(0, 0, 0, (byte)(255 * k)) };
                revealSave = canvas.SaveLayer(rp);
                canvas.Translate(0, -(1 - k) * 36f);
            }
        }

        bar.Draw(canvas, plateX, plateY, 1f, snap, DateTime.Now, nowMs, plateWidth);
        VpLayout.SessionBar = new SKRect(plateX, plateY, plateX + plateWidth, plateY + plateHeight);   // 2026-10-27: cards keep clear of it

        // Watch centrepiece in the bar alcove, with the legacy drop-shadow + v46
        // entrance overshoot, composited via a SaveLayer (matches MainWindow).
        float watchCx = plateX + plateWidth / 2f;
        float watchCy = plateY + plateHeight / 2f;
        const float watchRadius = 64f;

        var arrive = WatchArrive.Evaluate(nowMs);
        float shadowDy = 3f * watchRadius / 64f;
        float shadowSigma = 4f * watchRadius / 64f;
        // the steady-state shadow never changes: build it once instead of every present
        bool entrance = arrive.Blur > 0.01f;
        using var blur = entrance
            ? SKImageFilter.CreateBlur(arrive.Blur * watchRadius / 64f, arrive.Blur * watchRadius / 64f)
            : null;
        using var entranceShadow = entrance ? SKImageFilter.CreateDropShadow(
            0, shadowDy, shadowSigma, shadowSigma, new SKColor(0, 0, 0, 115), blur) : null;
        var shadow = entranceShadow ?? (steadyWatchShadow ??= SKImageFilter.CreateDropShadow(
            0, shadowDy, shadowSigma, shadowSigma, new SKColor(0, 0, 0, 115), null));
        layerPaintCache ??= new SKPaint();
        var layerPaint = layerPaintCache;
        layerPaint.ImageFilter = shadow;
        layerPaint.Color = new SKColor(0, 0, 0, (byte)(arrive.Opacity * 255));

        // PERF: an unbounded SaveLayer allocates a FULL-SCREEN offscreen buffer and
        // runs the drop-shadow blur over all of it EVERY present - the dominant
        // per-frame cost behind the clunky live transitions (the bar is Continuous,
        // so this ran ~30-60x/sec on top of the splash video). In steady state the
        // watch is a fixed disc, so bound the layer to a tight box around it: the
        // shadow is pixel-identical but the offscreen + blur shrink ~40x. During the
        // brief startup entrance (rotate/scale/overshoot) keep the unbounded layer so
        // the moving watch never clips.
        bool watchSteady = MathF.Abs(arrive.RotationDeg) < 0.01f
                           && MathF.Abs(arrive.Scale - 1f) < 0.001f
                           && arrive.Opacity >= 0.999f && arrive.Blur <= 0.01f;
        if (watchSteady)
        {
            float half = watchRadius + 40f;
            var watchBounds = new SKRect(watchCx - half, watchCy - half, watchCx + half, watchCy + half);
            canvas.SaveLayer(watchBounds, layerPaint);
        }
        else
        {
            canvas.SaveLayer(layerPaint);
        }
        canvas.Translate(watchCx, watchCy);
        canvas.RotateDegrees(arrive.RotationDeg);
        canvas.Scale(arrive.Scale);
        canvas.Translate(-watchCx, -watchCy);
        watch.Draw(canvas, watchCx, watchCy, watchRadius, DateTime.Now);
        canvas.Restore();
        if (revealSave >= 0) canvas.RestoreToCount(revealSave);
    }

    float ComputeBarWidth(float screenWidth, double elapsedMs)
    {
        const double BarExpandAnimMs = 400;
        bool wantFull = PresentationState.BarFullWidth;
        if (wantFull != barWasFullWidth)
        {
            barWidthFlipAtMs = elapsedMs;
            barWasFullWidth = wantFull;
        }
        float defaultWidth = SessionBarRenderer.BarWidth;
        float fullWidth = Math.Max(defaultWidth, screenWidth);
        if (barWidthFlipAtMs < 0) return wantFull ? fullWidth : defaultWidth;
        double t = (elapsedMs - barWidthFlipAtMs) / BarExpandAnimMs;
        float prog = (float)BroadcastTiming.Smoothstep(t);
        return wantFull
            ? defaultWidth + (fullWidth - defaultWidth) * prog
            : fullWidth - (fullWidth - defaultWidth) * prog;
    }

    /// <summary>
    /// Even-pacing presentation clock for the transition video. Deriving the clip
    /// frame index from the wall clock makes it jitter against vsync — the 2-2-3
    /// judder. Instead, while a splash is active we advance the video clock by
    /// exactly one steady present interval per present, so a 30 fps clip on a
    /// 60 Hz display lands on a clean 2-2-2 cadence (and the smoothest possible
    /// cadence on any refresh rate). The splash's start/swap/end are still decided
    /// by <see cref="SceneDirector"/> on the real clock, so it can't drift.
    /// </summary>
    double SplashNowMs(double realNowMs)
    {
        if (!SceneDirector.SplashActive) { splashClockArmed = false; return realNowMs; }
        if (!splashClockArmed)
        {
            splashClockArmed = true;
            splashStartPresent = presentIndex;
            splashStartReal = realNowMs;
            splashVideoMs = realNowMs;
            splashLastPresent = presentIndex;
            return splashVideoMs;
        }

        // 2026-10-18 (GTP "clunk" fix): this used to be
        //     splashStartReal + presentsSinceStart * presentIntervalMs
        // but presentIntervalMs is a running estimate updated EVERY present, so a
        // tiny change in it rescaled the whole elapsed timeline - 200 presents in,
        // 16.67 -> 16.60 ms moved the clock BACK ~14 ms and the clip stepped back a
        // frame (logged: 191 frame changes in a 183-frame clip). Now the clock only
        // ever moves FORWARD: one present interval per new present (idempotent if
        // called twice in one present), plus a gentle pull toward real time so it
        // can't drift - still an even 2-2-2 cadence, never backwards.
        long advanced = presentIndex - splashLastPresent;
        if (advanced > 0)
        {
            splashLastPresent = presentIndex;
            double next = splashVideoMs + advanced * presentIntervalMs;
            double drift = realNowMs - next;                 // + = video clock behind real time
            next += Math.Clamp(drift * 0.05, -2.0, 2.0);     // slow, bounded re-sync
            if (next > splashVideoMs) splashVideoMs = next;  // monotonic: never step back
        }
        return splashVideoMs;
    }

    double splashVideoMs;
    long splashLastPresent;

    /// <summary>
    /// Alloc-free FNV-1a hash of exactly the fields the registered renderers draw,
    /// so a static field set hashes identically every tick (=> the host idles) and
    /// any real change bumps the hash (=> present + settle window). Excludes
    /// time-derived fields (CapturedAt, session clocks) that change every tick
    /// without changing what these overlays show.
    /// </summary>
    static ulong Signature(RaceSnapshot snap)
    {
        ulong h = 14695981039346656037UL;
        static void Mix(ref ulong h, ulong v) { h = (h ^ v) * 1099511628211UL; }

        Mix(ref h, (ulong)(int)snap.Phase);
        Mix(ref h, (ulong)snap.Flags.GetHashCode());

        var scene = SceneDirector.CurrentScene;
        Mix(ref h, (ulong)(int)scene.Focus);
        if (scene.ClassName != null) Mix(ref h, (ulong)scene.ClassName.GetHashCode());

        // Indexed loops, not foreach: iterating the IReadOnlyList interface with
        // foreach allocates an enumerator per call (≈125 Hz) — the indexer does not.
        var classes = snap.ClassLeaderboard;
        for (int ci = 0; ci < classes.Count; ci++)
        {
            var drivers = classes[ci].Drivers;
            for (int di = 0; di < drivers.Count; di++)
            {
                var d = drivers[di];
                Mix(ref h, (ulong)d.CarIdx);
                Mix(ref h, (ulong)d.Position);
                Mix(ref h, (ulong)d.PositionInClass);
                Mix(ref h, (ulong)d.LapsCompleted);
                Mix(ref h, (ulong)BitConverter.DoubleToInt64Bits(d.GapToLeaderSeconds ?? 0));
                Mix(ref h, (ulong)BitConverter.DoubleToInt64Bits(d.IntervalSeconds ?? 0));
                Mix(ref h, (ulong)(uint)BitConverter.SingleToInt32Bits(d.BestLapTime));
                Mix(ref h, (ulong)(uint)BitConverter.SingleToInt32Bits(d.LastLapTime));
                Mix(ref h, (ulong)(uint)BitConverter.SingleToInt32Bits(d.LapDistPct));
            }
        }

        if (snap.CameraTarget is { } ct) Mix(ref h, (ulong)ct.CarIdx);
        return h;
    }

    public void Dispose()
    {
        splash.Dispose();
        tower.Dispose();
        featured.Dispose();
        ticker.Dispose();
        battle.Dispose();
        preCards.Dispose();
        grid.Dispose();
        outro.Dispose();
        spotlight.Dispose();
        fastest.Dispose();
        pit.Dispose();
        bar.Dispose();
        watch.Dispose();
        lensRain.Dispose();
        replayWipe.Dispose();
        vp.Dispose();
        foreach (var e in entries) e.Cache?.Dispose();
    }
}
