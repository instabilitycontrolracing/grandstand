using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-25 (Angelo: "it did not acknowledge that I pitted ... if there is a way for it to know that data even
/// with a restart ... for every piece of data we should keep"). iRacing only sends what is true right now - nobody's
/// pit-stop count, lap chart or lead changes - so the diary writes itself to disk every few seconds
/// (Documents\iRacing\Grandstand\race_diary.json) and, when Grandstand comes back up in the same race, reads it
/// back. A replay that was rewound is handled too: anything recorded for laps the cars haven't reached yet is dropped.
/// </summary>
public static partial class RaceDiary
{
    static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "race_diary.json");
    static readonly JsonSerializerOptions Opts = new()
    {
        IncludeFields = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
    static string storeKey = "";
    static DateTime lastSave = DateTime.MinValue;
    static bool trimPending;
    static readonly HashSet<string> once = new(StringComparer.Ordinal);

    /// <summary>True the first time it's asked for this race (kept across restarts) - e.g. the halfway card.</summary>
    public static bool Once(string name) => once.Add(name);

    sealed class Saved
    {
        public string Key = "";
        public bool Started, SawStart, Halfway, WasRaining;
        public int LastPlayerLaps = -1, ShownWet;
        public float StartTrackC = float.NaN, ShownTrackC = float.NaN, StartAirC = float.NaN;
        public double ClassPaceEarly;
        public Dictionary<int, int> Grid = new(), Pits = new(), CarLaps = new(), Incidents = new(), Offs = new();
        public Dictionary<int, List<int>> PitLaps = new();
        public Dictionary<int, bool> InPit = new();
        public Dictionary<int, DateTime> PitEntry = new();
        public List<PitStop> Stops = new();
        public Dictionary<string, int> LeaderOf = new(), LeadChanges = new();
        public Dictionary<string, (float Time, int CarIdx, string Name, int Lap)> Fastest = new();
        public List<(int Lap, int Pos)> PlayerHistory = new();
        public Dictionary<int, List<(int Lap, int Pos)>> CarHistory = new();
        public Dictionary<int, Dictionary<int, long>> Crossed = new();
        public List<Contact> Contacts = new();
        public List<(int CarIdx, float Pct)> OffSpots = new();
        public List<string> Once = new();
        public Dictionary<string, Dictionary<int, List<(float X, int Pos)>>> Live = new();
    }

    /// <summary>Called on every new session: start fresh, then pick the diary back up if the file is for this race.</summary>
    static void Restore(string key)
    {
        storeKey = key; once.Clear();
        if (key.Length == 0) return;
        try
        {
            if (!File.Exists(StorePath)) return;
            var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(StorePath), Opts);
            if (s == null || s.Key != key) return;
            started = s.Started; sawStart = s.SawStart; HalfwayReached = s.Halfway; wasRaining = s.WasRaining;
            lastPlayerLaps = s.LastPlayerLaps; shownWet = s.ShownWet;
            startTrackC = s.StartTrackC; shownTrackC = s.ShownTrackC; startAirC = s.StartAirC; classPaceEarly = s.ClassPaceEarly;
            Fill(grid, s.Grid); Fill(pits, s.Pits); Fill(carLaps, s.CarLaps); Fill(incidents, s.Incidents); Fill(offs, s.Offs);
            Fill(pitLaps, s.PitLaps); Fill(inPit, s.InPit); Fill(pitEntry, s.PitEntry);
            Fill(leaderOf, s.LeaderOf); Fill(leadChanges, s.LeadChanges); Fill(fastest, s.Fastest);
            Fill(carHistory, s.CarHistory); Fill(crossed, s.Crossed);
            stops.AddRange(s.Stops); playerHistory.AddRange(s.PlayerHistory); contacts.AddRange(s.Contacts); offSpots.AddRange(s.OffSpots);
            foreach (var o in s.Once) once.Add(o);
            foreach (var kv in s.Live) live[kv.Key] = kv.Value;
            trimPending = true;
            OverlayLog.Write($"[diary] picked up the race diary for {key}: {pits.Values.Sum()} pit stops, {stops.Count} timed, {carHistory.Count} cars charted");
        }
        catch (Exception ex) { OverlayLog.Write("[diary] couldn't read the saved diary: " + ex.Message); }
    }

    static void Fill<TK, TV>(Dictionary<TK, TV> into, Dictionary<TK, TV> from) where TK : notnull
    { foreach (var kv in from) into[kv.Key] = kv.Value; }

    /// <summary>A rewound replay: forget what was written down for laps the cars haven't got to yet.</summary>
    static void TrimToNow(RaceSnapshot snap)
    {
        trimPending = false;
        bool any = false;
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var d in cls.Drivers)
            {
                int lc = d.LapsCompleted; any = true;
                if (pitLaps.TryGetValue(d.CarIdx, out var pl))
                {
                    pl.RemoveAll(l => l > lc + 1);
                    pits[d.CarIdx] = pl.Count;
                }
                stops.RemoveAll(x => x.CarIdx == d.CarIdx && x.Lap > lc);
                if (carHistory.TryGetValue(d.CarIdx, out var h)) h.RemoveAll(x => x.Lap > lc);
                if (crossed.TryGetValue(d.CarIdx, out var cr)) foreach (var l in cr.Keys.Where(l => l > lc).ToList()) cr.Remove(l);
                if (carLaps.TryGetValue(d.CarIdx, out var cl) && cl > lc) carLaps[d.CarIdx] = lc;
                contacts.RemoveAll(x => x.CarIdx == d.CarIdx && x.Lap > lc + 1);
            }
        if (snap.Player is { } p)
        {
            playerHistory.RemoveAll(x => x.Lap > p.LapsCompleted);
            if (lastPlayerLaps > p.LapsCompleted) lastPlayerLaps = p.LapsCompleted;
        }
        if (!any) trimPending = true;   // no cars yet - try again next frame
        else TrimLive(snap);
    }

    /// <summary>Every 5 s during the race: write the diary down (the file write happens off the render thread).</summary>
    static void MaybeSave(DateTime now)
    {
        if (storeKey.Length == 0 || (now - lastSave).TotalSeconds < 5) return;
        lastSave = now;
        try
        {
            var s = new Saved
            {
                Key = storeKey, Started = started, SawStart = sawStart, Halfway = HalfwayReached, WasRaining = wasRaining,
                LastPlayerLaps = lastPlayerLaps, ShownWet = shownWet, StartTrackC = startTrackC, ShownTrackC = shownTrackC,
                StartAirC = startAirC, ClassPaceEarly = classPaceEarly,
                Grid = new(grid), Pits = new(pits), CarLaps = new(carLaps), Incidents = new(incidents), Offs = new(offs),
                PitLaps = pitLaps.ToDictionary(k => k.Key, v => new List<int>(v.Value)),
                InPit = new(inPit), PitEntry = new(pitEntry), Stops = new(stops),
                LeaderOf = new(leaderOf), LeadChanges = new(leadChanges), Fastest = new(fastest),
                PlayerHistory = new(playerHistory),
                CarHistory = carHistory.ToDictionary(k => k.Key, v => new List<(int, int)>(v.Value)),
                Crossed = crossed.ToDictionary(k => k.Key, v => new Dictionary<int, long>(v.Value)),
                Contacts = new(contacts), OffSpots = new(offSpots), Once = once.ToList(),
                Live = live.ToDictionary(k => k.Key, v => v.Value.ToDictionary(c => c.Key, c => new List<(float, int)>(c.Value)), StringComparer.OrdinalIgnoreCase),
            };
            string json = JsonSerializer.Serialize(s, Opts);
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    string tmp = StorePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, StorePath, true);
                }
                catch { /* best effort - next save tries again */ }
            });
        }
        catch (Exception ex) { OverlayLog.Write("[diary] save failed: " + ex.Message); }
    }
}
