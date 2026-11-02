using ApexHUD.Native.Common;

/// <summary>--vary: a deterministic moving field so the pixel hashes cover more of the tower / ticker than a static grid -
/// position swaps (arrows, row glides, flashes), a car in the pits, a lapped car, close intervals (battle glow), lap and
/// personal-best pulses with the lap-time flash, a long name that dissolves, a disconnected car, the player's row,
/// C / R licences, and practice (best-lap deltas) in the second half.</summary>
static class Vary
{
    public static RaceSnapshot At(RaceSnapshot b, int f, int frames)
    {
        int k = f / 90;
        var classes = new List<DriverClass>();
        for (int ci = 0; ci < b.ClassLeaderboard.Count; ci++)
        {
            var cl = b.ClassLeaderboard[ci];
            var ds = cl.Drivers.ToList();
            if (ci == 2 && k % 2 == 1) (ds[0], ds[1]) = (ds[1], ds[0]);
            if (ci == 1 && k % 3 == 2) (ds[2], ds[3]) = (ds[3], ds[2]);
            var outD = new List<Driver>();
            for (int i = 0; i < ds.Count; i++)
            {
                var d = ds[i] with { PositionInClass = i + 1 };
                if (ci == 1 && i == k % ds.Count) d = d with { IsInPit = true };
                if (ci == 2 && i == ds.Count - 1) d = d with { LapDifference = 1 };
                if (ci == 0 && i == 1) d = d with { IntervalSeconds = 0.3 };
                if (ci == 0 && i == 2) d = d with { IntervalSeconds = 0.8 };
                if (ci == 0 && i == 0) d = d with { LapsCompleted = d.LapsCompleted + k, BestLapTime = d.BestLapTime - 0.01f * k, LastLapTime = d.BestLapTime - 0.01f * k };
                if (ci == 2 && i == 3) d = d with { AbbrevName = "Vandersteenhoven-Montgomery, M" };
                if (ci == 1 && i == 4) d = d with { IsConnected = false };
                if (ci == 2 && i == 2) d = d with { LicenceString = "C 2.10" };
                if (ci == 2 && i == 4) d = d with { LicenceString = "R 1.00" };
                outD.Add(d);
            }
            classes.Add(cl with { Drivers = outD });
        }
        var player = classes[2].Drivers[1];
        return b with
        {
            ClassLeaderboard = classes,
            PlayerCarIdx = player.CarIdx, Player = player,
            SessionTypeName = f < frames / 2 ? "Race" : "Practice",
            CapturedAt = new DateTime(2026, 10, 29, 20, 0, 0, DateTimeKind.Utc).AddMilliseconds(f * 16.7),
        };
    }
}
