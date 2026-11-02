namespace ApexHUD.Native.Common;

/// <summary>
/// Broadcast-style time formatters for lap times and gap intervals. iRacing
/// publishes lap times as seconds (float); benofficial2 published them as
/// <c>"H:MM:SS.fff"</c> strings. We standardize on the broadcast forms most
/// overlays render: <c>M:SS.fff</c> for laps and a signed <c>+S.s</c> for gaps.
/// </summary>
public static class LapTimeFormat
{
    /// <summary>Format a lap time in seconds as <c>M:SS.fff</c>, or "—" for invalid.</summary>
    public static string Lap(float seconds) => Lap((double)seconds);

    public static string Lap(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return "—";
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}.{t.Milliseconds:D3}";
    }

    /// <summary>Format a session time remaining (seconds) as <c>H:MM:SS</c> or <c>M:SS</c>.</summary>
    public static string SessionTimer(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "—";
        if (seconds >= 172800) return "—";   // lap-limited races report a 604800 s placeholder, not a real clock
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";
    }

    /// <summary>Format a gap seconds value as a broadcast string (<c>+1.2</c>, <c>+12.3</c>, or <c>+1L</c> when lapped).</summary>
    public static string Gap(double? seconds, int lapsDifference = 0)
    {
        if (lapsDifference > 0) return $"+{lapsDifference}L";
        if (seconds == null || !double.IsFinite(seconds.Value)) return "—";
        var s = seconds.Value;
        if (s < 0) s = -s;
        return s < 10
            ? $"+{s:0.0}"
            : s < 60 ? $"+{s:0.0}" : SessionTimer(s);
    }
}
