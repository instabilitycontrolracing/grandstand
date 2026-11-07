using System.Globalization;
using ApexHUD.Native.Common;
using SkiaSharp;

namespace ApexHUD.Native.Overlays;

/// <summary>
/// Draws the Featured Driver Card — the lower-third lower-third that spotlights
/// the camera-focused driver. Port of the production-visible parts of the legacy
/// <c>featured-card.html</c> (PORT_PLAN §3): a 720×210 deep-glass card with a
/// 1 px class-color top bar and four columns — position / identity / perf /
/// brand — bound to <see cref="RaceSnapshot.CameraTarget"/>.
///
/// <para>The renderer also owns the visibility state machine (early-laps /
/// final-laps / recent-position-change triggers, with 8 s min-visible and 5 s
/// hide hysteresis) and the per-value count-bounce on changed values. The card
/// is force-visible by default to match the legacy's production behavior; the
/// state machine is in place so it's correct either way.</para>
/// </summary>
public sealed class FeaturedCardRenderer : IDisposable
{
    // 2026-11-06 (#942, Angelo: "I want the driver card to have the car image on it"): a car column (the car's own paint,
    // side view) between the name and the timing - the card grew by its width
    public const float ColCar = 230f;
    public const float CardWidth = 720f + ColCar;
    public const float CardHeight = 189f; // 210 − 10% (21 px), trimmed 10.5 px off top & bottom

    // Legacy CSS grid-template-columns: 110px 1fr 200px 110px (sum 420 + flex).
    public const float ColPosition = 110f;
    public const float ColPerf = 200f;
    public const float ColBrand = 110f;

    // Visibility timing (matches the legacy constants verbatim).
    const double EnterAnimMs = 420.0;
    const double ExitAnimMs = 420.0;
    const double CountBounceMs = 320.0;
    const double MinVisibleMs = 8_000.0;
    const double HideHysteresisMs = 5_000.0;
    const double PositionChangeWindowMs = 10_000.0;
    const int EarlyLaps = 3;
    const int FinalLaps = 3;

    static readonly SKColor BgGlass        = ParseHex(BroadcastPalette.TowerGlassBase, 209); // ~0.82 alpha
    static readonly SKColor TextPrimary    = ParseHex(BroadcastPalette.TextPrimary);
    static readonly SKColor TextTertiary   = ParseHex(BroadcastPalette.TextTertiary);
    static readonly SKColor BorderHairline = ParseHex(BroadcastPalette.BorderHairline, 120);
    static readonly SKColor LeaderGold     = ParseHex(BroadcastPalette.Leader);

    // Cached glass paint built once.
    readonly SKPaint glassPaint;

    // Manufacturer logo cache, mirrors the tower's pattern.
    readonly Dictionary<string, SKBitmap?> logoCache = new(StringComparer.OrdinalIgnoreCase);

    // Per-value count-bounce timestamps (keyed by an element id string).
    readonly Dictionary<string, ValueState> valueState = new(StringComparer.Ordinal);

    // Visibility state machine.
    bool isVisible;
    double shownAtMs;
    double wantHideAtMs;
    int lastFeaturedCarIdx = -1;
    int lastFeaturedPosition;
    double lastPositionChangeAtMs;
    double leaveStartedAtMs = -1;

    // Outer presentation gate: tracks PresentationState.FeaturedVisible across
    // frames so we can cross-fade in/out as the broadcast state machine flips
    // (pre-green → racing+lap1Complete → battle → back). Replaces the old
    // Port 06 battle-yield-to-10% behaviour — the brief now requires the card
    // to hide entirely during battle and return at full opacity afterwards.
    /// <summary>#428: the card's own opacity on the last frame (1 = fully in).</summary>
    public float LastAlpha;
    /// <summary>#428: diagnostics sink (Program.Log in the app).</summary>
    public static Action<string>? FadeLog;
    double partSince = -1, partLoggedAt = -1e9;
    bool wasGated;
    double gateFlipAtMs = -1;
    const double GateFadeAnimMs = 480;   // slide + fade, 2026-10-24

    /// <summary>When true, the state machine is bypassed and the card stays visible.</summary>
    public bool ForceVisible { get; set; } = true;

    public FeaturedCardRenderer()
    {
        glassPaint = new SKPaint { IsAntialias = true, Color = BgGlass };
    }

    /// <summary>
    /// Draw the card at <paramref name="originX"/>,<paramref name="originY"/>.
    /// Returns <c>true</c> if the card painted any pixels this frame (the
    /// window can skip <c>InvalidateVisual</c> ratcheting when fully hidden,
    /// but we keep the render loop running and let opacity zero do the work).
    /// </summary>
    public bool Draw(SKCanvas canvas, float originX, float originY, RaceSnapshot snapshot, double elapsedMs)
    {
        var featured = snapshot.CameraTarget ?? snapshot.Player;

        // 2026-10-25 (Angelo): "if I have not loaded in yet the driver card needs to not be just sitting blank,
        // it should be hidden". No driver, or a car that isn't on the track yet (not in the world, spectating):
        // draw nothing, and play the normal entrance once the car is there.
        if (featured == null || featured.LapDistPct < 0f || featured.IsSpectator || featured.IsPaceCar)
        {
            isVisible = false;
            shownAtMs = 0;
            leaveStartedAtMs = -1;
            LastAlpha = 0f;
            return false;
        }

        // Drive the state machine even when nothing's there yet (so we don't
        // pop on the moment the first snapshot arrives).
        UpdateVisibility(snapshot, featured, elapsedMs);

        float alpha = ComputeAlpha(elapsedMs);
        // Outer broadcast gate: cross-fade to 0 when PresentationState says
        // the card should be hidden (pre-green, before lap 1 complete, or
        // during battle), and back to 1 when it should be visible again.
        // Replaces the legacy 10% battle yield — the brief requires full
        // hide during battle and FULL opacity on return.
        alpha *= ComputeGateAlpha(elapsedMs);
        if (alpha <= 0.01f) { LastAlpha = 0f; partSince = -1; return false; }
        float translateY = ComputeTranslateY(elapsedMs);
        // 2026-10-24 (Angelo): when another card takes the lower third, the driver card slides down
        // out of the way (and back up) instead of just fading on the spot
        translateY += (1f - ComputeGateAlpha(elapsedMs)) * CardHeight * 0.55f;

        canvas.Save();
        canvas.Translate(originX, originY + translateY);

        var card = new SKRect(0, 0, CardWidth, CardHeight);

        // Disconnected fades the card to 55% per legacy CSS.
        // 2026-10-27 (#406, Angelo: "this weird clear version of the driver card keeps coming on"): it faded to 55 %
        // whenever the connection read "lost" for a moment (replay jumps / loads). The whole overlay hides when
        // iRacing is really gone, so the card stays solid.
        float connectedAlpha = 1f;
        float finalAlpha = alpha * connectedAlpha;
        LastAlpha = finalAlpha;
        // #428: a card left part-faded for more than 2 s is logged with why (entrance / gate / whole-overlay fades)
        float ov = (1f - PresentationState.CardsHiddenK) * (1f - PresentationState.ReplayHideK);
        float seen = finalAlpha * ov;
        if (seen > 0.05f && seen < 0.9f)
        {
            if (partSince < 0) partSince = elapsedMs;
            else if (elapsedMs - partSince > 2000 && elapsedMs - partLoggedAt > 10000)
            {
                partLoggedAt = elapsedMs;
                FadeLog?.Invoke($"driver card part-faded {seen:F2} for {(elapsedMs - partSince) / 1000:F1}s: enter={ComputeAlpha(elapsedMs):F2} gate={ComputeGateAlpha(elapsedMs):F2} featuredVisible={PresentationState.FeaturedVisible} onboardK={PresentationState.OnboardK:F2} replayHideK={PresentationState.ReplayHideK:F2}");
            }
        }
        else partSince = -1;

        DrawCard(canvas, card, featured, finalAlpha, elapsedMs);

        if (featured != null)
        {
            DrawPosition(canvas, card, featured, finalAlpha, elapsedMs);
            DrawIdentity(canvas, card, snapshot, featured, finalAlpha, elapsedMs);
            DrawPerf(canvas, card, featured, snapshot, finalAlpha, elapsedMs);
            DrawBrand(canvas, card, featured, finalAlpha);
        }

        canvas.Restore();
        return true;
    }

    // ----- card chrome -----

    void DrawCard(SKCanvas c, SKRect card, Driver? featured, float alpha, double elapsedMs)
    {
        var glassColor = BgGlass.WithAlpha((byte)(BgGlass.Alpha * alpha));
        using var glass = new SKPaint { IsAntialias = true, Color = glassColor };
        // Glass panel drawn 10.5 px (5% of the original height) shorter at the
        // BOTTOM only. Layout/content coords stay on the full CardHeight, so
        // every element keeps its exact position — only the empty bottom margin
        // of glass is trimmed. The lowest content (last perf row, logo, corner
        // line) all sit well above this new edge.
        var glassRect = new SKRect(card.Left, card.Top, card.Right, card.Bottom - 10.5f);
        c.DrawRect(glassRect, glass);
        Theme.Skin(c, glassRect, ThemeSurface.Driver, elapsedMs, alpha);   // #601: the broadcast theme (nothing on Default)

        // Top 1px class-color bar with a soft glow underneath.
        var classColor = featured == null
            ? LeaderGold
            : ParseHex(string.IsNullOrEmpty(featured.ClassColor)
                ? BroadcastPalette.Class(featured.ClassName)
                : featured.ClassColor);

        // Soft glow: a thin gradient fade from the class color into transparent.
        using var glowShader = SKShader.CreateLinearGradient(
            new SKPoint(card.Left, card.Top),
            new SKPoint(card.Left, card.Top + 10f),
            new[]
            {
                classColor.WithAlpha((byte)(140 * alpha)),
                classColor.WithAlpha(0),
            },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        using var glowPaint = new SKPaint { IsAntialias = true, Shader = glowShader };
        c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 10f), glowPaint);

        using var bar = new SKPaint { Color = classColor.WithAlpha((byte)(255 * alpha)) };
        c.DrawRect(new SKRect(card.Left, card.Top, card.Right, card.Top + 1f), bar);

        // Vertical hairlines at the perf and brand column boundaries (the
        // legacy `.perf::before` and `.brand::before` gradient strips). 18%/82%
        // alpha fade top/bottom so the line floats inside the column.
        DrawColumnHairline(c, card, card.Right - ColBrand - ColPerf, alpha);
        DrawColumnHairline(c, card, card.Right - ColBrand, alpha);
        if (featured != null) DrawCarImage(c, card, featured, alpha);
    }

    // #942: the car in its own paint (LiveryArt - the same pictures the onboard and pre-race cards use), side view,
    // in its column; nothing is drawn until the picture is ready (it never blocks a frame)
    void DrawCarImage(SKCanvas c, SKRect card, Driver d, float alpha)
    {
        var img = LiveryArt.Get(d);
        if (img == null || img.Width <= 0 || img.Height <= 0) return;
        float colL = card.Right - ColBrand - ColPerf - ColCar, colR = card.Right - ColBrand - ColPerf;
        float visBottom = card.Bottom - 10.5f;
        float maxW = ColCar - 16f, maxH = visBottom - card.Top - 30f;
        float sc = Math.Min(maxW / img.Width, maxH / img.Height);
        float w = img.Width * sc, h = img.Height * sc;
        float cx = (colL + colR) / 2f, cy = (card.Top + visBottom) / 2f + 6f;
        var dst = new SKRect(cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f);
        // a soft class-colour floor glow under the car
        var classColor = ParseHex(string.IsNullOrEmpty(d.ClassColor) ? BroadcastPalette.Class(d.ClassName) : d.ClassColor);
        using (var floorSh = SKShader.CreateRadialGradient(new SKPoint(cx, dst.Bottom - 4f), w * 0.55f,
                   new[] { classColor.WithAlpha((byte)(60 * alpha)), classColor.WithAlpha(0) }, new[] { 0f, 1f }, SKShaderTileMode.Clamp))
        using (var floor = new SKPaint { IsAntialias = true, Shader = floorSh })
        {
            c.Save(); c.Scale(1f, 0.22f, cx, dst.Bottom - 4f);
            c.DrawCircle(cx, dst.Bottom - 4f, w * 0.55f, floor);
            c.Restore();
        }
        using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, Color = SKColors.White.WithAlpha((byte)(255 * alpha)) };
        c.DrawImage(img, dst, p);
        DrawColumnHairline(c, card, colL, alpha);
    }

    void DrawColumnHairline(SKCanvas c, SKRect card, float x, float alpha)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, card.Top + 13.5f), new SKPoint(0, card.Bottom - 13.5f),
            new[]
            {
                BorderHairline.WithAlpha(0),
                BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha)),
                BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha)),
                BorderHairline.WithAlpha(0),
            },
            new[] { 0f, 0.18f, 0.82f, 1f },
            SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        c.DrawRect(new SKRect(x, card.Top + 13.5f, x + 1f, card.Bottom - 13.5f), paint);
    }

    // ----- column 1: position -----

    void DrawPosition(SKCanvas c, SKRect card, Driver d, float alpha, double elapsedMs)
    {
        float colLeft = card.Left;
        float colRight = card.Left + ColPosition;
        float centerX = (colLeft + colRight) / 2f;
        // Vertical centre of the VISIBLE glass (it is trimmed 10.5 px at the
        // bottom), which is what the eye reads as the card's middle. The number
        // and its glow/label are centred on this, not the layout midpoint.
        float centerY = (card.Top + card.Bottom - 10.5f) / 2f;

        // Soft radial glow under the number (class color, ~20% opacity).
        var classColor = ParseHex(string.IsNullOrEmpty(d.ClassColor)
            ? BroadcastPalette.Class(d.ClassName)
            : d.ClassColor);
        using (var radial = SKShader.CreateRadialGradient(
            new SKPoint(centerX, centerY), 75f,
            new[]
            {
                classColor.WithAlpha((byte)(51 * alpha)), // 0.20 alpha
                classColor.WithAlpha(0),
            },
            new[] { 0f, 0.65f },
            SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { IsAntialias = true, Shader = radial })
        {
            c.DrawCircle(centerX, centerY, 75f, paint);
        }

        // "POSITION" label above the number.
        using (var label = new SKPaint
        {
            Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
            IsAntialias = true,
            Typeface = Fonts.Inter.SemiBold,
            TextSize = 13f,   // #340: was 11
            TextAlign = SKTextAlign.Center,
            TextSkewX = -0.18f, // italic emulation (legacy is font-style: italic)
        })
        {
            TextTracked.Draw(c, label, "POSITION", centerX, centerY - 41f, 0.8f);
        }

        // Position number — Cormorant Garamond bold 80px.
        int posN = d.PositionInClass > 0 ? d.PositionInClass : d.Position;
        string posText = posN > 0 ? posN.ToString(CultureInfo.InvariantCulture) : "—";   // no position yet (qualifying before a lap)
        using var posPaint = new SKPaint
        {
            Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
            IsAntialias = true,
            Typeface = Fonts.CormorantGaramond.Bold,
            TextSize = 80f,
            TextAlign = SKTextAlign.Center,
        };
        // Cormorant uses old-style figures (e.g. "4" descends below the
        // baseline), so a fixed baseline offset reads off-centre and would
        // drift as the position digit changes. Measure the actual ink box and
        // place the baseline so the number's TRUE centre lands on centerY — the
        // visible card centre — for every position value.
        var inkBounds = new SKRect();
        posPaint.MeasureText(posText, ref inkBounds);
        float inkCenterRelBaseline = (inkBounds.Top + inkBounds.Bottom) / 2f; // negative = above baseline
        float numberBaseline = centerY - inkCenterRelBaseline;
        DrawAnimatedValue(c, "position", posText, centerX, numberBaseline, elapsedMs, posPaint);
    }

    // ----- column 2: identity -----

    void DrawIdentity(SKCanvas c, SKRect card, RaceSnapshot snap, Driver d, float alpha, double elapsedMs)
    {
        float left = card.Left + ColPosition + 4f;
        float right = card.Right - ColBrand - ColPerf - ColCar - 12f;
        float top = card.Top + 11.5f;
        float bottom = card.Bottom - 11.5f;

        float chipsY = top + 18f; // chips centered at 24 px high
        float nameY = top + 86f;  // hero name sits low + large, centred-ish in the column
        float carY = nameY + 26f;
        float cornerY = carY + 22f;

        // --- chips row (number / license / class) ---
        float chipX = left;
        float chipGap = 7f;

        // Number chip: white bg, dark text, min 42×24.
        string number = string.IsNullOrEmpty(d.CarNumber) ? "—" : d.CarNumber;
        using (var numText = new SKPaint
        {
            Color = new SKColor(8, 10, 16, (byte)(242 * alpha)),
            IsAntialias = true,
            Typeface = Fonts.Inter.Bold,
            TextSize = 19.5f,
        })
        {
            float textW = numText.MeasureText(number);
            float chipW = Math.Max(63f, textW + 27f);
            var rect = new SKRect(chipX, chipsY - 18, chipX + chipW, chipsY + 18);
            using var bg = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * alpha)) };
            c.DrawRect(rect, bg);
            using var hl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(178 * alpha)) };
            c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 1), hl);
            numText.GetFontMetrics(out var m);
            float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;
            numText.TextAlign = SKTextAlign.Center;
            c.DrawText(number, rect.MidX, baseline, numText);
            chipX += chipW + chipGap;
        }

        // License chip: 24×24, colored bg.
        string lic = string.IsNullOrEmpty(d.LicenceString)
            ? "R"
            : d.LicenceString[..1].ToUpperInvariant();
        var licColor = ParseHex(BroadcastPalette.License(lic));
        {
            var rect = new SKRect(chipX, chipsY - 18, chipX + 36, chipsY + 18);
            using var bg = new SKPaint { Color = licColor.WithAlpha((byte)(255 * alpha)) };
            c.DrawRect(rect, bg);
            using var hl = new SKPaint { Color = SKColors.White.WithAlpha((byte)(64 * alpha)) };
            c.DrawRect(new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 1), hl);
            var licTextColor = lic == "C" ? new SKColor(0x0A, 0x0E, 0x14) : SKColors.White;
            using var text = new SKPaint
            {
                Color = licTextColor.WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.Inter.Bold,
                TextSize = 19.5f,
                TextAlign = SKTextAlign.Center,
            };
            text.GetFontMetrics(out var m);
            float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;
            c.DrawText(lic, rect.MidX, baseline, text);
            chipX += 36 + chipGap;
        }

        // Class chip: gradient bg + 1px border. An IMSA class logo fills the
        // chip when one resolves (logo carries colour, so no stripe); else the
        // uppercase class name with its colour stripe.
        var classColor = ParseHex(string.IsNullOrEmpty(d.ClassColor)
            ? BroadcastPalette.Class(d.ClassName)
            : d.ClassColor);
        if (!string.IsNullOrEmpty(d.ClassName))
        {
            var clsLogo = LoadClassLogo(d.ClassName);
            if (clsLogo != null && clsLogo.Height > 0)
            {
                const float targetH = 30f; // fill the chip box height (number / license box height)
                // Wide wordmarks (esp. GTD PRO ~7:1) are width-limited by the stat
                // column; clamp to just before it so they fill as tall as fits.
                float chipMaxRight = right + 28f;
                float maxLogoW = Math.Max(40f, chipMaxRight - chipX - 12f);
                float aspect = (float)clsLogo.Width / clsLogo.Height;
                float logoW = Math.Min(targetH * aspect, maxLogoW);
                float logoH = logoW / aspect;
                float chipW = logoW + 12f;
                var rect = new SKRect(chipX, chipsY - 18, chipX + chipW, chipsY + 18);
                using var bg = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(
                        new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                        new[]
                        {
                            new SKColor(255, 255, 255, (byte)(8 * alpha)),
                            new SKColor(255, 255, 255, (byte)(18 * alpha)),
                        },
                        new[] { 0f, 1f },
                        SKShaderTileMode.Clamp),
                };
                c.DrawRect(rect, bg);
                using var border = new SKPaint
                {
                    IsAntialias = false,
                    Color = BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1f,
                };
                c.DrawRect(rect, border);
                float lt = rect.MidY - logoH / 2f;
                using var lp = new SKPaint
                {
                    IsAntialias = true,
                    FilterQuality = SKFilterQuality.High,
                    ColorFilter = SKColorFilter.CreateBlendMode(
                        new SKColor(255, 255, 255, (byte)(250 * alpha)),
                        SKBlendMode.DstIn),
                };
                c.DrawBitmap(clsLogo, new SKRect(rect.Left + 6f, lt, rect.Left + 6f + logoW, lt + logoH), lp);
            }
            else
            {
                using var text = new SKPaint
                {
                    Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
                    IsAntialias = true,
                    Typeface = Fonts.Inter.Bold,
                    TextSize = 15f,
                };
                string label = d.ClassName.ToUpperInvariant();
                float trackedW = MeasureTracked(text, label, 0.16f * 15f);
                float chipW = trackedW + 42f; // scaled 1.5×: ~10px left + 16px right + stripe
                var rect = new SKRect(chipX, chipsY - 18, chipX + chipW, chipsY + 18);
                using var bg = new SKPaint
                {
                    IsAntialias = true,
                    Shader = SKShader.CreateLinearGradient(
                        new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                        new[]
                        {
                            new SKColor(255, 255, 255, (byte)(8 * alpha)),
                            new SKColor(255, 255, 255, (byte)(18 * alpha)),
                        },
                        new[] { 0f, 1f },
                        SKShaderTileMode.Clamp),
                };
                c.DrawRect(rect, bg);
                using var border = new SKPaint
                {
                    IsAntialias = false,
                    Color = BorderHairline.WithAlpha((byte)(BorderHairline.Alpha * alpha)),
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 1f,
                };
                c.DrawRect(rect, border);

                using var stripe = new SKPaint { Color = classColor.WithAlpha((byte)(255 * alpha)) };
                c.DrawRect(new SKRect(rect.Left + 10.5f, rect.MidY - 10.5f, rect.Left + 15f, rect.MidY + 10.5f), stripe);

                text.GetFontMetrics(out var m);
                float baseline = rect.MidY - (m.Ascent + m.Descent) / 2f;
                TextTracked.Draw(c, text, label, rect.Left + 22.5f, baseline, 0.16f * 15f);
            }
        }

        // --- driver name (full name per FEATURE_BACKLOG §9; fall back to the
        // broadcast initial+surname form only when the full name is missing) ---
        string name = string.IsNullOrEmpty(d.Name) ? NameFormat.Broadcast(d) : d.Name;
        if (!string.IsNullOrEmpty(name))
        {
            // Hero name: auto-fit the largest size in [24..36] that fills the
            // identity column without clipping, so the name uses the available
            // width instead of sitting at a timid fixed size. Only genuinely
            // long full names (which would clip even small) get ellipsised at
            // the floor size.
            float nameWidth = right - left;
            float nameSize = 36f;
            using var paint = new SKPaint
            {
                Color = TextPrimary.WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.CormorantGaramond.Bold,
                TextSize = nameSize,
            };
            while (nameSize > 24f && paint.MeasureText(name) > nameWidth - 2f)
            {
                nameSize -= 1f;
                paint.TextSize = nameSize;
            }
            string ellipsised = EllipsiseToWidth(name, paint, nameWidth);
            DrawAnimatedValue(c, "name", ellipsised, left, nameY, elapsedMs, paint, valueToTrack: ellipsised);
        }

        // --- car model: moved to the brand column, below the logo (see DrawBrand) ---

        // --- corner line (only when we resolve a corner from LapDistPct) ---
        var corner = TrackCorners.Resolve(snap.TrackBroadcastName, d.LapDistPct);
        if (!string.IsNullOrEmpty(corner))
        {
            using var paint = new SKPaint
            {
                Color = classColor.WithAlpha((byte)(230 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.Inter.Bold,
                TextSize = 20f,
            };
            // Lead with the "»" cue, then the corner name (2× size per request).
            c.DrawText("»", left, cornerY, paint);
            float chevronW = paint.MeasureText("»");
            TextTracked.Draw(c, paint, corner.ToUpperInvariant(),
                left + chevronW + 10f, cornerY, 0.34f * 20f);
        }
    }

    // ----- column 3: perf -----

    void DrawPerf(SKCanvas c, SKRect card, Driver d, RaceSnapshot snap, float alpha, double elapsedMs)
    {
        float left = card.Right - ColBrand - ColPerf + 22f;
        float right = card.Right - ColBrand - 18f;
        float top = card.Top + 11.5f;
        float bottom = card.Bottom - 11.5f;

        // Build the rows that have data — hide empty ones (legacy
        // perf-row[data-empty="true"] { display: none; }).
        var rows = new List<PerfRow>(5);
        if (d.LastLapTime > 0)
            rows.Add(new PerfRow("last", "LAST", LapTimeFormat.Lap(d.LastLapTime), PerfTone.Normal));
        if (d.BestLapTime > 0)
            rows.Add(new PerfRow("best", "BEST", LapTimeFormat.Lap(d.BestLapTime), PerfTone.Normal));
        // Gap to the class leader (2026-10-24: gaps are per class now): "LEADER" for P1 in class.
        bool isLeader = d.PositionInClass == 1;
        // Interval to the car directly ahead in class (FEATURE_BACKLOG §9:
        // "gap to car ahead"). Hidden for the leader — no car ahead.
        if (!isLeader && d.IntervalSeconds is double iv && iv > 0.001)
            rows.Add(new PerfRow("int", "INT", LapTimeFormat.Gap(iv), PerfTone.Normal));
        if (isLeader)
            rows.Add(new PerfRow("gap", "GAP", "LEADER", PerfTone.Leader));
        else if (d.GapToLeaderSeconds is double g && g > 0.001)
            rows.Add(new PerfRow("gap", "GAP", LapTimeFormat.Gap(g, d.LapDifference), PerfTone.Normal));
        if (d.IRating > 0)
            rows.Add(new PerfRow("irating", "IRATING", FormatIRating(d.IRating), PerfTone.Normal));

        if (rows.Count == 0) return;

        float rowHeight = (bottom - top) / rows.Count;
        for (int i = 0; i < rows.Count; i++)
        {
            float rowY = top + (i + 0.5f) * rowHeight;
            var r = rows[i];

            using var keyPaint = new SKPaint
            {
                Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.Inter.SemiBold,
                TextSize = 12.5f,   // #340: was 9
            };
            keyPaint.GetFontMetrics(out var km);
            float keyBaseline = rowY - (km.Ascent + km.Descent) / 2f;
            TextTracked.Draw(c, keyPaint, r.Key, left, keyBaseline, 2.0f);

            var valColor = r.Tone switch
            {
                PerfTone.Leader => LeaderGold,
                _               => TextPrimary,
            };
            var valTypeface = r.Tone switch
            {
                PerfTone.Leader => Fonts.Inter.Bold,
                _               => Fonts.Inter.SemiBold,
            };
            using var valPaint = new SKPaint
            {
                Color = valColor.WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
                Typeface = valTypeface,
                TextSize = 17f,
                TextAlign = SKTextAlign.Right,
            };
            DrawAnimatedValue(c, "perf-" + r.Id, r.Value, right, keyBaseline + 3f /* val sits lower than key */, elapsedMs, valPaint);
        }
    }

    enum PerfTone { Normal, Leader }
    readonly record struct PerfRow(string Id, string Key, string Value, PerfTone Tone);

    // ----- column 4: brand -----

    void DrawBrand(SKCanvas c, SKRect card, Driver d, float alpha)
    {
        float left = card.Right - ColBrand + 10f;
        float right = card.Right - 16f;
        float centerX = (left + right) / 2f;
        float centerY = card.MidY;

        var logo = LoadLogo(d.CarBrand);
        if (logo != null)
        {
            // Contain inside max 88×92, preserve aspect ratio.
            float maxW = 88f, maxH = 92f;
            float scale = Math.Min(maxW / logo.Width, maxH / logo.Height);
            float dw = logo.Width * scale;
            float dh = logo.Height * scale;
            var dst = new SKRect(
                centerX - dw / 2f, centerY - dh / 2f,
                centerX + dw / 2f, centerY + dh / 2f);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                FilterQuality = SKFilterQuality.High,
                ColorFilter = SKColorFilter.CreateBlendMode(
                    new SKColor(255, 255, 255, (byte)(250 * alpha)),
                    SKBlendMode.DstIn),
            };
            c.DrawBitmap(logo, dst, paint);
        }
        else if (!string.IsNullOrEmpty(d.CarBrand))
        {
            // Fallback: brand text in Cormorant.
            using var paint = new SKPaint
            {
                Color = TextPrimary.WithAlpha((byte)(242 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.CormorantGaramond.Bold,
                TextSize = 28f,
                TextAlign = SKTextAlign.Center,
            };
            string text = d.CarBrand.ToUpperInvariant();
            TextTracked.Draw(c, paint, text, centerX, centerY + 8f, 0.04f * 28f);
        }

        // --- car model caption, centred below the logo (moved here from the
        // identity column). The brand lane is narrow (~84 px), so auto-fit the
        // text down from 11 px and ellipsise if a long model name still spills.
        string carModel = string.IsNullOrEmpty(d.CarName) ? "" : d.CarName.ToUpperInvariant();
        if (!string.IsNullOrEmpty(carModel))
        {
            float capW = right - left;
            using var cap = new SKPaint
            {
                Color = TextTertiary.WithAlpha((byte)(255 * alpha)),
                IsAntialias = true,
                Typeface = Fonts.Inter.Medium,
                TextSize = 13f,   // #340: was 11 (shrinks to fit, now never below 10)
                TextAlign = SKTextAlign.Center,
            };
            while (cap.TextSize > 10f && cap.MeasureText(carModel) > capW)
                cap.TextSize -= 0.5f;
            string capText = EllipsiseToWidth(carModel, cap, capW);
            c.DrawText(capText, centerX, centerY + 56f, cap);
        }
    }

    // ----- visibility state machine -----

    void UpdateVisibility(RaceSnapshot snap, Driver? featured, double nowMs)
    {
        if (ForceVisible)
        {
            isVisible = true;
            if (shownAtMs == 0) shownAtMs = nowMs;
            return;
        }

        bool relevant = false;

        if (featured != null)
        {
            // Track per-driver position changes.
            if (featured.CarIdx == lastFeaturedCarIdx && featured.Position > 0
                && lastFeaturedPosition > 0 && featured.Position != lastFeaturedPosition)
            {
                lastPositionChangeAtMs = nowMs;
            }
            if (featured.CarIdx != lastFeaturedCarIdx)
            {
                lastFeaturedCarIdx = featured.CarIdx;
                lastFeaturedPosition = featured.Position;
                lastPositionChangeAtMs = 0; // reset on driver change
            }
            else if (featured.Position > 0)
            {
                lastFeaturedPosition = featured.Position;
            }

            // Trigger 1: early laps (1..N).
            if (snap.CurrentLap > 0 && snap.CurrentLap <= EarlyLaps) relevant = true;

            // Trigger 2: final laps (only meaningful for lap-limited races).
            if (snap.TotalLaps >= 5 && snap.CurrentLap > 0)
            {
                int remaining = snap.TotalLaps - snap.CurrentLap;
                if (remaining >= 0 && remaining <= FinalLaps) relevant = true;
            }

            // Trigger 3: recent position change for the focused driver.
            if (lastPositionChangeAtMs > 0
                && (nowMs - lastPositionChangeAtMs) < PositionChangeWindowMs)
            {
                relevant = true;
            }
        }

        if (relevant)
        {
            if (!isVisible)
            {
                isVisible = true;
                shownAtMs = nowMs;
                leaveStartedAtMs = -1;
                StorySound.PlayBroadcast("featured_in");
            }
            wantHideAtMs = 0;
        }
        else if (isVisible)
        {
            if (wantHideAtMs == 0) wantHideAtMs = nowMs;
            bool minMet = (nowMs - shownAtMs) >= MinVisibleMs;
            bool hysMet = (nowMs - wantHideAtMs) >= HideHysteresisMs;
            if (minMet && hysMet)
            {
                isVisible = false;
                leaveStartedAtMs = nowMs;
            }
        }
    }

    float ComputeAlpha(double nowMs)
    {
        if (isVisible)
        {
            // Entering: ramp 0→1 over EnterAnimMs after first becoming visible.
            double t = nowMs - shownAtMs;
            if (t < EnterAnimMs) return (float)EaseOut(t / EnterAnimMs);
            return 1f;
        }
        else
        {
            if (leaveStartedAtMs < 0) return 0f;
            double t = nowMs - leaveStartedAtMs;
            if (t >= ExitAnimMs) return 0f;
            return 1f - (float)EaseOut(t / ExitAnimMs);
        }
    }

    float ComputeTranslateY(double nowMs)
    {
        if (isVisible)
        {
            double t = nowMs - shownAtMs;
            if (t < EnterAnimMs)
            {
                float prog = (float)EaseOut(t / EnterAnimMs);
                return 12f * (1f - prog); // 12 → 0
            }
            return 0f;
        }
        else
        {
            if (leaveStartedAtMs < 0) return 12f;
            double t = nowMs - leaveStartedAtMs;
            if (t >= ExitAnimMs) return -12f;
            return -12f * (float)EaseOut(t / ExitAnimMs);
        }
    }

    static double EaseOut(double t) => BroadcastTiming.EaseOut(t);

    float ComputeGateAlpha(double nowMs)
    {
        bool visible = PresentationState.FeaturedVisible;
        if (visible != wasGated)
        {
            gateFlipAtMs = nowMs;
            wasGated = visible;
        }
        if (gateFlipAtMs < 0) return visible ? 1f : 0f;
        double t = (nowMs - gateFlipAtMs) / GateFadeAnimMs;
        float prog = (float)BroadcastTiming.Smoothstep(t);
        return visible ? prog : 1f - prog;
    }

    // ----- count-bounce -----

    sealed class ValueState
    {
        public string? LastValue;
        public double BounceAtMs;
    }

    /// <summary>
    /// Draw a value with the legacy <c>count-bounce</c> overshoot when it
    /// changes. The paint is owned and disposed by the caller.
    /// </summary>
    void DrawAnimatedValue(SKCanvas c, string id, string text, float x, float baseline,
                            double nowMs, SKPaint paint, string? valueToTrack = null)
    {
        if (string.IsNullOrEmpty(text)) return;

        string key = valueToTrack ?? text;
        if (!valueState.TryGetValue(id, out var s))
        {
            s = new ValueState { LastValue = key, BounceAtMs = -1e9 };
            valueState[id] = s;
        }
        else
        {
            if (s.LastValue != null && s.LastValue != key && !string.IsNullOrEmpty(key))
                s.BounceAtMs = nowMs;
            s.LastValue = key;
        }

        double dt = nowMs - s.BounceAtMs;
        if (dt < 0 || dt > CountBounceMs)
        {
            c.DrawText(text, x, baseline, paint);
            return;
        }

        // cubic-bezier(0.34, 1.56, 0.64, 1) — overshoot ease. Approximate with
        // a sin-based overshoot scale that peaks ~1.12 then settles to 1.
        double prog = dt / CountBounceMs;
        double scale = 1.0 + 0.12 * Math.Sin(prog * Math.PI) * (1.0 - prog);

        c.Save();
        c.Translate(x, baseline);
        c.Scale((float)scale, (float)scale);
        c.Translate(-x, -baseline);
        c.DrawText(text, x, baseline, paint);
        c.Restore();
    }

    // ----- helpers -----

    static string FormatIRating(int ir)
    {
        if (ir >= 1000)
            return (ir / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        return ir.ToString(CultureInfo.InvariantCulture);
    }

    static string EllipsiseToWidth(string text, SKPaint paint, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (paint.MeasureText(text) <= maxWidth) return text;
        const string ellipsis = "…";
        float ellipsisW = paint.MeasureText(ellipsis);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = paint.MeasureText(text.AsSpan(0, mid).ToString()) + ellipsisW;
            if (w <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text.AsSpan(0, lo).ToString() + ellipsis;
    }

    static string EllipsiseTrackedToWidth(string text, SKPaint paint, float letterSpacing, float maxWidth)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (MeasureTracked(paint, text, letterSpacing) <= maxWidth) return text;
        const string ellipsis = "…";
        float ellipsisW = paint.MeasureText(ellipsis);
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            float w = MeasureTracked(paint, text[..mid], letterSpacing) + ellipsisW;
            if (w <= maxWidth) lo = mid;
            else hi = mid - 1;
        }
        return text[..lo] + ellipsis;
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

    SKBitmap? LoadLogo(string? brand)
    {
        var slug = ManufacturerBrand.Resolve(brand);
        if (slug == null) return null;
        if (logoCache.TryGetValue(slug, out var cached)) return cached;
        SKBitmap? bmp = null;
        try
        {
            var path = Assets.Resolve("logos", "manufacturers", slug + ".png");
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

    SKBitmap? LoadClassLogo(string? classLabel)
    {
        var slug = ClassLogo.Resolve(classLabel);
        if (slug == null) return null;
        var key = "class:" + slug;
        if (logoCache.TryGetValue(key, out var cached)) return cached;
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
        logoCache[key] = bmp;
        return bmp;
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
        glassPaint.Dispose();
        foreach (var b in logoCache.Values) b?.Dispose();
        logoCache.Clear();
    }
}
