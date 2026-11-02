using ApexHUD.Native.Common;
using ApexHUD.Native.Overlays;
using SkiaSharp;

/// <summary>The preview scenarios (round 2, 2026-10-27): realistic Le Mans moments over the REAL overlay composition
/// (tower, session bar + watch, ticker, radio card drawn by the app's own renderers - see <see cref="Comp"/>), with
/// clearly placeholder drivers.</summary>
static partial class Scenarios
{
    public static readonly string MapFile = Assets.Resolve("maps", "Circuit des 24 Heures du Mans.json");
    public const string MapKey = "Circuit des 24 Heures du Mans";
    static VpTrack? track;
    public static VpTrack? Track => track ??= VpTrack.Load(MapFile);

    // Le Mans (iRacing "lemans full", 13.626 km) corners as lap fractions, from the track landmarks
    public const float DunlopCurve = 0.043f, Esses = 0.111f, TertreRouge = 0.141f, Mulsanne = 0.568f, Indianapolis = 0.72f,
                       Arnage = 0.746f, PorscheCurves = 0.87f, FordChicane = 0.975f;

    /// <summary>The "PC clock" in previews (placeholder): 9:41:00 PM, running with the preview.</summary>
    public static readonly DateTime PreviewClock = new(2026, 10, 27, 21, 41, 0);

    public static IEnumerable<Scenario> All()
    {
        yield return W8();
        yield return W12();
        yield return W11();
        yield return W11C();
        yield return V1();
        yield return V2();
        yield return V5();
        yield return V6();
        yield return V4();
        yield return V4H();
        yield return V7();
        yield return V8();
        yield return V8B();
        yield return V8PS();
        yield return V8DU();
        yield return V8AR();
    }

    /// <summary>True on the one frame that crosses <paramref name="at"/> ms.</summary>
    public static bool Cross(double t, double at) => t >= at && t - 1000.0 / 60 < at;

    // placeholder drivers (invented names)
    const string GTP = "#000000", LMP2 = "#0072CE", GT3 = "#00A94F";

    // ------------------------------------------------------------------ W8 + #336
    static Scenario W8()
    {
        Comp? comp = null;
        return new("W8", "W8", "Going to break card + local-time watch (#336)", "bg_crowd", 9.0, new[] { 0.5, 1.2, 4.0, 8.0 },
            elev =>
            {
                comp = new Comp();
                var b = new BreakCard { Elevated = elev };
                b.Watch.Clock = () => PreviewClock.AddMilliseconds(clockMs);
                return b;
            },
            (g, t) => ((BreakCard)g).Show(new BreakModel("Circuit des 24 Heures du Mans", "Lap 14 of 30  ·  1:59:57 to go", "REPLAY PAUSED  ·  LAP 14  ·  20:42", new[]
            {
                new BreakClass("GTP", GTP, new[] { ("6", "L. Ardenne", "LEADER"), ("01", "R. Kestrel", "+2.8"), ("23", "K. Vosskamp", "+4.1") }),
                new BreakClass("LMP2", LMP2, new[] { ("10", "M. Haldane", "LEADER"), ("04", "C. Duval", "+6.3"), ("2", "B. Keaton", "+11.0") }),
                new BreakClass("GT3", GT3, new[] { ("7", "T. Morvan", "LEADER"), ("81", "J. Tallis", "+1.2"), ("62", "D. Serra", "+3.9") }),
            }, Track), t))
        {
            Frame = (g, t) => { clockMs = t; if (Cross(t, 7500)) g.Hide(t); },
            Under = (c, w, h, t) => { comp!.Under(c, t); comp.Top(c, t); },   // the break card covers the session bar (real z-order)
            Note = "Approved as is; #336 adds the broadcast watch (his dial via ClockDial, real PC time) top middle.",
        };
    }
    static double clockMs;

    // ------------------------------------------------------------------ W12
    static Scenario W12()
    {
        Comp? comp = null;
        return new("W12", "W12", "Race control card in the radio column", "bg_straight", 10.0, new[] { 1.2, 4.6, 8.6 },
            elev => { comp = new Comp { RadioAtMs = 3200, RadioWho = "RACE CONTROL", RadioText = "Car 51 stopped at Tertre Rouge, local yellow. Marshals attending.", BattleAt = t => t > 7000 }; return new RaceControlCard { Elevated = elev }; },
            (g, t) => ((RaceControlCard)g).Show(new RcMessage(RcCategory.TrackLimits, "L12", "14:32", "23", "K. VOSSKAMP", "GTP", GTP, "TRACK LIMITS AT THE FIRST FORD CHICANE, +1 INCIDENT POINT", 6, 17), t))
        {
            Frame = (g, t) =>
            {
                var r = (RaceControlCard)g;
                if (Cross(t, 2600)) r.Show(new RcMessage(RcCategory.Warning, "L12", "14:33", "10", "M. HALDANE", "LMP2", LMP2, "BLACK AND WHITE FLAG FOR TRACK LIMITS", 8, 17), t);
                if (Cross(t, 6800)) r.Show(new RcMessage(RcCategory.Penalty, "L13", "14:36", "81", "J. TALLIS", "GT3", GT3, "DRIVE THROUGH PENALTY: SPEEDING IN THE PIT LANE", 4, 17), t);
            },
            Under = (c, w, h, t) => comp!.Under(c, t),
            Top = (c, w, h, t) => comp!.Top(c, t),
            Note = "1.2 s: alone, standing on the radio card's anchor. 4.6 s: a race-control radio card is up - it stacks right above it. 8.6 s: battle mode (full-width bar + ticker, tower away) - same column, clear of the ticker.",
        };
    }

    // ------------------------------------------------------------------ W11
    // round 3: the flare only in free sky. W11: a stretch of the real race where the sun is in the right third's sky -
    // the flare shows there, then a battle starts at 4 s (full-width bar, ticker, battle map up near the sun, battle
    // card) and it fades away. W11C: the round-2 stretch (sun near the centre, by the session bar) - nothing is drawn.
    static Scenario W11() => W11Make("W11", "Lens flare, free sky only: a SCRIPTED sun in the right third's sky; fades when the battle graphics come up",
        new[] { 0.2, 1.0, 2.5, 4.6, 6.5 }, free: true,
        new[] { "0.2 s  camera cut: nothing", "1.0 s  sun in free sky: flare", "2.5 s  held, kept off the bar", "4.6 s  battle map up by it: faded", "6.5 s  stays off while it's there" });

    static Scenario W11C() => W11Make("W11C", "Lens flare, real .ibt sun (by the session bar, near the centre): not drawn",
        new[] { 0.6, 2.5, 5.5 }, free: false,
        new[] { "0.6 s  real sun next to the bar / centre: off", "2.5 s  off", "5.5 s  off" });

    static Scenario W11Make(string id, string title, double[] keys, bool free, string[] sheet)
    {
        Comp? comp = null;
        W11Feed? feed = null;
        return new(id, id, title, "bg_straight", 7.0, keys,
            elev =>
            {
                comp = free ? new Comp { Tower = true, BattleAt = t => t > 4000, BattleCard = true } : new Comp { Tower = true };
                feed = W11Feed.Load();
                // W11: the sun's screen position is SCRIPTED (the real race barely puts it in the free sky: 0.3 s in the
                // whole race), drifting as the car turns; the rest (camera cut, timing) is the real feed
                return free ? new LensFx { Elevated = elev, ForceGolden = 0.85f, ForceSun = new SKPoint(1760, 262) } : new LensFx { Elevated = elev };
            },
            (g, t) => { })
        {
            Frame = (g, t) =>
            {
                var lf = (LensFx)g;
                if (free) lf.ForceSun = new SKPoint((float)(1770 - 120 * Math.Min(1, t / 7000.0)), (float)(262 - 18 * Math.Sin(t / 2600.0)));
                feed?.Tick(lf, t);
            },
            Under = (c, w, h, t) => comp!.Under(c, t),
            Top = (c, w, h, t) => { comp!.Top(c, t); if (!free && comp.LastLens is LensFx ll) feed?.Diagram(c, ll, t); },
            Custom = (c, w, h, t, g) => { comp!.LastLens = g; g.Draw(c, w, h, t); },
            Sheet = sheet,
            Note = (free ? "SCRIPTED sun position (a geometry demo of where the flare may live - his real race only put the sun in free sky for 0.3 s); camera cut and timing from the real feed. " : "") + "Real telemetry from Angelo's Le Mans race (.ibt): the chase camera on his car looks along YawNorth; the flare sits where the sim's SolarAzimuth / SolarAltitude project through a 70 deg chase lens. Round 3: drawn only while the sun is in the top band (y < 620) of the left / right third and 260 px clear of every graphic; confined to that third with soft holes cut round every graphic; short ghost chain; no heat haze. The still background does NOT move with the car - the inset (a preview aid, bottom right) shows the geometry.",
        };
    }

    // ------------------------------------------------------------------ V1
    static Scenario V1()
    {
        Comp? comp = null;
        return new("V1", "V1", "StormExhibit weather card, placed under the session bar stack", "bg_trees", 11.0, new[] { 0.25, 1.6, 7.5 },
            elev => { comp = new Comp { BattleAt = t => t > 5200 }; return new WeatherCard { Elevated = elev }; },
            (g, t) => ((WeatherCard)g).Show(new WeatherModel(WeatherKind.RainStart, "RAIN AT", "TERTRE ROUGE", "LAP 14",
                4, 2, "64°F", "75°F", "70%", Track, TertreRouge), t))
        {
            Under = (c, w, h, t) => comp!.Under(c, t),
            Top = (c, w, h, t) => comp!.Top(c, t),
            Note = "Sponsor slot \"WeatherCard\" (StormExhibit by default): the name is typeset (Storm ivory + Exhibit storm blue), or his own logo file if he names one for the Storm partner. From 5.2 s the bar goes to battle mode with the ticker: the card moves down under it (in the app it also bows out of battles).",
        };
    }

    // ------------------------------------------------------------------ V2
    static float NightCurve(double t) => Vp.EaseIo(Vp.Seg(t, 1200, 7800));
    static readonly SKPaint nightTint = new() { BlendMode = SKBlendMode.Multiply };

    static Scenario V2() => new("V2", "V2", "Day -> dusk -> night palette over the real package", "bg_trees", 9.0, new[] { 0.5, 4.5, 8.8 },
        elev => new NightDemo { Elevated = elev, NightAt = NightCurve },
        (g, t) => { })
    {
        Under = (c, w, h, t) =>
        {
            float k = NightCurve(t);
            nightTint.Color = Vp.Mix(SKColors.White, new SKColor(52, 64, 98), k);
            c.DrawRect(0, 0, w, h, nightTint);
        },
        Sheet = new[] { "DAY  ·  SUN 12 DEG UP", "DUSK  ·  SUN AT THE HORIZON", "NIGHT  ·  SUN 6 DEG DOWN" },
        Note = "The picture is darkened as a stand-in for the sim's own night. The package itself changes palette: midnight-navy glass, warm ivory type, amber golds. In the app the change follows the sim's sun (+6 to -6 deg = about an hour of sim time at Le Mans) - the preview runs it in 6.6 s.",
    };

    // ------------------------------------------------------------------ V5
    static Scenario V5()
    {
        Comp? comp = null;
        return new("V5", "V5", "BACK TO LIVE card: free space only, gone 4 s after live", "bg_pits", 6.0, new[] { 0.22, 0.6, 1.25, 2.2, 3.1, 3.4 },
            elev =>
            {
                // round 3: back at live at -0.65 s (it cuts in as the wipe clears, at 0); at 1.0 s a battle starts - full-width bar, the
                // ticker, the battle card and map come up where the plate was - so it moves to free space; gone by 3.35 s
                comp = new Comp { BattleAt = t => t > 1000, BattleCard = true, RadioAtMs = 300, RadioWho = "RACE CONTROL", RadioText = "Track is clear. Green flag." };
                return new LiveTag();
            },
            (g, t) => ((LiveTag)g).Show("LAP 14  ·  1:59:57 TO GO", t))
        {
            Under = (c, w, h, t) => comp!.Under(c, t),
            Top = (c, w, h, t) => comp!.Top(c, t),
            Variants = new[] { false },
            Sheet = new[] { "0.2 s  cut in, REPLAY tag corner", "0.6 s  held (radio card up)", "1.25 s  bar widens: jumps under the ticker", "2.2 s  battle map up: below it", "3.1 s  on its way out", "3.4 s  gone (4.05 s after live)" },
            Note = "Round 3: placed only in free space (VpLayout: session bar + watch, ticker, tower, battle card + map, radio, story / custom / featured cards, the pack's own cards; centre third kept clear). Moves if something comes up where it is, goes at once if there is nowhere free, is skipped if there is no free spot when it would show. Gone 4.0 s after the picture is back at live (+0.65 s cut-in, 2.9 s hold, 0.4 s out).",
        };
    }

    // ------------------------------------------------------------------ V6 + #337
    static Scenario V6()
    {
        Comp? comp = null;
        ReplayBumper? bump = null;
        return new("V6", "V6", "Replay bumper: rewinding watch + battle-mode session bar (#337)", "bg_crowd", 3.4, new[] { 0.25, 0.9, 1.5, 2.6 },
            elev =>
            {
                bump = new ReplayBumper { Elevated = elev };
                comp = new Comp { BattleAt = t => bump.Active(t + 200) };
                return bump;
            },
            (g, t) => ((ReplayBumper)g).Show(new BumperModel("A&B", "GENERAL CONTRACTING", null, "LAP 12  ·  TERTRE ROUGE", TimeOfDay: 14 * 3600 + 50 * 60), t + 200))
        {
            Under = (c, w, h, t) => comp!.Under(c, t),
            Top = (c, w, h, t) => comp!.Top(c, t),
            Note = "The doors' centre is the broadcast watch (his dial through ClockDial - the bundled homage here, his own on the PC), hands rewinding to the replayed moment's sim time. The session bar goes full width with the standings ticker for as long as the bumper plays.",
        };
    }

    // ------------------------------------------------------------------ V4 / V7 / V8 live in Scenarios2.cs
}

sealed class W11Feed
{
    double[] yawN = Array.Empty<double>(), az = Array.Empty<double>(), alt = Array.Empty<double>(), pct = Array.Empty<double>(), spd = Array.Empty<double>(), tod = Array.Empty<double>();
    public int Start = 202540;
    readonly RaceSnapshot baseSnap = Canned.Build();
    public double LastOutline = double.NaN;
    public static long TickBytes; public static int Ticks;

    public static W11Feed? Load()
    {
        string? ibt = Environment.GetEnvironmentVariable("GS_IBT");
        if (ibt == null || !File.Exists(ibt)) { Console.WriteLine("   W11: set GS_IBT to the Le Mans race .ibt"); return null; }
        var f = IbtFile.Open(ibt);
        var d = f.Read(new[] { "YawNorth", "SolarAzimuth", "SolarAltitude", "LapDistPct", "Speed", "SessionTimeOfDay" });
        return new W11Feed { yawN = d["YawNorth"], az = d["SolarAzimuth"], alt = d["SolarAltitude"], pct = d["LapDistPct"], spd = d["Speed"], tod = d["SessionTimeOfDay"] };
    }

    public int Index(double t) => Math.Min(yawN.Length - 1, Start + (int)(t / 1000.0 * 60));


    public void Tick(LensFx lens, double t)
    {
        if (yawN.Length == 0) return;
        int i = Index(t);
        var gtp = baseSnap.ClassLeaderboard[0].Drivers;
        var me = gtp[0];
        var ob = new OnboardData(me.CarIdx, 5, "Chase", true, true, true, 0, 0, 0, 0, 0, (float)spd[i], 6, 9000, 9000, 9600, true, LapPct: (float)pct[i]) { GroupOnlyName = "Chase" };
        var snap = baseSnap with
        {
            PlayerCarIdx = me.CarIdx, Player = me, CameraTarget = me, CameraCarIdx = me.CarIdx, SessionTimeOfDay = tod[i],
            Onboard = ob, PlayerOnboard = ob, SolarAzimuth = az[i], SolarAltitude = alt[i], PlayerYawNorth = yawN[i],
            Extras = baseSnap.Extras with { Skies = 1 },
        };
        int camKey = t < 200 ? 1 : 5 * 4099 + me.CarIdx;   // a camera cut at 0.2 s
        long a0 = GC.GetAllocatedBytesForCurrentThread();
        lens.Tick(snap, Scenarios.Track, Scenarios.MapKey, camKey, true, t);
        long da = GC.GetAllocatedBytesForCurrentThread() - a0;
        if (t > 1000) { TickBytes += da; Ticks++; }
        if (t > 6900 && t < 6920 && Ticks > 0) Console.WriteLine($"   W11 LensFx.Tick (sun alignment): {TickBytes / (double)Ticks:0.0} B/tick over {Ticks} ticks");
        // what the outline method would have said for the same car (shown in the inset)
        lens.Align.SetTrack(Scenarios.MapKey, Scenarios.Track);
        double h = lens.Align.Heading(false, double.NaN, (float)pct[i], out _);
        LastOutline = h;
    }

    static readonly SKPaint pp = new() { IsAntialias = true };
    static readonly SKPaint tx = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 12f };

    /// <summary>A top-down inset: north up, the car's heading, the 70 deg camera cone, the sun's bearing; and the numbers.</summary>
    public void Diagram(SKCanvas c, LensFx lens, double t)
    {
        if (yawN.Length == 0) return;
        int i = Index(t);
        var box = new SKRect(1920 - 24 - 360, 1080 - 24 - 250, 1920 - 24, 1080 - 24);
        pp.Style = SKPaintStyle.Fill; pp.Color = new SKColor(4, 7, 11, 225); c.DrawRoundRect(box, 8, 8, pp);
        pp.Style = SKPaintStyle.Stroke; pp.StrokeWidth = 1; pp.Color = new SKColor(255, 255, 255, 40); c.DrawRoundRect(box, 8, 8, pp);
        var o = new SKPoint(box.Left + 110, box.MidY + 10);
        float R = 86;
        pp.Color = new SKColor(255, 255, 255, 30); c.DrawCircle(o, R, pp);
        tx.Color = Vp.Txt3; tx.TextAlign = SKTextAlign.Center; c.DrawText("N", o.X, o.Y - R - 6, tx);
        SKPoint Dir(double b, float r) => new(o.X + (float)Math.Sin(b) * r, o.Y - (float)Math.Cos(b) * r);
        double hd = yawN[i], half = 35 * Math.PI / 180;
        using (var cone = new SKPath())
        {
            cone.MoveTo(o); cone.LineTo(Dir(hd - half, R)); cone.ArcTo(new SKRect(o.X - R, o.Y - R, o.X + R, o.Y + R), (float)((hd - half) * 180 / Math.PI - 90), 70, false); cone.Close();
            pp.Style = SKPaintStyle.Fill; pp.Color = Vp.GoldHi.WithAlpha(40); c.DrawPath(cone, pp);
        }
        pp.Style = SKPaintStyle.Stroke; pp.StrokeWidth = 2.5f; pp.Color = Vp.Ivory; c.DrawLine(o, Dir(hd, R * 0.6f), pp);
        if (!double.IsNaN(LastOutline)) { pp.StrokeWidth = 1.2f; pp.Color = Vp.Storm; c.DrawLine(o, Dir(LastOutline, R * 0.9f), pp); }
        pp.Color = new SKColor(255, 190, 90); pp.StrokeWidth = 2; c.DrawLine(o, Dir(az[i], R + 6), pp);
        pp.Style = SKPaintStyle.Fill; c.DrawCircle(Dir(az[i], R + 12), 7, pp);
        tx.TextAlign = SKTextAlign.Left; tx.Color = Vp.Ivory; tx.TextSize = 12;
        float x = box.Left + 214, y = box.Top + 30;
        void L(string a, string b) { tx.Color = Vp.Txt3; c.DrawText(a, x, y, tx); tx.Color = Vp.Ivory; c.DrawText(b, x, y + 16, tx); y += 40; }
        double relDeg = SunAlign.Wrap(az[i] - hd) * 180 / Math.PI;
        L("SIM SUN (az / alt)", $"{az[i] * 180 / Math.PI:0.0}° / {alt[i] * 180 / Math.PI:0.0}°");
        L("CAR HEADING (YawNorth)", $"{(hd * 180 / Math.PI + 360) % 360:0.0}°");
        L("OUTLINE ESTIMATE", double.IsNaN(LastOutline) ? "-" : $"{(LastOutline * 180 / Math.PI + 360) % 360:0.0}°  ({lens.Align.CalibrationSource})");
        L("SUN vs LENS", $"{relDeg:+0.0;-0.0}°  ·  {(lens.SunInView ? "IN FRAME" : "OUT OF FRAME")}");
        L("FLARE", $"{lens.Visibility * 100:0}%  ·  ({lens.SunScreen.X:0}, {lens.SunScreen.Y:0})");
        tx.Color = Vp.Txt3; tx.TextSize = 10; c.DrawText("PREVIEW · REAL TELEMETRY · STILL PICTURE", box.Left + 14, box.Bottom - 12, tx);
    }
}
