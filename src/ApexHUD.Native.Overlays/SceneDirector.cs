using System.Diagnostics;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>Which focus the timing tower should be drawing right now.</summary>
public enum SceneFocus
{
    /// <summary>Render every class — the OVERALL board (unchanged from pre-rotation).</summary>
    Overall,
    /// <summary>Render only the named class. <see cref="Scene.ClassName"/> identifies it.</summary>
    Class,
    /// <summary>2026-10-24 (Angelo): the PIT STOPS board for the named class - how many stops each car has made
    /// and on which laps. Joins the rotation from lap 3 (lap 1-2 crash dives are noise, not strategy).</summary>
    Pits,
}

/// <summary>One board in the rotation. <see cref="ClassName"/> / <see cref="ClassColor"/>
/// are null for <see cref="SceneFocus.Overall"/>; non-null for <see cref="SceneFocus.Class"/>.</summary>
public sealed record Scene(SceneFocus Focus, string? ClassName, string? ClassColor)
{
    /// <summary>Display label for the splash composite. <c>"OVERALL"</c> for the overall board, else the class name.</summary>
    public string Label => Focus == SceneFocus.Overall ? "OVERALL" : ClassName ?? "";
}

/// <summary>
/// Phase 1 timing-tower scene rotation. Cycles the tower through
/// OVERALL → (GTP, LMP2, GT3 — whichever are present in the current session) →
/// loop, with a branded splash transition between each board. Driven entirely
/// from the same per-frame elapsed-ms clock the renderers use, so playback is
/// frame-rate-independent and consistent with <see cref="BroadcastTiming"/> /
/// <see cref="RepaintGate"/>.
///
/// <para><b>How it composes with the rest of the suite.</b> Every renderer
/// pulls <see cref="CurrentScene"/> / <see cref="SplashActive"/> /
/// <see cref="SplashScene"/> per frame at paint time — same pull-don't-push
/// pattern as <see cref="PresentationState"/>. The tower renderer gates its
/// per-class iteration on <see cref="CurrentScene"/>; the splash window draws
/// the composite (iRacing wordmark + series logo + class name over an animated
/// background) at <see cref="SplashAlpha"/>. Rotation is paused (and reset to
/// OVERALL) whenever <see cref="PresentationState.TowerVisible"/> is false so
/// it doesn't fight the battle choreography — when the tower returns, the
/// rotation resumes cleanly from OVERALL.</para>
///
/// <para><b>Phase 2 video seam.</b> <see cref="CurrentSplashDurationMs"/> is
/// per-scene and has no upper cap beyond <see cref="SplashFailsafeMs"/>.
/// Phase 2 sets it to the full video length per class; the scene swap still
/// fires at the splash's opaque midpoint, so the visual seam between the
/// animated background (Phase 1) and decoded video frames (Phase 2) is
/// identical from the rest of the suite's perspective.</para>
/// </summary>
public static class SceneDirector
{
    /// <summary>How long each board holds before the splash begins announcing the next.</summary>
    public const double SceneHoldMs = 20_000;

    /// <summary>The pit-stop pages hold longer - there's more to read on them (Angelo, 2026-10-24).</summary>
    public const double PitHoldMs = 32_000;

    /// <summary>The pit board appears once the overall leader has completed this many laps.</summary>
    public const int PitBoardFromLaps = 2;

    static double HoldFor(Scene s) => s.Focus == SceneFocus.Pits ? PitHoldMs : SceneHoldMs;

    /// <summary>Fade-in / fade-out duration of the splash, in ms. The splash is
    /// fully opaque between <c>SplashFadeMs</c> and <c>splashTotal - SplashFadeMs</c>.</summary>
    public const double SplashFadeMs = 250;

    /// <summary>Phase 1 default total splash duration (fade-in + opaque hold +
    /// fade-out). Phase 2 will override per-scene with the video length.</summary>
    public const double DefaultSplashTotalMs = 1200;

    /// <summary>Generous failsafe — if a splash runs longer than this it is forced
    /// to end. This is NOT a normal cap; it exists only so a future Phase-2 video
    /// decode hang can't stall the rotation indefinitely.</summary>
    public const double SplashFailsafeMs = 30_000;

    /// <summary>Fixed-priority class focus list. The active scene list each cycle
    /// is OVERALL + whichever of these classes is present in the current session,
    /// in this order. Adding a class is one append here — by design.</summary>
    static readonly string[] FocusClassPriority = { "GTP", "LMP2", "GT3" };

    static readonly Stopwatch clock = Stopwatch.StartNew();

    static List<Scene> activeScenes = new() { new Scene(SceneFocus.Overall, null, null) };
    static int currentSceneIdx;
    static double sceneStartedAtMs = -1;

    // Splash state. splashStartedAtMs < 0 means no splash is active.
    static double splashStartedAtMs = -1;
    static double currentSplashTotalMs = DefaultSplashTotalMs;
    static Scene? splashScene;        // the board the splash is announcing (the next one)
    static Scene? pendingScene;       // resolved-by-reference; what to swap currentScene to at midpoint
    static bool sceneSwappedDuringSplash;
    static bool wasTowerVisible;

    /// <summary>Elapsed-ms clock shared with the splash renderer so timing is consistent.</summary>
    public static double NowMs => clock.Elapsed.TotalMilliseconds;

    /// <summary>The board the timing tower should currently render. Read per
    /// frame at paint time; switches at the splash's opaque midpoint, so the
    /// rebuild happens hidden behind the splash.</summary>
    public static Scene CurrentScene =>
        currentSceneIdx < activeScenes.Count
            ? activeScenes[currentSceneIdx]
            : activeScenes[0];

    /// <summary>2026-10-30 (class transitions choppy): the board the NEXT splash will most likely announce (the next one
    /// in the rotation), so its roller can be drawn ahead in the background; null while a splash plays or with nothing to
    /// rotate to. A pit-mode jump can announce a different board - that splash just builds its pictures when it starts.</summary>
    public static Scene? UpcomingScene
    {
        get
        {
            var list = activeScenes;
            if (splashStartedAtMs >= 0 || list.Count <= 1) return null;
            return list[(currentSceneIdx + 1) % list.Count];
        }
    }

    /// <summary>True while a splash transition is animating.</summary>
    public static bool SplashActive => splashStartedAtMs >= 0;

    /// <summary>The scene the active splash is announcing (null when no splash).
    /// Drives the splash composite's class name + colour + series logo.</summary>
    public static Scene? SplashScene => splashScene;

    /// <summary>Current splash total duration (ms). Scenes with a bundled
    /// transition clip use the full clip length (Phase 2,
    /// <see cref="SceneTransitions.ClipDurationMs"/>); scenes without one use
    /// <see cref="DefaultSplashTotalMs"/> (the Phase 1 animated splash).</summary>
    public static double CurrentSplashDurationMs => currentSplashTotalMs;

    /// <summary>Milliseconds the active splash has been running, or -1 if no
    /// splash is active. The splash renderer derives the video frame index from
    /// this (frameIndex = floor(elapsed / (1000/fps))).</summary>
    public static double SplashElapsedMs(double nowMs) =>
        splashStartedAtMs < 0 ? -1 : nowMs - splashStartedAtMs;

    // ---- Wipe transition (2026-10-18, Angelo) -----------------------------------
    // The clips still play IN FULL; only the in/out changed from a soft cross-fade
    // to a hard-edged wipe. Entry: the video sweeps DOWN over the tower. Exit: it
    // sweeps away top-to-bottom, uncovering the new board (the board already
    // swapped at the opaque midpoint).
    public const double SplashWipeMs = 380;

    /// <summary>Fraction (0..1, top..bottom) of the tower lane the splash covers
    /// right now. (0,0) = no splash; (0,1) = fully covering.</summary>
    public static (float Top, float Bottom) SplashReveal(double nowMs)
    {
        if (splashStartedAtMs < 0) return (0f, 0f);
        double t = nowMs - splashStartedAtMs;
        double total = currentSplashTotalMs;
        double wipe = Math.Min(SplashWipeMs, total / 3.0);
        if (t < 0 || t >= total) return (0f, 0f);
        if (t < wipe) return (0f, (float)Smoothstep(t / wipe));
        if (t < total - wipe) return (0f, 1f);
        return ((float)Smoothstep((t - (total - wipe)) / wipe), 1f);
    }

    /// <summary>True while the splash completely covers the tower lane (so the
    /// tower underneath doesn't need painting).</summary>
    public static bool SplashFullyCovers(double nowMs)
    {
        var (top, bottom) = SplashReveal(nowMs);
        return top <= 0.0001f && bottom >= 0.9999f;
    }

    /// <summary>0..1 alpha for the splash overlay. Smoothstep fade-in over
    /// <see cref="SplashFadeMs"/>, opaque, smoothstep fade-out over the same.
    /// (Legacy - the GPU splash now uses <see cref="SplashReveal"/>.)</summary>
    public static float SplashAlpha(double nowMs)
    {
        if (splashStartedAtMs < 0) return 0f;
        double t = nowMs - splashStartedAtMs;
        double fade = SplashFadeMs;
        double total = currentSplashTotalMs;
        if (t < 0) return 0f;
        if (t < fade)            return (float)Smoothstep(t / fade);
        if (t < total - fade)    return 1f;
        if (t < total)           return (float)Smoothstep((total - t) / fade);
        return 0f;
    }

    /// <summary>Called once per frame from <see cref="App"/>'s
    /// <c>CompositionTarget.Rendering</c> hook so the rotation advances on the
    /// same render-thread cadence every other overlay reads.</summary>
    public static void Tick(RaceSnapshot snap)
    {
        double nowMs = NowMs;
        WatchClassPits(snap, nowMs);
        bool towerVisible = PresentationState.TowerLaneActive;   // keeps rotating behind an onboard shot (2026-10-24)

        // Visibility gate: pause + reset whenever the tower is hidden (battle
        // takeover, pre-green, post-race). Resume cleanly from OVERALL when the
        // tower returns — never carry a half-played splash across the gate.
        if (!towerVisible)
        {
            if (wasTowerVisible) ResetState();
            wasTowerVisible = false;
            return;
        }
        if (!wasTowerVisible)
        {
            wasTowerVisible = true;
            ResetState();
            sceneStartedAtMs = nowMs;
        }

        // Rebuild the active scene list each tick from the classes actually
        // present in the snapshot. OVERALL always; GTP/LMP2/GT3 if a car of
        // that class is on the leaderboard.
        RebuildScenes(snap);

        // Single-scene case (no focus classes present): no rotation, no splash.
        if (activeScenes.Count <= 1)
        {
            splashStartedAtMs = -1;
            splashScene = null;
            pendingScene = null;
            sceneSwappedDuringSplash = false;
            sceneStartedAtMs = nowMs;
            currentSceneIdx = 0;
            return;
        }

        if (splashStartedAtMs >= 0)
        {
            double t = nowMs - splashStartedAtMs;

            // Activate the next scene at the splash's opaque midpoint —
            // the rebuild is hidden behind a fully-opaque splash, so the
            // viewer never sees the rows rearrange.
            if (!sceneSwappedDuringSplash && t >= currentSplashTotalMs / 2)
            {
                int newIdx = pendingScene != null ? FindSceneIdx(activeScenes, pendingScene) : -1;
                currentSceneIdx = newIdx >= 0 ? newIdx : 0;
                sceneSwappedDuringSplash = true;
            }

            // Normal exit + failsafe — bound the splash so a future video
            // hang can't stall the rotation.
            if (t >= currentSplashTotalMs || t >= SplashFailsafeMs)
            {
                splashStartedAtMs = -1;
                splashScene = null;
                pendingScene = null;
                sceneSwappedDuringSplash = false;
                sceneStartedAtMs = nowMs;
            }
            return;
        }

        // 2026-10-25 (Angelo: the class transitions played over onboard shots with no tower on screen):
        // while an onboard shot is up the board just holds - the next transition plays once the tower is back
        // #510 (2026-10-30): nor in the ~1.6 s after any camera cut - the roller never starts on the same frame as a cut
        if (PresentationState.NewCardsHeld) return;

        // No splash: hold the current board, then trigger the next splash.
        if (sceneStartedAtMs < 0) sceneStartedAtMs = nowMs;
        // 2026-10-25 (Angelo: "if a car in class pits turn that timing tower to pit mode ... at least 10 seconds ...
        // restart the timer for each"): a car of our class turning into the pits brings our class's PIT STOPS board up
        // straight away, and it stays until 10 s after the last of them went in
        int pitIdx = pitHoldClass != null && nowMs < pitHoldUntilMs ? FindSceneIdx(activeScenes, new Scene(SceneFocus.Pits, pitHoldClass, null)) : -1;
        bool onPitBoard = pitIdx >= 0 && currentSceneIdx == pitIdx;
        if (onPitBoard) return;                                                   // hold it
        if (pitIdx >= 0 || nowMs - sceneStartedAtMs >= HoldFor(CurrentScene))
        {
            int nextIdx = pitIdx >= 0 ? pitIdx : (currentSceneIdx + 1) % activeScenes.Count;
            // 2026-10-24: transitions switched off in the control app - the board just swaps.
            if (SceneTransitions.Off)
            {
                currentSceneIdx = nextIdx;
                sceneStartedAtMs = nowMs;
                return;
            }
            pendingScene = activeScenes[nextIdx];
            splashScene = pendingScene;
            // Phase 2: if the announced scene has a bundled transition clip, the
            // splash lasts the full clip length (Angelo's "play in full" rule);
            // otherwise keep the Phase 1 animated-splash duration.
            // 2026-10-24: no clip -> the built-in animated stinger, which lasts as long as the
            // footage it replaces (ProceduralStinger.DurationMs), not the old 1.2 s Phase 1 splash.
            currentSplashTotalMs = SceneTransitions.SplashDurationMs(pendingScene);
            splashStartedAtMs = nowMs;
            sceneSwappedDuringSplash = false;
            // 2026-10-27 (Angelo: "a stinger every time the timing tower changes classes will get annoying"): no sound on tower swaps
        }
    }

    static void RebuildScenes(RaceSnapshot snap)
    {
        var prior = CurrentScene;
        var list = new List<Scene>(1 + FocusClassPriority.Length)
        {
            new Scene(SceneFocus.Overall, null, null),
        };
        foreach (var name in FocusClassPriority)
        {
            var match = FindClass(snap, name);
            if (match != null)
                list.Add(new Scene(SceneFocus.Class, match.Name, match.Color));
        }
        // PIT STOPS pages (one per class) close the cycle - race only, from lap 3, and only for a class where
        // somebody has actually stopped (a page of zeros tells nobody anything).
        bool pitHold = pitHoldClass != null && NowMs < pitHoldUntilMs;
        foreach (var name in FocusClassPriority)
        {
            var match = FindClass(snap, name);
            if (match == null) continue;
            bool held = pitHold && string.Equals(match.Name, pitHoldClass, StringComparison.OrdinalIgnoreCase);
            if (held || (PitBoardReady(snap) && RaceDiary.TotalPitStops(match.Drivers) > 0))
                list.Add(new Scene(SceneFocus.Pits, match.Name, match.Color));
        }
        activeScenes = list;

        // Keep currentSceneIdx pointing at the same Scene when possible (the
        // user-visible board doesn't jump just because the active set changed).
        int realigned = FindSceneIdx(list, prior);
        if (realigned >= 0) currentSceneIdx = realigned;
        else if (currentSceneIdx >= list.Count) currentSceneIdx = 0;
    }

    // our class's pit mode (2026-10-25)
    const double ClassPitHoldMs = 10000;
    static string? pitHoldClass;
    static double pitHoldUntilMs = -1;
    static readonly Dictionary<int, bool> classInPit = new();
    static int pitWatchSession = int.MinValue;

    static void WatchClassPits(RaceSnapshot snap, double nowMs)
    {
        if (snap.SessionNum != pitWatchSession) { pitWatchSession = snap.SessionNum; classInPit.Clear(); pitHoldUntilMs = -1; pitHoldClass = null; }
        var p = snap.Player;
        if (p == null || !RaceDiary.RaceStarted || !(snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var cls in snap.ClassLeaderboard)
        {
            if (!string.Equals(cls.Name, p.ClassName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var d in cls.Drivers)
            {
                bool was = classInPit.TryGetValue(d.CarIdx, out var w) && w;
                if (d.IsInPit && !was && classInPit.ContainsKey(d.CarIdx) && d.LapsCompleted >= 1)
                { pitHoldClass = cls.Name; pitHoldUntilMs = nowMs + ClassPitHoldMs; }      // each new car restarts the 10 s
                classInPit[d.CarIdx] = d.IsInPit;
            }
        }
    }

    static bool PitBoardReady(RaceSnapshot snap)
    {
        if (!RaceDiary.RaceStarted) return false;
        if (!(snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase)) return false;
        int leaderLaps = 0;
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
                if (d.LapsCompleted > leaderLaps) leaderLaps = d.LapsCompleted;
        return leaderLaps >= PitBoardFromLaps;
    }

    static int FindSceneIdx(List<Scene> list, Scene target)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s.Focus != target.Focus) continue;
            if (s.Focus == SceneFocus.Overall) return i;
            if (string.Equals(s.ClassName, target.ClassName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    static DriverClass? FindClass(RaceSnapshot snap, string name)
    {
        foreach (var cls in snap.ClassLeaderboard)
            if (string.Equals(cls.Name, name, StringComparison.OrdinalIgnoreCase))
                return cls;
        return null;
    }

    static void ResetState()
    {
        currentSceneIdx = 0;
        sceneStartedAtMs = -1;
        splashStartedAtMs = -1;
        splashScene = null;
        pendingScene = null;
        sceneSwappedDuringSplash = false;
    }

    static double Smoothstep(double t)
    {
        if (t <= 0) return 0;
        if (t >= 1) return 1;
        return t * t * (3 - 2 * t);
    }
}
