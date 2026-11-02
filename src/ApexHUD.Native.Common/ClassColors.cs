namespace ApexHUD.Native.Common;

/// <summary>
/// Class accent colours as on the real IMSA broadcast (Angelo 2026-10-24): GTP black, GTD (iRacing's GT3)
/// green, LMP2 blue. Used instead of iRacing's own class colours for the classes we know; anything
/// else keeps iRacing's colour.
/// </summary>
public static class ClassColors
{
    public static string For(string? className, string iracingColor) => (className ?? "").Trim().ToUpperInvariant() switch
    {
        "GTP" => "#000000",
        "GT3" or "GTD" => "#00A94F",
        "GTDP" or "GTD PRO" or "GTDPRO" => "#D2232A",
        "LMP2" => "#0072CE",
        _ => iracingColor,
    };
}
