using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Single coordinator for which overlay windows are visible right now and how the
/// session bar is sized. Every window pulls from these properties each frame so
/// the suite switches together and consistently â€” there is no second source of
/// truth. The state is derived from three signals: the race phase from the
/// snapshot, a process-wide <c>lap1Complete</c> latch (true once the overall
/// race leader has crossed the start-finish line on lap 1; resets on a
/// transition back to <see cref="RacePhase.PreRace"/>), and the battle-active
/// signal published by <see cref="BattleCardWindow"/> as its renderer's state
/// machine flips.
///
/// <para>Updated once per frame via <see cref="Tick"/> from <see cref="App"/>'s
/// <c>CompositionTarget.Rendering</c> hook so the latch is recomputed in lock-
/// step with every window's paint, on the WPF render thread, with no dispatcher
/// marshalling.</para>
/// </summary>
public static class PresentationState
{
    static bool lap1Complete;

    /// <summary>True while <see cref="BattleCardRenderer"/> is showing the
    /// battle card. Written by <see cref="BattleCardWindow"/> every frame from
    /// <c>renderer.IsActive</c>; pulled lock-free by every other consumer.</summary>
    public static bool IsBattleActive { get; set; }
    /// <summary>A callout (fastest lap) is on screen: story cards wait for it, so two graphics never stack (2026-10-24).</summary>
    public static bool CalloutActive { get; set; }

    /// <summary>Latches true the first time the overall race leader has at
    /// least one completed lap; stays true until the next pre-race window. Gates
    /// the timing tower and featured card so neither paints during the bunched
    /// formation lap.</summary>
    public static bool Lap1Complete => lap1Complete;

    /// <summary>The latest snapshot, captured every frame by <see cref="Tick"/>.
    /// PresentationState derives phase/session gating from THIS, not from the
    /// WPF-only App.LatestSnapshot, so the gates work identically in the WPF app
    /// and in the GPU host (which never populates App.LatestSnapshot).</summary>
    static RaceSnapshot latest = RaceSnapshot.Disconnected;

    /// <summary>Race phase from the most recent snapshot.</summary>
    public static RacePhase Phase => latest.Phase;

    /// <summary>Current session type name ("Practice", "Qualify", "Race", ...).</summary>
    static string SessionTypeName => latest.SessionTypeName ?? string.Empty;

    /// <summary>True only for an actual Race session (mirrors RacePhaseMap). Practice
    /// and qualifying are NOT races, so the formation-lap latch must not gate them.</summary>
    public static bool IsRaceSession => SessionTypeName.Contains("race", StringComparison.OrdinalIgnoreCase);

    public static bool IsRacing   => Phase == RacePhase.Broadcast || OutroDirector.Waiting;   // chequered, featured car still on its last lap
    public static bool IsPreGreen => Phase is RacePhase.PreRace or RacePhase.Unknown;
    public static bool IsPostRace => Phase == RacePhase.PostRace && !OutroDirector.Waiting;

    // --- per-element visibility (single source of truth) ---
    //
    // Battle mode takes precedence over the racing layout: when battle is
    // active during the broadcast phase the tower + featured are hidden and the
    // ticker + battle card take over. Before the leader has finished lap 1 the
    // tower + featured stay hidden too â€” the field is bunched, battle
    // conditions are essentially always true, and showing 30 cars within 1 s of
    // each other reads as noise rather than information.

    // Pre-race (Angelo 2026-10-18): the bar stays off through the broadcast intro and
    // arrives when the opener music ends, stretched edge to edge until the green flag.
    // 2026-10-28 (#486, Angelo: "I want the timing tower to disappear, just leave the battle mode session bar with the ticker only"):
    // after the flag the bar stays, in its battle look, with the standings ticker under it; the tower stays off
    public static bool BarVisible        => !(Phase == RacePhase.PreRace && StartingGridRenderer.MusicEndMs < 0);
    public static bool BarFullWidth      => (IsRacing && (IsBattleActive || BumperBattleStyle)) || Phase == RacePhase.PreRace || IsPostRace;
    /// <summary>2026-10-27 (#337, Angelo): while the replay bumper (V6) plays, the session bar takes its battle-mode look -
    /// full width with the standings ticker under it - and the tower / featured card step aside as they do in a battle.
    /// Set by the compositor from the bumper each tick.</summary>
    public static bool BumperBattleStyle { get; set; }
    public static bool GridVisible       => IsPreGreen;   // post-race: the outro (OutroDirector) took over (2026-10-20)
    // Lap1Complete protects the bunched RACE formation lap. Practice/qualifying
    // have no formation lap (and the leader-lap latch may never fire there), so
    // bypass it for non-race sessions and show the tower/featured immediately.
    // 2026-10-24 (Angelo): the tower shows on lap 1 too - live places, no times until the line.
    public static bool TowerVisible      => IsRacing && !IsBattleActive && !BumperBattleStyle && !OnboardActive;
    /// <summary>The tower's lane is in use (racing, no battle) - the class rotation keeps running even while an
    /// onboard shot has the tower faded off, so it doesn't restart from OVERALL after every onboard.</summary>
    public static bool TowerLaneActive   => IsRacing && !IsBattleActive;
    /// <summary>2026-10-24: an onboard camera shot is on air (held 0.6 s before it counts, so camera flicks don't
    /// flash the graphics). The onboard instrument shows; the tower and session bar step aside.</summary>
    public static bool OnboardActive { get; set; }
    /// <summary>0..1 eased: how far the onboard look has taken over (drives the session bar's fade).</summary>
    public static float OnboardK { get; set; }
    /// <summary>#510 (2026-10-29, Angelo: "on the onboards a card comes in at the same time, then disappears while it's
    /// loading in - looks weird"): the camera has cut to an onboard but the 0.6 s hold hasn't passed yet.</summary>
    public static bool OnboardPending { get; set; }
    /// <summary>#510: an onboard is on air or coming - no NEW card (story / mini / board transition) starts now.</summary>
    public static bool OnboardSoon => OnboardActive || OnboardPending;

    /// <summary>#510 (2026-10-30): no NEW card / board transition starts now - an onboard is on air or coming, or the camera
    /// cut less than <see cref="ShotSettleMs"/> ago. Only for STARTING things: a card already up is not pulled by a cut.</summary>
    public static bool NewCardsHeld => OnboardSoon || ShotSettling;

    /// <summary>#510 (2026-10-30, Angelo: "camera changes need a settle delay"): how long new cards wait after ANY camera
    /// cut (another car, another camera group or camera) so a card never slides in on the same frame as a cut.</summary>
    public const double ShotSettleMs = 1600;
    /// <summary>#510: the camera cut less than <see cref="ShotSettleMs"/> ago.</summary>
    public static bool ShotSettling { get; private set; }
    static int camCar = int.MinValue, camGroup = int.MinValue;
    static string camName = "";
    static double camCutMs = double.NegativeInfinity;

    /// <summary>#510: the host calls this every frame with the latest snapshot; notes camera cuts (car, group, or the
    /// camera's own name within the group) and keeps <see cref="ShotSettling"/> up for <see cref="ShotSettleMs"/> after each.</summary>
    public static void TickCamera(RaceSnapshot snap, double nowMs)
    {
        var o = snap.Onboard;
        if (snap.IsConnected && o != null && o.CamCarIdx >= 0)
        {
            if (o.CamCarIdx != camCar || o.CamGroup != camGroup || !string.Equals(o.CamGroupName, camName, StringComparison.Ordinal))
            {
                camCar = o.CamCarIdx; camGroup = o.CamGroup; camName = o.CamGroupName ?? "";
                camCutMs = nowMs;
            }
        }
        ShotSettling = nowMs - camCutMs < ShotSettleMs;
    }
    /// <summary>#510: 0..1, the cards' hide for an onboard. Unlike <see cref="OnboardK"/> it starts the instant the camera
    /// cuts to an onboard (no 0.6 s hold), so a card that was just starting never shows half-entered then vanishes.</summary>
    public static float CardHideK { get; set; }
    /// <summary>#510: how hidden the HideOnOnboard graphics are right now.</summary>
    public static float CardsHiddenK => Math.Max(OnboardK, CardHideK);
    /// <summary>2026-10-27 (#423, Angelo: "when the replays happen make there be no overlay, then it comes back smoothly"):
    /// 0 = graphics on, 1 = hidden for a replay (eased by the host; the replay wipe and its REPLAY tag stay).</summary>
    public static float ReplayHideK { get; set; }
    public static bool FeaturedVisible   => FeaturedFree && Environment.TickCount64 < featOpenUntil;
    static bool FeaturedFree => IsRacing && (Lap1Complete || !IsRaceSession) && !IsBattleActive && !BumperBattleStyle && !StoryDirector.Visible && !PitStopShowing;
    /// <summary>#716: our pit stop board is up (bottom centre) - the driver card stays away meanwhile.</summary>
    public static bool PitStopShowing;

    // #704 (2026-10-31, Angelo: "the driver card on the bottom middle of the overlay does not need to be there all the time
    // maybe it comes up on the screen every once in a while like every ten or fifteen minutes or every two laps"):
    // the card comes up for 20 s - once the race is under way, then every 2 laps (at least 5 min apart) or after 12 min,
    // whichever comes first. If a battle / story card is on screen when it's due, it waits until the screen is free.
    const long FeatShowMs = 9_000 /* #726: broadcast research - time on screen sized to its words (~0.35 s a word + 1.5 s), was 20 s */, FeatMinGapMs = 5 * 60_000, FeatMaxGapMs = 12 * 60_000;
    static long featOpenUntil, featLastShow = long.MinValue / 2;
    static int featLastLap = -1;
    static bool featPending;

    static void TickFeatured(RaceSnapshot snap)
    {
        long now = Environment.TickCount64;
        if (snap.Phase == RacePhase.PreRace) { featOpenUntil = 0; featLastShow = long.MinValue / 2; featLastLap = -1; featPending = false; return; }
        int lap = snap.Player?.LapsCompleted ?? -1;
        if (lap >= 0 && (featLastLap < 0 || lap < featLastLap)) featLastLap = Math.Max(0, lap - 2);   // first time / new session: due now
        bool due = (lap >= featLastLap + 2 && now - featLastShow > FeatMinGapMs) || now - featLastShow > FeatMaxGapMs;
        if (due && now >= featOpenUntil) featPending = true;
        if (featPending && FeaturedFree && now >= featOpenUntil)
        {
            featOpenUntil = now + FeatShowMs; featLastShow = now; featLastLap = Math.Max(lap, 0); featPending = false;
        }
    }

    /// <summary>2026-10-18 (Angelo): event notifications (pit callouts, pit /
    /// fastest-lap row wings, fastest-lap callout) only fire once this is true.
    /// Race: from the green flag until the checkered (drivers loading in before
    /// the start used to read as pit entries; the post-race pit-in rush is noise
    /// too). Practice / qualifying: always.</summary>
    public static bool NotificationsArmed => (!IsRaceSession || Phase == RacePhase.Broadcast)
        && Environment.TickCount64 >= QuietUntilTicks;

    /// <summary>2026-10-24: after a replay jump (rewind / skip) everything that changed in one go is
    /// not news - pit, fastest-lap and battle callouts stay quiet until this moment.</summary>
    public static long QuietUntilTicks;

    public static bool TickerVisible     => (IsRacing && (IsBattleActive || BumperBattleStyle)) || IsPostRace;
    public static bool BattleCardVisible => IsRacing && IsBattleActive;

    /// <summary>Updates the <see cref="Lap1Complete"/> latch from the snapshot.
    /// Idempotent and cheap; called every frame from <see cref="App"/>'s
    /// <c>CompositionTarget.Rendering</c> hook.</summary>
    public static void Tick(RaceSnapshot snap)
    {
        latest = snap;
        if (snap.Phase == RacePhase.PreRace)
            lap1Complete = false;
        else if (!lap1Complete && snap.Leader is { LapsCompleted: >= 1 })
            lap1Complete = true;
        try { TickFeatured(snap); } catch { }
    }
}
