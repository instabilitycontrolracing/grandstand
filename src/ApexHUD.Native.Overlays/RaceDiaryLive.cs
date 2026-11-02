using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The live lap chart (2026-10-25, Angelo: "they only show the start finish line position for each lap - I want it to
/// show every change"). Four times a second each class is put in order by how far round the race every car is, and
/// any change of place is written down with where in the race it happened (in laps, by the class leader's distance).
/// A change only counts once it has held for half a second, so cars side by side don't flicker. Race So Far draws
/// from this; Halfway keeps the simple lap-by-lap chart.
/// </summary>
public static partial class RaceDiary
{
    /// <summary>class -> car -> (race distance in laps where this place began, place)</summary>
    static readonly Dictionary<string, Dictionary<int, List<(float X, int Pos)>>> live = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, (int Pos, int Seen)> pendingPos = new();
    static readonly Dictionary<string, float> liveX = new(StringComparer.OrdinalIgnoreCase);
    static DateTime lastLive = DateTime.MinValue;
    static readonly List<(double P, Driver D)> liveSort = new();

    /// <summary>Every change of place in this class, per car; and where the race is now (laps).</summary>
    public static (Dictionary<int, List<(float X, int Pos)>> Chart, float Now)? LiveChart(string cls)
        => live.TryGetValue(cls, out var ch) && ch.Count > 0 ? (ch, liveX.TryGetValue(cls, out var x) ? x : 0f) : null;

    static void TickLive(RaceSnapshot snap, DateTime now)
    {
        if ((now - lastLive).TotalMilliseconds < 250) return;
        lastLive = now;
        foreach (var cls in snap.ClassLeaderboard)
        {
            liveSort.Clear();
            foreach (var d in cls.Drivers)
            {
                if (d.LapDistPct < 0 || d.CurrentLap <= 0) continue;            // not in the world
                liveSort.Add((d.CurrentLap - 1 + Math.Clamp(d.LapDistPct, 0f, 0.9999f), d));
            }
            if (liveSort.Count == 0) continue;
            liveSort.Sort((a, b) => b.P.CompareTo(a.P));
            float x = (float)liveSort[0].P;
            liveX[cls.Name] = x;
            if (!live.TryGetValue(cls.Name, out var ch)) live[cls.Name] = ch = new();
            for (int i = 0; i < liveSort.Count; i++)
            {
                var d = liveSort[i].D; int pos = i + 1;
                if (!ch.TryGetValue(d.CarIdx, out var list)) { ch[d.CarIdx] = list = new() { (x, pos) }; continue; }
                if (list[^1].Pos == pos) { pendingPos.Remove(d.CarIdx); continue; }
                // a new place: count it after it has held for two samples (~half a second)
                if (pendingPos.TryGetValue(d.CarIdx, out var pp) && pp.Pos == pos)
                {
                    if (pp.Seen + 1 >= 2) { list.Add((x, pos)); pendingPos.Remove(d.CarIdx); }
                    else pendingPos[d.CarIdx] = (pos, pp.Seen + 1);
                }
                else pendingPos[d.CarIdx] = (pos, 1);
            }
        }
    }

    static void ResetLive() { live.Clear(); pendingPos.Clear(); liveX.Clear(); lastLive = DateTime.MinValue; }

    /// <summary>A rewound replay: drop the changes recorded past where the race is now.</summary>
    static void TrimLive(RaceSnapshot snap)
    {
        foreach (var cls in snap.ClassLeaderboard)
        {
            float lead = 0;
            foreach (var d in cls.Drivers) if (d.LapDistPct >= 0 && d.CurrentLap > 0) lead = Math.Max(lead, d.CurrentLap - 1 + d.LapDistPct);
            if (lead <= 0 || !live.TryGetValue(cls.Name, out var ch)) continue;
            foreach (var list in ch.Values) { list.RemoveAll(p => p.X > lead + 0.01f); }
            foreach (var k in ch.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) ch.Remove(k);
        }
    }
}
