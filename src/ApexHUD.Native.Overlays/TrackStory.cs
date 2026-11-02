namespace ApexHUD.Native.Overlays;

/// <summary>
/// A few lines about a circuit for the track card (2026-10-25, Angelo: "it needs more information on it like a short
/// track description"). Only facts we're sure of; tracks without an entry simply show no description.
/// </summary>
public static class TrackStory
{
    static readonly (string Prefix, string Text, string? FirstRace)[] Stories =
    {
        ("lemans", "The Circuit de la Sarthe: public roads south of Le Mans, closed for the 24 Hours and joined to the permanent Bugatti section. Home of the race since 1923, it is still mostly flat out, from the Mulsanne Straight to the Porsche Curves.", "1923"),
        // 2026-10-28 (IMSA season, weeks 8-12) - sources: Wikipedia, formula1.com, mexicogp.mx, 24h-lemans.com
        ("mexicocity", "Opened in 1959 in Mexico City's Magdalena Mixhuca park, 2,238 m above sea level, and named for the racing brothers Ricardo and Pedro Rodríguez. The lap ends through the Foro, a ballpark built inside the old Peraltada.", null),
        ("suzuka", "Honda's own circuit, opened in 1962 and designed by John Hugenholtz as a test track. A figure of eight: the back straight crosses over the front half of the lap on a bridge.", "1962"),
        ("silverstone", "Built on RAF Silverstone, a wartime bomber station. It held the British Grand Prix in 1948 and in 1950, the first World Championship Grand Prix; its corners are named for the abbey, chapel, farm and woods around it.", "1948"),
        ("sebring", "Laid out on Hendricks Field, a World War II training base for B-17 crews, with stretches of the original concrete runways still in the lap. The first race ran on New Year's Eve 1950, the first 12 Hours in 1952.", "1950"),
        ("monza", "Built in 1922 in the wooded park of the Royal Villa of Monza. The Temple of Speed: long straights and fast corners, broken up by chicanes, with the old banked oval still standing beside it.", "1922"),
    };

    // 2026-10-28: tracks without a hand-made entry take the sourced story / first-race year from their track pack
    public static string? Description(string? trackRaw) => Find(trackRaw) is { } st ? st.Text : ApexHUD.Native.Common.TrackPacks.For(trackRaw)?.Story;
    public static string? FirstRace(string? trackRaw) => Find(trackRaw) is { } st ? st.FirstRace : ApexHUD.Native.Common.TrackPacks.For(trackRaw)?.FirstRace;

    static (string Prefix, string Text, string? FirstRace)? Find(string? trackRaw)
    {
        if (string.IsNullOrWhiteSpace(trackRaw)) return null;
        foreach (var st in Stories) if (trackRaw.StartsWith(st.Prefix, StringComparison.OrdinalIgnoreCase)) return st;
        return null;
    }
}
