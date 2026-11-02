using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Draws the broadcast Session Bar — the deep-glass strip the watch sits in, the
/// gold race-progress strip at the top, flag-state colouring across the whole
/// bar, the left zone (country flag, track name, air/track temps, animated
/// sun/rain weather) and the right zone (phase chip, session-time countdown,
/// lap counter, flag-state dot + label). Port of the production-visible parts
/// of the legacy <c>session-bar-ORIGINAL-v9-21.html</c>; see PORT_PLAN §1 for
/// the scoping cut (battle mode, ticker, rotating chip etc. are hidden in
/// production and intentionally omitted).
///
/// The watch centrepiece is drawn by the window on top of the bar, so this
/// class only carves a circular alcove in the middle and gets out of the way.
/// </summary>
public sealed class SessionBarRenderer : IDisposable
{
    // Bar geometry (authored in the legacy 976×92 dimensions; we scale to match).
    public const float BarWidth = 976f;
    public const float BarHeight = 92f;
    public const float ProgressBarHeight = 18f;

    // Watch alcove: the cached darkening behind the centrepiece, plus the area
    // we leave free in the right zone's left padding so layout doesn't collide.
    public const float WatchAlcoveRadius = 88f;

    // --- ported palette ---
    static readonly SKColor BarBase    = new(10, 15, 27, 222);   // 2026-10-23 redesign: the cards' deep glass
    static readonly SKColor AccentGold = new(0xC5, 0xA5, 0x72);                  // --accent-gold
    static readonly SKColor ProgressBase = new(0, 0, 0, (byte)(0.55 * 255));
    static readonly SKColor TextIvory  = new(252, 250, 245, 250);
    static readonly SKColor TextDim    = new(240, 240, 248, 235);
    static readonly SKColor TempLabelGold = new(255, 216, 118, 235);             // #340 legibility: was alpha 199 (0.78)
    static readonly SKColor TempDivider= new(201, 169, 97, 107);                 // rgba(201,169,97,0.42)
    static readonly SKColor PhaseGold  = new(0xFF, 0xD8, 0x76);
    static readonly SKColor PhaseFinalStint = new(0xFF, 0xB8, 0x70);
    static readonly SKColor PhaseFinalLaps  = new(0xFF, 0x88, 0x55);

    static readonly SKColor FlagGreen   = new(0x22, 0xC5, 0x5E);
    static readonly SKColor FlagYellow  = new(0xFF, 0xC1, 0x07);
    static readonly SKColor FlagRed     = new(0xDC, 0x26, 0x26);
    static readonly SKColor FlagWhite   = SKColors.White;
    static readonly SKColor FlagBlue    = new(0x3B, 0x82, 0xF6);
    static readonly SKColor FlagBlack   = new(0x1F, 0x29, 0x37);

    // Static paints/shaders (built once). The accent hairlines are NOT pre-
    // built because their gradient endpoints depend on the current bar width,
    // which now changes between default (976 px) and full screen during
    // battle — they're constructed inline each frame in DrawAccentHairlines.
    readonly SKPaint barBasePaint;
    readonly SKPaint alcovePaint;
    readonly SKPaint topGlossPaint;
    readonly SKPaint bottomDarkenPaint;
    readonly SKPaint progressBasePaint;
    readonly SKPaint progressGoldPaint;

    // Bar width currently being drawn — updated at the top of every Draw()
    // call. Lets the right zone re-anchor to the right edge as the bar
    // expands to full-screen during battle mode (Session 16 brief).
    float currentBarWidth = BarWidth;

    // ---- 2026-10-29 (efficiency): what the steady frame used to create every frame (a new SKPaint per draw, a gradient
    // shader per glow / hairline / bloom, the temperature / clock / lap strings) is kept between frames. Each kept paint
    // has exactly the properties the old "new SKPaint { ... }" had, re-set on every use where they vary; gradients sit in
    // ShaderSlots (rebuilt only when their inputs change); pixels checked identical with tools/race_frame's hashes.
    readonly SKPaint fillPaint = new();                                   // { Color = ... }
    readonly SKPaint aaFillPaint = new() { IsAntialias = true };           // { IsAntialias = true, Color = ... }
    readonly SKPaint shaderPaint = new();                                 // { Shader = ... }
    readonly SKPaint aaShaderPaint = new() { IsAntialias = true };         // { IsAntialias = true, Shader = ... }
    readonly SKPaint chipFillPaint = new() { Color = new SKColor(255, 255, 255, 14), IsAntialias = true };
    readonly SKPaint chipLinePaint = new() { Color = new SKColor(0xC5, 0xA5, 0x72, 70), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    readonly SKPaint smallLabelPaint = new() { Color = TempLabelGold, IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint smallValuePaint = new() { Color = TextDim, IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 15f };
    readonly SKPaint washPaint = new() { Color = SKColors.White.WithAlpha(135), FilterQuality = SKFilterQuality.Medium };
    readonly SKPaint scrimPaint = new() { Color = new SKColor(6, 9, 16, 42) };
    readonly SKPaint flagImgPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    readonly SKPaint flagBorderPaint = new() { Color = new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 128), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    readonly SKPaint trackPaint = new() { Color = TextIvory, IsAntialias = true, TextSize = 23f, Typeface = Fonts.CormorantGaramond.Bold };
    readonly SKPaint sunRayPaint = new() { Color = PhaseGold, Style = SKPaintStyle.Stroke, StrokeWidth = 1.7f, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
    readonly SKPaint sunCorePaint = new() { Color = PhaseGold, IsAntialias = true };
    readonly SKPaint sunCoreStroke = new() { Color = new SKColor(0xFF, 0xE8, 0xB0), Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f, IsAntialias = true };
    readonly SKPaint ghostLapPaint = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.ExtraBold, TextSize = 128f, Color = new SKColor(255, 255, 255, 15), TextAlign = SKTextAlign.Right };
    readonly SKPaint ghostLapLabel = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f, Color = new SKColor(0xC5, 0xA5, 0x72, 205), TextAlign = SKTextAlign.Right };
    readonly SKPaint phasePaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint timerPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 30f };
    readonly SKPaint flagOutlinePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
    readonly SKPaint flagShadowPaint = new() { Color = new SKColor(0, 0, 0, 150), IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f,
                                               MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 1.4f) };
    readonly SKPaint flagLabelPaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };
    readonly SKPaint wingNamePaint = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f };          // series name / page label
    readonly SKPaint wingTimePaint = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 19f };
    readonly SKPaint wingDriverPaint = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 22f };
    readonly SKPaint wingLogoPaint = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    readonly SKColorFilter?[] wingLogoFilters = new SKColorFilter?[256];
    readonly SKPath trendPath = new(), wingWashPath = new();
    readonly ShaderSlot bloomLeftShader = new(), bloomRightShader = new(), hairTopShader = new(), hairBottomShader = new(),
                        flagGlowShader = new(), flagLineShader = new(), progGlowShader = new(), progFillShader = new(), progHeadShader = new(),
                        wingDividerShader = new(), wingWashShader = new(), phaseRuleShader = new(), finalShineShader = new();
    readonly List<ShaderSlot> sectorShaders = new();
    static readonly float[] StopsEnds = { 0f, 1f }, StopsHair = { 0f, 0.18f, 0.82f, 1f }, StopsFlagLine = { 0f, 0.3f, 0.7f, 1f },
                            StopsGlow = { 0f, 0.4f, 1f }, StopsLight = { 0f, 0.5f, 1f };
    // strings drawn every frame, made once per value
    double airKey = double.NaN, trkKey = double.NaN; bool airF, trkF; string airText = "", trkText = "";
    int localMins = -1; string localText = "";
    long timerSec = -1; string timerText = "";
    int lapCur = int.MinValue, lapTot = int.MinValue; string lapText = "";
    string? trackIn; string trackUpper = "";
    int? seriesNameFor = int.MinValue; string seriesNameText = "";
    string? leaderClassIn; string leaderLabel = "";
    double fastLapIn = double.NaN; string fastLapText = "";
    double gapIn = double.NaN; string gapText = "";
    readonly Dictionary<(string?, string?, string?), string> wingNames = new();

    // Flag PNG cache (path-keyed). The cache lives for process lifetime.
    readonly Dictionary<string, SKBitmap?> flagCache = new(StringComparer.OrdinalIgnoreCase);

    // Track the longest session-time-left we've ever seen, mirroring the legacy
    // bar's `_bhMaxSecondsSeen`. Lets the phase chip compute a real progress
    // percent for timed races even though iRacing only publishes seconds-left.
    double maxSessionSecondsSeen = 0;

    // 2026-10-23 bar extras (Angelo said yes to all six)
    string lastFlagLabel = "";          // (1) flag-change wipe
    double flagChangeMs = -1e9;
    SKColor flagChangeCol;
    double lastElapsedMs;
    readonly List<(DateTime At, double C)> trackTempHist = new();   // (2) track temp trend
    int trendSession = int.MinValue;
    static readonly SKColor SecPurple = new(168, 85, 247), SecGreen = new(34, 197, 94), SecYellow = new(250, 204, 21);
    static readonly SKColor Amber = new(0xFF, 0xB8, 0x70);

    /// <summary>Final lap of a race: white flag, or a timed race whose clock has run out (leader on the last lap).</summary>
    // 2026-10-25 (Angelo: "what actually determines the last lap"): iRacing's rule for a timed race - when the clock
    // reaches zero the leader finishes the lap they're on and takes the WHITE flag at that crossing; the lap after is
    // the last one. So FINAL LAP starts when the leader crosses the line after the clock ran out (it used to start
    // at 0:00 - a lap early). Lap races: the leader starting the final lap. The white flag itself still counts live
    // (replays don't record flags, so the lap counts are what make it work there).
    static int finalSession = int.MinValue, expiredLeaderLaps = -1;
    internal static bool IsFinalLap(RaceSnapshot s)
    {
        if (!(s.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase) || s.Phase != RacePhase.Broadcast || s.Flags.Checkered) return false;
        if (s.SessionNum != finalSession) { finalSession = s.SessionNum; expiredLeaderLaps = -1; }
        int leaderLaps = s.Leader?.LapsCompleted ?? -1;
        if (s.Flags.White) return true;
        if (s.TotalLaps > 0) return leaderLaps >= 0 && leaderLaps >= s.TotalLaps - 1;
        if (s.SessionLengthSeconds <= 0 || s.SessionTimeRemaining > 0.5) { expiredLeaderLaps = -1; return false; }
        if (expiredLeaderLaps < 0) expiredLeaderLaps = leaderLaps;            // the clock just ran out
        return leaderLaps > expiredLeaderLaps;                                 // the leader has taken the white
    }

    /// <summary>Test seam for tools/race_frame: a fixed wall clock so the bar's wall-clock animations (sun, cloth,
    /// checker, temp trend) draw the same pixels run to run. Null (always, in the app) = the real clock.</summary>
    public static Func<DateTime>? TestClock;
    static DateTime WallUtc => TestClock?.Invoke() ?? DateTime.UtcNow;
    static long WallTickMs => TestClock is { } tc ? (long)tc().TimeOfDay.TotalMilliseconds : Environment.TickCount64;

    public SessionBarRenderer()
    {
        barBasePaint = new SKPaint { IsAntialias = true, Color = BarBase };

        alcovePaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(0, 0), WatchAlcoveRadius,
                new[] { new SKColor(0, 0, 0, 82), new SKColor(0, 0, 0, 41), new SKColor(0, 0, 0, 0) },
                new[] { 0f, 0.45f, 1f },
                SKShaderTileMode.Clamp),
        };

        topGlossPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, BarHeight),
                new[] { new SKColor(255, 255, 255, 11), new SKColor(0, 0, 0, 0) },
                new[] { 0f, 0.25f },
                SKShaderTileMode.Clamp),
        };

        bottomDarkenPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, BarHeight),
                new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 56) },
                new[] { 0.75f, 1f },
                SKShaderTileMode.Clamp),
        };

        progressBasePaint = new SKPaint { IsAntialias = true, Color = ProgressBase };

        progressGoldPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0), new SKPoint(0, ProgressBarHeight),
                new[] { new SKColor(0xFF, 0xD8, 0x76), new SKColor(0xC9, 0xA9, 0x61), new SKColor(0xB8, 0x96, 0x4C) },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp),
        };
    }

    /// <summary>
    /// Draw the bar at <paramref name="originX"/>,<paramref name="originY"/>
    /// scaled by <paramref name="scale"/>, with the plate sized to
    /// <paramref name="barWidth"/> pixels. The watch alcove is centred at
    /// <c>(originX + barWidth*scale/2, originY + BarHeight*scale/2)</c>; the
    /// caller draws the watch on top after this returns. <paramref name="barWidth"/>
    /// defaults to <see cref="BarWidth"/> when not specified, so existing call
    /// sites that don't care about the battle-mode expansion don't need to
    /// pass it.
    /// </summary>
    public void Draw(
        SKCanvas canvas,
        float originX,
        float originY,
        float scale,
        RaceSnapshot snapshot,
        DateTime now,
        double elapsedMs,
        float? barWidth = null)
    {
        currentBarWidth = barWidth ?? BarWidth;
        canvas.Save();
        canvas.Translate(originX, originY);
        canvas.Scale(scale);

        // The legacy bar hides entirely in post-race (the starting-grid takes
        // over as Final Classification). Mirror that here.
        // 2026-10-28 (#486): no longer hidden after the flag - it stays in its battle look with the ticker (PresentationState.BarVisible)

        var flag = ResolveFlag(snapshot.Flags);
        TrackSessionMax(snapshot);
        lastElapsedMs = elapsedMs;
        if (flag.Label != lastFlagLabel)
        {
            if (lastFlagLabel.Length > 0 && flag.HasFlag) { flagChangeMs = elapsedMs; flagChangeCol = flag.Colour; }
            lastFlagLabel = flag.Label;
        }

        DrawBarBackground(canvas, flag, elapsedMs, snapshot);
        DrawProgressBar(canvas, snapshot, flag);
        DrawAccentHairlines(canvas);
        DrawWings(canvas, snapshot, elapsedMs);
        DrawLeftZone(canvas, snapshot);
        DrawRightZone(canvas, snapshot, flag);
        DrawYellowLightBox(canvas, snapshot, elapsedMs);

        canvas.Restore();
    }

    // ----- yellow flag light box (2026-10-25) -----
    // Angelo: "during a yellow flag the entire session bar - battle mode or not, everything except the watch face and
    // the ticker - turns into the texture of the light box and flashes yellow, then goes back to normal when the
    // yellow is cleared. On the left it says YELLOW FLAG, on the right the corner name." The bar becomes a marshal's
    // LED board: a grid of LEDs flashing yellow, the words lit as LEDs that stay on. The watch alcove is left open
    // (the watch is drawn on top), and the ticker is its own strip below.
    // 2026-10-25 (Angelo: "maybe just do regular type and make the sides alternate"): the two halves of the board flash
    // in turn, like a pair of marshal lights; the words are plain type - black on the lit half, yellow on the dark half -
    // so they read at every moment.
    float lbAlpha; double lbLastMs = -1, lbSinceMs = -1;
    string lbKey = "";
    SKPoint[] lbLeft = Array.Empty<SKPoint>(), lbRight = Array.Empty<SKPoint>();
    static readonly SKColor LbYellow = new(0xFF, 0xC1, 0x07);
    const float LbCell = 3.6f;   // LED pitch of the board texture

    void DrawYellowLightBox(SKCanvas c, RaceSnapshot snap, double elapsedMs)
    {
        double dt = lbLastMs < 0 ? 16 : Math.Clamp(elapsedMs - lbLastMs, 0, 100); lbLastMs = elapsedMs;
        bool racing = snap.IsConnected && snap.Phase == RacePhase.Broadcast;
        bool fcy = racing && snap.Flags.Caution;
        bool yel = racing && (snap.Flags.Yellow || YellowFlagCallout.YellowNow || fcy) && !snap.Flags.Red && !snap.Flags.Checkered;   // 2026-10-25: also Grandstand's own yellows (replays)
        if (yel && lbSinceMs < 0) lbSinceMs = elapsedMs;
        bool hold = lbSinceMs >= 0 && elapsedMs - lbSinceMs < 4000;      // at least a few seconds, even for a short yellow
        bool on = yel || hold;
        if (!on) lbSinceMs = -1;
        lbAlpha = Math.Clamp(lbAlpha + (float)(dt / 220.0) * (on ? 1 : -1), 0f, 1f);
        if (lbAlpha <= 0.01f) return;
        float a = lbAlpha * lbAlpha * (3 - 2 * lbAlpha);
        float w = currentBarWidth, h = BarHeight;
        string left = fcy ? "FULL COURSE YELLOW" : "YELLOW FLAG";
        string right = fcy ? "" : YellowFlagCallout.CurrentPlace;
        string key = $"{w:0}|{h:0}";
        if (key != lbKey) { lbKey = key; BuildLightBox(w, h); }

        var bar = new SKRect(0, 0, w, h);
        using var clip = new SKPath();
        clip.AddRoundRect(bar, 6, 6);
        using (var hole = new SKPath()) { hole.AddCircle(w / 2f, h / 2f, WatchAlcoveRadius - 10f); using var cut = clip.Op(hole, SKPathOp.Difference); clip.Reset(); clip.AddPath(cut); }
        c.Save();
        c.ClipPath(clip, antialias: true);
        bool leftLit = ((int)(elapsedMs / 450) & 1) == 0;                   // the halves swap about twice a second
        using (var bg = new SKPaint { Color = new SKColor(6, 6, 6, (byte)(245 * a)) }) c.DrawRect(bar, bg);
        using (var dot = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round })
            foreach (var (pts, lit) in new[] { (lbLeft, leftLit), (lbRight, !leftLit) })
            {
                dot.StrokeWidth = LbCell * 0.7f;
                dot.Color = lit ? LbYellow.WithAlpha((byte)(255 * a)) : new SKColor(0x2A, 0x20, 0x04, (byte)(255 * a));
                c.DrawPoints(SKPointMode.Points, pts, dot);
                if (lit) { dot.StrokeWidth = LbCell * 1.1f; dot.Color = LbYellow.WithAlpha((byte)(70 * a)); c.DrawPoints(SKPointMode.Points, pts, dot); }
            }
        // the words, plain type, each centred in its half clear of the watch
        using var tp = new SKPaint { IsAntialias = true, Typeface = Fonts.BarlowCondensed.Black, TextSize = h * 0.6f };
        float sp = tp.TextSize * 0.06f;
        float Tw(string t) => tp.MeasureText(t) + sp * Math.Max(0, t.Length - 1);
        float zl0 = 18f, zl1 = w / 2f - WatchAlcoveRadius - 18f, zr0 = w / 2f + WatchAlcoveRadius + 18f, zr1 = w - 18f;
        void Put(string t, float z0, float z1, bool lit)
        {
            if (t.Length == 0) return;
            tp.TextSize = h * 0.6f; while (Tw(t) > z1 - z0 && tp.TextSize > 12) tp.TextSize -= 1;
            sp = tp.TextSize * 0.06f;
            tp.Color = lit ? new SKColor(0x08, 0x08, 0x08, (byte)(255 * a)) : LbYellow.WithAlpha((byte)(255 * a));
            TextTracked.Draw(c, tp, t, (z0 + z1) / 2f - Tw(t) / 2f, h / 2f + tp.TextSize * 0.36f, sp);
        }
        Put(left, zl0, zl1, leftLit);
        Put(right, zr0, zr1, !leftLit);
        c.Restore();
    }

    void BuildLightBox(float w, float h)
    {
        int cols = (int)(w / LbCell), rows = (int)(h / LbCell);
        float ox = (w - cols * LbCell) / 2f + LbCell / 2f, oy = (h - rows * LbCell) / 2f + LbCell / 2f;
        var l = new List<SKPoint>(cols * rows / 2); var r = new List<SKPoint>(cols * rows / 2);
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                var pt = new SKPoint(ox + col * LbCell, oy + row * LbCell);
                (pt.X < w / 2f ? l : r).Add(pt);
            }
        lbLeft = l.ToArray(); lbRight = r.ToArray();
    }

    static byte Marshal_Alpha(SKBitmap bmp, int x, int y) =>
        System.Runtime.InteropServices.Marshal.ReadByte(bmp.GetPixels() + y * bmp.RowBytes + x);

    // ----- bar background -----

    // 2026-10-23 redesign (Angelo: "reflect the rest of the updated design language"): the radio /
    // race-control card treatment - deep glass, a warm gold bloom, the host country's colours as a soft
    // angled wash behind the left zone, and the flag state as an edge spine + glow instead of a whole-bar tint.
    void DrawBarBackground(SKCanvas c, BarFlag flag, double elapsedMs, RaceSnapshot snap)
    {
        var rect = new SKRect(0, 0, currentBarWidth, BarHeight);
        c.DrawRect(rect, barBasePaint);

        DrawCountryWash(c, snap);

        // warm gold bloom from the top-left corner (the team-radio card's signature)
        shaderPaint.Shader = bloomLeftShader.Radial(new SKPoint(30f, 10f), 460f,
            [new SKColor(0xC5, 0xA5, 0x72, 44), new SKColor(0xC5, 0xA5, 0x72, 12), new SKColor(0xC5, 0xA5, 0x72, 0)], Stops.At045);
        c.DrawRect(rect, shaderPaint);

        c.DrawRect(rect, topGlossPaint);
        c.DrawRect(rect, bottomDarkenPaint);
        Theme.Skin(c, rect, ThemeSurface.Bar, elapsedMs);   // #494: the broadcast theme (surfaces only - nothing on Default)

        // Watch alcove darkening behind the centrepiece.
        c.Save();
        c.Translate(currentBarWidth / 2f, BarHeight / 2f);
        c.DrawCircle(0, 0, WatchAlcoveRadius, alcovePaint);
        c.Restore();

        if (flag.HasFlag)
        {
            float pulse = (float)(0.5 + 0.5 * Math.Sin(elapsedMs / 1000.0 * Math.PI * 2.0 / flag.PulseSeconds));
            float pa = 0.70f + 0.30f * pulse;
            // glow spreading in from the left edge
            shaderPaint.Shader = flagGlowShader.Linear(new SKPoint(0, 0), new SKPoint(300f, 0),
                [flag.Colour.WithAlpha((byte)(95 * pa)), flag.Colour.WithAlpha((byte)(22 * pa)), flag.Colour.WithAlpha(0)], StopsGlow);
            c.DrawRect(new SKRect(0, 0, 300f, BarHeight), shaderPaint);
            // the spine (same as the class-colour spine on the radio card) - mirrored on the right edge
            fillPaint.Color = flag.Colour.WithAlpha((byte)(255 * pa));
            c.DrawRect(new SKRect(0, 0, 5f, BarHeight), fillPaint);
            c.DrawRect(new SKRect(currentBarWidth - 5f, 0, currentBarWidth, BarHeight), fillPaint);
            // a flag-colour line along the bottom edge, strongest at the ends
            shaderPaint.Shader = flagLineShader.Linear(new SKPoint(0, 0), new SKPoint(currentBarWidth, 0),
                [flag.Colour.WithAlpha((byte)(230 * pa)), flag.Colour.WithAlpha((byte)(60 * pa)),
                 flag.Colour.WithAlpha((byte)(60 * pa)), flag.Colour.WithAlpha((byte)(230 * pa))], StopsFlagLine);
            c.DrawRect(new SKRect(0, BarHeight - 2f, currentBarWidth, BarHeight), shaderPaint);
        }
        else DrawSectorStrip(c, snap);

        // (1) flag change: one soft slanted wipe of the new flag's colour across the bar
        double since = elapsedMs - flagChangeMs;
        if (since >= 0 && since < 1300 && !Perf.Light)
        {
            float k = (float)(since / 1300.0);
            float ease = 1 - (1 - k) * (1 - k);
            float band = 300f, W = currentBarWidth;
            float cx = -band + (W + band * 2) * ease;
            using (var flash = new SKPaint { Color = flagChangeCol.WithAlpha((byte)(46 * (1 - k))) }) c.DrawRect(rect, flash);
            c.Save(); c.ClipRect(rect);
            using var path = new SKPath();
            float sl = 0.42f * BarHeight;
            path.MoveTo(cx - band / 2f + sl, 0); path.LineTo(cx + band / 2f + sl, 0); path.LineTo(cx + band / 2f, BarHeight); path.LineTo(cx - band / 2f, BarHeight); path.Close();
            using var wSh = SKShader.CreateLinearGradient(new SKPoint(cx - band / 2f, 0), new SKPoint(cx + band / 2f + sl, 0),
                new[] { flagChangeCol.WithAlpha(0), flagChangeCol.WithAlpha((byte)(150 * (1 - k * 0.5f))), flagChangeCol.WithAlpha(0) },
                new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            using var wp = new SKPaint { Shader = wSh, IsAntialias = true };
            c.DrawPath(path, wp);
            c.Restore();
        }
    }

    // (3) practice / qualifying: the featured car's sectors along the bottom edge as they're done this lap,
    // purple / green / yellow like the tower's sector bars (dim = not done yet)
    void DrawSectorStrip(SKCanvas c, RaceSnapshot snap)
    {
        if ((snap.SessionTypeName ?? "").Contains("race", StringComparison.OrdinalIgnoreCase)) return;
        int idx = snap.CameraCarIdx >= 0 ? snap.CameraCarIdx : snap.PlayerCarIdx;
        if (idx < 0) return;
        var marks = SectorTimer.MarksFor(idx);
        var starts = SectorTimer.Starts();
        if (marks == null || starts.Length < 2) return;
        float W = currentBarWidth, y0 = BarHeight - 3f;
        while (sectorShaders.Count < starts.Length) sectorShaders.Add(new ShaderSlot());
        for (int i = 0; i < starts.Length; i++)
        {
            float a = starts[i] * W + (i == 0 ? 0 : 2f), b = (i + 1 < starts.Length ? starts[i + 1] : 1f) * W - (i + 1 < starts.Length ? 2f : 0);
            var col = marks[i] switch
            {
                SectorTimer.Mark.ClassBest => SecPurple,
                SectorTimer.Mark.PersonalBest => SecGreen,
                SectorTimer.Mark.Slower => SecYellow,
                _ => new SKColor(255, 255, 255, 34),
            };
            fillPaint.Color = marks[i] == SectorTimer.Mark.None ? col : col.WithAlpha(235);
            c.DrawRect(new SKRect(a, y0, b, BarHeight), fillPaint);
            if (marks[i] != SectorTimer.Mark.None)
            {
                shaderPaint.Shader = sectorShaders[i].Linear(new SKPoint(0, BarHeight), new SKPoint(0, BarHeight - 14f),
                    [col.WithAlpha(70), col.WithAlpha(0)], StopsEnds);
                c.DrawRect(new SKRect(a, BarHeight - 14f, b, y0), shaderPaint);
            }
        }
    }

    // ---- host country colours (Angelo 2026-10-23: "the colors of the country being raced in ... an angled
    // background hue, not something crazy"). The track's flag, stretched, slanted and blurred into a soft
    // wash that fades out toward the watch. Built once per country, then it's a single cached image draw.
    readonly Dictionary<string, SKImage?> washCache = new(StringComparer.OrdinalIgnoreCase);
    const int WashW = 560;

    void DrawCountryWash(SKCanvas c, RaceSnapshot snap)
    {
        var code = snap.TrackCountryCode;
        if (string.IsNullOrEmpty(code)) return;
        if (!washCache.TryGetValue(code, out var img))
        {
            img = null;
            var flagBmp = LoadFlag(code);
            if (flagBmp != null)
            {
                try
                {
                    int h = (int)BarHeight;
                    using var surf = SKSurface.Create(new SKImageInfo(WashW, h, SKColorType.Rgba8888, SKAlphaType.Premul));
                    var cv = surf.Canvas;
                    cv.Clear(SKColors.Transparent);
                    using (var fp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                               ImageFilter = SKImageFilter.CreateBlur(7f, 7f) })
                    {
                        cv.Save();
                        cv.Skew(-0.42f, 0f);                       // vertical stripes lean like the cards' angles
                        cv.DrawBitmap(flagBmp, new SKRect(-30f, -12f, WashW + 0.42f * h + 30f, h + 12f), fp);
                        cv.Restore();
                    }
                    // fade toward the watch, with a slanted end
                    using (var mSh = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(WashW, 0),
                               new[] { SKColors.White, SKColors.White.WithAlpha(200), SKColors.White.WithAlpha(0) },
                               new[] { 0f, 0.45f, 1f }, SKShaderTileMode.Clamp))
                    using (var mp = new SKPaint { Shader = mSh, BlendMode = SKBlendMode.DstIn })
                        cv.DrawRect(new SKRect(0, 0, WashW, h), mp);
                    using (var path = new SKPath())
                    using (var clr = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true })
                    {
                        path.MoveTo(WashW - 70f, 0); path.LineTo(WashW, 0); path.LineTo(WashW, h); path.LineTo(WashW - 70f - 0.42f * h, h); path.Close();
                        cv.DrawPath(path, clr);
                    }
                    img = surf.Snapshot();
                }
                catch { img = null; }
            }
            washCache[code] = img;
        }
        if (img == null) return;
        float inset = Math.Max(0f, (currentBarWidth - BarWidth) / 2f);   // follows the left zone outward
        c.DrawImage(img, new SKRect(0, 0, WashW + inset, BarHeight), washPaint);
        // keep the text readable over bright flags (white bands)
        c.DrawRect(new SKRect(0, 0, WashW + inset, BarHeight), scrimPaint);
    }

    void DrawAccentHairlines(SKCanvas c)
    {
        var AccentGold = Theme.AccentOr(SessionBarRenderer.AccentGold, ThemeSurface.Bar);   // #494: the theme's accent (gold on Default)
        // Top + bottom 1 px gold hairlines fading at the edges. Built inline
        // because the gradient endpoints follow the dynamic bar width.
        aaShaderPaint.Shader = hairTopShader.Linear(new SKPoint(0, 0), new SKPoint(currentBarWidth, 0),
            [new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 0), new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 200),
             new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 200), new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 0)], StopsHair);
        c.DrawRect(new SKRect(0, 0, currentBarWidth, 1), aaShaderPaint);

        aaShaderPaint.Shader = hairBottomShader.Linear(new SKPoint(0, 0), new SKPoint(currentBarWidth, 0),
            [new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 0), new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 110),
             new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 110), new SKColor(AccentGold.Red, AccentGold.Green, AccentGold.Blue, 0)], StopsHair);
        c.DrawRect(new SKRect(0, BarHeight - 1, currentBarWidth, BarHeight), aaShaderPaint);
    }

    // ----- progress bar -----

    // 2026-10-23 redesign: a slim glowing gold line along the top edge with a bright head, quarter
    // marks, and the scrolling checker (slim too) at the flag.
    const float LineH = 4f;
    void DrawProgressBar(SKCanvas c, RaceSnapshot snap, BarFlag flag)
    {
        float W = currentBarWidth;
        fillPaint.Color = new SKColor(0, 0, 0, 150);
        c.DrawRect(new SKRect(0, 0, W, LineH), fillPaint);
        float progress = ComputeProgress(snap);
        if (progress > 0 && !flag.IsCheckered)
        {
            float x = W * progress;
            // soft glow under the line
            shaderPaint.Shader = progGlowShader.Linear(new SKPoint(0, LineH), new SKPoint(0, LineH + 12f),
                [new SKColor(0xFF, 0xD8, 0x76, 70), new SKColor(0xFF, 0xD8, 0x76, 0)], StopsEnds);
            c.DrawRect(new SKRect(0, LineH, x, LineH + 12f), shaderPaint);
            shaderPaint.Shader = progFillShader.Linear(new SKPoint(0, 0), new SKPoint(x, 0),
                [new SKColor(0xB8, 0x96, 0x4C), new SKColor(0xFF, 0xD8, 0x76)], StopsEnds);
            c.DrawRect(new SKRect(0, 0, x, LineH), shaderPaint);
            // bright head
            shaderPaint.Shader = progHeadShader.Radial(new SKPoint(x, LineH / 2f), 14f,
                [new SKColor(255, 240, 200, 210), new SKColor(255, 216, 118, 60), new SKColor(255, 216, 118, 0)], Stops.At035);
            c.DrawCircle(x, LineH / 2f, 14f, shaderPaint);
        }
        fillPaint.Color = new SKColor(255, 255, 255, 70);
        for (int q = 1; q <= 3; q++) { float tx = W * q / 4f; c.DrawRect(new SKRect(tx - 0.5f, 0, tx + 0.5f, LineH + 4f), fillPaint); }
        if (flag.IsCheckered)
        {
            var paint = fillPaint;
            float stripe = 8f;
            float offset = (float)((WallUtc.TimeOfDay.TotalSeconds * stripe / 1.4) % (stripe * 2));
            for (float x = -stripe * 2 - offset; x < W; x += stripe * 2)
            {
                paint.Color = SKColors.White; c.DrawRect(new SKRect(x, 0, x + stripe, LineH + 2f), paint);
                paint.Color = SKColors.Black; c.DrawRect(new SKRect(x + stripe, 0, x + stripe * 2, LineH + 2f), paint);
            }
        }
    }

    float ComputeProgress(RaceSnapshot snap) => (float)RaceProgress.Of(snap, maxSessionSecondsSeen);

    int maxSeenSessionNum = int.MinValue;
    void TrackSessionMax(RaceSnapshot snap)
    {
        // Fallback only (the official length from session info is preferred): reset on
        // every new session and ignore iRacing's week-long "unlimited" placeholder, which
        // used to pin the bar near empty for a whole race (2026-10-19).
        if (snap.SessionNum != maxSeenSessionNum) { maxSeenSessionNum = snap.SessionNum; maxSessionSecondsSeen = 0; }
        if (snap.SessionTimeRemaining > maxSessionSecondsSeen && snap.SessionTimeRemaining < 86400 * 2)
            maxSessionSecondsSeen = snap.SessionTimeRemaining;
    }

    // ----- wings (Angelo 2026-10-23: the stretched bar "looks unfinished the way it spreads stuff out") -----
    // When the bar stretches for a battle, the core stays snug around the watch and the extra width becomes
    // two wings: the event on the left (series logo + name, the host-country wash running to the edge), live
    // race info on the right (fastest lap of the race, alternating with the class leader). Each wing has its
    // own gold divider and fades in as the bar opens, like a curtain.
    SKBitmap? seriesLogo; int? seriesLogoFor = int.MinValue;
    SKBitmap? irWordmark; bool irTried;
    double wingSwapAt; int wingPage;
    static readonly SKColor FastPurple = new(168, 85, 247);

    void DrawWings(SKCanvas c, RaceSnapshot snap, double elapsedMs)
    {
        float wing = (currentBarWidth - BarWidth) / 2f;
        if (wing < 80f) return;
        float a = Math.Clamp((wing - 80f) / 220f, 0f, 1f);
        byte A(float v) => (byte)Math.Clamp(v * a, 0, 255);

        // gold dividers where the wings meet the core
        shaderPaint.Shader = wingDividerShader.Linear(new SKPoint(0, 10f), new SKPoint(0, BarHeight - 8f),
            [new SKColor(0xC5, 0xA5, 0x72, 0), new SKColor(0xC5, 0xA5, 0x72, A(150)), new SKColor(0xC5, 0xA5, 0x72, 0)], StopsLight);
        for (int k = 0; k < 2; k++)
        {
            float x = k == 0 ? wing : currentBarWidth - wing;
            c.DrawRect(new SKRect(x - 0.5f, 10f, x + 0.5f, BarHeight - 8f), shaderPaint);
        }

        // ---- left wing: the event ----
        if (seriesLogoFor != snap.SeriesId)
        {
            seriesLogoFor = snap.SeriesId; seriesLogo = null;
            try
            {
                var slug = SeriesNames.ResolveLogoSlug(snap.SeriesId);
                if (slug != null) { var path = Assets.Resolve("logos", "series", slug + ".png"); if (System.IO.File.Exists(path)) seriesLogo = Assets.Decode(path); }
            }
            catch { seriesLogo = null; }
        }
        if (seriesLogo == null && !irTried)
        {
            irTried = true;
            try { var path = Assets.Resolve("logos", "iracing-brand", "iRacing-Inline-Color-White.png"); if (System.IO.File.Exists(path)) irWordmark = Assets.Decode(path); }
            catch { irWordmark = null; }
        }
        var logo = seriesLogo ?? irWordmark;
        if (seriesNameFor != snap.SeriesId) { seriesNameFor = snap.SeriesId; seriesNameText = (SeriesNames.ResolveName(snap.SeriesId) ?? "").ToUpperInvariant(); }
        string seriesName = seriesNameText;
        float lcx = wing / 2f;
        {
            var lab = wingNamePaint;
            lab.TextSize = 13f; lab.Color = new SKColor(0xFF, 0xD8, 0x76, A(235));
            float blockW = 0f, lh = 30f, lw = 0f;
            if (logo != null) { lw = logo.Width * lh / logo.Height; if (lw > wing - 60f) { lw = wing - 60f; lh = lw * logo.Height / logo.Width; } blockW = lw; }
            if (logo != null)
            {
                byte la = A(240);
                wingLogoPaint.ColorFilter = wingLogoFilters[la] ??= SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, la), SKBlendMode.DstIn);
                BitmapImages.Draw(c, logo, SKRect.Create(lcx - lw / 2f, 26f - lh / 2f + 8f, lw, lh), wingLogoPaint);
            }
            if (seriesName.Length > 0)
            {
                float tw = MeasureTracked(lab, seriesName, 2.0f);
                if (tw > wing - 40f) { lab.TextSize = Math.Max(10f, 13f * (wing - 40f) / tw); tw = MeasureTracked(lab, seriesName, 2.0f); }
                DrawTextTracked(c, lab, seriesName, lcx - tw / 2f, 76f, 2.0f);
            }
        }

        // ---- right wing: fastest lap of the race <-> class leader, every 15 s ----
        if (elapsedMs - wingSwapAt > 15000 || wingSwapAt == 0) { wingSwapAt = elapsedMs; wingPage ^= 1; }
        double inT = elapsedMs - wingSwapAt;
        float pa = (float)Math.Clamp(Math.Min(inT / 450.0, (15000 - inT) / 450.0), 0, 1);
        float rx0 = currentBarWidth - wing + 26f, rx1 = currentBarWidth - 26f;
        Driver? fast = null; Driver? lead = null; string cls = "";
        var board = snap.ClassLeaderboard;
        for (int ci = 0; ci < board.Count; ci++)
        {
            var drivers = board[ci].Drivers;
            for (int di = 0; di < drivers.Count; di++)
            {
                var d = drivers[di];
                if (d.IsPaceCar || d.IsSpectator) continue;
                if (d.BestLapTime > 0 && (fast == null || d.BestLapTime < fast.BestLapTime)) fast = d;
            }
        }
        var me = snap.CameraTarget ?? snap.Player;
        if (me != null)
            for (int ci = 0; ci < board.Count; ci++)
            {
                var dc = board[ci];
                if (!string.Equals(dc.Name, me.ClassName, StringComparison.OrdinalIgnoreCase)) continue;
                lead = null;
                for (int di = 0; di < dc.Drivers.Count; di++) if (dc.Drivers[di].PositionInClass == 1) { lead = dc.Drivers[di]; break; }
                cls = dc.Name;
            }
        int page = wingPage;
        if (page == 0 && fast == null) page = 1;
        if (page == 1 && lead == null) page = 0;
        Driver? who = page == 0 ? fast : lead;
        if (who == null) return;
        byte P(float v) => (byte)Math.Clamp(v * a * pa, 0, 255);

        if (page == 1 && !ReferenceEquals(cls, leaderClassIn)) { leaderClassIn = cls; leaderLabel = (ClassLogo.DisplayName(cls) + " LEADER").Trim().ToUpperInvariant(); }
        string label = page == 0 ? "FASTEST LAP" : leaderLabel;
        SKColor lc = page == 0 ? FastPurple : PhaseGold;
        float wx0 = currentBarWidth - wing, wcx = wx0 + wing / 2f;

        // a slanted wash in the page colour, mirroring the country colours on the left
        {
            var path = wingWashPath;
            path.Reset();
            float sl = 0.42f * BarHeight;
            path.MoveTo(wx0 + 40f + sl, 0); path.LineTo(currentBarWidth, 0); path.LineTo(currentBarWidth, BarHeight); path.LineTo(wx0 + 40f, BarHeight); path.Close();
            aaShaderPaint.Shader = wingWashShader.Linear(new SKPoint(wx0 + 40f, 0), new SKPoint(currentBarWidth, 0),
                [lc.WithAlpha(0), lc.WithAlpha(P(34)), lc.WithAlpha(P(58))], Stops.At055);
            c.DrawPath(path, aaShaderPaint);
        }

        string right;
        if (page == 0)
        {
            double bl = who.BestLapTime;
            if (!bl.Equals(fastLapIn)) { fastLapIn = bl; fastLapText = LapTimeFormat.Lap(who.BestLapTime); }
            right = fastLapText;
        }
        else if (me != null && me.CarIdx != who.CarIdx && me.GapToLeaderSeconds is double g && g > 0)
        {
            if (!g.Equals(gapIn)) { gapIn = g; gapText = "+" + g.ToString("0.0", CultureInfo.InvariantCulture); }
            right = gapText;
        }
        else right = "";
        var nameKey = (who.CarNumber, who.Name, who.AbbrevName);
        if (!wingNames.TryGetValue(nameKey, out var nm))
        {
            if (wingNames.Count > 512) wingNames.Clear();
            string num = string.IsNullOrEmpty(who.CarNumber) ? "" : "#" + who.CarNumber + "  ";
            wingNames[nameKey] = nm = num + NameFormat.Broadcast(who).ToUpperInvariant();
        }
        var wlp = wingNamePaint; wlp.TextSize = 13f; wlp.Color = lc.WithAlpha(P(240));
        var tp = wingTimePaint; tp.Color = (page == 0 ? FastPurple : TextIvory).WithAlpha(P(250));
        var np = wingDriverPaint; np.TextSize = 22f; np.Color = TextIvory.WithAlpha(P(252));
        float rw = right.Length > 0 ? MeasureTracked(tp, right, 0.6f) : 0f;
        float maxRow = wing - 60f;
        float nw = TextBlobs.Measure(nm, np);
        if (nw + (rw > 0 ? rw + 18f : 0) > maxRow) { np.TextSize = Math.Max(13f, 22f * (maxRow - (rw > 0 ? rw + 18f : 0)) / nw); nw = TextBlobs.Measure(nm, np); }
        float rowW = nw + (rw > 0 ? rw + 18f : 0);
        float labW = MeasureTracked(wlp, label, 2.2f) + (page == 0 ? 14f : 0f);
        float rowX = wcx - rowW / 2f, lx = wcx - labW / 2f;
        if (page == 0) { aaFillPaint.Color = FastPurple.WithAlpha(P(255)); c.DrawCircle(lx + 4f, 32f, 4f, aaFillPaint); }
        DrawTextTracked(c, wlp, label, lx + (page == 0 ? 14f : 0f), 36f, 2.2f);
        TextBlobs.Draw(c, nm, rowX, 80f, np);
        if (rw > 0) DrawTextTracked(c, tp, right, rowX + nw + 18f, 80f, 0.6f);
    }

    // ----- left zone (flag / track / temps / weather) -----

    const float LeftPad = 24f;
    const float LeftTop = 24f;
    const float LeftBottom = 8f;

    void DrawLeftZone(SKCanvas c, RaceSnapshot snap)
    {
        // Anchor contents to a centered region of the DEFAULT bar width so they
        // stay put when the plate expands to full-width during a battle. The
        // plate background still grows; only the text/icons stay anchored.
        // Full width (Angelo 2026-10-19): the zones move OUT toward the screen edges (78 % of the way)
        float contentInset = Math.Max(0f, (currentBarWidth - BarWidth) / 2f);

        // Row 1: small country flag + track name (uppercase).
        float row1Y = LeftTop;
        float flagH = 18f;
        float cursorX = LeftPad + contentInset;

        var flagBitmap = LoadFlag(snap.TrackCountryCode);
        if (flagBitmap != null)
        {
            float ratio = (float)flagBitmap.Width / flagBitmap.Height;
            float flagW = flagH * ratio;
            var destRect = new SKRect(cursorX, row1Y, cursorX + flagW, row1Y + flagH);
            BitmapImages.Draw(c, flagBitmap, destRect, flagImgPaint);
            // Hairline border (matches legacy 1px gold-tinted border).
            c.DrawRect(destRect, flagBorderPaint);
            cursorX += flagW + 8f;
        }

        // Track name (uppercase, 17px, letter-spacing ~1px).
        var track = string.IsNullOrEmpty(snap.TrackBroadcastName)
            ? snap.TrackDisplayName
            : snap.TrackBroadcastName;
        if (!string.IsNullOrEmpty(track))
        {
            // 2026-10-23 redesign: the same Cormorant serif as the driver names on the cards
            var paint = trackPaint;
            paint.TextSize = 23f;
            if (!ReferenceEquals(track, trackIn)) { trackIn = track; trackUpper = track.ToUpperInvariant(); }
            float maxW = BarWidth / 2f - WatchAlcoveRadius - (cursorX - contentInset) - 12f;
            float tw = MeasureTracked(paint, trackUpper, 0.8f);
            if (tw > maxW && maxW > 60f) paint.TextSize = Math.Max(15f, 23f * maxW / tw);
            paint.GetFontMetrics(out var metrics);
            float baseline = row1Y + flagH * 0.5f - (metrics.Ascent + metrics.Descent) / 2f;
            DrawTextTracked(c, paint, trackUpper, cursorX, baseline, 0.8f);
        }

        // Row 2: AIR · TRACK temps, then weather icon.
        float row2Y = BarHeight - LeftBottom - 18f;
        float row2BaselineMono = row2Y + 14f;
        float tx = LeftPad + contentInset;

        bool fahrenheit = UnitPrefs.Fahrenheit(snap);
        string unit = fahrenheit ? "°F" : "°C";
        // values arrive in Celsius - convert when showing Fahrenheit
        double air = fahrenheit ? snap.AirTempC * 9.0 / 5.0 + 32 : snap.AirTempC;
        double trk = fahrenheit ? snap.TrackTempC * 9.0 / 5.0 + 32 : snap.TrackTempC;
        double airR = Math.Round(air), trkR = Math.Round(trk);
        if (!airR.Equals(airKey) || airF != fahrenheit) { airKey = airR; airF = fahrenheit; airText = $"{airR:0}{unit}"; }
        if (!trkR.Equals(trkKey) || trkF != fahrenheit) { trkKey = trkR; trkF = fahrenheit; trkText = $"{trkR:0}{unit}"; }

        // (2) temps in small glass chips (like the cards' voice meter), with a trend arrow on TRACK
        if (snap.SessionNum != trendSession) { trendSession = snap.SessionNum; trackTempHist.Clear(); }
        var nowUtc = WallUtc;
        if (trackTempHist.Count == 0 || (nowUtc - trackTempHist[^1].At).TotalSeconds >= 10) trackTempHist.Add((nowUtc, snap.TrackTempC));
        while (trackTempHist.Count > 0 && (nowUtc - trackTempHist[0].At).TotalMinutes > 6) trackTempHist.RemoveAt(0);
        int trend = 0;
        if (trackTempHist.Count > 1 && (nowUtc - trackTempHist[0].At).TotalMinutes >= 3)
        {
            double d = snap.TrackTempC - trackTempHist[0].C;
            if (d >= 0.8) trend = 1; else if (d <= -0.8) trend = -1;
        }
        TempChip(c, "AIR", airText, false, trend, ref tx, row2BaselineMono);
        tx += 8f;
        TempChip(c, "TRACK", trkText, true, trend, ref tx, row2BaselineMono);

        // Weather: animated sun (clear) or rain (raining), just right of the chips.
        float wx = tx + 10f;
        float wy = row2Y - 4f;
        if (snap.IsRaining) DrawRainIcon(c, wx + 13f, wy + 13f);
        else                 DrawSunIcon(c, wx + 13f, wy + 13f);

        // (4) local time at the circuit (sim time of day) - hidden while the weather partner tag shows
        double sinceW = (WallUtc - RaceDiary.WeatherChangedAt).TotalSeconds;
        bool partner = sinceW >= 0 && sinceW < 30;
        if (!partner && snap.SessionTimeOfDay > 0)
        {
            int mins = (int)(snap.SessionTimeOfDay / 60) % (24 * 60);
            if (mins != localMins) { localMins = mins; localText = $"{mins / 60:00}:{mins % 60:00}"; }
            string lt = localText;
            float lx = wx + 36f;
            float limit = BarWidth / 2f - WatchAlcoveRadius - 6f + contentInset;
            var lab = smallLabelPaint; var val = smallValuePaint;
            float need = MeasureTracked(lab, "LOCAL", 1.2f) + 6f + MeasureTracked(val, lt, 1.0f);
            if (lx + need <= limit)
            {
                DrawTextTracked(c, lab, "LOCAL", lx, row2BaselineMono - 1f, 1.2f);
                DrawTextTracked(c, val, lt, lx + MeasureTracked(lab, "LOCAL", 1.2f) + 6f, row2BaselineMono, 1.0f);
            }
        }

        // Weather partner tag for 30 s after the conditions change (2026-10-19)
        double since = (WallUtc - RaceDiary.WeatherChangedAt).TotalSeconds;
        if (since >= 0 && since < 30)
        {
            float wa = (float)Math.Min(Math.Min(since / 0.6, 1.0), Math.Min((30 - since) / 1.0, 1.0));
            Presenters.DrawMark(c, Presenters.WeatherBar, wx + 40f, wy + 13f, 22f, wa);
        }
    }

    // (2) a temperature in a small glass chip (like the cards' voice meter), with the trend arrow on TRACK
    void TempChip(SKCanvas c, string label, string value, bool arrow, int trend, ref float tx, float row2BaselineMono)
    {
        var labelPaint = smallLabelPaint; var valuePaint = smallValuePaint;
        float lw = MeasureTracked(labelPaint, label, 1.2f), vw = MeasureTracked(valuePaint, value, 1.0f);
        float w = 8f + lw + 7f + vw + (arrow && trend != 0 ? 13f : 0f) + 8f;
        var r = new SKRect(tx, row2BaselineMono - 15f, tx + w, row2BaselineMono + 6f);
        c.DrawRoundRect(r, 3f, 3f, chipFillPaint);
        c.DrawRoundRect(r, 3f, 3f, chipLinePaint);
        float x = tx + 8f;
        DrawTextTracked(c, labelPaint, label, x, row2BaselineMono - 1f, 1.2f); x += lw + 7f;
        DrawTextTracked(c, valuePaint, value, x, row2BaselineMono, 1.0f); x += vw;
        if (arrow && trend != 0)
        {
            float ax = x + 7f, ay = row2BaselineMono - 5f;
            aaFillPaint.Color = trend > 0 ? Amber : new SKColor(110, 170, 255);
            var tri = trendPath;
            tri.Reset();
            if (trend > 0) { tri.MoveTo(ax - 4f, ay + 3f); tri.LineTo(ax + 4f, ay + 3f); tri.LineTo(ax, ay - 4f); }
            else { tri.MoveTo(ax - 4f, ay - 3f); tri.LineTo(ax + 4f, ay - 3f); tri.LineTo(ax, ay + 4f); }
            tri.Close(); c.DrawPath(tri, aaFillPaint);
        }
        tx += w;
    }

    void DrawSunIcon(SKCanvas c, float cx, float cy)
    {
        double t = WallUtc.TimeOfDay.TotalSeconds;
        float rotateDeg = (float)((t * 360.0 / 28.0) % 360.0);
        float corePulse = 1f + 0.08f * (float)Math.Sin(t * Math.PI * 2.0 / 2.4);
        float glowPulse = 0.55f + 0.30f * (float)(0.5 + 0.5 * Math.Sin(t * Math.PI * 2.0 / 3.2));

        aaFillPaint.Color = new SKColor(255, 216, 118, (byte)(120 * glowPulse));
        c.DrawCircle(cx, cy, 9, aaFillPaint);

        c.Save();
        c.Translate(cx, cy);
        c.RotateDegrees(rotateDeg);
        var rayPaint = sunRayPaint;
        for (int i = 0; i < 12; i++)
        {
            c.Save();
            c.RotateDegrees(i * 30f);
            c.DrawLine(0, -13, 0, -9.5f, rayPaint);
            c.Restore();
        }
        c.Restore();

        c.Save();
        c.Translate(cx, cy);
        c.Scale(corePulse);
        c.DrawCircle(0, 0, 5.5f, sunCorePaint);
        c.DrawCircle(0, 0, 5.5f, sunCoreStroke);
        c.Restore();
    }

    void DrawRainIcon(SKCanvas c, float cx, float cy)
    {
        // Cloud (oval) + three falling drops with the legacy rainFall loop.
        var cloudPaint = aaFillPaint;
        cloudPaint.Color = new SKColor(220, 230, 240, 220);
        c.DrawOval(new SKRect(cx - 12, cy - 8, cx + 12, cy + 2), cloudPaint);

        var dropPaint = aaFillPaint;

        double t = WallUtc.TimeOfDay.TotalSeconds;
        for (int i = 0; i < 3; i++)
        {
            double phase = (t / 1.4 + i * 0.32) % 1.0;
            float ty = (float)(-3 + 7 * phase); // -3..+4
            byte alpha = (byte)(255 * (0.4 + 0.6 * Math.Sin(phase * Math.PI)));
            dropPaint.Color = new SKColor(120, 170, 255, alpha);
            float dropX = cx - 8 + i * 8;
            c.DrawCircle(dropX, cy + 6 + ty, 1.6f, dropPaint);
        }
    }

    // ----- right zone (phase chip / time / lap / flag state) -----

    const float RightPad = 24f;
    const float RightTop = 20f;
    const float RightBottom = 6f;

    // 2026-10-23 right-side redesign (Angelo: "how will you redo the right side"): the cards' language -
    // the phase as a gold tracked header with a hairline rule and a 5-segment race meter, a bigger timer,
    // LAP and FLAG as glass chips like AIR / TRACK, and the group kept together in the full-width layout.
    const float RightZoneW = 384f;
    void DrawRightZone(SKCanvas c, RaceSnapshot snap, BarFlag flag)
    {
        float contentInset = Math.Max(0f, (currentBarWidth - BarWidth) / 2f);
        float zoneRight = currentBarWidth - contentInset - RightPad;
        float zoneLeft = zoneRight - RightZoneW;                         // = centre + 80 at the normal width

        // gold bloom from the top-right corner, mirroring the left
        shaderPaint.Shader = bloomRightShader.Radial(new SKPoint(currentBarWidth - 30f, 10f), 380f,
            [new SKColor(0xC5, 0xA5, 0x72, 34), new SKColor(0xC5, 0xA5, 0x72, 9), new SKColor(0xC5, 0xA5, 0x72, 0)], Stops.At045);
        c.DrawRect(new SKRect(currentBarWidth / 2f, 0, currentBarWidth, BarHeight), shaderPaint);

        // the lap number huge and ghosted behind the timer (like the car number on the radio card)
        if (snap.CurrentLap > 0)
        {
            c.Save(); c.ClipRect(new SKRect(currentBarWidth / 2f + WatchAlcoveRadius, LineH, currentBarWidth, BarHeight));
            var gp = ghostLapPaint;
            string ln = SmallStrings.Num(snap.CurrentLap);
            BigText.Draw(c, ln, zoneRight + 10f, BarHeight + 22f, gp);   // drawn once, reused (4K perf)
            c.Restore();
            // a small "LAP" beside it so nobody reads it as a position or car number (Angelo 2026-10-23)
            DrawTextTracked(c, ghostLapLabel, "LAP", zoneRight + 10f - gp.MeasureText(ln) - 6f, 52f, 2.4f, alignRight: true);
        }

        // ---- row 1: phase header + race meter ----
        var phase = BarPhase.Compute(snap, maxSessionSecondsSeen);
        SKColor pc = phase.Kind switch
        {
            BarPhaseKind.FinalLap => new SKColor(0xFF, 0xE8, 0xB0),
            BarPhaseKind.FinalLaps => PhaseFinalLaps,
            BarPhaseKind.FinalStint => PhaseFinalStint,
            _ => PhaseGold,
        };
        float hy = 36f;
        {
            var hp = phasePaint;
            hp.Color = pc;
            float lw = MeasureTracked(hp, phase.Label, 2.4f);
            if (phase.Kind == BarPhaseKind.FinalLap)
            {
                // a soft gold plate behind FINAL LAP with a shine sweeping across it every 2.4 s (no shine in light mode)
                var plate = new SKRect(zoneLeft - 8f, hy - 16f, zoneLeft + lw + 8f, hy + 6f);
                aaFillPaint.Color = new SKColor(PhaseGold.Red, PhaseGold.Green, PhaseGold.Blue, 40);
                c.DrawRoundRect(plate, 3f, 3f, aaFillPaint);
                float k = (float)((lastElapsedMs % 2400) / 2400.0);
                float sx = Perf.Light ? -10000f : plate.Left - 60f + (plate.Width + 120f) * k;
                c.Save(); c.ClipRect(plate);
                shaderPaint.Shader = finalShineShader.Linear(new SKPoint(sx - 50f, 0), new SKPoint(sx + 50f, 0),
                    [SKColors.White.WithAlpha(0), new SKColor(255, 236, 190, 130), SKColors.White.WithAlpha(0)], StopsLight);
                c.DrawRect(plate, shaderPaint);
                c.Restore();
            }
            DrawTextTracked(c, hp, phase.Label, zoneLeft, hy, 2.4f);

            // 5-segment meter at the right end: fills as the race / session goes on
            const int segs = 5; const float sw = 15f, sg = 3f, sh = 6f;
            float mRight = zoneRight, mLeft = mRight - segs * sw - (segs - 1) * sg;
            double prog = ComputeProgress(snap);
            int lit = (int)Math.Ceiling(prog * segs - 0.001);
            for (int i = 0; i < segs; i++)
            {
                var r = new SKRect(mLeft + i * (sw + sg), hy - 9f, mLeft + i * (sw + sg) + sw, hy - 9f + sh);
                bool on = i < lit;
                aaFillPaint.Color = on ? pc.WithAlpha(235) : new SKColor(255, 255, 255, 30);
                c.DrawRoundRect(r, 1.5f, 1.5f, aaFillPaint);
            }
            // hairline rule between the label and the meter
            float r0 = zoneLeft + lw + 14f, r1 = mLeft - 12f;
            if (r1 > r0 + 10f)
            {
                shaderPaint.Shader = phaseRuleShader.Linear(new SKPoint(r0, 0), new SKPoint(r1, 0), [pc.WithAlpha(120), pc.WithAlpha(30)], StopsEnds);
                c.DrawRect(new SKRect(r0, hy - 6.5f, r1, hy - 5.5f), shaderPaint);
            }
        }

        // ---- row 2: big timer, LAP chip, FLAG chip ----
        float by = 80f;
        string timeText = SessionTimerText(snap.SessionTimeRemaining);
        // 2026-10-24 (Angelo): when the clock runs out it keeps counting, as -0:42, like the real endurance broadcasts
        if (snap.SessionOvertimeSeconds > 0.5) timeText = OvertimeClock.Text(snap.SessionOvertimeSeconds);
        SKColor timeCol = TextIvory;                                     // (5) amber last 5 min, gold pulse on the final lap
        if (IsFinalLap(snap))
        {
            float pl = (float)(0.5 + 0.5 * Math.Sin(lastElapsedMs / 1000.0 * Math.PI * 2 / 1.2));
            timeCol = new SKColor(PhaseGold.Red, PhaseGold.Green, PhaseGold.Blue, (byte)(170 + 85 * pl));
        }
        else if (snap.SessionTimeRemaining > 0 && snap.SessionTimeRemaining < 300) timeCol = Amber;
        timerPaint.Color = timeCol;
        DrawTextTracked(c, timerPaint, timeText, zoneLeft, by + 1f, 0.6f);

        var labelPaint = smallLabelPaint; var valuePaint = smallValuePaint;

        // FLAG chip, right-aligned: dot + label, outline / tint in the flag colour while a flag is out
        // 2026-10-25 (Angelo): no chip at all under green - races are nearly always green; it only appears for a real flag
        var fr = new SKRect(zoneRight + 10f, by - 15f, zoneRight + 10f, by + 6f);
        if (flag.HasFlag)
        {
            string fl = flag.Label;
            float flw = MeasureTracked(labelPaint, fl, 1.2f);
            float fw = 12f + flw + 12f;
            fr = new SKRect(zoneRight - fw, by - 15f, zoneRight, by + 6f);
            // 2026-10-25 (Angelo): the flag itself is the chip's background - the cloth in the flag colour, rippling
            // diagonally with several waves, behind the label
            // green (the normal state) is quiet: a still, see-through tint; only a real flag waves (2026-10-25, Angelo: the
            // green one "is stealing my attention in a bad way")
            if (flag.HasFlag) FlagCloth(c, fr, flag.Colour);
            else { aaFillPaint.Color = flag.Colour.WithAlpha(46); c.DrawRoundRect(fr, 3f, 3f, aaFillPaint); }
            flagOutlinePaint.Color = flag.HasFlag ? flag.Colour.WithAlpha(190) : new SKColor(0xC5, 0xA5, 0x72, 70);
            c.DrawRoundRect(fr, 3f, 3f, flagOutlinePaint);
            if (flag.HasFlag)
                DrawTextTracked(c, flagShadowPaint, fl, fr.Left + 12f, by - 0.2f, 1.2f);                   // soft shadow: readable on any flag
            flagLabelPaint.Color = flag.HasFlag ? TextIvory : TextDim;
            DrawTextTracked(c, flagLabelPaint, fl, fr.Left + 12f, by - 1f, 1.2f);
        }

        // LAP chip, just left of the flag chip
        if (snap.CurrentLap != lapCur || snap.TotalLaps != lapTot)
        {
            lapCur = snap.CurrentLap; lapTot = snap.TotalLaps;
            lapText = snap.TotalLaps > 0 ? $"{snap.CurrentLap}/{snap.TotalLaps}" : snap.CurrentLap > 0 ? $"{snap.CurrentLap}" : "--";
        }
        string lv = lapText;
        float llw = MeasureTracked(labelPaint, "LAP", 1.2f), lvw = MeasureTracked(valuePaint, lv, 1.0f);
        float lwid = 8f + llw + 7f + lvw + 8f;
        var lr = new SKRect(fr.Left - 10f - lwid, by - 15f, fr.Left - 10f, by + 6f);
        c.DrawRoundRect(lr, 3f, 3f, chipFillPaint);
        c.DrawRoundRect(lr, 3f, 3f, chipLinePaint);
        DrawTextTracked(c, labelPaint, "LAP", lr.Left + 8f, by - 1f, 1.2f);
        DrawTextTracked(c, valuePaint, lv, lr.Left + 8f + llw + 7f, by, 1.0f);
    }

    /// <summary>LapTimeFormat.SessionTimer, made once per whole second shown.</summary>
    string SessionTimerText(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= 172800) return LapTimeFormat.SessionTimer(seconds);
        long whole = TimeSpan.FromSeconds(seconds).Ticks / TimeSpan.TicksPerSecond;
        if (whole != timerSec) { timerSec = whole; timerText = LapTimeFormat.SessionTimer(seconds); }
        return timerText;
    }

    void DrawPhaseChip(SKCanvas c, float x, float y, float width, BarPhase phase)
    {
        // Chip box: full width of the right-zone row 1, with gold border-top
        // and border-bottom hairlines, gold-on-dark vertical gradient.
        SKColor topColor, bottomColor, textColor;
        switch (phase.Kind)
        {
            case BarPhaseKind.FinalLap:
                topColor = PhaseGold;
                bottomColor = topColor;
                textColor = new SKColor(0xFF, 0xE8, 0xB0);
                break;
            case BarPhaseKind.FinalLaps:
                topColor = new SKColor(0xFF, 0x6B, 0x35);
                bottomColor = topColor;
                textColor = PhaseFinalLaps;
                break;
            case BarPhaseKind.FinalStint:
                topColor = new SKColor(255, 184, 112, 179);
                bottomColor = topColor;
                textColor = PhaseFinalStint;
                break;
            default:
                topColor = new SKColor(PhaseGold.Red, PhaseGold.Green, PhaseGold.Blue, 140);
                bottomColor = topColor;
                textColor = PhaseGold;
                break;
        }

        const float chipHeight = 22f;
        var rect = new SKRect(x, y, x + width, y + chipHeight);

        using var bgShader = SKShader.CreateLinearGradient(
            new SKPoint(x, rect.Top), new SKPoint(x, rect.Bottom),
            new[] { textColor.WithAlpha(28), new SKColor(0, 0, 0, 115) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        using var bgPaint = new SKPaint { IsAntialias = true, Shader = bgShader };
        c.DrawRect(rect, bgPaint);

        using var borderPaint = new SKPaint
        {
            Color = topColor,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            IsAntialias = true,
        };
        c.DrawLine(rect.Left, rect.Top, rect.Right, rect.Top, borderPaint);
        c.DrawLine(rect.Left, rect.Bottom, rect.Right, rect.Bottom, borderPaint);

        // Centred chip text.
        using var textPaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true,
            Typeface = Fonts.Inter.Bold,
            TextSize = 13f,
            TextAlign = SKTextAlign.Center,
        };
        textPaint.GetFontMetrics(out var m);
        float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;
        DrawTextTracked(c, textPaint, phase.Label, rect.MidX, baseline, 2.2f);
        if (phase.Kind == BarPhaseKind.FinalLap)
        {
            // (6) a gold shine sweeping across the box every 2.4 s
            float k = (float)((lastElapsedMs % 2400) / 2400.0);
            float sx = rect.Left - 80f + (rect.Width + 160f) * k;
            c.Save(); c.ClipRect(rect);
            using var shSh = SKShader.CreateLinearGradient(new SKPoint(sx - 60f, 0), new SKPoint(sx + 60f, 0),
                new[] { SKColors.White.WithAlpha(0), new SKColor(255, 236, 190, 120), SKColors.White.WithAlpha(0) },
                new[] { 0f, 0.5f, 1f }, SKShaderTileMode.Clamp);
            using var shp = new SKPaint { Shader = shSh };
            c.DrawRect(new SKRect(sx - 60f, rect.Top, sx + 60f, rect.Bottom), shp);
            c.Restore();
        }
    }

    void DrawTimeText(SKCanvas c, string text, float left, float baseline, SKColor col)
    {
        using var paint = new SKPaint
        {
            Color = col,
            IsAntialias = true,
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 24f,
        };
        paint.GetFontMetrics(out var m);
        float drawAt = baseline - m.Descent;
        DrawTextTracked(c, paint, text, left, drawAt, 1.0f);
    }

    void DrawLapText(SKCanvas c, string text, float centerX, float baseline)
    {
        using var paint = new SKPaint
        {
            Color = TextDim,
            IsAntialias = true,
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 14f,
            TextAlign = SKTextAlign.Center,
        };
        paint.GetFontMetrics(out var m);
        float drawAt = baseline - m.Descent;
        DrawTextTracked(c, paint, text, centerX, drawAt, 1.2f);
    }

    readonly SKPaint wfFill = new() { IsAntialias = true }, wfPole = new() { IsAntialias = true, StrokeWidth = 1.2f, StrokeCap = SKStrokeCap.Round };
    readonly SKPath wfPath = new();
    const int WfStrips = 14;

    /// <summary>A flag on a pole at the left of <paramref name="box"/>, rippling slowly; never leaves the box.</summary>
    void WavingFlag(SKCanvas c, SKRect box, SKColor col)
    {
        double t = WallTickMs / 1000.0;
        float poleX = box.Left + 1f, fx0 = poleX + 1f, fx1 = box.Right;
        float amp = Math.Min(1.4f, box.Height * 0.12f);                     // room kept top and bottom for the wave
        float top = box.Top + amp, bot = box.Bottom - amp;
        wfPole.Color = new SKColor(0xD8, 0xDC, 0xE2, 200);
        c.DrawLine(poleX, box.Top, poleX, box.Bottom, wfPole);
        float sw = (fx1 - fx0) / WfStrips;
        for (int i = 0; i < WfStrips; i++)
        {
            float u0 = i / (float)WfStrips, u1 = (i + 1) / (float)WfStrips;
            // a travelling wave, stronger away from the pole; slow (about one ripple every 2.5 s)
            float Dy(float u) => amp * u * (float)Math.Sin(u * 5.2 - t * 2.5);
            float y0 = Dy(u0), y1 = Dy(u1);
            float slope = (y1 - y0) / Math.Max(0.001f, sw);
            float shade = Math.Clamp(0.5f + slope * 0.9f, 0f, 1f);           // facing the light / turned away
            var cc = shade > 0.5f ? Lerp(col, Lighten(col, 0.45f), (shade - 0.5f) * 2f) : Lerp(Darken(col, 0.45f), col, shade * 2f);
            wfFill.Color = cc;
            wfPath.Reset();
            float xa = fx0 + i * sw, xb = xa + sw + 0.35f;                    // a hair of overlap: no seams
            wfPath.MoveTo(xa, top + y0); wfPath.LineTo(xb, top + y1); wfPath.LineTo(xb, bot + y1); wfPath.LineTo(xa, bot + y0); wfPath.Close();
            c.DrawPath(wfPath, wfFill);
        }
    }

    static SKColor Lerp(SKColor a, SKColor b, float k)
    {
        k = Math.Clamp(k, 0f, 1f);
        return new SKColor((byte)(a.Red + (b.Red - a.Red) * k), (byte)(a.Green + (b.Green - a.Green) * k), (byte)(a.Blue + (b.Blue - a.Blue) * k), (byte)(a.Alpha + (b.Alpha - a.Alpha) * k));
    }

    readonly SKPaint clothBase = new() { IsAntialias = true }, clothWave = new() { IsAntialias = true };

    /// <summary>The flag as cloth filling <paramref name="r"/>: its colour, with diagonal ripples - two wave trains at
    /// slightly different angles and speeds, so the folds cross and drift like a flag in the wind.</summary>
    void FlagCloth(SKCanvas c, SKRect r, SKColor col)
    {
        float t = (float)(WallTickMs / 1000.0);
        c.Save();
        c.ClipRoundRect(new SKRoundRect(r, 3f, 3f), antialias: true);
        clothBase.Shader = null;
        clothBase.Color = Darken(col, 0.35f).WithAlpha(235);
        c.DrawRect(r, clothBase);
        // a wave train: a repeating diagonal gradient of light fold / colour / shadow, sliding along its direction
        void Train(float angleDeg, float wave, float speed, byte light, byte dark)
        {
            double a = angleDeg * Math.PI / 180;
            float dx = (float)Math.Cos(a), dy = (float)Math.Sin(a);
            float off = (t * speed) % wave;
            var p0 = new SKPoint(r.Left + dx * off, r.Top + dy * off);
            var p1 = new SKPoint(p0.X + dx * wave, p0.Y + dy * wave);
            using var sh = SKShader.CreateLinearGradient(p0, p1,
                new[] { new SKColor(255, 255, 255, light), new SKColor(255, 255, 255, 0), new SKColor(0, 0, 0, dark), new SKColor(0, 0, 0, 0), new SKColor(255, 255, 255, light) },
                new[] { 0f, 0.3f, 0.55f, 0.8f, 1f }, SKShaderTileMode.Repeat);
            clothWave.Shader = sh;
            c.DrawRect(r, clothWave);
            clothWave.Shader = null;
        }
        clothBase.Color = col.WithAlpha(150);                          // the cloth colour over the darker base
        c.DrawRect(r, clothBase);
        Train(28f, 34f, 9f, 60, 75);                                    // the main folds
        Train(40f, 21f, 14f, 32, 42);                                   // smaller ripples crossing them
        c.Restore();
    }

    void DrawFlagState(SKCanvas c, BarFlag flag, float right, float baseline)
    {
        // Right-aligned: [DOT] [LABEL]
        const float dotRadius = 6.5f;
        const float dotGap = 8f;

        string label = flag.Label;
        using var labelPaint = new SKPaint
        {
            Color = TextIvory,
            IsAntialias = true,
            Typeface = Fonts.Inter.Bold,
            TextSize = 13.5f,
            TextAlign = SKTextAlign.Right,
        };
        labelPaint.GetFontMetrics(out var m);
        float drawAt = baseline - m.Descent;
        DrawTextTracked(c, labelPaint, label, right, drawAt, 1.9f, alignRight: true);

        float labelWidth = MeasureTracked(labelPaint, label, 1.9f);
        float dotCx = right - labelWidth - dotGap - dotRadius;
        float dotCy = baseline - m.Descent + m.Ascent / 2f;

        // Radial dot in flag colour.
        using var dotPaint = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                new SKPoint(dotCx - dotRadius * 0.3f, dotCy - dotRadius * 0.4f),
                dotRadius * 1.4f,
                new[]
                {
                    Lighten(flag.Colour, 0.5f),
                    flag.Colour,
                    Darken(flag.Colour, 0.4f),
                },
                new[] { 0f, 0.55f, 1f },
                SKShaderTileMode.Clamp),
        };
        c.DrawCircle(dotCx, dotCy, dotRadius, dotPaint);
        using var dotHighlight = new SKPaint
        {
            Color = new SKColor(255, 255, 255, 128),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.8f,
            IsAntialias = true,
        };
        c.DrawCircle(dotCx, dotCy, dotRadius, dotHighlight);
    }

    // ----- text helpers -----

    // 2026-10-29 (efficiency): through the shared text-blob cache (TextTracked / TextBlobs) - the same glyph positions as
    // the old per-character DrawText loop and GetGlyphWidths sum, without a float[], a string and a text blob per
    // character every frame.
    static void DrawTextTracked(SKCanvas c, SKPaint paint, string text, float x, float y,
                                float letterSpacingPx, bool alignRight = false)
        => TextTracked.Draw(c, paint, text, x, y, letterSpacingPx, alignRight);

    static float MeasureTracked(SKPaint paint, string text, float letterSpacingPx)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        if (letterSpacingPx <= 0.01f) return TextBlobs.Measure(text, paint);
        return TextBlobs.TrackedWidth(text, paint, letterSpacingPx);
    }

    static SKColor Lighten(SKColor c, float amount) => new(
        (byte)Math.Min(255, c.Red + (255 - c.Red) * amount),
        (byte)Math.Min(255, c.Green + (255 - c.Green) * amount),
        (byte)Math.Min(255, c.Blue + (255 - c.Blue) * amount),
        c.Alpha);

    static SKColor Darken(SKColor c, float amount) => new(
        (byte)(c.Red * (1 - amount)),
        (byte)(c.Green * (1 - amount)),
        (byte)(c.Blue * (1 - amount)),
        c.Alpha);

    // ----- flag resolution -----

    BarFlag ResolveFlag(FlagState f)
    {
        if (f.Checkered) return new BarFlag(FlagWhite, 1.4f, "CHECKERED FLAG", true, true);
        if (f.Red)       return new BarFlag(FlagRed,    1.2f, "RED FLAG", true, false);
        if (f.White)     return new BarFlag(FlagWhite,  1.4f, "WHITE FLAG", true, false);
        if (f.Caution)   return new BarFlag(FlagYellow, 1.6f, "FULL COURSE YELLOW", true, false);
        if (f.Yellow)    return new BarFlag(FlagYellow, 1.6f, "YELLOW FLAG", true, false);
        if (f.Blue)      return new BarFlag(FlagBlue,   1.5f, "BLUE FLAG", true, false);
        if (f.Black)     return new BarFlag(FlagBlack,  1.4f, "BLACK FLAG", true, false);
        // Default green (no flag active in race) — no pulse, no overlay, just
        // the green dot.
        return new BarFlag(FlagGreen, 0f, "GREEN FLAG", false, false);
    }

    SKBitmap? LoadFlag(string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var key = code.ToLowerInvariant();
        if (flagCache.TryGetValue(key, out var cached)) return cached;

        var path = Assets.Resolve("flags", key + ".png");
        SKBitmap? bmp = null;
        try
        {
            if (System.IO.File.Exists(path))
            {
                using var stream = System.IO.File.OpenRead(path);
                bmp = SKBitmap.Decode(stream);
                bmp?.SetImmutable();   // uploaded to the GPU once (4K perf, 2026-10-24)
            }
        }
        catch
        {
            // Swallow — a missing flag should never crash the overlay.
            bmp = null;
        }
        flagCache[key] = bmp;
        return bmp;
    }

    public void Dispose()
    {
        barBasePaint.Dispose();
        alcovePaint.Dispose();
        topGlossPaint.Dispose();
        bottomDarkenPaint.Dispose();
        progressBasePaint.Dispose();
        progressGoldPaint.Dispose();
        foreach (var b in flagCache.Values) b?.Dispose();
        foreach (var w in washCache.Values) w?.Dispose();
        washCache.Clear();
        flagCache.Clear();
    }

    readonly record struct BarFlag(
        SKColor Colour,
        float PulseSeconds,
        string Label,
        bool HasFlag,
        bool IsCheckered);
}

/// <summary>
/// The "phase chip" label and styling state derived from the snapshot. Port of
/// the legacy <c>getPhase</c> / <c>getPhaseClass</c> rules (session-bar-ORIGINAL
/// lines 4852–4865): bucket the session-time-remaining into OPENING / MID /
/// FINAL STINT / FINAL LAPS, with FINAL LAPS pulsing orange.
/// </summary>
public readonly record struct BarPhase(string Label, BarPhaseKind Kind)
{
    public static BarPhase Compute(RaceSnapshot snap, double maxSessionSecondsSeen)
    {
        // pre-race: on the grid -> STARTING GRID; once the pace car rolls (SessionState 3) -> FORMATION LAP
        if (snap.Phase == RacePhase.PreRace)
            return new BarPhase(snap.SessionState == 3 ? "FORMATION LAP" : "STARTING GRID", BarPhaseKind.Standby);
        if (snap.Phase == RacePhase.Unknown) return new BarPhase("STAND BY", BarPhaseKind.Standby);
        // not a race (2026-10-24, replay test: Lone Qualify showed "OPENING STINT"): name the session instead
        string st = snap.SessionTypeName ?? "";
        if (st.Length > 0 && !st.Contains("race", StringComparison.OrdinalIgnoreCase))
        {
            string name = st.Contains("qual", StringComparison.OrdinalIgnoreCase) ? "QUALIFYING"
                        : st.Contains("warm", StringComparison.OrdinalIgnoreCase) ? "WARM UP"
                        : st.Contains("practice", StringComparison.OrdinalIgnoreCase) ? "PRACTICE"
                        : st.ToUpperInvariant();
            return new BarPhase(name, BarPhaseKind.Normal);
        }

        if (SessionBarRenderer.IsFinalLap(snap)) return new BarPhase("FINAL LAP", BarPhaseKind.FinalLap);
        double pct = (1.0 - RaceProgress.Of(snap, maxSessionSecondsSeen)) * 100.0;

        if (pct > 75) return new BarPhase("OPENING STINT", BarPhaseKind.Normal);
        if (pct > 30) return new BarPhase("MID RACE",      BarPhaseKind.Normal);
        if (pct > 10) return new BarPhase("FINAL STINT",   BarPhaseKind.FinalStint);
        return        new BarPhase("FINAL LAPS",           BarPhaseKind.FinalLaps);
    }
}

public enum BarPhaseKind { Standby, Normal, FinalStint, FinalLaps, FinalLap }

/// <summary>How far through the race we are, 0..1 (2026-10-19). Lap races: the leader's
/// completed laps over the race distance. Timed races: elapsed over the OFFICIAL session
/// length from iRacing's session info; only when that is missing, the largest time
/// remaining seen this session.</summary>
public static class RaceProgress
{
    public static double Of(RaceSnapshot snap, double fallbackLength = 0)
    {
        if (snap.TotalLaps > 0)
        {
            int done = snap.Leader?.LapsCompleted ?? Math.Max(0, snap.CurrentLap - 1);
            return Math.Clamp(done / (double)snap.TotalLaps, 0, 1);
        }
        double len = snap.SessionLengthSeconds > 0 ? snap.SessionLengthSeconds : fallbackLength;
        if (len <= 0) return 0;
        // 2026-10-25 (Angelo): once the clock runs out (negative time while the last laps run) the race is at its end,
        // not its start - this used to drop the bar back to "OPENING STINT"
        if (snap.SessionTimeRemaining < 0) return 1;
        return Math.Clamp((len - snap.SessionTimeRemaining) / len, 0, 1);
    }
}

/// <summary>"-m:ss" for the session clock once time has run out, cached per whole second (drawn every frame).</summary>
internal static class OvertimeClock
{
    static int sec = -1; static string text = "";
    public static string Text(double over)
    {
        int s = (int)over;
        if (s != sec) { sec = s; text = "-" + LapTimeFormat.SessionTimer(s); }
        return text;
    }
}
