namespace ApexHUD.Native.Common;

/// <summary>
/// The canonical broadcast palette used across overlays: per-class accent
/// colors, license-badge colors, and the row-status colors (leader gold,
/// player blue, fastest-lap purple, pit amber). Stored as <c>#RRGGBB</c>
/// strings so this stays SkiaSharp-free and overlays parse the hex they need.
/// </summary>
/// <remarks>
/// <para>Picks one canonical value where the legacy code disagreed — see
/// DECISIONS.md for the rationale on each. PORT_PLAN risk #3 noted the GTP
/// disagreement (theme.css champagne <c>#C5A572</c> vs the tower's purple
/// <c>#C084FC</c>); we match the tower so the standings column reads cleanly
/// at a glance.</para>
/// </remarks>
public static class BroadcastPalette
{
    /// <summary>Per-class accent (used for tower stripes, class headers, grid columns).</summary>
    public static string Class(string name) => name switch
    {
        "GTP"  => "#C084FC", // purple — Hypercar/LMDh tier
        "LMP"  => "#60A5FA", // blue
        "LMP2" => "#60A5FA",
        "LMP3" => "#6B85B8",
        "GTDP" => "#FFB627", // amber
        "GTD"  => "#FFDA59", // yellow
        "GT3"  => "#FFDA59",
        "GTE"  => "#F87171", // red
        "GT4"  => "#4ADE80", // green
        // 2026-10-29 (car parity): only used when iRacing sends no class colour of its own (a design choice, not a fact)
        "TCR"  => "#38BDF8", // sky
        "CUP"  => "#F472B6", // pink
        "SUPERCAR" => "#FB923C", // orange
        "LMP1" => "#2563EB", // deep blue (prototypes are blue)
        _      => "#6366F1", // OTHER / default
    };

    /// <summary>License badge background by iRacing license letter (R / D / C / B / A / P).</summary>
    public static string License(string letter) => letter?.Trim().ToUpperInvariant() switch
    {
        "P" => "#7C00FF",
        "A" => "#006EFF",
        "B" => "#33CC00",
        "C" => "#FFCC00",
        "D" => "#FF6600",
        "R" => "#E1251B",
        _   => "#E1251B",
    };

    /// <summary>License letter rendered on top of <see cref="License"/> — picks legible text per badge.</summary>
    public static string LicenseText(string letter) => letter?.Trim().ToUpperInvariant() switch
    {
        // C (yellow) needs dark text; everything else takes the ivory text colour.
        "C" => "#0A0E14",
        _   => "#FFFFFF",
    };

    // --- shared status colors (mirroring theme.css) ---
    public const string Leader        = "#C5A572"; // accent-gold (leader position + leader gap pill)
    public const string Player        = "#3B82F6"; // primary blue for the player-row background
    public const string PlayerStripe  = "#60A5FA"; // brighter inset stripe drawn on top of Player
    public const string FastestPurple = "#C084FC"; // status-purple — fastest-lap bullet
    public const string PitAmber      = "#FB923C"; // status-pit — PIT badge / lapped italic
    public const string Disconnected  = "#9CA3AF"; // dim grey for connection-lost rows

    public const string TextPrimary   = "#FCFAF5"; // warm ivory
    public const string TextSecondary = "#E5E7EB";
    // #340 (2026-10-27, Angelo: "make all this gray small text more visible" - he reads it from across the room):
    // labels were #9CA3AF (~62% luminance); now ~79%, still clearly quieter than TextSecondary.
    public const string TextTertiary  = "#C6CBD3";
    public const string BorderHairline = "#1F2937";

    /// <summary>The deep-glass tower base (matches <c>theme.css --bg-glass</c>).</summary>
    public const string TowerGlassBase = "#080A10"; // alpha applied at draw time
}

/// <summary>
/// Maps an iRacing/SimHub car-screen brand label to the filename slug used by
/// the bundled <c>assets/logos/manufacturers/*.png</c>. Mirrors the legacy
/// <c>manufacturerLogoPath()</c> aliases (Corvette → Chevrolet, AMR → Aston,
/// AMG → Mercedes) and normalises a few common variants.
/// </summary>
public static class ManufacturerBrand
{
    /// <summary>
    /// Returns the filename slug for a brand if we have a bundled logo, else
    /// null. Lookup is case-insensitive; aliases are honoured.
    /// </summary>
    public static string? Resolve(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand)) return null;
        var key = brand.Trim();

        // First: alias the legacy car-brand variants the inventory called out.
        // The aliases match the legacy tower's manufacturerLogoPath().
        var aliased = key.ToUpperInvariant() switch
        {
            "CORVETTE" => "Corvette",   // 2026-10-18 (Angelo): Corvette has its own crossflags logo
            "AMR"      => "Aston",
            "AMG"      => "Mercedes",
            "VW"       => "Volkswagen",
            "MCLAREN"  => "Mclaren",
            "AS_MARTIN" or "ASTON MARTIN" or "ASTON-MARTIN" => "Aston",
            "MERCEDES-AMG"  => "Mercedes",
            "HPD"      => "Honda",      // 2026-10-29: Honda Performance Development (HPD ARX-01c)
            _ => null,
        };
        if (aliased != null) return aliased;

        // Then: direct match against the bundled set (the 34 PNGs in
        // assets/logos/manufacturers/). Names are TitleCase in the filenames.
        foreach (var known in Known)
        {
            if (string.Equals(known, key, StringComparison.OrdinalIgnoreCase))
                return known;
        }

        return null;
    }

    /// <summary>The 34 brands we ship as PNGs.</summary>
    public static readonly string[] Known =
    {
        "Acura", "Alpine", "Aston", "Audi", "BMW", "Bentley", "Buick",
        "Cadillac", "Chevrolet", "Corvette", "Dallara", "Ferrari", "Ford", "Holden",
        "Honda", "Hyundai", "Jaguar", "Kia", "Lamborghini", "Lexus", "Ligier",
        "Lotus", "Maserati", "Mazda", "Mclaren", "Mercedes", "Nissan",
        "Pontiac", "Porsche", "Radical", "Renault", "SkipBarber", "Subaru",
        "Toyota", "Volkswagen",
    };
}
