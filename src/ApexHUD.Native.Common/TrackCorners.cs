using System.IO;
using System.Linq;
namespace ApexHUD.Native.Common;

/// <summary>
/// Maps <c>LapDistPct</c> (0.0..1.0) to a named corner per track. Port of the
/// legacy <c>shared/track-corners.js</c>, which the featured card uses to
/// surface the current corner name under the focused driver's identity row.
///
/// <para>The legacy table only covers Spa-Francorchamps; everywhere else it
/// returned null and the corner line stayed hidden. We preserve that scope —
/// expand as Angelo populates more corner ranges. Lookup keys off the
/// broadcast track name (uppercased), and ranges first-match-wins.</para>
/// </summary>
public static class TrackCorners
{
    public readonly record struct CornerRange(double From, double To, string Name);

    static readonly CornerRange[] SpaCorners =
    {
        new(0.000, 0.045, "La Source"),
        new(0.085, 0.135, "Eau Rouge"),
        new(0.220, 0.290, "Les Combes"),
        new(0.350, 0.395, "Rivage"),
        new(0.500, 0.575, "Pouhon"),
        new(0.610, 0.665, "Fagnes"),
        new(0.700, 0.755, "Stavelot"),
        new(0.820, 0.880, "Blanchimont"),
        new(0.925, 0.985, "Bus Stop"),
    };

    static readonly Dictionary<string, CornerRange[]> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Circuit de Spa-Francorchamps"] = SpaCorners,
        ["Spa"] = SpaCorners,
        ["Spa 2024"] = SpaCorners,
    };

    /// <summary>
    /// Resolve the current corner name from a broadcast track name and a
    /// lap-distance percentage (0..1). Returns null on a straight or when the
    /// track has no corner data bundled.
    /// </summary>
    public static string? Resolve(string? broadcastTrackName, float lapDistPct)
    {
        if (string.IsNullOrWhiteSpace(broadcastTrackName)) return null;
        if (!float.IsFinite(lapDistPct)) return null;
        if (!Table.TryGetValue(broadcastTrackName.Trim(), out var corners)) return null;
        foreach (var c in corners)
            if (lapDistPct >= c.From && lapDistPct <= c.To) return c.Name;
        return null;
    }

    // ---- 2026-10-25 (Angelo: "the On the Limit card should say the corner name, not what percent of the lap"):
    // every iRacing track's corners, from the trackLandmarksData.json of the user's own CrewChief install (the same
    // file the race engineer uses). Read once, on first use; nothing is copied or shipped.
    static readonly string[] LandmarkFiles =
    {
        @"C:\Program Files (x86)\Britton IT Ltd\CrewChiefV4\trackLandmarksData.json",
        @"C:\Program Files\Britton IT Ltd\CrewChiefV4\trackLandmarksData.json",
    };
    static Dictionary<string, List<(double A, double B, string Name)>>? landmarks;

    /// <summary>Extra place to look for trackLandmarksData.json first (offline preview tools); null on the PC.</summary>
    public static string? LandmarkFileOverride { get; set; }

    /// <summary>2026-10-28: the iRacing track (raw name) the overlay last asked about - lets track-specific
    /// corner themes (e.g. the Le Mans Dunlop bridge) stay on their own track.</summary>
    public static string? LastTrackRaw { get; private set; }
    static void Seen(string? trackRawName) { if (!string.IsNullOrWhiteSpace(trackRawName)) LastTrackRaw = trackRawName; }

    static readonly (string Track, string Whole, double At, string First, string Second)[] Splits =
    {
        ("lemans full", "Porsche Curves", 12020, "Porsche Curves", "Corvette"),   // known simply as "the Corvette"
    };

    // 2026-10-28 (IMSA season, weeks 8-12): CrewChief's positions stand, but some of its names are only a turn number,
    // a misspelling, or a short marker on a straight. Track ids (several with '|'), CrewChief name -> the circuit's own
    // name (null = not a corner: a marker on a straight, dropped so the straight reads by its real name). Sources per
    // line; adjacent stretches renamed to the same name become one.
    static readonly (string Tracks, string From, string? To)[] Renames =
    {
        // Autódromo Hermanos Rodríguez, GP (official curve names: mexicogp.mx, "Nombre de las curvas y rectas AHR")
        ("mexicocity gp", "Esses Moises Solana", "Moisés Solana"),        // curve 1 (curves 1-3 the "Complejo Moisés Solana")
        ("mexicocity gp", "Ese Del Lago", "La Presidencial"),             // curve 4
        ("mexicocity gp", "Horquilla", "Recorte Rebaque"),                // curve 6 (the old Horquilla is no longer used)
        ("mexicocity gp", "Turn 9", "Adrián Fernández"),                   // curve 12, the first corner after the esses
        ("mexicocity gp", "Mansel", "Curva Nigel Mansell"),               // curve 17 (formula1.com, 2015)
        // Suzuka GP (Wikipedia / f1chronicle: the S Curves, 200R after the Hairpin)
        ("suzuka grandprix|suzuka east", "First Esses", "S Curves"),
        ("suzuka grandprix|suzuka east", "Second Esses", "S Curves"),
        ("suzuka grandprix", "Turn 12", "200R"),
        // Silverstone GP since 2011 (formula1.com corner list: Abbey, Farm, Village, The Loop, Aintree, Wellington Straight,
        // Brooklands, Luffield ...): CrewChief's numbered turns 3 / 5 / 6 are Village / Aintree / Brooklands, and its
        // un-numbered "brooklands" stretch is the Wellington Straight before it
        ("silverstone 2019 gp", "Turn 3", "Village"),
        ("silverstone 2019 gp", "Turn 5", "Aintree"),
        ("silverstone 2019 gp", "Brooklands", null),
        ("silverstone 2019 gp", "Turn 6", "Brooklands"),
        // Monza (formula1.com "how every corner at Monza got its name": the chicane is the Variante Ascari; the back
        // straight entry in KnownStraights already expects that name)
        ("monza full|monza gpsecondchicane|monza nochicane|monza combinedchicanes|monza historic|monza historicsecondchicane", "Ascari", "Variante Ascari"),
        // Sebring (24h-lemans.com "12 Hours of Sebring: legendary race track"; IMSA spectator guide; sportscar365 2016)
        ("sebring international|sebring modified|sebring club", "Kristensen", "Kristensen Corner"),
        ("sebring international|sebring modified|sebring club", "Gurney", "Gurney Bend"),
        ("sebring international|sebring modified|sebring club", "Cunningham", "Cunningham Corner"),
        ("sebring international|sebring modified|sebring club", "Tower", "Tower Turn"),
        ("sebring international|sebring modified|sebring club", "Le Mans", "Le Mans Curve"),
        ("sebring international|sebring modified|sebring club", "Sunset", "Sunset Bend"),
        ("sebring international|sebring modified|sebring club", "Flying Fortress", null),   // the Flying Fortress Straight
        ("sebring international|sebring modified|sebring club", "Ullman", null),            // the Ulmann Straight
    };

    static Dictionary<string, List<(double A, double B, string Name)>> Landmarks()
    {
        if (landmarks != null) return landmarks;
        var map = new Dictionary<string, List<(double, double, string)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string? path = LandmarkFileOverride is { } ov && File.Exists(ov) ? ov : LandmarkFiles.FirstOrDefault(File.Exists);
            if (path != null)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                foreach (var t in doc.RootElement.GetProperty("trackLandmarksData").EnumerateArray())
                {
                    if (!t.TryGetProperty("irTrackName", out var ir) || ir.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                    string key = (ir.GetString() ?? "").Replace('_', ' ').Trim();
                    if (key.Length == 0 || !t.TryGetProperty("trackLandmarks", out var lms)) continue;
                    var list = new List<(double, double, string)>();
                    bool packed = false;
                    foreach (var lm in lms.EnumerateArray())
                    {
                        if (!lm.TryGetProperty("landmarkNames", out var names)) continue;
                        // 2026-10-28: the track pack's name for this landmark (TrackPacks; none for the six hand-made tracks)
                        double pa = lm.TryGetProperty("distanceRoundLapStart", out var px) ? px.GetDouble() : 0;
                        var (hit, packName) = TrackPacks.LandmarkName(key, pa);
                        if (hit)
                        {
                            packed = true;
                            if (packName != null) list.Add((pa, lm.TryGetProperty("distanceRoundLapEnd", out var py) ? py.GetDouble() : pa, packName));
                            continue;
                        }
                        string? pick = null, turn = null;
                        foreach (var n in names.EnumerateArray())
                        {
                            string raw = n.GetString() ?? "";
                            if (raw.Equals("no_name", StringComparison.OrdinalIgnoreCase) || raw.Length == 0) continue;
                            if (System.Text.RegularExpressions.Regex.IsMatch(raw, @"^(?i)turn_?\d+")) turn ??= raw;
                            else { pick = raw; break; }
                        }
                        string? nm = Pretty(pick ?? turn);
                        if (nm == null) continue;
                        double a = lm.TryGetProperty("distanceRoundLapStart", out var x) ? x.GetDouble() : 0;
                        double b = lm.TryGetProperty("distanceRoundLapEnd", out var y) ? y.GetDouble() : a;
                        list.Add((a, b, nm));
                    }
                    ApplyRenames(key, list);
                    if (packed) MergeSameNames(list);
                    // corners CrewChief keeps as one stretch that have a famous name of their own inside them
                    // (2026-10-25, Angelo: Corvette Corner at Le Mans gets the Corvette logo)
                    foreach (var (trk, whole, at, first, second) in Splits)
                        if (key.Equals(trk, StringComparison.OrdinalIgnoreCase))
                            for (int si = 0; si < list.Count; si++)
                                if (list[si].Item3 == whole && list[si].Item1 < at && at < list[si].Item2)
                                {
                                    var w = list[si];
                                    list[si] = (w.Item1, at, first);
                                    list.Insert(si + 1, (at, w.Item2, second));
                                    break;
                                }
                    if (list.Count > 0) map[key] = list;
                }
            }
        }
        catch { }
        return landmarks = map;
    }

    static void ApplyRenames(string key, List<(double A, double B, string Name)> list)
    {
        bool any = false;
        foreach (var (tracks, from, to) in Renames)
        {
            if (!tracks.Split('|').Any(t => t.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Name.Equals(from, StringComparison.OrdinalIgnoreCase))
                {
                    any = true;
                    if (to == null) list.RemoveAt(i); else list[i] = (list[i].A, list[i].B, to);
                }
        }
        if (!any) return;
        MergeSameNames(list);
    }

    static void MergeSameNames(List<(double A, double B, string Name)> list)
    {
        // two stretches back to back that now carry the same name are one corner (Suzuka's S Curves)
        list.Sort((x, y) => x.A.CompareTo(y.A));
        for (int i = list.Count - 1; i > 0; i--)
            if (list[i].Name == list[i - 1].Name && list[i].A - list[i - 1].B < 30)
            {
                list[i - 1] = (list[i - 1].A, Math.Max(list[i - 1].B, list[i].B), list[i].Name);
                list.RemoveAt(i);
            }
    }

    static string? Pretty(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var n = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(raw.Replace('_', ' ').Trim().ToLowerInvariant());
        n = System.Text.RegularExpressions.Regex.Replace(n, @"(\D)(\d+)$", "$1 $2");
        return n switch { "Radillion" => "Raidillon", "Lesmos 1" => "Lesmo 1", "Lesmos 2" => "Lesmo 2", "Stavlot" => "Stavelot",
            "Les Fanges" => "Les Fagnes", "Speed Corner" => "Speed Corner", "Mulsanne" => "Mulsanne Corner",
            // 2026-10-28: Monza's Italian names keep their small "del" / "della"; Silverstone's corner is Maggotts (formula1.com)
            "Variante Del Rettifilo" => "Variante del Rettifilo", "Variante Della Roggia" => "Variante della Roggia", "Maggots" => "Maggotts", _ => n.Replace("Nascar", "NASCAR") };
    }

    /// <summary>2026-10-27 (session review): every named corner of a track in lap order, as metres from the line
    /// (start, end), after the famous-name splits. null when the track has no landmarks.</summary>
    public static List<(double StartM, double EndM, string Name)>? Corners(string? trackRawName)
    {
        Seen(trackRawName);
        if (string.IsNullOrWhiteSpace(trackRawName) || !Landmarks().TryGetValue(trackRawName.Replace('_', ' ').Trim(), out var list) || list.Count == 0) return null;
        return list.OrderBy(x => x.A).Select(x => (x.A, x.B, x.Name)).ToList();
    }

    /// <summary>Track length in metres from iRacing's "13.53 km" / "8.41 mi" text (0 if unknown).</summary>
    public static double LengthM(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) return 0;
        return parts.Length > 1 && parts[1].StartsWith("mi", StringComparison.OrdinalIgnoreCase) ? v * 1609.344 : v * 1000;
    }

    /// <summary>The named corner the car is in, or the next one within <paramref name="aheadM"/> metres (2026-10-25,
    /// battle map: "FIGHTING INTO ARNAGE"). Inside = true when the car is already in it. Start / end as lap fractions.</summary>
    public static (string Name, bool Inside, float StartPct, float EndPct)? Upcoming(string? trackRawName, string? trackLengthText, float lapDistPct, double aheadM = 700)
    {
        Seen(trackRawName);
        double len = LengthM(trackLengthText);
        if (string.IsNullOrWhiteSpace(trackRawName) || len <= 0 || !Landmarks().TryGetValue(trackRawName.Replace('_', ' ').Trim(), out var list)) return null;
        double m = (lapDistPct - Math.Floor(lapDistPct)) * len;
        (string, bool, float, float)? best = null; double bestD = double.MaxValue;
        foreach (var (a, b, name) in list)
        {
            if (m >= a - 20 && m <= b) return (name, true, (float)(a / len), (float)(b / len));
            double d = a - m; if (d < 0) d += len;               // past the line: wraps to next lap
            if (d <= aheadM && d < bestD) { bestD = d; best = (name, false, (float)(a / len), (float)(b / len)); }
        }
        return best;
    }

    // 2026-10-25 (Angelo: "it says ON TRACK where it should say Mulsanne Straight ... FIGHTING DOWN THE MULSANNE
    // STRAIGHT"): the well-known straights, by track and the corner they lead into. Anything not listed is "the run
    // to <next corner>", and the stretch across the start/finish line is "the main straight".
    static readonly (string Track, string NextCorner, string Name)[] KnownStraights =
    {
        // the Mulsanne is cut in three by the chicanes (Angelo: "first, second or final Mulsanne straight")
        ("lemans", "Playstation Chicane", "the first Mulsanne Straight"),
        ("lemans", "Michelin Chicane", "the second Mulsanne Straight"),
        ("lemans", "Mulsanne Corner", "the final Mulsanne Straight"),
        ("spa", "Les Combes", "the Kemmel Straight"),
        ("silverstone", "Stowe", "the Hangar Straight"),
        ("silverstone", "Brooklands", "the Wellington Straight"),
        ("bathurst", "The Chase", "Conrod Straight"),
        ("bathurst", "Griffins Bend", "Mountain Straight"),
        ("sebring", "Sunset Bend", "the Ulmann Straight"),       // 2026-10-28: was "Turn 17" - CrewChief calls it Sunset (24h-lemans.com)
        ("sebring", "Bishop", "the Flying Fortress Straight"),  // 2026-10-28: Hendricks Field's B-17s (24h-lemans.com)
        ("silverstone", "Abbey", "the Hamilton Straight"),      // 2026-10-28: the pit straight, renamed for Lewis Hamilton in 2020
        ("suzuka", "130R", "the back straight"),                // 2026-10-28: the 1.2 km straight that crosses over the front half (Wikipedia)
        ("mexicocity", "La Presidencial", "the Recta Jim Clark"), // 2026-10-28: between curves 3 and 4 (mexicogp.mx)
        ("redbullring", "Remus", "the run to Remus"),
        ("monza", "Variante Ascari", "the back straight"),
        ("nurburgring", "Dottinger Hohe", "the Döttinger Höhe"),
    };

    /// <summary>The straight a spot on the lap is on (null inside a corner or when the track has no landmarks), as a
    /// phrase ready for "FIGHTING DOWN ...": "the Mulsanne Straight", "the main straight", "the run to Arnage".</summary>
    public static string? Straight(string? trackRawName, string? trackLengthText, float lapDistPct)
    {
        Seen(trackRawName);
        double len = LengthM(trackLengthText);
        if (string.IsNullOrWhiteSpace(trackRawName) || len <= 0 || !Landmarks().TryGetValue(trackRawName.Replace('_', ' ').Trim(), out var list) || list.Count < 2) return null;
        double m = (lapDistPct - Math.Floor(lapDistPct)) * len;
        var sorted = list.OrderBy(x => x.A).ToList();
        foreach (var c in sorted) if (m >= c.A - 20 && m <= c.B) return null;            // in a corner
        var next = sorted.FirstOrDefault(c => c.A > m);
        bool wraps = next.Name == null;                                                  // past the last corner: the line is ahead
        if (wraps) next = sorted[0];
        // 2026-10-28: the track pack's named straights, by the corner they follow (the last corner when the spot is
        // before the first one: the lap wraps)
        var before = sorted.LastOrDefault(c => c.B < m);
        // (round 2: on a hand-made track's other layouts the hand KnownStraights come first)
        bool handPack = TrackPacks.IsHand(trackRawName);
        if (!handPack && TrackPacks.StraightAfter(trackRawName, before.Name ?? sorted[^1].Name) is { } packed) return packed;
        string key = trackRawName.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        foreach (var (t, nc, name) in KnownStraights)
            if (key.Contains(t) && string.Equals(nc, next.Name, StringComparison.OrdinalIgnoreCase)) return name;
        if (handPack && TrackPacks.StraightAfter(trackRawName, before.Name ?? sorted[^1].Name) is { } handPacked) return handPacked;
        var prev = sorted.LastOrDefault(c => c.B < m);
        if (wraps || prev.Name == null) return "the main straight";
        return "the run to " + next.Name;
    }

    /// <summary>The lap in order (2026-10-25, Angelo: the track card "could roll through each named corner and straight,
    /// taking us through the lap"): every named corner, and between them the straights that have a real name (or the main
    /// straight), as lap fractions. null when the track has no landmarks.</summary>
    public static List<(string Name, float StartPct, float EndPct, bool Straight)>? Lap(string? trackRawName, string? trackLengthText)
    {
        Seen(trackRawName);
        double len = LengthM(trackLengthText);
        if (string.IsNullOrWhiteSpace(trackRawName) || len <= 0 || !Landmarks().TryGetValue(trackRawName.Replace('_', ' ').Trim(), out var list) || list.Count == 0) return null;
        var sorted = list.OrderBy(x => x.A).ToList();
        var outL = new List<(string, float, float, bool)>();
        for (int i = 0; i < sorted.Count; i++)
        {
            var c = sorted[i];
            outL.Add((c.Name, (float)(c.A / len), (float)(c.B / len), false));
            // the stretch to the next corner, when it is a named straight (unnamed "run to" stretches are skipped)
            double a = c.B, b = i + 1 < sorted.Count ? sorted[i + 1].A : sorted[0].A + len;
            if (b - a < 250) continue;
            float mid = (float)(((a + b) / 2 % len) / len);
            var st = Straight(trackRawName, trackLengthText, mid);
            if (st != null && !st.StartsWith("the run to", StringComparison.OrdinalIgnoreCase))
                outL.Add((st.StartsWith("the ") ? char.ToUpperInvariant(st[4]) + st[5..] : st, (float)(a / len), (float)((b % len) / len), true));
        }
        return outL;
    }

    /// <summary>Corner name for a spot on the lap, by iRacing's track id (WeekendInfo.TrackName, e.g. "lemans full")
    /// and lap fraction. Falls back to the bundled table. null = no named corner there.</summary>
    public static string? Named(string? trackRawName, string? trackLengthText, float lapDistPct, string? broadcastName = null)
    {
        Seen(trackRawName);
        double len = LengthM(trackLengthText);
        if (!string.IsNullOrWhiteSpace(trackRawName) && len > 0 && Landmarks().TryGetValue(trackRawName.Replace('_', ' ').Trim(), out var list))
        {
            double m = lapDistPct * len;
            string? best = null;
            foreach (var (a, b, name) in list)
                if (m >= a - 30 && m <= b + 60) best = name;
            if (best != null) return best;
        }
        return Resolve(broadcastName, lapDistPct);
    }
}
