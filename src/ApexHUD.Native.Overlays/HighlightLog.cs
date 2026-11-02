using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using ApexHUD.Native.Common;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Highlight markers (2026-10-28, #487, Angelo: "while the race is happening if we have cool moments the race director can
/// flag them so when I race I don't need to go back and edit a highlight reel of my streaming").
///
/// Every moment the broadcast already reacts to - green flag, a pass or a lost place for your car, a battle card, a
/// fastest lap, a yellow flag, a pit stop, the chequered flag - is written down with WHEN it happened in three ways:
/// the wall clock (lines up with an OBS recording), the iRacing session number + session time (iRacing's replay can jump
/// straight there), and the lap. One file per session in Documents\iRacing\Grandstand\highlights:
///   &lt;date&gt;_&lt;track&gt;_&lt;session&gt;.json      every marker
///   &lt;date&gt;_&lt;track&gt;_&lt;session&gt;_chapters.txt  YouTube chapters (times from the first marker = the start of the race)
/// <see cref="Marked"/> lets the host forward each marker (OBS chapter / replay-buffer clip, see ObsMarkers).
/// Nothing leaves the PC from here. Never throws into the render loop.
/// </summary>
public static class HighlightLog
{
    public sealed record Mark(DateTime Utc, int SessionNum, double SessionTime, int Lap, string Kind, string Label,
                              int[] Cars, bool Mine, bool Replay);

    /// <summary>Raised for every new marker (on the render thread - handlers must be quick).</summary>
    public static event Action<Mark>? Marked;
    /// <summary>Master switch (control app). On: it only writes small files on this PC.</summary>
    public static bool Enabled { get; set; } = true;

    static readonly List<Mark> marks = new();
    static readonly Dictionary<string, DateTime> lastKind = new();
    static RaceSnapshot last = RaceSnapshot.Disconnected;
    static string fileKey = "";
    static RacePhase lastPhase = RacePhase.Unknown;
    static int lastPos = -1, pendPos = -1, lastPlayer = -1;
    static DateTime pendSince;
    static bool lastPit;
    static readonly object gate = new();

    /// <summary>Where the highlight files live (the highlight reel reads them from here).</summary>
    public static string Folder => Dir;
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand", "highlights");

    /// <summary>Feed every snapshot (cheap). Works out the session, and the moments that need no renderer: start,
    /// finish, your car's place changes and pit stops, a battle starting.</summary>
    public static void Note(RaceSnapshot s, bool battleActive)
    {
        try
        {
            last = s;
            if (!Enabled || !s.IsConnected) return;
            if (HighlightReel.Playing) { lastPlayer = -1; lastPhase = RacePhase.Unknown; return; }   // #9: re-syncs after the reel
            bool race = (s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase);
            if (race && lastPhase == RacePhase.PreRace && s.Phase == RacePhase.Broadcast) Add("start", "Green flag");
            if (race && lastPhase == RacePhase.Broadcast && s.Phase == RacePhase.PostRace) Add("finish", "Chequered flag");
            lastPhase = s.Phase;

            var me = s.Player;
            if (me == null || me.CarIdx != lastPlayer) { lastPlayer = me?.CarIdx ?? -1; lastPos = pendPos = me?.PositionInClass ?? -1; lastPit = me?.IsInPit ?? false; }
            else if (s.Phase == RacePhase.Broadcast)
            {
                int p = me.PositionInClass;
                // a new place counts once it has held for 2 s (side by side doesn't flicker markers out)
                if (p > 0 && p != lastPos)
                {
                    if (p != pendPos) { pendPos = p; pendSince = DateTime.UtcNow; }
                    else if ((DateTime.UtcNow - pendSince).TotalSeconds >= 2 && lastPos > 0)
                    {
                        bool up = p < lastPos;
                        string cls = (me.ClassName ?? "").Length > 0 ? " in " + me.ClassName : "";
                        Add(up ? "pass" : "lost", (up ? "Up to P" : "Down to P") + p.ToString(CultureInfo.InvariantCulture) + cls, true, me.CarIdx);
                        lastPos = p;
                    }
                }
                else pendPos = p;
                if (me.IsInPit && !lastPit) Add("pit", "Pit stop", true, me.CarIdx);
                lastPit = me.IsInPit;
            }
        }
        catch { }
    }

    public static void Add(string kind, string label, params int[] cars) => Add(kind, label, false, cars);

    /// <summary>A marker now. The same kind within 8 s is one moment (a battle card re-showing, a flickering flag).</summary>
    public static void Add(string kind, string label, bool mine, params int[] cars)
    {
        try
        {
            if (!Enabled) return;
            var s = last;
            if (!s.IsConnected) return;
            if (ReplayState.Scrubbing) return;                         // rewinding / fast-forwarding isn't a moment
            if (HighlightReel.Playing) return;                         // #9: the reel replaying a moment isn't a new one
            var now = DateTime.UtcNow;
            lock (gate)
            {
                if (lastKind.TryGetValue(kind, out var t) && (now - t).TotalSeconds < 8) return;
                lastKind[kind] = now;
                string key = FileKey(s);
                if (key != fileKey) { fileKey = key; marks.Clear(); }
                var p = s.Player;
                bool me = mine || (p != null && Array.IndexOf(cars, p.CarIdx) >= 0);
                var m = new Mark(now, s.SessionNum, Math.Round(ReplayState.SessionTime, 2), p?.CurrentLap ?? 0, kind, label, cars, me, ReplayState.IsReplay);
                marks.Add(m);
                var copy = marks.ToArray();
                Task.Run(() => Save(key, copy));
                OverlayLog.Write($"[highlight] {kind}: {label} (session {m.SessionNum} @ {m.SessionTime:0.0}s, lap {m.Lap})");
                Marked?.Invoke(m);
            }
        }
        catch { }
    }

    static string FileKey(RaceSnapshot s)
    {
        string track = new string((s.TrackDisplayName ?? "track").Where(ch => char.IsLetterOrDigit(ch) || ch == ' ').ToArray()).Trim().Replace(' ', '-');
        string sess = new string((s.SessionTypeName ?? "session").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"{DateTime.Now:yyyy-MM-dd}_{(track.Length > 40 ? track[..40] : track)}_{sess}{(s.SessionNum >= 0 ? s.SessionNum : 0)}";
    }

    static readonly object fileGate = new();
    static void Save(string key, Mark[] list)
    {
        try
        {
            lock (fileGate)
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(Path.Combine(Dir, key + ".json"), JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(Dir, key + "_chapters.txt"), Chapters(list));
            }
        }
        catch { }
    }

    /// <summary>YouTube chapter lines, times from the first marker (YouTube needs the first at 0:00). Line the video's
    /// start up with that first marker, or shift them all by the offset when editing.</summary>
    public static string Chapters(IReadOnlyList<Mark> list)
    {
        var sb = new StringBuilder();
        if (list.Count == 0) return "";
        var t0 = list[0].Utc;
        foreach (var m in list)
        {
            var d = m.Utc - t0;
            string ts = d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";
            sb.Append(ts).Append(' ').Append(m.Lap > 0 ? $"L{m.Lap} " : "").AppendLine(m.Label);
        }
        return sb.ToString();
    }

    /// <summary>Test / preview: forget everything.</summary>
    public static void Reset() { lock (gate) { marks.Clear(); lastKind.Clear(); fileKey = ""; lastPhase = RacePhase.Unknown; lastPos = pendPos = lastPlayer = -1; } }
    public static IReadOnlyList<Mark> Current { get { lock (gate) return marks.ToArray(); } }
}
