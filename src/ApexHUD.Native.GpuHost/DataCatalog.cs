using System.IO;
using System.Text.Json;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-05 (Angelo: "the create cards thing needs to allow us to hook up any data point that could be used in the
/// game"): everything a card can show, for the card builder's data picker -
///   built in   - the ready-made race numbers (places gained, gap, best lap ...)
///   car        - every detail Grandstand knows about a car ("Car.TeamName", "Car.CarBrand" ...)
///   telemetry  - every iRacing live value (Speed, RPM, FuelLevel, CarIdxLapDistPct ...)
///   session    - every value in iRacing's session info (track, weather, series, drivers' teams ...: "Session.&lt;path&gt;")
/// iRacing's lists are remembered from the last session (Documents\iRacing\Grandstand\data_catalog.json), so the picker
/// is full even when iRacing is closed.
/// </summary>
internal static class DataCatalog
{
    static readonly string CacheFile = Path.Combine(ControlState.Folder, "data_catalog.json");
    static readonly object gate = new();
    static Dictionary<string, string> sessionMap = new(StringComparer.Ordinal);
    static long sessionMapAt;

    static readonly (string Name, string Desc)[] BuiltIns =
    {
        ("PlacesGained", "Places gained in class since the start"), ("Position", "Position in class (P3)"), ("PositionOverall", "Overall position"),
        ("BestLap", "Best lap time"), ("LastLap", "Last lap time"), ("Gap", "Gap to the class leader"), ("Interval", "Gap to the car ahead"),
        ("Incidents", "Incident points"), ("iRating", "iRating"), ("LapsCompleted", "Laps completed"),
    };

    /// <summary>Session info by flattened path, for cards (render thread). Re-read at most every 5 s.</summary>
    public static string? Session(RaceStateService rs, string path)
    {
        long now = Environment.TickCount64;
        Dictionary<string, string> map;
        lock (gate)
        {
            if (now - sessionMapAt > 5000)
            {
                sessionMapAt = now;
                var m = new Dictionary<string, string>(StringComparer.Ordinal);
                try { foreach (var v in rs.ListSessionInfo(6000)) m[v.Name] = v.Value; } catch { }
                if (m.Count > 0) sessionMap = m;
            }
            map = sessionMap;
        }
        return map.TryGetValue(path, out var s) ? s : null;
    }

    sealed record Item(string Name, string Desc, string Unit, string Value);
    sealed record Cache(List<Item> Telemetry, List<Item> Session, DateTime SavedUtc);

    /// <summary>The whole catalog for the picker (control thread). Live values when iRacing runs; the remembered lists otherwise.</summary>
    public static object Build(RaceStateService rs, RaceSnapshot snap)
    {
        bool live = rs.Current.IsConnected;
        var who = snap.CameraTarget ?? snap.Player ?? snap.Leader;
        var builtIn = BuiltIns.Select(b => new Item(b.Name, b.Desc, "", "")).ToList();
        var car = CustomCards.CarFields.Select(p => new Item("Car." + p.Name, Words(p.Name), "", who != null ? CustomCards.CarField(who, p.Name) : "")).ToList();

        List<Item> tel = new(), ses = new();
        Cache? cached = null;
        if (live)
        {
            try { tel = rs.ListTelemetry().Select(t => new Item(t.Name, t.Desc, t.Unit, t.Value)).ToList(); } catch { }
            try { ses = rs.ListSessionInfo(6000).Select(s => new Item("Session." + s.Name, "", "", s.Value)).ToList(); } catch { }
            if (tel.Count > 0) Save(tel, ses);
        }
        else cached = Load();
        if (!live && cached != null)
        {
            tel = cached.Telemetry.Select(t => t with { Value = "" }).ToList();
            ses = cached.Session;
        }
        return new
        {
            live,
            remembered = !live && cached != null ? cached.SavedUtc.ToLocalTime().ToString("d MMM HH:mm") : null,
            car = who != null ? $"#{who.CarNumber} {NameFormat.Broadcast(who)}" : null,
            groups = new object[]
            {
                new { key = "builtin", title = "Race numbers", hint = "Ready-made, formatted for TV", items = builtIn },
                new { key = "ext", title = "Garage 61 & league", hint = "From the Integrations page: reference laps and championship standings",
                      items = Integrations.DataFields.Select(f => new Item(f.Name, f.Desc, "", "")).ToList() },
                new { key = "car", title = "Car details", hint = "Anything Grandstand knows about the card's car", items = car },
                new { key = "tel", title = "Live telemetry", hint = "Every live value iRacing sends (CarIdx… values are read for the card's car)", items = tel },
                new { key = "session", title = "Session info", hint = "Track, weather, series, teams… ({car} in a path = the card's car)", items = ses },
            },
        };
    }

    static long lastRememberTicks = long.MinValue / 2;
    /// <summary>Called every few seconds by the host: while iRacing runs, keep the remembered lists fresh (every 15 minutes).</summary>
    public static void RememberIfLive(RaceStateService rs)
    {
        long now = Environment.TickCount64;
        if (now - lastRememberTicks < 15 * 60_000 || !rs.Current.IsConnected) return;
        lastRememberTicks = now;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var tel = rs.ListTelemetry().Select(t => new Item(t.Name, t.Desc, t.Unit, t.Value)).ToList();
                var ses = rs.ListSessionInfo(6000).Select(s => new Item("Session." + s.Name, "", "", s.Value)).ToList();
                if (tel.Count > 0) Save(tel, ses);
            }
            catch { }
        });
    }

    static string Words(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++) { if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' '); sb.Append(s[i]); }
        return sb.ToString();
    }

    static void Save(List<Item> tel, List<Item> ses)
    {
        try
        {
            // session values are kept only for paths that aren't per-driver rows (those change every race)
            var keep = ses.Where(s => !s.Name.Contains("Drivers[", StringComparison.Ordinal) || s.Name.Contains("Drivers[0]", StringComparison.Ordinal)).Take(2500).ToList();
            var c = new Cache(tel.Select(t => t with { Value = "" }).ToList(), keep, DateTime.UtcNow);
            Directory.CreateDirectory(ControlState.Folder);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(c));
        }
        catch { }
    }

    static Cache? Load()
    {
        try { return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) : null; }
        catch { return null; }
    }
}
