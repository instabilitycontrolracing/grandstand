using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "do 2 through 5"): the PRODUCER DESK - what a broadcast network's producer needs on top of the
/// automatic show.
///   History     - every car's laps, gaps at each lap, pit stops (time in the pit lane, time stopped), stint length,
///                 driver changes; reset each session
///   Replay      - mark a moment (button / Stream Deck "--do mark" / hotkey), list them, play any one now (normal or
///                 half speed, from 6 s before, on a TV camera), then back to live. Never while you drive.
///   Booth       - the commentator page's data (GET /api/booth): the whole field + storylines
///   On demand   - name tags (commentators), driver focus, head-to-head, gap graph, pit table, sector bests,
///                 live championship (league standings + this race's points)
///   League      - roster (iRacing name -> display name, team, country), steward penalties to the race-control bar,
///                 driver-change lower third (endurance), championship card at the flag
/// Settings: Documents\iRacing\Grandstand\desk.json.
/// </summary>
internal static class ProducerDesk
{
    // ================================================================ settings
    public sealed class Commentator { public string Name { get; set; } = ""; public string Role { get; set; } = "Commentator"; }
    public sealed class Settings
    {
        public List<Commentator> Commentators { get; set; } = new();
        public string PointsTable { get; set; } = "35,32,30,28,26,25,24,23,22,21,20,19,18,17,16,15,14,13,12,11";
        public string RosterSource { get; set; } = "";
        public bool AutoDriverChange { get; set; } = true;
        public bool AutoChampionshipAtFlag { get; set; } = true;
        public bool LanOn { get; set; }
        public string LanPin { get; set; } = "";
        public int CardSeconds { get; set; } = 12;
    }
    static readonly string FilePath = Path.Combine(ControlState.Folder, "desk.json");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public static Settings Current { get; private set; } = Load();
    static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new(); } catch { }
        var s = new Settings();
        if (s.LanPin.Length == 0) s.LanPin = Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
        return s;
    }
    public static void Save(Settings s)
    {
        string oldRoster = Current.RosterSource;
        if (string.IsNullOrWhiteSpace(s.LanPin)) s.LanPin = Random.Shared.Next(1000, 9999).ToString(CultureInfo.InvariantCulture);
        Current = s;
        try { Directory.CreateDirectory(ControlState.Folder); File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json)); } catch { }
        if (s.RosterSource != oldRoster) _ = Task.Run(ReloadRoster);
    }

    static RaceStateService rs = null!;
    static Action<string> log = _ => { };
    public static void Init(RaceStateService r, Action<string> logger)
    {
        rs = r; log = logger;
        RaceStateService.NameOverride = (name, num) => RosterFor(name, num)?.Display is { Length: > 0 } d ? d : null;
        _ = Task.Run(ReloadRoster);
        new Thread(() => { while (true) { try { Track(); } catch (Exception ex) { log("desk: " + ex.Message); } Thread.Sleep(250); } })
        { IsBackground = true, Name = "Grandstand-Desk", Priority = ThreadPriority.BelowNormal }.Start();
    }

    // ================================================================ history
    public sealed class PitStop { public int Lap; public double Lane; public double Stopped; public double At; }
    sealed class CarHist
    {
        public int Idx; public string Name = ""; public int LastLaps = -1;
        public readonly List<float> Laps = new();                       // lap times
        public readonly Dictionary<int, double> GapAtLap = new();      // gap to the class leader at each lap end
        public readonly List<(int Lap, double Interval)> Intervals = new();
        public readonly List<PitStop> Stops = new();
        public bool InPit; public double PitIn; public double Stationary; public float LastPct = -1; public double LastT = -1;
        public int StintStartLap; public double StintStartT;
        public int LastPos = -1, TrackGains;   // 2026-11-06 (#880): places won on track (not pit-stop shuffles)
        public readonly List<(int Lap, string From, string To)> Changes = new();
    }
    static readonly object gate = new();
    static readonly Dictionary<int, CarHist> hist = new();
    static string histKey = "";
    static int histJumps = -1;
    static int champShownFor; static string champFlagKey = "";

    static double SessionTime() { try { return rs.ReadVar("SessionTime") is double d ? d : ReplayState.SessionTime; } catch { return ReplayState.SessionTime; } }

    static void Track()
    {
        var s = rs.Current;
        if (!s.IsConnected) return;
        double t = SessionTime();
        lock (gate)
        {
            if (s.SessionKey != histKey) { histKey = s.SessionKey; hist.Clear(); }
            // 2026-11-06 (#933): a replay jump re-shuffles the running order - forget where each car was (not a pass)
            if (ReplayState.JumpCount != histJumps) { histJumps = ReplayState.JumpCount; foreach (var hh in hist.Values) hh.LastPos = -1; }
            bool race = (s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
            bool green = s.SessionState is >= 4;
            foreach (var cls in s.ClassLeaderboard)
                foreach (var d in cls.Drivers)
                {
                    if (d.IsPaceCar || d.IsSpectator) continue;
                    if (!hist.TryGetValue(d.CarIdx, out var h)) { hist[d.CarIdx] = h = new CarHist { Idx = d.CarIdx, Name = d.Name, LastLaps = d.LapsCompleted, StintStartLap = d.LapsCompleted, StintStartT = t }; continue; }
                    // 2026-11-06 (#880): a gain counts only when this car and every car it went past were out on the track
                    // 2026-11-06 (#933, "#25 has passed 6... 7... 8 cars" on the formation lap): only after the green, and
                    // NET - a place lost on track takes one back (positions flicker P5-P4-P5 in a pack; that is not 9 passes)
                    if (race && d.PositionInClass > 0)
                    {
                        if (green && h.LastPos > 0 && d.PositionInClass < h.LastPos && h.LastPos - d.PositionInClass <= 3 && !d.IsInPit
                            && !cls.Drivers.Any(o => o.CarIdx != d.CarIdx && o.PositionInClass > d.PositionInClass && o.PositionInClass <= h.LastPos && o.IsInPit))
                            h.TrackGains += h.LastPos - d.PositionInClass;
                        else if (green && h.LastPos > 0 && d.PositionInClass > h.LastPos && d.PositionInClass - h.LastPos <= 3 && !d.IsInPit)
                            h.TrackGains = Math.Max(0, h.TrackGains - (d.PositionInClass - h.LastPos));
                        h.LastPos = d.PositionInClass;
                    }
                    // a lap finished
                    if (d.LapsCompleted > h.LastLaps)
                    {
                        if (d.LastLapTime > 0) h.Laps.Add(d.LastLapTime);
                        if (d.GapToLeaderSeconds is double g) h.GapAtLap[d.LapsCompleted] = g;
                        if (d.IntervalSeconds is double iv) { h.Intervals.Add((d.LapsCompleted, iv)); if (h.Intervals.Count > 60) h.Intervals.RemoveAt(0); }
                        h.LastLaps = d.LapsCompleted;
                    }
                    // pit lane: time in the lane, and time standing still (lap position not moving)
                    if (d.IsInPit && !h.InPit) { h.InPit = true; h.PitIn = t; h.Stationary = 0; }
                    else if (d.IsInPit && h.LastT >= 0 && t > h.LastT && Math.Abs(d.LapDistPct - h.LastPct) < 0.00004f) h.Stationary += t - h.LastT;
                    else if (!d.IsInPit && h.InPit)
                    {
                        h.InPit = false;
                        double lane = t - h.PitIn;
                        if (lane > 5 && lane < 900) h.Stops.Add(new PitStop { Lap = d.LapsCompleted, Lane = lane, Stopped = h.Stationary, At = t });
                        h.StintStartLap = d.LapsCompleted; h.StintStartT = t;
                    }
                    h.LastPct = d.LapDistPct; h.LastT = t;
                    // driver change (team races): same car, another driver
                    if (d.Name != h.Name && d.Name.Length > 0 && h.Name.Length > 0)
                    {
                        h.Changes.Add((d.LapsCompleted, h.Name, d.Name));
                        if (race && Current.AutoDriverChange && !ReplayState.IsReplayFile)
                            ShowTag("DRIVER CHANGE", $"#{d.CarNumber}  {NameFormat.Broadcast(d)}", $"Takes over from {h.Name} · {d.ClassName} · P{d.PositionInClass} in class", d.ClassColor, 9);
                        log($"desk: driver change #{d.CarNumber} {h.Name} -> {d.Name}");
                    }
                    h.Name = d.Name;
                }
            // championship at the flag
            if (race && s.Phase == RacePhase.PostRace && champFlagKey != s.SessionKey && Current.AutoChampionshipAtFlag && Integrations.League.Count > 0 && !ReplayState.IsReplayFile)
            {
                champFlagKey = s.SessionKey;
                var card = ChampionshipCard(s, "After this race");
                if (card != null) _ = Task.Delay(25_000).ContinueWith(_ => DeskBoard.ShowCard(card, 14));
            }
        }
    }

    // ================================================================ replay desk
    static volatile bool replaying;
    public static string ReplayStatus { get; private set; } = "";

    public static string Mark(string label)
    {
        var s = rs.Current;
        if (!s.IsConnected) return "iRacing isn't running.";
        var focus = s.CameraTarget ?? s.Player;
        HighlightLog.Add("producer", label.Length > 0 ? label : "Marked by the producer" + (focus != null ? $" (#{focus.CarNumber})" : ""), true, focus != null ? new[] { focus.CarIdx } : Array.Empty<int>());
        return "Marked.";
    }

    public static object Marks() => HighlightLog.Current.Select((m, i) => new
    {
        i, kind = m.Kind, label = m.Label, lap = m.Lap, time = m.SessionTime, session = m.SessionNum,
        clock = TimeSpan.FromSeconds(Math.Max(0, m.SessionTime)).ToString(@"h\:mm\:ss"), cars = m.Cars,
    }).Reverse().Take(60).ToList();

    /// <summary>Plays one marked moment now: from 6 s before, on a TV camera, at normal or half speed, then back to live.</summary>
    public static string PlayMoment(int index, bool slow)
    {
        var list = HighlightLog.Current;
        if (index == int.MaxValue) index = list.Count - 1;   // Stream Deck "replay": the newest moment
        if (index < 0 || index >= list.Count) return "That moment isn't in the list any more.";
        if (DrivingState.DrivingLive || (rs.ReadBool("IsOnTrack") && !ReplayState.IsReplayFile)) return "You're in the car - replays only play when you're spectating or watching a replay.";
        if (replaying) return "A replay is already playing.";
        var m = list[index];
        if (m.SessionTime <= 0) return "That moment has no replay time.";
        replaying = true;
        _ = Task.Run(() =>
        {
            try
            {
                HighlightReel.Playing = true;   // the director keeps its hands off the camera
                ReplayStatus = $"Replaying: {m.Label}";
                rs.ReplaySpeed(0, false);
                rs.ReplayToSessionTime(m.SessionNum, (int)(Math.Max(0, m.SessionTime - 6) * 1000));
                Thread.Sleep(700);
                var groups = rs.CameraGroups();
                var g = groups.FirstOrDefault(x => x.Name.Equals("TV1", StringComparison.OrdinalIgnoreCase)) ?? groups.FirstOrDefault(x => x.Name.StartsWith("TV", StringComparison.OrdinalIgnoreCase));
                var nums = rs.CarNumbersRaw();
                if (m.Cars.Length > 0 && nums.TryGetValue(m.Cars[0], out int raw) && g != null) rs.CamToCar(raw, g.Num);
                Thread.Sleep(500);
                if (slow) rs.ReplaySpeed(2, true); else rs.ReplaySpeed(1, false);
                double length = 12;   // seconds of race time
                Thread.Sleep((int)(length * 1000 * (slow ? 2 : 1)));
                rs.ReplaySpeed(1, false);
                rs.ReplayToLive();
                ReplayStatus = "Back to live.";
            }
            catch (Exception ex) { ReplayStatus = "Replay stopped: " + ex.Message; }
            finally { HighlightReel.Playing = false; replaying = false; }
        });
        return slow ? "Replaying in slow motion." : "Replaying.";
    }

    public static string BackToLive() { try { rs.ReplaySpeed(1, false); rs.ReplayToLive(); return "Back to live."; } catch (Exception ex) { return ex.Message; } }

    // ================================================================ booth (commentator page)
    public static object Booth()
    {
        var s = rs.Current;
        var sec = SectorTimer.Snapshot();
        lock (gate)
        {
            var cars = new List<object>();
            // 2026-11-06 (#867, #868): storylines carry a weight (what a viewer most wants to know first) and work the same live
            // or in a replay - everything comes from the running order, gaps, laps and stops as they play out
            var weighted = new List<(int W, string Cls, string T)>();
            void Story(int w, string c, string t) => weighted.Add((w, c, t));
            foreach (var cls in s.ClassLeaderboard)
            {
                var field = cls.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator).OrderBy(d => d.PositionInClass <= 0 ? 999 : d.PositionInClass).ToList();
                var bestInClass = field.Where(d => d.BestLapTime > 0).OrderBy(d => d.BestLapTime).FirstOrDefault();
                int maxStint = 0; Driver? maxStintCar = null;
                for (int i = 0; i < field.Count; i++)
                {
                    var d = field[i];
                    hist.TryGetValue(d.CarIdx, out var h);
                    double? trend = Trend(h);
                    var ros = RosterFor(d.Name, d.CarNumber);
                    int grid = s.GridClassPositions.TryGetValue(d.CarIdx, out var gp) ? gp : 0;
                    sec.TryGetValue(d.CarIdx, out var st);
                    int stintLaps = h != null ? Math.Max(0, d.LapsCompleted - h.StintStartLap) : 0;
                    cars.Add(new
                    {
                        idx = d.CarIdx, cls = cls.Name, color = cls.Color, pos = d.PositionInClass, overall = d.Position, num = d.CarNumber, name = Full(d),
                        team = ros?.Team ?? "", country = ros?.Country ?? "", ir = d.IRating, lic = d.LicenceString, car = d.CarName,
                        gap = d.PositionInClass == 1 ? "LEADER" : LapTimeFormat.Gap(d.GapToLeaderSeconds, d.LapDifference),
                        interval = d.PositionInClass == 1 ? "" : LapTimeFormat.Gap(d.IntervalSeconds),
                        last = d.LastLapTime > 0 ? LapTimeFormat.Lap(d.LastLapTime) : "", best = d.BestLapTime > 0 ? LapTimeFormat.Lap(d.BestLapTime) : "",
                        classBest = bestInClass?.CarIdx == d.CarIdx, laps = d.LapsCompleted, inPit = d.IsInPit,
                        stops = h?.Stops.Count ?? 0, lastStop = h?.Stops.LastOrDefault() is { } ls ? $"L{ls.Lap} · {ls.Lane:0.0}s lane · {ls.Stopped:0.0}s stopped" : "",
                        stint = stintLaps, inc = d.Incidents,
                        gained = grid > 0 ? grid - d.PositionInClass : 0, trend = trend.HasValue ? Math.Round(trend.Value, 2) : (double?)null,
                        sectorsBest = st.Best?.Select(x => double.IsNaN(x) ? "" : x.ToString("0.000", CultureInfo.InvariantCulture)).ToArray() ?? Array.Empty<string>(),
                    });
                    if (stintLaps > maxStint && !d.IsInPit) { maxStint = stintLaps; maxStintCar = d; }
                    var ahead = i > 0 ? field[i - 1] : null;
                    bool top = d.PositionInClass <= 3;
                    if (ahead != null && d.IntervalSeconds is > 0 and < 1.0)
                        Story(top ? 10 : 7, cls.Name, $"Battle for P{ahead.PositionInClass}: #{d.CarNumber} {Full(d)} is {d.IntervalSeconds:0.0}s behind #{ahead.CarNumber} {Full(ahead)}{IrNote(d, ahead)}");
                    else if (ahead != null && trend is < -0.25 && d.IntervalSeconds is > 1.0 and < 8.0)
                        Story(top ? 8 : 6, cls.Name, $"#{d.CarNumber} {Full(d)} is catching #{ahead.CarNumber} {Full(ahead)} by {-trend.Value:0.0}s a lap - on them in about {Math.Ceiling(d.IntervalSeconds!.Value / -trend.Value)} laps");
                    if (i == 1 && ahead != null && trend is double tr && Math.Abs(tr) >= 0.2 && d.IntervalSeconds is > 1.0)
                        Story(8, cls.Name, tr > 0
                            ? $"Leader #{ahead.CarNumber} {Full(ahead)} is pulling away from #{d.CarNumber} {Full(d)} by {tr:0.0}s a lap (gap {d.IntervalSeconds:0.0}s)"
                            : $"#{d.CarNumber} {Full(d)} is closing on the leader #{ahead.CarNumber} {Full(ahead)} by {-tr:0.0}s a lap (gap {d.IntervalSeconds:0.0}s)");
                    if (h != null && h.TrackGains >= 3) Story(6, cls.Name, $"#{d.CarNumber} {Full(d)} has passed {h.TrackGains} cars on track" + (grid > 0 ? $" - started P{grid}, now P{d.PositionInClass}" : ""));
                    if (grid > 0 && d.PositionInClass > 0 && d.PositionInClass - grid >= 5) Story(4, cls.Name, $"#{d.CarNumber} {Full(d)} has dropped {d.PositionInClass - grid} places - started P{grid}, now P{d.PositionInClass}");
                    if (h?.Stops.LastOrDefault() is { } stp && SessionTime() - stp.At is >= 0 and < 120 && stp.Stopped is > 1 and < 60 && stp.Lane >= stp.Stopped) Story(7, cls.Name, $"#{d.CarNumber} {Full(d)} pitted on lap {stp.Lap} - {stp.Lane:0.0}s in the lane, {stp.Stopped:0.0}s stopped");
                    if (bestInClass?.CarIdx == d.CarIdx && d.LastLapTime > 0 && Math.Abs(d.LastLapTime - d.BestLapTime) < 0.0005)
                        Story(8, cls.Name, $"#{d.CarNumber} {Full(d)} has just set the fastest lap: {LapTimeFormat.Lap(d.BestLapTime)}");
                    if (d.Incidents >= 12) Story(5, cls.Name, $"#{d.CarNumber} {Full(d)} is on {d.Incidents} incident points");
                }
                if (bestInClass != null) Story(3, cls.Name, $"Fastest lap {LapTimeFormat.Lap(bestInClass.BestLapTime)} by #{bestInClass.CarNumber} {Full(bestInClass)}");
                if (maxStintCar != null && maxStint >= 8) Story(4, cls.Name, $"Longest stint: #{maxStintCar.CarNumber} {Full(maxStintCar)} - {maxStint} laps since their last stop");
            }

            // 2026-11-06 (#869): what is on air and what is coming up, with the numbers behind each card, before it airs
            var all = s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsPaceCar && !d.IsSpectator).ToList();
            var upNext = new List<object>();
            var queued = StoryDirector.Upcoming;
            foreach (var q in queued) upNext.Add(new { card = CardName(q), next = false, notes = CardNotes(q, s, all) });
            var nr = StoryDirector.NextRoutine;
            if (nr != StoryKind.None && !queued.Contains(nr)) upNext.Add(new { card = CardName(nr), next = true, notes = CardNotes(nr, s, all) });
            var cur = StoryDirector.Current;
            var tag = DeskBoard.TagWanted ? DeskBoard.Tag : null;
            var onAir = new
            {
                card = cur == StoryKind.None ? "" : CardName(cur), notes = cur == StoryKind.None ? new List<string>() : CardNotes(cur, s, all),
                tag = tag == null ? "" : $"{tag.Eyebrow}  {tag.Line1}  {tag.Line2}", why = StoryDirector.WhyNot,
                camera = s.CameraTarget is { } cam ? $"#{cam.CarNumber} {Full(cam)} · P{cam.PositionInClass} {cam.ClassName}" : "",
            };
            // 2026-11-06 (#901): before the race, the track intro and the pre-race cards are the storylines, and they lead
            // "Coming up on screen" with what each card will show
            if (s.Phase == RacePhase.PreRace)
            {
                var pre = PreRaceCards.BoothPreview(s);
                int at = 0;
                foreach (var (card, notes) in pre)
                {
                    upNext.Insert(at++, new { card = card + " (pre-race)", next = false, notes = CardNotesList(notes) });
                    foreach (var nt in notes) Story(9, "PRE-RACE", $"{card}: {nt}");
                }
            }
            var stories = weighted.OrderByDescending(x => x.W).GroupBy(x => x.T).Select(g => g.First()).Take(20)
                .Select(x => new { w = x.W, cls = x.Cls, text = x.T }).ToList();
            return new
            {
                live = s.IsConnected, track = s.TrackDisplayName, session = s.SessionTypeName,
                lap = s.CurrentLap, total = s.TotalLaps, left = s.SessionTimeRemaining > 0 ? LapTimeFormat.SessionTimer(s.SessionTimeRemaining) : "",
                flag = s.Flags.Caution ? "CAUTION" : s.Flags.Red ? "RED" : s.Flags.White ? "WHITE" : s.Flags.Checkered ? "CHEQUERED" : s.Flags.Green ? "GREEN" : "",
                air = s.AirTempC, trackTemp = s.TrackTempC, rain = s.IsRaining, sectors = SectorTimer.Count,
                camera = s.CameraTarget?.CarIdx ?? -1, cars, stories, upNext, onAir,
            };
        }
    }

    static string Full(Driver d) => CamCarChip.FullName(d);
    static List<string> CardNotesList(List<string> n) => n;

    /// <summary>One storyline the camera can show: the car to put on screen, a key (so it isn't repeated too often),
    /// its weight and the line the booth shows for it.</summary>
    public sealed record StoryShot(int Car, string Key, double W, string Text);

    /// <summary>2026-11-06 (#875, Angelo: "make the race director actually cut to those cameras"): every storyline the booth
    /// has, as a camera shot - most important first. Battles themselves come from TopFight.</summary>
    public static List<StoryShot> StoryShots(RaceSnapshot s)
    {
        var list = new List<StoryShot>();
        try
        {
            lock (gate)
            {
                foreach (var cls in s.ClassLeaderboard)
                {
                    var field = cls.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator && d.PositionInClass > 0).OrderBy(d => d.PositionInClass).ToList();
                    var best = field.Where(d => d.BestLapTime > 0).OrderBy(d => d.BestLapTime).FirstOrDefault();
                    int maxStint = 0; Driver? stintCar = null;
                    for (int i = 0; i < field.Count; i++)
                    {
                        var d = field[i];
                        if (d.IsInPit || d.OffTrack || !d.IsConnected) continue;
                        // 2026-11-06 (#905, Angelo: "why did it cut to a Mustang in clean air in P29"): storylines only for the
                        // top 10 of a class or his car - except the fastest lap of the race
                        bool matters = d.PositionInClass is > 0 and <= 10 || d.CarIdx == s.PlayerCarIdx;
                        if (!matters && best?.CarIdx != d.CarIdx) continue;
                        hist.TryGetValue(d.CarIdx, out var h);
                        double? tr = Trend(h);
                        var ahead = i > 0 ? field[i - 1] : null;
                        bool top = d.PositionInClass <= 3;
                        // catching the car ahead
                        if (ahead != null && tr is < -0.25 && d.IntervalSeconds is > 1.0 and < 8.0)
                            list.Add(new(d.CarIdx, "catch:" + d.CarIdx, top ? 8 : 6, $"#{d.CarNumber} {Full(d)} catching #{ahead.CarNumber} by {-tr.Value:0.0}s a lap"));
                        // the leader pulling away / P2 closing on the leader
                        if (i == 1 && ahead != null && tr is double t2 && Math.Abs(t2) >= 0.2 && d.IntervalSeconds is > 1.0)
                            list.Add(t2 > 0 ? new(ahead.CarIdx, "away:" + ahead.CarIdx, 7, $"{cls.Name} leader #{ahead.CarNumber} pulling away")
                                            : new(d.CarIdx, "closing:" + d.CarIdx, 7, $"#{d.CarNumber} closing on the {cls.Name} leader"));
                        // just set the fastest lap (still on the opening part of the next lap)
                        if (best?.CarIdx == d.CarIdx && d.LastLapTime > 0 && Math.Abs(d.LastLapTime - d.BestLapTime) < 0.0005 && d.LapDistPct is >= 0 and < 0.2f)
                            list.Add(new(d.CarIdx, $"fast:{d.CarIdx}:{d.LapsCompleted}", 8, $"#{d.CarNumber} {Full(d)} just set the {cls.Name} fastest lap"));
                        // back out of the pits in the last minute
                        if (h?.Stops.LastOrDefault() is { } stp && SessionTime() - stp.At is > 5 and < 60 && stp.Stopped is > 1 and < 60 && stp.Lane >= stp.Stopped)
                            list.Add(new(d.CarIdx, $"pit:{d.CarIdx}:{stp.Lap}", 6, $"#{d.CarNumber} {Full(d)} back out after the stop"));
                        // big gainers
                        int grid = s.GridClassPositions.TryGetValue(d.CarIdx, out var gp) ? gp : 0;
                        if (h != null && h.TrackGains >= 3)
                            list.Add(new(d.CarIdx, $"gain:{d.CarIdx}:{h.TrackGains}", 5, $"#{d.CarNumber} {Full(d)} has passed {h.TrackGains} cars on track"));
                        int stint = h != null ? Math.Max(0, d.LapsCompleted - h.StintStartLap) : 0;
                        if (stint > maxStint) { maxStint = stint; stintCar = d; }
                    }
                    if (stintCar != null && maxStint >= 8) list.Add(new(stintCar.CarIdx, "stint:" + stintCar.CarIdx, 3, $"#{stintCar.CarNumber} longest stint ({maxStint} laps)"));
                }
            }
        }
        catch { }
        return list.OrderByDescending(x => x.W).ToList();
    }

    /// <summary>2026-11-06 (#872, Angelo: "why isn't the director following the storylines"): the fight the booth leads with -
    /// close, closing, and for a place that matters; a pass could come any second under 0.4 s.</summary>
    public static (int Chaser, int Ahead, double Score)? TopFight(RaceSnapshot s)
    {
        try
        {
            lock (gate)
            {
                (int, int, double)? best = null;
                foreach (var cls in s.ClassLeaderboard)
                {
                    var field = cls.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator && d.PositionInClass > 0).OrderBy(d => d.PositionInClass).ToList();
                    for (int i = 1; i < field.Count; i++)
                    {
                        var d = field[i]; var a = field[i - 1];
                        if (d.IsInPit || a.IsInPit || d.OffTrack || a.OffTrack || !d.IsConnected || !a.IsConnected) continue;
                        if (d.IntervalSeconds is not double g || g <= 0 || g > 1.2 || d.LapDifference != 0) continue;
                        hist.TryGetValue(d.CarIdx, out var h);
                        double tr = Trend(h) ?? 0;   // negative = closing
                        double score = (1.2 - g) * 10 + Math.Max(0, -tr) * 15
                                     + (d.PositionInClass <= 3 ? 6 : d.PositionInClass <= 6 ? 3 : 0)
                                     + (g < 0.4 ? 8 : 0);
                        if (best == null || score > best.Value.Item3) best = (d.CarIdx, a.CarIdx, score);
                    }
                }
                return best;
            }
        }
        catch { return null; }
    }
    static string IrNote(Driver d, Driver a) => d.IRating > 0 && a.IRating > 0 && Math.Abs(d.IRating - a.IRating) >= 1000
        ? (d.IRating > a.IRating ? $" - the chaser has {d.IRating - a.IRating} more iRating" : $" - the car ahead has {a.IRating - d.IRating} more iRating") : "";

    static string CardName(StoryKind k) => k switch
    {
        StoryKind.FastestLaps => "Fastest laps (GoFast)",
        StoryKind.PitStops => "Pit stops",
        StoryKind.RaceSoFar => "The race so far",
        StoryKind.Cleanest => "Cleanest so far",
        StoryKind.YourRace => "Your race",
        StoryKind.MoveOfRace => "Move of the race",
        StoryKind.Limits => "On the limit",
        StoryKind.CarContact => "Car contact",
        StoryKind.Halfway => "Halfway",
        StoryKind.WeatherWatch => "Weather watch",
        StoryKind.DriverOfDay => "Driver of the day",
        _ => k.ToString(),
    };

    /// <summary>The numbers a card will show, for the commentator to read before it airs.</summary>
    static List<string> CardNotes(StoryKind k, RaceSnapshot s, List<Driver> all)
    {
        var n = new List<string>();
        try
        {
            switch (k)
            {
                case StoryKind.FastestLaps:
                    foreach (var cls in s.ClassLeaderboard)
                    {
                        var top3 = cls.Drivers.Where(d => d.BestLapTime > 0 && !d.IsPaceCar).OrderBy(d => d.BestLapTime).Take(3).ToList();
                        if (top3.Count > 0) n.Add($"{cls.Name}: " + string.Join("   ", top3.Select((d, i) => $"{i + 1}. #{d.CarNumber} {Full(d)} {LapTimeFormat.Lap(d.BestLapTime)}")));
                    }
                    break;
                case StoryKind.PitStops:
                    var stops = hist.SelectMany(kv => kv.Value.Stops.Select(x => (Car: kv.Key, Stop: x))).Where(x => x.Stop.Stopped > 1 && x.Stop.Stopped < 60 && x.Stop.Lane >= x.Stop.Stopped).OrderBy(x => x.Stop.Stopped).ToList();
                    if (stops.Count == 0) n.Add("No timed stops yet");
                    foreach (var x in stops.Take(4))
                        if (all.FirstOrDefault(a => a.CarIdx == x.Car) is { } d)
                            n.Add($"#{d.CarNumber} {Full(d)} ({d.ClassName}) - {x.Stop.Stopped:0.0}s stopped, {x.Stop.Lane:0.0}s in the lane, lap {x.Stop.Lap}");
                    if (stops.Count > 0) n.Add($"{stops.Count} stops so far");
                    break;
                case StoryKind.RaceSoFar:
                    foreach (var cls in s.ClassLeaderboard)
                    {
                        var f = cls.Drivers.Where(d => d.PositionInClass > 0 && !d.IsPaceCar).OrderBy(d => d.PositionInClass).ToList();
                        if (f.Count == 0) continue;
                        var l = f[0]; var p2 = f.Count > 1 ? f[1] : null;
                        n.Add($"{cls.Name}: #{l.CarNumber} {Full(l)} leads" + (p2 != null ? $" by {LapTimeFormat.Gap(p2.GapToLeaderSeconds, p2.LapDifference)} from #{p2.CarNumber} {Full(p2)}" : ""));
                    }
                    break;
                case StoryKind.MoveOfRace:
                    foreach (var x in all.Select(d => (D: d, G: s.GridClassPositions.TryGetValue(d.CarIdx, out var g0) && g0 > 0 && d.PositionInClass > 0 ? g0 - d.PositionInClass : 0))
                                         .Where(x => x.G > 0).OrderByDescending(x => x.G).Take(3))
                        n.Add($"#{x.D.CarNumber} {Full(x.D)} ({x.D.ClassName}) up {x.G} places, now P{x.D.PositionInClass}");
                    break;
                case StoryKind.Cleanest:
                    foreach (var d in all.Where(d => d.Incidents >= 0 && d.LapsCompleted > 0).OrderBy(d => d.Incidents).ThenBy(d => d.PositionInClass).Take(4))
                        n.Add($"#{d.CarNumber} {Full(d)} ({d.ClassName}) - {d.Incidents}x");
                    break;
                case StoryKind.Limits:
                    foreach (var d in all.Where(d => d.Incidents > 0).OrderByDescending(d => d.Incidents).Take(4))
                        n.Add($"#{d.CarNumber} {Full(d)} ({d.ClassName}) - {d.Incidents}x");
                    break;
                case StoryKind.YourRace:
                    if (s.Player is { } p)
                    {
                        int g = s.GridClassPositions.TryGetValue(p.CarIdx, out var gp) ? gp : 0;
                        n.Add($"#{p.CarNumber} {Full(p)}: P{p.PositionInClass} in {p.ClassName}" + (g > 0 ? $" (started P{g})" : "")
                            + (p.BestLapTime > 0 ? $", best {LapTimeFormat.Lap(p.BestLapTime)}" : "") + (p.Incidents >= 0 ? $", {p.Incidents}x" : ""));
                    }
                    break;
                case StoryKind.CarContact: n.Add("Contact between cars - who and the incident points are on the card"); break;
                case StoryKind.Halfway: n.Add($"Halfway - lap {s.CurrentLap}"); break;
                case StoryKind.WeatherWatch: n.Add($"Air {s.AirTempC:0} C, track {s.TrackTempC:0} C" + (s.IsRaining ? ", raining" : "")); break;
            }
        }
        catch { }
        return n;
    }

    static string Short(Driver d) => NameFormat.Broadcast(d);

    /// <summary>How the gap to the car ahead changed per lap over the last 3 laps (negative = catching).</summary>
    static double? Trend(CarHist? h)
    {
        if (h == null || h.Intervals.Count < 4) return null;
        var a = h.Intervals[^4]; var b = h.Intervals[^1];
        if (b.Lap - a.Lap <= 0 || b.Lap - a.Lap > 6) return null;
        return (b.Interval - a.Interval) / (b.Lap - a.Lap);
    }

    public static object CarList()
    {
        var s = rs.Current;
        return s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsPaceCar && !d.IsSpectator).OrderBy(d => d.ClassName).ThenBy(d => d.PositionInClass)
            .Select(d => new { idx = d.CarIdx, label = $"{d.ClassName} P{d.PositionInClass} · #{d.CarNumber} {d.Name}" }).ToList();
    }

    // ================================================================ on-demand graphics
    static int nextId = 1;
    static void ShowTag(string eyebrow, string l1, string l2, string accent, double secs) => DeskBoard.ShowTag(new NameTag(nextId++, eyebrow, l1, l2, accent), secs);
    static int CardSecs => Math.Clamp(Current.CardSeconds, 5, 60);

    /// <summary>2026-11-06: what the overlay is drawing now (the sample race when iRacing is closed) - set by Program.</summary>
    public static Func<RaceSnapshot>? OnScreen;

    public static string Show(string what, int a, int b, int i)
    {
        var s = rs.Current;
        Driver? D(int idx) => s.ClassLeaderboard.SelectMany(c => c.Drivers).FirstOrDefault(d => d.CarIdx == idx);
        switch (what)
        {
            case "clear": DeskBoard.Clear(); return "Cleared.";
            case "tag":
                if (i < 0 || i >= Current.Commentators.Count) return "Pick a commentator.";
                var cm = Current.Commentators[i];
                ShowTag(cm.Role.Length > 0 ? cm.Role : "Commentator", cm.Name, "", "", 8);
                return "Name tag: " + cm.Name;
            case "focus":
            {
                var d = D(a); if (d == null) return "Pick a car.";
                var ros = RosterFor(d.Name, d.CarNumber);
                hist.TryGetValue(d.CarIdx, out var h);
                string l2 = $"{(ros?.Team is { Length: > 0 } tm ? tm + " · " : "")}{d.CarName} · P{d.PositionInClass} in {d.ClassName} · {d.IRating:N0} iR{(h != null && h.Stops.Count > 0 ? $" · {h.Stops.Count} stop{(h.Stops.Count > 1 ? "s" : "")}" : "")}";
                ShowTag($"#{d.CarNumber} · {d.ClassName}", d.Name, l2, d.ClassColor, 9);
                // put the camera on the car when spectating (never while you drive)
                if (!DrivingState.DrivingLive && rs.CarNumbersRaw().TryGetValue(d.CarIdx, out int raw))
                {
                    var g = rs.CameraGroups().FirstOrDefault(x => x.Name.StartsWith("TV", StringComparison.OrdinalIgnoreCase));
                    if (g != null) rs.CamToCar(raw, g.Num);
                }
                return "Driver focus: " + d.Name;
            }
            case "h2h":
            {
                var x = D(a); var y = D(b); if (x == null || y == null || x.CarIdx == y.CarIdx) return "Pick two different cars.";
                hist.TryGetValue(x.CarIdx, out var hx); hist.TryGetValue(y.CarIdx, out var hy);
                string Avg(CarHist? h) => h != null && h.Laps.Count >= 3 ? LapTimeFormat.Lap(h.Laps.TakeLast(5).Average()) : "-";
                var rows = new List<(string, string, string)>
                {
                    ("Position", $"P{x.PositionInClass}", $"P{y.PositionInClass}"),
                    ("Last lap", x.LastLapTime > 0 ? LapTimeFormat.Lap(x.LastLapTime) : "-", y.LastLapTime > 0 ? LapTimeFormat.Lap(y.LastLapTime) : "-"),
                    ("Best lap", x.BestLapTime > 0 ? LapTimeFormat.Lap(x.BestLapTime) : "-", y.BestLapTime > 0 ? LapTimeFormat.Lap(y.BestLapTime) : "-"),
                    ("Last 5 avg", Avg(hx), Avg(hy)),
                    ("Pit stops", (hx?.Stops.Count ?? 0).ToString(CultureInfo.InvariantCulture), (hy?.Stops.Count ?? 0).ToString(CultureInfo.InvariantCulture)),
                    ("iRating", x.IRating.ToString("N0", CultureInfo.InvariantCulture), y.IRating.ToString("N0", CultureInfo.InvariantCulture)),
                    ("Incidents", x.Incidents >= 0 ? x.Incidents + "x" : "-", y.Incidents >= 0 ? y.Incidents + "x" : "-"),
                };
                DeskBoard.ShowCard(new DeskCard(nextId++, "compare", "Head to head", $"#{x.CarNumber} vs #{y.CarNumber}", Array.Empty<DeskRow>(), Short(x), Short(y), rows.ToArray()), CardSecs);
                return "Head to head on air.";
            }
            case "gap":
            {
                var x = D(a); var y = D(b); if (x == null || y == null || x.CarIdx == y.CarIdx) return "Pick two different cars.";
                if (x.ClassName != y.ClassName) return "Pick two cars in the same class.";
                lock (gate)
                {
                    if (!hist.TryGetValue(x.CarIdx, out var hx) || !hist.TryGetValue(y.CarIdx, out var hy)) return "No lap history yet.";
                    var laps = hx.GapAtLap.Keys.Intersect(hy.GapAtLap.Keys).OrderBy(l => l).TakeLast(10).ToList();
                    if (laps.Count < 2) return "They need two laps in common first.";
                    var series = laps.Select(l => (float)Math.Abs(hx.GapAtLap[l] - hy.GapAtLap[l])).ToArray();
                    var (front, back) = x.PositionInClass < y.PositionInClass ? (x, y) : (y, x);
                    DeskBoard.ShowCard(new DeskCard(nextId++, "graph", $"The gap · {x.ClassName}", $"#{back.CarNumber} {Short(back)} to #{front.CarNumber} {Short(front)}", Array.Empty<DeskRow>(), Series: series), CardSecs);
                }
                return "Gap graph on air.";
            }
            case "pits":
            {
                List<(Driver D, PitStop P)> all;
                lock (gate) all = hist.Values.SelectMany(h => h.Stops.Select(p => (D: D(h.Idx), P: p))).Where(x => x.D != null).Select(x => (x.D!, x.P)).ToList();
                if (all.Count == 0) return "No pit stops yet.";
                var rows = all.OrderBy(x => x.P.Stopped > 0 ? x.P.Stopped : x.P.Lane).Take(8)
                    .Select((x, n) => new DeskRow((n + 1).ToString(CultureInfo.InvariantCulture), $"#{x.D.CarNumber} {Short(x.D)}", $"{x.P.Stopped:0.0}s · {x.P.Lane:0.0}s lane", 0, x.D.ClassColor)).ToArray();
                DeskBoard.ShowCard(new DeskCard(nextId++, "table", "Pit lane", "Quickest stops", rows, Foot: "Time stopped · time in the pit lane"), CardSecs);
                return "Pit table on air.";
            }
            case "sectors":
            {
                var snap = SectorTimer.Snapshot();
                int n = Math.Max(1, SectorTimer.Count);
                var me = s.CameraTarget ?? s.Player ?? s.Leader;
                string cls = me?.ClassName ?? s.ClassLeaderboard.FirstOrDefault()?.Name ?? "";
                var field = s.ClassLeaderboard.FirstOrDefault(c => c.Name == cls)?.Drivers ?? Array.Empty<Driver>();
                var rows = new List<DeskRow>();
                for (int k = 0; k < n && k < 8; k++)
                {
                    var best = field.Select(d => (d, t: snap.TryGetValue(d.CarIdx, out var v) && k < v.Best.Length ? v.Best[k] : double.NaN))
                        .Where(x => !double.IsNaN(x.t)).OrderBy(x => x.t).FirstOrDefault();
                    if (best.d != null) rows.Add(new DeskRow($"S{k + 1}", $"#{best.d.CarNumber} {Short(best.d)}", best.t.ToString("0.000", CultureInfo.InvariantCulture), 0, "#9B5CFF"));
                }
                if (rows.Count == 0) return "No sector times yet.";
                double ideal = rows.Sum(r => double.Parse(r.Value, CultureInfo.InvariantCulture));
                DeskBoard.ShowCard(new DeskCard(nextId++, "table", $"Fastest sectors · {cls}", "Purple sectors", rows.ToArray(), Foot: $"Ideal lap {LapTimeFormat.Lap(ideal)}"), CardSecs);
                return "Sector bests on air.";
            }
            case "watch":   // 2026-11-06: Drivers to watch (DriversToWatch - Garage 61 pace, grid, iRating)
            {
                // iRacing closed: the sample race's field (what the Live view is showing)
                var picks = DriversToWatch.For(s.IsConnected ? s : (OnScreen?.Invoke() ?? s));
                if (picks.Count == 0) return "No drivers to pick from yet.";
                var rows = picks.Select(p => new DeskRow($"{p.Tag} · {ClassLogo.DisplayName(p.ClassName)}", $"#{p.Number} {p.Name}", p.Value, 0, p.ClassColor, p.Reason)).ToArray();
                DeskBoard.ShowCard(new DeskCard(nextId++, "watch", "The ones to watch", "Drivers to watch", rows), CardSecs + 4);
                return "Drivers to watch on air.";
            }
            case "champ":
            {
                var card = ChampionshipCard(s, "Live - if they finished now");
                if (card == null) return "Set up League standings (Integrations) first.";
                DeskBoard.ShowCard(card, CardSecs + 3);
                return "Live championship on air.";
            }
        }
        return "Unknown graphic.";
    }

    /// <summary>League standings + the points this race would give each driver now (by class position).</summary>
    static DeskCard? ChampionshipCard(RaceSnapshot s, string foot)
    {
        var table = Current.PointsTable.Split(',', ';', ' ').Select(x => int.TryParse(x.Trim(), out int v) ? v : -1).Where(v => v >= 0).ToArray();
        var league = Integrations.League;
        if (league.Count == 0) return null;
        var drivers = s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsPaceCar && !d.IsSpectator).ToList();
        var rows = league.Select(r =>
        {
            double pts = double.TryParse(r.Points, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
            var d = drivers.FirstOrDefault(x => Integrations.LeagueRowFor(x) == r);
            double add = d != null && d.PositionInClass > 0 && d.PositionInClass <= table.Length ? table[d.PositionInClass - 1] : 0;
            return (Row: r, Now: pts + add, Add: add);
        }).OrderByDescending(x => x.Now).ToList();
        var outRows = rows.Take(10).Select((x, n) => new DeskRow((n + 1).ToString(CultureInfo.InvariantCulture), x.Row.Name,
            x.Now.ToString("0.#", CultureInfo.InvariantCulture) + (x.Add > 0 ? $" (+{x.Add:0})" : ""), x.Row.Pos - (n + 1))).ToArray();
        string name = Integrations.Current.LeagueName.Length > 0 ? Integrations.Current.LeagueName : "Championship";
        return new DeskCard(nextId++, "table", name, "Championship", outRows, Foot: foot);
    }

    // ================================================================ penalties (stewards)
    public static string Penalty(int carIdx, string kind, string text)
    {
        var s = rs.Current;
        var d = s.ClassLeaderboard.SelectMany(c => c.Drivers).FirstOrDefault(x => x.CarIdx == carIdx);
        if (d == null) return "Pick a car.";
        var cat = kind switch
        {
            "warning" => RcCategory.Warning, "investigation" => RcCategory.Investigation, "noted" => RcCategory.Noted,
            "cleared" => RcCategory.Cleared, "dq" => RcCategory.Disqualified, "limits" => RcCategory.TrackLimits, _ => RcCategory.Penalty,
        };
        text = (text ?? "").Trim();
        if (text.Length == 0) text = kind switch { "warning" => "WARNING", "investigation" => "UNDER INVESTIGATION", "noted" => "INCIDENT NOTED", "cleared" => "NO FURTHER ACTION", "dq" => "DISQUALIFIED", _ => "PENALTY" };
        if (text.Length > 70) text = text[..70];
        RaceControlBar.Post(new RcMessage(cat, "L" + Math.Max(1, d.CurrentLap).ToString(CultureInfo.InvariantCulture), "", d.CarNumber, NameFormat.Broadcast(d), d.ClassName, d.ClassColor, text.ToUpperInvariant(), d.Incidents));
        log($"desk: steward {kind} #{d.CarNumber}: {text}");
        return $"Race control: #{d.CarNumber} {text}";
    }

    // ================================================================ league roster
    public sealed record RosterRow(string IRacingName, string Display, string Team, string Country, string Number);
    static volatile RosterRow[] roster = Array.Empty<RosterRow>();
    public static string RosterStatus { get; private set; } = "Not set up";
    public static int RosterCount => roster.Length;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static RosterRow? RosterFor(string iracingName, string number)
    {
        var r = roster;
        if (r.Length == 0) return null;
        return r.FirstOrDefault(x => x.IRacingName.Length > 0 && x.IRacingName.Equals(iracingName, StringComparison.OrdinalIgnoreCase))
            ?? r.FirstOrDefault(x => x.Display.Equals(iracingName, StringComparison.OrdinalIgnoreCase))
            ?? (number.Length > 0 ? r.FirstOrDefault(x => x.Number.Length > 0 && x.Number == number.TrimStart('#') && x.IRacingName.Length == 0) : null);
    }

    public static async Task ReloadRoster()
    {
        string src = Current.RosterSource.Trim();
        if (src.Length == 0) { roster = Array.Empty<RosterRow>(); RosterStatus = "Not set up"; return; }
        try
        {
            string text = src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await http.GetStringAsync(SheetCsv(src)) : await File.ReadAllTextAsync(src);
            var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(SplitCsv).ToList();
            if (lines.Count < 2) { RosterStatus = "The roster is empty"; return; }
            var h = lines[0].Select(c => c.Trim().ToLowerInvariant()).ToList();
            int Col(params string[] k) => h.FindIndex(c => k.Any(x => c.Contains(x)));
            int cIr = Col("iracing"), cName = Col("display", "name", "driver"), cTeam = Col("team"), cCountry = Col("country", "nation", "flag"), cNum = Col("#", "number", "car");
            if (cIr == cName) cName = h.FindIndex(c => (c.Contains("display") || c == "name" || c.Contains("driver")) && !c.Contains("iracing"));
            string Get(List<string> l, int i) => i >= 0 && i < l.Count ? l[i].Trim() : "";
            roster = lines.Skip(1).Select(l => new RosterRow(Get(l, cIr), Get(l, cName), Get(l, cTeam), Get(l, cCountry), Get(l, cNum).TrimStart('#')))
                .Where(r => r.Display.Length > 0 || r.IRacingName.Length > 0).ToArray();
            RosterStatus = $"{roster.Length} drivers ({DateTime.Now:HH:mm}). Names change on the graphics when iRacing next updates its driver list (a driver joins / leaves, or the next session)";
            log("desk: roster " + roster.Length + " drivers");
        }
        catch (Exception ex) { RosterStatus = "Couldn't read it: " + ex.Message; }
    }

    static string SheetCsv(string url)
    {
        if (!url.Contains("docs.google.com/spreadsheets", StringComparison.OrdinalIgnoreCase) || url.Contains("output=csv") || url.Contains("format=csv")) return url;
        int d = url.IndexOf("/d/", StringComparison.Ordinal);
        if (d < 0) return url;
        string id = url[(d + 3)..].Split('/', '?', '#')[0];
        int g = url.IndexOf("gid=", StringComparison.Ordinal);
        string gid = g > 0 ? new string(url[(g + 4)..].TakeWhile(char.IsDigit).ToArray()) : "0";
        return $"https://docs.google.com/spreadsheets/d/{id}/export?format=csv&gid={gid}";
    }

    static List<string> SplitCsv(string line)
    {
        var res = new List<string>(); var sb = new System.Text.StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') q = false; else sb.Append(ch); }
            else if (ch == '"') q = true; else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); } else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    public static object Describe() => new
    {
        settings = Current, marks = Marks(), cars = CarList(), replay = ReplayStatus, roster = RosterStatus, rosterCount = RosterCount,
        league = Integrations.League.Count, lanUrls = LanUrls(),
    };

    public static string[] LanUrls()
    {
        try
        {
            return System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a))
                .Select(a => $"http://{a}:{ControlServer.Port}/?pin={Current.LanPin}").ToArray();
        }
        catch { return Array.Empty<string>(); }
    }
}
