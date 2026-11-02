using System.Globalization;
using System.IO;
using System.Text.Json;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// 2026-10-29 (#576): everyone seen during this stream, for the end-of-race thank-you roll. A name is recorded when its
/// owner chats, joins (Streamer.bot "present viewers" / "user joined"), becomes a member / subscriber or sends money -
/// YouTube and Twitch don't tell anyone about silent viewers, so they can't be on it (the roll never says otherwise).
/// Names are recorded before the comment switch, the word filter on the message and the comment rate limit (a name that
/// fails the word filter itself is left out); duplicates are merged ignoring case. Money is summed per person in the unit
/// it came in (a person who gave in two currencies shows no total - nothing is converted on screen).
/// Kept in %LOCALAPPDATA%\Grandstand\viewer_names.json so a restart mid-stream keeps the list; a list untouched for
/// <see cref="StaleHours"/> hours starts fresh (the next stream), and the control app can clear it.
/// </summary>
public static class ViewerRoll
{
    public const int MaxPeople = 5000;
    public const double StaleHours = 8;

    /// <summary>One person on the roll.</summary>
    public sealed class Person
    {
        public string Name { get; set; } = "";
        public bool Chatted { get; set; }
        public bool Member { get; set; }
        public int Gifts { get; set; }
        /// <summary>Dollar-equivalent total (plain numbers, bits / 100) - only for ranking and picking the tier page.</summary>
        public double Usd { get; set; }
        /// <summary>The unit of the money ("$", "€", "£", "BITS", or "?" when mixed) and its total in that unit.</summary>
        public string Unit { get; set; } = "";
        public double Total { get; set; }
        public long First { get; set; }
        public bool Supporter => Usd > 0 || Member || Gifts > 0;
    }

    sealed class FileModel
    {
        public string Started { get; set; } = "";
        public string Updated { get; set; } = "";
        public List<Person> People { get; set; } = new();
    }

    static readonly object gate = new();
    static readonly Dictionary<string, Person> people = new(StringComparer.OrdinalIgnoreCase);
    static DateTime started = DateTime.Now;
    static bool loaded, dirty;
    static long lastSaveMs = long.MinValue / 2, seq;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Grandstand", "viewer_names.json");

    /// <summary>Remembers one viewer (any thread). <paramref name="name"/> is already cleaned; blocked / empty names are skipped.</summary>
    public static void Record(ViewerKind kind, string name, string amount, double? usd, int gifts)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "A viewer" || kind == ViewerKind.Follow) return;
        lock (gate)
        {
            EnsureLoaded();
            bool changed = false;
            if (!people.TryGetValue(name, out var p))
            {
                if (people.Count >= MaxPeople) return;
                p = new Person { Name = name, First = ++seq };
                people[name] = p;
                changed = true;
            }
            switch (kind)
            {
                case ViewerKind.Comment: if (!p.Chatted) { p.Chatted = true; changed = true; } break;
                case ViewerKind.Member or ViewerKind.Sub:
                    // #603: a gifter isn't a member themselves - only their own membership / sub marks them
                    if (gifts > 0) { p.Gifts += gifts; changed = true; }
                    else if (!p.Member) { p.Member = true; changed = true; }
                    break;
                case ViewerKind.SuperChat or ViewerKind.Tip or ViewerKind.Bits:
                    if (usd is double u && u > 0)
                    {
                        p.Usd = Math.Round(p.Usd + u, 2);
                        string unit = UnitOf(kind, amount);
                        double raw = ViewerFeed.ParseMoney(amount)?.Value ?? 0;
                        if (p.Unit.Length == 0) { p.Unit = unit; p.Total = raw; }
                        else if (p.Unit == unit) p.Total = Math.Round(p.Total + raw, 2);
                        else p.Unit = "?";
                        changed = true;
                    }
                    break;
            }
            if (changed) { dirty = true; SaveSoon(); }
        }
    }

    static string UnitOf(ViewerKind kind, string amount)
    {
        if (kind == ViewerKind.Bits || amount.Contains("bit", StringComparison.OrdinalIgnoreCase)) return "BITS";
        if (amount.Contains('€') || amount.Contains("EUR", StringComparison.OrdinalIgnoreCase)) return "€";
        if (amount.Contains('£') || amount.Contains("GBP", StringComparison.OrdinalIgnoreCase)) return "£";
        var m = ViewerFeed.ParseMoney(amount);
        return m is { Usd: true } ? "$" : "?";
    }

    /// <summary>The person's total as it came in: "$75", "€20", "1,500 BITS"; empty when mixed currencies.</summary>
    public static string TotalText(Person p) => p.Unit switch
    {
        "BITS" => p.Total.ToString("N0", CultureInfo.InvariantCulture) + " BITS",
        "$" or "€" or "£" => p.Unit + (p.Total % 1 == 0 ? p.Total.ToString("N0", CultureInfo.InvariantCulture) : p.Total.ToString("N2", CultureInfo.InvariantCulture)),
        _ => "",
    };

    /// <summary>A copy of everyone, in the order they first showed up.</summary>
    public static List<Person> Snapshot()
    {
        lock (gate)
        {
            EnsureLoaded();
            if (dirty) Save();
            return people.Values.OrderBy(p => p.First).Select(p => new Person
            {
                Name = p.Name, Chatted = p.Chatted, Member = p.Member, Gifts = p.Gifts, Usd = p.Usd, Unit = p.Unit, Total = p.Total, First = p.First,
            }).ToList();
        }
    }

    public static int Count { get { lock (gate) { EnsureLoaded(); return people.Count; } } }

    /// <summary>#603: how many on the roll are supporters (paid, members, subs, gifts) - for the control app.</summary>
    public static int Supporters { get { lock (gate) { EnsureLoaded(); int n = 0; foreach (var p in people.Values) if (p.Supporter) n++; return n; } } }

    /// <summary>#603: when this stream's list was started (the control app shows it next to "New stream").</summary>
    public static DateTime Started { get { lock (gate) { EnsureLoaded(); return started; } } }

    /// <summary>Starts a fresh list (control app "Clear names").</summary>
    public static void Clear()
    {
        lock (gate) { loaded = true; people.Clear(); seq = 0; started = DateTime.Now; dirty = true; Save(); }
    }

    static void EnsureLoaded()
    {
        if (loaded) return;
        loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath));
            if (m == null) return;
            DateTime.TryParse(m.Updated, CultureInfo.InvariantCulture, DateTimeStyles.None, out var upd);
            if (upd == default || (DateTime.Now - upd).TotalHours > StaleHours)
            {
                ViewerFeed.Log?.Invoke("thank-you roll: last list is from an earlier stream - starting a new one");
                return;
            }
            DateTime.TryParse(m.Started, CultureInfo.InvariantCulture, DateTimeStyles.None, out started);
            if (started == default) started = upd;
            foreach (var p in m.People.Take(MaxPeople))
            {
                if (string.IsNullOrWhiteSpace(p.Name) || people.ContainsKey(p.Name)) continue;
                p.First = ++seq;
                people[p.Name] = p;
            }
            ViewerFeed.Log?.Invoke($"thank-you roll: {people.Count} names kept from earlier in this stream");
        }
        catch (Exception ex) { ViewerFeed.Log?.Invoke("thank-you roll: could not read viewer_names.json: " + ex.Message); }
    }

    static void SaveSoon()
    {
        long now = Environment.TickCount64;
        if (now - lastSaveMs >= 2000) Save();
    }

    static void Save()
    {
        lastSaveMs = Environment.TickCount64;
        dirty = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var m = new FileModel
            {
                Started = started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Updated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                People = people.Values.OrderBy(p => p.First).ToList(),
            };
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(m));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { ViewerFeed.Log?.Invoke("thank-you roll: could not save viewer_names.json: " + ex.Message); }
    }

    /// <summary>Writes anything still waiting (called by the renderer now and then).</summary>
    public static void Flush() { lock (gate) { if (dirty && Environment.TickCount64 - lastSaveMs >= 2000) Save(); } }

    /// <summary>Sample people for the control app's roll test and the preview tool (never saved).</summary>
    public static List<Person> Sample(int viewers = 34)
    {
        string[] names =
        {
            "RacingDad_74", "Lena Hoffmann", "apex_hunter", "Marco Bellini", "TurboTortoise", "Sofia Marques", "kerb_crawler",
            "Jordan Lee", "NightStint", "Aiko Tanaka", "BoxBoxBox", "Pierre Duval", "slipstream_sam", "Grace O'Neill",
            "LateBraker99", "Diego Ramírez", "pitwall_pete", "Hannah Schmidt", "Mulsanne Mike", "Chen Wei", "flatout_fran",
            "Tomás Novák", "Eau Rouge Ed", "Priya Patel", "GT3_Gary", "Olivia Brown", "trailbrake_tim", "Mateus Costa",
            "Le Mans Lou", "Kofi Mensah", "rainmaster", "Isabella Rossi", "dive_bomb_dan", "Freya Larsen", "SimRigSteve",
            "Yusuf Demir", "chicane_charlie", "Emma Dubois", "hotlap_hugo", "Nina Kowalski",
        };
        var list = new List<Person>();
        for (int i = 0; i < Math.Min(viewers, names.Length); i++) list.Add(new Person { Name = names[i], Chatted = true, First = i });
        void Give(int i, double usd, string unit, double total, bool member = false, int gifts = 0)
        {
            var p = list[i]; p.Usd = usd; p.Unit = unit; p.Total = total; p.Member = member; p.Gifts = gifts;
        }
        Give(1, 100, "$", 100); Give(3, 75, "$", 75); Give(9, 50, "€", 50);
        Give(5, 25, "$", 25); Give(13, 20, "BITS", 2000); Give(7, 15, "$", 15); Give(17, 10, "£", 10); Give(11, 12, "$", 12, gifts: 5, member: true);
        Give(0, 5, "$", 5); Give(2, 2, "$", 2); Give(19, 5, "BITS", 500); Give(21, 0, "", 0, member: true); Give(23, 3, "$", 3);
        Give(25, 0, "", 0, member: true); Give(27, 1, "BITS", 100);
        return list;
    }
}
