// The made-up race from tools/storycard_preview (copied: that tool keeps it in its top-level Program.cs), so the story
// cards have a real RaceDiary behind them. Drivers are invented placeholders.
using System.Reflection;
using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;

/// <summary>A made-up race run through the real RaceDiary: a 3-class field at Le Mans, grid shuffles, one pit stop per
/// car, trips off the track (most at one corner), a contact between you and the car ahead, the track heating up.</summary>
static class RaceSim
{
    static readonly (string Cls, string Color, (string Name, string Abbrev, string Num, string Brand, string Car)[] Cars, float Lap)[] Field =
    {
        ("GTP", "#E8C26A", new[]
        {
            ("Marco Albera", "M. Albera", "5", "Acura", "Acura ARX-06 GTP"), ("Tobias Blom", "T. Blom", "01", "Cadillac", "Cadillac V-Series.R GTP"),
            ("Nate Tanner", "N. Tanner", "6", "Porsche", "Porsche 963 GTP"), ("Rory Tayler", "R. Tayler", "10", "Acura", "Acura ARX-06 GTP"),
            ("Angelo Morell", "A. Morell", "29", "BMW", "BMW M Hybrid V8"), ("Mattis Jansen", "M. Jansen", "7", "Porsche", "Porsche 963 GTP"),
            ("Paulo Deran", "P. Deran", "31", "Cadillac", "Cadillac V-Series.R GTP"), ("Lukas Brandt", "L. Brandt", "24", "BMW", "BMW M Hybrid V8"),
        }, 212f),
        ("LMP2", "#3B82F6", new[]
        {
            ("Bert Keaton", "B. Keaton", "2", "Oreca", "Dallara P217 LMP2"), ("Pierre Chalon", "P. Chalon", "04", "Oreca", "Dallara P217 LMP2"),
            ("Stefan Thorne", "S. Thorne", "11", "Oreca", "Dallara P217 LMP2"), ("Glen Kurtzman", "G. Kurtzman", "44", "Oreca", "Dallara P217 LMP2"),
            ("Niko Pinter", "N. Pinter", "8", "Oreca", "Dallara P217 LMP2"),
        }, 222f),
        ("GT3", "#34D399", new[]
        {
            ("Andres Garza", "A. Garza", "3", "Chevrolet", "Chevrolet Corvette Z06 GT3.R"), ("Jordy Tallis", "J. Tallis", "81", "BMW", "BMW M4 GT3"),
            ("Lars Heine", "L. Heine", "9", "Porsche", "Porsche 911 GT3 R (992)"), ("Dario Serrano", "D. Serrano", "62", "Ferrari", "Ferrari 296 GT3"),
            ("Kai Bachmann", "K. Bachmann", "77", "Porsche", "Porsche 911 GT3 R (992)"), ("Alessio Pieri", "A. Pieri", "82", "Ferrari", "Ferrari 296 GT3"),
            ("Owen Marsh", "O. Marsh", "14", "Mercedes", "Mercedes-AMG GT3"),
        }, 238f),
    };
    const int PlayerIdx = 4;          // car index of A. Morell (GTP, starts P5)
    static int contactOther = -1;

    public static RaceSnapshot Run()
    {
        var rnd = new Random(349);
        var cars = new List<(int Idx, int Cls, int Row, float Pace, double Dist, int PitLap, double PitLeft, bool Pitted, int Inc, double Best, double LapStart, double Last, int OffLeft)>();
        int idx = 0;
        for (int ci = 0; ci < Field.Length; ci++)
            for (int k = 0; k < Field[ci].Cars.Length; k++, idx++)
            {
                // pace: the player and a couple of others are quicker than where they start
                float pace = 1f - (idx == PlayerIdx ? 0.012f : k == 3 || k == 5 ? 0.008f : (float)(rnd.NextDouble() * 0.01 - 0.003)) + k * 0.0012f;
                cars.Add((idx, ci, k, pace, -k * 0.004 - ci * 0.03, 5 + rnd.Next(4), 0, false, 0, 0, 0, 0, 0));
            }
        var grid = new Dictionary<int, int>();
        foreach (var cr in cars) grid[cr.Idx] = cr.Row + 1;
        var t0 = new DateTime(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc);
        RaceSnapshot snap = RaceSnapshot.Disconnected;
        const double Dt = 0.5;
        bool contactDone = false;
        for (int step = 0; step < 14 * 215 * 2; step++)
        {
            double now = step * Dt;
            for (int i = 0; i < cars.Count; i++)
            {
                var cr = cars[i];
                float lapT = Field[cr.Cls].Lap * cr.Pace;
                double v = Dt / lapT * (1 + (rnd.NextDouble() - 0.5) * 0.02);
                if (cr.PitLeft > 0) { v *= 0.25; cr.PitLeft -= Dt; }
                int before = (int)Math.Floor(cr.Dist);
                if (!cr.Pitted && cr.Dist + v >= cr.PitLap + 0.985) { cr.Pitted = true; cr.PitLeft = 26 + rnd.NextDouble() * 14; }
                cr.Dist += v;
                int after = (int)Math.Floor(cr.Dist);
                if (after > before && after >= 1)
                {
                    double lap = now - cr.LapStart;
                    lap += rnd.NextDouble() * 0.5;   // timing finer than the sim step
                    if (before >= 1 && cr.PitLeft <= 0) { cr.Last = lap; if (cr.Best == 0 || lap < cr.Best) cr.Best = lap; }
                    cr.LapStart = now;
                }
                // trips off the track: mostly at one corner (about 62% round the lap), a few anywhere
                if (cr.OffLeft > 0) cr.OffLeft--;
                else if (cr.Dist > 1 && cr.PitLeft <= 0)
                {
                    double pct = cr.Dist - Math.Floor(cr.Dist);
                    bool hot = Math.Abs(pct - 0.62) < 0.004 && rnd.NextDouble() < 0.05;
                    bool any = rnd.NextDouble() < 0.00018;
                    if (hot || any) { cr.OffLeft = 2; cr.Inc += 1; }
                }
                cars[i] = cr;
            }
            // the contact: you and the car just ahead, lap 9, both pick up 4x
            if (!contactDone && cars[PlayerIdx].Dist > 9.30)
            {
                var order = cars.Where(x => x.Cls == 0).OrderByDescending(x => x.Dist).ToList();
                int at = order.FindIndex(x => x.Idx == PlayerIdx);
                var other = order[Math.Max(0, at - 1)];
                if (other.Idx == PlayerIdx) other = order[at + 1];
                int oi = cars.FindIndex(x => x.Idx == other.Idx);
                var o = cars[oi]; o.Dist = cars[PlayerIdx].Dist + 0.002; o.Inc += 4; cars[oi] = o;
                var p = cars[PlayerIdx]; p.Inc += 4; cars[PlayerIdx] = p;
                contactOther = other.Idx;
                contactDone = true;
            }
            snap = Build(cars, grid, t0.AddSeconds(now + rnd.NextDouble() * 0.45), now);
            RaceDiary.Tick(snap);
        }
        return snap;
    }

    static RaceSnapshot Build(List<(int Idx, int Cls, int Row, float Pace, double Dist, int PitLap, double PitLeft, bool Pitted, int Inc, double Best, double LapStart, double Last, int OffLeft)> cars,
                              Dictionary<int, int> grid, DateTime at, double now)
    {
        var classes = new List<DriverClass>();
        var all = new List<Driver>();
        int overall = 1;
        var byDist = cars.OrderByDescending(x => x.Dist).Select(x => x.Idx).ToList();
        for (int ci = 0; ci < Field.Length; ci++)
        {
            var mine = cars.Where(x => x.Cls == ci).OrderByDescending(x => x.Dist).ToList();
            var ds = new List<Driver>();
            for (int k = 0; k < mine.Count; k++)
            {
                var cr = mine[k];
                var f = Field[ci].Cars[cr.Row];
                float lapT = Field[ci].Lap;
                int laps = Math.Max(0, (int)Math.Floor(cr.Dist));
                double gap = (mine[0].Dist - cr.Dist) * lapT, interval = k == 0 ? 0 : (mine[k - 1].Dist - cr.Dist) * lapT;
                ds.Add(new Driver(cr.Idx, f.Name, f.Abbrev, f.Abbrev[..1] + f.Name.Split(' ')[^1][..1], f.Num, f.Car, f.Brand, Field[ci].Cls, Field[ci].Color,
                    "A 4.2", "#0153DB", byDist.IndexOf(cr.Idx) + 1, k + 1, laps + 1, (float)Math.Max(0, cr.Dist - Math.Floor(cr.Dist)), laps, 0,
                    (float)cr.Best, (float)cr.Last, gap, interval, cr.PitLeft > 0, true, false, false, 2400 + cr.Idx * 37,
                    Incidents: cr.Inc, OffTrack: cr.OffLeft > 0));
                overall++;
            }
            all.AddRange(ds);
            classes.Add(new DriverClass(Field[ci].Cls, Field[ci].Color, ds));
        }
        var player = all.First(d => d.CarIdx == PlayerIdx);
        var leader = classes[0].Drivers[0];
        float trackC = 31f + (float)Math.Min(4.2, now / 600.0);
        return RaceSnapshot.Disconnected with
        {
            IsConnected = true, CapturedAt = at, SessionState = 4, SessionTypeName = "Race", Phase = RacePhase.Broadcast, Flags = FlagState.Empty,
            CurrentLap = leader.CurrentLap, TotalLaps = 24, LapsRemaining = Math.Max(0, 24 - leader.LapsCompleted), SessionTimeRemaining = 3600,
            SessionTimeOfDay = 15 * 3600 + now, AirTempC = 23f, TrackTempC = trackC, TemperatureUnit = "C", IsRaining = false,
            TrackRawName = "lemans full", TrackDisplayName = "Circuit des 24 Heures du Mans", TrackBroadcastName = "Le Mans", TrackCountryCode = "FR",
            OpponentsCount = all.Count, PlayerCarIdx = PlayerIdx, CameraCarIdx = PlayerIdx, Player = player, Leader = leader, CameraTarget = player,
            ClassLeaderboard = classes, SessionNum = 2, GridClassPositions = grid,
            Extras = SessionExtras.Empty with { Humidity = 0.55f, Skies = 1, WindMs = 3.2f, RainChancePct = 20, TrackLength = "13.63 km", TrackWetness = 1 },
        };
    }

    static System.Collections.IList Contacts => (System.Collections.IList)typeof(RaceDiary).GetField("contacts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    static object? dqAdded;

    /// <summary>The Trading Paints DQ card: a car disqualified for incident points (added as the latest contact).</summary>
    public static void AddDq()
    {
        var gt3 = Field[2].Cars[6];
        int carIdx = Field[0].Cars.Length + Field[1].Cars.Length + 6;
        dqAdded = new RaceDiary.Contact(carIdx, gt3.Abbrev.ToUpperInvariant(), gt3.Brand, "GT3", 13, -1, 25, -1, null, null, 0, 0, DateTime.UtcNow, Dq: true);
        Contacts.Add(dqAdded);
    }

    public static void UndoDq()
    {
        if (dqAdded != null) { Contacts.Remove(dqAdded); dqAdded = null; }
    }
}
