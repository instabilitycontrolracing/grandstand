using System.IO;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApexHUD.Native.GpuHost;

/// <summary>Per-graphic settings the control app can change (2026-10-23).</summary>
internal sealed class ElementSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Offset in 1080p design pixels (the engine scales it with the rest of the layout).</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float Scale { get; set; } = 1f;
    // 2026-11-05 (card editor): how see-through the whole graphic is (1 = solid), and an optional picture for it -
    // a file in Documents\iRacing\Grandstand\card_images. "glass" = laid across the card behind the glass (only where
    // the card is drawn), "badge" = a small logo in the card's top-right corner.
    public float Opacity { get; set; } = 1f;
    public string Image { get; set; } = "";
    public string ImageMode { get; set; } = "glass";
    public float ImageOpacity { get; set; } = 0.3f;
}

/// <summary>Everything the Grandstand control app can set. Saved as JSON in
/// Documents\iRacing\Grandstand\control.json so it survives restarts.</summary>
internal sealed class ControlSettings
{
    /// <summary>"lean" (tower + session bar only), "broadcast" (full show) or "studio" (full show, editors open).</summary>
    public string Mode { get; set; } = "broadcast";
    /// <summary>Master switch: false hides every graphic.</summary>
    public bool OnAir { get; set; } = true;
    /// <summary>2026-11-05: the watch in the session bar; off = a small digital clock in its place.</summary>
    public bool ShowWatch { get; set; } = true;
    public Dictionary<string, ElementSettings> Elements { get; set; } = new();
    /// <summary>How long a story card stays on screen, seconds.</summary>
    public double StoryCardSeconds { get; set; } = 15;   // 2026-10-24 (Angelo): race graphics stay up longer
    /// <summary>Minimum minutes between routine story cards.</summary>
    public double StoryGapMinutes { get; set; } = 3;   // 2026-10-24: short races got none
    /// <summary>"auto" (light mode switches on by itself if frames get heavy), "full" or "light" (slower PCs).</summary>
    public string Performance { get; set; } = "auto";
    /// <summary>Temperatures on screen (2026-10-25): "auto" (iRacing's units setting), "F" or "C".</summary>
    public string TempUnit { get; set; } = "F";
    /// <summary>Replay director (2026-10-24): Grandstand steers the iRacing camera while you watch a
    /// replay or spectate. OFF by default; it switches itself off when you get in the car.</summary>
    public bool Director { get; set; }
    /// <summary>Director may jump back and replay a spin or crash in slow motion.</summary>
    public bool DirectorReplays { get; set; } = true;
    /// <summary>2026-10-25 (Angelo: "stay on my car for now, with an option to switch to other drivers in its UI,
    /// but default to my car"): false = the director never leaves your car, it only picks the camera for it.
    /// true = whole field (battles, passes, incidents and the leader anywhere).</summary>
    public bool DirectorField { get; set; }
    /// <summary>Custom cards from the card builder may play on air (they never cover a story card).</summary>
    public bool CustomCards { get; set; } = true;
    /// <summary>Class transition in the timing tower (2026-10-24): "auto" (your rollers / the owner's footage
    /// when present, else built-in animated), "builtin", "folder" (Documents\iRacing\Grandstand\transitions) or "off".</summary>
    public string Transitions { get; set; } = "auto";
    /// <summary>2026-10-26 (#30): send the finished broadcast to OBS through Spout2 (sender "Grandstand"),
    /// see-through parts included. OFF by default.</summary>
    public bool Spout { get; set; }
    /// <summary>With Spout on: the graphics go to the stream only - nothing is drawn on the driver's own screen.</summary>
    public bool StreamOnly { get; set; }
    /// <summary>2026-10-26 (#234): rain drops on the lens of trackside / TV cameras while it rains. ON by default.</summary>
    public bool LensRain { get; set; } = true;
    /// <summary>2026-10-26 (#235): the REPLAY / LIVE wipe when a replay starts or goes back to live, and the corner
    /// REPLAY tag while a replay plays behind live. ON by default.</summary>
    public bool ReplayWipes { get; set; } = true;
    /// <summary>2026-10-27: the loading splash (concept A "TRACE") on the TV / Apollo 4K screen while iRacing is in its
    /// menus or loading a session; it hands off to the game once the session is live. ON by default.</summary>
    public bool LoadingSplash { get; set; } = true;
    /// <summary>2026-10-28 (#487): write highlight markers (green flag, your passes, battles, fastest laps, yellows, pit
    /// stops, the flag) to Documents\iRacing\Grandstand\highlights - small files on this PC only. ON by default.</summary>
    public bool Highlights { get; set; } = true;
    /// <summary>2026-10-28 (#487): also tell OBS about each marker through its own remote-control link (obs-websocket,
    /// built into OBS 28+): a chapter in the recording (OBS 30.2+, Hybrid MP4) and, if OBS's Replay Buffer is running, a
    /// saved clip. Settings are read from OBS's own config on this PC. OFF by default.</summary>
    public bool HighlightsObs { get; set; }
    /// <summary>2026-11-06: play the highlight reel by itself after a race (once you leave the car, OBS running).</summary>
    public bool AutoReel { get; set; }
    /// <summary>2026-10-28 (#488): Grandstand switches OBS between its scenes (Racing / Battle / Story card / Onboard /
    /// Replay / Post-race / Starting soon) to match what's on screen - only while OBS shows one of those scenes.</summary>
    public bool ObsDirector { get; set; } = true;
    /// <summary>2026-10-27: SESSION REVIEW on the TV between live sessions (iRacing closed or in its menus): the last
    /// session's breakdown, corner by corner, on a loop. ON by default.</summary>
    public bool SessionReview { get; set; } = true;
    /// <summary>2026-10-27 (#115 / #118): the refined finish on the corner themes (CornerTheme.Refined). OFF by default
    /// until Angelo has seen the before / after previews.</summary>
    public bool CornerRefined { get; set; }
    /// <summary>2026-10-27 (#338): no story cards while Angelo is driving live and a battle is on (StoryDirector).
    /// ON by default; false brings back cards playing through battles.</summary>
    public bool NoCardsInBattleWhenDriving { get; set; } = true;
    /// <summary>2026-10-27 (#339): mini story cards during a battle while the full ones are held (#338). OFF by default -
    /// a preview until Angelo has seen the renders (tools/storycard_preview --mini).</summary>
    public bool MiniCardsInBattle { get; set; }
    /// <summary>2026-10-27 (#349 / #393): where the story cards sit. "mixed" (default: the right-third layouts in rotation,
    /// special partner cards in the tall column), "column", "halves", "flip", "corners", or "classic" (the old right-half card).</summary>
    public string CardLayout { get; set; } = "mixed";
    /// <summary>2026-10-27: the visual review pack (V1-V8, W8-W13). Every graphic OFF until Angelo approves it.</summary>
    public VisualPackSettings Visuals { get; set; } = new();
    /// <summary>2026-10-29 (#494): viewer cards (Super Chat / tips / bits / members / subs) and the track fund.</summary>
    public ViewerSettings Viewer { get; set; } = new();
    /// <summary>2026-10-29 (#494): the broadcast theme ("default" or "halloween"). Only skins the tower, session bar and
    /// viewer cards - the layout never changes.</summary>
    public string Theme { get; set; } = "default";
    /// <summary>2026-10-29 (#494): pick the theme by the date (Halloween from 24 to 31 October). OFF by default.</summary>
    public bool ThemeByDate { get; set; }
    /// <summary>2026-10-30 (#601): which holidays "theme by date" follows (Halloween 24-31 Oct, Christmas 1-26 Dec,
    /// July 4th 1-5 Jul), each with its own switch. All on by default.</summary>
    public ThemeDateSettings ThemeDates { get; set; } = new();
    /// <summary>2026-10-30 (#601): the custom themes made in the control app's theme editor (a custom theme with the id
    /// "team" is the edited Team colours preset).</summary>
    public List<ApexHUD.Native.Overlays.ThemeSpec> CustomThemes { get; set; } = new();
    /// <summary>2026-10-30 (#619): race control as a slim bar under the session bar (under the ticker in a battle, under the
    /// corner / straight name on an onboard). ON by default; off = the old race-control card in the radio column.</summary>
    public bool RaceControlBar { get; set; } = true;
    /// <summary>2026-11-05 (#777): CrewChief's calls on the radio card (read from CrewChief's shared memory). OFF by default.</summary>
    public bool CrewChiefRadio { get; set; } = false;
    /// <summary>2026-11-05 (#777): include CrewChief's spotter calls ("car left", "clear") - chatty, so OFF by default.</summary>
    public bool CrewChiefSpotter { get; set; } = false;
    /// <summary>2026-11-05 (#778): the user's telemetry widgets (Documents\iRacing\Grandstand\telemetry_widgets.json). OFF by default.</summary>
    public bool TelemetryWidgets { get; set; } = false;
    /// <summary>2026-10-30 (#611): keep the face / pedal cams in OBS clear of the graphics (moves them between slots).</summary>
    public CamSettings Cams { get; set; } = new();
    /// <summary>2026-10-31 (#644): Grandstand draws the face cam itself (bottom of the left column, above the tower, under
    /// the transitions). OFF by default until Angelo approves the preview.</summary>
    public FaceCamSettings FaceCam { get; set; } = new();
}

/// <summary>#644: the face cam drawn by Grandstand (picture from OBS's "GSFaceCam" Spout sender).</summary>
internal sealed class FaceCamSettings
{
    public bool On { get; set; }
    /// <summary>Camera width in 1080p units (16:9). 400 = the tower lane's width.</summary>
    public float Width { get; set; } = 400f;
}

/// <summary>#611: the cam placer. <see cref="Slots"/> per OBS scene and cam role ("face" / "pedal"): the home slot (where
/// the cam sits in the OBS scene) and its alternatives, in OBS canvas pixels. Filled from Angelo's scene layout
/// (2026-10-30 dump); a cam found in OBS somewhere that is neither its home nor an alternative (moved by hand) becomes the
/// new home and its alternatives are worked out again.</summary>
internal sealed class CamSettings
{
    /// <summary>"Keep cams clear of graphics" (ON by default).</summary>
    public bool KeepClear { get; set; } = true;
    public List<CamSlots> Slots { get; set; } = new();
}

internal sealed class CamSlots
{
    public string Scene { get; set; } = "";
    public string Role { get; set; } = "";
    public CamBox Home { get; set; } = new();
    public List<CamBox> Alts { get; set; } = new();
}

internal sealed class CamBox
{
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public SkiaSharp.SKRect Rect => SkiaSharp.SKRect.Create(X, Y, W, H);   // never serialised (SKRect.Standardized loops)
    public static CamBox Of(SkiaSharp.SKRect r) => new() { X = MathF.Round(r.Left), Y = MathF.Round(r.Top), W = MathF.Round(r.Width), H = MathF.Round(r.Height) };
}

/// <summary>2026-10-30 (#601): the holidays "theme by date" follows.</summary>
internal sealed class ThemeDateSettings
{
    public bool Halloween { get; set; } = true;
    public bool Christmas { get; set; } = true;
    public bool July4 { get; set; } = true;
}

/// <summary>2026-10-29 (#494): viewer interactions sent by Streamer.bot / StreamElements to POST /api/viewer.</summary>
internal sealed class ViewerSettings
{
    /// <summary>Paid messages and members / subs on screen. ON by default.</summary>
    public bool Cards { get; set; } = true;
    /// <summary>#575: ordinary chat comments on screen too (filtered, one every 20 s at most). OFF by default.</summary>
    public bool Comments { get; set; }
    /// <summary>From this Super Chat / tip amount ($ / € / £) the tower-top banner plays too.</summary>
    public double BigAmount { get; set; } = 20;
    /// <summary>From this many bits the tower-top banner plays too.</summary>
    public int BigBits { get; set; } = 2000;
    /// <summary>The "Track fund" goal bar under the session bar. OFF by default.</summary>
    public bool Fund { get; set; }
    public string FundGoal { get; set; } = "Track fund";
    public double FundTarget { get; set; } = 100;
    /// <summary>#576: the tiered looks (chat card, tier 3 card, tier 2 sleeve, tier 1 full-screen) replace the strip / member card.</summary>
    public bool TierLooks { get; set; }
    /// <summary>#576: from this amount a paid message is tier 2 (below: tier 3).</summary>
    public double Tier2From { get; set; } = 10;
    /// <summary>#576: from this amount a paid message is tier 1 (the most expensive).</summary>
    public double Tier1From { get; set; } = 50;
    /// <summary>#576: the thank-you roll plays by itself after the post-race outro.</summary>
    public bool AutoThanks { get; set; }
    /// <summary>#603: the tier looks play the existing broadcast stingers (Grandstand's sound goes to the TV / stream, never
    /// the headset): tier 3 the light lower-third cue, tier 2 and tier 1 the Grandstand string swell. ON by default.</summary>
    public bool TierSounds { get; set; } = true;
    /// <summary>#608: after a viewer look ends, a small "latest" plate keeps the most recent message up until the next one
    /// plays (top right, beside the session bar). ON by default.</summary>
    public bool KeepLatest { get; set; } = true;
    /// <summary>#608: the latest plate shows paid messages (and members / subs) only; false = paid + chat (default).</summary>
    public bool LatestPaidOnly { get; set; }
}

/// <summary>2026-10-27: the visual pack switches. Graphics default OFF except the approved W8 / W11 / W12 / W13 (round 2); the lower-third sounds follow the existing stingers
/// (on, heard only while a replay plays). <see cref="Elevated"/> lists the items ("V1".."W12") using the elevated design.</summary>
internal sealed class VisualPackSettings
{
    public bool WeatherCard { get; set; }        // V1
    public bool NightPalette { get; set; }       // V2
    public bool BattleRadar { get; set; }        // V3
    public bool StrategyBoard { get; set; }      // V4
    public bool LiveTag { get; set; }            // V5
    public bool ReplayBumper { get; set; }       // V6
    public bool StatsCards { get; set; }         // V7
    public bool YellowMap { get; set; }          // V8
    public bool BreakCard { get; set; } = true;  // W8 (approved 2026-10-27)
    public bool BreakMusic { get; set; }         // W8 sound bed (TV only, needs break_bed.wav)
    public bool LowerThirdSounds { get; set; } = true;   // W9
    public bool PipOnboard { get; set; }         // W10
    public bool PipCamera { get; set; }          // W10: let it cut the iRacing camera (replays / spectating only)
    public bool LensEffects { get; set; } = true;   // W11 (approved 2026-10-27: only where it lines up, see SunAlign)
    public bool PenaltyCard { get; set; } = true;   // W12 (approved 2026-10-27)
    public bool PurpleBeep { get; set; } = true;    // W13 (approved 2026-10-27, TV only)
    /// <summary>W11: horizontal field of view (degrees) of the chase / nose cameras; 0 = built-in default (70).</summary>
    public double LensFov { get; set; }
    /// <summary>W11: camera pitch in degrees (negative = looking down); null = built-in default (-4).</summary>
    public double? LensPitch { get; set; }
    /// <summary>2026-10-27: settings revision (2 = round-2 approvals applied once). 0 in files written before.</summary>
    public int Rev { get; set; }
    public List<string> Elevated { get; set; } = new();
}

/// <summary>
/// The bridge between the control app (web page, any screen) and the render loop.
/// The render loop never reads JSON or touches a dictionary per frame: it compares
/// <see cref="Version"/> once per tick and only re-applies settings when it moved.
/// </summary>
internal static class ControlState
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "iRacing", "Grandstand");
    static readonly string FilePath = Path.Combine(Folder, "control.json");

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    static ControlSettings current = new();
    static int version = 1;

    /// <summary>Bumped on every change; the compositor re-applies when it differs.</summary>
    public static int Version => Volatile.Read(ref version);
    public static ControlSettings Current => Volatile.Read(ref current);

    /// <summary>Test cards and other one-shot requests from the control app, consumed on the render thread.</summary>
    public static readonly ConcurrentQueue<string> Requests = new();

    /// <summary>Raised (on the caller's thread) after every change - the replay director waits on it while off.</summary>
    public static event Action? Changed;

    /// <summary>Graphics that stay on in Lean mode.</summary>
    public static readonly HashSet<string> LeanSet = new(StringComparer.Ordinal) { "tower", "splash", "sessionbar" };

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<ControlSettings>(File.ReadAllText(FilePath), Json);
                if (s != null)
                {
                    // 2026-10-25 (Angelo kept losing it on every restart): the director's on/off is remembered. It's still
                    // safe: it only ever steers in replays / spectating and switches itself off the moment you're in the car.
                    Volatile.Write(ref current, Sanitize(s)); Interlocked.Increment(ref version);
                }
            }
        }
        catch (Exception ex) { Program.Log("control: could not read settings (" + ex.Message + "), using defaults"); }
    }

    public static void Replace(ControlSettings s)
    {
        Volatile.Write(ref current, Sanitize(s));
        Interlocked.Increment(ref version);
        try { Changed?.Invoke(); } catch { }
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(current, Json));
        }
        catch (Exception ex) { Program.Log("control: could not save settings: " + ex.Message); }
    }

    /// <summary>#601: the cleaned theme (colours / numbers checked), keeping the id and flags the caller set.</summary>
    static ApexHUD.Native.Overlays.ThemeSpec Keep(ApexHUD.Native.Overlays.ThemeSpec clean, ApexHUD.Native.Overlays.ThemeSpec raw)
    {
        clean.Id = raw.Id; clean.BuiltIn = raw.BuiltIn; clean.Editable = raw.Editable;
        return clean;
    }

    /// <summary>2026-10-30 (#601): the theme editor's live preview - shown on the overlay instead of the picked theme until
    /// the editor stops it (or 10 minutes after its last change, so an abandoned editor can't leave it on air). Never saved.</summary>
    public static ApexHUD.Native.Overlays.ThemeSpec? PreviewTheme { get; private set; }
    public static DateTime PreviewUntilUtc { get; private set; }
    static int previewVersion;
    public static int PreviewVersion => Volatile.Read(ref previewVersion);

    public static void SetPreview(ApexHUD.Native.Overlays.ThemeSpec? spec)
    {
        PreviewTheme = spec;
        PreviewUntilUtc = DateTime.UtcNow.AddMinutes(10);
        Interlocked.Increment(ref previewVersion);
    }

    static ControlSettings Sanitize(ControlSettings s)
    {
        s.Mode = s.Mode is "lean" or "broadcast" or "studio" ? s.Mode : "broadcast";
        s.Elements ??= new();
        if (s.Elements.Count > 64) s.Elements = new();
        foreach (var e in s.Elements.Values)
        {
            e.Scale = Math.Clamp(e.Scale <= 0 ? 1f : e.Scale, 0.25f, 3f);
            e.X = Math.Clamp(e.X, -4000f, 4000f);
            e.Y = Math.Clamp(e.Y, -4000f, 4000f);
        }
        s.StoryCardSeconds = Math.Clamp(s.StoryCardSeconds, 4, 30);
        s.StoryGapMinutes = Math.Clamp(s.StoryGapMinutes, 0.5, 30);
        s.Performance = s.Performance is "auto" or "full" or "light" ? s.Performance : "auto";
        s.TempUnit = s.TempUnit is "auto" or "F" or "C" ? s.TempUnit : "F";
        s.Transitions = s.Transitions is "auto" or "builtin" or "folder" or "off" ? s.Transitions : "auto";
        s.CardLayout = (s.CardLayout ?? "").Trim().ToLowerInvariant() is "mixed" or "classic" or "column" or "halves" or "flip" or "corners" ? s.CardLayout!.Trim().ToLowerInvariant() : "mixed";
        s.Viewer ??= new();
        s.Viewer.BigAmount = double.IsFinite(s.Viewer.BigAmount) ? Math.Clamp(s.Viewer.BigAmount, 1, 100000) : 20;
        s.Viewer.BigBits = Math.Clamp(s.Viewer.BigBits, 1, 10_000_000);
        s.Viewer.FundGoal = (s.Viewer.FundGoal ?? "").Trim() is { Length: > 0 } g ? (g.Length > 40 ? g[..40] : g) : "Track fund";
        s.Viewer.Tier2From = double.IsFinite(s.Viewer.Tier2From) ? Math.Clamp(s.Viewer.Tier2From, 1, 100000) : 10;
        s.Viewer.Tier1From = double.IsFinite(s.Viewer.Tier1From) ? Math.Clamp(s.Viewer.Tier1From, s.Viewer.Tier2From, 100000) : 50;
        s.Viewer.FundTarget = double.IsFinite(s.Viewer.FundTarget) ? Math.Clamp(s.Viewer.FundTarget, 0, 10_000_000) : 100;
        // #601: custom themes - clean ids, no duplicates, no clash with a built-in (except Team colours), at most 32
        s.ThemeDates ??= new();
        var themes = new List<ApexHUD.Native.Overlays.ThemeSpec>();
        foreach (var t in s.CustomThemes ?? new())
        {
            if (t == null) continue;
            t.Id = ApexHUD.Native.Overlays.Theme.CleanId(t.Id);
            if (t.Id.Length == 0 || themes.Any(x => x.Id == t.Id)) continue;
            if (t.Id != "team" && ApexHUD.Native.Overlays.Theme.BuiltIns.Any(b => b.Id == t.Id)) continue;
            t.BuiltIn = t.Id == "team"; t.Editable = t.Id == "team";
            t.AppliesTo ??= new();
            themes.Add(Keep(ApexHUD.Native.Overlays.ThemeDef.FromSpec(t).ToSpec(), t));
            if (themes.Count >= 32) break;
        }
        s.CustomThemes = themes;
        s.Theme = ApexHUD.Native.Overlays.Theme.Known(s.Theme, themes) ? ApexHUD.Native.Overlays.Theme.CleanId(s.Theme) : "default";
        s.Visuals ??= new();
        s.Visuals.Elevated = (s.Visuals.Elevated ?? new()).Where(x => x is { Length: > 0 and < 8 }).Distinct().Take(20).ToList();
        // 2026-10-27 round 2: Angelo approved W8 (break card), W11 (lens effects, lined up with the sim's sun), W12 (race
        // control card) and W13 (purple beep) - switched on ONCE for a settings file written before that (rev < 2), so his
        // later choices stick. V3 (battle radar) was not approved: kept off and out of the control app.
        if (s.Visuals.Rev < 2)
        {
            s.Visuals.BreakCard = true; s.Visuals.PenaltyCard = true; s.Visuals.PurpleBeep = true; s.Visuals.LensEffects = true;
            s.Visuals.Rev = 2;
        }
        s.Visuals.BattleRadar = false;
        s.Visuals.LensFov = Math.Clamp(s.Visuals.LensFov, 0, 140);
        s.Visuals.LensPitch = s.Visuals.LensPitch is double lp ? Math.Clamp(lp, -45, 45) : null;
        s.Cams = CamDefaults.Clean(s.Cams);
        s.FaceCam ??= new();
        s.FaceCam.Width = Math.Clamp(s.FaceCam.Width, 240f, 400f);
        return s;
    }
}

/// <summary>#611: the cam slots from Angelo's OBS scene layout (obs_scene_layout.json, 2026-10-30, 3840x2160 canvas): the
/// face cam (VDO.Ninja, 16:9) and the pedal cam (S10 USB) per scene, each with a gold frame 6 px bigger behind it.
/// "Starting soon" is never touched; "Onboard" and "Grandstand Broadcast" have no cams.</summary>
internal static class CamDefaults
{
    public const float CanvasW = 3840, CanvasH = 2160;

    static readonly (string Scene, string Role, float X, float Y, float W, float H)[] Homes =
    {
        ("Racing", "pedal", 3280, 1310, 512, 288),
        ("Racing", "face", 2992, 1630, 800, 450),
        ("Battle", "pedal", 48, 1310, 512, 288),
        ("Battle", "face", 48, 1630, 800, 450),
        ("Story card", "face", 48, 1720, 640, 360),
        ("Replay", "face", 2720, 1400, 1072, 602),
        ("Post-race", "face", 3460, 1720, 352, 198),
    };

    public static List<CamSlots> Build()
    {
        var list = new List<CamSlots>();
        foreach (var h in Homes) list.Add(Make(h.Scene, h.Role, SkiaSharp.SKRect.Create(h.X, h.Y, h.W, h.H),
            Homes.Where(o => o.Scene == h.Scene && o.Role != h.Role).Select(o => SkiaSharp.SKRect.Create(o.X, o.Y, o.W, o.H)).ToArray(), CanvasW, CanvasH));
        return list;
    }

    public static CamSlots Make(string scene, string role, SkiaSharp.SKRect home, SkiaSharp.SKRect[] others, float cw, float ch) => new()
    {
        Scene = scene, Role = role, Home = CamBox.Of(home),
        Alts = ApexHUD.Native.Overlays.CamPlacer.Alternatives(home, cw, ch, others).Select(CamBox.Of).ToList(),
    };

    public static CamSettings Clean(CamSettings? c)
    {
        c ??= new();
        c.Slots ??= new();
        c.Slots = c.Slots.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Scene) && x.Role is "face" or "pedal" && x.Home is { W: > 8, H: > 8 })
                         .GroupBy(x => (x.Scene, x.Role)).Select(g => g.Last()).Take(40).ToList();
        foreach (var x in c.Slots) x.Alts = (x.Alts ?? new()).Where(a => a is { W: > 8, H: > 8 }).Take(8).ToList();
        if (c.Slots.Count == 0) c.Slots = Build();
        return c;
    }
}
