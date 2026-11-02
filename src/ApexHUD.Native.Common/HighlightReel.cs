using System.Globalization;
using System.Text.Json;

namespace ApexHUD.Native.Common;

/// <summary>
/// The highlight reel's plan (2026-10-29, #9 "4K highlight reel", phase 1): pure functions that turn a session's
/// highlight-marker file (HighlightLog, Documents\iRacing\Grandstand\highlights\*.json) into the list of replay moments
/// the reel plays, and pick a TV camera group for each. No iRacing, no OBS, no files here - the GpuHost runner
/// (HighlightReelRunner) does the playing; tools/reel_check tests these.
/// </summary>
public static class HighlightReel
{
    /// <summary>One moment of the reel. <paramref name="StartTime"/> is where the replay jumps to (session seconds),
    /// <paramref name="PlaySeconds"/> how long it plays at 1x. <paramref name="Seek"/> false = the moment starts where the
    /// previous one ended, so the replay just keeps playing (no jump) and only the camera changes.
    /// <paramref name="CarIdx"/> -1 = the marker named no car (green flag, chequered flag): the runner uses your car.</summary>
    public sealed record Moment(int SessionNum, double MarkTime, double StartTime, double PlaySeconds, string Kind,
                                string Label, int Lap, int CarIdx, bool Mine, bool Seek)
    {
        public double EndTime => StartTime + PlaySeconds;
        /// <summary>The OBS chapter name ("L12 Up to P3 in GTP").</summary>
        public string Chapter => (Lap > 0 ? "L" + Lap.ToString(CultureInfo.InvariantCulture) + " " : "") + Label;
    }

    /// <summary>A marker as read from the file (the same fields HighlightLog.Mark writes).</summary>
    public sealed record Marker(int SessionNum, double SessionTime, int Lap, string Kind, string Label, int[] Cars, bool Mine);

    /// <summary>Seconds of lead-up before the marked moment.</summary>
    public const double LeadIn = 6.0;
    /// <summary>Markers closer than this to the one kept before them (same session) are the same moment.</summary>
    public const double DuplicateWindow = 10.0;
    /// <summary>At most this many moments in one reel.</summary>
    public const int MaxMoments = 20;
    /// <summary>A moment squeezed by the one before it is dropped when less than this is left to play.</summary>
    public const double MinPlay = 3.0;

    /// <summary>Set by the runner while a reel is playing: the highlight log stops writing markers (the replayed
    /// passes and battles would otherwise be marked again, into the very file being played) and the replay director
    /// leaves the camera alone.</summary>
    public static volatile bool Playing;

    /// <summary>Play time at 1x: passes, lost places and battles 12 s (they need the run-up and the move), the rest 8 s.</summary>
    public static double PlayFor(string kind) => kind is "pass" or "lost" or "battle" ? 12.0 : 8.0;

    /// <summary>Which moments survive the cap first (higher = more important). Your own moments get a bonus.</summary>
    public static int Priority(string kind, bool mine)
    {
        int p = kind switch
        {
            "finish" => 100,
            "start" => 90,
            "pass" => 80,
            "flag" => 75,        // flagged by hand (stream deck / replay remote): you asked for it
            "battle" => 70,
            "lost" => 60,
            "fastest" => 50,
            "yellow" => 40,
            "pit" => 30,
            _ => 20,
        };
        return p + (mine ? 5 : 0);
    }

    /// <summary>Reads a highlight file's JSON (an array of marks). Bad entries are skipped; bad JSON gives an empty list.</summary>
    public static List<Marker> ParseMarkers(string json)
    {
        var list = new List<Marker>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                try
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    int sn = Int(e, "SessionNum", -1);
                    double st = Dbl(e, "SessionTime");
                    if (sn < 0 || double.IsNaN(st) || st <= 0) continue;            // no replay time: can't be played back
                    string kind = Str(e, "Kind");
                    if (kind.Length == 0) continue;
                    var cars = new List<int>();
                    if (e.TryGetProperty("Cars", out var c) && c.ValueKind == JsonValueKind.Array)
                        foreach (var x in c.EnumerateArray()) if (x.TryGetInt32(out var ci) && ci >= 0) cars.Add(ci);
                    bool mine = e.TryGetProperty("Mine", out var m) && m.ValueKind == JsonValueKind.True;
                    list.Add(new Marker(sn, st, Int(e, "Lap", 0), kind, Str(e, "Label"), cars.ToArray(), mine));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// The reel's moments from a session's markers: in time order; a marker within <see cref="DuplicateWindow"/> s of
    /// the one kept before it is the same moment (the more important of the two is kept); at most
    /// <see cref="MaxMoments"/> (the most important, then back in time order); each one starts <see cref="LeadIn"/> s
    /// early and plays <see cref="PlayFor"/>; when a moment would start before the previous one has finished, it carries
    /// straight on from there (no jump back, nothing shown twice).
    /// </summary>
    public static List<Moment> Build(IEnumerable<Marker> markers, int maxMoments = MaxMoments)
    {
        var sorted = markers.Where(m => m.SessionNum >= 0 && m.SessionTime > 0)
                            .OrderBy(m => m.SessionNum).ThenBy(m => m.SessionTime).ToList();
        // 1. duplicates: one moment per 10 s window (measured from the marker kept before)
        var kept = new List<Marker>();
        foreach (var m in sorted)
        {
            if (kept.Count > 0 && kept[^1].SessionNum == m.SessionNum && m.SessionTime - kept[^1].SessionTime < DuplicateWindow)
            {
                if (Priority(m.Kind, m.Mine) > Priority(kept[^1].Kind, kept[^1].Mine)) kept[^1] = m;
                continue;
            }
            kept.Add(m);
        }
        // 2. the cap: the most important moments (ties: the earlier one), then back in time order
        if (kept.Count > maxMoments)
            kept = kept.Select((m, i) => (m, i)).OrderByDescending(x => Priority(x.m.Kind, x.m.Mine)).ThenBy(x => x.i)
                       .Take(Math.Max(0, maxMoments)).OrderBy(x => x.i).Select(x => x.m).ToList();
        // 3. timing: lead-in, play time, carry on instead of jumping back over what was just shown
        var list = new List<Moment>();
        foreach (var m in kept)
        {
            double start = Math.Max(0, m.SessionTime - LeadIn);
            double end = start + PlayFor(m.Kind);
            bool seek = true;
            if (list.Count > 0 && list[^1].SessionNum == m.SessionNum && start < list[^1].EndTime + 0.5)
            {
                start = list[^1].EndTime;
                seek = false;
                if (end - start < MinPlay)
                {
                    // nearly all of it was already on screen: stretch the previous moment to cover it instead
                    var prev = list[^1];
                    list[^1] = prev with { PlaySeconds = Math.Max(prev.PlaySeconds, end - prev.StartTime) };
                    continue;
                }
            }
            int car = m.Cars.Length > 0 ? m.Cars[0] : -1;
            list.Add(new Moment(m.SessionNum, m.SessionTime, Math.Round(start, 2), Math.Round(end - start, 2), m.Kind,
                                m.Label.Length > 0 ? m.Label : m.Kind, m.Lap, car, m.Mine, seek));
        }
        return list;
    }

    /// <summary>Total play time of a reel in seconds (the length of the recorded video, give or take the jumps).</summary>
    public static double TotalSeconds(IReadOnlyList<Moment> moments) => moments.Sum(m => m.PlaySeconds);

    /// <summary>
    /// The camera group for a moment, by name from the session's CameraInfo. Trackside TV cameras only (never the
    /// cockpit, an onboard or the blimp): pit stops on "Pit Lane"; everything else on TV1 / TV2 / TV3 taken in turn
    /// (<paramref name="index"/> = the moment's number) so the reel doesn't look like one camera; then "Chase", then
    /// any TV-style group. -1 when nothing fits (the runner then keeps iRacing's camera group and only picks the car).
    /// </summary>
    public static int PickGroup(IReadOnlyList<(int Num, string Name)> groups, string kind, int index)
    {
        int Find(string name)
        {
            foreach (var g in groups) if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g.Num;
            return -1;
        }
        if (kind == "pit") { int pit = Find("Pit Lane"); if (pit >= 0) return pit; }
        var tv = new[] { "TV1", "TV2", "TV3" }.Select(Find).Where(n => n >= 0).ToList();
        if (kind is "start" or "finish" && tv.Count > 0) return tv[0];          // the start and the flag: the main TV camera
        if (tv.Count > 0) return tv[((index % tv.Count) + tv.Count) % tv.Count];
        int chase = Find("Chase");
        if (chase >= 0) return chase;
        foreach (var g in groups)
            if (g.Name.Contains("TV", StringComparison.OrdinalIgnoreCase) && !g.Name.Contains("Static", StringComparison.OrdinalIgnoreCase)) return g.Num;
        foreach (var g in groups) if (g.Name.Contains("TV", StringComparison.OrdinalIgnoreCase)) return g.Num;
        return -1;
    }

    /// <summary>A highlight file name the control app may ask for: a bare "*.json" name, no folders, not a chapters file.</summary>
    public static bool SafeFileName(string? name)
        => !string.IsNullOrEmpty(name) && name.Length <= 160 && name == Path.GetFileName(name) && !name.Contains("..")
           && !name.StartsWith('.') && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
           && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

    /// <summary>"2026-10-28_Road-Atlanta_race2.json" -> "2026-10-28 · Road Atlanta · race 2" for the session picker.</summary>
    public static string SessionLabel(string fileName)
    {
        string n = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName[..^5] : fileName;
        var parts = n.Split('_');
        if (parts.Length < 3) return n.Replace('_', ' ');
        string sess = parts[^1];
        int d = sess.Length;
        while (d > 0 && char.IsDigit(sess[d - 1])) d--;
        if (d > 0 && d < sess.Length) sess = sess[..d] + " " + sess[d..];
        return parts[0] + " · " + string.Join(" ", parts[1..^1]).Replace('-', ' ') + " · " + sess;
    }

    static int Int(JsonElement e, string k, int fb) => e.TryGetProperty(k, out var v) && v.TryGetInt32(out var i) ? i : fb;
    static double Dbl(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.TryGetDouble(out var d) ? d : double.NaN;
    static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
