using System.IO;
using System.Linq;
using System.Text.Json;
namespace ApexHUD.Native.Common;

/// <summary>
/// 2026-10-28: per-track broadcast content for every circuit beyond the six done by hand in code (Le Mans, Mexico,
/// Suzuka, Silverstone, Sebring, Monza): display name + flag, the track story and first-race year, corner-name fixes,
/// named straights, heritage lines and text themes. Read once from the embedded TrackData/tracks.json, which
/// tools/track_data_gen/build_tracks.py builds from the sourced research files. Keyed by iRacing's raw track id
/// (WeekendInfo.TrackName, as CrewChief's irTrackName lists it). No corner positions live here: a name is pinned to a
/// CrewChief landmark by that landmark's own start metre, so positions still come from CrewChief at runtime.
/// The hand-made tables always win; this only fills in where they have nothing.
/// </summary>
public static class TrackPacks
{
    public sealed record Pack(string Slug, string? Display, string? Iso2, string? Country, string? Story, string? FirstRace,
        Dictionary<string, (double Km, int Turns)> Layouts,
        Dictionary<string, List<(double StartM, string? Name)>> Names,
        Dictionary<string, Dictionary<string, string>> Straights,
        Dictionary<string, string> Heritage,
        Dictionary<string, string> Themes)
    {
        /// <summary>2026-10-28 (round 2): a hand-made track's other layouts (Silverstone 2011, Suzuka West, Monza
        /// Combined ...): its hand tables in code are checked first, the pack only fills in after them.</summary>
        public bool Hand { get; init; }
        /// <summary>Kerb paint for the story cards ("#RRGGBB", 2-3 colours); empty = the classic red / white.</summary>
        public IReadOnlyList<string> Kerbs { get; init; } = Array.Empty<string>();
    }

    static Dictionary<string, Pack>? byKey;
    static List<(string Prefix, Pack Pack)> byPrefix = new();

    static string Norm(string? raw) => string.Join(' ', (raw ?? "").Replace('_', ' ').Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    static string Bare(string name)
    {
        var n = name.Trim();
        return n.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? n[4..] : n;
    }

    static Dictionary<string, Pack> Load()
    {
        if (byKey != null) return byKey;
        var map = new Dictionary<string, Pack>(StringComparer.OrdinalIgnoreCase);
        var pre = new List<(string, Pack)>();
        try
        {
            using var s = typeof(TrackPacks).Assembly.GetManifestResourceStream("ApexHUD.Native.Common.TrackData.tracks.json");
            if (s != null)
            {
                using var doc = JsonDocument.Parse(s);
                foreach (var t in doc.RootElement.GetProperty("tracks").EnumerateArray())
                {
                    string? Str(string p) => t.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                    var layouts = new Dictionary<string, (double, int)>(StringComparer.OrdinalIgnoreCase);
                    foreach (var l in t.GetProperty("layouts").EnumerateObject())
                        layouts[l.Name] = (l.Value.GetProperty("km").GetDouble(), l.Value.TryGetProperty("turns", out var tu) ? tu.GetInt32() : 0);
                    var names = new Dictionary<string, List<(double, string?)>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in t.GetProperty("names").EnumerateObject())
                        names[k.Name] = k.Value.EnumerateArray().Select(x => (x[0].GetDouble(), x[1].ValueKind == JsonValueKind.String ? x[1].GetString() : null)).ToList();
                    var straights = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var k in t.GetProperty("straights").EnumerateObject())
                    {
                        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var x in k.Value.EnumerateArray()) m[x[0].GetString()!] = x[1].GetString()!;
                        straights[k.Name] = m;
                    }
                    var her = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in t.GetProperty("heritage").EnumerateObject()) her[Bare(h.Name)] = h.Value.GetString()!;
                    var th = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var h in t.GetProperty("themes").EnumerateObject()) th[Bare(h.Name)] = h.Value.GetString()!;
                    var kerbs = new List<string>();
                    if (t.TryGetProperty("kerbs", out var kb) && kb.ValueKind == JsonValueKind.Array)
                        foreach (var x in kb.EnumerateArray()) if (x.GetString() is { Length: 7 } hx && hx[0] == '#') kerbs.Add(hx);
                    var p = new Pack(Str("slug") ?? "", Str("display"), Str("iso2"), Str("country"), Str("story"), Str("firstRace"),
                        layouts, names, straights, her, th)
                    {
                        Hand = t.TryGetProperty("hand", out var hd) && hd.ValueKind == JsonValueKind.True,
                        Kerbs = kerbs,
                    };
                    foreach (var k in t.GetProperty("keys").EnumerateArray()) map[Norm(k.GetString())] = p;
                    foreach (var k in t.GetProperty("prefixes").EnumerateArray()) pre.Add((Norm(k.GetString()), p));
                }
            }
        }
        catch { }
        byPrefix = pre;
        return byKey = map;
    }

    /// <summary>Every iRacing track id that has a pack (preview / check tools).</summary>
    public static IReadOnlyCollection<string> Keys => Load().Keys;

    /// <summary>The pack for an iRacing track id, or null (the six hand-made tracks have none).</summary>
    public static Pack? For(string? trackRaw)
    {
        if (string.IsNullOrWhiteSpace(trackRaw)) return null;
        var k = Norm(trackRaw);
        if (Load().TryGetValue(k, out var p)) return p;
        foreach (var (pre, pk) in byPrefix) if (k.StartsWith(pre + " ", StringComparison.Ordinal) || k == pre) return pk;
        return null;
    }

    /// <summary>The circuit's own name for the CrewChief landmark starting at <paramref name="startM"/> on this track:
    /// (true, name) to rename / name it, (true, null) to drop it (a marker on a named straight), (false, _) to keep
    /// CrewChief's.</summary>
    public static (bool Hit, string? Name) LandmarkName(string trackKey, double startM)
    {
        if (!Load().TryGetValue(Norm(trackKey), out var p) || !p.Names.TryGetValue(Norm(trackKey), out var list)) return (false, null);
        foreach (var (a, n) in list) if (Math.Abs(a - startM) < 1.0) return (true, n);
        return (false, null);
    }

    /// <summary>The named straight that follows <paramref name="cornerBefore"/> ("the Kemmel Straight"), or null.</summary>
    public static string? StraightAfter(string? trackRaw, string? cornerBefore)
    {
        if (cornerBefore == null || string.IsNullOrWhiteSpace(trackRaw)) return null;
        if (!Load().TryGetValue(Norm(trackRaw), out var p) || !p.Straights.TryGetValue(Norm(trackRaw), out var m)) return null;
        return m.TryGetValue(cornerBefore, out var s) ? s : null;
    }

    /// <summary>The sourced heritage line for a corner / straight name on this track, or null.</summary>
    public static string? Heritage(string? trackRaw, string? name) =>
        name != null && For(trackRaw) is { } p && p.Heritage.TryGetValue(Bare(name), out var h) ? h : null;

    /// <summary>The text theme for a place on this track ("speed", "earth:water", ...), or null.</summary>
    public static string? Theme(string? trackRaw, string? name) =>
        name != null && For(trackRaw) is { } p && p.Themes.TryGetValue(Bare(name), out var h) ? h : null;

    /// <summary>2026-10-28 (round 2): true when this track id is one of a hand-made track's other layouts (its hand
    /// tables are checked before the pack).</summary>
    public static bool IsHand(string? trackRaw) => For(trackRaw)?.Hand == true;

    /// <summary>The circuit's kerb paint ("#RRGGBB" list) when the research gives it, else empty.</summary>
    public static IReadOnlyList<string> Kerbs(string? trackRaw) => For(trackRaw)?.Kerbs ?? Array.Empty<string>();

    /// <summary>Length (km) and turns of this exact layout when the research gives them (0 / 0 otherwise).</summary>
    public static (double Km, int Turns) Layout(string? trackRaw) =>
        For(trackRaw) is { } p && p.Layouts.TryGetValue(Norm(trackRaw), out var l) ? l : (0, 0);
}
