using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// The Standings Ticker — a horizontally-scrolling broadcast strip showing one
/// class at a time, rotated periodically. Lives directly under the session bar
/// and is visible only when a battle is active (battle-mode choreography: the
/// timing tower yields, the ticker takes over the standings lane). Reuses the
/// snapshot's per-class grouping (Port 02), <see cref="BroadcastPalette"/>,
/// and the <see cref="Fonts"/> bundle.
/// </summary>
public sealed class StandingsTickerRenderer : IDisposable
{
    public const float BarWidth = 976f;
    public const float BarHeight = 44f;
    public const float ClassBoxWidth = 150f;

    // Per-chip layout. Tabular-feel by design — every chip is one width so the
    // scroll loops cleanly.
    const float ChipPad = 20f;
    const float ChipGap = 12f;
    const float ChipHeight = BarHeight;

    // Entry typography sizes. Scaled to fill ~80% of the 44 px strip's vertical
    // height (line-box ≈ 1.20 × size for Inter Bold ⇒ 28 px size = 33.6 px box
    // = 76% of 44; size 26 = 71%). Pos is the broadcast-prominent number and
    // sits a notch above the rest of the row's hierarchy.
    const float PosTextSize  = 28f;
    const float NumTextSize  = 24f;
    const float NameTextSize = 26f;
    const float GapTextSize  = 24f;

    // Animation constants.
    const double FadeAnimMs = 320;          // overlay fade in/out on visibility flip
    const double ClassCycleMs = 12_000;     // rotate the displayed class every N ms
    const double ClassFadeAnimMs = 380;     // brief fade between classes during the rotation
    const double ScrollPxPerSec = 60;       // scroll speed (matches legacy)
    const int MinChipsToScroll = 4;         // below this we just centre the chips, no scroll

    static readonly SKColor BgGlass        = ParseHex(BroadcastPalette.TowerGlassBase, 209);
    static readonly SKColor BorderHairline = ParseHex(BroadcastPalette.BorderHairline, 120);
    static readonly SKColor TextPrimary    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextSecondary  = ParseHex(BroadcastPalette.TextSecondary);
    static readonly SKColor TextTertiary   = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor AccentGold     = ParseHex(BroadcastPalette.Leader);
    static readonly SKColor PitAmber       = ParseHex(BroadcastPalette.PitAmber);

    readonly SKPaint glassPaint;
    readonly SKPaint hairlinePaint;

    // PRE-RACE STARTING ORDER (Angelo 2026-10-18): from the end of the broadcast intro
    // until the green flag the strip rolls the grid - lowest class first, each class
    // in grid order (pos, #, maker logo, name, qualifying time), one full pass per class.
    public static bool PreRaceGridMode =>
        PresentationState.Phase == RacePhase.PreRace && StartingGridRenderer.MusicEndMs >= 0;   // arrives with the bar when the music ends
    public static bool ShouldShow => PresentationState.TickerVisible || PreRaceGridMode;
    const double PreScrollPxPerSec = 95;   // a touch quicker so each class gets its turn
    const double PreHoldMs = 1600;         // P1 sits still for a beat before rolling
    bool wasPre;
    bool preAdvanceClass;
    double preHoldUntilMs;
    double preEndAtMs = -1;
    readonly Dictionary<string, SKBitmap?> makerCache = new(StringComparer.OrdinalIgnoreCase);

    // Visibility (mirrors the FeaturedCard yield pattern but inverted — show on
    // battle, hide otherwise).
    bool wasVisible;
    double visFlipAtMs = -1;

    // Class rotation state.
    int currentClassIdx;
    double classShownAtMs;
    double classFadeStartedAtMs = -1;
    string lastClassName = string.Empty;
    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);
    // Logo trailing-edge slant (dx/dy), measured from the IMSA wordmarks (~27 deg forward lean).
    const float LogoSlantDxDy = 0.509f;
    float classBoxW = ClassBoxWidth;
    float chipClipTopX = ClassBoxWidth;
    float chipClipBotX = ClassBoxWidth;

    // Continuous scroll accumulator (delta-based). The previous
    // `nowMs % oneCopyWidth` approach was the source of the "seizurey"
    // jumpiness: when chip widths changed by a single px (gap "+0.3" vs
    // "+0.4"), oneCopyWidth changed and the modulo'd value jumped non-
    // monotonically. A free-running accumulator advanced by `delta_ms *
    // speed` is monotonic by construction; we wrap once per frame using the
    // current oneCopyWidth.
    double scrollPx;
    double lastScrollMs = -1;

    // Cached chip layout. The chip widths are pure functions of (typeface,
    // size, letter-spacing, text) — none of which change between frames for
    // the same driver. Re-measuring 25+ glyph widths × 12+ chips at 60 fps
    // was the dominant per-frame cost in the ticker; cache and only rebuild
    // when the class rotates or a driver enters/leaves the field. Pit dim
    // and leader flag are layered at draw time so a pit change does not
    // invalidate the cache.
    int cachedClassIdx = -1;
    int[] cachedCarIdxByIndex = Array.Empty<int>();
    ChipLayout[] cachedChips = Array.Empty<ChipLayout>();
    float cachedOneCopyWidth;

    // 2026-10-30 (#604, efficiency): the paints the ticker drew with were made and thrown away every frame (five text
    // paints, a divider paint per visible chip, the clip path, ...). A text paint with a typeface also carries a small
    // keep-alive table, so that was ~25 KB of garbage a frame. Made once now; only their colour changes per frame.
    // Same properties, same pixels (the race_frame bench's pixel hashes are unchanged).
    readonly SKPaint cardGlassPaint = new() { IsAntialias = true };
    readonly SKPaint cardBottomPaint = new();
    readonly SKPaint shaderPaint = new() { IsAntialias = true };          // gradients: Shader set for one draw
    readonly SKPaint posPaintC = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = PosTextSize };
    readonly SKPaint numPaintC = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = NumTextSize };
    readonly SKPaint namePaintC = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = NameTextSize };
    readonly SKPaint gapPaintC = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = GapTextSize };
    readonly SKPaint leaderGapPaintC = new() { IsAntialias = true, Typeface = Fonts.Inter.Bold, TextSize = GapTextSize };
    readonly SKPaint dividerPaintC = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    readonly SKPaint pitPaintC = new();
    readonly SKPath chipClipC = new();

    void FillShader(SKCanvas c, SKRect r, SKShader shader)
    {
        shaderPaint.Shader = shader;
        c.DrawRect(r, shaderPaint);
        shaderPaint.Shader = null;
    }

    public StandingsTickerRenderer()
    {
        glassPaint    = new SKPaint { IsAntialias = true, Color = BgGlass };
        hairlinePaint = new SKPaint { IsAntialias = false, Color = BorderHairline, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    }

    /// <summary>
    /// Draws the ticker at <paramref name="originX"/>,<paramref name="originY"/>.
    /// Returns true if anything painted (the window can skip work when fully
    /// hidden but we let the per-frame fade do the heavy lifting). The
    /// <paramref name="barWidth"/> argument lets the window stretch the strip
    /// to the full primary-monitor width during battle mode.
    /// </summary>
    public bool Draw(SKCanvas canvas, float originX, float originY, float barWidth,
                     RaceSnapshot snapshot, double nowMs)
    {
        // Pre-race (Angelo 2026-10-19): the session bar lands FIRST (its entrance runs 600 ms),
        // then the strip slides down out from underneath it.
        if (PreRaceGridMode)
        {
            double since = nowMs - StartingGridRenderer.MusicEndMs - PreSlideDelayMs;
            if (since <= 0) return false;
            float k = (float)Math.Clamp(since / PreSlideMs, 0, 1);
            k = 1 - (1 - k) * (1 - k) * (1 - k);
            if (k < 1f)
            {
                canvas.Save();
                canvas.ClipRect(new SKRect(originX, originY, originX + barWidth, originY + BarHeight + 6));
                canvas.Translate(0, -(1 - k) * (BarHeight + 4));
                bool drew = DrawCore(canvas, originX, originY, barWidth, snapshot, nowMs);
                canvas.Restore();
                return drew;
            }
        }
        return DrawCore(canvas, originX, originY, barWidth, snapshot, nowMs);
    }
    public const double PreSlideDelayMs = 650, PreSlideMs = 520;

    bool DrawCore(SKCanvas canvas, float originX, float originY, float barWidth,
                  RaceSnapshot snapshot, double nowMs)
    {
        float alpha = ComputeVisibilityAlpha(nowMs);
        if (alpha <= 0.01f) return false;

        var classes = snapshot.ClassLeaderboard;
        if (classes == null || classes.Count == 0) return false;

        bool pre = PreRaceGridMode;
        if (pre != wasPre)
        {
            // entering / leaving the starting-order roll: start fresh from the first class, P1
            wasPre = pre;
            currentClassIdx = 0; classShownAtMs = nowMs; scrollPx = 0; lastScrollMs = -1;
            preAdvanceClass = false; preHoldUntilMs = nowMs + PreHoldMs; cachedClassIdx = -1;
        }

        // Pick the class to show this frame, rotating every ClassCycleMs
        // (pre-race: after each class has scrolled all the way through).
        if (classShownAtMs == 0) classShownAtMs = nowMs;
        bool rotate = pre ? preAdvanceClass : (nowMs - classShownAtMs) >= ClassCycleMs;
        if (rotate && (classes.Count > 1 || pre))   // pre with one class: just roll it again
        {
            currentClassIdx = (currentClassIdx + 1) % classes.Count;
            classShownAtMs = nowMs;
            classFadeStartedAtMs = nowMs;
            if (pre) { scrollPx = 0; lastScrollMs = -1; preHoldUntilMs = nowMs + PreHoldMs; cachedClassIdx = -1; }
        }
        preAdvanceClass = false;
        currentClassIdx = Math.Clamp(currentClassIdx, 0, classes.Count - 1);
        // pre-race goes lowest class -> top class (the leaderboard lists the top class first)
        var cls = pre ? classes[classes.Count - 1 - currentClassIdx] : classes[currentClassIdx];

        if (cls.Name != lastClassName)
        {
            if (lastClassName.Length > 0) classFadeStartedAtMs = nowMs;
            lastClassName = cls.Name;
        }

        // Class fade — brief opacity dip on class swap so the change reads as a
        // deliberate transition rather than a content shift.
        float classAlpha = ComputeClassFadeAlpha(nowMs);
        float overallAlpha = alpha * classAlpha;

        canvas.Save();
        canvas.Translate(originX, originY);

        var rect = new SKRect(0, 0, barWidth, BarHeight);
        var classColor = ParseHex(string.IsNullOrEmpty(cls.Color)
            ? BroadcastPalette.Class(cls.Name)
            : cls.Color);

        DrawCard(canvas, rect, classColor, overallAlpha);
        DrawClassBox(canvas, classColor, cls.Name, overallAlpha);
        DrawChips(canvas, cls, classColor, overallAlpha, barWidth, nowMs);

        canvas.Restore();
        return true;
    }

    // ----- visibility -----

    float ComputeVisibilityAlpha(double nowMs)
    {
        bool visible = ShouldShow;
        if (visible != wasVisible)
        {
            visFlipAtMs = nowMs;
            wasVisible = visible;
        }
        if (visFlipAtMs < 0) return visible ? 1f : 0f;
        double t = (nowMs - visFlipAtMs) / FadeAnimMs;
        float prog = (float)BroadcastTiming.Smoothstep(t);
        return visible ? prog : 1f - prog;
    }

    float ComputeClassFadeAlpha(double nowMs)
    {
        if (classFadeStartedAtMs < 0) return 1f;
        double t = (nowMs - classFadeStartedAtMs) / ClassFadeAnimMs;
        if (t >= 1.0) return 1f;
        // Quick dip and rise: opacity 1 → 0.35 at t=0.35 → 1 at t=1.
        float prog = (float)BroadcastTiming.Smoothstep(t);
        return 0.35f + 0.65f * prog;
    }

    // ----- card chrome -----

    void DrawCard(SKCanvas c, SKRect rect, SKColor classColor, float alpha)
    {
        // Deep-glass base scaled by alpha.
        cardGlassPaint.Color = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha));
        c.DrawRect(rect, cardGlassPaint);
        Theme.SkinNow(c, rect, ThemeSurface.Ticker, alpha);   // #601: the broadcast theme (nothing on Default)

        // Top gold/class hairline fading at the edges (legacy `.ticker::before`).
        using var topShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top),
            new[]
            {
                classColor.WithAlpha(0),
                classColor.WithAlpha((byte)(220 * alpha)),
                classColor.WithAlpha((byte)(220 * alpha)),
                classColor.WithAlpha(0),
            },
            new[] { 0f, 0.20f, 0.80f, 1f },
            SKShaderTileMode.Clamp);
        FillShader(c, new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 1), topShader);

        // Subtle bottom hairline for depth.
        cardBottomPaint.Color = SKColors.White.WithAlpha((byte)(10 * alpha));
        c.DrawRect(new SKRect(rect.Left, rect.Bottom - 1, rect.Right, rect.Bottom), cardBottomPaint);
    }

    // ----- class label box -----

    void DrawClassBox(SKCanvas c, SKColor classColor, string className, float alpha)
    {
        // Class identity fills the strip vertically; the box snugs to the logo
        // width so the scrolling chips start right after it. Text classes (no
        // bundled logo) keep the default box width.
        var clsLogo = LoadClassLogo(className);
        bool hasLogo = clsLogo != null && clsLogo.Height > 0;
        float logoW = 0f, logoH = 0f;
        if (hasLogo)
        {
            logoH = BarHeight - 4f;
            logoW = logoH * ((float)clsLogo!.Width / clsLogo.Height);
        }
        classBoxW = hasLogo ? 18f + logoW + 20f : ClassBoxWidth;
        float clipLt = BarHeight / 2f - logoH / 2f;
        if (hasLogo)
        {
            float trailX = 18f + logoW;
            chipClipTopX = trailX + 10f + LogoSlantDxDy * clipLt;
            chipClipBotX = trailX + 10f - LogoSlantDxDy * (BarHeight - clipLt);
        }
        else
        {
            chipClipTopX = ClassBoxWidth;
            chipClipBotX = ClassBoxWidth;
        }

        var rect = new SKRect(0, 0, classBoxW, BarHeight);

        // Class-colour -> transparent left-to-right gradient (legacy .ticker__class).
        using var bgShader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, 0), new SKPoint(rect.Right, 0),
            new[]
            {
                classColor.WithAlpha((byte)(170 * alpha)),
                classColor.WithAlpha((byte)(50 * alpha)),
                classColor.WithAlpha(0),
            },
            new[] { 0f, 0.55f, 1f },
            SKShaderTileMode.Clamp);
        FillShader(c, rect, bgShader);

        if (hasLogo)
        {
            float lt = rect.MidY - logoH / 2f;
            using var lp = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
                ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor(255, 255, 255, (byte)(255 * alpha)),
                    SKBlendMode.DstIn),
            };
            c.DrawBitmap(clsLogo!, new SKRect(18f, lt, 18f + logoW, lt + logoH), lp);
        }
        else
        {
            using var namePaint = new SKPaint
            {
                Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.Inter.Bold,
                TextSize = 22f,
            };
            namePaint.GetFontMetrics(out var nm);
            float nameBaseline = rect.MidY - (nm.Ascent + nm.Descent) / 2f;
            TextTracked.Draw(c, namePaint, className.ToUpperInvariant(), 18f, nameBaseline, 0.08f * 22f);
        }
    }

    // ----- chips marquee -----

    void DrawChips(SKCanvas c, DriverClass cls, SKColor classColor, float alpha, float barWidth, double nowMs)
    {
        bool pre = PreRaceGridMode;
        IReadOnlyList<Driver> drivers = cls.Drivers;
        if (drivers == null || drivers.Count == 0) return;
        if (pre)   // grid order: by class starting slot (same key as the grid board)
            drivers = drivers.OrderBy(d => d.PositionInClass > 0 ? d.PositionInClass : 10_000 + (d.Position > 0 ? d.Position : 999)).ToList();
        const float MakerSlot = 40f;

        // Measure each chip width once per frame; tiny cost, avoids font handle
        // re-allocation inside the loop. Per-chip width is content-dependent.
        var posPaint = posPaintC; posPaint.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        var numPaint = numPaintC; numPaint.Color = TextTertiary.WithAlpha((byte)(220 * alpha));
        var namePaint = namePaintC; namePaint.Color = TextPrimary.WithAlpha((byte)(255 * alpha));
        var gapPaint = gapPaintC; gapPaint.Color = TextSecondary.WithAlpha((byte)(225 * alpha));
        var leaderGapPaint = leaderGapPaintC; leaderGapPaint.Color = AccentGold.WithAlpha((byte)(255 * alpha));

        posPaint.GetFontMetrics(out var posMetrics);
        float baseline = ChipHeight / 2f - (posMetrics.Ascent + posMetrics.Descent) / 2f;

        // GAP COLUMN PINNED to a fixed slot width. Per-frame gap text changes
        // ("+0.3" → "+0.4") were the dominant source of frame-to-frame
        // chip-width fluctuation; pinning the gap slot at GapSlotWidth removes
        // that source so chip widths only change on class rotation or a
        // driver leave/join, and the continuous scroll accumulator stays
        // smooth across both. Scaled with the new larger gap text so
        // "+59:59" / "LEADER" still have comfortable margin.
        const float GapSlotWidth = 100f;

        // Cached layout: rebuild only when something the LAYOUT depends on has
        // actually changed (class rotation, count, or any driver-by-position).
        // Re-measuring all glyph widths at 60 fps was a meaningful per-frame
        // cost; pos/num/name are stable across the session, so measuring once
        // per content change is enough.
        bool needRebuild = cachedClassIdx != currentClassIdx
                        || cachedChips.Length != drivers.Count;
        if (!needRebuild)
        {
            for (int i = 0; i < drivers.Count; i++)
            {
                if (cachedCarIdxByIndex[i] != drivers[i].CarIdx) { needRebuild = true; break; }
            }
        }
        if (needRebuild)
        {
            cachedClassIdx = currentClassIdx;
            if (cachedCarIdxByIndex.Length != drivers.Count) cachedCarIdxByIndex = new int[drivers.Count];
            if (cachedChips.Length != drivers.Count) cachedChips = new ChipLayout[drivers.Count];
            float total = 0;
            for (int i = 0; i < drivers.Count; i++)
            {
                var d = drivers[i];
                cachedCarIdxByIndex[i] = d.CarIdx;
                var pos = $"P{i + 1}";
                var num = "#" + (string.IsNullOrEmpty(d.CarNumber) ? "—" : d.CarNumber);
                var name = ShortName(d);
                float posW  = MeasureTracked(posPaint, pos, 0.02f * PosTextSize);
                float numW  = MeasureTracked(numPaint, num, 0.08f * NumTextSize);
                float nameW = MeasureTracked(namePaint, name, 0.04f * NameTextSize);
                float width = ChipPad + posW + ChipGap + numW + ChipGap + (pre ? MakerSlot + ChipGap : 0) + nameW + ChipGap + GapSlotWidth + (pre ? (i == 0 ? 116f : 26f) : 0) + ChipPad;   // pre: room for "POLE  3:28.378"
                cachedChips[i] = new ChipLayout(pos, num, name, width);
                total += width;
            }
            cachedOneCopyWidth = total;
        }
        var chipLayouts = cachedChips;
        float oneCopyWidth = cachedOneCopyWidth;
        if (oneCopyWidth <= 0 || chipLayouts.Length == 0) return;

        // Advance the continuous scroll accumulator. Delta-based so the cursor
        // is monotonic regardless of any frame-time skew, and we wrap at the
        // current oneCopyWidth without losing position. (The old time-based
        // `nowMs % oneCopyWidth` introduced cursor jumps whenever the modulus
        // changed; this does not.)
        bool shouldScroll = drivers.Count >= MinChipsToScroll;
        if (pre)
        {
            // one full pass of the class, then hand over to the next class
            // scroll until the last car of the class reaches the right edge, hold, next class
            double delta = lastScrollMs < 0 || nowMs < preHoldUntilMs ? 0 : Math.Max(0, nowMs - lastScrollMs);
            float viewW = barWidth - Math.Min(chipClipTopX, chipClipBotX);
            double maxScroll = Math.Max(0, oneCopyWidth - viewW + 30);
            if (maxScroll > 0)
            {
                scrollPx += delta / 1000.0 * PreScrollPxPerSec;
                if (scrollPx >= maxScroll)
                {
                    scrollPx = maxScroll;
                    if (preEndAtMs < 0) preEndAtMs = nowMs;
                    if (nowMs - preEndAtMs > PreHoldMs) { preAdvanceClass = true; preEndAtMs = -1; }
                }
            }
            else if (nowMs - classShownAtMs > 9000) preAdvanceClass = true;   // whole class fits: just hold it
        }
        else if (shouldScroll)
        {
            double delta = lastScrollMs < 0 ? 0 : Math.Max(0, nowMs - lastScrollMs);
            scrollPx += delta / 1000.0 * ScrollPxPerSec;
            while (scrollPx >= oneCopyWidth) scrollPx -= oneCopyWidth;
        }
        else
        {
            scrollPx = 0;
        }
        lastScrollMs = nowMs;

        // Viewport: right of the class box.
        float viewportLeft = Math.Min(chipClipTopX, chipClipBotX);
        float viewportRight = barWidth;
        c.Save();
        var chipClip = chipClipC;
        chipClip.Rewind();
        chipClip.MoveTo(chipClipTopX, 0f);
        chipClip.LineTo(viewportRight, 0f);
        chipClip.LineTo(viewportRight, BarHeight);
        chipClip.LineTo(chipClipBotX, BarHeight);
        chipClip.Close();
        c.ClipPath(chipClip, antialias: true);

        // Draw two copies of the chip strip for a seamless loop. The cursor
        // stays in float (sub-pixel) — integer truncation here used to cause
        // visible 1 px stutter every few frames, especially at SkiaSharp's
        // software-rasterised text on a transparent WPF window. SkiaSharp's
        // SUBPIXEL hinting handles fractional positions; we let it.
        // pre-race: P1 starts fully clear of the slanted class badge
        float cursor = (pre ? Math.Max(chipClipTopX, chipClipBotX) + 6f : viewportLeft) - (float)scrollPx;
        for (int copy = 0; copy < (pre ? 1 : 2); copy++)   // pre-race rolls each class once, no loop copy
        {
            for (int i = 0; i < chipLayouts.Length; i++)
            {
                var layout = chipLayouts[i];
                float left = cursor;
                cursor += layout.Width;
                // Skip chips entirely outside the viewport.
                if (left + layout.Width < viewportLeft) continue;
                if (left > viewportRight) break;

                var d = drivers[i];
                bool isLeader = i == 0;
                var liveChip = new Chip(
                    layout.Pos, layout.Num, layout.Name,
                    pre ? (i == 0 ? "POLE  " + QualTime(d.QualifyTime) : QualTime(d.QualifyTime)) : FormatGap(d, i, drivers),
                    layout.Width, !pre && d.IsInPit, isLeader);
                DrawChip(c, liveChip, classColor, left, baseline, alpha,
                    posPaint, numPaint, namePaint, isLeader ? leaderGapPaint : gapPaint,
                    pre ? MakerLogo(d) : null, pre ? MakerSlot : 0);
            }
        }

        c.Restore();

        // Left + right fade gradients to soften the chips entering/leaving.
        DrawEdgeFade(c, viewportRight - 28f, viewportRight, alpha, fadeOnLeft: false);
    }

    void DrawChip(SKCanvas c, Chip chip, SKColor classColor, float left, float baseline, float alpha,
                  SKPaint posPaint, SKPaint numPaint, SKPaint namePaint, SKPaint gapPaint,
                  SKBitmap? maker = null, float makerSlot = 0)
    {
        float chipAlpha = chip.IsInPit ? alpha * 0.45f : alpha;
        float right = left + chip.Width;

        // Right divider hairline.
        dividerPaintC.Color = BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha * 0.7f));
        c.DrawLine(right, 8, right, BarHeight - 8, dividerPaintC);

        // Leader gets a faint warm gold wash so the eye latches.
        if (chip.IsLeader)
        {
            using var leaderShader = SKShader.CreateLinearGradient(
                new SKPoint(left, 0), new SKPoint(left, BarHeight),
                new[]
                {
                    AccentGold.WithAlpha((byte)(46 * alpha)),
                    AccentGold.WithAlpha((byte)(18 * alpha)),
                },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp);
            FillShader(c, new SKRect(left, 0, right, BarHeight), leaderShader);
        }

        // Apply pit dimming by overriding paint alphas locally.
        var posColor  = (chip.IsLeader ? AccentGold : TextPrimary).WithAlpha((byte)(255 * chipAlpha));
        var numColor  = TextTertiary.WithAlpha((byte)(220 * chipAlpha));
        var nameColor = TextPrimary.WithAlpha((byte)(255 * chipAlpha));
        var gapColor  = chip.IsLeader
            ? AccentGold.WithAlpha((byte)(255 * chipAlpha))
            : TextSecondary.WithAlpha((byte)(225 * chipAlpha));

        var savedPos = posPaint.Color;
        var savedNum = numPaint.Color;
        var savedName = namePaint.Color;
        var savedGap = gapPaint.Color;
        posPaint.Color = posColor;
        numPaint.Color = numColor;
        namePaint.Color = nameColor;
        gapPaint.Color = gapColor;
        try
        {
            float cursor = left + ChipPad;
            TextTracked.Draw(c, posPaint, chip.Pos, cursor, baseline, 0.02f * PosTextSize);
            cursor += MeasureTracked(posPaint, chip.Pos, 0.02f * PosTextSize) + ChipGap;

            TextTracked.Draw(c, numPaint, chip.Num, cursor, baseline, 0.08f * NumTextSize);
            cursor += MeasureTracked(numPaint, chip.Num, 0.08f * NumTextSize) + ChipGap;

            if (makerSlot > 0)
            {
                // car-maker logo, fitted into a fixed slot so the chip widths stay tabular
                if (maker != null && maker.Width > 0 && maker.Height > 0)
                {
                    float mh = BarHeight - 14f, mw = maker.Width * (mh / maker.Height);
                    if (mw > makerSlot) { mh *= makerSlot / mw; mw = makerSlot; }
                    float mx = cursor + (makerSlot - mw) / 2f, my = BarHeight / 2f - mh / 2f;
                    using var mp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High,
                                                 Color = SKColors.White.WithAlpha((byte)(255 * chipAlpha)) };
                    c.DrawBitmap(maker, new SKRect(mx, my, mx + mw, my + mh), mp);
                }
                cursor += makerSlot + ChipGap;
            }

            TextTracked.Draw(c, namePaint, chip.Name, cursor, baseline, 0.04f * NameTextSize);

            // Gap text right-aligned within the fixed gap slot so the column
            // looks tabular regardless of length ("+0.3" / "+59:59" / "LEADER"
            // all visually align to the chip's right edge). Pinning the slot
            // is what keeps chip widths stable; right-aligning the text inside
            // is what makes it look intentional.
            float gapTextWidth = MeasureTracked(gapPaint, chip.Gap, 0.04f * GapTextSize);
            TextTracked.Draw(c, gapPaint, chip.Gap, right - ChipPad - gapTextWidth, baseline, 0.04f * GapTextSize);
        }
        finally
        {
            posPaint.Color = savedPos;
            numPaint.Color = savedNum;
            namePaint.Color = savedName;
            gapPaint.Color = savedGap;
        }

        // PIT cue: a thin amber stripe along the bottom of the chip.
        if (chip.IsInPit)
        {
            pitPaintC.Color = PitAmber.WithAlpha((byte)(180 * alpha));
            c.DrawRect(new SKRect(left + ChipPad, BarHeight - 3, right - ChipPad, BarHeight - 1), pitPaintC);
        }

        _ = classColor; // available for future per-chip class-color accent if needed
    }

    void DrawEdgeFade(SKCanvas c, float x0, float x1, float alpha, bool fadeOnLeft)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(x0, 0), new SKPoint(x1, 0),
            fadeOnLeft
                ? new[] { BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha)), BgGlass.WithAlpha(0) }
                : new[] { BgGlass.WithAlpha(0), BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha)) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        FillShader(c, new SKRect(x0, 0, x1, BarHeight), shader);
    }

    // ----- helpers -----

    static string ShortName(Driver d)
    {
        var name = NameFormat.Broadcast(d);
        return string.IsNullOrEmpty(name) ? "—" : name;
    }

    static string QualTime(float s)
    {
        if (s <= 0 || float.IsNaN(s)) return "—";
        int m = (int)(s / 60); float rest = s - m * 60;
        return m > 0 ? $"{m}:{rest.ToString("00.000", CultureInfo.InvariantCulture)}"
                     : rest.ToString("0.000", CultureInfo.InvariantCulture);
    }

    SKBitmap? MakerLogo(Driver d)
    {
        var slug = ManufacturerBrand.Resolve(d.CarBrand ?? "");
        if (slug == null) return null;
        if (makerCache.TryGetValue(slug, out var b)) return b;
        try
        {
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
            if (System.IO.File.Exists(path)) { using var st = System.IO.File.OpenRead(path); b = Assets.Decode(st); }
        }
        catch { b = null; }
        return makerCache[slug] = b;
    }

    static string FormatGap(Driver d, int classIndex, IReadOnlyList<Driver> drivers)
    {
        if (classIndex == 0) return "LEADER";
        // Use the snapshot's own per-row IntervalSeconds (gap to car ahead in class).
        // That gives the legacy "+s.s" feel without re-deriving anything.
        double? interval = d.IntervalSeconds;
        if (interval is not double s || double.IsNaN(s) || s <= 0)
        {
            if (d.LapDifference > 0)
                return d.LapDifference == 1 ? "+1L" : $"+{d.LapDifference}L";
            return "—";
        }
        if (s < 60) return "+" + s.ToString("0.0", CultureInfo.InvariantCulture);
        int mins = (int)(s / 60);
        int secs = (int)(s % 60);
        return "+" + mins + ":" + secs.ToString("00", CultureInfo.InvariantCulture);
    }

    static float MeasureTracked(SKPaint paint, string text, float letterSpacing)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var widths = paint.GetGlyphWidths(text);
        if (widths.Length == 0) return paint.MeasureText(text);
        float total = 0;
        for (int i = 0; i < widths.Length; i++)
            total += widths[i] + (i == widths.Length - 1 ? 0 : letterSpacing);
        return total;
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

    public void Dispose()
    {
        glassPaint.Dispose();
        hairlinePaint.Dispose();
        foreach (var p in new[] { cardGlassPaint, cardBottomPaint, shaderPaint, posPaintC, numPaintC, namePaintC, gapPaintC,
                                  leaderGapPaintC, dividerPaintC, pitPaintC }) p.Dispose();
        chipClipC.Dispose();
        foreach (var b in logoCache.Values) b?.Dispose();
        logoCache.Clear();
        foreach (var b in makerCache.Values) b?.Dispose();
        makerCache.Clear();
    }

    readonly record struct Chip(string Pos, string Num, string Name, string Gap,
                                 float Width, bool IsInPit, bool IsLeader);

    /// <summary>Cached per-driver chip layout — width + the three stable
    /// label strings whose glyphs we measured once. Pit dim, leader flag and
    /// gap text are applied per frame from the live snapshot, so a pit
    /// transition or a gap tick does not invalidate the cache.</summary>
    readonly record struct ChipLayout(string Pos, string Num, string Name, float Width);
}
