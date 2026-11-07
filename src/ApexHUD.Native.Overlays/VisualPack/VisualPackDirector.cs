using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>Which visual-pack items are switched on (control app) and which use their elevated design. All graphics
/// default OFF; the lower-third sounds follow the existing stingers (on, replays only).</summary>
public sealed class VpFlags
{
    public bool Weather, Night, Radar, Strategy, LiveTag, Bumper, Stats, YellowMap, Break, BreakMusic, Pip, PipCamera, Lens, RaceControl, PurpleBeep;
    public bool LowerThirdSounds = true;
    /// <summary>W11: chase / nose camera field of view (deg, 0 = default 70) and pitch (deg, NaN = default -4).</summary>
    public float LensFov, LensPitch = float.NaN;
    /// <summary>Item ids ("V1".."W12") using the elevated design.</summary>
    public HashSet<string> Elevated = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>What the compositor knows this tick that the snapshot doesn't carry.</summary>
public struct VpContext
{
    public bool BattleActive;
    public Driver? Opponent;
    public bool RadioActive;
    /// <summary>"back to live" / "into the replay" when the replay wipe fired this tick.</summary>
    public string? WipeFired;
    public bool TvCamera;
    public int CameraKey;
    /// <summary>Raw telemetry read (fuel); null = not available.</summary>
    public Func<string, double>? ReadVar;
    /// <summary>The replay director may steer the camera for the PiP (off when it is steering itself / you are driving).</summary>
    public bool CameraFree;
}

/// <summary>
/// 2026-10-27: the data hookup for the visual pack - decides from the live snapshot when each new graphic plays and
/// feeds it. Runs once per compositor tick on the render thread. Event moments (a graphic starting) allocate its model;
/// the steady per-tick work uses fixed arrays (64 cars) and allocates nothing.
/// </summary>
public sealed class VisualPackDirector : IDisposable
{
    public readonly WeatherCard Weather = new();
    public readonly NightPalette Night = new();
    public readonly BattleRadar Radar = new();
    public readonly StrategyBoard Strategy = new();
    public readonly LiveTag Live = new();
    public readonly ReplayBumper Bumper = new();
    public readonly RaceAwards Awards = new();
    public readonly YellowSectorMap YellowMap = new();
    public readonly BreakCard Break = new();
    public readonly PipOnboard Pip = new();
    public readonly LensFx Lens = new();
    public readonly RaceControlCard RaceControl = new();

    /// <summary>Asks the host to cut the iRacing camera to a car's cockpit (W10 elevated option).</summary>
    public Action<int>? CutToCockpit;
    /// <summary>One line for the host log when something plays.</summary>
    public Action<string>? Log;

    VpFlags flags = new();
    const int N = 64;

    public void Apply(VpFlags f)
    {
        flags = f;
        Weather.Elevated = f.Elevated.Contains("V1"); Night.Elevated = f.Elevated.Contains("V2"); Radar.Elevated = f.Elevated.Contains("V3");
        Strategy.Elevated = f.Elevated.Contains("V4"); Live.Elevated = f.Elevated.Contains("V5"); Bumper.Elevated = f.Elevated.Contains("V6");
        YellowMap.Elevated = f.Elevated.Contains("V8"); Break.Elevated = f.Elevated.Contains("W8");
        Pip.Elevated = f.Elevated.Contains("W10"); Lens.Elevated = f.Elevated.Contains("W11"); RaceControl.Elevated = f.Elevated.Contains("W12");
        Lens.Align.FovOverrideDeg = f.LensFov; Lens.Align.PitchOverrideDeg = f.LensPitch;
    }

    double connectedAt = -1;
    int seenJumps = int.MinValue;
    string sessionKey = "";

    public void Tick(RaceSnapshot snap, double nowMs, in VpContext ctx)
    {
        if (!snap.IsConnected) { connectedAt = -1; Night.Tick(0, 0, false, nowMs); return; }
        if (connectedAt < 0 || snap.SessionKey != sessionKey) { connectedAt = nowMs; sessionKey = snap.SessionKey; ResetSession(); }
        int jc = ReplayState.JumpCount;
        if (jc != seenJumps) { seenJumps = jc; quietUntil = nowMs + 8000; Array.Fill(lastInc, -2); }
        bool settled = nowMs - connectedAt > 10000;

        Night.Tick(snap.SessionTimeOfDay, snap.TrackLatitude, flags.Night, nowMs);
        TickWeather(snap, nowMs, ctx, settled);
        TickRadar(snap, nowMs, ctx);
        TickStints(snap, nowMs, ctx);
        TickStrategy(snap, nowMs, ctx);
        TickLive(snap, nowMs);
        TickBumper(snap, nowMs, ctx);
        TickLaps(snap, nowMs);
        TickStats(snap, nowMs);
        TickYellow(snap, nowMs, ctx);
        TickBreak(snap, nowMs);
        TickPip(snap, nowMs, ctx);
        var lensMap = MapFor(snap);
        if (lensTestUntil > 0 && nowMs >= lensTestUntil) { Lens.ForceGolden = float.NaN; Lens.ForceSun = null; lensTestUntil = double.NegativeInfinity; }   // 2026-11-06: test over
        Lens.Tick(snap, lensMap, mapKey ?? "", ctx.CameraKey, flags.Lens, nowMs);
        if (Lens.Mount != lensMount || lensSrc != Lens.Align.HeadingSource)
        {
            lensMount = Lens.Mount; lensSrc = Lens.Align.HeadingSource;
            if (flags.Lens) Log?.Invoke($"visual pack: lens camera {lensMount}, heading from {lensSrc}, outline calibration {Lens.Align.CalibrationSource}");
        }
        TickRaceControl(snap, nowMs, ctx, settled);
        TickSounds(snap, nowMs, ctx, settled);
    }

    double quietUntil;
    CamMount lensMount = (CamMount)(-1); string lensSrc = "";

    void ResetSession()
    {
        Array.Fill(lastInc, -2); Array.Clear(stopCount); Array.Clear(wasInPit); Array.Clear(maxStint);
        wxInit = false; statsDone = false; strategyAt = -1e12;
        Array.Clear(lapsLed); Array.Clear(lapN); Array.Clear(lapSeen); Array.Fill(lastLeaderLaps, -1); awardQueue.Clear(); awardNext = -1;
    }

    /// <summary>A screen shows a full-width moment (battle map / story card / outro / onboard): small cards wait.</summary>
    static bool Busy(in VpContext ctx) => StoryDirector.Visible || OutroDirector.FullScreen || PresentationState.OnboardK > 0.5f;

    // ------------------------------------------------------------------------------------------------ V1 weather
    bool wxInit, wasRaining; float precipAtCard, tempRef; int wetMax, lastWet; double wxLastCard = -1e12;
    WeatherKind? wxPending; double wxPendingAt;

    void TickWeather(RaceSnapshot s, double now, in VpContext ctx, bool settled)
    {
        var ex = s.Extras;
        bool raining = s.IsRaining || ex.PrecipNow >= 0.1f;
        int wet = ex.TrackWetness;
        if (!wxInit) { wxInit = true; wasRaining = raining; precipAtCard = ex.PrecipNow; tempRef = s.TrackTempC; wetMax = wet; lastWet = wet; return; }
        WeatherKind? ev = null;
        if (raining && !wasRaining) ev = WeatherKind.RainStart;
        else if (raining && ex.PrecipNow - precipAtCard >= 0.25f) ev = WeatherKind.RainHeavier;
        if (wet > wetMax) wetMax = wet;
        if (ev == null && !raining && wetMax >= 4 && wet > 0 && wet <= wetMax - 2) { ev = WeatherKind.Drying; wetMax = wet + 1; }
        if (ev == null && wet == 1 && wetMax >= 3) { ev = WeatherKind.TrackDry; wetMax = 1; }
        if (ev == null && s.TrackTempC > 0 && Math.Abs(s.TrackTempC - tempRef) >= 5f) ev = s.TrackTempC > tempRef ? WeatherKind.TempUp : WeatherKind.TempDown;
        wasRaining = raining;
        if (ev != null) { wxPending = ev; wxPendingAt = now; if (ev is WeatherKind.RainStart or WeatherKind.RainHeavier) precipAtCard = ex.PrecipNow; if (ev is WeatherKind.TempUp or WeatherKind.TempDown) { wxPendingFrom = tempRef; tempRef = s.TrackTempC; } }
        if (wxPending is { } k && flags.Weather && settled && PresentationState.IsRacing && now - wxLastCard > 60000)
        {
            if (now - wxPendingAt > 90000) { wxPending = null; return; }   // too late to be news
            if (ctx.BattleActive || Busy(ctx) || YellowMap.Showing) return;
            wxPending = null; wxLastCard = now;
            Weather.Show(WeatherModelFor(k, s), now);
            Log?.Invoke("visual pack: weather card " + k);
        }
        else if (!flags.Weather) wxPending = null;
        if (Weather.Showing && (ctx.BattleActive || Busy(ctx))) Weather.Hide(now);
        lastWet = wet;
    }
    float wxPendingFrom;

    string Temp(RaceSnapshot s, float c) => UnitPrefs.Fahrenheit(s) ? $"{c * 9f / 5f + 32f:0}°F" : $"{c:0}°C";

    WeatherModel WeatherModelFor(WeatherKind k, RaceSnapshot s)
    {
        var cam = s.CameraTarget ?? s.Player ?? s.Leader;
        float pct = cam?.LapDistPct ?? -1;
        string place = pct >= 0 ? (TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, pct, s.TrackBroadcastName) ?? "") : "";
        string lead = k switch
        {
            WeatherKind.RainStart => place.Length > 0 ? "RAIN AT" : "RAIN FALLING",
            WeatherKind.RainHeavier => "RAIN GETTING HEAVIER",
            WeatherKind.Drying => "TRACK DRYING",
            WeatherKind.TrackDry => "TRACK DRY AGAIN",
            WeatherKind.TempUp => $"TRACK TEMP UP {Diff(s):0}°",
            _ => $"TRACK TEMP DOWN {Diff(s):0}°",
        };
        if (k != WeatherKind.RainStart) place = "";
        string lap = s.CurrentLap > 0 ? "LAP " + s.CurrentLap : "";
        string rain = s.Extras.RainChancePct > 0 ? $"{s.Extras.RainChancePct:0}%" : (s.Extras.PrecipNow > 0 ? $"{s.Extras.PrecipNow * 100:0}%" : "");
        return new WeatherModel(k, lead, place, lap, s.Extras.TrackWetness, lastWet, Temp(s, s.AirTempC), Temp(s, s.TrackTempC), rain, MapFor(s), pct);
    }

    float Diff(RaceSnapshot s) { float d = Math.Abs(s.TrackTempC - wxPendingFrom); return UnitPrefs.Fahrenheit(s) ? d * 9f / 5f : d; }

    // ------------------------------------------------------------------------------------------------ track map cache
    string? mapCfg, mapName, mapKey; bool mapTried; VpTrack? map;
    VpTrack? MapFor(RaceSnapshot s)
    {
        if (mapTried && ReferenceEquals(mapCfg, s.TrackConfigName) && ReferenceEquals(mapName, s.TrackDisplayName)) return map;
        bool same = mapTried && mapCfg == s.TrackConfigName && mapName == s.TrackDisplayName;
        mapCfg = s.TrackConfigName; mapName = s.TrackDisplayName; mapTried = true;
        if (same) return map;
        var b = TrackMapCache.Resolve(s.TrackConfigName, s.TrackDisplayName, s.TrackBroadcastName, s.TrackRawName);
        mapKey = b;
        map = b != null ? VpTrack.Load(Assets.Resolve("maps", b + ".json")) : null;
        return map;
    }

    // ------------------------------------------------------------------------------------------------ V3 battle radar
    void TickRadar(RaceSnapshot s, double now, in VpContext ctx)
    {
        var me = s.Player; var op = ctx.Opponent;
        bool want = flags.Radar && ctx.BattleActive && me != null && op != null && PresentationState.IsRacing && !Busy(ctx);
        if (!want) { if (Radar.Showing) Radar.Hide(now); return; }
        bool meAhead = me!.PositionInClass > 0 && op!.PositionInClass > 0 ? me.PositionInClass < op.PositionInClass : true;
        var ahead = meAhead ? me : op!; var behind = meAhead ? op! : me;
        if (!Radar.Showing || radarPair != (ahead.CarIdx, behind.CarIdx))
        {
            radarPair = (ahead.CarIdx, behind.CarIdx);
            int pos = Math.Max(1, Math.Min(ahead.PositionInClass, behind.PositionInClass));
            float lap = me.BestLapTime > 0 ? me.BestLapTime : me.LastLapTime > 0 ? me.LastLapTime : 100f;
            Radar.LapMetres = TrackCorners.LengthM(s.Extras.TrackLength);
            Radar.Show(new BattleRadarModel("BATTLE FOR P" + pos, me.ClassName, me.ClassColor, ahead.CarNumber, NameFormat.Broadcast(ahead),
                behind.CarNumber, NameFormat.Broadcast(behind), lap), now);
            Log?.Invoke($"visual pack: battle radar #{ahead.CarNumber} vs #{behind.CarNumber}");
        }
        if (behind.IntervalSeconds is double g) Radar.Feed(ReplayState.SessionTime, g);
    }
    (int, int) radarPair;

    // ------------------------------------------------------------------------------------------------ stints (V4 / V7)
    readonly bool[] wasInPit = new bool[N];
    readonly int[] stopCount = new int[N], maxStint = new int[N];
    readonly int[,] stopLaps = new int[N, 16];

    void TickStints(RaceSnapshot s, double now, in VpContext ctx)
    {
        if (s.Phase != RacePhase.Broadcast) return;
        var cls = s.ClassLeaderboard;
        for (int ci = 0; ci < cls.Count; ci++)
        {
            var ds = cls[ci].Drivers;
            for (int di = 0; di < ds.Count; di++)
            {
                var d = ds[di];
                int i = d.CarIdx;
                if ((uint)i >= N) continue;
                if (d.IsInPit && !wasInPit[i] && d.LapsCompleted > 0)
                {
                    int n = stopCount[i];
                    int last = n > 0 ? stopLaps[i, Math.Min(n, 16) - 1] : 0;
                    if (d.LapsCompleted - last >= 2 || n == 0)   // a drive-through and a stop on the same lap count once
                    {
                        maxStint[i] = Math.Max(maxStint[i], d.LapsCompleted - last);
                        if (n < 16) stopLaps[i, n] = d.LapsCompleted;
                        stopCount[i] = n + 1;
                    }
                }
                wasInPit[i] = d.IsInPit;
                int cur = d.LapsCompleted - LastStop(i);
                if (cur > maxStint[i]) maxStint[i] = cur;
            }
        }
        // fuel per lap for our own car
        var me = s.Player;
        if (me != null && ctx.ReadVar != null && me.LapsCompleted != fuelLap)
        {
            double fuel = ctx.ReadVar("FuelLevel");
            if (fuelLap >= 0 && !double.IsNaN(fuel) && !double.IsNaN(fuelAtLap))
            {
                double used = fuelAtLap - fuel;
                if (used > 0.05 && used < 30) fuelPerLap = double.IsNaN(fuelPerLap) ? used : fuelPerLap * 0.7 + used * 0.3;
            }
            fuelLap = me.LapsCompleted; fuelAtLap = fuel;
        }
    }
    int fuelLap = -1; double fuelAtLap = double.NaN, fuelPerLap = double.NaN;

    int LastStop(int i) { int n = stopCount[i]; return n > 0 ? stopLaps[i, Math.Min(n, 16) - 1] : 0; }

    int TypicalStint(DriverClass cl)
    {
        var all = new List<int>();
        foreach (var d in cl.Drivers)
        {
            int i = d.CarIdx; if ((uint)i >= N) continue;
            int prev = 0;
            for (int k = 0; k < Math.Min(stopCount[i], 16); k++) { int l = stopLaps[i, k]; if (l - prev >= 3) all.Add(l - prev); prev = l; }
        }
        if (all.Count == 0) return 0;
        all.Sort();
        return all[all.Count / 2];
    }

    // ------------------------------------------------------------------------------------------------ V4 strategy board
    double strategyAt = -1e12;

    void TickStrategy(RaceSnapshot s, double now, in VpContext ctx)
    {
        if (Strategy.Active(now) && (ctx.BattleActive || OutroDirector.FullScreen)) Strategy.Hide(now);
        if (!flags.Strategy || !PresentationState.IsRacing || now - strategyAt < 12 * 60000) return;
        bool longRace = s.SessionLengthSeconds >= 45 * 60 || s.TotalLaps >= 40;
        if (!longRace || ctx.BattleActive || Busy(ctx) || now < quietUntil) return;
        var cl = ClassOf(s, s.Player) ?? (s.ClassLeaderboard.Count > 0 ? s.ClassLeaderboard[0] : null);
        if (cl == null || TypicalStint(cl) == 0) return;
        strategyAt = now;
        Strategy.Show(StrategyFor(s, cl), now);
        Log?.Invoke("visual pack: strategy board " + cl.Name);
    }

    static DriverClass? ClassOf(RaceSnapshot s, Driver? d)
    {
        if (d == null) return null;
        foreach (var c in s.ClassLeaderboard) foreach (var x in c.Drivers) if (x.CarIdx == d.CarIdx) return c;
        return null;
    }

    public StrategyModel StrategyFor(RaceSnapshot s, DriverClass cl)
    {
        int typical = TypicalStint(cl);
        int lap = Math.Max(1, cl.Drivers.Count > 0 ? cl.Drivers[0].LapsCompleted + 1 : s.CurrentLap);
        float best = cl.Drivers.Count > 0 && cl.Drivers[0].BestLapTime > 0 ? cl.Drivers[0].BestLapTime : 0;
        int raceLaps = s.TotalLaps > 0 ? s.TotalLaps : best > 0 && s.SessionTimeRemaining > 0 ? lap + (int)Math.Ceiling(s.SessionTimeRemaining / best) : 0;
        var rows = new List<StrategyRow>();
        var list = cl.Drivers.Take(8).ToList();
        if (s.Player != null && !list.Any(d => d.CarIdx == s.Player.CarIdx))
        {
            var mine = cl.Drivers.FirstOrDefault(d => d.CarIdx == s.Player.CarIdx);
            if (mine != null) { list.RemoveAt(list.Count - 1); list.Add(mine); }
        }
        foreach (var d in list)
        {
            int i = d.CarIdx;
            var stops = new List<int>();
            for (int k = 0; (uint)i < N && k < Math.Min(stopCount[i], 16); k++) stops.Add(stopLaps[i, k]);
            int last = stops.Count > 0 ? stops[^1] : 0;
            var planned = new List<int>();
            if (typical > 0) for (int l = Math.Max(last + typical, d.LapsCompleted + 1); raceLaps > 0 && l < raceLaps; l += typical) planned.Add(l);
            rows.Add(new StrategyRow(d.PositionInClass, d.CarNumber, NameFormat.Broadcast(d), stops.Count, d.LapsCompleted - last, last,
                planned.Count > 0 ? planned[0] : 0, d.IsInPit, stops, planned, s.Player != null && d.CarIdx == s.Player.CarIdx));
        }
        string fuel = "";
        if (s.Player != null && !double.IsNaN(fuelAtLap) && fuelAtLap > 0)
        {
            fuel = !double.IsNaN(fuelPerLap) && fuelPerLap > 0
                ? $"YOUR FUEL  {fuelAtLap:0.0} L  ·  {fuelPerLap:0.0} L / LAP  ·  {Math.Floor(fuelAtLap / fuelPerLap):0} LAPS LEFT"
                : $"YOUR FUEL  {fuelAtLap:0.0} L";
        }
        return new StrategyModel(cl.Name, cl.Color, lap, raceLaps, typical, rows, fuel);
    }

    // ------------------------------------------------------------------------------------------------ V5 live tag
    bool wasBehindLive;
    double liveAt = -1; string liveSub = "";

    void TickLive(RaceSnapshot s, double now)
    {
        bool behind = ReplayWipe.BehindLiveNow;
        bool atLive = !ReplayState.IsReplayFile && (!ReplayState.IsReplay || (ReplayState.FramesBehindLive >= 0 && ReplayState.FramesBehindLive < 30));
        if (wasBehindLive && atLive)
        {
            wasBehindLive = false;
            if (flags.LiveTag && PresentationState.IsRacing)
            {
                // round 2: the BACK TO LIVE plate is cut in as the LIVE wipe clears (~0.65 s)
                liveSub = "LAP " + Math.Max(1, s.CurrentLap) + (s.SessionTimeRemaining > 0 ? "  ·  " + VpStr.Clock(s.SessionTimeRemaining) + " TO GO" : "");
                liveAt = now + 650;
            }
        }
        if (liveAt > 0 && now >= liveAt)
        {
            liveAt = -1;
            Live.Show(liveSub, now);
            Log?.Invoke(Live.Skipped ? "visual pack: BACK TO LIVE card skipped (no free spot)" : "visual pack: BACK TO LIVE card");
        }
        if (behind) { wasBehindLive = true; liveAt = -1; }
    }

    // ------------------------------------------------------------------------------------------------ V6 bumper
    double bumperAt = -1;

    void TickBumper(RaceSnapshot s, double now, in VpContext ctx)
    {
        if (flags.Bumper && ctx.WipeFired == "into the replay") bumperAt = now + 320;   // as the wipe clears
        if (bumperAt > 0 && now >= bumperAt)
        {
            bumperAt = -1;
            var p = SponsorPack.For("ReplayBumper");
            if (p == Partner.None) p = Presenters.For(StoryKind.RaceSoFar);
            if (p == Partner.None) return;   // no partner on: no bumper
            SKImage? logo = null;
            try { var path = SponsorPack.LogoPath(p); if (path != null) { using var b = SKBitmap.Decode(path); if (b != null) logo = SKImage.FromBitmap(b); } } catch { }
            var info = SponsorPack.Info(p);
            (string title, string sub) = p == Partner.AB ? ("A&B", "GENERAL CONTRACTING") : ((info.Word ?? p.ToString()).ToUpperInvariant(), "");
            var cam = s.CameraTarget;
            string moment = "LAP " + Math.Max(1, s.CurrentLap);
            if (cam != null && TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, cam.LapDistPct, s.TrackBroadcastName) is { } cn) moment += "  ·  " + cn.ToUpperInvariant();
            Bumper.Show(new BumperModel(title, sub, logo, moment, TimeOfDay: s.SessionTimeOfDay > 0 ? s.SessionTimeOfDay : -1), now);
            Log?.Invoke("visual pack: replay bumper " + p);
        }
    }

    // ------------------------------------------------------------------------------------------------ V7 race awards
    // round 2: one award at a time, each its own sponsored card (RaceAwards). The data is gathered all race long:
    // laps led per car (a class leader completing a lap), every clean lap time per car (for consistency), stints (above).
    readonly int[] lapsLed = new int[N], lapN = new int[N], lapSeen = new int[N];
    readonly float[,] lapT = new float[N, 96];
    readonly bool[] pitThisLap = new bool[N];
    readonly int[] lastLeaderLaps = Enumerable.Repeat(-1, 8).ToArray();
    readonly List<AwardCard> awardQueue = new();
    int awardNext = -1; double awardAt = -1;
    bool statsDone; double statsAt = -1;

    void TickLaps(RaceSnapshot s, double now)
    {
        if (s.Phase != RacePhase.Broadcast) return;
        var cls = s.ClassLeaderboard;
        for (int ci = 0; ci < cls.Count; ci++)
        {
            var ds = cls[ci].Drivers;
            if (ds.Count > 0 && ci < lastLeaderLaps.Length)
            {
                var ld = ds[0];
                if (lastLeaderLaps[ci] >= 0 && ld.LapsCompleted > lastLeaderLaps[ci] && (uint)ld.CarIdx < N) lapsLed[ld.CarIdx]++;
                lastLeaderLaps[ci] = ld.LapsCompleted;
            }
            for (int di = 0; di < ds.Count; di++)
            {
                var d = ds[di]; int i = d.CarIdx;
                if ((uint)i >= N) continue;
                if (d.IsInPit) pitThisLap[i] = true;
                if (d.LapsCompleted != lapSeen[i])
                {
                    // a lap just ended: keep it if it was a clean, timed, green lap (no pit, not the opening lap)
                    if (lapSeen[i] > 0 && d.LapsCompleted == lapSeen[i] + 1 && !pitThisLap[i] && d.LastLapTime > 0 && d.LapsCompleted > 1 && !s.Flags.Caution)
                    {
                        int n = lapN[i];
                        if (n < 96) { lapT[i, n] = d.LastLapTime; lapN[i] = n + 1; }
                    }
                    lapSeen[i] = d.LapsCompleted; pitThisLap[i] = d.IsInPit;
                }
            }
        }
    }

    void TickStats(RaceSnapshot s, double now)
    {
        if (!flags.Stats || !PresentationState.IsRaceSession) { awardQueue.Clear(); return; }
        if (!statsDone)
        {
            if (s.Flags.Checkered && statsAt < 0) statsAt = now + 9000;
            if (statsAt > 0 && now >= statsAt)
            {
                statsDone = true;
                awardQueue.Clear(); awardQueue.AddRange(AwardsFor(s));
                awardNext = 0; awardAt = now;
                if (awardQueue.Count > 0) Log?.Invoke($"visual pack: race awards ({awardQueue.Count})");
            }
        }
        // one at a time, only while nothing else holds the right half (the outro's own cards, a story card) and the
        // right-third column is free (round 4, #349: never over the battle card / battle map / radio card or any other
        // graphic there - VpLayout)
        bool room = VpLayout.IsFree(RaceAwards.Spot(), now, VpLayout.SlotAwards, 10f) || now < awardTestUntil;
        if (awardNext >= 0 && awardNext < awardQueue.Count && !Awards.Active(now) && now >= awardAt && !OutroDirector.FullScreen && !StoryDirector.Visible && room)
        {
            if (now - statsAt > 300000) { awardNext = -1; return; }
            Awards.Show(awardQueue[awardNext], awardNext, awardQueue.Count, now);
            if (!awardGaveWayLogged) Log?.Invoke("visual pack: award " + RaceAwards.TitleOf(awardQueue[awardNext].Kind));
            awardNext++;
            awardAt = now + RaceAwards.CardMs + 900;
        }
        if (Awards.Active(now) && !Awards.Leaving(now) && (StoryDirector.Visible || OutroDirector.FullScreen || !room))
        {
            // gives way at once; an award cut short before 60 % of its time plays again once the column is free
            if (Awards.Progress(now) < 0.6 && awardNext > 0) awardNext--;
            Awards.Hide(now);
            awardAt = now + RaceAwards.ExitMs + 600;
            if (!room && !awardGaveWayLogged) { awardGaveWayLogged = true; Log?.Invoke("visual pack: award gives way (right third taken) - it tries again when the column is free"); }
        }
    }

    /// <summary>Tonight's awards from what the race had (an award without real data is left out).</summary>
    public List<AwardCard> AwardsFor(RaceSnapshot s)
    {
        var all = s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsSpectator && !d.IsPaceCar && (uint)d.CarIdx < N).ToList();
        var list = new List<AwardCard>();
        AwardCard Card(AwardKind k, Driver d, string value, string detail, float[] series, string rival = "", string rivalNum = "") =>
            new(k, NameFormat.Broadcast(d), d.CarNumber, d.ClassName, d.ClassColor, d.CarName, value, detail, series, rival, rivalNum);
        // longest stint
        var stint = all.OrderByDescending(d => maxStint[d.CarIdx]).FirstOrDefault();
        if (stint != null && maxStint[stint.CarIdx] >= 3)
        {
            int len = maxStint[stint.CarIdx], i = stint.CarIdx, from = 0, to = len;
            int prev = 0;
            for (int k = 0; k <= Math.Min(stopCount[i], 16); k++)
            {
                int end = k < Math.Min(stopCount[i], 16) ? stopLaps[i, k] : stint.LapsCompleted;
                if (end - prev == len) { from = prev; to = end; break; }
                prev = end;
            }
            list.Add(Card(AwardKind.Stint, stint, len.ToString(CultureInfo.InvariantCulture),
                $"{stopCount[i]} STOP{(stopCount[i] == 1 ? "" : "S")} ALL RACE  ·  FINISHED P{stint.PositionInClass} IN {stint.ClassName}", new float[] { from, to }));
        }
        // most laps led (any class)
        var led = all.OrderByDescending(d => lapsLed[d.CarIdx]).FirstOrDefault();
        if (led != null && lapsLed[led.CarIdx] >= 2)
        {
            int raceLaps = Math.Max(led.LapsCompleted, 1);
            list.Add(Card(AwardKind.LapsLed, led, lapsLed[led.CarIdx].ToString(CultureInfo.InvariantCulture),
                $"LED {led.ClassName} FOR {lapsLed[led.CarIdx]} OF {raceLaps} LAPS  ·  FINISHED P{led.PositionInClass}", new float[] { lapsLed[led.CarIdx], raceLaps }));
        }
        // most consistent: smallest spread of clean laps (at least 6), within 103 % of their median
        Driver? best = null; float bestSd = float.MaxValue; float[]? bestSeries = null;
        foreach (var d in all)
        {
            int n = lapN[d.CarIdx];
            if (n < 6) continue;
            var t = new float[n]; for (int k = 0; k < n; k++) t[k] = lapT[d.CarIdx, k];
            var sorted = t.OrderBy(x => x).ToArray(); float med = sorted[n / 2];
            var dev = t.Where(x => x <= med * 1.03f).Select(x => x - med).ToArray();
            if (dev.Length < 6) continue;
            float sd = MathF.Sqrt(dev.Sum(x => x * x) / dev.Length);
            if (sd < bestSd) { bestSd = sd; best = d; bestSeries = dev; }
        }
        if (best != null && bestSeries != null)
            list.Add(Card(AwardKind.Consistent, best, bestSd.ToString("0.00", CultureInfo.InvariantCulture),
                $"{bestSeries.Length} CLEAN LAPS  ·  FINISHED P{best.PositionInClass} IN {best.ClassName}", bestSeries));
        // photo finish: the closest class finish under a second
        foreach (var c in s.ClassLeaderboard)
        {
            if (c.Drivers.Count < 2) continue;
            var p1 = c.Drivers[0]; var p2 = c.Drivers[1];
            if (p2.GapToLeaderSeconds is double g && g > 0 && g < 1.0 && p2.LapsCompleted == p1.LapsCompleted)
                if (!list.Any(x => x.Kind == AwardKind.PhotoFinish) || g < float.Parse(list.First(x => x.Kind == AwardKind.PhotoFinish).Value, CultureInfo.InvariantCulture))
                {
                    list.RemoveAll(x => x.Kind == AwardKind.PhotoFinish);
                    list.Add(Card(AwardKind.PhotoFinish, p1, g.ToString("0.000", CultureInfo.InvariantCulture), $"{c.Name} WON BY {g:0.000} S AFTER {p1.LapsCompleted} LAPS", new float[] { (float)g },
                        NameFormat.Broadcast(p2), p2.CarNumber));
                }
        }
        // the closest finish closes the set
        return list.OrderBy(x => x.Kind == AwardKind.PhotoFinish ? 1 : 0).ToList();
    }

    // ------------------------------------------------------------------------------------------------ V8 yellow map
    bool yellowShown;

    void TickYellow(RaceSnapshot s, double now, in VpContext ctx)
    {
        // round 4: also in a battle - the plate finds its own room in the right third (compact under the battle map)
        bool want = flags.YellowMap && YellowFlagCallout.YellowNow && !Busy(ctx);
        var m = want ? MapFor(s) : null;
        if (want && m != null && !yellowShown)
        {
            yellowShown = true;
            float pct = YellowFlagCallout.CurrentPct;
            var (a, b) = SectorAround(s, pct);
            // #425: the place the incident is at (its corner / straight name at the car's lap %) - its theme takes over the plate
            string place = YellowFlagCallout.CurrentPlace;
            if (pct >= 0 && (TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, pct, s.TrackBroadcastName) ?? TrackCorners.Straight(s.TrackRawName, s.Extras.TrackLength, pct)) is { Length: > 0 } at)
                place = at.ToUpperInvariant();
            YellowMap.Show(new YellowMapModel(m, a, b, pct, place, YellowFlagCallout.CurrentCause, 0, GroundFor(s)), now);
            Log?.Invoke("visual pack: yellow map " + YellowFlagCallout.CurrentPlace);
        }
        if (!want && yellowShown) { yellowShown = false; YellowMap.Hide(now); }
        if (yellowTestUntil > 0 && now >= yellowTestUntil) { yellowTestUntil = double.NegativeInfinity; if (!yellowShown) YellowMap.Hide(now); }
        if (YellowMap.Active(now))
        {
            YellowMap.ClearCars();
            var cls = s.ClassLeaderboard;
            for (int ci = 0; ci < cls.Count; ci++)
            {
                var col = Vp.ClassLine(cls[ci].Color);
                var ds = cls[ci].Drivers;
                for (int di = 0; di < ds.Count; di++)
                {
                    var d = ds[di];
                    if (d.IsInPit || d.LapDistPct < 0) continue;
                    YellowMap.SetCar(d.CarIdx, d.LapDistPct, col, d.CarIdx == YellowFlagCallout.CurrentCarIdx);
                }
            }
        }
    }

    /// <summary>The track card's terrain for this circuit (null until TrackTerrain has it built / cached).</summary>
    TrackTerrain.Data? GroundFor(RaceSnapshot s)
    {
        if (MapFor(s) == null || mapKey == null) return null;
        try { return TrackTerrain.For(mapKey, Assets.Resolve("maps", mapKey + ".json"), s.TrackLatitude, s.TrackLongitude, TrackCorners.LengthM(s.Extras.TrackLength)); }
        catch { return null; }
    }

    static (float, float) SectorAround(RaceSnapshot s, float pct)
    {
        if (pct < 0) return (0, 0);
        var lap = TrackCorners.Lap(s.TrackRawName, s.Extras.TrackLength);
        if (lap != null)
            foreach (var (_, a, b, _) in lap)
            {
                bool inside = a <= b ? pct >= a - 0.004f && pct <= b + 0.004f : pct >= a || pct <= b;
                if (inside) return (a - 0.006f, b + 0.006f);
            }
        return (pct - 0.02f, pct + 0.02f);
    }

    // ------------------------------------------------------------------------------------------------ W8 going to break
    double pausedSince = -1;
    bool bedOn;
    // 2026-11-06 (Angelo: every Live view button must work): Test buttons - the break card stays up 8 s even with no paused
    // replay; the race awards play through a busy right-hand column
    double breakTestUntil = double.NegativeInfinity, awardTestUntil = double.NegativeInfinity;
    double pipTestUntil = double.NegativeInfinity, lensTestUntil = double.NegativeInfinity, yellowTestUntil = double.NegativeInfinity;
    bool awardGaveWayLogged;

    void TickBreak(RaceSnapshot s, double now)
    {
        bool paused = ReplayState.IsReplay && ReplayState.Speed == 0;
        if (paused) { if (pausedSince < 0) pausedSince = now; } else pausedSince = -1;
        if (flags.Break && paused && !Break.Showing && now - pausedSince > 6000 && !OutroDirector.FullScreen)
        {
            Break.Show(BreakFor(s), now);
            if (flags.BreakMusic) bedOn = StorySound.PlayBedLoop("break_bed");
            Log?.Invoke("visual pack: going to break");
        }
        if (Break.Showing && !paused && now >= breakTestUntil)   // 2026-11-06: the Test button holds it 8 s without a paused replay
        {
            Break.Hide(now);
            if (bedOn) { StorySound.StopBed(); bedOn = false; }
            StorySound.PlayBroadcast("replay_wipe");
        }
    }

    BreakModel BreakFor(RaceSnapshot s)
    {
        var classes = new List<BreakClass>();
        foreach (var c in s.ClassLeaderboard)
        {
            var top = new List<(string, string, string)>();
            for (int i = 0; i < Math.Min(3, c.Drivers.Count); i++)
            {
                var d = c.Drivers[i];
                top.Add((d.CarNumber, NameFormat.Broadcast(d), i == 0 ? "LEADER" : d.GapToLeaderSeconds is double g ? "+" + g.ToString("0.0", CultureInfo.InvariantCulture) : ""));
            }
            if (top.Count > 0) classes.Add(new BreakClass(c.Name, c.Color, top));
        }
        string status = "Lap " + Math.Max(1, s.CurrentLap) + (s.TotalLaps > 0 ? " of " + s.TotalLaps : "") + (s.SessionTimeRemaining > 0 ? "  ·  " + VpStr.Clock(s.SessionTimeRemaining) + " to go" : "");
        int mins = (int)(s.SessionTimeOfDay / 60) % 1440;
        string paused = $"REPLAY PAUSED  ·  LAP {Math.Max(1, s.CurrentLap)}" + (s.SessionTimeOfDay > 0 ? $"  ·  {mins / 60:00}:{mins % 60:00}" : "");
        return new BreakModel(s.TrackDisplayName, status, paused, classes, MapFor(s));
    }

    // ------------------------------------------------------------------------------------------------ W10 PiP
    readonly float[] pipPct = new float[N], pipKmh = new float[N], maxRpm = new float[N];
    readonly double[] pipT = new double[N];
    (int, int) pipPair = (-1, -1);

    // 2026-10-27 (round 2, Angelo): the PiP is LIVE ONLY - never in a replay or while watching behind live - and it stands
    // down by itself when the chasing car's CarIdx steering / gear / RPM look dead (all zero, or not changing for 2 s):
    // whether iRacing fills those for other cars is still being checked on the PC (tools/carIdx_probe).
    readonly double[] pipSigAt = new double[N];
    readonly float[] pipSig = new float[N];

    bool PipDataLive(RaceSnapshot s, int i, double now)
    {
        if ((uint)i >= N) return false;
        float steerV = i < s.CarSteer.Length ? s.CarSteer[i] : 0f;
        int gearV = i < s.CarGear.Length ? s.CarGear[i] : 0;
        float rpmV = i < s.CarRpm.Length ? s.CarRpm[i] : 0f;
        float sig = steerV * 1000f + gearV * 7919f + rpmV;
        if (sig != pipSig[i] || pipSigAt[i] == 0) { pipSig[i] = sig; pipSigAt[i] = now; }
        bool allZero = steerV == 0f && gearV == 0 && rpmV == 0f;
        return !allZero && now - pipSigAt[i] <= 2000;
    }

    void TickPip(RaceSnapshot s, double now, in VpContext ctx)
    {
        if (now < pipTestUntil) return;   // 2026-11-06: the Test button's 8 s (then the normal rules take it down)
        var me = s.Player; var op = ctx.Opponent;
        bool liveOnly = !ReplayState.IsReplay && !ReplayState.IsReplayFile && !ReplayWipe.BehindLiveNow;
        bool want = flags.Pip && liveOnly && ctx.BattleActive && me != null && op != null && PresentationState.IsRacing && !Busy(ctx);
        Driver? chaser = null, leader = null;
        if (me != null && op != null) { bool meAhead0 = me.PositionInClass < op.PositionInClass; chaser = meAhead0 ? op : me; leader = meAhead0 ? me : op; }
        bool dataOk = chaser != null && PipDataLive(s, chaser.CarIdx, now);
        if (want && !dataOk)
        {
            if (Pip.Showing) Log?.Invoke("visual pack: PiP onboard stood down (no live CarIdx steering / gear / RPM for #" + chaser!.CarNumber + ")");
            want = false;
        }
        if (!want) { if (Pip.Showing) Pip.Hide(now); pipPair = (-1, -1); return; }
        if (pipPair != (chaser!.CarIdx, leader!.CarIdx) || !Pip.Showing)
        {
            pipPair = (chaser.CarIdx, leader.CarIdx);
            Pip.Show(new PipModel("BATTLE FOR P" + Math.Max(1, leader.PositionInClass), chaser.CarNumber, NameFormat.Broadcast(chaser),
                leader.CarNumber, NameFormat.Broadcast(leader), me!.ClassName, me.ClassColor), now);
            if (flags.PipCamera && Pip.Elevated && ctx.CameraFree) CutToCockpit?.Invoke(chaser.CarIdx);
            Log?.Invoke($"visual pack: PiP onboard #{chaser.CarNumber} chasing #{leader.CarNumber}");
        }
        double len = TrackCorners.LengthM(s.Extras.TrackLength), st = ReplayState.SessionTime;
        Feed(s, 0, chaser, st, len); Feed(s, 1, leader, st, len);
        if (chaser.IntervalSeconds is double g) Pip.SetGap(g);
    }

    void Feed(RaceSnapshot s, int slot, Driver d, double st, double len)
    {
        int i = d.CarIdx;
        if ((uint)i >= N) return;
        double dt = st - pipT[i];
        if (dt >= 0.2 && dt < 3 && len > 0 && d.LapDistPct >= 0)
        {
            float dp = d.LapDistPct - pipPct[i]; if (dp < -0.5f) dp += 1; if (dp > 0.5f) dp -= 1;
            float k = (float)(Math.Max(0, dp) * len / dt * 3.6);
            pipKmh[i] = pipKmh[i] <= 0 ? k : pipKmh[i] * 0.6f + k * 0.4f;
            pipT[i] = st; pipPct[i] = d.LapDistPct;
        }
        else if (dt >= 3 || dt < 0) { pipT[i] = st; pipPct[i] = d.LapDistPct; }
        float steerV = i < s.CarSteer.Length ? s.CarSteer[i] : 0f;
        int gearV = i < s.CarGear.Length ? s.CarGear[i] : 0;
        float rpmV = i < s.CarRpm.Length ? s.CarRpm[i] : 0f;
        if (rpmV > maxRpm[i]) maxRpm[i] = rpmV;
        Pip.SetCar(slot, steerV, gearV, rpmV, Math.Max(7000f, maxRpm[i]), pipKmh[i]);
    }

    // ------------------------------------------------------------------------------------------------ W12 race control
    readonly int[] lastInc = Enumerable.Repeat(-2, N).ToArray();
    readonly double[] incAt = new double[N];
    FlagState lastFlags;

    void TickRaceControl(RaceSnapshot s, double now, in VpContext ctx, bool settled)
    {
        var f = s.Flags;
        bool armed = flags.RaceControl && settled && now > quietUntil && PresentationState.NotificationsArmed;
        var me = s.Player ?? s.CameraTarget;
        if (armed && me != null)
        {
            if (f.Black && !lastFlags.Black) Rc(s, me, RcCategory.Penalty, "BLACK FLAG: SERVE THE PENALTY IN THE PIT LANE", now);
            else if (f.Disqualify && !lastFlags.Disqualify) Rc(s, me, RcCategory.Disqualified, "DISQUALIFIED", now);
            else if (f.Furled && !lastFlags.Furled) Rc(s, me, RcCategory.Warning, "BLACK AND WHITE FLAG: WARNING", now);
            else if (f.Repair && !lastFlags.Repair) Rc(s, me, RcCategory.Repair, "MEATBALL FLAG: REPAIRS REQUIRED IN THE PITS", now);
        }
        lastFlags = f;
        // incident points for the cars on screen / in our fight
        Check(s, s.Player, now, armed); Check(s, ctx.Opponent, now, armed); Check(s, s.CameraTarget, now, armed);
    }

    void Check(RaceSnapshot s, Driver? d, double now, bool armed)
    {
        if (d == null || (uint)d.CarIdx >= N || d.Incidents < 0) return;
        int i = d.CarIdx, prev = lastInc[i];
        lastInc[i] = d.Incidents;
        if (prev < 0 || d.Incidents <= prev || !armed || now - incAt[i] < 8000) return;
        incAt[i] = now;
        int add = d.Incidents - prev;
        string at = TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, d.LapDistPct, s.TrackBroadcastName) is { } cn ? " AT " + cn.ToUpperInvariant() : "";
        var (cat, text) = add switch
        {
            1 => (RcCategory.TrackLimits, $"OFF TRACK{at}, +1 INCIDENT POINT"),
            2 => (RcCategory.Noted, $"LOSS OF CONTROL{at}, +2 INCIDENT POINTS"),
            _ => (RcCategory.Noted, $"CONTACT{at}, +{add} INCIDENT POINTS"),
        };
        Rc(s, d, cat, text, now);
    }

    void Rc(RaceSnapshot s, Driver d, RcCategory cat, string text, double now)
    {
        int mins = (int)(s.SessionTimeOfDay / 60) % 1440;
        var msg = new RcMessage(cat, "L" + Math.Max(1, d.CurrentLap), s.SessionTimeOfDay > 0 ? $"{mins / 60:00}:{mins % 60:00}" : "",
            d.CarNumber, NameFormat.Broadcast(d), d.ClassName, d.ClassColor, text, d.Incidents, 0);
        // #619: the race-control bar at the top of the screen takes the decisions while it is on (the card is the fallback)
        if (RaceControlBar.On) { RaceControlBar.Post(msg); Log?.Invoke($"visual pack: race control bar #{d.CarNumber} {cat}: {text}"); return; }
        RaceControl.Show(new RcMessage(cat, "L" + Math.Max(1, d.CurrentLap), s.SessionTimeOfDay > 0 ? $"{mins / 60:00}:{mins % 60:00}" : "",
            d.CarNumber, NameFormat.Broadcast(d), d.ClassName, d.ClassColor, text, d.Incidents, 0), now);
        Log?.Invoke($"visual pack: race control #{d.CarNumber} {cat}: {text}");
    }

    // ------------------------------------------------------------------------------------------------ W9 / W13 sounds
    int seenPurple = -1, featuredCar = -1; bool wasRadio, wasFeatured; double lastBeep = -1e12, featuredSince;

    void TickSounds(RaceSnapshot s, double now, in VpContext ctx, bool settled)
    {
        // W9: the lighter cue on small lower-third cards (the featured card's driver changing, a team radio card)
        bool feat = PresentationState.FeaturedVisible;
        int car = s.CameraTarget?.CarIdx ?? -1;
        if (feat && wasFeatured && car != featuredCar && now - featuredSince > 1500 && flags.LowerThirdSounds && settled)
            StorySound.PlayBroadcast("lowerthird_light");
        if (feat && (!wasFeatured || car != featuredCar)) featuredSince = now;
        wasFeatured = feat; featuredCar = car;
        if (ctx.RadioActive && !wasRadio && flags.LowerThirdSounds && settled) StorySound.PlayBroadcast("lowerthird_light");
        wasRadio = ctx.RadioActive;
        // W13: purple sector by a class leader or the car on camera - the timing-screen beep (replays only, off by default)
        int pe = SectorTimer.ClassBestEvents;
        if (seenPurple < 0) seenPurple = pe;
        if (pe != seenPurple)
        {
            seenPurple = pe;
            int who = SectorTimer.LastClassBestCar;
            if (flags.PurpleBeep && settled && s.CurrentLap >= 3 && now - lastBeep > 4000 && (who == car || IsClassLeader(s, who)))
            {
                lastBeep = now;
                StorySound.PlayBroadcast("timing_beep");
            }
        }
    }

    static bool IsClassLeader(RaceSnapshot s, int carIdx)
    {
        var cls = s.ClassLeaderboard;
        for (int i = 0; i < cls.Count; i++) if (cls[i].Drivers.Count > 0 && cls[i].Drivers[0].CarIdx == carIdx) return true;
        return false;
    }

    /// <summary>Control app "test" button: plays an item now with the live session's data (or a sample when there is none).</summary>
    public string Test(string id, RaceSnapshot s, double now)
    {
        switch (id.ToUpperInvariant())
        {
            case "V1": Weather.Show(WeatherModelFor(WeatherKind.RainStart, s), now); return "weather card";
            case "V2": Night.Override = float.IsNaN(Night.Override) ? 1f : float.NaN; return float.IsNaN(Night.Override) ? "night grade follows the sun" : "night grade forced on";
            case "V3":
            {
                var me = s.Player ?? s.Leader; var cl = ClassOf(s, me);
                var other = cl?.Drivers.FirstOrDefault(d => d.CarIdx != me?.CarIdx);
                if (me == null || other == null) return "no cars";
                Radar.Show(new BattleRadarModel("BATTLE FOR P1", me.ClassName, me.ClassColor, other.CarNumber, NameFormat.Broadcast(other), me.CarNumber, NameFormat.Broadcast(me), me.BestLapTime > 0 ? me.BestLapTime : 100), now);
                return "battle radar";
            }
            case "V4": { var cl = ClassOf(s, s.Player) ?? s.ClassLeaderboard.FirstOrDefault(); if (cl == null) return "no field"; Strategy.Show(StrategyFor(s, cl), now); return "strategy board"; }
            case "V5": Live.Show("LAP " + Math.Max(1, s.CurrentLap) + (s.SessionTimeRemaining > 0 ? "  ·  " + VpStr.Clock(s.SessionTimeRemaining) + " TO GO" : ""), now); return "BACK TO LIVE card";
            case "V6": bumperAt = now; return "replay bumper";
            case "V7":
            {
                var aw = AwardsFor(s);
                if (aw.Count == 0) return "no awards in this session's data yet";
                awardQueue.Clear(); awardQueue.AddRange(aw); awardNext = 0; awardAt = now; statsAt = now; statsDone = true;
                awardTestUntil = now + aw.Count * (RaceAwards.CardMs + 900) + 2000; awardGaveWayLogged = false;
                return "race awards (" + aw.Count + ", one at a time)";
            }
            case "V8":
            {
                var m = MapFor(s); if (m == null) return "no track map";
                float pct = (s.CameraTarget ?? s.Leader)?.LapDistPct ?? 0.1f; var (a, b) = SectorAround(s, pct);
                YellowMap.Show(new YellowMapModel(m, a, b, pct, TrackCorners.Named(s.TrackRawName, s.Extras.TrackLength, pct, s.TrackBroadcastName)?.ToUpperInvariant() ?? "TEST", "TEST CARD", 0, GroundFor(s)), now);
                yellowTestUntil = now + 7000;   // 2026-11-06: up 7 s for the Live view button (was its minimum 3 s)
                return "yellow map (7 s test)";
            }
            case "W8":
                if (Break.Showing) { Break.Hide(now); breakTestUntil = double.NegativeInfinity; return "break card off"; }
                Break.Show(BreakFor(s), now); breakTestUntil = now + 8000; return "break card (8 s)";
            case "W10":   // 2026-11-06: the Live view's PiP button - the card with two cars of a class for 8 s
            {
                var me = s.Player ?? s.CameraTarget ?? s.Leader; var cl = ClassOf(s, me);
                var other = cl?.Drivers.FirstOrDefault(d => me != null && d.CarIdx != me.CarIdx && Math.Abs(d.PositionInClass - me.PositionInClass) == 1)
                            ?? cl?.Drivers.FirstOrDefault(d => me != null && d.CarIdx != me.CarIdx);
                if (me == null || other == null) return "no cars";
                bool meAhead = me.PositionInClass < other.PositionInClass;
                var (chaser, lead) = meAhead ? (other, me) : (me, other);
                Pip.Show(new PipModel("BATTLE FOR P" + Math.Max(1, lead.PositionInClass), chaser.CarNumber, NameFormat.Broadcast(chaser),
                    lead.CarNumber, NameFormat.Broadcast(lead), me.ClassName, me.ClassColor), now);
                Pip.SetGap(0.6);
                pipTestUntil = now + 8000;
                return "PiP onboard (8 s test)";
            }
            case "W11":   // 2026-11-06: the Live view's lens button - a golden-hour flare with the sun top right for 8 s
            {
                var at = new SKPoint(1560f, 110f);   // top right, in the sky (the flare sits under every graphic)
                Lens.ForceGolden = 0.9f; Lens.ForceSun = at; lensTestUntil = now + 8000;
                return $"lens flare (8 s test, sun at {at.X:0},{at.Y:0})";
            }
            case "W12": { var d = s.CameraTarget ?? s.Player ?? s.Leader; if (d == null) return "no car"; Rc(s, d, RcCategory.TrackLimits, "TEST: TRACK LIMITS, +1 INCIDENT POINT", now); return "race control"; }
            case "W13": StorySound.Play("timing_beep"); return "purple beep";
            case "W9": StorySound.Play("lowerthird_light"); return "lower-third cue";
        }
        return "unknown";
    }

    /// <summary>The graphics in draw order (lens under everything, then cards, the bumper / break above).</summary>
    public IEnumerable<(string Name, VpGraphic G)> Graphics()
    {
        yield return ("vp_lens", Lens);
        yield return ("vp_weather", Weather);
        yield return ("vp_radar", Radar);
        yield return ("vp_yellowmap", YellowMap);
        yield return ("vp_pip", Pip);
        yield return ("vp_racecontrol", RaceControl);
        yield return ("vp_strategy", Strategy);
        yield return ("vp_stats", Awards);
        yield return ("vp_live", Live);
        yield return ("vp_bumper", Bumper);
        yield return ("vp_break", Break);
    }

    public void Dispose()
    {
        foreach (var (_, g) in Graphics()) g.Dispose();
        Night.Dispose();
        if (bedOn) StorySound.StopBed();
    }
}
