using System.Globalization;
using System.Text;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-11-06 (Angelo: "do the drivers to watch one"): three drivers worth keeping an eye on, each with a reason - the
/// way a TV broadcast sets up a race. Used by the pre-race info card / half-screen panel and the producer desk's
/// on-demand card.
///
/// Pace comes from Garage 61 when we have it (every driver's best public lap in their car at this track, fetched by the
/// host - Integrations.TickG61Field), otherwise from qualifying, otherwise from this session's best laps. The picks:
///   1. PACE SETTER - the fastest driver in the streamer's class (or the top class), by that pace.
///   2. THE CHARGER - starts / runs well behind where their pace says they should be (at least 3 places).
///   3. TOP RATED   - the highest iRating left in the field.
/// Gaps are filled with the pace setters of the other classes. The streamer himself is never picked (he has his own card).
/// </summary>
public static class DriversToWatch
{
    public sealed record Pick(int CarIdx, string Tag, string ClassName, string ClassColor, string Number, string Name, string Car,
        string Value, string Reason);

    // ---- Garage 61 pace, filled by the host: key = iRacing CarID + "|" + normalised driver name -> best lap (s)
    static volatile Dictionary<string, double> g61 = new(StringComparer.Ordinal);
    static readonly object gate = new();
    /// <summary>Which cars at which track the Garage 61 pace belongs to ("" = none yet).</summary>
    public static string PaceTrack { get; private set; } = "";
    public static int PaceDrivers => g61.Count;

    /// <summary>Host: every driver's best Garage 61 lap for one car at <paramref name="trackKey"/> (replaces that car's set).</summary>
    public static void SetPace(string trackKey, int carId, IEnumerable<(string Name, double Best)> laps)
    {
        lock (gate)
        {
            var d = new Dictionary<string, double>(PaceTrack == trackKey ? g61 : new Dictionary<string, double>(), StringComparer.Ordinal);
            string prefix = carId.ToString(CultureInfo.InvariantCulture) + "|";
            foreach (var k in d.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) d.Remove(k);
            foreach (var (name, best) in laps)
            {
                if (best <= 0) continue;
                string key = prefix + Norm(name);
                if (!d.TryGetValue(key, out var have) || best < have) d[key] = best;
            }
            PaceTrack = trackKey; g61 = d; cacheKey = "";
        }
    }

    /// <summary>"Jöhn  Smith2" -> "john smith" (iRacing adds digits to repeated names; Garage 61 has first + last).</summary>
    public static string Norm(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetter(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (ch == ' ' || ch == '-' || ch == '\'') sb.Append(' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    enum Src { None, G61, Quali, Session }
    static (double T, Src S) PaceOf(Driver d)
    {
        if (d.CarId > 0 && g61.TryGetValue(d.CarId.ToString(CultureInfo.InvariantCulture) + "|" + Norm(d.Name), out var t)) return (t, Src.G61);
        if (d.QualifyTime > 0) return (d.QualifyTime, Src.Quali);
        if (d.BestLapTime > 0) return (d.BestLapTime, Src.Session);
        return (0, Src.None);
    }
    static string SrcText(Src s) => s switch { Src.G61 => "on Garage 61", Src.Quali => "in qualifying", Src.Session => "this session", _ => "" };

    static string cacheKey = ""; static IReadOnlyList<Pick> cache = Array.Empty<Pick>();

    /// <summary>The picks for this field (cached until the field, positions or pace data change).</summary>
    public static IReadOnlyList<Pick> For(RaceSnapshot s)
    {
        var field = s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsPaceCar && !d.IsSpectator && d.CarIdx >= 0).ToList();
        string key = s.SessionNum + ":" + g61.Count + ":" + s.Phase + ":" + string.Join(",", field.Select(d => d.CarIdx + "/" + d.PositionInClass + "/" + d.IRating + "/" + (int)(d.QualifyTime * 10) + "/" + (int)(d.BestLapTime * 10)));
        if (key == cacheKey) return cache;
        var picks = Build(s, field);
        cacheKey = key; cache = picks;
        return picks;
    }

    static IReadOnlyList<Pick> Build(RaceSnapshot s, List<Driver> field)
    {
        int me = s.Player?.CarIdx ?? -1;
        bool pre = s.Phase is RacePhase.PreRace or RacePhase.Unknown;
        string verb = pre ? "Starts" : "Running";
        var picks = new List<Pick>();
        var used = new HashSet<int> { me };
        // pace rank inside each class
        var rank = new Dictionary<int, (int Rank, double T, Src S, int Of)>();
        foreach (var cl in s.ClassLeaderboard)
        {
            var ranked = cl.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator).Select(d => (d, p: PaceOf(d))).Where(x => x.p.T > 0).OrderBy(x => x.p.T).ToList();
            for (int i = 0; i < ranked.Count; i++) rank[ranked[i].d.CarIdx] = (i + 1, ranked[i].p.T, ranked[i].p.S, ranked.Count);
        }
        Pick Make(Driver d, string tag, string value, string reason) =>
            new(d.CarIdx, tag, d.ClassName, d.ClassColor, d.CarNumber, NameFormat.Broadcast(d), d.CarName, value, reason);

        // 1. pace setter: the streamer's class first, else the top class
        string? myClass = s.Player?.ClassName;
        var classes = s.ClassLeaderboard.Select(c => c.Name).ToList();
        if (myClass != null && classes.Remove(myClass)) classes.Insert(0, myClass);
        Driver? PaceSetter(string cls) => field.Where(d => d.ClassName == cls && !used.Contains(d.CarIdx) && rank.ContainsKey(d.CarIdx))
            .OrderBy(d => rank[d.CarIdx].T).FirstOrDefault();
        if (classes.Count > 0 && PaceSetter(classes[0]) is { } ps && rank[ps.CarIdx].Rank == 1)
        {
            var r = rank[ps.CarIdx];
            string where = ps.PositionInClass > 0 ? $" · {verb.ToLowerInvariant()} P{ps.PositionInClass}" : "";
            picks.Add(Make(ps, "Pace setter", LapTimeFormat.Lap(r.T), $"Fastest in {ClassLogo.DisplayName(ps.ClassName)} {SrcText(r.S)}{where}"));
            used.Add(ps.CarIdx);
        }
        // 2. the charger: furthest back compared with their pace (3+ places)
        var charger = field.Where(d => !used.Contains(d.CarIdx) && d.PositionInClass > 0 && rank.ContainsKey(d.CarIdx))
            .Select(d => (d, gap: d.PositionInClass - rank[d.CarIdx].Rank)).Where(x => x.gap >= 3)
            .OrderByDescending(x => x.gap).ThenBy(x => rank[x.d.CarIdx].T).FirstOrDefault();
        if (charger.d != null)
        {
            var r = rank[charger.d.CarIdx];
            picks.Add(Make(charger.d, "The charger", $"P{charger.d.PositionInClass}",
                $"{verb} P{charger.d.PositionInClass} with P{r.Rank} pace {SrcText(r.S)} ({LapTimeFormat.Lap(r.T)})"));
            used.Add(charger.d.CarIdx);
        }
        // 3. top rated
        var top = field.Where(d => !used.Contains(d.CarIdx) && d.IRating > 0).OrderByDescending(d => d.IRating).FirstOrDefault();
        if (top != null)
        {
            bool fieldTop = field.Where(d => d.CarIdx != me).Max(d => d.IRating) == top.IRating;
            picks.Add(Make(top, "Top rated", top.IRating.ToString("N0", CultureInfo.InvariantCulture) + " iR",
                (fieldTop ? "Highest iRating in the field" : $"Highest iRating left in {ClassLogo.DisplayName(top.ClassName)}")
                + (top.PositionInClass > 0 ? $" · {verb.ToLowerInvariant()} P{top.PositionInClass}" : "")));
            used.Add(top.CarIdx);
        }
        // fill: the other classes' pace setters, then the next fastest anywhere
        foreach (var cls in classes.Skip(1))
        {
            if (picks.Count >= 3) break;
            if (PaceSetter(cls) is { } p)
            {
                var r = rank[p.CarIdx];
                // the class's fastest may already be a pick (e.g. as Top rated): then say where this one's pace ranks
                picks.Add(Make(p, r.Rank == 1 ? "Pace setter" : "Quick", LapTimeFormat.Lap(r.T),
                    (r.Rank == 1 ? "Fastest" : $"P{r.Rank} pace") + $" in {ClassLogo.DisplayName(p.ClassName)} {SrcText(r.S)}"));
                used.Add(p.CarIdx);
            }
        }
        while (picks.Count < 3)
        {
            var next = field.Where(d => !used.Contains(d.CarIdx) && rank.ContainsKey(d.CarIdx)).OrderBy(d => rank[d.CarIdx].Rank).ThenBy(d => rank[d.CarIdx].T).FirstOrDefault();
            if (next == null) break;
            var r = rank[next.CarIdx];
            picks.Add(Make(next, "Quick", LapTimeFormat.Lap(r.T), $"P{r.Rank} pace in {ClassLogo.DisplayName(next.ClassName)} {SrcText(r.S)}"));
            used.Add(next.CarIdx);
        }
        return picks;
    }

    /// <summary>Where the pace came from, for the control app ("Garage 61: 214 drivers" / "qualifying and session laps").</summary>
    public static string SourceText => g61.Count > 0 ? $"Garage 61 pace for {g61.Count} driver/car pairs" : "No Garage 61 pace yet - using qualifying, session laps and iRating";
}
