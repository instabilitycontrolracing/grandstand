using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApexHUD.Native.Common;
using ApexHUD.Native.Data;
using ApexHUD.Native.Overlays;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06 (Angelo: "build the telemetry bets for viewers using channel points"): during a race Grandstand asks the
/// viewers a yes / no question that the telemetry settles, shows it on a card (top right), takes bets, locks, and shows
/// who called it.
///
/// <para><b>The questions</b> (about the streamer's own car, one at a time, every few minutes):
///   podium   - before the green: "Podium finish?" (locks at the green, settled at the flag)
///   nextlap  - at the start of a lap: "Next lap under 1:45.2?" (best + 0.4 s; locks when the lap ends, settled one lap
///              later; cancelled for a pit stop, a caution or an invalid lap)
///   gain     - "Gain a place in the next 3 laps?"
///   battle   - within a second of the car ahead in class: "Pass #12 within 3 laps?"</para>
///
/// <para><b>Two ways to bet</b> (either or both):
///   Twitch channel points - a real Twitch Prediction, made, locked and settled through Streamer.bot's HTTP server
///     (actions "Grandstand Bet Open / Lock / Resolve / Cancel"). Needs a Twitch affiliate / partner channel.
///   Grandstand points - for YouTube (no channel points there) or any chat: "!bet 1 200" / "!bet 2 all", "!points",
///     "!top". Everyone starts with 1,000; winners share the losers' points in proportion to their bet (like Twitch).
///     Saved in Documents\iRacing\Grandstand\bet_points.json.</para>
/// Never in the sample race, replays or practice. Test button: a pretend bet that runs the card through every stage.
/// </summary>
internal static class Bets
{
    sealed class Bet
    {
        public int Id; public string Kind = ""; public string Question = ""; public string TwitchTitle = "";
        public string[] Options = { "Yes", "No" };
        public long OpenedMs, ClosesMs, ResultUntilMs; public string State = "open"; public int Winner = -1;
        public bool Test, LockAtGreen;
        public readonly Dictionary<string, (string Name, int Opt, int Amt)> Entries = new();
        // what settles it
        public int Laps0, Pos0, RivalIdx = -1; public double Target; public string SessionKey = "";
        public int FakeBettors; public int[] FakePct = { 50, 50 };
    }

    static readonly object gate = new();
    static Bet? cur;
    static int nextId = 1;
    static long nextAllowedMs;
    static string podiumKey = "";
    static int kindTurn;
    static int lastLaps = -1; static long lapStartedMs;
    static Action<string> log = _ => { };
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(4) };
    public static string Status { get; private set; } = "";
    public static string TwitchStatus { get; private set; } = "";

    // ------------------------------------------------------------------ points (Grandstand's own)
    static readonly string PointsFile = Path.Combine(ControlState.Folder, "bet_points.json");
    sealed class Wallet { public string Name { get; set; } = ""; public int Points { get; set; } }
    static Dictionary<string, Wallet> wallets = LoadWallets();

    static Dictionary<string, Wallet> LoadWallets()
    {
        try { if (File.Exists(PointsFile)) return JsonSerializer.Deserialize<Dictionary<string, Wallet>>(File.ReadAllText(PointsFile)) ?? new(); } catch { }
        return new();
    }
    static void SaveWallets()
    {
        try { Directory.CreateDirectory(ControlState.Folder); File.WriteAllText(PointsFile, JsonSerializer.Serialize(wallets, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }
    static Wallet WalletOf(string user)
    {
        string key = user.Trim().ToLowerInvariant();
        if (!wallets.TryGetValue(key, out var w)) wallets[key] = w = new Wallet { Name = user.Trim(), Points = Math.Max(100, Integrations.Current.BetsStartPoints) };
        return w;
    }
    public static object Leaderboard(int n = 5) { lock (gate) return wallets.Values.OrderByDescending(w => w.Points).Take(n).Select(w => new { name = w.Name, points = w.Points }).ToList(); }
    public static void ResetPoints() { lock (gate) { wallets.Clear(); SaveWallets(); } }

    // ------------------------------------------------------------------ chat: !bet / !points / !top
    public static string Chat(string cmd, string user, string input)
    {
        var inv = CultureInfo.InvariantCulture;
        if (!Integrations.Current.BetsOn) return "";
        user = user.Trim();
        lock (gate)
        {
            if (cmd is "points" or "balance")
                return user.Length == 0 ? "" : $"{WalletOf(user).Name}: {WalletOf(user).Points.ToString("N0", inv)} points";
            if (cmd is "top" or "leaderboard")
            {
                var top = wallets.Values.OrderByDescending(w => w.Points).Take(5).ToList();
                return top.Count == 0 ? "No bets yet." : "Top bettors: " + string.Join(" | ", top.Select((w, i) => $"{i + 1}. {w.Name} {w.Points.ToString("N0", inv)}"));
            }
            if (cmd != "bet" || !Integrations.Current.BetsOwnPoints) return "";
            if (user.Length == 0) return "";
            if (cur == null || cur.State != "open" || cur.Test) return "No bet is open right now.";
            var parts = input.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return $"{cur.Question} Type !bet 1 for {cur.Options[0]} or !bet 2 for {cur.Options[1]}, then an amount (100 if you leave it out).";
            int opt = parts[0] switch { "1" or "yes" or "y" => 0, "2" or "no" or "n" => 1, _ => -1 };
            if (opt < 0) return "Use !bet 1 or !bet 2.";
            var w = WalletOf(user);
            // a new bet replaces the old one (the old stake comes back first)
            string key = user.ToLowerInvariant();
            if (cur.Entries.TryGetValue(key, out var old)) { w.Points += old.Amt; cur.Entries.Remove(key); }
            int amt = parts.Length > 1 ? (parts[1] == "all" ? w.Points : int.TryParse(parts[1].Replace(",", ""), NumberStyles.Integer, inv, out int a) ? a : 100) : 100;
            amt = Math.Min(amt, w.Points);
            if (amt < 10) { SaveWallets(); return $"{w.Name}, you need at least 10 points (you have {w.Points.ToString("N0", inv)})."; }
            w.Points -= amt;
            cur.Entries[key] = (w.Name, opt, amt);
            SaveWallets();
            Publish();
            return $"{w.Name} bet {amt.ToString("N0", inv)} on {cur.Options[opt]} ({w.Points.ToString("N0", inv)} left).";
        }
    }

    // ------------------------------------------------------------------ the loop
    public static void Init(RaceStateService rs, Action<string> logger)
    {
        log = logger;
        new Thread(() => { while (true) { try { Tick(rs); } catch (Exception ex) { log("bets: " + ex.Message); } Thread.Sleep(250); } })
        { IsBackground = true, Name = "Grandstand-Bets", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public static void Test()
    {
        lock (gate)
        {
            if (cur != null && !cur.Test) return;
            long now = Environment.TickCount64;
            cur = new Bet { Id = nextId++, Kind = "test", Test = true, Question = "Next lap under 1:44.9?", OpenedMs = now, ClosesMs = now + 15000, FakeBettors = 3, FakePct = new[] { 50, 50 } };
            Publish();
        }
    }

    static void Tick(RaceStateService rs)
    {
        long now = Environment.TickCount64;
        var set = Integrations.Current;
        lock (gate)
        {
            // the pretend bet (Test button): open 15 s with made-up bettors, locked 4 s, then YES for 9 s
            if (cur is { Test: true })
            {
                if (cur.State == "open")
                {
                    if (now - cur.OpenedMs > 1500 * cur.FakeBettors && cur.FakeBettors < 14) { cur.FakeBettors++; int y = 50 + (int)(Math.Sin(cur.FakeBettors) * 22); cur.FakePct = new[] { y, 100 - y }; }
                    if (now >= cur.ClosesMs) { cur.State = "locked"; cur.ClosesMs = now + 4000; }
                }
                else if (cur.State == "locked" && now >= cur.ClosesMs) { cur.State = "result"; cur.Winner = 0; cur.ResultUntilMs = now + 9000; }
                else if (cur.State == "result" && now >= cur.ResultUntilMs) cur = null;
                Publish();
                return;
            }
            if (!set.BetsOn) { if (cur != null) Cancel("bets switched off"); return; }
            var s = rs.Current;
            bool race = (s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
            var me = s.Player;
            if (!s.IsConnected || OverlayCompositor.SampleRunning || ReplayState.IsReplayFile || !race || me == null)
            {
                if (cur != null && (!s.IsConnected || s.SessionKey != cur.SessionKey)) Cancel("the session ended");
                return;
            }
            if (s.SessionKey != cur?.SessionKey && cur != null) { Cancel("a new session"); return; }
            // lap start detection (for the next-lap question)
            if (me.LapsCompleted != lastLaps) { lastLaps = me.LapsCompleted; lapStartedMs = now; }

            if (cur != null) { Advance(cur, s, me, now); Publish(); return; }

            // ---- maybe ask something
            var myClass = s.ClassLeaderboard.FirstOrDefault(c => c.Drivers.Any(d => d.CarIdx == me.CarIdx));
            var field = myClass?.Drivers.Where(d => !d.IsPaceCar && !d.IsSpectator).OrderBy(d => d.PositionInClass).ToList() ?? new();
            string first = (me.Name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "our driver";
            if (s.Phase == RacePhase.PreRace && podiumKey != s.SessionKey && field.Count >= 4)
            {
                podiumKey = s.SessionKey;
                Open(new Bet { Kind = "podium", Question = "Podium finish?", TwitchTitle = $"Will {first} finish on the podium?", LockAtGreen = true }, s, me, now, 1800);
                return;
            }
            if (s.Phase != RacePhase.Broadcast || now < nextAllowedMs || me.IsInPit || s.Flags.Caution || me.LapsCompleted < 1) return;
            if (s.LapsRemaining is > 0 and < 5) return;   // not in the last laps (it couldn't settle)
            int i = field.FindIndex(d => d.CarIdx == me.CarIdx);
            var ahead = i > 0 ? field[i - 1] : null;
            for (int tries = 0; tries < 3; tries++)
            {
                string kind = (kindTurn++ % 3) switch { 0 => "nextlap", 1 => "battle", _ => "gain" };
                if (kind == "nextlap" && me.BestLapTime > 30 && now - lapStartedMs < 6000)
                {
                    double target = Math.Round(me.BestLapTime + 0.4, 1);
                    string t = LapTimeFormat.Lap(target);
                    Open(new Bet { Kind = kind, Question = $"Next lap under {t}?", TwitchTitle = $"{first}'s next lap under {t}?", Target = target, Laps0 = me.LapsCompleted }, s, me, now, Math.Clamp(set.BetsOpenSeconds, 30, 120));
                    return;
                }
                if (kind == "battle" && ahead != null && me.IntervalSeconds is > 0 and < 1.0)
                {
                    Open(new Bet { Kind = kind, Question = $"Pass #{ahead.CarNumber} within 3 laps?", TwitchTitle = $"Will {first} pass #{ahead.CarNumber} within 3 laps?", RivalIdx = ahead.CarIdx }, s, me, now, Math.Clamp(set.BetsOpenSeconds, 30, 120));
                    return;
                }
                if (kind == "gain" && ahead != null)
                {
                    Open(new Bet { Kind = kind, Question = "Gain a place in the next 3 laps?", TwitchTitle = $"Will {first} gain a place in 3 laps?" }, s, me, now, Math.Clamp(set.BetsOpenSeconds, 30, 120));
                    return;
                }
            }
        }
    }

    static void Open(Bet b, RaceSnapshot s, Driver me, long now, int seconds)
    {
        b.Id = nextId++; b.OpenedMs = now; b.ClosesMs = b.LockAtGreen ? long.MaxValue / 4 : now + seconds * 1000L; b.SessionKey = s.SessionKey;
        b.Pos0 = me.PositionInClass;
        if (b.TwitchTitle.Length > 45) b.TwitchTitle = b.TwitchTitle[..45];
        cur = b;
        Status = $"Open: {b.Question}";
        log($"bets: open {b.Kind} \"{b.Question}\"");
        if (Integrations.Current.BetsTwitch)
            Sb("Grandstand Bet Open", new JsonObject { ["title"] = b.TwitchTitle, ["option1"] = b.Options[0], ["option2"] = b.Options[1], ["seconds"] = Math.Clamp(seconds, 30, 1800) });
        Publish();
    }

    static void Advance(Bet b, RaceSnapshot s, Driver me, long now)
    {
        var field = s.ClassLeaderboard.SelectMany(c => c.Drivers);
        if (b.State == "open")
        {
            bool lockNow = b.LockAtGreen ? s.Phase == RacePhase.Broadcast : now >= b.ClosesMs;
            if (b.Kind == "nextlap" && me.LapsCompleted > b.Laps0) lockNow = true;   // the lap being bet on has started
            if (!lockNow) return;
            b.State = "locked"; b.Pos0 = me.PositionInClass; b.Laps0 = b.Kind == "nextlap" ? b.Laps0 + 1 : me.LapsCompleted;
            Status = "Locked: " + b.Question;
            if (Integrations.Current.BetsTwitch) Sb("Grandstand Bet Lock", new JsonObject());
            log($"bets: locked ({Bettors(b)} bettors)");
            return;
        }
        if (b.State == "locked")
        {
            int win = -1;
            switch (b.Kind)
            {
                case "podium":
                    if (s.Phase == RacePhase.PostRace) win = me.PositionInClass is > 0 and <= 3 ? 0 : 1;
                    break;
                case "nextlap":
                    if (me.IsInPit || s.Flags.Caution) { Cancel("a pit stop or a caution"); return; }
                    if (me.LapsCompleted > b.Laps0)
                    {
                        if (me.LastLapTime <= 0) { Cancel("the lap didn't count"); return; }
                        win = me.LastLapTime < b.Target ? 0 : 1;
                    }
                    break;
                case "gain":
                    if (me.LapsCompleted >= b.Laps0 + 3) win = me.PositionInClass < b.Pos0 ? 0 : 1;
                    break;
                case "battle":
                    var rival = field.FirstOrDefault(d => d.CarIdx == b.RivalIdx);
                    if (rival == null || !rival.IsConnected) { Cancel("the other car left"); return; }
                    if (me.PositionInClass < rival.PositionInClass) win = 0;
                    else if (me.LapsCompleted >= b.Laps0 + 3) win = 1;
                    break;
            }
            if (win < 0 && s.Phase == RacePhase.PostRace && b.Kind != "podium") { Cancel("the race ended"); return; }
            if (win >= 0) Settle(b, win, now);
            return;
        }
        if (b.State == "result" && now >= b.ResultUntilMs)
        {
            cur = null;
            nextAllowedMs = now + Math.Clamp(Integrations.Current.BetsEveryMinutes, 2, 60) * 60_000L;
        }
    }

    static void Settle(Bet b, int win, long now)
    {
        b.State = "result"; b.Winner = win; b.ResultUntilMs = now + 9000;
        // Grandstand points: winners share the pool in proportion to their bets
        int pool = b.Entries.Values.Sum(e => e.Amt), winPool = b.Entries.Values.Where(e => e.Opt == win).Sum(e => e.Amt);
        if (winPool > 0)
            foreach (var (key, e) in b.Entries.Where(kv => kv.Value.Opt == win).Select(kv => (kv.Key, kv.Value)))
                if (wallets.TryGetValue(key, out var w)) w.Points += (int)Math.Floor((double)pool * e.Amt / winPool);
        SaveWallets();
        Status = $"Settled: {b.Question} {b.Options[win].ToUpperInvariant()}";
        log($"bets: {Status} (pool {pool}, {Bettors(b)} bettors)");
        if (Integrations.Current.BetsTwitch) Sb("Grandstand Bet Resolve", new JsonObject { ["winner"] = win });
    }

    static void Cancel(string why)
    {
        var b = cur; if (b == null) return;
        foreach (var (key, e) in b.Entries.Select(kv => (kv.Key, kv.Value)))
            if (wallets.TryGetValue(key, out var w)) w.Points += e.Amt;
        if (b.Entries.Count > 0) SaveWallets();
        if (Integrations.Current.BetsTwitch && !b.Test && b.State != "result") Sb("Grandstand Bet Cancel", new JsonObject());
        Status = "Cancelled (" + why + "), points given back";
        log("bets: cancelled - " + why);
        cur = null;
        BetBoard.Current = null;
        nextAllowedMs = Environment.TickCount64 + 60_000;
    }

    static int Bettors(Bet b) => b.Test ? b.FakeBettors : b.Entries.Count;

    static void Publish()
    {
        var b = cur;
        if (b == null) { BetBoard.Current = null; return; }
        int[] pct;
        if (b.Test) pct = b.FakePct;
        else
        {
            int tot = b.Entries.Values.Sum(e => e.Amt);
            pct = tot == 0 ? Array.Empty<int>() : new[] { (int)Math.Round(100.0 * b.Entries.Values.Where(e => e.Opt == 0).Sum(e => e.Amt) / tot), 0 };
            if (pct.Length == 2) pct[1] = 100 - pct[0];
        }
        var set = Integrations.Current;
        string how = set.BetsOwnPoints && set.BetsTwitch ? "Twitch: the prediction · Chat: !bet 1 or !bet 2"
            : set.BetsTwitch ? "Bet your channel points in the Twitch prediction" : "Type !bet 1 or !bet 2 in chat";
        if (b.LockAtGreen && b.State == "open") how += " · until the green flag";
        long now = Environment.TickCount64;
        double secs = b.LockAtGreen ? 99999 : Math.Max(0, (b.ClosesMs - now) / 1000.0);
        float frac = b.LockAtGreen ? 1f : (float)Math.Clamp((double)(b.ClosesMs - now) / Math.Max(1000, b.ClosesMs - b.OpenedMs), 0, 1);
        BetBoard.Current = new BetView(b.Id, b.State, b.Question, b.Options, pct, Bettors(b), secs, frac, b.Winner, how);
    }

    public static object Describe() { lock (gate) return new { open = cur != null && !cur.Test, state = cur?.State ?? "", question = cur?.Question ?? "", status = Status, twitch = TwitchStatus }; }

    // ------------------------------------------------------------------ Streamer.bot (Twitch Predictions)
    static void Sb(string action, JsonObject args)
    {
        int port = Math.Clamp(Integrations.Current.BetsSbPort, 1, 65535);
        var body = new JsonObject { ["action"] = new JsonObject { ["name"] = action }, ["args"] = args };
        _ = Task.Run(async () =>
        {
            try
            {
                using var res = await http.PostAsync($"http://127.0.0.1:{port}/DoAction", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
                TwitchStatus = res.IsSuccessStatusCode ? $"{action}: sent to Streamer.bot ({DateTime.Now:HH:mm})" : $"{action}: Streamer.bot answered {(int)res.StatusCode}";
            }
            catch { TwitchStatus = $"{action}: couldn't reach Streamer.bot on port {port} (Servers/Clients > HTTP Server, Auto Start)"; }
            log("bets: " + TwitchStatus);
        });
    }

    public static string TestTwitch()
    {
        Sb("Grandstand Bet Open", new JsonObject { ["title"] = "Grandstand test: is this working?", ["option1"] = "Yes", ["option2"] = "No", ["seconds"] = 30 });
        return "Sent a 30 s test prediction to Streamer.bot - check your Twitch channel (cancel it there when you've seen it).";
    }
}
