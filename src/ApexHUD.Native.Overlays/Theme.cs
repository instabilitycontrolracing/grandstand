using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>Which kind of surface a theme is skinning (the motif sits in a different corner on each, and the theme's
/// "applies to" list switches each family on or off). Tower / Bar / Card / Strip are the #494 originals (Card + Strip =
/// the viewer cards); the rest came with #601.</summary>
public enum ThemeSurface { Tower, Bar, Card, Strip, Driver, Story, Battle, Splash, Ticker, Popup }

/// <summary>#601: the procedural texture laid over a themed surface's glass.</summary>
public enum ThemeTexture { None, Grain, Carbon, Brushed, Embers, Snow, Stars }

/// <summary>#601: the small decoration in a quiet corner of each surface.</summary>
public enum ThemeMotif { None, CobwebBat, Snowflake, FlagStripes, Chequered, Pinstripe }

/// <summary>#601: what the particles look like ("auto" follows the texture: embers rise, snow falls, stars twinkle,
/// anything else = slow floating glints).</summary>
public enum ThemeParticles { Auto, Embers, Snow, Twinkle, Glints }

/// <summary>#601: which graphics a theme skins.</summary>
[Flags]
public enum ThemeTargets
{
    None = 0, Tower = 1, Bar = 2, Viewer = 4, Driver = 8, Story = 16, Battle = 32, Splash = 64, Ticker = 128, Popups = 256,
    All = Tower | Bar | Viewer | Driver | Story | Battle | Splash | Ticker | Popups,
}

/// <summary>
/// #601: a theme as data - what control.json keeps under <c>customThemes</c> and what the control app's theme editor
/// edits. Colours are "#RRGGBB"; anything missing or out of range falls back to a safe value (<see cref="ThemeDef.FromSpec"/>).
/// </summary>
public sealed class ThemeSpec
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Hairlines and eyebrows (gold on the default look).</summary>
    public string Accent { get; set; } = "#FFD876";
    /// <summary>The second colour: particles, snowflakes, the middle stripe.</summary>
    public string Secondary { get; set; } = "#F4F5F7";
    /// <summary>The soft light rising from the bottom edge and drifting across the glass.</summary>
    public string Glow { get; set; } = "#C9A961";
    public double GlowStrength { get; set; } = 0.35;
    /// <summary>The shade the glass is tinted towards, and how strongly (0 = untouched, 1 = solid).</summary>
    public string Glass { get; set; } = "#080A10";
    public double GlassOpacity { get; set; } = 0.45;
    /// <summary>none / grain / carbon / brushed / embers / snow / stars</summary>
    public string Texture { get; set; } = "grain";
    public double TextureStrength { get; set; } = 0.5;
    /// <summary>none / cobweb-bat / snowflake / flag-stripes / chequered / pinstripe</summary>
    public string Motif { get; set; } = "none";
    public bool Particles { get; set; }
    /// <summary>auto / embers / snow / twinkle / glints</summary>
    public string ParticleStyle { get; set; } = "auto";
    /// <summary>0..1 - how many particles (0.5 = the Halloween embers).</summary>
    public double Density { get; set; } = 0.4;
    /// <summary>A slow light sweep across each surface every few seconds.</summary>
    public bool Sweep { get; set; }
    /// <summary>tower / bar / driver / story / battle / splash / ticker / popups / viewer</summary>
    public List<string> AppliesTo { get; set; } = new(Theme.TargetKeys);
    /// <summary>Read-only for the control app: true on the built-in presets.</summary>
    public bool BuiltIn { get; set; }
    /// <summary>Read-only for the control app: the built-in preset may be edited in place (Team colours).</summary>
    public bool Editable { get; set; }
}

/// <summary>One broadcast theme (#494, data-driven since #601): colours + glass tint + a procedural texture + a small motif +
/// optional particles and light sweep. A theme only tints / skins surfaces - the layout and every number stay exactly as
/// they are.</summary>
public sealed class ThemeDef
{
    public string Id { get; init; } = "default";
    public string Name { get; init; } = "Default";
    public bool BuiltIn { get; init; }
    public bool Editable { get; init; }
    public SKColor Accent { get; init; }
    public SKColor Secondary { get; init; }
    public SKColor Glow { get; init; }
    public float GlowStrength { get; init; }
    public SKColor Glass { get; init; }
    public float GlassOpacity { get; init; }
    public ThemeTexture Texture { get; init; }
    public float TextureStrength { get; init; }
    public ThemeMotif Motif { get; init; }
    public bool Particles { get; init; }
    public ThemeParticles ParticleStyle { get; init; }
    public float Density { get; init; }
    public bool Sweep { get; init; }
    public ThemeTargets Targets { get; init; } = ThemeTargets.All;

    /// <summary>The particle look actually drawn ("auto" resolved from the texture).</summary>
    public ThemeParticles Effective => ParticleStyle != ThemeParticles.Auto ? ParticleStyle : Texture switch
    {
        ThemeTexture.Embers => ThemeParticles.Embers,
        ThemeTexture.Snow => ThemeParticles.Snow,
        ThemeTexture.Stars => ThemeParticles.Twinkle,
        _ => ThemeParticles.Glints,
    };

    public ThemeSpec ToSpec() => new()
    {
        Id = Id, Name = Name, Accent = Hex(Accent), Secondary = Hex(Secondary), Glow = Hex(Glow), GlowStrength = Math.Round(GlowStrength, 3),
        Glass = Hex(Glass), GlassOpacity = Math.Round(GlassOpacity, 3), Texture = Theme.TextureKey(Texture), TextureStrength = Math.Round(TextureStrength, 3),
        Motif = Theme.MotifKey(Motif), Particles = Particles, ParticleStyle = Theme.ParticleKey(ParticleStyle), Density = Math.Round(Density, 3),
        Sweep = Sweep, AppliesTo = Theme.TargetList(Targets), BuiltIn = BuiltIn, Editable = Editable,
    };

    /// <summary>A theme from data; every field is checked (bad colours / names fall back, numbers are clamped).</summary>
    public static ThemeDef FromSpec(ThemeSpec s, bool builtIn = false, bool editable = false)
    {
        string id = Theme.CleanId(s.Id);
        string name = (s.Name ?? "").Trim();
        if (name.Length == 0) name = id.Length > 0 ? id : "Custom";
        if (name.Length > 32) name = name[..32];
        return new ThemeDef
        {
            Id = id, Name = name, BuiltIn = builtIn, Editable = editable,
            Accent = Theme.ParseColor(s.Accent, new SKColor(0xFF, 0xD8, 0x76)),
            Secondary = Theme.ParseColor(s.Secondary, new SKColor(0xF4, 0xF5, 0xF7)),
            Glow = Theme.ParseColor(s.Glow, new SKColor(0xC9, 0xA9, 0x61)),
            GlowStrength = Clamp01(s.GlowStrength),
            Glass = Theme.ParseColor(s.Glass, new SKColor(0x08, 0x0A, 0x10)),
            GlassOpacity = Math.Min(0.9f, Clamp01(s.GlassOpacity)),   // never fully opaque: the glass must stay glass
            Texture = Theme.ParseTexture(s.Texture),
            TextureStrength = Clamp01(s.TextureStrength),
            Motif = Theme.ParseMotif(s.Motif),
            Particles = s.Particles,
            ParticleStyle = Theme.ParseParticles(s.ParticleStyle),
            Density = Clamp01(s.Density),
            Sweep = s.Sweep,
            Targets = Theme.ParseTargets(s.AppliesTo),
        };
    }

    static float Clamp01(double v) => double.IsFinite(v) ? (float)Math.Clamp(v, 0, 1) : 0f;
    static string Hex(SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";
}

/// <summary>
/// 2026-10-29 (#494, Angelo) / 2026-10-30 (#601, "the Halloween and customizable themes for the timing tower, session bar
/// and driver card, as well as graphics"): broadcast themes. <see cref="Default"/> draws nothing extra (the normal look,
/// pixel for pixel); every other theme tints the glass, lays a procedural texture over it, adds a small motif in a quiet
/// corner and (optionally) drifting particles and a slow light sweep - all procedural, no image files. Built-in presets:
/// Halloween, Christmas, July 4th, Le Mans week, Night race and an editable Team colours; the control app's editor saves
/// custom themes into control.json (<see cref="SetCustoms"/>). The compositor picks the theme (<see cref="Resolve"/>: a
/// fixed pick, or "auto by date" = Halloween 24-31 Oct, Christmas 1-26 Dec, July 4th 1-5 Jul, each with its own switch)
/// and calls <see cref="Set"/>; the editor's live preview goes through <see cref="Preview"/>. Renderers call
/// <see cref="Skin"/> right after drawing their glass and before their text, and <see cref="AccentOr(SKColor, ThemeSurface)"/>
/// for decorative hairlines.
/// <para>Performance (4K60 on a GPU shared with VR): no layers at all; every gradient / texture tile / motif path is cached
/// (per theme, per surface size) and drawn in the surface's own coordinates, so a frame allocates nothing; particles are
/// plain circles whose paths are pure functions of their index and the clock. Light mode drops particles and sweeps.</para>
/// </summary>
public static class Theme
{
    // ---------------- the presets ----------------

    public static readonly ThemeDef Default = new()
    {
        Id = "default", Name = "Default", BuiltIn = true,
        // never drawn (Skin / Applies / AccentOr short-circuit on Default); the values are the starting point when the
        // editor duplicates Default: the house gold on dark glass, film grain, a light sweep
        Accent = new SKColor(0xC5, 0xA5, 0x72), Secondary = new SKColor(0xFF, 0xD8, 0x76), Glow = new SKColor(0xB8, 0x96, 0x4C), GlowStrength = 0.3f,
        Glass = new SKColor(0x08, 0x0A, 0x10), GlassOpacity = 0.4f, Texture = ThemeTexture.Grain, TextureStrength = 0.5f,
        Motif = ThemeMotif.None, ParticleStyle = ThemeParticles.Glints, Density = 0.25f, Sweep = true, Targets = ThemeTargets.None,
    };

    /// <summary>#494 as it was: orange / black glass, drifting embers, a cobweb and a bat.</summary>
    public static readonly ThemeDef Halloween = new()
    {
        Id = "halloween", Name = "Halloween", BuiltIn = true,
        Accent = new SKColor(0xFF, 0x8A, 0x1E),        // pumpkin orange accent (hairlines, eyebrows)
        Secondary = new SKColor(0xFF, 0xB0, 0x48),     // the embers themselves
        Glow = new SKColor(0xFF, 0x5E, 0x00), GlowStrength = 1f,   // ember glow rising from the bottom edge
        Glass = new SKColor(0x12, 0x07, 0x02), GlassOpacity = 170f / 255f,   // the glass goes from navy to a warm black
        Texture = ThemeTexture.Embers, TextureStrength = 0.5f, Motif = ThemeMotif.CobwebBat,
        Particles = true, ParticleStyle = ThemeParticles.Embers, Density = 0.5f,
    };

    /// <summary>Deep pine glass, the gold accent kept, a cranberry glow, light snowfall and a frosted top edge.</summary>
    public static readonly ThemeDef Christmas = new()
    {
        Id = "christmas", Name = "Christmas", BuiltIn = true,
        Accent = new SKColor(0xE8, 0xC2, 0x6A), Secondary = new SKColor(0xF4, 0xF7, 0xFB),
        Glow = new SKColor(0xB3, 0x12, 0x2E), GlowStrength = 0.55f,
        Glass = new SKColor(0x04, 0x16, 0x0E), GlassOpacity = 0.6f,
        Texture = ThemeTexture.Snow, TextureStrength = 0.5f, Motif = ThemeMotif.Snowflake,
        Particles = true, ParticleStyle = ThemeParticles.Snow, Density = 0.45f,
    };

    /// <summary>Navy glass with a faint star field, flag-red hairlines, red / white / blue corner stripes, a few twinkles.</summary>
    public static readonly ThemeDef Usa = new()
    {
        Id = "usa", Name = "July 4th", BuiltIn = true,
        Accent = new SKColor(0xD9, 0x3A, 0x49), Secondary = new SKColor(0xF5, 0xF6, 0xF8),
        Glow = new SKColor(0x2F, 0x5B, 0xD3), GlowStrength = 0.45f,
        Glass = new SKColor(0x06, 0x10, 0x2B), GlassOpacity = 0.58f,
        Texture = ThemeTexture.Stars, TextureStrength = 0.55f, Motif = ThemeMotif.FlagStripes,
        Particles = true, ParticleStyle = ThemeParticles.Twinkle, Density = 0.35f, Sweep = true,
    };

    /// <summary>Endurance week: blue hairlines, carbon weave, a bleu / blanc / rouge pinstripe along the foot, light sweeps.</summary>
    public static readonly ThemeDef LeMans = new()
    {
        Id = "lemans", Name = "Le Mans week", BuiltIn = true,
        Accent = new SKColor(0x3D, 0x7B, 0xFF), Secondary = new SKColor(0xF4, 0xF5, 0xF7),
        Glow = new SKColor(0xE0, 0x31, 0x3C), GlowStrength = 0.22f,
        Glass = new SKColor(0x04, 0x0A, 0x1C), GlassOpacity = 0.5f,
        Texture = ThemeTexture.Carbon, TextureStrength = 0.55f, Motif = ThemeMotif.Pinstripe,
        Particles = false, ParticleStyle = ThemeParticles.Auto, Density = 0.3f, Sweep = true,
    };

    /// <summary>Under the floodlights: near-black glass, cool blue hairlines, film grain, floating glints, light sweeps.</summary>
    public static readonly ThemeDef Night = new()
    {
        Id = "night", Name = "Night race", BuiltIn = true,
        Accent = new SKColor(0x8F, 0xC1, 0xFF), Secondary = new SKColor(0xEE, 0xF3, 0xFF),
        Glow = new SKColor(0x2A, 0x4B, 0xFF), GlowStrength = 0.4f,
        Glass = new SKColor(0x01, 0x03, 0x09), GlassOpacity = 0.62f,
        Texture = ThemeTexture.Grain, TextureStrength = 0.5f, Motif = ThemeMotif.None,
        Particles = true, ParticleStyle = ThemeParticles.Glints, Density = 0.3f, Sweep = true,
    };

    /// <summary>Your team's colours (editable in place from the control app): accent / second colour / glow carry the livery.</summary>
    public static readonly ThemeDef Team = new()
    {
        Id = "team", Name = "Team colours", BuiltIn = true, Editable = true,
        Accent = new SKColor(0xE1, 0x06, 0x00), Secondary = new SKColor(0xFF, 0xFF, 0xFF),
        Glow = new SKColor(0xE1, 0x06, 0x00), GlowStrength = 0.3f,
        Glass = new SKColor(0x0B, 0x0C, 0x10), GlassOpacity = 0.5f,
        Texture = ThemeTexture.Carbon, TextureStrength = 0.6f, Motif = ThemeMotif.FlagStripes,
        Particles = false, ParticleStyle = ThemeParticles.Auto, Density = 0.3f, Sweep = true,
    };

    static readonly ThemeDef[] builtIns = { Default, Halloween, Christmas, Usa, LeMans, Night, Team };
    static volatile ThemeDef[] customs = Array.Empty<ThemeDef>();

    /// <summary>The built-in presets, Default first.</summary>
    public static IReadOnlyList<ThemeDef> BuiltIns => builtIns;

    /// <summary>Every theme that can be picked: the presets (Team colours as edited) then the custom themes.</summary>
    public static IReadOnlyList<ThemeDef> All
    {
        get
        {
            var cs = customs;
            var list = new List<ThemeDef>(builtIns.Length + cs.Length);
            foreach (var b in builtIns) list.Add(cs.FirstOrDefault(c => c.Id == b.Id) ?? b);
            foreach (var c in cs) if (!builtIns.Any(b => b.Id == c.Id)) list.Add(c);
            return list;
        }
    }

    static ThemeDef active = Default;
    static volatile ThemeDef? preview;

    /// <summary>The theme on air now (the editor's live preview wins while it runs). Written on the render thread.</summary>
    public static ThemeDef Current => preview ?? active;
    public static bool IsDefault => ReferenceEquals(Current, Default);
    /// <summary>The picked / dated theme (what goes back on air when the live preview stops).</summary>
    public static ThemeDef Active => active;
    public static bool Previewing => preview != null;

    /// <summary>The theme with this id (custom themes, and the edited Team colours, first), or Default.</summary>
    public static ThemeDef ById(string? id) => Find(id) ?? Default;

    public static ThemeDef? Find(string? id)
    {
        string k = (id ?? "").Trim();
        if (k.Length == 0) return null;
        foreach (var c in customs) if (string.Equals(c.Id, k, StringComparison.OrdinalIgnoreCase)) return c;
        foreach (var b in builtIns) if (string.Equals(b.Id, k, StringComparison.OrdinalIgnoreCase)) return b;
        return null;
    }

    /// <summary>Is <paramref name="id"/> a known theme, counting the custom themes in <paramref name="specs"/> (settings checks).</summary>
    public static bool Known(string? id, IEnumerable<ThemeSpec>? specs)
    {
        string k = (id ?? "").Trim();
        if (builtIns.Any(b => string.Equals(b.Id, k, StringComparison.OrdinalIgnoreCase))) return true;
        return specs != null && specs.Any(s => string.Equals(CleanId(s.Id), k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The custom themes from control.json (a custom theme with the id "team" is the edited Team colours).
    /// Re-picks the active theme so an edited theme on air updates straight away.</summary>
    public static void SetCustoms(IEnumerable<ThemeSpec>? specs)
    {
        var list = new List<ThemeDef>();
        if (specs != null)
            foreach (var s in specs)
            {
                if (s == null) continue;
                string id = CleanId(s.Id);
                if (id.Length == 0 || list.Any(x => x.Id == id)) continue;
                if (builtIns.Any(b => b.Id == id) && id != Team.Id) continue;   // only Team colours can be overridden
                list.Add(ThemeDef.FromSpec(s, builtIn: id == Team.Id, editable: id == Team.Id));
                if (list.Count >= 32) break;
            }
        customs = list.ToArray();
        active = ById(active.Id);
    }

    /// <summary>Halloween week: 24 to 31 October (local date).</summary>
    public static bool IsHalloweenWeek(DateTime day) => day.Month == 10 && day.Day >= 24;
    /// <summary>Christmas: 1 to 26 December.</summary>
    public static bool IsChristmas(DateTime day) => day.Month == 12 && day.Day <= 26;
    /// <summary>July 4th: 1 to 5 July.</summary>
    public static bool IsJuly4(DateTime day) => day.Month == 7 && day.Day <= 5;

    /// <summary>Which theme should be on: with <paramref name="autoByDate"/> the date wins in Halloween week, otherwise
    /// the picked theme (#494 behaviour; every holiday on).</summary>
    public static string Resolve(string? pick, bool autoByDate, DateTime today) => Resolve(pick, autoByDate, true, true, true, today);

    /// <summary>#601: with <paramref name="autoByDate"/> each holiday that is switched on wins in its dates (Halloween
    /// 24-31 Oct, Christmas 1-26 Dec, July 4th 1-5 Jul); otherwise the picked theme.</summary>
    public static string Resolve(string? pick, bool autoByDate, bool halloween, bool christmas, bool july4, DateTime today)
    {
        if (autoByDate)
        {
            if (halloween && IsHalloweenWeek(today)) return Halloween.Id;
            if (christmas && IsChristmas(today)) return Christmas.Id;
            if (july4 && IsJuly4(today)) return Usa.Id;
        }
        return ById(pick).Id;
    }

    public static void Set(string? id) => active = ById(id);

    /// <summary>#601: the editor's live preview (null = back to the picked theme).</summary>
    public static void Preview(ThemeDef? def) => preview = def;

    /// <summary>The clock for <see cref="SkinNow"/> (renderers whose glass helper has no time of its own). The compositor
    /// sets it once per frame; preview tools set it themselves.</summary>
    public static double Clock;

    /// <summary>True while the theme on air moves by itself (particles / light sweep) - the compositor then presents at
    /// least ~30 frames a second so the motion stays smooth. Always false on Default and in light mode.</summary>
    public static bool Animated
    {
        get
        {
            var t = Current;
            return !ReferenceEquals(t, Default) && t.Targets != ThemeTargets.None && !Perf.Light && ((t.Particles && t.Density > 0.01f) || t.Sweep);
        }
    }

    /// <summary>Does the theme on air skin this kind of surface?</summary>
    public static bool Applies(ThemeSurface s)
    {
        var t = Current;
        return !ReferenceEquals(t, Default) && (t.Targets & TargetOf(s)) != 0;
    }

    public static ThemeTargets TargetOf(ThemeSurface s) => s switch
    {
        ThemeSurface.Tower => ThemeTargets.Tower,
        ThemeSurface.Bar => ThemeTargets.Bar,
        ThemeSurface.Card or ThemeSurface.Strip => ThemeTargets.Viewer,
        ThemeSurface.Driver => ThemeTargets.Driver,
        ThemeSurface.Story => ThemeTargets.Story,
        ThemeSurface.Battle => ThemeTargets.Battle,
        ThemeSurface.Splash => ThemeTargets.Splash,
        ThemeSurface.Ticker => ThemeTargets.Ticker,
        _ => ThemeTargets.Popups,
    };

    /// <summary>The theme's accent, or <paramref name="fallback"/> (the surface's own colour) on the default theme.</summary>
    public static SKColor AccentOr(SKColor fallback) => IsDefault ? fallback : Current.Accent.WithAlpha(fallback.Alpha);

    /// <summary>The theme's accent where the theme skins <paramref name="surface"/>, else <paramref name="fallback"/>.</summary>
    public static SKColor AccentOr(SKColor fallback, ThemeSurface surface) => Applies(surface) ? Current.Accent.WithAlpha(fallback.Alpha) : fallback;

    // ---------------- names <-> values (JSON) ----------------

    public static readonly string[] TargetKeys = { "tower", "bar", "driver", "story", "battle", "splash", "ticker", "popups", "viewer" };
    public static readonly string[] TextureKeys = { "none", "grain", "carbon", "brushed", "embers", "snow", "stars" };
    public static readonly string[] MotifKeys = { "none", "cobweb-bat", "snowflake", "flag-stripes", "chequered", "pinstripe" };
    public static readonly string[] ParticleKeys = { "auto", "embers", "snow", "twinkle", "glints" };

    public static string TextureKey(ThemeTexture t) => TextureKeys[(int)t];
    public static string MotifKey(ThemeMotif m) => MotifKeys[(int)m];
    public static string ParticleKey(ThemeParticles p) => ParticleKeys[(int)p];
    public static ThemeTexture ParseTexture(string? s) => (ThemeTexture)Math.Max(0, Array.IndexOf(TextureKeys, (s ?? "").Trim().ToLowerInvariant()));
    public static ThemeMotif ParseMotif(string? s) => (ThemeMotif)Math.Max(0, Array.IndexOf(MotifKeys, (s ?? "").Trim().ToLowerInvariant()));
    public static ThemeParticles ParseParticles(string? s) => (ThemeParticles)Math.Max(0, Array.IndexOf(ParticleKeys, (s ?? "").Trim().ToLowerInvariant()));

    static readonly ThemeTargets[] TargetFlags =
        { ThemeTargets.Tower, ThemeTargets.Bar, ThemeTargets.Driver, ThemeTargets.Story, ThemeTargets.Battle, ThemeTargets.Splash, ThemeTargets.Ticker, ThemeTargets.Popups, ThemeTargets.Viewer };

    public static ThemeTargets ParseTargets(IEnumerable<string>? keys)
    {
        if (keys == null) return ThemeTargets.All;
        var t = ThemeTargets.None;
        foreach (var k in keys)
        {
            int i = Array.IndexOf(TargetKeys, (k ?? "").Trim().ToLowerInvariant());
            if (i >= 0) t |= TargetFlags[i];
        }
        return t;
    }

    public static List<string> TargetList(ThemeTargets t)
    {
        var l = new List<string>();
        for (int i = 0; i < TargetKeys.Length; i++) if ((t & TargetFlags[i]) != 0) l.Add(TargetKeys[i]);
        return l;
    }

    /// <summary>Ids are lower-case letters, digits and dashes (max 32).</summary>
    public static string CleanId(string? id)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char ch in (id ?? "").Trim().ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_') sb.Append(ch);
            else if (ch == ' ' && sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= 32) break;
        }
        return sb.ToString().Trim('-');
    }

    public static SKColor ParseColor(string? s, SKColor fallback)
    {
        string h = (s ?? "").Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
        if (h.Length != 6 || !uint.TryParse(h, System.Globalization.NumberStyles.HexNumber, null, out uint v)) return fallback;
        return new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    // ---------------- drawing ----------------

    [ThreadStatic] static SKPaint? fill;
    [ThreadStatic] static SKPaint? line;
    [ThreadStatic] static ThemeCache? cache;

    /// <summary><see cref="Skin"/> on <see cref="Clock"/> (for glass helpers that have no time of their own).</summary>
    public static void SkinNow(SKCanvas c, SKRect r, ThemeSurface surface, float alpha = 1f, SKPath? clip = null) =>
        Skin(c, r, surface, Clock, alpha, clip);

    /// <summary>
    /// Skins one surface with the current theme: call it right after the surface's glass is drawn and before its text.
    /// Draws nothing on the default theme (or where the theme doesn't apply). <paramref name="clip"/> (optional) is the
    /// surface's own outline (rounded / slanted cards); otherwise the rect is the clip. <paramref name="alpha"/> is the
    /// surface's own fade.
    /// </summary>
    public static void Skin(SKCanvas c, SKRect r, ThemeSurface surface, double nowMs, float alpha = 1f, SKPath? clip = null)
    {
        var t = Current;
        if (ReferenceEquals(t, Default) || (t.Targets & TargetOf(surface)) == 0 || alpha <= 0.01f || r.Width < 2 || r.Height < 2) return;
        var f = fill ??= new SKPaint { IsAntialias = true };
        var cc = cache ??= new ThemeCache();
        cc.Use(t);
        var e = cc.For(t, surface, r.Width, r.Height);
        float w = r.Width, h = r.Height;
        int save = c.Save();
        if (clip != null) c.ClipPath(clip, antialias: true); else c.ClipRect(r);
        c.Translate(r.Left, r.Top);
        var lr = new SKRect(0, 0, w, h);
        byte A(float k) => (byte)Math.Clamp(255f * k * alpha, 0f, 255f);

        // 1. colours: the glass is tinted towards the theme's shade (strongest at the top)
        if (t.GlassOpacity > 0.005f)
        {
            f.Shader = e.Wash; f.Color = new SKColor(255, 255, 255, A(1f));
            c.DrawRect(lr, f);
        }
        // 2. texture: the procedural tile (grain / carbon / brushed / snow dust / star field), a frosted top on snow
        if (cc.Tile != null && t.TextureStrength > 0.01f)
        {
            f.Shader = cc.Tile; f.Color = new SKColor(255, 255, 255, A(t.TextureStrength)); f.FilterQuality = SKFilterQuality.Low;
            c.DrawRect(lr, f);
            f.FilterQuality = SKFilterQuality.None;
        }
        if (e.Frost != null)
        {
            f.Shader = e.Frost; f.Color = new SKColor(255, 255, 255, A(t.TextureStrength));
            c.DrawRect(new SKRect(0, 0, w, Math.Min(h, e.FrostH)), f);
        }
        // 3. a low glow rising from the bottom edge, and two soft pools of light that drift very slowly
        double ts = nowMs / 1000.0;
        if (t.GlowStrength > 0.01f)
        {
            f.Shader = e.GlowEdge; f.Color = new SKColor(255, 255, 255, A(t.GlowStrength));
            c.DrawRect(new SKRect(0, h - e.GlowH, w, h), f);
            f.Shader = e.Pool;
            for (int i = 0; i < 2; i++)
            {
                float px = w * (0.25f + 0.5f * i + 0.12f * (float)Math.Sin(ts * 0.07 + i * 2.1));
                float py = h * (0.35f + 0.3f * i + 0.08f * (float)Math.Cos(ts * 0.05 + i));
                c.Save(); c.Translate(px, py);
                c.DrawCircle(0, 0, e.PoolR, f);
                c.Restore();
            }
        }
        f.Shader = null;

        // 4. particles (procedural: each one's path is a pure function of its index and the clock)
        if (t.Particles && t.Density > 0.01f && !Perf.Light) DrawParticles(c, w, h, t, ts, alpha, f);

        // 5. light sweep: a soft diagonal sheen crossing the surface every 9 s (each surface on its own beat)
        if (t.Sweep && !Perf.Light && e.SweepShader != null)
        {
            double period = 9000, dur = 1500;
            double ph = (nowMs + (int)surface * 1370.0) % period;
            if (ph >= 0 && ph < dur)
            {
                float k = (float)BroadcastTiming.Smoothstep(ph / dur);
                float x = -e.SweepW + (w + 2 * e.SweepW) * k;
                float fade = (float)Math.Sin(Math.PI * ph / dur);
                f.Shader = e.SweepShader; f.Color = new SKColor(255, 255, 255, A(fade));
                c.Save(); c.Translate(x, 0);
                c.DrawRect(new SKRect(-e.SweepW, 0, e.SweepW * 1.6f, h), f);
                c.Restore();
                f.Shader = null;
            }
        }

        // 6. motif in a quiet corner
        if (t.Motif != ThemeMotif.None) DrawMotif(c, cc, t, surface, w, h, ts, alpha, f);
        c.RestoreToCount(save);
    }

    static void DrawParticles(SKCanvas c, float w, float h, ThemeDef t, double ts, float alpha, SKPaint f)
    {
        int n = (int)Math.Clamp(w * h / 7000f * t.Density * 2f, 3f, 8f + 64f * t.Density);
        switch (t.Effective)
        {
            case ThemeParticles.Embers:
            {
                float span = h + 24f;
                for (int i = 0; i < n; i++)
                {
                    uint hs = Hash((uint)i * 2654435761u + 97u);
                    float x0 = (hs & 0xFFFF) / 65535f;
                    float speed = 9f + ((hs >> 16) & 0xFF) / 255f * 20f;            // px per second, upwards
                    float phase = ((hs >> 24) & 0xFF) / 255f * span;
                    float size = 0.9f + (Hash(hs) & 0xFF) / 255f * 1.4f;
                    float y = h + 12f - (float)((ts * speed + phase) % span);
                    float x = x0 * w + 7f * (float)Math.Sin(ts * (0.6 + x0) + i * 1.7);
                    float life = Math.Clamp(y / h, 0f, 1f);                       // fade as it rises
                    float flick = 0.6f + 0.4f * (float)Math.Sin(ts * (5.0 + (i % 5)) + i);
                    float a = alpha * life * flick;
                    if (a <= 0.02f) continue;
                    f.Color = t.Glow.WithAlpha((byte)(46 * a));
                    c.DrawCircle(x, y, size * 3.2f, f);
                    f.Color = t.Secondary.WithAlpha((byte)(225 * a));
                    c.DrawCircle(x, y, size, f);
                }
                break;
            }
            case ThemeParticles.Snow:
            {
                float span = h + 20f;
                for (int i = 0; i < n; i++)
                {
                    uint hs = Hash((uint)i * 2246822519u + 13u);
                    float x0 = (hs & 0xFFFF) / 65535f;
                    float speed = 7f + ((hs >> 16) & 0xFF) / 255f * 14f;            // px per second, downwards
                    float phase = ((hs >> 24) & 0xFF) / 255f * span;
                    float size = 0.7f + (Hash(hs) & 0xFF) / 255f * 1.5f;
                    float y = -10f + (float)((ts * speed + phase) % span);
                    float x = x0 * w + 9f * (float)Math.Sin(ts * (0.35 + x0 * 0.4) + i * 2.3);
                    float life = Math.Clamp(1.15f - y / h, 0f, 1f) * Math.Clamp((y + 10f) / 24f, 0f, 1f);   // melts towards the foot
                    float a = alpha * life * (0.55f + 0.45f * size / 2.2f);
                    if (a <= 0.02f) continue;
                    f.Color = t.Secondary.WithAlpha((byte)(36 * a));
                    c.DrawCircle(x, y, size * 2.4f, f);
                    f.Color = t.Secondary.WithAlpha((byte)(205 * a));
                    c.DrawCircle(x, y, size, f);
                }
                break;
            }
            case ThemeParticles.Twinkle:
            {
                var p = line ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
                p.StrokeWidth = 0.8f;
                for (int i = 0; i < n; i++)
                {
                    uint hs = Hash((uint)i * 3266489917u + 7u);
                    float x = (hs & 0xFFFF) / 65535f * w;
                    float y = ((hs >> 16) & 0xFFFF) / 65535f * h;
                    float rate = 0.5f + (Hash(hs) & 0xFF) / 255f * 0.9f;
                    float s = (float)Math.Sin(ts * rate + i * 1.9);
                    float a = alpha * Math.Max(0f, s);
                    a = a * a * a;                                                   // mostly dark, now and then a glint
                    if (a <= 0.02f) continue;
                    float size = 0.8f + (Hash(hs ^ 0x9E37u) & 0xFF) / 255f * 0.9f;
                    f.Color = t.Glow.WithAlpha((byte)(70 * a));
                    c.DrawCircle(x, y, size * 3.5f, f);
                    f.Color = t.Secondary.WithAlpha((byte)(240 * a));
                    c.DrawCircle(x, y, size, f);
                    if (a > 0.35f)
                    {
                        float arm = size * 5.5f * a;
                        p.Color = t.Secondary.WithAlpha((byte)(150 * a));
                        c.DrawLine(x - arm, y, x + arm, y, p);
                        c.DrawLine(x, y - arm, x, y + arm, p);
                    }
                }
                p.StrokeWidth = 1f;
                break;
            }
            default:   // glints: slow motes in the light, wandering sideways
            {
                for (int i = 0; i < n; i++)
                {
                    uint hs = Hash((uint)i * 668265263u + 31u);
                    float x0 = (hs & 0xFFFF) / 65535f, y0 = ((hs >> 16) & 0xFFFF) / 65535f;
                    float drift = 4f + (Hash(hs) & 0xFF) / 255f * 8f;
                    float span = w + 30f;
                    float x = -15f + (float)((x0 * span + ts * drift) % span);
                    float y = y0 * h + 6f * (float)Math.Sin(ts * 0.4 + i * 1.3);
                    float a = alpha * (0.35f + 0.35f * (float)Math.Sin(ts * (0.7 + y0) + i));
                    if (a <= 0.02f) continue;
                    float size = 0.8f + (Hash(hs ^ 0x51u) & 0xFF) / 255f * 1.2f;
                    f.Color = t.Glow.WithAlpha((byte)(40 * a));
                    c.DrawCircle(x, y, size * 4f, f);
                    f.Color = t.Secondary.WithAlpha((byte)(170 * a));
                    c.DrawCircle(x, y, size, f);
                }
                break;
            }
        }
    }

    /// <summary>Where the corner motif sits on each surface (corner + which way it grows + its size); Strip / Ticker are
    /// too thin for one (the pinstripe still runs along their foot).</summary>
    static int Corners(ThemeSurface s, ThemeMotif m, float w, float h, Span<(float X, float Y, int Dx, int Dy, float Size)> o)
    {
        switch (s)
        {
            // the session bar's chips sit along its foot: the #494 cobwebs hang in the bottom corners, the other motifs
            // stay small in the top corners, clear of the track name and the lap chip
            case ThemeSurface.Bar when m != ThemeMotif.CobwebBat: o[0] = (0, 0, 1, 1, 26f); o[1] = (w, 0, -1, 1, 26f); return 2;
            case ThemeSurface.Tower: o[0] = (w, 0, -1, 1, 54f); return 1;
            case ThemeSurface.Splash: o[0] = (w, 0, -1, 1, 54f); return 1;
            case ThemeSurface.Bar: o[0] = (0, h, 1, -1, 40f); o[1] = (w, h, -1, -1, 40f); return 2;
            case ThemeSurface.Strip: case ThemeSurface.Ticker: return 0;
            default: o[0] = (w, 0, -1, 1, Math.Min(44f, h * 0.45f)); return 1;
        }
    }

    static void DrawMotif(SKCanvas c, ThemeCache cc, ThemeDef t, ThemeSurface surface, float w, float h, double ts, float alpha, SKPaint f)
    {
        var p = line ??= new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        if (t.Motif == ThemeMotif.Pinstripe)
        {
            // a livery pinstripe along the foot: accent, second colour, glow (bleu / blanc / rouge on Le Mans)
            float y = h - 3.5f;
            f.Color = t.Accent.WithAlpha((byte)(200 * alpha)); c.DrawRect(new SKRect(0, y, w, y + 1.5f), f);
            f.Color = t.Secondary.WithAlpha((byte)(150 * alpha)); c.DrawRect(new SKRect(0, y - 2.5f, w, y - 1.5f), f);
            f.Color = t.Glow.WithAlpha((byte)(190 * alpha)); c.DrawRect(new SKRect(0, y - 5f, w, y - 3.5f), f);
            return;
        }
        Span<(float X, float Y, int Dx, int Dy, float Size)> corners = stackalloc (float, float, int, int, float)[2];
        int n = Corners(surface, t.Motif, w, h, corners);
        for (int i = 0; i < n; i++)
        {
            var (x, y, dx, dy, size) = corners[i];
            if (size < 8f) continue;
            c.Save(); c.Translate(x, y); c.Scale(dx, dy);
            switch (t.Motif)
            {
                case ThemeMotif.CobwebBat:
                    p.Color = new SKColor(255, 236, 214, (byte)(46 * alpha));
                    c.DrawPath(cc.Cobweb(size), p);
                    break;
                case ThemeMotif.Snowflake:
                    p.Color = t.Secondary.WithAlpha((byte)(64 * alpha)); p.StrokeWidth = 1.2f;
                    c.DrawPath(cc.Flake(size), p);
                    p.StrokeWidth = 1f;
                    break;
                case ThemeMotif.FlagStripes:
                {
                    var (s0, s1, s2) = cc.Stripes(size);
                    f.Color = t.Accent.WithAlpha((byte)(150 * alpha)); c.DrawPath(s0, f);
                    f.Color = t.Secondary.WithAlpha((byte)(120 * alpha)); c.DrawPath(s1, f);
                    f.Color = t.Glow.WithAlpha((byte)(150 * alpha)); c.DrawPath(s2, f);
                    break;
                }
                case ThemeMotif.Chequered:
                    f.Shader = cc.ChequerFade(size, t.Secondary); f.Color = new SKColor(255, 255, 255, (byte)(255 * alpha));
                    c.DrawPath(cc.Chequer(size), f);
                    f.Shader = null;
                    break;
            }
            c.Restore();
        }
        // the bat flies on the cards (Halloween)
        if (t.Motif == ThemeMotif.CobwebBat && surface is not (ThemeSurface.Tower or ThemeSurface.Bar or ThemeSurface.Strip or ThemeSurface.Ticker or ThemeSurface.Splash) && h > 40f)
        {
            float flap = 0.75f + 0.25f * (float)Math.Sin(ts * 3.1);
            float bob = 2f * (float)Math.Sin(ts * 1.3);
            c.Save(); c.Translate(w - 70f, 22f + bob); c.Scale(1f, flap);
            var bat = cc.Bat(13f);
            f.Color = new SKColor(8, 3, 1, (byte)(220 * alpha));
            c.DrawPath(bat, f);
            p.Color = t.Accent.WithAlpha((byte)(60 * alpha));
            c.DrawPath(bat, p);
            c.Restore();
        }
    }

    internal static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16;
        return x;
    }

    /// <summary>Everything a theme draws that does not move, built once: the texture tile (per theme), the gradients (per
    /// theme and surface size, in the surface's own coordinates) and the motif paths (per size). Render thread only; the
    /// whole cache is dropped when the theme changes.</summary>
    sealed class ThemeCache
    {
        ThemeDef? forTheme;
        public SKShader? Tile;
        SKImage? tileImg;
        readonly Dictionary<(ThemeSurface, int, int), Entry> entries = new();
        readonly Dictionary<int, SKPath> cobwebs = new(), flakes = new(), chequers = new();
        readonly Dictionary<int, (SKPath, SKPath, SKPath)> stripes = new();
        readonly Dictionary<(int, uint), SKShader> chequerFades = new();
        SKPath? bat;

        public sealed class Entry
        {
            public SKShader? Wash, GlowEdge, Pool, Frost, SweepShader;
            public float GlowH, PoolR, FrostH, SweepW;
            public void Dispose() { Wash?.Dispose(); GlowEdge?.Dispose(); Pool?.Dispose(); Frost?.Dispose(); SweepShader?.Dispose(); }
        }

        public void Use(ThemeDef t)
        {
            if (ReferenceEquals(forTheme, t)) return;
            Clear();
            forTheme = t;
            tileImg = MakeTile(t.Texture);
            // the tile is drawn at 2x density (512 device px = 256 design px on the 4K screen)
            Tile = tileImg?.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, SKMatrix.CreateScale(0.5f, 0.5f));
        }

        void Clear()
        {
            foreach (var e in entries.Values) e.Dispose();
            entries.Clear();
            foreach (var s in chequerFades.Values) s.Dispose();
            chequerFades.Clear();
            Tile?.Dispose(); Tile = null;
            tileImg?.Dispose(); tileImg = null;
        }

        public Entry For(ThemeDef t, ThemeSurface s, float w, float h)
        {
            // sizes snap to 4 px (wide) / 2 px (tall) so a surface that grows (the battle-mode bar) reuses a few entries
            var key = (s, (int)MathF.Round(w / 4f), (int)MathF.Round(h / 2f));
            if (entries.TryGetValue(key, out var e)) return e;
            if (entries.Count >= 64) { foreach (var x in entries.Values) x.Dispose(); entries.Clear(); }
            float W = key.Item2 * 4f, H = Math.Max(2f, key.Item3 * 2f);
            e = new Entry();
            byte op = (byte)Math.Clamp(255f * t.GlassOpacity, 0f, 255f), op2 = (byte)Math.Clamp(255f * t.GlassOpacity * (120f / 170f), 0f, 255f);
            e.Wash = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H),
                new[] { t.Glass.WithAlpha(op), t.Glass.WithAlpha(op2) }, null, SKShaderTileMode.Clamp);
            e.GlowH = Math.Min(H, s == ThemeSurface.Tower ? 260f : H * 0.9f);
            e.GlowEdge = SKShader.CreateLinearGradient(new SKPoint(0, H - e.GlowH), new SKPoint(0, H),
                new[] { t.Glow.WithAlpha(0), t.Glow.WithAlpha(38), t.Glow.WithAlpha(78) }, new[] { 0f, 0.7f, 1f }, SKShaderTileMode.Clamp);
            e.PoolR = Math.Max(W, H) * 0.45f;
            e.Pool = SKShader.CreateRadialGradient(new SKPoint(0, 0), e.PoolR, new[] { t.Glow.WithAlpha(20), t.Glow.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
            if (t.Texture == ThemeTexture.Snow)
            {
                e.FrostH = Math.Max(12f, H * 0.38f);
                e.Frost = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, e.FrostH),
                    new[] { t.Secondary.WithAlpha(46), t.Secondary.WithAlpha(12), t.Secondary.WithAlpha(0) }, new[] { 0f, 0.35f, 1f }, SKShaderTileMode.Clamp);
            }
            if (t.Sweep)
            {
                e.SweepW = Math.Clamp(W * 0.22f, 60f, 240f);
                e.SweepShader = SKShader.CreateLinearGradient(new SKPoint(-e.SweepW, 0), new SKPoint(e.SweepW, e.SweepW * 0.35f),
                    new[] { new SKColor(255, 255, 255, 0), new SKColor(255, 255, 255, 22), new SKColor(255, 255, 255, 0) }, new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            }
            entries[key] = e;
            return e;
        }

        /// <summary>A quarter cobweb growing from the origin into +x / +y.</summary>
        public SKPath Cobweb(float size)
        {
            int k = (int)MathF.Round(size);
            if (cobwebs.TryGetValue(k, out var path)) return path;
            path = new SKPath();
            const int Spokes = 5;
            var ends = new SKPoint[Spokes];
            for (int s = 0; s < Spokes; s++)
            {
                double ang = (Math.PI / 2) * s / (Spokes - 1);
                ends[s] = new SKPoint(size * (float)Math.Cos(ang), size * (float)Math.Sin(ang));
                path.MoveTo(0, 0); path.LineTo(ends[s]);
            }
            for (int ring = 1; ring <= 3; ring++)
            {
                float kk = ring / 3.4f;
                for (int s = 0; s < Spokes - 1; s++)
                {
                    var a0 = Lerp(default, ends[s], kk); var a1 = Lerp(default, ends[s + 1], kk);
                    var mid = Lerp(a0, a1, 0.5f);
                    var sag = Lerp(mid, default, 0.16f);             // the thread sags towards the corner
                    path.MoveTo(a0); path.QuadTo(sag, a1);
                }
            }
            cobwebs[k] = path;
            return path;
        }

        /// <summary>A six-armed snowflake (with side branches) sitting inside the corner.</summary>
        public SKPath Flake(float size)
        {
            int k = (int)MathF.Round(size);
            if (flakes.TryGetValue(k, out var path)) return path;
            path = new SKPath();
            float cx = size * 0.55f, cy = size * 0.55f, R = size * 0.4f;
            for (int a = 0; a < 6; a++)
            {
                double ang = Math.PI / 6 + a * Math.PI / 3;
                float ux = (float)Math.Cos(ang), uy = (float)Math.Sin(ang);
                path.MoveTo(cx, cy); path.LineTo(cx + ux * R, cy + uy * R);
                foreach (float at in new[] { 0.45f, 0.72f })
                {
                    float bx = cx + ux * R * at, by = cy + uy * R * at, bl = R * (at < 0.5f ? 0.3f : 0.2f);
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        double b = ang + sgn * Math.PI / 4;
                        path.MoveTo(bx, by); path.LineTo(bx + (float)Math.Cos(b) * bl, by + (float)Math.Sin(b) * bl);
                    }
                }
            }
            // a small second flake further in
            float cx2 = size * 1.25f, cy2 = size * 0.28f, r2 = size * 0.16f;
            for (int a = 0; a < 6; a++)
            {
                double ang = a * Math.PI / 3;
                path.MoveTo(cx2, cy2); path.LineTo(cx2 + (float)Math.Cos(ang) * r2, cy2 + (float)Math.Sin(ang) * r2);
            }
            flakes[k] = path;
            return path;
        }

        /// <summary>Three parallel bands cutting across the corner (a livery stripe).</summary>
        public (SKPath, SKPath, SKPath) Stripes(float size)
        {
            int k = (int)MathF.Round(size);
            if (stripes.TryGetValue(k, out var s)) return s;
            SKPath Band(float d0, float d1)
            {
                var p = new SKPath();
                // the band between the lines x + y = d0 and x + y = d1, i.e. a 45-degree cut across the corner
                p.MoveTo(d0, 0); p.LineTo(d1, 0); p.LineTo(0, d1); p.LineTo(0, d0); p.Close();
                return p;
            }
            float bw = Math.Max(3f, size * 0.11f), gap = Math.Max(2f, size * 0.07f), d = size * 0.72f;
            s = (Band(d, d + bw), Band(d + bw + gap, d + 2 * bw + gap), Band(d + 2 * bw + 2 * gap, d + 3 * bw + 2 * gap));
            stripes[k] = s;
            return s;
        }

        /// <summary>Chequer squares filling the corner triangle (faded by <see cref="ChequerFade"/>).</summary>
        public SKPath Chequer(float size)
        {
            int k = (int)MathF.Round(size);
            if (chequers.TryGetValue(k, out var path)) return path;
            path = new SKPath();
            float q = Math.Max(4f, size / 7f), lim = size * 1.5f;
            for (int iy = 0; iy * q < lim; iy++)
                for (int ix = 0; ix * q < lim; ix++)
                    if ((ix + iy) % 2 == 0 && (ix + iy) * q < lim) path.AddRect(new SKRect(ix * q, iy * q, ix * q + q, iy * q + q));
            chequers[k] = path;
            return path;
        }

        public SKShader ChequerFade(float size, SKColor col)
        {
            var key = ((int)MathF.Round(size), (uint)col);
            if (chequerFades.TryGetValue(key, out var sh)) return sh;
            sh = SKShader.CreateRadialGradient(new SKPoint(0, 0), size * 1.5f, new[] { col.WithAlpha(70), col.WithAlpha(24), col.WithAlpha(0) }, new[] { 0f, 0.55f, 1f }, SKShaderTileMode.Clamp);
            chequerFades[key] = sh;
            return sh;
        }

        /// <summary>A small bat silhouette centred on the origin, wings up (the flap is a vertical scale when drawn).</summary>
        public SKPath Bat(float s)
        {
            if (bat != null) return bat;
            var path = new SKPath();
            float x = 0, y = 0, wy = s * 0.55f;
            path.MoveTo(x, y - s * 0.25f);
            path.LineTo(x - s * 0.18f, y - s * 0.5f); path.LineTo(x - s * 0.12f, y - s * 0.22f);   // left ear
            path.QuadTo(x - s * 0.7f, y - wy - s * 0.2f, x - s * 1.6f, y - wy);                     // left wing top
            path.QuadTo(x - s * 1.25f, y - wy * 0.2f, x - s * 1.05f, y + s * 0.05f);
            path.QuadTo(x - s * 0.85f, y - s * 0.1f, x - s * 0.6f, y + s * 0.15f);
            path.QuadTo(x - s * 0.4f, y + s * 0.05f, x, y + s * 0.42f);                            // body
            path.QuadTo(x + s * 0.4f, y + s * 0.05f, x + s * 0.6f, y + s * 0.15f);
            path.QuadTo(x + s * 0.85f, y - s * 0.1f, x + s * 1.05f, y + s * 0.05f);
            path.QuadTo(x + s * 1.25f, y - wy * 0.2f, x + s * 1.6f, y - wy);
            path.QuadTo(x + s * 0.7f, y - wy - s * 0.2f, x + s * 0.12f, y - s * 0.22f);
            path.LineTo(x + s * 0.18f, y - s * 0.5f);
            path.Close();
            return bat = path;
        }

        static SKPoint Lerp(SKPoint a, SKPoint b, float k) => new(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k);

        /// <summary>The procedural texture tile (512 x 512, seamless), premultiplied white / black specks at low alpha;
        /// the paint's alpha sets the strength. Null for textures that have no tile (none / embers).</summary>
        static SKImage? MakeTile(ThemeTexture tex)
        {
            if (tex is ThemeTexture.None or ThemeTexture.Embers) return null;
            const int N = 512;
            var px = new byte[N * N * 4];
            void Put(int x, int y, bool white, float a)
            {
                x &= N - 1; y &= N - 1;
                byte aa = (byte)Math.Clamp(a * 255f, 0f, 255f), v = white ? aa : (byte)0;
                int o = (y * N + x) * 4;
                // keep the strongest speck where two land on the same pixel
                if (aa <= px[o + 3]) return;
                px[o] = v; px[o + 1] = v; px[o + 2] = v; px[o + 3] = aa;
            }
            switch (tex)
            {
                case ThemeTexture.Grain:
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            uint h = Hash((uint)(y * N + x) * 747796405u + 1u);
                            float v = (h & 0xFF) / 255f - 0.5f;
                            Put(x, y, v > 0, Math.Abs(v) * 0.16f);
                        }
                    break;
                case ThemeTexture.Carbon:
                    // a 2x2 twill of 16 px (device) cells, each a light-to-dark ramp across or down the weave
                    for (int y = 0; y < N; y++)
                        for (int x = 0; x < N; x++)
                        {
                            int cx = x / 16, cy = y / 16;
                            bool down = ((cx + cy) & 1) == 0;
                            float k = (down ? (y & 15) : (x & 15)) / 15f;
                            float v = 0.11f - k * 0.30f;                           // +11 % white at the start, -19 % black at the end
                            uint h = Hash((uint)(y * N + x) * 2891336453u + 5u);
                            v += ((h & 0xFF) / 255f - 0.5f) * 0.04f;
                            Put(x, y, v > 0, Math.Abs(v) * 0.75f);
                        }
                    break;
                case ThemeTexture.Brushed:
                    // long horizontal streaks: per-row value noise along x (wavelength 64 px), plus a little grain
                    for (int y = 0; y < N; y++)
                    {
                        float row = (Hash((uint)y * 1597334677u) & 0xFF) / 255f - 0.5f;
                        for (int x = 0; x < N; x++)
                        {
                            int i0 = x / 64, i1 = (i0 + 1) % (N / 64);
                            float fr = (x % 64) / 64f; fr = fr * fr * (3 - 2 * fr);
                            float n0 = (Hash((uint)(y * 64 + i0) * 3812015801u) & 0xFF) / 255f - 0.5f;
                            float n1 = (Hash((uint)(y * 64 + i1) * 3812015801u) & 0xFF) / 255f - 0.5f;
                            float v = row * 0.6f + (n0 + (n1 - n0) * fr) * 0.4f;
                            v += ((Hash((uint)(y * N + x) * 958473u) & 0xFF) / 255f - 0.5f) * 0.12f;
                            Put(x, y, v > 0, Math.Abs(v) * 0.22f);
                        }
                    }
                    break;
                case ThemeTexture.Snow:
                    // frost dust: sparse soft specks
                    for (int i = 0; i < 900; i++)
                    {
                        uint h = Hash((uint)i * 2654435761u + 11u);
                        int x = (int)(h & 511), y = (int)((h >> 9) & 511);
                        float a = 0.18f + ((h >> 18) & 0xFF) / 255f * 0.32f;
                        Put(x, y, true, a);
                        Put(x + 1, y, true, a * 0.45f); Put(x - 1, y, true, a * 0.45f); Put(x, y + 1, true, a * 0.45f); Put(x, y - 1, true, a * 0.45f);
                    }
                    break;
                case ThemeTexture.Stars:
                    // a faint star field: many tiny points, a few brighter ones with a soft cross
                    for (int i = 0; i < 420; i++)
                    {
                        uint h = Hash((uint)i * 1103515245u + 17u);
                        int x = (int)(h & 511), y = (int)((h >> 9) & 511);
                        float a = 0.16f + ((h >> 18) & 0xFF) / 255f * 0.38f;
                        bool big = ((h >> 26) & 7) == 0;
                        Put(x, y, true, big ? a + 0.3f : a);
                        if (big)
                            for (int d = 1; d <= 3; d++)
                            {
                                float q = (a + 0.3f) * (0.55f - d * 0.14f);
                                Put(x + d, y, true, q); Put(x - d, y, true, q); Put(x, y + d, true, q); Put(x, y - d, true, q);
                            }
                    }
                    break;
            }
            var info = new SKImageInfo(N, N, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var data = SKData.CreateCopy(px);
            return SKImage.FromPixels(info, data, N * 4);
        }
    }
}
