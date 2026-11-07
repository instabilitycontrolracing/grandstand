using ApexHUD.Native.Common;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// #769 (2026-11-05): BROADCAST mode rules for the whole-field director (control page "Broadcast mode"; the other
/// setting, STREAMER mode, stays on the user's car). From claude/broadcast-director-research.md:
///  - hold a battle shot while a pass is in progress (gap under 0.3 s, up to 30 s), +2 s after the pass completes
///  - a new battle opens on a wide shot (helicopter / TV1) before the onboards
///  - onboards capped at ~30 % of air time
///  - class air time balanced: a class with a live fight and under 20 % of recent air gets the next battle
///  - the overall leader revisited at least every 4 minutes
///  - after a full-course caution ends: the running order (leader on the helicopter) first
///  - pit stops of the top 3 in each class: entry, then the rejoin
///  - missed moments: a top-3-in-class pass that wasn't on air is replayed (from 6 s before) in the next lull, within 4 min
///  - the finish: every class winner crossing the line before the user's car
/// </summary>
internal sealed partial class ReplayDirector
{
    long airLastMs = -1;
    readonly Dictionary<string, double> classAir = new();
    double onboardAir, totalAir;
    int pairChaser = -1, pairAhead = -1;
    bool swapHeld;
    long lastLeaderShotMs = -1_000_000;
    bool wasCaution;
    readonly Dictionary<int, bool> lastPitState = new();
    readonly Queue<Missed> missed = new();
    readonly Dictionary<string, int> classLeaderLapAtFlag = new();
    long finishStage2AtMs = -1;

    readonly record struct Missed(int SessionNum, double SessionTime, int CarIdx, string CarNumber, long AtMs, string Line1 = "", string Line2 = "", string Accent = "");

    bool IsOnboardGroup(int g) => g >= 0 && (g == gRollBar || g == gNose || g == gGyro || g == gGearbox);

    /// <summary>Air time per class and for onboards, decayed over ~15 minutes.</summary>
    void AccountAir(RaceSnapshot snap, long now)
    {
        double dt = airLastMs < 0 ? 0 : Math.Clamp((now - airLastMs) / 1000.0, 0, 2);
        airLastMs = now;
        if (dt <= 0 || shotCar < 0) return;
        double keep = Math.Exp(-dt / 900.0);
        foreach (var k in classAir.Keys.ToList()) classAir[k] *= keep;
        onboardAir *= keep; totalAir *= keep;
        var car = AllCars(snap).FirstOrDefault(d => d.CarIdx == shotCar);
        if (car != null) classAir[car.ClassName] = (classAir.TryGetValue(car.ClassName, out var v) ? v : 0) + dt;
        totalAir += dt;
        if (IsOnboardGroup(shotGroup)) onboardAir += dt;
        if (snap.Leader is { } l && shotCar == l.CarIdx) lastLeaderShotMs = now;
    }

    double OnboardShare => totalAir > 30 ? onboardAir / totalAir : 0;

    double ClassShare(string cls)
    {
        double sum = classAir.Values.Sum();
        return sum > 30 && classAir.TryGetValue(cls, out var v) ? v / sum : sum > 30 ? 0 : 1;
    }

    /// <summary>Rule 4: don't cut while the battle on air is mid-pass; give 2 s after the pass completes.</summary>
    bool HoldForPass(RaceSnapshot snap, long now)
    {
        if (!shot.StartsWith("battle", StringComparison.Ordinal) || pairChaser < 0) return false;
        var c = AllCars(snap).FirstOrDefault(d => d.CarIdx == pairChaser);
        var a = AllCars(snap).FirstOrDefault(d => d.CarIdx == pairAhead);
        if (c == null || a == null) return false;
        if (c.PositionInClass > 0 && a.PositionInClass > 0 && c.PositionInClass < a.PositionInClass)
        {
            if (!swapHeld) { swapHeld = true; shotHoldMs = (now - shotAtMs) + 2000; Program.Log($"director: pass completed on air (#{c.CarNumber}) - holding 2 s"); return true; }
            return false;
        }
        return c.IntervalSeconds is double g && g > 0 && g < 0.3 && now - shotAtMs < 30000;
    }

    bool onBattleNow;
    readonly Dictionary<string, long> storyShown = new();

    // 2026-11-06 (#863): the moment an incident really started = the last time the car was still at racing speed
    readonly Dictionary<int, List<(double St, double Prog)>> progHist = new();
    double replayIncSt = double.NaN;
    bool replayLanded; int replaySn = -1; int replayTargetMs; long replayJumpAtMs, replayLogAtMs; int replayTries;

    double MomentOf(Driver? d, double upper = double.MaxValue)
    {
        if (d == null || !progHist.TryGetValue(d.CarIdx, out var h) || h.Count < 3) return double.NaN;
        float pace = d.BestLapTime > 30 ? d.BestLapTime : d.LastLapTime > 30 ? d.LastLapTime : 0f;
        if (pace <= 0) return double.NaN;
        for (int i = h.Count - 1; i > 0; i--)
        {
            double dp = h[i].Prog - h[i - 1].Prog;
            if (dp < -0.5) dp += 1;
            double dt = h[i].St - h[i - 1].St;
            if (dt <= 0 || h[i].St > upper) continue;   // 2026-11-06 (#874): never the car speeding up again as it rejoins
            if (dp / dt * pace > 0.85) return h[i].St;   // still at racing speed here: the trouble starts right after
        }
        return double.NaN;
    }

    // ---- 2026-11-06 (#851): iRacing's own incident markers in a saved replay, read once up front ----
    readonly List<(int Sn, double St, int Car)> incidentIndex = new();
    int indexState = -1;   // -1 not read, 1 reading, 2 ready
    // 2026-11-06 (#859): the scan only runs when asked for (POST /api/director/scan), never by itself, and gives up after 60 s
    public static volatile bool ScanRequested;
    long scanStartMs;
    long indexNextMs; int indexStalls; double scanResumeSt; int scanResumeSn;
    (int Sn, double St, int Car) lastScan = (-1, -1, -1);
    readonly HashSet<double> indexCovered = new();
    (int Sn, double St, int Car)? pendingIncident;
    int replayPhase = -1; long slowAtMs, slowEndMs;

    /// <summary>Reads every incident marker with iRacing's "next incident" search. True while reading.</summary>
    bool TickIncidentIndex(RaceSnapshot snap, long now)
    {
        if (!rs.SimMode.Equals("replay", StringComparison.OrdinalIgnoreCase)) { indexState = -1; return false; }
        if (indexState == 2) return false;
        if (indexState == -1 && !ScanRequested) return false;
        if (indexState == -1)
        {
            ScanRequested = false; scanStartMs = now;
            indexState = 1; incidentIndex.Clear(); indexCovered.Clear(); indexStalls = 0; lastScan = (-1, -1, -1); pendingIncident = null;
            scanResumeSn = rs.ReadInt("ReplaySessionNum", fallback: snap.SessionNum);
            scanResumeSt = rs.ReadDouble("ReplaySessionTime");
            rs.ReplaySpeed(0, false);
            rs.ReplaySearch(IRSDKSharper.IRacingSdkEnum.RpySrchMode.ToStart);
            indexNextMs = now + 700;
            Status = "On"; Why = "Reading the replay's incidents...";
            Program.Log("director: reading the replay's incident list");
            return true;
        }
        if (now < indexNextMs) return true;
        int sn = rs.ReadInt("ReplaySessionNum", fallback: -1);
        double st = rs.ReadDouble("ReplaySessionTime");
        int car = rs.ReadInt("CamCarIdx", fallback: -1);
        if (lastScan.Sn >= 0)
        {
            bool moved = sn != lastScan.Sn || Math.Abs(st - lastScan.St) > 0.05;
            if (!moved) indexStalls++;
            else { indexStalls = 0; incidentIndex.Add((sn, st, car)); }
        }
        if (indexStalls >= 3 || incidentIndex.Count >= 400 || now - scanStartMs > 60000)
        {
            indexState = 2;
            if (!double.IsNaN(scanResumeSt) && scanResumeSn >= 0) rs.ReplayToSessionTime(scanResumeSn, (int)(scanResumeSt * 1000));
            rs.ReplaySpeed(1, false);
            Program.Log($"director: {incidentIndex.Count} incidents in this replay - the director will be there for them");
            return true;
        }
        lastScan = (sn, st, car);
        rs.ReplaySearch(IRSDKSharper.IRacingSdkEnum.RpySrchMode.NextIncident);
        indexNextMs = now + 350;
        return true;
    }

    /// <summary>A known incident in the next 8 s: put the car on screen live, hold through the moment, then replay it.</summary>
    bool IncidentAhead(RaceSnapshot snap, long now)
    {
        if (indexState != 2 || incidentIndex.Count == 0) return false;
        int sn = rs.ReadInt("ReplaySessionNum", fallback: -1);
        double st = rs.ReadDouble("ReplaySessionTime");
        if (double.IsNaN(st)) return false;
        if (pendingIncident is { } p)
        {
            if (sn != p.Sn || st < p.St - 15 || st > p.St + 30) { pendingIncident = null; return false; }   // jumped away
            if (st >= p.St + 3.5)
            {
                pendingIncident = null;
                var pd = AllCars(snap).FirstOrDefault(x => x.CarIdx == p.Car);
                double pm = MomentOf(pd, p.St);
                StartShapedReplay(p.Sn, !double.IsNaN(pm) && pm >= p.St - 6 && pm <= p.St + 1 ? Math.Min(pm, p.St) : p.St - 1.0, p.Car, pd);
            }
            return true;
        }
        if (now - lastIncidentMs < 120000) return false;   // #879
        foreach (var inc in incidentIndex)
        {
            if (inc.Sn != sn || inc.Car < 0 || indexCovered.Contains(inc.St)) continue;
            double ahead = inc.St - st;
            if (ahead < 1.5 || ahead > 8) continue;
            var d = AllCars(snap).FirstOrDefault(x => x.CarIdx == inc.Car);
            if (d == null || !(d.PositionInClass is > 0 and <= 5 || d.CarIdx == snap.PlayerCarIdx)) continue;   // #879
            indexCovered.Add(inc.St);
            lastIncidentMs = now;
            pendingIncident = inc;
            Cut($"incident ahead #{d.CarNumber}", inc.Car, gTv1 >= 0 ? gTv1 : gTv2, (int)((ahead + 4) * 1000), force: true);
            Program.Log($"director: incident coming up for #{d.CarNumber} in {ahead:0.0} s - on it live, replay after");
            return true;
        }
        return false;
    }

    /// <summary>2026-11-06 (#853, #854): 6 s before the moment at normal speed, a third speed through 2 s of the moment,
    /// normal speed for 3.5 s, then back to where the race was.</summary>
    void StartShapedReplay(int sessionNum, double incidentSt, int carIdx, Driver? d)
    {
        int curSn = rs.ReadInt("ReplaySessionNum", fallback: sessionNum);
        double curSt = rs.ReadDouble("ReplaySessionTime");
        if (double.IsNaN(curSt)) curSt = rs.ReadDouble("SessionTime");
        if (double.IsNaN(curSt)) return;
        resumeSessionNum = curSn; resumeSessionTime = curSt; replayStartedMs = Now;
        replayIncSt = incidentSt;
        replaySn = sessionNum; replayTargetMs = (int)((incidentSt - 8.0) * 1000); replayJumpAtMs = Now; replayTries = 1; replayLogAtMs = 0;
        rs.ReplaySpeed(0, slowMotion: false);   // hold still while iRacing seeks
        rs.ReplayToSessionTime(sessionNum, replayTargetMs);
        rs.ReplaySpeed(1, slowMotion: false);
        int cam = shotGroup == gTv1 && gTv2 >= 0 ? gTv2 : gTv1 >= 0 ? gTv1 : gTv2;   // another angle than the live shot
        Cut("incident replay", carIdx, cam, 30000, force: true);
        inReplay = true; SlowMoActive = true;
        replayEndMs = Now + 40000; replayPhase = 0; replayLanded = false;   // 40 s = safety only; the replay clock ends it
        if (d != null)
            ShowReplayTag("REPLAY", $"#{d.CarNumber}  {CamCarChip.FullName(d)}",
                (d.OffTrack ? "Off the track" : "Incident") + $" \u00B7 {d.ClassName} \u00B7 P{d.PositionInClass} in class", d.ClassColor, 40);
        Program.Log($"director: replay of #{d?.CarNumber} - moment {incidentSt:0.0}, jumped to {incidentSt - 8.0:0.0} (was at {curSt:0.0}), camera group {cam}");
    }
    long lastTrafficMs = -1_000_000, lastHeliMs = -1_000_000;
    static int trafficTagId = -5000;

    /// <summary>The helicopter at most every 3 minutes (big moments only); otherwise the fallback camera.</summary>
    int Heli(int fallback)
    {
        if (gChopper < 0 || Now - lastHeliMs < 180000) return fallback;
        lastHeliMs = Now;
        return gChopper;
    }

    /// <summary>A class leader within 1.2 s behind a car of another class or a lapped car, on the road.</summary>
    (Driver Leader, Driver Traffic)? LeaderInTraffic(RaceSnapshot snap)
    {
        foreach (var cls in snap.ClassLeaderboard)
        {
            if (cls.Drivers.Count == 0) continue;
            var L = cls.Drivers[0];
            if (L.IsInPit || L.OffTrack || !L.IsConnected || L.LapsCompleted < 1) continue;
            float pace = L.BestLapTime > 30 ? L.BestLapTime : L.LastLapTime > 30 ? L.LastLapTime : 0f;
            if (pace <= 0) continue;
            foreach (var d in AllCars(snap))
            {
                if (d.CarIdx == L.CarIdx || d.IsInPit || !d.IsConnected || d.IsPaceCar) continue;
                if (d.ClassName == L.ClassName && d.LapsCompleted >= L.LapsCompleted) continue;   // a same-lap rival is a battle, not traffic
                double ahead = d.LapDistPct - L.LapDistPct;
                if (ahead < 0) ahead += 1;
                double s = ahead * pace;
                if (s > 0.05 && s < 1.2) return (L, d);
            }
        }
        return null;
    }
    long pairSinceMs;

    /// <summary>2026-11-06: the fight on air is still a fight (next to each other in class, within 1.3 s, both racing),
    /// for up to 2.5 minutes before the director looks around again.</summary>
    bool BattleAlive(RaceSnapshot snap)
    {
        if (!shot.StartsWith("battle", StringComparison.Ordinal) || pairChaser < 0 || pairAhead < 0) return false;
        var c = AllCars(snap).FirstOrDefault(d => d.CarIdx == pairChaser);
        var a = AllCars(snap).FirstOrDefault(d => d.CarIdx == pairAhead);
        if (c == null || a == null || c.IsInPit || a.IsInPit || c.OffTrack || a.OffTrack || !c.IsConnected || !a.IsConnected) return false;
        if (Now - pairSinceMs > 150000) return false;
        var back = c.PositionInClass > a.PositionInClass ? c : a;
        var front = ReferenceEquals(back, c) ? a : c;
        if (back.PositionInClass - front.PositionInClass != 1) return false;
        return back.IntervalSeconds is double g && g > 0 && g < 1.3;
    }

    // 2026-11-06 (Angelo: "it just shows a car spun around, not what happened"): an incident is a car off the track for
    // 1.2 s+ that has slowed right down (a spin or a crash), once per trip off - not every wide line
    readonly Dictionary<int, long> offSinceMs = new();
    readonly HashSet<int> offReplayed = new();
    readonly Dictionary<int, (double Prog, long At)> progSample = new(), progPrev = new();

    void TrackOffs(RaceSnapshot snap, long now)
    {
        foreach (var d in AllCars(snap))
        {
            double prog = d.LapsCompleted + Math.Max(0f, d.LapDistPct);
            double hst = rs.ReadDouble("ReplaySessionTime");
            if (double.IsNaN(hst)) hst = rs.ReadDouble("SessionTime");
            if (!double.IsNaN(hst))
            {
                if (!progHist.TryGetValue(d.CarIdx, out var hl)) progHist[d.CarIdx] = hl = new();
                if (hl.Count > 0 && hst < hl[^1].St - 1) hl.Clear();   // the replay jumped back
                if (hl.Count == 0 || hst - hl[^1].St >= 0.4) hl.Add((hst, prog));
                while (hl.Count > 0 && hl[0].St < hst - 25) hl.RemoveAt(0);
            }
            if (!progSample.TryGetValue(d.CarIdx, out var s) || now - s.At >= 700)
            {
                if (progSample.TryGetValue(d.CarIdx, out var old)) progPrev[d.CarIdx] = old;
                progSample[d.CarIdx] = (prog, now);
            }
            if (d.OffTrack && !d.IsInPit) { if (!offSinceMs.ContainsKey(d.CarIdx)) offSinceMs[d.CarIdx] = now; }
            else { offSinceMs.Remove(d.CarIdx); offReplayed.Remove(d.CarIdx); }
        }
    }

    bool RealIncident(Driver d, long now)
    {
        if (!offSinceMs.TryGetValue(d.CarIdx, out var since) || now - since < 1200 || offReplayed.Contains(d.CarIdx)) return false;
        if (!progSample.TryGetValue(d.CarIdx, out var a) || !progPrev.TryGetValue(d.CarIdx, out var b) || a.At <= b.At) return false;
        float pace = d.BestLapTime > 30 ? d.BestLapTime : d.LastLapTime > 30 ? d.LastLapTime : 0f;
        if (pace <= 0) return false;
        double dprog = a.Prog - b.Prog;
        if (dprog < -0.5) dprog += 1;
        double ratio = dprog / ((a.At - b.At) / 1000.0) * pace;   // 1.0 = racing speed
        if (ratio > 0.55) return false;                             // kept its speed: a wide moment, not an incident
        offReplayed.Add(d.CarIdx);
        return true;
    }

    /// <summary>Rules 1 + 12: the best battle, but a class with a live fight and under 20 % of recent air goes first.</summary>
    (Driver Chaser, Driver Ahead)? FindBattleBalanced(RaceSnapshot snap)
    {
        var best = FindBattle(snap);
        if (snap.ClassLeaderboard.Count < 2) return best;
        (Driver, Driver)? pick = null; double pickShare = 0.2;
        foreach (var cls in snap.ClassLeaderboard)
        {
            (Driver, Driver)? cb = null; double cs = double.MaxValue;
            for (int i = 1; i < cls.Drivers.Count; i++)
            {
                var b = cls.Drivers[i]; var a = cls.Drivers[i - 1];
                if (b.IsInPit || a.IsInPit || b.OffTrack || !b.IsConnected || !a.IsConnected) continue;
                if (b.IntervalSeconds is not double gap || gap <= 0 || gap > 1.0 || b.LapDifference != 0) continue;
                double score = gap + b.PositionInClass * 0.05;
                if (score < cs) { cs = score; cb = (b, a); }
            }
            if (cb == null) continue;
            double share = ClassShare(cls.Drivers[0].ClassName);
            if (share < pickShare) { pickShare = share; pick = cb; }
        }
        return pick ?? best;
    }

    /// <summary>The battle cut: a wide establishing shot for a new pair, then the active rotation (onboards capped).</summary>
    void BroadcastBattleCut(Driver chaser, Driver ahead)
    {
        bool newPair = chaser.CarIdx != pairChaser || ahead.CarIdx != pairAhead;
        if (newPair)
        {
            pairChaser = chaser.CarIdx; pairAhead = ahead.CarIdx; swapHeld = false; pairSinceMs = Now;
            int wide = chaser.PositionInClass <= 2 ? Heli(gTv1 >= 0 ? gTv1 : gTv2) : gTv1 >= 0 ? gTv1 : gTv2;   // the helicopter only for a fight for the class lead, rationed
            Cut("battle: establishing", chaser.CarIdx, wide, rng.Next(8000, 10001));
            return;
        }
        battleFlip++;
        bool onboardsOk = OnboardShare < 0.30;
        var shots = new List<(int G, int Car, string What)>();
        if (gTv3 >= 0) shots.Add((gTv3, chaser.CarIdx, "trackside 3"));
        if (onboardsOk && gNose >= 0) shots.Add((gNose, chaser.CarIdx, "chaser nose"));
        if (onboardsOk && gGearbox >= 0) shots.Add((gGearbox, ahead.CarIdx, "leader gearbox"));
        // Angelo 2026-10-25: "no static cams during battles" - active cameras only (helicopter + onboards)
        // 2026-11-06: every angle follows the chasing car, so both cars stay in the picture (no swinging between the two)
        if (gTv1 >= 0) shots.Add((gTv1, chaser.CarIdx, "trackside"));
        if (gTv2 >= 0) shots.Add((gTv2, chaser.CarIdx, "trackside 2"));
        if (onboardsOk && gRollBar >= 0) shots.Add((gRollBar, chaser.CarIdx, "chaser roll bar"));
        if (onboardsOk && gGyro >= 0) shots.Add((gGyro, chaser.CarIdx, "chaser gyro"));
        if (onboardsOk && gRollBar >= 0) shots.Add((gRollBar, chaser.CarIdx, "chaser roll bar"));   // #907: more roll bar + gyro
        if (onboardsOk && gGyro >= 0) shots.Add((gGyro, ahead.CarIdx, "leader gyro"));
        var (group, car, what) = shots.Count > 0 ? shots[battleFlip % shots.Count] : (-1, chaser.CarIdx, "battle");
        Cut("battle: " + what, car, group, rng.Next(12000, 18001));   // 2026-11-06 (#855): 12-18 s an angle (was 7-10)
    }

    /// <summary>Remember top-3-in-class passes the director is NOT going to put on air, for a replay in the next lull.</summary>
    int seenJumpsForPasses = -1;
    void NoteMissedPasses(RaceSnapshot snap, long now)
    {
        // 2026-11-06 (#931): a replay jump re-shuffles the running order in one frame - "#22 E. Dzly passes #11 for P1"
        // was a jump, not a pass. Any jump: forget the old order and the queued passes, start again from here.
        int jc = ApexHUD.Native.Common.ReplayState.JumpCount;
        if (jc != seenJumpsForPasses) { seenJumpsForPasses = jc; lastClassPos.Clear(); missed.Clear(); return; }
        if (snap.SessionState is < 4) return;   // no passes behind the pace car
        foreach (var d in AllCars(snap))
        {
            if (!lastClassPos.TryGetValue(d.CarIdx, out var before) || d.PositionInClass <= 0 || d.PositionInClass >= before) continue;
            if (d.PositionInClass > 3 || d.IsInPit || d.LapsCompleted < 1 || d.CarIdx == shotCar) continue;
            if ((d.IntervalSeconds ?? 99) > 1.5) continue;   // a pit cycle shuffle, not an overtake
            int sn = rs.ReadInt("ReplaySessionNum", fallback: snap.SessionNum);
            double st = rs.ReadDouble("ReplaySessionTime");
            if (double.IsNaN(st)) st = rs.ReadDouble("SessionTime");
            if (double.IsNaN(st) || sn < 0) continue;
            if (missed.Count >= 6) missed.Dequeue();
            // 2026-11-06 (Angelo: "if grandstand shows a replay of a car it should show that car's driver card"): who and what, for the replay tag
            var passed = AllCars(snap).FirstOrDefault(x => x.ClassName == d.ClassName && x.PositionInClass == d.PositionInClass + 1);
            string what = (passed != null ? $"Passes #{passed.CarNumber} {NameFormat.Broadcast(passed)} for P{d.PositionInClass}" : $"Up to P{d.PositionInClass}") + $" \u00B7 {d.ClassName}";
            missed.Enqueue(new Missed(sn, st, d.CarIdx, d.CarNumber, now, $"#{d.CarNumber}  {CamCarChip.FullName(d)}", what, d.ClassColor));
            Program.Log($"director: missed pass by #{d.CarNumber} (P{d.PositionInClass} in class) - queued for a replay");
        }
    }

    /// <summary>Caution end, pit stops, leader revisit, missed-moment replays. True when it cut.</summary>
    bool BroadcastExtras(RaceSnapshot snap, long now, bool canCut)
    {
        // pit transitions for the top 3 in each class (always tracked, so nothing fires late)
        Driver? pitIn = null, pitOut = null;
        foreach (var d in AllCars(snap))
        {
            bool was = lastPitState.TryGetValue(d.CarIdx, out var w) && w;
            lastPitState[d.CarIdx] = d.IsInPit;
            if (d.PositionInClass is < 1 or > 3 || d.LapsCompleted < 1) continue;
            if (d.IsInPit && !was) pitIn ??= d;
            if (!d.IsInPit && was) pitOut ??= d;
        }

        // running order after a full-course caution
        if (snap.Flags.Caution) wasCaution = true;
        else if (wasCaution)
        {
            wasCaution = false;
            if (snap.Leader is { } l)
            {
                Cut("restart: running order", l.CarIdx, gChopper >= 0 ? gChopper : gTv1, 10000, force: true);
                Program.Log("director: caution over - running order (leader, helicopter)");
                return true;
            }
        }
        if (!canCut) return false;

        if (pitOut != null) { Cut($"pit rejoin #{pitOut.CarNumber}", pitOut.CarIdx, gTv1 >= 0 ? gTv1 : gTv2, 8000); return true; }
        if (pitIn != null) { Cut($"pit entry #{pitIn.CarNumber}", pitIn.CarIdx, gPit >= 0 ? gPit : gTv1, 7000); return true; }

        if (snap.Leader is { } lead && now - lastLeaderShotMs > 240000)
        {
            Cut("leader revisit", lead.CarIdx, gTv1 >= 0 ? gTv1 : gTv2, 12000);
            return true;
        }

        // a lull (no battle anywhere): replay the oldest missed moment still fresh (under 4 min)
        // 2026-11-06 (#931, Angelo #879 "fewer replays"): four pass replays fired back to back at the start (#22, #23,
        // #34, #23 within 35 s). Pass replays now share the replay spacing (one replay of any kind per 2 min), only the
        // newest pass under 90 s old is kept, and a replay jump (seen as the replay clock going backwards) empties the line.
        while (missed.Count > 0 && now - missed.Peek().AtMs > 90000) missed.Dequeue();
        while (missed.Count > 1) missed.Dequeue();
        if (missed.Count > 0)
        {
            double stNow = rs.ReadDouble("ReplaySessionTime");
            var head = missed.Peek();
            if (!double.IsNaN(stNow) && (head.SessionNum != rs.ReadInt("ReplaySessionNum", fallback: head.SessionNum) || stNow < head.SessionTime - 5))
                missed.Clear();
        }
        if (missed.Count > 0 && ControlState.Current.DirectorReplays && FindBattle(snap) == null && now - lastIncidentMs > 120000)
        {
            var m = missed.Dequeue();
            lastIncidentMs = now;
            StartReplayAt(m);
            return true;
        }
        return false;
    }

    /// <summary>A missed pass, replayed at normal speed from 6 s before, on a TV camera; then back to where we were.</summary>
    void StartReplayAt(Missed m)
    {
        int sessionNum = rs.ReadInt("ReplaySessionNum", fallback: m.SessionNum);
        double sessionTime = rs.ReadDouble("ReplaySessionTime");
        if (double.IsNaN(sessionTime)) sessionTime = rs.ReadDouble("SessionTime");
        if (double.IsNaN(sessionTime) || sessionNum < 0) return;
        resumeSessionNum = sessionNum;
        resumeSessionTime = sessionTime;
        replayStartedMs = Now;
        rs.ReplayToSessionTime(m.SessionNum, (int)((m.SessionTime - 8.0) * 1000));   // 2026-11-06: 8 s back (was 6)
        int cam = gTv1 >= 0 ? gTv1 : gChopper >= 0 ? gChopper : gTv2;
        Cut("missed pass replay", m.CarIdx, cam, 11000, force: true);
        rs.ReplaySpeed(1, slowMotion: false);
        inReplay = true; SlowMoActive = true;
        replayEndMs = Now + 11000;
        Program.Log($"director: replaying the missed pass by #{m.CarNumber}");
        ShowReplayTag("REPLAY", m.Line1.Length > 0 ? m.Line1 : $"#{m.CarNumber}", m.Line2, m.Accent, 11);
    }

    /// <summary>At the flag: remember each class leader's lap, so every class winner is shown crossing the line.</summary>
    void NoteClassLeadersAtFlag(RaceSnapshot snap)
    {
        classLeaderLapAtFlag.Clear();
        foreach (var cls in snap.ClassLeaderboard)
            if (cls.Drivers.Count > 0) classLeaderLapAtFlag[cls.Drivers[0].ClassName] = cls.Drivers[0].LapsCompleted;
    }

    /// <summary>The next class leader still to cross the line (closest to it first); null when they all have (or 4 min passed).</summary>
    Driver? NextClassFinisher(RaceSnapshot snap, long now)
    {
        if (finishStage2AtMs < 0) finishStage2AtMs = now;
        if (now - finishStage2AtMs > 240000) return null;
        Driver? best = null;
        foreach (var cls in snap.ClassLeaderboard)
        {
            if (cls.Drivers.Count == 0) continue;
            var l = cls.Drivers[0];
            if (snap.Leader is { } ov && l.CarIdx == ov.CarIdx) continue;
            if (!classLeaderLapAtFlag.TryGetValue(l.ClassName, out var lap) || l.LapsCompleted > lap || !l.IsConnected) continue;
            if (best == null || l.LapDistPct > best.LapDistPct) best = l;
        }
        return best;
    }
}
