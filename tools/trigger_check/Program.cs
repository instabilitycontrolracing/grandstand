// Synthetic-telemetry checks (2026-10-27): the driving-live gate, regression checks for the yellow flag and the story
// cards (Angelo confirmed those triggers work; no behaviour change), and #338 (no story cards in a battle while driving).
// Every scenario below is made-up telemetry built here (labelled synthetic), not a recording.
// Build with -p:DefineConstants=BEFORE against the pre-fix assemblies to see which checks the old code failed
// (the sound-gate section needs the new API and is left out then).
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;

int fails = 0, passes = 0;
void Check(string name, bool ok, string detail = "")
{
    if (ok) passes++; else fails++;
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  [" + detail + "]" : "")}");
}

TrackCorners.LandmarkFileOverride = Path.Combine(Path.GetTempPath(), "no_landmarks_here.json");   // no CrewChief names
StorySound.Enabled = false;   // never touch a sound device; OnPlay tells us what would have played
var played = new List<string>();
StorySound.OnPlay = c => played.Add(c);

#if !BEFORE
// ------------------------------------------------------------------------------------------------ #309 sound gate
Console.WriteLine("== driving-live gate (used by #338) ==");
Check("in car, at live = driving", DrivingState.LooksLikeDriving(true, true, false, 0, false));
Check("in car via IsOnTrackCar only = driving", DrivingState.LooksLikeDriving(false, true, false, 0, false));
Check("replay playing = not driving", !DrivingState.LooksLikeDriving(true, true, true, 5000, false));
Check("paused rewound replay (not playing, 2 min behind live) = not driving", !DrivingState.LooksLikeDriving(true, true, false, 7200, false));
Check("saved replay file = not driving", !DrivingState.LooksLikeDriving(true, true, false, 0, true));
Check("garage / spectating = not driving", !DrivingState.LooksLikeDriving(false, false, false, 0, false));
Check("frames-behind unknown (-1), in car = driving", DrivingState.LooksLikeDriving(true, false, false, -1, false));

DrivingState.Reset();
long t0 = Environment.TickCount64;
DrivingState.Update(true, true, false, 0, false, t0);
Check("gate shut while driving", DrivingState.IsDrivingAt(t0 + 10));
DrivingState.Update(false, false, false, 0, false, t0 + 100);
Check("still shut 2.9 s after leaving the car (tow / reset flicker)", DrivingState.IsDrivingAt(t0 + 100 + 2900));
Check("open 3.1 s after leaving the car", !DrivingState.IsDrivingAt(t0 + 100 + 3100));
DrivingState.Update(true, true, false, 0, false, t0);
DrivingState.Disconnected();
Check("open after iRacing disconnects", !DrivingState.IsDrivingAt(t0 + 10));

DrivingState.Reset();
ReplayState.Set(false, 1, false);
#endif

// ------------------------------------------------------------------------------------------------ synthetic field
const string TrackLen = "5.00 km";
const double LenM = 5000;
Driver Car(int idx, float pct, int pos, string cls = "GT3", bool pit = false, bool off = false, int laps = 5, float best = 100f, double? interval = 3.0)
    => new(idx, $"Driver {idx}", $"D{idx}", "DD", (idx + 10).ToString(), "Car", "BMW", cls, "#00FF00", "A 4.0", "#0000FF",
           pos, pos, laps + 1, pct, laps, 0, best, best + 1, interval, interval, pit, true, false, false, 2000, OffTrack: off);
RaceSnapshot Snap(double tod, List<Driver> cars, Driver? player, Driver? camera, FlagState flags, int lapsRemaining = 20)
{
    var byClass = cars.GroupBy(c => c.ClassName).Select(g => new DriverClass(g.Key, "#00FF00", g.OrderBy(d => d.PositionInClass).ToList())).ToList();
    return new RaceSnapshot(true, DateTime.UtcNow, 4, "Race", RacePhase.Broadcast, flags, 6, 26, lapsRemaining, 3000, tod, 20, 30, "C", false,
        "synthetic_track", "Synthetic Track", "Synthetic Track", null, null, cars.Count, player?.CarIdx ?? -1, camera?.CarIdx ?? -1,
        player, cars.FirstOrDefault(), camera, byClass) { Extras = SessionExtras.Empty with { TrackLength = TrackLen }, SessionNum = 2 };
}

// runs a yellow-flag scenario: speeds in km/h per car, 0.1 s steps; returns (yellow seen, cause at first yellow)
(bool Yellow, string Cause) RunYellow(int n, Func<int, float> kmh, Func<int, float> startPct, int playerIdx, bool playerOnTrack,
                                     bool useCamera = true, double seconds = 6, FlagState? flags = null,
                                     Func<int, double, bool>? inPit = null, Func<int, bool>? off = null, double flagAt = 0)
{
    var y = new YellowFlagCallout();
    var pct = new float[n]; for (int i = 0; i < n; i++) pct[i] = startPct(i);
    double tod = 50000; bool seen = false; string cause = "";
    for (int step = 0; step <= seconds * 10; step++)
    {
        double el = step * 0.1;
        var cars = new List<Driver>();
        for (int i = 0; i < n; i++)
        {
            float p = (i == playerIdx && !playerOnTrack) ? -1f : pct[i];
            cars.Add(Car(i, p, i + 1, pit: inPit?.Invoke(i, el) ?? false, off: off?.Invoke(i) ?? false));
        }
        var player = cars[playerIdx];
        var snap = Snap(tod, cars, player, useCamera ? player : null, el >= flagAt ? flags ?? FlagState.Empty : FlagState.Empty);
        y.Tick(snap, el * 1000);
        if (YellowFlagCallout.YellowNow && !seen) { seen = true; cause = YellowFlagCallout.CurrentCause; }
        for (int i = 0; i < n; i++) { pct[i] += (float)(kmh(i) / 3.6 * 0.1 / LenM); pct[i] -= MathF.Floor(pct[i]); }
        tod += 0.1;
    }
    return (seen, cause);
}

Console.WriteLine("== yellow flag regression checks (synthetic field, 5.00 km track, 0.1 s ticks) ==");
// 20 cars at 200 km/h, 250 m apart; the player is car 0 at 10 %
float Spread(int i) => 0.10f + i * 0.05f;
var r = RunYellow(20, i => 200, Spread, 0, true);
Check("clean field at speed: no yellow", !r.Yellow);
r = RunYellow(20, i => i == 4 ? 20 : 200, Spread, 0, true);
Check("car crawling at 20 km/h 1 km ahead: yellow", r.Yellow, r.Cause);
Check("  ... and the cause names that car", r.Cause.StartsWith("CAR #14"), r.Cause);
r = RunYellow(20, i => i == 12 ? 0 : 200, Spread, 0, true);
Check("car stopped 3 km ahead: no yellow for us", !r.Yellow, r.Cause);
r = RunYellow(3, i => i == 1 ? 0 : 200, i => 0.10f + i * 0.05f, 0, true);
Check("3-car field, one stopped 250 m ahead: yellow", r.Yellow, r.Cause);
r = RunYellow(20, i => 10, Spread, 0, true);
Check("whole field crawling (formation / red): no yellow", !r.Yellow);
r = RunYellow(20, i => i == 4 ? 15 : 200, Spread, 0, true, inPit: (i, el) => i == 4 && el < 1.0);
Check("car leaving pit road slowly ahead (pit exit lane): no yellow", !r.Yellow, r.Cause);

// ------------------------------------------------------------------------------------------------ #12 story cards
Console.WriteLine("== story-card trigger regression check (synthetic race) ==");
// a 10-car GT3 race, everyone 5 s apart (calm); a new class fastest lap at t = 30 s
List<Driver> Field(float leaderBest, int laps) => Enumerable.Range(0, 10)
    .Select(i => Car(i, 0.1f + i * 0.08f, i + 1, laps: laps, best: i == 0 ? leaderBest : 101f + i, interval: i == 0 ? null : 5.0)).ToList();
int runNo = 0; double clock = 0;
double miniAtS = -1; StoryKind miniKind = StoryKind.None; bool miniWithFull = false;
StoryKind RunStory(FlagState flagsAt30, bool calloutAt30, double clearAtS, double endS, out double startedAtS,
                   double battleFromS = -1, double battleToS = -1, bool driving = false)
{
    clock += 600000; double now = clock; startedAtS = -1;   // ten minutes on: the last run's card has finished
    miniAtS = -1; miniKind = StoryKind.None; miniWithFull = false;
#if !BEFORE
    DrivingState.Reset();   // the gate's 3 s release is wall-clock: a driving run must not leak into the next one
#endif
    // a new session number for each run resets the director
    for (int step = 0; step <= endS * 10; step++)
    {
        double s = step * 0.1;
        float best = s < 30 ? 100f : 99.5f;
        var cars = Field(best, 5);
        bool blocked = s >= 30 && s < clearAtS;
        var snap = Snap(50000 + s, cars, cars[4], cars[4], blocked ? flagsAt30 : FlagState.Empty) with { SessionNum = 100 + runNo };
        PresentationState.CalloutActive = blocked && calloutAt30;
        bool battle = battleFromS >= 0 && s >= battleFromS && s < battleToS;
#if !BEFORE
        DrivingState.Update(driving, driving, !driving, driving ? 0 : 3000, false, Environment.TickCount64);
#endif
        StoryDirector.Tick(snap, now, battle);
        if (StoryDirector.Current == StoryKind.FastestLaps && startedAtS < 0) startedAtS = s;
#if !BEFORE
        if (StoryDirector.MiniVisible && miniAtS < 0) { miniAtS = s; miniKind = StoryDirector.Mini; }
        if (StoryDirector.MiniVisible && StoryDirector.Visible) miniWithFull = true;
#endif
        now += 100;
    }
    clock = now;
    runNo++;
    PresentationState.CalloutActive = false;
    return StoryDirector.Current;
}
RunStory(FlagState.Empty, false, 0, 40, out var at);
Check("new class fastest lap, calm, green: Fastest Laps takes over at once", at >= 30 && at < 31, $"started {at:0.0} s");

#if !BEFORE
Console.WriteLine("== #338 no story cards in a battle while driving live ==");
RunStory(FlagState.Empty, false, 0, 45, out at, battleFromS: 25, battleToS: 40, driving: true);
Check("driving, battle 25-40 s, fastest lap at 30 s: held until the battle ends (40 s)", at >= 40, $"started {at:0.0} s");
RunStory(FlagState.Empty, false, 0, 110, out at, battleFromS: 25, battleToS: 100, driving: true);
Check("driving, battle 25-100 s: the 30 s fastest lap is stale (> 60 s) and dropped", at < 0, $"started {at:0.0} s");
RunStory(FlagState.Empty, false, 0, 45, out at, battleFromS: 25, battleToS: 40, driving: false);
Check("replay / spectating, battle: fastest lap still takes over through it at 30 s (unchanged)", at >= 30 && at < 31, $"started {at:0.0} s");
StoryDirector.NoCardsInBattleWhenDriving = false;
RunStory(FlagState.Empty, false, 0, 45, out at, battleFromS: 25, battleToS: 40, driving: true);
Check("switch off (noCardsInBattleWhenDriving false): old behaviour, plays through the battle at 30 s", at >= 30 && at < 31, $"started {at:0.0} s");
StoryDirector.NoCardsInBattleWhenDriving = true;
// a card already up when a battle starts while driving bows out
RunStory(FlagState.Empty, false, 0, 33, out at, battleFromS: 32, battleToS: 99, driving: true);
Check("driving: a card on screen when the battle starts bows out", StoryDirector.Current == StoryKind.None || StoryDirector.ExitAtMs <= clock, $"current {StoryDirector.Current}");
DrivingState.Reset();

Console.WriteLine("== #339 mini story cards in a battle (preview switch miniCardsInBattle) ==");
RunStory(FlagState.Empty, false, 0, 45, out at, battleFromS: 25, battleToS: 100, driving: true);
Check("switch OFF (default): no mini card in the battle", miniAtS < 0, $"mini at {miniAtS:0.0} s");
StoryDirector.MiniCardsInBattle = true;
RunStory(FlagState.Empty, false, 0, 45, out at, battleFromS: 25, battleToS: 100, driving: true);
Check("switch ON, driving, battle from 25 s, fastest lap at 30 s: mini Fastest Laps 8 s into the battle (33 s)", miniAtS >= 33 && miniAtS < 34 && miniKind == StoryKind.FastestLaps, $"mini {miniKind} at {miniAtS:0.0} s");
Check("switch ON: the full card stays held while the mini plays", at < 0, $"full started {at:0.0} s");
RunStory(FlagState.Empty, false, 0, 120, out at, battleFromS: 25, battleToS: 100, driving: true);
Check("switch ON: the mini card is gone by the time the battle ends (6.4 s long)", !miniWithFull);
Check("switch ON: the fastest lap told as a mini is not replayed in full after the battle", at < 0, $"full started {at:0.0} s");
RunStory(FlagState.Empty, false, 0, 45, out at, battleFromS: 25, battleToS: 100, driving: false);
Check("switch ON, replay / spectating: no mini (the full card plays through, unchanged)", miniAtS < 0 && at >= 30 && at < 31, $"mini {miniAtS:0.0} s, full {at:0.0} s");
StoryDirector.MiniCardsInBattle = false;
DrivingState.Reset();
#endif

Console.WriteLine($"\n{passes} passed, {fails} failed");
return fails == 0 ? 0 : 1;
