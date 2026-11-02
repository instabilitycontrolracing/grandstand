namespace ApexHUD.Native.Common;

/// <summary>
/// Display units chosen in the Grandstand control app (2026-10-25, Angelo: "I personally want it in F, and make sure
/// there is a setting in the UI for that"). "auto" follows iRacing's own units setting; "F" / "C" force it.
/// </summary>
public static class UnitPrefs
{
    public static string Temperature { get; set; } = "auto";

    /// <summary>True when temperatures should show in Fahrenheit.</summary>
    public static bool Fahrenheit(RaceSnapshot s)
        => Temperature switch
        {
            "F" => true,
            "C" => false,
            _ => string.Equals(s.TemperatureUnit, "F", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(s.TemperatureUnit, "Fahrenheit", StringComparison.OrdinalIgnoreCase),
        };
}
