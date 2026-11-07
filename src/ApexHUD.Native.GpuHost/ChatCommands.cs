using System.Globalization;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.GpuHost;

/// <summary>
/// 2026-11-06: viewers' chat commands (!standings, !gap, !fastest ...) answered from the live race. Streamer.bot (already
/// used for viewer cards) catches the command in Twitch / YouTube chat, fetches
/// http://127.0.0.1:8790/api/chat?cmd=%command%&amp;user=%user% and sends the text it gets back to chat. With "Chat can
/// play cards" on, some commands also play the matching story card (at most one every 90 s).
/// </summary>
internal static class ChatCommands
{
    public static readonly (string Cmd, string What)[] List =
    {
        ("standings", "Top 5 in our class"), ("gap", "Our position and the gaps ahead and behind"), ("fastest", "Fastest lap in each class"),
        ("lap", "Lap count or time left"), ("track", "Track and session"), ("car", "Our car"), ("weather", "Air and track temperature, rain"),
        ("irating", "Our iRating and licence"), ("league", "Our championship position (League standings)"), ("commands", "The list of commands"),
        ("bet", "Bet on the open question: !bet 1 or !bet 2, then an amount"), ("points", "Your bet points"), ("top", "The top 5 bettors"),
        ("watch", "The drivers to watch and why"), ("clip", "Makes a vertical clip of the last 30 s (if you allow it)"),
    };
    static long lastCardMs = long.MinValue / 2;
    static readonly Dictionary<string, long> lastCmdMs = new();

    public static string Answer(RaceSnapshot s, string rawCmd, string user, string input = "")
    {
        var inv = CultureInfo.InvariantCulture;
        string cmd = rawCmd.Trim().TrimStart('!').ToLowerInvariant();
        if (cmd.Length == 0 || cmd.Length > 20) return "";
        if (!Integrations.Current.ChatOn) return "";
        // 2026-11-06: viewer bets - every viewer's own command, no shared cooldown
        if (cmd is "bet" or "points" or "balance" or "top" or "leaderboard") return Bets.Chat(cmd, user, input);
        // 2026-11-06: !clip - a vertical clip of the last N seconds (only when "chat can make clips" is on; 1 a minute)
        if (cmd == "clip") return Integrations.Current.ClipChat ? VerticalClip.Request("chat:" + user) : "";
        // 2026-11-06: !watch - the drivers to watch
        if (cmd is "watch" or "towatch")
        {
            var w = ApexHUD.Native.Overlays.DriversToWatch.For(s);
            return w.Count == 0 ? "" : "Drivers to watch: " + string.Join(" | ", w.Select(p => $"#{p.Number} {p.Name} ({p.Tag.ToLowerInvariant()}: {p.Reason})"));
        }
        // the same command answered at most every 10 s (chat spam)
        long now = Environment.TickCount64;
        lock (lastCmdMs)
        {
            if (lastCmdMs.TryGetValue(cmd, out var t) && now - t < 10_000) return "";
            lastCmdMs[cmd] = now;
        }
        if (!s.IsConnected && cmd != "commands") return "Not on track right now.";
        var me = s.Player ?? s.CameraTarget ?? s.Leader;
        var myClass = me != null ? s.ClassLeaderboard.FirstOrDefault(c => c.Drivers.Any(d => d.CarIdx == me.CarIdx)) : s.ClassLeaderboard.FirstOrDefault();
        IEnumerable<Driver> Racing(DriverClass? c) => (c?.Drivers ?? Array.Empty<Driver>()).Where(d => !d.IsPaceCar && !d.IsSpectator && d.PositionInClass > 0).OrderBy(d => d.PositionInClass);
        string Name(Driver d) => NameFormat.Broadcast(d);
        string reply;
        switch (cmd)
        {
            case "standings": case "top5": case "positions":
                reply = myClass == null ? "No standings yet." : myClass.Name + ": " + string.Join(" | ", Racing(myClass).Take(5).Select(d => $"P{d.PositionInClass} #{d.CarNumber} {Name(d)}"));
                PlayCard("RaceSoFar");
                break;
            case "gap": case "position": case "pos":
                if (me == null) { reply = "No car to follow yet."; break; }
                var order = Racing(myClass).ToList();
                int i = order.FindIndex(d => d.CarIdx == me.CarIdx);
                string ahead = i > 0 ? $"{LapTimeFormat.Gap(me.IntervalSeconds)} behind #{order[i - 1].CarNumber} {Name(order[i - 1])}" : "leading the class";
                string behind = i >= 0 && i < order.Count - 1 && order[i + 1].IntervalSeconds is double gb ? $", {LapTimeFormat.Gap(gb)} ahead of #{order[i + 1].CarNumber} {Name(order[i + 1])}" : "";
                reply = $"#{me.CarNumber} {Name(me)}: P{me.PositionInClass} in {me.ClassName}, {ahead}{behind}";
                break;
            case "fastest": case "fastestlap": case "best":
                reply = string.Join(" | ", s.ClassLeaderboard.Select(c => (c.Name, Best: c.Drivers.Where(d => d.BestLapTime > 0).OrderBy(d => d.BestLapTime).FirstOrDefault()))
                    .Where(x => x.Best != null).Select(x => $"{x.Name}: {LapTimeFormat.Lap(x.Best!.BestLapTime)} #{x.Best.CarNumber} {Name(x.Best)}"));
                if (reply.Length == 0) reply = "No timed laps yet.";
                PlayCard("FastestLaps");
                break;
            case "lap": case "laps": case "time":
                reply = s.TotalLaps > 0 && s.TotalLaps < 2000 ? $"Lap {Math.Max(1, s.CurrentLap)} of {s.TotalLaps}"
                    : s.SessionTimeRemaining > 0 ? $"Lap {Math.Max(1, s.CurrentLap)}, {LapTimeFormat.SessionTimer(s.SessionTimeRemaining)} to go" : $"Lap {Math.Max(1, s.CurrentLap)}";
                break;
            case "track": case "session":
                reply = $"{s.TrackDisplayName}{(s.TrackConfigName.Length > 0 ? " (" + s.TrackConfigName + ")" : "")} - {s.SessionTypeName}, {s.ClassLeaderboard.Sum(c => c.Drivers.Count(d => !d.IsPaceCar && !d.IsSpectator))} cars";
                break;
            case "car":
                reply = s.Player is { } p ? $"#{p.CarNumber} {p.CarBrand} {p.CarName} ({p.ClassName})" : "Spectating right now.";
                break;
            case "weather": case "temp": case "temps":
                bool f = s.TemperatureUnit.StartsWith("F", StringComparison.OrdinalIgnoreCase);
                string T(float c) => f ? Math.Round(c * 9 / 5 + 32).ToString(inv) + "°F" : Math.Round(c).ToString(inv) + "°C";
                reply = $"Air {T(s.AirTempC)}, track {T(s.TrackTempC)}{(s.IsRaining ? ", raining" : ", dry")}";
                PlayCard("WeatherWatch");
                break;
            case "irating": case "ir": case "license": case "licence":
                reply = s.Player is { } pl ? $"{Name(pl)}: {pl.IRating.ToString(inv)} iRating, {pl.LicenceString}" : "Spectating right now.";
                break;
            case "league": case "champ": case "championship":
                var row = me != null ? Integrations.LeagueRowFor(me) : null;
                reply = row == null ? "No league standings set up." : $"{(Integrations.Current.LeagueName.Length > 0 ? Integrations.Current.LeagueName + ": " : "")}{row.Name} is P{row.Pos} on {row.Points} points{(row.GapToLeader is { Length: > 0 } g && g != "LEADER" ? $" ({g} to the leader)" : "")}";
                break;
            case "commands": case "help":
                reply = "Commands: " + string.Join(" ", List.Select(c => "!" + c.Cmd));
                break;
            default: return "";
        }
        return reply.Length > 380 ? reply[..380] : reply;
    }

    static void PlayCard(string kind)
    {
        if (!Integrations.Current.ChatCards || OverlayCompositor.SampleRunning) return;
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastCardMs) < 90_000) return;
        Interlocked.Exchange(ref lastCardMs, now);
        ControlState.Requests.Enqueue(kind);
    }
}
