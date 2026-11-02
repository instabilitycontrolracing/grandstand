using System.Linq;
using System.Text.Json;
using System.IO;
using System.Threading;
namespace ApexHUD.Native.Common;

/// <summary>
/// Live sector times for every car (2026-10-24, Angelo: "sectors like F1 qualifying on the tower").
/// Fed once per data tick by RaceStateService with each car's lap position and the session clock;
/// sector lines come from iRacing's SplitTimeInfo (thirds if the track has none). Colours follow F1:
/// purple = fastest of the class, green = the driver's own best, yellow = slower.
/// A car's boxes show the lap in progress; when it finishes a lap they stay until its next sector 1.
/// </summary>
public static class SectorTimer
{
    public enum Mark : byte { None = 0, Slower = 1, PersonalBest = 2, ClassBest = 3 }

    sealed class Car
    {
        public int Id = -1;
        public float LastPct = -1; public double LastTime = -1;
        public int Sector = -1; public double SectorStart = -1;
        public readonly double[] Cur = { double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN };
        public readonly Mark[] Marks = new Mark[8];
        public readonly Mark[] Prev = new Mark[8];   // last lap's marks (shown faded for the sectors still to come)
        public readonly double[] Best = { double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue };
        public string Class = "";
    }

    static readonly object gate = new();
    static float[] starts = { 0f, 1f / 3f, 2f / 3f };
    static readonly Dictionary<int, Car> cars = new();
    static readonly Dictionary<string, double[]> classBest = new();

    public static int Count { get { lock (gate) return starts.Length; } }
    /// <summary>Copy of the sector start positions (0..1).</summary>
    public static float[] Starts() { lock (gate) return (float[])starts.Clone(); }

    /// <summary>Sector start positions (0..1), sorted, first = 0. Resets the timing when they change.</summary>
    public static void SetSectors(IReadOnlyList<float> s)
    {
        if (s == null || s.Count < 2 || s.Count > 8) return;
        var arr = s.OrderBy(x => x).ToArray();
        arr[0] = 0f;
        lock (gate)
        {
            if (arr.SequenceEqual(starts)) return;
            starts = arr; cars.Clear(); classBest.Clear();
        }
    }

    /// <summary>New session or a replay jump: forget everything.</summary>
    public static void Reset() { lock (gate) { cars.Clear(); classBest.Clear(); } }

    // ---- kept on disk (2026-10-24, Angelo: a Grandstand restart lost the purple/green sectors) ----
    static string key = "";
    static long lastSaveTicks;
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "sectors.json");

    /// <summary>Which session the timing belongs to ("subsession:sessionNum"). A new key starts fresh,
    /// unless the saved file is for that same session - then the bests come back after a restart.</summary>
    public static void UseSession(string k)
    {
        lock (gate)
        {
            if (k == key) return;
            key = k; cars.Clear(); classBest.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                var r = doc.RootElement;
                if (r.GetProperty("key").GetString() != k) return;
                foreach (var cp in r.GetProperty("cars").EnumerateObject())
                {
                    var c = new Car { Id = int.TryParse(cp.Name, out var cid) ? cid : -1, Class = cp.Value.GetProperty("cls").GetString() ?? "" };
                    var b = cp.Value.GetProperty("best"); var m = cp.Value.GetProperty("marks"); var cu = cp.Value.GetProperty("cur");
                    for (int i = 0; i < 8 && i < b.GetArrayLength(); i++)
                    {
                        double bv = b[i].GetDouble(); c.Best[i] = bv < 0 ? double.MaxValue : bv;
                        double cv = cu[i].GetDouble(); c.Cur[i] = cv < 0 ? double.NaN : cv;
                        c.Marks[i] = (Mark)m[i].GetInt32();
                    }
                    cars[int.Parse(cp.Name)] = c;
                }
                foreach (var cb in r.GetProperty("classBest").EnumerateObject())
                {
                    var arr = Enumerable.Repeat(double.MaxValue, 8).ToArray();
                    int i = 0;
                    foreach (var v in cb.Value.EnumerateArray()) { double x = v.GetDouble(); if (i < 8) arr[i++] = x < 0 ? double.MaxValue : x; }
                    classBest[cb.Name] = arr;
                }
            }
            catch { cars.Clear(); classBest.Clear(); }
        }
    }

    /// <summary>Save at most every 5 s (cheap, off nothing heavy: a few dozen numbers).</summary>
    public static void MaybeSave()
    {
        long now = Environment.TickCount64;
        if (now - lastSaveTicks < 5000) return;
        lastSaveTicks = now;
        string json;
        lock (gate)
        {
            if (key.Length == 0 || cars.Count == 0) return;
            static double W(double v) => double.IsNaN(v) || v >= double.MaxValue / 2 ? -1 : Math.Round(v, 4);
            var o = new Dictionary<string, object>
            {
                ["key"] = key,
                ["cars"] = cars.ToDictionary(kv => kv.Key.ToString(), kv => (object)new Dictionary<string, object>
                {
                    ["cls"] = kv.Value.Class,
                    ["best"] = kv.Value.Best.Select(W).ToArray(),
                    ["cur"] = kv.Value.Cur.Select(W).ToArray(),
                    ["marks"] = kv.Value.Marks.Select(x => (int)x).ToArray(),
                }),
                ["classBest"] = classBest.ToDictionary(kv => kv.Key, kv => (object)kv.Value.Select(W).ToArray()),
            };
            json = JsonSerializer.Serialize(o);
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath + ".tmp", json); File.Move(FilePath + ".tmp", FilePath, true); }
            catch { }
        });
    }

    // 2026-10-27 (visual pack W13): every class-best ("purple") sector bumps this, with the car that set it
    static int classBestEvents, lastClassBestCar = -1;
    /// <summary>Counts purple sectors set so far (any car); compare with the last value you saw.</summary>
    public static int ClassBestEvents => Volatile.Read(ref classBestEvents);
    /// <summary>CarIdx of the latest purple sector.</summary>
    public static int LastClassBestCar => Volatile.Read(ref lastClassBestCar);

    public static void Update(int carIdx, string className, float pct, double sessionTime, bool inPit)
    {
        lock (gate)
        {
            if (!cars.TryGetValue(carIdx, out var c)) cars[carIdx] = c = new Car { Id = carIdx };
            c.Class = className;
            if (pct < 0 || inPit) { c.LastPct = -1; c.Sector = -1; return; }   // off the timing line: start clean next time
            int sec = SectorOf(pct);
            if (c.LastPct < 0 || c.LastTime < 0 || sessionTime < c.LastTime)
            { c.LastPct = pct; c.LastTime = sessionTime; c.Sector = sec; c.SectorStart = -1; return; }

            float d = pct - c.LastPct;
            bool wrapped = d < -0.5f;
            if (!wrapped && (d < -0.02f || d > 0.25f))                          // teleport / tow: restart timing
            { c.LastPct = pct; c.LastTime = sessionTime; c.Sector = sec; c.SectorStart = -1; return; }

            if (sec != c.Sector)
            {
                // crossed the line into 'sec': the previous sector is complete (if we saw it start)
                int done = c.Sector;
                if (c.SectorStart >= 0 && done >= 0)
                {
                    double t = sessionTime - c.SectorStart;
                    if (t > 1 && t < 600) Record(c, done, t);
                }
                c.Sector = sec; c.SectorStart = sessionTime;
            }
            c.LastPct = pct; c.LastTime = sessionTime;
        }
    }

    static void Record(Car c, int s, double t)
    {
        c.Cur[s] = t;
        if (s == 0) for (int i = 1; i < starts.Length; i++) { c.Prev[i] = c.Marks[i]; c.Cur[i] = double.NaN; c.Marks[i] = Mark.None; }
        if (s == 0) c.Prev[0] = c.Marks[0];
        bool pb = t < c.Best[s];
        if (pb) c.Best[s] = t;
        if (!classBest.TryGetValue(c.Class, out var cb))
            classBest[c.Class] = cb = Enumerable.Repeat(double.MaxValue, 8).ToArray();
        bool overall = t <= cb[s];
        if (overall)
        {
            cb[s] = t;
            // someone else's purple in this sector drops to green
            foreach (var o in cars.Values)
                if (!ReferenceEquals(o, c) && o.Class == c.Class && o.Marks[s] == Mark.ClassBest) o.Marks[s] = Mark.PersonalBest;
        }
        c.Marks[s] = overall ? Mark.ClassBest : pb ? Mark.PersonalBest : Mark.Slower;
        if (overall) { lastClassBestCar = c.Id; Interlocked.Increment(ref classBestEvents); }
    }

    static int SectorOf(float pct)
    {
        var s = starts;
        for (int i = s.Length - 1; i >= 0; i--) if (pct >= s[i]) return i;
        return 0;
    }

    /// <summary>Last lap's marks for a car (length = sector count), or null if unknown.</summary>
    public static Mark[]? PrevMarksFor(int carIdx)
    {
        lock (gate)
        {
            if (!cars.TryGetValue(carIdx, out var c)) return null;
            var m = new Mark[starts.Length];
            Array.Copy(c.Prev, m, starts.Length);
            return m;
        }
    }

    /// <summary>The marks for a car's current lap (length = sector count), or null if unknown.</summary>
    public static Mark[]? MarksFor(int carIdx)
    {
        lock (gate)
        {
            if (!cars.TryGetValue(carIdx, out var c)) return null;
            var m = new Mark[starts.Length];
            Array.Copy(c.Marks, m, starts.Length);
            return m;
        }
    }
}
