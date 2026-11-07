using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The battle card's internal state machine label. Lives at namespace scope
/// (rather than nested inside the renderer) so the deterministic
/// <see cref="BattleTestLoop"/> can produce values without crossing a private
/// boundary; still <c>internal</c> so it doesn't leak out of the Overlays asm.
/// </summary>
internal enum BattleState { None, Attack, Defend }

/// <summary>
/// Port 06 — the Battle Card. Auto-triggered overlay that shows when the
/// player has a same-class opponent within ~1 s ahead (attack) or behind
/// (defend). 1000×280 deep-glass card with a 3-column layout
/// (left driver | gap timer | right driver), microcopy banner, a smoothly-
/// lerped gap value with AHEAD/BEHIND label, a CLOSING/OPENING/STEADY trend,
/// and an EMA-smoothed center-pivot tension meter. Owns the full state
/// machine (enter 1.0 s, exit 2.0 s hysteresis, min dwell 4 s, dropout grace
/// 1.5 s, reopen cooldown 2.5 s, race + broadcast-phase gated).
/// </summary>
public sealed class BattleCardRenderer : IDisposable
{
    public const float CardWidth = 1000f;
    public const float CardHeight = 280f;

    // 3-col layout (legacy grid-template-columns: 360px 280px 360px).
    public const float ColDriver = 360f;
    public const float ColGap    = 280f;

    // Battle activation thresholds. Two sources are OR'd in EvaluateLive:
    // (1) opening-lap force — battle ON regardless of gaps until the overall
    // leader completes lap 1 (see PresentationState.Lap1Complete); and
    // (2) the 2.0s same-class rule — enter at <= 2.0s, exit only at > 2.3s,
    // with MinDwellMs preventing a brief blip from strobing the card on/off.
    // All four constants tuned together; keep them adjacent.
    const double EnterThresholdS = 2.00;
    const double ExitThresholdS = 2.30;
    const double MinDwellMs = 4000;   // 2026-10-25: once up it stays at least 4 s (was 1.5 s) - no flicker
    const double ArmMs = 1800;        // 2026-10-25: close for 1.8 s before the card comes in
    double armSinceMs = -1;
    const double DropoutGraceMs = 1500;
    const double ReopenCooldownMs = 2500;

    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);

    // Gap smoothing + tension EMA (legacy lines 886-973).
    const double GapLerpRate = 0.18;
    const double GapConvergenceEps = 0.003;
    const double TensionEmaAlpha = 0.32;
    const double TensionFullScale = 0.045;
    const double TensionDeadband = 0.0015;
    const double TrendDeadband = 0.005;

    // Critical thresholds (legacy lines 538-561).
    const double CriticalGapS = 0.50;
    const double CriticalPulsePeriodMs = 1400;
    const double WheelCloseGapS = 0.30;
    const double WheelClosePulsePeriodMs = 850;

    // Entrance / leave timings (legacy stagger ~880 ms).
    const double EnterAnimMs = 920;
    const double ExitAnimMs = 380;

    static readonly SKColor BgGlass        = ParseHex(BroadcastPalette.TowerGlassBase, 209);
    static readonly SKColor TextPrimary    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextTertiary   = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor BorderHairline = ParseHex(BroadcastPalette.BorderHairline, 120);
    static readonly SKColor AccentGold     = ParseHex(BroadcastPalette.Leader);
    static readonly SKColor PlayerBlue     = ParseHex(BroadcastPalette.PlayerStripe);
    static readonly SKColor StatusGain     = ParseHex("#34D399");
    static readonly SKColor StatusLoss     = ParseHex("#F87171");

    // State machine fields.
    BattleState state;
    double stateEnteredAtMs;
    double visibleSinceMs;
    double firstNoneAtMs;
    double hiddenAtMs;
    int activeOpponentCarIdx = -1;
    bool isVisible;
    double leaveStartedAtMs = -1;

    // Cached last detection for use during dropout grace.
    Driver? lastOpponent;
    Driver? lastPlayer;
    double lastGapS;
    SKColor lastClassColor = AccentGold;
    int lastMicroPosition;
    BattleState lastDetectedState;

    // Smoothed display values.
    double displayGap;
    double smoothedTension;
    double prevGapForTension;
    bool haveSmoothing;
    // Last UpdateSmoothing tick — drives the dt that makes the gap lerp and
    // the tension EMA frame-rate-independent. The legacy constants
    // (GapLerpRate=0.18, TensionEmaAlpha=0.32) are calibrated for a 60 fps
    // step; we treat them as "per 1/60 s" and exponent-scale the per-step
    // weight by the actual elapsed time so the meter converges at the same
    // wall-clock pace whether iRacing is dropping us to 45 fps or holding 60.
    double lastSmoothMs = -1;

    /// <summary>True while the card is rendering any pixels. Featured card reads this to yield.</summary>
    public bool IsActive => isVisible || leaveStartedAtMs >= 0;
    /// <summary>The rival the card is about (for the pit lane battle card).</summary>
    public Driver? Opponent => lastOpponent;

    /// <summary>2026-10-25: the cars on the battle card right now (the focus car, its rival, and the three-way /
    /// five-car extras while they show) - for the battle track map.</summary>
    /// <summary>Left / right edge of the card as last drawn (it widens for three and five cars).</summary>
    public float DrawnLeft { get; private set; }
    public float DrawnRight { get; private set; }

    public IReadOnlyList<int> BattleCars
    {
        get
        {
            var l = new List<int>(5);
            void Add(Driver? d) { if (d != null && !l.Contains(d.CarIdx)) l.Add(d.CarIdx); }
            Add(lastPlayer); Add(lastOpponent);
            if (threeOn) { Add(threeAhead); Add(threeBehind); if (extraAOn) Add(extraAhead); if (extraBOn) Add(extraBehind); }
            return l;
        }
    }
    public int BattlePosition => lastMicroPosition;

    // Opening-lap force retired per Angelo: the 2.0s same-class rule governs
    // battle mode from the green flag onward (it comes on when the bunched
    // field is within 2.0s and releases to default when no one is within 2.3s).
    // Kept as a disabled toggle so the force path can be restored quickly.
    static bool OpeningLapForceEnabled => false;

    /// <summary>
    /// When non-null, overrides live detection with a synthetic battle each
    /// frame — used by <see cref="BattleTestLoop"/> for visual verification
    /// without iRacing.
    /// </summary>
    internal BattleTestLoop? TestLoop { get; set; }

    public BattleCardRenderer() { }

    // ----- #604 perf pass 2 (2026-10-30): everything a frame needs is made once ---------------------------------
    // The card used to create ~50 paints / shaders / round rects / strings a frame (~65 KB of garbage a frame in the
    // race_frame bench); they are kept here and only their colour / size changes. The blurred glows (the player's
    // position, the wheel-to-wheel gap halo) and the faded car pictures are kept as screen-grid pictures
    // (ScreenPlate) while they hold still, so the graphics card no longer runs a blur / layer pass for each of
    // them every frame. Same pixels (race_frame hashes, GPU frames compared).
    static readonly SKMaskFilter ShadowBlur6 = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f);
    static readonly SKMaskFilter HaloBlur9 = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 9f);
    static readonly SKImageFilter GlowBlur6 = SKImageFilter.CreateBlur(6, 6);
    static readonly SKImageFilter GapHaloBlur8 = SKImageFilter.CreateBlur(8, 8);
    static readonly SKColor HaloGold = ParseHex("#FFE89A");
    readonly SKPaint fillP = new();                                   // plain rects (colour set per draw)
    readonly SKPaint fillAaP = new() { IsAntialias = true };          // antialiased fills (colour / shader set per draw)
    readonly SKPaint shaderP = new();                                 // non-antialiased gradient fills
    readonly SKPaint strokeP = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    readonly SKPaint haloP = new() { IsAntialias = true, MaskFilter = HaloBlur9 };
    readonly SKPaint logoP = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High };
    readonly SKPaint tickP = new() { IsAntialias = true, StrokeWidth = 1f };
    // text paints, one per role (typeface / size / align fixed; colour per draw)
    readonly SKPaint tabHeadP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 17f };
    readonly SKPaint tabPosP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 19f };
    readonly SKPaint pressLabP = new() { IsAntialias = true, Typeface = Fonts.BarlowCondensed.ExtraBold, TextSize = 21f };
    readonly SKPaint pressVerbP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13.5f };
    readonly SKPaint swapP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14f };
    readonly SKPaint label14P = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 14f };
    readonly SKPaint posBigP = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 40f };
    readonly SKPaint nameP = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 30f, TextAlign = SKTextAlign.Center };
    readonly SKPaint lapP = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 22f };
    readonly SKPaint carP = new() { IsAntialias = true, Typeface = Fonts.Inter.Medium, TextSize = 14f, TextAlign = SKTextAlign.Right };
    readonly SKPaint chipP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 12f, TextAlign = SKTextAlign.Center };
    readonly SKPaint dirP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 14.5f, TextAlign = SKTextAlign.Center };
    readonly SKPaint miniDirP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 13f, TextAlign = SKTextAlign.Center };
    readonly SKPaint gapBigP = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 92f, TextAlign = SKTextAlign.Center };
    readonly SKPaint miniBigP = new() { IsAntialias = true, Typeface = Fonts.CormorantGaramond.Bold, TextSize = 58f, TextAlign = SKTextAlign.Center };
    readonly SKPaint trendP = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = 15f };
    readonly SKPaint meterLabP = new() { IsAntialias = true, Typeface = Fonts.Inter.SemiBold, TextSize = 13f };
    readonly ShaderSlot chromeGlow = new(), tensionRib = new();
    readonly ShaderSlot[] hairline = { new(), new(), new(), new(), new(), new(), new(), new(), new(), new() };
    readonly ShaderSlot[] playerBg = { new(), new(), new(), new(), new(), new(), new() };
    readonly ShaderSlot[] tabGlow = { new(), new() }, tabSweep = { new(), new() };
    readonly SKRoundRect tabRR = new();
    readonly SKPath chevPath = new(), ribPath = new(), headPath = new();
    readonly SKColorFilter?[] logoFade = new SKColorFilter?[256];
    readonly SKColor[] cols2 = new SKColor[2], cols3 = new SKColor[3], cols4 = new SKColor[4];
    static readonly float[] Stops01 = { 0f, 1f }, StopsHair = { 0f, 0.18f, 0.82f, 1f }, StopsSweep = { 0f, 0.5f, 1f }, StopsRib = { 0f, 0.45f, 1f };
    readonly (float L, float W)[] colsBuf = new (float L, float W)[9];
    readonly float[] colW = new float[9];
    // the screen-grid pictures: the player's position glow, the gap halo, and each column's faded car / maker logo
    readonly ScreenPlate<GlowKey> posGlowPlate = new(DrawGlow), gapHaloPlate = new(DrawGlow);
    readonly ScreenPlate<LiveryKey>[] liveryPlates = Enumerable.Range(0, 7).Select(_ => new ScreenPlate<LiveryKey>(DrawLiveryLayer)).ToArray();
    readonly ScreenPlate<BackdropKey>[] backdropPlates = Enumerable.Range(0, 7).Select(_ => new ScreenPlate<BackdropKey>(DrawBackdropLayer)).ToArray();
    // strings made once per value
    readonly Dictionary<(string?, string?), string> nameCache = new();
    readonly Dictionary<string, string> upperCache = new(StringComparer.Ordinal);
    readonly Dictionary<float, string> lapCache = new();
    readonly Dictionary<string, string> sufCache = new(StringComparer.Ordinal);
    readonly string?[] trendRate = new string?[4], trendLine = new string?[4];
    string gapTxt = "", miniTxtA = "", miniTxtB = "", miniTxtC = "", miniTxtD = ""; double gapTxtFor = double.NaN, miniForA = double.NaN, miniForB = double.NaN, miniForC = double.NaN, miniForD = double.NaN;
    int posTabFor = int.MinValue; string posTab = "";
    int swapTxtFor = int.MinValue; bool swapTxtGained; string swapTxt = "";

    readonly record struct GlowKey(string Text, SKTypeface Face, float Size, SKColor Color, SKImageFilter Blur, float X, float Y);
    static void DrawGlow(SKCanvas c, in GlowKey k, SKPaint p)
    {
        p.IsAntialias = true; p.Typeface = k.Face; p.TextSize = k.Size; p.Color = k.Color; p.ImageFilter = k.Blur;
        p.TextAlign = SKTextAlign.Left;
        TextBlobs.Draw(c, k.Text, k.X, k.Y, p);
        p.ImageFilter = null;
    }
    /// <summary>Glow area of text drawn at (x, y): its ink bounds plus three blur radii (and a pixel).</summary>
    static SKRect GlowBounds(SKPaint textP, string text, float x, float y, float sigma)
    {
        var b = new SKRect();
        textP.MeasureText(text, ref b);
        b.Offset(x, y);
        b.Inflate(sigma * 3f + 2f, sigma * 3f + 2f);
        return b;
    }

    readonly record struct LiveryKey(SKImage Livery, SKRect Dst, SKRect Col);
    static void DrawLiveryLayer(SKCanvas c, in LiveryKey k, SKPaint p)
    {
        var dst = k.Dst; var col = k.Col;
        float lw = dst.Width, lh = dst.Height;
        // contact shadow under the tyres
        p.IsAntialias = true; p.Color = new SKColor(0, 0, 0, 120); p.MaskFilter = ShadowBlur6;
        c.DrawOval(new SKRect(dst.Left + lw * 0.08f, dst.Bottom - lh * 0.10f, dst.Right - lw * 0.04f, dst.Bottom + lh * 0.04f), p);
        p.MaskFilter = null; p.Color = SKColors.Black; p.FilterQuality = SKFilterQuality.High;
        c.DrawImage(k.Livery, dst, p);
        p.FilterQuality = SKFilterQuality.None; p.IsAntialias = false;
        using var fadeX = SKShader.CreateLinearGradient(new SKPoint(col.Left, 0), new SKPoint(col.Left + col.Width * 0.08f, 0),
            new[] { SKColors.Transparent, SKColors.White }, new[] { 0f, 1f }, SKShaderTileMode.Clamp);
        p.Shader = fadeX; p.BlendMode = SKBlendMode.DstIn;
        c.DrawRect(col, p);
        p.Shader = null; p.BlendMode = SKBlendMode.SrcOver;
    }

    readonly record struct BackdropKey(SKImage Img, SKRect Dst);
    static void DrawBackdropLayer(SKCanvas c, in BackdropKey k, SKPaint p)
    {
        var dst = k.Dst;
        p.IsAntialias = true; p.Color = SKColors.Black; p.FilterQuality = SKFilterQuality.Medium;
        c.DrawImage(k.Img, dst, p);
        // soften the image's own edges (square-backed logos like Ferrari's shield otherwise show as a hard-edged block)
        p.IsAntialias = false; p.FilterQuality = SKFilterQuality.None;
        using var ms = SKShader.CreateRadialGradient(new SKPoint(dst.MidX, dst.MidY), Math.Max(dst.Width, dst.Height) * 0.52f,
            new[] { SKColors.White, SKColors.White, SKColors.White.WithAlpha(0) }, new[] { 0f, 0.3f, 1f }, SKShaderTileMode.Clamp);
        p.Shader = ms; p.BlendMode = SKBlendMode.DstIn;
        c.DrawRect(dst, p);
        p.Shader = null; p.BlendMode = SKBlendMode.SrcOver;
    }

    string Name(Driver d)
    {
        if (!nameCache.TryGetValue((d.Name, d.AbbrevName), out var n))
        {
            if (nameCache.Count > 256) nameCache.Clear();
            nameCache[(d.Name, d.AbbrevName)] = n = NameFormat.Broadcast(d);
        }
        return n;
    }
    string Upper(string s)
    {
        if (!upperCache.TryGetValue(s, out var u)) { if (upperCache.Count > 256) upperCache.Clear(); upperCache[s] = u = s.ToUpperInvariant(); }
        return u;
    }
    string Lap(float t)
    {
        if (!lapCache.TryGetValue(t, out var u)) { if (lapCache.Count > 256) lapCache.Clear(); lapCache[t] = u = LapTimeFormat.Lap(t); }
        return u;
    }
    static string Gap2(double v, ref double forV, ref string txt)
    {
        if (v != forV || txt.Length == 0) { forV = v; txt = v.ToString("0.00", CultureInfo.InvariantCulture); }
        return txt;
    }
    SKColorFilter LogoFade(byte a) => logoFade[a] ??= SKColorFilter.CreateBlendMode(new SKColor(255, 255, 255, a), SKBlendMode.DstIn);

    // ----- pass / swap animation (2026-10-19) -----
    double swapAtMs = -1;
    bool swapGained;
    double lastDrawMs;
    const double SwapGlideMs = 700, SwapBannerMs = 3600;
    const double WipeInMs = 560;

    // Car-maker logo backdrops for the driver panels (shared cache, 2026-10-18).
    readonly GridHeroBoard logoBackdrops = new();

    /// <summary>Draws one frame; runs the state machine first, then renders.</summary>
    public void Draw(SKCanvas canvas, float originX, float originY,
                     RaceSnapshot snapshot, double nowMs)
    {
        // Compute detection (live or test).
        BattleState detected;
        Driver? opponent;
        Driver? player;
        double? gapS;
        SKColor classColor;
        int microPos;

        if (TestLoop != null)
        {
            (detected, opponent, gapS, classColor, microPos, player) = TestLoop.Evaluate(snapshot, nowMs);
        }
        else
        {
            (detected, opponent, gapS, classColor, microPos, player) = EvaluateLive(snapshot);
        }

        if (detected != BattleState.None && gapS.HasValue && opponent != null && player != null)
        {
            lastOpponent = opponent;
            lastPlayer = player;
            lastGapS = gapS.Value;
            lastClassColor = classColor;
            lastMicroPosition = microPos;
            lastDetectedState = detected;
        }

        UpdateStateMachine(detected, opponent, gapS, nowMs);

        // Once the leave animation has fully elapsed, clear the leave marker so
        // IsActive returns false and the bar / tower / featured restore. Without
        // this reset, leaveStartedAtMs stays >= 0 forever after the first exit,
        // IsActive (and thus PresentationState.IsBattleActive) sticks on, and the
        // session bar stays expanded while the tower and featured never return.
        if (!isVisible && leaveStartedAtMs >= 0 && (nowMs - leaveStartedAtMs) >= ExitAnimMs)
            leaveStartedAtMs = -1;

        float alpha = ComputeAlpha(nowMs);
        if (alpha <= 0.005f) return;
        lastDrawMs = nowMs;
        if (lastPlayer != null && lastPlayer.BestLapTime > 30) lapForTrend = lastPlayer.BestLapTime;

        // If we're holding state via grace and the live evaluator no longer
        // returns an opponent, fall back to the cached last detection.
        var renderOpponent = opponent ?? lastOpponent;
        var renderPlayer   = player   ?? lastPlayer;
        var renderGap      = gapS     ?? lastGapS;
        var renderColor    = (opponent != null) ? classColor : lastClassColor;
        var renderMicroPos = (opponent != null) ? microPos : lastMicroPosition;

        // Drive smoothed gap + tension (uses the *raw* detected gap, not lerped).
        if (state != BattleState.None && opponent != null && gapS.HasValue)
        {
            UpdateSmoothing(gapS.Value, nowMs);
        }
        else
        {
            // Idle: reset the dt clock so the next battle's first frame doesn't
            // see a giant elapsed-time gap (which would over-weight the EMA).
            lastSmoothMs = -1;
        }

        // ---- Three-way battle (2026-10-18, Angelo) -------------------------------
        // When the player has a same-class car within 2 s AHEAD and BEHIND at the
        // same time, the card widens (1000 -> 1280 px, the full slot) and shows
        // BEHIND | gap | YOU | gap | AHEAD. Hysteresis (on <= 2.0 s both, off when
        // either > 2.3 s) stops it flickering; the layouts cross-fade over 320 ms.
        UpdateThreeWay(snapshot, nowMs);
        float t3 = ThreeWayProgress(nowMs);
        EaseExtras(nowMs);
        float cardW = CardWidth + (ThreeWidth() - CardWidth) * t3;
        float shift = (cardW - CardWidth) / 2f;
        DrawnLeft = originX - shift; DrawnRight = originX - shift + cardW;

        canvas.Save();
        canvas.Translate(originX - shift, originY + ComputeTranslateY(nowMs));

        var rect = new SKRect(0, 0, cardW, CardHeight);

        // 2026-10-24 (Angelo): "BATTLE FOR P3" and "UNDER PRESSURE presented by" sit on top of the card as
        // their own animated tabs (they were small lines inside the card and easy to miss)
        DrawTabs(canvas, rect, renderMicroPos, renderPlayer, renderColor, alpha, nowMs);

        // Entrance opens from the centre outward (and closes back to it), like the
        // grid pages' band wipe, instead of a plain fade.
        float wipe = WipeProgress(nowMs);
        if (wipe < 0.999f)
        {
            float half = cardW / 2f * wipe;
            canvas.ClipRect(new SKRect(cardW / 2f - half, -20, cardW / 2f + half, CardHeight + 20));
        }

        DrawCardChrome(canvas, rect, renderColor, alpha, nowMs);
        DrawMicrocopy(canvas, rect, renderMicroPos, renderPlayer, alpha);

        if (state == BattleState.None || renderOpponent == null || renderPlayer == null)
        {
            // Chrome only (rare — usually means we just entered/exited).
            canvas.Restore();
            return;
        }

        if (t3 > 0.001f && threeAhead != null && threeBehind != null)
            DrawThreeWay(canvas, rect, renderPlayer, alpha * t3);
        if (t3 >= 0.999f) { canvas.Restore(); return; }
        alpha *= 1f - t3;
        canvas.Translate(shift, 0);          // two-way content stays centred while morphing
        rect = new SKRect(0, 0, CardWidth, CardHeight);

        // Layout sides:
        //  attack → player LEFT, opponent ahead RIGHT
        //  defend → opponent behind LEFT, player RIGHT
        Driver leftDriver, rightDriver;
        bool leftIsPlayer;
        if (state == BattleState.Attack)
        {
            leftDriver = renderPlayer; rightDriver = renderOpponent; leftIsPlayer = true;
        }
        else
        {
            leftDriver = renderOpponent; rightDriver = renderPlayer; leftIsPlayer = false;
        }

        var leftRect  = new SKRect(rect.Left, rect.Top, rect.Left + ColDriver, rect.Bottom);
        var gapRect   = new SKRect(leftRect.Right, rect.Top, leftRect.Right + ColGap, rect.Bottom);
        var rightRect = new SKRect(gapRect.Right, rect.Top, rect.Right, rect.Bottom);

        DrawColumnHairline(canvas, rect, leftRect.Right, alpha, 8);
        DrawColumnHairline(canvas, rect, gapRect.Right, alpha, 9);

        // A pass: the two drivers trade places across the card, the gap re-fades.
        double st = swapAtMs >= 0 ? nowMs - swapAtMs : double.MaxValue;
        if (st < SwapGlideMs)
        {
            float e = (float)(st / SwapGlideMs); e = e < 0.5f ? 4 * e * e * e : 1 - (float)Math.Pow(-2 * e + 2, 3) / 2;
            float travel = rightRect.Left - leftRect.Left;
            canvas.Save(); canvas.ClipRect(rect);
            canvas.Save(); canvas.Translate(travel * (1 - e), 0);
            DrawDriverColumn(canvas, leftRect, leftDriver, leftIsPlayer, alpha, 0);
            canvas.Restore();
            canvas.Save(); canvas.Translate(-travel * (1 - e), 0);
            DrawDriverColumn(canvas, rightRect, rightDriver, !leftIsPlayer, alpha, 1);
            canvas.Restore();
            canvas.Restore();
            DrawGapColumn(canvas, gapRect, alpha * Math.Clamp((e - 0.5f) * 2f, 0f, 1f), nowMs);
        }
        else
        {
            DrawDriverColumn(canvas, leftRect,  leftDriver,  leftIsPlayer,  alpha, 0);
            DrawGapColumn(canvas, gapRect, alpha, nowMs);
            DrawDriverColumn(canvas, rightRect, rightDriver, !leftIsPlayer, alpha, 1);
        }
        // flash on the player's panel after a pass
        if (st >= SwapGlideMs && st < SwapBannerMs)
        {
            float fk = 1f - (float)((st - SwapGlideMs) / (SwapBannerMs - SwapGlideMs));
            var pr = leftIsPlayer ? leftRect : rightRect;
            var fc = swapGained ? StatusGain : StatusLoss;
            fillP.Color = fc.WithAlpha((byte)(46 * fk * alpha));
            canvas.DrawRect(new SKRect(pr.Left, pr.Top + 1, pr.Right, pr.Bottom), fillP);
            fillP.Color = fc.WithAlpha((byte)(255 * fk * alpha));
            canvas.DrawRect(new SKRect(pr.Left, pr.Top + 1, pr.Left + 3, pr.Bottom), fillP);
        }

        canvas.Restore();
    }

    // ----- three-way battle (2026-10-18) -----
    public const float ThreeWayWidth = 1280f;      // full battle slot width
    const float ThreeDriverW = 320f;               // 3 x 320 + 2 x 160 = 1280
    const double ThreeOnS = 2.0, ThreeOffS = 2.3, ThreeMorphMs = 320;
    bool threeOn;
    double threeFlipMs = -1;
    Driver? threeAhead, threeBehind;
    double threeGapA = double.NaN, threeGapB = double.NaN;
    // 2026-10-25 (Angelo: "maybe you could add up to 5 cards like one more on each side of the 3 way battle card that
    // shows margins like the 3 way card did"): when another class car is within 2 s of the car ahead (or of the car
    // behind), it joins on that end with its own gap - up to five cars. Each end eases in and out on its own.
    Driver? extraAhead, extraBehind;
    double extraGapA = double.NaN, extraGapB = double.NaN;
    bool extraAOn, extraBOn;
    float eA, eB; double extrasAtMs = -1;
    const double ExtraOnS = 2.0, ExtraOffS = 2.4;

    void UpdateExtras(RaceSnapshot snap)
    {
        Driver? a2 = null, b2 = null; double ga2 = double.NaN, gb2 = double.NaN;
        if (threeAhead != null && threeBehind != null)
            foreach (var cls in snap.ClassLeaderboard)
            {
                var ds = cls.Drivers;
                int ia = -1, ib = -1;
                for (int i = 0; i < ds.Count; i++) { if (ds[i].CarIdx == threeAhead.CarIdx) ia = i; if (ds[i].CarIdx == threeBehind.CarIdx) ib = i; }
                if (ia < 0 && ib < 0) continue;
                if (ia > 0 && threeAhead.LapDifference == ds[ia - 1].LapDifference && ds[ia].IntervalSeconds is double ia2 && ia2 > 0) { a2 = ds[ia - 1]; ga2 = ia2; }
                if (ib >= 0 && ib + 1 < ds.Count && ds[ib + 1].LapDifference == threeBehind.LapDifference && !ds[ib + 1].IsInPit && ds[ib + 1].IntervalSeconds is double ib2 && ib2 > 0) { b2 = ds[ib + 1]; gb2 = ib2; }
                break;
            }
        bool wantA = a2 != null && !a2.IsInPit && ga2 <= (extraAOn ? ExtraOffS : ExtraOnS);
        bool wantB = b2 != null && gb2 <= (extraBOn ? ExtraOffS : ExtraOnS);
        extraAOn = wantA; extraBOn = wantB;
        if (wantA) { extraAhead = a2; extraGapA = double.IsNaN(extraGapA) ? ga2 : extraGapA + (ga2 - extraGapA) * 0.25; }
        if (wantB) { extraBehind = b2; extraGapB = double.IsNaN(extraGapB) ? gb2 : extraGapB + (gb2 - extraGapB) * 0.25; }
    }

    void EaseExtras(double nowMs)
    {
        double dt = extrasAtMs < 0 ? 0 : Math.Clamp(nowMs - extrasAtMs, 0, 100); extrasAtMs = nowMs;
        float k = (float)(dt / 320.0);
        eA = Math.Clamp(eA + (extraAOn ? k : -k), 0f, 1f); eB = Math.Clamp(eB + (extraBOn ? k : -k), 0f, 1f);
        if (eA <= 0f) { extraAhead = null; extraGapA = double.NaN; }
        if (eB <= 0f) { extraBehind = null; extraGapB = double.NaN; }
    }

    static float Sm(float x) => x * x * (3 - 2 * x);
    float DW => 320f - 35f * (Sm(eA) + Sm(eB));
    float GW => 160f - 25f * (Sm(eA) + Sm(eB));
    float ThreeWidth() => DW * (3 + Sm(eA) + Sm(eB)) + GW * (2 + Sm(eA) + Sm(eB));

    /// <summary>Column x positions for the 3-5 car layout: (behind2, gapB2, behind, gapB, you, gapA, ahead, gapA2, ahead2) left edges + widths.</summary>
    (float L, float W)[] ThreeCols(SKRect rect)
    {
        float sa = Sm(eA), sb = Sm(eB), dw = DW, gw = GW;
        var w = colW;   // (kept between frames: no arrays per frame)
        w[0] = dw * sb; w[1] = gw * sb; w[2] = dw; w[3] = gw; w[4] = dw; w[5] = gw; w[6] = dw; w[7] = gw * sa; w[8] = dw * sa;
        float total = 0; foreach (var x in w) total += x;
        float x0 = rect.MidX - total / 2f;
        var res = colsBuf;
        for (int i = 0; i < 9; i++) { res[i] = (x0, w[i]); x0 += w[i]; }
        return res;
    }

    void UpdateThreeWay(RaceSnapshot snap, double nowMs)
    {
        bool want = false;
        if (TestLoop == null && state != BattleState.None)
        {
            var bc = BattleDetection.Compute(snap);
            if (bc.Player is { IsInPit: true }) bc = bc with { Ahead = bc.Ahead is { IsInPit: true } ? bc.Ahead : null, Behind = bc.Behind is { IsInPit: true } ? bc.Behind : null };
            double ga = bc.GapAheadSeconds ?? double.PositiveInfinity;
            double gb = bc.GapBehindSeconds ?? double.PositiveInfinity;
            double lim = threeOn ? ThreeOffS : ThreeOnS;
            want = bc.Ahead != null && bc.Behind != null && ga <= lim && gb <= lim;
            if (bc.Ahead != null && bc.Behind != null)
            {
                threeAhead = bc.Ahead; threeBehind = bc.Behind;
                if (double.IsFinite(ga)) threeGapA = double.IsNaN(threeGapA) ? ga : threeGapA + (ga - threeGapA) * 0.25;
                if (double.IsFinite(gb)) threeGapB = double.IsNaN(threeGapB) ? gb : threeGapB + (gb - threeGapB) * 0.25;
            }
        }
        if (want != threeOn) { threeOn = want; threeFlipMs = nowMs; }
        if (threeOn) UpdateExtras(snap); else { extraAOn = extraBOn = false; }
        if (!threeOn && ThreeWayProgress(nowMs) <= 0f) { threeGapA = double.NaN; threeGapB = double.NaN; }
    }

    float ThreeWayProgress(double nowMs)
    {
        double k = threeFlipMs < 0 ? 1 : Math.Clamp((nowMs - threeFlipMs) / ThreeMorphMs, 0, 1);
        k = k * k * (3 - 2 * k);                                   // smoothstep
        return (float)(threeOn ? k : 1 - k);
    }

    /// <summary>True while the three-way layout is showing or morphing (keeps the host presenting).</summary>
    public bool ThreeWayAnimating(double nowMs) { float p = ThreeWayProgress(nowMs); return p > 0f && p < 1f; }

    void DrawThreeWay(SKCanvas c, SKRect rect, Driver player, float alpha)
    {
        var cols = ThreeCols(rect);
        SKRect R(int i) => new(cols[i].L, rect.Top, cols[i].L + cols[i].W, rect.Bottom);
        for (int i = 0; i < 8; i++)
        {
            if (cols[i].W < 1f || cols[i + 1].W < 1f) continue;
            float ha = (i < 2 ? Sm(eB) : i >= 6 ? Sm(eA) : 1f);
            DrawColumnHairline(c, rect, cols[i].L + cols[i].W, alpha * ha, i);
        }
        if (extraBehind != null && eB > 0.02f)
        {
            c.Save(); c.ClipRect(new SKRect(cols[0].L, rect.Top, cols[1].L + cols[1].W, rect.Bottom));
            DrawDriverColumn(c, new SKRect(cols[0].L, rect.Top, cols[0].L + DW, rect.Bottom), extraBehind, false, alpha * Sm(eB), 5);
            DrawMiniGap(c, R(1), extraGapB, "BEHIND", alpha * Sm(eB), ref miniForA, ref miniTxtA);
            c.Restore();
        }
        DrawDriverColumn(c, R(2), threeBehind!, false, alpha, 2);
        DrawMiniGap(c, R(3), threeGapB, "BEHIND", alpha, ref miniForB, ref miniTxtB);
        DrawDriverColumn(c, R(4), player, true, alpha, 3);
        DrawMiniGap(c, R(5), threeGapA, "AHEAD", alpha, ref miniForC, ref miniTxtC);
        DrawDriverColumn(c, R(6), threeAhead!, false, alpha, 4);
        if (extraAhead != null && eA > 0.02f)
        {
            c.Save(); c.ClipRect(new SKRect(cols[7].L, rect.Top, cols[8].L + cols[8].W, rect.Bottom));
            DrawMiniGap(c, R(7), extraGapA, "AHEAD", alpha * Sm(eA), ref miniForD, ref miniTxtD);
            DrawDriverColumn(c, new SKRect(cols[8].L + cols[8].W - DW, rect.Top, cols[8].L + cols[8].W, rect.Bottom), extraAhead, false, alpha * Sm(eA), 6);
            c.Restore();
        }
    }

    void DrawMiniGap(SKCanvas c, SKRect rect, double gap, string label, float alpha, ref double txtFor, ref string txtCache)
    {
        miniDirP.Color = TextTertiary.WithAlpha((byte)(255 * alpha));   // #340: was 10
        TextTracked.Draw(c, miniDirP, label, rect.MidX, rect.Top + 92f, 0.30f * 13f);

        bool valid = double.IsFinite(gap) && gap >= 0;
        string txt = valid ? Gap2(gap, ref txtFor, ref txtCache) : "-";
        var bigP = miniBigP;
        bigP.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        bigP.TextSize = 58f;
        float w = TextBlobs.Measure(txt, bigP);
        if (w > rect.Width - 16f) bigP.TextSize *= (rect.Width - 16f) / w;    // never spill the column
        TextBlobs.Draw(c, txt, rect.MidX, rect.Top + 156f, bigP);
        // (no "SEC" label - Angelo 2026-10-18)
    }

    // ----- live evaluation -----

    (BattleState, Driver?, double?, SKColor, int, Driver?)
        EvaluateLive(RaceSnapshot snap)
    {
        if (snap.Phase != RacePhase.Broadcast || !LooksLikeRace(snap.SessionTypeName))
            return (BattleState.None, null, null, AccentGold, 0, snap.Player);
        // 2026-11-06 (#930): no "battle for P4 - under pressure" behind the pace car - fights start at the green
        if (snap.SessionState is < 4) return (BattleState.None, null, null, AccentGold, 0, snap.Player);

        var cand = BattleDetection.Compute(snap);
        if (cand.Player == null) return (BattleState.None, null, null, AccentGold, 0, snap.Player);
        // 2026-10-25 (Angelo: "in the pit only show a battle card to anyone in the pit with me"): while we're on pit
        // road the only fight worth a card is with a class car that's on pit road too
        if (cand.Player.IsInPit)
        {
            if (cand.Ahead != null && !cand.Ahead.IsInPit) cand = cand with { Ahead = null, GapAheadSeconds = null };
            if (cand.Behind != null && !cand.Behind.IsInPit) cand = cand with { Behind = null, GapBehindSeconds = null };
            if (cand.Ahead == null && cand.Behind == null) return (BattleState.None, null, null, AccentGold, 0, snap.Player);
        }

        // Opening-lap force: until the overall leader has completed lap 1, the
        // field is bunched and the start should read as battle mode regardless
        // of gaps. Pick the nearest same-class rival (ahead preferred when both
        // exist, closer-wins on ties) and report the true gap — no threshold
        // gating. If the player has no same-class peer at all there is no
        // sensible pair to show, so we fall through to None for that edge.
        if (!PresentationState.Lap1Complete && OpeningLapForceEnabled)
        {
            if (cand.Ahead != null || cand.Behind != null)
            {
                BattleState pickedF;
                Driver opponentF;
                double gapF;
                // Opening-lap gaps are commonly null: nobody has a completed
                // lap, so IntervalSeconds <= 0 and BattleDetection returns
                // null. Propagate that as NaN so the renderer shows a clean
                // placeholder rather than a synthetic sentinel value.
                if (cand.Ahead != null && cand.Behind != null)
                {
                    double? gaN = cand.GapAheadSeconds;
                    double? gbN = cand.GapBehindSeconds;
                    if (gaN.HasValue && (!gbN.HasValue || gaN.Value <= gbN.Value))
                    { pickedF = BattleState.Attack; opponentF = cand.Ahead;  gapF = gaN.Value; }
                    else if (gbN.HasValue)
                    { pickedF = BattleState.Defend; opponentF = cand.Behind; gapF = gbN.Value; }
                    else
                    { pickedF = BattleState.Attack; opponentF = cand.Ahead;  gapF = double.NaN; }
                }
                else if (cand.Ahead != null)
                {
                    pickedF = BattleState.Attack;
                    opponentF = cand.Ahead;
                    gapF = cand.GapAheadSeconds ?? double.NaN;
                }
                else
                {
                    pickedF = BattleState.Defend;
                    opponentF = cand.Behind!;
                    gapF = cand.GapBehindSeconds ?? double.NaN;
                }

                // 2026-10-31 (Angelo: the five-way card "hung for a while with the wrong information" after the cars
                // took off): the opening-lap force only holds while the fight is still close - once the real gap
                // (measured on the road on lap 1) is past the exit threshold, the normal 2.0 / 2.3 s rule decides
                if (!double.IsNaN(gapF) && gapF > ExitThresholdS
                    && !(cand.GapAheadSeconds is double fa && fa <= ExitThresholdS)
                    && !(cand.GapBehindSeconds is double fb && fb <= ExitThresholdS))
                    goto normalRule;
                int microF = pickedF == BattleState.Attack
                    ? opponentF.PositionInClass
                    : cand.Player.PositionInClass;
                var colorF = ResolveClassColor(opponentF);
                return (pickedF, opponentF, gapF, colorF, microF, cand.Player);
            }
            // Solo in class — no peer; let the 2.0s path return None below.
        }

        normalRule:
        // 2.0s rule with hysteresis: enter at gap <= 2.0s; once on, stay on
        // until no same-class neighbour is within 2.3s.
        double threshold = state == BattleState.None ? EnterThresholdS : ExitThresholdS;
        bool aheadActive  = cand.GapAheadSeconds  is double a && a <= threshold;
        bool behindActive = cand.GapBehindSeconds is double b && b <= threshold;

        BattleState picked = BattleState.None;
        Driver? opp = null;
        double? gap = null;
        int microPos = 0;

        if (aheadActive && behindActive)
        {
            double ga = cand.GapAheadSeconds!.Value;
            double gb = cand.GapBehindSeconds!.Value;
            if (ga <= gb) { picked = BattleState.Attack; opp = cand.Ahead; gap = ga; }
            else          { picked = BattleState.Defend; opp = cand.Behind; gap = gb; }
        }
        else if (aheadActive)
        {
            picked = BattleState.Attack; opp = cand.Ahead; gap = cand.GapAheadSeconds;
        }
        else if (behindActive)
        {
            picked = BattleState.Defend; opp = cand.Behind; gap = cand.GapBehindSeconds;
        }

        if (picked == BattleState.Attack)      microPos = opp?.PositionInClass ?? 0;
        else if (picked == BattleState.Defend) microPos = cand.Player.PositionInClass;

        var col = ResolveClassColor(opp ?? cand.Player);
        return (picked, opp, gap, col, microPos, cand.Player);
    }

    static bool LooksLikeRace(string? sessionTypeName)
        => !string.IsNullOrEmpty(sessionTypeName)
           && sessionTypeName.IndexOf("race", StringComparison.OrdinalIgnoreCase) >= 0;

    // ----- state machine -----

    void UpdateStateMachine(BattleState detected, Driver? opp, double? gap, double nowMs)
    {
        if (opp == null || !gap.HasValue) detected = BattleState.None;

        if (detected != BattleState.None)
        {
            // Reopen cooldown: refuse to re-show too soon after a hide.
            if (state == BattleState.None && hiddenAtMs > 0
                && (nowMs - hiddenAtMs) < ReopenCooldownMs)
                return;
            // 2026-10-25 (Angelo: "a buffer for cars approaching just barely in braking zones, a second or two before the
            // graphic comes in, so if it is only brief the battle mode doesn't trigger and go away quickly"): the fight
            // has to last ArmMs before the card comes in
            if (state == BattleState.None)
            {
                if (armSinceMs < 0) armSinceMs = nowMs;
                if (nowMs - armSinceMs < ArmMs) return;
            }

            // Same rival, sides flipped = a pass (2026-10-19): play the swap.
            if (state != BattleState.None && activeOpponentCarIdx == opp!.CarIdx && detected != state)
            {
                swapAtMs = nowMs;
                swapGained = detected == BattleState.Defend;
            }
            // Enter or switch.
            if (state == BattleState.None || activeOpponentCarIdx != opp!.CarIdx
                || detected != state)
            {
                state = detected;
                activeOpponentCarIdx = opp!.CarIdx;
                stateEnteredAtMs = nowMs;
                if (!isVisible)
                {
                    isVisible = true;
                    visibleSinceMs = nowMs;
                    leaveStartedAtMs = -1;
                    StorySound.PlayBroadcast("battle_in");
                    // #487: a highlight marker for the stream / replay (HighlightLog)
                    HighlightLog.Add("battle", lastPlayer != null ? $"Battle: #{lastPlayer.CarNumber} vs #{opp!.CarNumber}" : $"Battle with #{opp!.CarNumber}",
                                     lastPlayer != null ? new[] { lastPlayer.CarIdx, opp!.CarIdx } : new[] { opp!.CarIdx });
                }
                haveSmoothing = false;
                smoothedTension = 0;
            }
            firstNoneAtMs = 0;
        }
        else
        {
            armSinceMs = -1;
            if (state == BattleState.None) return;

            if (firstNoneAtMs == 0) firstNoneAtMs = nowMs;

            double dwell = nowMs - stateEnteredAtMs;
            double inGrace = nowMs - firstNoneAtMs;

            if (dwell >= MinDwellMs && inGrace >= DropoutGraceMs)
            {
                state = BattleState.None;
                activeOpponentCarIdx = -1;
                hiddenAtMs = nowMs;
                if (isVisible)
                {
                    isVisible = false;
                    leaveStartedAtMs = nowMs;
                }
                firstNoneAtMs = 0;
            }
        }
    }

    void UpdateSmoothing(double targetGap, double nowMs)
    {
        // Non-finite or negative inputs mean "no displayable gap yet" — typical
        // during the opening-lap force window before any car has completed a
        // lap. Park displayGap at NaN so the renderer shows a placeholder, and
        // arm the snap path so the next valid value lands cleanly.
        if (!double.IsFinite(targetGap) || targetGap < 0)
        {
            displayGap = double.NaN;
            prevGapForTension = double.NaN;
            smoothedTension = 0;
            haveSmoothing = false;
            lastSmoothMs = -1;
            return;
        }

        if (!haveSmoothing || lastSmoothMs < 0 || !double.IsFinite(displayGap))
        {
            gapHist.Clear(); trendPerLap = 0;
            displayGap = targetGap;
            prevGapForTension = targetGap;
            haveSmoothing = true;
            lastSmoothMs = nowMs;
            return;
        }

        double dtSeconds = Math.Max(0.001, (nowMs - lastSmoothMs) / 1000.0);
        lastSmoothMs = nowMs;
        // Number of legacy 60 fps steps this real-time slice represents.
        double framesAt60 = dtSeconds * 60.0;
        // 60 fps equivalent of the raw inter-tick delta — keeps TensionFullScale's
        // original "per-60-fps-frame" units valid regardless of actual fps.
        double frameScale = 1.0 / framesAt60;
        // Exponential per-step weight, scaled so total decay equals what the
        // legacy 60 fps would have produced over the same wall-clock interval.
        double gapLerpDt    = 1.0 - Math.Pow(1.0 - GapLerpRate,     framesAt60);
        double tensionEmaDt = 1.0 - Math.Pow(1.0 - TensionEmaAlpha, framesAt60);

        double diff = targetGap - displayGap;
        if (Math.Abs(diff) > GapConvergenceEps) displayGap += diff * gapLerpDt;
        else displayGap = targetGap;
        SampleTrend(targetGap, nowMs);

        double rawDelta = (targetGap - prevGapForTension) * frameScale;
        if (Math.Abs(rawDelta) < TensionDeadband) rawDelta = 0;
        smoothedTension += tensionEmaDt * (rawDelta - smoothedTension);
        prevGapForTension = targetGap;
    }

    // ----- animation -----

    float WipeProgress(double nowMs)
    {
        static float Out(double x) { x = Math.Clamp(x, 0, 1); return (float)(1 - Math.Pow(1 - x, 3)); }
        if (isVisible) return Out((nowMs - visibleSinceMs) / WipeInMs);
        if (leaveStartedAtMs < 0) return 0f;
        return 1f - Out((nowMs - leaveStartedAtMs) / ExitAnimMs);
    }

    float ComputeAlpha(double nowMs)
    {
        if (isVisible)
        {
            double t = nowMs - visibleSinceMs;
            return t < EnterAnimMs ? (float)EaseOut(t / EnterAnimMs) : 1f;
        }
        if (leaveStartedAtMs < 0) return 0f;
        double te = nowMs - leaveStartedAtMs;
        if (te >= ExitAnimMs) return 0f;
        return 1f - (float)EaseOut(te / ExitAnimMs);
    }

    float ComputeTranslateY(double nowMs)
    {
        if (isVisible)
        {
            double t = nowMs - visibleSinceMs;
            if (t < EnterAnimMs) return 14f * (1f - (float)EaseOut(t / EnterAnimMs));
            return 0f;
        }
        if (leaveStartedAtMs < 0) return 14f;
        double te = nowMs - leaveStartedAtMs;
        if (te >= ExitAnimMs) return -14f;
        return -14f * (float)EaseOut(te / ExitAnimMs);
    }

    static double EaseOut(double t) => BroadcastTiming.EaseOut(t);

    // ----- chrome -----

    void DrawCardChrome(SKCanvas c, SKRect rect, SKColor topBarColor, float alpha, double nowMs)
    {
        fillAaP.Color = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha));
        c.DrawRect(rect, fillAaP);
        Theme.Skin(c, rect, ThemeSurface.Battle, nowMs, alpha);   // #601: the broadcast theme (nothing on Default)

        // Pulse intensity for the gold glow under the top bar.
        float pulseMag = 1f;
        if (state != BattleState.None)
        {
            if (displayGap < WheelCloseGapS)
                pulseMag = 1f + 0.6f * (float)Math.Sin(2 * Math.PI * nowMs / WheelClosePulsePeriodMs);
            else if (displayGap < CriticalGapS)
                pulseMag = 1f + 0.4f * (float)Math.Sin(2 * Math.PI * nowMs / CriticalPulsePeriodMs);
        }
        byte glowAlpha = (byte)Math.Clamp(140f * pulseMag * alpha, 0f, 255f);

        cols2[0] = topBarColor.WithAlpha(glowAlpha); cols2[1] = topBarColor.WithAlpha(0);
        fillAaP.Shader = chromeGlow.Linear(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Top + 10f), cols2, Stops01);
        fillAaP.Color = SKColors.Black;
        c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 10f), fillAaP);
        fillAaP.Shader = null;

        fillP.Color = topBarColor.WithAlpha((byte)(255 * alpha));
        c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 1f), fillP);
    }

    void DrawColumnHairline(SKCanvas c, SKRect rect, float x, float alpha, int slot)
    {
        cols4[0] = BorderHairline.WithAlpha(0);
        cols4[1] = cols4[2] = BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha));
        cols4[3] = BorderHairline.WithAlpha(0);
        shaderP.Shader = hairline[slot].Linear(new SKPoint(0, rect.Top + 24), new SKPoint(0, rect.Bottom - 24), cols4, StopsHair);
        c.DrawRect(new SKRect(x, rect.Top + 24, x + 1f, rect.Bottom - 24), shaderP);
        shaderP.Shader = null;
    }

    void DrawMicrocopy(SKCanvas c, SKRect rect, int posNumber, Driver? player, float alpha)
    {
        // "UNDER PRESSURE" and "BATTLE FOR" moved to the tabs on top of the card (DrawTabs, 2026-10-24)
        if (swapAtMs >= 0 && lastDrawMs - swapAtMs < SwapBannerMs && player != null)
        {
            float bk = (float)Math.Clamp((lastDrawMs - swapAtMs) / 260.0, 0, 1);
            float fo = (float)Math.Clamp((SwapBannerMs - (lastDrawMs - swapAtMs)) / 400.0, 0, 1);
            int pp = player.PositionInClass > 0 ? player.PositionInClass : player.Position;
            if (pp != swapTxtFor || swapGained != swapTxtGained || swapTxt.Length == 0)
            { swapTxtFor = pp; swapTxtGained = swapGained; swapTxt = (swapGained ? "POSITION GAINED" : "POSITION LOST") + "  ·  P" + pp.ToString(CultureInfo.InvariantCulture); }
            string txt = swapTxt;
            var bp = swapP;   // #340: was 12
            bp.Color = (swapGained ? StatusGain : StatusLoss).WithAlpha((byte)(255 * alpha * Math.Min(bk, fo)));
            float sp = 0.34f * 14f, tw = MeasureTracked(bp, txt, sp);
            TextTracked.Draw(c, bp, txt, rect.MidX - tw / 2f, rect.Top + 27f - (1 - bk) * 8f, sp);
        }
        return;
    }

    // ----- tabs on top of the card (2026-10-24) -----
    double pressureSinceMs = -1, pressureOffMs = -1;
    const float TabH = 38f, TabGap = 5f;
    float pressX = float.NaN;   // where the UNDER PRESSURE tab sits (springs across to the car under pressure)
    float pressV, pressGoal = float.NaN; double pressLastMs = -1, windUpUntilMs = -1, thudMs = -1; bool moving; float thudPower, peakV; int pressSide;

    /// <summary>A heavy, under-damped spring with a wind-up: the tab pulls back, lunges, overshoots, settles.</summary>
    void PressSpring(float target, float pin, double nowMs)
    {
        if (float.IsNaN(pressX) || pin <= 0.01f) { pressX = pressGoal = target; pressV = 0; pressLastMs = nowMs; moving = false; return; }
        float dt = (float)Math.Clamp((nowMs - pressLastMs) / 1000.0, 0, 0.05); pressLastMs = nowMs;
        if (Math.Abs(target - pressGoal) > 40f)                     // a new car is under pressure: wind up first
        {
            windUpUntilMs = nowMs + 160; moving = true; peakV = 0;
        }
        pressGoal = target;
        float aim = target;
        if (nowMs < windUpUntilMs) aim = pressX + Math.Sign(pressX - target) * 26f;    // pull back against the move
        const float K = 34f, Zeta = 0.38f;                           // heavy and springy: ~1 s, one big overshoot
        float c2 = 2f * Zeta * MathF.Sqrt(K);
        pressV += (K * (aim - pressX) - c2 * pressV) * dt;
        pressX += pressV * dt;
        peakV = Math.Max(peakV, Math.Abs(pressV));
        // the impact: the moment it first passes over its new car (then the overshoot carries it on and back)
        int side = Math.Sign(pressX - target);
        if (moving && nowMs >= windUpUntilMs && peakV > 300f && side != 0 && pressSide != 0 && side != pressSide)
        {
            moving = false; thudMs = nowMs; thudPower = Math.Clamp(peakV / 1400f, 0.5f, 1f);
        }
        pressSide = side;
    }

    void DrawTabs(SKCanvas c, SKRect rect, int posNumber, Driver? player, SKColor accent, float alpha, double nowMs)
    {
        if (alpha <= 0.01f) return;
        // enter just after the card has opened: rise up from behind its top edge
        double since = nowMs - visibleSinceMs - 180;
        float rise = isVisible ? (float)EaseOut(Math.Clamp(since / 420.0, 0, 1)) : 1f;
        if (rise <= 0.001f) return;

        // ---------- BATTLE FOR P3 [class] (centre) ----------
        string classDisp = DisplayClass(player?.ClassName);
        var classLogo = LoadClassLogo(player?.ClassName);
        string head = "BATTLE FOR ";
        if (posNumber != posTabFor || posTab.Length == 0) { posTabFor = posNumber; posTab = posNumber > 0 ? $"P{posNumber}" : "P—"; }
        string posTxt = posTab;
        var hp = tabHeadP; hp.Color = TextTertiary.WithAlpha((byte)(255 * alpha));
        var pp = tabPosP; pp.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        float sp = 0.42f * 15f;
        float headW = MeasureTracked(hp, head, sp), posW = MeasureTracked(pp, posTxt, sp * 0.6f);
        float logoH = 17f, logoW = classLogo != null ? classLogo.Width * (logoH / classLogo.Height) : 0f;
        string suffix = "";
        if (classLogo == null && !string.IsNullOrEmpty(classDisp) && !sufCache.TryGetValue(classDisp, out suffix!)) sufCache[classDisp] = suffix = "  ·  " + classDisp;
        suffix ??= "";
        float sufW = suffix.Length > 0 ? MeasureTracked(hp, suffix, sp) : 0f;
        float content = headW + posW + (logoW > 0 ? 12f + logoW : sufW);
        float tw = content + 56f;

        // 2026-10-24 (Angelo, screenshot: UNDER PRESSURE hung off the card's right edge): work out the pressure
        // tab first; while it shows, the centre tab slides left so the PAIR sits centred within the card.
        // 2026-10-25 (Angelo: "in a three way battle the under pressure should show who is under pressure"):
        // the tab marks the car under pressure in the pairing on screen - us when defending, and the car ahead
        // (the right-hand column when we attack) once we're right on its gearbox. Hysteresis so it doesn't flicker.
        bool attackPressure = state == BattleState.Attack && isVisible && double.IsFinite(displayGap)
                              && (displayGap < 0.8 || (pressureSinceMs >= 0 && pressureOffMs < 0 && displayGap < 1.0));
        // three-way layout (BEHIND | gap | YOU | gap | AHEAD): someone is always under pressure - us when the car behind
        // is the closer one, the car ahead when we're the closer one - and the tab sits over THAT driver's column
        bool three = ThreeWayProgress(nowMs) > 0.5f && threeAhead != null && threeBehind != null && isVisible;
        bool threePressure = three && Math.Min(threeGapA, threeGapB) < 1.0;
        float threeTarget = float.NaN;
        if (three)
        {
            var tc = ThreeCols(rect);
            float youMid = tc[4].L + tc[4].W / 2f, aheadMid = tc[6].L + tc[6].W / 2f;
            threeTarget = threeGapB <= threeGapA ? youMid : aheadMid;
        }
        bool defend = (state == BattleState.Defend && isVisible) || attackPressure || threePressure;
        if (defend && pressureSinceMs < 0) { pressureSinceMs = nowMs; pressureOffMs = -1; }
        if (!defend && pressureSinceMs >= 0 && pressureOffMs < 0) pressureOffMs = nowMs;
        float pin = 0f, pout = 1f;
        if (pressureSinceMs >= 0)
        {
            pin = (float)EaseOut(Math.Clamp((nowMs - Math.Max(pressureSinceMs, visibleSinceMs + 380)) / 360.0, 0, 1));
            pout = pressureOffMs < 0 ? 1f : 1f - (float)Math.Clamp((nowMs - pressureOffMs) / 260.0, 0, 1);
            if (pressureOffMs >= 0 && pout <= 0) { pressureSinceMs = -1; pressureOffMs = -1; pin = 0f; }
        }
        // 2026-10-25 (Angelo: "the under pressure one should go above the row where battle for Px is so it can slide
        // across and the battle for one can stay in the middle"): BATTLE FOR stays centred; UNDER PRESSURE rides on
        // a row above it, sitting over the car under pressure and sliding across when that changes. Two cars: the car
        // under pressure is always the right-hand column (us defending, or the car ahead when we're on its gearbox).
        // 2026-10-25 (Angelo: "make the battle for and under pressure swap rows so battle for stays on top", and the
        // slide should "carry weight - I want to be scared when that moves"): UNDER PRESSURE sits on the card's edge,
        // BATTLE FOR lifts a row while it's out. When the pressure moves to another car the tab winds back, lunges
        // across on a heavy spring, leans into the move, overshoots and lands with a thud.
        float target = !float.IsNaN(threeTarget) ? threeTarget : rect.Left + (ColDriver + ColGap + rect.Width) / 2f;
        PressSpring(target, pin, nowMs);
        float lift = Smooth01(pin * pout) * (TabH + 6f);
        float cxTab = rect.MidX;
        var tab = new SKRect(cxTab - tw / 2f, rect.Top - TabGap - TabH - lift, cxTab + tw / 2f, rect.Top - TabGap - lift);
        DrawTabShape(c, rect, tab, accent, alpha, rise, nowMs, false);
        c.Save();
        c.ClipRect(new SKRect(tab.Left - 40, tab.Top - 40, tab.Right + 40, rect.Top - TabGap));   // rises out from behind the card edge
        c.Translate(0, (1 - rise) * (TabH + TabGap));
        float x = tab.MidX - content / 2f, base_ = tab.MidY + 15f * 0.36f;
        TextTracked.Draw(c, hp, head, x, base_, sp);
        TextTracked.Draw(c, pp, posTxt, x + headW, base_ + 0.5f, sp * 0.6f);
        if (classLogo != null)
        {
            float lx = x + headW + posW + 12f, lt = tab.MidY - logoH / 2f;
            logoP.ColorFilter = LogoFade((byte)(255 * alpha));
            BitmapImages.Draw(c, classLogo, new SKRect(lx, lt, lx + logoW, lt + logoH), logoP);
        }
        else if (suffix.Length > 0)
            TextTracked.Draw(c, hp, suffix, x + headW + posW, base_, sp);
        c.Restore();

        // ---------- UNDER PRESSURE · PRESENTED BY [partner] (right, only while defending) ----------
        if (pressureSinceMs < 0) return;
        float pa = alpha * pout * pin;
        if (pa <= 0.01f) return;
        var partner = Presenters.UnderPressure;
        const string label = "UNDER PRESSURE";
        var lpnt = pressLabP; lpnt.Color = SKColors.White.WithAlpha((byte)(255 * pa));
        var vp = pressVerbP; vp.Color = new SKColor(0xD8, 0xDE, 0xE6, (byte)(240 * pa));   // #340: was 12, C9D1DC @230
        float lsp = 0.06f * 21f, vsp = 0.24f * 10f;
        float labW = MeasureTracked(lpnt, label, lsp);
        string verb = "PRESENTED BY";
        float markH = 18f, markW = partner == Partner.None ? 0f : Presenters.MarkWidth(partner, markH);
        float verbW = markW > 0 ? MeasureTracked(vp, verb, vsp) : 0f;
        float chev = 26f;
        float pw = 22f + chev + labW + (markW > 0 ? 18f + verbW + 8f + markW : 0f) + 22f;
        float pl = Math.Clamp(pressX - pw / 2f, rect.Left + 8f, rect.Right - 8f - pw);
        float rowTop = rect.Top - TabGap - TabH;
        // the landing: a short drop-bounce after a big move
        double sinceThud = thudMs >= 0 ? nowMs - thudMs : double.MaxValue;
        float bounce = sinceThud < 420 ? (float)(Math.Sin(sinceThud / 420.0 * Math.PI * 3) * Math.Exp(-sinceThud / 140.0) * 4.0 * thudPower) : 0f;
        var pt = new SKRect(pl, rowTop + bounce, pl + pw, rowTop + TabH + bounce);
        // speed streaks behind it while it's flying
        float sp2 = Math.Abs(pressV);
        if (sp2 > 220f && pin > 0.9f)
            for (int g = 1; g <= 3; g++)
            {
                float gx = -Math.Sign(pressV) * g * Math.Min(60f, sp2 * 0.035f);
                fillAaP.Color = StatusLoss.WithAlpha((byte)(70 * pa * (1f - g / 4f) * Math.Min(1f, (sp2 - 220f) / 600f)));
                c.DrawRoundRect(new SKRect(pt.Left + gx, pt.Top + 3, pt.Right + gx, pt.Bottom - 3), 7f, 7f, fillAaP);
            }
        // rises out from behind the card edge as it arrives
        c.Save();
        c.ClipRect(new SKRect(rect.Left - 400, rect.Top - 400, rect.Right + 400, rect.Top - TabGap + 1));
        c.Translate(0, (1 - pin) * (TabH + TabGap));
        // leaning into the move: skew with speed, a touch of stretch along it
        float lean = Math.Clamp(-pressV * 0.00032f, -0.2f, 0.2f), stretch = 1f + Math.Min(0.06f, sp2 * 0.00004f);
        c.Translate(pt.MidX, pt.Bottom); c.Skew(lean, 0); c.Scale(stretch, 1f); c.Translate(-pt.MidX, -pt.Bottom);
        DrawTabShape(c, rect, pt, StatusLoss, pa, 1f, nowMs, true);
        if (sinceThud < 360)
        {
            // the thud: a red flash off the tab's edges
            float k = (float)(sinceThud / 360.0);
            strokeP.StrokeWidth = 2.5f * (1 - k); strokeP.StrokeCap = SKStrokeCap.Butt;
            strokeP.Color = StatusLoss.WithAlpha((byte)(220 * (1 - k) * pa * thudPower));
            c.DrawRoundRect(SKRect.Inflate(pt, 4 + 16 * k, 2 + 8 * k), 9f, 9f, strokeP);
        }
        // chevrons stepping upward (the car behind is pushing)
        float cx = pt.Left + 22f, cy = pt.MidY;
        for (int i = 0; i < 3; i++)
        {
            double ph = ((nowMs / 520.0) - i * 0.22) % 1.0; if (ph < 0) ph += 1;
            float ca = (float)(0.25 + 0.75 * Math.Max(0, Math.Sin(Math.PI * ph)));
            strokeP.StrokeWidth = 2.6f; strokeP.StrokeCap = SKStrokeCap.Round;
            strokeP.Color = StatusLoss.WithAlpha((byte)(255 * pa * ca));
            float yy = cy + 7f - i * 6f;
            var path = chevPath;
            path.Rewind();
            path.MoveTo(cx, yy); path.LineTo(cx + 7f, yy - 6f); path.LineTo(cx + 14f, yy);
            c.DrawPath(path, strokeP);
        }
        float lx2 = cx + chev;
        TextTracked.Draw(c, lpnt, label, lx2, pt.MidY + 21f * 0.35f, lsp);
        if (markW > 0)
        {
            float vx = lx2 + labW + 18f;
            fillP.Color = SKColors.White.WithAlpha((byte)(60 * pa));
            c.DrawRect(new SKRect(vx - 10f, pt.Top + 9f, vx - 9f, pt.Bottom - 9f), fillP);
            TextTracked.Draw(c, vp, verb, vx, pt.MidY + 12f * 0.36f, vsp);
            Presenters.DrawMark(c, partner, vx + verbW + 8f, pt.MidY, markH, pa);
        }
        c.Restore();
    }

    /// <summary>Glass tab with a coloured top edge, a soft bloom, a light sweep and (for pressure) a pulse.</summary>
    static float Smooth01(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3 - 2 * x); }

    /// <summary>Width of the UNDER PRESSURE tab - the same sums DrawTabs uses to lay it out.</summary>
    static float PressureTabWidth()
    {
        var partner = Presenters.UnderPressure;
        using var lpnt = new SKPaint { Typeface = Fonts.BarlowCondensed.ExtraBold, TextSize = 21f };
        using var vp = new SKPaint { Typeface = Fonts.Inter.Bold, TextSize = 13.5f };
        float labW = MeasureTracked(lpnt, "UNDER PRESSURE", 0.06f * 21f);
        float markW = partner == Partner.None ? 0f : Presenters.MarkWidth(partner, 18f);
        float verbW = markW > 0 ? MeasureTracked(vp, "PRESENTED BY", 0.24f * 10f) : 0f;
        return 22f + 26f + labW + (markW > 0 ? 18f + verbW + 8f + markW : 0f) + 22f;
    }

    void DrawTabShape(SKCanvas c, SKRect card, SKRect tab, SKColor accent, float alpha, float rise, double nowMs, bool pulse)
    {
        c.Save();
        c.ClipRect(new SKRect(tab.Left - 60, tab.Top - 60, tab.Right + 60, card.Top - TabGap));
        c.Translate(0, (1 - rise) * (TabH + TabGap));
        float r = 7f;
        var rr = tabRR;
        rr.SetRect(tab, r, r);
        int ti = pulse ? 1 : 0;
        fillAaP.Color = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha));
        c.DrawRoundRect(rr, fillAaP);
        // gap-driven pulse (closer car = faster, brighter)
        float pm = 1f;
        if (pulse)
        {
            double period = displayGap < WheelCloseGapS ? 520 : displayGap < CriticalGapS ? 800 : 1300;
            pm = 0.55f + 0.45f * (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * nowMs / period));
            haloP.Color = accent.WithAlpha((byte)(70 * alpha * pm));
            c.DrawRoundRect(SKRect.Inflate(tab, 1, 1), r, r, haloP);
            fillAaP.Color = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha));
            c.DrawRoundRect(rr, fillAaP);
        }
        // top edge in the accent colour + bloom under it
        cols2[0] = accent.WithAlpha((byte)(120 * alpha * pm)); cols2[1] = accent.WithAlpha(0);
        {
            var gp = fillAaP;
            gp.Color = SKColors.Black;
            gp.Shader = tabGlow[ti].Linear(new SKPoint(0, tab.Top), new SKPoint(0, tab.Top + 14f), cols2, Stops01);
            c.Save(); c.ClipRoundRect(rr, antialias: true);
            c.DrawRect(new SKRect(tab.Left, tab.Top, tab.Right, tab.Top + 14f), gp);
            gp.Shader = null;
            fillP.Color = accent.WithAlpha((byte)(255 * alpha));
            c.DrawRect(new SKRect(tab.Left, tab.Top, tab.Right, tab.Top + 2f), fillP);
            // light sweep every few seconds
            double per = pulse ? 2400 : 5200;
            double ph = (nowMs % per) / per;
            if (ph < 0.32)
            {
                float sx = tab.Left - 80f + (tab.Width + 160f) * (float)(ph / 0.32);
                cols3[0] = SKColors.White.WithAlpha(0); cols3[1] = SKColors.White.WithAlpha((byte)(38 * alpha)); cols3[2] = SKColors.White.WithAlpha(0);
                fillAaP.Color = SKColors.Black;
                fillAaP.Shader = tabSweep[ti].Linear(new SKPoint(sx - 60f, 0), new SKPoint(sx + 60f, 0), cols3, StopsSweep);
                c.DrawRect(tab, fillAaP);
                fillAaP.Shader = null;
            }
            c.Restore();
        }
        // little connector so the tab reads as part of the card
        fillP.Color = accent.WithAlpha((byte)(150 * alpha));
        c.DrawRect(new SKRect(tab.MidX - 18f, tab.Bottom, tab.MidX + 18f, tab.Bottom + TabGap), fillP);
        c.Restore();
    }

    void DrawMicrocopyUnused(SKCanvas c, SKRect rect, int posNumber, Driver? player, float alpha)
    {
        string classDisp = DisplayClass(player?.ClassName);
        var classLogo = LoadClassLogo(player?.ClassName);
        string head = "BATTLE FOR ";
        string posTxt = posNumber > 0 ? $"P{posNumber}" : "P—";
        string suffix = string.IsNullOrEmpty(classDisp) ? "" : "  ·  " + classDisp;

        using var headPaint = new SKPaint
        {
            Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
            IsAntialias = true,
            Typeface = Fonts.Inter.Bold,
            TextSize = 13f,   // #340: was 11
        };
        using var posPaint = new SKPaint
        {
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            IsAntialias = true,
            Typeface = Fonts.Inter.Bold,
            TextSize = 13f,
        };

        float spacing = 0.36f * 13f;
        float headW = MeasureTracked(headPaint, head, spacing);
        float posW  = MeasureTracked(posPaint, posTxt, spacing);
        float sufW  = MeasureTracked(headPaint, suffix, spacing);
        float baseY = rect.Top + 26f;
        if (classLogo != null)
        {
            const float logoH = 13f;
            float logoW = classLogo.Width * (logoH / classLogo.Height);
            const float gap = 10f;
            float total = headW + posW + gap + logoW;
            float baseX = rect.MidX - total / 2f;
            TextTracked.Draw(c, headPaint, head, baseX, baseY, spacing);
            TextTracked.Draw(c, posPaint, posTxt, baseX + headW, baseY, spacing);
            float lx = baseX + headW + posW + gap;
            float lt = baseY - (13f * 0.72f) / 2f - logoH / 2f;
            using var lp = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
                ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor(255, 255, 255, (byte)(255 * alpha)),
                    SKBlendMode.DstIn),
            };
            c.DrawBitmap(classLogo, new SKRect(lx, lt, lx + logoW, lt + logoH), lp);
        }
        else
        {
            float total = headW + posW + sufW;
            float baseX = rect.MidX - total / 2f;
            TextTracked.Draw(c, headPaint, head, baseX, baseY, spacing);
            TextTracked.Draw(c, posPaint, posTxt, baseX + headW, baseY, spacing);
            if (!string.IsNullOrEmpty(suffix))
                TextTracked.Draw(c, headPaint, suffix, baseX + headW + posW, baseY, spacing);
        }
    }

    static string DisplayClass(string? rawClass) => rawClass switch
    {
        "GTP"  => "GTP",
        "GTDP" => "GTD PRO",
        "GTD"  => "GTD",
        "GT3"  => "GT3",
        "GT4"  => "GT4",
        "GTE"  => "GTE",
        "LMP"  => "LMP",
        "LMP2" => "LMP2",
        "LMP3" => "LMP3",
        null or "" or "OTHER" => "",
        _      => rawClass.ToUpperInvariant(),
    };

    SKBitmap? LoadClassLogo(string? classLabel)
    {
        var slug = ClassLogo.Resolve(classLabel);
        if (slug == null) return null;
        if (logoCache.TryGetValue(slug, out var cached)) return cached;
        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "classes", slug + ".png");
            if (System.IO.File.Exists(path))
            {
                using var stream = System.IO.File.OpenRead(path);
                bmp = Assets.Decode(stream);
            }
        }
        catch { bmp = null; }
        logoCache[slug] = bmp;
        return bmp;
    }

    // ----- driver column -----

    void DrawDriverColumn(SKCanvas c, SKRect rect, Driver d, bool isPlayerSlot, float alpha, int slot)
    {
        if (isPlayerSlot)
        {
            cols2[0] = PlayerBlue.WithAlpha((byte)(38 * alpha)); cols2[1] = PlayerBlue.WithAlpha((byte)(10 * alpha));
            shaderP.Shader = playerBg[slot].Linear(new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom), cols2, Stops01);
            c.DrawRect(new SKRect(rect.Left, rect.Top + 1, rect.Right, rect.Bottom), shaderP);
            shaderP.Shader = null;
            fillP.Color = PlayerBlue.WithAlpha((byte)(255 * alpha));
            c.DrawRect(new SKRect(rect.Left, rect.Top + 1, rect.Left + 3, rect.Bottom), fillP);
        }

        string posT = SmallStrings.Num(d.PositionInClass > 0 ? d.PositionInClass : d.Position);

        // Watermark (2026-10-18, Angelo): the CAR MAKER's logo, big and faded,
        // bleeding off the panel's lower-right - replaces the huge position number
        // (the position is already shown as "POS"). Pre-scaled + cached once by
        // GridHeroBoard, so it costs one cheap image draw per frame.
        int maxW = (int)(rect.Width * 0.85f), maxH = (int)(rect.Height * 0.95f);
        // 2026-10-25 (Angelo): the driver's REAL car - iRacing's own renderer drawing their Trading Paints livery
        // (see LiveryArt). It sits low on the right of the column, under the name, like a team's car on a TV graphic;
        // a soft fade on its left keeps the name and numbers readable. Replaces the maker-logo watermark when present.
        var livery = LiveryArt.Get(d);
        if (livery != null)
        {
            var col = new SKRect(rect.Left, rect.Top + 1, rect.Right, rect.Bottom);
            // 2026-10-25 (Angelo: "the pos and car number and licence should be at the top of the card ... let the car
            // models breathe"): the top row is now one slim line, so the car gets the whole band down to the name
            float lw = rect.Width * 0.88f, lh = lw * livery.Height / livery.Width;
            float carMaxH = 112f;
            if (lh > carMaxH) { lh = carMaxH; lw = lh * livery.Width / livery.Height; }
            float cx = rect.MidX, by = rect.Top + 166f;
            var dst = new SKRect(cx - lw / 2f, by - lh, cx + lw / 2f, by);
            c.Save();
            c.ClipRect(col);
            // the car, its contact shadow and the fade on its left, faded together (a layer) - kept as a picture
            liveryPlates[slot].Draw(c, new LiveryKey(livery, dst, col), col, layer: true, layerAlpha: (byte)(235 * alpha));
            c.Restore();
        }
        var bd = livery != null ? null : logoBackdrops.Backdrop(d.CarBrand, maxW, maxH);
        if (bd != null)
        {
            float bw = bd.Width, bh = bd.Height;
            // Wide logos (width-limited, e.g. Cadillac) sit whole inside the panel,
            // centred low; tall ones bleed off the lower-right edge.
            bool wide = bw >= maxW - 2;
            float bx = wide ? rect.MidX - bw / 2f : rect.Right - bw * 0.78f;
            float by = wide ? rect.MidY - bh / 2f + 18f : rect.MidY - bh / 2f + 22f;
            c.Save();
            c.ClipRect(rect);
            // Fade by drawing into a layer and fading the LAYER (neither paint alpha
            // nor a colour filter dimmed the image reliably here).
            var dst = new SKRect(bx, by, bx + bw, by + bh);
            backdropPlates[slot].Draw(c, new BackdropKey(bd, dst), dst, layer: true, layerAlpha: (byte)(30 * alpha));
            c.Restore();   // clip
        }

        // The top row (2026-10-25): POS and the position on one slim line at the very top, the number + licence chips
        // opposite - the space below is the car's.
        float topY = rect.Top + 34f;
        float posX = rect.Left + 24f;
        {
            var label = label14P;   // #340: was 12
            label.Color = TextTertiary.WithAlpha((byte)(255 * alpha));
            TextTracked.Draw(c, label, "POS", posX, topY - 2f, 0.29f * 14f);
            posX += TextBlobs.Measure("POS", label) + 3 * 0.29f * 14f + 10f;
        }
        if (isPlayerSlot)
        {
            // the blurred glow behind our position: a picture while it holds still
            var gk = new GlowKey(posT, Fonts.CormorantGaramond.Bold, 40f, PlayerBlue.WithAlpha((byte)(110 * alpha)), GlowBlur6, posX, topY + 8f);
            posGlowPlate.Draw(c, gk, GlowBounds(posBigP, posT, posX, topY + 8f, 6f));
        }
        posBigP.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        TextBlobs.Draw(c, posT, posX, topY + 8f, posBigP);

        // Number + license chips top-right, level with the position.
        DrawChipsRow(c, d, rect.Right - 24f, topY - 4f, alpha);

        // Driver name (center).
        string name = Name(d);
        {
            var np = nameP;
            np.Color = (isPlayerSlot ? SKColors.White : TextPrimary).WithAlpha((byte)(255 * alpha));
            if (!string.IsNullOrEmpty(name))
            {
                string el = EllipsiseToWidth(name, np, rect.Width - 36f);
                TextBlobs.Draw(c, el, rect.MidX, rect.Top + 188f, np);
            }
        }

        // Bottom: BEST LAP label/value (left) + car model (right).
        float bestW = 0f;
        label14P.Color = TextTertiary.WithAlpha((byte)(255 * alpha));   // #340: was 12
        TextTracked.Draw(c, label14P, "BEST LAP", rect.Left + 24f, rect.Bottom - 42f, 0.30f * 14f);
        {
            var vp = lapP;
            vp.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
            string txt = d.BestLapTime > 0 ? Lap(d.BestLapTime) : "—";
            TextBlobs.Draw(c, txt, rect.Left + 24f, rect.Bottom - 13f, vp);
            bestW = TextBlobs.Measure(txt, vp);
        }
        // the car name gets whatever room the best lap leaves (narrow columns in a 4-5 car battle drop it)
        float carRoom = Math.Min(rect.Width / 2f, rect.Width - 48f - bestW - 16f);
        if (!string.IsNullOrEmpty(d.CarName) && carRoom >= 50f)
        {
            carP.Color = TextTertiary.WithAlpha((byte)(245 * alpha));   // #340: was 13 px @220
            string car = Upper(d.CarName);
            string el = EllipsiseToWidth(car, carP, carRoom);
            TextBlobs.Draw(c, el, rect.Right - 24f, rect.Bottom - 18f, carP);
        }
    }

    void DrawChipsRow(SKCanvas c, Driver d, float rightX, float midY, float alpha)
    {
        string number = string.IsNullOrEmpty(d.CarNumber) ? "—" : d.CarNumber;
        string lic = string.IsNullOrEmpty(d.LicenceString)
            ? "R" : SmallStrings.Char(char.ToUpperInvariant(d.LicenceString[0]));

        // License chip (outermost, right edge).
        var licColor = ParseHex(BroadcastPalette.License(lic));
        var licRect = new SKRect(rightX - 22, midY - 11, rightX, midY + 11);
        fillP.Color = licColor.WithAlpha((byte)(255 * alpha));
        c.DrawRect(licRect, fillP);
        fillP.Color = SKColors.White.WithAlpha((byte)(64 * alpha));
        c.DrawRect(new SKRect(licRect.Left, licRect.Top, licRect.Right, licRect.Top + 1), fillP);
        var t = chipP;
        t.GetFontMetrics(out var m);
        {
            t.Color = (lic == "C" ? new SKColor(0x0A, 0x0E, 0x14) : SKColors.White).WithAlpha((byte)(255 * alpha));
            float baseline = licRect.MidY - (m.Ascent + m.Descent) / 2f;
            TextBlobs.Draw(c, lic, licRect.MidX, baseline, t);
        }

        // Number chip (to the left of the license).
        {
            float textW = TextBlobs.Measure(number, t);
            float chipW = Math.Max(34f, textW + 14f);
            var numRect = new SKRect(licRect.Left - 6 - chipW, midY - 11, licRect.Left - 6, midY + 11);
            fillP.Color = SKColors.White.WithAlpha((byte)(255 * alpha));
            c.DrawRect(numRect, fillP);
            fillP.Color = SKColors.White.WithAlpha((byte)(178 * alpha));
            c.DrawRect(new SKRect(numRect.Left, numRect.Top, numRect.Right, numRect.Top + 1), fillP);
            t.Color = new SKColor(0x0A, 0x0E, 0x14, (byte)(242 * alpha));
            float baseline = numRect.MidY - (m.Ascent + m.Descent) / 2f;
            TextBlobs.Draw(c, number, numRect.MidX, baseline, t);
        }
    }

    // ----- gap column -----

    void DrawGapColumn(SKCanvas c, SKRect rect, float alpha, double nowMs)
    {
        string dirCombined = state == BattleState.Attack ? "↑  AHEAD" : "↓  BEHIND";

        dirP.Color = TextTertiary.WithAlpha((byte)(255 * alpha));   // #340: was 13
        TextTracked.Draw(c, dirP, dirCombined, rect.MidX, rect.Top + 70f, 0.38f * 14.5f);

        // Big gap value (92 px Cormorant). Wheel-close adds a warm halo.
        // displayGap can be NaN when the underlying gap isn't computable
        // yet (opening-lap force window, before any car has completed a
        // lap). Match the BEST LAP field's placeholder treatment: render an
        // em-dash with no SEC label and no halo, never a numeric fallback.
        bool gapValid = double.IsFinite(displayGap) && displayGap >= 0;
        double g = gapValid ? displayGap : 0;
        string gapText = gapValid
            ? Gap2(g, ref gapTxtFor, ref gapTxt)
            : "—";

        if (gapValid && g < WheelCloseGapS)
        {
            // the warm halo (a blur): a picture while the gap and the card hold still
            float hx = rect.MidX - TextBlobs.Measure(gapText, gapBigP) * 0.5f;
            var hk = new GlowKey(gapText, Fonts.CormorantGaramond.Bold, 92f, HaloGold.WithAlpha((byte)(140 * alpha)), GapHaloBlur8, hx, rect.Top + 160f);
            gapHaloPlate.Draw(c, hk, GlowBounds(gapBigP, gapText, hx, rect.Top + 160f, 8f));
        }
        gapBigP.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        TextBlobs.Draw(c, gapText, rect.MidX, rect.Top + 160f, gapBigP);

        // (2026-10-18, Angelo: no "SEC" label - the gap is obviously seconds.)

        // Trend block.
        var trend = ResolveTrend();
        string combined = trend.Label;   // (the arrow is part of it: no string per frame)
        {
            var lp = trendP;
            lp.Color = trend.Color.WithAlpha((byte)(255 * alpha));
            float spacing = 0.10f * 15f;
            float w = MeasureTracked(lp, combined, spacing);
            TextTracked.Draw(c, lp, combined, rect.MidX - w / 2f, rect.Top + 208f, spacing);
        }

        DrawTensionMeter(c, rect, alpha, nowMs);
    }

    // ----- trend (2026-10-19): gap change over the last ~10 s, as seconds per lap -----
    // The old per-frame delta almost never cleared its deadband at real closing rates
    // (tenths per lap), so the card sat on STEADY. A least-squares slope over a
    // rolling window, scaled by the lap time, reads the way commentators say it.
    readonly Queue<(double T, double G)> gapHist = new();
    double trendPerLap;
    double lapForTrend = 100;
    const double TrendWindowS = 10, TrendDeadPerLap = 0.05;

    void SampleTrend(double gap, double nowMs)
    {
        double t = nowMs / 1000.0;
        if (gapHist.Count > 0 && t - gapHist.Last().T < 0.1) return;
        gapHist.Enqueue((t, gap));
        while (gapHist.Count > 0 && t - gapHist.Peek().T > TrendWindowS) gapHist.Dequeue();
        if (gapHist.Count < 12 || t - gapHist.Peek().T < 3) { trendPerLap = 0; return; }
        double n = gapHist.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (x, y) in gapHist) { double xx = x - t; sx += xx; sy += y; sxx += xx * xx; sxy += xx * y; }
        double den = n * sxx - sx * sx;
        double slope = Math.Abs(den) < 1e-9 ? 0 : (n * sxy - sx * sy) / den;   // seconds of gap per second
        trendPerLap = slope * lapForTrend;
    }

    (string Arrow, string Label, SKColor Color) ResolveTrend()
    {
        double d = trendPerLap;
        // (Label is the whole line, arrow included - "▼  CLOSING  0.3s / LAP" - made once per value)
        if (Math.Abs(d) <= TrendDeadPerLap) return ("▶", "▶  HOLDING STEADY", TextTertiary);
        Span<char> buf = stackalloc char[24];
        Math.Abs(d).TryFormat(buf, out int n, "0.0", CultureInfo.InvariantCulture);
        bool closing = d < 0;
        int kind = state == BattleState.Defend ? (closing ? 0 : 1) : (closing ? 2 : 3);
        if (trendRate[kind] is not { } r0 || !r0.AsSpan().SequenceEqual(buf[..n]))
        {
            string rate = new string(buf[..n]);
            trendRate[kind] = rate;
            trendLine[kind] = kind switch
            {
                0 => "▼  UNDER PRESSURE  " + rate + "s / LAP",
                1 => "▲  PULLING AWAY  " + rate + "s / LAP",
                2 => "▼  CLOSING  " + rate + "s / LAP",
                _ => "▲  DROPPING BACK  " + rate + "s / LAP",
            };
        }
        string line = trendLine[kind]!;
        return kind switch
        {
            0 => ("▼", line, StatusLoss), 1 => ("▲", line, StatusGain), 2 => ("▼", line, StatusGain), _ => ("▲", line, StatusLoss),
        };
    }

    /// <summary>The IMSA red arrow as a gap meter (2026-10-19): it runs from the left
    /// driver toward the right one; the closer the cars, the further it reaches.</summary>
    void DrawTensionMeter(SKCanvas c, SKRect rect, float alpha, double nowMs)
    {
        float w = rect.Width * 0.80f, x0 = rect.MidX - w / 2f, x1 = x0 + w, y = rect.Top + 240f;
        {
            var tr = tickP; tr.Color = new SKColor(255, 255, 255, (byte)(28 * alpha));
            c.DrawLine(x0, y, x1, y, tr);
            for (int i = 0; i <= 4; i++) { float tx = x0 + w * i / 4f; c.DrawLine(tx, y - (i % 2 == 0 ? 4 : 2.5f), tx, y + (i % 2 == 0 ? 4 : 2.5f), tr); }
        }
        {
            var lab = meterLabP; lab.Color = TextTertiary.WithAlpha((byte)(235 * alpha));   // #340: was 11 @200
            lab.TextAlign = SKTextAlign.Left;  TextBlobs.Draw(c, "2.0", x0, y + 19f, lab);
            lab.TextAlign = SKTextAlign.Right; TextBlobs.Draw(c, "0.0", x1, y + 19f, lab);
        }
        bool ok = double.IsFinite(displayGap) && displayGap >= 0;
        float k = ok ? (float)Math.Clamp(1.0 - displayGap / 2.0, 0.02, 1.0) : 0.02f;
        float hx = x0 + w * k;
        var red = new SKColor(0xD8, 0x1E, 0x2A);
        // tapered ribbon, hairline at the tail, full width at the head
        {
            var rib = ribPath; rib.Rewind();
            rib.MoveTo(x0, y - 0.4f); rib.LineTo(hx - 6f, y - 2.6f); rib.LineTo(hx - 6f, y + 2.6f); rib.LineTo(x0, y + 0.4f); rib.Close();
            cols3[0] = red.WithAlpha(0); cols3[1] = red.WithAlpha((byte)(200 * alpha)); cols3[2] = red.WithAlpha((byte)(255 * alpha));
            fillAaP.Color = SKColors.Black;
            fillAaP.Shader = tensionRib.Linear(new SKPoint(x0, 0), new SKPoint(hx, 0), cols3, StopsRib);
            c.DrawPath(rib, fillAaP);
            fillAaP.Shader = null;
        }
        {
            var head = headPath; head.Rewind();
            head.MoveTo(hx + 8f, y); head.LineTo(hx - 7f, y - 7f); head.LineTo(hx - 4f, y); head.LineTo(hx - 7f, y + 7f); head.Close();
            fillAaP.Color = red.WithAlpha((byte)(255 * alpha));
            c.DrawPath(head, fillAaP);
        }
    }

    void DrawTensionMeterLegacy(SKCanvas c, SKRect rect, float alpha, double nowMs)
    {
        float meterW = rect.Width * 0.78f;
        float meterH = 8f;
        float midX = rect.MidX;
        float meterTop = rect.Top + 236f;
        var meterRect = new SKRect(midX - meterW / 2f, meterTop, midX + meterW / 2f, meterTop + meterH);

        using (var bg = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(10 * alpha)) })
            c.DrawRect(meterRect, bg);
        using (var border = new SKPaint
        {
            Color = new SKColor(255, 255, 255, (byte)(20 * alpha)),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
        })
            c.DrawRect(meterRect, border);

        double mag = Math.Min(1.0, Math.Abs(smoothedTension) / TensionFullScale);

        if (smoothedTension < 0 && mag > 0.001)
        {
            float half = meterRect.Width / 2f;
            float fillW = (float)(half * mag);
            var fillRect = new SKRect(midX - fillW, meterRect.Top, midX, meterRect.Bottom);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(midX, 0), new SKPoint(midX - fillW, 0),
                new[]
                {
                    StatusGain.WithAlpha((byte)(255 * alpha)),
                    StatusGain.WithAlpha((byte)(140 * alpha)),
                    StatusGain.WithAlpha(0),
                },
                new[] { 0f, 0.65f, 1f }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            c.DrawRect(fillRect, paint);
        }
        else if (smoothedTension > 0 && mag > 0.001)
        {
            float half = meterRect.Width / 2f;
            float fillW = (float)(half * mag);
            var fillRect = new SKRect(midX, meterRect.Top, midX + fillW, meterRect.Bottom);
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(midX, 0), new SKPoint(midX + fillW, 0),
                new[]
                {
                    StatusLoss.WithAlpha((byte)(255 * alpha)),
                    StatusLoss.WithAlpha((byte)(140 * alpha)),
                    StatusLoss.WithAlpha(0),
                },
                new[] { 0f, 0.65f, 1f }, SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            c.DrawRect(fillRect, paint);
        }

        // Center marker (gold), gently pulsing.
        float markerPulse = 0.78f + 0.22f * (float)Math.Sin(2 * Math.PI * nowMs / 2400.0);
        byte markerAlpha = (byte)Math.Clamp(255f * alpha * markerPulse, 0f, 255f);
        using (var marker = new SKPaint { Color = AccentGold.WithAlpha(markerAlpha) })
            c.DrawRect(new SKRect(midX - 1f, meterRect.Top - 4f, midX + 1f, meterRect.Bottom + 4f), marker);

        using (var lab = new SKPaint
        {
            Color = TextTertiary.WithAlpha((byte)(240 * alpha)),   // #340: was 10 px @220
            IsAntialias = true,
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 13f,
            TextAlign = SKTextAlign.Center,
        })
        {
            TextTracked.Draw(c, lab, "TENSION", midX, meterRect.Bottom + 17f, 0.36f * 13f);
        }
    }

    // ----- helpers -----

    static SKColor ResolveClassColor(Driver? d)
    {
        if (d == null) return AccentGold;
        string col = string.IsNullOrEmpty(d.ClassColor)
            ? BroadcastPalette.Class(d.ClassName) : d.ClassColor;
        return ParseHex(col);
    }

    static float MeasureTracked(SKPaint paint, string text, float spacing)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        return TextBlobs.TrackedWidth(text, paint, spacing);   // same sums (glyph widths + spacing), cached with the blob
    }

    static string EllipsiseToWidth(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text) || paint.MeasureText(text) <= maxWidth) return text;
        const string ellipsis = "…";
        float ellipsisW = paint.MeasureText(ellipsis);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = paint.MeasureText(text[..mid]) + ellipsisW;
            if (w <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo] + ellipsis;
    }

    static SKColor ParseHex(string hex, byte alpha = 255)
    {
        var s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) return new SKColor(255, 255, 255, alpha);
        byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor(r, g, b, alpha);
    }

    public void Dispose()
    {
        logoBackdrops.Dispose();
        foreach (var b in logoCache.Values) b?.Dispose();
        posGlowPlate.Dispose(); gapHaloPlate.Dispose();
        foreach (var pl in liveryPlates) pl.Dispose();
        foreach (var pl in backdropPlates) pl.Dispose();
        logoCache.Clear();
    }
}

/// <summary>
/// A scripted source that drives the battle card without iRacing. Cycles
/// Attack → Idle → Defend → Idle every ~17 s with a gap that drifts between
/// ~0.18 and ~0.85 s so the critical/wheel-close visuals get exercised.
/// Internal because the <see cref="BattleState"/> tuple it returns leaks
/// the renderer's private state enum across an asm boundary.
/// </summary>
internal sealed class BattleTestLoop
{
    // Synthetic drivers — built once.
    static readonly Driver SynthPlayer = new(
        CarIdx: 0, Name: "DRIVER", AbbrevName: "DRIVER", Initials: "DR",
        CarNumber: "27", CarName: "FERRARI 296 GT3", CarBrand: "Ferrari",
        ClassName: "GT3", ClassColor: "#FFDA59",
        LicenceString: "A 4.20", LicenceColor: "#006EFF",
        Position: 5, PositionInClass: 3,
        CurrentLap: 12, LapDistPct: 0.42f, LapsCompleted: 11, LapDifference: 0,
        BestLapTime: 113.452f, LastLapTime: 113.890f,
        GapToLeaderSeconds: 4.21, IntervalSeconds: 0.42,
        IsInPit: false, IsConnected: true, IsPaceCar: false, IsSpectator: false,
        IRating: 4250);
    static readonly Driver SynthAhead = new(
        CarIdx: 1, Name: "HARTWELL", AbbrevName: "HARTWELL", Initials: "JH",
        CarNumber: "7", CarName: "PORSCHE 911 GT3 R", CarBrand: "Porsche",
        ClassName: "GT3", ClassColor: "#FFDA59",
        LicenceString: "A 5.05", LicenceColor: "#006EFF",
        Position: 4, PositionInClass: 2,
        CurrentLap: 12, LapDistPct: 0.44f, LapsCompleted: 11, LapDifference: 0,
        BestLapTime: 113.110f, LastLapTime: 113.402f,
        GapToLeaderSeconds: 3.10, IntervalSeconds: 0.32,
        IsInPit: false, IsConnected: true, IsPaceCar: false, IsSpectator: false,
        IRating: 5500);
    static readonly Driver SynthBehind = new(
        CarIdx: 2, Name: "VANCE", AbbrevName: "VANCE", Initials: "RV",
        CarNumber: "33", CarName: "BMW M4 GT3", CarBrand: "BMW",
        ClassName: "GT3", ClassColor: "#FFDA59",
        LicenceString: "A 4.85", LicenceColor: "#006EFF",
        Position: 6, PositionInClass: 4,
        CurrentLap: 12, LapDistPct: 0.40f, LapsCompleted: 11, LapDifference: 0,
        BestLapTime: 113.298f, LastLapTime: 113.650f,
        GapToLeaderSeconds: 4.95, IntervalSeconds: 0.55,
        IsInPit: false, IsConnected: true, IsPaceCar: false, IsSpectator: false,
        IRating: 4800);

    const double CycleMs = 17_000;
    const double PhaseAttackEnd = 6_500;
    const double PhaseIdle1End  = 8_500;
    const double PhaseDefendEnd = 15_000;
    // 15_000 → 17_000 is the second idle.

    public (BattleState, Driver?, double?, SKColor, int, Driver?)
        Evaluate(RaceSnapshot snapshot, double nowMs)
    {
        // We bypass the renderer's private enum by going through its translator.
        // To preserve encapsulation we return BattleState via
        // the same tuple shape; renderer.TestEvaluate handles the conversion.
        // Practical path: just return the tuple it expects with our local enum.
        double cyc = nowMs % CycleMs;
        BattleState bs;
        Driver? opp;
        double? gap;
        int micro;
        SKColor color = ParseHex("#FFDA59"); // GT3 gold

        if (cyc < PhaseAttackEnd)
        {
            bs = BattleState.Attack;
            opp = SynthAhead;
            gap = LerpGap(cyc, 0, PhaseAttackEnd, 0.92, 0.22);
            micro = SynthAhead.PositionInClass;
        }
        else if (cyc < PhaseIdle1End)
        {
            bs = BattleState.None;
            opp = null; gap = null; micro = 0;
        }
        else if (cyc < PhaseDefendEnd)
        {
            bs = BattleState.Defend;
            opp = SynthBehind;
            // 0.85 → 0.18 → 0.85 — closes then opens to exercise wheel-close + recovery
            double t = (cyc - PhaseIdle1End) / (PhaseDefendEnd - PhaseIdle1End);
            gap = t < 0.5
                ? 0.85 - (0.85 - 0.18) * (t / 0.5)
                : 0.18 + (0.85 - 0.18) * ((t - 0.5) / 0.5);
            micro = SynthPlayer.PositionInClass;
        }
        else
        {
            bs = BattleState.None;
            opp = null; gap = null; micro = 0;
        }

        return (bs, opp, gap, color, micro, SynthPlayer);
    }

    static double LerpGap(double cyc, double t0, double t1, double from, double to)
    {
        double t = Math.Clamp((cyc - t0) / (t1 - t0), 0, 1);
        return from + (to - from) * t;
    }

    static SKColor ParseHex(string hex)
    {
        var s = hex.AsSpan();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length != 6) return SKColors.White;
        byte r = byte.Parse(s[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte g = byte.Parse(s[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte b = byte.Parse(s[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new SKColor(r, g, b);
    }
}
