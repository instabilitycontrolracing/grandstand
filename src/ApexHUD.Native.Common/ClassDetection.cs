namespace ApexHUD.Native.Common;

/// <summary>
/// Classifies a driver/car into a broadcast class label (GTP/GT3/GT4/GTE/LMP2/LMP3/...).
/// Mirrors the legacy <c>battle-mode.js</c> <c>classifyDriver</c> with the same
/// precedence: a roster name-prefix override (e.g. "GT3-Driver") wins over the
/// car-name keyword fallback, so spotter-defined classes carry through.
/// </summary>
public static class ClassDetection
{
    /// <summary>
    /// Class label from iRacing's own class data (2026-10-24) - the source of truth. Order:
    /// the roster <c>PREFIX-</c> override, then <paramref name="shortName"/> (DriverInfo
    /// CarClassShortName) through <see cref="ShortNames"/>, then <paramref name="classId"/>
    /// (CarClassID) through <see cref="KnownIds"/>, then the car-name fallback
    /// (<see cref="Classify"/>). A class none of those knows keeps iRacing's own short name
    /// (when short enough to fit the graphics) so two unknown classes don't merge into OTHER.
    /// Labels are the same set the colours/logos already use (GTP, LMP2, LMP3, GT3, GTD,
    /// GTDP, GT4, GTE, OTHER).
    /// </summary>
    public static string FromIRacing(int classId, string? shortName, string? name, string? carName)
        => Resolve(classId, shortName, name, carName).Label;

    /// <summary>Same as <see cref="FromIRacing"/>, plus which rule decided (for the log).</summary>
    public static (string Label, string Source) Resolve(int classId, string? shortName, string? name, string? carName)
        => Resolve(classId, shortName, name, carName, null);

    /// <summary>2026-10-29 (car parity): as above, with the car's own class from <see cref="CarCatalog"/> (sourced from
    /// iRacing's menus) deciding before the car-name guess - so the Porsche Cup cars and the Supercars Mustang are no
    /// longer GT3, and the 1980s Nissan GTP ZX-T is not a modern GTP. iRacing's short name / class ID still win.</summary>
    public static (string Label, string Source) Resolve(int classId, string? shortName, string? name, string? carName, string? catalogLabel)
    {
        if (RosterPrefix(name) is { } pre) return (pre, "roster");
        string key = Normalize(shortName);
        if (key.Length > 0)
        {
            if (ShortNames.TryGetValue(key, out var byShort)) return (byShort, "short");
            if (key.StartsWith("GT3", StringComparison.Ordinal)) return ("GT3", "short");
            if (key.StartsWith("GT4", StringComparison.Ordinal)) return ("GT4", "short");
        }
        if (classId > 0 && KnownIds.TryGetValue(classId, out var byId)) return (byId, "id");
        if (!string.IsNullOrEmpty(catalogLabel)) return (catalogLabel, "catalog");
        var byCar = Classify(null, carName);
        if (byCar == "OTHER" && !string.IsNullOrWhiteSpace(shortName) && shortName.Trim().Length <= 8)
            return (shortName.Trim().ToUpperInvariant(), "iracing");
        return (byCar, "car");
    }

    /// <summary>CarClassShortName (upper-cased; spaces, dashes, dots and a trailing
    /// "CLASS" removed) -> our label. iRacing's short names: "GT3 Class", "GTP", "LMP2",
    /// "GT4 Class", "GTE", "LMP3"; hosted/league sessions use IMSA names too.</summary>
    public static readonly IReadOnlyDictionary<string, string> ShortNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GTP"] = "GTP", ["LMDH"] = "GTP", ["LMH"] = "GTP", ["HYPERCAR"] = "GTP",
        ["LMP2"] = "LMP2", ["P2"] = "LMP2",
        ["LMP3"] = "LMP3", ["P3"] = "LMP3",
        ["GT3"] = "GT3",
        ["GTD"] = "GTD",
        ["GTDPRO"] = "GTDP", ["GTDP"] = "GTDP",
        ["GT4"] = "GT4",
        ["GTE"] = "GTE", ["GTEPRO"] = "GTE", ["GTEAM"] = "GTE", ["GTLM"] = "GTE",
        ["TCR"] = "TCR",   // 2026-10-29: the TCR cars' own class name
    };

    /// <summary>CarClassID -> label, used only when the short name isn't recognised.
    /// 4029 and 4011 are confirmed from the GPU host's "classes:" log line (replays can leave
    /// CarClassShortName empty, so these numbers decide there); the others are from memory -
    /// confirm against that log line when they show up.</summary>
    public static readonly IReadOnlyDictionary<int, string> KnownIds = new Dictionary<int, string>
    {
        [4029] = "GTP",    // IMSA GTP (Hybrid V8, 963, ARX-06, V-Series.R) - verified 2026-10-26 (Le Mans replay log)
        [4011] = "GT3",    // GT3 class at Le Mans - verified 2026-10-26 (the replay left the short name blank)
        [2523] = "LMP2",   // Dallara P217 LMP2
        [2708] = "GT3",    // GT3 Class
        [4084] = "GT4",    // GT4 Class
    };

    static string Normalize(string? shortName)
    {
        if (string.IsNullOrWhiteSpace(shortName)) return "";
        var sb = new System.Text.StringBuilder(shortName.Length);
        foreach (char ch in shortName.ToUpperInvariant())
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        var k = sb.ToString();
        if (k.Length > 5 && k.EndsWith("CLASS", StringComparison.Ordinal)) k = k[..^5];
        return k;
    }

    static string? RosterPrefix(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var i = 0;
        while (i < name.Length && i < 5 && char.IsAsciiLetterUpper(name[i])) i++;
        return i >= 2 && i < name.Length && name[i] == '-' ? name[..i] : null;
    }

    /// <summary>
    /// Returns a broadcast class label for a driver. <paramref name="name"/>
    /// is the on-roster display name (used for the optional <c>PREFIX-</c>
    /// override); <paramref name="carName"/> is the cleaned car screen name.
    /// </summary>
    public static string Classify(string? name, string? carName)
    {
        if (!string.IsNullOrEmpty(name))
        {
            var i = 0;
            while (i < name.Length && i < 5 && char.IsAsciiLetterUpper(name[i])) i++;
            if (i >= 2 && i < name.Length && name[i] == '-')
                return name[..i];
        }

        if (string.IsNullOrEmpty(carName)) return "OTHER";
        var s = carName.ToLowerInvariant();

        if (s.Contains("gt4")) return "GT4";
        // 2026-10-29 (car parity audit, G9): one-make Cup cars and Supercars are not GT3; the 1980s Nissan isn't a GTP
        if (s.Contains(" cup") || s.Contains("challenge")) return "CUP";
        if (s.Contains("supercar")) return "SUPERCAR";
        if (s.Contains("tcr")) return "TCR";
        if (s.Contains("zx-t") || s.Contains("zx t")) return "CLASSIC";

        if (s.Contains("gt3") || s.Contains("r8 lms") || s.Contains("z06 gt") ||
            s.Contains("mustang gt") || s.Contains("huracán gt") || s.Contains("huracan gt") ||
            s.Contains("911 cup")) return "GT3";

        if (s.Contains("gte") || s.Contains("911 rsr") || s.Contains("488 gte") ||
            s.Contains("m8 gte") || s.Contains("c8.r")) return "GTE";

        if (s.Contains("gtp") || s.Contains("lmdh") || s.Contains("hypercar") ||
            s.Contains("963") || s.Contains("m hybrid") ||
            s.Contains("v-series") || s.Contains("v series") ||
            s.Contains("arx-06") || s.Contains("arx 06") ||
            s.Contains("499p") || s.Contains("gr010") || s.Contains("9x8") ||
            s.Contains("valkyrie") || s.Contains("amr-lmh") || s.Contains("amr lmh")) return "GTP";

        if (s.Contains("lmp2") || s.Contains("p217") || s.Contains("oreca 07")) return "LMP2";
        if (s.Contains("lmp3") || s.Contains("p320")) return "LMP3";

        if (s.Contains("hybrid")) return "GTP";
        return "OTHER";
    }
}
