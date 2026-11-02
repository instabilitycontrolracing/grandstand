namespace ApexHUD.Native.Common;

/// <summary>
/// Maps an iRacing <c>SeriesID</c> (from <c>WeekendInfo.SeriesID</c>) to a
/// broadcast-friendly display name and an optional filename slug under
/// <c>assets/logos/series/</c>. Mirrors the legacy <c>series-logos.js</c>
/// (<c>ApexSeriesLogos.getName</c> / <c>resolve</c>) — pure data, no I/O.
/// </summary>
/// <remarks>
/// The legacy lookup table shipped only one mapped SeriesID
/// (<c>447</c> → <c>"IMSA Sportscar Championship"</c>) — every other ID fell
/// through to the track name. We preserve that behaviour exactly: callers that
/// get back <c>null</c> from <see cref="ResolveName"/> should fall back to the
/// broadcast track name (uppercased). Bundled PNG slugs are listed for the
/// times the grid grows a logo column.
/// </remarks>
public static class SeriesNames
{
    /// <summary>
    /// Display name for the eyebrow text above the grid title. Returns
    /// <c>null</c> for any SeriesID we don't have a curated name for; the
    /// caller falls back to the track name.
    /// </summary>
    public static string? ResolveName(int? seriesId) => seriesId switch
    {
        447 => "IMSA iRacing Series by GO Fast",   // this season's title sponsor (Angelo 2026-10-19)
        419 => "IMSA Endurance Series",            // 2026-10-30: iRacing's own series logo (seriesid_419), 160 min rounds
        582 => "iRacing iTested Series",           // 2026-10-25 (Angelo: "series 582 was the iTested series")
        _   => null,
    };

    /// <summary>
    /// Bundled PNG slug (no extension) for <see cref="seriesId"/>, or
    /// <c>null</c> when we don't ship a logo for it. Each slug matches a file
    /// in <c>assets/logos/series/</c>.
    /// </summary>
    public static string? ResolveLogoSlug(int? seriesId) => seriesId switch
    {
        447 => "IMSA iRacing Series",
        419 => "IMSA Endurance Series",
        582 => "iTested Series",                   // cut from iRacing's announcement art - personal asset, never shipped
        _   => null,
    };

    /// <summary>The 10 bundled series logo filenames (without extension).</summary>
    public static readonly string[] BundledLogos =
    {
        "Creventic Endurance Series",
        "GT3 Fanatec Challenge",
        "GTE Sprint Series",
        "Global Endurance Tour",
        "IMSA Endurance Series",
        "IMSA Michelin Pilot Challenge",
        "IMSA Sportscar Endurance Challenge",
        "IMSA iRacing Series",
        "ProtoGT Challenge",
        "Simucube GT Endurance",
        "Simucube GT Sprint Series",
    };
}
