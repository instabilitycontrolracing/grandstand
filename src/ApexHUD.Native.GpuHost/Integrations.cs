using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-05 (Angelo: "do all the other ones"): Grandstand's outside connections, set on the Integrations page.
///   Garage 61   - reference laps for this car + track (your best, the fastest you can see), as card data "G61.*"
///   Discord     - a webhook: "racing now" at the green flag, the results at the chequered flag (real races only)
///   League      - championship standings from a published Google Sheet (CSV link) or a CSV file, as card data
///                 "League.*" (Top 5 template + League.Points = the championship top 5)
///   Stream Deck - command line triggers: Grandstand.exe --do onair | prerace | story:RaceSoFar | ... (see Do())
/// Settings: Documents\iRacing\Grandstand\integrations.json. The Garage 61 key: %LOCALAPPDATA%\Grandstand\garage61_token.txt
/// (the Race Engineer's key is used when Grandstand has none). Nothing here ever runs for the sample race.
/// </summary>
internal static class Integrations
{
    public sealed class Settings
    {
        public string DiscordWebhook { get; set; } = "";
        public bool DiscordOnGreen { get; set; } = true;
        public bool DiscordOnFlag { get; set; } = true;
        public string DiscordStreamLink { get; set; } = "";
        public string LeagueName { get; set; } = "";
        public string LeagueSource { get; set; } = "";   // https://... CSV link, or a file path
        public bool Garage61 { get; set; } = true;
        // 2026-11-06: YouTube upload of the highlight reel, its Discord post, chat commands, Hue lights
        public bool YouTubeAuto { get; set; } = true;
        public string YouTubePrivacy { get; set; } = "unlisted";
        public bool DiscordOnHighlights { get; set; } = true;
        public bool ChatOn { get; set; } = true;
        public bool ChatCards { get; set; }
        public bool HueOn { get; set; }
        public string HueBridge { get; set; } = "";
        public string HueRoom { get; set; } = "";
        public int HueBrightness { get; set; } = 254;
        // 2026-11-06: viewer bets (Bets.cs)
        public bool BetsOn { get; set; }
        public bool BetsOwnPoints { get; set; } = true;
        public bool BetsTwitch { get; set; }
        public int BetsEveryMinutes { get; set; } = 6;
        public int BetsOpenSeconds { get; set; } = 45;
        public int BetsStartPoints { get; set; } = 1000;
        public int BetsSbPort { get; set; } = 7474;
        // 2026-11-06: vertical clips (VerticalClip.cs) + drivers to watch
        public bool ClipOn { get; set; } = true;
        public int ClipSeconds { get; set; } = 30;
        public string ClipHandle { get; set; } = "";
        public bool ClipKeepBuffer { get; set; } = true;
        public bool ClipUpload { get; set; }
        public bool ClipChat { get; set; }
        public bool ClipKeepSource { get; set; }
        public bool DriversToWatch { get; set; } = true;
    }

    /// <summary>The highlights video is up: post its link (YouTubeUpload, only for videos people can watch).</summary>
    public static Task<string> PostHighlights(string title, string url) => current.DiscordWebhook.Length == 0 ? Task.FromResult("") : PostDiscord(new JsonObject
    {
        ["username"] = "Grandstand",
        ["content"] = $"**Race highlights**: {title}\n{url}",
    });

    static readonly string FilePath = Path.Combine(ControlState.Folder, "integrations.json");
    static readonly string TokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "garage61_token.txt");
    static readonly string EngineerTokenFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaceEngineer", "garage61_token.txt");
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static Settings current = Load();
    public static Settings Current => current;
    static Action<string> log = _ => { };

    public static void Init(Action<string> logger)
    {
        log = logger;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Grandstand/0.1 (+https://github.com/instabilitycontrolracing/grandstand)");
        ApexHUD.Native.Overlays.CustomCards.ReadExternal = Value;
        ApexHUD.Native.Overlays.PreRaceCards.ShowWatch = current.DriversToWatch;   // 2026-11-06
        _ = Task.Run(ReloadLeague);
    }

    static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new(); } catch { }
        return new();
    }

    public static void Save(Settings s)
    {
        string oldLeague = current.LeagueSource;
        current = s;
        try { Directory.CreateDirectory(ControlState.Folder); File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Json)); } catch { }
        if (s.LeagueSource != oldLeague) _ = Task.Run(ReloadLeague);
        ApexHUD.Native.Overlays.PreRaceCards.ShowWatch = s.DriversToWatch;
    }

    // =========================================================== card data
    /// <summary>"G61.*" and "League.*" values for a card's car (render thread: only reads what the background work fetched).</summary>
    static string? Value(string field, Driver d)
    {
        if (field.StartsWith("G61.", StringComparison.Ordinal))
        {
            var r = g61Ref;
            if (r == null) return "-";
            return field[4..] switch
            {
                "FastestLap" => r.Fastest > 0 ? LapTimeFormat.Lap(r.Fastest) : "-",
                "FastestDriver" => r.FastestDriver.Length > 0 ? r.FastestDriver.ToUpperInvariant() : "-",
                "MyBest" => r.MyBest > 0 ? LapTimeFormat.Lap(r.MyBest) : "-",
                "GapToFastest" => r.MyBest > 0 && r.Fastest > 0 ? "+" + (r.MyBest - r.Fastest).ToString("0.000", CultureInfo.InvariantCulture) : "-",
                "DriversSeen" => r.Drivers.ToString(CultureInfo.InvariantCulture),
                _ => "-",
            };
        }
        if (field.StartsWith("League.", StringComparison.Ordinal))
        {
            var row = LeagueRowFor(d);
            if (row == null) return "-";
            return field[7..] switch
            {
                "Position" => "P" + row.Pos.ToString(CultureInfo.InvariantCulture),
                "Points" => row.Points,
                "GapToLeader" => row.GapToLeader,
                "Name" => row.Name.ToUpperInvariant(),
                _ => "-",
            };
        }
        return null;
    }

    public static IReadOnlyList<(string Name, string Desc)> DataFields => new[]
    {
        ("G61.FastestLap", "Garage 61: the fastest lap you can see for this car and track"), ("G61.FastestDriver", "Garage 61: who set it"),
        ("G61.MyBest", "Garage 61: your best lap here"), ("G61.GapToFastest", "Garage 61: your best compared to the fastest"),
        ("G61.DriversSeen", "Garage 61: drivers with laps here"),
        ("League.Position", "League: championship position of the card's car"), ("League.Points", "League: championship points (Top 5 template = the championship top 5)"),
        ("League.GapToLeader", "League: points behind the championship leader"), ("League.Name", "League: the name in the standings"),
    };

    // =========================================================== league standings
    public sealed record LeagueRow(int Pos, string Name, string Number, string Class, string Points, string GapToLeader);
    static volatile LeagueRow[] league = Array.Empty<LeagueRow>();
    public static IReadOnlyList<LeagueRow> League => league;
    public static string LeagueStatus { get; private set; } = "Not set up";

    public static async Task ReloadLeague()
    {
        string src = current.LeagueSource.Trim();
        if (src.Length == 0) { league = Array.Empty<LeagueRow>(); LeagueStatus = "Not set up"; return; }
        try
        {
            string text = src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? await http.GetStringAsync(CsvUrl(src)) : await File.ReadAllTextAsync(src);
            var rows = ParseStandings(text);
            league = rows.ToArray();
            LeagueStatus = rows.Count > 0 ? $"{rows.Count} drivers loaded ({DateTime.Now:HH:mm})" : "The file has no rows Grandstand understands (needs Driver and Points columns)";
            log("league standings: " + LeagueStatus);
        }
        catch (Exception ex) { LeagueStatus = "Couldn't read it: " + ex.Message; log("league standings: " + LeagueStatus); }
    }

    /// <summary>A normal Google Sheets link becomes its CSV export link.</summary>
    static string CsvUrl(string url)
    {
        if (url.Contains("docs.google.com/spreadsheets", StringComparison.OrdinalIgnoreCase) && !url.Contains("output=csv") && !url.Contains("format=csv"))
        {
            int d = url.IndexOf("/d/", StringComparison.Ordinal);
            if (d > 0)
            {
                string id = url[(d + 3)..].Split('/', '?', '#')[0];
                string gid = "0";
                int g = url.IndexOf("gid=", StringComparison.Ordinal);
                if (g > 0) gid = new string(url[(g + 4)..].TakeWhile(char.IsDigit).ToArray());
                return $"https://docs.google.com/spreadsheets/d/{id}/export?format=csv&gid={gid}";
            }
        }
        return url;
    }

    static List<LeagueRow> ParseStandings(string text)
    {
        var lines = text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Select(SplitCsv).ToList();
        var outp = new List<LeagueRow>();
        if (lines.Count < 2) return outp;
        int hi = lines.FindIndex(l => l.Any(c => Has(c, "driver", "name")) && l.Any(c => Has(c, "point", "pts")));
        if (hi < 0) return outp;
        var h = lines[hi];
        int Col(params string[] k) => h.FindIndex(c => Has(c, k));
        int cPos = Col("pos", "rank", "place"), cName = Col("driver", "name"), cPts = Col("point", "pts"), cNum = Col("#", "car", "number", "no."), cCls = Col("class");
        double lead = double.NaN;
        foreach (var l in lines.Skip(hi + 1))
        {
            string Get(int i) => i >= 0 && i < l.Count ? l[i].Trim() : "";
            string name = Get(cName), pts = Get(cPts);
            if (name.Length == 0) continue;
            int pos = int.TryParse(new string(Get(cPos).Where(char.IsDigit).ToArray()), out int p) ? p : outp.Count + 1;
            double pv = double.TryParse(pts, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : double.NaN;
            if (double.IsNaN(lead) && !double.IsNaN(pv)) lead = pv;
            string gap = double.IsNaN(pv) || double.IsNaN(lead) ? "" : pv >= lead ? "LEADER" : "-" + (lead - pv).ToString("0.#", CultureInfo.InvariantCulture);
            outp.Add(new LeagueRow(pos, name, Get(cNum).TrimStart('#'), Get(cCls), pts, gap));
        }
        return outp.OrderBy(r => r.Pos).ToList();
    }

    static bool Has(string cell, params string[] keys) { var c = cell.Trim().ToLowerInvariant(); return keys.Any(k => c == k || c.Contains(k)); }

    static List<string> SplitCsv(string line)
    {
        var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (q) { if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else if (ch == '"') q = false; else sb.Append(ch); }
            else if (ch == '"') q = true;
            else if (ch == ',') { res.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        res.Add(sb.ToString());
        return res;
    }

    /// <summary>The standings row for a car: by car number first, then by the driver's name.</summary>
    public static LeagueRow? LeagueRowFor(Driver d)
    {
        var rows = league;
        if (rows.Length == 0) return null;
        string num = (d.CarNumber ?? "").TrimStart('#');
        var hit = num.Length > 0 ? rows.FirstOrDefault(r => r.Number.Length > 0 && r.Number == num) : null;
        if (hit != null) return hit;
        string full = (d.Name ?? "").Trim();
        hit = rows.FirstOrDefault(r => r.Name.Equals(full, StringComparison.OrdinalIgnoreCase));
        if (hit != null) return hit;
        string last = full.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        return last.Length > 2 ? rows.FirstOrDefault(r => r.Name.Contains(last, StringComparison.OrdinalIgnoreCase)) : null;
    }

    // =========================================================== Garage 61
    sealed record G61Ref(double Fastest, string FastestDriver, double MyBest, int Drivers, string Key, DateTime At);
    static volatile G61Ref? g61Ref;
    public static string G61Status { get; private set; } = "";
    public static bool G61DrivingData { get; private set; }
    static string? g61MeId; static long g61NextTicks; static int g61Busy;

    public static string? G61Token()
    {
        foreach (var p in new[] { TokenFile, EngineerTokenFile })
            try { if (File.Exists(p)) { var t = File.ReadAllText(p).Trim(); if (t.Length > 10) return t; } } catch { }
        return null;
    }
    public static bool G61OwnToken => File.Exists(TokenFile);

    public static void SaveG61Token(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenFile)!);
        if (string.IsNullOrWhiteSpace(token)) { try { File.Delete(TokenFile); } catch { } }
        else File.WriteAllText(TokenFile, token.Trim());
        g61MeId = null; g61Ref = null; g61NextTicks = 0; G61Status = ""; G61DrivingData = false;
    }

    static async Task<JsonNode?> G61Get(string path)
    {
        var tok = G61Token() ?? throw new InvalidOperationException("No Garage 61 key yet");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://garage61.net/api/v1" + path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
        using var res = await http.SendAsync(req);
        string body = await res.Content.ReadAsStringAsync();
        if ((int)res.StatusCode == 401) throw new InvalidOperationException("Garage 61 didn't accept the key - make a new one on garage61.net/developer");
        if ((int)res.StatusCode == 403) throw new InvalidOperationException("The key is missing the driving data permission - make a NEW key on garage61.net/developer (keys made before the permission was added don't have it)");
        if ((int)res.StatusCode == 429) throw new InvalidOperationException("Garage 61 asked us to slow down - trying again later");
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"Garage 61 answered {(int)res.StatusCode}");
        return JsonNode.Parse(body);
    }

    public static async Task<object> TestG61()
    {
        try
        {
            var me = await G61Get("/me");
            g61MeId = me?["id"]?.ToString();
            var perms = me?["apiPermissions"]?.AsArray().Select(x => x?.ToString() ?? "").ToList() ?? new();
            G61DrivingData = perms.Contains("driving_data");
            string name = string.Join(" ", new[] { me?["firstName"]?.ToString(), me?["lastName"]?.ToString() }.Where(s => !string.IsNullOrEmpty(s)));
            G61Status = $"Connected as {name} ({me?["subscriptionPlan"]?.ToString() ?? "free"} plan)." + (G61DrivingData ? " Lap data: yes." : " Lap data: NO - this key was made before lap access was switched on. Make a new key on garage61.net/developer and paste it here.");
            g61NextTicks = 0;
            return new { ok = true, drivingData = G61DrivingData, status = G61Status };
        }
        catch (Exception ex) { G61Status = ex.Message; return new { ok = false, drivingData = false, status = ex.Message }; }
    }

    /// <summary>Host loop: while a real session runs, fetch this car + track's reference laps (every 15 minutes at most).</summary>
    public static void TickG61(RaceStateService rs)
    {
        if (!current.Garage61 || !rs.Current.IsConnected || G61Token() == null) return;
        long now = Environment.TickCount64;
        if (now < g61NextTicks || Interlocked.Exchange(ref g61Busy, 1) == 1) return;
        g61NextTicks = now + 15 * 60_000;
        _ = Task.Run(async () =>
        {
            try
            {
                string? trackId = DataCatalog.Session(rs, "WeekendInfo.TrackID");
                string? myIdx = DataCatalog.Session(rs, "DriverInfo.DriverCarIdx");
                string? carId = null;
                for (int i = 0; i < 80 && carId == null; i++)
                    if (DataCatalog.Session(rs, $"DriverInfo.Drivers[{i}].CarIdx") == myIdx) carId = DataCatalog.Session(rs, $"DriverInfo.Drivers[{i}].CarID");
                if (trackId == null || carId == null) { g61NextTicks = Environment.TickCount64 + 60_000; return; }
                if (g61MeId == null) await TestG61();
                if (!G61DrivingData) return;
                string? g61Car = await G61Match("/cars", carId), g61Track = await G61Match("/tracks", trackId);
                if (g61Car == null || g61Track == null) { G61Status = "Garage 61 doesn't know this car or track yet"; return; }
                var laps = await G61Get($"/laps?cars={g61Car}&tracks={g61Track}&group=driver&limit=50");
                double fastest = 0, mine = 0; string who = ""; int n = 0;
                foreach (var L in laps?["items"]?.AsArray() ?? new JsonArray())
                {
                    double t = L?["lapTime"]?.GetValue<double>() ?? 0; if (t <= 0) continue;
                    n++;
                    var drv = L?["driver"];
                    if (fastest == 0 || t < fastest) { fastest = t; who = string.Join(" ", new[] { drv?["firstName"]?.ToString(), drv?["lastName"]?.ToString() }.Where(s => !string.IsNullOrEmpty(s))); }
                    if (drv?["id"]?.ToString() == g61MeId && (mine == 0 || t < mine)) mine = t;
                }
                g61Ref = new G61Ref(fastest, who, mine, n, g61Car + "/" + g61Track, DateTime.Now);
                G61Status = n > 0 ? $"Reference laps for this car and track: fastest {LapTimeFormat.Lap(fastest)} ({who}), {n} drivers ({DateTime.Now:HH:mm})" : "No laps you can see for this car and track yet";
                log("garage 61: " + G61Status);
            }
            catch (Exception ex) { G61Status = ex.Message; log("garage 61: " + ex.Message); }
            finally { Interlocked.Exchange(ref g61Busy, 0); }
        });
    }

    // 2026-11-06 (Drivers to watch): every car model in the field -> each driver's best public Garage 61 lap at this
    // track, for DriversToWatch. Once per new field (track + car models), then every 30 minutes at most.
    static string g61FieldSig = ""; static long g61FieldNext; static int g61FieldBusy;
    public static string G61FieldStatus { get; private set; } = "";
    public static void TickG61Field(RaceStateService rs)
    {
        var s = rs.Current;
        if (!current.Garage61 || !s.IsConnected || G61Token() == null) return;
        string? trackId = DataCatalog.Session(rs, "WeekendInfo.TrackID");
        var carIds = s.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsPaceCar && !d.IsSpectator && d.CarId > 0).Select(d => d.CarId).Distinct().OrderBy(x => x).Take(8).ToList();
        if (trackId == null || carIds.Count == 0) return;
        string sig = trackId + ":" + string.Join(",", carIds);
        long now = Environment.TickCount64;
        if (sig == g61FieldSig && now < g61FieldNext) return;
        if (Interlocked.Exchange(ref g61FieldBusy, 1) == 1) return;
        g61FieldSig = sig; g61FieldNext = now + 30 * 60_000;
        _ = Task.Run(async () =>
        {
            try
            {
                if (g61MeId == null) await TestG61();
                if (!G61DrivingData) { G61FieldStatus = "Garage 61 key has no lap access"; return; }
                string? g61Track = await G61Match("/tracks", trackId);
                if (g61Track == null) { G61FieldStatus = "Garage 61 doesn't know this track yet"; return; }
                int pairs = 0, cars = 0;
                foreach (int carId in carIds)
                {
                    string? g61Car = await G61Match("/cars", carId.ToString(CultureInfo.InvariantCulture));
                    if (g61Car == null) continue;
                    var laps = await G61Get($"/laps?cars={g61Car}&tracks={g61Track}&group=driver&limit=1000");
                    var list = new List<(string, double)>();
                    foreach (var L in laps?["items"]?.AsArray() ?? new JsonArray())
                    {
                        double t = L?["lapTime"]?.GetValue<double>() ?? 0;
                        var drv = L?["driver"];
                        string nm = string.Join(" ", new[] { drv?["firstName"]?.ToString(), drv?["lastName"]?.ToString() }.Where(x => !string.IsNullOrEmpty(x)));
                        if (t > 0 && nm.Length > 0) list.Add((nm, t));
                    }
                    ApexHUD.Native.Overlays.DriversToWatch.SetPace(trackId, carId, list);
                    pairs += list.Count; cars++;
                }
                // how many of tonight's drivers we found
                var field = rs.Current.ClassLeaderboard.SelectMany(c => c.Drivers).Where(d => !d.IsPaceCar && !d.IsSpectator).ToList();
                G61FieldStatus = $"Drivers to watch: Garage 61 laps for {cars} car{(cars == 1 ? "" : "s")} here ({pairs} drivers in all) ({DateTime.Now:HH:mm})";
                log("garage 61: " + G61FieldStatus + $" - field of {field.Count}");
            }
            catch (Exception ex) { G61FieldStatus = ex.Message; log("garage 61 (drivers to watch): " + ex.Message); g61FieldNext = Environment.TickCount64 + 5 * 60_000; }
            finally { Interlocked.Exchange(ref g61FieldBusy, 0); }
        });
    }

    static readonly Dictionary<string, JsonArray> g61Lists = new();
    static async Task<string?> G61Match(string list, string iracingId)
    {
        if (!g61Lists.TryGetValue(list, out var items)) { items = (await G61Get(list))?["items"]?.AsArray() ?? new JsonArray(); g61Lists[list] = items; }
        foreach (var x in items)
            if ((x?["platform"]?.ToString() ?? "iracing") == "iracing" && x?["platform_id"]?.ToString() == iracingId) return x?["id"]?.ToString();
        return null;
    }

    // =========================================================== Discord
    static RacePhase lastPhase = RacePhase.Unknown; static string lastSessionKey = ""; static bool greenPosted, flagPosted;

    /// <summary>Host loop (real races only): post at the green flag and at the chequered flag.</summary>
    public static void TickDiscord(RaceStateService rs)
    {
        var s = rs.Current;
        if (!s.IsConnected) { lastPhase = RacePhase.Unknown; return; }
        if (s.SessionKey != lastSessionKey) { lastSessionKey = s.SessionKey; greenPosted = flagPosted = false; }
        var ph = s.Phase;
        bool isRace = (s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
        if (current.DiscordWebhook.Length > 0 && isRace && !ReplayStateIsReplay())
        {
            if (current.DiscordOnGreen && !greenPosted && lastPhase == RacePhase.PreRace && ph == RacePhase.Broadcast) { greenPosted = true; _ = PostDiscord(GreenMessage(s)); }
            if (current.DiscordOnFlag && !flagPosted && lastPhase == RacePhase.Broadcast && ph == RacePhase.PostRace) { flagPosted = true; _ = PostDiscord(ResultsMessage(s)); }
        }
        lastPhase = ph;
    }

    static bool ReplayStateIsReplay() { try { return ReplayState.IsReplayFile; } catch { return false; } }

    static JsonObject GreenMessage(RaceSnapshot s)
    {
        string link = current.DiscordStreamLink.Trim();
        var e = new JsonObject
        {
            ["title"] = $"Green flag at {s.TrackDisplayName}",
            ["description"] = (current.LeagueName.Length > 0 ? current.LeagueName + " - " : "") + $"{CountCars(s)} cars, racing now." + (link.Length > 0 ? $"\nWatch live: {link}" : ""),
            ["color"] = 0x19C46B,
        };
        return new JsonObject { ["username"] = "Grandstand", ["embeds"] = new JsonArray(e) };
    }

    static JsonObject ResultsMessage(RaceSnapshot s)
    {
        var fields = new JsonArray();
        foreach (var cls in s.ClassLeaderboard)
        {
            var top = cls.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator && d.PositionInClass > 0).OrderBy(d => d.PositionInClass).Take(3).Select(d => $"P{d.PositionInClass}  #{d.CarNumber}  {d.Name}");
            fields.Add(new JsonObject { ["name"] = cls.Name, ["value"] = string.Join("\n", top), ["inline"] = true });
        }
        if (s.Player is { } me) fields.Add(new JsonObject { ["name"] = "Our car", ["value"] = $"#{me.CarNumber} {me.Name}: P{me.PositionInClass} in {me.ClassName}", ["inline"] = false });
        var e = new JsonObject
        {
            ["title"] = $"Chequered flag at {s.TrackDisplayName}",
            ["description"] = current.LeagueName.Length > 0 ? current.LeagueName + " - provisional results" : "Provisional results",
            ["color"] = 0xC5A572,
            ["fields"] = fields,
            ["footer"] = new JsonObject { ["text"] = "Grandstand broadcast graphics" },
        };
        return new JsonObject { ["username"] = "Grandstand", ["embeds"] = new JsonArray(e) };
    }

    static int CountCars(RaceSnapshot s) => s.ClassLeaderboard.Sum(c => c.Drivers.Count(d => !d.IsPaceCar && !d.IsSpectator));

    public static async Task<string> PostDiscord(JsonObject msg)
    {
        string url = current.DiscordWebhook.Trim();
        if (!url.StartsWith("https://discord.com/api/webhooks/", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://discordapp.com/api/webhooks/", StringComparison.OrdinalIgnoreCase))
            return "That isn't a Discord webhook link (it starts with https://discord.com/api/webhooks/)";
        try
        {
            using var res = await http.PostAsync(url, new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json"));
            string r = res.IsSuccessStatusCode ? "Posted to Discord" : $"Discord answered {(int)res.StatusCode}";
            log("discord: " + r);
            return r;
        }
        catch (Exception ex) { log("discord: " + ex.Message); return "Couldn't reach Discord: " + ex.Message; }
    }

    public static Task<string> TestDiscord() => PostDiscord(new JsonObject
    {
        ["username"] = "Grandstand",
        ["embeds"] = new JsonArray(new JsonObject { ["title"] = "Grandstand is connected", ["description"] = "Race starts and results will be posted here.", ["color"] = 0xC5A572 }),
    });

    // =========================================================== status of everything
    public static object Status(RaceStateService rs)
    {
        bool Proc(params string[] names) { try { return names.Any(n => System.Diagnostics.Process.GetProcessesByName(n).Length > 0); } catch { return false; } }
        bool tp = false;
        try
        {
            foreach (var root in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
                foreach (var key in new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
                    using (var k = root.OpenSubKey(key))
                        if (k != null) foreach (var sub in k.GetSubKeyNames()) using (var s = k.OpenSubKey(sub)) if ((s?.GetValue("DisplayName") as string ?? "").Contains("Trading Paints")) tp = true;
        }
        catch { }
        bool cc = false; try { using var m = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(@"Local\CrewChiefV4"); cc = true; } catch { }
        bool simhubProp = false;
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            var t = tcp.ConnectAsync("127.0.0.1", 18082);
            simhubProp = t.Wait(150) && tcp.Connected;
            _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);   // a late failure is expected - observe it
        }
        catch { }
        var c = ControlState.Current;
        return new
        {
            iracing = new { connected = rs.Current.IsConnected, running = Proc("iRacingSim64DX11", "iRacingSim64") },
            tradingPaints = new { installed = tp, running = Proc("TradingPaints", "Trading Paints") },
            obs = new { running = Proc("obs64", "obs32"), spout = c.Spout, director = c.ObsDirector, highlights = c.HighlightsObs },
            streamerbot = new { running = Proc("Streamer.bot"), viewerCards = c.Viewer.Cards },
            engineer = new { running = Proc("pythonw", "RaceEngineer") },
            crewChief = new { running = Proc("CrewChiefV4"), sharedMemory = cc, on = c.CrewChiefRadio, spotter = c.CrewChiefSpotter },
            simhub = new { running = Proc("SimHubWPF"), propertyServer = simhubProp },
            garage61 = new { on = current.Garage61, key = G61Token() != null, ownKey = G61OwnToken, drivingData = G61DrivingData, status = G61Status },
            discord = new { webhook = current.DiscordWebhook.Length > 0, onGreen = current.DiscordOnGreen, onFlag = current.DiscordOnFlag, streamLink = current.DiscordStreamLink },
            league = new { name = current.LeagueName, source = current.LeagueSource, status = LeagueStatus, rows = league.Take(10).Select(r => new { r.Pos, r.Name, r.Number, r.Points }) },
            youtube = new { client = YouTubeUpload.HasClient, signedIn = YouTubeUpload.SignedIn, busy = YouTubeUpload.Busy, status = YouTubeUpload.Status, last = YouTubeUpload.LastVideo },
            hue = new { paired = HueLights.Paired, status = HueLights.Status },
            chat = ChatCommands.List.Select(c => new { cmd = c.Cmd, what = c.What }),
            bets = Bets.Describe(),
            betTop = Bets.Leaderboard(),
            clip = VerticalClip.StatusObject(),
            watch = new { source = ApexHUD.Native.Overlays.DriversToWatch.SourceText, g61 = G61FieldStatus },
            exe = Environment.ProcessPath ?? "",
        };
    }

    // =========================================================== Stream Deck / button box: Grandstand.exe --do <command>
    /// <summary>Run from the command line by a Stream Deck "Open" action: sends one command to the running Grandstand and exits.</summary>
    public static int Do(string cmd)
    {
        string c = cmd.Trim();
        if (c.Equals("window", StringComparison.OrdinalIgnoreCase) || c.Equals("liveview", StringComparison.OrdinalIgnoreCase))
        {
            // bring the Grandstand window to the front (the running copy waits on this signal)
            try { using var ev = EventWaitHandle.OpenExisting(@"Local\Grandstand.ShowWindow"); ev.Set(); return 0; } catch { return 1; }
        }
        (string Method, string Path) call = c.ToLowerInvariant() switch
        {
            "onair" => ("POST", "/api/do/onair"),
            "onair-on" => ("POST", "/api/do/onair?to=1"),
            "onair-off" => ("POST", "/api/do/onair?to=0"),
            "highlight" or "flag" => ("POST", "/api/highlight"),
            "mark" => ("POST", "/api/desk/mark"),
            "replay" => ("POST", "/api/desk/replay?i=" + int.MaxValue),   // replaced below: the newest mark
            "replay-slow" => ("POST", "/api/desk/replay?slow=1&i=" + int.MaxValue),
            "live" => ("POST", "/api/desk/live"),
            "champ" or "pits" or "sectors" or "clear" or "watch" => ("POST", "/api/desk/show?what=" + c.ToLowerInvariant()),
            "clip" => ("POST", "/api/clip"),   // 2026-11-06: vertical clip of the last N seconds
            "results" or "outro" => ("POST", "/api/test?card=outro"),
            "racecontrol" => ("POST", "/api/test?card=rcbar"),
            _ when c.StartsWith("story:", StringComparison.OrdinalIgnoreCase) => ("POST", "/api/test?card=" + Uri.EscapeDataString(c[6..])),
            _ when c.StartsWith("card:", StringComparison.OrdinalIgnoreCase) => ("POST", "/api/cards/" + Uri.EscapeDataString(c[5..]) + "/play"),
            _ when c.StartsWith("toggle:", StringComparison.OrdinalIgnoreCase) => ("POST", "/api/do/toggle?id=" + Uri.EscapeDataString(c[7..])),
            _ => ("", ""),
        };
        if (call.Path.Length == 0) return 2;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var req = new HttpRequestMessage(new HttpMethod(call.Method), ControlServer.Url.TrimEnd('/') + call.Path);
            req.Headers.Add("X-Grandstand", "1");
            using var res = client.Send(req);
            return res.IsSuccessStatusCode ? 0 : 1;
        }
        catch { return 1; }
    }
}
