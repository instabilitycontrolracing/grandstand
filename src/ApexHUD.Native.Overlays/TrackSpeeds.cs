using System.IO;
using System.Text.Json;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// How fast the field goes at every named corner and straight (2026-10-25, Angelo: the track card's lap walk should
/// "show minimum speed or top speed of each corner"). Every car is timed through every stop of the lap
/// (TrackCorners.Lap) from its lap-distance movement against the sim's own clock, so it works live and in replays at
/// any replay speed. Per class, per stop we keep the slowest point of each pass through a corner and the fastest point
/// of each run down a straight; the figure shown is a strong-but-typical one (the 80th percentile), so one wild pass
/// doesn't set it. Saved per track (Documents\iRacing\Grandstand\trackspeeds), so the intro has numbers from the
/// sessions watched or driven before.
/// </summary>
public static class TrackSpeeds
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "trackspeeds");
    static string track = "";
    static List<(string Name, float StartPct, float EndPct, bool Straight)>? stops;
    static Dictionary<string, Dictionary<string, List<float>>> data = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<int, Car> cars = new();
    static bool dirty;
    static DateTime savedAt = DateTime.UtcNow;

    sealed class Car { public double T = -1; public float Pct = -1, Kmh; public int Stop = -1; public float Lo = 9999, Hi; public bool Clean; }

    public static void Tick(RaceSnapshot snap)
    {
        if (!snap.IsConnected || string.IsNullOrWhiteSpace(snap.TrackRawName)) return;
        string key = snap.TrackRawName + "|" + snap.Extras.TrackLength;
        if (key != track) { Save(); Load(snap); track = key; cars.Clear(); }
        if (stops is not { Count: > 0 }) return;
        double len = TrackCorners.LengthM(snap.Extras.TrackLength);
        double t = snap.SessionTimeOfDay;
        if (len <= 0 || t <= 0) return;
        foreach (var cl in snap.ClassLeaderboard)
            foreach (var d in cl.Drivers)
            {
                if (d.IsPaceCar || d.LapDistPct < 0) continue;
                if (!cars.TryGetValue(d.CarIdx, out var c)) cars[d.CarIdx] = c = new Car();
                if (c.T < 0 || t < c.T || t - c.T > 1.5) { c.T = t; c.Pct = d.LapDistPct; c.Stop = -1; continue; }
                if (t - c.T < 0.2) continue;
                float dp = d.LapDistPct - c.Pct; if (dp < -0.5f) dp += 1; if (dp > 0.5f) dp -= 1;
                float kmh = (float)(dp * len / (t - c.T) * 3.6);
                c.T = t; c.Pct = d.LapDistPct;
                if (kmh < 0 || kmh > 450) { c.Stop = -1; continue; }
                c.Kmh = c.Kmh <= 0 ? kmh : c.Kmh * 0.4f + kmh * 0.6f;
                int s = StopAt(d.LapDistPct);
                if (s != c.Stop)
                {
                    // finished a stop cleanly (came in from the one before, never in the pits): keep its figure
                    if (c.Stop >= 0 && c.Clean && s == (c.Stop + 1) % stops.Count)
                    {
                        var st = stops[c.Stop];
                        float v = st.Straight ? c.Hi : c.Lo;
                        if (v > 25 && v < 450) Add(cl.Name, st.Name, v);
                    }
                    c.Clean = c.Stop >= 0 && s == (c.Stop + 1) % stops.Count && !d.IsInPit;
                    c.Stop = s; c.Lo = 9999; c.Hi = 0;
                }
                if (d.IsInPit || d.OffTrack) c.Clean = false;
                c.Lo = Math.Min(c.Lo, c.Kmh); c.Hi = Math.Max(c.Hi, c.Kmh);
            }
        if (dirty && (DateTime.UtcNow - savedAt).TotalSeconds > 60) Save();
    }

    static int StopAt(float pct)
    {
        for (int i = 0; i < stops!.Count; i++)
        {
            var (_, a, b, _) = stops[i];
            if (a <= b ? pct >= a && pct < b : pct >= a || pct < b) return i;
        }
        return -1;
    }

    static void Add(string cls, string stop, float v)
    {
        if (!data.TryGetValue(cls, out var m)) data[cls] = m = new(StringComparer.OrdinalIgnoreCase);
        if (!m.TryGetValue(stop, out var l)) m[stop] = l = new List<float>();
        l.Add(MathF.Round(v, 1));
        if (l.Count > 300) l.RemoveRange(0, l.Count - 300);
        dirty = true;
    }

    /// <summary>The typical minimum (corner) or top (straight) speed in km/h for this class, or null without data yet.</summary>
    public static float? Of(string? cls, string stopName, bool straight)
    {
        if (cls == null || !data.TryGetValue(cls, out var m) || !m.TryGetValue(stopName, out var l) || l.Count < 3) return null;
        var s = l.OrderBy(x => x).ToList();
        return s[(int)Math.Clamp(Math.Round((s.Count - 1) * 0.8), 0, s.Count - 1)];
    }

    static string FileFor(RaceSnapshot snap) => Path.Combine(Dir, string.Concat((snap.TrackRawName ?? "track").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".json");
    static string fileNow = "";

    static void Load(RaceSnapshot snap)
    {
        stops = TrackCorners.Lap(snap.TrackRawName, snap.Extras.TrackLength);
        data = new(StringComparer.OrdinalIgnoreCase);
        fileNow = FileFor(snap);
        try
        {
            if (File.Exists(fileNow))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<float>>>>(File.ReadAllText(fileNow));
                if (d != null) foreach (var (k, v) in d) data[k] = new Dictionary<string, List<float>>(v, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        dirty = false;
    }

    static void Save()
    {
        savedAt = DateTime.UtcNow;
        if (!dirty || fileNow.Length == 0) return;
        try { Directory.CreateDirectory(Dir); File.WriteAllText(fileNow, JsonSerializer.Serialize(data)); dirty = false; } catch { }
    }
}
