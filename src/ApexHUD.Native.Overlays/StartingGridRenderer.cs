using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Draws the Starting Grid — the broadcast-style centre-of-screen panel that
/// displays the entire field grouped by class before the race ("STARTING GRID")
/// and again, with gap data, after the chequer ("FINAL CLASSIFICATION"). Port
/// of the production-visible parts of the legacy <c>starting-grid.html</c>
/// (PORT_PLAN §8).
///
/// <para>Phase-gated: the panel is only painted when
/// <see cref="RaceSnapshot.Phase"/> is <see cref="RacePhase.PreRace"/> or
/// <see cref="RacePhase.PostRace"/>, cross-fading + sliding ~520 ms on a phase
/// flip. Outside those phases the renderer paints nothing (the window is still
/// alive but transparent).</para>
/// </summary>
public sealed class StartingGridRenderer : IDisposable
{
    public const float CardWidth = 1068f;   // window 1100 - 16 px inset both sides
    public const float CardHeight = 798f;   // window 830  - 16 px inset top/bottom

    // Header + footer chrome.
    const float HeaderHeight = 86f;   // 22 + ~46 + 18 = title + pill row
    const float FooterHeight = 38f;   // 12 + ~12 + 14
    const float BodyPadX = 22f;
    const float BodyPadY = 16f;
    const float ColumnGap = 18f;

    // Phase-change cross-fade. The entrance (grid / final classification
    // arriving) stays a crisp ~520 ms rise; the EXIT — the grid dissolving as
    // the race goes green — is a slower, deliberate "float up and dissolve" so
    // the start of the race gets a real broadcast transition, not a quick blink.
    const double FadeAnimMs  = 520.0;   // entrance duration
    const double ExitAnimMs  = 820.0;   // exit duration (the fade-before-green)
    const float  EnterDriftY = 12f;     // entrance settles down into place
    const float  ExitDriftY  = 30f;     // exit lifts up as it dissolves

    // --- palette (built once) ---
    static readonly SKColor BgGlass        = ParseHex(BroadcastPalette.TowerGlassBase, 209); // ~0.82 alpha — matches every other glass card
    static readonly SKColor BorderHairline = ParseHex(BroadcastPalette.BorderHairline, 110);
    static readonly SKColor TextPrimary    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextTertiary   = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor LeaderGold     = ParseHex(BroadcastPalette.Leader);
    static readonly SKColor PitAmber       = ParseHex(BroadcastPalette.PitAmber);
    static readonly SKColor DnfRed         = new(0xF8, 0x71, 0x71);
    static readonly SKColor PlayerBlue     = ParseHex(BroadcastPalette.Player);
    static readonly SKColor PlayerStripe   = ParseHex(BroadcastPalette.PlayerStripe);

    // IMSA-style class order — GTP first, GT3-tier last, OTHER as catch-all
    // (matches legacy CLASS_ORDER in starting-grid.html).
    static readonly string[] ClassOrder =
    {
        "GTP", "LMDh", "LMP", "LMP2", "LMP3", "GTDP", "GTE", "GTD", "GT3", "GT4", "OTHER",
    };

    // Logo cache shared across the grid lifetime.
    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);

    // Phase-fade state: tracks the last gated phase so a flip arms the fade.
    bool wasGated;
    double phaseFlipAtMs = -1;

    /// <summary>
    /// Draw the grid card centred at (originX, originY). Returns <c>true</c>
    /// when any pixels were painted (caller can ignore — paints are cheap and
    /// short-circuit when the phase isn't gated and the fade has elapsed).
    /// </summary>
    // Pre-race on/off cycle (2026-10-18, Angelo): the full starting-order board no
    // longer sits on screen all the way to the green. Before the race it shows for
    // BoardOnMs, hides for BoardOffMs, and repeats - using the board's own fade in
    // / fade out. Post-race (final classification) is unaffected and stays up.
    public const double BoardOnMs  = 30_000;
    public const double BoardOffMs = 30_000;
    double boardCycleStartMs = -1;
    double lastElapsedMs;
    readonly GridHeroBoard hero = new();
    /// <summary>Compositor time the pre-race info cards begin (-1 = not yet). Set when the opener's board leaves.</summary>
    public static double CardsStartMs = -1;
    /// <summary>Render-clock ms when the opener music ended (-1 = not yet): the session bar
    /// and the starting-order strip wait for it.</summary>
    public static double MusicEndMs = -1;
    /// <summary>2026-10-27 (#394, Angelo): when the music ends the camera cuts to the blimp and holds ~5 s "to take in the
    /// environment" before the track intro card starts.</summary>
    public const double BlimpHoldMs = 5600;
    /// <summary>The whole screen in canvas coordinates (set by the host each frame).</summary>
    public SKRect ScreenRect;
    int introState;
    double rearmT = -1;   // #421: the replay clock last frame (going backwards in the pre-race re-arms the intro)
    double preSinceMs = -1;   // when this pre-race session was first drawn (the early opener waits a few seconds)   // 0 = not started, 1 = intro logos, 2 = scripted board during the song, 3 = normal on/off cycle
    readonly GridIntro intro = new();

    /// <summary>Fire-and-forget: hand the opener theme to C:\SimRacing\Tools\play_sound.ps1
    /// in its own hidden process (plays up to the board's on-window, fading out at the end).</summary>
    /// <summary>Set false by preview / test tools so rendering samples stays silent.</summary>
    public static bool OpenerAudioEnabled = true;

    /// <returns>true if the music player was launched (the intro then syncs to it).</returns>
    static bool PlayOpener(bool globe = false)
    {
        if (!OpenerAudioEnabled) return false;
        try
        {
            // personal assets\audio -> Documents\iRacing\Grandstand\audio -> assets\audio_public (2026-10-24)
            string? file = SoundFiles.Find(globe ? "grid_opener_globe" : "grid_opener", ".mp3", ".m4a", ".wav", ".wma");
            if (file == null) return false;
            return OpenerAudio.Play(file, maxSeconds: 60, fadeSeconds: 1.0);   // in-process now (was a PowerShell script)
        }
        catch { /* audio is a nicety - never let it affect the overlay */ }
        return false;
    }

    /// <summary>True during the pre-race window, so the host keeps presenting through
    /// the board's on/off fades and page cross-fades (it otherwise repaints only on data changes).</summary>
    public static bool PreRaceAnimating => PresentationState.Phase == RacePhase.PreRace;

    bool introOverlayPending;

    // 2026-10-24 (Angelo: "the intro is horribly choppy"): the board's chrome and the class-change stinger keep
    // their paints / shaders / lists between frames instead of making new ones every frame (tools/intro_bench).
    readonly SKPaint cardFill = new(), footText = new();
    readonly ShaderSlot topLine = new(), slabSlot = new();
    readonly ShaderSlot[] streakSlots = Enumerable.Range(0, 14).Select(_ => new ShaderSlot()).ToArray();
    readonly List<DriverClass> orderedBuf = new(), nonEmptyBuf = new();
    readonly double[] slotBuf = new double[64];
    static readonly double[][] Slots123 = { new[] { GridIntro.GridS }, new[] { GridIntro.GridS, GridIntro.Class2S }, new[] { GridIntro.GridS, GridIntro.Class2S, GridIntro.Class3S } };
    static readonly Comparison<DriverClass> ByClassOrder = (a, b) => ClassOrderIndex(a.Name).CompareTo(ClassOrderIndex(b.Name));
    // the stinger's motion streaks: the same Random(7) sequence the old per-frame code drew from
    static readonly (float Y, float Len, float Th)[] Streaks = MakeStreaks();
    static (float, float, float)[] MakeStreaks()
    {
        var rnd = new Random(7); var s = new (float, float, float)[14];
        for (int i = 0; i < s.Length; i++) { float y = (float)rnd.NextDouble(), len = 80 + (float)rnd.NextDouble() * 260, th = 1.5f + (float)rnd.NextDouble() * 3f; s[i] = (y, len, th); }
        return s;
    }
    int footCarsN = -1, footConnN = -1; string footCars = "", footConn = "";

    public bool Draw(SKCanvas canvas, float originX, float originY, RaceSnapshot snap, double elapsedMs)
    {
        introOverlayPending = false;
        bool drew = DrawCore(canvas, originX, originY, snap, elapsedMs);
        if (introOverlayPending)
        {
            intro.Draw(canvas, new SKRect(originX, originY, originX + CardWidth, originY + CardHeight), snap, elapsedMs);
            drew = true;
        }
        return drew;
    }

    // ---- opener played once per race, remembered across a restart (2026-10-25) ----
    static readonly string OpenerFile = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "opener_played.txt");
    static bool openerSkipChecked;
    static string openerRunKey = ""; static double openerRunAt;   // this run: the opener already played for this session at this clock

    /// <summary>True only for the FIRST opener this run of Grandstand would play, when the file says it already played
    /// for this same race in the last 4 hours (i.e. Grandstand was restarted). A replay rewound to the start later in
    /// the same run plays the opener again as normal.</summary>
    static bool OpenerAlreadyPlayed(RaceSnapshot snap)
    {
        if (openerSkipChecked || snap.SessionKey.Length == 0) return false;
        openerSkipChecked = true;
        try
        {
            if (!System.IO.File.Exists(OpenerFile)) return false;
            var p = System.IO.File.ReadAllText(OpenerFile).Trim().Split('|');
            return p.Length == 2 && p[0] == snap.SessionKey && long.TryParse(p[1], out long ticks)
                   && (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalHours < 4;
        }
        catch { return false; }
    }

    static void MarkOpenerPlayed(RaceSnapshot snap)
    {
        openerSkipChecked = true;
        if (snap.SessionKey.Length == 0) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OpenerFile)!);
            System.IO.File.WriteAllText(OpenerFile, snap.SessionKey + "|" + DateTime.UtcNow.Ticks);
        }
        catch { }
    }

    bool DrawCore(SKCanvas canvas, float originX, float originY, RaceSnapshot snap, double elapsedMs)
    {
        lastElapsedMs = elapsedMs;
        bool pre = snap.Phase == RacePhase.PreRace;

        // Broadcast OPENER (2026-10-18, Angelo): the first time the pre-race grid
        // would appear, the theme starts and GridIntro plays iRacing -> series ->
        // track on the music's downbeats. The board's 30 s on/off cycle then starts
        // on the final downbeat, cross-dissolving out of the intro plate.
        // 2026-10-24 (choppy intro): before the opener, get its pictures decoded / keyed / laid out in the
        // background and uploaded to the graphics card, so its first frames don't do it
        if (pre && introState == 0) { intro.Prewarm(snap); intro.WarmGpu(canvas, ScreenRect, snap); }
        if (!pre) { boardCycleStartMs = -1; introState = 0; intro.Reset(); CardsStartMs = -1; MusicEndMs = -1; preSinceMs = -1; }
        else if (introState == 0 && preSinceMs < 0) preSinceMs = elapsedMs;
        // 2026-10-25 (Angelo: "start the intro as soon as qualifying ends"): iRacing publishes the qualifying result
        // the moment qualifying finishes, so once the race session is loaded and the grid is known the opener
        // starts - after a few seconds for the globe's dress rehearsal (never more than 25 s). The pace car
        // (SessionState 3) still starts it if we got here late.
        bool gridKnown = snap.GridClassPositions.Count > 0;
        double preFor = preSinceMs < 0 ? 0 : (elapsedMs - preSinceMs) / 1000.0;
        bool earlyReady = pre && gridKnown && snap.SessionState is 1 or 2 && preFor >= 4
                          && ((GlobeIntro.Ready(snap) && GridIntro.RehearsalDone) || preFor >= 25);
        if (!pre) { }
        else if (introState == 0 && (snap.SessionState == 3 || earlyReady) && !ReplayState.IsReplay && OpenerAlreadyPlayed(snap))
        {
            // 2026-10-25 (Angelo: "see if there is a way to not replay the intro when it restarts, that would be
            // disastrous for a broadcast"): Grandstand came back up in a race whose opener already played - go straight
            // to where the opener leaves off (session bar, then the info cards), no music, no splash pages.
            introState = 3; MusicEndMs = elapsedMs; CardsStartMs = elapsedMs + 1200;
            OverlayLog.Write("[opener] already played for this race before the restart - skipped");
        }
        else if (introState == 0 && (snap.SessionState == 3 || earlyReady) && (snap.SessionKey.Length > 0 || preFor >= 3)
                 && ReplayState.PlayingNormally            // 2026-10-25: never while rewinding / fast-forwarding a replay
                 && !(openerRunKey == snap.SessionKey + ":" + snap.SessionNum && ReplayState.SessionTime >= openerRunAt - 10))   // once per session, unless rewound to before it
        {
            introState = 1;
            MarkOpenerPlayed(snap);
            openerRunKey = snap.SessionKey + ":" + snap.SessionNum; openerRunAt = ReplayState.SessionTime;
            // Le Mans: the track page stays up behind the grid, and the dive comes 2 s after GTP leaves
            intro.DiveS = GridIntro.HoldsTrackPlate(snap) ? GridIntro.BoardOutS + GridIntro.HoldAfterBoardS : GridIntro.GridS;
            // globe beat on the series hit, iRacing + series sharing the first page (Angelo 2026-10-20);
            // original theme either way (grid_opener_globe.mp3 kept as a spare, unused)
            bool globe = GridIntro.GlobeOpener && GlobeIntro.Ready(snap);
            intro.Start(elapsedMs, PlayOpener(), globe);
        }
        // 2026-10-27 (#421, Angelo: "I rewound the replay to watch the intro but it doesn't fire"): in a replay, any
        // rewind / fast-forward / jump that lands us back in the pre-race re-arms the intro (it used to re-arm only if
        // we landed before the moment it first started, which the start of a replay file often can't reach)
        // only when the replay went BACKWARDS (a forward skip - iRacing jumping the wait on the grid - must not replay it)
        double rt = ReplayState.SessionTime;
        bool wentBack = rearmT >= 0 && rt < rearmT - 1.0;
        rearmT = rt;
        if (pre && ReplayState.IsReplay && introState is 0 or 3 && wentBack && openerRunKey.Length > 0)
        {
            openerRunKey = ""; introState = 0; intro.Reset(); preSinceMs = elapsedMs; CardsStartMs = -1; MusicEndMs = -1;
            OverlayLog.Write("[opener] replay rewound into the pre-race - the intro will play again");
        }
        if (pre && introState is 1 or 2 && ReplayState.Scrubbing)
        {   // rewinding / fast-forwarding through the opener: stop it; it plays again once the replay runs at normal speed
            // (a jump in the replay clock alone - iRacing skips the wait on the grid - does NOT stop it: it played twice)
            introState = 0; intro.Reset(); preSinceMs = elapsedMs; CardsStartMs = -1; MusicEndMs = -1; openerRunKey = "";
            OpenerAudio.Stop();
        }
        if (introState == 1 && intro.T(elapsedMs) >= GridIntro.GridS) introState = 2;
        // Song over: the board fades out for good - only the session bar remains until
        // the green flag (Angelo 2026-10-18). No more 30 s on/off cycling.
        // Sequence on the last big hit: board floats away + the full-width session bar (and
        // the starting-order strip) drop in together, then ~1.4 s later the first info card
        // rises - the bar always lands BEFORE the first card (Angelo 2026-10-18).
        if (introState == 2 && intro.T(elapsedMs) >= GridIntro.BoardOutS)
        { introState = 3; MusicEndMs = elapsedMs + 150 + Math.Max(0, intro.DiveS + 0.45 - GridIntro.BoardOutS) * 1000; CardsStartMs = MusicEndMs + BlimpHoldMs; }   // #394: ~5 s on the blimp after the music, then the track intro   // Le Mans: after the dive   // first card 2 s after the bar has landed (Angelo 2026-10-19)
        bool preCycleOn = pre && introState == 2;
        bool gated = preCycleOn || snap.Phase == RacePhase.PostRace;

        intro.Screen = ScreenRect;   // splash pages fill the whole screen
        // During the switch to the grid the intro is painted OVER the board (see Draw) so the
        // map can blast toward the camera in front of it while the plate dissolves away.
        bool introOnTop = pre && intro.Running && intro.T(elapsedMs) >= intro.DiveS - 0.6;
        introOverlayPending = introOnTop && intro.T(elapsedMs) < intro.EndT;
        if (pre && !introOnTop && intro.Running && intro.T(elapsedMs) < intro.EndT)
            intro.Draw(canvas, new SKRect(originX, originY, originX + CardWidth, originY + CardHeight), snap, elapsedMs);

        if (gated != wasGated)
        {
            phaseFlipAtMs = elapsedMs;
            wasGated = gated;
        }

        float fadeAlpha = ComputeFadeAlpha(gated, elapsedMs);
        if (fadeAlpha <= 0.01f) return false;
        float translateY = ComputeFadeTranslateY(gated, elapsedMs);

        // Disconnected fades to 55% per legacy CSS.
        float alpha = fadeAlpha;   // #406: no half-faded board on a momentary "disconnected"

        canvas.Save();
        canvas.Translate(originX, originY + translateY);
        var card = new SKRect(0, 0, CardWidth, CardHeight);

        DrawCard(canvas, card, alpha);
        // Pre-race opener (Angelo 2026-10-19): no series / "STARTING GRID" title and no
        // FORMATION pill - it's the opening titles, everyone knows. The class strip takes the room.
        bool showHeader = snap.Phase == RacePhase.PostRace;
        if (showHeader) DrawHeader(canvas, card, snap, alpha, elapsedMs);

        var body = new SKRect(
            card.Left + BodyPadX,
            card.Top + (showHeader ? HeaderHeight : 0f) + BodyPadY,
            card.Right - BodyPadX,
            card.Bottom - FooterHeight - BodyPadY);
        DrawBody(canvas, body, snap, alpha);

        DrawFooter(canvas, card, snap, alpha);

        canvas.Restore();
        return true;
    }

    // ---------------------------------------------------------- card chrome
    void DrawCard(SKCanvas c, SKRect card, float alpha)
    {
        var p = cardFill;
        p.Reset(); p.IsAntialias = true; p.Color = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha));
        c.DrawRect(card, p);

        // 1 px hairline border around the card (legacy --border-hairline).
        p.Reset(); p.IsAntialias = false; p.Color = BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha)); p.Style = SKPaintStyle.Stroke; p.StrokeWidth = 1f;
        c.DrawRect(card, p);

        // Gold top accent hairline fading at both edges (legacy .grid-card::before).
        p.Reset(); p.IsAntialias = false;
        p.Shader = topLine.Linear(new SKPoint(card.Left, card.Top), new SKPoint(card.Right, card.Top),
            [LeaderGold.WithAlpha(0), LeaderGold.WithAlpha((byte)(128 * alpha)), LeaderGold.WithAlpha((byte)(255 * alpha)), LeaderGold.WithAlpha((byte)(128 * alpha)), LeaderGold.WithAlpha(0)],
            Stops.Hairline);
        c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 1f), p);
    }

    // ------------------------------------------------------------- header
    void DrawHeader(SKCanvas c, SKRect card, RaceSnapshot snap, float alpha, double elapsedMs)
    {
        var header = new SKRect(card.Left, card.Top, card.Right, card.Top + HeaderHeight);
        // Subtle background sheen (legacy: rgba(255,255,255,0.02)).
        using (var bg = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(5 * alpha)) })
            c.DrawRect(header, bg);

        // Bottom hairline separating header from body.
        using (var line = new SKPaint
        {
            Color = BorderHairline.WithAlpha((byte)(80 * alpha)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        })
            c.DrawLine(header.Left, header.Bottom, header.Right, header.Bottom, line);

        // --- Title block (left): eyebrow + big uppercase title.
        float blockX = header.Left + 28f;
        float eyebrowY = header.Top + 28f;
        string eyebrow = ResolveEyebrow(snap);
        using (var eb = new SKPaint
        {
            IsAntialias = true,
            Color = LeaderGold.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 14f,   // #340: was 12
        })
        {
            TextTracked.Draw(c, eb, eyebrow, blockX, eyebrowY, 3.6f);
        }

        string title = snap.Phase == RacePhase.PostRace ? "FINAL CLASSIFICATION" : "STARTING GRID";
        using (var t = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.CormorantGaramond.Bold,
            TextSize = 42f,
            // Soft drop shadow (legacy: 0 2px 12px rgba(0,0,0,0.6)).
            ImageFilter = SKImageFilter.CreateDropShadow(0, 2, 6, 6,
                new SKColor(0, 0, 0, (byte)(150 * alpha))),
        })
        {
            float titleY = header.Top + 70f;
            TextTracked.Draw(c, t, title, blockX, titleY, 0.12f * 42f);
        }

        // --- Phase pill (right): bordered chip with pulsing dot + label.
        DrawPhasePill(c, header, snap, alpha, elapsedMs);
    }

    void DrawPhasePill(SKCanvas c, SKRect header, RaceSnapshot snap, float alpha, double elapsedMs)
    {
        string label = PhasePillLabel(snap);

        using var labelPaint = new SKPaint
        {
            IsAntialias = true,
            Color = LeaderGold.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 13f,
        };
        float spacing = 0.30f * 13f;
        var widths = labelPaint.GetGlyphWidths(label);
        float textW = 0f;
        for (int i = 0; i < widths.Length; i++)
            textW += widths[i] + (i == widths.Length - 1 ? 0 : spacing);

        const float DotSize = 8f;
        const float DotGap = 10f;
        const float PadX = 16f;
        const float PillHeight = 36f;
        float pillW = PadX + DotSize + DotGap + textW + PadX;
        float pillRight = header.Right - 28f;
        float pillLeft = pillRight - pillW;
        float pillTop = header.Top + (HeaderHeight - PillHeight) / 2f;
        var pill = new SKRect(pillLeft, pillTop, pillRight, pillTop + PillHeight);

        // Translucent gold background.
        using (var bg = new SKPaint { Color = LeaderGold.WithAlpha((byte)(26 * alpha)) })
            c.DrawRect(pill, bg);
        // 1 px gold border (legacy 0.45 alpha).
        using (var border = new SKPaint
        {
            Color = LeaderGold.WithAlpha((byte)(115 * alpha)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        })
            c.DrawRect(pill, border);

        // Pulsing dot — 1.8 s ease-in-out, opacity 0.55→1.0, scale 1.0→1.15.
        float t = (float)((elapsedMs % 1800.0) / 1800.0);
        float sin = (float)Math.Sin(t * Math.PI * 2.0);          // -1..1
        float pulse = (sin + 1f) / 2f;                            //  0..1
        float dotAlpha = 0.55f + 0.45f * pulse;
        float dotScale = 1.0f + 0.15f * pulse;
        float dotCx = pill.Left + PadX + DotSize / 2f;
        float dotCy = pill.MidY;
        // Soft glow ring (legacy box-shadow: 0 0 12px gold).
        using (var glow = new SKPaint
        {
            IsAntialias = true,
            Color = LeaderGold.WithAlpha((byte)(80 * dotAlpha * alpha)),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 4f),
        })
            c.DrawCircle(dotCx, dotCy, (DotSize / 2f) * dotScale + 2f, glow);
        using (var dot = new SKPaint
        {
            IsAntialias = true,
            Color = LeaderGold.WithAlpha((byte)(255 * dotAlpha * alpha)),
        })
            c.DrawCircle(dotCx, dotCy, (DotSize / 2f) * dotScale, dot);

        // Label.
        labelPaint.GetFontMetrics(out var m);
        float baseline = pill.MidY - (m.Ascent + m.Descent) / 2f;
        TextTracked.Draw(c, labelPaint, label,
            pill.Left + PadX + DotSize + DotGap, baseline, spacing);
    }

    static string ResolveEyebrow(RaceSnapshot snap)
    {
        var name = SeriesNames.ResolveName(snap.SeriesId);
        if (!string.IsNullOrEmpty(name)) return name.ToUpperInvariant();
        if (!string.IsNullOrEmpty(snap.TrackBroadcastName))
            return snap.TrackBroadcastName.ToUpperInvariant();
        if (!string.IsNullOrEmpty(snap.TrackDisplayName))
            return snap.TrackDisplayName.ToUpperInvariant();
        return "RACE";
    }

    static string PhasePillLabel(RaceSnapshot snap)
    {
        if (snap.Phase == RacePhase.PostRace) return "FINAL RESULT";
        if (snap.Phase == RacePhase.PreRace)
        {
            // Any car already on lap 1+ ⇒ formation is over, S/F not yet
            // crossed by the leader. Mirrors the legacy sub-state heuristic.
            bool anyLap = false;
            foreach (var cls in snap.ClassLeaderboard)
            {
                foreach (var d in cls.Drivers)
                    if (d.CurrentLap >= 1) { anyLap = true; break; }
                if (anyLap) break;
            }
            return anyLap ? "LAP 1" : "FORMATION";
        }
        return "GRID";
    }

    // --------------------------------------------------------------- body
    void DrawBody(SKCanvas c, SKRect body, RaceSnapshot snap, float alpha)
    {
        if (snap.ClassLeaderboard.Count == 0 || body.Width <= 0 || body.Height <= 0)
        {
            DrawEmpty(c, body, alpha);
            return;
        }

        // Re-order the snapshot's per-class list into the IMSA-style class order
        // the grid is authored against.
        var ordered = orderedBuf;   // reused list
        ordered.Clear();
        for (int i = 0; i < snap.ClassLeaderboard.Count; i++) ordered.Add(snap.ClassLeaderboard[i]);
        ordered.Sort(ByClassOrder);

        bool postRace = snap.Phase == RacePhase.PostRace;

        // 2026-10-18 (Angelo): PRE-RACE uses the "Front Row Heroes" board - one
        // page per class, big brand-logo hero cards for pole + P2, the rest listed.
        // Pages advance only while the board is ON (time accumulated across the
        // 30 s on-windows). Post-race keeps the classification columns below.
        if (!postRace)
        {
            hero.SetTrack(snap);   // the page-timer arrow follows this track's outline
            double cyc = BoardOnMs + BoardOffMs;
            if (introState == 2)
            {
                // Scripted to the opener, building UP to the top class as the music builds:
                // lowest class at 12.07 s, next class up at 19.75 s, top class at 27.41 s.
                // Two classes: top class arrives at 19.75 s and holds. More than three:
                // the classes share the 12.07 s -> ~33 s window evenly.
                double tm = intro.T(lastElapsedMs);
                int pages = 0; foreach (var cl in ordered) if (cl.Drivers.Count > 0) pages++;
                if (pages == 0) return;
                ReadOnlySpan<double> slotAt;
                if (pages <= 3) slotAt = Slots123[pages - 1];
                else
                {
                    var buf = pages <= slotBuf.Length ? slotBuf : new double[pages];
                    double span = (33.0 - GridIntro.GridS) / pages;
                    for (int i = 0; i < pages; i++) buf[i] = GridIntro.GridS + i * span;
                    slotAt = buf.AsSpan(0, pages);
                }
                int slot = 0;
                for (int i = 0; i < slotAt.Length; i++) if (tm >= slotAt[i]) slot = i;
                int PageFor(int s) => pages - 1 - s;           // ordered is top class first; show lowest first
                // Class change = a PUSH STINGER (Angelo 2026-10-18: needs weight). The old
                // card slides off left (shrinking, dimming); a thick slab in the NEW class's
                // colour, carrying its class logo and motion streaks, drives in from the right
                // and pushes the new card in behind it; a flash as it crosses the middle.
                const double StingerS = 0.75;
                float k = slot == 0 ? 1f : (float)Math.Clamp((tm - slotAt[slot]) / StingerS, 0, 1);
                // Each page BUILDS itself (Angelo 2026-10-19) - its clock starts as it lands.
                double pageDelay = slot == 0 ? 0.15 : 0.55;
                double pageT = tm - slotAt[slot] - pageDelay;
                double pageLen = (slot + 1 < slotAt.Length ? slotAt[slot + 1] : GridIntro.BoardOutS) - slotAt[slot] - pageDelay;
                if (k >= 1f) { hero.DrawClassPage(c, body, ordered, snap.PlayerCarIdx, alpha, PageFor(slot), pageT, pageLen); return; }
                float e = k < 0.5f ? 16 * k * k * k * k * k : 1 - (float)Math.Pow(-2 * k + 2, 5) / 2;   // ease-in-out quint
                const float Slab = 150f;
                float W = body.Width + Slab + 40;
                var clip = new SKRect(body.Left - 8, body.Top - 6, body.Right + 8, body.Bottom + 6);

                var nonEmpty = nonEmptyBuf; nonEmpty.Clear(); foreach (var cl in ordered) if (cl.Drivers.Count > 0) nonEmpty.Add(cl);
                int pi = PageFor(slot);
                var newCls = pi >= 0 && pi < nonEmpty.Count ? nonEmpty[pi] : null;
                SKColor col = newCls != null ? GridHeroBoard.ParseClassColor(newCls.Drivers[0].ClassColor) : new SKColor(0xE8, 0xC2, 0x5A);

                c.Save();
                c.ClipRect(clip);
                // outgoing card: pushed left, shrinking + dimming
                float oldX = -e * W;
                c.Save();
                c.Translate(oldX, 0);
                c.Translate(body.MidX, body.MidY); c.Scale(1f - 0.07f * e); c.Translate(-body.MidX, -body.MidY);
                hero.DrawClassPage(c, body, ordered, snap.PlayerCarIdx, alpha * (1f - 0.55f * e), PageFor(slot - 1));
                c.Restore();
                // incoming card: rides in right behind the slab
                float newX = (1 - e) * W;
                c.Save();
                c.Translate(newX, 0);
                hero.DrawClassPage(c, body, ordered, snap.PlayerCarIdx, alpha, PageFor(slot), pageT, pageLen);
                c.Restore();
                // the slab, leading the incoming card
                float sx = body.Left + newX - Slab;
                var slab = new SKRect(sx, clip.Top, sx + Slab, clip.Bottom);
                // the slab's and streaks' gradients are built from x = 0 and moved with the canvas (2026-10-24)
                var sp = cardFill;
                sp.Reset(); sp.IsAntialias = true;
                sp.Shader = slabSlot.Linear(new SKPoint(0, 0), new SKPoint(Slab, 0),
                    [col.WithAlpha((byte)(255 * alpha)), col.WithAlpha((byte)(235 * alpha)), new SKColor(255, 255, 255, (byte)(230 * alpha))], Stops.At082);
                c.Save(); c.Translate(slab.Left, 0);
                c.DrawRect(new SKRect(0, slab.Top, slab.Right - slab.Left, slab.Bottom), sp);
                c.Restore();
                // motion streaks trailing the slab
                for (int i = 0; i < Streaks.Length; i++)
                {
                    var (fy, len, th) = Streaks[i];
                    float y = clip.Top + fy * clip.Height;
                    sp.Shader = streakSlots[i].Linear(new SKPoint(0, 0), new SKPoint(len, 0),
                        [col.WithAlpha((byte)(200 * alpha * Math.Clamp((1 - e) * 2.2f, 0, 1))), col.WithAlpha(0)], []);
                    c.Save(); c.Translate(slab.Right, 0);
                    c.DrawRect(new SKRect(0, y, len, y + th), sp);
                    c.Restore();
                }
                // class logo riding the slab
                var cl0 = newCls != null ? hero.ClassLogoFor(newCls.Name) : null;
                if (cl0 != null)
                {
                    float lw = Slab - 16, lh = cl0.Height * (lw / cl0.Width);
                    var ld = new SKRect(slab.MidX - lw / 2 - 6, slab.MidY - lh / 2, slab.MidX + lw / 2 - 6, slab.MidY + lh / 2);
                    sp.Reset(); sp.IsAntialias = true; sp.FilterQuality = SKFilterQuality.High; sp.Color = SKColors.White.WithAlpha((byte)(255 * alpha));
                    BitmapImages.Draw(c, cl0, ld, sp);
                }
                // flash as the slab crosses the middle
                float fl = 1f - Math.Abs(k - 0.5f) / 0.22f;
                if (fl > 0)
                {
                    sp.Reset(); sp.Color = new SKColor(255, 255, 255, (byte)(60 * fl * alpha));
                    c.DrawRect(clip, sp);
                }
                c.Restore();
                return;
            }
            double t = boardCycleStartMs < 0 ? 0 : Math.Max(0, lastElapsedMs - boardCycleStartMs);
            double tOn = Math.Floor(t / cyc) * BoardOnMs + Math.Min(t % cyc, BoardOnMs);
            hero.Draw(c, body, ordered, snap.PlayerCarIdx, alpha, tOn, BoardOnMs);
            return;
        }

        int columnCount = ordered.Count;
        float totalGap = ColumnGap * (columnCount - 1);
        float columnWidth = (body.Width - totalGap) / columnCount;
        string playerName = snap.Player?.Name ?? string.Empty;

        for (int i = 0; i < columnCount; i++)
        {
            float left = body.Left + i * (columnWidth + ColumnGap);
            var col = new SKRect(left, body.Top, left + columnWidth, body.Bottom);
            DrawClassColumn(c, col, ordered[i], playerName, postRace, alpha);
        }
    }

    static int ClassOrderIndex(string className)
    {
        for (int i = 0; i < ClassOrder.Length; i++)
            if (string.Equals(ClassOrder[i], className, StringComparison.OrdinalIgnoreCase))
                return i;
        return ClassOrder.Length; // unknown class lands after OTHER alphabetically
    }

    void DrawEmpty(SKCanvas c, SKRect body, float alpha)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.CormorantGaramond.SemiBold,
            TextSize = 14f,
            TextAlign = SKTextAlign.Center,
        };
        p.GetFontMetrics(out var m);
        float baseline = body.MidY - (m.Ascent + m.Descent) / 2f;
        TextTracked.Draw(c, p, "WAITING FOR GRID…", body.MidX, baseline, 0.30f * 14f);
    }

    // --------------------------------------------------- per-class column
    void DrawClassColumn(SKCanvas c, SKRect col, DriverClass cls, string playerName,
                         bool postRace, float alpha)
    {
        if (col.Width <= 0 || col.Height <= 0) return;
        var classColor = ParseHex(string.IsNullOrEmpty(cls.Color)
            ? BroadcastPalette.Class(cls.Name)
            : cls.Color);

        // ---- class header (28 px) ----
        const float HeaderH = 28f;
        const float HeaderGap = 6f;
        var header = new SKRect(col.Left, col.Top, col.Right, col.Top + HeaderH);

        using (var shader = SKShader.CreateLinearGradient(
            new SKPoint(header.Left, header.Top),
            new SKPoint(header.Right, header.Top),
            new[]
            {
                classColor.WithAlpha((byte)(90 * alpha)),
                classColor.WithAlpha((byte)(15 * alpha)),
                classColor.WithAlpha(0),
            },
            new[] { 0f, 0.7f, 1f },
            SKShaderTileMode.Clamp))
        using (var hp = new SKPaint { IsAntialias = true, Shader = shader })
            c.DrawRect(header, hp);
        // 4 px class-color left border.
        using (var stripe = new SKPaint
        {
            IsAntialias = false,
            Color = classColor.WithAlpha((byte)(255 * alpha)),
        })
            c.DrawRect(new SKRect(header.Left, header.Top, header.Left + 4f, header.Bottom), stripe);

        string classLabel = ClassDisplay(cls.Name);
        if (!string.IsNullOrEmpty(classLabel))
        {
            using var label = new SKPaint
            {
                IsAntialias = true,
                Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
                Typeface = Fonts.CormorantGaramond.Bold,
                TextSize = 16f,
                ImageFilter = SKImageFilter.CreateDropShadow(0, 1, 1.5f, 1.5f,
                    new SKColor(0, 0, 0, (byte)(180 * alpha))),
            };
            label.GetFontMetrics(out var m);
            float baseline = header.MidY - (m.Ascent + m.Descent) / 2f;
            TextTracked.Draw(c, label, classLabel, header.Left + 14f, baseline, 0.22f * 16f);
        }

        // Right-aligned car count.
        string countLabel = cls.Drivers.Count + " CARS";
        using (var count = new SKPaint
        {
            IsAntialias = true,
            Color = TextPrimary.WithAlpha((byte)(225 * alpha)),   // #340: was 12 px @180
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 13.5f,
            TextAlign = SKTextAlign.Right,
        })
        {
            count.GetFontMetrics(out var m);
            float baseline = header.MidY - (m.Ascent + m.Descent) / 2f;
            TextTracked.Draw(c, count, countLabel, header.Right - 10f, baseline, 1.8f);
        }

        // ---- rows ----
        var rowsRect = new SKRect(col.Left, header.Bottom + HeaderGap, col.Right, col.Bottom);
        DrawRows(c, rowsRect, cls, classColor, playerName, postRace, alpha);
    }

    // ------------------------------------------------------------ rows
    //
    // One stable row layout for every column — the full detailed row
    // (number chip + license + name + iRating + manufacturer logo). Earlier
    // iterations bucketed by car count (Normal / Dense / UltraDense) and
    // flipped layouts at thresholds (≥18, ≥26), which produced visible
    // frame-to-frame flicker when a class hovered around 17–18 cars and the
    // count oscillated between two values. Holding a single deterministic
    // layout for all field sizes removes the flicker entirely; large fields
    // simply clip at the bottom of the column via the existing ClipRect.
    readonly record struct RowMetrics(
        float Height, float PosSize, float NumHeight, float NumFont,
        float NameFont, float LogoSize, float StripeHeight,
        bool ShowLic, bool ShowIRating, float LicW, float IRatingW, float GapW);

    static RowMetrics RichRow(bool postRace) =>
        new(40f, 26f, 22f, 13f, 17f, 24f, 26f,
            ShowLic: true, ShowIRating: true,
            LicW: 24f, IRatingW: 52f, GapW: postRace ? 82f : 0f);

    void DrawRows(SKCanvas c, SKRect rect, DriverClass cls, SKColor classColor,
                  string playerName, bool postRace, float alpha)
    {
        var m = RichRow(postRace);
        int count = cls.Drivers.Count;
        if (count == 0) return;

        // Clip rows to the column so very large fields can't bleed into the
        // footer (the legacy used overflow:hidden — same behaviour here).
        c.Save();
        c.ClipRect(rect);

        for (int i = 0; i < count; i++)
        {
            float top = rect.Top + i * m.Height;
            if (top >= rect.Bottom) break;
            var row = new SKRect(rect.Left, top, rect.Right, Math.Min(top + m.Height, rect.Bottom));
            DrawRow(c, row, cls.Drivers[i], classColor, playerName, postRace, m, i, alpha);
        }

        c.Restore();
    }

    void DrawRow(SKCanvas c, SKRect row, Driver d, SKColor classColor,
                 string playerName, bool postRace, RowMetrics m, int rowIndex, float alpha)
    {
        bool isPole = d.PositionInClass == 1;
        bool isPlayer = !string.IsNullOrEmpty(playerName)
            && string.Equals(playerName, d.Name, StringComparison.Ordinal);
        bool isDnf = !d.IsConnected;

        // Zebra stripe (very subtle).
        var zebra = (rowIndex & 1) == 0
            ? new SKColor(255, 255, 255, (byte)(1 * alpha))
            : new SKColor(255, 255, 255, (byte)(5 * alpha));
        using (var zp = new SKPaint { Color = zebra })
            c.DrawRect(row, zp);

        if (isPole)
        {
            // Pole / winner wash — gold horizontal gradient (richer in post-race).
            byte goldA = (byte)((postRace ? 56 : 46) * alpha);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(row.Left, row.Top), new SKPoint(row.Right, row.Top),
                new[]
                {
                    LeaderGold.WithAlpha(goldA),
                    LeaderGold.WithAlpha((byte)(8 * alpha)),
                    LeaderGold.WithAlpha(0),
                },
                new[] { 0f, 0.6f, 1f },
                SKShaderTileMode.Clamp);
            using var p = new SKPaint { Shader = shader };
            c.DrawRect(row, p);
        }
        if (isPlayer)
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(row.Left, row.Top), new SKPoint(row.Right, row.Top),
                new[]
                {
                    PlayerBlue.WithAlpha((byte)(140 * alpha)),
                    PlayerBlue.WithAlpha((byte)(45 * alpha)),
                    PlayerBlue.WithAlpha((byte)(10 * alpha)),
                },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp);
            using var p = new SKPaint { Shader = shader };
            c.DrawRect(row, p);
            using var leftBar = new SKPaint
            {
                Color = PlayerStripe.WithAlpha((byte)(255 * alpha)),
            };
            c.DrawRect(new SKRect(row.Left, row.Top, row.Left + 6f, row.Bottom), leftBar);
            using var rightBar = new SKPaint
            {
                Color = PlayerStripe.WithAlpha((byte)(140 * alpha)),
            };
            c.DrawRect(new SKRect(row.Right - 2f, row.Top, row.Right, row.Bottom), rightBar);
            using var inner = new SKPaint
            {
                Color = PlayerStripe.WithAlpha((byte)(90 * alpha)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
            };
            c.DrawRect(row, inner);
        }

        // Per-row top hairline (only between rows).
        if (rowIndex > 0)
        {
            using var line = new SKPaint
            {
                Color = new SKColor(255, 255, 255, (byte)(10 * alpha)),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
            };
            c.DrawLine(row.Left, row.Top, row.Right, row.Top, line);
        }

        // Column layout — packed left-to-right inside the row, mirroring the
        // legacy 9-col grid (stripe / pos / lic / num / pad / name / irating /
        // logo / gap). In pre-race the gap column is 0 (hidden); in post-race
        // it's 82 px and the number column is skipped (matches legacy CSS).
        const float PadAfterLeftMargin = 4f;
        const float PadAfterPos = 0f;
        const float PadAfterLic = 4f;
        const float PadAfterNum = 12f;
        const float PadAfterName = 6f;
        const float PadAfterIRating = 6f;
        const float PadAfterLogo = 8f;

        float cursor = row.Left;
        float colorAlpha = isDnf ? 0.5f * alpha : alpha;

        // 1) class-colour stripe (always)
        using (var stripe = new SKPaint
        {
            IsAntialias = false,
            Color = classColor.WithAlpha((byte)(255 * colorAlpha)),
        })
        {
            float sh = m.StripeHeight;
            float sy = row.MidY - sh / 2f;
            c.DrawRect(new SKRect(cursor, sy, cursor + 3f, sy + sh), stripe);
        }
        cursor += 3f + PadAfterLeftMargin;

        // 2) position (32 px column)
        const float PosColW = 32f;
        DrawPosition(c, new SKRect(cursor, row.Top, cursor + PosColW, row.Bottom),
            d.PositionInClass, isPole, isPlayer, m.PosSize, colorAlpha);
        cursor += PosColW + PadAfterPos;

        // 3) license chip (normal-density only)
        if (m.ShowLic && !string.IsNullOrEmpty(d.LicenceString))
        {
            DrawLicenseChip(c, cursor, row, d.LicenceString[..1].ToUpperInvariant(), colorAlpha);
        }
        cursor += m.LicW + PadAfterLic;

        // 4) car number chip (hidden in post-race)
        if (!postRace)
        {
            const float NumColW = 38f;
            DrawNumberChip(c, cursor, row, d.CarNumber, m.NumHeight, m.NumFont, colorAlpha);
            cursor += NumColW + PadAfterNum;
        }
        else
        {
            cursor += PadAfterNum;
        }

        // 5) name (fills the remaining flex between cursor and end-cap)
        float endCap = row.Right
            - (m.GapW > 0 ? m.GapW : 0f)
            - (m.GapW > 0 ? PadAfterLogo : 0f)
            - m.LogoSize
            - (m.ShowIRating ? PadAfterIRating + m.IRatingW : 0f)
            - PadAfterName;
        if (endCap < cursor + 24) endCap = cursor + 24;
        DrawName(c, cursor, row, endCap - cursor, d, m.NameFont, isPlayer, isDnf, colorAlpha);
        cursor = endCap + PadAfterName;

        // 6) iRating chip
        if (m.ShowIRating && d.IRating > 0)
        {
            DrawIRating(c, cursor, row, d.IRating, m.IRatingW, colorAlpha);
        }
        if (m.ShowIRating) cursor += m.IRatingW + PadAfterIRating;

        // 7) manufacturer logo
        DrawLogo(c, cursor, row, d.CarBrand, m.LogoSize, colorAlpha);
        cursor += m.LogoSize + PadAfterLogo;

        // 8) gap column (post-race only)
        if (m.GapW > 0)
        {
            DrawGap(c, new SKRect(cursor, row.Top, cursor + m.GapW, row.Bottom),
                d, isPole, isDnf, colorAlpha);
        }
    }

    // ---------- per-cell drawing helpers ----------
    void DrawPosition(SKCanvas c, SKRect cell, int pic, bool isPole, bool isPlayer,
                      float fontSize, float alpha)
    {
        var color = isPole ? LeaderGold : TextPrimary;
        if (isPlayer) color = TextPrimary; // player override (legacy: white)
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = color.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.CormorantGaramond.Bold,
            TextSize = fontSize,
            TextAlign = SKTextAlign.Center,
        };
        p.GetFontMetrics(out var m);
        float baseline = cell.MidY - (m.Ascent + m.Descent) / 2f;
        string text = pic > 0 ? pic.ToString(CultureInfo.InvariantCulture) : "–";
        c.DrawText(text, cell.MidX, baseline, p);
    }

    void DrawLicenseChip(SKCanvas c, float left, SKRect row, string letter, float alpha)
    {
        const float ChipSize = 22f;
        float cy = row.MidY;
        var chip = new SKRect(left, cy - ChipSize / 2f, left + ChipSize, cy + ChipSize / 2f);
        var bg = ParseHex(BroadcastPalette.License(letter));
        using (var p = new SKPaint { IsAntialias = false, Color = bg.WithAlpha((byte)(255 * alpha)) })
            c.DrawRect(chip, p);
        using (var hl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(64 * alpha)) })
            c.DrawRect(new SKRect(chip.Left, chip.Top, chip.Right, chip.Top + 1f), hl);

        var txtColor = letter == "C" ? new SKColor(8, 10, 16) : SKColors.White;
        using var text = new SKPaint
        {
            IsAntialias = true,
            Color = txtColor.WithAlpha((byte)(255 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = 13f,
            TextAlign = SKTextAlign.Center,
        };
        text.GetFontMetrics(out var m);
        float baseline = chip.MidY - (m.Ascent + m.Descent) / 2f;
        c.DrawText(letter, chip.MidX, baseline, text);
    }

    void DrawNumberChip(SKCanvas c, float left, SKRect row, string number,
                         float chipHeight, float fontSize, float alpha)
    {
        if (string.IsNullOrEmpty(number)) number = "—";
        const float MinWidth = 26f;
        using var text = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(8, 10, 16, (byte)(242 * alpha)),
            Typeface = Fonts.Inter.Bold,
            TextSize = fontSize,
            TextAlign = SKTextAlign.Center,
        };
        float textW = text.MeasureText(number);
        float chipW = Math.Max(MinWidth, textW + 12f);
        float cy = row.MidY;
        var chip = new SKRect(left, cy - chipHeight / 2f,
                              left + chipW, cy + chipHeight / 2f);
        using (var bg = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * alpha)) })
            c.DrawRect(chip, bg);
        using (var hl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(178 * alpha)) })
            c.DrawRect(new SKRect(chip.Left, chip.Top, chip.Right, chip.Top + 1f), hl);
        text.GetFontMetrics(out var m);
        float baseline = chip.MidY - (m.Ascent + m.Descent) / 2f;
        c.DrawText(number, chip.MidX, baseline, text);
    }

    void DrawName(SKCanvas c, float left, SKRect row, float width, Driver d,
                  float fontSize, bool isPlayer, bool isDnf, float alpha)
    {
        var color = isPlayer ? SKColors.White : TextPrimary;
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = color.WithAlpha((byte)((isDnf ? 200 : 255) * alpha)),
            Typeface = Fonts.Inter.SemiBold,
            TextSize = fontSize,
            ImageFilter = isPlayer
                ? SKImageFilter.CreateDropShadow(0, 1, 1.5f, 1.5f,
                    new SKColor(0, 0, 0, (byte)(180 * alpha)))
                : null,
        };
        p.GetFontMetrics(out var m);
        float baseline = row.MidY - (m.Ascent + m.Descent) / 2f;
        string display = NameFormat.Broadcast(d);
        string ellipsised = EllipsiseToWidth(display, p, width);
        c.DrawText(ellipsised, left, baseline, p);
    }

    void DrawIRating(SKCanvas c, float left, SKRect row, int iRating, float colWidth, float alpha)
    {
        string main = FormatIRating(iRating);
        const string suffix = " iR";
        using var mainP = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 215, 100, (byte)(240 * alpha)),   // #340: was 12 px @220
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 13.5f,
            TextAlign = SKTextAlign.Right,
        };
        using var sufP = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 215, 100, (byte)(205 * alpha)),   // #340: was 9 px @140
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 11.5f,
            TextAlign = SKTextAlign.Right,
        };
        mainP.GetFontMetrics(out var m);
        float baseline = row.MidY - (m.Ascent + m.Descent) / 2f;
        float right = left + colWidth - 6f;
        float sufW = sufP.MeasureText(suffix);
        c.DrawText(suffix, right, baseline, sufP);
        c.DrawText(main, right - sufW, baseline, mainP);
    }

    void DrawLogo(SKCanvas c, float left, SKRect row, string brand, float logoSize, float alpha)
    {
        var slug = ManufacturerBrand.Resolve(brand);
        if (slug == null) return;
        var bmp = LoadLogo(slug);
        if (bmp == null) return;
        float scale = Math.Min(logoSize / bmp.Width, logoSize / bmp.Height);
        float dw = bmp.Width * scale;
        float dh = bmp.Height * scale;
        float cx = left + logoSize / 2f;
        float cy = row.MidY;
        var dst = new SKRect(cx - dw / 2f, cy - dh / 2f, cx + dw / 2f, cy + dh / 2f);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.High,
            ColorFilter = SKColorFilter.CreateBlendMode(
                new SKColor(255, 255, 255, (byte)(242 * alpha)),
                SKBlendMode.DstIn),
        };
        c.DrawBitmap(bmp, dst, paint);
    }

    void DrawGap(SKCanvas c, SKRect cell, Driver d, bool isPole, bool isDnf, float alpha)
    {
        SKColor color;
        string text;
        SKTypeface tf = Fonts.Inter.SemiBold;
        bool italic = false;
        float spacing = 0.04f * 14f;

        if (isPole)
        {
            color = LeaderGold;
            text = "WINNER";
            tf = Fonts.Inter.Bold;
        }
        else if (isDnf)
        {
            color = DnfRed;
            text = "DNF";
            tf = Fonts.Inter.Bold;
            italic = true;
            spacing = 0.16f * 14f;
        }
        else if (d.LapDifference >= 1)        // laps behind the leader (never negative)
        {
            color = PitAmber;
            int n = Math.Abs(d.LapDifference);
            text = "+" + n + (n == 1 ? " LAP" : " LAPS");
            italic = true;
        }
        else if (d.GapToLeaderSeconds is double g && g > 0.001)
        {
            color = TextPrimary;
            text = "+" + LapTimeFormat.Gap(g, d.LapDifference).TrimStart('+');
        }
        else
        {
            color = TextPrimary.WithAlpha(120);
            text = "–";
        }

        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = color.WithAlpha((byte)(255 * alpha)),
            Typeface = tf,
            TextSize = 14f,
            TextAlign = SKTextAlign.Right,
            TextSkewX = italic ? -0.18f : 0f,
        };
        p.GetFontMetrics(out var m);
        float baseline = cell.MidY - (m.Ascent + m.Descent) / 2f;
        TextTracked.Draw(c, p, text, cell.Right - 6f, baseline, spacing);
    }

    // -------------------------------------------------------------- footer
    void DrawFooter(SKCanvas c, SKRect card, RaceSnapshot snap, float alpha)
    {
        var footer = new SKRect(card.Left, card.Bottom - FooterHeight, card.Right, card.Bottom);
        var fp = cardFill;
        fp.Reset(); fp.Color = new SKColor(255, 255, 255, (byte)(4 * alpha));
        c.DrawRect(footer, fp);
        fp.Reset(); fp.Color = BorderHairline.WithAlpha((byte)(80 * alpha)); fp.Style = SKPaintStyle.Stroke; fp.StrokeWidth = 1f;
        c.DrawLine(footer.Left, footer.Top, footer.Right, footer.Top, fp);

        // Three cells: GRANDSTAND (gold-soft, left) | center label | car count (right).
        string left = "GRANDSTAND";
        // pre-race: the CLASS car count sits here (moved from the class strip; no "PRE-RACE")
        if (hero.LastClassCars != footCarsN) { footCarsN = hero.LastClassCars; footCars = footCarsN == 1 ? "1 CAR" : footCarsN + " CARS IN CLASS"; }
        string center = snap.Phase == RacePhase.PostRace ? "PROVISIONAL"
                      : hero.LastClassCars > 0 ? footCars
                      : "";
        int connectedCount = 0;
        for (int i = 0; i < snap.ClassLeaderboard.Count; i++)   // index loops: foreach over IReadOnlyList boxes an enumerator
        {
            var ds = snap.ClassLeaderboard[i].Drivers;
            for (int j = 0; j < ds.Count; j++) if (ds[j].IsConnected) connectedCount++;
        }
        if (connectedCount != footConnN) { footConnN = connectedCount; footConn = connectedCount + " CARS"; }
        string right = connectedCount > 0 ? footConn : "–";

        {
            var p = footText;
            p.Reset(); p.IsAntialias = true; p.Color = LeaderGold.WithAlpha((byte)(230 * alpha)); p.Typeface = Fonts.Inter.SemiBold; p.TextSize = 13f;   // #340: was 11 @178
            p.GetFontMetrics(out var m);
            float baseline = footer.MidY - (m.Ascent + m.Descent) / 2f;
            TextTracked.Draw(c, p, left, footer.Left + 28f, baseline, 2.6f);
        }
        {
            var p = footText;
            p.Reset(); p.IsAntialias = true; p.Color = TextPrimary.WithAlpha((byte)(225 * alpha)); p.Typeface = Fonts.Inter.Bold; p.TextSize = 13f; p.TextAlign = SKTextAlign.Center;   // #340: was 11 @180
            p.GetFontMetrics(out var m);
            float baseline = footer.MidY - (m.Ascent + m.Descent) / 2f;
            TextTracked.Draw(c, p, center, footer.MidX, baseline, 2.6f);
        }
        {
            var p = footText;
            p.Reset(); p.IsAntialias = true; p.Color = TextTertiary.WithAlpha((byte)(240 * alpha)); p.Typeface = Fonts.Inter.SemiBold; p.TextSize = 13f; p.TextAlign = SKTextAlign.Right;   // #340: was 11 @220
            p.GetFontMetrics(out var m);
            float baseline = footer.MidY - (m.Ascent + m.Descent) / 2f;
            TextTracked.Draw(c, p, right, footer.Right - 28f, baseline, 2.6f);
        }
    }

    // ----------------------------------------------- phase-fade animation
    float ComputeFadeAlpha(bool gated, double nowMs)
    {
        if (phaseFlipAtMs < 0)
            return gated ? 1f : 0f;
        double dur = gated ? FadeAnimMs : ExitAnimMs;
        double t = nowMs - phaseFlipAtMs;
        if (t >= dur) return gated ? 1f : 0f;
        float prog = (float)EaseOut(t / dur);
        return gated ? prog : (1f - prog);
    }

    float ComputeFadeTranslateY(bool gated, double nowMs)
    {
        if (phaseFlipAtMs < 0) return 0f;
        double dur = gated ? FadeAnimMs : ExitAnimMs;
        double t = nowMs - phaseFlipAtMs;
        if (t >= dur) return 0f;
        float prog = (float)EaseOut(t / dur);
        return gated ? EnterDriftY * (1f - prog) : -ExitDriftY * prog;
    }

    static double EaseOut(double t) => BroadcastTiming.EaseOutSoft(t);

    // ----------------------------------------------------------- helpers
    static string ClassDisplay(string name) => name switch
    {
        "GTP"   => "GTP",
        "LMDh"  => "LMDh",
        "GTDP"  => "GTD PRO",
        "GTD"   => "GTD",
        "GT3"   => "GT3",
        "GT4"   => "GT4",
        "GTE"   => "GTE",
        "LMP"   => "LMP",
        "LMP2"  => "LMP2",
        "LMP3"  => "LMP3",
        "OTHER" => string.Empty,
        _       => name,
    };

    static string FormatIRating(int ir)
    {
        if (ir >= 1000)
            return (ir / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        return ir.ToString(CultureInfo.InvariantCulture);
    }

    static string EllipsiseToWidth(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (paint.MeasureText(text) <= maxWidth) return text;
        const string ellipsis = "…";
        float ellipsisW = paint.MeasureText(ellipsis);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = paint.MeasureText(text.AsSpan(0, mid).ToString()) + ellipsisW;
            if (w <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo] + ellipsis;
    }

    SKBitmap? LoadLogo(string slug)
    {
        if (logoCache.TryGetValue(slug, out var cached)) return cached;
        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
            if (System.IO.File.Exists(path))
            {
                using var stream = System.IO.File.OpenRead(path);
                bmp = Assets.Decode(stream);
            }
        }
        catch { bmp = null; }
        logoCache[slug] = bmp;
        return bmp;
    }

    static SKColor ParseHex(string hex, byte alpha = 255)
    {
        var s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor(r, g, b, alpha);
    }

    public void Dispose()
    {
        foreach (var b in logoCache.Values) b?.Dispose();
        logoCache.Clear();
        hero.Dispose();
        intro.Dispose();
    }
}
