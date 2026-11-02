using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// A card made in the control app's card builder (2026-10-24). Saved as one small JSON file in
/// Documents\iRacing\Grandstand\cards\, so cards can be shared by copying the file. Every value
/// is clamped on load; anything unknown falls back to a safe default.
/// </summary>
public sealed class CustomCardDef
{
    public string Id = "";
    public string Name = "Your card";
    /// <summary>"number" (one big value), "top5" (a ranked list) or "h2h" (two cars side by side).</summary>
    public string Template = "number";
    /// <summary>The data shown: a built-in field (PlacesGained, Position, BestLap...) or an iRacing telemetry variable.</summary>
    public string Field = "PlacesGained";
    public string Label = "";
    /// <summary>"gain" (a car gains Amount+ places in Laps laps), "everylaps" (every Amount leader laps),
    /// "above" (the field goes above Threshold for the camera car), or "manual" (test button only).</summary>
    public string Trigger = "gain";
    public int Amount = 4;
    public int Laps = 3;
    public double Threshold;
    public double Seconds = 8;
    public double CooldownMinutes = 5;
    /// <summary>"none", a partner (AB, Storm, GoFast, TradingPaints, Custom) or "slot" (the pack's CustomCards slot).</summary>
    public string Sponsor = "none";
    public SKColor Accent = new(0x34, 0xD2, 0x7B);
    public bool Enabled = true;
}

/// <summary>
/// Loads, decides and draws the builder's custom cards. Safety first: a bad file is skipped (and
/// reported to the control app), never thrown at the render loop; with no cards saved the per-tick
/// cost is a single null check. Decisions run at most twice a second. A custom card never shows
/// while a story card or the outro is up, and bows out if one starts.
/// </summary>
public static class CustomCards
{
    public static readonly string Folder = Path.Combine(SponsorPack.Folder, "cards");
    public const int MaxCards = 50;
    const long MaxFileBytes = 64 * 1024;

    static volatile CustomCardDef[] defs = Array.Empty<CustomCardDef>();
    static int version;
    /// <summary>Bumped when the saved cards change (the control app saved or deleted one).</summary>
    public static int Version => Volatile.Read(ref version);
    public static IReadOnlyList<CustomCardDef> Loaded => defs;

    /// <summary>Raw telemetry reader (variable name, index) supplied by the host; null = built-in fields only.</summary>
    public static Func<string, int, object?>? ReadVar;

    /// <summary>One-shot "play this card now" requests (test button), drained on the render thread.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Requests = new();

    // ------------------------------------------------------------------ loading

    /// <summary>Reads every card file (off the render thread). Returns the problems found, one line per bad file.</summary>
    public static List<string> LoadFolder()
    {
        var problems = new List<string>();
        var list = new List<CustomCardDef>();
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.GetFiles(Folder, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    if (list.Count >= MaxCards) { problems.Add($"more than {MaxCards} cards: the rest were skipped"); break; }
                    try
                    {
                        if (new FileInfo(f).Length > MaxFileBytes) { problems.Add(Path.GetFileName(f) + ": file too big"); continue; }
                        var d = Parse(File.ReadAllText(f));
                        d.Id = Path.GetFileNameWithoutExtension(f);
                        list.Add(d);
                    }
                    catch (Exception ex) { problems.Add(Path.GetFileName(f) + ": " + ex.Message); }
                }
        }
        catch (Exception ex) { problems.Add("cards folder: " + ex.Message); }
        defs = list.ToArray();
        Interlocked.Increment(ref version);
        return problems;
    }

    /// <summary>Validates a card's JSON (throws with a readable message when it can't be used).</summary>
    public static CustomCardDef Parse(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                   ?? throw new FormatException("not a card (expected a JSON object)");
        var d = new CustomCardDef
        {
            Name = Str(root, "name", "Your card", 40),
            Template = Str(root, "template", "number", 10) switch { "top5" => "top5", "h2h" => "h2h", _ => "number" },
            Field = Str(root, "field", "PlacesGained", 48),
            Label = Str(root, "label", "", 40),
            Seconds = Math.Clamp(Num(root, "seconds", 8), 3, 30),
            CooldownMinutes = Math.Clamp(Num(root, "cooldown", 5), 0.5, 60),
            Enabled = root["enabled"] is not JsonValue ev || !ev.TryGetValue<bool>(out var en) || en,
        };
        // the trigger: {"trigger": {"kind": "gain", "amount": 4, "laps": 3}}, or the first builder's flat "rule"
        var trig = root["trigger"] as JsonObject ?? root;
        d.Trigger = Str(trig, trig == root ? "rule" : "kind", "gain", 12) switch
        {
            "everylaps" => "everylaps", "above" => "above", "manual" => "manual", _ => "gain",
        };
        d.Amount = (int)Math.Clamp(Num(trig, "amount", 4), 1, 100);
        d.Laps = (int)Math.Clamp(Num(trig, "laps", 3), 1, 50);
        d.Threshold = Math.Clamp(Num(trig, "threshold", 0), -1e6, 1e6);
        d.Sponsor = root["sponsor"] switch
        {
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "slot" : "none",
            JsonValue v when v.TryGetValue<string>(out var s) => s is "slot" || Enum.TryParse<Partner>(s, true, out _) ? s : "none",
            _ => "none",
        };
        if (Str(root, "accent", "", 9) is { Length: 7 } hex && hex[0] == '#' && SKColor.TryParse(hex, out var col)) d.Accent = col;
        return d;
    }

    static string Str(JsonObject o, string key, string fallback, int max)
    {
        if (o[key] is JsonValue v && v.TryGetValue<string>(out var s))
        {
            s = s.Trim();
            var clean = new string(s.Where(ch => !char.IsControl(ch)).ToArray());
            if (clean.Length == 0) return fallback;
            return clean.Length > max ? clean[..max] : clean;
        }
        return fallback;
    }

    static double Num(JsonObject o, string key, double fallback)
    {
        if (o[key] is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d)) return d;
        }
        return fallback;
    }

    // ------------------------------------------------------------------ deciding (render thread)

    sealed class Showing
    {
        public required CustomCardDef Def;
        public int SubjectIdx = -1, OtherIdx = -1;
        public string Big = "", Sub = "", Name = "", Meta = "";
        public (string Pos, string Name, string Value)[] Rows = Array.Empty<(string, string, string)>();
        public (string Label, string A, string B)[] Compare = Array.Empty<(string, string, string)>();
        public string NameA = "", NameB = "", ColA = "", ColB = "";
        public double StartMs, EndMs;
        public Partner Partner;
    }

    static Showing? showing;
    static double nextDecideMs, revisionAtMs = -1e9;
    static int appliedVersion = -1;
    static readonly Dictionary<CustomCardDef, double> lastShown = new();
    static readonly Dictionary<CustomCardDef, double> lastAbove = new();
    static readonly Dictionary<CustomCardDef, int> lastLapFired = new();
    // per car: class position at each completed lap (for "gains N places in L laps")
    static readonly Dictionary<int, List<(int Lap, int Pos)>> history = new();
    const double FadeMs = 350;

    public static bool Visible => showing != null;
    /// <summary>True while fading in/out, or just after its values refreshed (the compositor presents then).</summary>
    public static bool Animating(double nowMs)
    {
        var s = showing;
        if (s == null) return false;
        return nowMs - s.StartMs < FadeMs + 50 || s.EndMs - nowMs < FadeMs + 50 || nowMs - revisionAtMs < 120;
    }

    /// <summary>Called once per compositor tick. Cheap when no cards are saved.</summary>
    public static void Tick(RaceSnapshot snap, double nowMs, bool storyUp)
    {
        if (Version != appliedVersion)
        {
            appliedVersion = Version;
            lastShown.Clear(); lastAbove.Clear(); lastLapFired.Clear();
            showing = null;
        }
        var cards = defs;
        while (Requests.TryDequeue(out var req))
        {
            var d = cards.FirstOrDefault(c => string.Equals(c.Id, req, StringComparison.OrdinalIgnoreCase));
            if (d != null) Start(d, snap, nowMs, Subject(snap), forced: true);
        }
        if (cards.Length == 0 && showing == null) return;

        if (showing is { } s)
        {
            if (nowMs >= s.EndMs) showing = null;
            else if (storyUp && s.EndMs - nowMs > FadeMs) s.EndMs = nowMs + FadeMs;   // a story card wins the screen
        }
        if (nowMs < nextDecideMs) return;
        nextDecideMs = nowMs + 500;
        if (!snap.IsConnected && cards.Length > 0 && showing == null) return;

        RecordHistory(snap);
        if (showing is { } cur) { Fill(cur, snap); revisionAtMs = nowMs; return; }
        if (storyUp || OutroDirector.FullScreen || snap.Phase != RacePhase.Broadcast) return;

        foreach (var d in cards)
        {
            if (!d.Enabled || d.Trigger == "manual") continue;
            if (lastShown.TryGetValue(d, out var last) && nowMs - last < d.CooldownMinutes * 60000) continue;
            int subject = Evaluate(d, snap);
            if (subject == int.MinValue) continue;
            Start(d, snap, nowMs, subject >= 0 ? Find(snap, subject) : Subject(snap), forced: false);
            break;
        }
    }

    static void Start(CustomCardDef d, RaceSnapshot snap, double nowMs, Driver? subject, bool forced)
    {
        var s = new Showing { Def = d, SubjectIdx = subject?.CarIdx ?? -1, StartMs = nowMs, EndMs = nowMs + d.Seconds * 1000 };
        s.Partner = d.Sponsor == "slot" ? SponsorPack.For("CustomCards")
                  : Enum.TryParse<Partner>(d.Sponsor, true, out var p) && SponsorPack.IsOn(p) ? p : Partner.None;
        try { Fill(s, snap); } catch { return; }
        StorySound.PlayBroadcast("custom_in");
        showing = s;
        lastShown[d] = nowMs;
        revisionAtMs = nowMs;
        if (forced) nextDecideMs = nowMs + 500;
    }

    static void RecordHistory(RaceSnapshot snap)
    {
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var dr in cls.Drivers)
            {
                if (dr.IsPaceCar || dr.IsSpectator || dr.PositionInClass <= 0) continue;
                if (!history.TryGetValue(dr.CarIdx, out var h)) history[dr.CarIdx] = h = new List<(int, int)>(24);
                if (h.Count > 0 && h[^1].Lap == dr.LapsCompleted) { h[^1] = (dr.LapsCompleted, dr.PositionInClass); continue; }
                if (h.Count > 0 && dr.LapsCompleted < h[^1].Lap) h.Clear();   // new session / replay rewind
                h.Add((dr.LapsCompleted, dr.PositionInClass));
                if (h.Count > 24) h.RemoveAt(0);
            }
    }

    /// <summary>Returns the subject CarIdx when the rule fires (-1 = fires with no particular car), int.MinValue when not.</summary>
    static int Evaluate(CustomCardDef d, RaceSnapshot snap)
    {
        switch (d.Trigger)
        {
            case "gain":
            {
                int best = int.MinValue, bestGain = 0;
                foreach (var (idx, h) in history)
                {
                    if (h.Count < 2) continue;
                    var nowPos = h[^1];
                    for (int i = h.Count - 2; i >= 0 && nowPos.Lap - h[i].Lap <= d.Laps; i--)
                    {
                        int gain = h[i].Pos - nowPos.Pos;
                        if (gain >= d.Amount && gain > bestGain && Find(snap, idx) is { IsInPit: false }) { bestGain = gain; best = idx; }
                    }
                }
                if (best != int.MinValue) history[best].Clear();   // the same charge won't fire twice
                return best;
            }
            case "everylaps":
            {
                int lap = snap.Leader?.LapsCompleted ?? 0;
                if (lap <= 0 || lap % d.Amount != 0) return int.MinValue;
                if (lastLapFired.TryGetValue(d, out var l) && l == lap) return int.MinValue;
                lastLapFired[d] = lap;
                return -1;
            }
            case "above":
            {
                var who = Subject(snap);
                if (who == null || !double.TryParse(Value(d.Field, who, snap, raw: true), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return int.MinValue;
                bool wasAbove = lastAbove.TryGetValue(d, out var prev) && prev > d.Threshold;
                lastAbove[d] = v;
                return v > d.Threshold && !wasAbove ? who.CarIdx : int.MinValue;
            }
        }
        return int.MinValue;
    }

    static Driver? Subject(RaceSnapshot snap) => snap.CameraTarget ?? snap.Player ?? snap.Leader;

    static Driver? Find(RaceSnapshot snap, int carIdx)
    {
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var dr in cls.Drivers)
                if (dr.CarIdx == carIdx) return dr;
        return null;
    }

    static DriverClass? ClassOf(RaceSnapshot snap, Driver? d)
    {
        if (d == null) return snap.ClassLeaderboard.Count > 0 ? snap.ClassLeaderboard[0] : null;
        foreach (var cls in snap.ClassLeaderboard)
            foreach (var dr in cls.Drivers)
                if (dr.CarIdx == d.CarIdx) return cls;
        return null;
    }

    /// <summary>The fields the builder offers before any iRacing variable.</summary>
    public static readonly string[] BuiltInFields =
        { "PlacesGained", "Position", "PositionOverall", "BestLap", "LastLap", "Gap", "Interval", "Incidents", "iRating", "LapsCompleted" };

    static string Value(string field, Driver d, RaceSnapshot snap, bool raw = false)
    {
        var inv = CultureInfo.InvariantCulture;
        switch (field)
        {
            case "PlacesGained":
                int g = snap.GridClassPositions.TryGetValue(d.CarIdx, out var start) && start > 0 ? start - d.PositionInClass : 0;
                return raw ? g.ToString(inv) : (g > 0 ? "+" : "") + g.ToString(inv);
            case "Position": return raw ? d.PositionInClass.ToString(inv) : "P" + d.PositionInClass.ToString(inv);
            case "PositionOverall": return raw ? d.Position.ToString(inv) : "P" + d.Position.ToString(inv);
            case "BestLap": return raw ? d.BestLapTime.ToString(inv) : d.BestLapTime > 0 ? LapTimeFormat.Lap(d.BestLapTime) : "-";
            case "LastLap": return raw ? d.LastLapTime.ToString(inv) : d.LastLapTime > 0 ? LapTimeFormat.Lap(d.LastLapTime) : "-";
            case "Gap": return raw ? (d.GapToLeaderSeconds ?? 0).ToString(inv) : LapTimeFormat.Gap(d.GapToLeaderSeconds, d.LapDifference);
            case "Interval": return raw ? (d.IntervalSeconds ?? 0).ToString(inv) : LapTimeFormat.Gap(d.IntervalSeconds);
            case "Incidents": { int n = RaceDiary.IncOf(d); return n.ToString(inv) + (raw ? "" : "x"); }
            case "iRating": return d.IRating.ToString(inv);
            case "LapsCompleted": return d.LapsCompleted.ToString(inv);
        }
        // an iRacing telemetry variable: CarIdx arrays are read for this car, everything else as-is
        var read = ReadVar;
        if (read == null) return "-";
        object? o;
        try { o = read(field, field.StartsWith("CarIdx", StringComparison.Ordinal) ? d.CarIdx : 0); } catch { o = null; }
        return o switch
        {
            null => "-",
            float f => f.ToString(raw ? "R" : Math.Abs(f) >= 100 ? "0" : "0.##", inv),
            double x => x.ToString(raw ? "R" : Math.Abs(x) >= 100 ? "0" : "0.##", inv),
            bool b => raw ? (b ? "1" : "0") : b ? "YES" : "NO",
            _ => Convert.ToString(o, inv) ?? "-",
        };
    }

    static string Short(Driver d) => NameFormat.Broadcast(d).ToUpperInvariant();

    static void Fill(Showing s, RaceSnapshot snap)
    {
        var d = s.Def;
        var who = s.SubjectIdx >= 0 ? Find(snap, s.SubjectIdx) : Subject(snap);
        switch (d.Template)
        {
            case "number":
                if (who == null) { s.Big = "-"; s.Name = ""; s.Meta = ""; break; }
                s.Big = Value(d.Field, who, snap);
                s.Sub = d.Label.Length > 0 ? d.Label.ToUpperInvariant()
                      : d.Trigger == "gain" ? $"PLACES IN {d.Laps} LAP{(d.Laps == 1 ? "" : "S")}" : FieldLabel(d.Field);
                s.Name = Short(who);
                s.Meta = $"#{who.CarNumber} · {who.ClassName} · P{who.PositionInClass}";
                break;
            case "top5":
            {
                var cls = ClassOf(snap, who);
                if (cls == null) { s.Rows = Array.Empty<(string, string, string)>(); break; }
                IEnumerable<Driver> drivers = cls.Drivers.Where(x => !x.IsPaceCar && !x.IsSpectator);
                drivers = d.Field switch
                {
                    "BestLap" => drivers.Where(x => x.BestLapTime > 0).OrderBy(x => x.BestLapTime),
                    "LastLap" => drivers.Where(x => x.LastLapTime > 0).OrderBy(x => x.LastLapTime),
                    "Incidents" => drivers.OrderBy(x => RaceDiary.IncOf(x)).ThenBy(x => x.PositionInClass),
                    "Position" or "PositionOverall" or "Gap" or "Interval" or "LapsCompleted" => drivers.OrderBy(x => x.PositionInClass),
                    _ => drivers.OrderByDescending(x => double.TryParse(Value(d.Field, x, snap, raw: true), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.MinValue),
                };
                s.Rows = drivers.Take(5).Select((x, i) => ((i + 1).ToString(CultureInfo.InvariantCulture), Short(x), Value(d.Field, x, snap))).ToArray();
                s.Sub = (d.Label.Length > 0 ? d.Label.ToUpperInvariant() : FieldLabel(d.Field)) + " · " + cls.Name;
                break;
            }
            case "h2h":
            {
                if (who == null) { s.Compare = Array.Empty<(string, string, string)>(); break; }
                var cls = ClassOf(snap, who);
                var other = s.OtherIdx >= 0 ? Find(snap, s.OtherIdx) : null;
                if (other == null && cls != null)
                {
                    int me = -1;
                    for (int i = 0; i < cls.Drivers.Count; i++) if (cls.Drivers[i].CarIdx == who.CarIdx) me = i;
                    other = me > 0 ? cls.Drivers[me - 1] : me >= 0 && me + 1 < cls.Drivers.Count ? cls.Drivers[me + 1] : null;
                    s.OtherIdx = other?.CarIdx ?? -1;
                }
                if (other == null) { s.Compare = Array.Empty<(string, string, string)>(); break; }
                var (a, b) = other.PositionInClass < who.PositionInClass ? (other, who) : (who, other);
                s.NameA = Short(a); s.NameB = Short(b);
                s.ColA = "#" + a.CarNumber; s.ColB = "#" + b.CarNumber;
                var rows = new List<(string, string, string)>
                {
                    ("POSITION", "P" + a.PositionInClass, "P" + b.PositionInClass),
                    ("LAST LAP", Value("LastLap", a, snap), Value("LastLap", b, snap)),
                    ("BEST LAP", Value("BestLap", a, snap), Value("BestLap", b, snap)),
                    ("INTERVAL", Value("Interval", a, snap), Value("Interval", b, snap)),
                };
                if (!rows.Any(r => r.Item1 == FieldLabel(d.Field)) && d.Field is not ("Position" or "LastLap" or "BestLap" or "Interval"))
                    rows.Add((d.Label.Length > 0 ? d.Label.ToUpperInvariant() : FieldLabel(d.Field), Value(d.Field, a, snap), Value(d.Field, b, snap)));
                s.Compare = rows.ToArray();
                s.Sub = cls?.Name ?? "";
                break;
            }
        }
    }

    static string FieldLabel(string f) => f switch
    {
        "PlacesGained" => "PLACES GAINED",
        "PositionOverall" => "OVERALL",
        "BestLap" => "BEST LAP",
        "LastLap" => "LAST LAP",
        "iRating" => "IRATING",
        "LapsCompleted" => "LAPS",
        _ => f.ToUpperInvariant(),
    };

    // ------------------------------------------------------------------ drawing (render thread)

    public const float CardW = 560f;
    static readonly SKPaint Fill0 = new() { IsAntialias = true };
    static readonly SKPaint Text0 = new() { IsAntialias = true };

    /// <summary>Card height for the template (the compositor anchors it under the session bar, right side).</summary>
    public static float Height => showing?.Def.Template switch { "top5" => 372f, "h2h" => 140f + 44f * Math.Max(1, showing.Compare.Length), _ => 252f };

    public static void Draw(SKCanvas c, float right, float top, double nowMs)
    {
        var s = showing;
        if (s == null) return;
        float a = (float)Math.Clamp(Math.Min((nowMs - s.StartMs) / FadeMs, (s.EndMs - nowMs) / FadeMs), 0, 1);
        if (a <= 0.001f) return;
        float ease = 1 - (1 - a) * (1 - a);
        float h = Height, x = right - CardW + (1 - ease) * 40f, y = top;
        byte A(float k) => (byte)Math.Clamp(255 * a * k, 0, 255);
        var accent = s.Def.Accent;
        var card = new SKRect(x, y, x + CardW, y + h);

        Fill0.Shader = null;
        Fill0.Color = new SKColor(0x0B, 0x0F, 0x17, A(0.92f));
        c.DrawRoundRect(card, 10, 10, Fill0);
        Fill0.Color = accent.WithAlpha(A(1));
        c.DrawRect(new SKRect(x, y, x + CardW, y + 5), Fill0);

        // header: LIVE pill, headline, sponsor
        float pad = 28;
        Fill0.Color = new SKColor(0xD8, 0x1E, 0x2A, A(1));
        c.DrawRoundRect(new SKRect(x + pad, y + 26, x + pad + 46, y + 46), 3, 3, Fill0);
        Txt(c, "LIVE", x + pad + 23, y + 41, Fonts.Inter.Bold, 13, SKColors.White, a, center: true, maxW: 40);   // #340: was 12
        if (s.Partner != Partner.None) Presenters.DrawPresentedBy(c, s.Partner, "PRESENTED BY", x + CardW - pad, y + 36, 22, a);
        Txt(c, s.Def.Name.ToUpperInvariant(), x + pad, y + 92, Fonts.BarlowCondensed.ExtraBold, 40, SKColors.White, a, maxW: CardW - pad * 2);
        Txt(c, s.Sub, x + pad, y + 118, Fonts.Inter.SemiBold, 14, Dim, a, maxW: CardW - pad * 2);

        switch (s.Def.Template)
        {
            case "number":
                Txt(c, s.Big, x + pad, y + 200, Fonts.BarlowCondensed.Black, 84, accent, a, maxW: 260);
                Txt(c, s.Name, x + CardW - pad, y + 186, Fonts.BarlowCondensed.Bold, 30, SKColors.White, a, right: true, maxW: 240);
                Txt(c, s.Meta, x + CardW - pad, y + 212, Fonts.Inter.Medium, 14, Dim, a, right: true, maxW: 240);
                break;
            case "top5":
                for (int i = 0; i < s.Rows.Length; i++)
                {
                    float ry = y + 146 + i * 44;
                    Fill0.Color = (i == 0 ? accent : new SKColor(0xFF, 0xFF, 0xFF)).WithAlpha(A(i == 0 ? 0.16f : 0.04f));
                    c.DrawRoundRect(new SKRect(x + pad - 8, ry, x + CardW - pad + 8, ry + 38), 6, 6, Fill0);
                    var (pos, name, val) = s.Rows[i];
                    Txt(c, pos, x + pad + 10, ry + 27, Fonts.BarlowCondensed.Black, 24, i == 0 ? accent : SKColors.White, a, center: true);
                    Txt(c, name, x + pad + 40, ry + 27, Fonts.BarlowCondensed.Bold, 24, SKColors.White, a, maxW: 300);
                    Txt(c, val, x + CardW - pad, ry + 27, Fonts.BarlowCondensed.SemiBold, 24, SKColors.White, a, right: true, maxW: 150);
                }
                if (s.Rows.Length == 0) Txt(c, "No data yet", x + pad, y + 170, Fonts.Inter.Medium, 16, Dim, a);
                break;
            case "h2h":
            {
                float mid = x + CardW / 2;
                Txt(c, s.NameA, mid - 70, y + 156, Fonts.BarlowCondensed.Bold, 26, SKColors.White, a, right: true, maxW: 190);
                Txt(c, s.NameB, mid + 70, y + 156, Fonts.BarlowCondensed.Bold, 26, SKColors.White, a, maxW: 190);
                Txt(c, "VS", mid, y + 156, Fonts.BarlowCondensed.Black, 22, accent, a, center: true);
                for (int i = 0; i < s.Compare.Length; i++)
                {
                    float ry = y + 172 + i * 44;
                    var (label, va, vb) = s.Compare[i];
                    Fill0.Color = new SKColor(0xFF, 0xFF, 0xFF).WithAlpha(A(0.04f));
                    c.DrawRoundRect(new SKRect(x + pad - 8, ry, x + CardW - pad + 8, ry + 38), 6, 6, Fill0);
                    Txt(c, va, mid - 70, ry + 27, Fonts.BarlowCondensed.SemiBold, 24, SKColors.White, a, right: true, maxW: 160);
                    Txt(c, label, mid, ry + 26, Fonts.Inter.Bold, 13, Dim, a, center: true, maxW: 130);   // #340: was 11
                    Txt(c, vb, mid + 70, ry + 27, Fonts.BarlowCondensed.SemiBold, 24, SKColors.White, a, maxW: 160);
                }
                if (s.Compare.Length == 0) Txt(c, "No rival close enough yet", x + pad, y + 170, Fonts.Inter.Medium, 16, Dim, a);
                break;
            }
        }
    }

    // #340 (legibility from across the room): the card's grey text was 9AA4B4 (~64%)
    static readonly SKColor Dim = new(0xC6, 0xCC, 0xD6);

    static void Txt(SKCanvas c, string text, float x, float y, SKTypeface tf, float size, SKColor col, float a,
                    bool right = false, bool center = false, float maxW = 0)
    {
        if (string.IsNullOrEmpty(text)) return;
        Text0.Typeface = tf; Text0.TextSize = size; Text0.Color = col.WithAlpha((byte)(col.Alpha * Math.Clamp(a, 0, 1)));
        float w = Text0.MeasureText(text);
        if (maxW > 0 && w > maxW) { Text0.TextSize = size * maxW / w; w = maxW; }
        float tx = right ? x - w : center ? x - w / 2 : x;
        c.DrawText(text, tx, y, Text0);
    }
}
