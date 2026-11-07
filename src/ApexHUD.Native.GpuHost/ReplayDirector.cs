using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// The automatic replay director (2026-10-24): steers iRacing's camera through the SDK broadcast
/// messages while Angelo watches a replay or spectates, so the broadcast cuts itself like TV.
///
/// <para><b>Rules.</b> Replays and spectating only: the moment the player is in the car
/// (IsOnTrack / IsOnTrackCar and not a replay) it switches the setting OFF and lets go. Only one
/// thing steers the camera: the control page's Director switch, OFF by default and OFF again after
/// every restart. If someone changes the camera by hand it backs off for 20 s.</para>
///
/// <para><b>Cost.</b> Its own low-priority thread. While switched off the thread sleeps on an event
/// (no polling at all); while on it wakes 4x a second, reads the snapshot the overlay already built
/// plus a few telemetry values, and sends at most one camera message per decision. It never touches
/// the render loop.</para>
///
/// <para><b>Shots, most important first.</b> Finish (hold the leader to the line, then the user's
/// car) → race start (trackside TV camera on the pack; never the blimp) → incident (jump back a few seconds, slow-motion
/// replay, back to where we were) → pass (the car that gained) → battle (the two close cars,
/// TV1/TV2/TV3 alternating every 8-12 s) → quiet (leader, the user's car, pit stops).</para>
/// </summary>
internal sealed partial class ReplayDirector : IDisposable   // #769: broadcast-mode rules in ReplayDirector.Broadcast.cs
{
    readonly RaceStateService rs;
    readonly Thread thread;
    readonly AutoResetEvent wake = new(false);
    volatile bool running = true;
    readonly Random rng = new();

    // camera groups by role, refreshed when the session info changes
    int infoUpdate = int.MinValue;
    int gRollBar = -1;
    int gWide = -1, gTv1 = -1, gTv2 = -1, gTv3 = -1, gPit = -1, gTvStatic = -1, gTvMixed = -1, gChopper = -1, gGearbox = -1, gNose = -1, gGyro = -1;
    IReadOnlyList<RaceStateService.CameraGroup> groups = Array.Empty<RaceStateService.CameraGroup>();
    IReadOnlyDictionary<int, int> carNumbers = new Dictionary<int, int>();

    // what we put on screen
    string shot = "";
    int shotCar = -1, shotGroup = -1;
    long lastFixMs;
    long shotAtMs, shotHoldMs;
    long overrideUntilMs;
    int battleFlip, myFlip;

    // event memory
    readonly Dictionary<int, int> lastClassPos = new();
    readonly Dictionary<int, bool> lastOff = new();
    int lastSessionState = -1, lastSessionNum = -1;
    long greenAtMs = -1;
    double greenDist = double.NaN;   // the leader's lap distance (laps + fraction) at the green
    long lastIncidentMs = -1_000_000, lastPassMs = -1_000_000;
    int finishStage;               // 0 = racing, 1 = holding the leader to the line, 2 = the user's car, 3 = done
    int leaderLapAtFlag = -1;
    int quietTurn;

    // slow-motion replay in progress
    bool inReplay;
    /// <summary>#423: a slow-motion incident replay is playing (the overlay hides).</summary>
    public static volatile bool SlowMoActive;
    long replayEndMs;
    int resumeSessionNum;
    double resumeSessionTime;
    long replayStartedMs;

    public string Status { get; private set; } = "Off";
    public string Why { get; private set; } = "Switch the director on in the control page to let Grandstand pick the shots.";

    public ReplayDirector(RaceStateService rs)
    {
        this.rs = rs;
        ControlState.Changed += () => wake.Set();
        thread = new Thread(Loop) { IsBackground = true, Name = "Grandstand-Director", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    /// <summary>Live status for the control page.</summary>
    public object Describe() => new
    {
        enabled = ControlState.Current.Director,
        status = Status,
        why = Why,
        shot,
        car = shotCar,
        group = groups.FirstOrDefault(g => g.Num == shotGroup)?.Name ?? "",
        groups = groups.Select(g => g.Name).ToArray(),
        replaying = inReplay,
    };

    void Loop()
    {
        while (running)
        {
            if (!ControlState.Current.Director)
            {
                if (inReplay) EndReplay();   // switched off mid slow-mo: put the replay back at normal speed
                Status = "Off"; shot = ""; shotCar = -1;
                // 2026-10-27 (#348, Angelo: "camera director is like froze up"): after it let go for a drive it stayed off
                // when he went back to a replay, so the camera sat on one shot. If it was the director that switched itself
                // off, it now switches itself back on as soon as he's watching again (replay / spectating, out of the car).
                if (autoOff)
                {
                    wake.WaitOne(2000);
                    try { if (!ControlState.Current.Director && WatchingAgain()) TurnBackOn(); } catch { }
                }
                else wake.WaitOne();         // switched off by hand: asleep until the control page changes something
                continue;
            }
            autoOff = false;   // on (by hand or by itself): a later hand switch-off stays off
            try { Tick(); }
            catch (Exception ex) { Status = "Error"; Why = ex.Message; Program.Log("director error: " + ex.Message); }
            wake.WaitOne(250);
        }
    }

    static long Now => Environment.TickCount64;

    void Tick()
    {
        // 2026-10-29 (#9): the highlight reel is steering the camera - hands off until it's done
        if (HighlightReel.Playing) { Status = "Paused"; Why = "The highlight reel is playing - the director waits until it's done."; shotCar = -1; return; }
        if (!rs.IsSessionLive || !rs.SdkReady) { Status = "Waiting"; Why = "Waiting for iRacing (open a replay or spectate a session)."; return; }

        bool replayPlaying = rs.ReadBool("IsReplayPlaying");
        // 2026-10-25: a PAUSED replay (rewinding) also reads "not playing + car on track" - that switched the director
        // off mid-replay. Really driving = the replay is at the live end (iRacing: ReplayFrameNumEnd ~ 0).
        bool atLive = FramesBehind(rs) < 30;
        bool inCar = (rs.ReadBool("IsOnTrack") || rs.ReadBool("IsOnTrackCar")) && atLive;
        // 2026-10-25 (Angelo: "did the camera director just decide to not do onboards any more?"): it had switched itself
        // off at 19:11 while watching a REPLAY FILE (paused at its end reads like being in the car). A replay file can't
        // be driven, so never there; elsewhere it must look like driving for 3 s in a row before it lets go.
        bool replayFile = rs.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase);
        if (inCar && !replayPlaying && !replayFile) { if (inCarSinceMs < 0) inCarSinceMs = Environment.TickCount64; }
        else inCarSinceMs = -1;
        if (inCarSinceMs >= 0 && Environment.TickCount64 - inCarSinceMs > 3000)
        {
            // never fight the driver for the camera: let go and switch the setting off
            inReplay = false; SlowMoActive = false;
            var s = ControlState.Current;
            Program.Log("director: you are in the car - switched off (back on by itself when you watch a replay)");
            autoOff = true;
            Status = "Off"; Why = "Switched itself off: you got in the car. It switches back on by itself when you watch a replay.";
            // 2026-10-27: a full copy, so no setting is ever dropped when it switches off (the hand-typed list kept losing new ones)
            var off = System.Text.Json.JsonSerializer.Deserialize<ControlSettings>(System.Text.Json.JsonSerializer.Serialize(s))!;
            off.Director = false;
            ControlState.Replace(off);
            return;
        }

        RefreshGroups();
        var snap = rs.Current;
        if (!snap.IsConnected || snap.ClassLeaderboard.Count == 0) { Status = "Waiting"; Why = "No cars in this session yet."; return; }
        long now = Now;

        // a slow-motion replay is playing: let it run, then go back
        if (inReplay)
        {
            Status = "On"; Why = "Replaying an incident in slow motion.";
            // 2026-11-06: no onboard / cockpit view over a replay either
            string rcam = rs.CurrentCameraName();
            if (false && UnrealisticCam(rcam) && snap.CameraCarIdx >= 0 && now - lastFixMs > 400 && gTv3 >= 0 && carNumbers.TryGetValue(snap.CameraCarIdx, out var rr))
            { lastFixMs = now; rs.CamToCar(rr, gTv3); Program.Log($"director: replay - replaced {rcam} with a trackside TV camera"); }
            double rst = rs.ReadDouble("ReplaySessionTime");
            if (replayPhase >= 0 && !replayLanded && !double.IsNaN(rst) && rst <= replayIncSt - 3.0 && rst >= replayIncSt - 12) { replayLanded = true; rs.ReplaySpeed(1, slowMotion: false); Program.Log($"director: replay landed at {rst:0.0} (try {replayTries})"); }
            if (replayPhase >= 0 && !replayLanded && now - replayJumpAtMs > 1500 && replayTries < 6)
            {
                replayTries++; replayJumpAtMs = now;
                rs.ReplaySpeed(0, slowMotion: false);
                rs.ReplayToSessionTime(replaySn, replayTargetMs);
                Program.Log($"director: replay jump not landed yet (at {rst:0.0}, want {replayTargetMs / 1000.0:0.0}) - sent again (try {replayTries})");
            }
            if (replayPhase >= 0 && !replayLanded && replayTries >= 6 && now - replayJumpAtMs > 1500) { Program.Log("director: replay jump never landed - back to the race"); replayEndMs = now; }
            if (replayPhase >= 0 && replayLanded && now - replayLogAtMs > 1000) { replayLogAtMs = now; Program.Log($"director: replay at {rst:0.0} (moment {replayIncSt:0.0}, phase {replayPhase})"); }
            bool landed = replayPhase >= 0 && replayLanded && !double.IsNaN(rst);
            if (replayPhase == 0 && landed && rst >= replayIncSt - 1.5) { rs.ReplaySpeed(2, slowMotion: true); replayPhase = 1; Program.Log($"director: replay slow motion at {rst:0.0} (moment {replayIncSt:0.0})"); }
            else if (replayPhase == 1 && landed && rst >= replayIncSt + 2.0) { rs.ReplaySpeed(1, slowMotion: false); replayPhase = 2; Program.Log($"director: replay back to normal speed at {rst:0.0}"); }
            else if (replayPhase == 2 && landed && rst >= replayIncSt + 5.0) replayEndMs = now;
            if (now >= replayEndMs) { replayPhase = -1; EndReplay(); }
            return;
        }

        // 2026-10-25 (Angelo: "it should never show the cockpit cam, that is not realistic"): iRacing puts the replay on
        // the cockpit (or a suspension / chase view) after a jump, a restart or a hand change - replace it straight away
        // with a TV shot of the same car, even while backing off from a manual change
        int curGroup = rs.ReadInt("CamGroupNumber");
        string curName = groups.FirstOrDefault(g => g.Num == curGroup)?.Name ?? "";
        string camName = rs.CurrentCameraName();
        // 2026-11-06 (Angelo: "don't switch drivers 2 seconds after going to an onboard camera"): when iRacing goes onboard
        // inside a TV set, the director stays on that driver for at least 10 s
        bool obCam = UnrealisticCam(camName) && !camName.Contains("Chopper", StringComparison.OrdinalIgnoreCase) && !camName.Contains("Blimp", StringComparison.OrdinalIgnoreCase);
        if (obCam && (!wasObCam || obCamCar != snap.CameraCarIdx)) { obCamSinceMs = now; obCamCar = snap.CameraCarIdx; }
        wasObCam = obCam;
        // 2026-11-06 (#857, Angelo: "the chasing car in a battle should not show the gearbox - nothing to see behind"): in a fight,
        // a rear-facing camera on the chaser moves to the same camera on the car ahead (which looks back AT the chaser), and a
        // forward-facing camera on the car ahead moves to the same camera on the chaser (which looks AT the car ahead)
        if (shot.StartsWith("battle", StringComparison.Ordinal) && pairChaser >= 0 && pairAhead >= 0 && now - lastBattleFlipMs > 1500
            && camName.Length > 0)
        {
            bool rear = camName.Contains("Gearbox", StringComparison.OrdinalIgnoreCase) || camName.Contains("Rear", StringComparison.OrdinalIgnoreCase);
            bool fwd = camName.Contains("Nose", StringComparison.OrdinalIgnoreCase) || camName.Contains("Roll Bar", StringComparison.OrdinalIgnoreCase)
                || camName.Contains("Gyro", StringComparison.OrdinalIgnoreCase) || camName.Contains("Cockpit", StringComparison.OrdinalIgnoreCase);
            int to = rear && snap.CameraCarIdx == pairChaser ? pairAhead : fwd && snap.CameraCarIdx == pairAhead ? pairChaser : -1;
            if (to >= 0 && carNumbers.TryGetValue(to, out var toRaw))
            {
                lastBattleFlipMs = now;
                if (rs.CamToCar(toRaw, rs.ReadInt("CamGroupNumber"), rs.ReadInt("CamCameraNumber")))
                {
                    shotCar = to;
                    Program.Log($"director: battle - {camName} pointed the wrong way, same camera on the other car");
                }
            }
        }
        // 2026-11-06 (#851): a saved replay's incident list is read once, up front (the director makes no cuts meanwhile)
        if (ControlState.Current.DirectorReplays && TickIncidentIndex(snap, now)) { shotCar = -1; shotAtMs = now; return; }
        if (Unrealistic(curName) && snap.CameraCarIdx >= 0 && now - lastFixMs > 400)
        {
            lastFixMs = now;
            // 2026-10-27 (#408, Angelo: "not the cockpit cam during replays - only roll bar, nose, gearbox, gyro"): the swap
            // goes to an onboard of the same car (a TV camera only if the car has none)
            int tv = gTv3 >= 0 ? gTv3 : gTvStatic >= 0 ? gTvStatic : gTv1;
            if (tv >= 0 && carNumbers.TryGetValue(snap.CameraCarIdx, out var rawNum) && rs.CamToCar(rawNum, tv))
            {
                Program.Log($"director: replaced the {curName} / {camName} camera with a trackside TV camera");
                shot = "no cockpit"; shotCar = snap.CameraCarIdx; shotGroup = tv; shotAtMs = now; shotHoldMs = 6000;
                return;
            }
        }

        // someone changed the camera by hand: back off for a while
        if (shotCar >= 0 && now - shotAtMs > 1500 && snap.CameraCarIdx >= 0 && snap.CameraCarIdx != shotCar)
        {
            overrideUntilMs = now + 20000;
            shotCar = -1; shot = "manual";
            Program.Log("director: camera changed by hand - backing off 20 s");
        }
        if (now < overrideUntilMs) { Status = "Paused"; Why = "You changed the camera - the director waits 20 s before cutting again."; return; }
        Status = "On"; Why = "Picking the shots.";

        bool race = snap.SessionTypeName.Contains("race", StringComparison.OrdinalIgnoreCase);
        int state = snap.SessionState ?? -1;
        if (snap.SessionNum != lastSessionNum) { lastSessionNum = snap.SessionNum; finishStage = 0; greenAtMs = -1; greenDist = double.NaN; lastClassPos.Clear(); lastOff.Clear(); missed.Clear(); lastPitState.Clear(); finishStage2AtMs = -1; pairChaser = pairAhead = -1; }
        if (state != lastSessionState)
        {
            if (state == 4 && lastSessionState is 1 or 2 or 3 && race) { greenAtMs = now; greenDist = snap.Leader is { } gl ? gl.LapsCompleted + Math.Max(0, gl.LapDistPct) : double.NaN; openTurn = 0; }   // the green flag
            if (state == 5 && race && finishStage == 0) { finishStage = 1; leaderLapAtFlag = snap.Leader?.LapsCompleted ?? -1; NoteClassLeadersAtFlag(snap); }
            lastSessionState = state;
        }
        bool canCut = now - shotAtMs >= shotHoldMs && !(wasObCam && obCamCar == snap.CameraCarIdx && now - obCamSinceMs < 10000);
        // 2026-10-27 (#394): from the end of the opener music, through the hold and the track / race cards: the blimp
        // (whole-field mode too)
        if (ControlState.Current.DirectorField && PreRaceCards.ScenicNow && gWide >= 0 && (UserCar(snap) ?? snap.Leader) is { } sc)
        {
            if (shot != "scenic") Cut("scenic", sc.CarIdx, gWide, 6000, force: true);
            return;
        }
        // 2026-11-06 (#896, Angelo: "during qualifying didn't we have it set up to prefer onboards?"): practice and qualifying
        // always use the your-car director (onboards of his car - roll bar, nose, gearbox, gyro - with a TV shot every fourth),
        // even with whole-field broadcast mode on; the field director is for the race
        if (!ControlState.Current.DirectorField || !race) { MyCarOnly(snap, race, now, canCut); return; }
        // #769 broadcast mode: air-time book-keeping, and no cut while the battle on air is mid-pass
        AccountAir(snap, now);
        if (canCut && HoldForPass(snap, now)) canCut = false;
        // 2026-11-06 (Angelo: "it shows two seconds of a battle and then goes to a car in free air"): while the fight on air
        // is still a fight, nothing routine takes the camera away from it
        onBattleNow = race && BattleAlive(snap);
        TrackOffs(snap, now);
        // 2026-11-06 (#851): a known incident a few seconds ahead -> be on that car live, then replay it
        if (race && ControlState.Current.DirectorReplays && IncidentAhead(snap, now)) return;

        // ---- finish: leader to the line, then the user's car ----
        if (race && (finishStage > 0 || IsFinalLap(snap)))
        {
            var leader = snap.Leader;
            if (finishStage == 0 || finishStage == 1)
            {
                if (finishStage == 1 && leader != null && leader.LapsCompleted > leaderLapAtFlag) { finishStage = 2; shotHoldMs = 0; }
                else { if (leader != null) Cut("finish: leader", leader.CarIdx, gTv1, 4000, force: shot != "finish: leader"); return; }
            }
            if (finishStage == 2)
            {
                // #769: every class winner crossing the line first (closest to the line first), then the user's car
                if (NextClassFinisher(snap, now) is { } cw)
                {
                    if (shot != "finish: class winner" || shotCar != cw.CarIdx) Cut("finish: class winner", cw.CarIdx, gTv1, 4000, force: true);
                    return;
                }
                var me = UserCar(snap);
                if (me != null && me.CarIdx != snap.Leader?.CarIdx) { Cut("finish: your car", me.CarIdx, gTv1, 15000, force: shot != "finish: your car"); }
                finishStage = 3;
                return;
            }
            return;   // stage 3: hold what we have through the cool-down lap
        }

        // ---- race start: the helicopter at the green, then only active cameras to halfway round the first lap ----
        if (Opening(snap, now))
        {
            var leader = snap.Leader;
            if (leader != null) OpeningCut(snap, now, StartTarget(snap)?.CarIdx ?? leader.CarIdx, canCut);
            lastClassPos.Clear();   // the opening shuffle isn't a "pass"
            Remember(snap);
            return;
        }

        // ---- incidents: a car off the track ----
        if (race && ControlState.Current.DirectorReplays && now - lastIncidentMs > 120000 && !onBattleNow && indexState != 2)
        {
            foreach (var d in AllCars(snap))
            {
                bool was = lastOff.TryGetValue(d.CarIdx, out var w) && w;
                if (d.OffTrack && !d.IsInPit && d.LapsCompleted >= 1 && (d.PositionInClass is > 0 and <= 5 || d.CarIdx == snap.PlayerCarIdx) && RealIncident(d, now))   // 2026-11-06 (#879): only incidents that matter   // 2026-11-06: a real moment (off 1.2 s+ and slowed right down), not a wide line
                {
                    lastIncidentMs = now;
                    Remember(snap);
                    StartReplay(snap, d);
                    return;
                }
            }
        }

        // ---- passes: the car that just gained a place ----
        if (race) NoteMissedPasses(snap, now);   // 2026-11-06: every pass is replayed from before it happened - no late live cut to a car already clear   // #769: replayed in the next lull
        if (false && race && now - lastPassMs > 15000 && canCut)
        {
            foreach (var d in AllCars(snap))
                if (lastClassPos.TryGetValue(d.CarIdx, out var before) && d.PositionInClass > 0 && d.PositionInClass < before && !d.IsInPit
                    && d.LapsCompleted >= 1 && (d.IntervalSeconds ?? 99) < 1.5)
                {
                    lastPassMs = now;
                    Remember(snap);
                    Cut("pass", d.CarIdx, gTv1, 8000);
                    return;
                }
        }
        Remember(snap);
        // #769: restart running order, top-3 pit stops, leader revisit, missed-moment replays
        if (race && BroadcastExtras(snap, now, canCut && !onBattleNow)) return;

        // ---- your own fight (2026-10-25, Angelo): a class car close BEHIND you -> the gearbox camera (looking back
        // at him); close AHEAD -> the nose or gyro camera (looking at him). Onboards only when something is happening
        // in front of / behind that lens, short, and swapped with a TV shot of the pair, so the cards still get air.
        if (race && canCut && MyFight(snap) is var (mine, _, behind) && !(ProducerDesk.TopFight(snap) is { } tm && tm.Chaser != mine.CarIdx && tm.Ahead != mine.CarIdx && tm.Score >= 15))
        {
            FightCut(mine.CarIdx, behind);
            return;
        }

        // ---- battles: the two close cars, alternating the TV cameras ----
        if (race && canCut && onBattleNow && ProducerDesk.TopFight(snap) is { } tb && tb.Chaser != pairChaser && tb.Chaser != pairAhead && tb.Score >= 20
            && AllCars(snap).FirstOrDefault(d => d.CarIdx == pairChaser) is { } oc && AllCars(snap).FirstOrDefault(d => d.CarIdx == pairAhead) is { } oa
            && ((oc.PositionInClass > oa.PositionInClass ? oc : oa).IntervalSeconds ?? 9) > 0.7)
        { onBattleNow = false; Program.Log("director: a closer fight elsewhere - switching to it"); }
        if (race && canCut && onBattleNow && AllCars(snap).FirstOrDefault(d => d.CarIdx == pairChaser) is { } kc
            && AllCars(snap).FirstOrDefault(d => d.CarIdx == pairAhead) is { } ka)
        {
            BroadcastBattleCut(kc, ka);   // same fight, next angle
            return;
        }
        // 2026-11-06 (Angelo: "it should know when the class leader is losing time to traffic"): a class leader coming up
        // behind slower or lapped cars is a story - shown with a tag, at most every 2 minutes
        if (race && canCut && Now - lastTrafficMs > 120000 && LeaderInTraffic(snap) is var (tl, tt))
        {
            lastTrafficMs = Now;
            Cut($"class leader in traffic #{tl.CarNumber}", tl.CarIdx, gTv1 >= 0 ? gTv1 : gTv2, 10000);
            ApexHUD.Native.Overlays.DeskBoard.ShowTag(new ApexHUD.Native.Overlays.NameTag(trafficTagId--, $"{tl.ClassName} leader in traffic",
                $"#{tl.CarNumber}  {CamCarChip.FullName(tl)}", $"Coming up on #{tt.CarNumber} {CamCarChip.FullName(tt)} \u00B7 {tt.ClassName}", tl.ClassColor), 8);
            Program.Log($"director: {tl.ClassName} leader #{tl.CarNumber} in traffic behind #{tt.CarNumber}");
            return;
        }
        // 2026-11-06 (#875): the booth's storylines are shots too - the best one not shown in the last 4 minutes, unless a
        // fight is hot (a pass could come any second), then the fight
        if (race && canCut && !(ProducerDesk.TopFight(snap) is { } hot && hot.Score >= 12)
            && ProducerDesk.StoryShots(snap).FirstOrDefault(x => !storyShown.TryGetValue(x.Key, out var at) || Now - at > 240000) is { } ss
            && AllCars(snap).Any(x => x.CarIdx == ss.Car))
        {
            storyShown[ss.Key] = Now;
            Cut("story: " + ss.Key.Split(':')[0], ss.Car, gTv1 >= 0 ? gTv1 : gTv2, rng.Next(14000, 18001));
            Program.Log($"director: storyline on screen - {ss.Text}");
            return;
        }
        if (race && canCut && ProducerDesk.TopFight(snap) is { } tf && AllCars(snap).FirstOrDefault(x => x.CarIdx == tf.Chaser) is { } fc
            && AllCars(snap).FirstOrDefault(x => x.CarIdx == tf.Ahead) is { } fa)
        {
            if (fc.CarIdx != pairChaser || fa.CarIdx != pairAhead) Program.Log($"director: following the top fight - #{fc.CarNumber} on #{fa.CarNumber} ({fc.IntervalSeconds:0.0}s, score {tf.Score:0})");
            BroadcastBattleCut(fc, fa);
            return;
        }
        if (race && canCut && FindBattleBalanced(snap) is var (chaser, ahead))
        {
            // #769: wide establishing shot for a new pair, onboards capped at ~30 % of air (ReplayDirector.Broadcast.cs)
            BroadcastBattleCut(chaser, ahead);
            return;
        }

        // ---- quiet moments: leader, the user's car, pit stops ----
        if (canCut)
        {
            quietTurn++;
            var pitting = AllCars(snap).FirstOrDefault(d => d.IsInPit && d.LapsCompleted >= 1);
            var me = UserCar(snap);
            switch (quietTurn % 3)
            {
                case 0 when pitting != null: Cut("pit stop", pitting.CarIdx, gPit >= 0 ? gPit : gTv1, 14000); break;
                case 1 when me != null: Cut("your car", me.CarIdx, gTv2 >= 0 ? gTv2 : gTv1, 16000); break;
                default: if (snap.Leader is { } l) Cut("leader", l.CarIdx, gTv1, 15000); break;
            }
        }
    }

    /// <summary>Default mode (2026-10-25): the camera never leaves the user's car - the director only picks the
    /// angle. A class car within 1 s behind = gearbox, within 1 s ahead = nose / gyro (short, swapped with a TV shot);
    /// in the pits = pit lane; otherwise TV / chase cameras (no onboards, so the story cards get air).</summary>
    void MyCarOnly(RaceSnapshot snap, bool race, long now, bool canCut)
    {
        var me = UserCar(snap);
        if (me == null) { Status = "Waiting"; Why = "Waiting for your car."; return; }
        Why = "Following your car.";
        // 2026-10-25 (Angelo: "the director can pick the blimp cam when the today's race card and the track logo card play")
        if (PreRaceCards.ScenicNow && gWide >= 0)
        {
            if (shot != "scenic") Cut("scenic", me.CarIdx, gWide, 6000, force: true);
            return;
        }
        if (Opening(snap, now)) { OpeningCut(snap, now, me.CarIdx, canCut); Remember(snap); return; }
        // your own spin or crash: slow-motion replay (when switched on)
        bool was = lastOff.TryGetValue(me.CarIdx, out var w) && w;
        if (race && ControlState.Current.DirectorReplays && me.OffTrack && !was && !me.IsInPit && me.LapsCompleted >= 1 && now - lastIncidentMs > 45000)
        { lastIncidentMs = now; Remember(snap); StartReplay(snap, me); return; }
        // 2026-10-27 (#431, Angelo: "if crashes are happening ahead I want it to switch to onboard quickly"): a car that
        // has just gone off within ~800 m in front of you (on track, not in the pits) -> straight to your onboard
        // (roll bar, nose or gyro - never the cockpit), so the TV sees the crash coming. No wait for a cut window.
        if (race && !me.IsInPit && !me.OffTrack && now - lastAheadMs > 12000 && CrashAhead(snap, me) is { } wreck)
        {
            int ob = gRollBar >= 0 ? gRollBar : gNose >= 0 ? gNose : gGyro;
            if (ob >= 0)
            {
                lastAheadMs = now;
                Remember(snap);
                Cut($"crash ahead (#{wreck.CarNumber})", me.CarIdx, ob, 8000, force: true);
                Program.Log($"director: crash ahead - #{wreck.CarNumber} off, onboard on your car");
                return;
            }
        }
        Remember(snap);
        if (!canCut) return;
        if (me.IsInPit) { Cut("your pit stop", me.CarIdx, gPit >= 0 ? gPit : gTv1, 9000); return; }
        if (race && MyFight(snap) is var (mine, _, behind))
        {
            FightCut(mine.CarIdx, behind);
            return;
        }
        quietTurn++;
        // 2026-10-27 (#407, Angelo: "prefer onboards in qualifying"): outside a race (qualifying, practice) the quiet
        // shots are onboards - roll bar, nose, gearbox, gyro in turn - with a TV shot every fourth one for variety
        if (!race && quietTurn % 4 != 0)
        {
            int[] obq = new[] { gRollBar, gGyro, gNose, gRollBar, gGyro, gGearbox }.Where(g => g >= 0).ToArray();   // #907: more gyro + roll bar
            if (obq.Length > 0) { Cut("your car: onboard", me.CarIdx, obq[quietTurn % obq.Length], rng.Next(10000, 14001)); return; }
        }
        // 2026-10-25 (Angelo: "we have not seen an onboard in ages"): every third quiet shot is an onboard of our car
        // (roll bar, nose, gyro in turn), 8-10 s, unless a story card is up (they get the TV shots)
        if (race && quietTurn % 3 == 0 && ApexHUD.Native.Overlays.StoryDirector.Current == ApexHUD.Native.Overlays.StoryKind.None)
        {
            int[] ob = new[] { gRollBar, gGyro, gRollBar, gNose, gGyro }.Where(g => g >= 0).ToArray();   // #907
            if (ob.Length > 0) { Cut("your car: onboard", me.CarIdx, ob[(quietTurn / 3) % ob.Length], rng.Next(8000, 10001)); return; }
        }
        // 2026-10-25 (Angelo: "I don't want it using the non-realistic cameras like the chase cams - realistic broadcast"):
        // trackside TV cameras, the static / mixed TV sets and the helicopter only
        int[] calm = new[] { gTv1, gTv2, gTvStatic, gTv3, gTvMixed }.Where(g => g >= 0).ToArray();
        Cut("your car", me.CarIdx, calm.Length > 0 ? calm[quietTurn % calm.Length] : -1, rng.Next(10000, 14001));
    }

    /// <summary>2026-10-25 (Angelo: "the green flag should never cut to the blimp, it should cut to something that has a
    /// good angle on all the cars"): the trackside TV camera at the start, which frames the pack as it comes past.</summary>
    int StartCam() => gChopper >= 0 ? gChopper : gTv1 >= 0 ? gTv1 : gTvStatic >= 0 ? gTvStatic : gTv2;

    // 2026-10-25 (Angelo: "the race director should never use passive cameras at the green - helicopter at the green,
    // then any active cam or onboard cam until the end of the first half of the first lap"): the opening phase lasts
    // from the green until the leader is half a lap further on. The helicopter takes the green; after ~10 s the
    // onboards (nose, gyro, roll bar, gearbox) and the helicopter take turns - no static trackside cameras.
    long inCarSinceMs = -1;
    volatile bool autoOff;   // true only while off because the director let go for a drive

    /// <summary>Watching, not driving: a replay file, a replay playing, or rewound away from the live end while out of the car.</summary>
    bool WatchingAgain()
    {
        if (!rs.IsSessionLive || !rs.SdkReady) return false;
        if (rs.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase)) return true;
        if (rs.ReadBool("IsReplayPlaying")) return true;
        bool atLive = FramesBehind(rs) < 30;
        bool onTrack = rs.ReadBool("IsOnTrack") || rs.ReadBool("IsOnTrackCar");
        return !atLive && !onTrack;
    }

    void TurnBackOn()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ControlState.Current);
        var copy = System.Text.Json.JsonSerializer.Deserialize<ControlSettings>(json)!;   // every setting kept as it is
        copy.Director = true;
        autoOff = false; inCarSinceMs = -1;
        Program.Log("director: watching a replay again - switched back on");
        ControlState.Replace(copy);
    }
    int openTurn;
    bool Opening(RaceSnapshot snap, long now)
    {
        if (greenAtMs < 0) return false;
        if (now - greenAtMs < 12000) return true;
        if (snap.Leader is not { } l || double.IsNaN(greenDist)) return now - greenAtMs < 45000;
        double gone = l.LapsCompleted + Math.Max(0, l.LapDistPct) - greenDist;
        return gone >= 0 && gone < 0.5 && now - greenAtMs < 300000;
    }

    void OpeningCut(RaceSnapshot snap, long now, int carIdx, bool canCut)
    {
        if (now - greenAtMs < 10000)
        {
            if (shot != "start") Cut("start", carIdx, StartCam(), 10000, force: true);
            return;
        }
        if (shot == "start" || canCut)
        {
            int[] active = new[] { gChopper, gTv1, gTv2, gTv3 }.Where(g => g >= 0).ToArray();
            if (active.Length == 0) return;
            int g = active[openTurn++ % active.Length];
            Cut("opening lap", carIdx, g, rng.Next(6000, 8501), force: shot == "start");
        }
    }

    /// <summary>At the start the TV camera follows the third car, so the leaders are ahead of it in the frame and the
    /// pack behind it.</summary>
    static Driver? StartTarget(RaceSnapshot snap) => AllCars(snap).Where(d => d.Position > 0).OrderBy(d => d.Position).Skip(2).FirstOrDefault();

    /// <summary>Car-mounted views no TV broadcast uses: the cockpit, suspension and chase cameras. (Roll bar, gearbox,
    /// nose and gyro stay: real broadcasts use those onboards - Angelo 2026-10-25: "roll bar is okay, that is a real camera".)</summary>
    /// <summary>2026-11-06: a camera (not group) name that is an onboard / cockpit / chase / aerial view.</summary>
bool wasObCam; long obCamSinceMs = -1_000_000; int obCamCar = -1;

    long lastBattleFlipMs = -1_000_000;

    static bool UnrealisticCam(string cam)
    {
        if (cam.Length == 0) return false;
        foreach (var k in new[] { "Cockpit", "Gyro", "Nose", "Roll Bar", "Gearbox", "Susp", "Chase", "Chopper", "Blimp", "Wing", "In Car" })
            if (cam.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool Unrealistic(string group) =>
        group.Length > 0 && (group.Equals("Cockpit", StringComparison.OrdinalIgnoreCase)
            || group.Contains("Susp", StringComparison.OrdinalIgnoreCase) || group.Contains("Chase", StringComparison.OrdinalIgnoreCase)
            // 2026-11-06 (Angelo: real-world TV cameras only): every onboard view is swapped for a TV shot too
            || group.Contains("Cockpit", StringComparison.OrdinalIgnoreCase) || group.Contains("In Car", StringComparison.OrdinalIgnoreCase));


    /// <summary>
    /// The user's own fight (2026-10-25, Angelo: "if a car is behind me - defending - prioritise the gearbox camera; if
    /// it's ahead, nose / gyro / roll bar; we need more variety in the battle cams"). Onboards and TV shots take turns:
    /// defending = the gearbox camera (looking back at him) every onboard turn; attacking = nose, gyro and roll bar in
    /// rotation. The TV shots rotate too (TV1, TV2, TV3, TV mixed, helicopter), so no single camera dominates.
    /// </summary>
    void FightCut(int carIdx, bool behind)
    {
        myFlip++;
        if (myFlip % 2 == 1)
        {
            int[] pool = behind ? new[] { gGearbox } : new[] { gGyro, gRollBar, gNose, gGyro, gRollBar };   // #907
            pool = pool.Where(g => g >= 0).ToArray();
            if (pool.Length > 0)
            {
                int g = pool[(myFlip / 2) % pool.Length];
                Cut(behind ? "your fight: gearbox" : "your fight: onboard", carIdx, g, rng.Next(6000, 8501));
                return;
            }
        }
        // 2026-10-25 (Angelo: "fights should always have active cameras, so no static cams during battles"): the
        // turn between onboards is the helicopter (or another onboard), never a trackside TV camera
        int[] act = new[] { gTv1, gTv2, gTv3 }.Where(g => g >= 0).ToArray();
        tvRot++;
        int pick = act.Length > 0 ? act[tvRot % act.Length] : -1;
        Cut("your fight: active", carIdx, pick, rng.Next(12000, 16001));
    }
    int tvRot;

    static bool IsFinalLap(RaceSnapshot snap) =>
        (snap.TotalLaps > 0 && snap.LapsRemaining <= 1 && snap.LapsRemaining >= 0 && (snap.Leader?.LapsCompleted ?? 0) > 0) || snap.Flags.White;

    static IEnumerable<Driver> AllCars(RaceSnapshot snap)
    {
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
                if (!d.IsPaceCar && !d.IsSpectator && d.IsConnected) yield return d;
    }

    /// <summary>The user's car: the player, or the car iRacing gives the user in a replay (DriverCarIdx).</summary>
    static Driver? UserCar(RaceSnapshot snap) => snap.Player;

    /// <summary>The user's car with a same-class car within 1 s behind (behind = true) or ahead, closest first.</summary>
    static (Driver Mine, Driver Rival, bool Behind)? MyFight(RaceSnapshot snap)
    {
        var me = snap.Player;
        if (me == null || me.IsInPit || me.OffTrack) return null;
        var cls = snap.ClassLeaderboard.FirstOrDefault(c => c.Drivers.Any(d => d.CarIdx == me.CarIdx));
        if (cls == null) return null;
        int i = -1;
        for (int k = 0; k < cls.Drivers.Count; k++) if (cls.Drivers[k].CarIdx == me.CarIdx) { i = k; break; }
        if (i < 0) return null;
        Driver? back = i + 1 < cls.Drivers.Count ? cls.Drivers[i + 1] : null;
        Driver? front = i > 0 ? cls.Drivers[i - 1] : null;
        double gb = back is { IsInPit: false, LapDifference: 0, IntervalSeconds: double b } && b > 0 ? b : 99;
        double ga = front != null && me.LapDifference == 0 && me.IntervalSeconds is double a && a > 0 && !front.IsInPit ? a : 99;
        if (gb > 1.0 && ga > 1.0) return null;
        return gb <= ga ? (me, back!, true) : (me, front!, false);
    }

    /// <summary>The closest same-class pair under a second apart, front of the field first.</summary>
    static (Driver Chaser, Driver Ahead)? FindBattle(RaceSnapshot snap)
    {
        (Driver, Driver)? best = null;
        double bestScore = double.MaxValue;
        foreach (var cls in snap.ClassLeaderboard)
            for (int i = 1; i < cls.Drivers.Count; i++)
            {
                var b = cls.Drivers[i]; var a = cls.Drivers[i - 1];
                if (b.IsInPit || a.IsInPit || b.OffTrack || !b.IsConnected || !a.IsConnected) continue;
                if (b.IntervalSeconds is not double gap || gap <= 0 || gap > 1.0 || b.LapDifference != 0) continue;
                double score = gap + b.PositionInClass * 0.05;   // tight gaps first, then the front of the field
                if (score < bestScore) { bestScore = score; best = (b, a); }
            }
        return best;
    }

    long lastAheadMs = -1_000_000;

    /// <summary>#431: a car that went off THIS tick, up to ~800 m (max a tenth of a lap) in front of <paramref name="me"/>.</summary>
    Driver? CrashAhead(RaceSnapshot snap, Driver me)
    {
        double lenM = 0;
        var tl = snap.Extras.TrackLength ?? "";
        var num = new string(tl.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        if (double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            lenM = tl.Contains("mi", StringComparison.OrdinalIgnoreCase) ? v * 1609.34 : v * 1000;
        double reach = lenM > 100 ? Math.Min(0.10, 800.0 / lenM) : 0.06;
        Driver? best = null; double bestGap = double.MaxValue;
        foreach (var d in AllCars(snap))
        {
            if (d.CarIdx == me.CarIdx || d.IsInPit || !d.OffTrack || d.LapDistPct < 0) continue;
            bool was = lastOff.TryGetValue(d.CarIdx, out var w) && w;
            if (was) continue;
            double gap = d.LapDistPct - me.LapDistPct;
            if (gap < 0) gap += 1;
            if (gap > 0.0005 && gap <= reach && gap < bestGap) { best = d; bestGap = gap; }
        }
        return best;
    }

    void Remember(RaceSnapshot snap)
    {
        foreach (var d in AllCars(snap))
        {
            lastClassPos[d.CarIdx] = d.PositionInClass;
            lastOff[d.CarIdx] = d.OffTrack;
        }
    }

    void RefreshGroups()
    {
        int upd = rs.SessionInfoUpdate;
        // 2026-10-25: retry until iRacing's camera list has actually arrived (at 13:55 it read 0 groups once and kept
        // cutting with "no group" - which leaves whatever camera iRacing had, an onboard)
        if (upd == infoUpdate && groups.Count > 0) return;
        infoUpdate = upd;
        groups = rs.CameraGroups();
        carNumbers = rs.CarNumbersRaw();
        int Find(params string[] names)
        {
            foreach (var n in names)
                foreach (var g in groups)
                    if (string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase)) return g.Num;
            foreach (var n in names)
                foreach (var g in groups)
                    if (g.Name.Contains(n, StringComparison.OrdinalIgnoreCase)) return g.Num;
            return -1;
        }
        gWide = Find("Blimp", "Chopper");
        gTv1 = Find("TV1");
        gTv2 = Find("TV2");
        gTv3 = Find("TV3");
        gPit = Find("Pit Lane", "Pit Lane 2");
        gTvStatic = Find("TV Static");
        gTvMixed = Find("TV Mixed");
        gChopper = Find("Chopper");
        gGearbox = Find("Gearbox");
        gNose = Find("Nose");
        gGyro = Find("Gyro");
        gRollBar = Find("Roll Bar");
        // Angelo 2026-11-06: real-world TV cameras only - no onboards (roll bar, nose, gyro, gearbox) and never the cockpit
        // 2026-11-06 (#870): onboards back on now that the TV sets hold trackside cameras only (the cockpit is still never used)
        // 2026-11-06 (Angelo: "NO MORE COCKPIT CAM - NOT IN TV1, TV2 OR TV3"): at Le Mans iRacing's TV1, TV2 and TV Mixed groups
        // also hold Cockpit / Roll Bar / Gyro / Nose / Gearbox / Chopper cameras that iRacing switches to by itself.
        // TV3 is trackside only, so every TV shot uses it; the camera check below catches anything else.
        // 2026-11-06: reverted - Angelo takes the onboard cameras out of TV1/TV2 himself in iRacing's camera editor
        // (was: every TV shot forced to TV3)
        Program.Log($"director: {groups.Count} camera groups (wide={gWide} tv1={gTv1} tv2={gTv2} tv3={gTv3} pit={gPit} gearbox={gGearbox} nose={gNose} gyro={gGyro})");
    }

    /// <summary>Puts a car on screen with a camera group (-1 = keep the current group).</summary>
    void Cut(string why, int carIdx, int group, long holdMs, bool force = false)
    {
        long now = Now;
        if (!force && now - shotAtMs < shotHoldMs) return;
        if (carIdx == shotCar && group == shotGroup && why == shot) { shotAtMs = now; shotHoldMs = holdMs; return; }
        if (!carNumbers.TryGetValue(carIdx, out var raw)) { carNumbers = rs.CarNumbersRaw(); if (!carNumbers.TryGetValue(carIdx, out raw)) return; }
        if (group < 0) group = gTv1 >= 0 ? gTv1 : gTv2;          // never "keep the current camera" (it may be an onboard)
        if (group < 0) return;                                    // no camera list yet: try again next tick
        if (!rs.CamToCar(raw, group)) return;
        shot = why; shotCar = carIdx; shotGroup = group; shotAtMs = now; shotHoldMs = holdMs;
    }

    void StartReplay(RaceSnapshot snap, Driver car)
    {
        // 2026-11-06 (#853): the moment is when the car first left the track (not when we noticed), then the shaped replay
        double offFor = offSinceMs.TryGetValue(car.CarIdx, out var since) ? (Now - since) / 1000.0 : 1.5;
        double st = rs.ReadDouble("ReplaySessionTime");
        if (double.IsNaN(st)) st = rs.ReadDouble("SessionTime");
        int sn = rs.ReadInt("ReplaySessionNum", fallback: snap.SessionNum);
        if (double.IsNaN(st) || sn < 0) { Cut("incident", car.CarIdx, gTv1, 8000, force: true); return; }
        double moment = MomentOf(car, st - offFor);
        StartShapedReplay(sn, !double.IsNaN(moment) && moment >= st - offFor - 8 && moment <= st ? moment : st - offFor - 1.5, car.CarIdx, car);
    }
    // 2026-11-06: the replayed car's name tag (who it was and what happened), shown over the replay
    static int replayTagId = -1000;
    static void ShowReplayTag(string eyebrow, string l1, string l2, string accent, double secs)
        => ApexHUD.Native.Overlays.DeskBoard.ShowTag(new ApexHUD.Native.Overlays.NameTag(replayTagId--, eyebrow, l1, l2, accent), secs);

    void EndReplay()
    {
        inReplay = false; SlowMoActive = false;
        // 2026-11-06 (#931): coming back from a replay the running order "changes" in one frame (replay -> race) - that
        // is not a pass; start the pass / off-track memory fresh so a replay can't queue another replay
        lastClassPos.Clear(); lastOff.Clear();
        ApexHUD.Native.Overlays.DeskBoard.EndReplayTag();
        if (rs.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase))
        {
            // a saved replay: carry on from where we left off, plus the time the slow-mo took
            double elapsed = (Now - replayStartedMs) / 1000.0;
            rs.ReplayToSessionTime(resumeSessionNum, (int)(resumeSessionTime * 1000));   // 2026-11-06: no part of the race is skipped
            rs.ReplaySpeed(1, false);
        }
        else rs.ReplayToLive();   // spectating a live session: back to live
        shotHoldMs = 0;
    }

    public void Dispose()
    {
        running = false;
        wake.Set();
    }

    /// <summary>2026-10-29: frames between the replay and live. Live in the car iRacing can report ReplayFrameNum 0 with
    /// ReplayFrameNumEnd counting up - no tape position and no replay playing means live (0).</summary>
    internal static int FramesBehind(ApexHUD.Native.Data.RaceStateService rs)
    {
        int end = rs.ReadInt("ReplayFrameNumEnd", 0, 0);
        int num = rs.ReadInt("ReplayFrameNum", 0, -1);
        return num == 0 && !rs.ReadBool("IsReplayPlaying") ? 0 : end;
    }
}

