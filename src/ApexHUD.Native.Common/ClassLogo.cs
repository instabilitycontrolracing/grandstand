namespace ApexHUD.Native.Common;

/// <summary>
/// Maps a broadcast class label (as produced by <see cref="ClassDetection"/>
/// and carried on <c>Driver.ClassName</c>) to the filename slug of a bundled
/// IMSA class logo in <c>assets/logos/classes/</c>, or <c>null</c> when no logo
/// ships for that class so the caller falls back to the text label.
/// </summary>
/// <remarks>
/// iRacing runs a single on-track GT3 class, whereas IMSA splits the same cars
/// into GTD PRO and GTD by driver rating (an entry rule, not a separate grid
/// class). There is therefore no data-driven GT3 logo; <see cref="Gt3LogoSlug"/>
/// is a deliberate branding choice. Flip it to <c>"GTD"</c> for a more
/// representative GT field, or keep <c>"GTDPRO"</c> for the prestige tier.
/// GT4, GTE, LMP3 and OTHER intentionally return null (no IMSA logo bundled).
/// </remarks>
public static class ClassLogo
{
    /// <summary>Which IMSA logo represents iRacing's single GT3 class:
    /// "GTDPRO" (prestige) or "GTD" (broader real-world GT field).</summary>
    public const string Gt3LogoSlug = "GTD";   // Angelo 2026-10-24: real broadcasts show GTD, not GTD PRO

    /// <summary>How a class is written on screen: follows the logo rule, so iRacing's GT3
    /// class reads "GTD PRO" everywhere (Angelo 2026-10-19).</summary>
    public static string DisplayName(string? classLabel)
    {
        if (string.IsNullOrWhiteSpace(classLabel)) return "";
        return Resolve(classLabel) switch
        {
            "GTDPRO" => "GTD PRO",
            string slug => slug,
            null => classLabel.Trim().ToUpperInvariant(),
        };
    }

    /// <summary>Returns the class-logo slug for a label, or null to fall back to text.</summary>
    public static string? Resolve(string? classLabel)
    {
        if (string.IsNullOrWhiteSpace(classLabel)) return null;
        return classLabel.Trim().ToUpperInvariant() switch
        {
            "GTP"  => "GTP",
            "LMP2" => "LMP2",
            "GTD"  => "GTD",
            "GTDP" or "GTDPRO" or "GTD PRO" => "GTDPRO",
            "GT3"  => Gt3LogoSlug,
            _      => null,
        };
    }
}
