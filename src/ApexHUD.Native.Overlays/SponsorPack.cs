using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Who presents what on the broadcast, read from Documents\iRacing\Grandstand\sponsors.json
/// (2026-10-23). The built-in defaults are Angelo's current partners (A&amp;B, StormExhibit,
/// GO FAST, Trading Paints), so with no file - or a broken one - everything looks exactly as
/// before. The file is written with those defaults the first time Grandstand runs, so it can
/// be edited: switch a partner off, point a card at a different partner, or use the "Custom"
/// slot with your own logo (put the png in that folder or in assets\logos\sponsors).
///
/// 2026-10-24: those owner defaults apply only when his logo file is installed (<see cref="PersonalDefaults"/>);
/// a public install gets neutral defaults ("YOUR BRAND HERE") instead.
///
/// Read once at startup on first use; nothing here runs per frame except dictionary lookups.
/// 2026-10-24: the control app's Event &amp; sponsors screen edits it live - <see cref="Save"/> parses
/// the new pack into fresh tables and swaps them in whole (readers never see a half-written pack),
/// then bumps <see cref="Version"/> so cached logo bitmaps are dropped on the render thread.
/// </summary>
public static class SponsorPack
{
    public sealed class PartnerInfo
    {
        public bool On = true;
        public string? Logo;     // file name (Grandstand folder, then assets\logos\sponsors) or full path
        public string? Word;     // drawn wordmark when there is no logo file
    }

    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand");
    public static readonly string FilePath = Path.Combine(Folder, "sponsors.json");

    static volatile Dictionary<Partner, PartnerInfo> partners = Defaults();
    static volatile Dictionary<string, Partner> presents = DefaultPresents();
    static bool loaded;
    static int version;

    /// <summary>Bumped whenever the pack is replaced; logo caches compare it and reload.</summary>
    public static int Version => Volatile.Read(ref version);

    /// <summary>Every slot a partner can present, in the order the control app lists them.</summary>
    public static IReadOnlyList<string> Slots => DefaultPresents().Keys.ToList();
    static readonly object gate = new();

    /// <summary>True on the owner's PC: his business logo (assets\logos\sponsors\AB_GC_Logo.png, never shipped)
    /// is installed, so the built-in defaults are his partners exactly as before. Without it (every public
    /// install) the defaults are neutral: no real company, one "YOUR BRAND HERE" slot (2026-10-24).</summary>
    public static bool PersonalDefaults
    {
        get
        {
            // not a field initializer: the partner tables above are built from this during type init
            if (personalDefaults is bool b) return b;
            try { b = File.Exists(Assets.Resolve("logos", "sponsors", "AB_GC_Logo.png")); } catch { b = false; }
            personalDefaults = b;
            return b;
        }
    }
    static bool? personalDefaults;

    static Dictionary<Partner, PartnerInfo> Defaults() => PersonalDefaults ? OwnerDefaults() : PublicDefaults();
    static Dictionary<string, Partner> DefaultPresents() => PersonalDefaults ? OwnerPresents() : PublicPresents();

    /// <summary>Public defaults: every named partner off and without a logo; the Custom slot on as
    /// "YOUR BRAND HERE" (drawn wordmark, no file) on the cards that carried a sponsor.</summary>
    static Dictionary<Partner, PartnerInfo> PublicDefaults() => new()
    {
        [Partner.AB] = new() { On = false, Logo = "", Word = "" },
        [Partner.Storm] = new() { On = false, Logo = "", Word = "" },
        [Partner.GoFast] = new() { On = false, Logo = "", Word = "" },
        [Partner.TradingPaints] = new() { On = false, Logo = "", Word = "" },
        [Partner.Custom] = new() { On = true, Logo = "", Word = "YOUR BRAND HERE" },
    };

    static Dictionary<string, Partner> PublicPresents()
    {
        var d = OwnerPresents();
        foreach (var k in d.Keys.ToList())
            d[k] = d[k] is Partner.AB or Partner.GoFast or Partner.TradingPaints && !k.StartsWith("PreRacePanel", StringComparison.Ordinal)
                ? Partner.Custom : Partner.None;
        return d;
    }

    static Dictionary<Partner, PartnerInfo> OwnerDefaults() => new()
    {
        [Partner.AB] = new() { Logo = "AB_GC_Logo.png" },
        [Partner.Storm] = new(),
        [Partner.GoFast] = new() { Logo = "gofast_logo.png", Word = "GO FAST" },
        [Partner.TradingPaints] = new() { Logo = "tradingpaints_logo.png", Word = "TRADING PAINTS" },
        [Partner.Custom] = new() { On = false, Logo = "", Word = "YOUR TEAM" },
    };

    static Dictionary<string, Partner> OwnerPresents() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["RaceSoFar"] = Partner.AB, ["MoveOfRace"] = Partner.AB, ["WeatherWatch"] = Partner.Storm,
        ["FastestLaps"] = Partner.GoFast, ["Limits"] = Partner.GoFast,
        ["CarContact"] = Partner.TradingPaints, ["Cleanest"] = Partner.TradingPaints,
        ["YourRace"] = Partner.None, ["PitStops"] = Partner.None, ["Halfway"] = Partner.None, ["DriverOfDay"] = Partner.None,
        ["UnderPressure"] = Partner.AB, ["WeatherBar"] = Partner.Storm,
        // the two pre-race partner panels (A&B roof, StormExhibit radar)
        ["PreRacePanelAB"] = Partner.AB, ["PreRacePanelStorm"] = Partner.Storm,
        // 2026-10-24: cards made in the control app's card builder that ask for "the pack's slot"
        ["CustomCards"] = Partner.None,
        // 2026-10-27 (visual pack round 2): the new sponsored graphics. Weather card = StormExhibit (Angelo); the strategy
        // board takes its WHOLE look from its partner's logo file (shape + colour), so it waits for one; the race awards
        // each have their own partner slot; the replay bumper has its own (falls back to the Race So Far partner).
        ["WeatherCard"] = Partner.Storm, ["StrategyBoard"] = Partner.None, ["ReplayBumper"] = Partner.AB,
        ["AwardStint"] = Partner.None, ["AwardLapsLed"] = Partner.None, ["AwardConsistent"] = Partner.None, ["AwardPhotoFinish"] = Partner.None,
    };

    static void Ensure()
    {
        if (loaded) return;
        lock (gate)
        {
            if (loaded) return;
            try
            {
                if (File.Exists(FilePath)) Apply(File.ReadAllText(FilePath));
                else WriteDefaults();
            }
            catch { /* broken file: keep the built-in defaults */ }
            loaded = true;
        }
    }

    /// <summary>Parses a pack into new tables (built-in defaults underneath) and swaps them in.</summary>
    static void Apply(string json)
    {
        var (ps, pr) = Parse(json);
        partners = ps; presents = pr;
        Interlocked.Increment(ref version);
    }

    static (Dictionary<Partner, PartnerInfo>, Dictionary<string, Partner>) Parse(string json)
    {
        var partners = Defaults();
        var presents = DefaultPresents();
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        if (root == null) return (partners, presents);
        if (root["partners"] is JsonObject ps)
            foreach (var (name, node) in ps)
            {
                if (!Enum.TryParse<Partner>(name, true, out var p) || p == Partner.None || node is not JsonObject o) continue;
                var info = partners.TryGetValue(p, out var x) ? x : partners[p] = new PartnerInfo();
                if (o["on"] is JsonValue on && on.TryGetValue<bool>(out var b)) info.On = b;
                if (o["logo"] is JsonValue lg && lg.TryGetValue<string>(out var l)) info.Logo = l;
                if (o["word"] is JsonValue wd && wd.TryGetValue<string>(out var w)) info.Word = w;
            }
        if (root["presents"] is JsonObject pr)
            foreach (var (slot, node) in pr)
                if (node is JsonValue v && v.TryGetValue<string>(out var who))
                    presents[slot] = Enum.TryParse<Partner>(who, true, out var p) ? p : Partner.None;
        foreach (var i in partners.Values)
        {
            if (i.Logo is { Length: > 260 }) i.Logo = i.Logo[..260];
            if (i.Word is { Length: > 40 }) i.Word = i.Word[..40];
        }
        return (partners, presents);
    }

    /// <summary>The pack as JSON (what sponsors.json holds), for the control app.</summary>
    public static JsonObject Export()
    {
        Ensure();
        return ToJson(partners, presents);
    }

    /// <summary>Validates and saves a pack from the control app, then applies it live. Throws on a bad pack.</summary>
    public static void Save(string json)
    {
        var (ps, pr) = Parse(json);                 // throws on broken JSON: nothing is written
        var root = ToJson(ps, pr);
        Directory.CreateDirectory(Folder);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOpts));
        File.Move(tmp, FilePath, true);
        lock (gate) { partners = ps; presents = pr; loaded = true; Interlocked.Increment(ref version); }
    }

    /// <summary>Back to the built-in partners - Angelo's on his PC, the neutral public set elsewhere
    /// (<see cref="PersonalDefaults"/>) - and the file is rewritten with them.</summary>
    public static void Reset()
    {
        lock (gate)
        {
            partners = Defaults(); presents = DefaultPresents(); loaded = true;
            try { WriteDefaults(); } catch { }
            Interlocked.Increment(ref version);
        }
    }

    static readonly JsonSerializerOptions WriteOpts = new()
    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonObject ToJson(Dictionary<Partner, PartnerInfo> partners, Dictionary<string, Partner> presents)
    {
        var ps = new JsonObject();
        foreach (var (p, i) in partners)
            ps[p.ToString()] = new JsonObject { ["on"] = i.On, ["logo"] = i.Logo ?? "", ["word"] = i.Word ?? "" };
        var pr = new JsonObject();
        foreach (var (k, v) in presents) pr[k] = v == Partner.None ? "none" : v.ToString();
        return new JsonObject
        {
            ["_about"] = "Grandstand partners. Set \"on\": false to hide a partner everywhere. 'presents' says which partner " +
                         "presents each card (AB, Storm, GoFast, TradingPaints, Custom or none). Custom: put your logo png in " +
                         "this folder and its file name in \"logo\" (or leave it empty to draw the \"word\"). Edit it in the control app " +
                         "(Event & sponsors) for a live change, or edit here and restart Grandstand.",
            ["partners"] = ps,
            ["presents"] = pr,
        };
    }

    static void WriteDefaults()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, ToJson(Defaults(), DefaultPresents()).ToJsonString(WriteOpts));
    }

    public static bool IsOn(Partner p)
    {
        Ensure();
        return p != Partner.None && partners.TryGetValue(p, out var i) && i.On;
    }

    /// <summary>The partner for a slot (a StoryKind name, "UnderPressure", "WeatherBar", "PreRacePanelAB"...), None when off.</summary>
    public static Partner For(string slot)
    {
        Ensure();
        return presents.TryGetValue(slot, out var p) && IsOn(p) ? p : Partner.None;
    }

    public static PartnerInfo Info(Partner p)
    {
        Ensure();
        return partners.TryGetValue(p, out var i) ? i : new PartnerInfo { On = false };
    }

    /// <summary>Full path of the partner's logo file, or null.</summary>
    public static string? LogoPath(Partner p)
    {
        var f = Info(p).Logo;
        if (string.IsNullOrWhiteSpace(f)) return null;
        try
        {
            if (Path.IsPathRooted(f)) return File.Exists(f) ? f : null;
            var mine = Path.Combine(Folder, f);
            if (File.Exists(mine)) return mine;
            var bundled = Assets.Resolve("logos", "sponsors", f);
            return File.Exists(bundled) ? bundled : null;
        }
        catch { return null; }
    }
}
