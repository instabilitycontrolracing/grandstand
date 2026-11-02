using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The race diary (2026-10-19, Angelo: "tell the story of the race"). Quietly writes
/// down what happens while the race runs - where everyone started, lead changes, pit
/// stops (with their pit-lane times), fastest laps, the weather, halfway, and the
/// player's position lap by lap - so the story cards can show real events instead of
/// just the current numbers. Ticked once per frame from the compositor; resets itself
/// on every new session or track.
/// </summary>
public static partial class RaceDiary
{
    public sealed record PitStop(int CarIdx, string Name, string Brand, string Class, int Lap, double Seconds);
    /// <summary>A contact moment (2026-10-20): a car picked up 4x (iRacing's contact penalty) or more at
    /// once - paired with the other car when two cars close together on track jumped at the same time.</summary>
    public sealed record Contact(int CarIdx, string Name, string Brand, string Class, int Lap, int Points, int Total,
                                 int OtherCarIdx, string? OtherName, string? OtherBrand, int OtherPoints, int OtherTotal, DateTime At,
                                 float Pct = -1f,    // 2026-10-25: where on the lap (for "at Arnage" / "down the Mulsanne Straight")
                                 bool Dq = false);   // 2026-10-25: a disqualification (Angelo: the Trading Paints card with a big DQ)

    static int sessionNum = int.MinValue;
    static string track = "";
    static bool started;

    static readonly Dictionary<int, int> grid = new();
    static readonly Dictionary<int, int> pits = new();
    static readonly Dictionary<int, List<int>> pitLaps = new();   // the lap of each stop, per car
    static readonly Dictionary<int, bool> inPit = new();
    static readonly Dictionary<int, DateTime> pitEntry = new();
    static readonly List<PitStop> stops = new();
    static readonly Dictionary<string, int> leaderOf = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, int> leadChanges = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, (float Time, int CarIdx, string Name, int Lap)> fastest = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<(int Lap, int Pos)> playerHistory = new();
    static readonly Dictionary<int, List<(int Lap, int Pos)>> carHistory = new();
    static readonly Dictionary<int, int> carLaps = new();
    // 2026-10-25 (Angelo: "the race so far card lines are not accurate at all"): when each car completed each lap.
    // A car's place on lap N = the order its class crossed the line on lap N (a real lap chart), instead of the
    // live running order sampled at the instant it crossed (which still had cars a moment either side of the line).
    static readonly Dictionary<int, Dictionary<int, long>> crossed = new();
    static bool sawStart;   // the diary saw lap 1 being completed (so the grid can be the chart's first column)
    static readonly Dictionary<int, int> incidents = new();
    static readonly List<Contact> contacts = new();
    static readonly List<(Driver D, string Class, int Delta)> rises = new();
    // track limits: every time a car goes fully off (TrackSurface = OffTrack), where on the lap
    static readonly Dictionary<int, int> offs = new();
    static readonly Dictionary<int, bool> wasOff = new();
    static readonly Dictionary<int, DateTime> lastOff = new();
    static readonly List<(int CarIdx, float Pct)> offSpots = new();
    // 2026-10-25 (Angelo: "the hotspot on the On the Limit card is always on the pit straight"): cars on the pit entry /
    // exit lanes read as off track for a moment either side of the pit-road line; those aren't excursions. An off within
    // 25 s of the car being on pit road (before or after) doesn't count, and one that turns out to be a pit entry is taken back.
    static readonly Dictionary<int, DateTime> lastPit = new();
    static readonly Dictionary<int, (DateTime At, float Pct)> pendingOff = new();
    public static int OffsOf(int carIdx) => offs.TryGetValue(carIdx, out var n) ? n : 0;
    public static int TotalOffs => offSpots.Count;
    public static IReadOnlyList<(int CarIdx, float Pct)> OffSpots => offSpots;

    /// <summary>Every contact moment so far, oldest first.</summary>
    public static IReadOnlyList<Contact> Contacts => contacts;
    /// <summary>Incident points this car has right now (0 when not known).</summary>
    public static int IncidentsOf(int carIdx) => incidents.TryGetValue(carIdx, out var n) ? n : 0;

    // 2026-10-25 (Angelo: incidents "should accurately show up with each car", the graphics only showed his): iRacing
    // only sends OUR incident points in official races - everyone else reads 0. So unless other cars really report
    // points (hosted races, some replays), another car's count is our own estimate: 1x per trip fully off the track,
    // 2x per contact we saw (incident-point jumps and the cars' motion).
    static int playerIdx = -1;
    /// <summary>True when iRacing gives real incident points for cars other than ours.</summary>
    public static bool OthersReal { get { foreach (var kv in incidents) if (kv.Key != playerIdx && kv.Value > 0) return true; return false; } }
    /// <summary>Our estimate of a car's incident points from what we saw.</summary>
    public static int EstimateOf(int carIdx)
    {
        int n = OffsOf(carIdx);
        foreach (var c in contacts) if (c.CarIdx == carIdx || c.OtherCarIdx == carIdx) n += 2;
        return n;
    }
    /// <summary>A car's incident points for the graphics: iRacing's when real, else the estimate.</summary>
    public static int IncOf(int carIdx, int reported) =>
        reported >= 0 && (carIdx == playerIdx || OthersReal) ? reported : EstimateOf(carIdx);
    public static int IncOf(Driver d) => IncOf(d.CarIdx, d.Incidents);
    /// <summary>True when iRacing is giving us incident counts at all.</summary>
    public static bool HasIncidents => incidents.Count > 0;
    /// <summary>Set by a new contact; the story director takes it for a Car Contact card.</summary>
    public static Contact? ContactEvent { get; private set; }
    public static Contact? TakeContactEvent() { var e = ContactEvent; ContactEvent = null; return e; }

    /// <summary>Every car's class position at the end of each lap (lap 0 = grid) - the lap chart.</summary>
    /// <summary>The lap chart for one class: for every car, its place at the end of each lap the diary saw, by the
    /// order the class crossed the line on that lap (lap 0 = the grid, only when the diary saw the start).</summary>
    public static Dictionary<int, List<(int Lap, int Pos)>> LapChart(IReadOnlyList<Driver> cls)
    {
        var res = new Dictionary<int, List<(int Lap, int Pos)>>();
        int maxLap = 0;
        foreach (var d in cls)
            if (crossed.TryGetValue(d.CarIdx, out var cr)) foreach (var l in cr.Keys) maxLap = Math.Max(maxLap, l);
        foreach (var d in cls)
        {
            var list = new List<(int Lap, int Pos)>();
            if (sawStart && GridPos(d.CarIdx) > 0) list.Add((0, GridPos(d.CarIdx)));
            res[d.CarIdx] = list;
        }
        for (int lap = 1; lap <= maxLap; lap++)
        {
            var order = new List<(long T, int Car)>();
            foreach (var d in cls)
                if (crossed.TryGetValue(d.CarIdx, out var cr) && cr.TryGetValue(lap, out var tk)) order.Add((tk, d.CarIdx));
            if (order.Count == 0) continue;
            order.Sort((x, y) => x.T.CompareTo(y.T));
            for (int i = 0; i < order.Count; i++) res[order[i].Car].Add((lap, i + 1));
        }
        return res;
    }

    public static IReadOnlyList<(int Lap, int Pos)> History(int carIdx)
        => carHistory.TryGetValue(carIdx, out var h) ? h : (IReadOnlyList<(int Lap, int Pos)>)Array.Empty<(int, int)>();
    static int lastPlayerLaps = -1;

    // weather + pace
    static float startTrackC = float.NaN, shownTrackC = float.NaN, startAirC = float.NaN;
    static bool wasRaining;
    static int shownWet;
    static double classPaceEarly;

    /// <summary>True once the green flag has fallen in a race session.</summary>
    public static bool RaceStarted => started;
    public static IReadOnlyList<(int Lap, int Pos)> PlayerHistory => playerHistory;
    public static IReadOnlyList<PitStop> Stops => stops;

    /// <summary>Halfway point reached (latched once per race).</summary>
    public static bool HalfwayReached { get; private set; }
    /// <summary>Set when the weather changes in a way worth a Weather Watch (rain starts / stops,
    /// track temperature moves 1 F since the last report). Cleared by <see cref="TakeWeatherEvent"/>.</summary>
    public static string? WeatherEvent { get; private set; }
    /// <summary>UTC time of the last weather change - the session bar shows its weather partner tag for a while after.</summary>
    public static DateTime WeatherChangedAt { get; private set; } = DateTime.MinValue;
    public static float StartTrackC => startTrackC;
    public static float StartAirC => startAirC;
    /// <summary>Average last lap of the class front-runners around lap 3 (0 = not captured yet).</summary>
    public static double ClassPaceEarly => classPaceEarly;

    /// <summary>Where this car started in its class (0 = unknown).</summary>
    public static int GridPos(int carIdx) => grid.TryGetValue(carIdx, out var p) ? p : 0;
    /// <summary>Places gained since the start (positive = gained).</summary>
    public static int Gained(Driver d) => GridPos(d.CarIdx) > 0 && d.PositionInClass > 0 ? GridPos(d.CarIdx) - d.PositionInClass : 0;
    public static int PitStops(int carIdx) => pits.TryGetValue(carIdx, out var n) ? n : 0;
    /// <summary>The lap each of this car's stops happened on (the in-lap), oldest first.</summary>
    public static IReadOnlyList<int> PitLaps(int carIdx)
        => pitLaps.TryGetValue(carIdx, out var l) ? l : (IReadOnlyList<int>)Array.Empty<int>();
    public static int TotalPitStops(IEnumerable<Driver> drivers) => drivers.Sum(d => PitStops(d.CarIdx));
    public static int LeadChanges(string cls) => leadChanges.TryGetValue(cls, out var n) ? n : 0;
    public static int AllLeadChanges => leadChanges.Values.Sum();
    public static (float Time, int CarIdx, string Name, int Lap)? Fastest(string cls)
        => fastest.TryGetValue(cls, out var f) ? f : null;

    public static string? TakeWeatherEvent() { var e = WeatherEvent; WeatherEvent = null; return e; }

    /// <summary>Average last-lap of a class's top five right now (0 when not enough laps).</summary>
    public static double ClassPace(DriverClass cls)
    {
        double sum = 0; int n = 0;
        for (int i = 0; i < cls.Drivers.Count && n < 5; i++)
        {
            var d = cls.Drivers[i];
            if (d.LastLapTime > 30 && !d.IsInPit) { sum += d.LastLapTime; n++; }
        }
        return n >= 3 ? sum / n : 0;
    }

    public static void Reset()
    {
        started = false; grid.Clear(); pits.Clear(); pitLaps.Clear(); inPit.Clear(); pitEntry.Clear(); stops.Clear();
        leaderOf.Clear(); leadChanges.Clear(); fastest.Clear(); playerHistory.Clear(); lastPlayerLaps = -1;
        carHistory.Clear(); carLaps.Clear(); crossed.Clear(); sawStart = false; incidents.Clear(); contacts.Clear(); rises.Clear(); ContactEvent = null; shownWet = 0;
        offs.Clear(); wasOff.Clear(); lastOff.Clear(); offSpots.Clear(); lastPit.Clear(); pendingOff.Clear();
        startTrackC = shownTrackC = startAirC = float.NaN; wasRaining = false; classPaceEarly = 0;
        HalfwayReached = false; WeatherEvent = null; WeatherChangedAt = DateTime.MinValue;
        ResetLive();
        ResetContacts();
    }

    public static void Tick(RaceSnapshot snap)
    {
        playerIdx = snap.PlayerCarIdx;
        if (!snap.IsConnected) return;
        string tk = snap.TrackRawName ?? "";
        if (snap.SessionNum != sessionNum || !string.Equals(tk, track, StringComparison.Ordinal))
        {
            sessionNum = snap.SessionNum; track = tk; Reset(); PitLaneMap.UseTrack(tk);
            Restore(snap.SessionKey.Length > 0 ? snap.SessionKey + ":" + tk : "");
        }
        else if (snap.SessionKey.Length > 0 && storeKey != snap.SessionKey + ":" + tk)
        {
            // the subsession id arrived after the session number (session info is read a moment later)
            Reset(); Restore(snap.SessionKey + ":" + tk);
        }
        bool isRace = (snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
        if (!isRace || snap.Phase != RacePhase.Broadcast) return;
        var now = snap.CapturedAt;
        if (trimPending) TrimToNow(snap);

        foreach (var cls in snap.ClassLeaderboard)
        {
            foreach (var d in cls.Drivers)
            {
                // starting slot: the qualifying grid, else where we first saw them racing
                if (!grid.ContainsKey(d.CarIdx))
                {
                    int g = snap.GridClassPositions.TryGetValue(d.CarIdx, out var gp) && gp > 0 ? gp : d.PositionInClass;
                    if (g > 0) grid[d.CarIdx] = g;
                }
                // pit stops: count each entry to pit road once the race is under way, and time
                // the trip down the lane (entry to exit)
                bool was = inPit.TryGetValue(d.CarIdx, out var w) && w;
                if (d.IsInPit && !was && inPit.ContainsKey(d.CarIdx)) PitLaneMap.SawEntry(d.LapDistPct);   // where the pit lane starts / ends
                if (!d.IsInPit && was) PitLaneMap.SawExit(d.LapDistPct);
                // (2026-10-25: not before the car's first lap is done - cars sitting in the pit lane at the start, or a
                // replay rewound to the start, were being counted as stops)
                if (started && d.IsInPit && !was && d.LapsCompleted >= 1)
                {
                    pits[d.CarIdx] = PitStops(d.CarIdx) + 1; pitEntry[d.CarIdx] = now;
                    if (!pitLaps.TryGetValue(d.CarIdx, out var pl)) pitLaps[d.CarIdx] = pl = new List<int>();
                    pl.Add(Math.Max(1, d.LapsCompleted + 1));      // the lap they came in on
                }
                if (started && !d.IsInPit && was && pitEntry.TryGetValue(d.CarIdx, out var t0))
                {
                    double secs = (now - t0).TotalSeconds;
                    if (secs > 5 && secs < 300)
                        stops.Add(new PitStop(d.CarIdx, NameFormat.Broadcast(d), d.CarBrand, cls.Name, Math.Max(1, d.LapsCompleted), secs));
                    pitEntry.Remove(d.CarIdx);
                }
                inPit[d.CarIdx] = d.IsInPit;
                // lap chart: class position at each lap end (grid slot as lap 0)
                if (!carHistory.TryGetValue(d.CarIdx, out var hist))
                {
                    hist = new List<(int Lap, int Pos)>();
                    carHistory[d.CarIdx] = hist;
                    if (GridPos(d.CarIdx) > 0) hist.Add((0, GridPos(d.CarIdx)));
                }
                if (carLaps.TryGetValue(d.CarIdx, out var clPrev) && d.LapsCompleted > clPrev && d.LapsCompleted > 0)
                {
                    if (!crossed.TryGetValue(d.CarIdx, out var cr)) crossed[d.CarIdx] = cr = new Dictionary<int, long>();
                    cr[d.LapsCompleted] = now.Ticks;
                    if (d.LapsCompleted == 1 && started) sawStart = true;
                }
                if ((!carLaps.TryGetValue(d.CarIdx, out var cl0) || d.LapsCompleted > cl0) && d.PositionInClass > 0)
                {
                    if (d.LapsCompleted > 0 && (hist.Count == 0 || hist[^1].Lap != d.LapsCompleted)) hist.Add((d.LapsCompleted, d.PositionInClass));
                    carLaps[d.CarIdx] = d.LapsCompleted;
                }
                // incident points: note every jump once the race is on
                if (d.Incidents >= 0)
                {
                    if (started && incidents.TryGetValue(d.CarIdx, out var inc0) && d.Incidents > inc0) rises.Add((d, cls.Name, d.Incidents - inc0));
                    incidents[d.CarIdx] = d.Incidents;
                }
                // track limits: a new excursion when a car leaves the track after 3 s+ on it
                bool off = d.OffTrack && !d.IsInPit;
                bool offBefore = wasOff.TryGetValue(d.CarIdx, out var wo) && wo;
                if (d.IsInPit)
                {
                    // just went into the pits after going "off": that was the pit entry lane - take it back
                    if (pendingOff.TryGetValue(d.CarIdx, out var po) && (now - po.At).TotalSeconds < 25)
                    {
                        int at = offSpots.FindLastIndex(x => x.CarIdx == d.CarIdx && x.Pct == po.Pct);
                        if (at >= 0) { offSpots.RemoveAt(at); offs[d.CarIdx] = Math.Max(0, OffsOf(d.CarIdx) - 1); }
                    }
                    pendingOff.Remove(d.CarIdx);
                    lastPit[d.CarIdx] = now;
                }
                bool nearPits = lastPit.TryGetValue(d.CarIdx, out var lp) && (now - lp).TotalSeconds < 25;
                if (started && off && !offBefore && !nearPits && d.LapDistPct >= 0 && (!lastOff.TryGetValue(d.CarIdx, out var lo) || (now - lo).TotalSeconds > 3))
                { offs[d.CarIdx] = OffsOf(d.CarIdx) + 1; offSpots.Add((d.CarIdx, d.LapDistPct)); pendingOff[d.CarIdx] = (now, d.LapDistPct); }
                if (off) lastOff[d.CarIdx] = now;
                wasOff[d.CarIdx] = off;
                // class fastest lap
                if (d.BestLapTime > 0 && (!fastest.TryGetValue(cls.Name, out var f) || d.BestLapTime < f.Time - 0.0005f))
                    fastest[cls.Name] = (d.BestLapTime, d.CarIdx, NameFormat.Broadcast(d), Math.Max(1, d.LapsCompleted));
            }
            // lead changes
            Driver? lead = cls.Drivers.Count > 0 ? cls.Drivers[0] : null;   // no LINQ in the per-frame path
            for (int i = 0; i < cls.Drivers.Count; i++) if (cls.Drivers[i].PositionInClass == 1) { lead = cls.Drivers[i]; break; }
            if (lead != null)
            {
                if (leaderOf.TryGetValue(cls.Name, out var prev) && prev != lead.CarIdx)
                    leadChanges[cls.Name] = LeadChanges(cls.Name) + 1;
                leaderOf[cls.Name] = lead.CarIdx;
            }
        }

        // contact: two cars close together jumping 4x+ in the same update are one contact;
        // a lone 4x+ is still contact (with a wall, or a car iRacing didn't charge)
        for (int i = 0; i < rises.Count; i++)
        {
            var (d, cn, delta) = rises[i];
            if (delta < 4) continue;
            int j = -1; float best = 0.03f;
            for (int k = 0; k < rises.Count; k++)
            {
                if (k == i || rises[k].Delta < 4) continue;
                float gap = Math.Abs(rises[k].D.LapDistPct - d.LapDistPct); gap = Math.Min(gap, 1 - gap);
                if (gap < best) { best = gap; j = k; }
            }
            var o = j >= 0 ? rises[j].D : null;
            var ct = new Contact(d.CarIdx, NameFormat.Broadcast(d), d.CarBrand, cn, Math.Max(1, d.LapsCompleted + 1), delta, d.Incidents,
                                 o?.CarIdx ?? -1, o != null ? NameFormat.Broadcast(o) : null, o?.CarBrand, j >= 0 ? rises[j].Delta : 0, o?.Incidents ?? 0, now, d.LapDistPct);
            contacts.Add(ct);
            contactAt[d.CarIdx] = now; if (o != null) contactAt[o.CarIdx] = now;
            // the player's own contact is the headline when it involves them
            if (ContactEvent == null || d.CarIdx == snap.PlayerCarIdx || o?.CarIdx == snap.PlayerCarIdx || ct.Points + ct.OtherPoints > ContactEvent.Points + ContactEvent.OtherPoints)
                ContactEvent = ct;
            if (j >= 0) rises[j] = (rises[j].D, rises[j].Class, 0);   // already paired
        }
        rises.Clear();
        if (started) TickContacts(snap, now);

        var p = snap.Player;
        if (p != null)
        {
            if (!started)
            {
                int g = GridPos(p.CarIdx);
                if (g > 0) playerHistory.Add((0, g));
            }
            if (p.LapsCompleted > lastPlayerLaps && p.PositionInClass > 0)
            {
                if (playerHistory.Count == 0 || playerHistory[^1].Lap != p.LapsCompleted)
                    playerHistory.Add((p.LapsCompleted, p.PositionInClass));
                lastPlayerLaps = p.LapsCompleted;
            }
            // early-race pace of the player's class, captured once around lap 3
            if (classPaceEarly <= 0 && p.LapsCompleted >= 3)
                foreach (var cls in snap.ClassLeaderboard)
                    if (string.Equals(cls.Name, p.ClassName, StringComparison.OrdinalIgnoreCase)) { classPaceEarly = ClassPace(cls); break; }
        }

        // weather
        bool raining = snap.IsRaining || snap.Extras.PrecipNow > 0.01f;
        if (!started)
        {
            startTrackC = shownTrackC = snap.TrackTempC; startAirC = snap.AirTempC; wasRaining = raining; shownWet = snap.Extras.TrackWetness;
        }
        else
        {
            int wet = snap.Extras.TrackWetness;
            if (shownWet == 0) shownWet = wet;
            if (raining != wasRaining) { WeatherEvent = raining ? "RAIN" : "DRYING"; WeatherChangedAt = now; wasRaining = raining; shownWet = wet; }
            // standing water without rain falling (or a drying line appearing): two steps on iRacing's wetness scale
            else if (wet > 0 && shownWet > 0 && Math.Abs(wet - shownWet) >= 2)
            {
                WeatherEvent = wet > shownWet ? "WET" : "DRYING"; WeatherChangedAt = now; shownWet = wet;
            }
            else if (!float.IsNaN(shownTrackC) && Math.Abs(snap.TrackTempC - shownTrackC) >= 5f / 9f)   // 2026-10-25 (Angelo): every 1 F (was 4 C) - "I want frequent updates"
            {
                WeatherEvent = snap.TrackTempC > shownTrackC ? "HEATING" : "COOLING";
                WeatherChangedAt = now; shownTrackC = snap.TrackTempC;
            }
        }
        if (!HalfwayReached && started && RaceProgress.Of(snap) >= 0.5) HalfwayReached = true;
        if (started) TickLive(snap, now);
        started = true;
        MaybeSave(now);
    }
}
