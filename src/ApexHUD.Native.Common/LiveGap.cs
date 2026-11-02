namespace ApexHUD.Native.Common;

/// <summary>
/// Pure live-gap math, ported from the legacy <c>live-timing.js</c>. The gap
/// between two cars is the difference in their progress (lap + lap-dist-pct)
/// multiplied by the trailing car's best lap time — the standard F1/IMSA
/// position-math gap. Smooth at full 60 Hz natively, where the legacy chain
/// was capped by HTTP polling.
/// </summary>
public static class LiveGap
{
    /// <summary>
    /// Total race progress for a car: <c>lap + lapDistPct</c>. Returns null
    /// when either input is missing/invalid.
    /// </summary>
    public static double? Progress(int? lap, float? lapDistPct)
    {
        if (lap == null || lapDistPct == null) return null;
        if (lapDistPct.Value < 0) return null;
        return lap.Value + lapDistPct.Value;
    }

    /// <summary>
    /// Live gap in seconds between an ahead car and a behind car. Returns
    /// null when inputs are missing, or when the "behind" car is actually
    /// ahead (lapped-wrap edge case — caller should fall back to lap-difference
    /// display).
    /// </summary>
    public static double? Between(double? progressAhead, double? progressBehind, float? bestLapBehind, float? bestLapAhead)
    {
        if (progressAhead == null || progressBehind == null) return null;
        if (progressBehind.Value >= progressAhead.Value) return null;

        float pace = bestLapBehind ?? 0f;
        if (pace <= 0) pace = bestLapAhead ?? 0f;
        if (pace <= 0) return null;

        return (progressAhead.Value - progressBehind.Value) * pace;
    }
}
