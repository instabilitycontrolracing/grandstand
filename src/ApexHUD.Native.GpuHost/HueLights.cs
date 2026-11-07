using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06: Philips Hue room lights follow the race flags - green at the start, amber under a full-course caution,
/// red for a red flag, white for the last lap, chequered (white flashes) at the finish, a blue blink when you're being
/// lapped. Between flags the lights go back to how they were. Uses the Hue bridge on the home network (its local API):
/// "Find my bridge" + press the bridge's round button + Pair. The bridge's key stays on this PC
/// (%LOCALAPPDATA%\Grandstand\hue_user.txt). Never during the sample race.
/// </summary>
internal static class HueLights
{
    static readonly string UserFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "hue_user.txt");
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
    static Action<string> log = _ => { };
    public static string Status { get; private set; } = "";
    static Thread? thread;

    public static void Init(RaceStateService rs, Action<string> logger)
    {
        log = logger;
        thread = new Thread(() => Loop(rs)) { IsBackground = true, Name = "Grandstand-Hue", Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    static string? User() { try { return File.Exists(UserFile) ? File.ReadAllText(UserFile).Trim() : null; } catch { return null; } }
    public static bool Paired => User() != null;
    static string Bridge => Integrations.Current.HueBridge.Trim();

    public static async Task<object> Discover()
    {
        try
        {
            var arr = JsonNode.Parse(await http.GetStringAsync("https://discovery.meethue.com/"))?.AsArray() ?? new JsonArray();
            var ips = arr.Select(x => x?["internalipaddress"]?.ToString() ?? "").Where(x => x.Length > 0).ToList();
            Status = ips.Count == 0 ? "No Hue bridge found on your network. Type its address (Hue app > Settings > Bridges > i) instead." : $"Found {ips.Count} bridge{(ips.Count > 1 ? "s" : "")}: press the round button on top of it, then Pair.";
            return new { ips, status = Status };
        }
        catch (Exception ex) { Status = "Couldn't look for a bridge: " + ex.Message; return new { ips = Array.Empty<string>(), status = Status }; }
    }

    public static async Task<object> Pair()
    {
        if (Bridge.Length == 0) { Status = "Find or type the bridge's address first."; return new { ok = false, status = Status }; }
        try
        {
            using var res = await http.PostAsync($"http://{Bridge}/api", new StringContent("{\"devicetype\":\"grandstand#pc\"}", Encoding.UTF8, "application/json"));
            var first = JsonNode.Parse(await res.Content.ReadAsStringAsync())?.AsArray().FirstOrDefault();
            string? user = first?["success"]?["username"]?.ToString();
            if (user == null)
            {
                Status = first?["error"]?["type"]?.GetValue<int>() == 101 ? "Press the round button on top of the bridge, then press Pair within 30 seconds." : "The bridge said: " + (first?["error"]?["description"]?.ToString() ?? "no");
                return new { ok = false, status = Status };
            }
            Directory.CreateDirectory(Path.GetDirectoryName(UserFile)!);
            File.WriteAllText(UserFile, user);
            Status = "Paired. Pick the room below.";
            log("hue: paired with " + Bridge);
            return new { ok = true, status = Status };
        }
        catch (Exception ex) { Status = "Couldn't reach the bridge at " + Bridge + ": " + ex.Message; return new { ok = false, status = Status }; }
    }

    public static async Task<object> Rooms()
    {
        var u = User();
        if (u == null || Bridge.Length == 0) return new { rooms = Array.Empty<object>() };
        try
        {
            var o = JsonNode.Parse(await http.GetStringAsync($"http://{Bridge}/api/{u}/groups"))?.AsObject();
            var rooms = o?.Where(kv => kv.Value?["type"]?.ToString() is "Room" or "Zone" or "Entertainment")
                .Select(kv => (object)new { id = kv.Key, name = kv.Value?["name"]?.ToString() ?? kv.Key, lights = kv.Value?["lights"]?.AsArray().Count ?? 0 }).ToList() ?? new();
            return new { rooms };
        }
        catch (Exception ex) { Status = "Couldn't read the rooms: " + ex.Message; return new { rooms = Array.Empty<object>() }; }
    }

    // --------------------------------------------------------------- the flag loop
    enum Look { None, Green, Caution, Red, White, Chequered, Blue, Test }
    static Look showing = Look.None;
    static JsonObject? saved;          // how the room was before we changed it
    static long lookUntil;              // timed looks (green, white, blue, chequered)
    static volatile bool testNow;
    public static void Test() => testNow = true;

    static void Loop(RaceStateService rs)
    {
        RacePhase lastPhase = RacePhase.Unknown; bool lastWhite = false, lastBlue = false, lastChequer = false;
        while (true)
        {
            try
            {
                Thread.Sleep(400);
                var s = Integrations.Current;
                if (!s.HueOn || User() == null || Bridge.Length == 0 || s.HueRoom.Length == 0) { if (showing != Look.None) Restore(); continue; }
                long now = Environment.TickCount64;
                if (testNow) { testNow = false; Show(Look.Test); lookUntil = now + 6000; continue; }
                var snap = rs.Current;
                if (!snap.IsConnected || OverlayCompositor.SampleRunning)
                {
                    if (showing != Look.None && showing != Look.Test) Restore();
                    else if (showing == Look.Test && now > lookUntil) Restore();
                    lastPhase = snap.Phase; continue;
                }
                var f = snap.Flags;
                bool race = (snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
                Look want = Look.None;
                // timed looks start on the change
                if (race && lastPhase == RacePhase.PreRace && snap.Phase == RacePhase.Broadcast) { want = Look.Green; lookUntil = now + 6000; }
                else if (f.Checkered && !lastChequer) { want = Look.Chequered; lookUntil = now + 10000; }
                else if (f.White && !lastWhite && race) { want = Look.White; lookUntil = now + 5000; }
                else if (f.Blue && !lastBlue) { want = Look.Blue; lookUntil = now + 2500; }
                lastPhase = snap.Phase; lastWhite = f.White; lastBlue = f.Blue; lastChequer = f.Checkered;
                // steady looks while the flag is out (a red flag / full-course caution beat the timed ones)
                if (f.Red) want = Look.Red;
                else if (f.Caution) want = Look.Caution;
                if (want == Look.None && showing is Look.Green or Look.White or Look.Blue or Look.Chequered or Look.Test && now < lookUntil)
                {
                    if (showing == Look.Chequered) Chequer(now);
                    continue;
                }
                if (want == Look.None) { if (showing != Look.None) Restore(); continue; }
                if (want != showing) Show(want);
                else if (want == Look.Chequered) Chequer(now);
            }
            catch { }
        }
    }

    static int chequerPhase = -1;
    static void Chequer(long now)
    {
        int p = (int)(now / 600 % 2);
        if (p == chequerPhase) return;
        chequerPhase = p;
        Put(p == 0 ? new JsonObject { ["on"] = true, ["bri"] = 254, ["ct"] = 153, ["transitiontime"] = 0 } : new JsonObject { ["on"] = true, ["bri"] = 20, ["transitiontime"] = 0 });
    }

    static void Show(Look look)
    {
        if (showing == Look.None) saved = Current();
        showing = look; chequerPhase = -1;
        int bri = Math.Clamp(Integrations.Current.HueBrightness, 30, 254);
        JsonObject Xy(double x, double y, string? alert = null)
        {
            var o = new JsonObject { ["on"] = true, ["bri"] = bri, ["xy"] = new JsonArray(x, y), ["transitiontime"] = 2 };
            if (alert != null) o["alert"] = alert;
            return o;
        }
        Put(look switch
        {
            Look.Green => Xy(0.17, 0.70),
            Look.Caution => Xy(0.56, 0.41),
            Look.Red => Xy(0.675, 0.322),
            Look.White => new JsonObject { ["on"] = true, ["bri"] = bri, ["ct"] = 153, ["transitiontime"] = 2 },
            Look.Blue => Xy(0.167, 0.04, "select"),
            Look.Test => Xy(0.56, 0.41, "lselect"),
            _ => new JsonObject { ["on"] = true, ["bri"] = 254, ["ct"] = 153, ["transitiontime"] = 0 },
        });
        log("hue: " + look);
    }

    static void Restore()
    {
        var back = saved; showing = Look.None; saved = null;
        if (back == null) return;
        back["transitiontime"] = 8; back["alert"] = "none";
        Put(back);
    }

    static JsonObject? Current()
    {
        try
        {
            var g = JsonNode.Parse(http.GetStringAsync($"http://{Bridge}/api/{User()}/groups/{Integrations.Current.HueRoom}").GetAwaiter().GetResult());
            var a = g?["action"]; var st = g?["state"];
            var o = new JsonObject { ["on"] = st?["any_on"]?.GetValue<bool>() ?? a?["on"]?.GetValue<bool>() ?? true };
            if (a?["bri"] != null) o["bri"] = a["bri"]!.GetValue<int>();
            if (a?["colormode"]?.ToString() == "ct" && a["ct"] != null) o["ct"] = a["ct"]!.GetValue<int>();
            else if (a?["xy"] is JsonArray xy && xy.Count == 2) o["xy"] = new JsonArray(xy[0]!.GetValue<double>(), xy[1]!.GetValue<double>());
            return o;
        }
        catch { return null; }
    }

    static void Put(JsonObject body)
    {
        try
        {
            using var c = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            http.PutAsync($"http://{Bridge}/api/{User()}/groups/{Integrations.Current.HueRoom}/action", c).GetAwaiter().GetResult().Dispose();
        }
        catch (Exception ex) { Status = "Couldn't reach the bridge: " + ex.Message; }
    }
}
