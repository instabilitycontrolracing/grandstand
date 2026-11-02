using System.Globalization;
using IRSDKSharper;

namespace ApexHUD.Native.Data;

/// <summary>
/// 2026-10-27 (loading splash, concept A "TRACE"): what the TV splash may show while the sim loads, taken
/// straight from the SessionInfo YAML as it arrives. Empty strings / NaN mean "not known yet" - the splash
/// hides those fields, it never fills them in.
/// </summary>
/// <param name="TrackName">WeekendInfo.TrackDisplayName.</param>
/// <param name="TrackRawName">WeekendInfo.TrackName (only used to find the track map).</param>
/// <param name="TrackConfig">WeekendInfo.TrackConfigName.</param>
/// <param name="TrackCountry">WeekendInfo.TrackCountry.</param>
/// <param name="SessionType">SessionType of SessionInfo.Sessions[CurrentSessionNum].</param>
/// <param name="Classes">Distinct DriverInfo.Drivers[].CarClassShortName, fastest class first.</param>
/// <param name="TimeOfDay">WeekendInfo.WeekendOptions.TimeOfDay as iRacing writes it ("2:30 pm").</param>
/// <param name="AirTempC">WeekendInfo.TrackAirTemp in °C, NaN when missing.</param>
public sealed record SplashInfo(string TrackName, string TrackRawName, string TrackConfig, string TrackCountry,
                                string SessionType, IReadOnlyList<string> Classes, string TimeOfDay, float AirTempC)
{
    /// <summary>#476 (2026-10-28): cars in the session (no pace car / spectators) - the splash says what is loading.</summary>
    public int Cars { get; init; }

    /// <summary>#492 (2026-10-30): WeekendInfo.SimMode - "replay" for a saved replay, "full" for a live session ("" unknown).</summary>
    public string SimMode { get; init; } = "";

    /// <summary>True for a saved replay (SimMode "replay").</summary>
    public bool IsReplay => string.Equals(SimMode, "replay", StringComparison.OrdinalIgnoreCase);
}

public sealed partial class RaceStateService
{
    SplashInfo? splashInfo;

    /// <summary>The loading-splash fields from the latest SessionInfo, or null before any arrived / after the sim closed.</summary>
    public SplashInfo? Splash => Volatile.Read(ref splashInfo);

    /// <summary>Read on every SessionInfo update, in its own guard so a half-filled YAML still gives what it has.</summary>
    void CaptureSplash(IRacingSdkSessionInfo info)
    {
        try
        {
            var weekend = info.WeekendInfo;
            string sessionType = "";
            var sessions = info.SessionInfo;
            if (sessions?.Sessions != null)
                foreach (var s in sessions.Sessions)
                    if (s.SessionNum == sessions.CurrentSessionNum) { sessionType = s.SessionType ?? ""; break; }

            var classes = new List<(string Name, float Est)>();
            int cars = 0;
            if (info.DriverInfo?.Drivers != null)
                foreach (var d in info.DriverInfo.Drivers)
                {
                    if (d.CarIsPaceCar != 0 || d.IsSpectator != 0) continue;
                    cars++;
                    string c = (d.CarClassShortName ?? "").Trim();
                    if (c.Length == 0) continue;
                    int at = classes.FindIndex(x => string.Equals(x.Name, c, StringComparison.OrdinalIgnoreCase));
                    float est = (float)d.CarClassEstLapTime;
                    if (at < 0) classes.Add((c, est > 0 ? est : float.MaxValue));
                }
            classes.Sort((a, b) => a.Est.CompareTo(b.Est));   // stable enough: GTP, LMP2, GT3 (fastest first)

            var next = new SplashInfo(
                TrackName: (weekend?.TrackDisplayName ?? "").Trim(),
                TrackRawName: (weekend?.TrackName ?? "").Trim(),
                TrackConfig: (weekend?.TrackConfigName ?? "").Trim(),
                TrackCountry: (weekend?.TrackCountry ?? "").Trim(),
                SessionType: sessionType.Trim(),
                Classes: classes.Select(x => x.Name).ToArray(),
                TimeOfDay: (weekend?.WeekendOptions?.TimeOfDay ?? "").Trim(),
                AirTempC: ParseTempC(weekend?.TrackAirTemp))
            {
                Cars = cars,
                SimMode = (weekend?.SimMode ?? "").Trim(),
            };
            Volatile.Write(ref splashInfo, next);
        }
        catch { /* the splash shows what it has; never break the session feed */ }
    }

    /// <summary>"22.00 C" / "71.6 F" -> °C; NaN when there is no number.</summary>
    public static float ParseTempC(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return float.NaN;
        var t = text.Trim();
        int end = 0;
        while (end < t.Length && (char.IsDigit(t[end]) || t[end] is '.' or '-' or '+')) end++;
        if (end == 0 || !float.TryParse(t.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return float.NaN;
        bool fahrenheit = t.EndsWith("F", StringComparison.OrdinalIgnoreCase);
        return fahrenheit ? (v - 32f) * 5f / 9f : v;
    }
}
