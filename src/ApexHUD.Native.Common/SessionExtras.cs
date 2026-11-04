namespace ApexHUD.Native.Common;

/// <summary>
/// 2026-10-18 (Angelo): live weather + static track facts for the pre-race info
/// cards (Weather Report presented by StormExhibit.com, Track Facts).
/// Telemetry: WindVel (m/s), WindDir (rad, direction the wind blows FROM),
/// RelativeHumidity (0..1), Skies (0 clear, 1 partly, 2 mostly cloudy, 3 overcast),
/// Precipitation (0..1, falling now). Session info: TrackPrecipitation (chance %),
/// TrackLength ("13.53 km"), TrackNumTurns, TrackCity, TrackCountry.
/// </summary>
public sealed record SessionExtras(
    float WindMs,
    float WindDirRad,
    float Humidity,
    int Skies,
    float PrecipNow,
    float RainChancePct,
    string TrackLength,
    int TrackTurns,
    string TrackCity,
    string TrackCountry)
{
    /// <summary>2026-10-20: iRacing TrackWetness (0 unknown, 1 dry, 2 mostly dry, 3 very lightly wet,
    /// 4 lightly wet, 5 moderately wet, 6 very wet, 7 extremely wet).</summary>
    public int TrackWetness { get; init; }
    /// <summary>2026-10-31 (Angelo: the formation-lap event card didn't show the race length): iRacing's
    /// SessionTimeTotal - the scheduled length of the current session in seconds (0 = unknown).</summary>
    public double SessionTimeTotal { get; init; }
    /// <summary>Plain-English name for <see cref="TrackWetness"/>.</summary>
    public static string WetnessName(int w) => w switch
    {
        1 => "DRY", 2 => "MOSTLY DRY", 3 => "VERY LIGHTLY WET", 4 => "LIGHTLY WET",
        5 => "MODERATELY WET", 6 => "VERY WET", 7 => "EXTREMELY WET", _ => "-",
    };
    public static readonly SessionExtras Empty = new(0, 0, -1, -1, 0, 0, "", 0, "", "");
}
