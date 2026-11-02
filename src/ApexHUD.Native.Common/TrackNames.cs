namespace ApexHUD.Native.Common;

/// <summary>
/// Resolves iRacing's raw <c>TrackName</c>/<c>TrackDisplayName</c> into a
/// proper broadcast name (and ISO-3166 country code for the session-bar flag).
/// Port of <c>track-names.js</c>: substring match on the lowercased raw name,
/// most-specific entries first so they shadow generic ones.
/// </summary>
public static class TrackNames
{
    public readonly record struct TrackInfo(string Name, string CountryCode, string CountryName, double LengthKm, int Turns);

    static readonly (string Key, string Name)[] Tracks =
    {
        ("nurburgring combined",     "Nürburgring Combined"),
        ("nurburgring nordschleife", "Nürburgring Nordschleife"),
        ("nurburgring 24",           "Nürburgring 24h"),
        ("nordschleife",             "Nürburgring Nordschleife"),
        ("nurburgring",              "Nürburgring GP"),
        ("indianapolis road",        "Indianapolis Motor Speedway (Road)"),
        ("indianapolis oval",        "Indianapolis Motor Speedway"),
        ("indianapolis",             "Indianapolis Motor Speedway"),
        ("charlotte roval",          "Charlotte Motor Speedway Roval"),
        ("charlotte",                "Charlotte Motor Speedway"),
        ("daytona road",             "Daytona International Speedway (Road)"),
        ("daytona oval",             "Daytona International Speedway"),
        ("daytona",                  "Daytona International Speedway"),
        ("homestead road",           "Homestead-Miami Speedway (Road)"),
        ("homestead",                "Homestead-Miami Speedway"),
        ("canadian tire",            "Canadian Tire Motorsport Park"),
        ("mosport",                  "Canadian Tire Motorsport Park"),
        ("mid-ohio",                 "Mid-Ohio Sports Car Course"),
        ("mid ohio",                 "Mid-Ohio Sports Car Course"),
        ("midohio",                  "Mid-Ohio Sports Car Course"),
        ("road atlanta",             "Michelin Raceway Road Atlanta"),
        ("road america",             "Road America"),
        ("watkins glen",             "Watkins Glen International"),
        ("watkinsglen",              "Watkins Glen International"),
        ("laguna seca",              "WeatherTech Raceway Laguna Seca"),
        ("lagunaseca",               "WeatherTech Raceway Laguna Seca"),
        ("mount panorama",           "Mount Panorama Circuit"),
        ("bathurst",                 "Mount Panorama Circuit"),
        ("paul ricard",              "Circuit Paul Ricard"),
        ("magny",                    "Circuit de Nevers Magny-Cours"),
        ("marina bay",               "Marina Bay Street Circuit"),
        ("singapore",                "Marina Bay Street Circuit"),
        ("yas marina",               "Yas Marina Circuit"),
        ("albert park",              "Albert Park Circuit"),
        ("gilles villeneuve",        "Circuit Gilles Villeneuve"),
        ("montreal",                 "Circuit Gilles Villeneuve"),
        ("brands hatch",             "Brands Hatch Circuit"),
        ("oulton park",              "Oulton Park Circuit"),
        ("donington",                "Donington Park"),
        ("snetterton",               "Snetterton Circuit"),
        ("knockhill",                "Knockhill Racing Circuit"),
        ("thruxton",                 "Thruxton Circuit"),
        ("le mans",                  "Circuit des 24 Heures du Mans"),
        ("lemans",                   "Circuit des 24 Heures du Mans"),
        ("barcelona",                "Circuit de Barcelona-Catalunya"),
        ("catalunya",                "Circuit de Barcelona-Catalunya"),
        ("interlagos",               "Autódromo José Carlos Pace"),
        ("carlos pace",              "Autódromo José Carlos Pace"),
        ("portimao",                 "Autódromo Internacional do Algarve"),
        ("algarve",                  "Autódromo Internacional do Algarve"),
        ("spa",                      "Circuit de Spa-Francorchamps"),
        ("silverstone",              "Silverstone Circuit"),
        ("monza",                    "Autodromo Nazionale Monza"),
        ("imola",                    "Autodromo Internazionale Enzo e Dino Ferrari"),
        ("mugello",                  "Autodromo Internazionale del Mugello"),
        ("vallelunga",               "Autodromo Vallelunga"),
        ("suzuka",                   "Suzuka International Racing Course"),
        ("fuji",                     "Fuji Speedway"),
        ("okayama",                  "Okayama International Circuit"),
        ("motegi",                   "Twin Ring Motegi"),
        ("twin ring",                "Twin Ring Motegi"),
        ("zandvoort",                "Circuit Zandvoort"),
        ("hungaroring",              "Hungaroring"),
        ("hockenheim",               "Hockenheimring"),
        ("sachsenring",              "Sachsenring"),
        ("oschersleben",             "Motorsport Arena Oschersleben"),
        ("lausitzring",              "DEKRA Lausitzring"),
        ("salzburgring",             "Salzburgring"),
        ("sebring",                  "Sebring International Raceway"),
        ("vir",                      "Virginia International Raceway"),
        ("cota",                     "Circuit of the Americas"),
        ("circuit of the americas",  "Circuit of the Americas"),
        ("austin",                   "Circuit of the Americas"),
        ("red bull ring",            "Red Bull Ring"),
        ("spielberg",                "Red Bull Ring"),
        ("monaco",                   "Circuit de Monaco"),
        ("mexicocity",               "Autódromo Hermanos Rodríguez"),   // 2026-10-28: iRacing's "mexicocity gp"
        ("mexico city",              "Autódromo Hermanos Rodríguez"),
        ("hermanos rodriguez",       "Autódromo Hermanos Rodríguez"),
    };

    /// <summary>
    /// (key, properName, countryCode, countryName, lengthKm, turns) — only the
    /// most common circuits in Angelo's series wheelhouse. Trimmed from the
    /// legacy table; expand as new tracks appear. (us/be/de/jp/au/it/gb/fr/...
    /// codes match <c>/assets/flags/{cc}.png</c> in the legacy bundle.)
    /// </summary>
    static readonly (string Key, string Name, string Cc, string Country, double Km, int Turns)[] Info =
    {
        ("spa",           "Circuit de Spa-Francorchamps",                "be", "Belgium",         7.004, 20),
        ("monza",         "Autodromo Nazionale Monza",                   "it", "Italy",           5.793, 11),
        ("imola",         "Autodromo Enzo e Dino Ferrari",               "it", "Italy",           4.909, 19),
        ("silverstone",   "Silverstone Circuit",                         "gb", "United Kingdom",  5.891, 18),
        ("le mans",       "Circuit des 24 Heures du Mans",               "fr", "France",         13.626, 38),
        ("lemans",        "Circuit des 24 Heures du Mans",               "fr", "France",         13.626, 38),
        ("nurburgring nordschleife", "Nürburgring Nordschleife",         "de", "Germany",        20.832, 73),
        ("nurburgring",   "Nürburgring GP",                              "de", "Germany",         5.148, 16),
        ("daytona road",  "Daytona International Speedway (Road)",       "us", "USA",             5.729, 12),
        ("daytona",       "Daytona International Speedway",              "us", "USA",             5.729, 12),
        ("sebring",       "Sebring International Raceway",               "us", "USA",             6.019, 17),
        ("watkins glen",  "Watkins Glen International",                  "us", "USA",             5.435, 11),
        ("laguna seca",   "WeatherTech Raceway Laguna Seca",             "us", "USA",             3.602, 11),
        ("road atlanta",  "Michelin Raceway Road Atlanta",               "us", "USA",             4.088, 12),
        ("road america",  "Road America",                                "us", "USA",             6.515, 14),
        ("cota",          "Circuit of the Americas",                     "us", "USA",             5.513, 20),
        ("zandvoort",     "Circuit Zandvoort",                           "nl", "Netherlands",     4.259, 14),
        ("suzuka",        "Suzuka International Racing Course",          "jp", "Japan",           5.807, 18),
        ("mexicocity",    "Autódromo Hermanos Rodríguez",                "mx", "Mexico",          4.304, 17),   // 2026-10-28: 2015- GP layout (Wikipedia)
        ("fuji",          "Fuji Speedway",                               "jp", "Japan",           4.563, 16),
        ("bathurst",      "Mount Panorama Circuit",                      "au", "Australia",       6.213, 23),
        ("mount panorama","Mount Panorama Circuit",                      "au", "Australia",       6.213, 23),
        ("red bull ring", "Red Bull Ring",                               "at", "Austria",         4.318, 10),
        ("spielberg",     "Red Bull Ring",                               "at", "Austria",         4.318, 10),
    };

    public static string Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        if (TrackPacks.For(raw)?.Display is { Length: > 0 } packName) return packName;   // 2026-10-28: track packs
        var norm = raw.Trim().ToLowerInvariant();
        foreach (var (key, name) in Tracks)
            if (norm.Contains(key, StringComparison.Ordinal))
                return name;
        // Fallback: title-case the raw, strip the noisier layout suffixes.
        var cleaned = StripLayoutSuffix(norm);
        return TitleCase(string.IsNullOrWhiteSpace(cleaned) ? raw : cleaned);
    }

    public static TrackInfo? ResolveInfo(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // 2026-10-28: track packs (exact iRacing ids from the research files) - name, flag, and the layout's length /
        // turns when the research gives them for that exact layout (0 otherwise; only name + flag are shown)
        if (TrackPacks.For(raw) is { Display: { Length: > 0 } pn, Iso2: { Length: 2 } cc } pk)
        {
            var (km, turns) = TrackPacks.Layout(raw);
            return new TrackInfo(pn, cc, pk.Country ?? "", km, turns);
        }
        var norm = raw.Trim().ToLowerInvariant();
        foreach (var e in Info)
            if (norm.Contains(e.Key, StringComparison.Ordinal))
                return new TrackInfo(e.Name, e.Cc, e.Country, e.Km, e.Turns);
        return null;
    }

    static readonly string[] Suffixes =
    {
        "long", "short", "club", "gp", "grand prix", "boot", "alt", "alternate",
        "rev", "reverse", "moto", "outer", "infield", "international",
        "road course", "rallycross", "combined", "industrial", "oval", "road",
    };

    static string StripLayoutSuffix(string s)
    {
        // Drop year tokens like "2024".
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            if (p.Length == 4 && p.StartsWith("20", StringComparison.Ordinal) && int.TryParse(p, out _)) continue;
            if (Array.Exists(Suffixes, x => x == p)) continue;
            kept.Add(p);
        }
        return string.Join(' ', kept);
    }

    static string TitleCase(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        var newWord = true;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { sb.Append(c); newWord = true; continue; }
            sb.Append(newWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
            newWord = false;
        }
        return sb.ToString();
    }
}
