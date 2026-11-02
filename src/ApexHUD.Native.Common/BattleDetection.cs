namespace ApexHUD.Native.Common;

/// <summary>
/// The player's same-class neighbours and the absolute gap (seconds) to each,
/// or null when there isn't a candidate. This is the raw input the battle-card
/// state machine consumes; thresholding, hysteresis, and the attack-vs-defend
/// pick all live there (in <c>BattleCardRenderer</c>), not here.
/// </summary>
/// <param name="Ahead">Same-class car ahead of the player in class order, or null.</param>
/// <param name="Behind">Same-class car behind the player in class order, or null.</param>
/// <param name="GapAheadSeconds">Positive seconds from player to <see cref="Ahead"/>, or null.</param>
/// <param name="GapBehindSeconds">Positive seconds from <see cref="Behind"/> to player, or null.</param>
/// <param name="Player">The player driver row at the time of detection (convenience).</param>
/// <param name="PlayerClass">The player's detected class name.</param>
public sealed record BattleCandidate(
    Driver? Ahead,
    Driver? Behind,
    double? GapAheadSeconds,
    double? GapBehindSeconds,
    Driver? Player,
    string PlayerClass)
{
    public static readonly BattleCandidate None = new(null, null, null, null, null, string.Empty);
}

/// <summary>
/// Pure detection: walks the player's class on the leaderboard and reports the
/// same-class neighbours and gaps to them. Mirrors the legacy
/// <c>battle-mode.js</c> <c>detect()</c> at the data-shape level (same-class
/// nearest ahead/behind, lapped-traffic guard, connected-only) while leaning on
/// the snapshot's per-row <c>IntervalSeconds</c> instead of re-walking standings.
/// </summary>
public static class BattleDetection
{
    /// <summary>Same-lap tolerance from the legacy: opponent must be within 1 lap of player.</summary>
    public const int SameLapTolerance = 1;

    public static BattleCandidate Compute(RaceSnapshot snap)
    {
        var player = snap.Player;
        if (player == null || snap.ClassLeaderboard.Count == 0) return BattleCandidate.None;

        // Find the player's class group.
        DriverClass? group = null;
        for (int i = 0; i < snap.ClassLeaderboard.Count; i++)
        {
            var g = snap.ClassLeaderboard[i];
            for (int j = 0; j < g.Drivers.Count; j++)
            {
                if (g.Drivers[j].CarIdx == player.CarIdx) { group = g; break; }
            }
            if (group != null) break;
        }
        if (group == null) return BattleCandidate.None;

        // 2026-10-24 (Angelo, replay test): iRacing's scored order only updates at the timing line, so
        // after a spin the cars that just went past still counted as "behind" and the card stuck on the
        // wrong rival. Neighbours now come from LIVE track position (lap + distance round the lap), and
        // the gap from that distance at the player's pace. Falls back to scored order when there is no
        // usable position or pace (e.g. before anyone has a lap time).
        if (LiveNeighbours(group, player) is { } live) return live;

        var drivers = group.Drivers;
        int idx = -1;
        for (int i = 0; i < drivers.Count; i++)
        {
            if (drivers[i].CarIdx == player.CarIdx) { idx = i; break; }
        }
        if (idx < 0) return BattleCandidate.None;

        Driver? ahead = idx > 0 ? drivers[idx - 1] : null;
        Driver? behind = idx + 1 < drivers.Count ? drivers[idx + 1] : null;

        // Connected + same-lap guards (lapped traffic doesn't count).
        if (ahead != null && (!ahead.IsConnected || !SameLap(player, ahead))) ahead = null;
        if (behind != null && (!behind.IsConnected || !SameLap(player, behind))) behind = null;

        // Gap to car ahead = player's own IntervalSeconds (gap from player to
        // the car immediately in front in class). Gap to car behind = behind's
        // IntervalSeconds (gap from behind to the player, since intervals on
        // each row are "gap to the car in front of me").
        double? gapAhead = ahead != null ? Positive(player.IntervalSeconds) : null;
        double? gapBehind = behind != null ? Positive(behind.IntervalSeconds) : null;

        return new BattleCandidate(
            Ahead: ahead,
            Behind: behind,
            GapAheadSeconds: gapAhead,
            GapBehindSeconds: gapBehind,
            Player: player,
            PlayerClass: group.Name);
    }

    /// <summary>Nearest same-class cars ahead/behind on the road right now, or null to use scored order.</summary>
    static BattleCandidate? LiveNeighbours(DriverClass group, Driver player)
    {
        if (LiveGap.Progress(player.CurrentLap, player.LapDistPct) is not double p) return null;
        float pace = player.BestLapTime > 30 ? player.BestLapTime : player.LastLapTime > 30 ? player.LastLapTime : 0f;
        Driver? ahead = null, behind = null;
        double da = double.MaxValue, db = double.MaxValue;
        foreach (var d in group.Drivers)
        {
            if (d.CarIdx == player.CarIdx || !d.IsConnected || d.IsPaceCar) continue;
            if (d.IsInPit && !player.IsInPit) continue;            // a car in the pit lane isn't racing you
            if (LiveGap.Progress(d.CurrentLap, d.LapDistPct) is not double q) continue;
            double delta = q - p;
            if (Math.Abs(delta) >= 0.5) continue;                 // lapping / lapped traffic, not a rival
            if (delta > 0 && delta < da) { da = delta; ahead = d; }
            else if (delta <= 0 && -delta < db) { db = -delta; behind = d; }
            if (pace <= 0 && d.BestLapTime > 30) pace = d.BestLapTime;
        }
        if (pace <= 0) return null;                               // no pace yet: scored order + intervals
        // 2026-10-24: when the road neighbour is also the tower neighbour (the race tower is ordered
        // by track position), use the snapshot's interval - iRacing's own timing (CarIdxF2Time, with
        // fallbacks), worked out once per data update - so the card and the tower show the same gap.
        int me = -1;
        for (int k = 0; k < group.Drivers.Count; k++) if (group.Drivers[k].CarIdx == player.CarIdx) { me = k; break; }
        double? gapA = ahead == null ? null
            : me > 0 && group.Drivers[me - 1].CarIdx == ahead.CarIdx && Positive(player.IntervalSeconds) is double ia ? ia
            : Math.Max(0.001, da * pace);
        double? gapB = behind == null ? null
            : me >= 0 && me + 1 < group.Drivers.Count && group.Drivers[me + 1].CarIdx == behind.CarIdx
              && Positive(group.Drivers[me + 1].IntervalSeconds) is double ib ? ib
            : Math.Max(0.001, db * pace);
        return new BattleCandidate(
            Ahead: ahead,
            Behind: behind,
            GapAheadSeconds: gapA,
            GapBehindSeconds: gapB,
            Player: player,
            PlayerClass: group.Name);
    }

    static bool SameLap(Driver player, Driver other)
        => Math.Abs(player.LapsCompleted - other.LapsCompleted) <= SameLapTolerance;

    static double? Positive(double? v) => v is double d && d > 0 ? d : null;
}
